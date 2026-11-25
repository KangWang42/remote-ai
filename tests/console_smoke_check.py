"""End-to-end smoke check for the unified Preact/TS console assets, CSP framing headers, and live relay serving."""
import http.client
import json
import os
import re
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONSOLE_DIR = ROOT / "web" / "console"
ALLOWED_EXT = {".html", ".js", ".css", ".png", ".svg", ".woff2", ".bcmap", ".json"}
SAFE_NAME = re.compile(r"^[a-zA-Z0-9_./-]+$")


def check_packaged_assets():
    assert CONSOLE_DIR.is_dir(), f"Missing built console directory: {CONSOLE_DIR}"
    index = CONSOLE_DIR / "index.html"
    assert index.is_file(), "Missing web/console/index.html"
    files = [p for p in CONSOLE_DIR.rglob("*") if p.is_file()]
    assert len(files) >= 3, f"Expected built assets under web/console, found: {files}"
    for f in files:
        rel = f.relative_to(ROOT / "web").as_posix()
        assert SAFE_NAME.match(rel), f"Asset filename violates Android packaging regex: {rel}"
        assert f.suffix.lower() in ALLOWED_EXT, f"Disallowed asset extension for Android APK: {rel}"
    html = index.read_text(encoding="utf-8")
    assert "./assets/" in html, "Built console index.html must use relative ./assets/ paths"
    print(f"OK packaged assets ({len(files)} files under web/console/)")


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def check_live_relay_serving():
    port = free_port()
    with tempfile.TemporaryDirectory(prefix="rcli-console-smoke-") as td:
        env = os.environ.copy()
        env["RCLI_PASSWORD"] = "smoke-pass-1234"
        env["PYTHONUTF8"] = "1"
        proc = subprocess.Popen(
            [sys.executable, str(ROOT / "relay" / "server.py"), "--host", "127.0.0.1", "--port", str(port), "--data", td, "--web", str(ROOT / "web")],
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        try:
            deadline = time.time() + 10
            conn = None
            while time.time() < deadline:
                try:
                    c = http.client.HTTPConnection("127.0.0.1", port, timeout=2)
                    c.request("GET", "/console/")
                    resp = c.getresponse()
                    body = resp.read().decode("utf-8")
                    assert resp.status == 200, f"GET /console/ returned {resp.status}"
                    xfo = resp.getheader("X-Frame-Options")
                    csp = resp.getheader("Content-Security-Policy") or ""
                    assert xfo == "SAMEORIGIN", f"Expected X-Frame-Options SAMEORIGIN, got {xfo!r}"
                    assert "frame-ancestors 'self'" in csp, f"Expected frame-ancestors 'self' in CSP: {csp!r}"
                    assert "<div id=\"app\"></div>" in body, "Unexpected console HTML body"
                    conn = c
                    break
                except OSError:
                    time.sleep(0.15)
            assert conn is not None, "Relay failed to respond in time"

            # Verify login -> rev long-poll unchanged -> terminal iframe allowed
            conn.request("POST", "/api/login", json.dumps({"password": "smoke-pass-1234"}), {"Content-Type": "application/json"})
            r = conn.getresponse()
            login_data = json.loads(r.read().decode("utf-8"))
            assert r.status == 200 and login_data.get("token"), f"Login failed: {r.status} {login_data}"
            auth = {"Authorization": "Bearer " + login_data["token"]}

            conn.request("GET", "/api/terminal", headers=auth)
            r = conn.getresponse()
            ov1 = json.loads(r.read().decode("utf-8"))
            assert r.status == 200 and ov1.get("rev", 0) >= 1, f"Missing rev in overview: {ov1}"

            conn.request("GET", f"/api/terminal?rev={ov1['rev']}&wait=0", headers=auth)
            r = conn.getresponse()
            ov2 = json.loads(r.read().decode("utf-8"))
            assert r.status == 200 and ov2.get("unchanged") is True and ov2.get("rev") == ov1["rev"], f"Expected unchanged=True, got {ov2}"

            conn.request("GET", "/terminal/?embed=1", headers=auth)
            r = conn.getresponse()
            r.read()
            assert r.status == 200 and r.getheader("X-Frame-Options") == "SAMEORIGIN"
            print("OK live relay /console/ + SAMEORIGIN iframe headers + rev incremental sync")
        finally:
            proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()


def main():
    check_packaged_assets()
    check_live_relay_serving()
    print("OK console_smoke_check")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
