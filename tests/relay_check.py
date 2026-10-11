"""What the Windows program says about a relay of one's own, against a relay started here: the right password,
a wrong one, a web server that is no relay, and a closed port.

    python tests/relay_check.py [https://address:port of a relay over https, file that holds its password]
"""
import http.server
import os
import secrets
import socket
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"


def free_port():
    with socket.socket() as reserved:
        reserved.bind(("127.0.0.1", 0))
        return reserved.getsockname()[1]


with tempfile.TemporaryDirectory(prefix="remote-cli-relay-check-") as folder:
    exe = Path(folder) / "RelayCheck.exe"
    subprocess.run([str(compiler), "/nologo", "/target:exe", "/platform:x64", "/codepage:65001",
                    "/reference:System.Management.dll", "/reference:System.Net.Http.dll", "/reference:System.Web.Extensions.dll",
                    "/reference:System.Security.dll", "/main:RelayCheck", "/out:" + str(exe),
                    str(ROOT / "agent-windows/CodexSessions.cs"), str(ROOT / "agent-windows/Reach.cs"), str(ROOT / "agent-windows/Marks.cs"), str(ROOT / "agent-windows/ClaudeRelay.cs"), str(ROOT / "agent-windows/RemoteCliAgent.cs"), str(ROOT / "tests/RelayCheck.cs")], check=True)
    port, other, closed, password = free_port(), free_port(), free_port(), "test-" + secrets.token_hex(12)
    relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(port), "--data", str(Path(folder) / "relay")],
                             env=dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1"), stdout=subprocess.DEVNULL)
    plain = http.server.ThreadingHTTPServer(("127.0.0.1", other), http.server.SimpleHTTPRequestHandler)      # a web server that is no relay
    threading.Thread(target=plain.serve_forever, daemon=True).start()
    try:
        for _ in range(40):
            try:
                socket.create_connection(("127.0.0.1", port), 1).close()
                break
            except OSError:
                time.sleep(0.25)
        arguments = [str(exe), "http://127.0.0.1:%d" % port, password, "http://127.0.0.1:%d" % other, "http://127.0.0.1:%d" % closed]
        if len(sys.argv) > 2:
            arguments += [sys.argv[1], Path(sys.argv[2]).read_text(encoding="utf-8").strip()]
        subprocess.run(arguments, check=True, timeout=120, cwd=folder)
    finally:
        relay.kill()
        plain.shutdown()
