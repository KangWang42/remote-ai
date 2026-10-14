"""Collaboration entry points, recovery and mobile layout with the real UI and relay.

    python tests/together_ui_check.py <chrome.exe> <evidence directory>
"""
import json
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import readme_pictures as pictures


class Controlled(pictures.Played):
    failed = ""
    lost_save = False
    failed_target = ""

    def answer(self, op):
        if op["action"] == self.failed:
            raise pictures.RemoteError("测试连接中断")
        if op["action"] == "work_chain_set" and self.lost_save:
            self.lost_save = False
            super().answer(op)
            raise pictures.RemoteError("保存回执暂时不可用，请重试")
        if op["action"] == "session_say" and op.get("session") == self.failed_target:
            raise pictures.RemoteError("目标对话已关闭")
        return super().answer(op)


out = Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="remote-ai-collaboration-", ignore_cleanup_errors=True) as temporary:
    played, browser = Controlled(temporary), pictures.Browser(sys.argv[1], temporary)
    try:
        value, until = browser.value, browser.until
        browser.ask("Page.addScriptToEvaluateOnNewDocument", source="window.__errors=[];addEventListener('error',e=>__errors.push(e.message));addEventListener('unhandledrejection',e=>__errors.push(String(e.reason)));")
        phone = lambda width: dict(width=width, height=844, deviceScaleFactor=1, mobile=True)
        sheet = "document.querySelector('#sheet[open] #sheet-form')"
        press = lambda text: value("[...document.querySelectorAll('#sheet button')].find(b=>b.textContent===%s).click()" % json.dumps(text))
        fill = lambda name, text: value("(n=>{n.value=%s;n.dispatchEvent(new Event('change',{bubbles:true}));})(document.getElementById(%s))" % (json.dumps(text), json.dumps(name)))
        open_page = lambda query, ready: browser.open(played.origin + '/?tab=together' + query, ready, query or 'collaboration', **phone(390))
        loaded = "document.querySelectorAll('#chains .card:not(.add)').length===1 && document.querySelectorAll('#schedules .card:not(.add)').length===1"
        opened = lambda name: sheet + " && !!document.getElementById(" + json.dumps(name) + ")"

        # Native rule rows must open the exact stored rule, after async loading finishes.
        open_page('&do=chain&item=' + 'e' * 32, opened('chain-text'))
        assert value("document.getElementById('chain-from').value") == pictures.THESIS
        assert value("document.getElementById('chain-text').value") == played.chains[0]['text']
        press('审查改动')
        assert value("document.getElementById('chain-text').value").startswith(played.chains[0]['text'] + '\n')
        press('取消')
        open_page('&do=schedule&item=' + 'f' * 32, opened('schedule-text'))
        assert value("document.getElementById('schedule-time').value") == '08:30'
        press('取消')
        played.schedules[0].update(kind='every', minutes=7)
        open_page('&do=schedule&item=' + 'f' * 32, opened('schedule-text'))
        assert value("document.getElementById('schedule-minutes').value") == '7', 'Editing must preserve existing custom intervals'
        press('取消')
        played.schedules[0].update(kind='daily', minutes=0)
        open_page('&do=chain&item=' + 'a' * 32, "document.getElementById('toast').textContent.includes('已不存在')")
        assert not value("document.getElementById('sheet').open"), 'A missing rule must not become a new rule'

        # Each list fails independently, with an explicit retry and no false empty state.
        played.failed = 'work_chains'
        open_page('', "document.querySelector('#together .notice')?.textContent.includes('测试连接中断')")
        assert value("document.querySelectorAll('#schedules .card:not(.add)').length") == 1
        assert '读取失败' in value("document.querySelector('#chains').textContent")
        played.failed = ''
        value("document.querySelector('#together .notice button').click()")
        until(loaded, 'retry restores both lists')
        played.failed = 'work_schedules'
        open_page('', "document.querySelector('#together .notice')?.textContent.includes('测试连接中断')")
        assert value("document.querySelectorAll('#chains .card:not(.add)').length") == 1
        played.failed = ''
        value("document.querySelector('#together .notice button').click()")
        until(loaded, 'schedule retry')

        # A lost save acknowledgement followed by a retry must not create two rules.
        played.lost_save = True
        value("document.getElementById('add-chain').click()")
        until(opened('chain-text'), 'new chain')
        fill('chain-from', pictures.THESIS); fill('chain-to', pictures.TABLES); fill('chain-text', '检查新的协作入口')
        press('保存')
        until("document.querySelector('#sheet .problem').textContent.includes('回执')", 'lost save receipt')
        press('保存')
        until("!document.getElementById('sheet').open && document.querySelectorAll('#chains .card:not(.add)').length===2", 'retried save')
        saves = [op for op in played.asked if op['action'] == 'work_chain_set']
        assert len(saves) == 2 and saves[0]['item']['id'] == saves[1]['item']['id']
        assert len(played.chains) == 2

        # Rapid toggle taps issue one mutation, then show the acknowledged state.
        before = sum(op['action'] == 'work_schedule_set' for op in played.asked)
        value("(b=>{b.click();b.click();})(document.querySelector('#schedules .side'))")
        until("document.querySelector('#schedules .side').getAttribute('aria-label')==='启用'", 'paused schedule')
        assert sum(op['action'] == 'work_schedule_set' for op in played.asked) == before + 1

        # Batch failure stays visible. Completing the sheet never resends successful messages.
        played.failed_target = pictures.EXPORT
        value("document.getElementById('ask-many').click()")
        until(opened('many-text'), 'batch question')
        value("document.getElementById('many-%s').click();document.getElementById('many-%s').click()" % (pictures.TABLES, pictures.EXPORT))
        assert '已选 2' in value("document.querySelector('#sheet [role=status]').textContent")
        fill('many-text', '协作测试问题')
        press('发送')
        until("[...document.querySelectorAll('#sheet button')].some(b=>b.textContent==='完成')", 'partial result')
        assert '已发送 1 / 2' in value("document.querySelector('#sheet [role=status]').textContent")
        assert '目标对话已关闭' in value("document.getElementById('sheet-form').textContent")
        sent = len([op for op in played.asked if op['action'] == 'session_say'])
        press('完成')
        until("!document.getElementById('sheet').open", 'batch closed')
        assert len([op for op in played.asked if op['action'] == 'session_say']) == sent

        # Keyboard close returns focus to the originating action.
        value("document.getElementById('add-chain').focus();document.getElementById('add-chain').click()")
        until(opened('chain-text'), 'keyboard sheet')
        browser.ask('Input.dispatchKeyEvent', type='keyDown', key='Escape', code='Escape', windowsVirtualKeyCode=27)
        browser.ask('Input.dispatchKeyEvent', type='keyUp', key='Escape', code='Escape', windowsVirtualKeyCode=27)
        until("!document.getElementById('sheet').open", 'escape closes sheet')
        assert value('document.activeElement.id') == 'add-chain'

        evidence = []
        for width in (320, 390, 430, 1440):
            for state, query, ready in [('overview', '', "document.querySelectorAll('#chains .card:not(.add)').length===2"), ('chain', '&do=chain', opened('chain-text'))]:
                browser.open(played.origin + '/?tab=together' + query, ready, state, **phone(width))
                time.sleep(.3)
                metrics = value("""(() => { const p=document.documentElement, f=document.querySelector('#sheet[open] #sheet-form'), box=(f||document.getElementById('together')).getBoundingClientRect();
                  const ids=[...document.querySelectorAll('[id]')].map(n=>n.id); return {inner:innerWidth,client:p.clientWidth,scroll:p.scrollWidth,body:document.body.scrollWidth,
                  left:box.left,right:innerWidth-box.right,form:f?{client:f.clientWidth,scroll:f.scrollWidth}:null,h1:document.querySelectorAll('h1').length,
                  duplicates:ids.filter((id,i)=>ids.indexOf(id)!==i),missingLabels:f?[...f.querySelectorAll('input,textarea,select')].filter(n=>!n.labels.length).length:0,
                  missingAlt:document.querySelectorAll('img:not([alt])').length,broken:[...document.images].filter(n=>!n.complete||!n.naturalWidth).length,errors:window.__errors}; })()""")
                assert metrics['inner'] == width and metrics['client'] == width and max(metrics['scroll'], metrics['body']) <= width, metrics
                assert abs(metrics['left'] - metrics['right']) <= 2, metrics
                assert not metrics['duplicates'] and not metrics['missingLabels'] and not metrics['errors'], metrics
                if metrics['form']:
                    assert metrics['form']['scroll'] <= metrics['form']['client'], metrics
                browser.picture(out / (state + '-' + str(width) + '.png'))
                evidence.append(dict(width=width, state=state, metrics=metrics))
        browser.ask('Emulation.setEmulatedMedia', features=[dict(name='prefers-reduced-motion', value='reduce')])
        browser.open(played.origin + '/?tab=together&skin=night&do=chain', opened('chain-text'), 'dark reduced motion', **phone(390))
        until("document.getElementById('sheet').open", 'dark chain')
        browser.picture(out / 'chain-dark-390.png')
        contrast = value("""(() => { const canvas=document.createElement('canvas'), ctx=canvas.getContext('2d');
          const luminance=color=>{ctx.fillStyle=color;ctx.fillRect(0,0,1,1);const rgb=[...ctx.getImageData(0,0,1,1).data].slice(0,3).map(x=>x/255).map(x=>x<=.04045?x/12.92:((x+.055)/1.055)**2.4);return rgb[0]*.2126+rgb[1]*.7152+rgb[2]*.0722;};
          const bg=luminance(getComputedStyle(document.documentElement).getPropertyValue('--panel'));
          return Object.fromEntries(['.collab-intro','.collab-help','.collab-presets button'].map(sel=>{const n=document.querySelector(sel), fg=luminance(getComputedStyle(n).color);return [sel,(Math.max(fg,bg)+.05)/(Math.min(fg,bg)+.05)];}));})()""")
        assert min(contrast.values()) >= 4.5, contrast
        evidence.append(dict(state='dark-reduced-motion', contrast=contrast))
        (out / 'checks.json').write_text(json.dumps(evidence, indent=2, ensure_ascii=False), encoding='utf-8')
    finally:
        browser.close(); played.close()
print('Collaboration: direct rule editing, partial recovery, stable save IDs, single toggle, batch results, keyboard and four viewports passed')
