"""Real embedded page + real relay in an isolated HTTP fixture, simulated desktop receipts.

Usage: python tests/desktop_ui_check.py <Edge/Chrome executable> <screenshot directory>
Never launches or modifies the user's desktop AI application.
"""
import base64
import http.client
import http.server
import json
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'relay'))
from relay import Relay
from ws_client import Socket


def main():
    out = Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='remote-ai-desktop-ui-') as temporary:
        relay = Relay(str(Path(temporary) / 'relay.json'))
        info = {'instance': 'b' * 32, 'enabled': True, 'tools': ['codex', 'shell'],
                'workspaces': ['中文项目 & 开发目录'], 'features': ['codex-desktop-open']}
        requests, events = [], []
        fail_once = [True]
        mode = ['handed_off']
        sid = '11111111-2222-3333-4444-555555555555'
        sessions = []
        native_requests = []
        lost_send = [True]
        echo_send = [False]
        transcript = [{'role': 'user', 'text': '电脑上的第一句'}, {'role': 'assistant', 'text': '电脑回答，手机可以接着提问。'}]

        def report():
            relay.agent({'info': info, 'sessions': sessions})

        class Handler(http.server.SimpleHTTPRequestHandler):
            def __init__(self, *args, **kwargs):
                super().__init__(*args, directory=str(ROOT / 'web'), **kwargs)

            def log_message(self, *args):
                pass

            def reply(self, value, status=200):
                body = json.dumps(value, ensure_ascii=False).encode('utf-8')
                self.send_response(status); self.send_header('Content-Type', 'application/json; charset=utf-8')
                self.send_header('Content-Length', str(len(body))); self.end_headers(); self.wfile.write(body)

            def do_GET(self):
                if self.path == '/api/session':
                    return self.reply({'signed_in': True})
                if self.path == '/api/terminal':
                    report()
                    return self.reply(relay.overview())
                if self.path == '/favicon.ico':
                    self.send_response(204); self.end_headers(); return
                super().do_GET()

            def do_POST(self):
                if self.path == '/api/conversation':
                    payload = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                    if payload.get('action', 'read') == 'read':
                        if payload.get('since') == str(len(transcript)):
                            return self.reply({'same': True, 'status': sessions[0]['status'], 'stamp': str(len(transcript))})
                        return self.reply({'messages': transcript, 'status': sessions[0]['status'], 'stamp': str(len(transcript))})
                    native_requests.append(payload)
                    answers = {}
                    def handle():
                        try: answers['value'] = relay.conversation(payload, wait=5)
                        except Exception as error: answers['error'] = str(error)
                    worker = threading.Thread(target=handle); worker.start()
                    operations = relay.pull({'instance': info['instance'], 'wait': 2})['operations']
                    if operations:
                        operation = operations[0]
                        result = {'status': 'accepted', 'sent': True, 'now': True, 'turn': 'turn-1'} if payload['action'] == 'say' else {'status': 'confirmed', 'turn': 'turn-1'}
                        relay.agent({'info': info, 'acks': [{'id': operation['id'], 'result': result}]})
                    worker.join(6)
                    if 'error' in answers: return self.reply({'error': answers['error']}, 400)
                    if payload['action'] == 'say' and not payload.get('receipt'):
                        if echo_send[0]:
                            transcript.append({'role': 'user', 'text': payload['text']})
                            sessions[0]['queued'] = [{'id': payload['id'], 'text': payload['text'], 'state': 'sending'}]
                        sessions[0].update(status='busy', reach=['stop'], turn='turn-1')
                        report()
                        if lost_send[0]:
                            lost_send[0] = False
                            return self.reply({'error': '模拟发送回执丢失'}, 503)
                    elif payload['action'] == 'stop':
                        sessions[0].update(status='waiting', reach=['say'], turn='')
                        report()
                    return self.reply(answers['value'])
                assert self.path == '/api/terminal', self.path
                payload = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                requests.append(payload)
                answer = relay.command(payload)
                relay.agent({'info': info, 'acks': [{'id': payload['id'], 'result': {
                    'status': mode[0], 'sent': False,
                    'message': '已交给 Codex 桌面应用，内容尚未发送。' if mode[0] == 'handed_off' else '上次操作的结果未知，未重复打开。请先检查电脑上的应用。'
                }}]})
                if fail_once[0]:
                    fail_once[0] = False
                    return self.reply({'error': '模拟响应丢失，请查询本次操作'}, 503)
                self.reply(answer)

        server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        port = server.server_address[1]
        browser = subprocess.Popen([sys.argv[1], '--headless=new', '--no-first-run', '--remote-debugging-port=0',
                                    '--remote-allow-origins=*', '--user-data-dir=' + temporary + '/browser', 'about:blank'],
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        pipe = None
        try:
            active = Path(temporary) / 'browser/DevToolsActivePort'
            for _ in range(100):
                if active.exists(): break
                time.sleep(.1)
            debug = int(active.read_text().splitlines()[0])
            conn = http.client.HTTPConnection('127.0.0.1', debug)
            conn.request('PUT', '/json/new?about:blank')
            page = json.loads(conn.getresponse().read()); conn.close()
            pipe = Socket('http://127.0.0.1:%d' % debug, '/devtools/page/' + page['id'], '')
            sequence = [0]

            def ask(method, **params):
                sequence[0] += 1
                pipe.send({'id': sequence[0], 'method': method, 'params': params})
                while True:
                    message = pipe.receive(15)
                    if message.get('id') == sequence[0]:
                        assert 'error' not in message, message
                        return message.get('result', {})
                    if message.get('method') in ('Runtime.exceptionThrown', 'Log.entryAdded'):
                        events.append(message)

            def value(code):
                result = ask('Runtime.evaluate', expression=code, returnByValue=True, awaitPromise=True)
                assert 'exceptionDetails' not in result, result
                return result.get('result', {}).get('value')

            def until(code):
                for _ in range(100):
                    result = value(code)
                    if result: return result
                    time.sleep(.1)
                raise AssertionError(code)

            def navigate():
                ask('Page.navigate', url='http://127.0.0.1:%d/?new=1' % port)
                until("document.querySelector('#sheet[open] h3')?.textContent === '新建对话'")

            def composer():
                # an application stands open first, with Codex chosen: a press on the project asks for the first words
                navigate()
                assert value("Array.from(document.querySelectorAll('#sheet [aria-pressed=\"true\"]')).map(b => b.textContent)") == ['应用', 'Codex']
                value("document.querySelector('#sheet .anywhere button.choice').click()")
                until("document.querySelector('#desktop-draft') !== null")
                assert value("document.querySelector('#sheet-form h3').textContent") == 'Codex · ' + info['workspaces'][0]

            def picture(name):
                (out / name).write_bytes(base64.b64decode(ask('Page.captureScreenshot', format='png')['data']))

            ask('Page.enable'); ask('Runtime.enable'); ask('Log.enable')
            shapes = []
            for width in [390, 320, 430, 1440]:
                ask('Emulation.setDeviceMetricsOverride', width=width, height=900, deviceScaleFactor=1, mobile=width < 600)
                composer()
                time.sleep(.25)
                shape = value("""(() => { const f = document.getElementById('sheet-form'), r = f.getBoundingClientRect(); const ids = Array.from(document.querySelectorAll('[id]')).map(e => e.id); return {width: innerWidth, client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth, body: document.body.scrollWidth, form: f.scrollWidth, left: r.left, right: innerWidth-r.right, h1: document.querySelectorAll('h1').length, duplicateIds: ids.length-new Set(ids).size, missingAlt: document.querySelectorAll('img:not([alt])').length, broken: Array.from(document.images).filter(i => !i.complete || !i.naturalWidth).length}; })()""")
                assert shape['width'] == width and shape['scroll'] <= width and shape['body'] <= width, shape
                assert shape['form'] <= min(560, width) and abs(shape['left'] - shape['right']) <= 1, shape
                assert not shape['duplicateIds'] and not shape['missingAlt'] and not shape['broken'], shape
                shapes.append(shape); picture('draft-%d.png' % width)
            # Actual keyboard focus stays in the dialog: from the first words to the button that hands them over.
            value("document.getElementById('desktop-draft').focus()")
            ask('Input.dispatchKeyEvent', type='keyDown', key='Tab', code='Tab', windowsVirtualKeyCode=9)
            ask('Input.dispatchKeyEvent', type='keyUp', key='Tab', code='Tab', windowsVirtualKeyCode=9)
            assert value('document.activeElement.textContent') == '在 Codex 中打开' and value("!!document.activeElement.closest('#sheet')")
            value("document.getElementById('desktop-draft').value = '中文草稿 & # +\\n第二行'; document.querySelector('#sheet button.solid').click(); document.querySelector('#sheet button.solid').click()")
            until("document.querySelector('#sheet button.solid').textContent === '查询本次操作结果'")
            assert len(requests) == 1, requests
            assert value("document.getElementById('desktop-draft').value") == '中文草稿 & # +\n第二行'
            picture('retry.png')
            value("document.querySelector('#sheet button.solid').click()")
            until("document.querySelector('#sheet button.solid').textContent === '在电脑上发送后开始'")
            assert len({r['id'] for r in requests}) == 1 and all(r == requests[0] for r in requests), requests
            assert value("document.querySelector('#sheet button.solid').disabled")
            picture('handed-off.png')
            mode[0] = 'unknown'; composer()
            value("document.querySelector('#sheet button.solid').click()")
            until("document.querySelector('#sheet button.solid').textContent === '结果未知，请检查电脑'")
            picture('unknown.png')
            info['features'] = []
            navigate()
            # a computer without the desktop entry offers its terminals, and no choice of kind
            assert value("document.querySelectorAll('#sheet .seg button').length") == 0
            assert value("Array.from(document.querySelectorAll('#sheet .tools button')).map(b => b.textContent)") == ['Codex', '终端']
            # The same real embedded reader, with native control capabilities relayed from the computer.
            info['features'] = ['peek', 'reach', 'end', 'codex-desktop-control']
            sessions.append(dict(id=sid, tool='codex', dir=info['workspaces'][0], title='电脑与手机的同一段对话', updated=int(time.time()*1000), live=True,
                                 host='shared', origin='Codex 桌面版', status='waiting', reach=['say'], transport='codex-desktop', turn='', can_takeover=False))
            report()
            for width in [390, 320, 430, 1440]:
                ask('Emulation.setDeviceMetricsOverride', width=width, height=900, deviceScaleFactor=1, mobile=width < 600)
                ask('Page.navigate', url='http://127.0.0.1:%d/?take=%s' % (port, sid))
                until("document.querySelector('#sheet .write textarea') && document.querySelector('#sheet .said')?.textContent.includes('电脑上的第一句')")
                shape = value("({width:innerWidth,client:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth,body:document.body.scrollWidth})")
                assert shape['width'] == width and shape['scroll'] <= width and shape['body'] <= width, shape
                assert value("document.querySelector('#sheet .write').hidden") is False
                assert value("Array.from(document.querySelectorAll('#sheet button.end')).find(b=>b.textContent==='结束').hidden") is True
                picture('native-reader-%d.png' % width)
            value("document.querySelector('#sheet .write textarea').value='手机接着问一句'; document.querySelector('#sheet .write button.solid').click(); document.querySelector('#sheet .write button.solid').click()")
            until("document.querySelector('#sheet .said').textContent.includes('查询发送结果')")
            assert len(native_requests) == 1 and native_requests[0]['transport'] == 'codex-desktop', native_requests
            # Recover the unknown operation after a full reload. Opening the reader must not resend.
            ask('Page.navigate', url='http://127.0.0.1:%d/?take=%s' % (port, sid))
            until("document.querySelector('#sheet .said')?.textContent.includes('查询发送结果')")
            assert len(native_requests) == 1, 'restoring a pending send must not dispatch it'
            until("document.querySelector('#sheet .write button.solid').disabled")
            assert value("document.querySelector('#sheet .write').hidden") is False
            value("document.querySelector('#sheet .write textarea').value='等回答结束后再问'")
            picture('native-lost-response.png')
            value("Array.from(document.querySelectorAll('#sheet .said button')).find(b=>b.textContent==='查询发送结果').click()")
            until("document.querySelector('#sheet .said').textContent.includes('工具已接收')")
            assert len(native_requests) == 2 and native_requests[0]['id'] == native_requests[1]['id'] and native_requests[1]['receipt'] is True, native_requests
            # Use pointer hit testing: HTMLElement.click() also activates covered controls.
            ask('Emulation.setDeviceMetricsOverride', width=390, height=844, deviceScaleFactor=1, mobile=True)
            value("window.RemoteCliPage.data.sessions[0].turn = ''")
            stop_point = value("(() => { const b=document.querySelector('#sheet .halt'), r=b.getBoundingClientRect(), x=r.x+r.width/2, y=r.y+r.height/2; return {x,y,hit:b.contains(document.elementFromPoint(x,y)), disabled:b.disabled}; })()")
            assert stop_point['hit'] and not stop_point['disabled'], stop_point
            ask('Input.dispatchMouseEvent', type='mousePressed', x=stop_point['x'], y=stop_point['y'], button='left', clickCount=1)
            ask('Input.dispatchMouseEvent', type='mouseReleased', x=stop_point['x'], y=stop_point['y'], button='left', clickCount=1)
            until("!document.querySelector('#sheet .write button.solid').disabled")
            assert native_requests[-1]['action'] == 'stop' and native_requests[-1]['turn'] == 'turn-1'
            assert value("document.querySelector('#sheet .stop-note').textContent") == '已停止本次回答，可以继续对话'
            assert value("document.querySelector('#sheet .stop-note').getClientRects().length > 0")
            assert value("document.querySelector('#sheet .write textarea').value") == '等回答结束后再问'
            assert value("document.querySelector('#sheet').open") is True
            transcript.append({'role': 'assistant', 'text': '电脑上继续回答后的新内容'})
            until("document.querySelector('#sheet .said').textContent.includes('电脑上继续回答后的新内容')")
            picture('native-stopped.png')
            pending_key = value("Object.keys(sessionStorage).find(k => k.startsWith('rcli-desktop-pending-'))")
            assert pending_key, 'native operation remains recoverable until it appears in the transcript'
            # Computer identity is part of the recovery scope, even with an identical session id.
            info['instance'] = 'c' * 32
            ask('Page.navigate', url='http://127.0.0.1:%d/?take=%s' % (port, sid))
            until("document.querySelector('#sheet .said')?.textContent.includes('电脑上的第一句')")
            assert not value("document.querySelector('#sheet .said .mine')"), 'another computer must not inherit pending input'
            info['instance'] = 'b' * 32
            # A crashed tab may have stored the operation before it got any answer at all.
            value("(() => { const key = %s, rows = JSON.parse(sessionStorage.getItem(key)); rows[0].sent = false; rows[0].uncertain = false; rows[0].delivery = ''; sessionStorage.setItem(key, JSON.stringify(rows)); })()" % json.dumps(pending_key))
            ask('Page.navigate', url='http://127.0.0.1:%d/?take=%s' % (port, sid))
            until("document.querySelector('#sheet .said')?.textContent.includes('查询发送结果')")
            assert len(native_requests) == 3, 'a send with no response is restored as uncertain without dispatch'
            # Old recovery records are removed on reopening, not shown as current deliveries.
            value("(() => { const key = %s, rows = JSON.parse(sessionStorage.getItem(key)); rows[0].createdAt = Date.now() - 86400001; sessionStorage.setItem(key, JSON.stringify(rows)); })()" % json.dumps(pending_key))
            ask('Page.navigate', url='http://127.0.0.1:%d/?take=%s' % (port, sid))
            until("document.querySelector('#sheet .said')?.textContent.includes('电脑上的第一句')")
            assert not value("document.querySelector('#sheet .said .mine')") and value("sessionStorage.getItem(%s)" % json.dumps(pending_key)) is None
            # A fast transcript echo replaces the temporary bubble even if all later polls return same.
            echo_send[0] = True
            for occurrence in [1, 2]:
                sessions[0].update(status='waiting', reach=['say'], turn='', queued=[])
                report()
                ask('Emulation.setDeviceMetricsOverride', width=390, height=844, deviceScaleFactor=1, mobile=True)
                ask('Page.navigate', url='http://127.0.0.1:%d/?take=%s' % (port, sid))
                until("document.querySelector('#sheet .said')?.textContent.includes('电脑上的第一句') && !document.querySelector('#sheet .write button.solid').disabled")
                value("document.querySelector('#sheet .write textarea').value='重复发送同一句'; document.querySelector('#sheet .write button.solid').click()")
                until("Array.from(document.querySelectorAll('#sheet .said .turn.user:not(.mine) .md')).filter(n=>n.textContent.trim()==='重复发送同一句').length === %d && !document.querySelector('#sheet .said .mine')" % occurrence)
                time.sleep(1.5)
                assert value("Array.from(document.querySelectorAll('#sheet .said .turn.user .md, #sheet .said .turn.user .plain')).filter(n=>n.textContent.trim()==='重复发送同一句').length") == occurrence
            transcript.extend({'role': 'assistant', 'text': '滚动阅读内容 %d\n' % i + '这段内容用于验证正文滚动，不遮挡输入区。' * 6} for i in range(12))
            until("document.querySelector('#sheet .said').textContent.includes('滚动阅读内容 11')")
            value("document.querySelector('#sheet .said').scrollTop = 0")
            until("!document.querySelector('#sheet .latest').hidden")
            assert value("document.querySelector('#sheet .said').scrollHeight > document.querySelector('#sheet .said').clientHeight")
            picture('native-scroll-390.png')
            value("document.querySelector('#sheet .latest').click()")
            until("document.querySelector('#sheet .latest').hidden")
            assert value("document.querySelector('#sheet .write').getBoundingClientRect().bottom <= innerHeight")
            picture('native-reconciled-390.png')
            ask('Emulation.setDeviceMetricsOverride', width=390, height=460, deviceScaleFactor=1, mobile=True)
            value("document.querySelector('#sheet .write textarea').focus(); document.querySelector('#sheet .write textarea').value='键盘打开时保留草稿'")
            assert value("document.querySelector('#sheet .write').getBoundingClientRect().bottom <= innerHeight")
            assert value("document.querySelector('#sheet .said').clientHeight > 100")
            picture('native-keyboard-390.png')
            sessions[0].update(status='busy', reach=[], turn='', queued=[],
                               ways={'say': '', 'stop': '', 'answer': '', 'end': ''},
                               why={'stop': '当前执行目标只能在电脑上停止'})
            report()
            ask('Page.navigate', url='http://127.0.0.1:%d/?take=%s' % (port, sid))
            until("document.querySelector('#sheet .halt') && !document.querySelector('#sheet .halt').hidden")
            before_stop = len(native_requests)
            point = value("(() => { const r=document.querySelector('#sheet .halt').getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+r.height/2}; })()")
            ask('Input.dispatchMouseEvent', type='mousePressed', **point, button='left', clickCount=1)
            ask('Input.dispatchMouseEvent', type='mouseReleased', **point, button='left', clickCount=1)
            until("document.querySelector('#sheet .stop-note')?.textContent === '当前执行目标只能在电脑上停止' && !document.querySelector('#sheet .halt').disabled")
            assert len(native_requests) == before_stop, 'unsupported stop must not kill the application or dispatch an invalid turn'
            assert value("document.querySelector('#sheet .stop-note').getClientRects().length > 0")
            picture('native-stop-unavailable-390.png')
            sessions[0].update(reach=['stop'], ways={'say': '', 'stop': 'codex-desktop', 'answer': '', 'end': ''}, why={})
            report()
            value("window.RemoteCliPage.refresh()")
            value("document.querySelector('#sheet .halt').click()")
            until("!document.querySelector('#sheet .halt').disabled && !document.querySelector('#sheet .stop-note').hidden")
            assert '正在' not in value("document.querySelector('#sheet .stop-note').textContent"), value("document.querySelector('#sheet .stop-note').textContent")
            assert len(native_requests) == before_stop, 'missing turn must not dispatch an unscoped interruption'
            assert not any(e['method'] == 'Runtime.exceptionThrown' for e in events), events
            # The single injected 503 is intentional; no other error-level log is acceptable.
            errors = [e for e in events if e['method'] == 'Log.entryAdded' and e['params']['entry']['level'] == 'error']
            assert len(errors) <= 2 and all('503' in e['params']['entry']['text'] for e in errors), errors
            (out / 'report.json').write_text(json.dumps({'layouts': shapes, 'requests': len(requests), 'uniqueOperations': len({r['id'] for r in requests}), 'injected503': len(errors)}, ensure_ascii=False, indent=2), encoding='utf-8')
            print('desktop UI: 320/390/430/1440 layouts, keyboard, native send/stop, receipt-only retry, busy draft, transcript refresh and legacy handoff passed')
        finally:
            if pipe: pipe.close()
            browser.terminate(); browser.wait(timeout=10)
            server.shutdown(); server.server_close()


if __name__ == '__main__':
    main()
