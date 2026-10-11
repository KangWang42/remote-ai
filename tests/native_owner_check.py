"""Compile and test ownership using only disposable fake codex processes."""
import os
import shutil
from pathlib import Path
import subprocess
import tempfile

ROOT=Path(__file__).resolve().parents[1]
compiler=Path(os.environ['WINDIR'])/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
with tempfile.TemporaryDirectory(prefix='remote-cli-native-') as folder:
    exe=Path(folder)/'codex.exe'
    subprocess.run([str(compiler),'/nologo','/target:exe','/platform:x64','/codepage:65001',
                    '/reference:System.Management.dll','/reference:System.Net.Http.dll','/reference:System.Web.Extensions.dll',
                    '/reference:System.Security.dll','/main:CodexOwnerCheck','/out:'+str(exe),
                    str(ROOT/'agent-windows/CodexSessions.cs'), str(ROOT/'agent-windows/Reach.cs'), str(ROOT/'agent-windows/Marks.cs'),str(ROOT/'agent-windows/RemoteCliAgent.cs'),
                    str(ROOT/'tests/CodexOwnerCheck.cs')],check=True)
    shutil.copy2(exe, Path(folder)/'RemoteCliAgent.exe')
    shutil.copy2(exe, Path(folder)/'Positron.exe')
    shutil.copy2(exe, Path(folder)/'claude.exe')
    subprocess.run([str(exe)],check=True,timeout=90)
