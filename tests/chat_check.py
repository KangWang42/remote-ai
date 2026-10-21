"""Conversations run through their tool's structured mode (agent-windows/Chats.cs), through the real relay and the
real Windows program, for both tools: a new one begun with its first words, the words given back with the phone's
number, the answer in pieces, a step and what it asked before it ran, the answer from the phone, a message while it
works, a stop, a saved conversation gone on with (what was said before comes first), the list's view of each, and
the end. The tools are stand-ins speaking their protocols (tests/chat_standin.py); their homes are made up.

    python tests/chat_check.py [<folder with RemoteCliAgent.exe>]
"""
import http.client
import json
import secrets
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))
from made_up import Homes       # noqa: E402

SAVED = "0199eeee-ffff-7000-8111-000000000001"


def free_port():
    with socket.socket() as reserved:
        reserved.bind(("127.0.0.1", 0))
        return reserved.getsockname()[1]


def main():
    program = Path(sys.argv[1] if len(sys.argv) > 1 else ROOT / "agent-windows" / "bin") / "RemoteCliAgent.exe"
    port, password = free_port(), "test-" + secrets.token_hex(8)
    temp = tempfile.TemporaryDirectory(prefix="remote-ai-chat-", ignore_cleanup_errors=True)
    data, shop = Path(temp.name) / "agent", Path(temp.name) / "projects" / "web-shop"
    data.mkdir(); shop.mkdir(parents=True)
    homes = Homes(temp.name)
    homes.claude_saved(shop, SAVED, "修复结算页的金额显示", 1, said=("结算页的金额有时多出一位小数", "问题在 formatPrice，已经改好。"))
    environment = homes.environment(RCLI_PASSWORD=password)
    tools = homes.stand_ins(temp.name)
    for name in ("claude", "codex"):
        (tools / (name + ".cmd")).write_text('@if "%%1"=="--help" (echo stand-in) else ("%s" "%s" %s %%*)\r\n' % (sys.executable, ROOT / "tests" / "chat_standin.py", name), encoding="mbcs")
    environment["PATH"] = str(tools) + ";" + environment["PATH"]
    (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % port, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["web-shop=" + str(shop)]}), encoding="utf-8")
    relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(port), "--data", str(Path(temp.name) / "relay")], env=environment, stdout=subprocess.DEVNULL)
    subprocess.run([str(program), "--set-password", str(data)], input=password.encode(), check=True)
    agent = subprocess.Popen([str(program), "--data", str(data)], env=environment)
    cookie = {}
    try:
        def call(path, payload=None, expect=200):
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=60)
            body = None if payload is None else json.dumps(payload).encode()
            connection.request("POST" if body is not None else "GET", path, body=body, headers=dict(cookie, **({"Content-Type": "application/json"} if body else {})))
            reply = connection.getresponse()
            text = reply.read()
            if reply.getheader("Set-Cookie"):
                cookie["Cookie"] = reply.getheader("Set-Cookie").split(";")[0]
            assert reply.status == expect, (path, reply.status, text[:300].decode("utf-8", "replace"))
            return json.loads(text)

        def op(expect="", **payload):
            payload.setdefault("id", secrets.token_hex(16))
            answer = call("/api/terminal", payload)
            for _ in range(200):
                if answer.get("state") != "queued":
                    break
                time.sleep(0.1)
                answer = call("/api/terminal", payload)
            assert (answer.get("error") or "") == expect or expect and expect in (answer.get("error") or ""), (payload["action"], answer)
            return answer

        class Read:
            """What a page reads of a conversation: its events, in order."""
            def __init__(self, terminal):
                self.terminal, self.after, self.rest, self.events = terminal, 0, "", []

            def more(self):
                view = call("/api/terminal?terminal=%s&after=%d" % (self.terminal, self.after))
                for chunk in view["chunks"]:
                    self.after = chunk["seq"]
                    self.rest += chunk["data"]
                *whole, self.rest = self.rest.split("\n")
                self.events += [json.loads(line) for line in whole if line.strip()]
                return view["terminal"]

            def until(self, test, what, seconds=30):
                deadline = time.time() + seconds
                while time.time() < deadline:
                    self.more()
                    found = [e for e in self.events if test(e)]
                    if found:
                        return found[-1]
                    time.sleep(0.15)
                raise AssertionError(("waited in vain for " + what, self.events[-8:]))

            def text(self, item):
                return "".join(e.get("text", "") for e in self.events if e.get("id") == item and e["t"] in ("item", "more"))

        def listed(terminal):
            return next(t for t in call("/api/terminal")["terminals"] if t["id"] == terminal)

        for _ in range(60):
            try:
                call("/api/login", {"password": password})
                break
            except OSError:
                time.sleep(0.5)
        for _ in range(80):
            device = call("/api/terminal")["device"]
            if device["online"] and "chat" in device.get("features", []):
                break
            time.sleep(0.25)
        assert "chat" in device["features"], device

        for tool in ("claude", "codex"):
            # ---- begun with its first words: given back with the phone's number, the answer in pieces, a step that asks
            first = secrets.token_hex(16)
            terminal = op(id=first, action="start", tool=tool, dir="web-shop", form="chat", text="帮我检查一下金额")["terminal"]
            read = Read(terminal)
            mine = read.until(lambda e: e["t"] == "item" and e["kind"] == "user", tool + ": the first words")
            assert mine["text"] == "帮我检查一下金额" and mine["op"] == first, mine
            session = read.until(lambda e: e["t"] == "session", "the conversation's id")["session"]
            ask = read.until(lambda e: e["t"] == "ask", tool + ": what it asks before the step")
            assert [o["id"] for o in ask["options"]] == ["allow", "always", "deny"] and "echo chat-ok" in ask["text"], ask
            step = read.until(lambda e: e["t"] == "item" and e["kind"] == "step", "the step")
            assert "echo chat-ok" in step["text"] + step.get("detail", ""), step
            words = read.until(lambda e: e["t"] == "item" and e["kind"] == "text", "the answer")
            read.until(lambda e: read.text(words["id"]) == "我先看看。", "the answer, from its pieces")
            entry = listed(terminal)
            assert entry["form"] == "chat" and entry["phase"] == "confirm" and any("echo chat-ok" in line for line in entry["asks"]) and entry["session"] == session, entry
            assert entry["title"] == "帮我检查一下金额", entry["title"]
            # what is not a decision is refused by the relay; an answer to nothing by the computer
            call("/api/terminal", {"id": secrets.token_hex(16), "action": "answer", "terminal": terminal, "ask": ask["id"], "decision": "maybe"}, expect=400)
            call("/api/terminal", {"id": secrets.token_hex(16), "action": "input", "terminal": terminal, "data": "y\r"}, expect=400)
            op(expect="已经处理过", action="answer", terminal=terminal, ask="0123456789abcdef", decision="allow")
            op(action="answer", terminal=terminal, ask=ask["id"], decision="allow")
            read.until(lambda e: e["t"] == "answered" and e["id"] == ask["id"] and e["decision"] == "allow", "the answer to what it asked")
            done = read.until(lambda e: e["t"] == "done" and e["id"] == step["id"], "the step's end")
            assert done["ok"] is True, done
            assert "chat-ok" in "".join(e.get("text", "") for e in read.events if e["t"] == "out" and e["id"] == step["id"]) + done.get("result", ""), read.events
            read.until(lambda e: e["t"] == "turn" and e["end"] == "done", tool + ": the end of the turn")
            read.until(lambda e: e["t"] == "state" and e["state"] == "idle", "waiting")
            assert listed(terminal)["phase"] == "idle" and "chat-ok" in listed(terminal)["said"], listed(terminal)

            # ---- told to stop while a step runs: the turn ends as stopped, the conversation stays
            n = len(read.events)
            op(action="say", terminal=terminal, text="慢慢部署")
            ask = read.until(lambda e: e in read.events[n:] and e["t"] == "ask", "the slow step's question")
            op(action="answer", terminal=terminal, ask=ask["id"], decision="always")
            read.until(lambda e: e in read.events[n:] and e["t"] == "state" and e["state"] == "busy", "at work again")
            time.sleep(0.5)
            op(action="stop", terminal=terminal)
            end = read.until(lambda e: e in read.events[n:] and e["t"] == "turn", "the stopped turn")
            assert end["end"] == "stopped", end
            read.until(lambda e: e in read.events[n:] and e["t"] == "state" and e["state"] == "idle", "waiting after the stop")
            assert "没有在执行" in call("/api/terminal", {"id": secrets.token_hex(16), "action": "stop", "terminal": terminal}, expect=400)["error"]

            # ---- a refusal: the step does not run, the turn goes on to its end
            n = len(read.events)
            op(action="say", terminal=terminal, text="再检查一次")
            ask = read.until(lambda e: e in read.events[n:] and e["t"] == "ask", "the third question")
            op(action="answer", terminal=terminal, ask=ask["id"], decision="deny")
            read.until(lambda e: e in read.events[n:] and e["t"] == "turn" and e["end"] == "done", "the turn after a refusal")
            assert not any(e["t"] == "done" and e.get("ok") for e in read.events[n:]), "the refused step did not run"

            # ---- ended: the terminal closes, and the conversation is listed as saved, not held
            op(action="close", terminal=terminal)
            for _ in range(100):
                if listed(terminal)["state"] == "closed":
                    break
                time.sleep(0.1)
            assert listed(terminal)["state"] == "closed"
            print(tool + ": begun with its first words, a step asked for and answered, stopped, refused and ended")

        # ---- a saved conversation gone on with: what was said before comes first, then what follows
        for _ in range(80):
            if any(s["id"] == SAVED and not s["live"] for s in call("/api/terminal")["sessions"]):
                break
            time.sleep(0.25)
        terminal = op(action="start", tool="claude", dir="web-shop", form="chat", session=SAVED)["terminal"]
        read = Read(terminal)
        before = read.until(lambda e: e["t"] == "history", "what was said before")
        assert [m["text"] for m in before["messages"]][:2] == ["结算页的金额有时多出一位小数", "问题在 formatPrice，已经改好。"], before
        assert read.until(lambda e: e["t"] == "session", "its id")["session"] == SAVED
        assert read.events.index(before) < min([read.events.index(e) for e in read.events if e["t"] == "item"] or [len(read.events)])
        # it is the phone's now: going on with it in a terminal at the same time is refused
        call("/api/terminal", {"id": secrets.token_hex(16), "action": "start", "tool": "claude", "dir": "web-shop", "session": SAVED, "history": False}, expect=400)
        op(action="close", terminal=terminal)
        print("a saved conversation gone on with, what was said before first")
    finally:
        agent.kill()
        relay.kill()
        homes.close()


if __name__ == "__main__":
    main()
