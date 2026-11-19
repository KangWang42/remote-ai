"""Compile and run the check of the tools' usage limits (agent-windows/Quotas.cs) against the built agent, which is
also run as Claude Code's status line. Settings are written in a temporary folder only.

    python tests/quotas_check.py
"""
import subprocess
import tempfile
from pathlib import Path

from agent_build import ROOT, compile_check

with tempfile.TemporaryDirectory(prefix="remote-ai-quotas-") as folder:
    exe = Path(folder) / "check.exe"
    compile_check("QuotasCheck", exe, [ROOT / "tests" / "QuotasCheck.cs"])
    subprocess.run([str(exe), str(ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe")], check=True, timeout=120)
