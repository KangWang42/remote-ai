"""The phone's first page with many conversations, end to end: the real relay and agent, made-up homes of the tools
(tests/made_up.py) and the page in a browser, pressed as a finger presses it. Build the agent first.

    python tests/inbox_check.py <chrome.exe> [output folder]

Checked on the page: what waits for an answer, what is at work and what was used lately stand in their own lists,
across the projects; a conversation that is open in a folder that is no project is among them, with its folder; the
filters by tool and by where a thing runs; one search over all of it, the projects too; a held press (a finger's,
and a mouse's other button) asks what to do with a card and opens nothing; pinning, hiding and finding the hidden
again; taking on a conversation that is open outside every project, in its own folder, without making that folder
a project; and making it one. Uses a temporary data folder and port 8747 (RCLI_TEST_PORT changes it). Nothing of the
real tools is read or started: stand-ins take their place.
"""
import base64
import hashlib
import http.client
import json
import os
import secrets
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from made_up import Homes
from ws_client import Socket

ROOT = Path(__file__).resolve().parents[1]
PORT = int(os.environ.get("RCLI_TEST_PORT", "8747"))
password = "test-" + secrets.token_hex(8)
new_id = lambda: secrets.token_hex(16)
uuid = lambda n: "0199aaaa-bbbb-7ccc-8ddd-%012x" % n
ASKING, WORKING, HELD, IDLE = uuid(1), uuid(2), uuid(3), uuid(4)


