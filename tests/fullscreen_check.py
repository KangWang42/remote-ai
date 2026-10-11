"""A fullscreen program on the phone, end to end: the real relay, the real agent and the terminal page in Chrome,
driven by touch. Build the agent first.

    python tests/fullscreen_check.py <chrome.exe> [output folder] [--real]

A stand-in (tests/fullscreen_standin.py) takes the place of Codex; --real starts the Codex installed on this
computer instead and checks the scrolling only. What is checked:

- a drag moves the program's own list by the line: wheel reports reach it, page keys do not;
- while a finger scrolls, what is on the screen is the terminal's own rows, pixel for pixel, each where it belongs
  (screenshots are compared), and they are moved without anything being drawn;
- "回到最新" leads back to the end;
- after a long run the page is read from the program's last output only, still knows that the program is
  fullscreen and reads the mouse, and shows the whole picture, the part the program drew long ago included.

Uses a temporary data folder and port 8743 (RCLI_TEST_PORT changes it), on a screen as dense as a phone's
(RCLI_TEST_DENSITY changes it; 2.625 unless set). Needs Pillow, to read the screenshots.
"""
import base64
import io
import http.client
import json
import os
import secrets
import subprocess
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ws_client import Socket
from PIL import Image, ImageChops

ROOT = Path(__file__).resolve().parents[1]
PORT = int(os.environ.get("RCLI_TEST_PORT", "8743"))
password = "test-" + secrets.token_hex(8)
new_id = lambda: secrets.token_hex(16)


