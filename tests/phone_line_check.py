"""The phone changes the program's settings and the line it is reached by, with the computer out of reach. The
Windows program runs as installed (run after tools/package_windows.py), off the screen; this script is the phone.

- the phone is told the lines there are and a few settings, and nothing that says where a relay is;
- a setting changed from the phone is kept by the program; one that does not exist is refused;
- to another relay: the new line is made ready beside the one in use, the phone is handed its address and a sign-in
  that works once, says through the new line that it arrived, and from then on that line is the program's;
- a phone that does not arrive: the program goes back to the line it had, which was left as it was;
- to the tunnel and from it to the local network, where the program's own relay is started beside a relay, and
  started again to listen wider or narrower;
- a relay of an earlier version, which could not pass on the phone's word, is not turned to.

With Chrome, the pages are then used as on a phone: a message is said to a running terminal from the list without
going into it, and on the page of the computer's settings a setting and the line are changed by pressing them.

Nothing is opened to the network: the relays listen on this computer, a stand-in takes the place of cloudflared, and
REMOTECLI_LAN makes "the local network" this computer too. Uses temporary data folders and ports 8746 to 8750.

    python tests/phone_line_check.py [chrome.exe [folder for the pictures]]
"""
import base64
import http.client
import json
import os
import secrets
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ws_client import Socket

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / ".build" / "windows" / "stage"
LOCAL, FIRST, SECOND, EARLIER, DEBUG = 8746, 8747, 8748, 8749, 8750
TRIAL = 8           # seconds the phone is given to arrive (REMOTECLI_TRIAL)
STANDIN = "https://remote-cli-standin.trycloudflare.com"
hidden = {"creationflags": subprocess.CREATE_NO_WINDOW}


def listening(port):
    try:
        socket.create_connection(("127.0.0.1", port), 1).close()
        return True
    except OSError:
        return False


class Phone:
    """What the phone's pages do at one address: sign in, and ask the program about itself."""

    def __init__(self, origin):
        self.host, self.cookie, self.password = origin.split("://", 1)[1], "", ""

    def post(self, path, payload):
        connection = http.client.HTTPConnection(self.host, timeout=40)
        try:
            connection.request("POST", path, json.dumps(payload), {"Content-Type": "application/json", "Cookie": self.cookie})
            reply = connection.getresponse()
            body = json.loads(reply.read() or b"{}")
            if reply.getheader("Set-Cookie"):
                self.cookie = reply.getheader("Set-Cookie").split(";", 1)[0]
            return reply.status, body
        except (OSError, http.client.HTTPException):        # the program's relay is started again in the middle of a change
            return 0, {"error": "the connection was cut"}
        finally:
            connection.close()

    def get(self, path):
        connection = http.client.HTTPConnection(self.host, timeout=10)
        try:
            connection.request("GET", path, headers={"Cookie": self.cookie})
            return json.loads(connection.getresponse().read())
        finally:
            connection.close()

    def online(self):
        """Whether the relay at this address has the computer."""
        return self.get("/api/terminal")["device"]["online"]

    def sign_in(self, **how):
        return self.post("/api/login", how)[0] == 200

    def ask(self, action, **fields):
        """The answer of the program, or the words it (or the relay) refused with."""
        status, body = self.post("/api/computer", dict(fields, id=secrets.token_hex(16), action=action))
        return body if status == 200 else body.get("error", "HTTP %d" % status)

    def about(self, seconds=30):
        """Told once the computer is there: after it turned to this relay, that takes a moment."""
        deadline = time.monotonic() + seconds
        while True:
            said = self.ask("settings_read")
            if isinstance(said, dict) or time.monotonic() > deadline:
                return said
            time.sleep(0.3)


def until(check, what, seconds=60):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        found = check()
        if found:
            return found
        time.sleep(0.3)
    raise AssertionError("waited in vain for " + what)


