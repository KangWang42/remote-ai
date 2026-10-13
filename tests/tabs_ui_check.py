"""The four tabs of the phone's list page, with the real pages (web/), the real relay and a computer that is played
(tools/readme_pictures.py): each tab shows its own part and nothing lies under the tabs; 协作 sets, changes and
removes what the computer keeps (接力, 定时提问), hands a conversation over and asks several at once; 我的 draws the
numbers it is given; a computer of an earlier version is told so.

    python tests/tabs_ui_check.py <chrome.exe> <folder for the pictures>
"""
import json
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import readme_pictures as pictures      # noqa: E402
from readme_pictures import ASKING, FIXING, TABLES, EXPORT, THESIS      # noqa: E402

out = Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="remote-ai-tabs-", ignore_cleanup_errors=True) as temporary:
    played, browser = pictures.Played(temporary), pictures.Browser(sys.argv[1], temporary)
    try:
        value, until = browser.value, browser.until
        browser.ask("Emulation.setFocusEmulationEnabled", enabled=True)       # (a page that is not on a screen is told of focus too)
        browser.ask("Page.addScriptToEvaluateOnNewDocument", source="window.__errors = []; window.addEventListener('error', e => __errors.push(String(e.message)));"
                    " window.addEventListener('unhandledrejection', e => __errors.push('rejected: ' + (e.reason && e.reason.message || e.reason)));")
        phone = lambda width: dict(width=width, height=844, deviceScaleFactor=1, mobile=True)
        listed = "document.body.innerText.includes('部署到测试环境')"
        tab = lambda name: value("document.querySelector('#tabs button[data-tab=\"%s\"]').click()" % name)
        current = "document.querySelector('#tabs button[aria-current]').dataset.tab"
        shown = lambda selector: value("(n => !!n && n.getClientRects().length > 0)(document.querySelector(%s))" % json.dumps(selector))
        toast = "(t => t.hidden ? '' : t.textContent)(document.getElementById('toast'))"
        sheet = "document.querySelector('#sheet[open] #sheet-form')"
        press = lambda text: value("Array.from(%s.querySelectorAll('button')).find(b => b.textContent === %s).click()" % (sheet, json.dumps(text)))
        # a field of a sheet is filled in as a person would: its value, then the word that it changed
        fill = lambda field, text: value("(n => { n.value = %s; n.dispatchEvent(new Event('input', { bubbles: true })); n.dispatchEvent(new Event('change', { bubbles: true })); })(document.getElementById(%s))" % (json.dumps(text), json.dumps(field)))
        problem = "(%s.querySelector('.problem') || {}).textContent" % sheet
        asked = lambda action: [op for op in played.asked if op["action"] == action]

        # ---- the tabs: one part of the page each
        browser.open(played.origin + "/", listed, "the first page", **phone(390))
        assert value(current) == "tasks" and value("document.getElementById('heading').textContent") == "任务"
        assert shown("#sections") and not shown("#projects") and not shown("#together") and not shown("#me") and shown("#new-task") and not shown("#settings")
        assert value("document.getElementById('tab-count').textContent") == "1", "what waits for an answer is counted on its tab"
        # a search on 任务 finds the projects too, as before
        fill("query", "thesis")
        until("document.getElementById('projects').getClientRects().length > 0 && document.querySelectorAll('#project-list .card').length === 1", "the project a search names")
        fill("query", "")
        tab("projects")
        until("document.querySelectorAll('#project-list .card').length === 4", "the projects and the card that adds one")
        assert value(current) == "projects" and not shown("#sections") and shown("#project-list") and value("document.getElementById('heading').textContent") == "项目"
        value("Array.from(document.querySelectorAll('#project-list .card b')).find(b => b.textContent === 'web-shop').closest('button').click()")
        until("document.getElementById('heading').textContent === 'web-shop' && !document.getElementById('project').hidden", "one project")
        assert value(current) == "projects" and value("document.getElementById('overview').hidden")
        tab("projects")         # pressed again: back to the list
        until("document.getElementById('heading').textContent === '项目' && document.getElementById('project').hidden && !document.getElementById('overview').hidden", "the list of projects again")
        # the tab is kept: the page opened again is where it was; a link names another
        browser.open(played.origin + "/", "document.querySelectorAll('#project-list .card').length === 4", "the page again", **phone(390))
        assert value(current) == "projects"
        # typing: the tabs make room for the keyboard
        value("document.getElementById('query').focus()")
        assert not shown("#tabs")
        value("document.getElementById('query').blur()")
        assert shown("#tabs")

        # ---- 协作
        tab("together")
        until("document.querySelectorAll('#chains .card').length === 2 && document.querySelectorAll('#schedules .card').length === 2", "what the computer keeps, and the two cards that add")
        assert "整理第三章的参考文献 → 核对回归表的系数" in value("document.querySelector('#chains .card b').textContent") and "还剩 2 次" in value("document.querySelector('#chains .card').textContent")
        assert "每天 08:30" in value("document.querySelector('#schedules .card').textContent")
        browser.picture(out / "together.png")
        # 接力: refused while both ends are one conversation; then set, changed, removed
        value("document.querySelector('#chains .card.add button').click()")
        until(sheet + " && !!document.getElementById('chain-from')", "the sheet of a rule")
        time.sleep(0.5)         # (the sheet has risen)
        browser.picture(out / "chain-sheet.png")
        fill("chain-from", FIXING); fill("chain-to", FIXING); fill("chain-text", "审查上面的改动")
        press("保存")
        until(problem + " === '不能接力给同一段对话'", "what is wrong with it, said in the sheet")
        fill("chain-to", TABLES)
        value("Array.from(%s.querySelectorAll('.seg button')).find(b => b.textContent === '3').click()" % sheet)
        press("保存")
        until("document.querySelectorAll('#chains .card').length === 3 && !document.getElementById('sheet').open", "the new rule on the page")
        item = asked("work_chain_set")[-1]["item"]
        assert (item["from"], item["from_tool"], item["to"], item["to_tool"], item["text"], item["rounds"]) == (FIXING, "claude", TABLES, "claude", "审查上面的改动", 3), item
        value("Array.from(document.querySelectorAll('#chains .card:not(.add)')).find(c => c.textContent.includes('审查上面的改动')).querySelector('button.open').click()")
        until(sheet + " && document.getElementById('chain-text').value === '审查上面的改动' && document.getElementById('chain-from').value === '%s'" % FIXING, "the rule in its sheet, as it was set")
        press("删除")
        until("document.querySelectorAll('#chains .card').length === 2", "the rule gone")
        assert asked("work_chain_remove")[-1]["chain"] == item["id"]
        # 定时提问: every day at a time; switched off from its card; one whose time has passed is refused in the sheet
        value("document.querySelector('#schedules .card.add button').click()")
        until(sheet + " && !!document.getElementById('schedule-session')", "the sheet of a question")
        assert not value("document.getElementById('schedule-time').closest('.field').hidden") and value("document.getElementById('schedule-at').closest('.field').hidden")
        fill("schedule-session", TABLES); fill("schedule-text", "进展如何？"); fill("schedule-time", "21:45")
        time.sleep(0.5)         # (the sheet has risen)
        browser.picture(out / "schedule-sheet.png")
        press("保存")
        until("document.querySelectorAll('#schedules .card').length === 3", "the new question on the page")
        item = asked("work_schedule_set")[-1]["item"]
        assert (item["session"], item["tool"], item["text"], item["kind"], item["time"], item["on"]) == (TABLES, "claude", "进展如何？", "daily", "21:45", True), item
        value("Array.from(document.querySelectorAll('#schedules .card:not(.add)')).find(c => c.textContent.includes('进展如何')).querySelector('.side').click()")
        until("Array.from(document.querySelectorAll('#schedules .card')).some(c => c.textContent.includes('进展如何') && c.textContent.includes('已停用'))", "the question switched off")
        assert asked("work_schedule_set")[-1]["item"]["on"] is False
        value("document.querySelector('#schedules .card.add button').click()")
        until(sheet + " && !!document.getElementById('schedule-session')", "the sheet again")
        fill("schedule-session", EXPORT); fill("schedule-text", "只问一次")
        press("一次")
        assert not value("document.getElementById('schedule-at').closest('.field').hidden")
        fill("schedule-at", "2020-01-01T08:00")
        press("保存")
        until(problem + ".includes('时间需在现在之后')", "the computer's refusal, said in the sheet")
        assert value("document.getElementById('schedule-text').value") == "只问一次", "what was filled in stays"
        press("取消")
        # one conversation handed over to another
        value("document.getElementById('handoff').click()")
        until(sheet + " && !!document.getElementById('handoff-from')", "the sheet of a handover")
        saved = pictures.uuid(11)
        fill("handoff-from", saved); fill("handoff-to", FIXING); fill("handoff-note", "先看优惠券那一段")
        press("交接")
        until(toast + ".includes('已交给“修复结算页的金额显示”')", "the handover said")
        op = asked("work_handoff")[-1]
        assert (op["from"], op["from_tool"], op["to"], op["to_tool"], op["note"]) == (saved, "claude", FIXING, "claude", "先看优惠券那一段"), op
        # one question for several conversations
        value("document.getElementById('ask-many').click()")
        until(sheet + " && document.querySelectorAll('#sheet .check').length >= 3", "the conversations that can be told something")
        assert not value("!!document.getElementById('many-%s')" % ASKING), "one that asks a question is not told anything meanwhile"
        value("['%s', '%s'].forEach(id => document.getElementById('many-' + id).click())" % (TABLES, EXPORT))
        fill("many-text", "各自总结一下进度")
        time.sleep(0.5)         # (the sheet has risen)
        browser.picture(out / "ask-many.png")
        press("发送")
        until(toast + " === '已发给 2 段对话'", "both told")
        told = [op for op in asked("session_say") if op.get("text") == "各自总结一下进度"]
        assert sorted(op["session"] for op in told) == sorted([TABLES, EXPORT]), told
        # from a card: a held press offers the handover, and the conversation is the one it starts from
        tab("tasks")
        until("!!document.querySelector('#needs-list .card')", "the cards")
        value("Array.from(document.querySelectorAll('#overview .card')).find(c => c.textContent.includes('核对回归表的系数')).dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }))")
        until(sheet + " && Array.from(document.querySelectorAll('#sheet button')).some(b => b.textContent === '交接给另一段对话')", "what to do with a conversation")
        press("交接给另一段对话")
        until(sheet + " && (document.getElementById('handoff-from') || {}).value === '%s'" % TABLES, "the handover, from that conversation")
        press("取消")
        # a conversation's window still stands as it did: what the new pages are drawn with does not reach into it
        browser.open(played.origin + "/?tab=tasks&take=" + FIXING, "document.querySelector('#sheet .state') && document.querySelector('#sheet .said')?.textContent.includes('formatPrice')", "a conversation's window", **phone(390))
        window = value("(() => { const edge = sel => document.querySelector('#sheet ' + sel).getBoundingClientRect(); return { bar: edge('.bar').height, mode: edge('.control-mode').bottom, reads: edge('.reads').top, hand: !document.querySelector('#sheet .bar .hand').hidden }; })()")
        assert window["bar"] < 60 and window["mode"] <= window["reads"] and window["hand"], window
        value("document.querySelector('#sheet .bar .hand').click()")
        until(sheet + " && (document.getElementById('handoff-from') || {}).value === '%s'" % FIXING, "the handover from the window, of the conversation it shows")
        press("取消")

        # ---- 我的
        tab("me")
        until("document.querySelectorAll('#me .day').length === 7 && document.querySelectorAll('#me .tile').length === 4", "today in figures and the last seven days")
        assert shown("#settings") and not shown("#new-task") and asked("work_stats")[-1]["days"] == 7
        assert "次提问" in value("document.querySelector('#me .at').textContent") and "Claude Code" in value("document.querySelector('#me .shares').textContent")
        assert value("document.querySelector('#computer').textContent").count("1.6.0") == 1
        browser.picture(out / "me.png")
        value("Array.from(document.querySelectorAll('#period .section-head .seg button')).find(b => b.textContent === '30 天').click()")
        until("document.querySelectorAll('#me .day').length === 30", "thirty days")
        assert asked("work_stats")[-1]["days"] == 30
        value("document.querySelector('#me .chart-head .seg button:last-child').click()")
        until("document.querySelector('#me .at').textContent.includes('token')", "the days by tokens")
        before = value("document.querySelector('#me .at').textContent")
        value("document.querySelectorAll('#me .day')[3].click()")
        until("document.querySelector('#me .at').textContent !== %s && document.querySelectorAll('#me .day')[3].getAttribute('aria-pressed') === 'true'" % json.dumps(before), "the day that was pressed")

        # ---- every tab at three widths: nothing wider than the screen, the tabs at its foot, nothing left under them
        for width in (320, 390, 430):
            for name, ready in (("tasks", listed), ("projects", "document.querySelectorAll('#project-list .card').length === 4"),
                                ("together", "document.querySelectorAll('#chains .card').length >= 2"), ("me", "document.querySelectorAll('#me .day').length === 7")):
                browser.open(played.origin + "/?tab=" + name, ready, "%s at %d" % (name, width), **phone(width))
                value("scrollTo(0, document.documentElement.scrollHeight)")
                time.sleep(0.2)
                seen = value("""(() => {
                    const tabs = document.getElementById('tabs').getBoundingClientRect(), page = document.documentElement;
                    const parts = Array.from(document.querySelectorAll('#home > :not([hidden]) > :not([hidden]), #home > header')).filter(n => n.offsetParent);
                    return { inner: innerWidth, client: page.clientWidth, wide: Math.max(page.scrollWidth, document.body.scrollWidth), foot: Math.round(innerHeight - tabs.bottom), top: tabs.top,
                             last: Math.max(...parts.map(n => n.getBoundingClientRect().bottom)), high: tabs.height, buttons: Array.from(document.querySelectorAll('#tabs button')).map(b => Math.round(b.getBoundingClientRect().height)) };
                })()""")
                assert seen["inner"] == width and seen["client"] == width and seen["wide"] <= width, (name, width, seen)
                assert seen["foot"] == 0 and seen["last"] <= seen["top"] and min(seen["buttons"]) >= 48, (name, width, seen)
                if width == 320:
                    browser.picture(out / ("narrow-%s.png" % name))

        # ---- a computer of an earlier version: said on both tabs, and the lists do not pretend to be empty
        played.info["features"] = [feature for feature in played.info["features"] if feature != "work"]
        browser.open(played.origin + "/?tab=together", "document.querySelector('#together .notice') && !document.querySelector('#together .notice').hidden", "said that the computer is too old", **phone(390))
        assert "需要更新电脑端" in value("document.querySelector('#together .notice').textContent") and not shown("#chains")
        value("document.getElementById('handoff').click()")
        until(toast + ".includes('电脑端版本不支持交接')", "why nothing is handed over")
        tab("me")
        until("document.querySelector('#me .notice') && document.querySelector('#me .notice').textContent.includes('需要更新电脑端')", "the same on 我的")
        assert shown("#computer")
        errors = value("JSON.stringify(window.__errors)")
        assert errors == "[]", errors
    finally:
        browser.close(); played.close()
print("tabs: four tabs with a part of the page each, nothing under them at three widths; rules set, changed and removed; a handover, one question for several; the numbers drawn")