def main():
    real = "--real" in sys.argv
    args = [a for a in sys.argv[1:] if a != "--real"]
    chrome, out = args[0], Path(args[1]) if len(args) > 1 else None
    temp = tempfile.TemporaryDirectory()
    data, profile, work, tools = (Path(temp.name) / name for name in ("agent", "profile", "demo", "tools"))
    for made in (data, work, tools):
        made.mkdir()
    environment = dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1")
    if not real:
        (tools / "codex.cmd").write_text('@"%s" "%s" %%*\r\n' % (sys.executable, ROOT / "tests" / "fullscreen_standin.py"), encoding="mbcs")
        environment["PATH"] = str(tools) + os.pathsep + environment["PATH"]
    (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % PORT, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["demo=" + str(work)]}), encoding="utf-8")
    relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(PORT), "--data", str(Path(temp.name) / "relay")], env=environment)
    agent_exe = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
    subprocess.run([str(agent_exe), "--set-password", str(data)], input=password.encode(), check=True)
    agent = subprocess.Popen([str(agent_exe), "--data", str(data)], env=environment)
    browser = None
    report = {"program": "codex" if real else "stand-in"}
    try:
        cookie = {}

        def call(path, payload=None):
            connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=30)
            body = None if payload is None else json.dumps(payload).encode()
            connection.request("POST" if body is not None else "GET", path, body=body, headers=dict(cookie, **({"Content-Type": "application/json"} if body is not None else {})))
            reply = connection.getresponse()
            text = reply.read()
            if reply.getheader("Set-Cookie"):
                cookie["Cookie"] = reply.getheader("Set-Cookie").split(";")[0]
            assert reply.status == 200, (path, reply.status, text[:200])
            return json.loads(text)

        def op(**payload):
            payload["id"] = new_id()
            for _ in range(80):
                result = call("/api/terminal", payload)
                if result["state"] != "queued":
                    break
                time.sleep(0.25)
            assert result["state"] == "done", result
            return result

        for _ in range(40):
            try:
                call("/api/login", {"password": password})
                break
            except OSError:
                time.sleep(0.5)
        for _ in range(80):
            device = call("/api/terminal")["device"]
            if device.get("workspaces") and "codex" in device.get("tools", []):
                break
            time.sleep(0.5)
        else:
            raise AssertionError("the agent did not report Codex: %s" % device)
        terminal = op(action="start", tool="codex", dir="demo", history=False)["terminal"]

        # A screen as dense as a phone's: about two and a half to three of its pixels to one of the page's, seldom a
        # whole number. The terminal measures its picture in the screen's pixels, which an emulated density does not change.
        browser = subprocess.Popen([chrome, "--headless=new", "--force-device-scale-factor=" + os.environ.get("RCLI_TEST_DENSITY", "2.625"), "--enable-unsafe-swiftshader", "--hide-scrollbars", "--no-first-run", "--user-data-dir=" + str(profile),
                                    "--remote-debugging-port=%d" % (PORT + 1), "--remote-allow-origins=*", "--window-size=412,892", "about:blank"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(60):
            try:
                connection = http.client.HTTPConnection("127.0.0.1", PORT + 1, timeout=5)
                connection.request("PUT", "/json/new?about:blank")
                page = json.loads(connection.getresponse().read())
                break
            except (OSError, ValueError):
                time.sleep(0.5)
        pipe = Socket("http://127.0.0.1:%d" % (PORT + 1), "/devtools/page/" + page["id"], "", 20)
        sent, said = [0], []

        def ask(method, **params):
            sent[0] += 1
            pipe.send({"id": sent[0], "method": method, "params": params})
            while True:
                message = pipe.receive(30)
                if message.get("method") == "Runtime.exceptionThrown":
                    said.append(str(message["params"]["exceptionDetails"].get("exception", {}).get("description", message["params"]["exceptionDetails"].get("text")))[:300])
                if message.get("id") == sent[0]:
                    assert "error" not in message, message
                    return message.get("result", {})

        def page_value(expression):
            return ask("Runtime.evaluate", expression=expression, returnByValue=True, awaitPromise=True).get("result", {}).get("value")

        def until(expression, seconds=40, what=""):
            deadline = time.time() + seconds
            while time.time() < deadline:
                if page_value(expression) is True:
                    return
                time.sleep(0.2)
            raise AssertionError("never true: %s; screen:\n%s\n%s" % (what or expression, page_value(SCREEN), said[:3]))

        def picture(name):
            data = base64.b64decode(ask("Page.captureScreenshot", format="png")["data"])
            if out:
                out.mkdir(parents=True, exist_ok=True)
                (out / name).write_bytes(data)
            return Image.open(io.BytesIO(data)).convert("RGB")

        def rows_seen(before, was, now, state):
            """Two screenshots and what the page said of each (PLACED): `before` with the terminal at rest, `now`
            with the list anywhere. Every line of the list should be seen in `now` where it was in `before`, moved by
            as far as the picture says it stands. Returns the share of the pixels that are the same where lines of
            `before` should be seen again, how many rows that is, and the share of dark pixels in the whole list
            (a light skin: text is a small part of it; a black patch is not)."""
            assert not was["shown"], was
            high, top, tall = state["high"], round(state["first"]), state["rows"] * state["high"]
            left, wide = round(state["left"]), round(state["wide"])
            moved = ((state["base"] - state["place"]) if state["shown"] else 0) + was["line"] - state["line"]      # rows down
            same = rows = 0
            for more in (-1, 0, 1):             # the picture stands on whole pixels: which of two, at a half, is not said
                by = round(moved * high) + more
                first, last = max(top, top + by), min(top + tall, top + tall + by)
                if last - first < high:
                    continue
                # the left part: the button "回到最新" lies over the right of the list in one of the two only
                a = before.crop((left, first - by, left + wide * 6 // 10, last - by))
                b = now.crop((left, first, left + wide * 6 // 10, last))
                other = sum(ImageChops.difference(a, b).convert("L").point(lambda v: 255 if v else 0).histogram()[255:])
                share = 1 - other / (a.width * a.height)
                if share > same:
                    same, rows = share, (last - first) // high
            grey = now.crop((left, top, left + wide, top + tall)).convert("L").histogram()
            return same, rows, sum(grey[:70]) / sum(grey)

        SCREEN = "(() => { const t = TerminalUI.terminal, b = t.buffer.active, rows = []; for (let y = 0; y < t.rows; y++) rows.push(b.getLine(b.baseY + y).translateToString(true)); return rows.join('\\n'); })()"
        READY = "!!window.TerminalUI && TerminalUI.terminal.buffer.active.type === 'alternate' && TerminalUI.terminal.modes.mouseTrackingMode !== 'none'"
        TOP = "Number((/top=(\\d+)/.exec(" + SCREEN + ") || [0, -1])[1])"
        COUNT = lambda name: "Number((/" + name + "=(\\d+)/.exec(" + SCREEN + ") || [0, -1])[1])"

        # Watches the strip while a finger scrolls. Every frame: how far between two rows the picture stands, whether it
        # is shown, how many of its rows have nothing on them. Every time the terminal draws; every time the strip
        # asks anything of the graphics card (a copy of the terminal's picture, or something drawn); and anything
        # painted on a 2D canvas, by anyone. `glides`: frames in which the picture stood elsewhere than in the frame
        # before, and none of that had happened.
        WATCH = """(() => {
          const t = TerminalUI.terminal, slide = TerminalUI.slide, strip = TerminalUI.strip, seen = window.__watched = { draws: 0, copies: 0, calls: 0, frames: 0, glides: 0 };
          window.__slid = [];
          t.onRender(() => { if (slide.active) seen.draws++; });
          const card = WebGL2RenderingContext.prototype, flat = CanvasRenderingContext2D.prototype;
          for (const [name, key] of [['texSubImage2D', 'copies'], ['texImage2D', 'copies'], ['drawArrays', ''], ['clear', '']]) { const real = card[name]; card[name] = function () { if (this === strip.gl) { seen.calls++; if (key) seen[key]++; } return real.apply(this, arguments); }; }
          for (const name of ['drawImage', 'fillRect', 'clearRect', 'putImageData']) { const real = flat[name]; flat[name] = function () { seen.calls++; return real.apply(this, arguments); }; }
          let before = null;
          (function look() {
            window.__slid.push(strip.shown ? [strip.between(), true, strip.blank()] : [0, false, 0]);
            if (strip.shown) { seen.frames++; const now = [strip.place, seen.calls]; if (before && now[0] !== before[0] && now[1] === before[1]) seen.glides++; before = now; } else before = null;
            if (window.__slid.length < 1500) requestAnimationFrame(look);
          })();
          return 1;
        })()"""
        # Where the list is on the screen, in the screen's pixels, and where the picture stands: `first` the top of the
        # first row that moves, `rows` how many move, `high` a row's height, `line` the number of the list's first line
        # as the program has drawn it, `base` the place the terminal's first row has and `place` the one shown there.
        PLACED = """(() => {
          const t = TerminalUI.terminal, slide = TerminalUI.slide, strip = TerminalUI.strip, d = devicePixelRatio;
          const canvas = document.querySelector('#terminal .xterm-screen > canvas:not(.xterm-link-layer)'), r = canvas.getBoundingClientRect();
          const high = Math.round(canvas.height / t.rows), above = canvas.height - t.rows * high;
          return { shown: strip.shown, base: slide.base, place: strip.place, high, rows: t.rows - slide.top - slide.bottom, still: [slide.top, slide.bottom],
            left: r.left * d, wide: r.width * d, first: r.top * d + above + slide.top * high, line: LINE };
        })()""".replace("LINE", TOP)
        ask("Page.enable")
        ask("Runtime.enable")
        ask("Emulation.setDeviceMetricsOverride", width=412, height=892, deviceScaleFactor=0, mobile=True)        # the density is the browser's own, see where it is started
        ask("Emulation.setTouchEmulationEnabled", enabled=True, maxTouchPoints=1)
        base = "http://127.0.0.1:%d" % PORT
        ask("Page.navigate", url=base + "/#p=" + password)
        time.sleep(2.5)

        def open_terminal():
            ask("Page.navigate", url=base + "/terminal/?id=" + terminal)
            until(READY, 60, "the page knows the program is fullscreen and reads the mouse")

        def drag(lines, steps=24, pause=0.016, halfway=None):
            """One finger moves down (lines > 0: towards older output) by that many rows of the terminal, then lifts."""
            box = page_value("(() => { const r = document.getElementById('screen').getBoundingClientRect(), t = TerminalUI.terminal; return [r.left + r.width / 2, r.top + r.height / 2, document.getElementById('terminal').clientHeight / t.rows]; })()")
            x, y, row = box
            ask("Input.dispatchTouchEvent", type="touchStart", touchPoints=[{"x": x, "y": y}])
            for step in range(1, steps + 1):
                time.sleep(pause)
                ask("Input.dispatchTouchEvent", type="touchMove", touchPoints=[{"x": x, "y": y + lines * row * step / steps}])
                if halfway and step == steps // 2:
                    halfway()
            time.sleep(0.25)        # the finger rests before it lifts: no fling
            ask("Input.dispatchTouchEvent", type="touchEnd", touchPoints=[])
            time.sleep(0.8)

        def tap(name):
            """One finger touches the middle of that element and lifts. Returns what the tap pressed and what took
            the focus by it: a tap that lands on the terminal under a button opens the phone's keyboard instead."""
            page_value("(() => { window.__tapped = []; if (!window.__taps) { window.__taps = true; for (const kind of ['click', 'focusin']) document.addEventListener(kind, e => window.__tapped.push(kind + ' ' + (e.target.id || e.target.className || e.target.tagName)), true); } return 1; })()")
            x, y = page_value("(() => { const r = document.getElementById('%s').getBoundingClientRect(); return [r.left + r.width / 2, r.top + r.height / 2]; })()" % name)
            ask("Input.dispatchTouchEvent", type="touchStart", touchPoints=[{"x": x, "y": y}])
            time.sleep(0.06)
            ask("Input.dispatchTouchEvent", type="touchEnd", touchPoints=[])
            time.sleep(0.3)
            return page_value("window.__tapped")

        open_terminal()
        if real:
            # Codex's own list: something to scroll through first, made without asking the model anything.
            until("/Ask Codex|for shortcuts|OpenAI Codex|Update available/.test(" + SCREEN + ")", 60, "Codex has started")
            if "Update available" in page_value(SCREEN):        # not now: the installed Codex is left as it is
                op(action="input", terminal=terminal, data="\x1b")
                until("/Ask Codex|for shortcuts|OpenAI Codex/.test(" + SCREEN + ")", 60, "Codex has started")
            time.sleep(1.5)
            for n in range(1, 13):
                op(action="input", terminal=terminal, data="!echo mark%02d" % n)
                time.sleep(0.4)
                op(action="input", terminal=terminal, data="\r")
                time.sleep(0.9)
            until("/mark12/.test(" + SCREEN + ")", 30, "the last command is on the screen")
            before = page_value(SCREEN)
            picture("codex-before.png")
            drag(6)
            after = page_value(SCREEN)
            picture("codex-dragged.png")
            rows_before, rows_after = before.split("\n"), after.split("\n")
            moved = next((move for move in range(1, 20) if sum(1 for y in range(len(rows_before) - 8) if y + move < len(rows_after) - 6 and rows_before[y].strip() and rows_before[y] == rows_after[y + move]) >= 5), 0)
            report["codex_rows_moved_by_a_6_row_drag"] = moved
            assert 4 <= moved <= 9, (moved, before, after)
            assert page_value("document.getElementById('latest').hidden") is False
            assert tap("latest") == ["click latest"]
            time.sleep(1.2)
            assert "mark12" in page_value(SCREEN), page_value(SCREEN)
            # a long run of drawing, then the page is opened again: it reads the last output only and shows the same picture
            for _ in range(14):
                for button in (64, 65):
                    op(action="input", terminal=terminal, data=("\x1b[<%d;10;10M" % button) * 12)
                    time.sleep(0.12)
            time.sleep(1.5)
            settled = page_value(SCREEN).split("\n")
            fresh = call("/api/terminal?terminal=%s&after=0" % terminal)
            report["output_kept"], report["first_piece_read_by_a_new_page"] = fresh["terminal"]["seq"], fresh["chunks"][0]["seq"]
            assert fresh["reset"] and fresh["chunks"][0]["seq"] > 1, (fresh["reset"], fresh["chunks"][0]["seq"])
            open_terminal()
            time.sleep(3)
            again = page_value(SCREEN).split("\n")
            same = sum(1 for a, b in zip(settled, again) if a.rstrip() == b.rstrip())      # blank cells and no cells look the same
            report["rows_the_same_after_reopening"] = "%d / %d" % (same, len(settled))
            picture("codex-reopened.png")
            assert len(again) == len(settled) and same >= len(settled) - 2, "\n".join(settled) + "\n----\n" + "\n".join(again)
            assert not said, said
            print(json.dumps(report, ensure_ascii=False))
            return

        OWN_SIZE = SCREEN + ".split(String.fromCharCode(10))[0] === 'FULLSCREEN STAND-IN ' + TerminalUI.terminal.cols + 'x' + TerminalUI.terminal.rows"
        until(OWN_SIZE + " && /line 299/.test(" + SCREEN + ")", 40, "the stand-in has drawn itself for the size of this page")
        top = page_value(TOP)
        picture("fullscreen.png")
        # ---- a drag moves the list by the line
        drag(10)
        moved = top - page_value(TOP)
        report["rows_moved_by_a_10_row_drag"] = moved
        report["wheel_reports"], report["page_keys"] = page_value(COUNT("wheel")), page_value(COUNT("page"))
        # the program was kept some rows ahead of the finger while it moved and has come back by as many: it stands
        # where the finger left it, and was sent that many wheel reports more, half of them each way
        beyond = report["wheel_reports"] - moved
        assert 9 <= moved <= 10 and 0 <= beyond <= 24 and beyond % 2 == 0 and report["page_keys"] == 0, report
        assert "other= " in page_value(SCREEN) + " " or "other=\n" in page_value(SCREEN) + "\n", page_value(SCREEN)      # nothing arrived as typed characters
        assert page_value("document.getElementById('latest').hidden") is False
        drag(-4)                    # a whole row is sent once the finger has passed it: the last one may still be under way
        assert moved - 4 <= top - page_value(TOP) <= moved - 3, (top, page_value(TOP), moved)
        # ---- a slow drag is followed by the pixel: between the program's moves the picture is slid under the finger,
        # the rows the program holds still stay where they are, and at rest the picture is where the terminal draws it
        page_value(WATCH)
        rest, resting = picture("fullscreen-at-rest.png"), page_value(PLACED)
        # a finger comes down and moves a little, less than makes the program move: what is on the screen is the same
        # rows, a few pixels further down, with nothing lying over them
        x, y = page_value("(() => { const r = document.getElementById('screen').getBoundingClientRect(); return [r.left + r.width / 2, r.top + r.height / 2]; })()")
        ask("Input.dispatchTouchEvent", type="touchStart", touchPoints=[{"x": x, "y": y}])
        for far in (30, 33):                # the first move says where the finger is, the second moves the picture
            time.sleep(0.05)
            ask("Input.dispatchTouchEvent", type="touchMove", touchPoints=[{"x": x, "y": y + far}])
        time.sleep(0.4)
        touched = page_value(PLACED)
        same, rows, dark = rows_seen(rest, resting, picture("fullscreen-touched.png"), touched)
        report["touched"] = {"shown": touched["shown"], "rows_down": round(touched["base"] - (touched["place"] or 0), 2), "same": round(same, 4), "rows_compared": rows, "dark": round(dark, 3)}
        assert touched["shown"] and 0 < touched["base"] - touched["place"] < 0.5 and touched["line"] == resting["line"], (touched, resting)
        assert same > 0.999 and rows >= touched["rows"] - 1 and dark < 0.2, report
        ask("Input.dispatchTouchEvent", type="touchEnd", touchPoints=[])
        time.sleep(0.8)
        assert page_value("document.getElementById('slid').hidden") is True and page_value(TOP) == resting["line"]
        slow = page_value(TOP)
        halfway = []

        def look():
            time.sleep(0.4)                 # what the program was asked for has arrived: the picture stands
            state = page_value(PLACED)
            halfway.extend([picture("fullscreen-sliding.png"), state, page_value(PLACED)])

        drag(5, steps=75, pause=0.02, halfway=look)
        time.sleep(0.6)
        samples = page_value("window.__slid.splice(0)")
        places = [sample[0] for sample in samples]
        report["frames_seen"], report["frames_slid"], report["places_between_rows"] = len(places), sum(1 for place in places if place), len(set(places))
        report["drawn_over_the_terminal"] = any(sample[1] for sample in samples)
        report["rows_of_nothing_in_a_slow_drag"] = max(sample[2] for sample in samples)
        report["rows_moved_by_a_slow_5_row_drag"] = slow - page_value(TOP)
        assert report["places_between_rows"] >= 15 and report["drawn_over_the_terminal"], report
        # rows come into sight at the edge as the finger moves: at no time is a row's height of the list without text
        assert report["rows_of_nothing_in_a_slow_drag"] < 1, report
        assert places[-1] == 0 and page_value("document.getElementById('slid').hidden") is True, places[-5:]
        # halfway, with the program some rows ahead of the finger and the picture between two rows: the lines that
        # were on the screen at rest are where they belong, pixel for pixel; the rows the program holds still are found
        sliding, state, after = halfway
        assert state == after and state["shown"], (state, after)
        same, rows, dark = rows_seen(rest, resting, sliding, state)
        report["sliding"] = {"rows_down": round(state["base"] - state["place"] + resting["line"] - state["line"], 2), "still": state["still"], "same": round(same, 4), "rows_compared": rows, "dark": round(dark, 3)}
        assert state["still"] == [1, 1], state
        assert same > 0.999 and rows >= state["rows"] - 4 and dark < 0.2, report
        # moving the picture draws nothing: the terminal's picture was copied once each time the terminal drew, the
        # strip drawn from it then, and in the many frames between it was only moved
        watched = page_value("window.__watched")
        report["density"], report["picture"] = page_value("devicePixelRatio"), page_value("(() => { const s = TerminalUI.strip; return [s.high, s.above, s.places, s.spare]; })()")
        report["times_the_terminal_drew"], report["copies_of_its_picture"], report["asked_of_the_card"] = watched["draws"], watched["copies"], watched["calls"]
        report["frames_shown"], report["frames_moved_without_drawing"] = watched["frames"], watched["glides"]
        assert 0 < watched["copies"] <= watched["draws"] and watched["calls"] <= 8 * watched["draws"] + 16 and watched["glides"] >= 15, report
        assert 4 <= report["rows_moved_by_a_slow_5_row_drag"] <= 5, report
        drag(-5)
        # a quick flick of about ten rows glides on after the finger lifts, as far as its speed carries it (the speed
        # fades with the scroller's GLIDE, 325 ms), comes to rest, and a touch stops a glide at once. How quick the
        # finger is depends on this computer's timer: the speed is taken from when the page saw the finger move.
        was = page_value(TOP)
        box = page_value("(() => { const r = document.getElementById('screen').getBoundingClientRect(), t = TerminalUI.terminal; return [r.left + r.width / 2, r.top + r.height / 2, document.getElementById('terminal').clientHeight / t.rows]; })()")
        page_value("(() => { window.__moves = []; document.addEventListener('touchmove', () => window.__moves.push(performance.now()), { capture: true, passive: true }); return 1; })()")
        ask("Input.dispatchTouchEvent", type="touchStart", touchPoints=[{"x": box[0], "y": box[1]}])
        for step in range(1, 7):
            time.sleep(0.016)
            ask("Input.dispatchTouchEvent", type="touchMove", touchPoints=[{"x": box[0], "y": box[1] + step * 30}])
        ask("Input.dispatchTouchEvent", type="touchEnd", touchPoints=[])
        time.sleep(2.8)
        moves = page_value("window.__moves")
        speed = min(2.5, 30 * (len(moves) - 1) / (moves[-1] - moves[0]))            # pixels a millisecond
        dragged, carried = 180 / box[2], speed * 325 / box[2]
        report["rows_moved_by_a_flick"], report["rows_its_speed_carries_it"] = was - page_value(TOP), round(dragged + carried, 1)
        assert carried >= 5 and abs(report["rows_moved_by_a_flick"] - dragged - carried) <= 2 + carried * .25, report
        still = page_value(TOP)
        time.sleep(0.6)
        assert page_value(TOP) == still
        ask("Input.dispatchTouchEvent", type="touchStart", touchPoints=[{"x": box[0], "y": box[1]}])
        for step in range(1, 7):
            time.sleep(0.016)
            ask("Input.dispatchTouchEvent", type="touchMove", touchPoints=[{"x": box[0], "y": box[1] - step * 30}])
        ask("Input.dispatchTouchEvent", type="touchEnd", touchPoints=[])
        time.sleep(0.15)
        ask("Input.dispatchTouchEvent", type="touchStart", touchPoints=[{"x": box[0], "y": box[1]}])        # a finger put down on the moving picture
        time.sleep(0.3)
        HELD = "(() => { const s = TerminalUI.slide; return [TerminalUI.strip.place, s.active, s.base, s.offset, s.asked.length, s.led]; })()"
        place = page_value(HELD)
        time.sleep(0.8)
        assert place[0] is not None and page_value(HELD)[0] == place[0], ("the picture moved on under a finger that held it", place, page_value(HELD))
        ask("Input.dispatchTouchEvent", type="touchEnd", touchPoints=[])
        time.sleep(1.2)             # the program comes back the rows it was kept ahead of the finger by
        held = page_value(TOP)
        report["rows_of_a_flick_stopped_by_a_touch"] = held - still
        assert 0 < held - still < report["rows_moved_by_a_flick"], (still, held)
        picture("dragged.png")
        # the finger has only just lifted from the picture it held: the tap must reach the button all the same
        assert tap("latest") == ["click latest"]
        until(TOP + " === %d" % top, 10, "back at the end")
        assert page_value("document.getElementById('latest').hidden") is True

        # ---- a phone on which the strip cannot be had: the graphics card gives it nothing, or takes away what it had.
        # The list is scrolled all the same, by the row, with the terminal seen as it draws itself; and once the card is
        # back, so is the strip.
        shown = []
        for what, change in (("no card for the strip", "(() => { const s = TerminalUI.strip; s.canvas.getContext = () => null; s.gl = null; s.forget(); return 1; })()"),
                             ("the card is there", "(() => { delete TerminalUI.strip.canvas.getContext; return 1; })()"),
                             ("the card takes back what the strip had", "(() => { window.__lose = TerminalUI.strip.gl.getExtension('WEBGL_lose_context'); window.__lose.loseContext(); return 1; })()"),
                             ("and gives it again", "(() => { window.__lose.restoreContext(); return 1; })()")):
            page_value(change)
            time.sleep(0.5)
            was, seen = page_value(TOP), []
            drag(3 if len(shown) % 2 == 0 else -3, halfway=lambda: seen.append(page_value("TerminalUI.strip.shown")))
            shown.append([what, seen[0], was - page_value(TOP)])
        report["without_the_strip"] = shown
        assert [one[1] for one in shown] == [False, True, False, True], shown
        assert all(2 <= abs(one[2]) <= 3 and (one[2] > 0) == (n % 2 == 0) for n, one in enumerate(shown)), shown
        assert not said, said
        # a phone whose browser takes long over every copy of the terminal's picture: after some of them the strip
        # gives up, and the list is scrolled by the row
        page_value("(() => { const gl = TerminalUI.strip.gl, real = gl.texSubImage2D.bind(gl); gl.texSubImage2D = (...all) => { const until = performance.now() + 9; while (performance.now() < until); return real(...all); }; return 1; })()")
        was = page_value(TOP)
        drag(14, steps=60)
        report["cost_of_a_copy_made_slow"] = [round(page_value("TerminalUI.strip.cost"), 1), page_value("TerminalUI.strip.copies"), page_value("TerminalUI.strip.slow")]
        # (the strip was given up halfway through this drag: the rows the program was ahead by have been asked back)
        assert report["cost_of_a_copy_made_slow"][2] is True and 12 <= was - page_value(TOP) <= 14, (report, was, page_value(TOP))
        was, seen = page_value(TOP), []
        drag(-14, steps=60, halfway=lambda: seen.append(page_value("TerminalUI.strip.shown")))
        assert seen == [False] and 12 <= page_value(TOP) - was <= 14, (seen, was, page_value(TOP))
        assert not said, said
        # ---- after a long run: the last output only, the switches, and the whole picture
        op(action="input", terminal=terminal, data="f")
        until(COUNT("flood") + " === 600", 60, "the long run is over")
        fresh = call("/api/terminal?terminal=%s&after=0" % terminal)
        size = sum(len(c["data"]) for c in fresh["chunks"])
        report["output_kept"], report["output_read_by_a_new_page"] = fresh["terminal"]["seq"], size
        assert fresh["reset"] and fresh["chunks"][0]["seq"] > 1 and size < 200_000, (fresh["reset"], fresh["chunks"][0]["seq"], size)
        lead = fresh["chunks"][0]["data"][:80]
        assert lead.startswith("\x1b[?") and "\x1b[?1049h" in lead and "\x1b[?1006h" in lead, lead
        started = time.time()
        open_terminal()
        until("/FULLSCREEN STAND-IN/.test(" + SCREEN + ") && /flood=600/.test(" + SCREEN + ")", 30, "the whole picture is back, the heading drawn long ago included")
        report["seconds_to_the_whole_picture"] = round(time.time() - started, 1)
        # the size is the page's own again, not the one asked for in between
        time.sleep(1.5)
        sizes = page_value("TerminalUI.terminal.cols + 'x' + TerminalUI.terminal.rows")
        now = call("/api/terminal?terminal=%s&after=%d" % (terminal, fresh["terminal"]["seq"]))["terminal"]
        assert "%dx%d" % (now["cols"], now["rows"]) == sizes, (now["cols"], now["rows"], sizes)
        assert page_value(OWN_SIZE) is True, page_value(SCREEN)
        top = page_value(TOP)
        wheel = page_value(COUNT("wheel"))
        drag(5)
        assert 4 <= top - page_value(TOP) <= 5 and 0 <= page_value(COUNT("wheel")) - wheel - (top - page_value(TOP)) <= 24 and page_value(COUNT("page")) == 0, page_value(SCREEN)
        picture("reopened.png")
        assert not said, said
        op(action="input", terminal=terminal, data="q")
        print(json.dumps(report, ensure_ascii=False))
    finally:
        if browser:
            browser.kill()
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(agent.pid)], capture_output=True)       # with the programs in its terminals
        agent.kill(); relay.kill()
        time.sleep(1)
        try:
            temp.cleanup()
        except OSError:
            pass


if __name__ == "__main__":
    main()
