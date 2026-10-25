"""Builds dist/RemoteCli-Setup-<version>.exe: the two programs, the relay, the app's pages and an official
embeddable Python, packed into one installer. Windows only; uses the C# compiler that ships with Windows.

    python tools/package_windows.py
"""
import hashlib
import os
import shutil
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VERSION = "1.7.3"
PYTHON = ("3.12.10", "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3")   # version, SHA-256 of the embeddable zip
# Parts of the embeddable Python the relay never loads.
UNUSED = ("_msi.pyd", "_sqlite3.pyd", "sqlite3.dll", "_elementtree.pyd", "pyexpat.pyd", "_wmi.pyd", "_zoneinfo.pyd", "winsound.pyd",
          "_decimal.pyd", "_lzma.pyd", "_bz2.pyd", "_asyncio.pyd", "_overlapped.pyd", "_multiprocessing.pyd", "_ctypes.pyd", "libffi-8.dll", "pythonw.exe", "python.cat")


def python_zip():
    name = "python-%s-embed-amd64.zip" % PYTHON[0]
    path = ROOT / ".cache" / name
    if not path.is_file():
        path.parent.mkdir(exist_ok=True)
        urllib.request.urlretrieve("https://www.python.org/ftp/python/%s/%s" % (PYTHON[0], name), path)
    if hashlib.sha256(path.read_bytes()).hexdigest() != PYTHON[1]:
        raise SystemExit("unexpected content: " + str(path))
    return path


def main():
    build, dist = ROOT / ".build" / "windows", ROOT / "dist"
    shutil.rmtree(build, ignore_errors=True)
    stage = build / "stage"
    stage.mkdir(parents=True)
    dist.mkdir(exist_ok=True)
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "agent-windows" / "build.ps1"), "-OutDir", str(stage)], check=True)
    (stage / "relay").mkdir()
    for name in ("server.py", "relay.py", "screen.py", "websocket.py"):
        shutil.copy2(ROOT / "relay" / name, stage / "relay" / name)
    shutil.copytree(ROOT / "web", stage / "web")
    with zipfile.ZipFile(python_zip()) as archive:
        archive.extractall(stage / "python", [n for n in archive.namelist() if n not in UNUSED])
    for name in ("LICENSE", "THIRD_PARTY.md", "README.md"):
        shutil.copy2(ROOT / name, stage / name)
    # the packed relay must start with the packed Python before it is shipped
    check = subprocess.run([str(stage / "python" / "python.exe"), "-c", "import sys; sys.path.insert(0, r'%s'); import server, relay; print(server.VERSION)" % (stage / "relay")],
                           capture_output=True, text=True)
    if check.returncode != 0 or check.stdout.strip() != VERSION:
        raise SystemExit("the packed relay does not start:\n" + check.stdout + check.stderr)
    payload = build / "payload.zip"
    with zipfile.ZipFile(payload, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(stage.rglob("*")):
            if path.is_file() and "__pycache__" not in path.parts:
                archive.write(path, path.relative_to(stage).as_posix())
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe"
    setup = dist / ("RemoteCli-Setup-%s.exe" % VERSION)
    subprocess.run([str(compiler), "/nologo", "/target:winexe", "/platform:x64", "/optimize+", "/codepage:65001", "/out:" + str(setup),
                    "/resource:%s,payload.zip" % payload, "/reference:System.Windows.Forms.dll", "/reference:System.Drawing.dll", "/reference:System.IO.Compression.dll",
                    "/reference:System.IO.Compression.FileSystem.dll", str(ROOT / "agent-windows" / "Setup.cs"), str(ROOT / "agent-windows" / "Controls.cs")], check=True)
    print("%s  %d bytes  sha256 %s" % (setup.name, setup.stat().st_size, hashlib.sha256(setup.read_bytes()).hexdigest()))


if __name__ == "__main__":
    sys.exit(main())
