"""A new conversation, begun on the real pages (web/) through the real relay, with a computer that is played
(tools/readme_pictures.py). One list of the AIs, on a project's page and in the sheet of 任务: an AI the computer
runs as a conversation is begun as one with one press, the shell as a terminal, the AI chosen last stays; a computer
that runs no conversations offers the desktop apps (handed the first words, the conversation that comes of them
entered by itself) beside the terminals; a project that was added is entered. And the tabs inside the app: they are
the app's own, and what the app's lists opened leads back to them.

    python tests/begin_ui_check.py <chrome.exe> <folder for the pictures>
"""
import json
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import readme_pictures as pictures      # noqa: E402

# The app, as the pages see it (window.RemoteCliNative): what it is asked is kept in the page's own storage, so
# that it is still there after the page went on to another one.
APP = """window.RemoteCliNative = (() => {
  const note = (what, value) => { const all = JSON.parse(sessionStorage.getItem('app-asked') || '[]'); all.push([what, value]); sessionStorage.setItem('app-asked', JSON.stringify(all)); };
  const kept = name => localStorage.getItem('app-pref-' + name) || '';
  return Object.assign({ chrome() {}, pref: kept, setPref(name, value) { localStorage.setItem('app-pref-' + name, value); }, home() { note('home', ''); }, appPage(tab) { note('appPage', tab); }, computer() { return '办公室电脑'; } }, %s);
})();"""
NEW_APP, OLD_APP = APP % "{ appTabs() { return 'tasks,projects,together,me'; }, forgetCurrent() { note('forgetCurrent', ''); } }", APP % "{}"

out = Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="remote-ai-begin-", ignore_cleanup_errors=True) as temporary:
    played, browser = pictures.Played(temporary), pictures.Browser(sys.argv[1], temporary)
    try:
        value, until = browser.value, browser.until
        browser.ask("Page.addScriptToEvaluateOnNewDocument", source="window.__errors = []; window.addEventListener('error', e => __errors.push(String(e.message)));"
                    " window.addEventListener('unhandledrejection', e => __errors.push('rejected: ' + (e.reason && e.reason.message || e.reason)));")
        phone = lambda width: dict(width=width, height=844, deviceScaleFactor=1, mobile=True)
        sheet = "document.querySelector('#sheet[open] #sheet-form')"
        toast = "(t => t.hidden ? '' : t.textContent)(document.getElementById('toast'))"
        texts = lambda selector: value("Array.from(document.querySelectorAll(%s)).map(n => n.textContent)" % json.dumps(selector))
        pressed = lambda selector: value("Array.from(document.querySelectorAll(%s)).filter(n => n.getAttribute('aria-pressed') === 'true').map(n => n.textContent)" % json.dumps(selector))
        press = lambda selector, text: value("Array.from(document.querySelectorAll(%s)).find(n => n.textContent === %s).click()" % (json.dumps(selector), json.dumps(text)))
        asked = lambda action: [op for op in played.asked if op["action"] == action]
        project = "!document.getElementById('project').hidden && document.querySelectorAll('#launch button').length > 0"
        errors = lambda: value("JSON.stringify(window.__errors)")
        widths = "(() => { const b = document.getElementById('begin'); return { page: document.documentElement.scrollWidth, inner: innerWidth, right: Math.max(...Array.from(b.querySelectorAll('button')).map(n => n.getBoundingClientRect().right)), low: Math.min(...Array.from(b.querySelectorAll('#launch button')).map(n => n.getBoundingClientRect().height)) }; })()"

        # ---- a project's page of a computer that runs conversations: one list, the AIs first, the shell last
        played.info["features"] += ["chat"]
        browser.open(played.origin + "/?project=web-shop", project, "a project's page", **phone(390))
        assert value("document.querySelector('#begin h2').textContent") == "新建对话"
        assert texts("#begin .seg button") == [], "no choice of kind"
        assert texts("#launch button") == ["Codex", "Claude", "PowerShell"], texts("#launch button")
        browser.picture(out / "project-list.png")
        for width in (320, 390, 430):
            browser.open(played.origin + "/?project=web-shop", project, "a project's page", **phone(width))
            seen = value(widths)
            assert seen["page"] <= seen["inner"] and seen["right"] <= seen["inner"] and seen["low"] >= 48, (width, seen)
        # one press begins a conversation in the project, and the page goes on to it
        browser.open(played.origin + "/?project=thesis", project, "another project", **phone(390))
        press("#launch button", "Codex")
        until("location.pathname.endsWith('/chat/')", "the conversation's page")
        op = asked("start")[-1]
        assert (op["tool"], op["dir"], op.get("form")) == ("codex", "thesis", "chat") and not op.get("session"), op
        # the shell begins as a terminal
        browser.open(played.origin + "/?project=web-shop", project, "a project's page", **phone(390))
        press("#launch button", "PowerShell")
        until("location.pathname.endsWith('/terminal/')", "the terminal's page")
        op = asked("start")[-1]
        assert (op["tool"], op["dir"], op.get("form")) == ("shell", "web-shop", None), op

        # ---- from 任务: the same list, the AI chosen last, the projects
        browser.open(played.origin + "/?tab=tasks", "document.body.innerText.includes('部署到测试环境')", "the first page", **phone(390))
        assert value("document.getElementById('new-task').getAttribute('aria-label')") == "新建对话"
        value("document.getElementById('new-task').click()")
        until(sheet + " && " + sheet + ".querySelector('h3').textContent === '新建对话'", "the sheet of a new conversation")
        assert texts("#sheet-form .seg button") == [] and texts("#sheet-form .tools button") == ["Codex", "Claude", "PowerShell"]
        assert pressed("#sheet-form .tools button") == ["Codex"], "one AI stands chosen"
        names = texts("#sheet-form .anywhere button.choice")
        assert sorted(names) == ["data-tools", "thesis", "web-shop"] and names[0] == "web-shop", ("the projects, first the one a terminal was just started in", names)
        time.sleep(0.5)
        browser.picture(out / "new-sheet.png")
        press("#sheet-form .tools button", "PowerShell")
        assert pressed("#sheet-form .tools button") == ["PowerShell"]
        for width in (320, 430):
            browser.ask("Emulation.setDeviceMetricsOverride", **phone(width))
            seen = value("(f => ({ client: f.clientWidth, scroll: f.scrollWidth, low: Math.min(...Array.from(f.querySelectorAll('.tools button, button.choice')).map(n => n.getBoundingClientRect().height)) }))(document.getElementById('sheet-form'))")
            assert seen["scroll"] <= seen["client"] and seen["low"] >= 48, (width, seen)
        browser.ask("Emulation.setDeviceMetricsOverride", **phone(390))
        before = len(asked("start"))
        press("#sheet-form .anywhere button.choice", "data-tools")
        until("location.pathname.endsWith('/terminal/')", "the terminal's page")
        op = asked("start")[-1]
        assert len(asked("start")) == before + 1 and (op["tool"], op["dir"]) == ("shell", "data-tools"), op
        # the AI chosen last stands chosen the next time
        browser.open(played.origin + "/?tab=tasks&new=1", sheet + " && " + sheet + ".querySelector('h3').textContent === '新建对话'", "the sheet, by a link", **phone(390))
        assert pressed("#sheet-form .tools button") == ["PowerShell"], "what was chosen last"
        press("#sheet-form .tools button", "Claude")
        press("#sheet-form .anywhere button.choice", "thesis")
        until("location.pathname.endsWith('/chat/')", "Claude's conversation, in the project that was pressed")
        op = asked("start")[-1]
        assert (op["tool"], op["dir"], op.get("form")) == ("claude", "thesis", "chat"), op
        assert errors() == "[]", errors()

        # ---- a computer that runs no conversations: the desktop apps beside the terminals
        played.info["features"].remove("chat")
        browser.open(played.origin + "/?project=web-shop", project, "a project's page", **phone(390))
        assert texts("#launch button") == ["Codex 桌面版", "Claude 桌面版", "Claude Code", "Codex", "PowerShell"], texts("#launch button")
        browser.picture(out / "project-desktop.png")
        press("#launch button", "Codex")
        until("location.pathname.endsWith('/terminal/')", "Codex's terminal")
        op = asked("start")[-1]
        assert (op["tool"], op["dir"], op.get("form")) == ("codex", "web-shop", None), op
        # its desktop app is handed the first words, and the conversation that comes of them is entered
        browser.open(played.origin + "/?project=web-shop", project, "a project's page", **phone(390))
        press("#launch button", "Codex 桌面版")
        until(sheet + " && !!document.getElementById('desktop-draft')", "the sheet of the application")
        assert value(sheet + ".querySelector('h3').textContent") == "Codex · web-shop"
        assert texts("#sheet-form button") == ["在 Codex 中打开", "返回"], texts("#sheet-form button")
        assert value("document.querySelectorAll('#sheet-form select, #sheet-form label').length") == 0, "the project is the one of the page: nothing more is asked"
        value("(n => { n.value = '先看一下 README\\n再列出要改的地方'; })(document.getElementById('desktop-draft'))")
        time.sleep(0.5)         # (the sheet has risen)
        browser.picture(out / "app-sheet.png")
        press("#sheet-form button", "在 Codex 中打开")
        until("document.querySelector('#sheet-form button.solid').textContent === '在电脑上发送后开始'", "handed to the application")
        op = asked("app_open")[-1]
        assert (op["tool"], op["dir"], op["draft"]) == ("codex", "web-shop", "先看一下 README\n再列出要改的地方"), op
        assert "尚未发送" in value("document.querySelector('#sheet-form [role=status]').textContent") and value("document.querySelector('#sheet-form button.solid').disabled")
        assert len(asked("app_open")) == 1, "the application is opened once"
        began = pictures.uuid(21)
        played.sessions.append(dict(id=began, tool="codex", dir="web-shop", title="先看一下 README", origin="Codex 桌面版", kind="Codex 桌面版", host="shared", status="busy", now=True, live=True,
                                    updated=int(time.time() * 1000), can_takeover=False, ownership_known=True, opened=op["id"], reach=["say", "stop"], transport="codex-desktop", turn="turn-1",
                                    ways={"say": "codex-desktop", "stop": "codex-desktop", "answer": "", "end": ""}, why={}))
        played.said[began] = [{"role": "user", "text": "先看一下 README\n再列出要改的地方"}, {"role": "assistant", "text": "好的，我先读 README。"}]
        until("document.getElementById('sheet').classList.contains('window') && document.querySelector('#sheet .said')?.textContent.includes('我先读 README')", "the window of the conversation that began")
        assert value("document.querySelector('#sheet-form .who h3').textContent") == "先看一下 README"
        value("document.querySelector('#sheet-form .bar .icon').click()")
        until("!document.getElementById('sheet').open", "the window left")
        played.sessions[:] = [s for s in played.sessions if s["id"] != began]
        assert errors() == "[]", errors()

        # ---- a project that is added is entered: what it was added for is there
        browser.open(played.origin + "/?tab=projects", "document.querySelectorAll('#project-list .card').length === 4", "the projects", **phone(390))
        value("document.querySelector('#project-list .card.add button').click()")
        until(sheet + " && !!document.getElementById('field-path')", "the sheet of a new project")
        value("document.getElementById('field-path').value = 'D:\\\\work\\\\notes'")
        press("#sheet-form button", "添加填写的路径")
        until("document.getElementById('heading').textContent === 'notes' && " + project, "the project that was added, entered")
        assert asked("project_add")[-1]["path"] == "D:\\work\\notes" and len(texts("#launch button")) == 5
        assert value("document.getElementById('empty').textContent") == "这个项目还没有对话"

        # ---- a computer without the desktop apps: terminals only
        played.info["features"] = [feature for feature in played.info["features"] if not feature.endswith("-desktop-open")]
        browser.open(played.origin + "/?project=web-shop", project, "a project's page", **phone(390))
        assert texts("#launch button") == ["Claude Code", "Codex", "PowerShell"], "terminals only"
        browser.open(played.origin + "/?tab=tasks&new=1", sheet + " && " + sheet + ".querySelector('h3').textContent === '新建对话'", "the sheet", **phone(390))
        assert len(texts("#sheet-form .tools button")) == 3
        press("#sheet-form button", "取消")
        played.info["features"] += ["codex-desktop-open", "claude-desktop-open"]
        assert errors() == "[]", errors()

        # ---- Antigravity CLI: a terminal, unless the computer runs it as a conversation (chat-agy)
        played.info["tools"] = played.info["tools"] + ["agy"]
        browser.open(played.origin + "/?project=web-shop", project, "a project's page, with agy", **phone(390))
        assert "agy" in texts("#launch button"), texts("#launch button")
        played.info["features"] += ["chat", "chat-agy"]
        browser.open(played.origin + "/?project=web-shop", project, "a project's page, agy as a conversation", **phone(390))
        until("document.querySelectorAll('#launch button').length === 4", "the conversations with agy")
        assert texts("#launch button") == ["Codex", "Claude", "agy", "PowerShell"], texts("#launch button")
        press("#launch button", "agy")
        until("location.pathname.endsWith('/chat/')", "agy's conversation page")
        op = asked("start")[-1]
        assert (op["tool"], op["dir"], op.get("form")) == ("agy", "web-shop", "chat"), op
        played.info["features"] = [feature for feature in played.info["features"] if feature not in ("chat", "chat-agy")]
        played.info["tools"] = [tool for tool in played.info["tools"] if tool != "agy"]
        assert errors() == "[]", errors()

        # ---- inside the app: the tabs are the app's own
        app = browser.ask("Page.addScriptToEvaluateOnNewDocument", source=NEW_APP)["identifier"]
        noted = lambda: json.loads(value("sessionStorage.getItem('app-asked') || '[]'"))
        browser.open(played.origin + "/?tab=projects", "document.querySelectorAll('#project-list .card').length >= 4", "a computer's projects, in the app", **phone(390))
        value("sessionStorage.removeItem('app-asked')")
        assert value("document.getElementById('device-text').textContent").startswith("办公室电脑 · "), "the line says which computer this is"
        assert value("document.querySelectorAll('#tabs button').length") == 4 and value("document.querySelector('#tabs button[aria-current]').dataset.tab") == "projects"
        for name in ("tasks", "together", "me", "projects"):
            value("document.querySelector('#tabs button[data-tab=\"%s\"]').click()" % name)
        assert noted() == [["appPage", "tasks"], ["appPage", "together"], ["appPage", "me"], ["appPage", "projects"]], noted()
        assert value("document.querySelector('#tabs button[aria-current]').dataset.tab") == "projects", "the page stays what it was opened for"
        # what the app's lists opened leads back to them: the sheet of a new conversation that is left; the AI chosen
        # in it is kept by the app, for every computer
        value("sessionStorage.removeItem('app-asked')")
        browser.open(played.origin + "/?tab=tasks&new=1", sheet + " && " + sheet + ".querySelector('h3').textContent === '新建对话'", "the sheet, opened by the app", **phone(390))
        press("#sheet-form .tools button", "PowerShell")
        assert value("localStorage.getItem('app-pref-newtool')") == "term:shell"
        press("#sheet-form button", "取消")
        until("sessionStorage.getItem('app-asked') === JSON.stringify([['home', '']])", "back to the app's list")
        # without a project there is nothing to begin in: the page shows its projects, where one is added, and is not left
        kept = played.info["workspaces"]
        played.info["workspaces"] = []
        value("sessionStorage.removeItem('app-asked')")
        browser.open(played.origin + "/?tab=tasks&new=1", toast + " === '请先添加一个项目'", "no project yet", **phone(390))
        until("document.getElementById('heading').textContent === '项目' && !!document.querySelector('#project-list .card.add')", "this computer's projects, with the card that adds one")
        assert noted() == [], noted()
        played.info["workspaces"] = kept
        # a terminal that is started is gone on to, and says where it came from
        value("sessionStorage.removeItem('app-asked')")
        browser.open(played.origin + "/?tab=tasks&new=1", sheet + " && " + sheet + ".querySelector('h3').textContent === '新建对话'", "the sheet, opened by the app", **phone(390))
        press("#sheet-form .anywhere button.choice", "web-shop")
        until("location.pathname.endsWith('/terminal/') && location.search.includes('from=bench')", "the terminal's page, which leads back to the app's list")
        assert noted() == [], "the page is not left for the app's list while it goes on to the terminal"
        # one conversation's window that is left
        browser.open(played.origin + "/?project=web-shop&take=" + pictures.FIXING, "document.querySelector('#sheet .state') && document.querySelector('#sheet .said')?.textContent.includes('formatPrice')", "a conversation's window, opened by the app", **phone(390))
        value("sessionStorage.removeItem('app-asked')")
        value("document.querySelector('#sheet-form .bar .icon').click()")
        until("sessionStorage.getItem('app-asked') === JSON.stringify([['home', '']])", "back to the app's list")
        # 协作 opened by the app for one thing: that thing is begun at once
        # (in the sheet of ui/sheet.js: the handover begins with the conversation to hand over)
        for what, ready in (("handoff", "(document.querySelector('dialog.rs[open] .rs-bar h2') || {}).textContent === '交接哪段对话'"), ("many", "!!document.querySelector('dialog.rs[open] #many-text')"),
                            ("chain", "!!document.querySelector('dialog.rs[open] #chain-from')"), ("schedule", "!!document.querySelector('dialog.rs[open] #schedule-session')")):
            browser.open(played.origin + "/?tab=together&do=" + what, ready, "协作, opened for " + what, **phone(390))
        # expired session in the app: login screen offers direct removal and back without asking for a password
        played.signed_in = False
        value("sessionStorage.removeItem('app-asked')")
        browser.open(played.origin + "/", "!document.getElementById('login').hidden && !document.getElementById('login-native').hidden && !document.getElementById('login-forget').hidden", "login screen with native forget", **phone(390))
        value("document.getElementById('login-forget').click()")
        value("document.getElementById('login-back').click()")
        assert noted() == [["forgetCurrent", ""], ["home", ""]], noted()
        assert errors() == "[]", errors()
        played.signed_in = True
        browser.ask("Page.removeScriptToEvaluateOnNewDocument", identifier=app)

        # ---- an app of an earlier version has lists for 协作 and 我的 only: the other two stay the page's
        browser.ask("Page.addScriptToEvaluateOnNewDocument", source=OLD_APP)
        browser.open(played.origin + "/?tab=tasks", "document.body.innerText.includes('部署到测试环境')", "the first page, in an earlier app", **phone(390))
        value("sessionStorage.removeItem('app-asked')")
        value("document.querySelector('#tabs button[data-tab=\"projects\"]').click()")
        until("document.getElementById('heading').textContent === '项目'", "the page's own projects")
        value("document.querySelector('#tabs button[data-tab=\"together\"]').click()")
        assert noted() == [["appPage", "together"]], noted()
        assert errors() == "[]", errors()
    finally:
        browser.close(); played.close()
print("begin: one list of the AIs on a project's page and from 任务, the AI chosen last stays; a conversation begun with one press, the shell as a terminal; desktop apps handed the first words beside the terminals; terminals only without them; an added project entered; the app's tabs")
