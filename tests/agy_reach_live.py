"""The phone's message and stop for a real Antigravity CLI, through this program's hook program (build the agent
first): agy runs twice in its print mode, in a temporary workspace whose .agents/hooks.json holds the hooks as
ReachHooks writes them. Its global hooks file is not touched. Costs two short turns of the agy account; not in CI.

    python tests/agy_reach_live.py [path to agy.exe]
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

from agent_build import ROOT, compile_check

agy = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(os.environ["LOCALAPPDATA"]) / "agy" / "bin" / "agy.exe"
agent = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
if not agy.exists() or not agent.exists():
    sys.exit("needs agy (%s) and a built agent (%s)" % (agy, agent))
with tempfile.TemporaryDirectory(prefix="remote-ai-agy-live-") as folder:
    exe = Path(folder) / "check.exe"
    compile_check("AgyReachLive", exe, [ROOT / "tests" / "AgyReachLive.cs"])
    workspace = Path(folder) / "workspace"
    workspace.mkdir()
    subprocess.run([str(exe), str(agent), str(agy), str(workspace)], check=True, timeout=600)
