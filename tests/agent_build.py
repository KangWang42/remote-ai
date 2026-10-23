"""Builds a check program together with the Windows agent's sources, with the C# compiler that ships with Windows.
The list of the agent's sources is kept here once (agent-windows/build.ps1 keeps the one for the program itself)."""
import os
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
COMPILER = Path(os.environ.get("WINDIR", "C:/Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
AGENT = ["RemoteCliAgent", "Chats", "ClaudePermissions", "CodexSessions", "Reach", "Outbox", "Running", "Stats", "Schedules", "Chains", "Marks",
         "ClaudeRelay", "Folders", "FileTransfers", "Removal", "CodexQueue", "DesktopAdapters", "Ways", "CodexDesktopControl"]
REFERENCES = ["System.Management", "System.Net.Http", "System.Web.Extensions", "System.Web", "System.Security"]


def compile_check(main, exe, extra=(), references=()):
    """Compiles the agent's sources and `extra` (paths, or names of files in agent-windows/) into `exe`, entering at `main`."""
    sources = [str(ROOT / "agent-windows" / (name + ".cs")) for name in AGENT]
    for one in extra:
        sources.append(str(one) if ("/" in str(one) or "\\" in str(one)) else str(ROOT / "agent-windows" / (one + ".cs")))
    subprocess.run([str(COMPILER), "/nologo", "/target:exe", "/platform:x64", "/codepage:65001",
                    *["/reference:" + name + ".dll" for name in REFERENCES + list(references)],
                    "/main:" + main, "/out:" + str(exe), *sources], check=True)
