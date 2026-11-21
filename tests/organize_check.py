"""Run the Windows organizer check, optionally using a real Codex turn on synthetic text."""
import argparse
import http.client
import json
import os
import secrets
import subprocess
import sys
import time
from pathlib import Path
from tempfile import TemporaryDirectory

from agent_build import ROOT, compile_check
from teams_e2e_check import free_port


def check_agent(agent, root):
    port = free_port()
    password = "organize-test-" + secrets.token_hex(8)
    root = Path(root)
    data, project = root / "agent", root / "project"
    data.mkdir()
    project.mkdir()
    claude = root / "claude"
    folder = claude / "projects" / "".join(c if c.isascii() and c.isalnum() else "-" for c in str(project))
    folder.mkdir(parents=True)
    sid = "11111111-2222-3333-4444-555555555555"
    (folder / (sid + ".jsonl")).write_text(
        '{"type":"user","message":{"role":"user","content":"Plan a seed library."}}\n', encoding="utf-8"
    )
    env = dict(os.environ, RCLI_PASSWORD=password, CLAUDE_CONFIG_DIR=str(claude),
               CODEX_HOME=str(root / "codex"), AGY_HOME=str(root / "agy"))
    (data / "config.json").write_text(json.dumps({"Server": f"http://127.0.0.1:{port}", "RemoteEnabled": True,
                                                 "RemoteDirs": [f"sample={project}"]}), encoding="utf-8")
    subprocess.run([str(agent), "--set-password", str(data)], input=password.encode(), check=True)
    processes = []
    cookie = {}

    def call(path, body=None):
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=30)
        try:
            headers = dict(cookie, **{"Content-Type": "application/json"})
            connection.request("GET" if body is None else "POST", path,
                               json.dumps(body).encode() if body is not None else None, headers)
            reply = connection.getresponse()
            if reply.getheader("Set-Cookie"):
                cookie["Cookie"] = reply.getheader("Set-Cookie").split(";", 1)[0]
            text = reply.read()
            assert reply.status == 200, (path, reply.status, text.decode("utf-8", "replace"))
            return json.loads(text)
        finally:
            connection.close()

    def operation(verb, **fields):
        return call("/api/organize", {"id": secrets.token_hex(16), "verb": verb, **fields})

    try:
        processes.append(subprocess.Popen(
            [sys.executable, str(ROOT / "relay/server.py"), "--port", str(port), "--data", str(root / "relay")],
            env=env, stdout=subprocess.DEVNULL))
        for _ in range(50):
            try:
                call("/api/login", {"password": password})
                break
            except OSError:
                time.sleep(0.1)
        else:
            raise AssertionError("relay did not start")
        processes.append(subprocess.Popen([str(agent), "--data", str(data)], env=env))
        for _ in range(80):
            view = call("/api/terminal")
            if "organize" in view["device"].get("features", []) and view["sessions"]:
                break
            time.sleep(0.25)
        else:
            raise AssertionError("agent did not advertise organize and report its synthetic session")
        original = view["sessions"][0]["title"]
        item = operation("list", session=sid, tool="claude")["items"][0]
        assert item["id"] == sid and item["state"] == "pending"
        edited = operation("edit", session=sid, tool="claude", title="Seed library")["item"]
        assert edited["manual_title"] == "Seed library" and edited["state"] == "pending"
        listed = next(s for s in call("/api/terminal")["sessions"] if s["id"] == sid)
        assert listed["title"] == original and listed["organize"]["title"] == "Seed library"
        print("Windows EXE and local relay passed: organize capability, session_organize routing, edit, and compatible overview")
    finally:
        for process in reversed(processes):
            if process.poll() is None:
                subprocess.run(["taskkill.exe", "/PID", str(process.pid), "/T", "/F"], check=True,
                               stdout=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
            process.wait(timeout=10)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true")
    parser.add_argument("--agent-dir", type=Path, help="Check the built Windows agent through a local relay")
    args = parser.parse_args()
    if args.agent_dir:
        with TemporaryDirectory(prefix="remote-ai-organize-relay-") as temp:
            check_agent((args.agent_dir / "RemoteCliAgent.exe").resolve(), temp)
        return
    with TemporaryDirectory(prefix="remote-ai-organize-") as temp:
        exe = Path(temp) / "OrganizeCheck.exe"
        compile_check("OrganizeCheck", exe, [ROOT / "tests/OrganizeCheck.cs"])
        subprocess.run([str(exe), temp, *(["live"] if args.live else [])], check=True, timeout=125)


if __name__ == "__main__":
    main()
