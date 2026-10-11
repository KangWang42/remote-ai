"""Looking before taking over, and one terminal used from two places, end to end with an isolated relay and the
real agent. Build the agent first.

    python tests/look_check.py

- a conversation saved on the computer is read back through the relay: what was asked, answered and done;
- a ticket signs a second viewer in once, without the password;
- that second viewer and the first see the same terminal: what one types the other reads, and the terminal takes
  the size of whoever asked last.

The made-up conversation lies in a home of Claude Code's that is made here (CLAUDE_CONFIG_DIR, and CODEX_HOME for
Codex): the agent reads nothing of the tools on this computer, and nothing is written into the real profile.
"""
import http.client
import json
import os
import re
import secrets
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PORT = int(os.environ.get("RCLI_TEST_PORT", "8739"))
password = "test-" + secrets.token_hex(8)


PLAIN = re.compile(r"\x1b\[[0-9;?<=>]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]|\r")      # what a terminal draws with


class Viewer:
    """One signed-in place: the phone, or the window on the computer."""

    def __init__(self):
        self.cookie = {}

    def call(self, path, payload=None, expect=200):
        connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=40)
        body = None if payload is None else json.dumps(payload).encode()
        connection.request("POST" if body is not None else "GET", path, body=body,
                           headers=dict(self.cookie, **({"Content-Type": "application/json"} if body is not None else {})))
        reply = connection.getresponse()
        text = reply.read()
        if reply.getheader("Set-Cookie"):
            self.cookie["Cookie"] = reply.getheader("Set-Cookie").split(";")[0]
        assert reply.status == expect, (path, reply.status, text[:200].decode("utf-8", "replace"))
        return json.loads(text)

    def op(self, **payload):
        return self.call("/api/terminal", dict(payload, id=secrets.token_hex(16)))

    def read(self, terminal, marker, seconds=40):
        after, text, deadline = 0, "", time.time() + seconds
        while time.time() < deadline:
            view = self.call("/api/terminal?terminal=%s&after=%d&wait=3" % (terminal, after))
            after = view["after"]
            text += "".join(chunk["data"] for chunk in view["chunks"])
            if (marker in PLAIN.sub("", text)) if marker else text:
                return PLAIN.sub("", text)
        raise AssertionError("never saw %r in %r" % (marker, PLAIN.sub("", text)[-300:]))


