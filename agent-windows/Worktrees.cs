using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteCli {
/// Manages isolated Git worktrees for parallel multi-AI comparison tasks (`Teams` compare mode).
/// Each candidate AI works in `<rootDir>/<teamId>/<tool>` branched from the repo's HEAD at start time; once the
/// owner compares diffs and picks a winner, the winning worktree's changes (committed, unstaged, and untracked) are
/// safely checked and applied to the main project folder, and all temporary worktrees for that team are removed.
public sealed class Worktrees {
    readonly string rootDir;
    static readonly Regex SafeId = new Regex(@"\A[a-f0-9]{16,32}\z");
    static readonly Regex SafeTool = new Regex(@"\A[a-z0-9_-]{1,24}\z");

    public sealed class Created {
        public string Path = "", BaseCommit = "";
    }

    public Worktrees(string rootDir) { this.rootDir = rootDir; }

    sealed class GitResult { public int Code; public byte[] OutBytes = new byte[0]; public string Out = "", Err = ""; }

    static GitResult RunGit(string folder, string args, int timeoutMs = 20000) {
        try {
            var info = new ProcessStartInfo("git", "-C \"" + folder + "\" " + args) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            using (var p = Process.Start(info)) {
                var outBuf = new MemoryStream();
                var copyOut = p.StandardOutput.BaseStream.CopyToAsync(outBuf);
                var readErr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) {
                    try { p.Kill(); } catch (Exception) { }
                    return new GitResult { Code = -1, Err = "git 命令超时" };
                }
                copyOut.Wait(3000); readErr.Wait(3000);
                byte[] bytes = outBuf.ToArray();
                return new GitResult { Code = p.ExitCode, OutBytes = bytes, Out = Encoding.UTF8.GetString(bytes), Err = (readErr.Result ?? "").Trim() };
            }
        } catch (System.ComponentModel.Win32Exception) {
            return new GitResult { Code = -1, Err = "电脑上未找到 git 命令" };
        } catch (InvalidOperationException ex) {
            return new GitResult { Code = -1, Err = ex.Message };
        }
    }

    /// Creates an isolated detached-HEAD worktree for one candidate tool of a compare task.
    public Created Create(string repoDir, string teamId, string tool) {
        if (String.IsNullOrEmpty(repoDir) || !Directory.Exists(repoDir)) throw new ArgumentException("项目文件夹不存在");
        if (!SafeId.IsMatch(teamId ?? "") || !SafeTool.IsMatch(tool ?? "")) throw new ArgumentException("工作区参数无效");

        var top = RunGit(repoDir, "rev-parse --show-toplevel");
        if (top.Code != 0) throw new InvalidOperationException("该项目文件夹不是 Git 仓库，无法创建独立 worktree 进行并行比稿");
        var head = RunGit(repoDir, "rev-parse HEAD");
        if (head.Code != 0 || String.IsNullOrWhiteSpace(head.Out)) throw new InvalidOperationException("该 Git 仓库尚无提交记录，无法创建独立 worktree 进行并行比稿");
        string baseCommit = head.Out.Trim();

        string teamFolder = Path.Combine(rootDir, teamId);
        string path = Path.Combine(teamFolder, tool);
        Directory.CreateDirectory(teamFolder);
        if (Directory.Exists(path)) {
            RunGit(repoDir, "worktree remove --force \"" + path + "\"");
            TryDeleteDir(path);
        }
        var added = RunGit(repoDir, "worktree add --detach \"" + path + "\" " + baseCommit, 30000);
        if (added.Code != 0 || !Directory.Exists(path)) {
            throw new InvalidOperationException("未能创建独立工作区：" + (added.Err.Length > 0 ? added.Err : "git worktree add 失败"));
        }
        return new Created { Path = path, BaseCommit = baseCommit };
    }

    /// Human-readable file status + diff of a worktree relative to its starting commit (including untracked files).
    public string Diff(string worktreePath, string baseCommit) {
        if (String.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath)) return "";
        string target = String.IsNullOrWhiteSpace(baseCommit) ? "HEAD" : baseCommit.Trim();
        var status = RunGit(worktreePath, "-c core.quotepath=false status --porcelain --untracked-files=all");
        var diff = RunGit(worktreePath, "-c core.quotepath=false diff " + target + " --no-color");
        var parts = new List<string>();
        string statusLines = (status.Out ?? "").TrimEnd();
        if (statusLines.Length > 0) parts.Add("文件：\n" + statusLines);
        else {
            // Candidate may have committed all changes inside its worktree; show name-status vs baseCommit.
            var committed = RunGit(worktreePath, "-c core.quotepath=false diff --name-status " + target);
            if (!String.IsNullOrWhiteSpace(committed.Out)) parts.Add("文件：\n" + committed.Out.TrimEnd());
        }
        string diffText = (diff.Out ?? "").TrimEnd();
        if (diffText.Length > 0) parts.Add(diffText);
        // Preview untracked text files so the user can compare newly added files too.
        var untracked = RunGit(worktreePath, "-c core.quotepath=false ls-files --others --exclude-standard");
        foreach (string rel in (untracked.Out ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(8)) {
            string full = Path.Combine(worktreePath, rel.Replace('/', Path.DirectorySeparatorChar));
            try {
                if (!File.Exists(full)) continue;
                byte[] bytes = File.ReadAllBytes(full);
                if (bytes.Take(4096).Any(b => b == 0)) { parts.Add("+++ " + rel + "（二进制文件，" + bytes.Length + " 字节）"); continue; }
                string text = Encoding.UTF8.GetString(bytes);
                if (text.Length > 4000) text = text.Substring(0, 4000) + "\n…（后文省略）";
                parts.Add("+++ 新建文件：" + rel + "\n" + text);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return String.Join("\n\n", parts);
    }

    /// Short one-line summary of how many files changed in a worktree relative to `baseCommit`.
    public string Stat(string worktreePath, string baseCommit) {
        if (String.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath)) return "工作区不可用";
        string target = String.IsNullOrWhiteSpace(baseCommit) ? "HEAD" : baseCommit.Trim();
        var names = new List<string>();
        var diffNames = RunGit(worktreePath, "-c core.quotepath=false diff --name-only " + target);
        foreach (string line in (diffNames.Out ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) names.Add(line.Trim());
        var untracked = RunGit(worktreePath, "-c core.quotepath=false ls-files --others --exclude-standard");
        foreach (string line in (untracked.Out ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) if (!names.Contains(line.Trim())) names.Add(line.Trim());
        if (names.Count == 0) return "没有文件改动";
        var shortNames = names.Take(3).Select(n => { int i = n.LastIndexOfAny(new[] { '/', '\\' }); return i >= 0 ? n.Substring(i + 1) : n; }).ToArray();
        return names.Count + " 个文件改动：" + String.Join("、", shortNames) + (names.Count > 3 ? " 等" : "");
    }

    /// Checks and applies all changes from `worktreePath` (committed, unstaged, and untracked) onto `repoDir`.
    /// Leaves `repoDir` untouched if a conflict exists.
    public string Apply(string repoDir, string worktreePath, string baseCommit) {
        if (String.IsNullOrEmpty(repoDir) || !Directory.Exists(repoDir)) throw new ArgumentException("主项目文件夹不存在");
        if (String.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath)) throw new InvalidOperationException("候选方案的工作区已不存在");
        string target = String.IsNullOrWhiteSpace(baseCommit) ? "HEAD" : baseCommit.Trim();
        string stat = Stat(worktreePath, target);

        // Stage everything in the isolated worktree so the binary-safe patch includes untracked files and commits.
        var add = RunGit(worktreePath, "add -A", 20000);
        if (add.Code != 0) throw new InvalidOperationException("未能整理候选工作区的改动：" + add.Err);
        var patch = RunGit(worktreePath, "-c core.quotepath=false diff --cached --binary " + target, 30000);
        if (patch.Code != 0) throw new InvalidOperationException("未能生成候选改动补丁：" + patch.Err);
        if (patch.OutBytes.Length == 0) return "没有文件改动";

        Directory.CreateDirectory(rootDir);
        string patchFile = Path.Combine(rootDir, "apply-" + Guid.NewGuid().ToString("N") + ".patch");
        try {
            File.WriteAllBytes(patchFile, patch.OutBytes);
            var check = RunGit(repoDir, "apply --check \"" + patchFile + "\"", 20000);
            if (check.Code != 0) throw new InvalidOperationException("无法直接合并到主目录（存在冲突或主目录已有冲突改动）：" + (check.Err.Length > 0 ? check.Err : "git apply --check 失败"));
            var applied = RunGit(repoDir, "apply \"" + patchFile + "\"", 20000);
            if (applied.Code != 0) throw new InvalidOperationException("合并补丁失败：" + applied.Err);
            return stat;
        } finally {
            try { if (File.Exists(patchFile)) File.Delete(patchFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// Removes all worktrees created for `teamId` and prunes Git worktree metadata.
    public void RemoveAll(string repoDir, string teamId) {
        if (!SafeId.IsMatch(teamId ?? "")) return;
        string teamFolder = Path.Combine(rootDir, teamId);
        if (Directory.Exists(teamFolder)) {
            foreach (string sub in Directory.GetDirectories(teamFolder)) {
                if (!String.IsNullOrEmpty(repoDir) && Directory.Exists(repoDir)) RunGit(repoDir, "worktree remove --force \"" + sub + "\"", 15000);
                TryDeleteDir(sub);
            }
            TryDeleteDir(teamFolder);
        }
        if (!String.IsNullOrEmpty(repoDir) && Directory.Exists(repoDir)) RunGit(repoDir, "worktree prune", 10000);
    }

    /// Shared helper: git status + diff in a working directory (empty string if not a repo or clean).
    public static string ChangesIn(string folder) {
        if (String.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return "";
        var files = RunGit(folder, "status --porcelain --untracked-files=all", 15000);
        if (files.Code != 0 || String.IsNullOrWhiteSpace(files.Out)) return "";
        var diff = RunGit(folder, "-c core.quotepath=false diff HEAD --no-color", 15000);
        return "文件：\n" + files.Out.TrimEnd() + "\n\n" + (diff.Code == 0 ? diff.Out.TrimEnd() : "");
    }

    static void TryDeleteDir(string path) {
        try {
            if (!Directory.Exists(path)) return;
            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch (Exception) { }
            }
            Directory.Delete(path, true);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
}
