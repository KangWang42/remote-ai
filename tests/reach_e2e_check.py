"""A conversation that Codex has open on the computer, answered and told from the phone, end to end: the real relay,
the real agent and the real hook program, with a made-up Codex home (a saved conversation that something holds
open) and made-up hook events, handed over the way Codex hands them. Build the agent first.

    python tests/reach_e2e_check.py [chrome.exe [output folder]]

Checked through the relay, as the phone asks it: the conversation is listed as one at work, a request for
permission is shown and answered, a message left while the tool works reaches it when it stops, and one sent while
it waits is taken at once; it is pinned and hidden, and the computer keeps that; a conversation that is open in a
folder that is no project is listed with its folder, read and told from the phone, and becomes the project's own
once the folder is added. With a browser, the list page shows the card and its "允许" answers the request.
Uses a temporary data folder and port 8739 (RCLI_TEST_PORT changes it). Nothing of the real tools is touched.
"""
import base64
import hashlib
import http.client
import json
import msvcrt
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
from ws_client import Socket

ROOT = Path(__file__).resolve().parents[1]
PORT = int(os.environ.get("RCLI_TEST_PORT", "8739"))
password = "test-" + secrets.token_hex(8)
new_id = lambda: secrets.token_hex(16)
SESSION, OUTSIDE = "0199aaaa-bbbb-7ccc-8ddd-eeeeffff0001", "0199aaaa-bbbb-7ccc-8ddd-eeeeffff0002"
INSIDE = "0199aaaa-bbbb-7ccc-8ddd-eeeeffff0003"       # one that runs in a terminal of the phone's own


