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
from tunnel_check import Client, await_ready, operation

ROOT = Path(__file__).resolve().parents[1]


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
            listed = client.call("/api/terminal")["terminals"]
            assert now["terminal"]["state"] == "running" and not now["reset"] and now["terminal"]["seq"] > before["terminal"]["seq"], now["terminal"]
            assert (now["terminal"]["cols"], now["terminal"]["rows"]) == (100, 30), now["terminal"]
            assert [t["id"] for t in listed if t["state"] == "running"] == [terminal], listed
            client.call("/api/terminal", operation(terminal, "close"))
            print(json.dumps({"same_terminal": True, "size_kept": True, "pieces_before": before["terminal"]["seq"], "pieces_after": now["terminal"]["seq"]}))
        finally:
            for process in (agent, relay):
                if process and process.poll() is None:
                    subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True)
                    process.wait(10)


if __name__ == "__main__":
    main()
