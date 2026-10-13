"""Exercise the production download checks with local valid and corrupted files.

python tests/tunnel_download_check.py --bash <bash.exe>
Windows also compiles and runs FetchTunnel against a local HTTP server.
No installed tunnel or user settings are touched.
"""
import argparse
import hashlib
import http.server
import os
from pathlib import Path
import subprocess
import tempfile
import threading

ROOT = Path(__file__).resolve().parents[1]


def run(args, **kwargs):
    return subprocess.run([str(arg) for arg in args], capture_output=True, **kwargs)


def linux_checks(folder, bash):
    source = (ROOT / "agent-linux/install.sh").read_text(encoding="utf-8")
    method = source.split("tunnel_program() {", 1)[1].split("\ninstall() {", 1)[0]
    hashes = {}
    for kind in ("amd64", "arm64", "arm", "386"):
        data = ("#!/usr/bin/env bash\n# " + kind + "\nexit 0\n").encode()
        (folder / kind).write_bytes(data)
        hashes[kind] = hashlib.sha256(data).hexdigest()
    script = folder / "linux.sh"
    script.write_text(
        "set -euo pipefail\n"
        "share=$PWD/work\ntunnel_url=fixture-\n"
        "command() { if [ \"$*\" = '-v cloudflared' ]; then return 1; fi; builtin command \"$@\"; }\n"
        "uname() { printf '%s' \"$ARCH\"; }\n"
        "say() { :; }\nfail() { printf '%s\\n' \"$*\" >&2; exit 1; }\n"
        "download() { [ \"$1\" = \"fixture-$KIND\" ]; cp \"$KIND\" \"$2\"; "
        "if [ \"$BAD\" = 1 ]; then printf X | dd of=\"$2\" bs=1 seek=5 conv=notrunc 2>/dev/null; fi; }\n"
        + "".join('cf_sha256_%s="%s"\n' % item for item in hashes.items())
        + "tunnel_program() {" + method + "\ntunnel_program\n", encoding="utf-8", newline="\n")
    for arch, kind in (("x86_64", "amd64"), ("aarch64", "arm64"), ("armv7l", "arm"), ("i686", "386")):
        for bad in (False, True):
            target = folder / "work/bin/cloudflared"
            if target.exists():
                target.unlink()
            result = run([bash, "linux.sh"], cwd=folder,
                         env=dict(os.environ, ARCH=arch, KIND=kind, BAD=str(int(bad))))
            assert (result.returncode == 0) == (not bad), result.stderr.decode("utf-8", errors="replace")
            assert target.exists() == (not bad)
            assert not target.with_name("cloudflared.part").exists()
            if not bad:
                assert target.read_bytes() == (folder / kind).read_bytes()
    print("Linux: all four architectures accept valid downloads and reject same-size corruption; partial files removed")


def windows_checks(folder):
    source = (ROOT / "agent-windows/RemoteCliApp.cs").read_text(encoding="utf-8")
    method = "async Task FetchTunnel" + source.split("async Task FetchTunnel", 1)[1].split("// ---------- window", 1)[0]
    payload = b"valid tunnel fixture\n"
    class Handler(http.server.BaseHTTPRequestHandler):
        data = payload

        def do_GET(self):
            self.send_response(200)
            self.send_header("Content-Length", str(len(self.data)))
            self.end_headers()
            self.wfile.write(self.data)

        def log_message(self, *args):
            pass

    server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        harness = folder / "Download.cs"
        harness.write_text(
            "using System; using System.IO; using System.Net; using System.Threading.Tasks;\n"
            "class Download {\n"
            'const string TunnelDownload = "http://127.0.0.1:%d/fixture";\n' % server.server_port
            + 'const long TunnelBytes = %d;\n' % len(payload)
            + 'const string TunnelSha256 = "%s";\n' % hashlib.sha256(payload).hexdigest()
            + 'string TunnelFile = "tunnel.exe";\n' + method
            + 'static int Main() { try { new Download().FetchTunnel(p => {}).GetAwaiter().GetResult(); return 0; } '
              'catch (IOException e) { Console.WriteLine(e.Message); return 2; } } }', encoding="utf-8")
        exe = folder / "Download.exe"
        compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
        compiled = run([compiler, "/nologo", "/codepage:65001", "/out:" + str(exe), harness])
        assert compiled.returncode == 0, compiled.stdout
        for data in (payload, b"X" + payload[1:], payload[:-1]):
            target = folder / "tunnel.exe"
            if target.exists():
                target.unlink()
            Handler.data = data
            result = run([exe], cwd=folder)
            valid = data == payload
            assert result.returncode == (0 if valid else 2), result.stdout
            assert target.exists() == valid
            assert not target.with_name("tunnel.exe.part").exists()
            if valid:
                assert target.read_bytes() == payload
        print("Windows: valid download accepted, same-size corruption and truncation rejected; partial files removed")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bash", default="bash")
    args = parser.parse_args()
    (ROOT / ".build").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="tunnel-integrity-", dir=ROOT / ".build") as work:
        folder = Path(work)
        linux_checks(folder, args.bash)
        if os.name == "nt":
            windows_checks(folder)
