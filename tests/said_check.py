"""Compile and run the check of what the phone is shown of a saved conversation, with made-up conversation files only.

    python tests/said_check.py
"""
import os
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
with tempfile.TemporaryDirectory(prefix="remote-cli-said-") as folder:
    exe = Path(folder) / "check.exe"
    subprocess.run([str(compiler), "/nologo", "/target:exe", "/platform:x64", "/codepage:65001",
                    "/reference:System.Management.dll", "/reference:System.Net.Http.dll", "/reference:System.Web.Extensions.dll",
                    "/reference:System.Security.dll", "/main:SaidCheck", "/out:" + str(exe),
                    str(ROOT / "agent-windows/CodexSessions.cs"), str(ROOT / "agent-windows/Reach.cs"), str(ROOT / "agent-windows/Marks.cs"), str(ROOT / "agent-windows/RemoteCliAgent.cs"),
                    str(ROOT / "tests/SaidCheck.cs")], check=True)
    subprocess.run([str(exe)], check=True, timeout=60)
