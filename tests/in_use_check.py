"""Compile and run the check of which conversations count as in use now, and of what the window does with a newer
version it has found.

    python tests/in_use_check.py
"""
import os
import sys
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402
with tempfile.TemporaryDirectory(prefix="remote-cli-in-use-") as folder:
    exe = Path(folder) / "check.exe"
    agent_build.compile_check('InUseCheck', str(exe), ['AutoUpdater', ROOT / 'tests/InUseCheck.cs'])
    subprocess.run([str(exe)], check=True, timeout=60)
