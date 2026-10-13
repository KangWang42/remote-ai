"""Compile and run the check of which conversations count as in use now, and of what the window does with a newer
version it has found.

    python tests/in_use_check.py
"""
import os
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
with tempfile.TemporaryDirectory(prefix="remote-cli-in-use-") as folder:
    exe = Path(folder) / "check.exe"
    subprocess.run([str(compiler), "/nologo", "/target:exe", "/platform:x64", "/codepage:65001",
                    "/reference:System.Management.dll", "/reference:System.Net.Http.dll", "/reference:System.Web.Extensions.dll",
                    "/reference:System.Security.dll", "/main:InUseCheck", "/out:" + str(exe),
                    str(ROOT / "agent-windows/CodexSessions.cs"), str(ROOT / "agent-windows/Reach.cs"), str(ROOT / "agent-windows/Marks.cs"), str(ROOT / "agent-windows/ClaudeRelay.cs"), str(ROOT / "agent-windows/Folders.cs"), str(ROOT / "agent-windows/Removal.cs"), str(ROOT / "agent-windows/CodexQueue.cs"), str(ROOT / "agent-windows/DesktopAdapters.cs"), str(ROOT / "agent-windows/Ways.cs"), str(ROOT / "agent-windows/CodexDesktopControl.cs"), str(ROOT / "agent-windows/RemoteCliAgent.cs"),
                    str(ROOT / "agent-windows/AutoUpdater.cs"), str(ROOT / "tests/InUseCheck.cs")], check=True)
    subprocess.run([str(exe)], check=True, timeout=60)
