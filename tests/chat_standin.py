"""A stand-in for Claude Code in its stream-json mode, for `codex app-server` and for `agy acp`, speaking each one's protocol in the
shapes recorded from the real tools (Claude Code 2.1.283, Codex 0.163.0): what a conversation run by the computer
(agent-windows/Chats.cs, agent-linux/agent.py) is checked against. Every turn goes the same way: it says a few
words in pieces, wants to run one command and asks first, prints what the command printed, and says it is done. A
message with "慢" in it runs a command that only ends when the turn is stopped; one with "后台" (Claude Code) sends a
command to the background, ends the turn, and goes on by itself when the command ends.

    python chat_standin.py claude -p --input-format stream-json ... --session-id <id>
    python chat_standin.py codex app-server
"""
import json
import os
import sys
import threading
import time
import uuid

out_lock = threading.Lock()


def record_standin(env_key, entry):
    path = os.environ.get(env_key)
    if not path:
        return
    with out_lock:
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(json.dumps(entry, ensure_ascii=False) + "\n")


def put(message):
    with out_lock:
        sys.stdout.write(json.dumps(message, ensure_ascii=False) + "\n")
        sys.stdout.flush()


def lines():
    for raw in sys.stdin.buffer:
        try:
            yield json.loads(raw.decode("utf-8"))
        except ValueError:
            continue


