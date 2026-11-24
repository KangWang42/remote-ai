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
        value = browser.value

        def until(code, what):
            try:
                browser.until(code, what)
            except AssertionError:
                # what the sheet showed when it was waited on in vain
                raise AssertionError((what, value("(d => d ? d.innerText.slice(0, 600) : 'no sheet')(document.querySelector('dialog.rs'))"), value("location.href"), value("JSON.stringify(window.__errors)")))
        browser.ask("Emulation.setFocusEmulationEnabled", enabled=True)       # (a page that is not on a screen is told of focus too)
        browser.ask("Page.addScriptToEvaluateOnNewDocument", source="window.__errors = []; window.addEventListener('error', e => __errors.push(String(e.message)));"
                    " window.addEventListener('unhandledrejection', e => __errors.push('rejected: ' + (e.reason && e.reason.message || e.reason)));")
        phone = lambda width: dict(width=width, height=844, deviceScaleFactor=1, mobile=True)
        listed = "document.body.innerText.includes('部署到测试环境')"
        tab = lambda name: value("document.querySelector('#tabs button[data-tab=\"%s\"]').click()" % name)
        current = "document.querySelector('#tabs button[aria-current]').dataset.tab"
        shown = lambda selector: value("(n => !!n && n.getClientRects().length > 0)(document.querySelector(%s))" % json.dumps(selector))
        toast = "(t => t.hidden ? '' : t.textContent)(document.getElementById('toast'))"
        sheet = "document.querySelector('dialog.rs[open] .rs-card')"
        closed = "!document.querySelector('dialog.rs:not(.closing)')"
        # a button of the sheet (ui/sheet.js), by the words it starts with; a conversation on a page of it, by its title
        press = lambda text: until("(b => !!b && !b.disabled && (b.click(), true))(Array.from(document.querySelectorAll('dialog.rs:not(.closing) button')).find(b => b.innerText.trim().startsWith(%s)))" % json.dumps(text), "press " + text)
        pick = lambda title: until("(r => !!r && (r.click(), true))(Array.from(document.querySelectorAll('dialog.rs:not(.closing) .rs-row')).find(r => r.innerText.includes(%s)))" % json.dumps(title), "pick " + title)
        title = lambda session: next(one["title"] for one in played.sessions if one["id"] == session)
        # a field of a sheet is filled in as a person would: its value, then the word that it changed
        fill = lambda field, text: value("(n => { n.value = %s; n.dispatchEvent(new Event('input', { bubbles: true })); n.dispatchEvent(new Event('change', { bubbles: true })); })(document.getElementById(%s))" % (json.dumps(text), json.dumps(field)))
        problem = "(document.querySelector('dialog.rs .rs-said') || {}).textContent"
        asked = lambda action: [op for op in played.asked if op["action"] == action]

        # ---- the tabs: one part of the page each
        browser.open(played.origin + "/", listed, "the first page", **phone(390))
        assert value(current) == "tasks" and value("document.getElementById('heading').textContent") == "任务"
        assert shown("#sections") and not shown("#projects") and not shown("#together") and not shown("#me") and shown("#new-task") and not shown("#settings")
        assert value("document.getElementById('tab-count').textContent") == "1", "what waits for an answer is counted on its tab"
        assert value("Array.from(document.querySelectorAll('#sections > section')).map(n=>n.id).join(',')") == 'needs,replies,unseen,active,opened,pinned,recent'
        # Retained desktop conversations stay easy to find even among many phone-created idle terminals.
        saved_terminals = value("window.RemoteCliPage.data.terminals")
        value("window.RemoteCliPage.data.terminals = Array.from({length:30}, (_,i)=>({id:'idle-'+i, tool:'shell', title:'手机空闲 '+i, state:'running', phase:'idle', created:Date.now(), dir:'thesis'})); document.getElementById('query').dispatchEvent(new Event('input'))")
        assert value("document.querySelector('#opened-list .card b').textContent") == '核对回归表的系数'
        assert value("document.querySelectorAll('#opened-list .card').length") >= 32
        browser.picture(out / 'opened-desktop-first.png')
        value("window.RemoteCliPage.data.terminals = %s; document.getElementById('query').dispatchEvent(new Event('input'))" % json.dumps(saved_terminals))
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
        assert value("[...document.querySelectorAll('.collaboration-actions .act:not([hidden])')].map(b => b.id)") == ['handoff', 'ask-many', 'add-schedule', 'add-chain']
        browser.picture(out / "together.png")
        # 接力: refused while both ends are one conversation; then set, changed, removed
        value("document.querySelector('#chains .card.add button').click()")
        until(sheet + " && !!document.getElementById('chain-from')", "the sheet of a rule")
        time.sleep(0.5)         # (the sheet has risen)
        browser.picture(out / "chain-sheet.png")
        value("document.getElementById('chain-from').click()"); pick(title(FIXING))
        until(sheet + " && ((document.getElementById('chain-from') || {}).innerText || '').includes(%s)" % json.dumps(title(FIXING)), "the source chosen")
        value("document.getElementById('chain-to').click()"); pick(title(FIXING))
        until(sheet + " && ((document.getElementById('chain-to') || {}).innerText || '').includes(%s)" % json.dumps(title(FIXING)), "the same one, chosen to take over")
        fill("chain-text", "审查上面的改动")
        press("保存")
        until(problem + " === '不能接力给同一段对话'", "what is wrong with it, said in the sheet")
        value("document.getElementById('chain-to').click()"); pick(title(TABLES))
        until(sheet + " && ((document.getElementById('chain-to') || {}).innerText || '').includes(%s)" % json.dumps(title(TABLES)), "another one to take over")
        value("document.querySelector('dialog.rs .rs-seg button[data-value=\"3\"]').click()")
        press("保存")
        until("document.querySelectorAll('#chains .card').length === 3 && " + closed, "the new rule on the page")
        item = asked("work_chain_set")[-1]["item"]
        assert (item["from"], item["from_tool"], item["to"], item["to_tool"], item["text"], item["rounds"]) == (FIXING, "claude", TABLES, "claude", "审查上面的改动", 3), item
        value("Array.from(document.querySelectorAll('#chains .card:not(.add)')).find(c => c.textContent.includes('审查上面的改动')).querySelector('button.open').click()")
        until(sheet + " && document.getElementById('chain-text').value === '审查上面的改动' && ((document.getElementById('chain-from') || {}).innerText || '').includes(%s)" % json.dumps(title(FIXING)), "the rule in its sheet, as it was set")
        press("删除")
        until("document.getElementById('chain-remove').innerText.includes('再按一次')", "asked once more before it is removed")
        press("再按一次删除")
        until("document.querySelectorAll('#chains .card').length === 2", "the rule gone")
        assert asked("work_chain_remove")[-1]["chain"] == item["id"]
        # 定时提问: every day at a time; switched off from its card; one whose time has passed is refused in the sheet
        value("document.querySelector('#schedules .card.add button').click()")
        until(sheet + " && !!document.getElementById('schedule-session')", "the sheet of a question")
        assert value("!!document.getElementById('schedule-time') && !document.getElementById('schedule-at')"), "every day, at first"
        value("document.getElementById('schedule-session').click()"); pick(title(TABLES))
        until(sheet + " && ((document.getElementById('schedule-session') || {}).innerText || '').includes(%s)" % json.dumps(title(TABLES)), "the conversation chosen")
        fill("schedule-text", "进展如何？"); fill("schedule-time", "21:45")
        until("document.querySelector('dialog.rs .rs-summary').textContent.includes('21:45')", "when it is asked next, said under the choice")
        time.sleep(0.5)         # (the sheet has risen)
        browser.picture(out / "schedule-sheet.png")
        press("保存")
        until("document.querySelectorAll('#schedules .card').length === 3", "the new question on the page")
        item = asked("work_schedule_set")[-1]["item"]
        assert (item["session"], item["tool"], item["text"], item["kind"], item["time"], item["on"]) == (TABLES, "claude", "进展如何？", "daily", "21:45", True), item
        value("Array.from(document.querySelectorAll('#schedules .card:not(.add)')).find(c => c.textContent.includes('进展如何')).querySelector('.rs-switch').click()")
        until("Array.from(document.querySelectorAll('#schedules .card')).some(c => c.textContent.includes('进展如何') && c.textContent.includes('已停用'))", "the question switched off")
        assert asked("work_schedule_set")[-1]["item"]["on"] is False
        value("document.querySelector('#schedules .card.add button').click()")
        until(sheet + " && !!document.getElementById('schedule-session')", "the sheet again")
        value("document.getElementById('schedule-session').click()"); pick(title(EXPORT))
        until(sheet + " && ((document.getElementById('schedule-session') || {}).innerText || '').includes(%s)" % json.dumps(title(EXPORT)), "the conversation chosen")
        fill("schedule-text", "只问一次")
        press("一次")
        until("!!document.getElementById('schedule-at')", "a day and a time to choose")
        value("document.querySelector('dialog.rs .rs-chip[data-value=\"30\"]').click()")
        until("document.querySelector('dialog.rs .rs-summary').textContent.includes('下次提问')", "in half an hour")
        fill("schedule-at", "2020-01-01T08:00")
        until("document.querySelector('dialog.rs .rs-summary').textContent.includes('已经过去')", "a time that has passed is said at once")
        press("保存")
        until(problem + ".includes('现在之后')", "refused in the sheet")
        assert value("document.getElementById('schedule-text').value") == "只问一次", "what was filled in stays"
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, "the sheet closed")
        # one conversation handed over to another: first the one it is from, then who takes it
        value("document.getElementById('handoff').click()")
        until(sheet + " && document.querySelector('dialog.rs .rs-bar h2').textContent === '交接哪段对话'", "the conversation to hand over")
        saved = pictures.uuid(11)
        pick(title(saved))
        until(sheet + " && !!document.getElementById('handoff-note')", "the sheet of a handover")
        pick(title(FIXING))
        fill("handoff-note", "先看优惠券那一段")
        until("document.getElementById('handoff-go').innerText.includes(%s)" % json.dumps(title(FIXING)), "the button says who takes it")
        press("交给")
        until(toast + ".includes('已交给“修复结算页的金额显示”')", "the handover said")
        op = asked("work_handoff")[-1]
        assert (op["from"], op["from_tool"], op["to"], op["to_tool"], op["note"]) == (saved, "claude", FIXING, "claude", "先看优惠券那一段"), op
        # one question for several conversations
        value("document.getElementById('ask-many').click()")
        until(sheet + " && document.querySelectorAll('dialog.rs .rs-row[role=checkbox]').length >= 3", "the conversations that can be told something")
        assert not value("!!document.getElementById('many-%s')" % ASKING), "one that asks a question is not told anything meanwhile"
        value("document.getElementById('many-%s').click()" % TABLES)
        until("!!document.getElementById('many-%s')" % EXPORT, "drawn anew")
        value("document.getElementById('many-%s').click()" % EXPORT)
        fill("many-text", "各自总结一下进度")
        time.sleep(0.5)         # (the sheet has risen)
        browser.picture(out / "ask-many.png")
        press("发给 2 段对话")
        until(toast + " === '已发给 2 段对话'", "both told")
        told = [op for op in asked("session_say") if op.get("text") == "各自总结一下进度"]
        assert sorted(op["session"] for op in told) == sorted([TABLES, EXPORT]), told
        # from a card: a held press offers the handover, and the conversation is the one it starts from
        tab("tasks")
        until("!!document.querySelector('#needs-list .card')", "the cards")
        value("Array.from(document.querySelectorAll('#overview .card')).find(c => c.textContent.includes('核对回归表的系数')).dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }))")
        until(sheet + " && Array.from(document.querySelectorAll('dialog.rs:not(.closing) button')).some(b => b.innerText.includes('交接给另一段对话'))", "what to do with a conversation")
        press("交接给另一段对话")
        until(sheet + " && !!document.getElementById('handoff-note') && document.querySelector('dialog.rs .rs-who').innerText.includes(%s)" % json.dumps(title(TABLES)), "the handover, from that conversation")
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, "closed")
        # a conversation's window still stands as it did: what the new pages are drawn with does not reach into it
        browser.open(played.origin + "/?tab=tasks&take=" + FIXING, "document.querySelector('#sheet .state') && document.querySelector('#sheet .said')?.textContent.includes('formatPrice')", "a conversation's window", **phone(390))
        window = value("(() => { const edge = sel => document.querySelector('#sheet ' + sel).getBoundingClientRect(); return { bar: edge('.bar').height, mode: edge('.control-mode').bottom, reads: edge('.reads').top, hand: !document.querySelector('#sheet .bar .hand').hidden }; })()")
        assert window["bar"] < 60 and window["mode"] <= window["reads"] and window["hand"], window
        value("document.querySelector('#sheet .bar .hand').click()")
        until(sheet + " && !!document.getElementById('handoff-note') && document.querySelector('dialog.rs .rs-who').innerText.includes(%s)" % json.dumps(title(FIXING)), "the handover from the window, of the conversation it shows")
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")

        # ---- 我的
        tab("me")
        until("document.querySelectorAll('#me .setting-row').length >= 3", "settings before statistics")
        assert not shown('#today')
        value("Array.from(document.querySelectorAll('#me .setting-row')).find(b => b.textContent.includes('工作统计')).click()")
        until("document.querySelectorAll('#me .day').length === 7 && document.querySelectorAll('#me .tile').length === 4", "today in figures and the last seven days")
        assert shown("#settings") and not shown("#new-task") and asked("work_stats")[-1]["days"] == 7
        assert "次提问" in value("document.querySelector('#me .at').textContent") and "Claude Code" in value("document.querySelector('#me .shares').textContent")
        assert "连续 5 天" in value("document.querySelector('#me .stats-meta').textContent")
        assert "缓存复用" in value("document.querySelector('#me .token-card').textContent")
        assert value("document.querySelectorAll('#me .bar-seg').length") >= 7
        assert value("!!document.querySelector('#me .avg-line')")
        assert value("document.querySelector('#computer').textContent").count("1.6.0") == 1
        browser.picture(out / "me.png")
        value("Array.from(document.querySelectorAll('#period .section-head .seg button')).find(b => b.textContent === '14 天').click()")
        until("document.querySelectorAll('#me .day').length === 14", "fourteen days")
        assert asked("work_stats")[-1]["days"] == 14
        value("Array.from(document.querySelectorAll('#period .section-head .seg button')).find(b => b.textContent === '30 天').click()")
        until("document.querySelectorAll('#me .day').length === 30", "thirty days")
        assert asked("work_stats")[-1]["days"] == 30
        value("document.querySelector('#me .chart-head .seg button:last-child').click()")
        until("document.querySelector('#me .at').textContent.includes('token')", "the days by tokens")
        before = value("document.querySelector('#me .at').textContent")
        value("document.querySelectorAll('#me .day')[3].click()")
        until("document.querySelector('#me .at').textContent !== %s && document.querySelectorAll('#me .day')[3].getAttribute('aria-pressed') === 'true'" % json.dumps(before), "the day that was pressed")
        # interactive 24-hour rhythm bar selection
        value("document.querySelectorAll('#me .hour-bar')[14].click()")
        until("document.querySelector('#me .hours-note').textContent.startsWith('14:00')", "picked hour bar updates header")
        # continuity cache saved in localStorage
        assert value("Object.keys(localStorage).some(k => k.startsWith('rcli:stats:v2:'))"), "stats cached in localStorage for continuity"

        # ---- the limits, first on 我的: a line for each window; one nearly used up stands out; why one is not known;
        # agy shows only its primary Gemini/Neon pool by default and expands secondary pools on click
        soon = int(time.time()) + 2 * 3600
        played.info["quotas"] = [{"tool": "claude", "at": int(time.time() * 1000), "why": "", "windows": [{"name": "five_hour", "used": 38, "resets": soon}, {"name": "seven_day", "used": 93.4, "resets": soon + 5 * 86400}]},
                                 {"tool": "codex", "at": int(time.time() * 1000), "billing": {"type": "api", "balance": "0", "unit": "USD"}, "windows": []},
                                 {"tool": "agy", "at": int(time.time() * 1000), "why": "", "windows": [
                                     {"name": "five_hour", "used": 2, "resets": soon},
                                     {"name": "seven_day", "used": 1, "resets": soon + 5 * 86400},
                                     {"name": "3p_five_hour", "used": 50, "resets": soon, "group": "Claude / GPT"},
                                     {"name": "3p_seven_day", "used": 59, "resets": soon + 5 * 86400, "group": "Claude / GPT"},
                                     {"name": "gemini_argon_wtu_seven_day", "used": 80, "resets": soon + 5 * 86400, "group": "Gemini 4 Argon"}]}]
        browser.open(played.origin + "/?tab=me", "!!document.querySelector('#quotas li')", "the limits on 我的", **phone(390))
        lines = value("Array.from(document.querySelectorAll('#quotas > ul > li')).map(li => li.innerText.replace(/\\s+/g, ' ').trim())")
        assert lines[0].startswith("Claude Code · 5 小时") and "剩余 62%" in lines[0] and "2 小时后重置" in lines[0], lines
        assert "本周" in lines[1] and "剩余 7%" in lines[1] and lines[2] == "Codex · API 余额 0 USD", lines
        assert lines[3].startswith("Antigravity CLI · 5 小时") and "剩余 98%" in lines[3] and lines[4].startswith("Antigravity CLI · 本周") and "剩余 99%" in lines[4], lines
        assert "其他 2 个池子" in lines[5] and "余量不足" in lines[5] and "展开" in lines[5], lines
        assert value("Array.from(document.querySelectorAll('#quotas > ul > li .track i')).slice(0, 2).map(bar => Math.round(parseFloat(bar.style.width) * 10) / 10)") == [62.0, 6.6], "bars show remaining share"
        assert value("document.querySelectorAll('#quotas .quota-extra li').length") == 0, "secondary pools start collapsed"
        assert value("Array.from(document.querySelectorAll('#quotas > ul > li')).slice(0, 3).map(li => li.classList.contains('high'))") == [False, True, False], "the one nearly used up stands out"
        assert value("document.querySelector('#me > section, #me > *:nth-child(2)').id") == "quotas", "the limits stand first"
        value("document.querySelector('#quotas .quota-more').click()")
        until("document.querySelectorAll('#quotas .quota-extra li').length === 3", "secondary agy pools expanded")
        extra_lines = value("Array.from(document.querySelectorAll('#quotas .quota-extra li')).map(li => li.innerText.replace(/\\s+/g, ' ').trim())")
        assert value("Array.from(document.querySelectorAll('#quotas .quota-extra li'), li => li.className)") == ["", "elevated", "high"]
        assert value("new Set(Array.from(document.querySelectorAll('#quotas .quota-extra .track i'), bar => getComputedStyle(bar).backgroundColor)).size") == 3
        assert extra_lines[0].startswith("Claude / GPT · 5 小时") and "剩余 50%" in extra_lines[0] and extra_lines[2].startswith("Gemini 4 Argon · 本周") and "剩余 20%" in extra_lines[2], extra_lines
        seen = value("(() => { const q = document.getElementById('quotas').getBoundingClientRect(); return { right: q.right, inner: innerWidth, page: document.documentElement.scrollWidth }; })()")
        assert seen["right"] <= seen["inner"] and seen["page"] <= seen["inner"], seen
        browser.picture(out / "me-quotas.png")
        value("document.querySelector('#quotas .quota-more').click()")
        until("document.querySelectorAll('#quotas .quota-extra li').length === 0", "secondary agy pools collapsed again")
        del played.info["quotas"]

        # ---- every tab at three widths: nothing wider than the screen, the tabs at its foot, nothing left under them
        for width in (320, 390, 430, 1440):
            for name, ready in (("tasks", listed), ("projects", "document.querySelectorAll('#project-list .card').length === 4"),
                                ("together", "document.querySelectorAll('#chains .card').length >= 2"), ("me", "document.querySelectorAll('#me .day').length === 7")):
                browser.open(played.origin + "/?tab=" + name + ('&stats=1' if name == 'me' else ''), ready, "%s at %d" % (name, width), **phone(width))
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
                    if name == "together":
                        value("document.documentElement.style.fontSize='19px'; scrollTo(0,0)")
                        assert value("""[...document.querySelectorAll('.collaboration-actions .act:not([hidden])')].every(b=>{
                            const r=b.getBoundingClientRect(), icon=b.querySelector('.tool').getBoundingClientRect();
                            return r.width>=48 && r.height>=48 && icon.width===38 && b.scrollWidth<=b.clientWidth
                                && b.querySelector('small').getBoundingClientRect().bottom<=r.bottom;
                        }) && document.documentElement.scrollWidth===320""")
                        browser.picture(out / "collaboration-large-type.png")

        # Native global collaboration opens the form directly; instructions and choices fit even at 320 px.
        for width in (320, 390, 430, 1440):
            browser.open(played.origin + '/?tab=together&newchain=1', sheet + " && !!document.getElementById('chain-text')", 'direct new chain', **phone(width))
            assert '来源完成一轮后' in value("document.querySelector('dialog.rs').textContent")
            geometry = value("(() => { const f=document.querySelector('dialog.rs .rs-body'), card=document.querySelector('dialog.rs .rs-card').getBoundingClientRect(); return { width: innerWidth, client: f.clientWidth, scroll:f.scrollWidth, inside: card.left >= 0 && card.right <= innerWidth, missing:[...f.querySelectorAll('input,select,textarea')].filter(n=>!n.labels.length && !n.getAttribute('aria-label')).length }; })()")
            assert geometry['width'] == width and geometry['scroll'] <= geometry['client'] and geometry['inside'] and geometry['missing'] == 0, geometry
            time.sleep(.3)
            browser.picture(out / ('chain-' + str(width) + '.png'))
            value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")

        # ---- a computer of an earlier version: said on both tabs, and the lists do not pretend to be empty
        played.info["features"] = [feature for feature in played.info["features"] if feature != "work"]
        browser.open(played.origin + "/?tab=together", "document.querySelector('#together .notice') && !document.querySelector('#together .notice').hidden", "said that the computer is too old", **phone(390))
        assert "需要更新电脑端" in value("document.querySelector('#together .notice').textContent") and not shown("#chains")
        value("document.getElementById('handoff').click()")
        until(toast + ".includes('电脑端版本不支持交接')", "why nothing is handed over")
        tab("me")
        value("Array.from(document.querySelectorAll('#me .setting-row')).find(b => b.textContent.includes('工作统计')).click()")
        until("document.querySelector('#me .notice') && document.querySelector('#me .notice').textContent.includes('需要更新电脑端')", "the same on 我的")
        assert shown("#computer")
        errors = value("JSON.stringify(window.__errors)")
        assert errors == "[]", errors
    finally:
        browser.close(); played.close()
print("tabs: four tabs with a part of the page each, nothing under them at three widths; rules set, changed and removed; a handover, one question for several; the numbers drawn")
