"""Draws the pictures of the phone's pages for the README into docs/images/: the real embedded pages (web/) and the
real relay, with a computer that is played here and conversations that are made up, so nothing of this computer's
is drawn into a picture. Then sets them into the explained figures (docs/images/src/*.html) and draws those.

    python tools/readme_pictures.py <chrome.exe> [only: shots | terminal | figures]

The Windows program's own pages are drawn by tools/window_pictures.py, the terminal page by tests/pages_check.py.
"""
import base64
import http.client
import http.server
import json
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "relay"))
sys.path.insert(0, str(ROOT / "tests"))
from relay import Relay                 # noqa: E402
from ws_client import Socket            # noqa: E402

OUT = ROOT / "docs" / "images"
SRC = OUT / "src"
PHONE = dict(width=390, height=844, deviceScaleFactor=2, mobile=True)
uuid = lambda n: "0199aaaa-bbbb-7ccc-8ddd-%012d" % n
ASKING, FIXING, THESIS, TABLES, EXPORT, DRAFTED = (uuid(n) for n in range(1, 7))


def story(now):
    """The computer as it is played: three projects, conversations of both tools in the places they run in."""
    live = dict(live=True, updated=now, can_takeover=False, ownership_known=True)
    app = "这段对话由电脑上的应用管理，请在那里关闭它"
    sessions = [
        dict(live, id=ASKING, tool="codex", dir="web-shop", title="部署到测试环境", origin="命令行", kind="终端", host="cli", status="confirm", now=True, can_takeover=True,
             reach=["say", "answer"], transport="", ask={"id": "a1b2c3d4e5f6", "text": "Deploy to staging\nBash：npm run deploy:staging"},
             ways={"say": "hook", "stop": "", "answer": "hook", "end": "process"}, why={}),
        dict(live, id=FIXING, tool="claude", dir="web-shop", title="修复结算页的金额显示", origin="Claude 桌面版", kind="Claude 桌面版", host="shared", status="busy", now=True,
             reach=["say", "stop"], transport="hook", last="", ways={"say": "hook", "stop": "hook", "answer": "", "end": ""}, why={"end": app},
             queued=[{"id": "c" * 32, "text": "改完后顺便把优惠券那里的金额也检查一下", "state": "queued", "at": now}]),
        dict(live, id=THESIS, tool="codex", dir="thesis", title="整理第三章的参考文献", origin="Codex 桌面版", kind="Codex 桌面版", host="shared", status="busy", now=True,
             reach=["say", "stop"], transport="codex-desktop", turn="turn-7", ways={"say": "codex-desktop", "stop": "codex-desktop", "answer": "", "end": ""}, why={"end": app}),
        dict(live, id=TABLES, tool="claude", dir="thesis", title="核对回归表的系数", origin="Positron", kind="终端", host="cli", status="idle", now=False, can_takeover=True,
             updated=now - 9 * 60000, reach=["say"], wake=True, transport="", last="三处系数与正文不一致，已列在 notes.md。", ways={"say": "wake", "stop": "", "answer": "", "end": "process"}, why={}),
        dict(live, id=EXPORT, tool="codex", dir="data-tools", title="把导出脚本改成增量", origin="命令行", kind="终端", host="cli", status="idle", now=False, can_takeover=True,
             updated=now - 26 * 60000, reach=["say"], wake=True, transport="", last="增量导出已通过三组样例。", ways={"say": "wake", "stop": "", "answer": "", "end": "process"}, why={}),
        dict(id=uuid(11), tool="claude", dir="web-shop", title="给结算页加上优惠券", updated=now - 3 * 3600000, live=False),
        dict(id=uuid(12), tool="codex", dir="data-tools", title="清理重复的导入记录", updated=now - 26 * 3600000, live=False),
        dict(id=uuid(13), tool="claude", dir="thesis", title="摘要翻译", updated=now - 70 * 3600000, live=False),
    ]
    said = {FIXING: [
        {"role": "user", "text": "结算页的金额有时多出一位小数，帮我看看"},
        {"role": "thought", "text": "先确认金额是在哪一步换算的，再对比两版的输出。"},
        {"role": "tool", "text": "Bash：对比两版的金额", "detail": "node compare.js --old 2.0 --new 4.0", "result": "2.0: 12.50\n4.0: 12.5\ndiffers in 3 of 40 rows"},
        {"role": "tool", "text": "Read：src/cart/formatPrice.js"},
        {"role": "assistant", "text": "问题在 `formatPrice`：金额从分换算成元之后没有固定两位小数。我来改，并补一条用例。"},
        {"role": "tool", "text": "Edit：src/cart/formatPrice.js"},
        {"role": "tool", "text": "Bash：运行测试", "detail": "npm test -- cart", "result": "PASS  cart/formatPrice.test.js\n  keeps two decimals (3 ms)\nTests: 14 passed, 14 total"},
    ]}
    return sessions, said


