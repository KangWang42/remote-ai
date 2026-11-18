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
        sheet = "document.querySelector('dialog.rs[open] .rs-body')"
        closed = "!document.querySelector('dialog.rs:not(.closing)')"
        # a button of the sheet, by the words it starts with
        press = lambda text: until("(b => b && !b.disabled && (b.click(), true))([...document.querySelectorAll('dialog.rs:not(.closing) button')].find(b => b.innerText.trim().startsWith(%s)))" % json.dumps(text), 'press ' + text)
        typed = lambda name, text: value("(n=>{n.value=%s;n.dispatchEvent(new Event('input',{bubbles:true}));})(document.getElementById(%s))" % (json.dumps(text), json.dumps(name)))
        # a conversation chosen on a page of the sheet, by its title
        picked = lambda title: until("(r => r && (r.click(), true))([...document.querySelectorAll('dialog.rs:not(.closing) .rs-row')].find(r => r.innerText.includes(%s)))" % json.dumps(title), 'pick ' + title)
        open_page = lambda query, ready: browser.open(played.origin + '/?tab=together' + query, ready, query or 'collaboration', **phone(390))
        loaded = "document.querySelectorAll('#chains .card:not(.add)').length===1 && document.querySelectorAll('#schedules .card:not(.add)').length===1"
        opened = lambda name: sheet + " && !!document.getElementById(" + json.dumps(name) + ")"
        title = lambda session: next(one["title"] for one in played.sessions if one["id"] == session)

        # Native rule rows must open the exact stored rule, after async loading finishes.
        open_page('&do=chain&item=' + 'e' * 32, opened('chain-text'))
        assert title(pictures.THESIS) in value("((document.getElementById('chain-from') || {}).innerText || '')")
        assert value("document.getElementById('chain-text').value") == played.chains[0]['text']
        press('审查改动')
        assert value("document.getElementById('chain-text').value").startswith(played.chains[0]['text'] + '\n')
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed by its button')
        open_page('&do=schedule&item=' + 'f' * 32, opened('schedule-text'))
        assert value("document.getElementById('schedule-time').value") == '08:30'
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        played.schedules[0].update(kind='every', minutes=7)
        open_page('&do=schedule&item=' + 'f' * 32, opened('schedule-text'))
        assert value("document.querySelector('.rs-chip[data-value=\"7\"]').getAttribute('aria-pressed')") == 'true', 'Editing must preserve existing custom intervals'
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        played.schedules[0].update(kind='daily', minutes=0)
        open_page('&do=chain&item=' + 'a' * 32, "document.getElementById('toast').textContent.includes('已不存在')")
        assert value(closed), 'A missing rule must not become a new rule'

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
        value("document.getElementById('chain-from').click()"); picked(title(pictures.THESIS))
        until(opened('chain-text') + " && ((document.getElementById('chain-from') || {}).innerText || '').includes(%s)" % json.dumps(title(pictures.THESIS)), 'source chosen')
        value("document.getElementById('chain-to').click()"); picked(title(pictures.TABLES))
        until(opened('chain-text') + " && ((document.getElementById('chain-to') || {}).innerText || '').includes(%s)" % json.dumps(title(pictures.TABLES)), 'taker chosen')
        typed('chain-text', '检查新的协作入口')
        press('保存')
        until("document.querySelector('dialog.rs .rs-said').textContent.includes('回执')", 'lost save receipt')
        press('保存')
        until(closed + " && document.querySelectorAll('#chains .card:not(.add)').length===2", 'retried save')
        saves = [op for op in played.asked if op['action'] == 'work_chain_set']
        assert len(saves) == 2 and saves[0]['item']['id'] == saves[1]['item']['id']
        assert (saves[1]['item']['from'], saves[1]['item']['to']) == (pictures.THESIS, pictures.TABLES)
        assert len(played.chains) == 2

        # Rapid toggle taps issue one mutation, then show the acknowledged state.
        before = sum(op['action'] == 'work_schedule_set' for op in played.asked)
        value("(b=>{b.click();b.click();})(document.querySelector('#schedules .rs-switch'))")
        until("document.querySelector('#schedules .rs-switch').getAttribute('aria-checked')==='false' && document.querySelector('#schedules .rs-switch').getAttribute('aria-label')==='启用'", 'paused schedule')
        assert sum(op['action'] == 'work_schedule_set' for op in played.asked) == before + 1

        # A new question at a time: the conversation chosen in the sheet, on weekdays.
        value("document.getElementById('add-schedule').click()")
        until(opened('schedule-text'), 'new schedule')
        value("document.getElementById('schedule-session').click()"); picked(title(pictures.TABLES))
        until(opened('schedule-text') + " && ((document.getElementById('schedule-session') || {}).innerText || '').includes(%s)" % json.dumps(title(pictures.TABLES)), 'conversation chosen')
        typed('schedule-text', '汇报一下当前进展')
        press('工作日')
        browser.picture(out / 'schedule-new-390.png')
        press('保存')
        until(closed + " && document.querySelectorAll('#schedules .card:not(.add)').length===2", 'schedule saved')
        made = [op for op in played.asked if op['action'] == 'work_schedule_set'][-1]['item']
        assert (made['session'], made['kind'], made['days'], made['text']) == (pictures.TABLES, 'daily', [1, 2, 3, 4, 5], '汇报一下当前进展'), made

        # Batch failure stays visible. Completing the sheet never resends successful messages.
        played.failed_target = pictures.EXPORT
        value("document.getElementById('ask-many').click()")
        until(opened('many-text'), 'batch question')
        value("document.getElementById('many-%s').click()" % pictures.TABLES)
        until("!!document.getElementById('many-%s')" % pictures.EXPORT, 'the page drawn anew')
        value("document.getElementById('many-%s').click()" % pictures.EXPORT)
        until("document.querySelector('dialog.rs .rs-note').textContent.includes('已选 2')", 'two chosen')
        typed('many-text', '协作测试问题')
        press('发给 2 段对话')
        until("[...document.querySelectorAll('dialog.rs:not(.closing) button')].some(b=>b.innerText.trim()==='完成')", 'partial result')
        assert '已发送 1 / 2' in value("document.querySelector('dialog.rs .rs-note').textContent")
        assert '目标对话已关闭' in value("document.querySelector('dialog.rs').innerText")
        browser.picture(out / 'many-partial-390.png')
        sent = len([op for op in played.asked if op['action'] == 'session_say'])
        press('完成')
        until(closed, 'batch closed')
        assert len([op for op in played.asked if op['action'] == 'session_say']) == sent

        # Keyboard close returns focus to the originating action.
        value("document.getElementById('add-chain').focus();document.getElementById('add-chain').click()")
        until(opened('chain-text'), 'keyboard sheet')
        browser.ask('Input.dispatchKeyEvent', type='keyDown', key='Escape', code='Escape', windowsVirtualKeyCode=27)
        browser.ask('Input.dispatchKeyEvent', type='keyUp', key='Escape', code='Escape', windowsVirtualKeyCode=27)
        until(closed, 'escape closes sheet')
        try:
            until("document.activeElement.id === 'add-chain'", 'focus back on the action')
        except AssertionError:
            raise AssertionError(('focus back on the action', value("[document.activeElement.id, document.activeElement.className, document.activeElement.tagName, document.querySelectorAll('dialog[open]').length]")))

        evidence = []
        for width in (320, 390, 430, 1440):
            for state, query, ready in [('overview', '', "document.querySelectorAll('#chains .card:not(.add)').length===2"), ('chain', '&do=chain', opened('chain-text'))]:
                browser.open(played.origin + '/?tab=together' + query, ready, state, **phone(width))
                time.sleep(.3)
                metrics = value("""(() => { const p=document.documentElement, f=document.querySelector('dialog.rs[open] .rs-body'), box=(f||document.getElementById('together')).getBoundingClientRect();
                  const ids=[...document.querySelectorAll('[id]')].map(n=>n.id); return {inner:innerWidth,client:p.clientWidth,scroll:p.scrollWidth,body:document.body.scrollWidth,
                  left:box.left,right:innerWidth-box.right,form:f?{client:f.clientWidth,scroll:f.scrollWidth}:null,h1:document.querySelectorAll('h1').length,
                  duplicates:ids.filter((id,i)=>ids.indexOf(id)!==i),missingLabels:f?[...f.querySelectorAll('input,textarea,select')].filter(n=>!n.labels.length&&!n.getAttribute('aria-label')).length:0,
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
        until("!!document.querySelector('dialog.rs[open]')", 'dark chain')
        browser.picture(out / 'chain-dark-390.png')
        contrast = value("""(() => { const canvas=document.createElement('canvas'), ctx=canvas.getContext('2d');
          const luminance=color=>{ctx.fillStyle=color;ctx.fillRect(0,0,1,1);const rgb=[...ctx.getImageData(0,0,1,1).data].slice(0,3).map(x=>x/255).map(x=>x<=.04045?x/12.92:((x+.055)/1.055)**2.4);return rgb[0]*.2126+rgb[1]*.7152+rgb[2]*.0722;};
          const bg=luminance(getComputedStyle(document.documentElement).getPropertyValue('--bg'));
          return Object.fromEntries(['dialog.rs .rs-note','dialog.rs .rs-chip','dialog.rs .rs-label'].map(sel=>{const n=document.querySelector(sel), fg=luminance(getComputedStyle(n).color);return [sel,(Math.max(fg,bg)+.05)/(Math.min(fg,bg)+.05)];}));})()""")
        assert min(contrast.values()) >= 4.5, contrast
        evidence.append(dict(state='dark-reduced-motion', contrast=contrast))
        (out / 'checks.json').write_text(json.dumps(evidence, indent=2, ensure_ascii=False), encoding='utf-8')
    finally:
        browser.close(); played.close()
print('Collaboration: direct rule editing, partial recovery, stable save IDs, single toggle, a question on weekdays, batch results, keyboard and four viewports passed')
