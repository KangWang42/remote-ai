import threading
import time
import unittest
import tempfile

import relay as tr
from relay import RemoteError


class RevAndGitRelayTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.relay = tr.Relay(self.temp.name + "/terminal.json")
        self.info = {
            "instance": "b" * 32,
            "environment_id": "e" * 32,
            "enabled": True,
            "tools": ["claude", "codex", "shell"],
            "workspaces": ["demo"],
            "features": ["git", "worktree", "chat"],
        }
        self.relay.agent({"info": self.info}, now=1000)

    def test_overview_rev_returns_unchanged_and_wakes_on_change(self):
        first = self.relay.overview(now=1001)
        rev = first["rev"]
        self.assertGreater(rev, 0)
        same = self.relay.overview(now=1001, wait=0, rev=rev)
        self.assertEqual(same, {"unchanged": True, "rev": rev, "online": True})

        got = {}
        def waiter():
            got["res"] = self.relay.overview(wait=5, rev=rev)

        t = threading.Thread(target=waiter)
        t.start()
        time.sleep(0.05)
        self.relay.agent({"info": dict(self.info, enabled=False)}, now=time.time())
        t.join(timeout=3)
        self.assertIn("device", got["res"])
        self.assertGreater(got["res"]["rev"], rev)

    def test_git_api_validation_and_round_trip(self):
        with self.assertRaises(RemoteError):
            self.relay.git({"id": "a" * 32, "action": "git_file_diff", "dir": "demo", "path": "../secret.txt"}, now=1001)
        with self.assertRaises(RemoteError):
            self.relay.git({"id": "a" * 32, "action": "git_commit", "dir": "demo", "message": "   "}, now=1001)
        with self.assertRaises(RemoteError):
            self.relay.git({"id": "a" * 32, "action": "git_status", "dir": "other"}, now=1001)

        out = {}
        def ask():
            out["res"] = self.relay.git({"id": "c" * 32, "action": "git_file_diff", "dir": "demo", "path": "src/app.ts", "scope": "working"}, now=1002, wait=5)
        t = threading.Thread(target=ask)
        t.start()
        pulled = self.relay.pull({"instance": self.info["instance"], "wait": 5})["operations"]
        self.assertEqual((pulled[0]["action"], pulled[0]["path"], pulled[0]["scope"]), ("git_file_diff", "src/app.ts", "working"))
        self.relay.agent({"info": self.info, "acks": [{"id": "c" * 32, "result": {"path": "src/app.ts", "diff": "+hello"}}]}, now=1003)
        t.join(timeout=3)
        self.assertEqual(out["res"]["diff"], "+hello")

    def test_start_with_worktree_forwards_options_and_branch_metadata(self):
        res = self.relay.command({
            "id": "d" * 32, "action": "start", "tool": "codex", "dir": "demo",
            "history": False, "worktree": True, "branch": "feat/fast-ui", "base_branch": "main",
        }, now=1004)
        op = self.relay.pull({"instance": self.info["instance"], "wait": 0})["operations"][0]
        self.assertEqual((op.get("worktree"), op.get("branch"), op.get("base_branch")), (True, "feat/fast-ui", "main"))
        self.relay.agent({"info": self.info, "acks": [{"id": "d" * 32}], "terminals": [{
            "id": res["terminal"], "state": "running", "branch": "feat/fast-ui", "worktree": "wt-1234",
        }]}, now=1005)
        term = self.relay.overview(now=1005)["terminals"][0]
        self.assertEqual((term["branch"], term["worktree"]), ("feat/fast-ui", "wt-1234"))


if __name__ == "__main__":
    unittest.main()