class Played:
    """The relay as the phone's pages reach it, and the computer behind it, played."""

    def __init__(self, temporary):
        self.relay = Relay(str(Path(temporary) / "relay.json"))
        self.now = int(time.time() * 1000)
        self.info = {"instance": "d" * 32, "enabled": True, "tools": ["claude", "codex", "shell"], "workspaces": ["web-shop", "thesis", "data-tools"], "name": "办公室的电脑",
                     "features": ["peek", "reach", "end", "outbox", "marks", "files", "projects", "codex-desktop-control", "codex-desktop-open", "claude-desktop-open"]}
        self.sessions, self.said = story(self.now)
        played = self

        class Handler(http.server.SimpleHTTPRequestHandler):
            def __init__(self, *args, **kwargs):
                super().__init__(*args, directory=str(ROOT / "web"), **kwargs)

            def log_message(self, *args):
                pass

            def reply(self, value, status=200):
                body = json.dumps(value, ensure_ascii=False).encode("utf-8")
                self.send_response(status); self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)

            def do_GET(self):
                if self.path == "/api/session":
                    return self.reply({"signed_in": True})
                if self.path == "/api/terminal":
                    played.relay.agent({"info": played.info, "sessions": played.sessions})
                    return self.reply(played.relay.overview())
                if self.path == "/favicon.ico":
                    self.send_response(204); self.end_headers(); return
                super().do_GET()

            def do_POST(self):
                payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                if self.path == "/api/conversation" and payload.get("action", "read") == "read":
                    one = next(s for s in played.sessions if s["id"] == payload["session"])
                    return self.reply({"messages": played.said.get(one["id"], []), "status": one.get("status", ""), "stamp": "1", "asked": played.now - 84000, "updated": one["updated"]})
                self.reply({"error": "这里只是画图"}, 400)

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.origin = "http://127.0.0.1:%d" % self.server.server_address[1]

    def close(self):
        self.server.shutdown(); self.server.server_close()


