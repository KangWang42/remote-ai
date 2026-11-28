"""The teams orchestration on this machine (agent-linux/teams.py): the ways of working,
turn handling, multi-line assignments, cards/grouping, cleanup, and persistence."""
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import teams  # noqa: E402


class FakeHands:
    def __init__(self, tools=("claude", "codex", "opencode", "qoder", "cline", "pi")):
        self.opened, self.said, self.stopped, self.closed_terms = [], [], [], []
        self.changes_text = ""
        self.available_tools = list(tools)
        self.count = 0
        self.limits = None
        self.worktrees_created = []
        self.worktrees_cleaned = []
        self.applied = []
        self.fail_open = None

    def begin_in(self, tool, dir_name, words, read, folder_override=None):
        if self.fail_open:
            raise self.fail_open
        self.count += 1
        terminal = "t%d" % self.count
        self.opened.append({"tool": tool, "dir": dir_name, "words": words, "read": read,
                            "folder": folder_override, "terminal": terminal})
        return terminal

    def say(self, terminal, words):
        self.said.append((terminal, words))

    def stop(self, terminal):
        self.stopped.append(terminal)

    def close(self, terminal):
        self.closed_terms.append(terminal)

    def changes(self, dir_name):
        return self.changes_text

    def available(self):
        return list(self.available_tools)

    def make_worktree(self, dir_name, team_id, tool):
        path = "/tmp/wt/%s/%s" % (team_id, tool)
        self.worktrees_created.append((dir_name, team_id, tool, path))
        return {"path": path, "base_commit": "abc123"}

    def worktree_diff(self, path, base_commit):
        return "diff for " + path

    def worktree_stat(self, path, base_commit):
        return "1 个文件改动：" + os.path.basename(path)

    def apply_worktree(self, dir_name, path, base_commit):
        self.applied.append((dir_name, path, base_commit))
        return "1 个文件改动：app.py"

    def clean_worktrees(self, dir_name, team_id):
        self.worktrees_cleaned.append((dir_name, team_id))


class CustomError(Exception):
    pass


