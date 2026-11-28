"""Work a conversation the phone runs hands to another AI on this computer by itself (`delegate_task`, offered to it
by peers.py), as the Windows program does (agent-windows/Delegations.cs). Each task is a conversation of its own,
begun by this program, that the phone can open; it runs while the conversation that gave it out goes on. When it
ends its result is told to that conversation: at once where it waits, else as soon as its turn ends, the results of
several that ended meanwhile in one message. A question the task asks with choices is told the same way, and may be
answered by that conversation (`task_answer`) or on the phone. Stopping the conversation that gave the work out
stops its tasks; so does its end."""
import re
import secrets
import threading
import time

MOST_RUNNING = 6
RESULT_CHARS = 12000
TASK_CHARS, TITLE_CHARS, STATUS_CHARS = 20000, 40, 24000
ISOLATIONS = ("shared", "worktree")
DELEGATE, STATUS, CANCEL, ANSWER = "delegate_task", "task_status", "task_cancel", "task_answer"
_MODEL = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:/@\[\]-]{0,79}$")
STATES = {"running": "进行中", "waiting": "在等回答", "completed": "已完成", "failed": "失败", "cancelled": "已停止"}


class Refused(Exception):
    """What the hands refuse, in words for the AI that gave the work out."""


class Hands:
    """begin(opening) -> {"terminal", "worktree", "branch"}; state_of(terminal) -> starting/busy/confirm/idle or ""
    where it ended; say(terminal, words); stop(terminal); close(terminal); answer(terminal, ask, decision) -> bool;
    card(terminal, card): shows a task's card in the conversation that gave it out; label(tool) -> its name."""

    def __init__(self, begin, state_of, say, stop, close, answer, card, label=None):
        self.begin, self.state_of, self.say, self.stop, self.close = begin, state_of, say, stop, close
        self.answer, self.card, self.label = answer, card, label or (lambda tool: tool)


def _now():
    return int(time.time() * 1000)


def _line(text, most=200):
    for one in str(text or "").replace("\r\n", "\n").split("\n"):
        one = one.strip().lstrip("#-* ").strip()
        if one:
            return one if len(one) <= most else one[:most - 1] + "…"
    return ""


def _fold(text, most):
    if len(text) <= most:
        return text
    head = most * 2 // 3
    return text[:head] + "\n…（中间已省略）…\n" + text[-(most - head - 12):]


def _open(task):
    return task["state"] in ("running", "waiting")


