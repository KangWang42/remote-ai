import tempfile
import threading
import time
import unittest

import relay
import server


def session_id(number):
    return "%08x-1111-2222-3333-%012x" % (number, number)


class OrganizeRelayTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = self.temp.name + "/terminals.json"
        self.relay = relay.Relay(self.path)
        self.info = {
            "instance": "b" * 32,
            "enabled": True,
            "tools": ["claude", "codex", "agy"],
            "workspaces": ["demo"],
            "features": ["organize"],
        }
        self.relay.agent({"info": self.info}, now=1000)

    def answer(self, request, result):
        received = {}

        def ask():
            try:
                received["value"] = self.relay.organize(request, now=1001, wait=3)
            except Exception as error:
                received["error"] = error

        worker = threading.Thread(target=ask)
        worker.start()
        for _ in range(30):
            operations = self.relay.pull({"instance": self.info["instance"], "wait": 0})["operations"]
            if operations:
                break
            time.sleep(0.01)
        else:
            self.fail("organization request was not pulled by the computer")
        operation = operations[-1]
        self.assertEqual(operation["action"], "session_organize")
        self.relay.agent({"info": self.info, "acks": [{"id": request["id"], "result": result}]}, now=1002)
        worker.join(1)
        self.assertFalse(worker.is_alive())
        self.assertNotIn("error", received)
        return operation, received["value"]

    def test_missing_capability_is_refused(self):
        self.relay.agent({"info": dict(self.info, features=[])}, now=1001)
        with self.assertRaises(relay.RemoteError):
            self.relay.organize({"id": "a" * 32, "verb": "status"}, now=1002)

    def test_organized_metadata_is_separate_from_old_title(self):
        sid = session_id(1)
        self.relay.agent({
            "info": self.info,
            "sessions": [{
                "id": sid, "tool": "codex", "dir": "demo", "title": "电脑原名", "updated": 100,
                "live": False, "organize": {
                    "title": "整理后的名称", "original_title": "电脑原名", "manual_title": "",
                    "summary": "修复登录", "tags": ["登录"], "follow_up": True,
                    "state": "done", "error": "", "model": "gpt-6-luna",
                },
            }],
        }, now=1001)
        listed = self.relay.overview(now=1001)["sessions"][0]
        self.assertEqual(listed["title"], "电脑原名")
        self.assertEqual(listed["organize"]["title"], "整理后的名称")
        self.assertEqual(listed["organize"]["tags"], ["登录"])

    def test_list_is_paged_beyond_the_legacy_120_session_overview(self):
        operation, result = self.answer(
            {"id": "a" * 32, "verb": "list", "offset": 120, "limit": 5},
            {"items": [{"id": session_id(121), "tool": "codex", "state": "pending"}], "total": 125, "more": False},
        )
        self.assertEqual(
            (operation["verb"], operation["offset"], operation["limit"]),
            ("list", 120, 5),
        )
        self.assertEqual((result["total"], result["more"], result["items"][0]["id"]), (125, False, session_id(121)))

    def test_batch_request_round_trips_to_computer(self):
        operation, result = self.answer(
            {"id": "b" * 32, "verb": "start", "scope": "all"},
            {"queued": 123, "skipped": 2},
        )
        self.assertEqual((operation["verb"], operation["scope"]), ("start", "all"))
        self.assertEqual(result, {"queued": 123, "skipped": 2})

    def test_two_computer_spaces_do_not_share_organization_operations(self):
        first = server.Space(self.temp.name + "/one")
        second = server.Space(self.temp.name + "/two")
        first_info = dict(self.info, instance="1" * 32)
        second_info = dict(self.info, instance="2" * 32)
        first.relay.agent({"info": first_info}, now=1000)
        second.relay.agent({"info": second_info}, now=1000)
        received = {}

        def ask():
            received["value"] = first.relay.organize({"id": "f" * 32, "verb": "start", "scope": "all"}, now=1001, wait=2)

        worker = threading.Thread(target=ask)
        worker.start()
        for _ in range(30):
            if first.relay.pull({"instance": first_info["instance"], "wait": 0})["operations"]:
                break
            time.sleep(0.01)
        self.assertEqual(second.relay.pull({"instance": second_info["instance"], "wait": 0})["operations"], [])
        first.relay.agent({"info": first_info, "acks": [{"id": "f" * 32, "result": {"queued": 1, "skipped": 0}}]}, now=1002)
        worker.join(1)
        self.assertEqual(received["value"], {"queued": 1, "skipped": 0})

    def test_identity_and_settings_are_forwarded(self):
        sid = session_id(9)
        operation, _ = self.answer(
            {"id": "c" * 32, "verb": "settings", "model": "gpt-6-luna", "auto": True},
            {"auto": True, "model": "gpt-6-luna"},
        )
        self.assertEqual(
            {key: operation[key] for key in ("verb", "model", "auto")},
            {"verb": "settings", "model": "gpt-6-luna", "auto": True},
        )
        operation, _ = self.answer(
            {"id": "d" * 32, "verb": "edit", "tool": "codex", "session": sid, "title": "新的名字"},
            {"item": {"id": sid, "tool": "codex", "title": "新的名字", "state": "done"}},
        )
        self.assertEqual(
            {key: operation[key] for key in ("verb", "tool", "session", "title")},
            {"verb": "edit", "tool": "codex", "session": sid, "title": "新的名字"},
        )

    def test_follow_up_edits_forward_only_supplied_fields(self):
        sid = session_id(10)
        self.relay.agent({"info": self.info, "sessions": [
            {"id": sid, "tool": "codex", "dir": "demo", "title": "原名", "updated": 100},
        ]}, now=1001)
        for index, fields in enumerate(({"follow_up": True}, {"title": "已完成", "follow_up": False})):
            with self.subTest(fields=fields):
                item = {"id": sid, "tool": "codex", **fields}
                operation, result = self.answer(
                    {"id": str(index + 1) * 32, "verb": "edit", "tool": "codex", "session": sid, **fields},
                    {"item": item},
                )
                self.assertEqual({key: operation[key] for key in ("title", "follow_up") if key in operation}, fields)
                self.assertEqual(result, {"item": item})
                listed = self.relay.overview(now=1002)["sessions"][0]
                self.assertEqual(listed["title"], "原名")
                self.assertIs(listed["organize"]["follow_up"], fields["follow_up"])

    def test_edit_requires_at_least_one_valid_field(self):
        request = {"id": "a" * 32, "verb": "edit", "tool": "codex", "session": session_id(11)}
        for fields in ({}, {"follow_up": None}, {"follow_up": 0}, {"follow_up": "false"}, {"title": "", "follow_up": True}):
            with self.subTest(fields=fields), self.assertRaises(relay.RemoteError):
                self.relay.organize(dict(request, **fields), now=1001)

    def test_invalid_pages_scopes_and_identity_are_refused(self):
        base = {"id": "e" * 32, "verb": "list"}
        for change in (
            {"offset": -1},
            {"limit": 0},
            {"limit": 121},
            {"tool": "codex"},
            {"scope": "all"},
        ):
            with self.assertRaises(relay.RemoteError):
                self.relay.organize(dict(base, **change), now=1001)
        with self.assertRaises(relay.RemoteError):
            self.relay.organize({"id": "f" * 32, "verb": "start", "scope": "all", "tool": "codex", "session": "bad"}, now=1001)

    def test_source_settings_and_count_are_passed_on(self):
        request = {"id": "1" * 32, "verb": "settings", "source": "api", "api_url": " https://api.example.test/v1 ",
                   "api_key": "sk-test-123", "model": "small-model", "auto": False}
        operation, result = self.answer(request, {"source": "api", "api_key_set": True})
        self.assertEqual({key: operation[key] for key in ("source", "api_url", "api_key", "model", "auto")},
                         {"source": "api", "api_url": "https://api.example.test/v1", "api_key": "sk-test-123",
                          "model": "small-model", "auto": False})
        self.assertEqual(result, {"source": "api", "api_key_set": True})
        operation, _ = self.answer({"id": "2" * 32, "verb": "settings", "source": "tool", "source_tool": "claude"}, {})
        self.assertEqual((operation["source"], operation["source_tool"]), ("tool", "claude"))
        operation, result = self.answer({"id": "3" * 32, "verb": "count", "scope": "all"}, {"needed": 2, "skipped": 5, "total": 7})
        self.assertEqual((operation["verb"], operation["scope"]), ("count", "all"))
        self.assertEqual(result["needed"], 2)

    def test_invalid_source_settings_are_refused(self):
        base = {"id": "a" * 32, "verb": "settings"}
        for change in (
            {"source": "cloud"},
            {"source_tool": "vim"},
            {"api_url": "ftp://example.test"},
            {"api_url": "https://example.test/v1?key=1"},
            {"api_url": 7},
            {"api_key": "two words"},
            {"api_key": "x" * 401},
        ):
            with self.subTest(change=change), self.assertRaises(relay.RemoteError):
                self.relay.organize(dict(base, **change), now=1001)
        for change in ({}, {"scope": "one"}, {"scope": "all", "tool": "codex", "session": session_id(3)}):
            with self.subTest(change=change), self.assertRaises(relay.RemoteError):
                self.relay.organize(dict({"id": "b" * 32, "verb": "count"}, **change), now=1001)


if __name__ == "__main__":
    unittest.main()
