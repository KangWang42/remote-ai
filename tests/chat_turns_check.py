"""Compile and run the check of when a conversation the phone runs tells that its turn is over (tests/ChatTurnsCheck.cs),
with the tools played by tests/chat_standin.py.

    python tests/chat_turns_check.py
"""
import subprocess
import sys
import tempfile
from pathlib import Path

from agent_build import ROOT, compile_check

with tempfile.TemporaryDirectory(prefix="remote-ai-turns-") as folder:
    root = Path(folder)
    exe = root / "check.exe"
    compile_check("ChatTurnsCheck", exe, [ROOT / "tests" / "ChatTurnsCheck.cs"])
    for tool in ("claude",):
        launcher = root / (tool + ".cmd")
        launcher.write_text('@"%s" "%s" %s %%*\r\n' % (sys.executable, ROOT / "tests" / "chat_standin.py", tool), encoding="mbcs")
        work = root / ("work-" + tool)
        work.mkdir()
        subprocess.run([str(exe), tool, str(launcher), str(work)], check=True, timeout=120)
