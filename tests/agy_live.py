"""Antigravity CLI on the real tool, in temporary workspaces (build the agent first); its global hooks file is not
touched. Costs five short turns of the agy account; not in CI.
- AgyReachLive: the phone's message and stop for a conversation open on the computer, through this program's hook
  program and its hooks as ReachHooks writes them (.agents/hooks.json of the workspace), agy in its print mode;
- AgyChatLive: a conversation the phone runs (AgyChat, `agy acp`): answered, stopped, gone on with by its id.

    python tests/agy_live.py [path to agy.exe]
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
    for name, given in (("AgyReachLive", [str(agent), str(agy)]), ("AgyChatLive", [str(agy)])):
        exe = Path(folder) / (name + ".exe")
        compile_check(name, exe, [ROOT / "tests" / (name + ".cs")])
        workspace = Path(folder) / name
        workspace.mkdir()
        subprocess.run([str(exe), *given, str(workspace)], check=True, timeout=700)
