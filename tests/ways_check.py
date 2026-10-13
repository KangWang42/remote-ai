"""What the phone can do to a conversation on the computer, and by which way: the table in WaysCheck.cs."""
import os
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
with tempfile.TemporaryDirectory(prefix='remote-ai-ways-') as folder:
    exe = Path(folder) / 'check.exe'
    sources = ['CodexSessions', 'Reach', 'Marks', 'ClaudeRelay', 'Folders', 'Removal', 'CodexQueue', 'DesktopAdapters', 'Ways', 'CodexDesktopControl', 'RemoteCliAgent']
    subprocess.run([str(compiler), '/nologo', '/target:exe', '/platform:x64', '/codepage:65001',
                    '/reference:System.Management.dll', '/reference:System.Net.Http.dll', '/reference:System.Web.Extensions.dll',
                    '/reference:System.Web.dll', '/reference:System.Security.dll', '/main:WaysCheck', '/out:' + str(exe),
                    *[str(ROOT / 'agent-windows' / (name + '.cs')) for name in sources], str(ROOT / 'tests/WaysCheck.cs')], check=True)
    subprocess.run([str(exe)], check=True, timeout=30)
