"""The page of a conversation run through its tool's structured mode (web/chat/), through the real relay server: text
that is written in pieces, a step with its command and what it prints, a question answered from the page, a message
sent and given back by the tool, a stop, the end and going on again; the terminal page hands such a terminal over to
it. The computer is played here, by the relay's own interface for computers: nothing real is started or told.

Usage: python tests/chat_ui_check.py <Edge/Chrome executable> <screenshot directory>
"""
import base64
import binascii
import http.client
import json
import os
import re
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
PROJECT = "D:\\work\\web-shop"
PIECE = 737280


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
        self.features = ["chat", "files", "peek", "claude-desktop-open", "codex-desktop-open"]
        self.files, self.uploads, self.refuse_upload = {}, {}, ""
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
        self.call("/api/terminal/agent", {"info": {"instance": self.instance, "enabled": True, "tools": ["claude", "codex"], "workspaces": ["web-shop"], "features": list(self.features),
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
            self.acks.append(dict({"id": op["id"], "error": error}, **({"result": result} if result is not None else {})))

    def file(self, op):
        """The project's files as the computer keeps them: an upload in pieces, published whole; a piece of one read."""
        action, path = op["action"], op["path"]
        if action == "file_upload_start":
            if self.refuse_upload:
                raise ValueError(self.refuse_upload)
            self.uploads[op["upload"]] = {"path": path, "size": op["size"], "data": bytearray()}
            return {"offset": 0}
        if action == "file_upload_chunk":
            upload = self.uploads[op["upload"]]
            assert op["offset"] == len(upload["data"]), (op["offset"], len(upload["data"]))
            upload["data"] += base64.b64decode(op["data"])
            return {"offset": len(upload["data"])}
        if action == "file_upload_finish":
            upload = self.uploads.pop(op["upload"])
            if path in self.files:
                raise ValueError("已有同名文件")
            self.files[path] = bytes(upload["data"])
            return {"offset": len(upload["data"])}
        if action == "file_upload_cancel":
            self.uploads.pop(op["upload"], None)
            return {}
        if action == "file_read":
            if path not in self.files:
                raise ValueError("没有这个文件")
            whole, at = self.files[path], op["offset"]
            piece = whole[at:at + PIECE]
            return {"path": path, "size": len(whole), "modified": 1, "offset": at, "data": base64.b64encode(piece).decode(), "end": at + len(piece) >= len(whole)}
        raise ValueError("不支持此操作")

    def carry(self, op):
        action, terminal = op["action"], op.get("terminal")
        if action.startswith("file_"):
            try:
                self.ack(op, result=self.file(op))
            except (ValueError, KeyError, AssertionError, binascii.Error) as refused:
                self.ack(op, error=str(refused) or "操作没有执行")
            return
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
        if action == "session_read":
            # what the saved conversation said, as the reader shows it before it is gone on with
            self.ack(op, result={"title": "结算页的金额", "updated": int(time.time() * 1000), "more": False, "messages": [
                {"role": "user", "text": "结算页的金额有时多出一位小数，帮我看看"},
                {"role": "tool", "text": "Bash：对比两版的金额", "detail": "node compare.js --old 2.0 --new 4.0", "result": "differs in 3 of 40 rows"},
                {"role": "assistant", "text": "问题在 formatPrice：优惠券的金额按两位小数显示了。"}]})
            return
        self.ack(op)
        if action == "answer":
            self.emit(terminal, t="answered", id=op["ask"], decision=op["decision"])
            self.emit(terminal, t="state", state="busy", at=int(time.time() * 1000))
            self.answer.set()
        elif action == "say":
            self.emit(terminal, t="state", state="busy", at=int(time.time() * 1000))
            # the computer names each picture after the words, in a line of its own
            shown = "\n".join(([op["text"]] if op["text"] else []) + ["附图：" + PROJECT + "\\" + path.replace("/", "\\") for path in op.get("attachments", [])])
            self.emit(terminal, t="item", id="u-" + op["id"], kind="user", text=shown, op=op["id"])
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
            entry = next(t for t in listed["terminals"] if t["id"] == terminal)
            # The computer may already have reported the conversation's title.
            expected = "结算页的金额" if entry["session"] else "结算页的金额有时多出一位小数，帮我看看"
            assert entry["title"] == expected, entry
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
            # Successful steps collapse fully; their header remains a keyboard/touch control.
            assert value("document.querySelectorAll('.step')[0].className") == "step ok shut"
            assert value("document.querySelector('.step button').getAttribute('aria-expanded')") == "false"
            value("document.querySelector('.step button').click()")
            assert value("document.querySelector('.step button').getAttribute('aria-expanded')") == "true"
            assert value("!document.querySelector('.step pre').hidden")
            value("document.querySelector('.step button').click()")
            picture("chat-done.png")

            # ---- a message: it stands at once, and is the tool's once given back
            value("document.getElementById('box').value = '改完后顺便把优惠券那里也检查一下'; document.getElementById('write').requestSubmit()")
            until("Array.from(document.querySelectorAll('.user')).some(u => u.textContent.includes('改完后') && u.textContent.includes('正在送达')) || document.querySelectorAll('.user:not(.going)').length === 2", "the message at once")
            until("document.querySelectorAll('.user:not(.going)').length === 2 && !document.querySelector('.user.going')", "the message given back by the tool")
            until("document.querySelectorAll('.text')[2]?.textContent.trim() === '好的，优惠券的金额也按两位小数显示了。'", "the answer to the message")
            said = [op for op in computer.seen if op["action"] == "say"]
            assert said[-1]["text"] == "改完后顺便把优惠券那里也检查一下", said

            # ---- pictures with a message (ui/attach.js): no picture button where the computer does not take them
            time.sleep(1.2)
            assert value("document.querySelector('#write .attach-pick').hidden") is True, "no picture button without the attachments feature"
            computer.features += ["file-upload", "attachments"]
            time.sleep(0.5)
            ask("Page.reload")
            until("!document.querySelector('#write .attach-pick')?.hidden && document.querySelectorAll('.user').length === 2", "the picture button where the computer takes pictures")
            pick = """(async colours => {
              const files = [];
              for (const colour of colours) {
                const canvas = document.createElement('canvas'); canvas.width = 160; canvas.height = 120;
                const ctx = canvas.getContext('2d'); ctx.fillStyle = colour; ctx.fillRect(0, 0, 160, 120); ctx.fillStyle = '#ffffff'; ctx.fillRect(20, 20, 60, 40);
                files.push(new File([await new Promise(done => canvas.toBlob(done, 'image/png'))], colour.slice(1) + '.png', { type: 'image/png' }));
              }
              const box = new DataTransfer(); files.forEach(file => box.items.add(file));
              const input = document.querySelector('#write input[type=file]'); input.files = box.files; input.dispatchEvent(new Event('change'));
              return document.querySelectorAll('.attach-tray .attach-chip').length;
            })(%s)"""
            tray = "document.querySelectorAll('main > .attach-tray .attach-chip').length"
            assert value(pick % json.dumps(["#2f54eb", "#16a34a"])) == 2
            assert value("!document.querySelector('main > .attach-tray').hidden && document.querySelector('main > .attach-tray').nextElementSibling.id === 'write'")
            # one of two removed from the tray
            value("document.querySelector('.attach-tray .attach-chip .attach-remove').click()")
            until(tray + " === 1", "one picture removed")
            assert value(pick % json.dumps(["#ea8c00"])) == 2
            value("document.getElementById('box').value = '按截图里的样子改'; document.getElementById('box').dispatchEvent(new Event('input'))")
            picture("chat-tray-light.png")
            # a failed upload keeps the words and the pictures
            computer.refuse_upload = "磁盘空间不足"
            says = len([op for op in computer.seen if op["action"] == "say"])
            value("document.getElementById('write').requestSubmit()")
            until("!document.getElementById('toast').hidden && document.getElementById('toast').textContent.includes('磁盘空间不足')", "the reason the pictures did not go")
            until("document.getElementById('send').textContent === '发送'", "the send button again")
            assert value("document.getElementById('box').value") == "按截图里的样子改" and value(tray) == 2
            assert len([op for op in computer.seen if op["action"] == "say"]) == says and not computer.files, "nothing said or kept after a failed upload"
            # sent: up first, into the project's attachments, then named in the message
            computer.refuse_upload = ""
            names = value("[...document.querySelectorAll('.attach-tray .attach-chip img')].length")
            value("document.getElementById('write').requestSubmit()")
            until("document.querySelectorAll('.user.with-shots:not(.going) .attach-shot').length === 2", "the message given back with its two pictures", 20)
            sent = [op for op in computer.seen if op["action"] == "say"][-1]
            assert sent["text"] == "按截图里的样子改" and len(sent["attachments"]) == names == 2, sent
            for path in sent["attachments"]:
                assert re.fullmatch(r"\.remote-ai/attachments/\d{8}-\d{6}-[0-9a-f]{6}\.png", path), path
                assert computer.files[path][:8] == b"\x89PNG\r\n\x1a\n", path
            assert value(tray) == 0 and value("document.querySelector('main > .attach-tray').hidden") and value("document.getElementById('box').value") == ""
            shown = value("(u => ({text: u.innerText, words: u.querySelector('.words')?.textContent}))([...document.querySelectorAll('.user.with-shots')].pop())")
            assert shown["words"] == "按截图里的样子改" and "附图" not in shown["text"], shown
            # a message that did not go is put back with its words and its pictures (already on the computer)
            value("window.savedSend = ProjectTerminal.send; window.sentOps = []; ProjectTerminal.send = op => sentOps.push(op)")
            assert value(pick % json.dumps(["#7c3aed"])) == 1
            value("document.getElementById('box').value = '退回的消息'; document.getElementById('box').dispatchEvent(new Event('input')); document.getElementById('write').requestSubmit()")
            until("sentOps.length === 1 && (sentOps[0].attachments || []).length === 1", "the message with its picture handed to the bridge", 20)
            value("TerminalUI.inputState('rejected', sentOps[0], '电脑没有收到')")
            until("!!document.querySelector('.user.failed > button')", "the message that did not go")
            uploads = len(computer.files)
            value("document.querySelector('.user.failed > button').click()")
            until("document.getElementById('box').value === '退回的消息' && " + tray + " === 1 && document.querySelector('.attach-tray .attach-chip img')?.naturalWidth === 160", "words and picture back in the box")
            assert len(computer.files) == uploads, "a picture put back is not sent up again"
            value("ProjectTerminal.send = savedSend; document.querySelector('.attach-tray .attach-remove').click(); document.getElementById('box').value = ''; document.getElementById('box').dispatchEvent(new Event('input'))")
            until(tray + " === 0", "the tray empty again")
            # pictures only, no words
            assert value(pick % json.dumps(["#c0261a"])) == 1
            value("document.getElementById('write').requestSubmit()")
            until("document.querySelectorAll('.user.with-shots:not(.going)').length === 2", "a message of a picture alone", 20)
            alone = [op for op in computer.seen if op["action"] == "say"][-1]
            assert alone["text"] == "" and len(alone["attachments"]) == 1, alone
            assert value("[...document.querySelectorAll('.user.with-shots')].pop().innerText.trim()") == ""
            # read again from the computer: the lines 附图 drawn as the pictures, read with file_read
            reads = len([op for op in computer.seen if op["action"] == "file_read"])
            ask("Page.reload")
            until("document.querySelectorAll('.user.with-shots .attach-shot img').length === 3 && [...document.querySelectorAll('.user.with-shots .attach-shot img')].every(i => i.complete && i.naturalWidth === 160)", "the pictures read back from the computer", 20)
            assert len([op for op in computer.seen if op["action"] == "file_read"]) >= reads + 3
            assert "附图" not in value("document.getElementById('said').innerText")
            value("document.getElementById('said').scrollTop = document.getElementById('said').scrollHeight")
            assert value(pick % json.dumps(["#2f54eb", "#16a34a"])) == 2
            picture("chat-shots-light.png")
            for width in (320, 390):
                ask("Emulation.setDeviceMetricsOverride", width=width, height=844, deviceScaleFactor=2, mobile=True)
                sizes = value("({client: document.documentElement.clientWidth, html: document.documentElement.scrollWidth, body: document.body.scrollWidth, write: document.getElementById('write').scrollWidth - document.getElementById('write').clientWidth, said: document.getElementById('said').scrollWidth - document.getElementById('said').clientWidth})")
                assert sizes["client"] == width and max(sizes["html"], sizes["body"]) <= width and sizes["write"] <= 0 and sizes["said"] <= 0, sizes
                picture("chat-shots-%d.png" % width)
            ask("Emulation.setDeviceMetricsOverride", width=390, height=844, deviceScaleFactor=2, mobile=True)
            value("localStorage.setItem('rcli-skin', 'night')")
            ask("Page.reload")
            until("document.querySelectorAll('.user.with-shots .attach-shot img').length === 3 && [...document.querySelectorAll('.user.with-shots .attach-shot img')].every(i => i.naturalWidth === 160)", "the pictures in the dark skin", 20)
            value("document.getElementById('said').scrollTop = document.getElementById('said').scrollHeight")
            assert value(pick % json.dumps(["#2f54eb", "#16a34a"])) == 2
            picture("chat-shots-dark.png")
            value("localStorage.setItem('rcli-skin', 'paper')")
            # a picture opens in the project's files, where it lies
            first = value("[...document.querySelectorAll('.user.with-shots .attach-shot')][0].dataset.name")
            value("[...document.querySelectorAll('.user.with-shots .attach-shot')][0].click()")
            until("location.pathname.endsWith('/files/')", "the picture in the files page")
            opened = value("Object.fromEntries(new URLSearchParams(location.search))")
            assert opened == {"dir": "web-shop", "path": ".remote-ai/attachments", "file": first, "from": "terminal"}, opened
            ask("Page.navigate", url=base + "chat/?id=" + terminal)
            until("document.querySelectorAll('.user.with-shots').length === 2", "back in the conversation")
            value("document.querySelectorAll('.attach-tray .attach-remove').forEach(b => b.click())")

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
            until("document.querySelectorAll('.user').length === 5 && document.querySelectorAll('.step').length === 3 && document.getElementById('standing').textContent === '已停止'", "the same conversation after a reload")
            assert value("document.getElementById('asks').hidden") is True

            # ---- the dark skin
            value("localStorage.setItem('rcli-skin', 'night')")
            ask("Page.reload")
            until("document.querySelectorAll('.step').length === 3", "reloaded in the dark skin")
            value("document.getElementById('said').scrollTop = 0")
            picture("chat-dark.png")
            value("localStorage.setItem('rcli-skin', 'paper')")

            # A draft belongs to this conversation and survives page recreation.
            value("document.getElementById('box').value = '还没发出的草稿'; document.getElementById('box').dispatchEvent(new Event('input'))")
            ask("Page.reload")
            until("document.getElementById('box')?.value === '还没发出的草稿' && document.querySelectorAll('.step').length === 3", "draft after reload")

            # Transport receipts clear only the draft they actually acknowledged, not a newer edit.
            value("window.savedSend = ProjectTerminal.send; window.sentOps = []; ProjectTerminal.send = op => sentOps.push(op); document.getElementById('write').requestSubmit()")
            assert value("document.getElementById('box').value") == '还没发出的草稿'
            value("document.getElementById('box').value = '下一条消息'; document.getElementById('box').dispatchEvent(new Event('input')); TerminalUI.inputState('retrying', sentOps[0])")
            assert value("document.querySelector('.user.going').textContent.includes('正在确认送达')")
            assert not value("document.querySelector('.user.going').classList.contains('failed')")
            value("TerminalUI.inputState('accepted', sentOps[0])")
            assert value("document.getElementById('box').value") == '下一条消息'
            assert value("document.querySelector('.user.going').textContent.includes('已提交，等待对话接收')")
            value("document.getElementById('write').requestSubmit(); TerminalUI.inputState('unknown', sentOps[1])")
            before_retry = value("sentOps.length")
            value("document.getElementById('write').requestSubmit()")
            assert value("sentOps.length") == before_retry, 'an uncertain delivery cannot be repeated by pressing Send'
            assert value("document.querySelector('.user.failed').textContent.includes('送达结果未知')")
            pending_id = value("sentOps[1].id")
            ask("Page.reload")
            until("document.querySelector('.user.failed')?.textContent.includes('送达结果未知') && document.getElementById('box').value === '下一条消息'", "uncertain receipt after reload")
            assert not any(op.get('id') == pending_id for op in computer.seen), 'stored messages must not replay on reload'
            value("TerminalUI.inputState('accepted', {id: %s})" % json.dumps(pending_id))
            assert value("document.getElementById('box').value") == ''

            # Selecting a live answer freezes only that node until the selection is released.
            computer.emit(terminal, t="item", id="selection-check", kind="text", text="保留这段选中文字")
            until("Array.from(document.querySelectorAll('.text')).some(n => n.textContent.trim() === '保留这段选中文字')", "selection fixture")
            value("window.selectedAnswer = Array.from(document.querySelectorAll('.text')).find(n => n.textContent.trim() === '保留这段选中文字'); var range = document.createRange(); range.selectNodeContents(selectedAnswer.querySelector('p')); getSelection().removeAllRanges(); getSelection().addRange(range)")
            computer.emit(terminal, t="more", id="selection-check", text="，新内容继续到达。")
            until("document.querySelector('.text.writing') === selectedAnswer", "same streaming node")
            time.sleep(0.3)
            assert value("getSelection().toString()") == '保留这段选中文字'
            assert value("selectedAnswer.textContent.trim()") == '保留这段选中文字'
            value("getSelection().removeAllRanges()")
            until("selectedAnswer.textContent.includes('新内容继续到达')", "deferred selected text update")

            # The shared reader retains unchanged native-message nodes, open details and focus.
            assert value("""(() => {
              const root = document.createElement('div'); document.body.append(root);
              const reader = new RemoteConversation.Reader(root);
              const old = document.createElement('details'); old.innerHTML = '<summary>操作</summary><p>完整输出</p>'; old.readKey = 'step'; old.open = true; root.append(old); old.firstChild.focus();
              const fresh = document.createElement('div'); const same = old.cloneNode(true); same.open = false; same.readKey = 'step'; fresh.append(same);
              const answer = document.createElement('p'); answer.textContent = '新回答'; answer.readKey = 'answer'; fresh.append(answer);
              reader.replace(fresh);
              const good = root.firstChild === old && old.open && document.activeElement === old.firstChild && root.children.length === 2;
              reader.dispose(); root.remove(); return good;
            })()""")

            # Narrow phones, large text and desktop share the same readable column.
            for width in (320, 390, 430, 1440):
                ask("Emulation.setDeviceMetricsOverride", width=width, height=844, deviceScaleFactor=1, mobile=width < 500)
                value("document.documentElement.style.fontSize = '19px'")
                sizes = value("({viewport:innerWidth, client:document.documentElement.clientWidth, html:document.documentElement.scrollWidth, body:document.body.scrollWidth})")
                assert sizes['viewport'] == width and sizes['client'] == width, sizes
                assert max(sizes['html'], sizes['body']) <= width, sizes
                picture('chat-width-%d.png' % width)
            ask("Emulation.setDeviceMetricsOverride", width=390, height=844, deviceScaleFactor=2, mobile=True)
            value("document.documentElement.style.fontSize = '15.5px'")

            # ---- ended from the menu: the box goes, and the way to go on with it comes
            # (the menu is the sheet of ../ui/sheet.js: its rows, and a page that asks once before the end)
            sheet_button = "Array.from(document.querySelectorAll('dialog.rs button')).find(b => b.innerText.trim() === %s)"
            value("document.getElementById('menu').click()")
            until("!!document.querySelector('dialog.rs[open]')", "the menu")
            rows = value("Array.from(document.querySelectorAll('dialog.rs .rs-row b, dialog.rs .rs-row.danger > span')).map(b => b.textContent)")
            assert rows[:4] == ["转为协作", "重命名", "重启对话", "在终端中打开"] and rows[-1] == "结束对话", rows
            value(sheet_button % json.dumps("结束对话") + ".click()")
            until("!!document.getElementById('rs-confirm') && document.getElementById('rs-confirm').classList.contains('danger')", "asked once")
            value("document.getElementById('rs-confirm').click()")
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
            until("!!document.querySelector('dialog.rs[open]')", "the menu")
            value(sheet_button % json.dumps("结束对话") + ".click()")
            until("!!document.getElementById('rs-confirm')", "asked once")
            value("document.getElementById('rs-confirm').click()")
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
            until("Array.from(document.querySelectorAll('dialog.rs[open] button')).some(b => b.innerText.trim() === '在手机终端继续')", "the terminal behind a held press")
            value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
            until("!document.querySelector('dialog.rs')", "the sheet closed")
            before = len(computer.seen)
            # it opens in the reader first (8c34d4e), with what was said; 继续对话 goes on with it as a conversation
            value(card + ".querySelector('.open').click()")
            until("(r => !!r && r.innerText.includes('优惠券的金额按两位小数显示了'))(document.querySelector('dialog.rs[open] .saved-reader'))", "the saved conversation in the reader", 20)
            shown = value("document.querySelector('dialog.rs[open] .saved-reader').innerText")
            assert "结算页的金额有时多出一位小数" in shown and "暂无可读内容" not in shown, shown
            assert any(op["action"] == "session_read" and op.get("session") == SESSION for op in computer.seen[before:])
            assert not [op for op in computer.seen[before:] if op["action"] == "start"], "reading starts nothing"
            value("Array.from(document.querySelectorAll('dialog.rs[open] button')).find(b => b.innerText.trim() === '继续对话').click()")
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
