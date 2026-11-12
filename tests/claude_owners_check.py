"""Exercise duplicate Claude ownership with isolated records and real Windows fixture processes."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

import agent_build

with tempfile.TemporaryDirectory(prefix="remote-ai-claude-owners-") as folder:
    root = Path(folder)
    exe = root / "check.exe"
    agent_build.compile_check("ClaudeOwnersCheck", exe, [agent_build.ROOT / "tests/ClaudeOwnersCheck.cs"])
    for name in ("claude.exe", "Positron.exe"):
        shutil.copy2(exe, root / name)
    env = dict(os.environ, CLAUDE_CONFIG_DIR=str(root / "claude"), CODEX_HOME=str(root / "codex"))
    subprocess.run([exe], env=env, check=True, timeout=60)
