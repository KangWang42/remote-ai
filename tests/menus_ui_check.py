"""The menus of a conversation's page and a terminal's page (web/ui/sheet.js, web/ui/work.js), through the real relay
server: the menu opens as one sheet; a Codex conversation is handed to a new Claude Code conversation, which is begun
with what the computer wrote for the hand-over (`brief`); a question is set for it at a time on weekdays and switched
off from its row; the look is changed from the menu; a terminal's menu shows its own choices; the phone's back key
(history) leads back a page and then closes the sheet. The computer is played here, by the relay's own interface for
computers: nothing real is started or told.

Usage: python tests/menus_ui_check.py <Edge/Chrome executable> <screenshot directory>
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
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))
from ws_client import Socket        # noqa: E402

CODEX = "0199cccc-dddd-7eee-8fff-000000000001"
BRIEF = "【对话交接】下面是我在另一段对话里的进展（Codex，“结算页的金额”，项目 web-shop），请接着处理。\n\n我最初的要求：\n结算页的金额有时多出一位小数"
FEATURES = ["chat", "files", "peek", "work", "chains", "brief", "schedule-days"]


def free_port():
    with socket.socket() as reserved:
        reserved.bind(("127.0.0.1", 0))
        return reserved.getsockname()[1]


def http_get(port, path, cookie):
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
    connection.request("GET", path, headers={"Cookie": cookie})
    return connection.getresponse().read()


class Computer:
    """A computer as the relay sees it: its terminals (conversations, and one plain terminal), the conversations it
    lists, and the owner's work it keeps (questions at a time) and does (what a hand-over says)."""

    def __init__(self, port, password):
        self.port, self.cookie, self.instance = port, "", secrets.token_hex(16)
        self.lock = threading.Lock()
        self.terminals, self.output, self.acks, self.seen, self.schedules = {}, [], [], [], []
        self.alive = True
        self.call("/api/login", {"password": password})

    def call(self, path, payload, timeout=30):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=timeout)
        connection.request("POST", path, body=json.dumps(payload).encode(), headers={"Content-Type": "application/json", "Origin": "http://127.0.0.1:%d" % self.port, **({"Cookie": self.cookie} if self.cookie else {})})
        reply = connection.getresponse()
        if reply.getheader("Set-Cookie"):
            self.cookie = reply.getheader("Set-Cookie").split(";")[0]
        return json.loads(reply.read() or b"{}")

    def emit(self, terminal, **event):
        with self.lock:
            term = self.terminals[terminal]
            term["seq"] += 1
            self.output.append({"terminal": terminal, "seq": term["seq"], "data": json.dumps(event, ensure_ascii=False) + "\n"})
            if event["t"] == "state":
                term["status"] = event["state"]

    def report(self):
        with self.lock:
            output, self.output = self.output, []
            acks, self.acks = self.acks, []
            terminals = [{"id": t["id"], "state": t["state"], "tool": t["tool"], "dir": t["dir"], "folder": "", "session": t["session"], "status": t["status"] if t["state"] == "running" else "",
                          "created": t["created"], "title": t["title"], "form": t["form"], "said": "", "ask": "", "cols": 80, "rows": 24} for t in self.terminals.values()]
            sessions = [{"id": t["session"], "tool": t["tool"], "dir": t["dir"], "title": t["title"] or "新的对话", "updated": t["created"], "live": t["state"] == "running"}
                        for t in self.terminals.values() if t["session"]]
        self.call("/api/terminal/agent", {"info": {"instance": self.instance, "enabled": True, "tools": ["claude", "codex", "shell"], "workspaces": ["web-shop"], "features": FEATURES,
                                                    "projects": [{"name": "web-shop", "path": "D:\\work\\web-shop"}]},
                                           "terminals": terminals, "output": output, "acks": acks, "sessions": sessions})

    def run(self):
        threading.Thread(target=self.pulling, daemon=True).start()
        while self.alive:
            try:
                self.report()
            except OSError:
                pass
            time.sleep(0.12)

    def pulling(self):
        while self.alive:
            try:
                operations = self.call("/api/terminal/agent/pull", {"instance": self.instance, "wait": 2}).get("operations", [])
            except OSError:
                continue
            for op in operations:
                self.seen.append(op)
                threading.Thread(target=self.carry, args=(op,), daemon=True).start()

    def ack(self, op, error="", result=None):
        with self.lock:
            self.acks.append({"id": op["id"], "error": error, **({"result": result} if result is not None else {})})

    def carry(self, op):
        action, terminal = op["action"], op.get("terminal")
        if action.startswith("work_"):
            return self.ack(op, result=self.work(action, op))
        if action == "start":
            n = len(self.terminals)
            session = "" if op["tool"] == "shell" else CODEX if n == 0 else "0199cccc-dddd-7eee-8fff-%012d" % (n + 1)
            with self.lock:
                self.terminals[terminal] = {"id": terminal, "tool": op["tool"], "dir": op["dir"], "session": "", "status": "starting", "state": "running", "seq": 0, "form": op.get("form", ""),
                                            "title": "结算页的金额" if n == 0 else (op.get("text") or "")[:40], "created": int(time.time() * 1000)}
            self.ack(op)
            if op.get("form") == "chat":
                self.emit(terminal, t="session", session=session)
                with self.lock:
                    self.terminals[terminal]["session"] = session
                self.emit(terminal, t="state", state="idle", at=int(time.time() * 1000))
                if op.get("text"):
                    self.emit(terminal, t="item", id="u1", kind="user", text=op["text"], op=op["id"])
                    self.emit(terminal, t="item", id="t1", kind="text", text="好的，我接着处理结算页的金额。")
            else:
                with self.lock:
                    self.output.append({"terminal": terminal, "seq": 1, "data": "PS D:\\work\\web-shop> "})
                    self.terminals[terminal]["seq"] = 1
            return
        self.ack(op)
        if action == "say":
            self.emit(terminal, t="item", id="u-" + op["id"], kind="user", text=op["text"], op=op["id"])
            self.emit(terminal, t="item", id="t-" + op["id"], kind="text", text="金额统一保留两位小数，四舍五入在最后一步做。")
            self.emit(terminal, t="turn", end="done", at=int(time.time() * 1000))
        elif action == "close":
            with self.lock:
                self.terminals[terminal]["state"] = "closed"

    def work(self, action, op):
        if action == "work_schedules":
            return {"schedules": self.schedules}
        if action == "work_schedule_set":
            item = dict(op["item"], title="结算页的金额", next=int(time.time() * 1000) + 86400000 if op["item"].get("on", True) else 0, runs=0, last=None)
            item.setdefault("days", [])
            self.schedules = [s for s in self.schedules if s["id"] != item["id"]] + [item]
            return {"schedule": item}
        if action == "work_schedule_remove":
            self.schedules = [s for s in self.schedules if s["id"] != op["schedule"]]
            return {}
        if action == "work_brief":
            return {"text": BRIEF + ("\n\n我的补充：\n" + op["note"] if op.get("note") else ""), "tool": op["from_tool"], "dir": "web-shop", "title": "结算页的金额"}
        if action == "work_chains":
            return {"chains": []}
        return {}


