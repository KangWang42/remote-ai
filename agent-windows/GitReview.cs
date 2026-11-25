using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RemoteCli {
/// Two-stage workspace Git review engine:
/// 1) `git_status` returns lightweight branch/worktree metadata and per-file change counts without raw diff bodies.
/// 2) `git_file_diff` fetches bounded unified diffs per file on demand (including synthetic diffs for untracked files).
/// Also provides commit, push, worktree merge/cleanup and non-intrusive hidden-ref checkpoints with index.lock backoff.
public static class GitReview {
    const int MaxDiffBytes = 256 * 1024;
    const int MaxUntrackedBytes = 128 * 1024;

    sealed class RunOut { public int Code; public string Out = "", Err = ""; }

    static RunOut Exec(string folder, string args, int timeoutMs = 20000, string stdinText = null, bool retryLock = false) {
        int attempts = retryLock ? 4 : 1;
        RunOut last = null;
        for (int attempt = 0; attempt < attempts; attempt++) {
            last = RunOnce(folder, args, timeoutMs, stdinText);
            if (last.Code == 0) return last;
            bool lockErr = (last.Err ?? "").IndexOf("index.lock", StringComparison.OrdinalIgnoreCase) >= 0
                || (last.Err ?? "").IndexOf("Another git process", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!lockErr || attempt == attempts - 1) return last;
            Thread.Sleep(150 << attempt);
        }
        return last ?? new RunOut { Code = -1, Err = "git 执行失败" };
    }

    static RunOut RunOnce(string folder, string args, int timeoutMs, string stdinText) {
        try {
            var info = new ProcessStartInfo("git", "-C \"" + folder + "\" " + args) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = stdinText != null,
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            using (var p = Process.Start(info)) {
                if (stdinText != null) {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(stdinText);
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    p.StandardInput.Close();
                }
                var outBuf = new MemoryStream();
                var copyOut = p.StandardOutput.BaseStream.CopyToAsync(outBuf);
                var readErr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) {
                    try { p.Kill(); } catch { }
                    return new RunOut { Code = -1, Err = "git 命令超时" };
                }
                copyOut.Wait(3000); readErr.Wait(3000);
                return new RunOut { Code = p.ExitCode, Out = Encoding.UTF8.GetString(outBuf.ToArray()), Err = (readErr.Result ?? "").Trim() };
            }
        } catch (System.ComponentModel.Win32Exception) {
            return new RunOut { Code = -1, Err = "电脑上未找到 git 命令" };
        } catch (Exception ex) {
            return new RunOut { Code = -1, Err = ex.Message };
        }
    }

    static string Str(IDictionary<string, object> d, string key) {
        object v; return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : "";
    }

    static string QuoteArg(string value) {
        return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    public static Dictionary<string, object> Handle(string repoDir, Dictionary<string, object> op, Worktrees worktrees) {
        string action = Str(op, "action");
        string wtId = Str(op, "worktree");
        string targetDir = wtId.Length > 0 ? worktrees.ResolveSessionWorktreePath(repoDir, wtId) : repoDir;

        if (action == "git_status") return Status(repoDir, targetDir, wtId, worktrees);
        if (action == "git_file_diff") return FileDiff(targetDir, Str(op, "path"), Str(op, "scope"), Str(op, "base"));
        if (action == "git_commit") return Commit(targetDir, Str(op, "message"), op.ContainsKey("files") ? op["files"] as System.Collections.IEnumerable : null);
        if (action == "git_push") return Push(targetDir, true.Equals(op.ContainsKey("set_upstream") ? op["set_upstream"] : null));
        if (action == "git_worktree_list") return WorktreeList(repoDir, worktrees);
        if (action == "git_worktree_apply") return WorktreeApply(repoDir, wtId, true.Equals(op.ContainsKey("remove_after") ? op["remove_after"] : null), worktrees);
        if (action == "git_worktree_remove") return WorktreeRemove(repoDir, wtId, worktrees);
        if (action == "git_checkpoint") return worktrees.CaptureCheckpoint(targetDir, Str(op, "label"));
        throw new ArgumentException("不支持此 Git 操作");
    }

    public static Dictionary<string, object> Status(string repoDir, string targetDir, string wtId, Worktrees worktrees) {
        var top = Exec(targetDir, "rev-parse --show-toplevel", 10000);
        if (top.Code != 0) {
            return new Dictionary<string, object> {
                { "is_repo", false }, { "branch", "" }, { "head", "" }, { "ahead", 0 }, { "behind", 0 },
                { "worktree", wtId }, { "files", new object[0] }, { "worktrees", new object[0] }
            };
        }
        var branchRes = Exec(targetDir, "rev-parse --abbrev-ref HEAD", 8000);
        string branch = branchRes.Code == 0 ? branchRes.Out.Trim() : "";
        var headRes = Exec(targetDir, "rev-parse --short HEAD", 8000);
        string head = headRes.Code == 0 ? headRes.Out.Trim() : "";
        int ahead = 0, behind = 0;
        var ab = Exec(targetDir, "rev-list --left-right --count HEAD...@{upstream}", 8000);
        if (ab.Code == 0) {
            string[] nums = (ab.Out ?? "").Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length == 2) { Int32.TryParse(nums[0], out ahead); Int32.TryParse(nums[1], out behind); }
        }

        var numstat = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var statsRes = Exec(targetDir, "-c core.quotepath=false diff --numstat HEAD", 15000);
        if (statsRes.Code == 0) {
            foreach (string line in (statsRes.Out ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                string[] cols = line.Split('\t');
                if (cols.Length < 3) continue;
                bool bin = cols[0] == "-" || cols[1] == "-";
                int add = 0, del = 0;
                if (!bin) { Int32.TryParse(cols[0], out add); Int32.TryParse(cols[1], out del); }
                numstat[cols[cols.Length - 1]] = new[] { add, del, bin ? 1 : 0 };
            }
        }

        var porcelain = Exec(targetDir, "-c core.quotepath=false status --porcelain=v1 --untracked-files=all", 15000);
        var files = new List<Dictionary<string, object>>();
        if (porcelain.Code == 0) {
            foreach (string raw in (porcelain.Out ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                if (raw.Length < 4) continue;
                char x = raw[0], y = raw[1];
                string path = raw.Substring(3).Trim();
                int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0) path = path.Substring(arrow + 4);
                string status = (x == '?' && y == '?') ? "?" : (x == 'A' || y == 'A') ? "A" : (x == 'D' || y == 'D') ? "D" : (x == 'R' || y == 'R') ? "R" : "M";
                int add = 0, del = 0; bool binary = false;
                int[] counts;
                if (numstat.TryGetValue(path, out counts)) {
                    add = counts[0]; del = counts[1]; binary = counts[2] == 1;
                } else if (status == "?") {
                    string full = Path.Combine(targetDir, path.Replace('/', Path.DirectorySeparatorChar));
                    try {
                        var fi = new FileInfo(full);
                        if (fi.Exists && fi.Length <= MaxUntrackedBytes) {
                            byte[] bytes = File.ReadAllBytes(full);
                            binary = bytes.Take(4096).Any(b => b == 0);
                            if (!binary) add = Encoding.UTF8.GetString(bytes).Split('\n').Length;
                        } else if (fi.Exists) {
                            binary = true;
                        }
                    } catch { }
                }
                files.Add(new Dictionary<string, object> {
                    { "path", path }, { "status", status },
                    { "staged", x != ' ' && x != '?' }, { "unstaged", y != ' ' },
                    { "additions", add }, { "deletions", del }, { "binary", binary }
                });
                if (files.Count >= 300) break;
            }
        }

        var wts = worktrees.ListWorktrees(repoDir).Select(w => (object)new Dictionary<string, object> {
            { "id", w.Id }, { "branch", w.Branch }, { "head", w.Head.Length > 7 ? w.Head.Substring(0, 7) : w.Head },
            { "main", w.Main }, { "managed", w.Managed }, { "locked", w.Locked }
        }).ToArray();

        return new Dictionary<string, object> {
            { "is_repo", true }, { "branch", branch }, { "head", head }, { "ahead", ahead }, { "behind", behind },
            { "worktree", wtId }, { "files", files.ToArray() }, { "worktrees", wts }
        };
    }

    public static Dictionary<string, object> FileDiff(string targetDir, string relPath, string scope, string baseRef) {
        if (String.IsNullOrWhiteSpace(relPath)) throw new ArgumentException("文件路径无效");
        string norm = relPath.Replace('\\', '/').Trim();
        if (norm.StartsWith("/") || norm.Split('/').Contains("..")) throw new ArgumentException("文件路径无效");
        string full = Path.GetFullPath(Path.Combine(targetDir, norm.Replace('/', Path.DirectorySeparatorChar)));
        string root = Path.GetFullPath(targetDir).TrimEnd('\\') + "\\";
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("文件路径超出项目目录");

        string diffCmd;
        if (scope == "staged") diffCmd = "-c core.quotepath=false diff --cached --no-color -- " + QuoteArg(norm);
        else if (scope == "base" && !String.IsNullOrWhiteSpace(baseRef)) diffCmd = "-c core.quotepath=false diff --no-color " + QuoteArg(baseRef.Trim()) + " -- " + QuoteArg(norm);
        else diffCmd = "-c core.quotepath=false diff --no-color HEAD -- " + QuoteArg(norm);

        var res = Exec(targetDir, diffCmd, 15000);
        string text = res.Code == 0 ? (res.Out ?? "") : "";
        bool binary = text.IndexOf("Binary files ", StringComparison.Ordinal) >= 0 || text.IndexOf("GIT binary patch", StringComparison.Ordinal) >= 0;
        bool truncated = false;

        // Synthesize a standard unified diff for untracked files so the Diff viewer renders them uniformly.
        if (text.Length == 0 && File.Exists(full)) {
            var tracked = Exec(targetDir, "ls-files --error-unmatch -- " + QuoteArg(norm), 8000);
            if (tracked.Code != 0) {
                var fi = new FileInfo(full);
                if (fi.Length > MaxUntrackedBytes) {
                    truncated = true;
                    text = "--- /dev/null\n+++ b/" + norm + "\n@@ -0,0 +1,1 @@\n+[新文件过大，" + fi.Length + " 字节，已省略预览]";
                } else {
                    byte[] bytes = File.ReadAllBytes(full);
                    binary = bytes.Take(4096).Any(b => b == 0);
                    if (binary) {
                        text = "Binary files /dev/null and b/" + norm + " differ\n";
                    } else {
                        string content = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
                        string[] lines = content.Split('\n');
                        if (lines.Length > 0 && lines[lines.Length - 1].Length == 0) lines = lines.Take(lines.Length - 1).ToArray();
                        var sb = new StringBuilder();
                        sb.Append("--- /dev/null\n+++ b/").Append(norm).Append("\n@@ -0,0 +1,").Append(lines.Length).Append(" @@\n");
                        foreach (string l in lines) sb.Append('+').Append(l).Append('\n');
                        text = sb.ToString();
                    }
                }
            }
        }

        if (Encoding.UTF8.GetByteCount(text) > MaxDiffBytes) {
            truncated = true;
            int cut = Math.Min(text.Length, MaxDiffBytes / 2);
            text = text.Substring(0, cut) + "\n…（差异超过 256 KB，后文已省略）";
        }
        int additions = 0, deletions = 0;
        foreach (string line in text.Split('\n')) {
            if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal)) additions++;
            else if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal)) deletions++;
        }
        return new Dictionary<string, object> {
            { "path", norm }, { "diff", text }, { "truncated", truncated }, { "binary", binary },
            { "additions", additions }, { "deletions", deletions }
        };
    }

    public static Dictionary<string, object> Commit(string targetDir, string message, System.Collections.IEnumerable files) {
        if (String.IsNullOrWhiteSpace(message)) throw new ArgumentException("请输入提交说明");
        var fileList = new List<string>();
        if (files != null) foreach (object f in files) {
            string p = Convert.ToString(f ?? "").Trim().Replace('\\', '/');
            if (p.Length > 0 && !p.StartsWith("/") && !p.Split('/').Contains("..")) fileList.Add(p);
        }
        RunOut add = fileList.Count == 0
            ? Exec(targetDir, "add -A", 20000, retryLock: true)
            : Exec(targetDir, "add -- " + String.Join(" ", fileList.Select(QuoteArg)), 20000, retryLock: true);
        if (add.Code != 0) throw new InvalidOperationException("暂存文件失败：" + add.Err);
        var commit = Exec(targetDir, "commit -F -", 20000, message.Trim() + "\n", retryLock: true);
        if (commit.Code != 0) throw new InvalidOperationException("提交失败：" + (commit.Err.Length > 0 ? commit.Err : commit.Out));
        var head = Exec(targetDir, "rev-parse --short HEAD", 8000);
        var branch = Exec(targetDir, "rev-parse --abbrev-ref HEAD", 8000);
        return new Dictionary<string, object> {
            { "commit", head.Code == 0 ? head.Out.Trim() : "" },
            { "branch", branch.Code == 0 ? branch.Out.Trim() : "" },
            { "summary", (commit.Out ?? "").Trim() }
        };
    }

    public static Dictionary<string, object> Push(string targetDir, bool setUpstream) {
        string cmd = "push";
        if (setUpstream) {
            var br = Exec(targetDir, "rev-parse --abbrev-ref HEAD", 8000);
            if (br.Code == 0 && !String.IsNullOrWhiteSpace(br.Out) && br.Out.Trim() != "HEAD")
                cmd = "push -u origin " + QuoteArg(br.Out.Trim());
        }
        var pushed = Exec(targetDir, cmd, 45000);
        if (pushed.Code != 0) throw new InvalidOperationException("推送失败：" + (pushed.Err.Length > 0 ? pushed.Err : pushed.Out));
        return new Dictionary<string, object> { { "ok", true }, { "output", (pushed.Err.Length > 0 ? pushed.Err : pushed.Out).Trim() } };
    }

    static Dictionary<string, object> WorktreeList(string repoDir, Worktrees worktrees) {
        var list = worktrees.ListWorktrees(repoDir).Select(w => (object)new Dictionary<string, object> {
            { "id", w.Id }, { "branch", w.Branch }, { "head", w.Head.Length > 7 ? w.Head.Substring(0, 7) : w.Head },
            { "main", w.Main }, { "managed", w.Managed }, { "locked", w.Locked }
        }).ToArray();
        return new Dictionary<string, object> { { "worktrees", list } };
    }

    static Dictionary<string, object> WorktreeApply(string repoDir, string wtId, bool removeAfter, Worktrees worktrees) {
        string wtPath = worktrees.ResolveSessionWorktreePath(repoDir, wtId);
        var baseRes = Exec(repoDir, "merge-base HEAD " + QuoteArg(Exec(wtPath, "rev-parse HEAD", 8000).Out.Trim()), 10000);
        string baseCommit = baseRes.Code == 0 ? baseRes.Out.Trim() : "HEAD";
        string stat = worktrees.Apply(repoDir, wtPath, baseCommit);
        if (removeAfter) worktrees.SafeRemoveWorktree(repoDir, wtPath);
        return new Dictionary<string, object> { { "ok", true }, { "stat", stat }, { "removed", removeAfter } };
    }

    static Dictionary<string, object> WorktreeRemove(string repoDir, string wtId, Worktrees worktrees) {
        string wtPath = worktrees.ResolveSessionWorktreePath(repoDir, wtId);
        worktrees.SafeRemoveWorktree(repoDir, wtPath);
        return new Dictionary<string, object> { { "ok", true } };
    }
}
}
