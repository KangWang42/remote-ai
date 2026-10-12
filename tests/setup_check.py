"""The installer in its sandbox mode: a chosen folder that already holds other files, moving to another folder,
and removal. A real installation on this computer is not touched (separate registry entry and shortcut names).

    python tests/setup_check.py dist/RemoteCli-Setup-x.y.z.exe
"""
import os
import subprocess
import sys
import tempfile
import time
import winreg
from pathlib import Path

KEY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteCli-Sandbox"
OWN = {"python", "relay", "web", "RemoteCli.exe", "RemoteCliAgent.exe", "Uninstall.exe", "LICENSE", "THIRD_PARTY.md", "README.md"}


def location():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, KEY) as key:
            return winreg.QueryValueEx(key, "InstallLocation")[0]
    except OSError:
        return None


def main():
    setup = str(Path(sys.argv[1]).resolve())
    env = dict(os.environ, REMOTECLI_SETUP_SANDBOX="1")
    links = [Path(os.environ["APPDATA"]) / "Microsoft/Windows/Start Menu/Programs/Remote AI sandbox.lnk", Path(os.environ["USERPROFILE"]) / "Desktop/Remote AI sandbox.lnk"]
    # the shortcuts an installation under the earlier name left: taken away when this one is installed
    earlier = [link.with_name("Remote CLI sandbox.lnk") for link in links]
    for link in earlier:
        link.write_bytes(b"left by the earlier name")
    assert location() is None, "a sandbox installation is left over from an earlier run"
    with tempfile.TemporaryDirectory() as temp:
        busy, second = Path(temp) / "has things", Path(temp) / "second place"
        busy.mkdir(); second.mkdir()
        (busy / "keep.txt").write_text("keep")
        # 1. a folder with other files in it: the program goes into a RemoteCli folder inside, the files stay
        assert subprocess.run([setup, "--quiet", "--dir", str(busy)], env=env).returncode == 0
        first = busy / "RemoteCli"
        assert {p.name for p in first.iterdir()} == OWN, sorted(p.name for p in first.iterdir())
        assert (busy / "keep.txt").read_text() == "keep" and location() == str(first)
        assert links[0].is_file()
        assert not earlier[0].exists() and not earlier[1].exists(), "the earlier name's shortcuts are gone"
        # 2. installing again elsewhere moves it: the earlier copy and its now empty folder go, the neighbours stay
        assert subprocess.run([setup, "--quiet", "--dir", str(second)], env=env).returncode == 0
        assert not first.exists() and (busy / "keep.txt").is_file()
        assert {p.name for p in second.iterdir()} == OWN and location() == str(second)
        # 3. removal takes only what the installer put there
        (second / "mine.txt").write_text("mine")
        assert subprocess.run([str(second / "Uninstall.exe"), "--uninstall", "--quiet"], env=env).returncode == 0
        time.sleep(6)       # the last file is removed by a command window after the program has exited
        assert [p.name for p in second.iterdir()] == ["mine.txt"], [p.name for p in second.iterdir()]
        assert location() is None and not links[0].exists() and not links[1].exists()
    print("installer: chosen folder, move and removal behave")


if __name__ == "__main__":
    main()
