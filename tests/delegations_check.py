"""Compile and run the check of the tasks a conversation gives out (tests/DelegationsCheck.cs).

    python tests/delegations_check.py
"""
import subprocess
import tempfile
from pathlib import Path

from agent_build import ROOT, compile_check

with tempfile.TemporaryDirectory(prefix="remote-ai-delegations-") as folder:
    exe = Path(folder) / "check.exe"
    compile_check("DelegationsCheck", exe, [ROOT / "tests" / "DelegationsCheck.cs"])
    subprocess.run([str(exe)], check=True, timeout=60)
