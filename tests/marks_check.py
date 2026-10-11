"""Compile and run the check of the marks the computer keeps of single conversations (pinned, hidden), in a
temporary folder.

    python tests/marks_check.py
"""
import os
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
with tempfile.TemporaryDirectory(prefix="remote-cli-marks-") as folder:
    exe = Path(folder) / "check.exe"
    subprocess.run([str(compiler), "/nologo", "/target:exe", "/platform:x64", "/codepage:65001", "/reference:System.Web.Extensions.dll",
                    "/main:MarksCheck", "/out:" + str(exe), str(ROOT / "agent-windows/Marks.cs"), str(ROOT / "tests/MarksCheck.cs")], check=True)
    subprocess.run([str(exe)], check=True, timeout=60)
