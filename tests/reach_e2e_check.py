"""A conversation that Codex has open on the computer, answered and told from the phone, end to end: the real relay,
the real agent and the real hook program, with a made-up Codex home (a saved conversation that something holds
open) and made-up hook events, handed over the way Codex hands them. Build the agent first.

    python tests/reach_e2e_check.py [chrome.exe [output folder]]

Checked through the relay, as the phone asks it: the conversation is listed as one at work, a request for
permission is shown and answered, a message left while the tool works reaches it when it stops, and one sent while
it waits is taken at once. With a browser, the list page shows the card and its "允许" answers the request.
Uses a temporary data folder and port 8739 (RCLI_TEST_PORT changes it). Nothing of the real tools is touched.
"""
import base64
import hashlib
import http.client
import json
import msvcrt
import os
import secrets
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
SESSION = "0199aaaa-bbbb-7ccc-8ddd-eeeeffff0001"


def main():
    chrome = sys.argv[1] if len(sys.argv) > 1 else None
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else None
    temp = tempfile.TemporaryDirectory()
    data, work, codex, profile = (Path(temp.name) / name for name in ("agent", "demo", "codex", "profile"))
    for made in (data, work, codex / "sessions" / "2026" / "10" / "11", codex / "thread-writer-locks"):
        made.mkdir(parents=True)
    # a conversation Codex saved in the project's folder, with a name, and a lock on it that a program holds
    rollout = codex / "sessions" / "2026" / "10" / "11" / ("rollout-2026-10-11T08-00-00-%s.jsonl" % SESSION)
    rollout.write_text(json.dumps({"type": "session_meta", "payload": {"id": SESSION, "cwd": str(work)}}) + "\n", encoding="utf-8")
    (codex / "session_index.jsonl").write_text(json.dumps({"id": SESSION, "thread_name": "给结算页加上优惠券"}, ensure_ascii=False) + "\n", encoding="utf-8")
    lock = open(codex / "thread-writer-locks" / (SESSION + ".lock"), "w+b")
    lock.write(b"held"); lock.flush(); lock.seek(0)
    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 4)
    environment = dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1", CODEX_HOME=str(codex), REMOTECLI_UNATTENDED="600")       # nobody is at the computer
    (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % PORT, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["demo=" + str(work)]}), encoding="utf-8")
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

        def listed():
            return next((s for s in call("/api/terminal").get("sessions", []) if s["id"] == SESSION), None)

        def until(test, seconds, what):
            deadline = time.time() + seconds
            while time.time() < deadline:
                found = test()
                if found:
                    return found
                time.sleep(0.2)
            raise AssertionError("never: %s; listed: %s" % (what, json.dumps(listed(), ensure_ascii=False)))

        def hook(name, **more):
            """The hook, as Codex runs it for an event. Returns the running program; its output is what Codex reads."""
            event = dict({"hook_event_name": name, "session_id": SESSION, "transcript_path": str(rollout), "cwd": str(work), "model": "gpt", "permission_mode": "default"}, **more)
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
        assert call("/api/conversation", {"id": new_id(), "session": SESSION, "action": "say", "text": "然后把说明文档也更新"}) == {"now": False}
        went = json.loads(said(hook("Stop", last_assistant_message="优惠券已经加上了。", stop_hook_active=False)))
        assert went["decision"] == "block" and "然后把说明文档也更新" in went["reason"], went
        stopping = hook("Stop", last_assistant_message="文档也更新了。", stop_hook_active=True)
        waiting = until(lambda: (lambda s: s if s["status"] == "waiting" else None)(listed()), 10, "it has stopped and waits for the phone")
        assert waiting["last"] == "文档也更新了。" and waiting["reach"] == ["say"] and stopping.poll() is None, waiting
        report["listed_while_it_waits"] = {key: waiting[key] for key in ("status", "reach", "last", "activity", "host")}
        assert call("/api/conversation", {"id": new_id(), "session": SESSION, "action": "say", "text": "再跑一遍测试"}) == {"now": True}
        went = json.loads(said(stopping))
        assert went["decision"] == "block" and "再跑一遍测试" in went["reason"], went
        until(lambda: listed()["status"] == "busy", 10, "it works on")

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
            CARD = "(() => { const card = Array.from(document.querySelectorAll('#active-list .card')).find(c => c.textContent.includes('给结算页加上优惠券')); if (!card) return null;" \
                   " return { pill: card.querySelector('.pill').textContent, kind: card.className, ask: (card.querySelector('.ask pre') || {}).textContent || '', buttons: Array.from(card.querySelectorAll('.answers button')).map(b => b.textContent), say: !!card.querySelector('.side.say'), peek: (card.querySelector('.peek') || {}).textContent || '', summary: document.getElementById('device-text').textContent }; })()"
            deadline = time.time() + 20
            while time.time() < deadline and not (page_value(CARD) or {}).get("ask"):
                time.sleep(0.3)
            card = page_value(CARD)
            report["card_asking"] = card
            assert card and card["pill"] == "等你确认" and "npm test" in card["ask"] and card["buttons"] == ["允许", "拒绝", "看对话"] and "等你确认" in card["summary"], card
            picture("reach-asking.png")
            page_value("(() => { const card = Array.from(document.querySelectorAll('#active-list .card')).find(c => c.textContent.includes('给结算页加上优惠券')); card.querySelector('.answers .yes').click(); return 1; })()")
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
        print("a conversation open on the computer: listed at work, asked and answered, told what to do next, from the phone")
    finally:
        if browser:
            browser.kill()
        agent.kill(); relay.kill(); lock.close()
        time.sleep(1)
        try:
            temp.cleanup()
        except OSError:
            pass


if __name__ == "__main__":
    main()
