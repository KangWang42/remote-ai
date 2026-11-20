"""Compile and run the Git worktree manager check (tests/WorktreesCheck.cs) against a real temporary Git repository.

    python tests/worktrees_check.py
"""
import subprocess
import tempfile
from pathlib import Path

from agent_build import ROOT, compile_check

with tempfile.TemporaryDirectory(prefix="remote-ai-worktrees-") as folder:
    root = Path(folder)
    exe = root / "check.exe"
    compile_check("WorktreesCheck", exe, [ROOT / "tests" / "WorktreesCheck.cs"])
    subprocess.run([str(exe), str(root)], check=True, timeout=60)
