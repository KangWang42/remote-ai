"""The desktop console (web-app/ → web/console/) on the real relay and the Windows agent just built, with made-up
homes of Claude Code and Codex and stand-ins for both (tests/made_up.py, tests/chat_standin.py): nothing real is
listed or started. A conversation is listed once however it runs; a saved one is gone on with by a message, as a
chat; a chat is shown on its tool's own screen and back with the same conversation (`reopen`); menus, the
hand-over, the new conversation's project picker and the branch menu are on top of what lies under them; a file's
changes open in the middle; a narrow window shows the list or what was opened from it.

    python tests/console_ui_check.py <chrome.exe> <folder for the pictures> [<folder of RemoteCliAgent.exe>]
"""
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))
sys.path.insert(0, str(ROOT / "tools"))
from made_up import Homes          # noqa: E402
import readme_pictures as pictures  # noqa: E402

PASSWORD = "console-1234"
WIDE = dict(width=1440, height=900, deviceScaleFactor=1, mobile=False)
SAVED = "11111111-2222-3333-4444-555555555555"


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def main():
    chrome, out = sys.argv[1], Path(sys.argv[2])
    program = Path(sys.argv[3] if len(sys.argv) > 3 else ROOT / "agent-windows" / "bin") / "RemoteCliAgent.exe"
    out.mkdir(parents=True, exist_ok=True)
    # (a short folder: the made-up homes name projects by their whole path)
    with tempfile.TemporaryDirectory(prefix="rc-", ignore_cleanup_errors=True) as temporary:
        base = Path(temporary)
        data, shop, notes = base / "agent", base / "web-shop", base / "notes"
        for folder in (data, shop, notes):
            folder.mkdir()
        (shop / "docs").mkdir()
        (shop / "docs" / "notes.md").write_text("# 结算说明\n\n按分计算金额，避免浮点小数误差。\n", encoding="utf-8")
        (shop / "a-old.txt").write_text("older note\n", encoding="utf-8")
        (shop / "app.js").write_text("export const total = items => items.reduce((a, b) => a + b.price, 0);\n", encoding="utf-8")
        for step in (["init", "-q"], ["add", "."], ["commit", "-qm", "init"]):
            subprocess.run(["git", "-c", "user.email=check@example.com", "-c", "user.name=check"] + step, cwd=shop, check=True)
        old_ts = time.time() - 72 * 3600
        os.utime(shop / "a-old.txt", (old_ts, old_ts))
        os.utime(shop / "docs" / "notes.md", (old_ts, old_ts))
        os.utime(shop / "docs", (old_ts, old_ts))
        (shop / "app.js").write_text("export const total = items => items.reduce((a, b) => a + b.price * b.count, 0);\n", encoding="utf-8")
        homes = Homes(temporary)
        long_thought = "\n".join(f"思考步骤 {i}：核对结算金额舍入与分单位换算边界条件" for i in range(1, 21))
        homes.claude_saved(shop, SAVED, "结算页金额多一位小数", 0.05, said=("结算页的金额有时多一位小数", "已经改成按分计算。"),
                           worked=({"type": "thinking", "thinking": long_thought},))
        homes.codex_saved(shop, "019a0000-0000-7000-8000-000000000001", "补充单元测试", 0.1)
        homes.codex_saved(notes, "019a0000-0000-7000-8000-000000000002", "整理周报", 50)
        model_log = base / "model.jsonl"
        prompt_log = base / "prompts.jsonl"
        environment = homes.environment(RCLI_PASSWORD=PASSWORD, STANDIN_MODEL_LOG=str(model_log),
                                        STANDIN_SAVE_TRANSCRIPT="1", STANDIN_PROMPT_LOG=str(prompt_log))
        (data / "quotas").mkdir()
        (data / "quotas" / "claude.json").write_text(json.dumps({
            "tool": "claude", "why": "", "at": int(time.time() * 1000),
            "windows": [dict(name=str(i), used=n, resets=0) for i, n in enumerate((50, 50.1, 79.9, 80))],
        }), encoding="utf-8")
        tools = homes.stand_ins(temporary)
        for name in ("claude", "codex"):
            (tools / (name + ".cmd")).write_text('@if "%%1"=="--help" (echo stand-in) else ("%s" "%s" %s %%*)\r\n'
                                                 % (sys.executable, ROOT / "tests" / "chat_standin.py", name), encoding="mbcs")
        environment["PATH"] = str(tools) + ";" + environment["PATH"]
        port = free_port()
        (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % port, "RemoteEnabled": True, "RemoteMaxMode": "full",
                                                      "RemoteDirs": ["web-shop=" + str(shop), "notes=" + str(notes)]}), encoding="utf-8")
        relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--host", "127.0.0.1", "--port", str(port), "--data", str(base / "relay"),
                                  "--web", str(ROOT / "web")], env=environment, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run([str(program), "--set-password", str(data)], input=PASSWORD.encode(), check=True)
        agent = subprocess.Popen([str(program), "--data", str(data)], env=environment)
        browser = None
        try:
            browser = pictures.Browser(chrome, temporary)
            value, until = browser.value, browser.until
            browser.ask("Page.addScriptToEvaluateOnNewDocument", source="window.__errors = []; addEventListener('error', e => __errors.push(String(e.message)));"
                        " addEventListener('unhandledrejection', e => __errors.push('rejected: ' + (e.reason && e.reason.message || e.reason)));")
            origin = "http://127.0.0.1:%d" % port
            q = json.dumps
            titles = "Array.from(document.querySelectorAll('.proj .th-row .th-title')).map(n => n.textContent)"
            press = lambda scope, text: until("(() => { const b = Array.from(document.querySelectorAll(%s)).find(b => b.textContent.trim() === %s && !b.disabled); if (!b) return false; b.click(); return true; })()"
                                              % (q(scope), q(text)), "press " + text)
            # the element at the middle of what is found is that element or within it: nothing lies over it
            on_top = lambda selector: value("(n => { if (!n) return false; const r = n.getBoundingClientRect(); const at = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2); return !!at && (n === at || n.contains(at)); })(document.querySelector(%s))" % q(selector))
            listed = lambda: value("fetch('../api/terminal').then(r => r.json()).then(j => j.terminals.filter(t => t.state === 'running').map(t => [t.id, t.form || '', t.session || '']))")
            errors = lambda: value("JSON.stringify(window.__errors)")

            # ---- the list: conversations by project, each once; today badges and sidebar filters
            browser.open(origin + "/console/#p=" + PASSWORD, "document.querySelectorAll('.proj .th-row').length === 3", "the console's list", **WIDE)
            assert sorted(value(titles)) == sorted(["整理周报", "补充单元测试", "结算页金额多一位小数"]), value(titles)
            assert value("document.querySelectorAll('.proj-name').length") == 2
            assert value("Array.from(document.querySelectorAll('.proj .th-row')).filter(n => n.querySelector('.rec-tag')).map(n => n.querySelector('.th-title').textContent).sort()") == sorted(["补充单元测试", "结算页金额多一位小数"])
            assert value("Array.from(document.querySelectorAll('.proj')).find(n => n.querySelector('.proj-name')?.textContent === 'web-shop')?.querySelector('.proj-today')?.textContent") == "今日 2"
            assert not value("Array.from(document.querySelectorAll('.proj')).find(n => n.querySelector('.proj-name')?.textContent === 'notes')?.querySelector('.proj-today')")
            value("Array.from(document.querySelectorAll('.side-filters .filter-pill')).find(b => b.textContent.startsWith('今日')).click()")
            until("document.querySelectorAll('.proj .th-row').length === 2", "today sidebar filter")
            assert sorted(value(titles)) == sorted(["补充单元测试", "结算页金额多一位小数"]), value(titles)
            value("Array.from(document.querySelectorAll('.side-filters .filter-pill')).find(b => b.textContent.startsWith('全部')).click()")
            until("document.querySelectorAll('.proj .th-row').length === 3", "all sidebar filter restored")
            home_geom = value("(() => { const kpis = Array.from(document.querySelectorAll('.home-kpi-strip .kpi-val')).map(n => n.textContent); const cards = Array.from(document.querySelectorAll('.home-grid .home-card')).map(n => { const r = n.getBoundingClientRect(); return [Math.round(r.width), Math.round(r.height)]; }); const earlier = Array.from(document.querySelectorAll('.home-list-row .hl-title')).map(n => n.textContent); const stripes = [...document.querySelectorAll('.th-row, .home-card')].filter(n => getComputedStyle(n, '::before').content !== 'none').length; return { kpis, cards, earlier, stripes }; })()")
            assert home_geom["kpis"] == ["0", "0", "2", "3"], home_geom
            assert len(home_geom["cards"]) == 2 and abs(home_geom["cards"][0][0] - home_geom["cards"][1][0]) <= 2 and abs(home_geom["cards"][0][1] - home_geom["cards"][1][1]) <= 2, home_geom
            assert home_geom["earlier"] == ["整理周报"], home_geom
            assert home_geom["stripes"] == 0, ("no left accent stripes on rows/cards", home_geom)
            assert value("document.querySelectorAll('.home-hub .hub-tile').length") >= 3, "overview task hub shows teams, schedules and chains"
            browser.picture(out / "list.png")
            # Multi-select batch mode right in Overview
            press(".home-head-acts button", "多选")
            until("!!document.querySelector('.home-batch-bar')", "overview batch selection bar")
            value("document.querySelectorAll('.home-grid .home-card')[0].click()")
            value("document.querySelectorAll('.home-grid .home-card')[1].click()")
            until("document.querySelector('.batch-count')?.textContent === '已选 2 个对话' && document.querySelectorAll('.home-card.is-picked .hc-check.on').length === 2", "two overview cards selected")
            time.sleep(0.2)
            browser.picture(out / "batch.png")
            press(".home-batch-bar button", "完成")
            until("!document.querySelector('.home-batch-bar')", "exited multi-select mode")
            # Schedule creation & management directly in Overview
            press(".home-task-bar button", "定时任务")
            until("!!document.querySelector('.home-sec h2') && document.querySelector('.home-sec h2').textContent.includes('定时提问')", "schedules tab opened")
            press(".home-sec-head button", "新建定时")
            until("!!document.querySelector('.dlg textarea')", "schedule creation dialog")
            value("(t => { t.value = '每天检查测试结果'; t.dispatchEvent(new Event('input', { bubbles: true })); })(document.querySelector('.dlg textarea'))")
            press(".dlg-foot button", "保存")
            until("!document.querySelector('.dlg') && document.querySelectorAll('.auto-grid .auto-card').length === 1", "created schedule card visible")
            browser.picture(out / "schedules.png")
            press(".home-task-bar button", "全部概览")
            until("document.querySelectorAll('.home-grid .home-card').length === 2", "back to overview")
            press(".side-foot button", "设置")
            until("document.querySelectorAll('.quota-bar').length >= 4", "account quota windows")
            assert not value("document.querySelector('.dlg a[href*=\"/computer/\"]')"), "settings dialog must never navigate away to mobile /computer/"
            assert value("Array.from(document.querySelectorAll('.dlg h3')).some(h => h.textContent.includes('电脑端与更新'))"), "settings dialog shows host & update section"
            assert value("Array.from(document.querySelectorAll('.quota-bar')).slice(0,4).map(n => n.className)") == [
                "quota-bar", "quota-bar elevated", "quota-bar elevated", "quota-bar high"]
            colors = value("Array.from(document.querySelectorAll('.quota-bar span')).slice(0,4).map(n => getComputedStyle(n).backgroundColor.match(/\\d+/g).map(Number))")
            assert colors[0][2] > colors[0][0] and colors[3][0] > colors[3][2], colors
            value("document.querySelector('.dlg-head button').click()")

            # ---- a right click on a conversation: its menu, with the reasons of what cannot be done now
            value("(n => n.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 120, clientY: 300 })))(Array.from(document.querySelectorAll('.th-row')).find(n => n.textContent.includes('结算页')))")
            until("!!document.querySelector('.ctx-menu')", "the conversation's menu")
            items = value("Array.from(document.querySelectorAll('.ctx-item .ctx-label')).map(n => n.textContent)")
            assert "交给另一个 AI…" in items and "在终端界面中继续" in items and "停止这一轮" in items, items
            assert value("Array.from(document.querySelectorAll('.ctx-item')).find(n => n.textContent.startsWith('停止这一轮')).disabled"), "a stop of what does not run is greyed"
            assert on_top(".ctx-menu .ctx-item"), "the menu is on top"
            value("window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))")
            until("!document.querySelector('.ctx-menu')", "the menu closed")

            # ---- a saved conversation is gone on with by a message: a chat that runs it, listed once; thought expands without clipping
            value("Array.from(document.querySelectorAll('.proj .th-row')).find(n => n.textContent.includes('结算页')).click()")
            until("document.querySelectorAll('.tl-item').length === 3 && !!document.querySelector('.thought-card')", "its saved messages with thought")
            assert value("document.querySelector('.thought-head').getAttribute('aria-expanded')") == "false"
            assert not value("document.querySelector('.thought-body')")
            value("document.querySelector('.thought-head').click()")
            until("document.querySelector('.thought-head')?.getAttribute('aria-expanded') === 'true' && !!document.querySelector('.thought-body.is-full')", "expanded full thought")
            thought_info = value("(() => { const b = document.querySelector('.thought-body.is-full'); const cs = getComputedStyle(b); return { text: b.textContent, maxHeight: cs.maxHeight, clipped: b.scrollHeight > b.clientHeight + 2 }; })()")
            assert "思考步骤 1：" in thought_info["text"] and "思考步骤 20：" in thought_info["text"], thought_info
            assert thought_info["maxHeight"] == "none" and not thought_info["clipped"], thought_info
            assert value("!!document.querySelector('.thought-foot')"), "long thought has bottom collapse button"
            browser.picture(out / "thought.png")
            value("document.querySelector('.thought-foot').click()")
            until("document.querySelector('.thought-head')?.getAttribute('aria-expanded') === 'false' && !document.querySelector('.thought-body')", "collapsed thought")
            until("!!document.querySelector('.thread-head .branch .chip')", "the project's branch")
            assert on_top(".thread-head .branch .chip"), "the branch is reachable"
            value("document.querySelector('.thread-head .branch .chip').click()")
            until("!!document.querySelector('.branch-pop')", "the branch menu")
            assert on_top(".branch-pop .pop-item"), "the branch menu is on top of the conversation"
            value("document.querySelector('.thread-head .branch .chip').click()")
            value("(b => { b.focus(); b.value = '再检查一下优惠券'; b.dispatchEvent(new Event('input', { bubbles: true })); })(document.querySelector('.composer textarea'))")
            value("document.querySelector('.composer textarea').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))")
            until("!!document.querySelector('.ask-card')", "the chat that goes on with it, asking first")
            texts = value("Array.from(document.querySelectorAll('.tl-item')).map(n => n.innerText.trim().split('\\n')[0])")
            assert texts[0] == "结算页的金额有时多一位小数" and "已经改成按分计算。" in texts and "再检查一下优惠券" in texts and "我先看看。" in texts, texts
            assert value(titles).count("结算页金额多一位小数") == 1, value(titles)
            chats = [t for t in listed() if t[2] == SAVED]
            assert len(chats) == 1 and chats[0][1] == "chat", chats
            browser.picture(out / "chat.png")
            press(".ask-card button", "允许")
            until("Array.from(document.querySelectorAll('.thread-head .seg button')).some(b => b.textContent === '终端' && !b.disabled)", "the turn ended: it can be shown the other way")

            # Switch the model from the conversation and check the actual tool invocation.
            # the button at the end of the box: AI tabs on top, and only the selected AI's models below
            value("document.querySelector('.composer .tool-switch-btn').click()")
            until("!!document.querySelector('.tool-switch-pop .tsp-row[data-model=\"sonnet\"]') && document.querySelectorAll('.tool-switch-pop .tsp-tool-tab').length >= 2", "the tool's model list with AI tabs")
            tabs = value("Array.from(document.querySelectorAll('.tool-switch-pop .tsp-tool-tab')).map(n => n.dataset.tool)")
            assert "claude" in tabs and "codex" in tabs, tabs
            assert value("document.querySelector('.tool-switch-pop .tsp-head span').textContent") == "Claude Code"
            browser.picture(out / "switch.png")
            value("document.querySelector('.tool-switch-pop .tsp-row[data-model=\"sonnet\"]').click()")
            until("!document.querySelector('.tool-switch-pop') && document.querySelector('.tool-switch-btn')?.textContent.includes('Sonnet')", "the original chat on the new model")
            until("fetch('../api/terminal').then(r=>r.json()).then(j=>j.terminals.some(t=>t.state==='running' && t.model==='sonnet' && t.session===%s))" % q(SAVED), "running model")
            def compose(text, button="发送"):
                value("(b => { b.value = %s; b.dispatchEvent(new Event('input', {bubbles:true})); })(document.querySelector('.composer textarea'))" % q(text))
                press(".composer button", button)

            compose("慢一点执行")
            until("document.querySelector('.run-status')?.textContent.includes('等待确认')", "waiting for confirmation")
            launches = [json.loads(line) for line in model_log.read_text(encoding="utf-8").splitlines()]
            assert any(a[a.index("--resume") + 1] == SAVED and "--model" in a and a[a.index("--model") + 1] == "sonnet" for a in launches if "--resume" in a)
            press(".ask-card button", "允许")
            until("!!document.querySelector('[role=timer]')", "elapsed running time")
            first = value("document.querySelector('[role=timer]').textContent")
            until("!!document.querySelector('[role=timer]') && document.querySelector('[role=timer]').textContent !== %s" % q(first), "clock ticks")
            # at work the model is not changed: the list says why
            value("document.querySelector('.composer .tool-switch-btn').click()")
            until("Array.from(document.querySelectorAll('.tool-switch-pop .tsp-note')).some(n => n.textContent.includes('这一轮结束'))", "why the model waits")
            value("document.querySelector('.composer .tool-switch-btn').click()")
            compose("随后检查第二项", "加入待发")
            compose("顺便核对第三项", "加入待发")
            compose("临时误发", "加入待发")
            until("document.querySelectorAll('.pending-list li').length === 3 && document.querySelector('.pending-panel')?.textContent.includes('合并一次发出')", "three stacked pending messages")
            value("Array.from(document.querySelectorAll('.pending-list li')).find(li => li.textContent.includes('临时误发')).querySelector('.pending-del').click()")
            until("document.querySelectorAll('.pending-list li').length === 2", "removed mistaken pending message")
            value("Array.from(document.querySelectorAll('.proj .th-row')).find(n=>n.textContent.includes('补充单元测试')).click()")
            until("document.querySelector('.head-title')?.textContent === '补充单元测试' && !document.querySelector('.pending-panel')", "another conversation has its own status")
            value("Array.from(document.querySelectorAll('.proj .th-row')).find(n=>n.textContent.includes('结算页')).click()")
            until("document.querySelectorAll('.pending-list li').length === 2 && document.querySelector('.pending-panel')?.textContent.includes('随后检查第二项') && document.querySelector('.pending-panel')?.textContent.includes('顺便核对第三项') && !!document.querySelector('[role=timer]')", "pending batch and timer restored")
            browser.picture(out / "pending.png")
            press(".pending-head button", "停止并合并发送")
            until("!document.querySelector('.pending-panel') && Array.from(document.querySelectorAll('.tl-item')).some(n => n.textContent.includes('随后检查第二项') && n.textContent.includes('顺便核对第三项'))", "merged batch sent as one user message after stop")
            press(".ask-card button", "允许")
            until("document.querySelector('.run-status')?.textContent === '等待消息' && !document.querySelector('[role=timer]')", "the clock stops")

            # Stop a running turn and immediately dispatch a new instruction while pausing any old queued items.
            compose("慢一点再跑")
            until("document.querySelector('.run-status')?.textContent.includes('等待确认')", "second slow confirmation")
            press(".ask-card button", "允许")
            until("!!document.querySelector('[role=timer]')", "second slow running")
            compose("待会再做的旧项", "加入待发")
            until("document.querySelector('.pending-panel')?.textContent.includes('待会再做的旧项')", "old pending queued")
            compose("先改这个紧急项", "停止并发送")
            until("!!document.querySelector('.pending-group.is-paused') && Array.from(document.querySelectorAll('.tl-item')).some(n => n.textContent.includes('先改这个紧急项'))", "stopped and ran new instruction while pausing old queue")
            press(".ask-card button", "允许")
            until("document.querySelector('.run-status')?.textContent === '等待消息' && !document.querySelector('[role=timer]')", "finished emergency instruction")
            press(".pending-group.is-paused button", "清空")
            until("!document.querySelector('.pending-panel')", "cleared paused queue")

            # ---- the same conversation on its tool's own screen, and back: one terminal at a time, the same conversation
            press(".thread-head .seg button", "终端")
            until("!!document.querySelector('.screen-frame')", "its own screen")
            until("fetch('../api/terminal').then(r => r.json()).then(j => j.terminals.filter(t => t.state === 'running' && t.session === %s).map(t => t.form || 'screen').join() === 'screen')" % q(SAVED),
                  "one terminal runs it, on its screen")
            running = [t for t in listed() if t[2] == SAVED]
            assert len(running) == 1 and running[0][0] != chats[0][0], running
            assert value(titles).count("结算页金额多一位小数") == 1
            browser.picture(out / "screen.png")
            until("Array.from(document.querySelectorAll('.thread-head .seg button')).some(b => b.textContent === '对话' && !b.disabled)", "it can be a chat again")
            press(".thread-head .seg button", "对话")
            until("!document.querySelector('.screen-frame') && document.querySelectorAll('.tl-item').length >= 2", "a chat again")
            until("fetch('../api/terminal').then(r => r.json()).then(j => j.terminals.filter(t => t.state === 'running' && t.session === %s).map(t => t.form || 'screen').join() === 'chat')" % q(SAVED),
                  "one terminal runs it, as a chat")

            # ---- the hand-over: a new conversation of another AI, or one that is open
            press(".thread-head button", "交接")
            until("!!document.querySelector('.dlg .pick')", "the hand-over")
            offers = value("Array.from(document.querySelectorAll('.dlg .pick strong')).map(n => n.textContent)")
            assert offers[0] == "新对话 · Codex", offers
            assert on_top(".dlg .pick"), "the dialog is on top"
            browser.picture(out / "handoff.png")
            press(".dlg-foot button", "取消")

            # ---- in-place AI switch (Claude -> Codex): same conversation, switch divider, clean user bubble, single list item
            value("document.querySelector('.composer .tool-switch-btn').click()")
            until("!!document.querySelector('.tool-switch-pop .tsp-tool-tab[data-tool=\"codex\"]')", "Codex tab in switcher")
            value("document.querySelector('.tool-switch-pop .tsp-tool-tab[data-tool=\"codex\"]').click()")
            until("document.querySelector('.tool-switch-pop .tsp-head span')?.textContent === 'Codex'", "Codex models shown")
            value("document.querySelector('.tool-switch-pop .tsp-row[data-model=\"\"]').click()")
            until("!document.querySelector('.tool-switch-pop') && document.querySelector('.switch-banner')?.textContent.includes('Codex')", "staged in-place switch banner")
            compose("继续核对优惠券边界")
            until("!!document.querySelector('.timeline-switch') && !!document.querySelector('.ask-card')", "Codex took over in-place with switch divider")
            assert "以下由 Codex 继续" in value("document.querySelector('.timeline-switch').textContent")
            user_bubbles = value("Array.from(document.querySelectorAll('.msg-user .msg-bubble')).map(n => n.textContent)")
            assert "继续核对优惠券边界" in user_bubbles and not any("【对话交接】" in b for b in user_bubbles), user_bubbles
            press(".ask-card button", "允许")
            until("document.querySelector('.run-status')?.textContent === '等待消息'", "Codex finished handover turn")
            until("Array.from(document.querySelectorAll('.proj .th-row .th-title')).filter(n => n.textContent === '结算页金额多一位小数').length === 1", "handed-over conversation still listed once under original title")
            assert value("document.querySelectorAll('.proj .th-row').length") == 3, (value(titles), value("fetch('../api/terminal').then(r=>r.json()).then(j=>({terminals:j.terminals.map(t=>({id:t.id,tool:t.tool,state:t.state,session:t.session,title:t.title})),sessions:j.sessions.map(s=>({id:s.id,tool:s.tool,title:s.title,live:s.live}))}))"))
            browser.picture(out / "handover-inline.png")

            # ---- switch back in-place (Codex -> Claude): resumes SAVED Claude session with delta summary in chronological order
            until("fetch('../api/terminal').then(r => r.json()).then(j => j.sessions.some(s => s.tool === 'codex' && s.title === '结算页金额多一位小数'))",
                  "Codex handover session indexed in relay sessions")
            value("document.querySelector('.composer .tool-switch-btn').click()")
            until("!!document.querySelector('.tool-switch-pop .tsp-tool-tab[data-tool=\"claude\"]')", "Claude tab in switcher")
            value("document.querySelector('.tool-switch-pop .tsp-tool-tab[data-tool=\"claude\"]').click()")
            until("document.querySelector('.tool-switch-pop .tsp-head span')?.textContent === 'Claude Code'", "Claude models shown")
            value("document.querySelector('.tool-switch-pop .tsp-row[data-model=\"\"]').click()")
            until("!document.querySelector('.tool-switch-pop') && document.querySelector('.switch-banner')?.textContent.includes('Claude Code')", "staged switch-back banner")
            compose("回到 Claude 汇总确认")
            until("document.querySelectorAll('.timeline-switch').length === 2 && !!document.querySelector('.ask-card')", "Claude resumed in-place with second switch divider")
            press(".ask-card button", "允许")
            until("document.querySelector('.run-status')?.textContent === '等待消息'", "Claude finished round-trip handover turn")

            dividers = value("Array.from(document.querySelectorAll('.timeline-switch')).map(n => n.textContent)")
            assert len(dividers) == 2 and "以下由 Codex 继续" in dividers[0] and "以下由 Claude Code 继续" in dividers[1], dividers
            timeline_rows = value("Array.from(document.querySelectorAll('.timeline-switch, .msg-user .msg-bubble')).map(n => n.textContent)")
            idx_early = next(i for i, t in enumerate(timeline_rows) if "再检查一下优惠券" in t)
            idx_div_codex = next(i for i, t in enumerate(timeline_rows) if "以下由 Codex 继续" in t)
            idx_codex = next(i for i, t in enumerate(timeline_rows) if "继续核对优惠券边界" in t)
            idx_div_claude = next(i for i, t in enumerate(timeline_rows) if "以下由 Claude Code 继续" in t)
            idx_claude = next(i for i, t in enumerate(timeline_rows) if "回到 Claude 汇总确认" in t)
            assert idx_early < idx_div_codex < idx_codex < idx_div_claude < idx_claude, timeline_rows
            user_bubbles_2 = value("Array.from(document.querySelectorAll('.msg-user .msg-bubble')).map(n => n.textContent)")
            assert "继续核对优惠券边界" in user_bubbles_2 and "回到 Claude 汇总确认" in user_bubbles_2 and not any("【对话交接】" in b for b in user_bubbles_2), user_bubbles_2
            assert value("document.querySelectorAll('.proj .th-row').length") == 3
            assert value("Array.from(document.querySelectorAll('.proj .th-row .th-title')).filter(n => n.textContent === '结算页金额多一位小数').length") == 1
            until("fetch('../api/terminal').then(r => r.json()).then(j => j.terminals.some(t => t.state === 'running' && t.tool === 'claude' && t.session === %s) && j.sessions.some(s => s.tool === 'claude' && s.id === %s && s.title === '结算页金额多一位小数'))" % (q(SAVED), q(SAVED)),
                  "resumed Claude terminal running with SAVED session and original title")
            browser.picture(out / "handover-roundtrip.png")

            disk_read = value("fetch('../api/conversation', { method: 'POST', credentials: 'same-origin', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({ id: '9'.repeat(32), session: %s }) }).then(r => r.json())" % q(SAVED))
            disk_roles = [(m.get("role"), m.get("tool")) for m in disk_read.get("messages", [])]
            assert [m for m in disk_roles if m[0] == "switch"] == [("switch", "codex"), ("switch", "claude")], disk_roles

            value("Array.from(document.querySelectorAll('.proj .th-row')).find(n => n.textContent.includes('补充单元测试')).click()")
            until("document.querySelector('.head-title')?.textContent === '补充单元测试'", "switched to another thread")
            value("Array.from(document.querySelectorAll('.proj .th-row')).find(n => n.textContent.includes('结算页')).click()")
            until("document.querySelector('.head-title')?.textContent === '结算页金额多一位小数' && document.querySelectorAll('.timeline-switch').length === 2", "returning still shows both chronological dividers")

            prompts = [json.loads(line) for line in prompt_log.read_text(encoding="utf-8").splitlines() if line.strip()]
            claude_prompts = [p for p in prompts if p.get("tool") == "claude"]
            assert claude_prompts, prompts
            last_claude = claude_prompts[-1]
            assert "--resume" in last_claude["args"] and last_claude["args"][last_claude["args"].index("--resume") + 1] == SAVED, last_claude["args"]
            assert last_claude["text"].startswith("【对话交接】上面你暂停之后，我切到 Codex"), last_claude["text"]
            assert "继续核对优惠券边界" in last_claude["text"] and "【现在的要求】\n回到 Claude 汇总确认" in last_claude["text"], last_claude["text"]
            assert "下面是我在另一段对话里的进展" not in last_claude["text"], last_claude["text"]

            # ---- a new conversation: the project picker opens over the page and chooses
            value("document.querySelector('.new-btn').click()")
            until("!!document.querySelector('.new-page')", "the new conversation")
            value("document.querySelector('.new-field .cselect-trigger').click()")
            until("!!document.querySelector('.cselect-menu')", "the projects")
            assert on_top(".cselect-menu .cselect-item:last-child"), "the project picker is not covered"
            press(".cselect-menu .cselect-item-label", "notes")
            assert value("document.querySelector('.new-field .cselect-val').textContent") == "notes"
            browser.picture(out / "new.png")

            # ---- a file's changes and file explorer recency/git badges, sort, filter, direct diff, and per-thread file memory
            value("Array.from(document.querySelectorAll('.proj .th-row')).find(n => n.textContent.includes('结算页')).click()")
            until("document.querySelector('.head-title')?.textContent === '结算页金额多一位小数'", "back to web-shop thread")
            press(".insp-head .seg button", "文件")
            until("document.querySelectorAll('.file-row-wrap').length >= 3 && !!document.querySelector('.git-st-btn.st-M')", "files with git & recency badges")
            file_names = lambda: value("Array.from(document.querySelectorAll('.file-row-wrap .file-name')).map(n => n.textContent)")
            assert file_names()[:3] == ["docs", "a-old.txt", "app.js"], file_names()
            assert value("Array.from(document.querySelectorAll('.file-row-wrap')).find(n => n.querySelector('.file-name')?.textContent === 'app.js')?.querySelector('.rec-tag')?.textContent") == "刚改"
            assert not value("Array.from(document.querySelectorAll('.file-row-wrap')).find(n => n.querySelector('.file-name')?.textContent === 'a-old.txt')?.querySelector('.rec-tag')")
            assert value("Array.from(document.querySelectorAll('.file-row-wrap')).filter(n => getComputedStyle(n, '::before').content !== 'none').length") == 0, "no left accent stripes on file rows"
            value("document.querySelector('.files-sort-btn').click()")
            until("Array.from(document.querySelectorAll('.file-row-wrap .file-name')).map(n => n.textContent)[1] === 'app.js'", "sorted by recent modified")
            value("Array.from(document.querySelectorAll('.files-controls .filter-pill')).find(b => b.textContent.startsWith('今日')).click()")
            until("document.querySelectorAll('.file-row-wrap').length === 1", "today file filter")
            assert file_names() == ["app.js"], file_names()
            browser.picture(out / "files.png")
            value("Array.from(document.querySelectorAll('.files-controls .filter-pill')).find(b => b.textContent.startsWith('全部')).click()")
            until("document.querySelectorAll('.file-row-wrap').length >= 3", "all files restored")

            # Open docs/notes.md in the right pane, switch to another project's conversation and back: preview & folder must be remembered.
            value("Array.from(document.querySelectorAll('.file-row-wrap .file-row')).find(b => b.querySelector('.file-name')?.textContent === 'docs').click()")
            until("Array.from(document.querySelectorAll('.file-row-wrap .file-name')).some(n => n.textContent === 'notes.md')", "entered docs folder")
            value("Array.from(document.querySelectorAll('.file-row-wrap .file-row')).find(b => b.querySelector('.file-name')?.textContent === 'notes.md').click()")
            until("document.querySelector('.fpreview-name')?.textContent === 'notes.md' && document.querySelector('.fpreview-md')?.textContent.includes('结算说明')", "opened notes.md preview")
            value("Array.from(document.querySelectorAll('.proj .th-row')).find(n => n.textContent.includes('整理周报')).click()")
            until("document.querySelector('.head-title')?.textContent === '整理周报' && !document.querySelector('.fpreview')", "switched to notes project thread")
            value("Array.from(document.querySelectorAll('.proj .th-row')).find(n => n.textContent.includes('结算页')).click()")
            until("document.querySelector('.head-title')?.textContent === '结算页金额多一位小数' && document.querySelector('.fpreview-name')?.textContent === 'notes.md' && document.querySelector('.fpreview-md')?.textContent.includes('结算说明')", "restored notes.md preview on returning to thread")
            press(".insp-head .seg button", "变更")
            until("!!document.querySelector('.fc-main')", "switched to changes tab")
            press(".insp-head .seg button", "文件")
            until("document.querySelector('.fpreview-name')?.textContent === 'notes.md'", "restored notes.md preview across inspector tab switch")
            press(".fpreview-head button", "← 返回目录")
            until("document.querySelector('.crumb-btn.current')?.textContent === 'docs' && Array.from(document.querySelectorAll('.file-row-wrap .file-name')).some(n => n.textContent === 'notes.md')", "returned to docs subfolder instead of root")
            value("document.querySelector('.files-crumb .crumb-btn').click()")
            until("!!document.querySelector('.git-st-btn.st-M')", "returned to project root")
            value("document.querySelector('.git-st-btn.st-M').click()")
            until("document.querySelectorAll('.d-line.k-add').length === 1 && document.querySelectorAll('.d-line.k-del').length === 1", "the file's changes from explorer git badge")
            browser.picture(out / "diff.png")
            assert errors() == "[]", errors()

            # ---- a compact desktop window (e.g. half-screen / high-DPI): sidebar and main console stay side by side
            browser.open(origin + "/console/", "document.querySelectorAll('.proj .th-row').length >= 3", "the console, compact desktop", width=780, height=700, deviceScaleFactor=1, mobile=False)
            assert value("getComputedStyle(document.querySelector('.col-side')).display") != "none"
            assert value("getComputedStyle(document.querySelector('.col-main')).display") != "none"

            # ---- a narrow window: the list, or what was opened from it
            browser.open(origin + "/console/", "document.querySelectorAll('.proj .th-row').length >= 3", "the list, narrow", width=390, height=844, deviceScaleFactor=1, mobile=True)
            assert value("getComputedStyle(document.querySelector('.col-main')).display") == "none"
            value("Array.from(document.querySelectorAll('.proj .th-row')).find(n => n.textContent.includes('补充单元测试')).click()")
            until("getComputedStyle(document.querySelector('.col-side')).display === 'none' && !!document.querySelector('.thread-head')", "the conversation, narrow")
            assert value("document.documentElement.scrollWidth <= innerWidth"), "nothing wider than the window"
            browser.picture(out / "narrow.png")
            assert errors() == "[]", errors()
        finally:
            if browser:
                browser.close()
            for process in (agent, relay):
                process.kill()
            homes.close()
    print("console: conversations listed once by project; menus with reasons; a saved one gone on with as a chat; shown on its own screen and back with the same conversation; hand-over; project picker and branch menu on top; a file's changes; narrow window")


if __name__ == "__main__":
    main()
