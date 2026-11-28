"""The tasks a conversation gives out, as the phone's pages show them (web/chat/, web/app.js), through the real relay
server with the computer played here: the task's card in the conversation that gave it out, brought up to date from
running to completed in place; the result Remote AI brought back folded rather than shown as the user's words; the
task's own conversation left out of the list while the one that gave it out is there; and the card opening it, its
back arrow coming back. Takes pictures of the conversation and the list.

Usage: python tests/tasks_ui_check.py <Edge/Chrome executable> <screenshot directory>
"""
import base64
import http.client
import json
import os
import secrets
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))
from ws_client import Socket        # noqa: E402
from chat_ui_check import Computer, free_port, http_get        # noqa: E402

PARENT, CHILD = "a" * 32, "b" * 32


class Giver(Computer):
    """A computer with two conversations: one that gave a task out, and the task's."""

    def __init__(self, port, password):
        super().__init__(port, password)
        self.features.append("tasks")
        now = int(time.time() * 1000)
        for tid, tool, session, parent in ((PARENT, "claude", "0199aaaa-0000-7000-8000-000000000001", ""), (CHILD, "codex", "0199bbbb-0000-7000-8000-000000000002", PARENT)):
            self.terminals[tid] = {"id": tid, "state": "running", "tool": tool, "dir": "web-shop", "session": session, "status": "idle", "created": now, "said": "", "ask": "", "seq": 0, "parent": parent,
                                   "task": "c0ffee01" if parent else ""}

    def emit(self, which, **event):      # (a task's card carries a `terminal` of its own)
        with self.lock:
            term = self.terminals[which]
            term["seq"] += 1
            self.output.append({"terminal": which, "seq": term["seq"], "data": json.dumps(event, ensure_ascii=False) + "\n"})

    def report(self):
        with self.lock:
            output, self.output = self.output, []
            acks, self.acks = self.acks, []
            terminals = [{"id": t["id"], "state": t["state"], "tool": t["tool"], "dir": t["dir"], "folder": "", "session": t["session"], "status": t["status"], "created": t["created"],
                          "title": "核对条文" if t["parent"] else "检查结算页", "form": "chat", "said": t["said"], "ask": t["ask"], "cols": 80, "rows": 24, "parent": t["parent"], "task": t["task"]}
                         for t in self.terminals.values()]
        self.call("/api/terminal/agent", {"info": {"instance": self.instance, "enabled": True, "tools": ["claude", "codex"], "workspaces": ["web-shop"], "features": list(self.features),
                                                    "projects": [{"name": "web-shop", "path": "D:\\work\\web-shop"}]},
                                           "terminals": terminals, "output": output, "acks": acks, "sessions": []})


