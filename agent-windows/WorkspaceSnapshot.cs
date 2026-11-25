using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RemoteCli {
/// Builds a compact, strictly bounded, fail-open read-only workspace snapshot (Git summary, 1-2 level directory
/// layout, and runnable scripts) in <100ms for injection into the first turn of multi-AI collaboration members
/// (`Teams.cs`) and read-only peer consultants (`Peers.cs`), avoiding redundant cold-start exploration turns.
public static class WorkspaceSnapshot {
    public const int DefaultMaxBytes = 1500;
    public const int DefaultScanTimeoutMs = 250;
    public const int DefaultGitTimeoutMs = 800;

    static readonly HashSet<string> IgnoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        ".git", ".hg", ".svn", "node_modules", "bower_components", ".pnpm-store",
        "bin", "obj", "target", "dist", "build", "out", ".build", ".cache",
        "__pycache__", ".pytest_cache", ".ruff_cache", ".mypy_cache", ".venv", "venv", "env",
        "vendor", ".next", ".nuxt", ".turbo", ".gradle", ".idea", ".vs",
        "coverage", "playwright-report", "test-results", "logs", "tmp", "temp", "09_backup",
        "AppData", "Application Data", "Program Files", "Program Files (x86)", "ProgramData",
        "$Recycle.Bin", "System Volume Information", "Windows", "System32", "SysWOW64"
    };

    static readonly HashSet<string> PriorityDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "src", "app", "lib", "packages", "server", "web", "web-app", "client", "api",
        "tests", "test", "scripts", "tools", "docs", "relay", "android", "ios",
        "agent-windows", "agent-linux", "agent-rust"
    };

    /// Returns a compact read-only snapshot of `folder`, or "" if `folder` is inaccessible, empty, or not a project
    /// directory (filesystem root or user home directory). Never throws.
    public static string Build(string folder, int maxBytes = DefaultMaxBytes, int scanTimeoutMs = DefaultScanTimeoutMs, int gitTimeoutMs = DefaultGitTimeoutMs) {
        if (String.IsNullOrWhiteSpace(folder)) return "";
        try {
            string full = Path.GetFullPath(folder.Trim());
            if (!Directory.Exists(full) || IsNonWorkspaceDir(full)) return "";

            var gitTask = Task.Run(() => BuildGitSummary(full, gitTimeoutMs));
            string tree = BuildDirectoryTree(full, scanTimeoutMs);
            string scripts = BuildScriptsSummary(full);
            string git = "";
            try {
                if (gitTask.Wait(Math.Max(200, gitTimeoutMs + 250))) git = gitTask.Result ?? "";
            } catch { }

            var sections = new List<string>();
            string name = Path.GetFileName(full.TrimEnd('\\', '/'));
            sections.Add("目录：" + (String.IsNullOrEmpty(name) ? full : name));
            if (git.Length > 0) sections.Add(git);
            if (tree.Length > 0) sections.Add("结构：\n" + tree);
            if (scripts.Length > 0) sections.Add("脚本：" + scripts);
            if (sections.Count <= 1) return "";

            string combined = String.Join("\n", sections.ToArray());
            return ProcessText.TruncateUtf8(combined, Math.Max(256, maxBytes), "\n…（快照已截断）");
        } catch {
            return "";
        }
    }

    static bool IsNonWorkspaceDir(string fullPath) {
        string norm = fullPath.TrimEnd('\\', '/');
        string root = (Path.GetPathRoot(fullPath) ?? "").TrimEnd('\\', '/');
        if (String.Equals(norm, root, StringComparison.OrdinalIgnoreCase)) return true;
        string home = (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? "").TrimEnd('\\', '/');
        if (home.Length > 0 && String.Equals(norm, home, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool HasGitRepo(string folder) {
        string cur = folder;
        for (int depth = 0; depth < 16 && !String.IsNullOrEmpty(cur); depth++) {
            string git = Path.Combine(cur, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return true;
            string parent = Path.GetDirectoryName(cur);
            if (String.IsNullOrEmpty(parent) || String.Equals(parent, cur, StringComparison.OrdinalIgnoreCase)) break;
            cur = parent;
        }
        return false;
    }

    static string RunGitQuick(string folder, string args, int timeoutMs) {
        try {
            var info = new ProcessStartInfo("git", ProcessText.HardenedReadOnlyGitPrefix + "-C \"" + folder + "\" " + args) {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            ProcessText.ApplyNonInteractiveGitEnv(info);
            using (var p = Process.Start(info)) {
                var outBuf = new MemoryStream();
                var copyOut = p.StandardOutput.BaseStream.CopyToAsync(outBuf);
                var drainErr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(Math.Max(150, timeoutMs))) {
                    ProcessText.KillProcessTree(p.Id, 1500);
                    return null;
                }
                copyOut.Wait(500);
                drainErr.Wait(200);
                if (p.ExitCode != 0) return null;
                byte[] data = outBuf.ToArray();
                if (data.Length > 32768) {
                    byte[] trimmed = new byte[32768];
                    Buffer.BlockCopy(data, 0, trimmed, 0, trimmed.Length);
                    data = trimmed;
                }
                return ProcessText.DecodeProcessOutput(data);
            }
        } catch {
            return null;
        }
    }

    static string BuildGitSummary(string folder, int timeoutMs) {
        if (!HasGitRepo(folder)) return "";
        var statusTask = Task.Run(() => RunGitQuick(folder, "status --porcelain=v1 --branch", timeoutMs));
        var logTask = Task.Run(() => RunGitQuick(folder, "log -n 3 --format=\"%h %s\"", timeoutMs));
        Task.WaitAll(new Task[] { statusTask, logTask }, Math.Max(300, timeoutMs + 200));

        string statusOut = statusTask.IsCompleted ? statusTask.Result : null;
        string logOut = logTask.IsCompleted ? logTask.Result : null;
        if (statusOut == null && logOut == null) return "";

        var lines = new List<string>();
        if (statusOut != null) {
            string[] raw = statusOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string branch = "HEAD";
            var modified = new List<string>();
            for (int i = 0; i < raw.Length; i++) {
                string line = raw[i];
                if (line.StartsWith("## ", StringComparison.Ordinal)) {
                    string head = line.Substring(3).Trim();
                    int dots = head.IndexOf("...", StringComparison.Ordinal);
                    if (dots >= 0) head = head.Substring(0, dots).Trim();
                    if (head.StartsWith("No commits yet on ", StringComparison.Ordinal)) head = head.Substring(18).Trim() + " (未提交)";
                    else if (head.StartsWith("Initial commit on ", StringComparison.Ordinal)) head = head.Substring(18).Trim() + " (未提交)";
                    branch = head.Length > 0 ? head : "HEAD";
                } else {
                    string trimmed = line.Trim();
                    if (trimmed.Length > 0) modified.Add(trimmed);
                }
            }
            if (modified.Count == 0) {
                lines.Add("Git：分支 " + branch + "（工作区干净）");
            } else {
                var shown = new List<string>();
                int chars = 0;
                for (int i = 0; i < Math.Min(modified.Count, 8); i++) {
                    string item = ProcessText.TruncateSafe(modified[i], 70);
                    if (shown.Count > 0 && chars + item.Length + 2 > 220) break;
                    shown.Add(item);
                    chars += item.Length + 2;
                }
                string more = shown.Count < modified.Count ? " 等 " + modified.Count + " 项" : "";
                lines.Add("Git：分支 " + branch + " | 改动(" + modified.Count + ")：" + String.Join(", ", shown.ToArray()) + more);
            }
        }
        if (!String.IsNullOrWhiteSpace(logOut)) {
            var commits = logOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => ProcessText.TruncateSafe(l.Trim(), 64))
                .Where(l => l.Length > 0)
                .Take(3)
                .ToArray();
            if (commits.Length > 0) lines.Add("最近提交：" + String.Join(" ; ", commits));
        }
        return String.Join("\n", lines.ToArray());
    }

    static string BuildDirectoryTree(string folder, int scanTimeoutMs) {
        var sw = Stopwatch.StartNew();
        var entries = new List<string>();
        var dirInfo = new DirectoryInfo(folder);
        FileSystemInfo[] top;
        try {
            top = dirInfo.GetFileSystemInfos();
        } catch {
            return "";
        }

        var dirs = new List<DirectoryInfo>();
        var files = new List<FileInfo>();
        foreach (var item in top) {
            if (sw.ElapsedMilliseconds > scanTimeoutMs) break;
            string name = item.Name;
            if (name.Length == 0 || name.StartsWith(".", StringComparison.Ordinal)) continue;
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var d = item as DirectoryInfo;
            if (d != null) {
                if (!IgnoredDirs.Contains(name)) dirs.Add(d);
            } else if (item is FileInfo) {
                files.Add((FileInfo)item);
            }
        }

        dirs.Sort((a, b) => {
            int pa = PriorityDirs.Contains(a.Name) ? 0 : 1;
            int pb = PriorityDirs.Contains(b.Name) ? 0 : 1;
            if (pa != pb) return pa.CompareTo(pb);
            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        int maxTopDirs = 12;
        for (int i = 0; i < Math.Min(dirs.Count, maxTopDirs); i++) {
            if (sw.ElapsedMilliseconds > scanTimeoutMs) break;
            var d = dirs[i];
            string subSummary = "";
            if (PriorityDirs.Contains(d.Name)) {
                try {
                    var subs = new List<string>();
                    foreach (var child in d.EnumerateFileSystemInfos()) {
                        if (sw.ElapsedMilliseconds > scanTimeoutMs) break;
                        if (child.Name.StartsWith(".", StringComparison.Ordinal)) continue;
                        if (child is DirectoryInfo && IgnoredDirs.Contains(child.Name)) continue;
                        subs.Add(child is DirectoryInfo ? child.Name + "/" : child.Name);
                        if (subs.Count >= 6) break;
                    }
                    if (subs.Count > 0) subSummary = " (" + String.Join(", ", subs.ToArray()) + ")";
                } catch { }
            }
            entries.Add("- " + d.Name + "/" + subSummary);
        }
        if (dirs.Count > maxTopDirs) entries.Add("- …另有 " + (dirs.Count - maxTopDirs) + " 个目录");

        int maxTopFiles = 8;
        if (files.Count > 0) {
            var shownFiles = files.Take(maxTopFiles).Select(f => f.Name).ToArray();
            string more = files.Count > maxTopFiles ? " 等 " + files.Count + " 个文件" : "";
            entries.Add("- 顶层文件：" + String.Join(", ", shownFiles) + more);
        }
        return String.Join("\n", entries.ToArray());
    }

    static string BuildScriptsSummary(string folder) {
        try {
            var res = GitReview.ProjectScripts(folder);
            object raw;
            if (res == null || !res.TryGetValue("scripts", out raw)) return "";
            var list = raw as Dictionary<string, object>[];
            if (list == null || list.Length == 0) return "";
            var shown = list.Take(6).Select(s => Convert.ToString(s["command"])).Where(c => !String.IsNullOrWhiteSpace(c)).ToArray();
            return String.Join(" | ", shown);
        } catch {
            return "";
        }
    }
}
}
