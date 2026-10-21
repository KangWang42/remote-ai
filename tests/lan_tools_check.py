"""Network selection, local listener diagnostics, optional install dialog and skip. No tool is installed."""
import os
import subprocess
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402
out = root / '.build' / 'lan-tools-check'
out.mkdir(parents=True, exist_ok=True)
exe = out / 'check.exe'
agent_build.compile_check('LanToolsCheck', str(exe), ['LanNetwork', 'ToolInstaller', root / 'tests/LanToolsCheck.cs'], ['System.Drawing', 'System.Windows.Forms'])
subprocess.run([str(exe), str(out), *sys.argv[1:]], check=True, timeout=210)