class TeamsTest(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.hands = FakeHands()
        self.file = os.path.join(self.folder.name, "teams.json")
        self.teams = teams.Teams(self.file, self.hands, error_types=(teams.OpError, CustomError, ValueError))

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
        self.assertEqual(len(self.hands.opened), 2)
        self.assertEqual(self.hands.said[-1][0], writer)
        self.assertIn("审查意见", self.hands.said[-1][1])
        self.teams.turned(writer, "done", "改好了")
        self.assertEqual(self.hands.said[-1][0], reviewer)
        self.teams.turned(reviewer, "done", "REVIEW: CHANGES\n还有问题")
        self.assertEqual(self.teams.of(team.id).state, "done")
        self.assertIn("最后一轮", self.teams.of(team.id).note)

    def test_a_leader_assigns_multiline_tasks_and_collects_their_results(self):
        team = self.teams.start(self.item(mode="lead", tools=("codex", "opencode", "pi")))
        leader = self.opened_terminal(0)
        self.teams.turned(
            leader, "done",
            "我来分派。\nASSIGN opencode: 写加法\n- 支持整数和浮点\n- 写文档注释\nASSIGN pi: 写测试\n- 覆盖负数和零")
        self.assertEqual(len(self.hands.opened), 3)
        self.assertEqual(self.hands.opened[1]["tool"], "opencode")
        self.assertEqual(self.hands.opened[2]["tool"], "pi")
        self.assertIn("支持整数和浮点", self.hands.opened[1]["words"])
        self.assertIn("覆盖负数和零", self.hands.opened[2]["words"])
        self.teams.turned(self.opened_terminal(1), "done", "加法写好了")
        self.teams.turned(self.opened_terminal(2), "done", "测试写好了")
        self.assertEqual(len(self.hands.said), 1)
        self.assertIn("加法写好了", self.hands.said[0][1])
        self.teams.turned(leader, "done", "DONE: 全部完成")
        # a bare DONE opens the acceptance phase; the leader must then show evidence
        self.assertEqual(self.teams.of(team.id).phase, "verify")
        self.assertEqual(self.teams.of(team.id).state, "running")
        self.teams.turned(leader, "done", "VERIFIED: 跑了测试，全部通过\nDONE: 全部完成")
        self.assertEqual(self.teams.of(team.id).state, "done")

    def test_a_leader_without_assignments_is_reminded_then_the_team_pauses(self):
        team = self.teams.start(self.item(mode="lead", tools=("codex", "opencode")))
        leader = self.opened_terminal(0)
        self.teams.turned(leader, "done", "我想想")
        self.assertEqual(len(self.hands.said), 1)
        self.assertIn("ASSIGN", self.hands.said[0][1])
        self.teams.turned(leader, "done", "还是想想")
        self.assertEqual(self.teams.of(team.id).state, "paused")

    def test_a_failed_member_with_no_backup_pauses_and_resume_clears_failure(self):
        self.hands.available_tools = ["codex", "opencode"]
        team = self.teams.start(self.item(mode="lead", tools=("codex", "opencode")))
        leader = self.opened_terminal(0)
        self.teams.turned(leader, "done", "ASSIGN opencode: 做事")
        member = self.opened_terminal(1)
        self.teams.turned(member, "failed", "hit your limit")
        one = self.teams.of(team.id)
        self.assertEqual(one.state, "paused")
        self.assertEqual(one.failed.get("opencode"), "额度已用尽")
        self.teams.resume(team.id)
        self.assertEqual(self.teams.of(team.id).state, "running")
        self.assertEqual(self.teams.of(team.id).failed, {})

    def test_short_quota_done_reply_triggers_failure_handling(self):
        self.hands.available_tools = ["claude", "opencode"]
        team = self.teams.start(self.item())
        writer = self.opened_terminal(0)
        self.teams.turned(writer, "done", "You have hit your limit for this window.")
        one = self.teams.of(team.id)
        self.assertEqual(one.state, "paused")
        self.assertEqual(one.failed.get("claude"), "额度已用尽")

    def test_compare_keeps_summaries_stats_and_diffs_distinct_and_picks_winner(self):
        team = self.teams.start(self.item(mode="compare", tools=("codex", "opencode"), rounds=1))
        self.assertEqual(len(self.hands.opened), 2)
        self.assertEqual(self.hands.opened[0]["folder"], "/tmp/wt/%s/codex" % team.id)
        self.assertEqual(self.hands.opened[1]["folder"], "/tmp/wt/%s/opencode" % team.id)
        self.teams.turned(self.opened_terminal(0), "done", "Codex 方案说明")
        self.teams.turned(self.opened_terminal(1), "done", "OpenCode 方案说明")
        ready = self.teams.of(team.id)
        self.assertEqual(ready.state, "choose")
        self.assertEqual(ready.summaries["codex"], "Codex 方案说明")
        self.assertEqual(ready.stats["codex"], "1 个文件改动：codex")
        self.assertIn("diff for", ready.diffs["codex"])
        self.assertNotEqual(ready.summaries["codex"], ready.diffs["codex"])
        self.assertNotEqual(ready.diffs["codex"], ready.stats["codex"])
        shown = teams.Teams.shown(ready)
        by_tool = {c["tool"]: c for c in shown["candidates"]}
        self.assertEqual(by_tool["opencode"]["summary"], "OpenCode 方案说明")
        self.assertIn("diff for", by_tool["opencode"]["diff"])
        picked = self.teams.pick(team.id, "opencode")
        self.assertEqual(picked.state, "done")
        self.assertEqual(picked.picked, "opencode")
        self.assertEqual(len(self.hands.applied), 1)
        self.assertEqual(len(self.hands.worktrees_cleaned), 1)

    def test_the_owner_interjects_into_the_awaited_member(self):
        team = self.teams.start(self.item())
        writer = self.opened_terminal(0)
        self.teams.say(team.id, "记得写测试")
        self.assertEqual(self.hands.said[-1][0], writer)
        self.assertIn("【用户插话】", self.hands.said[-1][1])

    def test_swap_replaces_a_paused_writer_and_reopens_it(self):
        self.hands.available_tools = ["claude", "opencode"]
        team = self.teams.start(self.item())
        writer = self.opened_terminal(0)
        self.teams.turned(writer, "failed", "额度已用尽")
        self.assertEqual(self.teams.of(team.id).state, "paused")
        self.teams.swap(team.id, "writer", "opencode", "换个试试")
        self.assertEqual(self.teams.of(team.id).tools[0], "opencode")
        self.assertIn(writer, self.hands.stopped)
        self.assertEqual(self.hands.opened[-1]["tool"], "opencode")
        self.assertIn("接手", self.hands.opened[-1]["words"])

    def test_closed_awaited_member_pauses_cleanly(self):
        team = self.teams.start(self.item())
        writer = self.opened_terminal(0)
        self.teams.closed(writer)
        one = self.teams.of(team.id)
        self.assertEqual(one.state, "paused")
        self.assertIn("中断", one.note)

    def test_cards_learn_and_remove_closes_opened_terminals(self):
        team = self.teams.start(self.item())
        writer = self.opened_terminal(0)
        self.teams.learn(writer, "claude", "11111111-2222-3333-4444-555555555555")
        self.assertEqual(self.teams.team_of(writer), team.id)
        self.assertEqual(self.teams.team_of_session("claude", "11111111-2222-3333-4444-555555555555"), team.id)
        shown = teams.Teams.shown(self.teams.of(team.id))
        self.assertEqual(shown["member_sessions"]["writer"],
                         {"id": "11111111-2222-3333-4444-555555555555", "tool": "claude"})
        cards = self.teams.cards(asking=lambda term: term == writer)
        self.assertEqual(len(cards), 1)
        self.assertTrue(cards[0]["confirm"])
        self.assertEqual(cards[0]["tools"], ["claude", "opencode"])
        with self.assertRaises(teams.OpError):
            self.teams.remove(team.id)
        self.teams.stop(team.id)
        self.assertTrue(self.teams.remove(team.id))
        self.assertIn(writer, self.hands.closed_terms)
        self.assertIsNone(self.teams.of(team.id))

    def test_teams_survive_a_restart_and_a_running_one_pauses(self):
        team = self.teams.start(self.item())
        self.teams.learn(self.opened_terminal(0), "claude", "11111111-2222-3333-4444-555555555555")
        again = teams.Teams(self.file, self.hands)
        loaded = again.of(team.id)
        self.assertIsNotNone(loaded)
        self.assertEqual(loaded.state, "paused")
        self.assertIn("重新启动", loaded.note)
        self.assertEqual(loaded.sessions["writer"]["tool"], "claude")

    def test_custom_error_type_message_is_preserved_when_open_fails(self):
        self.hands.fail_open = CustomError("最多同时运行 16 个终端")
        team = self.teams.start(self.item())
        self.assertEqual(team.state, "failed")
        self.assertIn("最多同时运行 16 个终端", team.note)


class RosterAndVerifyTest(unittest.TestCase):
    """The Windows roster/acceptance additions now working on this machine."""

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.hands = FakeHands()
        self.file = os.path.join(self.folder.name, "teams.json")
        self.teams = teams.Teams(self.file, self.hands)
        self.item = {"id": "b" * 24, "mode": "lead", "dir": "demo", "goal": "写加法",
                     "tools": ["codex", "opencode"], "rounds": 3}

    def tearDown(self):
        self.folder.cleanup()

    def test_roster_rows_are_checked_and_kept(self):
        given = dict(self.item, roster=[
            {"role": "leader", "tool": "codex", "model": "gpt-5.1-codex", "effort": "high"},
            {"role": "member-1", "tool": "opencode", "model": "", "effort": "medium"}])
        team = self.teams.start(given)
        self.assertEqual(team.tools, ["codex", "opencode"])
        self.assertEqual(team.models["codex"], "gpt-5.1-codex")
        self.assertEqual(team.efforts["opencode"], "medium")
        self.assertEqual(teams.Team.from_json(team.to_json()).models, team.models)
        self.assertEqual(teams.Team.from_json(team.to_json()).efforts, team.efforts)

    def test_an_invalid_roster_is_refused(self):
        bad = [
            [{"role": "member-1", "tool": "opencode", "model": "", "effort": ""}],
            [{"role": "leader", "tool": "codex", "model": "", "effort": ""},
             {"role": "leader", "tool": "opencode", "model": "", "effort": ""}],
            [{"role": "leader", "tool": "codex", "model": "", "effort": ""},
             {"role": "member-1", "tool": "", "model": "", "effort": ""}],
            [{"role": "leader", "tool": "codex", "model": "bad name!", "effort": ""},
             {"role": "member-1", "tool": "opencode", "model": "", "effort": ""}],
            [{"role": "leader", "tool": "codex", "model": "", "effort": "very high"},
             {"role": "member-1", "tool": "opencode", "model": "", "effort": ""}],
        ]
        for rows in bad:
            with self.subTest(rows=rows), self.assertRaises(teams.OpError):
                self.teams.start(dict(self.item, roster=rows))

    def test_acceptance_needs_evidence_and_survives_a_restart(self):
        team = self.teams.start(self.item)
        leader = self.hands.opened[0]["terminal"]
        self.teams.turned(leader, "done", "DONE: 写好了")
        self.assertEqual(self.teams.of(team.id).phase, "verify")
        self.teams.turned(leader, "done", "DONE: 还是写好了")
        self.assertTrue(self.teams.of(team.id).reminded)
        self.assertEqual(self.teams.of(team.id).state, "running")
        self.teams.turned(leader, "done", "VERIFIED: 跑了 unittest\nDONE: 完成")
        self.assertEqual(self.teams.of(team.id).state, "done")
        again = teams.Teams(self.file, FakeHands())
        self.assertEqual(again.of(team.id).state, "done")

    def test_model_and_effort_accept_only_safe_names(self):
        self.assertTrue(teams.model_ok(""))
        self.assertTrue(teams.model_ok("models/gemini-3-pro"))
        self.assertTrue(teams.model_ok("sonnet[1m]"))
        self.assertFalse(teams.model_ok("gpt 5; rm -rf /"))
        self.assertTrue(teams.effort_ok(""))
        self.assertTrue(teams.effort_ok("xhigh"))
        self.assertFalse(teams.effort_ok("VERY HIGH"))

    def test_auto_roster_accepts_a_leader_only_phone_roster(self):
        # The phone sends one leader row plus auto_roster when it asks the
        # computer to pick the members; this is not the manual two-role case.
        given = dict(self.item, roster=[{"role": "leader", "tool": "codex", "model": "", "effort": ""}],
                     auto_roster=True)
        team = self.teams.start(given)
        self.assertEqual(team.tools, ["codex"])
        self.assertEqual(team.state, "running")

    def test_a_roster_tool_must_be_one_this_computer_has(self):
        rows = [{"role": "leader", "tool": "codex", "model": "", "effort": ""},
                {"role": "member-1", "tool": "not-a-tool", "model": "", "effort": ""}]
        with self.assertRaises(teams.OpError):
            self.teams.start(dict(self.item, roster=rows))

    def test_a_non_list_roster_is_refused_not_ignored(self):
        with self.assertRaises(teams.OpError):
            self.teams.start(dict(self.item, roster={"role": "leader", "tool": "codex"}))

    def test_assignment_in_the_acceptance_turn_is_not_lost(self):
        team = self.teams.start(self.item)
        leader = self.hands.opened[0]["terminal"]
        self.teams.turned(leader, "done", "DONE: 写好了")
        self.assertEqual(self.teams.of(team.id).phase, "verify")
        self.teams.turned(leader, "done",
                          "VERIFIED: 跑了 unittest\nDONE: 还有问题\nASSIGN opencode: 修掉失败的用例")
        state = self.teams.of(team.id)
        self.assertNotEqual(state.state, "done")   # a fix was asked for; it must run first
        self.assertEqual(state.awaited, ["opencode"])

    def test_metadata_reaches_the_phone_and_survives_a_restart(self):
        given = dict(self.item, roster=[
            {"role": "leader", "tool": "codex", "model": "gpt-5.1-codex", "effort": "high"},
            {"role": "member-1", "tool": "opencode", "model": "", "effort": "medium"}],
            context="只用标准库")
        team = self.teams.start(given)
        shown = teams.Teams.shown(team)
        for key in ("roster", "auto_roster", "models", "efforts", "context"):
            self.assertIn(key, shown)
        cards = {card["id"]: card for card in self.teams.cards()}
        self.assertEqual(cards[team.id]["models"], team.models)
        again = teams.Teams(self.file, FakeHands()).of(team.id)
        self.assertEqual(again.context, "只用标准库")


if __name__ == "__main__":
    unittest.main()