class Delegations:
    def __init__(self, hands, changed=None):
        self.hands, self.changed = hands, changed
        self.gate = threading.RLock()
        self.tasks, self.owed, self.questions = {}, {}, {}

    # ---- what the AI that gives work out asks for (peers.py)
    def start(self, parent, parent_tool, args):
        word = lambda key: str((args or {}).get(key) or "").strip()
        tool, words, title, model = word("tool").lower(), word("task"), _line(word("title")), word("model")
        isolation = word("isolation").lower() or "shared"
        if not words:
            return _fail("请在 task 里写清子任务：背景、范围、负责的文件和要交回的验收证据")
        if len(words) > TASK_CHARS:
            return _fail("子任务说明太长（最多 %d 字），请精简或改为指向项目里的文件" % TASK_CHARS)
        if isolation not in ISOLATIONS:
            return _fail("isolation 只能是 shared 或 worktree")
        if model and not _MODEL.match(model):
            return _fail("模型名称无效")
        title = title or _line(words)
        if len(title) > TITLE_CHARS:
            title = title[:TITLE_CHARS - 1] + "…"
        with self.gate:
            if sum(1 for t in self.tasks.values() if t["parent"] == parent and _open(t)) >= MOST_RUNNING:
                return _fail("这段对话已有 %d 个子任务在进行，请等其中一些完成或用 task_cancel 取消后再派" % MOST_RUNNING)
        task = {"id": secrets.token_hex(4), "parent": parent, "parent_tool": parent_tool or "", "child": "", "tool": tool, "model": model,
                "title": title, "worktree": "", "branch": "", "state": "running", "result": "", "error": "", "started": _now(), "ended": 0,
                "ask": "", "question": "", "choices": [], "delivered": False}
        try:
            began = self.hands.begin({"parent": parent, "parent_tool": task["parent_tool"], "tool": tool, "model": model, "title": title,
                                      "worktree": isolation == "worktree", "words": self._brief(task, words, isolation == "worktree")})
        except Refused as error:
            return _fail(str(error))
        task["child"], task["worktree"], task["branch"] = began["terminal"], began.get("worktree") or "", began.get("branch") or ""
        with self.gate:
            self.tasks[task["id"]] = task
        self._show(task)
        back = self._shown(task)
        back["next"] = "完成后结果会自动作为一条消息送回这段对话。不要查询或等待它；继续做其它事，没有其它事就结束本轮回答。"
        return _ok(back)

    def status(self, parent, task_id):
        task = self._mine(parent, task_id)
        if task is None:
            return _fail("没有找到这个子任务：" + str(task_id))
        child_state = self.hands.state_of(task["child"])
        with self.gate:
            self._settle(task, child_state)
            back = self._shown(task)
            if task["state"] in ("completed", "failed"):
                if task["result"]:
                    back["result"] = _fold(task["result"], STATUS_CHARS)
                task["delivered"] = True
            if task["state"] == "running":
                back["next"] = "子任务还在进行，完成后结果会自动送回，不必再查询。"
            return _ok(back)

    def cancel(self, parent, task_id, reason=""):
        task = self._mine(parent, task_id)
        if task is None:
            return _fail("没有找到这个子任务：" + str(task_id))
        with self.gate:
            stopping = _open(task)
            if stopping:
                task.update(state="cancelled", error=_line(reason), ended=_now(), delivered=True)
                self._forget(task)
        if stopping:
            self._halt(task)
            self._show(task)
        return _ok(self._shown(task))

    def answer(self, parent, task_id, answer):
        task = self._mine(parent, task_id)
        if task is None:
            return _fail("没有找到这个子任务：" + str(task_id))
        child_state = self.hands.state_of(task["child"])
        with self.gate:
            self._settle(task, child_state)
            if task["state"] != "waiting" or not task["ask"]:
                return _fail("这个子任务现在没有在等回答")
            ask, given, decision = task["ask"], str(answer or "").strip(), None
            if given.isdigit() and 1 <= int(given) <= len(task["choices"]):
                decision = "choice:%d" % (int(given) - 1)
            else:
                for at, choice in enumerate(task["choices"]):
                    if choice.strip().lower() == given.lower():
                        decision = "choice:%d" % at
                        break
            if decision is None:
                return _fail("answer 要写选项的序号（1 至 %d）或选项原文：%s" % (len(task["choices"]), _choices(task)))
        if not self.hands.answer(task["child"], ask, decision):
            return _fail("这个问题已经回答过或不再需要回答")
        with self.gate:
            task.update(state="running", ask="", question="", choices=[])
        self._show(task)
        return _ok(self._shown(task))

    # ---- what the conversations do
    def turned(self, terminal, how, said):
        """A turn of a conversation the phone runs ended. A task's: its result is kept and told. One that gave work
        out: what is owed to it is told now; stopped, its tasks are stopped too."""
        with self.gate:
            ended = next((t for t in self.tasks.values() if t["child"] == terminal and _open(t)), None)
            if ended is not None:
                state = "completed" if how == "done" else "cancelled" if how == "stopped" else "failed"
                result = str(said or "").strip()
                ended.update(state=state, result=result, ended=_now(), ask="", question="", choices=[],
                             error=(_line(result) or "子任务出错") if state == "failed" else "在手机上被停止" if state == "cancelled" else "")
                self._owe(ended["parent"], ended["id"], False)
            parent = any(t["parent"] == terminal for t in self.tasks.values())
        if ended is not None:
            self._show(ended)
            try:
                self.hands.close(ended["child"])
            except Exception:
                pass
            if self._idle(ended["parent"]):
                self._deliver(ended["parent"])
        if not parent:
            return
        if how == "stopped":
            self._stop_all(terminal, "原对话被停止")
        self._deliver(terminal)

    def asked(self, terminal, ask, kind, title, text, choices):
        with self.gate:
            task = next((t for t in self.tasks.values() if t["child"] == terminal and _open(t)), None)
            if task is None or kind != "question" or not choices:
                return
            task.update(state="waiting", ask=ask, question=_line(str(title or "") + " " + str(text or ""), 600), choices=list(choices))
            self._owe(task["parent"], task["id"], True)
        self._show(task)
        if self._idle(task["parent"]):
            self._deliver(task["parent"])

    def sweep(self):
        """Now and then: a task whose conversation ended by itself failed; the tasks of a conversation that ended
        stop; a question answered on the phone is no longer waited for."""
        with self.gate:
            every = list(self.tasks.values())
        states = {}
        for task in every:
            for terminal in (task["parent"], task["child"]):
                if terminal not in states:
                    states[terminal] = self.hands.state_of(terminal) or ""
        gone, moved, parents = [], [], set()
        with self.gate:
            for task in [t for t in self.tasks.values() if _open(t) and t["child"] in states]:
                if not states.get(task["parent"]):
                    parents.add(task["parent"])
                    continue
                state = states.get(task["child"], "")
                if not state:
                    task.update(state="failed", error="子任务的对话已经结束", ended=_now())
                    self._owe(task["parent"], task["id"], False)
                    gone.append(task)
                elif task["state"] == "waiting" and state != "confirm":
                    task.update(state="running", ask="", question="", choices=[])
                    moved.append(task)
            for task_id in [k for k, t in self.tasks.items() if not _open(t) and t["parent"] in states and not states[t["parent"]]]:
                self._forget(self.tasks.pop(task_id))
        for parent in parents:
            self._stop_all(parent, "原对话已结束")
        for task in gone + moved:
            self._show(task)
        for parent in {t["parent"] for t in gone}:
            if self._idle(parent):
                self._deliver(parent)

    # ---- what the phone lists
    def of(self, terminal):
        with self.gate:
            return next((dict(t) for t in self.tasks.values() if t["child"] == terminal), None)

    def given(self, parent):
        with self.gate:
            return sorted((dict(t) for t in self.tasks.values() if t["parent"] == parent), key=lambda t: t["started"])

    # ---- inside
    def _mine(self, parent, task_id):
        with self.gate:
            task = self.tasks.get(str(task_id or "").strip())
            return task if task is not None and task["parent"] == parent else None

    @staticmethod
    def _settle(task, child_state):
        if task["state"] == "waiting" and child_state != "confirm":
            task.update(state="running", ask="", question="", choices=[])

    def _idle(self, terminal):
        return self.hands.state_of(terminal) == "idle"

    def _owe(self, parent, task_id, question):
        into = self.questions if question else self.owed
        listed = into.setdefault(parent, [])
        if task_id not in listed:
            listed.append(task_id)

    def _forget(self, task):
        for into in (self.owed, self.questions):
            if task["id"] in into.get(task["parent"], []):
                into[task["parent"]].remove(task["id"])

    def _stop_all(self, parent, why):
        with self.gate:
            stopping = [t for t in self.tasks.values() if t["parent"] == parent and _open(t)]
            for task in stopping:
                task.update(state="cancelled", error=why, ended=_now(), delivered=True)
                self._forget(task)
        for task in stopping:
            self._halt(task)
            self._show(task)

    def _halt(self, task):
        for act in (self.hands.stop, self.hands.close):
            try:
                act(task["child"])
            except Exception:
                pass

    def _deliver(self, parent):
        with self.gate:
            done = [self.tasks[k] for k in self.owed.pop(parent, []) if k in self.tasks and not self.tasks[k]["delivered"]]
            waiting = [self.tasks[k] for k in self.questions.pop(parent, []) if k in self.tasks and self.tasks[k]["state"] == "waiting"]
            if not done and not waiting:
                return
            for task in done:
                task["delivered"] = True
            still = sum(1 for t in self.tasks.values() if t["parent"] == parent and t["state"] == "running")
            words = self._told(done, waiting, still)
        try:
            self.hands.say(parent, words)
        except Exception:
            pass

    def _told(self, done, waiting, still):
        """The message that brings results and questions back. It says whose they are, so that it is not taken for
        the user's own words."""
        text = ["【子任务结果】以下是你派出的子任务的结果，由 Remote AI 送回，不是用户的新要求。" if done else
                "【子任务提问】你派出的子任务在等回答，由 Remote AI 转来，不是用户的新要求。"]
        room = RESULT_CHARS
        for task in done:
            text.append("\n\n## 子任务 %s（%s）%s" % (task["id"], self._who(task), _ending(task)))
            if task["worktree"]:
                text.append("\n改动在独立工作树 %s%s，主目录未改动；是否合入由你检查后决定。" % (task["worktree"], "（分支 %s）" % task["branch"] if task["branch"] else ""))
            result = task["result"].strip()
            if not result:
                continue
            if len(result) <= room:
                text.append("\n" + result)
                room -= len(result)
            elif room == RESULT_CHARS:
                text.append("\n" + _fold(result, room) + "\n（中间已省略，全文用 task_status 读取，task_id: %s）" % task["id"])
                room = 0
            else:
                text.append("\n结果较长，用 task_status 读取（task_id: %s）。" % task["id"])
        for task in waiting:
            text.append("\n\n## 子任务 %s（%s）在等回答\n%s\n选项：%s" % (task["id"], self._who(task), task["question"], _choices(task)))
        if waiting:
            text.append("\n\n答案能从任务和用户的要求推出时，用 task_answer 回答（task_id 和选项序号）；推不出时先问用户。用户也可以在手机上直接回答。")
        if still:
            text.append("\n\n还有 %d 个子任务在进行，完成后会再送来，不必等待或查询。" % still)
        if done:
            text.append("\n\n请据此继续原来的工作。")
        return "".join(text)

    def _who(self, task):
        return self.hands.label(task["tool"]) + (" · " + task["model"] if task["model"] else "") + " · " + task["title"]

    @staticmethod
    def _shown(task):
        shown = {"task_id": task["id"], "tool": task["tool"], "title": task["title"], "state": task["state"], "terminal": task["child"],
                 "started": task["started"]}
        if task["model"]:
            shown["model"] = task["model"]
        if task["ended"]:
            shown["ended"] = task["ended"]
        if task["error"]:
            shown["error"] = task["error"]
        if task["worktree"]:
            shown["worktree"], shown["branch"] = task["worktree"], task["branch"]
        if task["state"] == "waiting":
            shown["question"], shown["choices"] = task["question"], list(task["choices"])
        return shown

    def _show(self, task):
        with self.gate:
            card = dict(self._shown(task), t="task", id="task-" + task["id"])
            said = _line(task["result"], 200) if task["state"] == "completed" else ""
            if said:
                card["said"] = said
        try:
            self.hands.card(task["parent"], card)
        except Exception:
            pass
        if self.changed:
            try:
                self.changed()
            except Exception:
                pass

    def _brief(self, task, words, worktree):
        return ("【子任务】" + self.hands.label(task["parent_tool"]) + " 在另一段对话里把这项工作派给你。你看不到那段对话，下面就是全部要求：\n\n" + words
                + ("\n\n你在这个项目的独立 Git 工作树里工作，改动不会影响主目录；不要合并到其它分支。" if worktree else "")
                + "\n\n请自己完成，不要再派出子任务。做完后，在回答的最后写清结果、改动的文件和验证证据；需要用户决定的事写明后结束本轮。")

    def said(self, tool, back):
        """What a task tool answers, in words the AI reads."""
        word = lambda key: str(back.get(key) or "")
        who = self.hands.label(word("tool")) + (" · " + word("model") if word("model") else "") + " · " + word("title")
        state = word("state")
        if tool == DELEGATE:
            text = "已派出子任务 %s（%s）。" % (word("task_id"), who)
            if word("worktree"):
                text += "它在独立工作树 %s（分支 %s）里工作。" % (word("worktree"), word("branch"))
        elif tool == ANSWER:
            text = "已回答子任务 %s 的问题，它会接着做。" % word("task_id")
        else:
            text = "子任务 %s（%s）：%s%s。" % (word("task_id"), who, STATES.get(state, state), "（%s）" % word("error") if word("error") else "")
            if state == "waiting":
                text += "\n问题：%s\n选项：%s" % (word("question"), "；".join("%d. %s" % (n + 1, c) for n, c in enumerate(back.get("choices") or [])))
            if word("result"):
                text += "\n\n" + word("result")
        if word("next"):
            text += "\n" + word("next")
        return text


def _ending(task):
    took = ""
    if task["ended"] > task["started"]:
        seconds = (task["ended"] - task["started"]) // 1000
        took = "%d 秒" % seconds if seconds < 60 else "%d 分 %d 秒" % (seconds // 60, seconds % 60)
    if task["state"] == "completed":
        return "已完成" + ("，用时 " + took if took else "")
    if task["state"] == "cancelled":
        return "已停止" + ("：" + task["error"] if task["error"] else "")
    return "失败" + ("：" + task["error"] if task["error"] else "")


def _choices(task):
    return "；".join("%d. %s" % (n + 1, c) for n, c in enumerate(task["choices"]))


def _ok(back):
    back["ok"] = True
    return back


def _fail(error):
    return {"ok": False, "error": error}
