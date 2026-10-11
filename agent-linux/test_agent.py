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
        return error.code, json.loads(error.read().decode("utf-8") or "{}")


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

        def claude(folder, session, title, at):
            saved = os.path.join(home, ".claude", "projects", re.sub("[^a-zA-Z0-9]", "-", folder))
            os.makedirs(saved, exist_ok=True)
            path = os.path.join(saved, session + ".jsonl")
            with open(path, "w", encoding="utf-8") as stream:
                stream.write(json.dumps({"type": "user", "message": {"role": "user", "content": title}}, ensure_ascii=False) + "\n" if title else "{}\n")
            os.utime(path, (at, at))

        claude(project, inside, "项目里的对话", 1000)
        claude(project, old, "更早的对话", 500)
        claude(apart, outside, "整理读书笔记", 2000)
        claude(apart, silent, "", 2000)                   # open, and nothing said in it yet
        claude(passing, scratch, "临时的", 2000)
        rollouts = os.path.join(home, ".codex", "sessions", "2026", "10", "11")
        os.makedirs(rollouts)
        files = {}
        for session, folder in ((codex_out, apart + "/"), (codex_idle, apart)):
            files[session] = os.path.join(rollouts, "rollout-2026-10-11T10-00-00-%s.jsonl" % session)
            with open(files[session], "w", encoding="utf-8") as stream:
                stream.write(json.dumps({"type": "session_meta", "payload": {"id": session, "cwd": folder}}, ensure_ascii=False) + "\n")
        marks = agent.Marks(data)
        marks.set("pinned", "claude", old, True)
        marks.set("hidden", "claude", inside, True)
        sessions = agent.Sessions(marks)
        sessions.home = home
        # which conversations a program has open is read from /proc; here it is said
        sessions._claude_open = lambda terminals: {outside: apart, silent: apart, scratch: passing, inside: project}
        sessions._codex_open = lambda root: {files[codex_out]: 4000000}
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
            # it is read where it is; one of a project is read by its project as before
            self.assertEqual(sessions.read(outside, "claude", "", {"项目": project})["messages"], [{"role": "user", "text": "整理读书笔记"}])
            self.assertEqual(sessions.read(codex_out, "codex", "", {"项目": project})["messages"], [])
            for wrong in ((inside, "claude", ""), (scratch, "claude", ""), (outside, "codex", "")):
                with self.assertRaises(agent.OpError):
                    sessions.read(*wrong, {"项目": project})
            # once its folder is a project it is one of that project's
            joined = {s["id"]: s for s in sessions.scan({"项目": project, "笔记": apart}, [])}
            self.assertEqual([(joined[s]["dir"], joined[s]["folder"]) for s in (outside, codex_out, codex_idle)], [("笔记", ""), ("笔记", ""), ("笔记", "")])
        finally:
            if before is not None:
                os.environ["CODEX_HOME"] = before
        for folder, said in ((apart, True), ("/", False), ("relative/x", False), ("/tmp", False), ("/var/tmp/x", False), ("/tmpfiles", True), ("", False), (None, False), ("/a\nb", False)):
            self.assertEqual(agent.elsewhere(folder), said, folder)

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
        cls.saved = "0a1b2c3d-1111-2222-3333-444455556666"
        saved = os.path.join(cls.home, ".claude", "projects", re.sub("[^a-zA-Z0-9]", "-", cls.project))
        os.makedirs(saved)
        with open(os.path.join(saved, cls.saved + ".jsonl"), "w", encoding="utf-8") as stream:
            stream.write(json.dumps({"type": "user", "message": {"role": "user", "content": "上次说到哪了"}}, ensure_ascii=False) + "\n")
            stream.write(json.dumps({"type": "ai-title", "aiTitle": "上次的对话", "sessionId": cls.saved}) + "\n")
        with open(os.path.join(cls.project, "说明.txt"), "w", encoding="utf-8") as stream:
            stream.write("文件内容 e2e\n")
        cls.agent = subprocess.Popen(cls.agent_command(agent_data),
                                     env={**os.environ, "RCLI_PASSWORD": PASSWORD, "HOME": cls.home},
                                     stdout=subprocess.DEVNULL, stderr=open(os.path.join(cls.tmp, "agent.log"), "wb"))
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
        self.assertEqual(device["tools"], ["claude", "shell"])
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
            if agent is not None:
                agent.terminate()
            relay.terminate()
            import shutil
            shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
