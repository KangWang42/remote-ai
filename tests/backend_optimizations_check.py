"""Compile and run BackendOptimizationsCheck covering ProcessText, WorkspaceSnapshot,
GitReview porcelain v2, Worktrees.ChangesIn, Chats.Shown, Teams snapshots/folding,
Peers agy timeout/byte ceiling/Content-Length framing, and Reach unclosed-stdin handling.

    python tests/backend_optimizations_check.py
"""
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402


def main():
    with tempfile.TemporaryDirectory(prefix="remote-ai-backend-opt-") as tmp:
        exe = Path(tmp) / "check.exe"
        agent_build.compile_check("BackendOptimizationsCheck", str(exe), [ROOT / "tests/BackendOptimizationsCheck.cs"])
        subprocess.run([str(exe), tmp], check=True, timeout=60)


if __name__ == "__main__":
    main()
