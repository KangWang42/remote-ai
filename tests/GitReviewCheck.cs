using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using RemoteCli;

class GitReviewCheck {
    static void Require(bool ok, string msg) { if (!ok) throw new Exception("FAIL: " + msg); }
    static string Git(string dir, string args) {
        var p = Process.Start(new ProcessStartInfo("git", "-C \"" + dir + "\" " + args) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        });
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        if (p.ExitCode != 0) throw new Exception("git " + args + " failed: " + e);
        return o.Trim();
    }

    static int Main(string[] args) {
        string root = args[0];
        string repo = Path.Combine(root, "repo");
        string wtRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repo);
        Git(repo, "init");
        Git(repo, "config user.email test@example.com");
        Git(repo, "config user.name Test");
        File.WriteAllText(Path.Combine(repo, "app.txt"), "line1\nline2\n", new UTF8Encoding(false));
        Git(repo, "add -A");
        Git(repo, "commit -m \"initial\"");

        var wts = new Worktrees(wtRoot);

        // 1. Two-stage status (modified + untracked) without raw diff bodies
        File.WriteAllText(Path.Combine(repo, "app.txt"), "line1\nline2-edited\nline3\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(repo, "new.txt"), "hello\nworld\n", new UTF8Encoding(false));
        var st = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "git_status" } }, wts);
        Require(true.Equals(st["is_repo"]), "repo detected");
        var files = ((Dictionary<string, object>[])st["files"]).ToDictionary(f => Convert.ToString(f["path"]));
        Require(files.ContainsKey("app.txt") && files.ContainsKey("new.txt"), "both modified and untracked listed");
        Require(!files["app.txt"].ContainsKey("diff"), "status stays lightweight without diff body");

        // 2. On-demand per-file diff (tracked + synthetic unified diff for untracked)
        var d1 = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "git_file_diff" }, { "path", "app.txt" }, { "scope", "working" } }, wts);
        Require(Convert.ToString(d1["diff"]).Contains("+line2-edited") && Convert.ToInt32(d1["additions"]) == 2, "tracked diff parsed");
        var d2 = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "git_file_diff" }, { "path", "new.txt" }, { "scope", "working" } }, wts);
        Require(Convert.ToString(d2["diff"]).Contains("+++ b/new.txt") && Convert.ToString(d2["diff"]).Contains("+hello"), "untracked synthetic unified diff");

        // 3. Hidden-ref checkpoint does not pollute user branch HEAD
        string headBefore = Git(repo, "rev-parse HEAD");
        var cp = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "git_checkpoint" }, { "label", "turn-1" } }, wts);
        string cpRef = Convert.ToString(cp["ref"]);
        Require(cpRef.StartsWith("refs/remote-ai/checkpoints/"), "hidden checkpoint ref created");
        Require(Git(repo, "rev-parse HEAD") == headBefore, "checkpoint does not advance user branch");
        Require(Git(repo, "show " + cpRef + ":new.txt").Contains("hello"), "checkpoint captured untracked file");

        // 4. Commit changes
        var committed = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "git_commit" }, { "message", "feat: update app" } }, wts);
        Require(Convert.ToString(committed["commit"]).Length >= 7, "commit hash returned");
        Require(Git(repo, "status --porcelain").Length == 0, "working tree clean after commit");

        // 5. Session branch worktree creation, editing, safe merge back and junction-safe removal
        var created = wts.CreateSessionWorktree(repo, "sess1234", "", "feat/isolated");
        Require(Directory.Exists(created.Path) && created.Branch == "feat/isolated", "isolated branch worktree created");
        File.WriteAllText(Path.Combine(created.Path, "isolated.txt"), "from-worktree\n", new UTF8Encoding(false));
        var wtStatus = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "git_status" }, { "worktree", created.Id } }, wts);
        Require(Convert.ToString(wtStatus["branch"]) == "feat/isolated", "worktree status reports its branch");
        var applied = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "git_worktree_apply" }, { "worktree", created.Id }, { "remove_after", true } }, wts);
        Require(true.Equals(applied["ok"]) && true.Equals(applied["removed"]), "worktree applied and removed");
        Require(File.Exists(Path.Combine(repo, "isolated.txt")), "main repo received worktree changes");
        Require(!Directory.Exists(created.Path), "worktree folder cleaned");

        // 6. Branches, checkout, checkpoint listing & restore, and project scripts detection
        File.WriteAllText(Path.Combine(repo, "package.json"), "{\"scripts\":{\"dev\":\"vite\",\"test\":\"vitest\"}}\n", new UTF8Encoding(false));
        var scriptsRes = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "project_scripts" } }, wts);
        var scriptsArr = (Dictionary<string, object>[])scriptsRes["scripts"];
        Require(scriptsArr.Any(s => Convert.ToString(s["name"]) == "dev" && Convert.ToString(s["command"]) == "npm run dev"), "package.json scripts discovered");

        var checkoutRes = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "git_checkout" }, { "branch", "feat/ui-polish" }, { "create", true } }, wts);
        Require(Convert.ToString(checkoutRes["branch"]) == "feat/ui-polish", "new branch created and checked out");
        var branchesRes = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "git_branches" } }, wts);
        Require(Convert.ToString(branchesRes["current"]) == "feat/ui-polish", "branches lists active branch");

        File.WriteAllText(Path.Combine(repo, "app.txt"), "overwritten-temporarily\n", new UTF8Encoding(false));
        var cpListRes = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "git_checkpoints_list" } }, wts);
        var cps = (Dictionary<string, object>[])cpListRes["checkpoints"];
        Require(cps.Any(c => Convert.ToString(c["ref"]) == cpRef), "saved checkpoint listed");
        var restoreRes = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "git_checkpoint_restore" }, { "ref", cpRef } }, wts);
        Require(true.Equals(restoreRes["ok"]), "checkpoint restored");
        Require(File.ReadAllText(Path.Combine(repo, "app.txt"), Encoding.UTF8).Contains("line2-edited"), "restored file matches checkpoint");

        Console.WriteLine("GitReviewCheck: status, per-file diff, checkpoints+restore, branches, scripts, commit and worktree lifecycle passed");
        return 0;
    }
}