# ---- Claude Code
def claude(args):
    session = args[args.index("--session-id") + 1] if "--session-id" in args else args[args.index("--resume") + 1]
    answers, stop, turns, peer_turns = {}, threading.Event(), [], [0]

    def said(text):
        message = "msg_" + uuid.uuid4().hex[:12]
        put({"type": "assistant", "message": {"id": message, "content": [{"type": "text", "text": text}]}})

    def peer_reply(text):
        peer_turns[0] += 1
        if "要求改文件" in text:
            use = "toolu_" + uuid.uuid4().hex[:10]
            put({"type": "assistant", "message": {"id": "msg_" + uuid.uuid4().hex[:12], "content": [{"type": "tool_use", "id": use, "name": "Edit", "input": {"file_path": "a.txt"}}]}})
            req = "req-" + uuid.uuid4().hex[:8]
            answered = threading.Event()
            answers[req] = [answered, None]
            put({"type": "control_request", "request_id": req, "request": {"subtype": "can_use_tool", "tool_name": "Edit", "display_name": "Edit", "input": {"file_path": "a.txt"}, "description": "Edit a.txt"}})
            answered.wait(10)
            dec = answers.pop(req)[1] or {}
            verdict = "已拒绝修改" if dec.get("behavior") == "deny" else "意外允许了修改"
            put({"type": "user", "message": {"role": "user", "content": [{"type": "tool_result", "tool_use_id": use, "content": verdict, "is_error": dec.get("behavior") == "deny"}]}})
            reply = f"claude只读回复：{verdict}，仅提供只读建议（第 {peer_turns[0]} 轮）"
        else:
            reply = f"claude只读回复（第 {peer_turns[0]} 轮）：收到 {text.splitlines()[-1]}"
        said(reply)
        put({"type": "result", "subtype": "success", "is_error": False, "result": reply, "session_id": session})

    def call_peer():
        use = "toolu_" + uuid.uuid4().hex[:10]
        inp = {"tool": "codex", "question": "这个锁会死锁吗？"}
        put({"type": "assistant", "message": {"id": "msg_" + uuid.uuid4().hex[:12], "content": [{"type": "tool_use", "id": use, "name": "mcp__remote_ai__ask_ai", "input": inp}]}})
        req = "req-" + uuid.uuid4().hex[:8]
        answered = threading.Event()
        answers[req] = [answered, None]
        put({"type": "control_request", "request_id": req, "request": {"subtype": "can_use_tool", "tool_name": "mcp__remote_ai__ask_ai", "display_name": "ask_ai", "input": inp, "description": "ask_ai"}})
        answered.wait(10)
        dec = answers.pop(req)[1] or {}
        if dec.get("behavior") != "allow":
            put({"type": "user", "message": {"role": "user", "content": [{"type": "tool_result", "tool_use_id": use, "content": "denied", "is_error": True}]}})
            put({"type": "result", "subtype": "success", "is_error": False, "result": "被拒", "session_id": session})
            return
        put({"type": "user", "message": {"role": "user", "content": [{"type": "tool_result", "tool_use_id": use, "content": "不会死锁，因为锁顺序固定。", "is_error": False}]}})
        said("Codex 确认不会死锁。")
        put({"type": "result", "subtype": "success", "is_error": False, "result": "Codex 确认不会死锁。", "session_id": session})

    def background():
        # As Claude Code 2.1.322 does it: the command goes to the background and the turn ends; when the command ends
        # it says so and goes on by itself, in a turn of its own.
        use = "toolu_" + uuid.uuid4().hex[:10]
        put({"type": "assistant", "message": {"id": "msg_" + uuid.uuid4().hex[:12], "content": [{"type": "tool_use", "id": use, "name": "Bash", "input": {"command": "run-tests", "run_in_background": True}}]}})
        put({"type": "system", "subtype": "background_tasks_changed", "tasks": [{"task_id": "b1", "task_type": "local_bash", "description": "测试"}]})
        put({"type": "system", "subtype": "task_started", "task_id": "b1", "tool_use_id": use, "is_backgrounded": True})
        put({"type": "user", "message": {"role": "user", "content": [{"type": "tool_result", "tool_use_id": use, "content": "Command running in background with ID: b1.", "is_error": False}]}})
        said("测试在后台运行，跑完后汇报。")
        put({"type": "result", "subtype": "success", "is_error": False, "result": "测试在后台运行，跑完后汇报。", "session_id": session})
        time.sleep(0.8)
        put({"type": "system", "subtype": "task_notification", "task_id": "b1", "status": "completed", "tool_use_id": use})
        put({"type": "system", "subtype": "background_tasks_changed", "tasks": []})
        put({"type": "system", "subtype": "init", "session_id": session, "cwd": ".", "tools": ["Bash"]})
        put({"type": "system", "subtype": "status", "status": "requesting"})
        said("测试全部通过，改了 checkout.js。")
        put({"type": "result", "subtype": "success", "is_error": False, "result": "测试全部通过，改了 checkout.js。", "session_id": session, "origin": "task-notification"})

    def turn(text):
        put({"type": "system", "subtype": "init", "session_id": session, "cwd": ".", "tools": ["Bash"]})
        put({"type": "user", "message": {"role": "user", "content": text}, "session_id": session, "uuid": str(uuid.uuid4())})
        if "只读请教" in text or "继续请教" in text:
            return peer_reply(text)
        if os.environ.get("STANDIN_TEAM_AUTO") and ("【协作任务" in text or "【用户新一轮要求" in text):
            record_standin("STANDIN_CLAUDE_LOG", {"event": "turn", "args": args, "text": text[:160]})
            reply = "已完成执行成员子任务并自测通过。"
            said(reply)
            put({"type": "result", "subtype": "success", "is_error": False, "result": reply, "session_id": session})
            return
        if "调用请教" in text:
            return call_peer()
        if "后台" in text:
            return background()
        message = "msg_" + uuid.uuid4().hex[:12]
        put({"type": "stream_event", "event": {"type": "message_start", "message": {"id": message}}})
        put({"type": "stream_event", "event": {"type": "content_block_start", "index": 0, "content_block": {"type": "text", "text": ""}}})
        for piece in ("我先", "看看。"):
            time.sleep(0.05)
            put({"type": "stream_event", "event": {"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": piece}}})
        slow = "慢" in text
        use = "toolu_" + uuid.uuid4().hex[:10]
        command = "ping -n 40 127.0.0.1" if slow else "echo chat-ok"
        put({"type": "assistant", "message": {"id": message, "content": [{"type": "tool_use", "id": use, "name": "Bash", "input": {"command": command, "description": "运行命令"}}]}})
        request = "req-" + uuid.uuid4().hex[:8]
        answered = threading.Event()
        answers[request] = [answered, None]
        put({"type": "control_request", "request_id": request, "request": {"subtype": "can_use_tool", "tool_name": "Bash", "display_name": "Bash", "input": {"command": command, "description": "运行命令"},
                                                                          "description": "运行命令", "permission_suggestions": [{"type": "addRules", "rules": [{"toolName": "Bash", "ruleContent": command}], "behavior": "allow", "destination": "session"}]}})
        while not answered.wait(0.05):
            if stop.is_set():
                put({"type": "control_cancel_request", "request_id": request})
                return interrupted(use, "The user doesn't want to proceed with this tool use.")
        decision = answers.pop(request)[1]
        if decision.get("behavior") != "allow":
            put({"type": "user", "message": {"role": "user", "content": [{"type": "tool_result", "tool_use_id": use, "content": decision.get("message", "denied"), "is_error": True}]}})
            put({"type": "result", "subtype": "success", "is_error": False, "result": "", "session_id": session})
            return
        if slow:
            while not stop.wait(0.05):
                pass
            return interrupted(use, "interrupted")
        put({"type": "user", "message": {"role": "user", "content": [{"type": "tool_result", "tool_use_id": use, "content": "chat-ok", "is_error": False}]}})
        message = "msg_" + uuid.uuid4().hex[:12]
        put({"type": "stream_event", "event": {"type": "message_start", "message": {"id": message}}})
        put({"type": "stream_event", "event": {"type": "content_block_start", "index": 0, "content_block": {"type": "text", "text": ""}}})
        put({"type": "stream_event", "event": {"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": "好了：**chat-ok**"}}})
        put({"type": "result", "subtype": "success", "is_error": False, "result": "好了", "session_id": session})

    def interrupted(use, why):
        stop.clear()
        put({"type": "user", "message": {"role": "user", "content": [{"type": "tool_result", "tool_use_id": use, "content": why, "is_error": True}]}})
        put({"type": "user", "message": {"role": "user", "content": [{"type": "text", "text": "[Request interrupted by user for tool use]"}]}})
        put({"type": "result", "subtype": "error_during_execution", "is_error": True, "session_id": session})

    def worker():
        while True:
            while not turns:
                time.sleep(0.02)
            turn(turns.pop(0))

    threading.Thread(target=worker, daemon=True).start()
    for message in lines():
        kind = message.get("type")
        if kind == "control_request":
            sub = message["request"].get("subtype")
            if sub == "interrupt":
                stop.set()
            resp = {}
            if sub == "initialize":
                resp = {"models": [
                    {"value": "default", "resolvedModel": "claude-opus-5-5", "displayName": "Default", "supportsEffort": True, "supportedEffortLevels": ["low", "medium", "high", "xhigh", "max"]},
                    {"value": "opus", "resolvedModel": "claude-opus-5-5", "displayName": "Opus 5.5", "supportsEffort": True, "supportedEffortLevels": ["low", "medium", "high", "xhigh", "max"]},
                    {"value": "sonnet", "resolvedModel": "claude-sonnet-5-5", "displayName": "Sonnet 5.5", "supportsEffort": True, "supportedEffortLevels": ["low", "medium", "high"]},
                    {"value": "claude-haiku-4-5", "resolvedModel": "claude-haiku-4-5", "displayName": "Haiku 4.5", "supportsEffort": False},
                ]}
            put({"type": "control_response", "response": {"subtype": "success", "request_id": message["request_id"], "response": resp}})
        elif kind == "control_response":
            response = message["response"]
            if response["request_id"] in answers:
                answers[response["request_id"]][1] = response.get("response") or {}
                answers[response["request_id"]][0].set()
        elif kind == "user":
            content = message["message"]["content"]
            turns.append(content if isinstance(content, str) else content[0]["text"])


