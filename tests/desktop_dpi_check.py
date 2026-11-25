"""Exercise the desktop console and WebView2 on each connected Windows monitor after packaging."""
import os
import shutil
import subprocess
import tempfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
stage = root / ".build/windows/stage"
src = root / "agent-windows"
compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
manifest = src / "RemoteCli.manifest"
assert b"PerMonitorV2" in (stage / "RemoteCli.exe").read_bytes(), "packaged window lacks its DPI manifest"
with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as folder:
    out = Path(folder)
    for dll in stage.glob("*.dll"):
        shutil.copy2(dll, out)
    shutil.copy2(stage / "RemoteCli.exe.config", out / "probe.exe.config")
    exe = out / "probe.exe"
    subprocess.run([str(compiler), "/nologo", "/platform:x64", "/codepage:65001", "/win32manifest:" + str(manifest),
                    "/resource:" + str(src / "remote-ai.ico") + ",remote-ai.ico",
                    "/reference:System.Windows.Forms.dll", "/reference:System.Drawing.dll",
                    *["/reference:" + str(out / ("Microsoft.Web.WebView2." + name + ".dll")) for name in ("Core", "WinForms")],
                    "/out:" + str(exe), str(src / "DesktopConsole.cs"), str(src / "Controls.cs"),
                    str(root / "tests/DesktopDpiCheck.cs")], check=True)
    subprocess.run([str(exe), str(out / "profile")], check=True, timeout=45)
