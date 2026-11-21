"""The quota parsers and store (agent-linux/quotas.py): what each tool's own answer makes of
itself, in the exact shapes the tools emit, and what survives a restart and an expired window."""
import os
import sys
import time
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "agent-linux"))
import quotas  # noqa: E402


class ClaudeEventTest(unittest.TestCase):
    def test_unified_windows_become_percentages(self):
        windows = quotas.from_claude_event({"unifiedWindows": {
            "five_hour": {"utilization": 0.42, "resetsAt": 1790000000000},
            "seven_day": {"utilization": 0.9, "resetsAt": 1790000000}}})
        self.assertEqual([(w["name"], round(w["used"]), w["resets"]) for w in windows],
                         [("five_hour", 42, 1790000000), ("seven_day", 90, 1790000000)])

    def test_a_window_without_utilization_is_left_out(self):
        windows = quotas.from_claude_event({"unifiedWindows": {
            "five_hour": {"resetsAt": 1790000000}, "seven_day": {"utilization": 0.5, "resetsAt": 0}}})
        self.assertEqual([w["name"] for w in windows], ["seven_day"])

    def test_the_status_line_shape_also_parses(self):
        windows = quotas.from_claude_status({"rate_limits": {
            "five_hour": {"used_percentage": 37.5, "resets_at": 1790000000}}})
        self.assertEqual(round(windows[0]["used"], 1), 37.5)


class CodexTest(unittest.TestCase):
    def test_primary_and_secondary_are_named_by_their_length(self):
        windows = quotas.from_codex({"primary": {"usedPercent": 20, "windowDurationMins": 300, "resetsAt": 1790000000},
                                     "secondary": {"usedPercent": 65, "windowDurationMins": 10080, "resetsAt": 0}})
        self.assertEqual([(w["name"], w["used"]) for w in windows],
                         [("five_hour", 20), ("seven_day", 65)])

    def test_an_odd_length_keeps_its_minutes_as_the_name(self):
        windows = quotas.from_codex({"primary": {"usedPercent": 5, "windowDurationMins": 60, "resetsAt": 0}})
        self.assertEqual(windows[0]["name"], "60m")


class AgyTest(unittest.TestCase):
    def test_the_gemini_pool_is_primary_and_the_others_carry_a_group(self):
        given = {"command": {"data": {"groups": [
            {"name": "Claude and GPT models", "buckets": [{"id": "claude-5h", "window": "5h", "remaining_fraction": 0.5}]},
            {"name": "Gemini Models", "description": "includes neon quota", "buckets": [
                {"id": "gemini-5h", "window": "5h", "remaining_fraction": 0.8, "reset_time": 1790000000},
                {"id": "gemini-weekly", "window": "weekly", "remaining_fraction": 0.1}]},
            {"name": "Specialist models", "buckets": [
                {"id": "image-1", "window": "daily", "remaining_fraction": 0.3}]}]}}}
        windows = quotas.from_agy(given)
        self.assertEqual([w["name"] for w in windows][:2], ["five_hour", "seven_day"])
        self.assertEqual(windows[0]["group"], "")                       # the primary pool has none
        self.assertEqual(round(windows[1]["used"]), 90)                 # 1 - remaining
        others = [w for w in windows if w["group"]]
        self.assertEqual([w["group"] for w in others], ["Claude / GPT", "Specialist models"])

    def test_buckets_without_a_fraction_are_dropped(self):
        windows = quotas.from_agy({"groups": [{"name": "Gemini Models", "buckets": [
            {"id": "broken"}, {"id": "gemini-5h", "window": "5h", "remaining_fraction": 0.9}]}]})
        # the primary pool's windows carry the names the rest of the system knows
        self.assertEqual([w["name"] for w in windows], ["five_hour"])


class StoreTest(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.quotas = quotas.Quotas(self.folder.name)

    def tearDown(self):
        self.folder.cleanup()

    def test_what_was_noted_is_reported_and_read_back_for_the_teams(self):
        self.quotas.note("claude", [{"name": "five_hour", "used": 42.4, "resets": 0, "group": ""}])
        report = self.quotas.report()
        self.assertEqual(report[0]["tool"], "claude")
        self.assertEqual(report[0]["windows"][0]["used"], 42.4)
        windows = self.quotas.of("claude")
        self.assertEqual(windows[0]["name"], "five_hour")

    def test_an_expired_window_is_left_out_of_what_is_reported_and_weighed(self):
        self.quotas.note("codex", [{"name": "five_hour", "used": 20, "resets": int(time.time()) - 100, "group": ""}])
        # the entry is still told (when it was last known), with the expired window gone
        report = self.quotas.report()
        self.assertEqual(report[0]["windows"], [])
        self.assertEqual(self.quotas.of("codex"), [])

    def test_unknown_is_why_nothing_is_known(self):
        self.quotas.unknown("agy", "CLI 未安装")
        report = self.quotas.report()
        self.assertEqual(report[0]["why"], "CLI 未安装")
        self.assertEqual(report[0]["windows"], [])

    def test_secondary_pools_stay_out_of_what_the_teams_weigh(self):
        self.quotas.note("agy", [{"name": "five_hour", "used": 10, "resets": 0, "group": ""},
                                 {"name": "image", "used": 90, "resets": 0, "group": "Specialist models"}])
        self.assertEqual([one["name"] for one in self.quotas.of("agy")], ["five_hour"])

    def test_the_store_file_is_owner_only(self):
        self.quotas.note("claude", [{"name": "five_hour", "used": 1, "resets": 0, "group": ""}])
        mode = os.stat(self.quotas._file_of("claude")).st_mode & 0o777
        self.assertEqual(mode, 0o600)


if __name__ == "__main__":
    unittest.main()
