"""Compile and run the check of conversations that the tools' hooks tell of: made-up events are handed to the real
hook program (build the agent first), and the tools' settings are written in a temporary folder only.

    python tests/reach_check.py
"""
import os
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
with tempfile.TemporaryDirectory(prefix="remote-cli-reach-") as folder:
    exe = Path(folder) / "check.exe"
    subprocess.run([str(compiler), "/nologo", "/target:exe", "/platform:x64", "/codepage:65001",
                    "/reference:System.Management.dll", "/reference:System.Web.Extensions.dll", "/main:ReachCheck", "/out:" + str(exe),
                    str(ROOT / "agent-windows/CodexSessions.cs"), str(ROOT / "agent-windows/Reach.cs"), str(ROOT / "agent-windows/Outbox.cs"), str(ROOT / "agent-windows/Running.cs"), str(ROOT / "tests/ReachCheck.cs")], check=True)
    subprocess.run([str(exe), str(ROOT / "agent-windows/bin/RemoteCliAgent.exe")], check=True, timeout=120)
