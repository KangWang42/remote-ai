"""Tests for the Linux agent: pure-logic unit tests, and an end-to-end test that runs the
real relay (../relay/server.py) with a real agent subprocess and speaks the viewer protocol
to it, the way the phone does.

    python3 -m unittest discover -s agent-linux -p "test_*.py"
"""
import base64
import json
import os
import re
import secrets
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.error
import urllib.request
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
sys.path.insert(0, HERE)

import agent  # noqa: E402

PASSWORD = "unit-test-password-8f2c"

# Raw pty output interleaves echo, prompts and redraw escapes; match on the plain text.
ANSI = re.compile(r"\x1b\[[0-9;?<=>]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]|\r")


def plain(text):
    return ANSI.sub("", text)


def request(method, url, payload=None, token=None, timeout=10):
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            return response.status, json.loads(response.read().decode("utf-8") or "{}")
    except urllib.error.HTTPError as error:
        with error:
            return error.code, json.loads(error.read().decode("utf-8") or "{}")


def stat_mode(path):
    return os.stat(path).st_mode & 0o777


def prompted(text):
    """A shell prompt has arrived: `$` for a person, `#` for root."""
    return "$" in plain(text) or "#" in plain(text)


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


class Pure(unittest.TestCase):
    """The pieces that need no network and no processes."""

    def test_normal_folder(self):
        self.assertEqual(agent.normal_folder("/home/you/demo "), "/home/you/demo")
        self.assertEqual(agent.normal_folder("/a/./b/../c"), "/a/c")
        for bad in ("demo", "relative/path", "/tmp/*", "", "/x" * 130, "/home/\x01"):
            with self.assertRaises(agent.OpError):
                agent.normal_folder(bad)

    def test_tidy(self):
        self.assertEqual(agent.tidy("  a<b>bad</b>c  "), "a bad c")
        self.assertEqual(agent.tidy("x\x01y"), "x y")
        self.assertEqual(len(agent.tidy("长" * 50, 40)), 40)
        self.assertTrue(agent.tidy("长" * 50, 40).endswith("…"))

    def test_parse_dirs(self):
        tmp = tempfile.mkdtemp()
        self.assertEqual(agent.parse_dirs({}), {})
        dirs = agent.parse_dirs({"RemoteDirs": ["名字=" + tmp, "bad", "=x", "d={" + tmp + "}"]})
        self.assertEqual(dirs, {"名字": tmp})
        dicts = agent.parse_dirs({"RemoteDirs": [{"name": "n", "path": tmp}, {"name": "gone", "path": "/no/such"}]})
        self.assertEqual(dicts, {"n": tmp})

    def test_check_op(self):
        good = {"id": secrets.token_hex(16), "terminal": secrets.token_hex(16), "at": time.time()}
        self.assertEqual(agent.check_op(good)[0], good["id"])
        with self.assertRaises(agent.OpError):
            agent.check_op({"id": "XYZ", "at": time.time()})
        with self.assertRaises(agent.OpError):
            agent.check_op({"id": secrets.token_hex(16), "at": time.time() - 500})
        with self.assertRaises(agent.OpError):
            agent.check_op({"id": secrets.token_hex(16), "at": "not-a-number"})
        # project operations carry no terminal
        self.assertEqual(agent.check_op({"id": secrets.token_hex(8), "at": time.time()},
                                        need_terminal=False)[1], "")

    def test_seal_chunks(self):
        live = agent.Live.__new__(agent.Live)     # no process: only the buffer parts
        live.id = "t" * 32
        live.lock = __import__("threading").Lock()
        live.pending, live.output, live.seq, live.out_bytes = [], [], 0, 0
        live.pending.append("字" * 20000)          # 20000 chars -> a 12000 and a 8000 piece
        agent.seal(live)
        self.assertEqual([len(c["data"]) for c in live.output], [12000, 8000])
        self.assertEqual([c["seq"] for c in live.output], [1, 2])
        self.assertEqual("".join(c["data"] for c in live.output), "字" * 20000)
        self.assertEqual(live.pending, [])

    def test_dispose_waits_for_inflight_read(self):
        import codecs
        import threading
        from unittest import mock

        reading, release, attempted = threading.Event(), threading.Event(), threading.Event()
        acquired = []
        lock = threading.Lock()

        class ObservedLock:
            def __enter__(self):
                if threading.current_thread().name == "dispose-check":
                    immediate = lock.acquire(False)
                    acquired.append(immediate)
                    attempted.set()
                    if immediate:
                        return self
                lock.acquire()
                return self

            def __exit__(self, *args):
                lock.release()

        live = agent.Live.__new__(agent.Live)
        live.fd, other = os.pipe()
        live.lock, live.stopping, live.drained = ObservedLock(), False, False
        live.pending, live.decoder, live.wake = [], codecs.getincrementaldecoder("utf-8")(), lambda: None

        def read(fd, size):
            reading.set()
            if not release.wait(5):
                raise OSError("read test timed out")
            return b""

        reader = threading.Thread(target=live._read)
        disposer = threading.Thread(target=live.dispose, name="dispose-check")
        try:
            with mock.patch.object(agent.select, "select", return_value=([live.fd], [], [])), \
                    mock.patch.object(agent.os, "read", side_effect=read):
                reader.start()
                try:
                    self.assertTrue(reading.wait(3), "reader reached the descriptor")
                    disposer.start()
                    self.assertTrue(attempted.wait(3), "dispose attempted the same lock")
                    self.assertEqual(acquired, [False], "dispose must not recycle a descriptor being read")
                finally:
                    release.set()
                    reader.join(5)
                    if disposer.ident is not None:
                        disposer.join(5)
            self.assertFalse(reader.is_alive() or disposer.is_alive())
            self.assertTrue(live.drained and live.stopping)
            self.assertEqual(live.fd, -1)
        finally:
            live.dispose()
            os.close(other)

    def test_qoder_read_mode(self):
        self.assertEqual(agent.tool_argv("qoder", "qoder", "", False, "read", False),
                         ["qoder", "--permission-mode", "plan"])

    def test_output_batch_limits(self):
        import threading
        def fake(data, count):
            live = agent.Live.__new__(agent.Live)
            live.id = "t" * 32
            live.lock = threading.Lock()
            live.pending = []
            live.output = [{"terminal": live.id, "seq": n, "data": data} for n in range(1, count + 1)]
            live.seq = count
            return live
        # many small pieces: the piece count is what stops the batch — 30 per terminal,
        # as the Windows agent also only seals 30 of each into one report
        lives = [fake("x" * 100, 40) for _ in range(6)]
        batch = agent.output_batch(lives)
        self.assertEqual(len(batch), agent.BATCH_CHUNKS)
        # few huge pieces: the encoded bytes are what stop it
        batch = agent.output_batch([fake("x" * 400000, 3)])
        self.assertLess(len(batch), 3)
        self.assertLessEqual(sum(len(json.dumps(c, ensure_ascii=False).encode()) for c in batch),
                             agent.BATCH_BYTES)

    def test_projects_change(self):
        import threading
        root = tempfile.mkdtemp()
        data = tempfile.mkdtemp()
        projects = agent.Projects(data)
        cfg = {"RemoteDirs": ["固定=" + root]}
        terminals = {}

        def live_for(name):                       # as start_terminal makes it: name and folder
            t = agent.Live.__new__(agent.Live)
            t.project, t.dir, t.closed = name, root + "/" + name, True
            return t

        terminals["a"] = live_for("手机")
        os.makedirs(root + "/手机")
        projects.change({"path": root + "/手机", "name": "手机"}, "project_add", cfg, terminals)
        self.assertEqual([i["name"] for i in projects.own], ["手机"])
        # a running terminal blocks removal
        terminals["a"].closed = False
        with self.assertRaises(agent.OpError):
            projects.change({"name": "手机"}, "project_remove", cfg, terminals)
        terminals["a"].closed = True
        projects.change({"name": "手机", "to": "改名"}, "project_rename", cfg, terminals)
        self.assertEqual(projects.own[0]["name"], "改名")
        # the terminal follows the rename, so removal is still held back while it runs
        self.assertEqual(terminals["a"].project, "改名")
        terminals["a"].closed = False
        with self.assertRaises(agent.OpError):
            projects.change({"name": "改名"}, "project_remove", cfg, terminals)
        terminals["a"].closed = True
        # a fixed project cannot be touched from the phone
        with self.assertRaises(agent.OpError):
            projects.change({"name": "固定"}, "project_remove", cfg, terminals)
        # create makes the folder; a second folder with the same basename gets a numbered name
        os.makedirs(root + "/子")
        projects.change({"path": root + "/新建", "create": True}, "project_add", cfg, terminals)
        projects.change({"path": root + "/子/新建", "create": True}, "project_add", cfg, terminals)
        self.assertTrue(os.path.isdir(root + "/新建"))
        self.assertEqual([i["name"] for i in projects.own], ["改名", "新建", "新建 2"])
        projects.change({"name": "新建 2"}, "project_remove", cfg, terminals)
        # persistence across a fresh instance
        again = agent.Projects(data)
        self.assertEqual([i["name"] for i in again.own], ["改名", "新建"])

    def test_files(self):
        root = tempfile.mkdtemp()
        outside = tempfile.mkdtemp()
        os.makedirs(root + "/子/深")
        with open(root + "/子/文件.bin", "wb") as stream:
            stream.write(b"x" * (agent.FILE_PIECE + 10))
        with open(root + "/.hidden", "w") as stream:
            stream.write("h")
        with open(outside + "/secret", "w") as stream:
            stream.write("s")
        os.symlink(outside, root + "/link")
        os.symlink(outside + "/secret", root + "/子/leak")
        os.mkfifo(root + "/pipe")
        listed = agent.files(root, "", "file_list", 0)
        names = {e["name"]: e for e in listed["entries"]}
        self.assertEqual(listed["path"], "")
        self.assertEqual(set(names), {"子", ".hidden", "link", "pipe"})
        self.assertTrue(names["子"]["dir"] and names[".hidden"]["hidden"] and not names["子"]["hidden"])
        inner = agent.files(root, "子/", "file_list", 0)
        self.assertEqual(inner["path"], "子")
        self.assertEqual({e["name"]: e["size"] for e in inner["entries"]}["文件.bin"], agent.FILE_PIECE + 10)
        # a file comes in pieces; the second piece ends it
        first = agent.files(root, "子/文件.bin", "file_read", 0)
        self.assertEqual((len(base64.b64decode(first["data"])), first["end"], first["size"]),
                         (agent.FILE_PIECE, False, agent.FILE_PIECE + 10))
        rest = agent.files(root, "子/文件.bin", "file_read", agent.FILE_PIECE)
        self.assertEqual((base64.b64decode(rest["data"]), rest["end"]), (b"x" * 10, True))
        # nothing outside the project, by name or through a link; no pipe that would hold the agent
        for path, action in (("../x", "file_list"), ("子/../../x", "file_read"), ("link", "file_list"),
                             ("link/secret", "file_read"), ("子/leak", "file_read"), ("pipe", "file_read"),
                             ("子", "file_read"), ("子/文件.bin", "file_list"), ("没有", "file_read")):
            with self.assertRaises(agent.OpError, msg=path):
                agent.files(root, path, action, 0)
        with self.assertRaises(agent.OpError):
            agent.files(root, ".hidden", "file_read", 5)

    def test_tool_argv(self):
        sid = "11111111-2222-3333-4444-555555555555"
        self.assertEqual(agent.tool_argv("claude", "/b/claude", sid, False, "", True), ["/b/claude", "--session-id", sid])
        self.assertEqual(agent.tool_argv("claude", "/b/claude", sid, False, "read", False),
                         ["/b/claude", "--resume", sid, "--permission-mode", "plan"])
        self.assertEqual(agent.tool_argv("claude", "/b/claude", "", True, "edit", False),
                         ["/b/claude", "--resume", "--permission-mode", "acceptEdits"])
        self.assertEqual(agent.tool_argv("codex", "/b/codex", sid, False, "", False), ["/b/codex", "resume", sid])
        self.assertEqual(agent.tool_argv("codex", "/b/codex", "", False, "read", False), ["/b/codex", "-s", "read-only"])
        self.assertEqual(agent.tool_argv("opencode", "/b/opencode", "ses_abc123", False, "", False),
                         ["/b/opencode", "--session", "ses_abc123"])
        self.assertEqual(agent.tool_argv("opencode", "/b/opencode", "", True, "edit", False),
                         ["/b/opencode", "--continue", "--auto"])
        self.assertEqual(agent.tool_argv("opencode", "/b/opencode", "", False, "read", False), ["/b/opencode"])
        self.assertEqual(agent.tool_argv("qoder", "/b/qoder", sid, False, "", True), ["/b/qoder", "--session-id", sid])
        self.assertEqual(agent.tool_argv("qoder", "/b/qoder", sid, False, "read", False),
                         ["/b/qoder", "--resume", sid, "--permission-mode", "plan"])
        self.assertEqual(agent.tool_argv("qoder", "/b/qoder", "", True, "edit", False),
                         ["/b/qoder", "--continue", "--permission-mode", "accept_edits"])
        self.assertEqual(agent.tool_argv("cline", "/b/cline", "1792116013202_ffehs", False, "", False),
                         ["/b/cline", "--id", "1792116013202_ffehs"])
        self.assertEqual(agent.tool_argv("cline", "/b/cline", "", False, "read", False), ["/b/cline", "--plan"])
        self.assertEqual(agent.tool_argv("cline", "/b/cline", "", False, "", False), ["/b/cline"])
        self.assertTrue(agent.session_id_ok("opencode", "ses_ebd8614d0ffe4Z18FfuXFMXIFJ"))
        self.assertTrue(agent.session_id_ok("qoder", sid))
        self.assertTrue(agent.session_id_ok("cline", "1792116013202_ffehs"))
        self.assertFalse(agent.session_id_ok("cline", sid))
        self.assertFalse(agent.session_id_ok("opencode", sid))
        self.assertTrue(agent.session_id_ok("claude", sid))

    def test_sessions(self):
        home = tempfile.mkdtemp()
        project = tempfile.mkdtemp() + "/我的 项目"
        os.makedirs(project)
        saved = os.path.join(home, ".claude", "projects", re.sub("[^a-zA-Z0-9]", "-", project))
        os.makedirs(saved)
        named, asked, empty, codex, helper = (str(uuid.uuid4()) for _ in range(5))
        with open(os.path.join(saved, named + ".jsonl"), "w", encoding="utf-8") as stream:
            stream.write(json.dumps({"type": "user", "message": {"role": "user", "content": "第一句"}}) + "\n")
            stream.write(json.dumps({"type": "ai-title", "aiTitle": "修好 登录", "sessionId": named}) + "\n")
        with open(os.path.join(saved, asked + ".jsonl"), "w", encoding="utf-8") as stream:
            stream.write(json.dumps({"type": "user", "isMeta": True, "message": {"role": "user", "content": "meta"}}) + "\n")
            stream.write(json.dumps({"type": "user", "message": {"role": "user", "content": [{"type": "text", "text": "帮我看看"}]}}) + "\n")
        with open(os.path.join(saved, empty + ".jsonl"), "w", encoding="utf-8") as stream:
            stream.write(json.dumps({"type": "mode", "mode": "x"}) + "\n")
        with open(os.path.join(saved, "not-a-conversation.jsonl"), "w") as stream:
            stream.write("{}\n")
        rollouts = os.path.join(home, ".codex", "sessions", "2026", "10", "09")
        os.makedirs(rollouts)
        with open(os.path.join(rollouts, "rollout-2026-10-09T10-00-00-%s.jsonl" % codex), "w", encoding="utf-8") as stream:
            stream.write(json.dumps({"type": "session_meta", "payload": {"id": codex, "cwd": project}}) + "\n")
        with open(os.path.join(rollouts, "rollout-2026-10-09T10-00-01-%s.jsonl" % helper), "w", encoding="utf-8") as stream:
            stream.write(json.dumps({"type": "session_meta", "payload": {"id": helper, "cwd": project, "parent_thread_id": codex}}) + "\n")
        with open(os.path.join(home, ".codex", "session_index.jsonl"), "w", encoding="utf-8") as stream:
            stream.write(json.dumps({"id": codex, "thread_name": "部署脚本"}) + "\n")
        sessions = agent.Sessions()
        sessions.home = home
        before = os.environ.pop("CODEX_HOME", None)
        try:
            found = {s["id"]: s for s in sessions.scan({"项目": project, "别的": "/no/such"}, [])}
        finally:
            if before is not None:
                os.environ["CODEX_HOME"] = before
        self.assertEqual(set(found), {named, asked, codex})
        self.assertEqual((found[named]["title"], found[named]["tool"], found[named]["dir"], found[named]["live"]),
                         ("修好 登录", "claude", "项目", False))
        self.assertEqual(found[asked]["title"], "帮我看看")
        self.assertEqual((found[codex]["title"], found[codex]["tool"]), ("部署脚本", "codex"))
        self.assertIsNotNone(sessions.known(named, "claude", "项目"))
        self.assertIsNone(sessions.known(named, "codex", "项目"))

        # what was said, for the phone to look at before it does anything to the program
        with open(os.path.join(saved, named + ".jsonl"), "a", encoding="utf-8") as stream:
            for row in (
                {"type": "user", "message": {"role": "user", "content": "<system-reminder>不给人看</system-reminder>"}},
                {"type": "user", "isSidechain": True, "message": {"role": "user", "content": "子任务"}},
                {"type": "assistant", "message": {"role": "assistant", "content": [
                    {"type": "text", "text": "我先看看登录的代码。"},
                    {"type": "tool_use", "name": "Bash", "input": {"command": "grep -rn login src"}}]}},
                {"type": "user", "message": {"role": "user", "content": [{"type": "tool_result", "content": "很长的输出"}]}},
                {"type": "assistant", "message": {"role": "assistant", "content": [{"type": "text", "text": "长" * 3000}]}},
            ):
                stream.write(json.dumps(row, ensure_ascii=False) + "\n")
        dirs = {"项目": project}
        got = sessions.read(named, "claude", "项目", dirs)
        self.assertEqual(got["title"], "修好 登录")
        self.assertEqual([(m["role"], m["text"][:14]) for m in got["messages"]], [
            ("user", "第一句"), ("assistant", "我先看看登录的代码。"), ("tool", "Bash：grep -rn "), ("assistant", "长" * 14)])
        self.assertEqual(len(got["messages"][-1]["text"]), agent.SAID_CHARS)
        self.assertFalse(got["more"])
        # A conversation at work is mostly things done. What was said between them is given all the same: the forty
        # are counted of what was said; of a long run of things done the last thirty, with how many came before.
        # What a tool gave back is long, and is passed over unread (here it is not even whole JSON).
        busy = os.path.join(home, "busy.jsonl")
        says = lambda text: json.dumps({"type": "assistant", "message": {"role": "assistant", "content": [{"type": "text", "text": text}]}}, ensure_ascii=False)
        does = lambda n: json.dumps({"type": "assistant", "message": {"role": "assistant", "content": [{"type": "tool_use", "name": "Bash", "input": {"command": "step %d" % n}}]}})
        back = '{"type":"user","message":{"role":"user","content":[{"tool_use_id":"t","type":"tool_result","content":"' + "x" * 9000
        lines = [json.dumps({"type": "user", "message": {"role": "user", "content": "开始吧"}}, ensure_ascii=False), says("先看看现状。")]
        for n in range(1, 46):
            lines += [does(n), back]
        lines.append(says("找到原因了。"))
        for n in range(46, 51):
            lines += [does(n), back]
        lines.append(says("改好了。"))
        with open(busy, "w", encoding="utf-8") as stream:
            stream.write("\n".join(lines) + "\n")
        work = agent.said(busy, "claude")
        shown = ["%s:%s" % (m["role"], m["text"]) for m in work["messages"]]
        self.assertEqual((len(shown), shown[:4], shown[32:34], shown[-1], work["more"]),
                         (40, ["user:开始吧", "assistant:先看看现状。", "tool:更早的 15 个操作", "tool:Bash：step 16"], ["tool:Bash：step 45", "assistant:找到原因了。"], "assistant:改好了。", True))
        self.assertEqual(agent.talk([], 40, 30), [])
        # What the tool said of its work between its steps (the summary Claude Code keeps of its thinking, where it
        # keeps one) is given; a step is told by what it is for, where the tool says so.
        silent = os.path.join(home, "silent.jsonl")
        part = lambda piece: json.dumps({"type": "assistant", "message": {"role": "assistant", "content": [piece]}}, ensure_ascii=False)
        with open(silent, "w", encoding="utf-8") as stream:
            stream.write("\n".join([json.dumps({"type": "user", "message": {"role": "user", "content": "比一下两个版本"}}, ensure_ascii=False),
                                    part({"type": "thinking", "thinking": "", "signature": "x"}),
                                    part({"type": "tool_use", "id": "t1", "name": "Bash", "input": {"command": "python compare.py", "description": "对比 4.0 和 2.0 的结果"}}),
                                    json.dumps({"type": "user", "message": {"role": "user", "content": [{"tool_use_id": "t1", "type": "tool_result", "content": "step 1 same\nstep 3 differs\n"}]}}),
                                    part({"type": "thinking", "thinking": "已对比：差别在第三步。"}),
                                    part({"type": "tool_use", "id": "t2", "name": "Read", "input": {"file_path": "step3.R"}}),
                                    # what came back is long, and its row is not read: its first lines are taken all the same
                                    '{"type":"user","message":{"role":"user","content":[{"tool_use_id":"t2","type":"tool_result","content":[{"type":"text","text":"     1\\tlibrary(\\"dplyr\\")\\n     2\\t# \\u4e2d\\n' + "".join("     %d\\tx <- %d\\n" % (n, n) for n in range(3, 400)) + '"}]}]}}',
                                    part({"type": "thinking", "thinking": "想" * 700}),
                                    part({"type": "text", "text": "差别在第三步。"})]) + "\n")
        shown = ["%s:%s" % (m["role"], m["text"] if len(m["text"]) < 30 else len(m["text"])) for m in agent.said(silent, "claude")["messages"]]
        self.assertEqual(shown, ["user:比一下两个版本", "tool:Bash：对比 4.0 和 2.0 的结果", "thought:已对比：差别在第三步。", "tool:Read：step3.R", "thought:%d" % agent.SAID_NOTED, "assistant:差别在第三步。"])
        # a step as a terminal would show it: the command itself where it is told by what it is for, and the first lines of what came back
        steps = [m for m in agent.said(silent, "claude")["messages"] if m["role"] == "tool"]
        self.assertEqual((steps[0].get("detail"), steps[0].get("result")), ("python compare.py", "step 1 same\nstep 3 differs"))
        self.assertEqual((steps[1].get("detail"), steps[1].get("result")), (None, '     1\tlibrary("dplyr")\n     2\t# 中\n     3\tx <- 3\n     4\tx <- 4\n     5\tx <- 5\n     6\tx <- 6\n…'))
        self.assertEqual((agent.first("a\n" + "x" * 300 + "\nb"), agent.first(" \n\n"), agent.begins('{"type":"tool_result"}', 1)), ("a\n" + "x" * 200 + "…\nb", "", ""))
        # Codex's steps by what they were, as it notes them for its own window; the calls that only wrap them are passed over
        stepped = os.path.join(home, "stepped.jsonl")
        item = lambda **noted: json.dumps({"type": "event_msg", "payload": {"type": "item_completed", "item": noted}}, ensure_ascii=False)
        with open(stepped, "w", encoding="utf-8") as stream:
            stream.write("\n".join([json.dumps({"type": "response_item", "payload": {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "比一下"}]}}, ensure_ascii=False),
                                    json.dumps({"type": "response_item", "payload": {"type": "reasoning", "summary": [{"type": "summary_text", "text": "**对比两个版本**"}]}}, ensure_ascii=False),
                                    json.dumps({"type": "response_item", "payload": {"type": "custom_tool_call", "name": "exec", "input": "x"}}),
                                    item(type="CommandExecution", command=["sh", "-c", "Rscript compare.R"], parsed_cmd=[{"type": "unknown", "cmd": "Rscript compare.R"}]),
                                    item(type="CommandExecution", command=["cat", "a.R"], parsed_cmd=[{"type": "read", "cmd": "cat a.R", "name": "a.R", "path": "/work/a.R"}]),
                                    item(type="CommandExecution", command=["git", "status"], parsed_cmd=[], aggregated_output="fatal: not a git repository\n", exit_code=128),
                                    item(type="FileChange", changes={"/work/a.R": {"type": "update"}, "/work/src/b.R": {"type": "add"}}),
                                    json.dumps({"type": "response_item", "payload": {"type": "function_call", "name": "request_user_input", "arguments": "{}"}}),
                                    json.dumps({"type": "response_item", "payload": {"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": "差别在第三步。"}]}}, ensure_ascii=False)]) + "\n")
        self.assertEqual(["%s:%s" % (m["role"], m["text"]) for m in agent.said(stepped, "codex")["messages"]],
                         ["user:比一下", "thought:**对比两个版本**", "tool:运行：Rscript compare.R", "tool:读取：a.R", "tool:运行：git status", "tool:修改：a.R、b.R", "tool:request_user_input", "assistant:差别在第三步。"])
        self.assertEqual(agent.said(stepped, "codex")["messages"][4].get("result"), "（退出码 128）\nfatal: not a git repository")
        with open(os.path.join(rollouts, "rollout-2026-10-09T10-00-00-%s.jsonl" % codex), "a", encoding="utf-8") as stream:
            for payload in (
                {"type": "message", "role": "developer", "content": [{"type": "input_text", "text": "规则"}]},
                {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "<environment_context>x</environment_context>"},
                                                                 {"type": "input_text", "text": "部署一下"}]},
                {"type": "function_call", "name": "shell", "arguments": "{}"},
                {"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": "部署好了"}]},
            ):
                stream.write(json.dumps({"type": "response_item", "payload": payload}, ensure_ascii=False) + "\n")
        before = os.environ.pop("CODEX_HOME", None)
        try:
            got = sessions.read(codex, "codex", "项目", dirs)
        finally:
            if before is not None:
                os.environ["CODEX_HOME"] = before
        self.assertEqual([(m["role"], m["text"]) for m in got["messages"]],
                         [("user", "部署一下"), ("tool", "shell"), ("assistant", "部署好了")])
        for wrong in ((named, "codex", "项目"), (empty, "claude", "项目"), ("not-an-id", "claude", "项目"), (named, "claude", "别的")):
            with self.assertRaises(agent.OpError):
                sessions.read(*wrong, dirs)

    def test_opencode_sessions(self):
        home = tempfile.mkdtemp()
        project = tempfile.mkdtemp() + "/项目"
        os.makedirs(project)
        data = tempfile.mkdtemp()                      # stands in for XDG_DATA_HOME
        os.makedirs(os.path.join(data, "opencode"))
        con = sqlite3.connect(os.path.join(data, "opencode", "opencode.db"))
        con.execute("create table project (id text, worktree text)")
        con.execute("create table session_v2 (id text, project_id text, directory text, title text, "
                    "time_updated integer, time_archived text)")
        con.execute("create table session_message (id text, session_id text, type text, seq integer, data text)")
        con.execute("insert into project values ('p1', ?)", (project,))
        con.execute("insert into session_v2 values (?, 'p1', ?, '修登录', 1792107587487, null)", ("ses_" + "a" * 26, project))
        con.execute("insert into session_v2 values (?, 'p1', ?, '', 1792100000000, null)", ("ses_" + "b" * 26, project))
        con.execute("insert into session_v2 values (?, 'p1', ?, '归档的', 1792100000000, 'x')", ("ses_" + "c" * 26, project))
        con.executemany("insert into session_message values (?, ?, ?, ?, ?)", [
            ("m1", "ses_" + "a" * 26, "user", 1, json.dumps({"time": {"created": 1}, "text": "登录报错"})),
            ("m2", "ses_" + "a" * 26, "assistant", 2, json.dumps({"time": {"created": 2}, "content": [
                {"type": "reasoning", "text": "先想"}, {"type": "text", "text": "看一下登录代码"}]})),
        ])
        con.commit()
        con.close()
        sessions = agent.Sessions()
        sessions.home = home
        before = os.environ.get("XDG_DATA_HOME")
        os.environ["XDG_DATA_HOME"] = data
        try:
            found = {s["id"]: s for s in sessions.scan({"项目": project, "别的": "/no/such"}, [])}
            got = sessions.read("ses_" + "a" * 26, "opencode", "项目", {"项目": project})
        finally:
            if before is None:
                os.environ.pop("XDG_DATA_HOME", None)
            else:
                os.environ["XDG_DATA_HOME"] = before
        self.assertEqual(set(found), {"ses_" + "a" * 26, "ses_" + "b" * 26})       # the archived one is out
        self.assertEqual((found["ses_" + "a" * 26]["title"], found["ses_" + "a" * 26]["tool"],
                          found["ses_" + "a" * 26]["dir"], found["ses_" + "a" * 26]["live"]),
                         ("修登录", "opencode", "项目", False))
        self.assertTrue(found["ses_" + "b" * 26]["title"].startswith("Opencode 对话 "))
        self.assertEqual(got["title"], "修登录")
        self.assertEqual([(m["role"], m["text"]) for m in got["messages"]],
                         [("user", "登录报错"), ("assistant", "看一下登录代码")])

    def test_qoder_sessions(self):
        home = tempfile.mkdtemp()
        project = tempfile.mkdtemp() + "/项目"
        os.makedirs(project)
        saved = os.path.join(home, ".qoder", "projects", re.sub("[^a-zA-Z0-9]", "-", project))
        os.makedirs(os.path.join(saved, "11111111-2222-3333-4444-555555555555"))
        os.makedirs(os.path.join(saved, "aaaaaaaa-0000-0000-0000-000000000001"))
        with open(os.path.join(saved, "aaaaaaaa-0000-0000-0000-000000000001.jsonl"), "w", encoding="utf-8") as stream:
            stream.write(json.dumps({"type": "workspace-directories", "sessionId":
                                     "aaaaaaaa-0000-0000-0000-000000000001", "directories": [project]}) + "\n")
            stream.write(json.dumps({"type": "user", "message": {"role": "user", "content": [
                {"type": "text", "text": "第一行\n第二行"}]}}) + "\n")
            stream.write(json.dumps({"type": "assistant", "message": {"role": "assistant", "content": [
                {"type": "thinking", "thinking": "想"},
                {"type": "text", "text": "部署好了"},
                {"type": "tool_use", "name": "Bash", "input": {"command": "ls"}}]}}) + "\n")
        with open(os.path.join(saved, "11111111-2222-3333-4444-555555555555", "state.json"), "w",
                  encoding="utf-8") as stream:
            json.dump({"sessionId": "11111111-2222-3333-4444-555555555555", "updatedAt":
                       "2026-10-09T03:54:27.349Z", "data": {}, "items": {},
                       "workspaceDirectories": [project]}, stream)
        sessions = agent.Sessions()
        sessions.home = home
        found = {s["id"]: s for s in sessions.scan({"项目": project, "别的": "/no/such"}, [])}
        self.assertEqual(set(found), {"aaaaaaaa-0000-0000-0000-000000000001", "11111111-2222-3333-4444-555555555555"})
        self.assertEqual((found["aaaaaaaa-0000-0000-0000-000000000001"]["title"],
                          found["aaaaaaaa-0000-0000-0000-000000000001"]["tool"],
                          found["aaaaaaaa-0000-0000-0000-000000000001"]["dir"]),
                         ("第一行", "qoder", "项目"))
        self.assertTrue(found["11111111-2222-3333-4444-555555555555"]["title"].startswith("Qoder 对话 "))
        self.assertEqual(found["11111111-2222-3333-4444-555555555555"]["updated"],
                         agent.iso_ms("2026-10-09T03:54:27.349Z"))     # the store's own stamp, in UTC
        got = sessions.read("aaaaaaaa-0000-0000-0000-000000000001", "qoder", "项目", {"项目": project})
        self.assertEqual([(m["role"], m["text"]) for m in got["messages"]],
                         [("user", "第一行\n第二行"), ("thought", "想"), ("assistant", "部署好了"), ("tool", "Bash：ls")])

    def test_cline_sessions(self):
        home = tempfile.mkdtemp()
        project = tempfile.mkdtemp() + "/项目"
        os.makedirs(project)
        data = tempfile.mkdtemp()                      # stands in for CLINE_HOME
        os.makedirs(os.path.join(data, "data", "db"))
        session = "1700000000000_fixture"        # an id no real cline of this machine holds
        search = sqlite3.connect(os.path.join(data, "data", "db", "session-search.db"))
        search.execute("create table session_search (session_id text, document_id text, ordinal integer, "
                       "role text, started_at text, workspace_root text, title text, content text)")
        search.executemany("insert into session_search values (?, ?, ?, ?, ?, ?, ?, ?)", [
            (session, "d1", 1, "session", "2026-10-16T02:00:13.210Z", project, "评估 CI 建构", ""),
            (session, "d2", 2, "user", "2026-10-16T02:00:13.210Z", project, "",
             'text\n<user_input mode="act">评估 CI 建构是否可以替代本地</user_input>'),
            (session, "d3", 3, "assistant", "2026-10-16T02:00:14.000Z", project, "",
             'text\n正在清点本地占用。\ntool_use\ncall_01\nrun_commands\npwd'),
            (session, "d4", 4, "user", "2026-10-16T02:00:15.000Z", project, "",
             "tool_result\ncall_01\n/home/doro"),
        ])
        search.commit()
        search.close()
        runs = sqlite3.connect(os.path.join(data, "data", "db", "sessions.db"))
        runs.execute("create table sessions (session_id text, source text, pid integer, started_at text, "
                     "ended_at text, exit_code integer, status text, status_lock integer, interactive integer, provider text)")
        runs.execute("insert into sessions values (?, 'cli', 999999999, ?, null, null, 'idle', 0, 1, 'cline')",
                     (session, "2026-10-16T02:00:13.210Z"))     # a pid no linux can have: dead, not reused
        runs.commit()
        runs.close()
        sessions = agent.Sessions()
        sessions.home = home
        before = os.environ.get("CLINE_HOME")
        os.environ["CLINE_HOME"] = data
        try:
            found = {s["id"]: s for s in sessions.scan({"项目": project, "别的": "/no/such"}, [])}
            got = sessions.read(session, "cline", "项目", {"项目": project})
        finally:
            if before is None:
                os.environ.pop("CLINE_HOME", None)
            else:
                os.environ["CLINE_HOME"] = before
        self.assertEqual(set(found), {session})
        self.assertEqual((found[session]["title"], found[session]["tool"], found[session]["dir"],
                          found[session]["live"]), ("评估 CI 建构", "cline", "项目", False))
        self.assertEqual(found[session]["updated"], agent.iso_ms("2026-10-16T02:00:15.000Z"))
        self.assertEqual(got["title"], "评估 CI 建构")
        self.assertEqual([(m["role"], m["text"]) for m in got["messages"]],
                         [("user", "评估 CI 建构是否可以替代本地"), ("assistant", "正在清点本地占用。")])

    def test_marks_are_kept_by_tool_and_conversation(self):
        data = tempfile.mkdtemp()
        one = str(uuid.uuid4())
        marks = agent.Marks(data)
        self.assertFalse(marks.has("pinned", "claude", one))
        self.assertFalse(os.path.exists(os.path.join(data, "conversation-marks.json")))     # nothing is written until something is said
        self.assertTrue(marks.set("pinned", "claude", one, True))
        self.assertFalse(marks.set("pinned", "claude", one, True))                          # said again, nothing changes
        self.assertEqual((marks.has("pinned", "claude", one), marks.has("hidden", "claude", one), marks.has("pinned", "codex", one)), (True, False, False))
        marks.set("hidden", "codex", one, True)
        again = agent.Marks(data)                                                         # another start of the agent
        self.assertTrue(again.has("pinned", "claude", one) and again.has("hidden", "codex", one))
        self.assertEqual(os.stat(os.path.join(data, "conversation-marks.json")).st_mode & 0o777, 0o600)
        self.assertTrue(again.set("pinned", "claude", one, False))
        self.assertEqual((agent.Marks(data).has("pinned", "claude", one), agent.Marks(data).has("hidden", "codex", one)), (False, True))
        with self.assertRaises(agent.OpError):
            marks.set("starred", "claude", one, True)
        for n in range(agent.MAX_MARKS + 5):                                              # the ones marked longest ago give way
            again.set("hidden", "claude", "s%d" % n, True)
        full = agent.Marks(data)
        self.assertEqual((full.has("hidden", "claude", "s0"), full.has("hidden", "claude", "s%d" % (agent.MAX_MARKS + 4)), full.has("hidden", "codex", one)), (False, True, False))
        for broken in ("not json", "[1, 2]", '{"pinned": "claude:x", "hidden": [3, null, "claude:kept"]}'):
            with open(os.path.join(data, "conversation-marks.json"), "w", encoding="utf-8") as stream:
                stream.write(broken)
            read = agent.Marks(data)
            self.assertEqual((read.has("pinned", "claude", "x"), read.has("hidden", "claude", "kept")), (False, "kept" in broken), broken)
            self.assertTrue(read.set("pinned", "codex", one, True) and agent.Marks(data).has("pinned", "codex", one))

    def test_conversations_open_outside_every_project_and_marked_ones(self):
        home, data = tempfile.mkdtemp(), tempfile.mkdtemp()
        project = tempfile.mkdtemp() + "/项目"
        os.makedirs(project)
        apart, passing = "/srv/读书 笔记", "/tmp/rcli-scratch"        # folders that are no projects; the second is the system's own
        inside, outside, silent, scratch, codex_out, codex_idle, old = (str(uuid.uuid4()) for _ in range(7))

        def claude(folder, session, title, at, **said):
            saved = os.path.join(home, ".claude", "projects", re.sub("[^a-zA-Z0-9]", "-", folder))
            os.makedirs(saved, exist_ok=True)
            path = os.path.join(saved, session + ".jsonl")
            with open(path, "w", encoding="utf-8") as stream:
                stream.write(json.dumps(dict({"type": "user", "message": {"role": "user", "content": title}}, **said), ensure_ascii=False) + "\n" if title else "{}\n")
            os.utime(path, (at, at))

        claude(project, inside, "项目里的对话", 1000)
        claude(project, old, "更早的对话", 500)
        claude(apart, outside, "整理读书笔记", 2000)
        claude(apart, silent, "", 2000)                   # open, and nothing said in it yet
        claude(passing, scratch, "临时的", 2000)
        # folders with conversations that nothing has open: one used in a terminal, one that is gone since
        quiet, gone, edited = "/srv/old-notes", "/srv/removed", "/srv/edited"
        claude(quiet, str(uuid.uuid4()), "旧笔记", 1500, cwd=quiet, entrypoint="cli")
        claude(gone, str(uuid.uuid4()), "删掉的", 1800, cwd=gone, entrypoint="cli")
        rollouts = os.path.join(home, ".codex", "sessions", "2026", "10", "11")
        os.makedirs(rollouts)
        files = {}
        for session, folder in ((codex_out, apart + "/"), (codex_idle, apart)):
            files[session] = os.path.join(rollouts, "rollout-2026-10-11T10-00-00-%s.jsonl" % session)
            with open(files[session], "w", encoding="utf-8") as stream:
                stream.write(json.dumps({"type": "session_meta", "payload": {"id": session, "cwd": folder}}, ensure_ascii=False) + "\n")
        with open(os.path.join(rollouts, "rollout-2026-10-11T09-00-00-%s.jsonl" % uuid.uuid4()), "w", encoding="utf-8") as stream:
            stream.write(json.dumps({"type": "session_meta", "payload": {"cwd": edited, "originator": "codex_vscode"}}) + "\n")
        os.utime(stream.name, (1700, 1700))
        marks = agent.Marks(data)
        marks.set("pinned", "claude", old, True)
        marks.set("hidden", "claude", inside, True)
        sessions = agent.Sessions(marks)
        sessions.home = home
        # which conversations a program has open is read from /proc; here it is said
        sessions._claude_open = lambda terminals: {outside: apart, silent: apart, scratch: passing, inside: project}
        sessions._codex_open = lambda root: {files[codex_out]: 4000000}
        sessions.exists = lambda folder: folder != gone
        before = os.environ.pop("CODEX_HOME", None)
        try:
            found = sessions.scan({"项目": project}, [])
            by = {s["id"]: s for s in found}
            # those a program has open elsewhere are listed without a project, with their folder; not one in which
            # nothing was said, not one in the system's folder for passing things, not one that nothing has open
            self.assertEqual(set(by), {inside, old, outside, codex_out})
            self.assertEqual([(by[s]["dir"], by[s]["folder"], by[s]["live"], by[s]["tool"]) for s in (outside, codex_out)],
                             [("", apart, True, "claude"), ("", apart, True, "codex")])
            self.assertEqual((by[inside]["dir"], by[inside]["folder"], by[inside]["live"]), ("项目", "", True))
            self.assertEqual([(by[s]["pinned"], by[s]["hidden"]) for s in (inside, old, outside)], [(False, True), (True, False), (False, False)])
            # the ones that are open and the pinned one come first, whatever their age
            self.assertEqual({s["id"] for s in found[:3]} | {found[3]["id"]}, set(by))
            self.assertTrue(all(s["live"] or s["pinned"] for s in found))
            # the folders that are no projects and have conversations are offered, to make projects of: the ones in
            # use first, each with by what it is used; not the system's folder, not one that is gone
            self.assertEqual([(c["path"], c["name"], c["live"], c["tools"], c["kinds"]) for c in sessions.candidates],
                             [(apart, "读书 笔记", True, "claude codex", ["终端"]), (edited, "edited", False, "codex", ["编辑器"]), (quiet, "old-notes", False, "claude", ["终端"])])
            self.assertEqual([c["updated"] for c in sessions.candidates[1:]], [1700000, 1500000])
            # every project is told with the tools and by what its folder is used; each conversation with by what it was started
            self.assertEqual(sessions.told([{"name": "项目", "path": project}, {"name": "空", "path": "/srv/empty"}]),
                             [{"name": "项目", "path": project, "tools": "claude", "kinds": ["终端"]}, {"name": "空", "path": "/srv/empty", "tools": "", "kinds": []}])
            self.assertEqual({entry["kind"] for entry in found}, {"终端"})
            # a page that follows a conversation is given it anew only when its file has changed
            first = sessions.read(outside, "claude", "", {"项目": project})
            self.assertEqual(sessions.read(outside, "claude", "", {"项目": project}, first["stamp"]), {"same": True, "stamp": first["stamp"], "title": "整理读书笔记", "status": ""})
            self.assertIn("messages", sessions.read(outside, "claude", "", {"项目": project}, "1.1"))
            # a saved conversation of a project is put away: Claude Code's into the trash with a note of where it lay,
            # Codex's by Codex's own archive; not one a program has open, not one that is unknown; its marks go with it
            before_data = os.environ.get("XDG_DATA_HOME")
            os.environ["XDG_DATA_HOME"] = os.path.join(home, "share")
            try:
                saved_file = os.path.join(home, ".claude", "projects", re.sub("[^a-zA-Z0-9]", "-", project), old + ".jsonl")
                os.makedirs(saved_file[:-6])
                self.assertTrue(marks.has("pinned", "claude", old))
                for wrong in ((inside, "claude"), (outside, "claude"), (str(uuid.uuid4()), "claude"), ("x", "claude")):
                    with self.assertRaises(agent.OpError):
                        sessions.delete(*wrong, {"项目": project}, [])
                sessions.delete(old, "claude", {"项目": project}, [])
                trashed = os.path.join(home, "share", "Trash")
                self.assertFalse(os.path.exists(saved_file) or os.path.exists(saved_file[:-6]))
                self.assertEqual(sorted(os.listdir(os.path.join(trashed, "files"))), sorted([old, old + ".jsonl"]))
                with open(os.path.join(trashed, "info", old + ".jsonl.trashinfo"), encoding="utf-8") as stream:
                    self.assertIn("Path=" + agent.quote(saved_file), stream.read())
                self.assertFalse(marks.has("pinned", "claude", old))
                self.assertNotIn(old, [s["id"] for s in sessions.found])
            finally:
                if before_data is None:
                    del os.environ["XDG_DATA_HOME"]
                else:
                    os.environ["XDG_DATA_HOME"] = before_data
            found = sessions.scan({"项目": project}, [])
            by = {s["id"]: s for s in found}
            # it is read where it is; one of a project is read by its project as before
            self.assertEqual(sessions.read(outside, "claude", "", {"项目": project})["messages"], [{"role": "user", "text": "整理读书笔记"}])
            self.assertEqual(sessions.read(codex_out, "codex", "", {"项目": project})["messages"], [])
            for wrong in ((inside, "claude", ""), (scratch, "claude", ""), (outside, "codex", "")):
                with self.assertRaises(agent.OpError):
                    sessions.read(*wrong, {"项目": project})
            # once its folder is a project it is one of that project's
            joined = {s["id"]: s for s in sessions.scan({"项目": project, "笔记": apart}, [])}
            self.assertEqual([(joined[s]["dir"], joined[s]["folder"]) for s in (outside, codex_out, codex_idle)], [("笔记", ""), ("笔记", ""), ("笔记", "")])
            self.assertEqual([c["path"] for c in sessions.candidates], [edited, quiet])
        finally:
            if before is not None:
                os.environ["CODEX_HOME"] = before
        for folder, said in ((apart, True), ("/", False), ("relative/x", False), ("/tmp", False), ("/var/tmp/x", False), ("/tmpfiles", True), ("", False), (None, False), ("/a\nb", False)):
            self.assertEqual(agent.elsewhere(folder), said, folder)

    def test_the_owners_work_in_numbers(self):
        import datetime
        home = tempfile.mkdtemp()
        claude, codex = os.path.join(home, "claude"), os.path.join(home, "codex")
        today = datetime.datetime.combine(datetime.date.today(), datetime.time())
        utc = lambda local: datetime.datetime.fromtimestamp(local.timestamp(), datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
        later = lambda start, seconds: utc(start + datetime.timedelta(seconds=seconds))
        row = lambda **fields: json.dumps(fields, separators=(",", ":"))
        # Claude Code: two prompts yesterday in one conversation. The first is answered after 4 s and worked on for
        # 90 s (a step and what it gave back belong to the turn); its answer is noted in two rows and counted once.
        a = today - datetime.timedelta(days=1) + datetime.timedelta(hours=9)
        usage = {"input_tokens": 100, "cache_creation_input_tokens": 30, "cache_read_input_tokens": 70, "output_tokens": 40}
        os.makedirs(os.path.join(claude, "projects", "-work-shop"))
        with open(os.path.join(claude, "projects", "-work-shop", "11111111-2222-3333-4444-555555555555.jsonl"), "w", encoding="utf-8") as stream:
            stream.write("\n".join([
                row(type="user", isSidechain=False, cwd="/work/shop", message={"role": "user", "content": "fix it"}, timestamp=utc(a)),
                row(type="assistant", isSidechain=False, message={"id": "msg_one", "role": "assistant", "content": [{"type": "text", "text": "looking"}], "usage": usage}, timestamp=later(a, 4)),
                row(type="assistant", isSidechain=False, message={"id": "msg_one", "role": "assistant", "content": [{"type": "tool_use", "id": "t1", "name": "Bash", "input": {}}], "usage": usage}, timestamp=later(a, 5)),
                row(type="user", isSidechain=False, message={"role": "user", "content": [{"type": "tool_result", "tool_use_id": "t1", "content": "ok"}]}, timestamp=later(a, 60)),
                row(type="assistant", isSidechain=False, message={"id": "msg_two", "role": "assistant", "content": [{"type": "text", "text": "done"}],
                                                                  "usage": {"input_tokens": 10, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 200, "output_tokens": 5}}, timestamp=later(a, 90)),
                row(type="user", isMeta=True, message={"role": "user", "content": "a note of its own"}, timestamp=later(a, 95)),
                row(type="user", isSidechain=True, message={"role": "user", "content": "a helper's"}, timestamp=later(a, 96)),
                row(type="user", isSidechain=False, message={"role": "user", "content": "and the tests"}, timestamp=later(a, 7200)),
                row(type="assistant", isSidechain=False, message={"id": "msg_three", "role": "assistant", "content": [{"type": "text", "text": "ok"}],
                                                                  "usage": {"input_tokens": 1, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0, "output_tokens": 2}}, timestamp=later(a, 7210)),
            ]) + "\n")
        # Codex: one turn today, 30 s long, first answered after 3 s, two requests with their tokens
        b = today + datetime.timedelta(hours=8)
        os.makedirs(os.path.join(codex, "sessions", "2026", "10", "13"))
        count = lambda given, cached, written: {"type": "token_count", "info": {"total_token_usage": {"input_tokens": 9000, "output_tokens": 900},
                                                "last_token_usage": {"input_tokens": given, "cached_input_tokens": cached, "output_tokens": written, "total_tokens": given + written}, "model_context_window": 1}}
        with open(os.path.join(codex, "sessions", "2026", "10", "13", "rollout-2026-10-13T08-00-00-66666666-7777-8888-9999-000000000000.jsonl"), "w", encoding="utf-8") as stream:
            stream.write("\n".join([
                row(timestamp=utc(b), type="session_meta", payload={"id": "66666666-7777-8888-9999-000000000000", "cwd": "/notes/thesis"}),
                row(timestamp=later(b, 1), type="event_msg", payload={"type": "task_started", "turn_id": "t", "started_at": 1}),
                row(timestamp=later(b, 4), type="response_item", payload={"type": "reasoning"}),
                row(timestamp=later(b, 9), type="event_msg", payload=count(1000, 600, 50)),
                row(timestamp=later(b, 20), type="event_msg", payload=count(2000, 1500, 70)),
                row(timestamp=later(b, 31), type="event_msg", payload={"type": "task_complete", "turn_id": "t", "started_at": 1, "duration_ms": 30000}),
            ]) + "\n")
        stats = agent.Stats(lambda: claude, lambda: codex)
        named = lambda folder: "web-shop" if folder.endswith("shop") else os.path.basename(folder)
        read = stats.read(7, named)
        days = read["days"]
        self.assertEqual((len(days), days[6]["date"], [d["prompts"] for d in days[:5]]), (7, datetime.date.today().isoformat(), [0] * 5))
        self.assertEqual({k: days[5][k] for k in ("prompts", "sessions", "active", "in", "out", "cached", "wait")},
                         {"prompts": 2, "sessions": 1, "active": 100, "in": 111, "out": 47, "cached": 300, "wait": 7.0})
        self.assertEqual({k: days[6][k] for k in ("prompts", "active", "in", "out", "cached", "wait")},
                         {"prompts": 1, "active": 30, "in": 900, "out": 120, "cached": 2100, "wait": 3.0})
        self.assertEqual({t["name"]: (t["prompts"], t["tokens"]) for t in read["tools"]}, {"claude": (2, 158), "codex": (1, 1020)})
        self.assertEqual([(x["name"], x["prompts"], x["active"]) for x in read["projects"]], [("web-shop", 2, 100), ("thesis", 1, 30)])
        self.assertEqual((read["hours"][9], read["hours"][11], read["hours"][8], sum(read["hours"])), (1, 1, 1, 3))
        self.assertEqual([d["prompts"] for d in stats.read(1, named)["days"]], [1])

    def test_the_work_records_stay_within_their_budget(self):
        import datetime
        home = tempfile.mkdtemp()
        claude, codex = os.path.join(home, "claude"), os.path.join(home, "codex")
        today = datetime.datetime.combine(datetime.date.today(), datetime.time())
        utc = lambda local: datetime.datetime.fromtimestamp(local.timestamp(), datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
        later = lambda start, seconds: utc(start + datetime.timedelta(seconds=seconds))
        row = lambda **fields: json.dumps(fields, separators=(",", ":"))
        at = today + datetime.timedelta(hours=9)
        os.makedirs(os.path.join(claude, "projects", "-work-shop"))
        # six conversations of the same day: the room the records may take is small, so the
        # oldest go before new ones are kept — and are read anew when they are asked for
        for number in range(6):
            name = "%08d-1111-2222-3333-444455556666" % number
            with open(os.path.join(claude, "projects", "-work-shop", name + ".jsonl"), "w", encoding="utf-8") as stream:
                stream.write("\n".join([
                    row(type="user", isSidechain=False, cwd="/work/shop", message={"role": "user", "content": "q"}, timestamp=utc(at)),
                    row(type="assistant", isSidechain=False, message={"id": "msg_one", "role": "assistant",
                                                                      "content": [{"type": "text", "text": "a"}],
                                                                      "usage": {"input_tokens": 1, "output_tokens": 1}}, timestamp=later(at, 3)),
                ]) + "\n")
        stats = agent.Stats(lambda: claude, lambda: codex)
        budget = agent.RECORDS_BYTES
        try:
            agent.RECORDS_BYTES = 1000        # a small room: the records are counted by what they hold
            read = stats.read(1, lambda folder: "shop")
            self.assertEqual(len(stats.records), 2, sorted(stats.records))
            self.assertLessEqual(stats.kept, 1000)
            self.assertEqual([d["prompts"] for d in read["days"]], [6])     # every conversation still counts
            self.assertEqual(read["days"][0]["wait"], 3.0)
        finally:
            agent.RECORDS_BYTES = budget

    def test_questions_asked_at_a_time_and_a_conversation_handed_over(self):
        import datetime
        data = tempfile.mkdtemp()
        path = os.path.join(data, "schedules.json")
        kept = agent.Schedules(path)
        now = datetime.datetime.combine(datetime.date.today(), datetime.time(10)).timestamp()
        session = "11111111-2222-3333-4444-555555555555"
        item = lambda key, kind, **more: dict({"id": key * 32, "tool": "claude", "session": session, "title": "甲", "text": "进展如何？", "kind": kind,
                                               "time": "09:30", "minutes": 30, "at": int((now + 1200) * 1000)}, **more)
        self.assertEqual(kept.set(item("a", "daily"), now)["next"], int((now - 1800 + 86400) * 1000))     # past that today, so tomorrow
        self.assertEqual((kept.set(item("b", "every"), now)["next"], kept.set(item("c", "once"), now)["next"]), (int((now + 1800) * 1000), int((now + 1200) * 1000)))
        for wrong, why in ((item("d", "once", at=int((now - 60) * 1000)), "现在之后"), (item("d", "every", minutes=2), "5 分钟"), (item("d", "daily", text=" "), "1 至 2000"),
                           (item("d", "daily", time="9:30"), "09:30"), (item("d", "weekly"), "重复方式")):
            with self.assertRaisesRegex(agent.OpError, why):
                kept.set(wrong, now)
        self.assertEqual((len(kept.all()), kept.due(now + 600)), (3, []))
        self.assertEqual([one["id"][0] for one in kept.due(now + 1500)], ["c"])        # the single one: asked once, then off
        kept.done("c" * 32, False, "这段对话没有在手机打开的终端里运行", now + 1500)
        again = {one["id"][0]: one for one in agent.Schedules(path).all()}             # kept in the data folder, read anew
        self.assertEqual((again["c"]["on"], again["c"]["next"], again["c"]["runs"], again["c"]["last"]["ok"]), (False, 0, 1, False))
        self.assertEqual([one["id"][0] for one in kept.due(now + 1900)], ["b"])
        self.assertEqual(agent.Schedules(path).all()[1]["next"], int((now + 1900 + 1800) * 1000))
        self.assertEqual(stat_mode(path), 0o600)
        self.assertTrue(kept.remove("a" * 32) and not kept.remove("a" * 32))

        # what is said to a conversation is typed into the terminal of the phone's that runs it
        class Typed:
            closed, tool = False, "claude"

            def __init__(self):
                self.got, self.session = [], session.upper()

            def write(self, text):
                self.got.append(text)

        one = agent.Agent(data, "p")
        one.schedules = kept
        other = "66666666-7777-8888-9999-000000000000"
        one.sessions.found = [{"id": session, "tool": "claude", "dir": "项目", "title": "甲", "live": True}, {"id": other, "tool": "codex", "dir": "", "title": "乙", "live": False}]
        one.sessions.read = lambda *asked: {"messages": [{"role": "user", "text": "先做第一步"}, {"role": "tool", "text": "Bash：运行测试"}, {"role": "assistant", "text": "第一步做完了"},
                                                          {"role": "user", "text": "再做第二步"}, {"role": "assistant", "text": "第二步也做完了"}]}
        with self.assertRaisesRegex(agent.OpError, "手机打开的终端"):
            one.owner_work("work_handoff", {"from": session, "from_tool": "claude", "to": other, "to_tool": "codex", "note": ""}, {})
        typed = one.terminals["t"] = Typed()
        answer = one.owner_work("work_handoff", {"from": other, "from_tool": "codex", "to": session, "to_tool": "claude", "note": "看一下边界情况"}, {})
        self.assertEqual((answer["status"], answer["note"]), ("accepted", "已发到手机终端"))
        said = typed.got[0]
        self.assertTrue(said.startswith("\x1b[200~【对话交接】下面是我在另一段对话里的进展（Codex，“乙”），请接着处理。") and said.endswith("\x1b[201~\r"), said)
        for part in ("我最初的要求：\n先做第一步", "我最近的要求：\n再做第二步", "它最后的回答：\n第二步也做完了", "它最近做的操作：\n- Bash：运行测试", "我的补充：\n看一下边界情况"):
            self.assertIn(part, said)
        with self.assertRaisesRegex(agent.OpError, "同一段"):
            one.owner_work("work_handoff", {"from": session, "from_tool": "claude", "to": session, "to_tool": "claude"}, {})
        # a question that is due is typed there too, and how it went is kept
        shown = one.owner_work("work_schedule_set", {"item": item("e", "every", title="")}, {})["schedule"]
        self.assertEqual((shown["title"], shown["on"], shown["last"]), ("甲", True, None))
        one.ask(time.time() + 1801)
        self.assertEqual(typed.got[1], "\x1b[200~进展如何？\x1b[201~\r")
        asked = next(s for s in one.owner_work("work_schedules", {}, {})["schedules"] if s["id"] == "e" * 32)
        self.assertEqual((asked["runs"], asked["last"]["ok"], asked["last"]["note"]), (1, True, "已发到手机终端"))
        with self.assertRaisesRegex(agent.OpError, "没有找到"):
            one.owner_work("work_schedule_set", {"item": item("f", "daily", session="99999999-7777-8888-9999-000000000000")}, {})

    def test_qr_code(self):
        text = "remotecli://connect?u=https%3A%2F%2Fa-b-c-d.trycloudflare.com&p=3AasQ_tDwOpmumz1aqxb6znL&n=ubuntu"
        code = agent.qr_code(text)
        size = len(code)
        self.assertEqual((size - 17) % 4, 0)
        self.assertTrue(all(len(row) == size for row in code))
        ring = [[True] * 7, [True] + [False] * 5 + [True]] + [[True, False, True, True, True, False, True]] * 3 \
            + [[True] + [False] * 5 + [True], [True] * 7]
        for top, left in ((0, 0), (0, size - 7), (size - 7, 0)):       # the three corner marks
            self.assertEqual([row[left:left + 7] for row in code[top:top + 7]], ring)
        self.assertEqual(agent.qr_code(text), code)
        self.assertNotEqual(agent.qr_code(text + "x"), code)
        self.assertIsNone(agent.qr_code("x" * 214))
        drawn = agent.qr_text(code).rstrip("\n").split("\n")
        self.assertEqual(len(drawn), (size + 6 + 1) // 2)
        try:                                # read back where OpenCV is at hand
            import cv2
            import numpy
        except ImportError:
            return
        wide = [[False] * (size + 8)] * 4 + [[False] * 4 + row + [False] * 4 for row in code] + [[False] * (size + 8)] * 4
        image = numpy.array([[0 if cell else 255 for cell in row] for row in wide], dtype=numpy.uint8).repeat(8, 0).repeat(8, 1)
        self.assertEqual(cv2.QRCodeDetector().detectAndDecode(image)[0], text)

    def test_read_password_not_generated(self):
        self.assertEqual(agent.read_password(tempfile.mkdtemp()), "")
        data = tempfile.mkdtemp()
        agent.write_private(os.path.join(data, "password.txt"), PASSWORD + "\n")
        self.assertEqual(agent.read_password(data), PASSWORD)


class EndToEnd(unittest.TestCase):
    """The real relay on 127.0.0.1, the real agent as a subprocess, this test as the phone."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="rcli-e2e-")
        cls.project = os.path.join(cls.tmp, "项目")
        os.makedirs(cls.project)
        port = free_port()
        cls.relay_data = os.path.join(cls.tmp, "relay")
        cls.base = "http://127.0.0.1:%d" % port
        cls.relay = subprocess.Popen(
            [sys.executable, os.path.join(REPO, "relay", "server.py"),
             "--host", "127.0.0.1", "--port", str(port), "--data", cls.relay_data,
             "--web", os.path.join(REPO, "web")],
            env={**os.environ, "RCLI_PASSWORD": PASSWORD},
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        cls.wait_tcp(port)
        agent_data = os.path.join(cls.tmp, "agent")
        os.makedirs(agent_data)
        with open(os.path.join(agent_data, "config.json"), "w", encoding="utf-8") as stream:
            json.dump({"Server": cls.base, "RemoteEnabled": True, "Name": "e2e-ubuntu",
                       "RemoteDirs": ["项目=" + cls.project]}, stream)
        # A home of its own, with a stand-in for Claude Code that says how it was started,
        # and one conversation saved for the project.
        cls.home = os.path.join(cls.tmp, "home")
        tools = os.path.join(cls.home, ".local", "bin")
        os.makedirs(tools)
        with open(os.path.join(tools, "claude"), "w") as stream:
            stream.write("#!/bin/sh\necho \"STANDIN_CLAUDE[$*]\"\nexec sleep 600\n")
        os.chmod(os.path.join(tools, "claude"), 0o755)
        # and one for opencode, where its own installer puts it
        other = os.path.join(cls.home, ".opencode", "bin")
        os.makedirs(other)
        with open(os.path.join(other, "opencode"), "w") as stream:
            stream.write("#!/bin/sh\necho \"STANDIN_OPENCODE[$*]\"\nexec sleep 600\n")
        os.chmod(os.path.join(other, "opencode"), 0o755)
        cls.saved = "0a1b2c3d-1111-2222-3333-444455556666"
        saved = os.path.join(cls.home, ".claude", "projects", re.sub("[^a-zA-Z0-9]", "-", cls.project))
        os.makedirs(saved)
        with open(os.path.join(saved, cls.saved + ".jsonl"), "w", encoding="utf-8") as stream:
            stream.write(json.dumps({"type": "user", "message": {"role": "user", "content": "上次说到哪了"}}, ensure_ascii=False) + "\n")
            stream.write(json.dumps({"type": "ai-title", "aiTitle": "上次的对话", "sessionId": cls.saved}) + "\n")
        with open(os.path.join(cls.project, "说明.txt"), "w", encoding="utf-8") as stream:
            stream.write("文件内容 e2e\n")
        cls.agent_log = open(os.path.join(cls.tmp, "agent.log"), "wb")
        cls.agent = subprocess.Popen(cls.agent_command(agent_data),
                                     env={**os.environ, "RCLI_PASSWORD": PASSWORD, "HOME": cls.home,
                                          # a PATH of the system's own: the stand-ins above are what
                                          # find_tool must see, not whatever this machine has installed
                                          "PATH": "/usr/local/bin:/usr/bin:/bin"},
                                     stdout=subprocess.DEVNULL, stderr=cls.agent_log)
        cls.token = request("POST", cls.base + "/api/login", {"password": PASSWORD})[1]["token"]
        view = None
        deadline = time.time() + 30
        while time.time() < deadline:
            status, view = request("GET", cls.base + "/api/terminal", token=cls.token)
            if status == 200 and view.get("device", {}).get("online") \
                    and "项目" in view.get("device", {}).get("workspaces", []):
                return
            time.sleep(0.3)
        raise AssertionError("agent did not come online: %r" % view)

    @classmethod
    def agent_command(cls, agent_data):
        """What spawns the agent under test; overridden by the Rust contract test."""
        return [sys.executable, os.path.join(HERE, "agent.py"), "--data", agent_data]

    @classmethod
    def wait_tcp(cls, port):
        deadline = time.time() + 10
        while time.time() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=1):
                    return
            except OSError:
                time.sleep(0.1)
        raise AssertionError("relay did not start")

    @classmethod
    def tearDownClass(cls):
        for process in (cls.agent, cls.relay):
            if process.poll() is None:
                process.terminate()
        for process in (cls.agent, cls.relay):
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
        cls.agent_log.close()
        with open(os.path.join(cls.tmp, "agent.log"), encoding="utf-8", errors="replace") as stream:
            refused = [line for line in stream if "失败" in line or "拒绝" in line]
        if refused:                     # what the agent itself said, when a step went wrong
            sys.stderr.write("agent: " + "agent: ".join(refused))
        import shutil
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def overview(self):
        status, view = request("GET", self.base + "/api/terminal", token=self.token)
        self.assertEqual(status, 200)
        return view

    def poll(self, what, until, seconds=20, step=0.3):
        deadline = time.time() + seconds
        while time.time() < deadline:
            value = what()
            if until(value):
                return value
            time.sleep(step)
        raise AssertionError("timed out waiting for %r, last: %r" % (until, value))

    def op(self, payload):
        payload = {"id": secrets.token_hex(16), **payload}
        status, result = request("POST", self.base + "/api/terminal", payload, token=self.token)
        self.assertEqual(status, 200, result)
        return result

    def test_end_to_end(self):
        # 1. start a shell in the project and wait for its prompt to arrive
        started = self.op({"action": "start", "tool": "shell", "dir": "项目"})
        self.assertEqual(started["state"], "queued", started)
        terminal = started["terminal"]
        after, text = 0, ""
        deadline = time.time() + 40
        while time.time() < deadline:
            status, view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                                   % (self.base, terminal, after), token=self.token)
            self.assertEqual(status, 200, view)
            for chunk in view["chunks"]:
                text += chunk["data"]
                after = chunk["seq"]
            if prompted(text):
                break
        self.assertTrue(prompted(text), "no shell prompt arrived")
        self.assertEqual(self.overview()["terminals"][0]["state"], "running")

        # 2. text round trip: the phone types, the shell answers
        self.op({"action": "input", "terminal": terminal, "data": "echo E2EOK_$((21*2))\n"})
        deadline = time.time() + 30
        while time.time() < deadline and "E2EOK_42" not in plain(text):
            status, view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                                   % (self.base, terminal, after), token=self.token)
            for chunk in view.get("chunks", []):
                text += chunk["data"]
                after = chunk["seq"]
            time.sleep(0.2)
        self.assertIn("E2EOK_42", plain(text), "the shell never answered")

        # 3. resize reaches the program (the pty's winsize is what the shell reports);
        # the probe is typed once, the shell runs it when it gets to it
        self.op({"action": "resize", "terminal": terminal, "cols": 100, "rows": 30})
        self.op({"action": "input", "terminal": terminal,
                 "data": "python3 -c \"import shutil;print('COLS_'+str(shutil.get_terminal_size().columns))\"\n"})
        deadline = time.time() + 40
        while time.time() < deadline and "COLS_100" not in plain(text):
            status, view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                                   % (self.base, terminal, after), token=self.token)
            for chunk in view.get("chunks", []):
                text += chunk["data"]
                after = chunk["seq"]
            time.sleep(0.3)
        self.assertIn("COLS_100", plain(text), "resize did not reach the terminal")

        # 4. a graceful exit is reported with its code
        self.op({"action": "input", "terminal": terminal, "data": "exit\n"})
        view = self.poll(lambda: self.overview(),
                         lambda v: v["terminals"] and v["terminals"][0]["state"] == "closed")
        self.assertEqual(view["terminals"][0].get("exit_code"), 0)

        # 5. projects added from the phone appear as workspaces, rename and remove follow
        fresh = os.path.join(self.tmp, "新建项目")
        self.op({"action": "project_add", "path": fresh, "name": "新增", "create": True})
        self.poll(lambda: self.overview(), lambda v: "新增" in v["device"]["workspaces"])
        self.assertTrue(os.path.isdir(fresh), "the agent did not create the folder")
        self.op({"action": "project_rename", "name": "新增", "to": "改名"})
        self.poll(lambda: self.overview(),
                  lambda v: "改名" in v["device"]["workspaces"] and "新增" not in v["device"]["workspaces"])
        self.op({"action": "project_remove", "name": "改名"})
        self.poll(lambda: self.overview(), lambda v: "改名" not in v["device"]["workspaces"])

        # 6. the owner's work in numbers, asked for as the phone's page asks: the last seven days, today the last
        self.assertIn("work", self.overview()["device"]["features"])
        status, numbers = request("POST", self.base + "/api/work", {"id": secrets.token_hex(16), "action": "stats", "days": 7}, token=self.token, timeout=40)
        self.assertEqual((status, numbers.get("ready"), len(numbers.get("days", [])), len(numbers.get("hours", []))), (200, True, 7, 24), numbers)
        self.assertEqual(numbers["days"][-1]["date"], time.strftime("%Y-%m-%d"))
        # and passing on when a conversation stops is not offered here: this program has no hooks of the tools
        status, refused = request("POST", self.base + "/api/work", {"id": secrets.token_hex(16), "action": "chains"}, token=self.token)
        self.assertEqual((status, "不支持接力" in refused.get("error", "")), (400, True), refused)

        # 6. a folder that is not a project never becomes a terminal: the relay refuses it
        # at the door (the agent checks again itself, but this request cannot even reach it)
        status, refused = request("POST", self.base + "/api/terminal",
                                  {"id": secrets.token_hex(16), "action": "start",
                                   "tool": "shell", "dir": "其他"}, token=self.token)
        self.assertEqual(status, 400)
        self.assertIn("工具或目录", refused["error"])

        # 7. the files of a project, through the relay: a listing, a file, and a refusal
        device = self.overview()["device"]
        self.assertIn("files", device.get("features", []))
        status, listed = self.file({"action": "file_list", "dir": "项目", "path": ""})
        self.assertEqual(status, 200, listed)
        self.assertIn("说明.txt", [e["name"] for e in listed["entries"]])
        status, piece = self.file({"action": "file_read", "dir": "项目", "path": "说明.txt", "offset": 0})
        self.assertEqual(status, 200, piece)
        self.assertEqual(base64.b64decode(piece["data"]).decode("utf-8"), "文件内容 e2e\n")
        self.assertTrue(piece["end"])
        status, refused = self.file({"action": "file_read", "dir": "项目", "path": "../agent/config.json"})
        self.assertEqual(status, 400)
        self.assertIn("项目文件夹", refused["error"])

        # 8. a tool installed for this user is offered and started; a new Claude Code
        # conversation is named by the agent, so the terminal knows it at once
        self.assertIn("claude", device["tools"])       # the stand-in; what else is here is the machine's own
        self.assertIn("shell", device["tools"])
        new = self.op({"action": "start", "tool": "claude", "dir": "项目"})["terminal"]
        shown = self.read_until(new, "STANDIN_CLAUDE[")
        named = re.search(r"STANDIN_CLAUDE\[--session-id ([0-9a-f-]{36})\]", shown)
        self.assertIsNotNone(named, shown)
        view = self.poll(lambda: self.overview(),
                         lambda v: any(t["id"] == new and t.get("session") == named.group(1) for t in v["terminals"]))

        # 9. a saved conversation is listed with its name and continued by its id
        self.assertEqual([(s["id"], s["title"], s["tool"], s["dir"]) for s in view["sessions"]],
                         [(self.saved, "上次的对话", "claude", "项目")])
        self.assertIn("peek", device.get("features", []))
        status, looked = request("POST", self.base + "/api/conversation", {"id": secrets.token_hex(16), "session": self.saved},
                                 token=self.token, timeout=30)
        self.assertEqual(status, 200, looked)
        self.assertEqual((looked["title"], looked["messages"]), ("上次的对话", [{"role": "user", "text": "上次说到哪了"}]))
        # it is pinned from the phone, and the computer keeps that: its own later lists say so too
        self.assertIn("marks", device.get("features", []))
        self.assertEqual((view["sessions"][0]["pinned"], view["sessions"][0]["hidden"], view["sessions"][0]["folder"]), (False, False, ""))
        status, marked = request("POST", self.base + "/api/conversation", {"id": secrets.token_hex(16), "session": self.saved, "action": "mark", "pinned": True},
                                 token=self.token, timeout=30)
        self.assertEqual((status, marked), (200, {}))
        self.assertTrue(self.overview()["sessions"][0]["pinned"])
        time.sleep(12)                  # the agent lists its conversations anew at least every ten seconds
        self.assertEqual((self.overview()["sessions"][0]["pinned"], self.overview()["sessions"][0]["hidden"]), (True, False))
        status, marked = request("POST", self.base + "/api/conversation", {"id": secrets.token_hex(16), "session": self.saved, "action": "mark", "pinned": False, "hidden": True},
                                 token=self.token, timeout=30)
        self.assertEqual((status, marked), (200, {}))
        self.poll(lambda: self.overview(), lambda v: (v["sessions"][0]["pinned"], v["sessions"][0]["hidden"]) == (False, True))
        request("POST", self.base + "/api/conversation", {"id": secrets.token_hex(16), "session": self.saved, "action": "mark", "hidden": False}, token=self.token, timeout=30)
        again = self.op({"action": "start", "tool": "claude", "dir": "项目", "session": self.saved})["terminal"]
        self.assertIn("STANDIN_CLAUDE[--resume %s]" % self.saved, self.read_until(again, "STANDIN_CLAUDE["))
        for terminal in (new, again):
            self.op({"action": "close", "terminal": terminal})
        self.poll(lambda: self.overview(),
                  lambda v: all(t["state"] == "closed" for t in v["terminals"] if t["id"] in (new, again)))

    def file(self, payload):
        return request("POST", self.base + "/api/files", {"id": secrets.token_hex(16), **payload},
                       token=self.token, timeout=30)

    def test_a_tool_beyond_the_first_three_is_started_and_closed(self):
        # the phone offers every tool the agent reports: one beyond claude/codex/shell
        # passes the door, gets the agent-named conversation, and ends for good
        view = self.poll(lambda: self.overview(),
                         lambda v: "opencode" in v["device"].get("tools", []))
        started = self.op({"action": "start", "tool": "opencode", "dir": "项目"})
        self.assertEqual(started["state"], "queued", started)
        terminal = started["terminal"]
        shown = self.read_until(terminal, "STANDIN_OPENCODE[")
        named = re.search(r"STANDIN_OPENCODE\[--session (ses_[a-z0-9]{26})\]", shown)
        self.assertIsNotNone(named, shown)
        view = self.poll(lambda: self.overview(),
                         lambda v: any(t["id"] == terminal and t.get("session") == named.group(1)
                                       for t in v["terminals"]))
        self.assertEqual(next(t for t in view["terminals"] if t["id"] == terminal)["state"], "running")
        self.op({"action": "close", "terminal": terminal})
        self.poll(lambda: self.overview(),
                  lambda v: any(t["id"] == terminal and t["state"] == "closed" for t in v["terminals"]))

    def test_close_kills_what_the_program_left_behind(self):
        # a background task that made a session of its own is past a kill of the process
        # group: closing the terminal takes it too, or it holds the screen open for ever
        leftover = os.path.join(self.home, ".local", "bin", "leftover")
        with open(leftover, "w") as stream:
            stream.write("#!/bin/sh\nexec sleep 617\n")
        os.chmod(leftover, 0o755)
        started = self.op({"action": "start", "tool": "shell", "dir": "项目"})
        terminal = started["terminal"]
        after, text = 0, ""
        deadline = time.time() + 20
        while time.time() < deadline and not prompted(text):
            status, view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                                   % (self.base, terminal, after), token=self.token)
            for chunk in view.get("chunks", []):
                text += chunk["data"]
                after = chunk["seq"]
            time.sleep(0.2)
        self.assertTrue(prompted(text), "no shell prompt arrived")
        self.op({"action": "input", "terminal": terminal, "data": "setsid %s & echo LEFTOVER_UP\n" % leftover})
        deadline = time.time() + 10
        while time.time() < deadline and "LEFTOVER_UP" not in plain(text):
            status, view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                                   % (self.base, terminal, after), token=self.token)
            for chunk in view.get("chunks", []):
                text += chunk["data"]
                after = chunk["seq"]
            time.sleep(0.2)
        self.assertIn("LEFTOVER_UP", plain(text), "the background task never reported: %r" % plain(text))
        deadline = time.time() + 10
        while time.time() < deadline and not self.leftovers():
            time.sleep(0.2)
        self.assertTrue(self.leftovers(), "the background task did not start: %r" % plain(text))
        self.op({"action": "close", "terminal": terminal})
        self.poll(lambda: self.overview(),
                  lambda v: any(t["id"] == terminal and t["state"] == "closed" for t in v["terminals"]))
        deadline = time.time() + 10
        while time.time() < deadline and self.leftovers():
            time.sleep(0.2)
        self.assertFalse(self.leftovers(), "the background task outlived the terminal")

    def leftovers(self):
        """The background tasks this test started, by the sleep they are in."""
        found = []
        for entry in os.listdir("/proc"):
            if not entry.isdigit():
                continue
            try:
                with open("/proc/%s/cmdline" % entry, "rb") as stream:
                    command = stream.read().decode("utf-8", "replace").replace("\x00", " ")
            except OSError:
                continue
            if "sleep 617" in command:
                found.append(entry)
        return found

    def read_until(self, terminal, marker, seconds=30):
        after, text = 0, ""
        deadline = time.time() + seconds
        while time.time() < deadline and not (marker in plain(text) and "]" in plain(text).split(marker, 1)[1]):
            status, view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                                   % (self.base, terminal, after), token=self.token)
            for chunk in view.get("chunks", []):
                text += chunk["data"]
                after = chunk["seq"]
        return plain(text)


class InputNeverHolds(unittest.TestCase):
    """A program that has stopped reading must not hold the agent: input is refused after
    a short wait, and the terminal can still be ended."""

    def test_program_that_does_not_read(self):
        code = "import time, tty; tty.setraw(0); print('RAW', flush=True); time.sleep(120)"
        live = agent.Live("a" * 32, "p", tempfile.mkdtemp(), 80, 24, [sys.executable, "-c", code], lambda: None)
        try:
            deadline = time.time() + 15
            while time.time() < deadline and "RAW" not in "".join(live.pending):
                time.sleep(0.05)
            self.assertIn("RAW", "".join(live.pending))
            started, refused = time.monotonic(), ""
            for _ in range(40):                     # far more than a terminal's input buffer holds
                try:
                    live.write("x" * 16000)
                except agent.OpError as error:
                    refused = str(error)
                    break
            self.assertIn("没有读取输入", refused)
            self.assertLess(time.monotonic() - started, 3 * agent.WRITE_WAIT)
            live.begin_close()
            deadline = time.time() + 5
            while time.time() < deadline and not live.closed:
                time.sleep(0.05)
            self.assertTrue(live.closed)
        finally:
            live.kill()
            live.dispose()


class SingletonLock(unittest.TestCase):
    """A second agent on the same data directory must refuse to start (exit 3)."""

    def run_second(self, command_for):
        import fcntl
        data = tempfile.mkdtemp()
        lock = open(os.path.join(data, "agent.lock"), "w")
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            proc = subprocess.run(command_for(data), capture_output=True, timeout=30)
            self.assertEqual(proc.returncode, 3)
            self.assertIn("已有 agent", proc.stderr.decode("utf-8"))
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)
            lock.close()

    def test_python_second_instance_exits_3(self):
        self.run_second(lambda data: [sys.executable, os.path.join(HERE, "agent.py"), "--data", data])


class EnvILocaleFallback(unittest.TestCase):
    """A bare service environment (no LANG) must still yield a UTF-8 locale in the pty."""

    def test_envi_locale_fallback(self):
        tmp = tempfile.mkdtemp(prefix="rcli-envi-")
        project = os.path.join(tmp, "p")
        os.makedirs(project)
        port = free_port()
        base = "http://127.0.0.1:%d" % port
        relay_log = open(os.path.join(tmp, "relay.log"), "w")
        relay = subprocess.Popen(
            [sys.executable, os.path.join(REPO, "relay", "server.py"), "--host", "127.0.0.1",
             "--port", str(port), "--data", os.path.join(tmp, "relay"), "--web", os.path.join(REPO, "web")],
            env={**os.environ, "RCLI_PASSWORD": PASSWORD},
            stdout=subprocess.DEVNULL, stderr=relay_log)
        deadline = time.time() + 10
        while time.time() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=1):
                    break
            except OSError:
                time.sleep(0.1)
        agent = None
        try:
            agent_data = os.path.join(tmp, "agent")
            os.makedirs(agent_data)
            with open(os.path.join(agent_data, "config.json"), "w", encoding="utf-8") as stream:
                json.dump({"Server": base, "RemoteEnabled": True, "RemoteDirs": ["p=" + project]}, stream)
            # a bare service environment: no LANG, no LC_*
            agent = subprocess.Popen(
                [sys.executable, os.path.join(HERE, "agent.py"), "--data", agent_data],
                env={"RCLI_PASSWORD": PASSWORD, "HOME": os.path.expanduser("~"), "PATH": "/usr/bin:/bin"},
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            token = request("POST", base + "/api/login", {"password": PASSWORD})[1]["token"]
            deadline = time.time() + 30
            while time.time() < deadline:
                view = request("GET", base + "/api/terminal", token=token)[1]
                if view.get("device", {}).get("online"):
                    break
                time.sleep(0.3)
            started = request("POST", base + "/api/terminal",
                              {"id": secrets.token_hex(16), "action": "start", "tool": "shell", "dir": "p"},
                              token=token)[1]
            self.assertIn("terminal", started, started)
            terminal, after, text = started["terminal"], 0, ""
            deadline = time.time() + 30
            while time.time() < deadline and not prompted(text):
                view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                               % (base, terminal, after), token=token)[1]
                for chunk in view.get("chunks", []):
                    text += chunk["data"]
                    after = chunk["seq"]
                time.sleep(0.2)
            request("POST", base + "/api/terminal", {"id": secrets.token_hex(16), "action": "input",
                      "terminal": terminal, "data": "locale | head -1; echo 中文兜底\n"}, token=token)
            deadline = time.time() + 20
            while time.time() < deadline and not ("LANG=" in plain(text) and plain(text).count("中文兜底") >= 2):
                view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                               % (base, terminal, after), token=token)[1]
                for chunk in view.get("chunks", []):
                    text += chunk["data"]
                    after = chunk["seq"]
                time.sleep(0.2)
            p = plain(text)
            self.assertIn("中文兜底", p, "Chinese echo broken")
            self.assertIn("LANG=C.UTF-8", p, "locale fallback did not reach the pty")
        finally:
            for process in (agent, relay):
                if process is not None and process.poll() is None:
                    process.terminate()
            for process in (agent, relay):
                if process is not None:
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
            relay_log.close()
            import shutil
            shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
