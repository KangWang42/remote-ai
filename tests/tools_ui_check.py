import json
import sys
import tempfile
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import readme_pictures as pictures
sys.path.insert(0, str(ROOT / 'relay'))
import server

out = Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='release-167-ui-', ignore_cleanup_errors=True) as folder:
    played = pictures.Played(folder)
    answer = played.answer
    played.answer = lambda op: {} if op['action'] in ('input', 'resize') else answer(op)
    played.server.shutdown(); played.server.server_close()
    played.server = server.make_server('127.0.0.1', 0, str(Path(folder) / 'actual-relay'), str(ROOT / 'web'), password='test-ui-only')
    played.server.space.relay = played.relay
    played.origin = 'http://127.0.0.1:%d' % played.server.server_port
    threading.Thread(target=played.server.serve_forever, daemon=True).start()
    def heartbeat():
        while not played.closed:
            played.relay.agent({'info': played.info, 'sessions': played.sessions})
            time.sleep(.5)
    threading.Thread(target=heartbeat, daemon=True).start()
    browser = pictures.Browser(sys.argv[1], folder)
    try:
        browser.ask('Network.setCookie', name='rcli', value=played.server.space.sessions.create(), url=played.origin, httpOnly=True, sameSite='Strict')
        played.info['tools'] = ['claude', 'codex', 'agy', 'opencode', 'qoder', 'cline', 'shell']
        played.info['agy_external'] = [int(time.time() * 1000)]
        played.relay.agent({'info': played.info, 'sessions': played.sessions})
        browser.ask('Page.addScriptToEvaluateOnNewDocument', source="window.__errors=[];addEventListener('error',e=>__errors.push(e.message));addEventListener('unhandledrejection',e=>__errors.push(String(e.reason)));localStorage.setItem('newkind','term');")
        report = []
        for width in (390, 1440, 320, 430):
            screen = dict(width=width, height=900, deviceScaleFactor=1, mobile=width < 500)
            browser.open(played.origin + '/?tab=tasks', "document.body.innerText.includes('部署到测试环境')", 'tasks', **screen)
            browser.until("!document.querySelector('#monitor').hidden && !document.querySelector('#external-agy').hidden", 'task monitor')
            browser.value("document.querySelector('#external-agy summary').click()")
            assert browser.value("document.querySelector('#external-agy li').innerText.includes('启动')")
            browser.picture(out / ('tasks-%d.png' % width))
            browser.value("document.getElementById('new-task').click()")
            browser.until("!!document.querySelector('#sheet-form .tools')", 'new task dialog')
            browser.value("Array.from(document.querySelectorAll('#sheet-form .seg button')).find(b=>b.textContent==='终端').click()")
            browser.until("document.querySelectorAll('#sheet-form .tools button').length===7", 'seven tools in new task')
            dialog = browser.value("(()=>{const f=document.getElementById('sheet-form'),r=f.getBoundingClientRect();return {client:f.clientWidth,scroll:f.scrollWidth,left:r.left,right:innerWidth-r.right}})()")
            assert dialog['scroll'] <= dialog['client'] and abs(dialog['left'] - dialog['right']) <= 1, dialog
            browser.value("Promise.all(document.getElementById('sheet').getAnimations().filter(a=>a.effect.getComputedTiming().iterations!==Infinity).map(a=>a.finished.catch(()=>{}))).then(()=>true)")
            browser.picture(out / ('dialog-%d.png' % width))
            browser.open(played.origin + '/?project=web-shop', "document.querySelectorAll('#launch button').length>0", 'project tools', **screen)
            browser.value("Array.from(document.querySelectorAll('#begin .seg button')).find(b=>b.textContent==='终端').click()")
            browser.until("document.querySelectorAll('#launch button').length===7", 'seven tool buttons')
            assert browser.value("Array.from(document.querySelectorAll('#launch button')).map(b=>b.textContent)") == ['Claude Code', 'Codex', 'agy', 'OpenCode', 'Qoder', 'Cline', 'PowerShell']
            if width < 500:
                assert browser.value("new Set(Array.from(document.querySelectorAll('#launch button'),b=>b.offsetTop)).size >= 2")
            geometry = browser.value("({inner:innerWidth,client:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth,body:document.body.scrollWidth,errors:__errors})")
            assert geometry['inner'] == width and max(geometry['scroll'], geometry['body']) <= width and not geometry['errors'], geometry
            browser.picture(out / ('tools-%d.png' % width))
            tool, name = ('opencode', 'OpenCode') if width == 430 else ('agy', 'Antigravity CLI')
            label = 'agy' if tool == 'agy' else name
            browser.value(f"Array.from(document.querySelectorAll('#launch button')).find(b=>b.textContent==={json.dumps(label)}).click()")
            browser.until("location.pathname.endsWith('/terminal/')", name + ' terminal')
            terminal = next(op['terminal'] for op in reversed(played.asked) if op['action'] == 'start')
            assert next(op for op in reversed(played.asked) if op['action'] == 'start')['tool'] == tool
            played.relay.agent({'info': played.info, 'terminals': [{'id': terminal, 'state': 'running'}]})
            browser.until("document.getElementById('status-text').textContent.includes('已连接')", 'terminal connected')
            assert browser.value(f"document.getElementById('status-text').textContent.startsWith({json.dumps(name)})"), 'terminal retains tool name'
            browser.value("document.querySelector('[data-key=ctrlp]').click()")
            for _ in range(80):
                if any(op['action'] == 'input' and op.get('data') == '\x10' and op.get('terminal') == terminal for op in played.asked):
                    break
                time.sleep(.1)
            else:
                raise AssertionError('Ctrl+P did not reach relay input')
            geometry['terminal'] = browser.value("({inner:innerWidth,client:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth,body:document.body.scrollWidth,errors:__errors})")
            assert geometry['terminal']['inner'] == width and max(geometry['terminal']['scroll'], geometry['terminal']['body']) <= width and not geometry['terminal']['errors'], geometry
            browser.value("document.querySelector('[data-key=ctrlp]').scrollIntoView({block:'nearest',inline:'center'})")
            browser.picture(out / ('terminal-%d.png' % width))
            report.append(geometry)
        # 1.7.2 exposes PowerShell but discards agy from a Windows agent's heartbeat.
        server.VERSION = '1.7.2'
        played.info['tools'].remove('agy')
        played.relay.agent({'info': played.info, 'sessions': played.sessions})
        browser.open(played.origin + '/?tab=tasks&new=1', "!!document.querySelector('#sheet-form .tools')", 'old relay new task', **screen)
        browser.value("Array.from(document.querySelectorAll('#sheet-form .seg button')).find(b=>b.textContent==='应用').click()")
        browser.value("Array.from(document.querySelectorAll('#sheet-form .tools button')).find(b=>b.textContent==='agy').click()")
        browser.value("Array.from(document.querySelectorAll('#sheet-form .folders button')).find(b=>b.textContent==='thesis').click()")
        browser.until("location.pathname.endsWith('/terminal/')", 'legacy PowerShell terminal')
        op = next(op for op in reversed(played.asked) if op['action'] == 'start')
        assert (op['tool'], op['dir']) == ('shell', 'thesis')
        terminal = op['terminal']
        played.relay.agent({'info': played.info, 'terminals': [{'id': terminal, 'state': 'running'}]})
        browser.until("document.querySelector('#input').value==='agy --mode plan'", 'editable agy command in new shell')
        assert not any(op['action'] == 'input' and op['terminal'] == terminal for op in played.asked)
        browser.value("document.querySelector('#send').click()")
        for _ in range(60):
            if any(op['action'] == 'input' and op['terminal'] == terminal and op['data'] == 'agy --mode plan\r' for op in played.asked):
                break
            time.sleep(.1)
        else:
            raise AssertionError('agy command did not reach the new PowerShell')
        (out / 'ui-report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print('Browser checks passed: direct agy, legacy PowerShell launch draft and send, Ctrl+P, 320/390/430/1440 layouts.')
    finally:
        browser.close()
        played.close()
