"""Compile isolated desktop adapter checks; never opens the real application."""
import os
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
with tempfile.TemporaryDirectory(prefix='remote-ai-adapter-') as folder:
    exe = Path(folder) / 'check.exe'
    sources = ['CodexSessions', 'Reach', 'Outbox', 'Running', 'Marks', 'ClaudeRelay', 'Folders', 'Removal', 'CodexQueue', 'DesktopAdapters', 'Ways', 'CodexDesktopControl', 'RemoteCliAgent']
    subprocess.run([str(compiler), '/nologo', '/target:exe', '/platform:x64', '/codepage:65001',
                    '/reference:System.Management.dll', '/reference:System.Net.Http.dll', '/reference:System.Web.Extensions.dll',
                    '/reference:System.Web.dll', '/reference:System.Security.dll', '/main:DesktopAdapterCheck', '/out:' + str(exe),
                    *[str(ROOT / 'agent-windows' / (name + '.cs')) for name in sources], str(ROOT / 'tests/DesktopAdapterCheck.cs'), str(ROOT / 'tests/CodexDesktopControlCheck.cs')], check=True)
    subprocess.run([str(exe), folder], check=True, timeout=30)