# ---- Codex
def codex(args):
    state = {"thread": "", "turn": "", "next": 0, "peer_turns": 0, "sandbox": ""}
    waiting, stop = {}, threading.Event()

    def note(method, **params):
        put({"method": method, "params": params})

    def turn(text, client):
        turn_id = state["turn"]
        note("turn/started", threadId=state["thread"], turn={"id": turn_id, "status": "inProgress", "items": []})
        note("item/started", threadId=state["thread"], turnId=turn_id, item={"type": "userMessage", "id": "u-" + uuid.uuid4().hex[:8], "clientId": client, "content": [{"type": "text", "text": text}]})
        if "只读请教" in text or "继续请教" in text:
            state["peer_turns"] += 1
            verdict = ""
            if "要求改文件" in text:
                run = "edit-" + uuid.uuid4().hex[:8]
                state["next"] += 1
                num, ev = state["next"], threading.Event()
                waiting[num] = [ev, None]
                put({"id": num, "method": "item/fileChange/requestApproval", "params": {"threadId": state["thread"], "turnId": turn_id, "itemId": run, "reason": "Edit a.txt"}})
                ev.wait(10)
                dec = (waiting.pop(num)[1] or {}).get("decision")
                verdict = "已拒绝修改，" if dec == "decline" else "意外允许修改，"
            msg_id = "msg_" + uuid.uuid4().hex[:10]
            reply = f"codex只读回复（sandbox={state['sandbox']}，第 {state['peer_turns']} 轮）：{verdict}{text.splitlines()[-1]}"
            note("item/started", item={"type": "agentMessage", "id": msg_id, "text": ""}, threadId=state["thread"], turnId=turn_id)
            note("item/completed", item={"type": "agentMessage", "id": msg_id, "text": reply}, threadId=state["thread"], turnId=turn_id)
            return ended("completed", None)
        if os.environ.get("STANDIN_TEAM_AUTO") and ("【协作任务" in text or "【用户新一轮要求" in text):
            msg_id = "msg_" + uuid.uuid4().hex[:10]
            if "【协作任务 · 领队验收" in text:
                reply = "VERIFIED: 运行单元与集成检查全部通过\nDONE: 验收通过并完成任务"
            elif "【协作任务 · 领队】" in text or "【用户新一轮要求" in text:
                if "ASSIGN member-1 codex:" in text:
                    reply = "开始自动组队。\nASSIGN member-1 claude: 实现并自测目标改动"
                else:
                    reply = "开始分派。\nASSIGN member-1: 实现并自测目标改动"
            else:
                reply = "已完成执行成员子任务。"
            note("item/started", item={"type": "agentMessage", "id": msg_id, "text": ""}, threadId=state["thread"], turnId=turn_id)
            note("item/completed", item={"type": "agentMessage", "id": msg_id, "text": reply}, threadId=state["thread"], turnId=turn_id)
            return ended("completed", None)
        if "调用请教" in text:
            call_id = "mcp-" + uuid.uuid4().hex[:8]
            note("item/started", threadId=state["thread"], turnId=turn_id, item={"type": "mcpToolCall", "id": call_id, "server": "remote_ai", "tool": "ask_ai", "arguments": {"tool": "claude", "question": "方案可行吗？"}})
            note("item/completed", threadId=state["thread"], turnId=turn_id, item={"type": "mcpToolCall", "id": call_id, "server": "remote_ai", "tool": "ask_ai", "status": "completed", "result": {"content": [{"type": "text", "text": "方案可行，建议拆出接口。"}], "isError": False}})
            msg_id = "msg_" + uuid.uuid4().hex[:10]
            note("item/started", item={"type": "agentMessage", "id": msg_id, "text": ""}, threadId=state["thread"], turnId=turn_id)
            note("item/completed", item={"type": "agentMessage", "id": msg_id, "text": "已采纳 Claude 的建议。"}, threadId=state["thread"], turnId=turn_id)
            return ended("completed", None)
        said = "msg_" + uuid.uuid4().hex[:10]
        note("item/started", item={"type": "agentMessage", "id": said, "text": "", "phase": "commentary"}, threadId=state["thread"], turnId=turn_id)
        for piece in ("我先", "看看。"):
            time.sleep(0.05)
            note("item/agentMessage/delta", threadId=state["thread"], turnId=turn_id, itemId=said, delta=piece)
        note("item/completed", item={"type": "agentMessage", "id": said, "text": "我先看看。"}, threadId=state["thread"], turnId=turn_id)
        slow = "慢" in text
        command = "ping -n 40 127.0.0.1" if slow else "echo chat-ok"
        run = "exec-" + uuid.uuid4().hex[:8]
        note("item/started", threadId=state["thread"], turnId=turn_id, item={"type": "commandExecution", "id": run, "command": "powershell -Command '%s'" % command,
                                                                            "commandActions": [{"type": "unknown", "command": command}], "status": "inProgress"})
        state["next"] += 1
        number, answered = state["next"], threading.Event()
        waiting[number] = [answered, None]
        put({"id": number, "method": "item/commandExecution/requestApproval", "params": {"threadId": state["thread"], "turnId": turn_id, "itemId": run, "command": command,
                                                                                         "commandActions": [{"type": "unknown", "command": command}], "startedAtMs": int(time.time() * 1000)}})
        while not answered.wait(0.05):
            if stop.is_set():
                note("serverRequest/resolved", threadId=state["thread"], requestId=number)
                return ended("interrupted", run)
        decision = waiting.pop(number)[1].get("decision")
        note("serverRequest/resolved", threadId=state["thread"], requestId=number)
        if decision == "decline":
            note("item/completed", threadId=state["thread"], turnId=turn_id, item={"type": "commandExecution", "id": run, "status": "declined", "exitCode": None, "aggregatedOutput": None})
            return ended("completed", None)
        note("item/commandExecution/outputDelta", threadId=state["thread"], turnId=turn_id, itemId=run, delta="chat-ok\n")
        if slow:
            while not stop.wait(0.05):
                pass
            return ended("interrupted", run)
        note("item/completed", threadId=state["thread"], turnId=turn_id, item={"type": "commandExecution", "id": run, "status": "completed", "exitCode": 0, "aggregatedOutput": "chat-ok\n"})
        said = "msg_" + uuid.uuid4().hex[:10]
        note("item/started", item={"type": "agentMessage", "id": said, "text": ""}, threadId=state["thread"], turnId=turn_id)
        note("item/agentMessage/delta", threadId=state["thread"], turnId=turn_id, itemId=said, delta="好了：**chat-ok**")
        note("item/completed", item={"type": "agentMessage", "id": said, "text": "好了：**chat-ok**"}, threadId=state["thread"], turnId=turn_id)
        ended("completed", None)

    def ended(status, run):
        stop.clear()
        if run:
            note("item/completed", threadId=state["thread"], turnId=state["turn"], item={"type": "commandExecution", "id": run, "status": "failed", "exitCode": -1, "aggregatedOutput": ""})
        done = state["turn"]
        state["turn"] = ""
        note("turn/completed", threadId=state["thread"], turn={"id": done, "status": status, "items": [], "error": None})

    for message in lines():
        method, params, number = message.get("method"), message.get("params") or {}, message.get("id")
        if method is None and number in waiting:
            waiting[number][1] = message.get("result") or {}
            waiting[number][0].set()
            continue
        if method == "initialize":
            put({"id": number, "result": {"userAgent": "stand-in", "platformOs": "windows"}})
        elif method == "model/list":
            put({"id": number, "result": {"data": [
                {"id": "gpt-6.2-sol", "model": "gpt-6.2-sol", "displayName": "GPT-6.2 Sol", "hidden": False, "isDefault": True, "defaultReasoningEffort": "medium",
                 "supportedReasoningEfforts": [{"reasoningEffort": "low"}, {"reasoningEffort": "medium"}, {"reasoningEffort": "high"}, {"reasoningEffort": "xhigh"}, {"reasoningEffort": "ultra"}]},
                {"id": "gpt-6.2-astra", "model": "gpt-6.2-astra", "displayName": "GPT-6.2 Astra", "hidden": False, "isDefault": False, "defaultReasoningEffort": "high",
                 "supportedReasoningEfforts": [{"reasoningEffort": "medium"}, {"reasoningEffort": "high"}, {"reasoningEffort": "xhigh"}]},
                {"id": "gpt-5.4", "model": "gpt-5.4", "displayName": "GPT-5.4", "hidden": False, "isDefault": False, "defaultReasoningEffort": "medium",
                 "supportedReasoningEfforts": [{"reasoningEffort": "low"}, {"reasoningEffort": "medium"}, {"reasoningEffort": "high"}]},
                {"id": "hidden-model", "model": "hidden-model", "displayName": "Hidden", "hidden": True, "supportedReasoningEfforts": [{"reasoningEffort": "high"}]},
            ]}})
        elif method in ("thread/start", "thread/resume"):
            state["sandbox"] = params.get("sandbox") or ""
            state["thread"] = params.get("threadId") or "0199dddd-eeee-7fff-8000-%012d" % int(time.time() * 1000 % 10 ** 12)
            cfg = params.get("config") if isinstance(params.get("config"), dict) else {}
            record_standin("STANDIN_CODEX_LOG", {"event": method, "model": params.get("model") or "", "config_effort": cfg.get("model_reasoning_effort") or ""})
            put({"id": number, "result": {"thread": {"id": state["thread"], "cwd": params.get("cwd")}, "model": params.get("model") or "gpt-6.2-sol", "reasoningEffort": cfg.get("model_reasoning_effort") or "medium"}})
            note("thread/started", thread={"id": state["thread"]})
        elif method == "turn/start":
            state["turn"] = "turn-" + uuid.uuid4().hex[:8]
            record_standin("STANDIN_CODEX_LOG", {"event": "turn/start", "model": params.get("model") or "", "effort": params.get("effort") or "", "text": params["input"][0]["text"][:160]})
            put({"id": number, "result": {"turn": {"id": state["turn"], "status": "inProgress", "items": []}}})
            threading.Thread(target=turn, args=(params["input"][0]["text"], params.get("clientUserMessageId")), daemon=True).start()
        elif method == "turn/steer":
            if params.get("expectedTurnId") != state["turn"] or not state["turn"]:
                put({"id": number, "error": {"code": -32600, "message": "expected active turn id"}})
            else:
                put({"id": number, "result": {"turnId": state["turn"]}})
                note("item/started", threadId=state["thread"], turnId=state["turn"], item={"type": "userMessage", "id": "u-" + uuid.uuid4().hex[:8], "clientId": params.get("clientUserMessageId"),
                                                                                       "content": [{"type": "text", "text": params["input"][0]["text"]}]})
        elif method == "turn/interrupt":
            put({"id": number, "result": {}})
            stop.set()
        elif number is not None:
            put({"id": number, "result": {}})


