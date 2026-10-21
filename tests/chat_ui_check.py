"""The page of a conversation run through its tool's structured mode (web/chat/), through the real relay server: text
that is written in pieces, a step with its command and what it prints, a question answered from the page, a message
sent and given back by the tool, a stop, the end and going on again; the terminal page hands such a terminal over to
it. The computer is played here, by the relay's own interface for computers: nothing real is started or told.

Usage: python tests/chat_ui_check.py <Edge/Chrome executable> <screenshot directory>
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

SESSION = "0199cccc-dddd-7eee-8fff-000000000001"


def free_port():
    with socket.socket() as reserved:
        reserved.bind(("127.0.0.1", 0))
        return reserved.getsockname()[1]


class Computer:
    """A computer as the relay sees it: it reports its terminals and their output, takes operations, and answers them.
    Its one tool runs every conversation by the same script."""

    def __init__(self, port, password):
        self.port, self.cookie, self.instance = port, "", secrets.token_hex(16)
        self.lock = threading.Lock()
        self.terminals, self.output, self.acks, self.seen = {}, [], [], []
        self.answer = threading.Event()
        self.stopped = threading.Event()
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
            if event["t"] == "ask":
                term["ask"] = event["title"] + "\n" + event["text"]
            if event["t"] == "answered":
                term["ask"] = ""
            if event["t"] in ("set",):
                term["said"] = event["text"]

    def report(self):
        with self.lock:
            output, self.output = self.output, []
            acks, self.acks = self.acks, []
            terminals = [{"id": t["id"], "state": t["state"], "tool": t["tool"], "dir": t["dir"], "folder": "", "session": t["session"], "status": t["status"] if t["state"] == "running" else "",
                          "created": t["created"], "title": "", "form": "chat", "said": t["said"], "ask": t["ask"], "cols": 80, "rows": 24} for t in self.terminals.values()]
            # the conversations the computer lists: one that runs here is held by this program
            sessions = {}
            for t in self.terminals.values():
                if t["session"]:
                    sessions[t["session"]] = {"id": t["session"], "tool": t["tool"], "dir": t["dir"], "title": "结算页的金额" if t["session"] == SESSION else "新的对话",
                                              "updated": t["created"], "live": t["state"] == "running" or sessions.get(t["session"], {}).get("live", False)}
            sessions = list(sessions.values())
        self.call("/api/terminal/agent", {"info": {"instance": self.instance, "enabled": True, "tools": ["claude", "codex"], "workspaces": ["web-shop"], "features": ["chat", "files", "peek", "claude-desktop-open", "codex-desktop-open"],
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

    def ack(self, op, error=""):
        with self.lock:
            self.acks.append({"id": op["id"], "error": error})

    def carry(self, op):
        action, terminal = op["action"], op.get("terminal")
        if action == "start":
            with self.lock:
                self.terminals[terminal] = {"id": terminal, "tool": op["tool"], "dir": op["dir"], "session": "", "status": "starting", "state": "running", "seq": 0, "said": "", "ask": "", "created": int(time.time() * 1000)}
            self.ack(op)
            # the first conversation is the one gone on with later; one begun anew afterwards is another
            session = op.get("session") or (SESSION if len(self.terminals) == 1 else "0199cccc-dddd-7eee-8fff-%012d" % len(self.terminals))
            self.emit(terminal, t="session", session=session)
            with self.lock:
                self.terminals[terminal]["session"] = session
            self.emit(terminal, t="state", state="idle", at=int(time.time() * 1000))
            if op.get("text"):
                self.first(terminal, op)
            return
        self.ack(op)
        if action == "answer":
            self.emit(terminal, t="answered", id=op["ask"], decision=op["decision"])
            self.emit(terminal, t="state", state="busy", at=int(time.time() * 1000))
            self.answer.set()
        elif action == "say":
            self.emit(terminal, t="state", state="busy", at=int(time.time() * 1000))
            self.emit(terminal, t="item", id="u-" + op["id"], kind="user", text=op["text"], op=op["id"])
            if "慢慢" in op["text"]:
                self.emit(terminal, t="item", id="s3", kind="step", text="Bash：等待部署", detail="npm run deploy:staging", state="run", name="Bash")
                for n in range(60):
                    if self.stopped.wait(0.25):
                        break
                    self.emit(terminal, t="out", id="s3", text="上传 %d%%\n" % (n * 2))
                return
            self.emit(terminal, t="item", id="t-" + op["id"], kind="text", text="")
            for piece in ("好的，", "优惠券的金额", "也按两位小数显示了。"):
                time.sleep(0.15)
                self.emit(terminal, t="more", id="t-" + op["id"], text=piece)
            self.emit(terminal, t="turn", end="done", at=int(time.time() * 1000))
            self.emit(terminal, t="state", state="idle", at=int(time.time() * 1000))
        elif action == "stop":
            self.stopped.set()
            self.emit(terminal, t="done", id="s3", ok=False)
            self.emit(terminal, t="turn", end="stopped", at=int(time.time() * 1000))
            self.emit(terminal, t="state", state="idle", at=int(time.time() * 1000))
        elif action == "close":
            self.emit(terminal, t="state", state="ended", at=int(time.time() * 1000))
            with self.lock:
                self.terminals[terminal]["state"] = "closed"

    def first(self, terminal, op):
        """What a conversation does with its first words: it says it will look, runs a step that prints, asks before the next."""
        now = lambda: int(time.time() * 1000)
        self.emit(terminal, t="state", state="busy", at=now())
        self.emit(terminal, t="item", id="u1", kind="user", text=op["text"], op=op["id"])
        self.emit(terminal, t="item", id="t1", kind="text", text="")
        for piece in ("我先看看", "金额是在哪一步换算的，", "再对比两版的输出。\n\n- 第一步：**读代码**\n- 第二步：跑测试"):
            time.sleep(0.4)
            self.emit(terminal, t="more", id="t1", text=piece)
        self.emit(terminal, t="item", id="s1", kind="step", text="Bash：对比两版的金额", detail="node compare.js --old 2.0 --new 4.0", state="run", name="Bash")
        for line in ("2.0: 12.50\n", "4.0: 12.5\n", "differs in 3 of 40 rows\n"):
            time.sleep(0.3)
            self.emit(terminal, t="out", id="s1", text=line)
        self.emit(terminal, t="done", id="s1", ok=True)
        self.emit(terminal, t="item", id="s2", kind="step", text="Edit：src/cart/formatPrice.js", detail="src/cart/formatPrice.js", state="run", name="Edit")
        self.emit(terminal, t="ask", id="a1b2c3d4e5f60718", kind="tool", title="Edit", text="src/cart/formatPrice.js",
                  options=[{"id": "allow", "label": "允许"}, {"id": "always", "label": "总是允许"}, {"id": "deny", "label": "拒绝"}])
        self.emit(terminal, t="state", state="confirm", at=now())
        if not self.answer.wait(60):
            return
        self.emit(terminal, t="done", id="s2", ok=True, result="已修改 1 处")
        self.emit(terminal, t="item", id="t2", kind="text", text="")
        self.emit(terminal, t="more", id="t2", text="问题在 `formatPrice`：已经固定为两位小数。")
        self.emit(terminal, t="turn", end="done", at=now())
        self.emit(terminal, t="state", state="idle", at=now())


def main():
    chrome, out = sys.argv[1], Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    port, password = free_port(), "test-" + secrets.token_hex(8)
    with tempfile.TemporaryDirectory(prefix="remote-ai-chat-ui-", ignore_cleanup_errors=True) as temporary:
        relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(port), "--data", str(Path(temporary) / "relay")],
                                 env=dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1"), stdout=subprocess.DEVNULL)
        browser = None
        computer = None
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
                    found = value(code)
                    if found:
                        return found
                    time.sleep(0.1)
                raise AssertionError((why, value("({url: location.href, body: document.body.innerText.slice(0, 1500)})"), problems[-2:]))

            def picture(name):
                (out / name).write_bytes(base64.b64decode(ask("Page.captureScreenshot", format="png")["data"]))

            ask("Page.enable"); ask("Runtime.enable"); ask("Network.enable")
            ask("Emulation.setDeviceMetricsOverride", width=390, height=844, deviceScaleFactor=2, mobile=True)
            name, cookie = phone.cookie.split("=", 1)
            ask("Network.setCookie", name=name, value=cookie, url="http://127.0.0.1:%d/" % port)
            base = "http://127.0.0.1:%d/" % port

            # ---- a new conversation with its first words: started, then shown by the terminal page's link, which hands it over
            started = phone.call("/api/terminal", {"id": secrets.token_hex(16), "action": "start", "tool": "claude", "dir": "web-shop", "form": "chat", "text": "结算页的金额有时多出一位小数，帮我看看"})
            terminal = started["terminal"]
            listed = json.loads(http_get(port, "/api/terminal", phone.cookie))
            assert [t["title"] for t in listed["terminals"] if t["id"] == terminal] == ["结算页的金额有时多出一位小数，帮我看看"], listed["terminals"]
            ask("Page.navigate", url=base + "terminal/?id=" + terminal)
            until("location.pathname.endsWith('/chat/') && document.getElementById('title').textContent.includes('结算页')", "the chat page, from the terminal page's link")
            assert value("location.search") == "?id=" + terminal
            until("document.querySelector('.user')?.textContent === '结算页的金额有时多出一位小数，帮我看看'", "the first words, given back by the tool")
            # the answer as it is written: a part of it first, then the whole, set as Markdown
            until("(t => t && t.textContent.includes('我先看看') && !t.textContent.includes('再对比'))(document.querySelector('.text'))", "a part of the answer while it is written")
            until("document.querySelector('.text strong')?.textContent === '读代码'", "the whole answer, set as Markdown")
            until("document.querySelector('#stands').className === 'busy' && document.getElementById('standing').textContent === '正在执行' && !document.getElementById('stop').hidden", "at work, with a stop")
            # a step with its command, and what it prints as it prints it
            until("(s => s && s.textContent.includes('node compare.js') && s.querySelector('pre')?.textContent.includes('differs in 3 of 40 rows'))(document.querySelector('.step'))", "the step, its command and its output")
            # what it asks, above the box; answered from here
            until("!document.getElementById('asks').hidden && document.querySelector('.ask pre').textContent === 'src/cart/formatPrice.js'", "the question")
            assert value("Array.from(document.querySelectorAll('.ask button')).map(b => b.textContent)") == ["允许", "总是允许", "拒绝"]
            assert value("document.getElementById('standing').textContent") == "等你确认"
            listed = json.loads(http_get(port, "/api/terminal", phone.cookie))
            entry = next(t for t in listed["terminals"] if t["id"] == terminal)
            assert entry["phase"] == "confirm" and entry["asks"] == ["Edit", "src/cart/formatPrice.js"] and entry["form"] == "chat", entry
            picture("chat-ask.png")
            value("Array.from(document.querySelectorAll('.ask button')).find(b => b.textContent === '允许').click()")
            until("document.getElementById('asks').hidden", "the question gone once answered")
            answered = [op for op in computer.seen if op["action"] == "answer"]
            assert answered and answered[0]["decision"] == "allow" and answered[0]["ask"] == "a1b2c3d4e5f60718", answered
            until("document.querySelectorAll('.text')[1]?.querySelector('code')?.textContent === 'formatPrice'", "the answer after the step")
            until("document.getElementById('standing').textContent === '等你的消息' && document.getElementById('stop').hidden", "waiting once the turn has ended")
            # done steps fold to their last lines; a press opens one
            assert value("document.querySelectorAll('.step')[0].className") == "step ok"
            picture("chat-done.png")

            # ---- a message: it stands at once, and is the tool's once given back
            value("document.getElementById('box').value = '改完后顺便把优惠券那里也检查一下'; document.getElementById('write').requestSubmit()")
            until("Array.from(document.querySelectorAll('.user')).some(u => u.textContent.includes('改完后') && u.textContent.includes('正在送达')) || document.querySelectorAll('.user:not(.going)').length === 2", "the message at once")
            until("document.querySelectorAll('.user:not(.going)').length === 2 && !document.querySelector('.user.going')", "the message given back by the tool")
            until("document.querySelectorAll('.text')[2]?.textContent.trim() === '好的，优惠券的金额也按两位小数显示了。'", "the answer to the message")
            said = [op for op in computer.seen if op["action"] == "say"]
            assert said[-1]["text"] == "改完后顺便把优惠券那里也检查一下", said

            # ---- stopped while a step runs: the step ends, the turn says so, and "继续" stands beside the box
            value("document.getElementById('box').value = '慢慢部署到测试环境'; document.getElementById('write').requestSubmit()")
            until("(s => s && s.textContent.includes('npm run deploy:staging') && s.querySelector('pre')?.textContent.includes('上传'))(document.querySelectorAll('.step')[2])", "the long step printing")
            picture("chat-busy.png")
            value("document.getElementById('stop').click()")
            until("Array.from(document.querySelectorAll('.note')).some(n => n.textContent === '已停止') && document.getElementById('standing').textContent === '已停止'", "stopped")
            assert any(op["action"] == "stop" for op in computer.seen)
            until("!document.getElementById('go').hidden", "继续 beside the box")
            assert value("document.querySelectorAll('.step')[2].className") == "step fail"

            # ---- read again from the start (a page opened later): the same conversation
            ask("Page.reload")
            until("document.querySelectorAll('.user').length === 3 && document.querySelectorAll('.step').length === 3 && document.getElementById('standing').textContent === '已停止'", "the same conversation after a reload")
            assert value("document.getElementById('asks').hidden") is True

            # ---- the dark skin
            value("localStorage.setItem('rcli-skin', 'night')")
            ask("Page.reload")
            until("document.querySelectorAll('.step').length === 3", "reloaded in the dark skin")
            value("document.getElementById('said').scrollTop = 0")
            picture("chat-dark.png")
            value("localStorage.setItem('rcli-skin', 'paper')")

            # ---- ended from the menu: the box goes, and the way to go on with it comes
            value("document.getElementById('menu').click()")
            until("document.getElementById('chat-sheet')?.open", "the menu")
            assert value("Array.from(document.querySelectorAll('#chat-sheet button')).map(b => b.textContent)") == ["重命名", "在终端中打开", "结束对话", "取消"]
            value("Array.from(document.querySelectorAll('#chat-sheet button')).find(b => b.textContent === '结束对话').click()")
            until("Array.from(document.querySelectorAll('#chat-sheet button')).some(b => b.textContent === '结束对话' && b.className === 'danger') && document.getElementById('chat-sheet').open", "asked once")
            value("Array.from(document.querySelectorAll('#chat-sheet button')).find(b => b.textContent === '结束对话').click()")
            until("document.getElementById('standing').textContent === '对话已结束' && !document.getElementById('again').hidden && document.getElementById('write').hidden", "ended", 20)
            assert any(op["action"] == "close" for op in computer.seen)
            # going on with it starts it again, as a conversation, with the same id
            value("document.getElementById('again').click()")
            until("location.search !== '?id=%s' && location.pathname.endsWith('/chat/')" % terminal, "the conversation again", 20)
            again = [op for op in computer.seen if op["action"] == "start"][-1]
            assert again.get("form") == "chat" and again.get("session") == SESSION, again

            # ---- the ways in. 应用 on a project's page begins a conversation at one press, and its page waits for the first words
            texts = lambda selector: value("Array.from(document.querySelectorAll(%s)).map(n => n.textContent)" % json.dumps(selector))
            ask("Page.navigate", url=base + "?project=web-shop")
            until("!document.getElementById('project').hidden && document.querySelectorAll('#launch button').length === 2", "the project's page with the tools of 应用")
            assert texts("#begin .seg button") == ["应用", "终端"] and texts("#launch button") == ["Codex", "Claude"], texts("#launch button")
            # a held press opens the desktop app on the computer instead, with the first words in its box
            value("document.querySelectorAll('#launch button')[1].dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }))")
            until("Array.from(document.querySelectorAll('#sheet[open] button')).some(b => b.textContent === '在 Claude 中打开')", "the desktop app's sheet at a held press")
            value("document.getElementById('sheet').close()")
            before = len(computer.seen)
            value("Array.from(document.querySelectorAll('#launch button')).find(b => b.textContent === 'Claude').click()")
            until("location.pathname.endsWith('/chat/') && document.querySelector('.empty')?.textContent === '发一句话开始'", "a new conversation's page")
            begun = [op for op in computer.seen[before:] if op["action"] == "start"]
            assert len(begun) == 1 and begun[0]["form"] == "chat" and begun[0]["tool"] == "claude" and begun[0]["dir"] == "web-shop" and "text" not in begun[0] and "session" not in begun[0], begun
            assert not any(op["action"] == "app_open" for op in computer.seen), "the desktop app is not opened by a press"
            value("bridge = window.ProjectTerminal; document.getElementById('menu').click()")
            until("document.getElementById('chat-sheet')?.open", "the menu")
            value("Array.from(document.querySelectorAll('#chat-sheet button')).find(b => b.textContent === '结束对话').click()")
            value("Array.from(document.querySelectorAll('#chat-sheet button')).find(b => b.textContent === '结束对话').click()")
            until("document.getElementById('standing').textContent === '对话已结束'", "the new one ended", 20)
            # a saved conversation in 任务 is gone on with as a conversation; a held press offers the terminal
            for terminal_id in [t["id"] for t in json.loads(http_get(port, "/api/terminal", phone.cookie))["terminals"] if t["state"] == "running"]:
                phone.call("/api/terminal", {"id": secrets.token_hex(16), "action": "close", "terminal": terminal_id})
            until_closed = time.time() + 20
            while time.time() < until_closed and any(s["live"] for s in json.loads(http_get(port, "/api/terminal", phone.cookie))["sessions"]):
                time.sleep(0.3)
            ask("Page.navigate", url=base + "?tab=tasks")
            card = "Array.from(document.querySelectorAll('#recent-list .card')).find(c => c.textContent.includes('结算页的金额'))"
            until("!!" + card, "the saved conversation's card")
            value(card + ".dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }))")
            until("Array.from(document.querySelectorAll('#sheet[open] button')).some(b => b.textContent === '在手机终端继续')", "the terminal behind a held press")
            value("document.getElementById('sheet').close()")
            before = len(computer.seen)
            value(card + ".querySelector('.open').click()")
            until("location.pathname.endsWith('/chat/') && document.getElementById('title').textContent === '结算页的金额'", "the saved conversation as a conversation", 20)
            resumed = [op for op in computer.seen[before:] if op["action"] == "start"]
            assert len(resumed) == 1 and resumed[0]["form"] == "chat" and resumed[0]["session"] == SESSION, resumed

            # nothing on the page wider than the phone
            assert value("document.documentElement.scrollWidth <= innerWidth") is True
            assert not problems, problems
            print("chat page: text as it is written, steps and their output, a question answered, a message given back, a stop, the end and going on again")
        finally:
            if computer:
                computer.alive = False
            if browser:
                browser.kill()
            relay.kill()


def http_get(port, path, cookie):
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
    connection.request("GET", path, headers={"Cookie": cookie})
    return connection.getresponse().read()


if __name__ == "__main__":
    main()
