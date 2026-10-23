"""Run Windows transfer integrity and filesystem boundary checks in an isolated workspace."""
import subprocess
import tempfile
from pathlib import Path
from agent_build import ROOT, compile_check

work = ROOT / '09_backup/workbench/file-transfer'
work.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(dir=work) as folder:
    exe = Path(folder) / 'check.exe'
    compile_check('FileTransfersCheck', exe, extra=[ROOT / 'tests/FileTransfersCheck.cs'])
    subprocess.run([str(exe), str(Path(folder) / 'project')], check=True, timeout=60)
