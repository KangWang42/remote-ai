"""Compile and run the peer AI consultation check (tests/PeersCheck.cs), with the tools played by
tests/chat_standin.py and the MCP bridge served over a real Windows named pipe.

    python tests/peers_check.py
"""
import subprocess
import sys
import tempfile
from pathlib import Path

from agent_build import ROOT, compile_check

with tempfile.TemporaryDirectory(prefix="remote-ai-peers-") as folder:
    root = Path(folder)
    exe = root / "check.exe"
    compile_check("PeersCheck", exe, [ROOT / "tests" / "PeersCheck.cs"])
    launchers = {}
    for tool in ("claude", "codex", "agy"):
        launcher = root / (tool + ".cmd")
        launcher.write_text('@"%s" "%s" %s %%*\r\n' % (sys.executable, ROOT / "tests" / "chat_standin.py", tool), encoding="mbcs")
        launchers[tool] = str(launcher)
    work = root / "work"
    work.mkdir()
    data = root / "data"
    data.mkdir()
    subprocess.run([str(exe), str(data), launchers["claude"], launchers["codex"], launchers["agy"], str(work)], check=True, timeout=120)
