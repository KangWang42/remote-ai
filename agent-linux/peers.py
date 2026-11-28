"""Lets one conversation on this machine ask another AI tool in the same project for a
read-only second opinion or cross-review (`ask_ai`), delivered as a local MCP server over a
Unix domain socket (agent-windows/Peers.cs uses a named pipe). Each caller conversation gets
its own seat — a token, the project folder, and cached read-only peer chats for follow-ups.
Peer chats are opened without MCP so consultation never recurses, their permission asks are
answered automatically (reads allowed, everything else denied), and all peer chats and
temporary config files are disposed when the caller chat ends.

A conversation the phone runs may also hand work to another AI (`delegate_task`, `task_status`, `task_cancel`,
`task_answer`, kept by delegations.py); a conversation that is such a task is not offered them.

A tool reaches it through a stdio MCP bridge this agent runs as
`python3 agent.py --mcp <socket> <token>`: the bridge speaks the MCP protocol on its standard
input and output and forwards the calls to the socket, one length-prefixed frame per request.
"""
import hashlib
import json
import os
import secrets
import socket
import sys
import threading
import time

import delegations as delegations_mod

SERVER_NAME = "remote_ai"
TOOL_NAME = "ask_ai"
TASK_TOOLS = (delegations_mod.DELEGATE, delegations_mod.STATUS, delegations_mod.CANCEL, delegations_mod.ANSWER)
TOOL_NAMES = (TOOL_NAME,) + TASK_TOOLS
MCP_FLAG = "--mcp"
DEFAULT_TIMEOUT_SECONDS = 240
MAX_FRAME_BYTES = 16 * 1024 * 1024
MAX_QUESTION_CHARS = 16000
MAX_ANSWER_CHARS = 24000

STRENGTHS = {
    "codex": "擅长决策拍板、架构权衡、疑难排查与代码审查",
    "claude": "擅长复杂实现、重构思路、文档撰写与深度交叉审查",
    "agy": "擅长 UI 与前端设计、长上下文检索与交叉核验",
    "opencode": "终端里的开源编码代理，擅长直接改代码、跑命令与多模型切换",
    "qoder": "擅长任务拆解、多文件重构与按规范推进",
    "cline": "擅长逐步执行与编辑器联动，适合小步验证的改动",
    "pi": "轻量快速，适合小步修改、速查与交叉检查"}


def socket_path(data_folder):
    """The socket file for one data folder, matching the Windows pipe's per-installation naming."""
    digest = hashlib.sha256(os.path.abspath(data_folder).lower().encode("utf-8")).hexdigest()[:12]
    return os.path.join(data_folder, "peers-" + digest + ".sock")


def _at(given, key):
    return given.get(key) if isinstance(given, dict) else None


def _text(given, key):
    value = _at(given, key)
    return value if isinstance(value, str) else ""


def step_title(peer_tool, question):
    who = "其他 AI" if not peer_tool else peers_label(peer_tool)
    first = ""
    for line in (question or "").replace("\r\n", "\n").split("\n"):
        first = line.strip()
        if first:
            break
    if len(first) > 140:
        first = first[:140] + "…"
    return "请教 " + who + ("：" + first if first else "")


def own_tool(name):
    """Which of this server's tools a chat driver's step calls, by the name its tool gives it; "" for none."""
    name = name or ""
    for one in TOOL_NAMES:
        if name == one or name.endswith("__" + one) or name.endswith("_" + one):
            return one
    return ""


def step_of(tool, given):
    """The title of a step that calls one of this server's tools, and what it shows unfolded."""
    given = given if isinstance(given, dict) else {}
    word = lambda key: str(given.get(key) or "")
    if tool == TOOL_NAME:
        return step_title(word("tool"), word("question")), word("question")
    if tool == delegations_mod.DELEGATE:
        title = word("title").strip() or next((l.strip() for l in word("task").replace("\r\n", "\n").split("\n") if l.strip()), "")
        if len(title) > 60:
            title = title[:60] + "…"
        return "派给 " + peers_label(word("tool")) + ("：" + title if title else ""), word("task")
    if tool == delegations_mod.STATUS:
        return "查看子任务 " + word("task_id"), ""
    if tool == delegations_mod.CANCEL:
        return "停止子任务 " + word("task_id"), word("reason")
    if tool == delegations_mod.ANSWER:
        return "回答子任务 " + word("task_id") + ("：" + word("answer") if word("answer") else ""), word("answer")
    return tool, ""


