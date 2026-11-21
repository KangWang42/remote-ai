"""Lets one conversation on this machine ask another AI tool in the same project for a
read-only second opinion or cross-review (`ask_ai`), delivered as a local MCP server over a
Unix domain socket (agent-windows/Peers.cs uses a named pipe). Each caller conversation gets
its own seat — a token, the project folder, and cached read-only peer chats for follow-ups.
Peer chats are opened without MCP so consultation never recurses, their permission asks are
answered automatically (reads allowed, everything else denied), and all peer chats and
temporary config files are disposed when the caller chat ends.

A tool reaches it through a stdio MCP bridge this agent runs as
`agent.py --mcp <socket> <token>`: the bridge speaks the MCP protocol on its standard input
and output and forwards the calls to the socket, one length-prefixed frame per request.
"""
import hashlib
import json
import os
import re
import secrets
import socket
import threading
import time

SERVER_NAME = "remote_ai"
TOOL_NAME = "ask_ai"
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


def peers_label(tool, labels=None):
    known = {"claude": "Claude Code", "codex": "Codex", "agy": "Antigravity CLI", "opencode": "OpenCode",
             "qoder": "Qoder", "cline": "Cline", "pi": "Pi"}
    if labels:
        known.update(labels)
    return known.get(tool, tool)


def read_frame(stream):
    """One length-prefixed frame: a little-endian four-byte length, then the bytes."""
    head = stream.recv(4)
    if len(head) < 4:
        return None
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
    """The seats of this machine, and the socket that serves them."""

    def __init__(self, sock_path, find_tool, quota_of=None, config_dir="", labels=None,
                 open_read_only=None, chat_tools=("claude", "codex", "agy")):
        self.path = sock_path
        self.find_tool = find_tool or (lambda tool: None)
        self.quota_of = quota_of or (lambda tool: [])
        self.config_dir = config_dir
        self.labels = labels or {}
        self.open_read_only = open_read_only      # (tool, launcher, folder) -> a Chat without MCP
        self.chat_tools = tuple(chat_tools)
        self.timeout_seconds = DEFAULT_TIMEOUT_SECONDS
        self.gate = threading.RLock()
        self.seats = {}
        self.disposed = False
        # bound here, so a seat's MCP server can connect as soon as its offer is made
        if os.path.exists(self.path):
            os.remove(self.path)
        self.listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.listener.bind(self.path)
        os.chmod(self.path, 0o600)
        self.listener.listen(8)
        threading.Thread(target=self._accept, daemon=True, name="peers").start()

    def offer(self, caller_tool, folder):
        """Offers `ask_ai` to a conversation of caller_tool working in folder. None when no
        other chat-capable tool is installed here."""
        if self.disposed or not folder or not self.available_peers(caller_tool):
            return None
        token = secrets_token()
        command, args = self.bridge_command(token)
        config_file = self._write_claude_config(token, command, args)
        seat = {"token": token, "caller_tool": caller_tool or "", "folder": folder,
                "config_file": config_file, "consultants": {}, "closed": False}
        with self.gate:
            if self.disposed:
                self._delete_quietly(config_file)
                return None
            self.seats[token] = seat
        return McpServer(SERVER_NAME, command, args, config_file,
                         on_dispose=lambda: self.withdraw(token))

    def bridge_command(self, token):
        """What the tool launches as its MCP server for this seat: this same program, told the
        socket and the seat's token."""
        program = os.path.abspath(sys_argv0())
        return program, [MCP_FLAG, self.path, token]

    def _write_claude_config(self, token, command, args):
        if not self.config_dir:
            return ""
        try:
            os.makedirs(self.config_dir, exist_ok=True)
            file = os.path.join(self.config_dir, token + ".json")
            with open(file, "w", encoding="utf-8") as stream:
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
        try:
            probe = socket.socket(socket.AF_UNIX)
            probe.settimeout(0.1)
            probe.connect(self.path)
            probe.close()
        except OSError:
            pass
        try:
            os.remove(self.path)
        except OSError:
            pass

    def available_peers(self, caller_tool):
        peers = []
        for tool in self.chat_tools:
            if tool != caller_tool and self.find_tool(tool):
                peers.append(tool)
        return peers

    def tool_definitions(self, seat):
        peers = self.available_peers(seat["caller_tool"])
        if not peers:
            return []
        lines = []
        for peer in peers:
            strength = "，" + STRENGTHS[peer] if peer in STRENGTHS else ""
            quota = ""
            try:
                room = self.room(self.quota_of(peer))
            except Exception:
                room = "额度未知"
            if room != "额度未知":
                quota = "；" + room + ("（非必要请勿调用）" if room.endswith("紧张") else "")
            lines.append("- " + peer + "（" + peers_label(peer, self.labels) + strength + quota + "）")
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
                                 "destructiveHint": False, "idempotentHint": False, "openWorldHint": False}}]

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

        with self.gate:
            if seat["closed"] or self.disposed:
                return self._fail("原对话已结束")
            consultants = seat["consultants"]
            consultant = consultants.setdefault(peer_tool, {"chat": None, "turns": 0})

        with self.gate:
            if seat["closed"] or self.disposed:
                return self._fail("请教已取消")
            if fresh and consultant.get("chat"):
                try:
                    consultant["chat"].dispose()
                except Exception:
                    pass
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
            return self._fail(peers_label(peer_tool, self.labels) + " 回答超时（超过 " + str(self.timeout_seconds) + " 秒）")
        how, text = answer
        if how != "done":
            err = "被停止" if how == "stopped" else "执行出错"
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

    # ---- the socket server: one frame in, one reply out (as the Windows pipe serves)
    def _accept(self):
        while not self.disposed:
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
                op, token = _text(req, "op"), _text(req, "token")
                with self.gate:
                    seat = self.seats.get(token)
                if seat is None or seat["closed"]:
                    reply = {"ok": True, "tools": []} if op == "list" else self._fail("当前对话已结束，无法请教其他 AI")
                elif op == "list":
                    reply = {"ok": True, "tools": self.tool_definitions(seat)}
                elif op == "call":
                    reply = self.ask(seat, _text(req, "tool"), _text(req, "question"),
                                     _at(req, "fresh") is True)
                else:
                    reply = self._fail("不支持的请求")
                write_frame(conn, json.dumps(reply, ensure_ascii=False).encode("utf-8"))
            except (OSError, ValueError):
                pass