def main():
    chrome, out = sys.argv[1], Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    port, password = free_port(), "test-" + secrets.token_hex(8)
    with tempfile.TemporaryDirectory(prefix="remote-ai-tasks-ui-", ignore_cleanup_errors=True) as temporary:
        relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(port), "--data", str(Path(temporary) / "relay")],
                                 env=dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1"), stdout=subprocess.DEVNULL)
        browser = computer = None
        try:
            for _ in range(60):
                try:
                    computer = Giver(port, password)
                    break
                except OSError:
                    time.sleep(0.25)
            now = int(time.time() * 1000)
            computer.emit(PARENT, t="item", id="u1", kind="user", text="核对一下结算页用到的公开条文", at=now)
            computer.emit(PARENT, t="item", id="s1", kind="step", text="派给 Codex：核对条文", name="mcp", detail="读出 docs/terms.md 并核对", at=now)
            computer.emit(PARENT, t="done", id="s1", ok=True, result="已派出子任务 c0ffee01（Codex · 核对条文）。")
            computer.emit(PARENT, t="task", id="task-c0ffee01", task_id="c0ffee01", tool="codex", title="核对条文", state="running", terminal=CHILD, started=now)
            computer.emit(PARENT, t="item", id="t1", kind="text", text="已经派给 Codex，结果回来后我再整理。", at=now)
            computer.emit(PARENT, t="turn", end="done", at=now)
            computer.emit(CHILD, t="item", id="c1", kind="user", text="【子任务】Claude Code 在另一段对话里把这项工作派给你。", at=now)
            threading.Thread(target=computer.run, daemon=True).start()
            phone = Computer.__new__(Computer)
            phone.port, phone.cookie = port, ""
            phone.call("/api/login", {"password": password})
            for _ in range(60):
                if json.loads(http_get(port, "/api/terminal", phone.cookie))["device"]["online"]:
                    break
                time.sleep(0.2)
            listed = json.loads(http_get(port, "/api/terminal", phone.cookie))
            child = next(t for t in listed["terminals"] if t["id"] == CHILD)
            assert child["parent"] == PARENT and child["task"] == "c0ffee01", child

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
            pipe = Socket("http://127.0.0.1:%d" % debug, "/devtools/page/" + json.loads(connection.getresponse().read())["id"], "")
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
                    if value(code):
                        return
                    time.sleep(0.1)
                raise AssertionError((why, value("document.body.innerText.slice(0, 1200)"), problems[-2:]))

            def picture(name):
                (out / name).write_bytes(base64.b64decode(ask("Page.captureScreenshot", format="png")["data"]))

            ask("Page.enable"); ask("Runtime.enable"); ask("Network.enable")
            ask("Emulation.setDeviceMetricsOverride", width=390, height=844, deviceScaleFactor=2, mobile=True)
            name, cookie = phone.cookie.split("=", 1)
            ask("Network.setCookie", name=name, value=cookie, url="http://127.0.0.1:%d/" % port)
            base = "http://127.0.0.1:%d/" % port

            # the list: the conversation that gave the task out, not the task's
            ask("Page.navigate", url=base)
            until("document.body.innerText.includes('检查结算页')", "the giver in the list", 20)
            assert not value("document.body.innerText.includes('核对条文')"), "the task is listed on its own"
            picture("tasks-list.png")

            # the card, running
            ask("Page.navigate", url=base + "chat/?id=" + PARENT)
            until("document.querySelector('.task')?.dataset.state === 'running'", "the running card")
            assert value("document.querySelector('.task b').textContent") == "Codex · 核对条文"
            assert value("document.querySelector('.task .state').textContent") == "进行中"
            assert value("!!document.querySelector('.task').closest('.steps')"), "the card is a row of the steps"
            assert value("document.querySelector('.step[data-task]').hidden"), "the step that gave the task out gives way to the card"
            picture("tasks-running.png")

            # completed in place, and the result folded
            later = now + 49000
            computer.emit(PARENT, t="task", id="task-c0ffee01", task_id="c0ffee01", tool="codex", title="核对条文", state="completed", terminal=CHILD, started=now, ended=later, said="三条条文与原文一致，没有改动文件。")
            computer.emit(PARENT, t="item", id="u2", kind="user", text="【子任务结果】以下是你派出的子任务的结果，由 Remote AI 送回，不是用户的新要求。\n\n## 子任务 c0ffee01（Codex · 核对条文）已完成，用时 49 秒\n三条条文与原文一致。", at=later)
            computer.emit(PARENT, t="item", id="t2", kind="text", text="Codex 核对完了：三条条文与原文一致。", at=later)
            until("document.querySelector('.task')?.dataset.state === 'completed'", "the completed card")
            assert value("document.querySelectorAll('.task').length") == 1, "one card per task"
            assert value("document.querySelector('.task .state').textContent") == "已完成 · 49 秒"
            assert value("document.querySelector('.task .line').textContent") == "三条条文与原文一致，没有改动文件。"
            until("!!document.querySelector('.relayed')", "the result, folded")
            assert not value("[...document.querySelectorAll('.user')].some(u => u.textContent.includes('子任务结果'))"), "shown as the user's words"
            assert value("document.querySelector('.relayed .what').textContent") == "子任务的结果已送回" and value("document.querySelector('.relayed > .text').hidden")
            value("document.querySelector('.relayed > button').click()")
            until("!document.querySelector('.relayed > .text').hidden && document.querySelector('.relayed > .text').textContent.includes('三条条文与原文一致')", "the result unfolded")
            picture("tasks-completed.png")

            # the card opens the task's conversation; its back arrow comes back
            value("document.querySelector('.task').click()")
            until("location.search.includes('id=" + CHILD + "') && location.search.includes('from=task')", "the task's conversation")
            until("document.querySelector('.user')?.textContent.startsWith('【子任务】')", "the task's first words")
            value("document.getElementById('back').click()")
            until("location.search.includes('id=" + PARENT + "')", "back to the giver")
            assert not problems, problems
            print("tasks ui: list without the task, card running and completed in place, result folded, card opens the task and back ok")
        finally:
            if computer:
                computer.alive = False
            if browser:
                browser.kill()
            relay.kill()


if __name__ == "__main__":
    main()
