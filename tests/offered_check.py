"""The window's page of projects, as installed (run after tools/package_windows.py): beside the projects it lists the
folders with conversations of Claude Code or Codex that are no projects yet, those used in a terminal apart from those
used by a desktop app or an editor (the latter also while the app is closed), each with by what it is used; all of a
list are made projects at once.

The program runs off the screen with a data folder of its own; the tools' conversations are made up (tests/made_up.py),
so nothing of the tools on this computer is read or listed.

    python tests/offered_check.py [folder for the pictures]
"""
import json
import os
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / ".build" / "windows" / "stage"
sys.path.insert(0, str(ROOT / "tests"))
from made_up import Homes        # noqa: E402

uuid = lambda n: "0199cccc-bbbb-7ccc-8ddd-%012x" % n


def main():
    kept = Path(sys.argv[1]) if len(sys.argv) > 1 else None
    # the system's folder for passing things is never offered: the made-up folders lie elsewhere
    apart = ROOT / ".build" / ("offered-" + secrets.token_hex(4))
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as scratch:
        try:
            out = kept or Path(scratch)
            out.mkdir(parents=True, exist_ok=True)
            data = Path(scratch) / "data"
            project, typed, both, desk, drawn, edited, gone = (apart / name for name in ("web-shop", "term-notes", "both-ways", "desk-notes", "claude-drafts", "edited", "gone"))
            for made in (data, project, typed, both, desk, drawn, edited, gone):
                made.mkdir(parents=True)
            homes = Homes(scratch)
            homes.claude_saved(project, uuid(1), "项目里的对话", 1, entry="cli")
            homes.codex_saved(typed, uuid(2), "终端里的笔记", 2, originator="codex-tui", source="cli")
            homes.claude_saved(both, uuid(3), "终端里开的", 3, entry="cli")
            homes.codex_saved(both, uuid(4), "桌面版里接着做的", 4, originator="Codex Desktop", source="vscode")
            homes.codex_saved(desk, uuid(5), "桌面版里的笔记", 5, originator="Codex Desktop", source="vscode")      # the app says "vscode" of itself, and names itself
            homes.claude_saved(drawn, uuid(6), "桌面版里的草稿", 6, entry="claude-desktop-3p")
            homes.codex_saved(edited, uuid(7), "编辑器里的", 7, originator="codex_vscode", source="vscode")
            # an earlier Codex wrote its instructions before the folder: it is found all the same
            homes.codex_saved(apart / "long-ago", uuid(8), "很早以前的", 900, originator="codex-tui", source="cli", instructions="x" * 40000)
            (apart / "long-ago").mkdir()
            homes.codex_saved(gone, uuid(9), "删掉的文件夹", 8, originator="codex-tui", source="cli")
            gone.rmdir()
            with socket.socket() as reserved:
                reserved.bind(("127.0.0.1", 0))
                port = reserved.getsockname()[1]
            (data / "config.json").write_text(json.dumps({"Mode": "lan", "Port": port, "UpdateCheckUtc": "2099-01-01T00:00:00.0000000Z", "RemoteEnabled": True, "RemoteDirs": ["web-shop=%s" % project]}), encoding="utf-8")
            listed = Path(scratch) / "relays.json"         # the published list is one made here: nothing is asked of the internet
            listed.write_text(json.dumps({"relays": []}), encoding="utf-8")
            environment = homes.environment(REMOTECLI_DATA=str(data), REMOTECLI_RELAYS=str(listed))

            def window(name, *pressed):
                picture = out / name
                subprocess.run([str(STAGE / "RemoteCli.exe"), "--screenshot", str(picture), "1", *pressed], env=environment, cwd=str(STAGE), check=True, timeout=120)
                rows, projects, left = (part.split("\n") if part else [] for part in Path(str(picture) + ".txt").read_text(encoding="utf-8").split("\n--\n"))
                Path(str(picture) + ".txt").unlink()
                return [tuple(row.split("\t")) for row in rows], projects, left

            terminal, projects, _ = window("offered-terminal.png", "terminal")
            assert projects == ["web-shop=%s" % project], projects
            # used in a terminal: with the tools; the newest first; not the project, not the folder that is gone
            assert terminal == [("term-notes", "Codex", str(typed)), ("both-ways", "Claude Code · Codex", str(both)), ("long-ago", "Codex", str(apart / "long-ago"))], terminal
            # used by an app or an editor: with by what; whether the apps run now makes no difference here
            apps, _, _ = window("offered-apps.png", "apps")
            assert apps == [("both-ways", "Codex 桌面版", str(both)), ("desk-notes", "Codex 桌面版", str(desk)), ("claude-drafts", "Claude 桌面版", str(drawn)), ("edited", "编辑器", str(edited))], apps
            # all of a list at once
            _, projects, left = window("offered-added.png", "apps", "all")
            assert [line.split("=")[0] for line in projects] == ["web-shop", "both-ways", "desk-notes", "claude-drafts", "edited"] and left == [], (projects, left)
            terminal, _, _ = window("offered-rest.png", "terminal")
            assert [row[0] for row in terminal] == ["term-notes", "long-ago"], terminal
            print(json.dumps({"terminal": terminal, "apps": apps, "projects": projects}, ensure_ascii=False))
            print("the window offers the folders with conversations, those of a terminal apart from those of an app, and adds them")
        finally:
            homes.close()
            shutil.rmtree(apart, ignore_errors=True)


if __name__ == "__main__":
    main()
