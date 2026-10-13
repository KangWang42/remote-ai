"""A new conversation, begun on the real pages (web/) through the real relay, with a computer that is played
(tools/readme_pictures.py). In an application or in a terminal: both are offered on a project's page and in the
sheet of 任务, the kind chosen last stays, a terminal is started with one press, an application is handed the first
words and the conversation that comes of them is entered by itself; a computer without desktop apps offers
terminals only; a project that was added is entered. And the tabs inside the app: they are the app's own, and what
the app's lists opened leads back to them.

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
NEW_APP, OLD_APP = APP % "{ appTabs() { return 'tasks,projects,together,me'; } }", APP % "{}"

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

        # ---- a project's page: the two kinds, an application first for a project nothing tells of
        browser.open(played.origin + "/?project=web-shop", project, "a project's page", **phone(390))
        assert value("document.querySelector('#begin h2').textContent") == "新建对话"
        assert texts("#begin .seg button") == ["应用", "终端"] and pressed("#begin .seg button") == ["应用"], "both kinds, an application first"
        assert texts("#launch button") == ["Codex", "Claude"], texts("#launch button")
        browser.picture(out / "project-app.png")
        for width in (320, 390, 430):
            browser.open(played.origin + "/?project=web-shop", project, "a project's page", **phone(width))
            seen = value(widths)
            assert seen["page"] <= seen["inner"] and seen["right"] <= seen["inner"] and seen["low"] >= 48, (width, seen)
        browser.open(played.origin + "/?project=web-shop", project, "a project's page", **phone(390))
        press("#begin .seg button", "终端")
        until("document.querySelectorAll('#launch button').length === 3", "the tools of a terminal")
        assert texts("#launch button") == ["Claude Code", "Codex", "PowerShell"] and pressed("#begin .seg button") == ["终端"]
        browser.picture(out / "project-terminal.png")
        # the kind that was chosen stays, on this page opened again and for another project
        browser.open(played.origin + "/?project=thesis", project, "another project", **phone(390))
        assert pressed("#begin .seg button") == ["终端"] and len(texts("#launch button")) == 3, "the kind chosen last stays"
        # one press starts a terminal in the project, and the page goes on to it
        press("#launch button", "Codex")
        until("location.pathname.endsWith('/terminal/')", "the terminal's page")
        op = asked("start")[-1]
        assert (op["tool"], op["dir"]) == ("codex", "thesis") and not op.get("session"), op

        # ---- in an application: the first words are handed to its desktop app, and the conversation that comes of them is entered
        browser.open(played.origin + "/?project=web-shop", project, "a project's page", **phone(390))
        press("#begin .seg button", "应用")
        until("document.querySelectorAll('#launch button').length === 2", "the applications")
        press("#launch button", "Codex")
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
        time.sleep(0.3)
        browser.picture(out / "app-handed.png")
        # it is sent on the computer: the conversation is there, and the phone goes into it
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

        # ---- from 任务: the same two kinds, the tools of the kind, the projects
        browser.open(played.origin + "/?tab=tasks", "document.body.innerText.includes('部署到测试环境')", "the first page", **phone(390))
        assert value("document.getElementById('new-task').getAttribute('aria-label')") == "新建对话"
        value("document.getElementById('new-task').click()")
        until(sheet + " && " + sheet + ".querySelector('h3').textContent === '新建对话'", "the sheet of a new conversation")
        assert pressed("#sheet-form .seg button") == ["应用"] and texts("#sheet-form .tools button") == ["Codex", "Claude"], "the kind chosen last, here too"
        assert pressed("#sheet-form .tools button") == ["Codex"], "one tool stands chosen"
        names = texts("#sheet-form .anywhere button.choice")
        assert sorted(names) == ["data-tools", "thesis", "web-shop"] and names[0] == "thesis", ("the projects, first the one a terminal was just started in", names)
        time.sleep(0.5)
        browser.picture(out / "new-app.png")
        press("#sheet-form .seg button", "终端")
        until("document.querySelectorAll('#sheet-form .tools button').length === 3", "the tools of a terminal")
        assert texts("#sheet-form .tools button") == ["Claude Code", "Codex", "PowerShell"] and pressed("#sheet-form .tools button") == ["Claude Code"]
        press("#sheet-form .tools button", "PowerShell")
        assert pressed("#sheet-form .tools button") == ["PowerShell"]
        time.sleep(0.3)
        browser.picture(out / "new-terminal.png")
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
        # the tool chosen last stands chosen the next time; an application from the sheet asks for the first words
        browser.open(played.origin + "/?tab=tasks&new=1", sheet + " && " + sheet + ".querySelector('h3').textContent === '新建对话'", "the sheet, by a link", **phone(390))
        assert pressed("#sheet-form .seg button") == ["终端"] and pressed("#sheet-form .tools button") == ["PowerShell"], "what was chosen last"
        press("#sheet-form .seg button", "应用")
        press("#sheet-form .tools button", "Claude")
        press("#sheet-form .anywhere button.choice", "thesis")
        until(sheet + " && !!document.getElementById('desktop-draft') && " + sheet + ".querySelector('h3').textContent === 'Claude · thesis'", "the sheet of the application, for the project that was pressed")
        press("#sheet-form button", "在 Claude 中打开")
        until("document.querySelector('#sheet-form button.solid').textContent === '在电脑上发送后开始'", "handed to the application")
        op = asked("app_open")[-1]
        assert (op["tool"], op["dir"], op["draft"]) == ("claude", "thesis", ""), op
        press("#sheet-form button", "返回")
        assert errors() == "[]", errors()

        # ---- a project whose folder has only been used in terminals: a terminal first, while nothing was chosen
        played.info["projects"] = [{"name": "web-shop", "path": "D:\\work\\web-shop", "kinds": ["终端"]}, {"name": "thesis", "path": "D:\\work\\thesis", "kinds": ["Codex 桌面版", "终端"]},
                                   {"name": "data-tools", "path": "D:\\work\\data-tools", "kinds": []}]
        value("localStorage.removeItem('rcli-newkind')")
        browser.open(played.origin + "/?project=web-shop", project, "a project of terminals", **phone(390))
        assert pressed("#begin .seg button") == ["终端"], "a terminal for a project of terminals"
        browser.open(played.origin + "/?project=thesis", project, "a project of both", **phone(390))
        assert pressed("#begin .seg button") == ["应用"], "an application for any other"

        # ---- a project that is added is entered: what it was added for is there
        browser.open(played.origin + "/?tab=projects", "document.querySelectorAll('#project-list .card').length === 4", "the projects", **phone(390))
        value("document.querySelector('#project-list .card.add button').click()")
        until(sheet + " && !!document.getElementById('field-path')", "the sheet of a new project")
        value("document.getElementById('field-path').value = 'D:\\\\work\\\\notes'")
        press("#sheet-form button", "添加填写的路径")
        until("document.getElementById('heading').textContent === 'notes' && " + project, "the project that was added, entered")
        assert asked("project_add")[-1]["path"] == "D:\\work\\notes" and texts("#begin .seg button") == ["应用", "终端"]
        assert value("document.getElementById('empty').textContent") == "这个项目还没有对话"

        # ---- a computer without the desktop apps: terminals, and no choice of kind
        played.info["features"] = [feature for feature in played.info["features"] if not feature.endswith("-desktop-open")]
        browser.open(played.origin + "/?project=web-shop", project, "a project's page", **phone(390))
        assert texts("#begin .seg button") == [] and texts("#launch button") == ["Claude Code", "Codex", "PowerShell"], "terminals only"
        browser.open(played.origin + "/?tab=tasks&new=1", sheet + " && " + sheet + ".querySelector('h3').textContent === '新建对话'", "the sheet", **phone(390))
        assert texts("#sheet-form .seg button") == [] and len(texts("#sheet-form .tools button")) == 3
        press("#sheet-form button", "取消")
        played.info["features"] += ["codex-desktop-open", "claude-desktop-open"]
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
        # the kind of a new conversation is kept by the app, for every computer
        value("document.querySelector('#project-list .card button.open').click()")
        until(project, "a project, in the app")
        press("#begin .seg button", "终端")
        assert value("localStorage.getItem('app-pref-newkind')") == "term"
        # what the app's lists opened leads back to them: the sheet of a new conversation that is left
        value("sessionStorage.removeItem('app-asked')")
        browser.open(played.origin + "/?tab=tasks&new=1", sheet + " && " + sheet + ".querySelector('h3').textContent === '新建对话'", "the sheet, opened by the app", **phone(390))
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
        for what, field in (("handoff", "handoff-from"), ("many", "many-text"), ("chain", "chain-from"), ("schedule", "schedule-session")):
            browser.open(played.origin + "/?tab=together&do=" + what, sheet + " && !!document.getElementById('%s')" % field, "协作, opened for " + what, **phone(390))
        assert errors() == "[]", errors()
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
print("begin: an application or a terminal on a project's page and from 任务, the kind and the tool chosen last stay; a terminal started with one press; an application handed the first words, its conversation entered; terminals only without the apps; an added project entered; the app's tabs")
