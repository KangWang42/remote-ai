"""The tasks a conversation gives out (agent-linux/delegations.py) with stand-in conversations, and the task tools
peers.py offers: the same cases as tests/DelegationsCheck.cs on Windows."""
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import delegations  # noqa: E402
import peers  # noqa: E402


class Stand:
    def __init__(self):
        self.states, self.said, self.stopped, self.closed, self.answered, self.cards, self.last = {}, [], [], [], [], [], None
        self.next = 0

    def hands(self):
        def begin(one):
            if one["tool"] == "nope":
                raise delegations.Refused("电脑上没有安装 nope")
            self.last = one
            self.next += 1
            child = "child-%d" % self.next
            self.states[child] = "busy"
            return {"terminal": child, "worktree": "/wt/" + child if one["worktree"] else "", "branch": ""}

        def say(terminal, words):
            if not self.states.get(terminal):
                raise RuntimeError("ended")
            self.said.append((terminal, words))

        def close(terminal):
            self.closed.append(terminal)
            self.states[terminal] = ""

        def answer(terminal, ask, decision):
            self.answered.append("%s %s %s" % (terminal, ask, decision))
            return self.states.get(terminal) == "confirm"

        return delegations.Hands(begin=begin, state_of=lambda t: self.states.get(t, ""), say=say, stop=self.stopped.append, close=close,
                                 answer=answer, card=lambda t, c: self.cards.append(c), label=peers.peers_label)


class DelegationsTest(unittest.TestCase):
    def setUp(self):
        self.stand = Stand()
        self.tasks = delegations.Delegations(self.stand.hands())
        self.stand.states["parent"] = "busy"

    def start(self, **args):
        back = self.tasks.start("parent", "codex", args)
        self.assertTrue(back["ok"], back)
        return back["task_id"]

    def child(self, task_id):
        return next(t["child"] for t in self.tasks.given("parent") if t["id"] == task_id)

    def test_refused(self):
        self.assertFalse(self.tasks.start("parent", "codex", {"tool": "claude"})["ok"])
        self.assertIn("isolation", self.tasks.start("parent", "codex", {"tool": "claude", "task": "x", "isolation": "copy"})["error"])
        self.assertIn("没有安装", self.tasks.start("parent", "codex", {"tool": "nope", "task": "x"})["error"])
        self.assertEqual(self.stand.next, 0)

    def test_results_told_after_a_busy_turn_together(self):
        back = self.tasks.start("parent", "codex", {"tool": "claude", "task": "核对 docs/a.md 的条文\n第二行", "title": "核证公开法条"})
        a = back["task_id"]
        self.assertTrue(self.stand.last["words"].startswith("【子任务】Codex") and "不要再派出子任务" in self.stand.last["words"])
        self.assertIn("不要查询或等待", self.tasks.said(delegations.DELEGATE, back))
        self.assertEqual((self.stand.cards[-1]["state"], self.stand.cards[-1]["id"]), ("running", "task-" + a))
        b = self.start(tool="agy", task="复核界面文字", isolation="worktree")
        self.assertTrue(self.stand.last["worktree"])
        self.assertEqual(self.tasks.of("child-2")["id"], b)
        self.tasks.turned("child-1", "done", "结论：条文一致。")
        self.assertEqual(self.stand.said, [])
        self.assertIn("child-1", self.stand.closed)
        self.assertEqual(self.stand.cards[-1]["said"], "结论：条文一致。")
        self.tasks.turned("child-2", "failed", "构建失败：缺少依赖")
        self.stand.states["parent"] = "idle"
        self.tasks.turned("parent", "done", "")
        self.assertEqual(len(self.stand.said), 1)
        told = self.stand.said[0][1]
        for part in ("【子任务结果】", "不是用户的新要求", "## 子任务 %s（Claude Code · 核证公开法条）已完成" % a, "结论：条文一致", "失败：构建失败", "独立工作树 /wt/child-2"):
            self.assertIn(part, told)
        status = self.tasks.status("parent", a)
        self.assertEqual(status["state"], "completed")
        self.assertIn("条文一致", status["result"])
        self.assertFalse(self.tasks.status("other", a)["ok"])

    def test_long_result_pointed_to_and_idle_told_at_once(self):
        c, d, e = self.start(tool="claude", task="短"), self.start(tool="claude", task="长"), self.start(tool="claude", task="还在做")
        self.tasks.turned(self.child(c), "done", "短结果")
        self.tasks.turned(self.child(d), "done", "长" * delegations.RESULT_CHARS)
        self.stand.states["parent"] = "idle"
        self.tasks.turned("parent", "done", "")
        told = self.stand.said[-1][1]
        self.assertIn("短结果", told)
        self.assertIn("用 task_status 读取（task_id: %s）" % d, told)
        self.assertIn("还有 1 个子任务在进行", told)
        self.tasks.turned(self.child(e), "done", "做完了")
        self.assertIn("做完了", self.stand.said[-1][1])

    def test_questions(self):
        q = self.start(tool="codex", task="选方案")
        child = self.child(q)
        self.stand.states[child] = "confirm"
        self.stand.states["parent"] = "idle"
        self.tasks.asked(child, "ask-1", "tool", "运行命令", "rm x", [])
        self.assertEqual(self.stand.said, [])
        self.tasks.asked(child, "ask-2", "question", "用哪种格式？", "", ["CSV", "Excel"])
        self.assertTrue(self.stand.said[-1][1].startswith("【子任务提问】") and "1. CSV；2. Excel" in self.stand.said[-1][1])
        self.assertIn("1 至 2", self.tasks.answer("parent", q, "3")["error"])
        self.assertTrue(self.tasks.answer("parent", q, "excel")["ok"])
        self.assertEqual(self.stand.answered[-1], child + " ask-2 choice:1")
        self.assertFalse(self.tasks.answer("parent", q, "1")["ok"])

    def test_stop_cancel_sweep_bound(self):
        s = self.start(tool="claude", task="会被停")
        self.tasks.turned("parent", "stopped", "")
        self.assertIn(self.child(s), self.stand.stopped)
        self.assertEqual(self.tasks.status("parent", s)["state"], "cancelled")
        x = self.start(tool="claude", task="取消我")
        self.assertEqual(self.tasks.cancel("parent", x, "不需要了")["state"], "cancelled")
        y = self.start(tool="claude", task="会意外结束")
        self.stand.states[self.child(y)] = ""
        self.stand.states["parent"] = "idle"
        self.tasks.sweep()
        self.assertEqual(self.tasks.status("parent", y)["state"], "failed")
        self.assertIn("子任务的对话已经结束", self.stand.said[-1][1])
        for n in range(delegations.MOST_RUNNING):
            self.start(tool="claude", task="并行 %d" % n)
        self.assertIn("task_cancel", self.tasks.start("parent", "codex", {"tool": "claude", "task": "多一个"})["error"])
        self.stand.states["parent"] = ""
        self.tasks.sweep()
        self.assertTrue(all(t["state"] in ("cancelled", "completed", "failed") for t in self.tasks.given("parent")))
        self.tasks.sweep()
        self.assertEqual(self.tasks.given("parent"), [])


