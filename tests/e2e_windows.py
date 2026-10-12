"""End to end on this Windows computer: relay + agent + a PowerShell terminal, driven over HTTP like the phone does.

    python tests/e2e_windows.py            (build the agent first: agent-windows/build.ps1)

Uses a temporary data folder and port 8733; nothing of an installed copy is touched.
"""
import http.client
import json
import os
import secrets
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
AGENT = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
PORT = int(os.environ.get("RCLI_TEST_PORT", "8733"))
password = "test-" + secrets.token_hex(8)
new_id = lambda: secrets.token_hex(16)


def main():
    temp = tempfile.TemporaryDirectory()
    data, work = Path(temp.name) / "agent", Path(temp.name) / "work"
    data.mkdir(); work.mkdir()
    (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % PORT, "RemoteEnabled": True, "RemoteMaxMode": "full",
                                                  "RemoteDirs": ["demo=" + str(work)]}), encoding="utf-8")
    env = dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1")
    relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(PORT), "--data", str(Path(temp.name) / "relay")],
                             env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    agent = None
    report = {}
    try:
        subprocess.run([str(AGENT), "--set-password", str(data)], input=password.encode(), check=True)
        agent = subprocess.Popen([str(AGENT), "--data", str(data)])
        time.sleep(1)
        cookie = {}

        def call(path, payload=None, expect=200):
            connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=30)
            body = None if payload is None else json.dumps(payload).encode()
            connection.request("POST" if body is not None else "GET", path, body=body, headers=dict(cookie, **({"Content-Type": "application/json"} if body is not None else {})))
            reply = connection.getresponse()
            text = reply.read()
            if reply.getheader("Set-Cookie"):
                cookie["Cookie"] = reply.getheader("Set-Cookie").split(";")[0]
            assert reply.status == expect, (path, reply.status, text[:200])
            connection.close()
            return json.loads(text) if reply.getheader("Content-Type", "").startswith("application/json") else text

        # the door: nothing without the password, pages are served, a wrong password is refused
        call("/api/terminal", expect=401)
        call("/api/terminal", {"action": "start"}, expect=401)
        assert b"Remote AI" in call("/") and b"xterm" in call("/terminal/") and b"ProjectTerminal" in call("/terminal/bridge.js")
        call("/../relay/server.py", expect=404)
        call("/api/login", {"password": "wrong"}, expect=401)
        assert call("/api/session")["signed_in"] is False
        call("/api/login", {"password": password})
        assert call("/api/session")["signed_in"] is True

        deadline = time.time() + 30
        while time.time() < deadline:
            device = call("/api/terminal")["device"]
            if device["online"] and device["workspaces"]:
                break
            time.sleep(0.5)
        report["device"] = {k: device[k] for k in ("online", "enabled", "workspaces", "tools")}
        assert device["online"] and device["enabled"] and device["workspaces"] == ["demo"] and "shell" in device["tools"], device

        op = {"action": "start", "id": new_id(), "tool": "shell", "dir": "demo"}
        terminal = call("/api/terminal", op)["terminal"]
        after, text, state = 0, "", ""
        deadline = time.time() + 40
        sent = False
        while time.time() < deadline and "RCLI_E2E_OK" not in text:
            item = call("/api/terminal?terminal=%s&after=%d&wait=5" % (terminal, after))
            after, state = item["after"], item["terminal"]["state"]
            text += "".join(c["data"] for c in item["chunks"])
            if state == "running" and not sent and text:      # the prompt differs from one profile to the next
                time.sleep(1.5)
                call("/api/terminal", {"action": "input", "id": new_id(), "terminal": terminal, "data": "'RCLI_'+'E2E_OK'; (Get-Location).Path\r"})
                sent = True
        assert "RCLI_E2E_OK" in text, text[-400:]
        report["echo"] = True

        # the stream: a line at once, then output as it is produced
        connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=30)
        connection.request("GET", "/api/terminal/stream?terminal=%s&after=%d" % (terminal, after), headers=cookie)
        reply = connection.getresponse()
        assert reply.status == 200 and reply.getheader("Content-Type").startswith("text/event-stream")
        first = json.loads(reply.readline()[5:])
        assert first["terminal"]["id"] == terminal
        began = time.time()
        call("/api/terminal", {"action": "input", "id": new_id(), "terminal": terminal, "data": "'STREAM_'+'OK'\r"})
        got = ""
        while time.time() - began < 15 and "STREAM_OK" not in got:
            line = reply.readline()
            if line.startswith(b"data:"):
                got += "".join(c["data"] for c in json.loads(line[5:])["chunks"])
        connection.close()
        assert "STREAM_OK" in got, got[-300:]
        report["stream_echo_ms"] = round((time.time() - began) * 1000)

        call("/api/terminal", {"action": "close", "id": new_id(), "terminal": terminal})
        deadline = time.time() + 20
        while time.time() < deadline and state != "closed":
            state = call("/api/terminal?terminal=%s&after=%d&wait=3" % (terminal, after))["terminal"]["state"]
        assert state == "closed"
        call("/api/logout", {})
        call("/api/terminal", expect=401)
        report["closed"] = True
        print(json.dumps(report, ensure_ascii=False))
    finally:
        if agent:
            agent.kill()
        relay.kill()
        time.sleep(0.5)
        try:
            temp.cleanup()
        except OSError:
            pass


if __name__ == "__main__":
    main()
