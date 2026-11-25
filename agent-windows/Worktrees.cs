using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RemoteCli {
/// Manages isolated Git worktrees for parallel multi-AI comparison tasks (`Teams` compare mode).
/// Each candidate AI works in `<rootDir>/<teamId>/<tool>` branched from the repo's HEAD at start time; once the
/// owner compares diffs and picks a winner, the winning worktree's changes (committed, unstaged, and untracked) are
/// safely checked and applied to the main project folder, and all temporary worktrees for that team are removed.
public sealed class Worktrees {
    readonly string rootDir;
    readonly object autoCpGate = new object();
    readonly Dictionary<string, string> lastAutoTree = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    const int MaxAutoCheckpoints = 25;
    static readonly Regex SafeId = new Regex(@"\A[a-f0-9]{16,32}\z");
    static readonly Regex SafeTool = new Regex(@"\A[a-z0-9_-]{1,24}\z");

    public sealed class Created {
        public string Path = "", BaseCommit = "", Branch = "", Id = "";
    }

    public sealed class Info {
        public string Id = "", Path = "", Branch = "", Head = "";
        public bool Main, Managed, Locked;
    }

    public Worktrees(string rootDir) { this.rootDir = rootDir; }

    sealed class GitResult { public int Code; public byte[] OutBytes = new byte[0]; public string Out = "", Err = ""; }

    static GitResult RunGit(string folder, string args, int timeoutMs = 20000, Dictionary<string, string> extraEnv = null, string stdinText = null, bool readOnly = false) {
        try {
            string prefix = readOnly ? ProcessText.HardenedReadOnlyGitPrefix : "";
            var info = new ProcessStartInfo("git", prefix + "-C \"" + folder + "\" " + args) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = stdinText != null
            };
            ProcessText.ApplyNonInteractiveGitEnv(info);
            if (extraEnv != null) foreach (var kv in extraEnv) info.EnvironmentVariables[kv.Key] = kv.Value;
            using (var p = Process.Start(info)) {
                if (stdinText != null) {
                    byte[] inputBytes = new UTF8Encoding(false).GetBytes(stdinText);
                    p.StandardInput.BaseStream.Write(inputBytes, 0, inputBytes.Length);
                    p.StandardInput.Close();
                }
                var outBuf = new MemoryStream();
                var errBuf = new MemoryStream();
                var copyOut = p.StandardOutput.BaseStream.CopyToAsync(outBuf);
                var copyErr = p.StandardError.BaseStream.CopyToAsync(errBuf);
                if (!p.WaitForExit(timeoutMs)) {
                    ProcessText.KillProcessTree(p.Id, 3000);
                    return new GitResult { Code = -1, Err = "git 命令超时" };
                }
                copyOut.Wait(3000); copyErr.Wait(3000);
                byte[] bytes = outBuf.ToArray();
                return new GitResult {
                    Code = p.ExitCode,
                    OutBytes = bytes,
                    Out = ProcessText.DecodeProcessOutput(bytes),
                    Err = ProcessText.DecodeProcessOutput(errBuf.ToArray()).Trim()
                };
            }
        } catch (System.ComponentModel.Win32Exception) {
            return new GitResult { Code = -1, Err = "电脑上未找到 git 命令" };
        } catch (InvalidOperationException ex) {
            return new GitResult { Code = -1, Err = ex.Message };
        }
    }

    /// Creates an isolated named-branch worktree for an ordinary interactive session or terminal.
    public Created CreateSessionWorktree(string repoDir, string sessionKey, string baseBranch = "", string branchName = "") {
        if (String.IsNullOrEmpty(repoDir) || !Directory.Exists(repoDir)) throw new ArgumentException("项目文件夹不存在");
        string safeKey = Regex.Replace(sessionKey ?? "", @"[^a-zA-Z0-9_-]", "");
        if (safeKey.Length < 4 || safeKey.Length > 40) safeKey = Guid.NewGuid().ToString("N").Substring(0, 12);
        var top = RunGit(repoDir, "rev-parse --show-toplevel");
        if (top.Code != 0) throw new InvalidOperationException("该项目文件夹不是 Git 仓库，无法创建独立 Worktree");
        string targetRef = String.IsNullOrWhiteSpace(baseBranch) ? "HEAD" : baseBranch.Trim();
        var resolved = RunGit(repoDir, "rev-parse \"" + targetRef + "\"");
        if (resolved.Code != 0 || String.IsNullOrWhiteSpace(resolved.Out)) throw new InvalidOperationException("找不到基线分支或提交：" + targetRef);
        string baseCommit = resolved.Out.Trim();
        string wantedBranch = String.IsNullOrWhiteSpace(branchName) ? "remote/" + safeKey.Substring(0, Math.Min(12, safeKey.Length)) : branchName.Trim();
        string sessionsRoot = Path.Combine(rootDir, "sessions");
        Directory.CreateDirectory(sessionsRoot);
        string path = Path.Combine(sessionsRoot, safeKey);
        if (Directory.Exists(path)) SafeRemoveWorktree(repoDir, path);

        string branch = wantedBranch;
        var exists = RunGit(repoDir, "show-ref --verify --quiet \"refs/heads/" + branch + "\"");
        if (exists.Code == 0) branch = wantedBranch + "-" + DateTime.UtcNow.ToString("HHmmss");
        var added = RunGit(repoDir, "worktree add -b \"" + branch + "\" \"" + path + "\" " + baseCommit, 30000);
        if (added.Code != 0 || !Directory.Exists(path))
            throw new InvalidOperationException("未能创建独立 Worktree：" + (added.Err.Length > 0 ? added.Err : "git worktree add 失败"));
        return new Created { Id = safeKey, Path = path, BaseCommit = baseCommit, Branch = branch };
    }

    public string ResolveSessionWorktreePath(string repoDir, string worktreeId) {
        if (String.IsNullOrWhiteSpace(worktreeId)) return repoDir;
        string clean = worktreeId.Trim();
        if (!Regex.IsMatch(clean, @"\A[A-Za-z0-9_-]{4,40}\z")) throw new ArgumentException("Worktree 标识无效");
        string full = Path.Combine(rootDir, "sessions", clean);
        if (!Directory.Exists(full)) throw new InvalidOperationException("指定的 Worktree 不存在或已清理");
        return full;
    }

    public List<Info> ListWorktrees(string repoDir) {
        var list = new List<Info>();
        if (String.IsNullOrEmpty(repoDir) || !Directory.Exists(repoDir)) return list;
        var res = RunGit(repoDir, "worktree list --porcelain", 15000, readOnly: true);
        if (res.Code != 0) return list;
        string managedPrefix = Path.GetFullPath(Path.Combine(rootDir, "sessions")).TrimEnd('\\') + "\\";
        Info current = null;
        foreach (string rawLine in (res.Out ?? "").Split(new[] { '\r', '\n' })) {
            string line = rawLine.Trim();
            if (line.Length == 0) {
                if (current != null && current.Path.Length > 0) { list.Add(current); current = null; }
                continue;
            }
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) {
                if (current != null && current.Path.Length > 0) list.Add(current);
                string p = line.Substring(9).Trim().Replace('/', '\\');
                bool managed = p.StartsWith(managedPrefix, StringComparison.OrdinalIgnoreCase);
                string id = managed ? Path.GetFileName(p.TrimEnd('\\')) : "";
                current = new Info { Path = p, Main = list.Count == 0, Managed = managed, Id = id };
            } else if (current != null) {
                if (line.StartsWith("HEAD ", StringComparison.Ordinal)) current.Head = line.Substring(5).Trim();
                else if (line.StartsWith("branch ", StringComparison.Ordinal)) current.Branch = line.Substring(7).Trim().Replace("refs/heads/", "");
                else if (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal)) current.Locked = true;
            }
        }
        if (current != null && current.Path.Length > 0) list.Add(current);
        return list;
    }

    /// Windows-safe worktree removal: unlinks NTFS junctions without following them, removes directory, then prunes Git metadata.
    public void SafeRemoveWorktree(string repoDir, string worktreePath) {
        if (String.IsNullOrWhiteSpace(worktreePath)) return;
        SafeDeleteDirectory(worktreePath);
        if (!String.IsNullOrEmpty(repoDir) && Directory.Exists(repoDir)) {
            RunGit(repoDir, "worktree remove --force --force \"" + worktreePath + "\"", 15000);
            RunGit(repoDir, "worktree prune", 10000);
        }
    }

    /// Captures a non-intrusive workspace snapshot into a hidden Git ref (refs/remote-ai/checkpoints/<id>)
    /// using a persistent scratch index so user branches and index remain untouched.
    public Dictionary<string, object> CaptureCheckpoint(string folder, string label = "") {
        var common = RunGit(folder, "rev-parse --git-common-dir", 10000);
        if (common.Code != 0 || String.IsNullOrWhiteSpace(common.Out)) throw new InvalidOperationException("不是有效的 Git 仓库");
        var head = RunGit(folder, "rev-parse HEAD", 10000);
        if (head.Code != 0 || String.IsNullOrWhiteSpace(head.Out)) throw new InvalidOperationException("仓库尚无提交，无法创建快照");
        string commonDir = common.Out.Trim();
        if (!Path.IsPathRooted(commonDir)) commonDir = Path.GetFullPath(Path.Combine(folder, commonDir));
        string indexDir = Path.Combine(commonDir, "remote-ai-checkpoints");
        Directory.CreateDirectory(indexDir);
        string scratchIndex = Path.Combine(indexDir, "scratch.index");
        var env = new Dictionary<string, string> { { "GIT_INDEX_FILE", scratchIndex } };
        var readTree = RunGit(folder, "read-tree HEAD", 15000, env);
        if (readTree.Code != 0) throw new InvalidOperationException("读取基线索引失败：" + readTree.Err);
        var addAll = RunGit(folder, "add -A", 20000, env);
        if (addAll.Code != 0) throw new InvalidOperationException("暂存快照文件失败：" + addAll.Err);
        var tree = RunGit(folder, "write-tree", 15000, env);
        if (tree.Code != 0 || String.IsNullOrWhiteSpace(tree.Out)) throw new InvalidOperationException("写入快照树失败：" + tree.Err);
        string treeOid = tree.Out.Trim();
        string headOid = head.Out.Trim();
        string cpId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N").Substring(0, 6);
        string msg = String.IsNullOrWhiteSpace(label) ? "Remote AI checkpoint " + cpId : label.Trim();
        var commit = RunGit(folder, "commit-tree " + treeOid + " -p " + headOid, 15000, null, msg);
        if (commit.Code != 0 || String.IsNullOrWhiteSpace(commit.Out)) throw new InvalidOperationException("创建快照提交失败：" + commit.Err);
        string commitOid = commit.Out.Trim();
        string refName = "refs/remote-ai/checkpoints/" + cpId;
        var updated = RunGit(folder, "update-ref \"" + refName + "\" " + commitOid, 10000);
        if (updated.Code != 0) throw new InvalidOperationException("更新快照引用失败：" + updated.Err);
        return new Dictionary<string, object> { { "id", cpId }, { "ref", refName }, { "commit", commitOid }, { "head", headOid }, { "label", msg } };
    }

    /// Low-overhead automatic turn checkpoint using the persistent scratch index:
    /// skips creating a ref when the working tree matches HEAD or the previous auto-checkpoint,
    /// and prunes older auto-* refs beyond MaxAutoCheckpoints so ref storage stays bounded.
    public Dictionary<string, object> CaptureAutoCheckpoint(string folder, string label = "") {
        if (String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        string normalized = Path.GetFullPath(folder).TrimEnd('\\');
        lock (autoCpGate) {
            var common = RunGit(normalized, "rev-parse --git-common-dir", 8000);
            if (common.Code != 0 || String.IsNullOrWhiteSpace(common.Out)) return null;
            var head = RunGit(normalized, "rev-parse HEAD", 8000);
            if (head.Code != 0 || String.IsNullOrWhiteSpace(head.Out)) return null;
            var headTree = RunGit(normalized, "rev-parse HEAD^{tree}", 8000);
            string headOid = head.Out.Trim();
            string headTreeOid = headTree.Code == 0 ? (headTree.Out ?? "").Trim() : "";

            string commonDir = common.Out.Trim();
            if (!Path.IsPathRooted(commonDir)) commonDir = Path.GetFullPath(Path.Combine(normalized, commonDir));
            string indexDir = Path.Combine(commonDir, "remote-ai-checkpoints");
            Directory.CreateDirectory(indexDir);
            string scratchIndex = Path.Combine(indexDir, "scratch.index");
            var env = new Dictionary<string, string> { { "GIT_INDEX_FILE", scratchIndex } };
            if (RunGit(normalized, "read-tree HEAD", 12000, env).Code != 0) return null;
            if (RunGit(normalized, "add -A", 15000, env).Code != 0) return null;
            var tree = RunGit(normalized, "write-tree", 12000, env);
            if (tree.Code != 0 || String.IsNullOrWhiteSpace(tree.Out)) return null;
            string treeOid = tree.Out.Trim();

            string prevTree;
            if (treeOid == headTreeOid || (lastAutoTree.TryGetValue(normalized, out prevTree) && prevTree == treeOid)) {
                return null;
            }
            string cpId = "auto-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N").Substring(0, 6);
            string msg = String.IsNullOrWhiteSpace(label) ? "Auto checkpoint " + cpId : label.Trim();
            var commit = RunGit(normalized, "commit-tree " + treeOid + " -p " + headOid, 12000, null, msg);
            if (commit.Code != 0 || String.IsNullOrWhiteSpace(commit.Out)) return null;
            string commitOid = commit.Out.Trim();
            string refName = "refs/remote-ai/checkpoints/" + cpId;
            if (RunGit(normalized, "update-ref \"" + refName + "\" " + commitOid, 8000).Code != 0) return null;
            lastAutoTree[normalized] = treeOid;
            PruneAutoCheckpoints(normalized);
            return new Dictionary<string, object> { { "id", cpId }, { "ref", refName }, { "commit", commitOid }, { "head", headOid }, { "label", msg } };
        }
    }

    void PruneAutoCheckpoints(string folder) {
        var listed = RunGit(folder, "for-each-ref --sort=-creatordate --format=\"%(refname)\" refs/remote-ai/checkpoints/auto-*", 8000);
        if (listed.Code != 0 || String.IsNullOrWhiteSpace(listed.Out)) return;
        string[] refs = listed.Out.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = MaxAutoCheckpoints; i < refs.Length; i++) {
            string r = refs[i].Trim();
            if (r.StartsWith("refs/remote-ai/checkpoints/auto-", StringComparison.Ordinal))
                RunGit(folder, "update-ref -d \"" + r + "\"", 6000);
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
        var status = RunGit(worktreePath, "status --porcelain --untracked-files=all", readOnly: true);
        var diff = RunGit(worktreePath, "diff --no-ext-diff " + target + " --no-color", readOnly: true);
        var parts = new List<string>();
        string statusLines = (status.Out ?? "").TrimEnd();
        if (statusLines.Length > 0) parts.Add("文件：\n" + statusLines);
        else {
            // Candidate may have committed all changes inside its worktree; show name-status vs baseCommit.
            var committed = RunGit(worktreePath, "diff --name-status " + target, readOnly: true);
            if (!String.IsNullOrWhiteSpace(committed.Out)) parts.Add("文件：\n" + committed.Out.TrimEnd());
        }
        string diffText = (diff.Out ?? "").TrimEnd();
        if (diffText.Length > 0) parts.Add(diffText);
        // Preview untracked text files so the user can compare newly added files too.
        var untracked = RunGit(worktreePath, "ls-files --others --exclude-standard", readOnly: true);
        foreach (string rel in (untracked.Out ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(8)) {
            string full = Path.Combine(worktreePath, rel.Replace('/', Path.DirectorySeparatorChar));
            try {
                if (!File.Exists(full)) continue;
                byte[] bytes = File.ReadAllBytes(full);
                if (bytes.Take(4096).Any(b => b == 0)) { parts.Add("+++ " + rel + "（二进制文件，" + bytes.Length + " 字节）"); continue; }
                string text = ProcessText.DecodeProcessOutput(bytes);
                if (text.Length > 4000) text = ProcessText.FoldHeadTail(text, 4000);
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
        var diffNames = RunGit(worktreePath, "diff --name-only " + target, readOnly: true);
        foreach (string line in (diffNames.Out ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) names.Add(line.Trim());
        var untracked = RunGit(worktreePath, "ls-files --others --exclude-standard", readOnly: true);
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

    /// Shared helper: git status + stat summary + diff in a working directory (empty string if not a repo or clean).
    public static string ChangesIn(string folder) {
        if (String.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return "";
        var files = RunGit(folder, "status --porcelain --untracked-files=all", 15000, readOnly: true);
        if (files.Code != 0 || String.IsNullOrWhiteSpace(files.Out)) return "";
        var statTask = Task.Run(() => RunGit(folder, "diff --no-ext-diff --stat HEAD --no-color", 15000, readOnly: true));
        var diffTask = Task.Run(() => RunGit(folder, "diff --no-ext-diff HEAD --no-color", 15000, readOnly: true));
        Task.WaitAll(statTask, diffTask);
        var parts = new List<string> { "文件：\n" + files.Out.TrimEnd() };
        if (statTask.Result.Code == 0 && !String.IsNullOrWhiteSpace(statTask.Result.Out))
            parts.Add("改动统计：\n" + statTask.Result.Out.TrimEnd());
        if (diffTask.Result.Code == 0 && !String.IsNullOrWhiteSpace(diffTask.Result.Out))
            parts.Add(diffTask.Result.Out.TrimEnd());
        return String.Join("\n\n", parts.ToArray());
    }

    static void TryDeleteDir(string path) {
        SafeDeleteDirectory(path);
    }

    /// Safely deletes a directory on Windows without following NTFS junctions or symlinks into targets.
    public static void SafeDeleteDirectory(string path) {
        try {
            if (!Directory.Exists(path)) return;
            var dirInfo = new DirectoryInfo(path);
            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0) {
                Directory.Delete(path, false);
                return;
            }
            foreach (string sub in Directory.GetDirectories(path)) SafeDeleteDirectory(sub);
            foreach (string file in Directory.GetFiles(path)) {
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch (Exception) { }
            }
            try { File.SetAttributes(path, FileAttributes.Normal); } catch (Exception) { }
            Directory.Delete(path, false);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
}
