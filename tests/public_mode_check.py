"""A relay for everyone, end to end. First the Windows program as installed (run after tools/package_windows.py):
with such a relay in the published list, named there in the field that is not readable at a glance and with the word
it asks for, choosing it gives this computer a place of its own there without anything being typed, shows neither
the address nor the password, and keeps the place for the next start; a place the relay removed is asked for again. Then the agent alone in such a place: a terminal is started, typed into and read by a
phone under that place's address. The relay and the list are made here; ports 8744 (the program's own relay) and 8746.

    python tests/public_mode_check.py
"""
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from own_mode_check import ROOT, STAGE, listening, press
from tunnel_check import Client, await_ready, operation

sys.path.insert(0, str(ROOT / "tools"))
from relay_list import hide, show

PORT = 8746
WORD = "word-" + secrets.token_hex(6)       # what this relay asks for before it makes a space


def main():
    with tempfile.TemporaryDirectory(prefix="remote-cli-public-", ignore_cleanup_errors=True) as folder:
        store = Path(folder) / "relay"
        start = lambda: subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(PORT), "--data", str(store), "--public"],
                                         env=dict(os.environ, PYTHONUTF8="1", RCLI_JOIN_KEY=WORD), stdout=subprocess.DEVNULL)
        relay, agent = start(), None
        try:
            await_ready(lambda: listening(PORT), "relay")
            origin = "http://127.0.0.1:%d" % PORT
            # the list names it in the field that is not readable at a glance, with the word it asks for
            assert show(hide(origin, WORD)) == {"url": origin, "key": WORD} and origin not in hide(origin, WORD)
            listed = [{"name": "测试中转", "hidden": hide(origin, WORD)}, {"name": "not https", "url": "http://relay.example.com"}, {"name": "not a field", "hidden": "x"}]
            places = lambda: sorted(p.name for p in (store / "spaces").iterdir()) if (store / "spaces").exists() else []
            # the relay tab lists it without anything having been set up; only looking changes nothing
            looked = press(folder, "looked", relays=listed, kind="everyone")
            assert (looked["mode"], looked["tab"], looked["published"], looked["field"], looked["own_relay"]) == ("cloud", "2", "1", "测试中转", "running"), looked
            assert looked["address"] == "公共中转 · 测试中转" and looked["password_shown"] == "False" and places() == [], looked
            # choosing it: a place of this computer's own, nothing typed, neither address nor password shown, a code for the phone
            chosen = press(folder, "chosen", kind="public", relays=listed)
            assert (chosen["mode"], chosen["tab"], chosen["own_relay"]) == ("public", "2", "stopped") and chosen["status"].startswith("已连上公共中转“测试中转”"), chosen
            assert re.fullmatch(re.escape(origin) + r"/c/[a-f0-9]{20}", chosen["server"]) and chosen["code"] == "True", chosen
            assert "127.0.0.1" not in chosen["address"] and chosen["password_shown"] == "False" and chosen["new_password_button"] == "False", chosen
            assert places() == [chosen["server"][-20:]] and (store / "spaces" / places()[0] / "sessions.json").exists(), places()
            # the next start is at the same place: the phone keeps the address it knows
            later = press(folder, "later", again="chosen", mode="public", relays=listed)
            assert (later["mode"], later["server"]) == ("public", chosen["server"]) and later["status"].startswith("已连上公共中转"), later
            assert places() == [chosen["server"][-20:]], places()
            # a relay that no longer has the place gives another
            relay.kill(); relay.wait(10)
            shutil.rmtree(store / "spaces" / places()[0])
            relay = start()
            await_ready(lambda: listening(PORT), "relay again")
            anew = press(folder, "anew", again="chosen", mode="public", relays=listed)
            assert anew["mode"] == "public" and anew["server"] != chosen["server"] and anew["status"].startswith("已连上公共中转"), anew
            assert places() == [anew["server"][-20:]], places()
            # taken off the list while in use: said so, and the tab offers what is left
            gone = press(folder, "gone", again="chosen", mode="public", relays=[])
            assert gone["mode"] == "public" and "已从列表中移除" in gone["status"] and gone["published"] == "0", gone
            # leaving for the program's own relay: that relay is given a password of its own
            before = (Path(folder) / "chosen" / "password.dpapi").read_bytes()

            # ---- the agent alone, in a place of its own
            hidden = {"creationflags": subprocess.CREATE_NO_WINDOW}
            data, work = Path(folder) / "agent", Path(folder) / "work"
            data.mkdir(); work.mkdir()
            setup = Client(origin, timeout=10)
            try:
                setup.call("/api/space", {})
                raise AssertionError("a space was made without the word")
            except RuntimeError as refused:
                assert "403" in str(refused), refused
            made = setup.call("/api/space", {"key": WORD})
            space = made["space"]
            (data / "config.json").write_text(json.dumps({"Server": origin + space, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["demo=" + str(work)]}), encoding="utf-8")
            subprocess.run([str(STAGE / "RemoteCliAgent.exe"), "--set-password", str(data)], input=made["password"].encode(), check=True, timeout=10, **hidden)
            agent = subprocess.Popen([str(STAGE / "RemoteCliAgent.exe"), "--data", str(data)], **hidden)
            phone = Client(origin, timeout=10)
            phone.call(space + "/api/login", {"password": made["password"]})
            await_ready(lambda: phone.call(space + "/api/terminal")["device"]["workspaces"], "agent in its place")
            terminal = phone.call(space + "/api/terminal", operation("", "start", tool="shell", dir="demo"))["terminal"]
            text = lambda: "".join(c["data"] for c in phone.call(space + "/api/terminal?terminal=" + terminal)["chunks"])
            await_ready(lambda: phone.call(space + "/api/terminal?terminal=" + terminal)["terminal"]["state"] == "running", "terminal")
            time.sleep(3)
            phone.call(space + "/api/terminal", operation(terminal, "input", data="'IN_' + 'MY_PLACE'\r"))
            await_ready(lambda: "IN_MY_PLACE" in text(), "the terminal's answer")
            other = setup.call("/api/space", {"key": WORD})        # another computer's place has none of it
            stranger = Client(origin, timeout=10)
            stranger.call(other["space"] + "/api/login", {"password": other["password"]})
            assert stranger.call(other["space"] + "/api/terminal")["terminals"] == []
            phone.call(space + "/api/terminal", operation(terminal, "close"))
            assert before, "the program kept the place's password"
            print("relay for everyone: listed without setup; choosing it gives a place of this computer's own, shows neither address nor password and makes a code; "
                  "the place is kept, and asked for again when the relay removed it; the agent runs a terminal in its place and another place sees nothing of it")
        finally:
            for process in (agent, relay):
                if process and process.poll() is None:
                    subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True)
                    process.wait(10)


if __name__ == "__main__":
    main()
