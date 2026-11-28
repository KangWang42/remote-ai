"""Tasks a conversation gives out (agent-windows/Delegations.cs), on the real tools, through the real relay and the
real Windows program: a conversation of <giver> is asked to hand a small piece of reading to <taker>; it passes where
the giver called delegate_task (and did not poll it), the task's conversation is listed under the giver, its card
went from running to completed, the result came back to the giver as a 【子任务结果】 message and the giver's next
answer carries what only the task read. With `simple`, the giver is asked something it can do itself, and it passes
where no task was given out. With `worktree`, the task writes a file in a worktree of its own: it passes where the
file is there and not in the project's folder. Costs a few turns of each tool's account; not in CI.

    python tests/delegations_live.py <giver> <taker> [simple]     e.g.  claude codex
"""
import http.client
import json
import secrets
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def free_port():
    with socket.socket() as reserved:
        reserved.bind(("127.0.0.1", 0))
        return reserved.getsockname()[1]


def main():
    giver, taker = sys.argv[1], sys.argv[2]
    simple = len(sys.argv) > 3 and sys.argv[3] == "simple"
    isolated = len(sys.argv) > 3 and sys.argv[3] == "worktree"
    program = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
    port, password, token = free_port(), "test-" + secrets.token_hex(8), "TK-" + secrets.token_hex(4).upper()
    work = ROOT / "09_backup" / "workbench" / "delegations-live"
    work.mkdir(parents=True, exist_ok=True)
    temp = tempfile.TemporaryDirectory(dir=work, prefix=giver + "-" + taker + "-", ignore_cleanup_errors=True)
    data, project = Path(temp.name) / "agent", Path(temp.name) / "scratch"
    data.mkdir()
    (project / "notes").mkdir(parents=True)
    (project / "notes" / "token.txt").write_text(token + "\n", encoding="utf-8")
    (project / "README.md").write_text("# 练习项目\n\n用来试派子任务。\n", encoding="utf-8")
    subprocess.run(["git", "init", "-q", str(project)], check=True)
    subprocess.run(["git", "-C", str(project), "-c", "user.name=t", "-c", "user.email=t@t", "add", "-A"], check=True)
    subprocess.run(["git", "-C", str(project), "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init"], check=True)
    (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % port, "RemoteEnabled": True, "RemoteMaxMode": "full",
                                                  "RemoteDirs": ["scratch=" + str(project)]}), encoding="utf-8")
    environment = dict(__import__("os").environ, RCLI_PASSWORD=password, PYTHONUTF8="1")
    hidden = {"creationflags": subprocess.CREATE_NO_WINDOW}
    relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(port), "--data", str(Path(temp.name) / "relay")],
                             env=environment, stdout=subprocess.DEVNULL, **hidden)
    subprocess.run([str(program), "--set-password", str(data)], input=password.encode(), check=True, **hidden)
    agent = subprocess.Popen([str(program), "--data", str(data)], env=environment, **hidden)
    cookie, log = {}, []
    try:
        def call(path, payload=None, expect=200):
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=60)
            body = None if payload is None else json.dumps(payload).encode()
            connection.request("POST" if body is not None else "GET", path, body=body, headers=dict(cookie, **({"Content-Type": "application/json"} if body else {})))
            reply = connection.getresponse()
            text = reply.read()
            if reply.getheader("Set-Cookie"):
                cookie["Cookie"] = reply.getheader("Set-Cookie").split(";")[0]
            assert reply.status == expect, (path, reply.status, text[:300].decode("utf-8", "replace"))
            return json.loads(text)

        def op(**payload):
            payload.setdefault("id", secrets.token_hex(16))
            answer = call("/api/terminal", payload)
            for _ in range(300):
                if answer.get("state") != "queued":
                    break
                time.sleep(0.1)
                answer = call("/api/terminal", payload)
            assert not answer.get("error"), (payload["action"], answer)
            return answer

        class Read:
            def __init__(self, terminal):
                self.terminal, self.after, self.rest, self.events = terminal, 0, "", []

            def more(self):
                view = call("/api/terminal?terminal=%s&after=%d" % (self.terminal, self.after))
                for chunk in view["chunks"]:
                    self.after = chunk["seq"]
                    self.rest += chunk["data"]
                *whole, self.rest = self.rest.split("\n")
                self.events += [json.loads(line) for line in whole if line.strip()]

            def until(self, test, what, seconds):
                deadline = time.time() + seconds
                while time.time() < deadline:
                    self.more()
                    found = [e for e in self.events if test(e)]
                    if found:
                        return found[-1]
                    time.sleep(0.5)
                raise AssertionError("waited in vain for " + what)

            def texts(self):
                said = {}
                for e in self.events:
                    if e.get("t") in ("item", "more") and e.get("kind", "text") in ("text", "user") or e.get("t") == "more":
                        said[e["id"]] = said.get(e["id"], "") + e.get("text", "")
                return said

        for _ in range(60):
            try:
                call("/api/login", {"password": password})
                break
            except OSError:
                time.sleep(0.5)
        for _ in range(120):
            device = call("/api/terminal")["device"]
            if device["online"] and "tasks" in device.get("features", []):
                break
            time.sleep(0.5)
        assert "tasks" in device["features"], device
        if simple:
            words = "README.md 的第一行写的是什么？直接告诉我。"
        elif isolated:
            words = ("请用 delegate_task 把这件事派给 %s，isolation 设为 worktree：在 notes 文件夹里新建 done.txt，内容写 %s。"
                     "等子任务的结果送回后，告诉我它是在哪个工作树里做的。" % (taker, token))
        else:
            words = ("请用 delegate_task 把这件事派给 %s：读出项目里 notes/token.txt 的内容，原样交回。你自己不要读这个文件。"
                     "等子任务的结果送回后，把文件里的内容原样告诉我。" % taker)
        terminal = op(action="start", tool=giver, dir="scratch", form="chat", text=words)["terminal"]
        read = Read(terminal)
        started = time.time()
        if simple:
            read.until(lambda e: e.get("t") == "turn", "the giver's turn", 300)
            read.more()
            steps = [e for e in read.events if e.get("t") == "item" and e.get("kind") == "step"]
            given = [s for s in steps if s.get("text", "").startswith("派给")]
            log.append("steps: " + " | ".join(s.get("text", "") for s in steps))
            assert not given, "a simple question was given out: " + str(given)
            assert any("练习项目" in t for t in read.texts().values()), "answered itself"
            print("PASS simple", giver, "in", int(time.time() - started), "s;", log[-1])
            return
        card = read.until(lambda e: e.get("t") == "task", "the task's card", 300)
        assert card["tool"] == taker and card["state"] == "running", card
        child = card["terminal"]
        listed = next(t for t in call("/api/terminal")["terminals"] if t["id"] == child)
        assert listed["parent"] == terminal and listed["task"] == card["task_id"], listed
        done = read.until(lambda e: e.get("t") == "task" and e.get("state") in ("completed", "failed"), "the task's end", 600)
        assert done["state"] == "completed", done
        back = read.until(lambda e: e.get("t") == "item" and e.get("kind") == "user" and e.get("text", "").startswith("【子任务结果】"), "the result told", 300)
        if isolated:
            tree = Path(done["worktree"])
            assert (tree / "notes" / "done.txt").read_text(encoding="utf-8").strip() == token, done
            assert not (project / "notes" / "done.txt").exists(), "the task wrote in the project's folder"
            assert str(tree) in back["text"], back["text"][:500]
            print("PASS worktree", giver, "->", taker, "in", int(time.time() - started), "s; at", tree)
            return
        assert token in back["text"], back["text"][:500]
        # the giver's answer after the result carries what only the task read
        deadline = time.time() + 300
        while time.time() < deadline:
            read.more()
            texts = read.texts()
            later = [t for i, t in texts.items() if token in t and not t.startswith("【子任务结果】") and not t.startswith("请用 delegate_task")]
            if later:
                break
            time.sleep(1)
        else:
            raise AssertionError("the giver did not report the token after the result")
        steps = [e.get("text", "") for e in read.events if e.get("t") == "item" and e.get("kind") == "step"]
        polled = [s for s in steps if s.startswith("查看子任务")]
        read_itself = [s for s in steps if "token.txt" in s and not s.startswith("派给")]
        log.append("steps: " + " | ".join(steps))
        assert not read_itself, "the giver read the file itself: " + str(read_itself)
        print("PASS", giver, "->", taker, "in", int(time.time() - started), "s; polled", len(polled), "times;", log[-1])
    finally:
        try:
            for t in call("/api/terminal")["terminals"]:
                if t["state"] == "running":
                    call("/api/terminal", {"id": secrets.token_hex(16), "action": "close", "terminal": t["id"]})
        except Exception:
            pass
        time.sleep(2)
        agent.kill(); relay.kill()
        print("\n".join(log))


if __name__ == "__main__":
    main()