class TaskToolsTest(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.begun = []

        def begin(one):
            self.begun.append(one)
            return {"terminal": "child-%d" % len(self.begun)}

        hands = delegations.Hands(begin=begin, state_of=lambda t: "busy", say=lambda t, w: None, stop=lambda t: None, close=lambda t: None,
                                  answer=lambda t, a, d: False, card=lambda t, c: None, label=peers.peers_label)
        self.peers = peers.Peers("", lambda tool: "/usr/bin/" + tool, config_dir=self.folder.name, chat_tools=("claude", "codex"),
                                 bridge_script="/opt/remote-ai/agent.py", delegations=delegations.Delegations(hands))

    def tearDown(self):
        self.peers.dispose_all()
        self.folder.cleanup()

    def seat_token(self, server):
        return server.args[-1]

    def test_offered_to_a_conversation_not_to_a_task(self):
        giver = self.peers.offer("claude", self.folder.name, "a" * 32)
        child = self.peers.offer("codex", self.folder.name, "b" * 32, child=True)
        names = [t["name"] for t in self.peers.handle_request({"op": "list", "token": self.seat_token(giver)})["tools"]]
        self.assertEqual(set(names), {"ask_ai", "delegate_task", "task_status", "task_cancel", "task_answer"})
        about = next(t for t in self.peers.handle_request({"op": "list", "token": self.seat_token(giver)})["tools"] if t["name"] == "delegate_task")["description"]
        self.assertIn("不要查询或等待", about)
        self.assertIn("简单的活自己做", about)
        names = [t["name"] for t in self.peers.handle_request({"op": "list", "token": self.seat_token(child)})["tools"]]
        self.assertEqual(names, ["ask_ai"])
        said = self.peers.handle_request({"op": "task", "token": self.seat_token(giver), "name": "delegate_task", "args": {"tool": "codex", "task": "实现导出", "title": "实现导出"}})
        self.assertTrue(said["ok"] and "已派出子任务" in said["text"] and "Codex · 实现导出" in said["text"], said)
        self.assertEqual(self.begun[0]["parent"], "a" * 32)
        refused = self.peers.handle_request({"op": "task", "token": self.seat_token(child), "name": "delegate_task", "args": {"tool": "claude", "task": "再派"}})
        self.assertIn("不能派出子任务", refused["error"])

    def test_steps_named(self):
        self.assertEqual(peers.own_tool("mcp__remote_ai__delegate_task"), "delegate_task")
        self.assertEqual(peers.own_tool("Bash"), "")
        self.assertEqual(peers.step_of("delegate_task", {"tool": "agy", "task": "复核界面文字"}), ("派给 Antigravity CLI：复核界面文字", "复核界面文字"))


if __name__ == "__main__":
    unittest.main()