def main():
    chrome, out = sys.argv[1], Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    port, password = free_port(), "test-" + secrets.token_hex(8)
    with tempfile.TemporaryDirectory(prefix="remote-ai-menus-", ignore_cleanup_errors=True) as temporary:
        relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(port), "--data", str(Path(temporary) / "relay")],
                                 env=dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1"), stdout=subprocess.DEVNULL)
        browser = computer = None
        try:
            for _ in range(60):
                try:
                    computer = Computer(port, password)
                    break
                except OSError:
                    time.sleep(0.25)
            threading.Thread(target=computer.run, daemon=True).start()
            phone = Computer.__new__(Computer)
            phone.port, phone.cookie = port, ""
            phone.call("/api/login", {"password": password})
            for _ in range(60):
                if json.loads(http_get(port, "/api/terminal", phone.cookie))["device"]["online"]:
                    break
                time.sleep(0.2)

            browser = subprocess.Popen([chrome, "--headless=new", "--no-first-run", "--remote-debugging-port=0", "--remote-allow-origins=*",
                                        "--user-data-dir=" + temporary + "/browser", "about:blank"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            active = Path(temporary) / "browser/DevToolsActivePort"
            for _ in range(100):
                if active.exists():
                    break
                time.sleep(0.1)
            debug = int(active.read_text().splitlines()[0])
            connection = http.client.HTTPConnection("127.0.0.1", debug)
            connection.request("PUT", "/json/new?about:blank")
            target = json.loads(connection.getresponse().read())
            pipe = Socket("http://127.0.0.1:%d" % debug, "/devtools/page/" + target["id"], "")
            counter, problems = [0], []

            def ask(method, **params):
                counter[0] += 1
                pipe.send({"id": counter[0], "method": method, "params": params})
                while True:
                    message = pipe.receive(15)
                    if message.get("id") == counter[0]:
                        assert "error" not in message, message
                        return message.get("result", {})
                    if message.get("method") == "Runtime.exceptionThrown":
                        problems.append(message["params"]["exceptionDetails"])

            def value(code):
                result = ask("Runtime.evaluate", expression=code, returnByValue=True, awaitPromise=True)
                assert "exceptionDetails" not in result, result
                return result.get("result", {}).get("value")

            def until(code, why, seconds=15):
                deadline = time.time() + seconds
                while time.time() < deadline:
                    found = value("!!(" + code + ")")
                    if found:
                        return found
                    time.sleep(0.1)
                raise AssertionError((why, value("({url: location.href, sheet: (document.querySelector('dialog.rs') || {}).innerText, body: document.body.innerText.slice(0, 800)})"), problems[-2:]))

            def picture(name):
                time.sleep(0.45)            # the sheet and its page have risen
                (out / name).write_bytes(base64.b64decode(ask("Page.captureScreenshot", format="png")["data"]))

            def press(selector, why):
                until("(() => { const node = document.querySelector(%s); if (!node || node.disabled) return false; node.click(); return true; })()" % json.dumps(selector), why)

            def press_text(text, why):
                until("(() => { const node = [...document.querySelectorAll('dialog.rs:not(.closing) button')].find(b => b.innerText.trim().startsWith(%s)); if (!node || node.disabled) return false; node.click(); return true; })()" % json.dumps(text), why)

            sheet_title = "(document.querySelector('dialog.rs[open] .rs-bar h2') || {}).textContent"
            ask("Page.enable"); ask("Runtime.enable"); ask("Network.enable")
            ask("Emulation.setDeviceMetricsOverride", width=390, height=844, deviceScaleFactor=2, mobile=True)
            name, cookie = phone.cookie.split("=", 1)
            ask("Network.setCookie", name=name, value=cookie, url="http://127.0.0.1:%d/" % port)
            base = "http://127.0.0.1:%d/" % port

            # ---- a Codex conversation, and its menu
            started = phone.call("/api/terminal", {"id": secrets.token_hex(16), "action": "start", "tool": "codex", "dir": "web-shop", "form": "chat"})
            first = started["terminal"]
            ask("Page.navigate", url=base + "chat/?id=" + first)
            until("document.getElementById('title').textContent === '结算页的金额'", "the conversation's page")
            press("#menu", "the menu")
            until(sheet_title + " === '对话'", "the menu opens as a sheet")
            tiles = value("[...document.querySelectorAll('.rs-tile')].map(t => t.innerText.trim())")
            assert tiles == ["交给 AI", "定时提问", "项目文件", "复制全文"], tiles
            assert "Codex · web-shop" in value("document.querySelector('.rs-who').innerText"), value("document.querySelector('.rs-who').innerText")
            picture("menu-chat.png")

            # ---- handed to a new Claude Code conversation: the other tool is offered first
            press("#menu-handoff", "hand over")
            until(sheet_title + " === '交给另一个 AI'", "the hand-over page")
            until("document.querySelector('.rs-row[role=radio][aria-checked=true]') && document.querySelector('.rs-row[role=radio][aria-checked=true]').innerText.includes('新对话 · Claude Code')", "a new Claude Code conversation chosen at first")
            until("document.getElementById('handoff-go').innerText.includes('交给新的 Claude Code')", "the button says where it goes")
            value("document.querySelector('#handoff-note textarea, textarea#handoff-note').value = ''")
            press(".rs-chip", "a note from the presets")
            assert value("document.getElementById('handoff-note').value") == "接着完成剩下的工作", value("document.getElementById('handoff-note').value")
            picture("menu-handoff.png")
            # what it will say, read first
            press_text("预览交接内容", "the preview")
            until("document.querySelector('.rs-preview') && document.querySelector('.rs-preview').textContent.includes('我的补充：\\n接着完成剩下的工作')", "the brief, with the note")
            picture("menu-handoff-preview.png")
            ask("Runtime.evaluate", expression="history.back()")
            until(sheet_title + " === '交给另一个 AI'", "the phone's back key leads back a page")
            press("#handoff-go", "hand over")
            until("location.pathname.endsWith('/chat/') && new URLSearchParams(location.search).get('id') !== %s" % json.dumps(first), "the new conversation's page", 20)
            start = [op for op in computer.seen if op["action"] == "start" and op.get("tool") == "claude"]
            assert len(start) == 1 and start[0].get("form") == "chat" and start[0]["dir"] == "web-shop" and start[0]["text"].startswith(BRIEF) and start[0]["text"].endswith("接着完成剩下的工作"), start
            brief = [op for op in computer.seen if op["action"] == "work_brief"]
            assert brief and brief[-1]["from"] == CODEX and brief[-1]["from_tool"] == "codex", brief
            until("document.getElementById('said').innerText.includes('我接着处理结算页的金额')", "the new conversation began with the hand-over")
            assert value("!!document.querySelector('dialog.rs')") is False
            picture("handoff-new-conversation.png")

            # ---- a question at a time for the first conversation, on weekdays
            ask("Page.navigate", url=base + "chat/?id=" + first)
            until("document.getElementById('title').textContent === '结算页的金额'", "the first conversation again")
            press("#menu", "the menu")
            press("#menu-schedule", "questions at a time")
            until(sheet_title + " === '定时提问'", "the list of questions")
            until("document.getElementById('schedule-add')", "the button for a new one")
            picture("menu-schedules-empty.png")
            press("#schedule-add", "a new question")
            until(sheet_title + " === '新建定时提问'", "the editor")
            assert value("!document.getElementById('schedule-session')"), "the conversation is not chosen again from its own menu"
            value("(() => { const box = document.getElementById('schedule-text'); box.value = '汇报一下当前进展'; box.dispatchEvent(new Event('input')); })()")
            press_text("工作日", "weekdays")
            days = value("[...document.querySelectorAll('.rs-chips.days .rs-chip')].map(c => c.getAttribute('aria-pressed'))")
            assert days == ["true"] * 5 + ["false"] * 2, days
            until("document.querySelector('.rs-summary').innerText.startsWith('下次提问：')", "when it will be asked")
            picture("menu-schedule-editor.png")
            press("#schedule-save", "save")
            until(sheet_title + " === '定时提问' && document.querySelector('.rs-switch')", "back on the list, with the new one")
            saved = [op for op in computer.seen if op["action"] == "work_schedule_set"][-1]["item"]
            assert (saved["session"], saved["tool"], saved["kind"], saved["time"], saved["days"], saved["text"]) == (CODEX, "codex", "daily", "09:00", [1, 2, 3, 4, 5], "汇报一下当前进展"), saved
            assert "工作日 09:00" in value("document.querySelector('dialog.rs .rs-group').innerText"), value("document.querySelector('dialog.rs .rs-group').innerText")
            picture("menu-schedules.png")
            press(".rs-switch", "switched off from its row")
            until("document.querySelector('.rs-switch') && document.querySelector('.rs-switch').getAttribute('aria-checked') === 'false' && !document.querySelector('.rs-switch').disabled", "switched off")
            assert [op for op in computer.seen if op["action"] == "work_schedule_set"][-1]["item"]["on"] is False
            # the back key: a page back, then the sheet is gone
            ask("Runtime.evaluate", expression="history.back()")
            until(sheet_title + " === '对话'", "back to the menu")
            ask("Runtime.evaluate", expression="history.back()")
            until("!document.querySelector('dialog.rs')", "the sheet is gone")
            assert value("location.pathname").endswith("/chat/"), value("location.href")

            # ---- the look, from the menu
            press("#menu", "the menu")
            press_text("外观", "the look")
            until(sheet_title + " === '外观'", "the skins")
            press(".rs-swatch[data-value=night]", "a dark skin")
            until("getComputedStyle(document.documentElement).getPropertyValue('--bg').trim() === '#1a1b26'", "the page turned dark")
            ask("Runtime.evaluate", expression="history.back()")
            until(sheet_title + " === '对话'", "back to the menu")
            press(".rs-seg[aria-label=字号] button[data-value=large]", "a larger text")
            until("document.documentElement.style.fontSize === '17px'", "the text is larger")
            picture("menu-chat-dark.png")
            press(".rs-seg[aria-label=字号] button[data-value=normal]", "the usual text")
            press(".rs-close", "closed")
            until("!document.querySelector('dialog.rs')", "the sheet is gone")
            value("localStorage.setItem('rcli-skin', 'paper')")

            # ---- a plain terminal's menu: no conversation to hand over
            shell = phone.call("/api/terminal", {"id": secrets.token_hex(16), "action": "start", "tool": "shell", "dir": "web-shop"})["terminal"]
            ask("Page.navigate", url=base + "terminal/?id=" + shell)
            until("window.TerminalUI && window.TerminalUI.fontSizes", "the terminal page")
            # (the menu waits for the terminal to be known: pressed again until it opens)
            until("(() => { if (document.querySelector('dialog.rs')) return true; document.getElementById('menu').click(); return false; })()", "the terminal's menu")
            until(sheet_title + " === '终端'", "the terminal's menu opens as a sheet")
            assert value("document.getElementById('menu-handoff').getAttribute('aria-disabled')") == "true"
            sizes = value("document.querySelector('.rs-stepper output').textContent")
            press(".rs-stepper button:last-child", "a larger font")
            until("window.TerminalUI.fontSize() > %d" % int(sizes), "the font grew")
            picture("menu-terminal.png")
            press("#menu-handoff", "a tile that cannot be used")
            until("document.getElementById('notice').textContent === '终端里没有 AI 对话'", "says why")
            press_text("重命名", "rename")
            until(sheet_title + " === '重命名'", "the name")
            value("document.getElementById('terminal-name').value = '部署脚本'")
            press("#rename-save", "save the name")
            until(sheet_title + " === '终端'", "back on the menu")
            assert [t["title"] for t in json.loads(http_get(port, "/api/terminal", phone.cookie))["terminals"] if t["id"] == shell] == ["部署脚本"]
            press(".rs-close", "closed")

            assert not problems, problems
            print("menus: one sheet with pages for a conversation and a terminal; handed to a new Claude Code conversation with the brief; a question on weekdays set and switched off; the look; the back key")
        finally:
            if computer:
                computer.alive = False
            if browser:
                browser.kill()
            relay.kill()


if __name__ == "__main__":
    main()