def _when(seconds):
    moment = time.localtime(max(0, seconds))
    return time.strftime("%H:%M", moment) if time.strftime("%Y-%m-%d", moment) == time.strftime("%Y-%m-%d") \
        else time.strftime("%m月%d日 %H:%M", moment)


def secrets_token():
    import secrets
    return secrets.token_hex(16)


def sys_argv0():
    import sys
    return sys.argv[0]


def bridge(sock_path, token, input=None, output=None):
    """A stdio MCP server for one seat: the tool launches this as
    `agent.py --mcp <socket> <token>`, and the calls it is asked forward to the socket.
    Runs until the input ends. Returns 0."""
    write_lock = threading.Lock()
    inflight = {}
    inflight_lock = threading.Lock()
    import sys
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
        try:
            client = socket.socket(socket.AF_UNIX)
            client.settimeout(timeout_ms / 1000.0)
            client.connect(sock_path)
            write_frame(client, json.dumps(payload, ensure_ascii=False).encode("utf-8"))
            raw = read_frame(client)
            return json.loads(raw.decode("utf-8")) if raw else None
        except (OSError, ValueError):
            return None

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
            if name != TOOL_NAME:
                fail(request_id, -32602, "不支持的工具：" + name)
                continue
            payload = {"op": "call", "token": token,
                       "tool": _text(arguments, "tool"),
                       "question": _text(arguments, "question"),
                       "fresh": _at(arguments, "fresh") is True}
            thread = threading.Thread(target=lambda rid=request_id, payload=payload: (
                (lambda back: reply(rid, tool_result(
                    (_text(back, "text") if back and _at(back, "ok") is True else
                     (_text(back, "error") or "请教失败") if back else "电脑端协作服务暂时不可用或调用已取消"),
                    not (back and _at(back, "ok") is True))))(pipe_call(payload, 60000))), daemon=True).start()
            # the answer arrives on its own thread; the loop keeps reading (a cancelled call's
            # input ends the process, which ends the thread with it)
        else:
            fail(request_id, -32601, "Method not found")
    return 0
