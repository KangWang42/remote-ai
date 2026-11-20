using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using RemoteCli;

/// Verifies `agent-windows/Worktrees.cs` against a real local Git repository:
/// - refuses a non-git folder cleanly;
/// - creates two isolated worktrees for two candidate AIs;
/// - computes diff & stat including modified, committed, and untracked files;
/// - applies the chosen candidate cleanly into the main repo;
/// - refuses a conflicting candidate without modifying the main repo;
/// - removes all temporary worktrees and prunes git metadata cleanly.
public static class WorktreesCheck {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    static void Git(string dir, string args) {
        var info = new ProcessStartInfo("git", "-C \"" + dir + "\" " + args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(info)) {
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            Require(p.ExitCode == 0, "git " + args + " failed: " + err);
        }
    }

    public static int Main(string[] args) {
        try { return Run(args); }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message + "\n" + ex.StackTrace); return 1; }
    }
    static int Run(string[] args) {
        string root = args[0];
        string repo = Path.Combine(root, "repo");
        string nonGit = Path.Combine(root, "plain");
        string wtRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(nonGit);

        var wt = new Worktrees(wtRoot);
        bool refusedNonGit = false;
        try { wt.Create(nonGit, "aaaaaaaaaaaaaaaa", "claude"); } catch (InvalidOperationException) { refusedNonGit = true; }
        Require(refusedNonGit, "non-git directory refused");

        // Init real local repo with one commit
        Git(repo, "init");
        Git(repo, "config user.email check@example.com");
        Git(repo, "config user.name Check");
        File.WriteAllText(Path.Combine(repo, "app.txt"), "line1\nline2\n", new UTF8Encoding(false));
        Git(repo, "add app.txt");
        Git(repo, "commit -m init");

        string teamId = "1111222233334444";
        var cClaude = wt.Create(repo, teamId, "claude");
        var cCodex = wt.Create(repo, teamId, "codex");
        Require(Directory.Exists(cClaude.Path) && Directory.Exists(cCodex.Path), "two isolated worktrees created");
        Require(cClaude.Path != cCodex.Path && cClaude.BaseCommit.Length >= 7 && cClaude.BaseCommit == cCodex.BaseCommit, "distinct paths, same base commit");

        // Candidate 1 (claude): edits app.txt and creates a new untracked file extra.txt
        File.WriteAllText(Path.Combine(cClaude.Path, "app.txt"), "line1\nline2-claude\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(cClaude.Path, "extra.txt"), "from-claude\n", new UTF8Encoding(false));

        // Candidate 2 (codex): edits app.txt differently AND commits inside its own worktree
        File.WriteAllText(Path.Combine(cCodex.Path, "app.txt"), "line1\nline2-codex\n", new UTF8Encoding(false));
        Git(cCodex.Path, "add app.txt");
        Git(cCodex.Path, "commit -m codex-way");

        // Main repo is still completely untouched
        Require(File.ReadAllText(Path.Combine(repo, "app.txt"), Encoding.UTF8) == "line1\nline2\n", "main repo untouched during parallel edits");

        string claudeDiff = wt.Diff(cClaude.Path, cClaude.BaseCommit);
        string claudeStat = wt.Stat(cClaude.Path, cClaude.BaseCommit);
        Require(claudeDiff.Contains("line2-claude") && claudeDiff.Contains("from-claude"), "claude diff covers modified and untracked: " + claudeDiff);
        Require(claudeStat.Contains("2 个文件改动"), "claude stat counts both files: " + claudeStat);

        string codexDiff = wt.Diff(cCodex.Path, cCodex.BaseCommit);
        string codexStat = wt.Stat(cCodex.Path, cCodex.BaseCommit);
        Require(codexDiff.Contains("line2-codex") && codexStat.Contains("1 个文件改动"), "codex diff/stat covers committed changes inside worktree: " + codexStat);

        // Apply claude's solution cleanly into main repo
        string applied = wt.Apply(repo, cClaude.Path, cClaude.BaseCommit);
        Require(applied.Contains("2 个文件改动"), "apply summary: " + applied);
        Require(File.ReadAllText(Path.Combine(repo, "app.txt"), Encoding.UTF8).Replace("\r\n", "\n") == "line1\nline2-claude\n", "main repo received modified file");
        Require(File.ReadAllText(Path.Combine(repo, "extra.txt"), Encoding.UTF8).Replace("\r\n", "\n") == "from-claude\n", "main repo received untracked file");

        // Applying codex's conflicting patch now fails cleanly without corrupting main repo
        bool conflictRefused = false;
        try { wt.Apply(repo, cCodex.Path, cCodex.BaseCommit); } catch (InvalidOperationException ex) { conflictRefused = ex.Message.Contains("冲突"); }
        Require(conflictRefused, "conflicting apply refused cleanly");
        Require(File.ReadAllText(Path.Combine(repo, "app.txt"), Encoding.UTF8).Replace("\r\n", "\n") == "line1\nline2-claude\n", "main repo unchanged after failed conflict apply");

        // Cleanup removes all worktrees for this team
        wt.RemoveAll(repo, teamId);
        Require(!Directory.Exists(cClaude.Path) && !Directory.Exists(cCodex.Path), "worktree folders removed");
        Console.WriteLine("worktrees: isolated creation, diff/stat (untracked + committed), safe apply, conflict guard and cleanup ok");
        return 0;
    }
}
