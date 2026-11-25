"""Unit and live dual-relay integration test for Stage 1 multi-route Windows components."""
import http.client
import json
import os
import secrets
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class RelayClient:
    def __init__(self, port):
        self.port = port
        self.cookie = ""

    def get(self, path):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        try:
            conn.request("GET", path, headers={"Cookie": self.cookie} if self.cookie else {})
            resp = conn.getresponse()
            return resp.status, json.loads(resp.read() or b"{}")
        finally:
            conn.close()

    def post(self, path, payload):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=15)
        try:
            headers = {"Content-Type": "application/json"}
            if self.cookie:
                headers["Cookie"] = self.cookie
            conn.request("POST", path, json.dumps(payload), headers)
            resp = conn.getresponse()
            if resp.getheader("Set-Cookie"):
                self.cookie = resp.getheader("Set-Cookie").split(";", 1)[0]
            return resp.status, json.loads(resp.read() or b"{}")
        finally:
            conn.close()


def wait_for(fn, label, seconds=25):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        val = fn()
        if val:
            return val
        time.sleep(0.25)
    raise AssertionError("timed out waiting for " + label)


def main():
    out = ROOT / ".build" / "multi-route-check"
    out.mkdir(parents=True, exist_ok=True)
    exe = out / "check.exe"
    agent_build.compile_check("MultiRouteCheck", str(exe), ["TunnelSupervisor", ROOT / "tests/MultiRouteCheck.cs"])
    with tempfile.TemporaryDirectory(prefix="remote-cli-multiroute-") as tmp:
        subprocess.run([str(exe), tmp], check=True, timeout=60)

        # Live dual-relay test: one TerminalAgent reporting concurrently to two independent relays.
        p1, p2 = free_port(), free_port()
        pass1, pass2 = "local-" + secrets.token_hex(8), "remote-" + secrets.token_hex(8)
        work = Path(tmp) / "work"
        work.mkdir()
        agent_dir = Path(tmp) / "agent"
        agent_dir.mkdir()
        (agent_dir / "config.json").write_text(json.dumps({
            "Server": f"http://127.0.0.1:{p1}",
            "RemoteEnabled": True,
            "RemoteMaxMode": "full",
            "RemoteDirs": [f"demo={work}"],
        }), encoding="utf-8")

        hidden = {"creationflags": subprocess.CREATE_NO_WINDOW}
        r1 = subprocess.Popen([sys.executable, str(ROOT / "relay/server.py"), "--port", str(p1), "--data", str(Path(tmp) / "r1")],
                              env=dict(os.environ, RCLI_PASSWORD=pass1, PYTHONUTF8="1"), stdout=subprocess.DEVNULL, **hidden)
        r2 = subprocess.Popen([sys.executable, str(ROOT / "relay/server.py"), "--port", str(p2), "--data", str(Path(tmp) / "r2")],
                              env=dict(os.environ, RCLI_PASSWORD=pass2, PYTHONUTF8="1"), stdout=subprocess.DEVNULL, **hidden)
        agent = None
        try:
            c1, c2 = RelayClient(p1), RelayClient(p2)
            wait_for(lambda: c1.post("/api/login", {"password": pass1})[0] == 200, "relay1 ready")
            wait_for(lambda: c2.post("/api/login", {"password": pass2})[0] == 200, "relay2 ready")
            agent = subprocess.Popen([str(exe), str(agent_dir), "--dual-agent",
                                      f"http://127.0.0.1:{p1}", pass1, f"http://127.0.0.1:{p2}", pass2], **hidden)

            # Both relays expose the exact same environment_id via /.well-known/remote-ai and device.routes
            def both_online():
                s1, d1 = c1.get("/.well-known/remote-ai")
                s2, d2 = c2.get("/.well-known/remote-ai")
                return s1 == 200 and s2 == 200 and d1.get("online") and d2.get("online") and d1.get("environment_id") and d1["environment_id"] == d2.get("environment_id")
            wait_for(both_online, "both relays report matching environment_id")

            _, ov1 = c1.get("/api/terminal")
            _, ov2 = c2.get("/api/terminal")
            assert len(ov1["device"]["routes"]) == 2 and ov1["device"]["routes"] == ov2["device"]["routes"], (ov1["device"], ov2["device"])

            # Start shell via relay 2 (remote), verify relay 1 (local) also sees it and receives its output
            st, started = c2.post("/api/terminal", {"id": secrets.token_hex(16), "action": "start", "tool": "shell", "dir": "demo", "history": False})
            assert st == 200, started
            tid = started["terminal"]
            wait_for(lambda: any(t["id"] == tid and t["state"] == "running" for t in c1.get("/api/terminal")[1]["terminals"]), "relay1 sees terminal started on relay2")

            # Send input from relay 1 (local), verify output arrives on BOTH relay 1 and relay 2
            st, _ = c1.post("/api/terminal", {"id": secrets.token_hex(16), "action": "input", "terminal": tid, "data": "echo DUAL_SYNC_OK_42\r"})
            assert st == 200
            wait_for(lambda: "DUAL_SYNC_OK_42" in "".join(c["data"] for c in c1.get(f"/api/terminal?terminal={tid}&after=0")[1].get("chunks", [])), "output on relay1")
            wait_for(lambda: "DUAL_SYNC_OK_42" in "".join(c["data"] for c in c2.get(f"/api/terminal?terminal={tid}&after=0")[1].get("chunks", [])), "output on relay2")

            # Close via relay 2
            c2.post("/api/terminal", {"id": secrets.token_hex(16), "action": "close", "terminal": tid})
            print("MultiRouteCheck: unit + live dual-relay concurrency and discovery verified")
        finally:
            for proc in (agent, r1, r2):
                if proc and proc.poll() is None:
                    subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
                    proc.wait(10)


if __name__ == "__main__":
    main()
