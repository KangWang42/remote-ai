"""Verifies Windows Handovers.cs persistence, cycle rejection, title preservation, cap and StripAsk."""
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))
from agent_build import compile_check  # noqa: E402


def main():
    with tempfile.TemporaryDirectory(prefix="handovers-") as tmp:
        exe = Path(tmp) / "HandoversCheck.exe"
        compile_check("HandoversCheck", exe, extra=[ROOT / "tests" / "HandoversCheck.cs"])
        subprocess.run([str(exe), tmp], check=True)


if __name__ == "__main__":
    main()
