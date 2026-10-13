"""The owner's work in numbers, questions asked at a time, and what is passed on when a conversation stops:
WorkCheck.cs, on made-up records in a temporary folder."""
import os
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
with tempfile.TemporaryDirectory(prefix='remote-ai-work-') as folder:
    exe = Path(folder) / 'check.exe'
    subprocess.run([str(compiler), '/nologo', '/target:exe', '/platform:x64', '/codepage:65001', '/reference:System.Web.Extensions.dll', '/reference:System.Security.dll',
                    '/main:WorkCheck', '/out:' + str(exe), *[str(ROOT / 'agent-windows' / (name + '.cs')) for name in ('Stats', 'Schedules', 'Chains')], str(ROOT / 'tests/WorkCheck.cs')], check=True)
    subprocess.run([str(exe), str(Path(folder) / 'made-up')], check=True, timeout=60)
