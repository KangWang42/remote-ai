"""The "中转" tab of the Windows program with a relay of one's own, as installed (run after tools/package_windows.py):
opening the tab changes nothing; a relay that refuses the password leaves the connection in use as it was and says
why, and is not saved; a relay that accepts it is saved and switched to; "重新连接" with the tab open connects to the
relay the field shows; a relay kept by an earlier version becomes the first of the saved ones. The program presses
its own controls (--screenshot ... own) and writes what came of it; the relay is one started here. Uses temporary data
folders and ports 8744 and 8745.

    python tests/own_mode_check.py [folder for the pictures]
"""
import json
import os
import secrets
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / ".build" / "windows" / "stage"
OWN, LOCAL = 8745, 8744
relay_password, local_password = "relay-" + secrets.token_hex(8), "local-" + secrets.token_hex(8)


def listening(port):
    try:
        socket.create_connection(("127.0.0.1", port), 1).close()
        return True
    except OSError:
        return False


def press(folder, name, *typed, mode="cloud", own_server="", password=None, again=None, kind="own", relays=()):
    """Starts the program with its own data folder, opens the tab and does what is given; returns what it wrote.
    `again` names an earlier start whose data folder is used once more, with another way of connecting chosen."""
    data = Path(folder) / (again or name)
    if again:
        config = json.loads((data / "config.json").read_text(encoding="utf-8-sig"))
        (data / "config.json").write_text(json.dumps(dict(config, Mode=mode)), encoding="utf-8")
    else:
        data.mkdir()
        # "cloud" without the tunnel program: the program's own relay listens on this computer only.
        (data / "config.json").write_text(json.dumps({"Mode": mode, "Port": LOCAL, "OwnServer": own_server, "RemoteEnabled": True}), encoding="utf-8")
        subprocess.run([str(STAGE / "RemoteCliAgent.exe"), "--set-password", str(data)], input=(password or local_password).encode(), check=True)
    listed = Path(folder) / (name + "-relays.json")      # the published list is one made here: nothing is asked of the internet
    listed.write_text(json.dumps({"relays": list(relays)}), encoding="utf-8")
    picture = Path(folder) / (name + ".png")
    subprocess.run([str(STAGE / "RemoteCli.exe"), "--screenshot", str(picture), "0", kind, *typed], env=dict(os.environ, REMOTECLI_DATA=str(data), REMOTECLI_RELAYS=str(listed)),
                   cwd=str(STAGE), check=True, timeout=120)
    said = dict(line.split("=", 1) for line in Path(str(picture) + ".txt").read_text(encoding="utf-8").splitlines())
    said["config"] = json.loads((data / "config.json").read_text(encoding="utf-8-sig"))
    for _ in range(20):         # the program's relay goes with it
        if not listening(LOCAL):
            break
        time.sleep(0.5)
    return said


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else None
    with tempfile.TemporaryDirectory(prefix="remote-cli-own-mode-") as folder:
        relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(OWN), "--data", str(Path(folder) / "relay")],
                                 env=dict(os.environ, RCLI_PASSWORD=relay_password, PYTHONUTF8="1"), stdout=subprocess.DEVNULL)
        try:
            for _ in range(40):
                if listening(OWN):
                    break
                time.sleep(0.25)
            address = "http://127.0.0.1:%d" % OWN
            # only looking at the tab: the way in use goes on; with no relay known, the field offers to add one
            looked = press(folder, "looked")
            assert (looked["mode"], looked["tab"], looked["own_relay"], looked["saved"]) == ("cloud", "3", "running", "0"), looked
            assert looked["field"].startswith("添加") and looked["new_password_button"] == "False" and "尚未添加中转" in looked["about"], looked
            assert looked["config"]["Mode"] == "cloud" and looked["skin"] == "paper", looked
            # a password the relay refuses: said so, nothing switched or saved, the program's own relay still there
            refused = press(folder, "refused", address, "not-the-password")
            assert (refused["mode"], refused["own_relay"], refused["saved"]) == ("cloud", "running", "0") and "密码" in refused["about"] and "未改变" in refused["about"], refused
            assert refused["config"]["Mode"] == "cloud" and not refused["config"].get("OwnServer"), refused["config"]
            # an address without https:// in front, and a relay that is not there
            for typed, word in ((("127.0.0.1:%d" % OWN, relay_password), "https://"), (("http://127.0.0.1:1", relay_password), "连不上中转")):
                wrong = press(folder, "wrong-" + word.strip(":/"), *typed)
                assert wrong["mode"] == "cloud" and wrong["own_relay"] == "running" and word in wrong["about"], wrong
            # the right password: saved, switched to, said, and its address and password shown for the phone
            accepted = press(folder, "accepted", address, relay_password)
            assert (accepted["mode"], accepted["tab"], accepted["own_relay"], accepted["saved"]) == ("own", "3", "stopped", "1"), accepted
            assert accepted["status"].startswith("已连上中转") and accepted["address"] == address and accepted["password_shown"] == "True", accepted
            assert accepted["config"]["Mode"] == "own" and accepted["config"]["OwnServer"] == address and accepted["field"] == "127.0.0.1:%d" % OWN, accepted
            # back on another way, the tab remembers that relay, and "重新连接" with the tab open connects to it
            again = press(folder, "again", "-", "-", "again", again="accepted", mode="cloud")
            assert (again["mode"], again["own_relay"], again["saved"]) == ("own", "stopped", "1") and again["status"].startswith("已连上中转"), again
            # started with that relay in use: the two boxes show it, and "重新连接" connects to it again
            started = press(folder, "started", "-", "-", "again", again="accepted", mode="own")
            assert (started["mode"], started["address"], started["tab"]) == ("own", address, "3") and started["status"].startswith("已连上中转"), started
            # a relay an earlier version kept (its address in the settings, no list yet) is the first of the saved ones
            earlier = press(folder, "earlier", mode="own", own_server=address, password=relay_password)
            assert (earlier["mode"], earlier["saved"], earlier["address"]) == ("own", "1", address) and earlier["status"].startswith("已连上中转"), earlier
            assert earlier["config"]["Relay"] == "own " + address, earlier["config"]
            if out:
                out.mkdir(parents=True, exist_ok=True)
                for name in ("looked", "refused", "accepted"):
                    (out / ("own-" + name + ".png")).write_bytes((Path(folder) / (name + ".png")).read_bytes())
            print("relay tab, one's own relay: opening it changes nothing; a refused password, a bad address and an absent relay leave the connection as it was, say why and save nothing; "
                  "an accepted one is saved and switched to; the tab remembers it, and the button below connects to it")
        finally:
            relay.kill()


if __name__ == "__main__":
    main()
