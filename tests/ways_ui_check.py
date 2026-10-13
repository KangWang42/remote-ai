"""The window of a conversation that is open on the computer, the same for Claude Code and Codex: sending, stopping
and ending go by the way the computer names for each thing, and the computer's words are shown where a thing cannot
be done. The real embedded page and the real relay; the computer is played here, so nothing real is opened or told.

Usage: python tests/ways_ui_check.py <Edge/Chrome executable> <screenshot directory>
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

CLAUDE, CLI, APP, OUTSIDE = ('11111111-2222-3333-4444-55555555555%d' % n for n in range(1, 5))
PROJECT = '中文项目'


def main():
    out = Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='remote-ai-ways-ui-') as temporary:
        relay = Relay(str(Path(temporary) / 'relay.json'))
        info = {'instance': 'c' * 32, 'enabled': True, 'tools': ['claude', 'codex'], 'workspaces': [PROJECT],
                'features': ['peek', 'reach', 'end', 'outbox', 'codex-desktop-control', 'codex-desktop-open', 'claude-desktop-open']}
        now = int(time.time() * 1000)
        usual = dict(dir=PROJECT, updated=now, live=True, host='shared', status='busy')
        sessions = [
            # Claude Code in its desktop app, at work: told and stopped through its hooks; its application closes it
            dict(usual, id=CLAUDE, tool='claude', title='Claude 桌面版里的对话', origin='Claude 桌面版', kind='Claude 桌面版', reach=['say', 'stop'], transport='hook', can_takeover=False,
                 ways={'say': 'hook', 'stop': 'hook', 'answer': '', 'end': ''}, why={'end': '这段对话由电脑上的应用管理，请在那里关闭它'}),
            # Codex's command line, at work: told through its hooks; it cannot be stopped, it can be ended
            dict(usual, id=CLI, tool='codex', title='命令行里的 Codex', origin='命令行', kind='终端', reach=['say'], transport='', can_takeover=True,
                 ways={'say': 'hook', 'stop': '', 'answer': '', 'end': 'process'}, why={'stop': 'Codex 命令行的回答不能从外面单独停止'}),
            # Codex's desktop app, at work: stopped by the application's channel, told through its hooks meanwhile
            dict(usual, id=APP, tool='codex', title='Codex 桌面版里的对话', origin='Codex 桌面版', kind='Codex 桌面版', reach=['say', 'stop'], transport='codex-desktop', turn='turn-7', can_takeover=False,
                 ways={'say': 'hook', 'stop': 'codex-desktop', 'answer': '', 'end': ''}, why={'end': '这段对话由电脑上的应用管理，请在那里关闭它'}),
            # A discovered conversation outside projects whose native channel is temporarily unavailable.
            dict(usual, id=OUTSIDE, dir='', folder='D:\\outside', tool='codex', title='项目之外的对话', origin='Codex 桌面版', kind='Codex 桌面版', status='idle', reach=[], transport='', can_takeover=False,
                 ways={'say': '', 'stop': '', 'answer': '', 'end': ''}, why={'say': '暂时无法连接 Codex 原对话：应用连接尚未就绪、工作目录无法确认，或此版本尚未适配。请在电脑上打开这段对话后刷新；无需先添加项目', 'end': '这段对话由电脑上的应用管理，请在那里关闭它'}),
        ]
        asked, forwarded, opened, events, refuse = [], [], [], [], [False]
        board_mode = [False]
        board_terminals = []
        transcript = [{'role': 'user', 'text': '电脑上的第一句'}, {'role': 'assistant', 'text': '电脑上的回答。'}]

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
                    overview = relay.overview()
                    if board_mode[0]: overview['terminals'] = board_terminals
                    return self.reply(overview)
                if self.path == '/favicon.ico':
                    self.send_response(204); self.end_headers(); return
                super().do_GET()

            def do_POST(self):
                payload = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                if self.path == '/api/conversation':
                    if payload.get('action', 'read') == 'read':
                        one = next(s for s in sessions if s['id'] == payload['session'])
                        return self.reply({'messages': transcript, 'status': one['status'], 'stamp': str(len(transcript))})
                    asked.append(payload)
                    answers = {}

                    def handle():
                        try: answers['value'] = relay.conversation(payload, wait=5)
                        except Exception as error: answers['error'] = str(error)
                    worker = threading.Thread(target=handle); worker.start()
                    operations = relay.pull({'instance': info['instance'], 'wait': 2})['operations']
                    if operations:
                        # the computer answers as the program does: the same shape for every way of saying
                        op, by = operations[0], payload.get('transport', '')
                        forwarded.append(op)
                        if refuse[0]:
                            ack = {'id': op['id'], 'error': '电脑上的这段对话现在联系不上，请刷新后重试'}
                        elif op['action'] == 'session_say':
                            ack = {'id': op['id'], 'result': {'status': 'accepted', 'sent': True, 'now': by == 'codex-desktop', 'way': by or 'hook', 'turn': 'turn-8'}}
                        elif op['action'] == 'session_stop':
                            ack = {'id': op['id'], 'result': {'status': 'asked', 'transport': 'hook'} if by == 'hook' else {'status': 'confirmed', 'turn': op.get('turn')}}
                        elif op['action'] == 'session_end' and board_mode[0]:
                            time.sleep(.3)
                            one = next(s for s in sessions if s['id'] == payload['session'])
                            one.update(live=False, status='idle')
                            ack = {'id': op['id'], 'result': {}}
                        elif op['action'] in ('session_withdraw', 'session_release'):
                            # the computer keeps what waits: taken back it is gone, released it waits again
                            one = next(s for s in sessions if s['id'] == payload['session'])
                            one['queued'] = [dict(m, state='queued') if op['action'] == 'session_release' else m for m in one.get('queued', []) if m['id'] != op.get('message')]
                            ack = {'id': op['id'], 'result': {'state': 'withdrawn'} if op['action'] == 'session_withdraw' else {'released': 1, 'way': 'wake'}}
                        else:
                            ack = {'id': op['id'], 'result': {}}
                        relay.agent({'info': info, 'acks': [ack]})
                    worker.join(6)
                    if 'error' in answers: return self.reply({'error': answers['error']}, 400)
                    return self.reply(answers['value'])
                assert self.path == '/api/terminal', self.path
                opened.append(payload)
                answer = relay.command(payload)
                relay.agent({'info': info, 'acks': [{'id': payload['id'], 'result': {'status': 'handed_off', 'sent': False, 'message': '已交给 Claude 桌面应用。请在电脑上确认项目和草稿，内容尚未发送。'}}]})
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

            def until(code, why=''):
                for _ in range(100):
                    result = value(code)
                    if result: return result
                    time.sleep(.1)
                raise AssertionError(why or code)

            def picture(name):
                (out / name).write_bytes(base64.b64decode(ask('Page.captureScreenshot', format='png')['data']))

            def window(session):
                ask('Page.navigate', url='http://127.0.0.1:%d/?take=%s' % (port, session))
                until("document.querySelector('#sheet .state') && document.querySelector('#sheet .said')?.textContent.includes('电脑上的第一句')", 'the window of ' + session)
                time.sleep(.3)

            button = lambda text: "Array.from(document.querySelectorAll('#sheet button')).find(b => b.textContent === '%s')" % text
            shown = lambda text: value("(b => !!b && !b.hidden)(%s)" % button(text))
            write = lambda text: value("(() => { const box = document.querySelector('#sheet .write textarea'); box.value = '%s'; document.querySelector('#sheet .write button.solid').click(); })()" % text)
            toast = "document.getElementById('toast').textContent"

            ask('Page.enable'); ask('Runtime.enable'); ask('Log.enable')
            ask('Emulation.setDeviceMetricsOverride', width=390, height=844, deviceScaleFactor=1, mobile=True)

            # The overview and project detail use the same mutually exclusive state groups.
            original = [dict(s) for s in sessions]
            board_mode[0] = True
            sessions[0].update(status='confirm')
            sessions[1].update(origin='Positron', pinned=True)
            sessions[2].update(status='waiting')
            retained = dict(original[2], id='11111111-2222-3333-4444-555555555556', title='应用保留的闲置对话', now=False)
            sessions.append(retained)
            board_terminals[:] = [dict(id='a' * 32, tool='shell', title='等待输入的终端', dir=PROJECT,
                                      state='running', phase='idle', created=now, phase_at=now, done=True),
                                  dict(id='b' * 32, tool='codex', title='正在启动的终端', dir=PROJECT,
                                      state='starting', phase='starting', created=now, phase_at=now)]
            base = 'http://127.0.0.1:%d/' % port
            names = lambda selector: value("Array.from(document.querySelectorAll(%s + ' .card b')).map(b => b.textContent)" % json.dumps(selector))

            def overview():
                value("history.replaceState(null, '')")
                ask('Page.navigate', url=base)
                until("document.querySelector('#opened-list .card') !== null")

            overview()
            assert names('#needs') == [sessions[0]['title']]
            assert names('#active') == [sessions[1]['title'], '正在启动的终端']
            assert set(names('#opened')) == {sessions[2]['title'], sessions[3]['title'], retained['title'], '等待输入的终端'}
            assert names('#pinned') == [], 'a live pinned task stays in its state group'
            assert value("document.querySelector('#project-list').textContent.includes('2 个正在运行')")
            assert value("document.querySelector('#active .chip').textContent") == PROJECT
            assert value("document.querySelector('#active').textContent.includes('Positron 的终端')")
            assert value("document.querySelectorAll('.end-session').length") == 1, 'only the verified CLI can be ended'
            geometries = []
            for width in (320, 390, 430, 1440):
                ask('Emulation.setDeviceMetricsOverride', width=width, height=844, deviceScaleFactor=1, mobile=width < 500)
                geometry = value("({width:innerWidth, client:document.documentElement.clientWidth, scroll:document.documentElement.scrollWidth, body:document.body.scrollWidth})")
                assert geometry['width'] == width and max(geometry['scroll'], geometry['body']) <= geometry['client'] + 1, geometry
                # Login and signed-in content each own a heading/landmark; only one is visible at a time.
                assert value("Array.from(document.querySelectorAll('h1')).filter(e => e.getClientRects().length).length") == 1
                assert value("Array.from(document.querySelectorAll('main')).filter(e => e.getClientRects().length).length") == 1
                assert value("new Set(Array.from(document.querySelectorAll('[id]')).map(e => e.id)).size === document.querySelectorAll('[id]').length")
                geometries.append(geometry)
                picture('board-%d.png' % width)
            (out / 'board-layout.json').write_text(json.dumps(geometries, indent=2), encoding='utf-8')
            ask('Emulation.setDeviceMetricsOverride', width=390, height=844, deviceScaleFactor=1, mobile=True)
            value("document.getElementById('opened').scrollIntoView()")
            picture('board-opened.png')
            value("scrollTo(0, 0)")
            value("document.querySelector('#project-list .card button.open').click()")
            until("!document.getElementById('project').hidden")
            for section in ('p-active', 'p-live'):
                value("document.querySelector('#%s .section-head').click()" % section)
            assert set(names('#p-active')) == {sessions[1]['title'], '正在启动的终端'}
            assert set(names('#p-live')) == {sessions[2]['title'], retained['title'], '等待输入的终端'}
            assert names('#p-needs') == [sessions[0]['title']]
            assert value("Array.from(document.querySelectorAll('#project .card')).every(c => c.querySelector('.chip')?.textContent === %s)" % json.dumps(PROJECT))
            picture('project-states.png')

            # Live refresh moves the same session to opened and back without duplicating it.
            sessions[1]['status'] = 'waiting'
            until("Array.from(document.querySelectorAll('#p-live .card b')).some(b => b.textContent === '命令行里的 Codex')")
            assert sessions[1]['title'] not in names('#p-active')
            sessions[1]['status'] = 'busy'
            until("Array.from(document.querySelectorAll('#p-active .card b')).some(b => b.textContent === '命令行里的 Codex')")
            overview()
            before = len(asked)
            value("document.querySelector('.end-session').click()")
            until("document.querySelector('#sheet[open] button.danger') !== null")
            assert len(asked) == before
            picture('card-end-confirm.png')
            value("document.querySelector('#sheet button.cancel').click()")
            until("!document.getElementById('sheet').open")
            assert len(asked) == before, 'cancel never submits an end operation'
            value("document.querySelector('.end-session').click()")
            until("document.querySelector('#sheet[open] button.danger') !== null")
            value("document.querySelector('#sheet button.danger').click()")
            until("document.querySelector('.end-session')?.disabled === true")
            value("document.querySelector('.end-session').click()")
            until("document.querySelectorAll('.end-session').length === 0")
            assert len(asked) == before + 1 and asked[-1]['action'] == 'end' and asked[-1]['session'] == CLI
            assert sessions[1]['title'] not in names('#active') + names('#opened')
            assert sessions[1]['title'] in names('#pinned'), 'closed pinned conversation is retained as a saved record'
            sessions[:] = original
            board_mode[0] = False
            board_terminals.clear()

            # ---- Claude Code in its desktop app, at work
            window(CLAUDE)
            assert shown('停止回答') and not shown('结束') and not value("document.querySelector('#sheet .write').hidden"), 'at work: it can be told and stopped; its application closes it'
            picture('claude-at-work.png')
            write('做完这一步后看这里')
            until("document.querySelector('#sheet .said').textContent.includes('已排队，它做完手头这一步后收到')", 'what was said is left for it')
            assert asked[-1] == {'id': asked[-1]['id'], 'session': CLAUDE, 'action': 'say', 'text': '做完这一步后看这里'}, asked[-1]
            assert not value("document.querySelector('#sheet .said').textContent.includes('查询发送结果')"), 'nothing is asked after: its hooks have said that they took it'
            value(button('停止回答') + '.click()')
            until(toast + ".includes('已告诉它停止')", 'told to stop through its hooks')
            assert {k: v for k, v in asked[-1].items() if k != 'id'} == {'session': CLAUDE, 'action': 'stop', 'transport': 'hook'}, asked[-1]
            # what the computer refuses was not sent: it stands in the box again, and no line of it is left above
            refuse[0] = True
            write('这一句送不出去')
            until(toast + ".includes('联系不上')", 'the refusal is said')
            until("document.querySelector('#sheet .write textarea').value === '这一句送不出去'", 'what was not sent is in the box again')
            assert not value("Array.from(document.querySelectorAll('#sheet .said .turn.mine')).some(row => row.textContent.includes('这一句送不出去'))"), 'and does not stand as sent'
            refuse[0] = False

            # ---- what the phone said and the conversation has not taken yet is shown message by message, as the computer
            # keeps it, also to a page that was opened anew: one waits, one is held since a stop. Each can be taken
            # back, and what is held can be sent after all.
            sessions[0]['queued'] = [{'id': 'a' * 32, 'text': '排着队的一句', 'state': 'queued', 'at': now}, {'id': 'b' * 32, 'text': '停止时还没发出的一句', 'state': 'paused', 'at': now}]
            window(CLAUDE)
            rows = "Array.from(document.querySelectorAll('#sheet .said .turn.mine')).map(row => row.querySelector('.plain').textContent + ' / ' + row.querySelector('small').textContent + ' / ' + Array.from(row.querySelectorAll('button')).map(b => b.textContent).join(','))"
            until("document.querySelectorAll('#sheet .said .turn.mine').length === 2", 'the two that wait are shown')
            assert value(rows) == ['排着队的一句 / 排队中 · 它做完手头这一步后收到 / 撤回', '停止时还没发出的一句 / 已暂停 · 对话被停止时还没有发出 / 撤回'], value(rows)
            assert value("document.querySelector('#sheet .said .held button').textContent") == '继续发送'
            picture('waiting-messages.png')
            value("document.querySelector('#sheet .said .turn.mine button').click()")
            until("document.querySelectorAll('#sheet .said .turn.mine').length === 1", 'taken back: it stands there no more')
            assert {k: v for k, v in asked[-1].items() if k != 'id'} == {'session': CLAUDE, 'action': 'withdraw', 'message': 'a' * 32}, asked[-1]
            value("document.querySelector('#sheet .said .held button').click()")
            until("!document.querySelector('#sheet .said .held') && document.querySelector('#sheet .said .turn.mine small').textContent.startsWith('排队中')", 'released: it waits again')
            assert {k: v for k, v in asked[-1].items() if k != 'id'} == {'session': CLAUDE, 'action': 'release'}, asked[-1]
            sessions[0]['queued'] = []

            # ---- Codex's command line, at work: the same line and the same buttons; stopping says why, and offers to end it
            window(CLI)
            assert shown('停止回答') and shown('结束') and not value("document.querySelector('#sheet .write').hidden"), 'the same controls as Claude Code has'
            before = len(asked)
            value(button('停止回答') + '.click()')
            until("document.querySelector('#sheet-form .confirm')?.textContent.includes('Codex 命令行的回答不能从外面单独停止')", 'the reason, and the question whether to end it')
            assert value("document.querySelector('#sheet-form .confirm').textContent.includes('结束电脑上的这段对话')") and len(asked) == before, 'nothing is done before its owner says so'
            picture('codex-cli-stop.png')
            value("document.querySelector('#sheet-form .confirm button.see').click()")
            assert len(asked) == before and value("document.querySelector('#sheet').open"), 'not ended when that is not wanted'
            value(button('停止回答') + '.click()')
            until("document.querySelector('#sheet-form .confirm') !== null")
            value("document.querySelector('#sheet-form .confirm button.no').click()")
            until(toast + ".includes('已结束电脑上的这段对话')", 'ended on the computer')
            assert {k: v for k, v in asked[-1].items() if k != 'id'} == {'session': CLI, 'action': 'end'}, asked[-1]

            # ---- Codex's desktop app, at work: stopped by its own channel, told through its hooks meanwhile
            window(APP)
            assert shown('停止回答') and not shown('结束')
            write('桌面版执行时的留言')
            until("document.querySelector('#sheet .said').textContent.includes('已排队，它做完手头这一步后收到')")
            assert 'transport' not in asked[-1] and asked[-1]['action'] == 'say', asked[-1]
            value(button('停止回答') + '.click()')
            until(toast + ".includes('已停止本次回答')", 'stopped by the application')
            assert {k: v for k, v in asked[-1].items() if k != 'id'} == {'session': APP, 'action': 'stop', 'turn': 'turn-7', 'transport': 'codex-desktop', 'receipt': False}, asked[-1]

            # ---- one that cannot be told anything: the computer's words why, and no box
            window(OUTSIDE)
            assert value("document.querySelector('#sheet .write').hidden") and not shown('停止回答')
            assert value("document.querySelector('#sheet .control-mode').textContent.includes('无需先添加项目')"), 'why nothing can be said to it'
            picture('outside.png')

            # The same outside conversation becomes reachable as soon as the app channel is available.
            sessions[3].update(status='waiting', reach=['say'], transport='codex-desktop',
                               ways={'say': 'codex-desktop', 'stop': '', 'answer': '', 'end': ''}, why={})
            window(OUTSIDE)
            assert not value("document.querySelector('#sheet .write').hidden"), 'project membership is not required for the composer'
            write('项目外也能接着问')
            until("document.querySelector('#sheet .said').textContent.includes('项目外也能接着问')")
            until("document.querySelector('#sheet .write button.solid').disabled === false")
            assert asked[-1]['transport'] == 'codex-desktop' and forwarded[-1]['dir'] == '', forwarded[-1]
            picture('outside-connected.png')
            sessions[3].update(status='busy', reach=['say', 'stop'], turn='turn-8',
                               ways={'say': 'codex-desktop', 'stop': 'codex-desktop', 'answer': '', 'end': ''})
            window(OUTSIDE)
            value(button('停止回答') + '.click()')
            until(toast + ".includes('已停止本次回答')")
            assert forwarded[-1]['dir'] == '' and forwarded[-1]['action'] == 'session_stop', forwarded[-1]

            # ---- a relay of an earlier version passes neither the ways nor the reasons on: the one way it names is taken
            for s in sessions:
                s.pop('ways'); s.pop('why')
            window(CLAUDE)
            value(button('停止回答') + '.click()')
            until(toast + ".includes('已告诉它停止')")
            assert asked[-1]['transport'] == 'hook' and asked[-1]['action'] == 'stop', asked[-1]
            window(APP)
            value(button('停止回答') + '.click()')
            until(toast + ".includes('已停止本次回答')")
            assert asked[-1]['transport'] == 'codex-desktop' and asked[-1]['turn'] == 'turn-7', asked[-1]

            # ---- a new conversation in a desktop application: each tool's own, offered alike
            ask('Page.navigate', url='http://127.0.0.1:%d/?new=1' % port)
            until("document.querySelector('#sheet[open] h3')?.textContent === '新建对话'")
            texts = lambda selector: value("Array.from(document.querySelectorAll(%s)).map(b => b.textContent)" % json.dumps(selector))
            assert texts('#sheet .seg button') == ['应用', '终端'] and texts('#sheet .tools button') == ['Codex', 'Claude'], (texts('#sheet .seg button'), texts('#sheet .tools button'))
            picture('new-task.png')
            value("Array.from(document.querySelectorAll('#sheet .tools button')).find(b => b.textContent === 'Claude').click()")
            value("document.querySelector('#sheet .anywhere button.choice').click()")
            until("document.querySelector('#desktop-draft') !== null")
            assert value("document.querySelector('#sheet-form h3').textContent") == 'Claude · ' + PROJECT and value("document.querySelector('#sheet button.solid').textContent") == '在 Claude 中打开'
            value("document.getElementById('desktop-draft').value = '先看一下 README'; document.querySelector('#sheet button.solid').click()")
            until("document.querySelector('#sheet button.solid').textContent === '在电脑上发送后开始'")
            assert {k: v for k, v in opened[-1].items() if k != 'id'} == {'action': 'app_open', 'tool': 'claude', 'dir': PROJECT, 'draft': '先看一下 README'}, opened[-1]
            picture('claude-draft.png')
            # The draft is sent on the computer, and only then is there a conversation: the computer says which one
            # came of this operation, and the page goes into it.
            fresh = '11111111-2222-3333-4444-555555555559'
            sessions.append(dict(usual, id=fresh, tool='claude', title='从手机新建的对话', origin='Claude 桌面版', kind='Claude 桌面版', reach=['say'], transport='', can_takeover=False, opened=opened[-1]['id'],
                                 ways={'say': 'hook', 'stop': '', 'answer': '', 'end': ''}, why={}))
            until("document.querySelector('#sheet .state') && document.querySelector('#sheet h3').textContent === '从手机新建的对话'", 'the conversation that came of it, entered: its window')
            picture('claude-came.png')
            info['features'] = ['peek', 'reach', 'codex-desktop-open']
            ask('Page.navigate', url='http://127.0.0.1:%d/?new=1' % port)
            until("document.querySelector('#sheet[open] h3')?.textContent === '新建对话'")
            assert texts('#sheet .tools button') == ['Codex'], 'only what the computer offers'

            assert not any(e['method'] == 'Runtime.exceptionThrown' for e in events), events
            errors = [e for e in events if e['method'] == 'Log.entryAdded' and e['params']['entry']['level'] == 'error']
            assert len(errors) == 1 and '400' in errors[0]['params']['entry']['text'], errors      # the one refusal that was played
            print('ways UI: Claude Code and Codex are told, stopped and ended by the same controls, each by its own way; reasons shown; a desktop app opened for either')
        finally:
            if pipe: pipe.close()
            browser.terminate(); browser.wait(timeout=10)
            server.shutdown(); server.server_close()


if __name__ == '__main__':
    main()
