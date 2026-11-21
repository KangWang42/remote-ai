"""Compile and run the check of the model and thinking level an Antigravity CLI conversation is begun with, against the
stand-in of `agy acp` (tests/chat_standin.py), once with the options agy 1.5.13 names and once without any.

    python tests/acp_options_check.py
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402

with tempfile.TemporaryDirectory(prefix="remote-ai-acp-options-") as folder:
    folder = Path(folder)
    exe = folder / "check.exe"
    agent_build.compile_check("AcpOptionsCheck", str(exe), [ROOT / "tests" / "AcpOptionsCheck.cs"])
    agy = folder / "agy.cmd"
    agy.write_text('@"%s" "%s" agy %%*\r\n' % (sys.executable, ROOT / "tests" / "chat_standin.py"), encoding="mbcs")
    workspace = folder / "workspace"
    workspace.mkdir()
    for options in ("1", ""):
        record = folder / "record.jsonl"
        record.unlink(missing_ok=True)
        environment = dict(os.environ, STANDIN_AGY_OPTIONS=options, STANDIN_AGY_RECORD=str(record), PYTHONUTF8="1")
        subprocess.run([str(exe), str(agy), str(workspace), str(record)], env=environment, check=True, timeout=180)
