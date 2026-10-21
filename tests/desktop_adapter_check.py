"""Compile isolated desktop adapter checks; never opens the real application."""
import os
import sys
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402
with tempfile.TemporaryDirectory(prefix='remote-ai-adapter-') as folder:
    exe = Path(folder) / 'check.exe'
    agent_build.compile_check('DesktopAdapterCheck', str(exe), [ROOT / 'tests/DesktopAdapterCheck.cs', ROOT / 'tests/CodexDesktopControlCheck.cs'])
    subprocess.run([str(exe), folder], check=True, timeout=30)
