"""Compile and test ownership using only disposable fake codex processes."""
import os
import sys
import shutil
from pathlib import Path
import subprocess
import tempfile

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402
with tempfile.TemporaryDirectory(prefix='remote-cli-native-') as folder:
    exe=Path(folder)/'codex.exe'
    agent_build.compile_check('CodexOwnerCheck', str(exe), [ROOT / 'tests/CodexOwnerCheck.cs'])
    shutil.copy2(exe, Path(folder)/'RemoteCliAgent.exe')
    for host in ['Positron', 'Code', 'rstudio']:
        shutil.copy2(exe, Path(folder)/(host + '.exe'))
    shutil.copy2(exe, Path(folder)/'claude.exe')
    subprocess.run([str(exe)],check=True,timeout=90)
