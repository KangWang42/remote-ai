"""Compile and run the check of what the Windows agent keeps of a terminal's output and gives again to a relay
that has less of it than it was given.

    python tests/history_check.py
"""
import os
import sys
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402
with tempfile.TemporaryDirectory(prefix="remote-cli-history-") as folder:
    exe = Path(folder) / "check.exe"
    agent_build.compile_check('HistoryCheck', str(exe), [ROOT / 'tests/HistoryCheck.cs'])
    subprocess.run([str(exe)], check=True, timeout=60)
