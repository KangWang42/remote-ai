"""Windows agy arguments check. Pass --live to open a new, isolated real CLI terminal."""
from pathlib import Path
import subprocess
import sys
import tempfile

from agent_build import ROOT, compile_check

# The CLI can keep its workspace open briefly after its terminal exits.
workspace = ROOT / ".build/agy-check/live-workspace"
workspace.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="remote-cli-agy-") as folder:
    exe = Path(folder) / "check.exe"
    compile_check("AntigravityCheck", exe, [Path(__file__).resolve().with_name("AntigravityCheck.cs")])
    subprocess.run([str(exe)] + ([str(workspace)] if "--live" in sys.argv[1:] else []), check=True, timeout=45)