def peers_label(tool, labels=None):
    known = {"claude": "Claude Code", "codex": "Codex", "agy": "Antigravity CLI", "opencode": "OpenCode",
             "qoder": "Qoder", "cline": "Cline", "pi": "Pi"}
    if labels:
        known.update(labels)
    return known.get(tool, tool)


def read_frame(stream):
    """One length-prefixed frame: a little-endian four-byte length, then the bytes."""
    head = b""
    while len(head) < 4:
        part = stream.recv(4 - len(head))
        if not part:
            return None
        head += part
    length = int.from_bytes(head, "little")
    if length < 0 or length > MAX_FRAME_BYTES:
        return None
    body = b""
    while len(body) < length:
        part = stream.recv(length - len(body))
        if not part:
            return None
        body += part
    return body


def write_frame(stream, body):
    stream.sendall(len(body).to_bytes(4, "little") + body)


class McpServer:
    """The MCP server a chat is offered: what the tool is told to launch, and what is undone
    when the conversation ends."""

    def __init__(self, name, command, args, config_file, on_dispose=None):
        self.name, self.command, self.args = name, command, args
        self.config_file = config_file
        self.on_dispose = on_dispose

    def dispose(self):
        if self.on_dispose:
            self.on_dispose()


class Peers:
    """The seats of this machine, and the Unix-domain socket that serves them."""

    def __init__(self, sock_path, find_tool, quota_of=None, config_dir="", labels=None,
                 open_read_only=None, chat_tools=("claude", "codex"), bridge_script=None, delegations=None):
        self.path = sock_path
        self.find_tool = find_tool or (lambda tool: None)
        self.quota_of = quota_of or (lambda tool: [])
        self.config_dir = config_dir
        self.labels = labels or {}
        self.open_read_only = open_read_only      # (tool, launcher, folder) -> a Chat without MCP
        self.chat_tools = tuple(chat_tools)
        self.bridge_script = os.path.abspath(bridge_script or sys.argv[0])
        self.delegations = delegations
        self.timeout_seconds = DEFAULT_TIMEOUT_SECONDS
        self.gate = threading.RLock()
        self.seats = {}
        self.disposed = False
        self.listener = None
        if self.path and hasattr(socket, "AF_UNIX"):
            os.makedirs(os.path.dirname(os.path.abspath(self.path)), mode=0o700, exist_ok=True)
            if os.path.exists(self.path):
                os.remove(self.path)
            self.listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            self.listener.bind(self.path)
            os.chmod(self.path, 0o600)
            self.listener.listen(8)
            threading.Thread(target=self._accept, daemon=True, name="peers").start()

    def offer(self, caller_tool, folder, terminal="", child=False):
        """Offers `ask_ai` to a conversation of caller_tool working in folder, and to one the phone runs (`terminal`)
        that is no task itself (`child`) the task tools. None when there is nothing to offer."""
        tasks = self.delegations is not None and bool(terminal) and not child and bool(self.available_tools())
        if self.disposed or not folder or not (self.available_peers(caller_tool) or tasks):
            return None
        token = secrets.token_hex(16)
        command, args = self.bridge_command(token)
        config_file = self._write_claude_config(token, command, args)
        seat = {"token": token, "caller_tool": caller_tool or "", "folder": folder, "terminal": terminal or "", "child": bool(child),
                "config_file": config_file, "consultants": {}, "closed": False}
        with self.gate:
            if self.disposed:
                self._delete_quietly(config_file)
                return None
            self.seats[token] = seat
        return McpServer(SERVER_NAME, command, args, config_file,
                         on_dispose=lambda: self.withdraw(token))

    def bridge_command(self, token):
        """What the tool launches as its MCP server for this seat: this Python interpreter and
        agent script, told the socket path and the seat's token."""
        return sys.executable, [self.bridge_script, MCP_FLAG, self.path or "", token]

    def _write_claude_config(self, token, command, args):
        if not self.config_dir:
            return ""
        try:
            os.makedirs(self.config_dir, mode=0o700, exist_ok=True)
            file = os.path.join(self.config_dir, "mcp-" + token + ".json")
            fd = os.open(file, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as stream:
                json.dump({"mcpServers": {SERVER_NAME: {"command": command, "args": args}}}, stream)
            return file
        except OSError:
            return ""

    def withdraw(self, token):
        with self.gate:
            seat = self.seats.pop(token, None)
        if seat:
            self._close_seat(seat)

    def _close_seat(self, seat):
        with self.gate:
            seat["closed"] = True
            chats = [one["chat"] for one in seat["consultants"].values() if one.get("chat")]
            seat["consultants"] = {}
        for chat in chats:
            try:
                chat.dispose()
            except Exception:
                pass
        self._delete_quietly(seat.get("config_file"))

    @staticmethod
    def _delete_quietly(file):
        if file:
            try:
                os.remove(file)
            except OSError:
                pass

    def dispose_all(self):
        if self.disposed:
            return
        self.disposed = True
        with self.gate:
            active = list(self.seats.values())
            self.seats.clear()
        for seat in active:
            self._close_seat(seat)
        if self.listener is not None:
            try:
                self.listener.close()
            except OSError:
                pass
            self.listener = None
        if self.path:
            self._delete_quietly(self.path)

    def available_tools(self):
        return [tool for tool in self.chat_tools if self.find_tool(tool)]

    def available_peers(self, caller_tool):
        return [tool for tool in self.available_tools() if tool != caller_tool]

    def can_delegate(self, seat):
        return self.delegations is not None and bool(seat.get("terminal")) and not seat.get("child") and bool(self.available_tools())

    def tool_lines(self, tools):
        """One line per tool for the lists the AI reads: its name, what it is good at, and how much is left."""
        lines = []
        for peer in tools:
            strength = "，" + STRENGTHS[peer] if peer in STRENGTHS else ""
            quota = ""
            try:
                room = self.room(self.quota_of(peer))
            except Exception:
                room = "额度未知"
            if room != "额度未知":
                quota = "；" + room + ("（非必要请勿调用）" if room.endswith("紧张") else "")
            lines.append("- " + peer + "（" + peers_label(peer, self.labels) + strength + quota + "）")
        return lines

    def tool_definitions(self, seat):
        tasks = self.task_tool_definitions(self.available_tools()) if self.can_delegate(seat) else []
        peers = self.available_peers(seat["caller_tool"])
        if not peers:
            return tasks
        lines = self.tool_lines(peers)
        description = ("向同一项目中的另一位 AI 只读请教或请求交叉审查（例如架构权衡、疑难排查、方案复核或代码审查）。"
                       "对方默认只读，只给意见、不修改文件；同一段对话内再次请教同一 AI 会延续上下文，传入 fresh=true 可开启新上下文。\n"
                       "当前可请教的 AI：\n" + "\n".join(lines) +
                       "\n标记“紧张”的 AI 额度已用 80% 以上，尽量减少调用或保持问题简短。")
        return [{"name": TOOL_NAME, "description": description,
                 "inputSchema": {"type": "object", "properties": {
                     "tool": {"type": "string", "enum": peers,
                              "description": "要请教的 AI 工具名：" + " / ".join(peers)},
                     "question": {"type": "string",
                                  "description": "具体问题、背景、涉及的文件路径及希望对方重点核验或拍板的点"},
                     "fresh": {"type": "boolean",
                               "description": "为 true 时不延续本次对话里与该 AI 之前的请教记录，另起一段新的只读上下文（默认 false）"}},
                     "required": ["tool", "question"], "additionalProperties": False},
                 "annotations": {"title": "请教另一位 AI（只读）", "readOnlyHint": True,
                                 "destructiveHint": False, "idempotentHint": False, "openWorldHint": False}}] + tasks

    def task_tool_definitions(self, tools):
        """The task tools (the same words as agent-windows/Peers.cs): their descriptions are the rules the AI works by."""
        schema = lambda properties, required: {"type": "object", "properties": properties, "required": required, "additionalProperties": False}
        text = lambda about: {"type": "string", "description": about}
        marks = lambda title, read_only, destructive: {"title": title, "readOnlyHint": read_only, "destructiveHint": destructive,
                                                       "idempotentHint": read_only, "openWorldHint": False}
        task_id = text("delegate_task 返回的 task_id")
        about = ("派一位 AI 在这台电脑上独立完成一项子任务。它是一段新的对话，看不到你的对话历史，task 里要写清背景、范围、负责的文件和要交回的验收证据。"
                 "调用会立即返回 task_id，子任务在后台进行，完成后结果会作为一条消息自动送回这段对话：不要查询或等待，继续做其它事，没有其它事就结束本轮回答。\n"
                 "适合：需要另一种工具的长处；几块互不依赖的工作要并行；需要独立的 Git 工作树（isolation=worktree，改动不进主目录）。\n"
                 "不适合：简单的活自己做，不为分工而拆分；同一工具、同一模型的小活用你自带的子代理或自己做；审查意见里的小问题自己改。"
                 "每次复审都派一个新的子任务，写明要点、已有发现和异议。用户明确要求“协作任务”时才用 start_collaboration。\n"
                 "子任务的权限不超过本对话；本对话被停止或结束时，子任务一并停止。\n"
                 "可派的 AI：\n" + "\n".join(self.tool_lines(tools)))
        return [
            {"name": delegations_mod.DELEGATE, "description": about, "inputSchema": schema({
                "tool": {"type": "string", "enum": list(tools), "description": "做这项子任务的 AI：" + " / ".join(tools)},
                "task": text("完整的任务说明：背景与已定约束、成品与范围（负责哪些文件、不碰哪些）、接口约定、已有线索、要交回的验收证据"),
                "title": text("卡片上显示的简短标题，不超过 20 字"),
                "model": text("选填：该工具提供的模型名；省略用工具默认模型"),
                "isolation": {"type": "string", "enum": list(delegations_mod.ISOLATIONS),
                              "description": "shared（默认）：在本项目目录里工作；worktree：在新的独立 Git 工作树里工作，改动不进主目录"}}, ["tool", "task"]),
             "annotations": marks("派出子任务", False, False)},
            {"name": delegations_mod.STATUS, "description": "读取你派出的一个子任务的状态和结果。只在送回的消息说“用 task_status 读取”或用户问起时用；不要用来轮询进行中的子任务。",
             "inputSchema": schema({"task_id": task_id}, ["task_id"]), "annotations": marks("查看子任务", True, False)},
            {"name": delegations_mod.CANCEL, "description": "停止你派出的一个子任务：不再需要、要换人重做或用户要求少派时。已经结束的子任务保留结果。",
             "inputSchema": schema({"task_id": task_id, "reason": text("选填：停止的原因")}, ["task_id"]), "annotations": marks("停止子任务", False, True)},
            {"name": delegations_mod.ANSWER, "description": "回答子任务提出的问题（送回的消息会列出问题和选项）。答案能从任务和用户的要求推出时才代答，推不出时先问用户。",
             "inputSchema": schema({"task_id": task_id, "answer": text("选项的序号（从 1 开始）或选项原文")}, ["task_id", "answer"]),
             "annotations": marks("回答子任务的问题", False, False)}]

    def task_call(self, seat, name, args):
        if not self.can_delegate(seat):
            return self._fail("这段对话不能派出子任务")
        args = args if isinstance(args, dict) else {}
        d, task_id = self.delegations, str(args.get("task_id") or "")
        back = (d.start(seat["terminal"], seat["caller_tool"], args) if name == delegations_mod.DELEGATE
                else d.status(seat["terminal"], task_id) if name == delegations_mod.STATUS
                else d.cancel(seat["terminal"], task_id, str(args.get("reason") or "")) if name == delegations_mod.CANCEL
                else d.answer(seat["terminal"], task_id, str(args.get("answer") if args.get("answer") is not None else "")) if name == delegations_mod.ANSWER
                else self._fail("不支持的工具：" + str(name)))
        if back.get("ok") is not True:
            return back
        return {"ok": True, "text": d.said(name, back)}

    def room(self, windows):
        if not windows:
            return "额度未知"
        said = []
        for one in windows:
            name = {"five_hour": "5 小时窗口", "seven_day": "本周"}.get(one["name"], one["name"])
            said.append(name + "已用 " + str(round(one["used"])) + "%" +
                        ("（" + _when(one["resets"]) + " 重置）" if one.get("resets", 0) > 0 else ""))
        exhausted = any(round(one["used"]) >= 100 for one in windows)
        tag = "，已用尽（不可派工）" if exhausted else ("，紧张" if any(round(one["used"]) >= 80 for one in windows) else "")
        return "额度：" + "，".join(said) + tag

    def ask(self, seat, peer_tool, question, fresh):
        peer_tool = (peer_tool or "").strip().lower()
        question = (question or "").strip()
        if not question:
            return self._fail("请提供要请教的具体问题")
        if len(question) > MAX_QUESTION_CHARS:
            question = question[:MAX_QUESTION_CHARS] + "\n…（问题过长，后文已省略）"
        if peer_tool not in self.chat_tools:
            return self._fail("不支持请教工具：" + peer_tool)
        if peer_tool == seat["caller_tool"]:
            return self._fail("不能请教自己，请选择另一位 AI")
        launcher = self.find_tool(peer_tool)
        if not launcher:
            return self._fail("电脑上没有安装 " + peers_label(peer_tool, self.labels))
        if self.open_read_only is None:
            return self._fail("当前环境不支持只读请教")

        with self.gate:
            if seat["closed"] or self.disposed:
                return self._fail("原对话已结束")
            consultant = seat["consultants"].setdefault(
                peer_tool, {"chat": None, "turns": 0, "lock": threading.Lock()})

        with consultant["lock"]:
            with self.gate:
                if seat["closed"] or self.disposed:
                    return self._fail("请教已取消")
                if fresh and consultant.get("chat"):
                    self._reset(consultant["chat"])
                    consultant["chat"], consultant["turns"] = None, 0
                if consultant.get("chat") is None or consultant["chat"].closed:
                    consultant["turns"] = 0
                    try:
                        consultant["chat"] = self.open_read_only(peer_tool, launcher, seat["folder"])
                    except Exception as ex:
                        return self._fail("未能启动 " + peers_label(peer_tool, self.labels) + "：" + str(ex))
                    consultant["chat"].auto_ask = True      # reads allowed, everything else denied
                chat = consultant["chat"]
                turns = consultant["turns"]

            prompt = ("【只读请教】同一项目里的另一位 AI（" + peers_label(seat["caller_tool"], self.labels) +
                      "）向你请教以下问题。请只读查看所需文件并直接给出简明、具体、可操作的意见；不要修改任何文件，"
                      "不要执行会改动状态的命令，也不要等待额外确认。\n\n" + question if turns == 0 else "【继续请教】\n" + question)
            with self.gate:
                consultant["turns"] = turns + 1

            answer = self._wait_turn(chat, prompt, seat)
            if answer is None:
                with self.gate:
                    consultant["chat"], consultant["turns"] = None, 0
                return self._fail(peers_label(peer_tool, self.labels) + " 回答超时（超过 " + str(self.timeout_seconds) + " 秒）")
            how, text = answer
            if how != "done":
                err = "被停止" if how == "stopped" else (text.strip()[:200] if text and text.strip() else "执行出错")
                if chat.closed:
                    with self.gate:
                        consultant["chat"], consultant["turns"] = None, 0
                return self._fail(peers_label(peer_tool, self.labels) + " 未能完成回答：" + err)
            text = (text or "").strip()
            if not text:
                return self._fail(peers_label(peer_tool, self.labels) + " 没有给出文字回答")
            if len(text) > MAX_ANSWER_CHARS:
                text = text[:MAX_ANSWER_CHARS] + "\n…（回答过长，后文已省略）"
            return {"ok": True, "text": text}

    def _wait_turn(self, chat, prompt, seat):
        """Says the prompt and waits for the turn to end (or the seat to close, the chat to
        end, or the ask to time out). Returns (how, said) or None on a timeout."""
        done = threading.Event()
        answer = {}

        def on_turned(how, said):
            answer["how"], answer["said"] = how, said or ""
            done.set()

        chat.turned = on_turned
        try:
            try:
                chat.say(prompt)
            except Exception as ex:
                self._reset(chat)
                return ("failed", str(ex))
            deadline = time.time() + self.timeout_seconds
            while not done.wait(0.2):
                if seat["closed"] or self.disposed:
                    try:
                        chat.stop_turn()
                    except Exception:
                        pass
                    return ("failed", "请教已取消")
                if chat.closed:
                    self._reset(chat)
                    return ("failed", "已提前退出")
                if time.time() >= deadline:
                    try:
                        chat.stop_turn()
                    except Exception:
                        pass
                    self._reset(chat)
                    return None
        finally:
            chat.turned = None
        return answer.get("how", ""), answer.get("said", "")

    @staticmethod
    def _reset(chat):
        try:
            chat.dispose()
        except Exception:
            pass

    @staticmethod
    def _fail(error):
        return {"ok": False, "error": error}

    def handle_request(self, req):
        """Handles one decoded request dict; shared by the socket server and unit tests."""
        if not isinstance(req, dict):
            return self._fail("不支持的请求")
        op, token = _text(req, "op"), _text(req, "token")
        with self.gate:
            seat = self.seats.get(token)
        if seat is None or seat["closed"]:
            return {"ok": True, "tools": []} if op == "list" else self._fail("当前对话已结束，无法请教其他 AI")
        if op == "list":
            return {"ok": True, "tools": self.tool_definitions(seat)}
        if op == "call":
            return self.ask(seat, _text(req, "tool"), _text(req, "question"), _at(req, "fresh") is True)
        if op == "task":
            return self.task_call(seat, _text(req, "name"), _at(req, "args"))
        return self._fail("不支持的请求")

    # ---- the socket server: one frame in, one reply out (as the Windows pipe serves)
    def _accept(self):
        while not self.disposed and self.listener is not None:
            try:
                conn, _ = self.listener.accept()
            except OSError:
                if self.disposed:
                    return
                continue
            threading.Thread(target=self._serve, args=(conn,), daemon=True).start()

    def _serve(self, conn):
        with conn:
            try:
                raw = read_frame(conn)
                if raw is None:
                    return
                req = json.loads(raw.decode("utf-8"))
                reply = self.handle_request(req)
                write_frame(conn, json.dumps(reply, ensure_ascii=False).encode("utf-8"))
            except (OSError, ValueError):
                pass


def _when(seconds):
    moment = time.localtime(max(0, seconds))
    return time.strftime("%H:%M", moment) if time.strftime("%Y-%m-%d", moment) == time.strftime("%Y-%m-%d") \
        else time.strftime("%m月%d日 %H:%M", moment)


def bridge(sock_path, token, input=None, output=None, call_handler=None):
    """A stdio MCP server for one seat: the tool launches this as
    `python3 agent.py --mcp <socket> <token>`, and the calls it is asked forward to the socket.
    Runs until the input ends. Returns 0."""
    write_lock = threading.Lock()
    stream_in = input if input is not None else sys.stdin
    stream_out = output if output is not None else sys.stdout

    def send(message):
        with write_lock:
            stream_out.write(json.dumps(message, ensure_ascii=False) + "\n")
            stream_out.flush()

    def reply(request_id, result):
        send({"jsonrpc": "2.0", "id": request_id, "result": result})

    def fail(request_id, code, message):
        send({"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": message}})

    def tool_result(text, is_error):
        return {"content": [{"type": "text", "text": text or ""}], "isError": is_error}

    def pipe_call(payload, timeout_ms=3000):
        if call_handler is not None:
            return call_handler(payload, timeout_ms)
        try:
            with socket.socket(socket.AF_UNIX) as client:
                client.settimeout(timeout_ms / 1000.0)
                client.connect(sock_path)
                write_frame(client, json.dumps(payload, ensure_ascii=False).encode("utf-8"))
                raw = read_frame(client)
                return json.loads(raw.decode("utf-8")) if raw else None
        except (OSError, ValueError):
            return None

    def dispatch_call(request_id, payload):
        back = pipe_call(payload, (DEFAULT_TIMEOUT_SECONDS + 30) * 1000)
        ok = bool(back and _at(back, "ok") is True)
        text = _text(back, "text") if ok else (_text(back, "error") or ("请教失败" if payload["op"] == "call" else "子任务操作失败")) if back else "电脑端协作服务暂时不可用或调用已取消"
        reply(request_id, tool_result(text, not ok))

    for raw in stream_in:
        raw = raw.strip()
        if not raw:
            continue
        try:
            msg = json.loads(raw)
        except ValueError:
            continue
        if not isinstance(msg, dict) or "id" not in msg:
            continue
        request_id, method = msg["id"], _text(msg, "method")
        params = _at(msg, "params") or {}
        if method == "initialize":
            version = _text(params, "protocolVersion") or "2025-03-26"
            reply(request_id, {"protocolVersion": version,
                               "capabilities": {"tools": {}},
                               "serverInfo": {"name": "remote-ai", "version": "1"}})
        elif method == "ping":
            reply(request_id, {})
        elif method == "resources/list":
            reply(request_id, {"resources": []})
        elif method == "prompts/list":
            reply(request_id, {"prompts": []})
        elif method == "tools/list":
            back = pipe_call({"op": "list", "token": token}, 3000)
            tools = (_at(back, "tools") or []) if back else []
            reply(request_id, {"tools": tools})
        elif method == "tools/call":
            name = _text(params, "name")
            arguments = _at(params, "arguments") or {}
            if name not in TOOL_NAMES:
                fail(request_id, -32602, "不支持的工具：" + name)
                continue
            payload = {"op": "call", "token": token,
                       "tool": _text(arguments, "tool"),
                       "question": _text(arguments, "question"),
                       "fresh": _at(arguments, "fresh") is True} if name == TOOL_NAME else \
                {"op": "task", "token": token, "name": name, "args": arguments if isinstance(arguments, dict) else {}}
            threading.Thread(target=dispatch_call, args=(request_id, payload), daemon=True).start()
        else:
            fail(request_id, -32601, "Method not found")
    return 0