# ---- Antigravity CLI's ACP server (`agy acp`), in the shapes recorded from agy 1.5.12 (its options from 1.5.13)
def agy(args):
    state = {"session": "", "next": 0, "peer_turns": 0, "mcp": []}
    waiting, stop = {}, threading.Event()
    options = [{"id": "model", "name": "Model", "category": "model", "type": "select", "currentValue": "gemini-4-neon",
                "options": [{"value": "gemini-4-neon", "name": "Gemini 4 Neon"}, {"value": "gemini-4-argon", "name": "Gemini 4 Argon"}]},
               {"id": "thought_level", "name": "Thinking Level", "category": "thought_level", "type": "select", "currentValue": "medium",
                "options": [{"value": v, "name": v} for v in ("low", "medium", "high", "xhigh")]}]

    def update(**what):
        put({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": state["session"], "update": what}})

    def turn(number, text):
        if "只读请教" in text or "继续请教" in text:
            state["peer_turns"] += 1
            extra = ""
            if "读取授权" in text or "要求改文件" in text:
                kind = "read" if "读取授权" in text else "edit"
                call = "call_" + uuid.uuid4().hex[:10]
                state["next"] += 1
                asked, ev = 9000 + state["next"], threading.Event()
                waiting[asked] = [ev, None]
                put({"jsonrpc": "2.0", "id": asked, "method": "session/request_permission", "params": {"sessionId": state["session"],
                     "toolCall": {"toolCallId": call, "kind": kind, "title": "File README.md", "rawInput": {"Path": "README.md"}},
                     "options": [{"optionId": "proceed_once", "name": "Allow", "kind": "allow_once"}, {"optionId": "cancel", "name": "Reject", "kind": "reject_once"}]}})
                ev.wait(10)
                outcome = (waiting.pop(asked)[1] or {}).get("outcome") or {}
                extra = ("允许读取，" if outcome.get("optionId") == "proceed_once" else "拒绝读取，") if kind == "read" else ("已拒绝修改，" if outcome.get("optionId") == "cancel" else "意外允许修改，")
            update(sessionUpdate="agent_message_chunk", content={"type": "text", "text": f"agy只读回复（第 {state['peer_turns']} 轮）：{extra}{text.splitlines()[-1]}"})
            return ended(number, "end_turn", None)
        if "调用请教" in text:
            call = "call_" + uuid.uuid4().hex[:10]
            inp = {"tool": "codex", "question": "边界条件对吗？"}
            update(sessionUpdate="tool_call", toolCallId=call, name="ask_ai", kind="other", status="pending", title="ask_ai", rawInput=inp, args=inp)
            state["next"] += 1
            asked, ev = 9000 + state["next"], threading.Event()
            waiting[asked] = [ev, None]
            put({"jsonrpc": "2.0", "id": asked, "method": "session/request_permission", "params": {"sessionId": state["session"],
                 "toolCall": {"toolCallId": call, "kind": "other", "name": "ask_ai", "title": "ask_ai", "rawInput": inp},
                 "options": [{"optionId": "proceed_once", "name": "Allow", "kind": "allow_once"}, {"optionId": "cancel", "name": "Reject", "kind": "reject_once"}]}})
            ev.wait(10)
            outcome = (waiting.pop(asked)[1] or {}).get("outcome") or {}
            if outcome.get("optionId") != "proceed_once":
                update(sessionUpdate="tool_call_update", toolCallId=call, status="failed")
                return ended(number, "end_turn", None)
            update(sessionUpdate="tool_call_update", toolCallId=call, status="completed", content=[{"type": "content", "content": {"type": "text", "text": "边界条件正确，mcpCount=" + str(len(state["mcp"]))}}])
            update(sessionUpdate="agent_message_chunk", content={"type": "text", "text": "Codex 确认边界条件正确。"})
            return ended(number, "end_turn", None)
        for piece in ("我先", "看看。"):
            time.sleep(0.05)
            update(sessionUpdate="agent_message_chunk", content={"type": "text", "text": piece})
        slow = "慢" in text
        command = "ping -n 40 127.0.0.1" if slow else "echo chat-ok"
        call = "call_" + uuid.uuid4().hex[:10]
        update(sessionUpdate="tool_call", toolCallId=call, name="run_command", kind="execute", status="pending", title="Run command",
               rawInput={"CommandLine": command, "Cwd": "."}, args={"CommandLine": command})
        state["next"] += 1
        asked, answered = 9000 + state["next"], threading.Event()
        waiting[asked] = [answered, None]
        put({"jsonrpc": "2.0", "id": asked, "method": "session/request_permission", "params": {"sessionId": state["session"],
             "toolCall": {"toolCallId": call, "kind": "execute", "title": "Run command", "rawInput": {"CommandLine": command}},
             "options": [{"optionId": "proceed_once", "name": "Allow", "kind": "allow_once"}, {"optionId": "proceed_always", "name": "Always allow", "kind": "allow_always"},
                         {"optionId": "cancel", "name": "Reject", "kind": "reject_once"}]}})
        while not answered.wait(0.05):
            if stop.is_set():
                return ended(number, "cancelled", call)
        outcome = (waiting.pop(asked)[1] or {}).get("outcome") or {}
        if outcome.get("optionId") == "cancel":
            update(sessionUpdate="tool_call_update", toolCallId=call, status="failed")
            return ended(number, "end_turn", None)
        update(sessionUpdate="tool_call_update", toolCallId=call, status="in_progress", content=[{"type": "content", "content": {"type": "text", "text": "chat-ok\n"}}])
        if slow:
            while not stop.wait(0.05):
                pass
            return ended(number, "cancelled", call)
        update(sessionUpdate="tool_call_update", toolCallId=call, status="completed")
        update(sessionUpdate="agent_message_chunk", content={"type": "text", "text": "好了：**chat-ok**"})
        ended(number, "end_turn", None)

    def ended(number, reason, call):
        stop.clear()
        if call:
            update(sessionUpdate="tool_call_update", toolCallId=call, status="failed")
        put({"jsonrpc": "2.0", "id": number, "result": {"stopReason": reason}})

    for message in lines():
        method, params, number = message.get("method"), message.get("params") or {}, message.get("id")
        if method is None and number in waiting:
            waiting[number][1] = message.get("result") or {}
            waiting[number][0].set()
            continue
        if method == "initialize":
            put({"jsonrpc": "2.0", "id": number, "result": {"protocolVersion": 1, "agentCapabilities": {"loadSession": True}}})
        elif method == "session/new":
            state["mcp"] = params.get("mcpServers") or []
            state["session"] = str(uuid.uuid4())
            result = {"sessionId": state["session"]}
            if os.environ.get("STANDIN_AGY_OPTIONS"):        # agy 1.5.13 names its models and thinking levels as options
                result["configOptions"] = options
            put({"jsonrpc": "2.0", "id": number, "result": result})
        elif method == "session/set_config_option" and any(o["id"] == params.get("configId") for o in options):
            option = next(o for o in options if o["id"] == params["configId"])
            if params.get("value") not in [v["value"] for v in option["options"]]:
                put({"jsonrpc": "2.0", "id": number, "error": {"code": -32602, "message": "unknown value"}})
                continue
            option["currentValue"] = params["value"]
            record_standin("STANDIN_AGY_RECORD", {"set": params["configId"], "value": params["value"]})
            put({"jsonrpc": "2.0", "id": number, "result": {"configOptions": options}})
        elif method == "session/load":
            state["mcp"] = params.get("mcpServers") or []
            state["session"] = params["sessionId"]
            update(sessionUpdate="user_message_chunk", content={"type": "text", "text": "replayed, not shown again"})
            put({"jsonrpc": "2.0", "id": number, "result": {}})
        elif method == "session/prompt":
            record_standin("STANDIN_AGY_RECORD", {"prompt": params["prompt"][0]["text"][:80]})
            threading.Thread(target=turn, args=(number, params["prompt"][0]["text"]), daemon=True).start()
        elif method == "session/cancel":
            stop.set()
        elif number is not None:
            put({"jsonrpc": "2.0", "id": number, "error": {"code": -32601, "message": "not here"}})


if __name__ == "__main__":
    tool, rest = sys.argv[1], sys.argv[2:]
    sys.stdout.reconfigure(encoding="utf-8")
    if tool == "claude":
        claude(rest)
    elif tool == "agy":
        agy(rest)
    else:
        codex(rest)
