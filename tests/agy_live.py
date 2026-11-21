"""Antigravity CLI on the real tool, in temporary workspaces (build the agent first); its global hooks file is not
touched. Costs six short turns of the agy account; not in CI.
- AgyReachLive: the phone's message and stop for a conversation open on the computer, through this program's hook
  program and its hooks as ReachHooks writes them (.agents/hooks.json of the workspace), agy in its print mode;
- AgyChatLive: a conversation the phone runs (AgyChat, `agy acp`): answered, stopped, gone on with by its id;
- AgyOptionsLive: one begun as a team's member with Gemini 4 Neon at a high thinking level: chosen in that order
  before its first message (seen through tests/acp_record_proxy.py), and answered.

    python tests/agy_live.py [path to agy.exe] [name of one check]
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

from agent_build import ROOT, compile_check

agy = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(os.environ["LOCALAPPDATA"]) / "agy" / "bin" / "agy.exe"
only = sys.argv[2] if len(sys.argv) > 2 else ""
agent = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
if not agy.exists() or not agent.exists():
    sys.exit("needs agy (%s) and a built agent (%s)" % (agy, agent))
with tempfile.TemporaryDirectory(prefix="remote-ai-agy-live-") as folder:
    record = Path(folder) / "acp.jsonl"
    proxy = Path(folder) / "agy-recorded.cmd"
    proxy.write_text('@"%s" "%s" "%s" "%s" %%*\r\n' % (sys.executable, ROOT / "tests" / "acp_record_proxy.py", record, agy), encoding="mbcs")
    checks = (("AgyReachLive", [str(agent), str(agy)]), ("AgyChatLive", [str(agy)]), ("AgyOptionsLive", [str(proxy), str(record)]))
    for name, given in checks:
        if only and name != only:
            continue
        exe = Path(folder) / (name + ".exe")
        compile_check(name, exe, [ROOT / "tests" / (name + ".cs")])
        workspace = Path(folder) / name
        workspace.mkdir()
        subprocess.run([str(exe), *given, str(workspace)], check=True, timeout=700)
