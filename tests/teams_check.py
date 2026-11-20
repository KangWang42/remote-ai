"""Compile and run the check of work several AIs do together (agent-windows/Teams.cs), its members played.

    python tests/teams_check.py
"""
import subprocess
import tempfile
from pathlib import Path

from agent_build import ROOT, compile_check

with tempfile.TemporaryDirectory(prefix="remote-ai-teams-") as folder:
    exe = Path(folder) / "check.exe"
    compile_check("TeamsCheck", exe, [ROOT / "tests" / "TeamsCheck.cs"])
    subprocess.run([str(exe)], check=True, timeout=120)
