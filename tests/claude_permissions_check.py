"""Check Windows permission selection and chat replies without launching tools or touching settings."""
import subprocess
import tempfile
from pathlib import Path

from agent_build import compile_check

ROOT = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix="remote-ai-permissions-") as folder:
    program = Path(folder) / "check.exe"
    compile_check("ClaudePermissionsCheck", program, extra=[ROOT / "tests/ClaudePermissionsCheck.cs"])
    subprocess.run([str(program), str(ROOT / "tests/claude_permissions_cases.json")], check=True, timeout=30)