def pages(chrome, out, folder, first, addresses, config):
    """The pages in Chrome, pressed as on a phone. `first` is signed in at the relay in use; `addresses` are the two relays."""
    profile = folder / "chrome"
    browser = subprocess.Popen([chrome, "--headless=new", "--enable-unsafe-swiftshader", "--hide-scrollbars", "--no-first-run", "--user-data-dir=" + str(profile),
                                "--remote-debugging-port=%d" % DEBUG, "--remote-allow-origins=*", "--window-size=412,892", "about:blank"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        for _ in range(60):
            try:
                connection = http.client.HTTPConnection("127.0.0.1", DEBUG, timeout=5)
                connection.request("PUT", "/json/new?about:blank")
                page = json.loads(connection.getresponse().read())
                break
            except (OSError, ValueError):
                time.sleep(0.5)
        pipe = Socket("http://127.0.0.1:%d" % DEBUG, "/devtools/page/" + page["id"], "", 30)
        sent, said = [0], []

        def ask(method, **params):
            sent[0] += 1
            pipe.send({"id": sent[0], "method": method, "params": params})
            while True:
                message = pipe.receive(40)
                if message.get("method") == "Runtime.exceptionThrown":
                    said.append(str(message["params"]["exceptionDetails"].get("exception", {}).get("description", message["params"]["exceptionDetails"].get("text")))[:300])
                if message.get("id") == sent[0]:
                    return message.get("result", {})

        value = lambda expression: ask("Runtime.evaluate", expression=expression, returnByValue=True).get("result", {}).get("value")

        def press(element):
            """A click in the middle of what the expression finds, where a finger would touch it."""
            x, y = until(lambda: value("(() => { const e = %s; if (!e) return null; const r = e.getBoundingClientRect(); return r.width ? [r.left + r.width / 2, r.top + r.height / 2] : null; })()" % element), element, 20)
            for kind in ("mousePressed", "mouseReleased"):
                ask("Input.dispatchMouseEvent", type=kind, x=x, y=y, button="left", clickCount=1)

        def picture(name):
            if out:
                out.mkdir(parents=True, exist_ok=True)
                (out / name).write_bytes(base64.b64decode(ask("Page.captureScreenshot", format="png")["data"]))

        ask("Page.enable")
        ask("Runtime.enable")
        ask("Emulation.setDeviceMetricsOverride", width=412, height=892, deviceScaleFactor=2, mobile=True)
        # ---- the list: a message is said to a running terminal without going into it
        started = first.post("/api/terminal", {"id": secrets.token_hex(16), "action": "start", "tool": "shell", "dir": "demo"})[1]
        terminal = started["terminal"]
        until(lambda: next((t for t in first.get("/api/terminal")["terminals"] if t["id"] == terminal and t["state"] == "running"), None), "the terminal to run", 30)
        time.sleep(3)
        ask("Page.navigate", url=addresses[0] + "/#p=" + first.password)
        press("document.querySelector('#active-list .side.say')")
        until(lambda: value("!!document.querySelector('#sheet[open] textarea')"), "the sheet", 10)
        picture("say.png")
        ask("Input.insertText", text="echo (20000+261)")
        press("document.querySelector('#sheet-form .choice.solid')")
        printed = lambda: "".join(chunk["data"] for chunk in first.get("/api/terminal?terminal=%s&after=0" % terminal)["chunks"])
        until(lambda: "20261" in printed(), "the shell to have run what was said", 20)
        assert value("!document.getElementById('sheet').open")
        first.post("/api/terminal", {"id": secrets.token_hex(16), "action": "close", "terminal": terminal})
        # ---- the settings, reached by the one button for them: told, a setting changed, and the line changed by pressing it
        ask("Page.navigate", url=addresses[0] + "/")
        until(lambda: value("!!document.getElementById('settings') && !document.getElementById('home').hidden"), "the list", 20)
        assert value("!document.getElementById('more') && !document.getElementById('look')")
        press("document.getElementById('settings')")
        until(lambda: (value("location.pathname") or "").endswith("/computer/") and value("document.querySelectorAll('#lines li').length") == 3
              and value("document.querySelectorAll('#settings .pick, #settings .flip').length") == 5, "the page of settings", 20)
        # the lines (the local network, the tunnel, and one row for the relays of one's own: there are none for everyone
        # here), the five settings and the version of the program; the look; signing out. The app's own row is for the app.
        # A copy with a data folder of its own, as this one, has not put its hooks into the tools' settings: that switch is off
        assert value("document.querySelectorAll('#settings li').length") == 6 and value("Array.from(document.querySelectorAll('#settings li')).find(li => li.textContent.includes('手机处理电脑上的对话')).querySelector('.flip').getAttribute('aria-checked')") == "false" and value("document.querySelectorAll('#skins button').length") >= 6
        assert value("document.getElementById('part-app').hidden") is True and value("document.querySelector('#account b').textContent") == "退出登录"
        assert value("document.querySelector('#lines li.current b').textContent") == "自己的中转" and addresses[0].split("://")[1] in value("document.querySelector('#lines li.current small').textContent"), value("document.getElementById('lines').innerText")
        assert "http" not in value("document.getElementById('lines').innerText")
        picture("computer.png")
        press("Array.from(document.querySelectorAll('#settings .pick button')).find(b => b.textContent === '自动改文件')")
        until(lambda: config()["RemoteMaxMode"] == "edit", "the setting to be kept", 15)
        until(lambda: value("Array.from(document.querySelectorAll('#settings .pick button')).find(b => b.textContent === '自动改文件').getAttribute('aria-pressed')") == "true", "the page to show it", 10)
        # the kind first, then one of its relays from the list, each with how fast it answered the computer
        press("document.querySelector('#lines li.current button')")
        until(lambda: value("document.querySelectorAll('#sheet[open] .choice').length") == 3, "the list of relays", 10)
        assert value("document.querySelectorAll('#sheet[open] .choice .tick').length") == 1
        picture("computer-relays.png")
        press("Array.from(document.querySelectorAll('#sheet-form .choice')).find(b => b.textContent.startsWith('%s'))" % addresses[1].split("://")[1])
        until(lambda: value("!!document.querySelector('#sheet[open] .choice.solid')"), "the question", 10)
        picture("computer-asks.png")
        press("document.querySelector('#sheet-form .choice.solid')")
        # the page is taken to the other relay, signs in there with what it was handed, and says that it arrived
        until(lambda: value("location.origin") == addresses[1] and (value("document.getElementById('state-title').textContent") or "").startswith("已切换到"), "the page to arrive at the other relay", 60)
        until(lambda: addresses[1].split("://")[1] in (value("document.querySelector('#lines li.current small') && document.querySelector('#lines li.current small').textContent") or ""), "the line in use to be shown", 10)
        assert value("location.hash") == ""
        assert config()["OwnServer"] == addresses[1], config()
        picture("computer-kept.png")
        assert not said, said[:3]
    finally:
        browser.kill()


def main():
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    with tempfile.TemporaryDirectory(prefix="remote-cli-phone-line-", ignore_cleanup_errors=True) as folder:
        folder = Path(folder)
        data, listed, work = folder / "program", folder / "relays.json", folder / "work"
        data.mkdir()
        work.mkdir()
        listed.write_text(json.dumps({"relays": []}), encoding="utf-8")       # no relay is asked of the internet
        subprocess.run([str(compiler), "/nologo", "/out:" + str(data / "cloudflared.exe"), str(ROOT / "tests" / "TunnelStandin.cs")], check=True, **hidden)
        # a relay as version 1.1.4 had it, before the program could be asked about itself
        old = folder / "earlier" / "relay"
        old.mkdir(parents=True)
        for name in ("server.py", "relay.py", "screen.py", "websocket.py"):
            (old / name).write_bytes(subprocess.run(["git", "show", "v1.1.4:relay/" + name], cwd=str(ROOT), check=True, capture_output=True).stdout)
        passwords = {port: "relay-" + secrets.token_hex(8) for port in (FIRST, SECOND, EARLIER)}
        local_password = "local-" + secrets.token_hex(8)
        origin = lambda port: "http://127.0.0.1:%d" % port
        environment = dict(os.environ, REMOTECLI_DATA=str(data), REMOTECLI_RELAYS=str(listed), REMOTECLI_LAN="127.0.0.1", REMOTECLI_TRIAL=str(TRIAL), PYTHONUTF8="1")
        relays = [subprocess.Popen([sys.executable, str(script), "--port", str(port), "--data", str(folder / ("relay-%d" % port)), "--web", str(ROOT / "web")],
                                   env=dict(os.environ, RCLI_PASSWORD=passwords[port], PYTHONUTF8="1"), stdout=subprocess.DEVNULL, **hidden)
                  for port, script in ((FIRST, ROOT / "relay" / "server.py"), (SECOND, ROOT / "relay" / "server.py"), (EARLIER, old / "server.py"))]
        program = None
        try:
            until(lambda: all(listening(port) for port in passwords), "the relays")
            (data / "config.json").write_text(json.dumps({"Mode": "cloud", "Port": LOCAL, "RemoteEnabled": True, "RemoteDirs": ["demo=" + str(work)]}), encoding="utf-8")
            subprocess.run([str(STAGE / "RemoteCliAgent.exe"), "--set-password", str(data)], input=local_password.encode(), check=True, **hidden)
            # the three relays are added at the computer, as its owner does; the last one added is the one in use
            for port in (EARLIER, SECOND, FIRST):
                subprocess.run([str(STAGE / "RemoteCli.exe"), "--screenshot", str(folder / "added.png"), "0", "own", origin(port), passwords[port]], env=environment, cwd=str(STAGE), check=True, timeout=120)
                until(lambda: not listening(LOCAL), "the program's relay to go with it", 15)
            config = lambda: json.loads((data / "config.json").read_text(encoding="utf-8-sig"))
            assert (config()["Mode"], config()["OwnServer"]) == ("own", origin(FIRST)), config()

            program = subprocess.Popen([str(STAGE / "RemoteCli.exe"), "--offstage"], env=environment, cwd=str(STAGE))
            first, second = Phone(origin(FIRST)), Phone(origin(SECOND))
            assert first.sign_in(password=passwords[FIRST])
            told = until(lambda: (lambda said: isinstance(said, dict) and said)(first.ask("settings_read")), "the program at its relay")
            lines = {line["id"]: line for line in told["lines"]}
            assert [line["kind"] for line in told["lines"]] == ["lan", "cloud", "own", "own", "own"] and lines["cloud"]["ready"] is True, told
            assert [line["id"] for line in told["lines"] if line["current"]] == [told["lines"][-1]["id"]] and told["lines"][-1]["name"] == "127.0.0.1:%d" % FIRST, told
            # a line is named by a word that tells nothing of where it leads
            assert all(line["id"] in ("lan", "cloud") or (len(line["id"]) == 13 and line["id"].isalnum()) for line in told["lines"]), told
            assert told["change"] == {"state": "", "to": "", "note": "", "left": 0} and told["settings"]["rights"] == "full", told
            by_port = {port: next(line["id"] for line in told["lines"] if line.get("name") == "127.0.0.1:%d" % port) for port in passwords}

            # ---- settings
            assert first.ask("settings_change", name="rights", value="read")["settings"]["rights"] == "read" and config()["RemoteMaxMode"] == "read"
            assert first.ask("settings_change", name="tunnel", value="http2")["settings"]["tunnel"] == "http2" and config()["TunnelProtocol"] == "http2"
            assert first.ask("settings_change", name="tunnel", value="auto")["settings"]["tunnel"] == "auto"
            for name, value in (("rights", "everything"), ("port", "9000"), ("update", "yes")):
                assert "没有这项设置" in first.ask("settings_change", name=name, value=value), (name, value)
            assert config()["Port"] == LOCAL and config()["RemoteMaxMode"] == "read"

            def turn(phone, to, expect_address, twice=False):
                """Asks for the line, waits until it is ready and takes it; returns what the phone is handed."""
                asked = phone.ask("line_switch", to=to)
                assert isinstance(asked, dict) and asked["change"]["state"] in ("preparing", "ready") and asked["change"]["to"] == to, asked
                if twice:
                    assert "正在切换" in phone.ask("line_switch", to="lan")        # one change at a time
                until(lambda: (lambda said: isinstance(said, dict) and said["change"]["state"] == "ready")(phone.ask("settings_read")), "the line to be ready")
                handed = phone.ask("line_take")
                assert isinstance(handed, dict) and handed["address"] == expect_address and handed["seconds"] == TRIAL and len(handed["key"]) == 32 and handed["ticket"], handed
                return handed

            # ---- refused before anything is done
            assert "已经在使用" in first.ask("line_switch", to=by_port[FIRST])
            assert "没有这条线路" in first.ask("line_switch", to="r000000000000")
            assert "还没有准备好" in first.ask("line_take") and "没有等待确认" in first.ask("line_keep", key="0" * 32)

            # ---- a relay of an earlier version is not turned to; the line in use goes on
            first.ask("line_switch", to=by_port[EARLIER])
            failed = until(lambda: (lambda said: isinstance(said, dict) and said["change"]["state"] == "failed" and said)(first.ask("settings_read")), "the refusal")
            assert "版本较早" in failed["change"]["note"] and config()["OwnServer"] == origin(FIRST), failed
            assert "版本较早" in first.ask("line_take")

            # ---- to another relay, and the phone arrives
            handed = turn(first, by_port[SECOND], origin(SECOND), twice=True)
            assert config()["OwnServer"] == origin(FIRST)              # nothing is kept before the phone has arrived
            assert second.sign_in(ticket=handed["ticket"]) and not second.sign_in(ticket=handed["ticket"])       # once
            assert isinstance(second.about(), dict)
            assert "没有等待确认" in second.ask("line_keep", key="f" * 32)
            kept = second.ask("line_keep", key=handed["key"])
            assert [line["id"] for line in kept["lines"] if line["current"]] == [by_port[SECOND]] and kept["change"]["state"] in ("kept", ""), kept
            assert (config()["Mode"], config()["OwnServer"], config()["Relay"]) == ("own", origin(SECOND), "own " + origin(SECOND)), config()
            until(lambda: not first.online(), "the computer to have left the first relay", 30)
            assert "电脑未连接" in first.ask("settings_read") and second.online()

            # ---- back to the first, and the phone does not arrive: the program returns to the line it had
            turn(second, by_port[FIRST], origin(FIRST))
            back = until(lambda: (lambda said: isinstance(said, dict) and said["change"]["state"] == "failed" and said)(second.ask("settings_read")), "the return", TRIAL + 40)
            assert "没有在新线路上出现" in back["change"]["note"] and [line["id"] for line in back["lines"] if line["current"]] == [by_port[SECOND]], back
            assert config()["OwnServer"] == origin(SECOND) and config()["Server"] == origin(SECOND), config()

            # ---- to the tunnel: the program's own relay and the tunnel are started beside the relay in use
            assert not listening(LOCAL)
            handed = turn(second, "cloud", STANDIN)
            local = Phone(origin(LOCAL))
            assert local.sign_in(ticket=handed["ticket"]) and isinstance(local.about(), dict)
            kept = local.ask("line_keep", key=handed["key"])
            assert [line["id"] for line in kept["lines"] if line["current"]] == ["cloud"] and config()["Mode"] == "cloud", kept
            tunnels = lambda: subprocess.run(["tasklist", "/FI", "IMAGENAME eq cloudflared.exe", "/FO", "CSV", "/NH"], capture_output=True, text=True, **hidden).stdout.count("cloudflared.exe")
            assert tunnels() >= 1

            # ---- from the tunnel to the local network and not arriving: the tunnel was never ended, the relay listens as before
            before = tunnels()
            turn(local, "lan", origin(LOCAL))
            back = until(lambda: (lambda said: isinstance(said, dict) and said["change"]["state"] == "failed" and said)(local.ask("settings_read")), "the return", TRIAL + 40)
            assert [line["id"] for line in back["lines"] if line["current"]] == ["cloud"] and config()["Mode"] == "cloud" and tunnels() == before, back

            # ---- the same, arriving: the phone stays signed in through the relay's new start, and the tunnel is ended
            handed = turn(local, "lan", origin(LOCAL))
            kept = until(lambda: (lambda said: isinstance(said, dict) and said)(local.ask("line_keep", key=handed["key"])), "the program to keep the line")
            assert [line["id"] for line in kept["lines"] if line["current"]] == ["lan"] and config()["Mode"] == "lan", kept
            until(lambda: tunnels() == before - 1, "the tunnel to end", 20)
            assert isinstance(local.about(), dict)

            # ---- from the local network to the tunnel: the relay listens for this computer alone again, the phone stays signed in
            handed = turn(local, "cloud", STANDIN)
            kept = until(lambda: (lambda said: isinstance(said, dict) and said)(local.ask("line_keep", key=handed["key"])), "the program to keep the line")
            assert [line["id"] for line in kept["lines"] if line["current"]] == ["cloud"] and config()["Mode"] == "cloud", kept
            time.sleep(3)           # the relay is started again behind the tunnel
            assert isinstance(local.about(), dict) and tunnels() == before

            # ---- and back to a relay: the program's own relay and the tunnel are ended once the phone is there
            handed = turn(local, by_port[FIRST], origin(FIRST))
            assert first.sign_in(ticket=handed["ticket"]) and isinstance(first.about(), dict)
            assert [line["id"] for line in first.ask("line_keep", key=handed["key"])["lines"] if line["current"]] == [by_port[FIRST]]
            until(lambda: not listening(LOCAL), "the program's own relay to end", 20)
            assert (config()["Mode"], config()["OwnServer"]) == ("own", origin(FIRST)), config()
            until(lambda: tunnels() == before - 1, "the tunnel to end", 20)
            if len(sys.argv) > 1:
                first.password = passwords[FIRST]
                pages(sys.argv[1], Path(sys.argv[2]) if len(sys.argv) > 2 else None, folder, first, [origin(FIRST), origin(SECOND)], config)
            print("the phone and the program itself: the lines and settings are told without an address; a setting is changed and a wrong one refused; "
                  "a line is made ready beside the one in use, handed over with a sign-in that works once and kept when the phone arrives; "
                  "without the phone the program returns to the line it had; the tunnel, the local network and a relay follow one another; an earlier relay is not turned to")
        finally:
            if program:
                subprocess.run(["taskkill", "/F", "/T", "/PID", str(program.pid)], capture_output=True)
            for relay in relays:
                relay.kill()
            time.sleep(1)


if __name__ == "__main__":
    main()