class Browser:
    """A headless browser, driven over its own debugging pipe: a page at a phone's size, or one as wide as a figure."""

    def __init__(self, chrome, temporary):
        self.process = subprocess.Popen([chrome, "--headless=new", "--disable-gpu", "--no-first-run", "--hide-scrollbars", "--remote-debugging-port=0", "--remote-allow-origins=*",
                                         "--user-data-dir=" + str(Path(temporary) / "browser"), "about:blank"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        active = Path(temporary) / "browser" / "DevToolsActivePort"
        for _ in range(100):
            if active.exists():
                break
            time.sleep(0.1)
        port = int(active.read_text().splitlines()[0])
        connection = http.client.HTTPConnection("127.0.0.1", port)
        connection.request("PUT", "/json/new?about:blank")
        page = json.loads(connection.getresponse().read()); connection.close()
        self.pipe, self.sequence = Socket("http://127.0.0.1:%d" % port, "/devtools/page/" + page["id"], ""), 0
        self.ask("Page.enable"); self.ask("Runtime.enable")

    def ask(self, method, **params):
        self.sequence += 1
        self.pipe.send({"id": self.sequence, "method": method, "params": params})
        while True:
            message = self.pipe.receive(30)
            if message.get("id") == self.sequence:
                assert "error" not in message, message
                return message.get("result", {})

    def value(self, code):
        result = self.ask("Runtime.evaluate", expression=code, returnByValue=True, awaitPromise=True)
        assert "exceptionDetails" not in result, result
        return result.get("result", {}).get("value")

    def until(self, code, what):
        for _ in range(150):
            if self.value(code):
                return
            time.sleep(0.1)
        raise AssertionError(what)

    def open(self, url, ready, what, **screen):
        self.ask("Emulation.setDeviceMetricsOverride", **(screen or PHONE))
        self.ask("Page.navigate", url=url)
        self.until(ready, what)
        time.sleep(0.5)

    def picture(self, path, whole=False, clear=False):
        if clear:
            self.ask("Emulation.setDefaultBackgroundColorOverride", color={"r": 0, "g": 0, "b": 0, "a": 0})
        shot = self.ask("Page.captureScreenshot", format="png", captureBeyondViewport=whole)
        Path(path).write_bytes(base64.b64decode(shot["data"]))

    def close(self):
        self.pipe.close(); self.process.terminate(); self.process.wait(timeout=10)


def shots(browser, played):
    """The phone's pages as they are: the first page, a conversation at work, and the sheet for a new task."""
    browser.open(played.origin + "/", "document.querySelectorAll('.card, .task, article').length > 3 || document.body.innerText.includes('部署到测试环境')", "the first page")
    browser.picture(OUT / "overview.png")
    browser.open(played.origin + "/?take=" + FIXING, "document.querySelector('#sheet .state') && document.querySelector('#sheet .said')?.textContent.includes('formatPrice')", "a conversation's window")
    browser.value("(() => { const said = document.querySelector('#sheet .said'); said.scrollTop = said.scrollHeight; })()")
    time.sleep(0.4)
    browser.picture(OUT / "look.png")
    browser.open(played.origin + "/?new=1", "document.querySelector('#sheet[open] h3')?.textContent === '新建任务'", "the sheet for a new task")
    browser.picture(OUT / "new-task.png")


def terminal(browser, temporary):
    """The terminal page with a terminal that really runs: a relay and the computer's own program on this computer
    for the moment (build it first), in a folder made for it, with homes of the tools that hold nothing."""
    import secrets
    from made_up import Homes
    port, password = 8739, "test-" + secrets.token_hex(8)
    data, project = Path(temporary) / "agent", Path(temporary) / "projects" / "web-shop"
    data.mkdir(); project.mkdir(parents=True)
    homes = Homes(temporary)
    (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % port, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["web-shop=%s" % project]}), encoding="utf-8")
    relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(port), "--data", str(Path(temporary) / "relay-data")],
                             env=dict(homes.environment(), RCLI_PASSWORD=password), stdout=subprocess.DEVNULL)
    program = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
    subprocess.run([str(program), "--set-password", str(data)], input=password.encode(), check=True)
    agent = subprocess.Popen([str(program), "--data", str(data)], env=homes.environment())
    cookie = {}

    def call(path, payload=None):
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=30)
        body = None if payload is None else json.dumps(payload).encode()
        connection.request("POST" if body is not None else "GET", path, body=body, headers=dict(cookie, **({"Content-Type": "application/json"} if body is not None else {})))
        reply = connection.getresponse()
        text = reply.read()
        if reply.getheader("Set-Cookie"):
            cookie["Cookie"] = reply.getheader("Set-Cookie").split(";")[0]
        assert reply.status == 200, (path, reply.status, text[:200])
        return json.loads(text)

    try:
        for _ in range(40):
            try:
                call("/api/login", {"password": password})
                break
            except OSError:
                time.sleep(0.5)
        for _ in range(60):
            if call("/api/terminal")["device"]["workspaces"]:
                break
            time.sleep(0.5)
        started = call("/api/terminal", {"action": "start", "id": secrets.token_hex(16), "tool": "shell", "dir": "web-shop"})["terminal"]
        time.sleep(3.5)
        call("/api/terminal", {"action": "rename", "id": secrets.token_hex(16), "terminal": started, "title": "跑一遍结算页的测试"})
        lines = ("function prompt { 'PS web-shop> ' }; Clear-Host; 'PS web-shop> npm test -- cart'; ''; "
                 "Write-Host ' PASS ' -ForegroundColor Black -BackgroundColor Green -NoNewline; ' cart/formatPrice.test.js'; '   keeps two decimals (3 ms)'; '   rounds half up (1 ms)'; "
                 "Write-Host ' PASS ' -ForegroundColor Black -BackgroundColor Green -NoNewline; ' cart/coupon.test.js'; '   applies one coupon per order (2 ms)'; ''; "
                 "Write-Host 'Tests:       ' -NoNewline; Write-Host '14 passed' -ForegroundColor Green -NoNewline; ', 14 total'; 'Time:        2.41 s'; ''")
        call("/api/terminal", {"action": "input", "id": secrets.token_hex(16), "terminal": started, "data": lines + chr(13)})
        time.sleep(3)
        origin = "http://127.0.0.1:%d" % port
        browser.open(origin + "/#p=" + password, "document.body.innerText.includes('web-shop')", "signed in")
        # (drawn once the terminal holds what was typed: its screen is read from the page, then drawn anew for the picture)
        held = "!!window.TerminalUI && (t => { const b = t.buffer.active; let said = ''; for (let y = 0; y < t.rows; y++) said += b.getLine(b.baseY + y).translateToString(true); return said.includes('14 passed'); })(TerminalUI.terminal)"
        browser.open(origin + "/terminal/?id=" + started, held, "the terminal page with its screen")
        browser.value("(TerminalUI.terminal.refresh(0, TerminalUI.terminal.rows - 1), new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))))")
        time.sleep(2)
        browser.picture(OUT / "terminal.png")
    finally:
        agent.kill(); relay.kill()
        time.sleep(1)


