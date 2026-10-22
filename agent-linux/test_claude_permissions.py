import copy
import json
from pathlib import Path
import unittest

import claude_permissions


class ClaudePermissions(unittest.TestCase):
    def suggestion(self, rules, destination="localSettings"):
        return {"type": "addRules", "behavior": "allow", "destination": destination, "rules": rules}

    def test_shared_cases(self):
        cases = json.loads((Path(__file__).resolve().parents[1] / "tests/claude_permissions_cases.json").read_text(encoding="utf-8"))
        for case in cases:
            with self.subTest(case=case["name"]):
                rule = {"toolName": case["tool"]}
                if "content" in case:
                    rule["ruleContent"] = case["content"]
                suggestion = self.suggestion([rule])
                before = copy.deepcopy(suggestion)
                self.assertEqual(claude_permissions.select([suggestion]) is suggestion, case["valid"])
                self.assertEqual(suggestion, before)

    def test_scope_and_selection(self):
        safe = self.suggestion([{"toolName": "Bash", "ruleContent": "npm test"}])
        unsafe = self.suggestion([{"toolName": "Bash", "ruleContent": "mv *.csv backup/"}])
        self.assertIs(claude_permissions.select([unsafe, safe]), safe)
        self.assertIsNone(claude_permissions.select(self.suggestion(safe["rules"] + unsafe["rules"])))
        self.assertIsNone(claude_permissions.select([self.suggestion(safe["rules"] + unsafe["rules"])]))
        for destination in ("userSettings", "projectSettings", "unknown"):
            self.assertIsNone(claude_permissions.select([self.suggestion(safe["rules"], destination)], allow_session=True))
        session = self.suggestion(safe["rules"], "session")
        self.assertIsNone(claude_permissions.select([session]))
        self.assertIs(claude_permissions.select([session], allow_session=True), session)
        self.assertTrue(claude_permissions.detail(session).startswith("仅本次会话"))
        for key, value in (("type", "replaceRules"), ("behavior", "deny"), ("rules", []), ("rules", "bad")):
            changed = dict(safe, **{key: value})
            self.assertIsNone(claude_permissions.select([changed]))
        self.assertIsNone(claude_permissions.select("bad"))
        self.assertIsNone(claude_permissions.select([None, "bad"]))
        self.assertIsNone(claude_permissions.select([self.suggestion(safe["rules"] * 5)]))
        self.assertIsNone(claude_permissions.select([self.suggestion([{"toolName": "Bash", "ruleContent": "x" * 700}])]))


if __name__ == "__main__":
    unittest.main()
