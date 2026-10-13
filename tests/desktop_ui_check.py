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
                    relay.agent({'info': info})
                    return self.reply(relay.overview())
                if self.path == '/favicon.ico':
                    self.send_response(204); self.end_headers(); return
                super().do_GET()

            def do_POST(self):
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
                until("document.querySelector('#sheet[open] h3')?.textContent === '新建任务'")

            def composer():
                navigate()
                value("Array.from(document.querySelectorAll('#sheet button')).find(b => b.textContent.includes('在 Codex 桌面应用中打开')).click()")
                until("document.querySelector('#desktop-draft') !== null")

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
            # Actual keyboard focus stays in the dialog; native select and textarea participate.
            value("document.getElementById('desktop-project').focus()")
            ask('Input.dispatchKeyEvent', type='keyDown', key='Tab', code='Tab', windowsVirtualKeyCode=9)
            ask('Input.dispatchKeyEvent', type='keyUp', key='Tab', code='Tab', windowsVirtualKeyCode=9)
            assert value('document.activeElement.id') == 'desktop-draft'
            value("document.getElementById('desktop-draft').value = '中文草稿 & # +\\n第二行'; document.querySelector('#sheet button.solid').click(); document.querySelector('#sheet button.solid').click()")
            until("document.querySelector('#sheet button.solid').textContent === '查询本次操作结果'")
            assert len(requests) == 1, requests
            assert value("document.getElementById('desktop-draft').value") == '中文草稿 & # +\n第二行'
            picture('retry.png')
            value("document.querySelector('#sheet button.solid').click()")
            until("document.querySelector('#sheet button.solid').textContent === '已交给应用，尚未发送'")
            assert len({r['id'] for r in requests}) == 1 and all(r == requests[0] for r in requests), requests
            assert value("document.querySelector('#sheet button.solid').disabled")
            picture('handed-off.png')
            mode[0] = 'unknown'; composer()
            value("document.querySelector('#sheet button.solid').click()")
            until("document.querySelector('#sheet button.solid').textContent === '结果未知，请检查电脑'")
            picture('unknown.png')
            info['features'] = []
            navigate()
            assert value("document.querySelector('#sheet-form').textContent.includes('桌面应用入口暂不可用')")
            assert not value("Array.from(document.querySelectorAll('#sheet button')).some(b => b.textContent.includes('在 Codex 桌面应用中打开'))")
            assert value("document.querySelectorAll('#sheet button.tool').length") == 2
            assert not any(e['method'] == 'Runtime.exceptionThrown' for e in events), events
            # The single injected 503 is intentional; no other error-level log is acceptable.
            errors = [e for e in events if e['method'] == 'Log.entryAdded' and e['params']['entry']['level'] == 'error']
            assert len(errors) <= 1 and all('503' in e['params']['entry']['text'] for e in errors), errors
            (out / 'report.json').write_text(json.dumps({'layouts': shapes, 'requests': len(requests), 'uniqueOperations': len({r['id'] for r in requests}), 'injected503': len(errors)}, ensure_ascii=False, indent=2), encoding='utf-8')
            print('desktop UI: 320/390/430/1440 layouts, keyboard, duplicate click, lost-response retry, unknown and unavailable passed')
        finally:
            if pipe: pipe.close()
            browser.terminate(); browser.wait(timeout=10)
            server.shutdown(); server.server_close()


if __name__ == '__main__':
    main()
