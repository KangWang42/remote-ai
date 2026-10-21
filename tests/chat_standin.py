"""A stand-in for Claude Code in its stream-json mode and for `codex app-server`, speaking each one's protocol in the
shapes recorded from the real tools (Claude Code 2.1.283, Codex 0.163.0): what a conversation run by the computer
(agent-windows/Chats.cs, agent-linux/agent.py) is checked against. Every turn goes the same way: it says a few
words in pieces, wants to run one command and asks first, prints what the command printed, and says it is done. A
message with "慢" in it runs a command that only ends when the turn is stopped.

    python chat_standin.py claude -p --input-format stream-json ... --session-id <id>
    python chat_standin.py codex app-server
"""
import json
import sys
import threading
import time
import uuid

out_lock = threading.Lock()


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
    answers, stop, turns = {}, threading.Event(), []

    def turn(text):
        put({"type": "system", "subtype": "init", "session_id": session, "cwd": ".", "tools": ["Bash"]})
        put({"type": "user", "message": {"role": "user", "content": text}, "session_id": session, "uuid": str(uuid.uuid4())})
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
                                                                          "description": "运行命令", "permission_suggestions": [{"type": "addRules", "rules": [{"toolName": "Bash"}], "behavior": "allow", "destination": "session"}]}})
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
            put({"type": "control_response", "response": {"subtype": "success", "request_id": message["request_id"], "response": {}}})
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
    state = {"thread": "", "turn": "", "next": 0}
    waiting, stop = {}, threading.Event()

    def note(method, **params):
        put({"method": method, "params": params})

    def turn(text, client):
        turn_id = state["turn"]
        note("turn/started", threadId=state["thread"], turn={"id": turn_id, "status": "inProgress", "items": []})
        note("item/started", threadId=state["thread"], turnId=turn_id, item={"type": "userMessage", "id": "u-" + uuid.uuid4().hex[:8], "clientId": client, "content": [{"type": "text", "text": text}]})
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
        elif method in ("thread/start", "thread/resume"):
            state["thread"] = params.get("threadId") or "0199dddd-eeee-7fff-8000-%012d" % int(time.time() * 1000 % 10 ** 12)
            put({"id": number, "result": {"thread": {"id": state["thread"], "cwd": params.get("cwd")}}})
            note("thread/started", thread={"id": state["thread"]})
        elif method == "turn/start":
            state["turn"] = "turn-" + uuid.uuid4().hex[:8]
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


if __name__ == "__main__":
    tool, rest = sys.argv[1], sys.argv[2:]
    sys.stdout.reconfigure(encoding="utf-8")
    if tool == "claude":
        claude(rest)
    else:
        codex(rest)
