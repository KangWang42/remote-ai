"""Compile and run the check of what the phone is shown of a saved conversation, with made-up conversation files only.

    python tests/said_check.py
"""
import os
import sys
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402
with tempfile.TemporaryDirectory(prefix="remote-cli-said-") as folder:
    exe = Path(folder) / "check.exe"
    agent_build.compile_check('SaidCheck', str(exe), [ROOT / 'tests/SaidCheck.cs'])
    subprocess.run([str(exe)], check=True, timeout=60)
