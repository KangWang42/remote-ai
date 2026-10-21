"""What the phone can do to a conversation on the computer, and by which way: the table in WaysCheck.cs."""
import os
import sys
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402
with tempfile.TemporaryDirectory(prefix='remote-ai-ways-') as folder:
    exe = Path(folder) / 'check.exe'
    agent_build.compile_check('WaysCheck', str(exe), [ROOT / 'tests/WaysCheck.cs'])
    subprocess.run([str(exe)], check=True, timeout=30)
