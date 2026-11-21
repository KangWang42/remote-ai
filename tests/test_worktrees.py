"""The isolated worktrees of a compare team (agent-linux/worktrees.py), against a real Git
repository in a temporary folder: create per-tool detached worktrees, read a candidate's
diff and stat, apply the winner binary-safely, clean up — and refuse when the folder is
not a repository."""
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "agent-linux"))
import worktrees  # noqa: E402


def git(folder, *args):
    done = subprocess.run(["git", "-C", folder] + list(args), capture_output=True)
    assert done.returncode == 0, done.stderr.decode("utf-8", "replace")
    return done.stdout.decode("utf-8", "replace")


class WorktreesTest(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.root = os.path.join(self.folder.name, "wt-root")
        self.repo = os.path.join(self.folder.name, "repo")
        os.makedirs(self.repo)
        git(self.repo, "init", "-q")
        git(self.repo, "config", "user.email", "test@example.com")
        git(self.repo, "config", "user.name", "test")
        with open(os.path.join(self.repo, "app.txt"), "w", encoding="utf-8") as stream:
            stream.write("one\n")
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-q", "-m", "first")
        self.worktrees = worktrees.Worktrees(self.root)

    def tearDown(self):
        self.folder.cleanup()

    def test_a_candidate_works_in_its_own_detached_copy(self):
        made = self.worktrees.create(self.repo, "c" * 24, "opencode")
        self.assertTrue(os.path.isdir(made["path"]))
        head = git(self.repo, "rev-parse", "HEAD").strip()
        self.assertEqual(made["base_commit"], head)
        self.assertIn("(detached", git(made["path"], "branch", "--show-current") or "(detached")

    def test_a_non_repository_folder_is_refused(self):
        plain = os.path.join(self.folder.name, "plain")
        os.makedirs(plain)
        with self.assertRaises(worktrees.OpError):
            self.worktrees.create(plain, "c" * 24, "opencode")

    def test_diff_and_stat_read_what_the_candidate_changed(self):
        made = self.worktrees.create(self.repo, "c" * 24, "codex")
        with open(os.path.join(made["path"], "app.txt"), "w", encoding="utf-8") as stream:
            stream.write("one\ntwo\n")
        with open(os.path.join(made["path"], "new.txt"), "w", encoding="utf-8") as stream:
            stream.write("fresh\n")
        diff = self.worktrees.diff(made["path"], made["base_commit"])
        self.assertIn("文件：", diff)
        self.assertIn("+++ 新建文件：new.txt", diff)
        stat = self.worktrees.stat(made["path"], made["base_commit"])
        self.assertIn("2 个文件改动", stat)

    def test_the_winner_is_applied_binary_safe_and_the_rest_cleaned(self):
        winner = self.worktrees.create(self.repo, "c" * 24, "codex")
        loser = self.worktrees.create(self.repo, "c" * 24, "claude")
        with open(os.path.join(winner["path"], "app.txt"), "wb") as stream:
            stream.write(b"one\ntwo\n\x00binary")
        with open(os.path.join(winner["path"], "new.txt"), "wb") as stream:
            stream.write(b"fresh\n")
        note = self.worktrees.apply(self.repo, winner["path"], winner["base_commit"])
        self.assertIn("2 个文件改动", note)
        with open(os.path.join(self.repo, "app.txt"), "rb") as stream:
            self.assertEqual(stream.read(), b"one\ntwo\n\x00binary")
        self.worktrees.remove_all(self.repo, "c" * 24)
        self.assertFalse(os.path.isdir(winner["path"]))
        self.assertFalse(os.path.isdir(loser["path"]))
        with open(os.path.join(self.repo, "app.txt"), "rb") as stream:
            self.assertEqual(stream.read(), b"one\ntwo\n\x00binary")      # the pick kept its changes

    def test_a_conflicting_main_folder_is_left_untouched(self):
        winner = self.worktrees.create(self.repo, "c" * 24, "codex")
        with open(os.path.join(winner["path"], "app.txt"), "w", encoding="utf-8") as stream:
            stream.write("candidate\n")
        with open(os.path.join(self.repo, "app.txt"), "w", encoding="utf-8") as stream:
            stream.write("owner changed it first\n")                      # the owner edited the same file
        with self.assertRaises(worktrees.OpError):
            self.worktrees.apply(self.repo, winner["path"], winner["base_commit"])
        with open(os.path.join(self.repo, "app.txt"), "r", encoding="utf-8") as stream:
            self.assertEqual(stream.read(), "owner changed it first\n")   # untouched

    def test_changes_in_says_nothing_for_a_clean_or_missing_folder(self):
        self.assertEqual(worktrees.changes_in(self.repo), "")
        self.assertEqual(worktrees.changes_in(os.path.join(self.folder.name, "gone")), "")
        with open(os.path.join(self.repo, "app.txt"), "a", encoding="utf-8") as stream:
            stream.write("more\n")
        self.assertIn("文件：", worktrees.changes_in(self.repo))


if __name__ == "__main__":
    unittest.main()
