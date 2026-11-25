"""Compile and run GitReviewCheck against an isolated temporary Git repository."""
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402


def main():
    out = ROOT / ".build" / "git-review-check"
    out.mkdir(parents=True, exist_ok=True)
    exe = out / "check.exe"
    agent_build.compile_check("GitReviewCheck", str(exe), [ROOT / "tests/GitReviewCheck.cs"])
    with tempfile.TemporaryDirectory(prefix="remote-cli-gitreview-") as tmp:
        subprocess.run([str(exe), tmp], check=True, timeout=60)


if __name__ == "__main__":
    main()