def figures(browser):
    """The explained figures: each a page of its own in docs/images/src (its <main data-width> says how wide it is
    laid out), drawn at twice that size on a clear ground."""
    import re
    for page in sorted(SRC.glob("*.html")):
        width = int(re.search(r'data-width="(\d+)"', page.read_text(encoding="utf-8")).group(1))
        browser.open(page.as_uri(), "document.fonts.status === 'loaded' && Array.from(document.images).every(image => image.complete && image.naturalWidth > 0)", page.name,
                     width=width, height=900, deviceScaleFactor=2, mobile=False)
        high = int(browser.value("Math.ceil(document.querySelector('main').getBoundingClientRect().height)"))
        browser.ask("Emulation.setDeviceMetricsOverride", width=width, height=high, deviceScaleFactor=2, mobile=False)
        time.sleep(0.3)
        browser.picture(OUT / (page.stem + ".png"), clear=True)
        print("figure", page.stem + ".png", "%dx%d" % (width * 2, high * 2))


def main():
    chrome, only = sys.argv[1], (sys.argv[2] if len(sys.argv) > 2 else "")
    with tempfile.TemporaryDirectory(prefix="remote-ai-pictures-", ignore_cleanup_errors=True) as temporary:
        played, browser = Played(temporary), Browser(chrome, temporary)
        try:
            if only in ("", "shots"):
                shots(browser, played)
            if only in ("", "terminal"):
                # (a browser of its own: one that has drawn other pages before leaves the terminal's canvas empty in a picture)
                with tempfile.TemporaryDirectory(prefix="remote-ai-terminal-", ignore_cleanup_errors=True) as apart:
                    fresh = Browser(chrome, apart)
                    try:
                        terminal(fresh, apart)
                    finally:
                        fresh.close()
            if only in ("", "figures"):
                figures(browser)
        finally:
            browser.close(); played.close()


if __name__ == "__main__":
    main()
