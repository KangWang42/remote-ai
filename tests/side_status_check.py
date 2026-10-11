"""The side of the Windows program's window, as installed (run after tools/package_windows.py): under how the phone's
access stands it names the tools that are connected (Claude Code, Codex: those whose hooks are this program's and that
have told of a conversation through them), and only those; with none connected there is the one line.

The program runs off the screen with a data folder of its own, and the tools' settings are folders made here
(CLAUDE_CONFIG_DIR, CODEX_HOME): the settings of the tools on this computer are not touched, which is checked.

    python tests/side_status_check.py [folder for the pictures]
"""
import hashlib
import json
import os
import socket
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / ".build" / "windows" / "stage"
REAL = [Path.home() / ".claude" / "settings.json", Path.home() / ".codex" / "hooks.json"]


def marks():
    return [hashlib.sha256(file.read_bytes()).hexdigest() if file.is_file() else None for file in REAL]


def window(folder, name, heard):
    """Starts the program with the switch on and the tools in `heard` known to have told of a conversation;
    returns what it wrote of its window, and the folders that stand for the tools' settings."""
    data, claude, codex = (Path(folder) / name / part for part in ("data", "claude", "codex"))
    for made in (data, claude, codex):
        made.mkdir(parents=True)
    with socket.socket() as reserved:
        reserved.bind(("127.0.0.1", 0))
        port = reserved.getsockname()[1]
    (data / "config.json").write_text(json.dumps({"Mode": "lan", "Port": port, "UpdateCheckUtc": "2099-01-01T00:00:00.0000000Z", "Reach": "True", "ReachHeard": heard}), encoding="utf-8")
    listed = Path(folder) / name / "relays.json"         # the published list is one made here: nothing is asked of the internet
    listed.write_text(json.dumps({"relays": []}), encoding="utf-8")
    picture = Path(folder) / (name + ".png")
    environment = dict(os.environ, REMOTECLI_DATA=str(data), REMOTECLI_RELAYS=str(listed), CLAUDE_CONFIG_DIR=str(claude), CODEX_HOME=str(codex))
    subprocess.run([str(STAGE / "RemoteCli.exe"), "--screenshot", str(picture), "0", "everyone"], env=environment, cwd=str(STAGE), check=True, timeout=120)
    said = dict(line.split("=", 1) for line in Path(str(picture) + ".txt").read_text(encoding="utf-8").splitlines())
    return said, claude, codex


def main():
    before = marks()
    kept = sys.argv[1] if len(sys.argv) > 1 else None
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as scratch:
        folder = kept or scratch
        report = {}
        for name, heard, tools in (("side-none", [], ""), ("side-one", ["claude"], "Claude Code 已连接"), ("side-both", ["claude", "codex"], "Claude Code · Codex 已连接")):
            said, claude, codex = window(folder, name, heard)
            report[name] = said["side_lines"]
            assert said["tools"] == tools, (name, said)
            assert said["side_lines"] == (said["side"] + "|" + tools if tools else said["side"]) and said["side"], (name, said)
            # the hooks went into the folders made here, and name the program that was run
            for file in (claude / "settings.json", codex / "hooks.json"):
                assert "RemoteCliAgent.exe" in file.read_text(encoding="utf-8") and "--hook" in file.read_text(encoding="utf-8"), file
    assert marks() == before, "the settings of the tools on this computer were changed"
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
