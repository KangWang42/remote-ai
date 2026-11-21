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
    tools = root / "tools"
    tools.mkdir()
    (tools / "codex.cmd").write_text(
        f'@if "%1"=="app-server" "{sys.executable}" "{ROOT / "tests/chat_standin.py"}" organize %*\r\n', encoding="mbcs"
    )
    for tool in ("claude", "agy"):
        (tools / (tool + ".cmd")).write_text("@exit /b 0\r\n", encoding="ascii")
    gate = root / "release-model"
    env = dict(os.environ, RCLI_PASSWORD=password, CLAUDE_CONFIG_DIR=str(claude),
               CODEX_HOME=str(root / "codex"), AGY_HOME=str(root / "agy"),
               STANDIN_ORGANIZE_GATE=str(gate), PATH=str(tools) + os.pathsep + os.environ["PATH"])
    subprocess.run([str(agent), "--set-password", str(data)], input=password.encode(), check=True)
    processes = []
    cookie = {}
    port = 0
    target = {"session": sid, "tool": "claude"}

    def call(path, body=None, status=200):
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=30)
        try:
            headers = dict(cookie, **{"Content-Type": "application/json"})
            connection.request("GET" if body is None else "POST", path,
                               json.dumps(body).encode() if body is not None else None, headers)
            reply = connection.getresponse()
            if reply.getheader("Set-Cookie"):
                cookie["Cookie"] = reply.getheader("Set-Cookie").split(";", 1)[0]
            text = reply.read()
            assert reply.status == status, (path, reply.status, text.decode("utf-8", "replace"))
            return json.loads(text)
        finally:
            connection.close()

    def operation(verb, status=200, **fields):
        return call("/api/organize", {"id": secrets.token_hex(16), "verb": verb, **fields}, status)

    def connect(new_port):
        nonlocal port
        port = new_port
        cookie.clear()
        pending = data / "config.tmp"
        pending.write_text(json.dumps({"Server": f"http://127.0.0.1:{port}", "RemoteEnabled": True,
                                       "RemoteDirs": [f"sample={project}"]}), encoding="utf-8")
        pending.replace(data / "config.json")
        for _ in range(50):
            try:
                call("/api/login", {"password": password})
                return
            except OSError:
                time.sleep(0.1)
        raise AssertionError("relay did not start")

    def start_relay(name):
        new_port = free_port()
        processes.append(subprocess.Popen(
            [sys.executable, str(ROOT / "relay/server.py"), "--port", str(new_port), "--data", str(root / name)],
            env=env, stdout=subprocess.DEVNULL))
        return new_port

    def start_agent():
        process = subprocess.Popen([str(agent), "--data", str(data)], env=env)
        processes.append(process)
        return process

    def stop(process):
        if process.poll() is None:
            subprocess.run(["taskkill.exe", "/PID", str(process.pid), "/T", "/F"], check=False,
                           capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
        process.wait(timeout=10)

    def until(read, expected, detail):
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            value = read()
            if expected(value):
                return value
            time.sleep(0.1)
        raise AssertionError(detail)

    def overview(follow_up=None):
        def current():
            view = call("/api/terminal")
            if view["device"]["online"] and "organize" in view["device"].get("features", []):
                return next((item for item in view["sessions"] if item["id"] == sid), None)
        return until(current, lambda item: item and (follow_up is None or
                     (item.get("organize") or {}).get("follow_up") is follow_up), "overview did not report the expected follow-up")

    def selected():
        return operation("list", **target)["items"][0]

    try:
        first_line = start_relay("relay-first")
        connect(first_line)
        process = start_agent()
        original = overview()["title"]
        assert selected()["state"] == "pending"
        added = operation("edit", **target, follow_up=True)["item"]
        assert added["follow_up"] is True and added["title"] == original and added["state"] == "pending"
        assert overview(True)["title"] == original

        stop(process)
        process = start_agent()
        assert overview(True)["title"] == original and selected()["follow_up"] is True
        second_line = start_relay("relay-second")
        connect(second_line)
        assert overview(True)["title"] == original and selected()["follow_up"] is True

        off = operation("status")
        assert off["source"] == "off" and off["source_tool"] == "" and off["calls_today"] == 0, off
        assert "已关闭" in operation("start", status=400, **target)["error"]
        assert not gate.with_suffix(".started").exists()
        chosen = operation("settings", source="tool", source_tool="codex")
        assert chosen["source"] == "tool" and chosen["source_tool"] == "codex" and chosen["model"] == "gpt-6-luna", chosen
        assert operation("count", scope="all")["needed"] == 0
        assert operation("start", **target)["queued"] == 1
        until(gate.with_suffix(".started").exists, bool, "model did not start")
        assert selected()["state"] == "running"
        removed = operation("edit", **target, follow_up=False)["item"]
        assert removed["follow_up"] is False and removed["title"] == original and removed["state"] == "running"
        edited = operation("edit", **target, title="Seed library plan", follow_up=True)["item"]
        assert edited["title"] == "Seed library plan" and edited["follow_up"] is True
        removed = operation("edit", **target, follow_up=False)["item"]
        assert removed["title"] == "Seed library plan" and removed["follow_up"] is False
        gate.touch()
        completed = until(selected, lambda item: item["state"] != "running", "model did not finish")
        assert completed["state"] == "done" and completed["summary"] == "Agree on a seed collection date."
        assert completed["title"] == "Seed library plan" and completed["follow_up"] is False
        assert operation("start", **target)["queued"] == 0
        used = operation("status")
        assert used["calls_today"] == 1 and used["tokens_today"] == 1234, used
        legacy = operation("edit", **target, title="Seed library next steps")["item"]
        assert legacy["title"] == "Seed library next steps" and legacy["follow_up"] is False

        connect(first_line)
        assert overview(False)["organize"]["title"] == "Seed library next steps"
        assert selected()["follow_up"] is False
        stop(process)
        process = start_agent()
        assert overview(False)["title"] == original and selected()["follow_up"] is False
        print("Windows EXE and two local relays passed: off by default, source chosen through the relay, add, remove during a model turn, combined edit, unchanged not sent again, usage counted, legacy title-only edit, restart and relay switch")
    finally:
        for process in reversed(processes):
            stop(process)


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
        subprocess.run([str(exe), temp, *(["live"] if args.live else [])], check=True, timeout=420 if args.live else 125)


if __name__ == "__main__":
    main()
