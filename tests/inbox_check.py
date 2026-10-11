"""The phone's first page with many conversations, end to end: the real relay and agent, made-up homes of the tools
(tests/made_up.py) and the page in a browser, pressed as a finger presses it. Build the agent first.

    python tests/inbox_check.py <chrome.exe> [output folder]

Checked on the page: what waits for an answer, what is at work and what was used lately stand in their own lists,
across the projects; a conversation that is open in a folder that is no project is among them, with its folder; the
filters by tool and by where a thing runs; one search over all of it, the projects too; a held press (a finger's,
and a mouse's other button) asks what to do with a card and opens nothing; pinning, hiding and finding the hidden
again; making the folder of a conversation a project. Uses a temporary data folder and port 8747 (RCLI_TEST_PORT
changes it). Nothing of the real tools is read or started.
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
ASKING, WORKING, HELD = uuid(1), uuid(2), uuid(3)


def main():
    chrome = sys.argv[1]
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else None
    temp = tempfile.TemporaryDirectory()
    data, profile = Path(temp.name) / "agent", Path(temp.name) / "profile"
    shop, thesis = Path(temp.name) / "web-shop", Path(temp.name) / "thesis"
    # the system's folder for passing things is never spoken of to the phone: the folder that is no project lies elsewhere
    apart_root = ROOT / ".build" / ("inbox-" + secrets.token_hex(4))
    notes = apart_root / "reading-notes"
    for made in (data, shop, thesis, notes):
        made.mkdir(parents=True)
    homes = Homes(temp.name)
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
    environment = homes.environment(RCLI_PASSWORD=password, REMOTECLI_UNATTENDED="600")       # nobody is at the computer
    environment["PATH"] = str(homes.stand_ins(temp.name)) + ";" + environment["PATH"]
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
            raise AssertionError("never: " + what)

        def hook(name, session, folder, **more):
            """The hook, as Codex runs it for an event of a conversation."""
            event = dict({"hook_event_name": name, "session_id": session, "transcript_path": str(homes.rollouts[session]), "cwd": str(folder), "model": "gpt", "permission_mode": "default"}, **more)
            program = subprocess.Popen([str(agent_exe), "--hook", "codex", pipe], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
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
        assert {s["id"] for s in call("/api/terminal")["sessions"]} == {uuid(n) for n in (1, 2, 3, 10, 11, 12, 13, 14, 15, 16)}, [s["title"] for s in call("/api/terminal")["sessions"]]
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
                assert time.time() < deadline, (what, seen)
                time.sleep(0.3)

        # ---- by what needs its owner: what asks, what is at work, what was used lately, across the projects
        first = lists(lambda seen: seen["needs"] and seen["recent"] and seen["active"], "the lists are filled")
        report["first"] = first
        mine = (first["needs"] or []) + (first["active"] or [])
        assert "部署到测试环境" in first["needs"] and "整理读书笔记" in first["active"] and "看日志" in mine and len(mine) == 3, first
        assert first["recent"] == ["修复结算页的金额显示", "给结算页加上优惠券", "画流程图", "整理第三章的参考文献", "摘要翻译", "旧的讨论"] and first["more"], first
        assert first["background"] == ["被占着的对话"] and first["pinned"] is None and first["projects"] == ["web-shop", "thesis"] and "等你确认" in first["summary"], first
        assert first["filters"][:4] == ["*全部", "Claude Code", "Codex", "PowerShell"] and "手机终端" in first["filters"], first
        # each card says where it is and by what it runs; the one outside every project says its folder, and that it is none
        outside, shell, saved = about("整理读书笔记"), about("看日志"), about("画流程图")
        report["cards"] = {"outside": outside, "terminal": shell, "saved": saved}
        assert outside["pill"] == "正在执行" and outside["apart"] and outside["meta"][:2] == ["reading-notes", "未加入项目"], outside
        assert shell["meta"][0] == "thesis" and shell["meta"][1].startswith("PowerShell · 手机终端") and not shell["apart"], shell
        assert saved["meta"][0] == "thesis" and saved["meta"][1].startswith("Codex · ") and saved["pill"] == "", saved
        picture("inbox.png")
        page_value("(() => { document.getElementById('more-recent').click(); return 1; })()")
        lists(lambda seen: seen["recent"] and len(seen["recent"]) == 7 and not seen["more"], "the rest of what was used lately")

        # ---- told apart by tool, and by where a thing runs
        press("Claude Code")
        only = lists(lambda seen: seen["filters"][1] == "*Claude Code", "one tool")
        assert only["needs"] is None and only["active"] is None and only["background"] is None and len(only["recent"]) == 6 and "画流程图" not in only["recent"] and not only["nothing"], only
        press("手机终端")
        none = lists(lambda seen: seen["nothing"], "nothing is both")
        assert none["recent"] is None and "*手机终端" in none["filters"] and none["projects"] == ["web-shop", "thesis"], none
        press("Claude Code")
        phone = lists(lambda seen: not seen["nothing"], "what runs from the phone")
        assert (phone["needs"] or []) + (phone["active"] or []) == ["看日志"] and phone["recent"] is None, phone
        press("全部")
        lists(lambda seen: seen["filters"][0] == "*全部" and seen["recent"] and len(seen["recent"]) == 6, "everything again")

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
        HISTORY = "(() => document.getElementById('project').hidden ? null : { saved: Array.from(document.querySelectorAll('#p-history .card b')).map(b => b.textContent), put: document.getElementById('hidden-history').hidden ? '' : document.getElementById('hidden-history').textContent })()"
        inside = until(lambda: page_value(HISTORY), 5, "the project's page")
        assert inside == {"saved": ["画流程图", "整理第三章的参考文献"], "put": "已隐藏 1 段 · 显示"}, inside
        page_value("(() => { document.getElementById('hidden-history').click(); return 1; })()")
        assert page_value(HISTORY)["saved"] == ["画流程图", "整理第三章的参考文献", "摘要翻译"] and about("摘要翻译")["pill"] == "已隐藏"
        page_value("(() => { %s('摘要翻译').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })); return 1; })()" % CARD)
        assert until(lambda: page_value(SHEET), 5, "the sheet in a project") == ["置顶", "取消隐藏"]
        choose("取消隐藏")
        until(lambda: listed(uuid(14))["hidden"] is False, 10, "no longer hidden")
        page_value("(() => { document.getElementById('back').click(); return 1; })()")
        lists(lambda seen: seen["recent"] and "摘要翻译" in seen["recent"], "back among what was used lately")

        # ---- the folder of the conversation that is open outside every project is made one
        page_value("(() => { %s('整理读书笔记').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })); return 1; })()" % CARD)
        offered = until(lambda: page_value(SHEET), 5, "the sheet of the one outside")
        assert offered == ["置顶", "把所在文件夹加为项目"], offered
        choose("把所在文件夹加为项目")
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
