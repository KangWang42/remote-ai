"""A terminal comes back after the program on the computer restarted for an update: the relay and the agent are
both ended and started again, the agent with the note the old one leaves, and the phone's terminal goes on under
the same number. Isolated relay and agent; build the agent first.

    python tests/restart_check.py
"""
import datetime
import json
import os
import secrets
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from made_up import Homes
from tunnel_check import Client, await_ready, operation

ROOT = Path(__file__).resolve().parents[1]
SAVED_CHAT = "0199eeee-ffff-7000-8111-000000000099"


def check_chat_restart(agent_exe, hidden):
    """A chat conversation returned after an update keeps its previous messages (and heals a wiped relay buffer)."""
    with tempfile.TemporaryDirectory(prefix="rcli-restart-chat-", ignore_cleanup_errors=True) as folder:
        root = Path(folder)
        data, work, store = root / "agent", root / "work", root / "relay"
        for made in (data, work, store):
            made.mkdir()
        homes = Homes(folder)
        homes.claude_saved(work, SAVED_CHAT, "修复结算页的金额显示", 1,
                           said=("结算页的金额有时多出一位小数", "问题在 formatPrice，已经改好。"))
        tools = homes.stand_ins(folder)
        (tools / "claude.cmd").write_text(
            '@if "%%1"=="--help" (echo stand-in) else ("%s" "%s" claude %%*)\r\n'
            % (sys.executable, ROOT / "tests" / "chat_standin.py"), encoding="mbcs")
        with socket.socket() as reserved:
            reserved.bind(("127.0.0.1", 0))
            port = reserved.getsockname()[1]
        origin, password = "http://127.0.0.1:%d" % port, "test-" + secrets.token_hex(16)
        env = homes.environment(RCLI_PASSWORD=password)
        env["PATH"] = str(tools) + ";" + env["PATH"]
        (data / "config.json").write_text(json.dumps({
            "Server": origin, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["demo=" + str(work)],
        }), encoding="utf-8")
        subprocess.run([str(agent_exe), "--set-password", str(data)], input=password.encode(), check=True, timeout=10, **hidden)
        start_relay = lambda: subprocess.Popen(
            [sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(port), "--data", str(store)],
            env=env, stdout=subprocess.DEVNULL, **hidden)
        start_agent = lambda: subprocess.Popen([str(agent_exe), "--data", str(data)], env=env, **hidden)
        relay, agent = start_relay(), None
        try:
            client = Client(origin, timeout=10)
            await_ready(lambda: client.call("/api/login", {"password": password}), "chat relay")
            agent = start_agent()
            await_ready(lambda: client.call("/api/terminal")["device"]["workspaces"], "chat agent")
            terminal = client.call("/api/terminal", operation(
                "", "start", tool="claude", dir="demo", form="chat", session=SAVED_CHAT))["terminal"]
            look = lambda after=0: client.call("/api/terminal?terminal=%s&after=%d" % (terminal, after))
            await_ready(lambda: "formatPrice" in "".join(c["data"] for c in look()["chunks"]), "initial recalled chat history")
            time.sleep(2.5)
            before = look()
            kept = json.loads((store / "terminals.json").read_text(encoding="utf-8"))["threads"][terminal]
            subprocess.run(["taskkill", "/PID", str(agent.pid), "/T", "/F"], capture_output=True); agent.wait(10)
            relay.kill(); relay.wait(10)

            # Simulate an update from a build whose relay buffer was already wiped down to only an update note.
            out_file = store / "terminals-output" / (terminal + ".jsonl")
            if out_file.exists():
                out_file.write_text(json.dumps({"seq": before["terminal"]["seq"], "data": json.dumps({"t": "note", "text": "旧版更新记录"}) + "\n"}) + "\n", encoding="utf-8")
            (data / "restart.json").write_text(json.dumps({
                "instance": kept["instance"],
                "at": datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
                "terminals": [{
                    "id": terminal, "tool": "claude", "dir": "demo", "folder": "",
                    "session": SAVED_CHAT, "seq": before["terminal"]["seq"],
                    "cols": 80, "rows": 24, "form": "chat", "model": "", "effort": "",
                }],
            }), encoding="utf-8")
            relay = start_relay()
            await_ready(lambda: client.call("/api/session")["signed_in"], "chat relay again")
            agent = start_agent()
            await_ready(lambda: client.call("/api/terminal")["device"]["online"], "chat agent again")
            await_ready(lambda: "Remote AI 已更新，对话已接回" in "".join(c["data"] for c in look(0)["chunks"]), "returned chat update note")

            full_text = "".join(c["data"] for c in look(0)["chunks"])
            events = [json.loads(line) for line in full_text.splitlines() if line.strip()]
            histories = [e for e in events if e.get("t") == "history"]
            notes = [e for e in events if e.get("t") == "note" and "Remote AI 已更新，对话已接回" in e.get("text", "")]
            assert len(histories) == 1, ("expected single recalled history event without duplicates", events)
            assert len(notes) == 1, ("expected single update note", events)
            assert events.index(histories[0]) < events.index(notes[0]), ("history must appear before update note", events)
            history_texts = [m.get("text", "") for m in histories[0].get("messages", [])]
            assert any("结算页的金额有时多出一位小数" in t for t in history_texts), history_texts
            assert any("问题在 formatPrice，已经改好。" in t for t in history_texts), history_texts
            assert "旧版更新记录" not in full_text, "wiped stale buffer should be cleanly replaced by recalled history"
            client.call("/api/terminal", operation(terminal, "close"))
        finally:
            for process in (agent, relay):
                if process and process.poll() is None:
                    subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True)
                    process.wait(10)
            homes.close()