with tempfile.TemporaryDirectory(prefix="remote-cli-look-", ignore_cleanup_errors=True) as temp:       # a shell that is still leaving may hold its folder
    project, data = Path(temp) / "demo", Path(temp) / "agent"
    project.mkdir()
    data.mkdir()
    homes = {"CLAUDE_CONFIG_DIR": str(Path(temp) / "claude-home"), "CODEX_HOME": str(Path(temp) / "codex-home")}
    saved = Path(homes["CLAUDE_CONFIG_DIR"]) / "projects" / re.sub("[^a-zA-Z0-9]", "-", str(project))
    session = str(uuid.uuid4())
    saved.mkdir(parents=True)
    rows = [
        {"type": "user", "message": {"role": "user", "content": "把登录页的报错修一下"}},
        {"type": "user", "message": {"role": "user", "content": "<system-reminder>不给人看</system-reminder>"}},
        {"type": "assistant", "message": {"role": "assistant", "content": [
            {"type": "text", "text": "我先看看登录的代码。"}, {"type": "tool_use", "name": "Bash", "input": {"command": "grep -rn login src"}}]}},
        {"type": "user", "message": {"role": "user", "content": [{"type": "tool_result", "content": "很长的输出"}]}},
        {"type": "assistant", "message": {"role": "assistant", "content": [{"type": "text", "text": "找到了，是密码为空时没有提示。要我现在改吗？"}]}},
        {"type": "ai-title", "aiTitle": "修登录页报错", "sessionId": session},
    ]
    (saved / (session + ".jsonl")).write_text("".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows), encoding="utf-8")
    (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % PORT, "RemoteEnabled": True, "RemoteMaxMode": "full",
                                                  "RemoteDirs": ["demo=%s" % project]}), encoding="utf-8")
    hidden = {"creationflags": subprocess.CREATE_NO_WINDOW}
    relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(PORT), "--data", str(Path(temp) / "relay")],
                             env=dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1"), stdout=subprocess.DEVNULL, **hidden)
    agent_exe = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
    subprocess.run([str(agent_exe), "--set-password", str(data)], input=password.encode(), check=True, **hidden)
    agent = subprocess.Popen([str(agent_exe), "--data", str(data)], env=dict(os.environ, **homes), **hidden)
    try:
        phone = Viewer()
        for _ in range(40):
            try:
                phone.call("/api/login", {"password": password})
                break
            except OSError:
                time.sleep(0.5)
        for _ in range(80):
            view = phone.call("/api/terminal")
            if view["device"]["workspaces"] and any(s["id"] == session for s in view["sessions"]):
                break
            time.sleep(0.5)
        assert "peek" in view["device"]["features"], view["device"]
        listed = next(s for s in view["sessions"] if s["id"] == session)
        assert (listed["title"], listed["tool"], listed["dir"]) == ("修登录页报错", "claude", "demo"), listed

        # 1. what was said, read by the computer, before anything is done to the program that has it open
        looked = phone.call("/api/conversation", {"id": secrets.token_hex(16), "session": session})
        assert looked["title"] == "修登录页报错" and looked["more"] is False, looked
        assert [(m["role"], m["text"]) for m in looked["messages"]] == [
            ("user", "把登录页的报错修一下"), ("assistant", "我先看看登录的代码。"), ("tool", "Bash：grep -rn login src"),
            ("assistant", "找到了，是密码为空时没有提示。要我现在改吗？")], looked["messages"]
        for wrong in ({"session": str(uuid.uuid4())}, {"session": "not-an-id"}):
            assert "error" in phone.call("/api/conversation", dict(wrong, id=secrets.token_hex(16)), expect=400)
        print("looked at a conversation: %d things said, nothing started on the computer" % len(looked["messages"]))
        assert not phone.call("/api/terminal")["terminals"]

        # 2. the window on the computer signs in with a ticket: once, and without the password
        ticket = phone.call("/api/ticket", {})["ticket"]
        window = Viewer()
        window.call("/api/terminal", expect=401)
        window.call("/api/login", {"ticket": ticket})
        Viewer().call("/api/login", {"ticket": ticket}, expect=401)
        print("a ticket signed the computer's window in, and was of no use a second time")

        # 3. one terminal, two places
        terminal = phone.op(action="start", tool="shell", dir="demo")["terminal"]
        phone.read(terminal, "")                   # any prompt: it differs from one profile to the next
        time.sleep(1.5)
        assert [t["id"] for t in window.call("/api/terminal")["terminals"]] == [terminal]
        phone.op(action="input", terminal=terminal, data="echo FROM_PHONE_$(6*7)\r")
        assert "FROM_PHONE_42" in window.read(terminal, "FROM_PHONE_42")
        window.op(action="input", terminal=terminal, data="echo FROM_WINDOW_$(7*7)\r")
        assert "FROM_WINDOW_49" in phone.read(terminal, "FROM_WINDOW_49")
        for who, cols, rows in ((phone, 46, 30), (window, 100, 40), (phone, 46, 30)):
            who.op(action="resize", terminal=terminal, cols=cols, rows=rows)
            for _ in range(40):
                now = window.call("/api/terminal")["terminals"][0]
                if (now["cols"], now["rows"]) == (cols, rows):
                    break
                time.sleep(0.25)
            assert (now["cols"], now["rows"]) == (cols, rows), now
        print("one terminal used from the phone and from the computer's window: each read what the other typed; the size follows whoever asked last")
        phone.op(action="close", terminal=terminal)
        print("ok")
    finally:
        agent.terminate()
        relay.terminate()
        for process in (agent, relay):
            try:
                process.wait(10)
            except subprocess.TimeoutExpired:
                process.kill()
