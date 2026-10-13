"""Network selection, local listener diagnostics, optional install dialog and skip. No tool is installed."""
import os
import subprocess
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1]
out = root / '.build' / 'lan-tools-check'
out.mkdir(parents=True, exist_ok=True)
sources = ['LanNetwork', 'ToolInstaller', 'RemoteCliAgent', 'DesktopAdapters', 'Ways', 'Outbox', 'Running', 'Stats', 'Schedules', 'Chains', 'CodexDesktopControl', 'CodexSessions', 'Reach', 'Marks', 'ClaudeRelay', 'Folders', 'Removal', 'CodexQueue']
compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
exe = out / 'check.exe'
subprocess.run([str(compiler), '/nologo', '/codepage:65001', '/target:exe', '/main:LanToolsCheck', '/out:' + str(exe),
                *['/reference:' + name + '.dll' for name in ['System.Net.Http', 'System.Web.Extensions', 'System.Security', 'System.Management', 'System.Drawing', 'System.Windows.Forms']],
                *[str(root / 'agent-windows' / (name + '.cs')) for name in sources], str(root / 'tests/LanToolsCheck.cs')], check=True)
subprocess.run([str(exe), str(out), *sys.argv[1:]], check=True, timeout=210)