def main():
    hidden = {"creationflags": subprocess.CREATE_NO_WINDOW}
    agent_exe = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
    with tempfile.TemporaryDirectory(prefix="rcli-restart-", ignore_cleanup_errors=True) as folder:
        root = Path(folder)
        data, work, store = root / "agent", root / "work", root / "relay"
        for made in (data, work, store):
            made.mkdir()
        with socket.socket() as reserved:
            reserved.bind(("127.0.0.1", 0))
            port = reserved.getsockname()[1]
        origin, password = "http://127.0.0.1:%d" % port, "test-" + secrets.token_hex(16)
        (data / "config.json").write_text(json.dumps({"Server": origin, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["demo=" + str(work)]}), encoding="utf-8")
        subprocess.run([str(agent_exe), "--set-password", str(data)], input=password.encode(), check=True, timeout=10, **hidden)
        start_relay = lambda: subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(port), "--data", str(store)],
                                               env=dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1"), stdout=subprocess.DEVNULL, **hidden)
        start_agent = lambda: subprocess.Popen([str(agent_exe), "--data", str(data)], **hidden)
        relay, agent = start_relay(), None
        try:
            client = Client(origin, timeout=10)
            await_ready(lambda: client.call("/api/login", {"password": password}), "relay")
            agent = start_agent()
            await_ready(lambda: client.call("/api/terminal")["device"]["workspaces"], "agent")
            terminal = client.call("/api/terminal", operation("", "start", tool="shell", dir="demo"))["terminal"]
            look = lambda after=0: client.call("/api/terminal?terminal=%s&after=%d" % (terminal, after))
            await_ready(lambda: look()["terminal"]["state"] == "running", "terminal")
            time.sleep(3)
            client.call("/api/terminal", operation(terminal, "resize", cols=100, rows=30))
            client.call("/api/terminal", operation(terminal, "input", data="'BEFORE_' + 'RESTART'\r"))
            await_ready(lambda: "BEFORE_RESTART" in "".join(c["data"] for c in look()["chunks"]), "first output")
            time.sleep(2.5)         # the relay has written the output down
            before = look()
            kept = json.loads((store / "terminals.json").read_text(encoding="utf-8"))["threads"][terminal]
            # What the program does when it restarts for an update: note the terminals, end, start again.
            subprocess.run(["taskkill", "/PID", str(agent.pid), "/T", "/F"], capture_output=True); agent.wait(10)
            relay.kill(); relay.wait(10)
            (data / "restart.json").write_text(json.dumps({"instance": kept["instance"], "at": datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
                                                           "terminals": [{"id": terminal, "tool": "shell", "dir": "demo", "session": "", "seq": before["terminal"]["seq"] + 3, "cols": 100, "rows": 30}]}), encoding="utf-8")
            relay = start_relay()
            await_ready(lambda: client.call("/api/session")["signed_in"], "relay again")       # the sign-in is still good
            agent = start_agent()
            await_ready(lambda: client.call("/api/terminal")["device"]["online"], "agent again")
            after = before["after"]
            await_ready(lambda: "Remote AI 已更新" in "".join(c["data"] for c in look(after)["chunks"]), "the returned terminal's first words")
            assert not (data / "restart.json").exists(), "the note is used once"
            time.sleep(3)
            client.call("/api/terminal", operation(terminal, "input", data="'AFTER_' + 'RESTART'\r"))
            await_ready(lambda: "AFTER_RESTART" in "".join(c["data"] for c in look(after)["chunks"]), "output after the restart")
            now = look(after)
            full_after_restart = "".join(c["data"] for c in look(0)["chunks"])
            assert "BEFORE_RESTART" in full_after_restart and "AFTER_RESTART" in full_after_restart, "pre-update terminal output must stay intact after restart"
            listed = client.call("/api/terminal")["terminals"]
            assert now["terminal"]["state"] == "running" and not now["reset"] and now["terminal"]["seq"] > before["terminal"]["seq"], now["terminal"]
            assert (now["terminal"]["cols"], now["terminal"]["rows"]) == (100, 30), now["terminal"]
            assert [t["id"] for t in listed if t["state"] == "running"] == [terminal], listed
            client.call("/api/terminal", operation(terminal, "close"))
            check_chat_restart(agent_exe, hidden)
            print(json.dumps({"same_terminal": True, "size_kept": True, "history_kept": True, "chat_recalled": True, "pieces_before": before["terminal"]["seq"], "pieces_after": now["terminal"]["seq"]}))
        finally:
            for process in (agent, relay):
                if process and process.poll() is None:
                    subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True)
                    process.wait(10)


if __name__ == "__main__":
    main()