def main():
    chrome = sys.argv[1]
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else None
    temp = tempfile.TemporaryDirectory()
    data, profile = Path(temp.name) / "agent", Path(temp.name) / "profile"
    shop, thesis = Path(temp.name) / "web-shop", Path(temp.name) / "thesis"
    # the system's folder for passing things is never spoken of to the phone: the folder that is no project lies elsewhere
    apart_root = ROOT / ".build" / ("inbox-" + secrets.token_hex(4))
    notes = apart_root / "reading-notes"
    # folders that are no projects and have conversations nothing has open: used in a terminal, or by a desktop app
    typed, desk, drawn = apart_root / "term-notes", apart_root / "desk-notes", apart_root / "claude-drafts"
    more = [apart_root / ("scratch-%d" % at) for at in range(1, 7)]
    for made in [data, shop, thesis, notes, typed, desk, drawn] + more:
        made.mkdir(parents=True)
    homes = Homes(temp.name)
    homes.codex_saved(typed, uuid(30), "终端里的笔记", 6, originator="codex-tui")
    homes.codex_saved(desk, uuid(31), "桌面版里的笔记", 7, originator="Codex Desktop")
    homes.claude_saved(drawn, uuid(32), "桌面版里的草稿", 8, entry="claude-desktop-3p")
    for at, folder in enumerate(more):
        homes.claude_saved(folder, uuid(40 + at), "零散的 %d" % at, 30 + at, entry="cli")
    homes.claude_saved(shop, uuid(10), "修复结算页的金额显示", 1)
    homes.claude_saved(shop, uuid(11), "给结算页加上优惠券", 2)
    homes.codex_saved(thesis, uuid(12), "画流程图", 5)
    homes.claude_saved(thesis, uuid(13), "整理第三章的参考文献", 24)
    homes.claude_saved(thesis, uuid(14), "摘要翻译", 72)
    homes.claude_saved(shop, uuid(15), "旧的讨论", 240)
    homes.claude_saved(shop, uuid(16), "更旧的讨论", 480)
    homes.codex_saved(shop, ASKING, "部署到测试环境", 0, held=True)
    homes.codex_saved(notes, WORKING, "整理读书笔记", 0, held=True)
    homes.codex_saved(thesis, HELD, "被占着的对话", 3, held=True)
    # a conversation of Claude Code's that the desktop app has loaded and nobody is at: a process keeps it, half an hour after it was last used
    homes.claude_saved(shop, IDLE, "桌面版里闲置的对话", 0.5, said=("结算页的金额有时多出一位小数，帮我看看", "好的。问题在 `formatPrice`：金额从分换算成元之后没有固定两位小数。\n\n- 换算后统一用 `toFixed(2)`\n- 在 `cart.test.js` 补了一条用例\n\n要顺便检查优惠券那里的金额吗？"))
    homes.claude_open(shop, IDLE)
    environment = homes.environment(RCLI_PASSWORD=password, REMOTECLI_UNATTENDED="600")       # nobody is at the computer
    environment["PATH"] = str(homes.stand_ins(temp.name, queue=True)) + ";" + environment["PATH"]
    (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % PORT, "RemoteEnabled": True, "RemoteMaxMode": "full",
                                                  "RemoteDirs": ["web-shop=" + str(shop), "thesis=" + str(thesis)]}), encoding="utf-8")
    relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(PORT), "--data", str(Path(temp.name) / "relay")], env=environment, stdout=subprocess.DEVNULL)
    agent_exe = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
    subprocess.run([str(agent_exe), "--set-password", str(data)], input=password.encode(), check=True)
    agent = subprocess.Popen([str(agent_exe), "--data", str(data)], env=environment)
    pipe = "RemoteCli.reach." + hashlib.sha256(os.path.abspath(str(data)).lower().encode("utf-8")).hexdigest()[:12]
    browser, hooks, report = None, [], {}
    try:
        cookie = {}

        def call(path, payload=None):
            connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=40)
            body = None if payload is None else json.dumps(payload).encode()
            connection.request("POST" if body is not None else "GET", path, body=body, headers=dict(cookie, **({"Content-Type": "application/json"} if body is not None else {})))
            reply = connection.getresponse()
            text = reply.read()
            if reply.getheader("Set-Cookie"):
                cookie["Cookie"] = reply.getheader("Set-Cookie").split(";")[0]
            assert reply.status == 200, (path, reply.status, text[:300].decode("utf-8", "replace"))
            return json.loads(text)

        def listed(session):
            return next((s for s in call("/api/terminal").get("sessions", []) if s["id"] == session), None)

        def until(test, seconds, what):
            deadline = time.time() + seconds
            while time.time() < deadline:
                found = test()
                if found:
                    return found
                time.sleep(0.25)
            raise AssertionError("never: " + (what() if callable(what) else what))

        def hook(name, session, folder, tool="codex", **more):
            """The hook, as a tool runs it for an event of a conversation."""
            event = dict({"hook_event_name": name, "session_id": session, "transcript_path": str(homes.rollouts.get(session, "")), "cwd": str(folder), "model": "gpt", "permission_mode": "default"}, **more)
            program = subprocess.Popen([str(agent_exe), "--hook", tool, pipe], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
            program.stdin.write(json.dumps(event, ensure_ascii=False).encode("utf-8")); program.stdin.close()
            hooks.append(program)
            return program

        for _ in range(40):
            try:
                call("/api/login", {"password": password})
                break
            except OSError:
                time.sleep(0.5)
        until(lambda: listed(WORKING) and listed(ASKING) and listed(uuid(16)), 40, "the conversations are listed")
        # none of the tools' real conversations: only the made-up ones
        assert {s["id"] for s in call("/api/terminal")["sessions"]} == {uuid(n) for n in (1, 2, 3, 4, 10, 11, 12, 13, 14, 15, 16)}, [s["title"] for s in call("/api/terminal")["sessions"]]
        assert hook("UserPromptSubmit", WORKING, notes, prompt="整理提纲").wait(10) == 0
        assert hook("UserPromptSubmit", ASKING, shop, prompt="部署").wait(10) == 0
        asking = hook("PermissionRequest", ASKING, shop, tool_name="Bash", tool_input={"command": "npm run deploy:staging", "description": "Deploy to staging"})
        until(lambda: listed(ASKING)["status"] == "confirm" and listed(WORKING)["status"] == "busy", 15, "one asks and one works")
        terminal = call("/api/terminal", {"action": "start", "id": new_id(), "tool": "shell", "dir": "thesis"})["terminal"]
        until(lambda: any(t["id"] == terminal and t["state"] == "running" for t in call("/api/terminal")["terminals"]), 20, "the phone's terminal runs")
        call("/api/terminal", {"action": "rename", "id": new_id(), "terminal": terminal, "title": "看日志"})
        time.sleep(4)
        started = len(call("/api/terminal")["terminals"])

        browser = subprocess.Popen([chrome, "--headless=new", "--hide-scrollbars", "--no-first-run", "--user-data-dir=" + str(profile),
                                    "--remote-debugging-port=%d" % (PORT + 1), "--remote-allow-origins=*", "--window-size=412,892", "about:blank"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(60):
            try:
                connection = http.client.HTTPConnection("127.0.0.1", PORT + 1, timeout=5)
                connection.request("PUT", "/json/new?about:blank")
                page = json.loads(connection.getresponse().read())
                break
            except (OSError, ValueError):
                time.sleep(0.5)
        pipe_page = Socket("http://127.0.0.1:%d" % (PORT + 1), "/devtools/page/" + page["id"], "", 20)
        sent = [0]

        def ask(method, **params):
            sent[0] += 1
            pipe_page.send({"id": sent[0], "method": method, "params": params})
            while True:
                message = pipe_page.receive(30)
                if message.get("id") == sent[0]:
                    assert "error" not in message, message
                    return message.get("result", {})

        def page_value(expression):
            got = ask("Runtime.evaluate", expression=expression, returnByValue=True, awaitPromise=True)
            assert "exceptionDetails" not in got, got["exceptionDetails"]
            return got.get("result", {}).get("value")

        def picture(name):
            if out:
                out.mkdir(parents=True, exist_ok=True)
                (out / name).write_bytes(base64.b64decode(ask("Page.captureScreenshot", format="png")["data"]))

        ask("Page.enable"); ask("Runtime.enable")
        ask("Emulation.setDeviceMetricsOverride", width=412, height=892, deviceScaleFactor=2, mobile=True)
        ask("Emulation.setTouchEmulationEnabled", enabled=True)
        ask("Page.navigate", url="http://127.0.0.1:%d/#p=%s" % (PORT, password))
        LISTS = """(() => {
          const names = id => Array.from(document.querySelectorAll('#' + id + '-list > .card')).map(c => c.querySelector('b').textContent);
          const out = {};
          for (const id of ['needs', 'pinned', 'active', 'recent', 'background']) out[id] = document.getElementById('sections').hidden || document.getElementById(id).hidden ? null : names(id);
          out.results = document.getElementById('results').hidden ? null : names('results');
          out.filters = document.getElementById('filters').hidden ? null : Array.from(document.querySelectorAll('#filters button')).map(b => (b.classList.contains('on') ? '*' : '') + b.textContent);
          out.projects = document.getElementById('projects').hidden ? null : Array.from(document.querySelectorAll('#project-list .card:not(.add) b')).map(b => b.textContent);
          out.nothing = !document.getElementById('nothing').hidden && !document.getElementById('sections').hidden;
          out.more = !document.getElementById('more-recent').hidden;
          out.summary = document.getElementById('device-text').textContent;
          return out;
        })()"""
        CARD = "((title) => Array.from(document.querySelectorAll('#overview .card, #project .card')).find(c => c.querySelector('b') && c.querySelector('b').textContent === title && c.offsetParent))"
        about = lambda title: page_value("(() => { const c = %s(%s); if (!c) return null; return { pill: (c.querySelector('.pill') || {}).textContent || '', meta: Array.from(c.querySelectorAll('.meta > span:not(.pill)')).map(s => s.textContent),"
                                         " apart: !!c.querySelector('.chip.apart'), pin: !!c.querySelector('b .pin'), list: c.parentElement.id }; })()" % (CARD, json.dumps(title, ensure_ascii=False)))
        SHEET = "(() => { const s = document.getElementById('sheet'); return s.open ? Array.from(s.querySelectorAll('button.choice')).map(b => b.firstChild.firstChild.textContent) : null; })()"
        choose = lambda label: page_value("(() => { const b = Array.from(document.querySelectorAll('#sheet button.choice')).find(b => b.firstChild.firstChild.textContent === %s); b.click(); return 1; })()" % json.dumps(label, ensure_ascii=False))
        press = lambda text: page_value("(() => { const b = Array.from(document.querySelectorAll('#filters button')).find(b => b.textContent === %s); b.click(); return 1; })()" % json.dumps(text, ensure_ascii=False))
        search = lambda words: page_value("(() => { const q = document.getElementById('query'); q.value = %s; q.dispatchEvent(new Event('input')); return 1; })()" % json.dumps(words, ensure_ascii=False))

        def lists(test, what):
            deadline = time.time() + 15
            while True:
                seen = page_value(LISTS)
                if test(seen):
                    return seen
                assert time.time() < deadline, (what, seen, page_value("(document.getElementById('toast') || {}).textContent"))
                time.sleep(0.3)

        # ---- by what needs its owner: what asks, what is at work, what was used lately, across the projects
        first = lists(lambda seen: seen["needs"] and seen["recent"] and seen["active"], "the lists are filled")
        report["first"] = first
        mine = (first["needs"] or []) + (first["active"] or [])
        assert "部署到测试环境" in first["needs"] and "整理读书笔记" in first["active"] and "看日志" in mine and len(mine) == 3, first
        assert first["recent"] == ["修复结算页的金额显示", "给结算页加上优惠券"] and first["more"], first
        assert first["background"] == ["桌面版里闲置的对话", "被占着的对话"] and first["pinned"] is None and first["projects"] == ["web-shop", "thesis"] and "等你确认" in first["summary"], first
        assert first["filters"] == ["*全部", "Claude Code", "Codex", "PowerShell", "终端", "应用"], first
        # each card says where it is and by what it runs; the one outside every project says its folder, and that it is none
        outside, shell = about("整理读书笔记"), about("看日志")
        assert outside["pill"] == "正在执行" and outside["apart"] and outside["meta"][:2] == ["reading-notes", "未加入项目"], outside
        assert shell["meta"][0] == "thesis" and shell["meta"][1].startswith("PowerShell · 手机终端") and not shell["apart"], shell
        picture("inbox.png")
        page_value("(() => { document.getElementById('more-recent').click(); return 1; })()")
        lists(lambda seen: seen["recent"] and len(seen["recent"]) == 7 and not seen["more"], "the rest of what was used lately")
        saved = about("画流程图")
        report["cards"] = {"outside": outside, "terminal": shell, "saved": saved}
        assert saved["meta"][0] == "thesis" and saved["meta"][1].startswith("Codex · ") and saved["pill"] == "", saved

        # ---- a conversation that sits idle in the desktop app is told things where it is: it is not one that is going on,
        # it is not taken from the app, and the message is handed to it by a session of its own tool (here a stand-in)
        idle = listed(IDLE)
        assert (idle["live"], idle["now"], idle["activity"], idle["reach"], idle["wake"], idle["origin"]) == (True, False, "locked", ["say"], True, "Claude 桌面版"), idle
        page_value("(() => { document.getElementById('background').open = true; return 1; })()")
        kept = about("桌面版里闲置的对话")
        assert kept["list"] == "background-list" and kept["pill"] == "已停下" and kept["meta"][0] == "web-shop", kept
        page_value("(() => { %s('桌面版里闲置的对话').querySelector('button.open').click(); return 1; })()" % CARD)
        # its window: what was said and a box to write in, filling the screen; nothing to choose, no lines of explanation
        WINDOW = "(() => { const s = document.getElementById('sheet'), w = s.querySelector('.write'); return s.open && w && !w.hidden ? { window: s.classList.contains('window'), tall: s.getBoundingClientRect().height / innerHeight," \
                 " choices: s.querySelectorAll('button.choice').length, small: Array.from(s.querySelectorAll('p.quiet, small')).map(e => e.textContent).filter(Boolean), box: w.querySelector('textarea').placeholder, head: s.querySelector('.stands').textContent } : null; })()"
        window = until(lambda: page_value(WINDOW), 8, "the window of the idle conversation, with its box")
        report["window_of_a_conversation"] = window
        assert window["window"] and window["tall"] > 0.98 and window["choices"] == 0 and window["small"] == [] and window["box"] == "发消息" and window["head"] == "Claude 桌面版 · web-shop", window
        until(lambda: "好的。" in (page_value("document.querySelector('#sheet .said').textContent") or ""), 10, "what was said in it")
        picture("conversation.png")
        page_value("(() => { const box = document.querySelector('#sheet .write textarea'); box.value = '把结论再写短一点'; document.querySelector('#sheet .write button').click(); return 1; })()")
        # what is written stands in the window at once, while it is still on its way
        assert page_value("(() => { const mine = document.querySelectorAll('#sheet .said .turn.mine'); return mine.length === 1 && mine[0].textContent === '把结论再写短一点' && document.querySelector('#sheet .write button').disabled; })()"), "shown at once"
        until(lambda: page_value("(() => { const mine = document.querySelectorAll('#sheet .said .turn.user'); return mine.length && mine[mine.length - 1].textContent === '把结论再写短一点' && !document.querySelector('#sheet .write button').disabled; })()"), 30, "what was written stands in the window as what the user said")
        if out:
            # the picture for the guide, in the size of its other pictures
            ask("Emulation.setDeviceMetricsOverride", width=500, height=960, deviceScaleFactor=1, mobile=True)
            time.sleep(0.5)
            picture("look.png")
            ask("Emulation.setDeviceMetricsOverride", width=412, height=892, deviceScaleFactor=2, mobile=True)
        handed = json.loads((data / "relay" / "remote-cli-phone" / "stand-in-run.json").read_text(encoding="utf-8"))
        report["handed_over_with"] = handed["arguments"][:8]
        assert "to:\nuds:\\\\.\\pipe\\LOCAL\\cc-msg-made-up\n" in handed["given"] and "from their phone" in handed["given"] and handed["given"].rstrip().endswith("把结论再写短一点\n-----END-----"), handed["given"]
        assert handed["arguments"][0] == "-p" and handed["arguments"][handed["arguments"].index("--tools") + 1] == "SendMessage" and "--no-session-persistence" in handed["arguments"] and '"disableAllHooks": true' in handed["settings"], handed
        assert listed(IDLE)["live"] is True and len(call("/api/terminal")["terminals"]) == started, "the conversation is where it was, and no terminal was started for it"
        page_value("(() => { document.getElementById('sheet').close(); return 1; })()")
        # Once Claude Code runs this program's hooks, the words are not sent as the message itself (it would take them
        # for another session's): the message only wakes the conversation, and its own Stop hook gives it the words.
        assert hook("SessionStart", IDLE, shop, tool="claude").wait(10) == 0
        (data / "relay" / "remote-cli-phone" / "stand-in-run.json").unlink()
        answers = {}
        saying = threading.Thread(target=lambda: answers.update(got=call("/api/conversation", {"id": new_id(), "session": IDLE, "action": "say", "text": "再加一个例子"})))
        saying.start()
        woke = until(lambda: (data / "relay" / "remote-cli-phone" / "stand-in-run.json").is_file() and json.loads((data / "relay" / "remote-cli-phone" / "stand-in-run.json").read_text(encoding="utf-8")), 30, "the conversation is woken")
        assert "Remote CLI wake-up" in woke["given"] and "再加一个例子" not in woke["given"] and saying.is_alive(), woke["given"]
        stopped = hook("Stop", IDLE, shop, tool="claude", last_assistant_message="…", stop_hook_active=False)
        given = json.loads(stopped.communicate(timeout=10)[0].decode("utf-8"))
        assert given["decision"] == "block" and given["reason"].endswith("\n\n再加一个例子") and "from their phone" in given["reason"], given
        saying.join(15)
        assert answers.get("got") == {"now": True, "woke": True}, answers

        # ---- and so is a Codex conversation that a program holds idle: through Codex's own queue, as what its user says
        # next, whatever signs the message holds
        held = listed(HELD)
        assert (held["live"], held["reach"], held["wake"], held["activity"]) == (True, ["say"], True, "locked"), held
        words = '把 "结论" 再写短一点 & 100% 保留 <原样>\n第二行 \\ 结尾\\'
        assert call("/api/conversation", {"id": new_id(), "session": HELD, "action": "say", "text": words}) == {"now": True, "woke": True}
        assert homes.queued.read_text(encoding="utf-8") == HELD + "\n" + words, homes.queued.read_text(encoding="utf-8")
        # one that asks is answered first, and one that is at work is left its message as before: neither is woken
        assert (listed(ASKING)["wake"], listed(WORKING)["wake"]) == (False, False)

        # ---- told apart by tool, and by where a thing runs
        press("Claude Code")
        only = lists(lambda seen: seen["filters"][1] == "*Claude Code", "one tool")
        assert only["needs"] is None and only["active"] is None and only["background"] == ["桌面版里闲置的对话"] and len(only["recent"]) == 2 and "画流程图" not in only["recent"] and not only["nothing"], only
        # in an app: the conversation the desktop app holds, not the ones started in a terminal; and the project it is in
        press("应用")
        both = lists(lambda seen: "*应用" in seen["filters"] and seen["recent"] is None, "one tool, in an app")
        assert both["background"] == ["桌面版里闲置的对话"] and both["needs"] is None and both["active"] is None and "web-shop" in both["projects"], both
        # in a terminal: the phone's own, and what was started in one; the projects that are used so
        press("Claude Code")
        press("终端")
        term = lists(lambda seen: "*终端" in seen["filters"] and seen["filters"][1] == "Claude Code", "what runs in a terminal")
        assert "看日志" in (term["needs"] or []) + (term["active"] or []) and "桌面版里闲置的对话" not in (term["background"] or []) and term["recent"] and term["projects"] == ["web-shop", "thesis"], term
        # a tool nothing of which is in an app: nothing, and no project
        press("PowerShell")
        press("应用")
        none = lists(lambda seen: seen["nothing"], "nothing is both")
        assert none["recent"] is None and none["projects"] == [], none
        press("全部")
        lists(lambda seen: seen["filters"][0] == "*全部" and seen["recent"] and len(seen["recent"]) == 2, "everything again")

        # ---- one search over all of it, the projects too
        search("读书")
        found = lists(lambda seen: seen["results"] is not None, "a search")
        assert found["results"] == ["整理读书笔记"] and found["projects"] is None and found["needs"] is None, found
        search("THESIS")
        found = lists(lambda seen: seen["results"] and len(seen["results"]) == 5, "by the name of a project")
        assert found["results"][0] == "看日志" and set(found["results"]) == {"看日志", "画流程图", "被占着的对话", "整理第三章的参考文献", "摘要翻译"} and found["projects"] == ["thesis"], found
        search("结算  优惠")
        assert lists(lambda seen: seen["results"] is not None and len(seen["results"]) < 5, "every word")["results"] == ["给结算页加上优惠券"]
        picture("inbox-search.png")
        search("没有这样的")
        assert lists(lambda seen: seen["results"] == [], "nothing found")["projects"] is None
        search("")
        lists(lambda seen: seen["results"] is None and seen["recent"], "the lists again")

        # ---- a held press asks what to do with a card: a mouse's other button
        page_value("(() => { %s('给结算页加上优惠券').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })); return 1; })()" % CARD)
        offered = until(lambda: page_value(SHEET), 5, "the sheet of a held press")
        assert offered == ["置顶", "从列表隐藏", "进入项目 web-shop"], offered
        time.sleep(0.5)             # the sheet has come up
        picture("inbox-manage.png")
        choose("置顶")
        pinned = lists(lambda seen: seen["pinned"] == ["给结算页加上优惠券"], "pinned")
        assert "给结算页加上优惠券" not in pinned["recent"] and about("给结算页加上优惠券")["pin"], pinned
        assert listed(uuid(11))["pinned"] is True and uuid(11) in (data / "conversation-marks.json").read_text(encoding="utf-8")

        # The default recent list is compact; expand it before testing a card outside the first two.
        page_value("(() => { document.getElementById('more-recent').click(); return 1; })()")

        # ---- and a finger that stays down: it opens nothing, though it ends on the card
        at = page_value("(() => { const c = %s('摘要翻译'); c.scrollIntoView({ block: 'center' }); const r = c.getBoundingClientRect(); return [r.left + r.width / 2, r.top + r.height / 2]; })()" % CARD)
        time.sleep(0.3)
        ask("Input.dispatchTouchEvent", type="touchStart", touchPoints=[{"x": at[0], "y": at[1]}])
        offered = until(lambda: page_value(SHEET), 5, "the sheet of a finger held down")
        ask("Input.dispatchTouchEvent", type="touchEnd", touchPoints=[])
        time.sleep(0.6)
        assert offered == ["置顶", "从列表隐藏", "进入项目 thesis"] and page_value(SHEET) == offered, (offered, page_value(SHEET))
        choose("从列表隐藏")
        hidden = lists(lambda seen: seen["recent"] and "摘要翻译" not in seen["recent"], "hidden")
        assert listed(uuid(14))["hidden"] is True and len(call("/api/terminal")["terminals"]) == started, hidden
        # a finger that moves is scrolling: no sheet
        ask("Input.dispatchTouchEvent", type="touchStart", touchPoints=[{"x": at[0], "y": at[1]}])
        ask("Input.dispatchTouchEvent", type="touchMove", touchPoints=[{"x": at[0], "y": at[1] - 40}])
        time.sleep(0.8)
        ask("Input.dispatchTouchEvent", type="touchEnd", touchPoints=[])
        assert page_value(SHEET) is None
        page_value("scrollTo(0, 0)")
        # what is hidden is still found by a search, and says so
        search("摘要")
        assert lists(lambda seen: seen["results"] is not None, "the hidden one is found")["results"] == ["摘要翻译"] and about("摘要翻译")["pill"] == "已隐藏"
        search("")

        # ---- in its project: the pinned first, the hidden only when asked for
        page_value("(() => { Array.from(document.querySelectorAll('#project-list .card b')).find(b => b.textContent === 'thesis').closest('button').click(); return 1; })()")
        HISTORY = "(() => document.getElementById('project').hidden ? null : { saved: Array.from(document.querySelectorAll('#p-history .card b')).map(b => b.textContent), folded: document.querySelector('#p-history > ul').hidden, put: document.getElementById('hidden-history').hidden ? '' : document.getElementById('hidden-history').textContent })()"
        inside = until(lambda: page_value(HISTORY), 5, "the project's page")
        assert inside == {"saved": ["画流程图", "整理第三章的参考文献"], "folded": True, "put": "已隐藏 1 段 · 显示"}, inside
        page_value("(() => { document.querySelector('#p-history .section-head').click(); return 1; })()")
        assert page_value(HISTORY)["folded"] is False
        page_value("(() => { document.getElementById('hidden-history').click(); return 1; })()")
        assert page_value(HISTORY)["saved"] == ["画流程图", "整理第三章的参考文献", "摘要翻译"] and about("摘要翻译")["pill"] == "已隐藏"
        page_value("(() => { %s('摘要翻译').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })); return 1; })()" % CARD)
        assert until(lambda: page_value(SHEET), 5, "the sheet in a project") == ["置顶", "取消隐藏"]
        choose("取消隐藏")
        until(lambda: listed(uuid(14))["hidden"] is False, 10, "no longer hidden")
        page_value("(() => { document.getElementById('back').click(); return 1; })()")
        lists(lambda seen: seen["recent"] and "摘要翻译" in seen["recent"], "back among what was used lately")

        # ---- a conversation outside every project is taken on as it is: the terminal runs in its folder, and no project
        # is made of it. Here by a copy: what holds this one open is a made-up lock, which cannot be asked to close.
        page_value("(() => { %s('整理读书笔记').querySelector('button.open').click(); return 1; })()" % CARD)
        until(lambda: page_value(WINDOW), 8, "the window of the one outside")
        # the window works beside the conversation: what is said on the computer appears in it while it is open
        with open(homes.rollouts[WORKING], "a", encoding="utf-8") as stream:
            stream.write(json.dumps({"type": "response_item", "payload": {"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": "第二部分写好了。"}]}}, ensure_ascii=False) + "\n")
        # the window asks the computer for the conversation itself: it is there within a moment, whatever the lists know
        until(lambda: "第二部分写好了。" in (page_value("document.querySelector('#sheet .said').textContent") or ""), 4, "what is said on the computer appears in the open window")
        head = page_value("document.querySelector('#sheet .stands').textContent")
        assert head == "正在执行 · Codex · reading-notes", head
        # a message is written in the window; it stays open
        page_value("(() => { const box = document.querySelector('#sheet .write textarea'); box.value = '再加一节小结'; document.querySelector('#sheet .write button').click(); return 1; })()")
        went = hook("Stop", WORKING, notes, last_assistant_message="提纲写好了。", stop_hook_active=False)
        assert went.wait(10) == 0 and "再加一节小结" in json.loads(went.stdout.read().decode("utf-8"))["reason"]
        assert page_value("document.getElementById('sheet').open") is True, "the window is still open"
        page_value("(() => { document.querySelector('#sheet .bar button').click(); return 1; })()")
        until(lambda: page_value("!document.getElementById('sheet').open"), 5, "the way back leaves the window")
        # into a terminal of the phone's: what a held press on the card offers
        page_value("(() => { %s('整理读书笔记').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })); return 1; })()" % CARD)
        offered = until(lambda: page_value(SHEET), 5, "what can be done with the one outside")
        report["offered_for_the_one_outside"] = offered
        assert offered == ["置顶", "接到手机终端（电脑上已关闭时）", "另开一份到手机终端", "把 reading-notes 加为项目"], offered
        assert page_value("document.querySelectorAll('#sheet small, #sheet p').length") == 0, "no lines of explanation"
        choose("另开一份到手机终端")
        apart = until(lambda: next((t for t in call("/api/terminal")["terminals"] if t["dir"] == "" and t["state"] == "running"), None), 30,
                      lambda: "a terminal outside every project runs; the page says: %s; listed: %s" % (page_value("document.getElementById('toast').textContent"), [(t["dir"], t["state"], t.get("error")) for t in call("/api/terminal")["terminals"]]))
        assert (apart["folder"], apart["tool"], apart["title"]) == (str(notes), "codex", "整理读书笔记 · 副本"), apart
        printed = until(lambda: (lambda text: text if "STAND-IN" in text and "]" in text.split("with [")[-1] else None)("".join(c["data"] for c in call("/api/terminal?terminal=%s&after=0" % apart["id"])["chunks"])), 20, "what the stand-in printed")
        report["started_outside_every_project"] = printed[printed.index("STAND-IN"):].split("\r")[0][:200]
        assert ("in [%s]" % notes).lower() in printed.lower() and "fork" in printed and WORKING in printed, printed       # in the conversation's folder, as a copy of it
        assert "reading-notes" not in call("/api/terminal")["device"]["workspaces"]
        until(lambda: "/terminal/" in page_value("location.pathname"), 10, "the page has gone to the terminal")
        on_page = until(lambda: (lambda text: text if "reading-notes" in text else None)(page_value("document.getElementById('status') ? document.getElementById('status').textContent : ''") or ""), 15, "the terminal's page says where it runs")
        assert on_page.startswith("Codex · reading-notes") and page_value("document.getElementById('files').hidden") is True, on_page
        ask("Page.navigate", url="http://127.0.0.1:%d/" % PORT)
        until(lambda: about("整理读书笔记 · 副本"), 15, "the terminal is in the lists")
        copy = about("整理读书笔记 · 副本")
        assert copy["apart"] and copy["meta"][:2] == ["reading-notes", "未加入项目"] and copy["meta"][2].startswith("Codex · 手机终端"), copy
        # typed into like any terminal; and ended
        call("/api/terminal", {"action": "input", "id": new_id(), "terminal": apart["id"], "data": "x"})
        call("/api/terminal", {"action": "close", "id": new_id(), "terminal": apart["id"]})
        until(lambda: next((t for t in call("/api/terminal")["terminals"] if t["id"] == apart["id"] and t["state"] != "running"), None), 20, "it has ended")
        started = len(call("/api/terminal")["terminals"])

        # ---- a project is added: the folders with conversations are offered, each with by what it is used; the ones
        # only a desktop app uses while that app runs (whether it does is this computer's own matter: it is looked up)
        images = subprocess.run(["tasklist", "/FO", "CSV", "/NH"], capture_output=True, text=True, encoding="mbcs", errors="replace").stdout
        names = {row.split('","')[0].strip('"') for row in images.splitlines() if row}
        codex_app = any(n.lower() == "chatgpt.exe" or n == "Codex.exe" for n in names)
        claude_app = "Claude.exe" in names
        report["desktop_apps_running"] = {"codex": codex_app, "claude": claude_app}
        FOLDERS = "(() => { const s = document.getElementById('sheet'); return s.open && s.querySelector('h3').textContent === '添加项目' ? { find: !!s.querySelector('input.find'), small: s.querySelectorAll('#sheet-form > p').length," \
                  " folders: Array.from(s.querySelectorAll('.folders button.choice')).map(b => [b.firstChild.firstChild.textContent, b.querySelector('small').textContent]) } : null; })()"
        find = lambda words: page_value("(() => { const q = document.querySelector('#sheet input.find'); q.value = %s; q.dispatchEvent(new Event('input')); return 1; })()" % json.dumps(words, ensure_ascii=False))
        until(lambda: len(call("/api/terminal")["device"]["candidates"]) >= 8, 30, "the computer has told of the folders")
        page_value("(() => { document.getElementById('query').value = ''; document.getElementById('query').dispatchEvent(new Event('input')); return 1; })()")
        until(lambda: page_value("(() => { const b = document.querySelector('#project-list .card.add button.open'); if (!b || !b.offsetParent) return false; b.click(); return true; })()"), 15, "the card that adds a project")
        offered = until(lambda: page_value(FOLDERS), 5, "the sheet that adds a project")
        report["folders_offered"] = offered
        by = dict(offered["folders"])
        assert offered["find"] and offered["small"] == 0 and offered["folders"][0][0] == "reading-notes", offered
        assert by["term-notes"] == "终端 · " + str(typed) and by["scratch-3"] == "终端 · " + str(more[2]), by
        assert ("desk-notes" in by) == codex_app and ("claude-drafts" in by) == claude_app, (by, codex_app, claude_app)
        assert not codex_app or by["desk-notes"] == "Codex 桌面版 · " + str(desk), by
        assert not claude_app or by["claude-drafts"] == "Claude 桌面版 · " + str(drawn), by
        time.sleep(0.5)
        picture("add-project.png")
        # found by its name, its path, or by what it is used
        find("scratch")
        assert [name for name, _ in page_value(FOLDERS)["folders"]] == ["scratch-%d" % at for at in range(1, 7)], page_value(FOLDERS)
        find("终端 term")
        assert [name for name, _ in page_value(FOLDERS)["folders"]] == ["term-notes"], page_value(FOLDERS)
        choose("term-notes")
        lists(lambda seen: seen["projects"] and "term-notes" in seen["projects"], "the folder that was chosen is a project")
        assert "term-notes" not in [c["name"] for c in call("/api/terminal")["device"]["candidates"]] or until(lambda: "term-notes" not in [c["name"] for c in call("/api/terminal")["device"]["candidates"]], 15, "and is offered no more")

        # ---- the folder of the conversation that is open outside every project is made one
        page_value("(() => { %s('整理读书笔记').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })); return 1; })()" % CARD)
        offered = until(lambda: page_value(SHEET), 5, "the sheet of the one outside")
        assert offered == ["置顶", "接到手机终端（电脑上已关闭时）", "另开一份到手机终端", "把 reading-notes 加为项目"], offered
        choose("把 reading-notes 加为项目")
        joined = lists(lambda seen: seen["projects"] and "reading-notes" in seen["projects"], "its folder is a project")
        until(lambda: (lambda card: card and not card["apart"] and card["meta"][0] == "reading-notes")(about("整理读书笔记")), 30, "the conversation is the project's own")
        report["projects_after"] = joined["projects"]

        # ---- what asks is answered on its card, as before
        page_value("(() => { %s('部署到测试环境').querySelector('.answers .yes').click(); return 1; })()" % CARD)
        assert asking.wait(10) == 0 and json.loads(asking.stdout.read().decode("utf-8"))["hookSpecificOutput"]["decision"]["behavior"] == "allow"
        assert len(call("/api/terminal")["terminals"]) == started, "nothing was started by any of this"
        print(json.dumps(report, ensure_ascii=False))
        print("the first page: by what needs its owner, filtered, searched, pinned, hidden, and a folder made a project")
    finally:
        if browser:
            browser.kill()
        for program in hooks:
            if program.poll() is None:
                program.kill()
        agent.kill(); relay.kill(); homes.close()
        shutil.rmtree(apart_root, ignore_errors=True)
        time.sleep(1)
        try:
            temp.cleanup()
        except OSError:
            pass


if __name__ == "__main__":
    main()
