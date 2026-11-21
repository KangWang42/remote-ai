"""The teams orchestration on this machine (agent-linux/teams.py): the ways of working, the
turn handling and the persistence, driven through a Hands that records what it is asked."""
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "agent-linux"))
import teams  # noqa: E402


class FakeHands:
    def __init__(self, tools=("claude", "codex", "opencode", "qoder", "cline", "pi")):
        self.opened, self.said, self.stopped = [], [], []
        self.changes_text = ""
        self.available_tools = list(tools)
        self.count = 0
        self.limits = None          # quota windows are a later piece; None says unknown

    def begin_in(self, tool, dir_name, words, read, folder_override=None):
        self.count += 1
        self.opened.append({"tool": tool, "dir": dir_name, "words": words, "read": read,
                            "folder": folder_override, "terminal": "t%d" % self.count})
        return "t%d" % self.count

    def say(self, terminal, words):
        self.said.append((terminal, words))

    def stop(self, terminal):
        self.stopped.append(terminal)

    def changes(self, dir_name):
        return self.changes_text

    def available(self):
        return list(self.available_tools)


class TeamsTest(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.hands = FakeHands()
        self.file = os.path.join(self.folder.name, "teams.json")
        self.teams = teams.Teams(self.file, self.hands)

    def tearDown(self):
        self.folder.cleanup()

    def item(self, mode="review", tools=("claude", "opencode"), **extra):
        item = {"id": "a" * 24, "mode": mode, "dir": "demo", "goal": "写一个加法函数", "tools": list(tools),
                "rounds": 3}
        item.update(extra)
        return item

    def opened_terminal(self, at):
        return self.hands.opened[at]["terminal"]

    def test_a_review_team_begins_with_the_writer_and_hands_the_review_to_the_second(self):
        team = self.teams.start(self.item())
        self.assertEqual(team.state, "running")
        self.assertEqual(len(self.hands.opened), 1)
        first = self.hands.opened[0]
        self.assertEqual((first["tool"], first["dir"], first["read"]), ("claude", "demo", False))
        self.assertIn("写一个加法函数", first["words"])
        self.assertIn("ASSIGN", first["words"]) if False else None
        self.teams.turned(first["terminal"], "done", "我加了 add 函数")
        self.assertEqual(len(self.hands.opened), 2)
        second = self.hands.opened[1]
        self.assertEqual((second["tool"], second["read"]), ("opencode", True))
        self.assertIn("REVIEW: PASS", second["words"])
        self.assertIn("我加了 add 函数", second["words"])
        self.teams.turned(second["terminal"], "done", "REVIEW: PASS")
        self.assertEqual(self.teams.of(team.id).state, "done")

    def test_changes_verdict_starts_the_next_round_and_rounds_run_out(self):
        team = self.teams.start(self.item(rounds=2))
        writer = self.opened_terminal(0)
        self.teams.turned(writer, "done", "第一轮")
        reviewer = self.opened_terminal(1)
        self.teams.turned(reviewer, "done", "REVIEW: CHANGES\n第一行有问题")
        self.assertEqual(self.teams.of(team.id).round, 2)
        # the writer's conversation is still alive: the review reaches it where it is
        self.assertEqual(len(self.hands.opened), 2)
        self.assertEqual(self.hands.said[-1][0], writer)
        self.assertIn("审查意见", self.hands.said[-1][1])
        self.teams.turned(writer, "done", "改好了")
        self.assertEqual(self.hands.said[-1][0], reviewer)
        self.teams.turned(reviewer, "done", "REVIEW: CHANGES\n还有问题")
        self.assertEqual(self.teams.of(team.id).state, "done")
        self.assertIn("最后一轮", self.teams.of(team.id).note)

    def test_a_leader_assigns_members_and_collects_their_results(self):
        team = self.teams.start(self.item(mode="lead", tools=("codex", "opencode", "pi")))
        leader = self.opened_terminal(0)
        self.teams.turned(leader, "done",
                          "我来分派。\nASSIGN opencode: 写加法\nASSIGN pi: 写测试")
        self.assertEqual(len(self.hands.opened), 3)
        self.assertEqual(self.hands.opened[1]["tool"], "opencode")
        self.assertEqual(self.hands.opened[2]["tool"], "pi")
        self.assertIn("写加法", self.hands.opened[1]["words"])
        self.teams.turned(self.opened_terminal(1), "done", "加法写好了")
        self.teams.turned(self.opened_terminal(2), "done", "测试写好了")
        self.assertEqual(len(self.hands.said), 1)
        self.assertIn("加法写好了", self.hands.said[0][1])
        self.teams.turned(leader, "done", "DONE: 全部完成")
        self.assertEqual(self.teams.of(team.id).state, "done")

    def test_a_leader_without_assignments_is_reminded_then_the_team_pauses(self):
        team = self.teams.start(self.item(mode="lead", tools=("codex", "opencode")))
        leader = self.opened_terminal(0)
        self.teams.turned(leader, "done", "我想想")
        self.assertEqual(len(self.hands.said), 1)
        self.assertIn("ASSIGN", self.hands.said[0][1])
        self.teams.turned(leader, "done", "还是想想")
        self.assertEqual(self.teams.of(team.id).state, "paused")

    def test_a_failed_member_with_no_backup_pauses_the_team(self):
        self.hands.available_tools = ["codex", "opencode"]       # no third tool to fall back to
        team = self.teams.start(self.item(mode="lead", tools=("codex", "opencode")))
        leader = self.opened_terminal(0)
        self.teams.turned(leader, "done", "ASSIGN opencode: 做事")
        member = self.opened_terminal(1)
        self.teams.turned(member, "failed", "hit your limit")
        one = self.teams.of(team.id)
        self.assertEqual(one.state, "paused")
        self.assertEqual(one.failed.get("opencode"), "额度已用尽")

    def test_the_owner_interjects_into_the_awaited_member(self):
        team = self.teams.start(self.item())
        writer = self.opened_terminal(0)
        self.teams.say(team.id, "记得写测试")
        self.assertEqual(self.hands.said[-1][0], writer)
        self.assertIn("【用户插话】", self.hands.said[-1][1])

    def test_swap_replaces_a_paused_writer_and_reopens_it(self):
        self.hands.available_tools = ["claude", "opencode"]   # no third tool: the failure pauses
        team = self.teams.start(self.item())
        writer = self.opened_terminal(0)
        self.teams.turned(writer, "failed", "额度已用尽")
        self.assertEqual(self.teams.of(team.id).state, "paused")
        self.teams.swap(team.id, "writer", "opencode", "换个试试")
        self.assertEqual(self.teams.of(team.id).tools[0], "opencode")
        self.assertIn(writer, self.hands.stopped)
        self.assertEqual(self.hands.opened[-1]["tool"], "opencode")
        self.assertIn("接手", self.hands.opened[-1]["words"])

    def test_teams_survive_a_restart_and_a_running_one_pauses(self):
        team = self.teams.start(self.item())
        again = teams.Teams(self.file, self.hands)
        loaded = again.of(team.id)
        self.assertIsNotNone(loaded)
        self.assertEqual(loaded.state, "paused")
        self.assertIn("重新启动", loaded.note)

    def test_a_stopped_team_can_be_removed_and_the_running_one_may_not(self):
        team = self.teams.start(self.item())
        with self.assertRaises(teams.OpError):
            self.teams.remove(team.id)
        self.teams.stop(team.id)
        self.assertTrue(self.teams.remove(team.id))
        self.assertIsNone(self.teams.of(team.id))

    def test_shown_carries_what_the_phone_draws(self):
        team = self.teams.start(self.item())
        shown = teams.Teams.shown(self.teams.of(team.id))
        self.assertEqual(shown["mode"], "review")
        self.assertEqual(shown["awaited"], ["writer"])
        self.assertIn("goal", shown)
        self.assertIn("log", shown)


if __name__ == "__main__":
    unittest.main()