def main():
    chrome = sys.argv[1] if len(sys.argv) > 1 else None
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else None
    temp = tempfile.TemporaryDirectory()
    data, work, codex, profile = (Path(temp.name) / name for name in ("agent", "demo", "codex", "profile"))
    # the system's folder for passing things is never spoken of to the phone: the folder that is no project lies elsewhere
    apart_root = ROOT / ".build" / ("e2e-" + secrets.token_hex(4))
    elsewhere = apart_root / "reading-notes"
    for made in (data, work, elsewhere, codex / "sessions" / "2026" / "10" / "11", codex / "thread-writer-locks"):
        made.mkdir(parents=True)
    # a conversation Codex saved in the project's folder, with a name, and a lock on it that a program holds
    rollout = codex / "sessions" / "2026" / "10" / "11" / ("rollout-2026-10-11T08-00-00-%s.jsonl" % SESSION)
    rollout.write_text(json.dumps({"type": "session_meta", "payload": {"id": SESSION, "cwd": str(work)}}) + "\n", encoding="utf-8")
    (codex / "session_index.jsonl").write_text(json.dumps({"id": SESSION, "thread_name": "给结算页加上优惠券"}, ensure_ascii=False) + "\n", encoding="utf-8")
    lock = open(codex / "thread-writer-locks" / (SESSION + ".lock"), "w+b")
    lock.write(b"held"); lock.flush(); lock.seek(0)
    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 4)
    # another, which a program holds open in a folder that is no project, with something said in it
    apart = codex / "sessions" / "2026" / "10" / "11" / ("rollout-2026-10-11T09-00-00-%s.jsonl" % OUTSIDE)
    spoken = [("user", "把这一章的笔记整理成提纲"), ("assistant", "提纲分成三部分。")]
    apart.write_text("\n".join([json.dumps({"type": "session_meta", "payload": {"id": OUTSIDE, "cwd": str(elsewhere)}})] + [json.dumps(
        {"type": "response_item", "payload": {"type": "message", "role": role, "content": [{"type": "input_text" if role == "user" else "output_text", "text": text}]}}, ensure_ascii=False) for role, text in spoken]) + "\n", encoding="utf-8")
    with open(codex / "session_index.jsonl", "a", encoding="utf-8") as index:
        index.write(json.dumps({"id": OUTSIDE, "thread_name": "整理读书笔记"}, ensure_ascii=False) + "\n")
    lock_apart = open(codex / "thread-writer-locks" / (OUTSIDE + ".lock"), "w+b")
    lock_apart.write(b"held"); lock_apart.flush(); lock_apart.seek(0)
    msvcrt.locking(lock_apart.fileno(), msvcrt.LK_NBLCK, 4)
    environment = dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1", CODEX_HOME=str(codex), REMOTECLI_UNATTENDED="600")       # nobody is at the computer
    # The Codex of this check is a script that does nothing: what is checked here is what the hooks alone make
    # possible, and nothing is ever handed to the Codex that is installed on this computer.
    tools = Path(temp.name) / "tools"
    tools.mkdir()
    (tools / "codex.cmd").write_text("@exit /b 2\r\n", encoding="mbcs")
    environment["PATH"] = str(tools) + ";" + environment["PATH"]
    (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % PORT, "RemoteEnabled": True, "RemoteMaxMode": "full", "ReachWait": 300, "ReachAsk": True, "RemoteDirs": ["demo=" + str(work)]}), encoding="utf-8")       # its owner has Codex's requests, and a tool that stops, kept for the phone
    relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(PORT), "--data", str(Path(temp.name) / "relay")], env=environment, stdout=subprocess.DEVNULL)
    agent_exe = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
    subprocess.run([str(agent_exe), "--set-password", str(data)], input=password.encode(), check=True)
    agent = subprocess.Popen([str(agent_exe), "--data", str(data)], env=environment)
    pipe = "RemoteCli.reach." + hashlib.sha256(os.path.abspath(str(data)).lower().encode("utf-8")).hexdigest()[:12]
    browser, report = None, {}
    try:
        cookie = {}

        def call(path, payload=None, expect=200):
            connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=40)
            body = None if payload is None else json.dumps(payload).encode()
            connection.request("POST" if body is not None else "GET", path, body=body, headers=dict(cookie, **({"Content-Type": "application/json"} if body is not None else {})))
            reply = connection.getresponse()
            text = reply.read()
            if reply.getheader("Set-Cookie"):
                cookie["Cookie"] = reply.getheader("Set-Cookie").split(";")[0]
            assert reply.status == expect, (path, reply.status, text[:300].decode("utf-8", "replace"))
            return json.loads(text)

        def listed(session=SESSION):
            return next((s for s in call("/api/terminal").get("sessions", []) if s["id"] == session), None)

        def until(test, seconds, what):
            deadline = time.time() + seconds
            while time.time() < deadline:
                found = test()
                if found:
                    return found
                time.sleep(0.2)
            raise AssertionError("never: %s; listed: %s" % (what, json.dumps(listed(), ensure_ascii=False)))

        def hook(name, session=SESSION, **more):
            """The hook, as Codex runs it for an event. Returns the running program; its output is what Codex reads."""
            event = dict({"hook_event_name": name, "session_id": session, "transcript_path": str(rollout if session == SESSION else apart), "cwd": str(work if session == SESSION else elsewhere), "model": "gpt", "permission_mode": "default"}, **more)
            program = subprocess.Popen([str(agent_exe), "--hook", "codex", pipe], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
            program.stdin.write(json.dumps(event, ensure_ascii=False).encode("utf-8")); program.stdin.close()
            return program

        def said(program, seconds=10):
            assert program.wait(seconds) == 0
            return program.stdout.read().decode("utf-8")

        for _ in range(40):
            try:
                call("/api/login", {"password": password})
                break
            except OSError:
                time.sleep(0.5)
        # ---- held by a program, and nothing told of it yet: it is listed as before, as one that is held
        first = until(lambda: (lambda s: s if s and s["live"] else None)(listed()), 40, "the conversation is listed as one a program has open")
        assert (first["title"], first["reach"], first["activity"], first["tool"], first["dir"]) == ("给结算页加上优惠券", None, "locked", "codex", "demo"), first
        assert "reach" in call("/api/terminal")["device"]["features"]
        refused = call("/api/conversation", {"id": new_id(), "session": SESSION, "action": "say", "text": "hi"}, expect=400)
        assert "不能从手机发消息" in refused["error"], refused
        # ---- its tool tells of it: it is one at work, and can be left a message
        assert said(hook("UserPromptSubmit", prompt="加上优惠券")) == ""
        busy = until(lambda: (lambda s: s if s["status"] == "busy" else None)(listed()), 10, "it works")
        assert (busy["reach"], busy["activity"], busy["ask"]) == (["say"], "active", None), busy
        # (and by which way each thing is done, with the computer's words for what cannot be: Codex's command line
        # is told through its hooks while it works, and cannot be stopped from outside)
        assert busy["ways"]["say"] == "hook" and busy["ways"]["stop"] == "" and "Codex 命令行" in busy["why"]["stop"] and "say" not in busy["why"], (busy["ways"], busy["why"])
        # ---- it asks for permission, with nobody at the computer: the phone is shown the request and answers it
        asking = hook("PermissionRequest", tool_name="Bash", tool_input={"command": "npm install coupon-kit", "description": "Install the coupon library"})
        asked = until(lambda: (lambda s: s if s["status"] == "confirm" else None)(listed()), 10, "it asks")
        assert asked["reach"] == ["say", "answer"] and "npm install coupon-kit" in asked["ask"]["text"] and "Install the coupon library" in asked["ask"]["text"], asked
        wrong = call("/api/conversation", {"id": new_id(), "session": SESSION, "action": "answer", "ask": "ffffffffffff", "allow": True}, expect=400)
        assert "已经处理过" in wrong["error"] and asking.poll() is None, wrong
        assert call("/api/conversation", {"id": new_id(), "session": SESSION, "action": "answer", "ask": asked["ask"]["id"], "allow": True}) == {}
        verdict = json.loads(said(asking))
        assert verdict["hookSpecificOutput"]["decision"]["behavior"] == "allow", verdict
        until(lambda: listed()["status"] == "busy" and listed()["ask"] is None, 10, "it works again")
        # ---- a message left while it works reaches it when it stops; then it waits, and one is taken at once
        assert call("/api/conversation", {"id": new_id(), "session": SESSION, "action": "say", "text": "然后把说明文档也更新"}) == {"status": "accepted", "sent": True, "now": False, "way": "hook"}
        went = json.loads(said(hook("Stop", last_assistant_message="优惠券已经加上了。", stop_hook_active=False)))
        assert went["decision"] == "block" and "然后把说明文档也更新" in went["reason"], went
        stopping = hook("Stop", last_assistant_message="文档也更新了。", stop_hook_active=True)
        waiting = until(lambda: (lambda s: s if s["status"] == "waiting" else None)(listed()), 10, "it has stopped and waits for the phone")
        assert waiting["last"] == "文档也更新了。" and waiting["reach"] == ["say"] and stopping.poll() is None, waiting
        report["listed_while_it_waits"] = {key: waiting[key] for key in ("status", "reach", "last", "activity", "host")}
        assert call("/api/conversation", {"id": new_id(), "session": SESSION, "action": "say", "text": "再跑一遍测试"}) == {"status": "accepted", "sent": True, "now": True, "way": "hook"}
        went = json.loads(said(stopping))
        assert went["decision"] == "block" and "再跑一遍测试" in went["reason"], went
        until(lambda: listed()["status"] == "busy", 10, "it works on")
        # ---- unless its owner chose a time, a tool that stops is not kept: its hook ends at once, and the phone is
        # told all the same that it is its turn. What is said then goes in by the tool's own way, where it has one
        # (this stand-in for Codex has none: tests/inbox_check.py sends such a message).
        settings = json.loads((data / "config.json").read_text(encoding="utf-8"))
        (data / "config.json").write_text(json.dumps(dict(settings, ReachWait=0, ReachAsk=False)), encoding="utf-8")
        began = time.time()
        # a request of Codex's: its hook ends at once, so Codex shows its own question; the phone sees that it asks
        assert said(hook("PermissionRequest", tool_name="Bash", tool_input={"command": "npm publish", "description": "Publish it"})) == "" and time.time() - began < 5
        shown = until(lambda: (lambda s: s if s["status"] == "confirm" else None)(listed()), 10, "it asks, at the computer")
        assert shown["reach"] == ["say"] and "npm publish" in shown["ask"]["text"], shown
        call("/api/conversation", {"id": new_id(), "session": SESSION, "action": "answer", "ask": shown["ask"]["id"], "allow": True}, expect=400)
        report["codex_request_unkept"] = {key: shown[key] for key in ("status", "reach")}
        began = time.time()
        assert said(hook("Stop", last_assistant_message="测试也跑完了。", stop_hook_active=False)) == "" and time.time() - began < 5
        rested = until(lambda: (lambda s: s if s["status"] == "waiting" else None)(listed()), 10, "it has stopped, and it is the phone's turn")
        assert rested["last"] == "测试也跑完了。" and rested["reach"] == [] and rested["wake"] is False, rested
        report["listed_when_it_has_stopped_unkept"] = {key: rested[key] for key in ("status", "reach", "wake", "last")}
        (data / "config.json").write_text(json.dumps(settings), encoding="utf-8")
        said(hook("UserPromptSubmit", prompt="接着做"))
        until(lambda: listed()["status"] == "busy", 10, "it works on, told at the computer")

        # ---- pinned and hidden from the phone: the computer keeps it, and goes on saying so in its own reports
        assert "marks" in call("/api/terminal")["device"]["features"] and (listed()["pinned"], listed()["hidden"]) == (False, False)
        assert call("/api/conversation", {"id": new_id(), "session": SESSION, "action": "mark", "pinned": True}) == {}
        assert listed()["pinned"] is True
        assert SESSION in (data / "conversation-marks.json").read_text(encoding="utf-8")
        time.sleep(12)          # the computer lists its conversations anew at least every ten seconds
        assert (listed()["pinned"], listed()["hidden"]) == (True, False), listed()
        assert call("/api/conversation", {"id": new_id(), "session": SESSION, "action": "mark", "pinned": False, "hidden": True}) == {}
        until(lambda: (listed()["pinned"], listed()["hidden"]) == (False, True), 5, "no longer pinned, and hidden")
        assert call("/api/conversation", {"id": new_id(), "session": SESSION, "action": "mark", "hidden": False}) == {}
        assert SESSION not in (data / "conversation-marks.json").read_text(encoding="utf-8")

        # ---- a conversation open in a folder that is no project: listed without a project, with its folder
        outside = until(lambda: listed(OUTSIDE), 40, "the conversation outside every project is listed")
        assert (outside["dir"], outside["folder"], outside["live"], outside["title"], outside["tool"]) == ("", str(elsewhere), True, "整理读书笔记", "codex"), outside
        report["listed_outside_every_project"] = {key: outside[key] for key in ("dir", "live", "host", "activity", "reach")}
        got = call("/api/conversation", {"id": new_id(), "session": OUTSIDE})
        assert [(m["role"], m["text"]) for m in got["messages"]] == spoken, got       # it is read where it is
        # its tool tells of it: it is told from the phone like any other
        assert said(hook("UserPromptSubmit", OUTSIDE, prompt="整理提纲")) == ""
        until(lambda: (lambda s: s if s["status"] == "busy" and s["reach"] == ["say"] else None)(listed(OUTSIDE)), 10, "the one outside works")
        assert call("/api/conversation", {"id": new_id(), "session": OUTSIDE, "action": "say", "text": "再加一节小结"}) == {"status": "accepted", "sent": True, "now": False, "way": "hook"}
        went = json.loads(said(hook("Stop", OUTSIDE, last_assistant_message="提纲写好了。", stop_hook_active=False)))
        assert went["decision"] == "block" and "再加一节小结" in went["reason"], went
        # a terminal of the phone's takes it on like any other, by closing what has it open or by a copy: without either it is refused, as one in a project is
        refused = call("/api/terminal", {"action": "start", "id": new_id(), "tool": "codex", "dir": "", "session": OUTSIDE}, expect=400)
        assert "接管" in refused["error"], refused
        # once the folder is a project, the conversation is one of that project
        assert call("/api/terminal", {"action": "project_add", "id": new_id(), "path": str(elsewhere), "name": "", "create": False})["state"] in ("queued", "done")
        joined = until(lambda: (lambda s: s if s and s["dir"] else None)(listed(OUTSIDE)), 30, "the conversation belongs to the new project")
        assert (joined["dir"], joined["folder"]) == ("reading-notes", "") and "reading-notes" in call("/api/terminal")["device"]["workspaces"], joined

        # ---- in one of the phone's own terminals nothing is kept: the phone is at that terminal, where the tool shows
        # its own question. The same request from outside it, with nobody at the computer, waits for the phone (above).
        terminal = call("/api/terminal", {"action": "start", "id": new_id(), "tool": "shell", "dir": "demo"})["terminal"]
        event = Path(temp.name) / "inside.json"
        event.write_text(json.dumps({"hook_event_name": "PermissionRequest", "session_id": INSIDE, "transcript_path": str(rollout), "cwd": str(work), "tool_name": "Bash", "tool_input": {"command": "git push"}}), encoding="ascii")
        after, printed, typed, deadline = 0, "", 0, time.time() + 60
        while time.time() < deadline and "HOOK_ENDED" not in printed:
            item = call("/api/terminal?terminal=%s&after=%d&wait=5" % (terminal, after))
            after = item["after"]
            printed += "".join(chunk["data"] for chunk in item["chunks"])
            if item["terminal"]["state"] == "running" and not typed and printed:
                time.sleep(1.5)
                call("/api/terminal", {"action": "input", "id": new_id(), "terminal": terminal, "data": "Get-Content -Raw '%s' | & '%s' --hook codex %s; 'HOOK_'+'ENDED'\r" % (event, agent_exe, pipe)})
                typed = time.time()
        assert "HOOK_ENDED" in printed and time.time() - typed < 12, printed[-400:]
        report["hook_in_own_terminal_ended_after_s"] = round(time.time() - typed, 1)
        call("/api/terminal", {"action": "close", "id": new_id(), "terminal": terminal})

        # ---- the list page: a card like a terminal's, in the same list; "允许" on it answers a request
        if chrome:
            asking = hook("PermissionRequest", tool_name="Bash", tool_input={"command": "npm test", "description": "Run the tests"})
            asked = until(lambda: (lambda s: s if s["status"] == "confirm" else None)(listed()), 10, "it asks again")
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
                return ask("Runtime.evaluate", expression=expression, returnByValue=True, awaitPromise=True).get("result", {}).get("value")

            def picture(name):
                if out:
                    out.mkdir(parents=True, exist_ok=True)
                    (out / name).write_bytes(base64.b64decode(ask("Page.captureScreenshot", format="png")["data"]))

            ask("Page.enable"); ask("Runtime.enable")
            ask("Emulation.setDeviceMetricsOverride", width=412, height=892, deviceScaleFactor=2, mobile=True)
            ask("Page.navigate", url="http://127.0.0.1:%d/#p=%s" % (PORT, password))
            CARD = "(() => { const card = Array.from(document.querySelectorAll('#overview .card')).find(c => c.textContent.includes('给结算页加上优惠券')); if (!card) return null;" \
                   " return { pill: card.querySelector('.pill').textContent, kind: card.className, ask: (card.querySelector('.ask pre') || {}).textContent || '', buttons: Array.from(card.querySelectorAll('.answers button')).map(b => b.textContent), say: !!card.querySelector('.side.say'), peek: (card.querySelector('.peek') || {}).textContent || '', summary: document.getElementById('device-text').textContent }; })()"
            deadline = time.time() + 20
            while time.time() < deadline and not (page_value(CARD) or {}).get("ask"):
                time.sleep(0.3)
            card = page_value(CARD)
            report["card_asking"] = card
            assert card and card["pill"] == "等你确认" and "npm test" in card["ask"] and card["buttons"] == ["允许", "拒绝", "看对话"] and "等你确认" in card["summary"], card
            picture("reach-asking.png")
            page_value("(() => { const card = Array.from(document.querySelectorAll('#overview .card')).find(c => c.textContent.includes('给结算页加上优惠券')); card.querySelector('.answers .yes').click(); return 1; })()")
            verdict = json.loads(said(asking))
            assert verdict["hookSpecificOutput"]["decision"]["behavior"] == "allow", verdict
            # it stops: the card says that it waits for a message, shows what it said last, and offers to send one
            stopping = hook("Stop", last_assistant_message="测试全部通过。", stop_hook_active=False)
            deadline = time.time() + 20
            while time.time() < deadline and (page_value(CARD) or {}).get("pill") != "等你的消息":
                time.sleep(0.3)
            card = page_value(CARD)
            report["card_waiting"] = card
            assert card["pill"] == "等你的消息" and card["say"] and card["peek"] == "测试全部通过。" and not card["ask"], card
            picture("reach-waiting.png")
            stopping.kill()
        print(json.dumps(report, ensure_ascii=False))
        print("a conversation open on the computer: listed at work, asked and answered, told what to do next, pinned and hidden, from the phone; one outside every project too")
    finally:
        if browser:
            browser.kill()
        agent.kill(); relay.kill(); lock.close(); lock_apart.close()
        shutil.rmtree(apart_root, ignore_errors=True)
        time.sleep(1)
        try:
            temp.cleanup()
        except OSError:
            pass


if __name__ == "__main__":
    main()
