"""Compile and run the check that a conversation is listed once, with made-up conversation files only.

    python tests/session_copies_check.py
"""
import os
import sys
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402
with tempfile.TemporaryDirectory(prefix="remote-cli-copies-") as folder:
    exe = Path(folder) / "check.exe"
    agent_build.compile_check('SessionCopiesCheck', str(exe), [ROOT / 'tests/SessionCopiesCheck.cs'])
    subprocess.run([str(exe)], check=True, timeout=60)
