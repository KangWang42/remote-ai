using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
public sealed class SessionOrigin {
    public bool Remote;
    /// Where a conversation runs, in words for its owner: a command line unless a program that is known stands above it.
    public const string CommandLine = "命令行";
    public string Name = CommandLine;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ProcessEntry {
        public uint size, usage, processId; public IntPtr heap; public uint module, threads, parentId; public int priority; public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string file;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")] static extern bool CloseSnapshot(IntPtr handle);
    /// Every process and its parent, as they are at this moment: one look at the system for all of them.
    public static Dictionary<int, int> Parents() {
        var map = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return map;
        try {
            var entry = new ProcessEntry { size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
            if (Process32FirstW(snapshot, ref entry)) do { map[(int)entry.processId] = (int)entry.parentId; } while (Process32NextW(snapshot, ref entry));
        } finally { CloseSnapshot(snapshot); }
        return map;
    }
    /// Whether a process was started, however many steps down, by another: read from the processes as they are now.
    public static bool Descends(int pid, int ancestor) {
        var parents = Parents(); var seen = new HashSet<int>();
        for (int depth = 0; depth < 32 && pid > 0 && seen.Add(pid); depth++) {
            int parent;
            if (!parents.TryGetValue(pid, out parent) || parent <= 0 || parent == pid) return false;
            try {
                // a number is given anew once its process has ended: a parent is older than its child
                using (var child = Process.GetProcessById(pid)) using (var above = Process.GetProcessById(parent)) if (above.StartTime.ToUniversalTime() > child.StartTime.ToUniversalTime()) return false;
            } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; } catch (System.ComponentModel.Win32Exception) { return false; }
            if (parent == ancestor) return true;
            pid = parent;
        }
        return false;
    }
    /// Whether `pid` is one of `roots` or descends from any of them in the given parent map.
    public static bool Descends(int pid, IEnumerable<int> roots, Dictionary<int, int> parents) {
        if (roots == null || parents == null) return false;
        var rootSet = new HashSet<int>(roots); var seen = new HashSet<int>();
        while (pid > 0 && seen.Add(pid)) { if (rootSet.Contains(pid)) return true; int parent; if (!parents.TryGetValue(pid, out parent)) break; pid = parent; }
        return false;
    }
    /// Maps a process name to the nearest user-visible host. Kept separate so new editor hosts can be tested and
    /// added without changing ownership or takeover rules.
    public static string HostOf(string processName) {
        string name = processName ?? "";
        if (name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) || name.Equals("Codex", StringComparison.Ordinal)) return "Codex 桌面版";
        if (name.Equals("Claude", StringComparison.Ordinal)) return "Claude 桌面版";
        if (name.Equals("Positron", StringComparison.OrdinalIgnoreCase)) return "Positron";
        if (name.Equals("Code", StringComparison.OrdinalIgnoreCase) || name.Equals("Code - Insiders", StringComparison.OrdinalIgnoreCase)) return "VS Code";
        if (name.Equals("Cursor", StringComparison.OrdinalIgnoreCase)) return "Cursor";
        if (name.Equals("Windsurf", StringComparison.OrdinalIgnoreCase)) return "Windsurf";
        if (name.Equals("RStudio", StringComparison.OrdinalIgnoreCase) || name.Equals("rsession", StringComparison.OrdinalIgnoreCase)) return "RStudio";
        if (name.Equals("idea64", StringComparison.OrdinalIgnoreCase) || name.Equals("pycharm64", StringComparison.OrdinalIgnoreCase) || name.Equals("webstorm64", StringComparison.OrdinalIgnoreCase) || name.Equals("rider64", StringComparison.OrdinalIgnoreCase) || name.Equals("clion64", StringComparison.OrdinalIgnoreCase)) return "JetBrains";
        return "";
    }
    /// `parents`: the list to read from, where one was taken already (several conversations are looked at in a row).
    public static SessionOrigin Inspect(int pid, Dictionary<int, int> parents = null) {
        var result = new SessionOrigin();
        var seen = new HashSet<int>();
        if (parents == null) parents = Parents();
        for (int depth = 0; depth < 16 && pid > 0 && seen.Add(pid); depth++) {
            int parent;
            if (!parents.TryGetValue(pid, out parent) || parent <= 0 || parent == pid) break;
            try {
                using (var child = Process.GetProcessById(pid)) using (var ancestor = Process.GetProcessById(parent)) {
                    if (ancestor.StartTime.ToUniversalTime() > child.StartTime.ToUniversalTime()) break;
                    string name = ancestor.ProcessName;
                    if (new[] { "RemoteCli", "RemoteCliAgent", "TerminalAgent" }.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                        result.Remote = true; result.Name = "其它远程终端"; return result;
                    }
                    // The desktop apps run the tools as their own children. Claude's app is Claude.exe, its command line
                    // claude.exe: the two differ in the capital only.
                    // The first recognized host above the tool is the one the owner sees. Do not let a
                    // shared Codex/Claude process farther up the tree overwrite an editor such as Positron.
                    // This matters when an editor starts an app-server through its own helper process.
                    if (result.Name == CommandLine) {
                        string host = HostOf(name);
                        if (host.Length > 0) result.Name = host;
                    }
                }
            } catch (ArgumentException) { break; }
            catch (InvalidOperationException) { break; }
            catch (System.ComponentModel.Win32Exception) { break; }
            pid = parent;
        }
        return result;
    }
}

// Claude session records carry an exact Windows process start fingerprint. A PID alone is insufficient.
public sealed class ClaudeSessions {
    public const string MultipleReason = "这段 Claude Code 对话同时在多个终端打开，请先在电脑上关闭多余终端再接管或结束";
    // A resumed conversation can have more than one live process. A local editor must not be
    // replaced by a remote terminal's record just because that file was enumerated last.
    public static Owner Preferred(IEnumerable<Owner> owners) {
        return owners.Where(o => o.Live).OrderBy(o => o.Host == "remote")
            .ThenByDescending(o => o.Status == "confirm" ? 3 : o.Status == "busy" ? 2 : o.Status == "waiting" ? 1 : 0)
            .ThenByDescending(o => o.Started).ThenBy(o => o.Pid).FirstOrDefault();
    }
    public sealed class Owner {
        public string Session = "", Dir = "", Status = "", Host = "unknown", Origin = "", Reason = "无法核实 Claude Code 会话，请先在原终端结束后重试";
        public string Socket = "";      // where it takes messages from other sessions (Claude Code's own note), "" when it says none
        public string Entry = "";       // how it was started, as Claude Code notes it: "cli", "claude-desktop…", "claude-vscode"
        public int Pid;
        public long Started;
        public bool Live, CanTakeover;
    }
    /// Where Claude Code keeps its conversations and the notes of the programs that have them open: its own folder
    /// in the person's home, or the one CLAUDE_CONFIG_DIR names, as Claude Code itself reads it.
    public static string Home {
        get {
            string named = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return String.IsNullOrWhiteSpace(named) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : named;
        }
    }
    /// How a session was started, as Claude Code notes it ("cli", "claude-desktop…", "sdk-cli" for a script's run);
    /// "" when it has no note. Whether its program still runs is not looked at.
    public static string Entry(string session) { return Noted(session, "entrypoint"); }
    /// The version of the Claude Code that runs a session, as it notes it ("2.1.283"); "" when it has no note.
    public static string Version(string session) { return Noted(session, "version"); }
    /// The process that has a session, as Claude Code notes it; 0 when it has no note. It is the one that starts
    /// the commands of the session's steps.
    public static int ProcessOf(string session) { int pid; return Int32.TryParse(Noted(session, "pid"), out pid) ? pid : 0; }
    static string Noted(string session, string what) {
        try {
            string folder = Path.Combine(Home, "sessions");
            if (!Directory.Exists(folder)) return "";
            string fallback = ""; DateTime fallbackAt = DateTime.MinValue;
            var reader = new JavaScriptSerializer();
            foreach (string file in Directory.GetFiles(folder, "*.json")) {
                try {
                    var record = reader.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
                    object value;
                    if (record == null || !record.TryGetValue("sessionId", out value) || Convert.ToString(value) != session) continue;
                    string found = record.TryGetValue(what, out value) ? Convert.ToString(value) : "";
                    if (Inspect(file).Live) return found;
                    DateTime modified = File.GetLastWriteTimeUtc(file);
                    if (fallback.Length == 0 || modified >= fallbackAt) { fallback = found; fallbackAt = modified; }
                } catch (IOException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
            }
            return fallback;
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return "";
    }
    public static Owner Inspect(string file, Dictionary<int, int> parents = null) {
        var result = new Owner();
        try {
            var record = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            object value; int pid; long started;
            if (!record.TryGetValue("pid", out value) || !Int32.TryParse(Convert.ToString(value), out pid)) return result;
            if (!record.TryGetValue("sessionId", out value)) return result;
            result.Session = Convert.ToString(value);
            if (!Regex.IsMatch(result.Session, @"\A[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\z")) return result;
            using (var process = Process.GetProcessById(pid)) {
                if (!process.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase) && !process.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase)) return result;
                started = process.StartTime.ToUniversalTime().ToFileTimeUtc();
                long fingerprint;
                bool exact = record.TryGetValue("procStart", out value) && Int64.TryParse(Convert.ToString(value), out fingerprint) && fingerprint == started;
                if (record.ContainsKey("procStart") && !exact) return result;
                if (!exact && File.GetCreationTimeUtc(file) < process.StartTime.ToUniversalTime()) return result;
                result.Pid = pid; result.Started = started; result.Live = true;
                if (record.TryGetValue("cwd", out value)) result.Dir = Convert.ToString(value);
                if (record.TryGetValue("status", out value)) result.Status = Convert.ToString(value);
                if (record.TryGetValue("messagingSocketPath", out value) && value != null) result.Socket = Convert.ToString(value);
                var origin = SessionOrigin.Inspect(pid, parents); result.Origin = origin.Name;
                // Claude Code says itself how it was started; its app starts it as "claude-desktop".
                if (record.TryGetValue("entrypoint", out value)) result.Entry = Convert.ToString(value);
                if (record.TryGetValue("entrypoint", out value) && Convert.ToString(value).StartsWith("claude-desktop", StringComparison.OrdinalIgnoreCase)) result.Origin = "Claude 桌面版";
                result.Host = origin.Remote ? "remote" : exact ? "cli" : "unknown";
                result.CanTakeover = exact && !origin.Remote;
                result.Reason = result.CanTakeover ? "接管会结束原终端中的这段 Claude Code 对话，再在手机恢复同一段历史" : origin.Remote ? "此对话由其它远程终端管理，请先在原终端结束后再接管" : result.Reason;
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        return result;
    }
}
// Read-only ownership checks run outside the terminal input/report lock.
public sealed class CodexSessions {
    public sealed class Owner : LockOwner { }
    readonly object gate = new object();
    Dictionary<string, Owner> cached = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
    bool refreshing;
    DateTime next = DateTime.MinValue;
    public static string Home { get { return Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"); } }
    public static string LockPath(string id) {
        if (!SessionLocks.IsUuid(id)) throw new ArgumentException("会话编号无效");
        return Path.Combine(Home, "thread-writer-locks", id + ".lock");
    }
    public static Owner Inspect(string id, Dictionary<int, int> parents = null) {
        string path = LockPath(id); bool? held = SessionLocks.Held(path);
        if (held == false) return new Owner { Known = true, Host = "", Reason = "" };
        if (held == null) return new Owner { Reason = "无法核实写入锁，请在电脑结束会话后重试，或在手机新开副本" };
        var result = new Owner { Known = true, Live = true, Reason = "无法确认独立 CLI 的归属，可在手机新开副本" };
        try {
            var holder = SessionLocks.SingleHolder(path);
            if (holder == null) return result;
            result.Pid = holder.Pid; result.Started = holder.Started;
            if (!String.Equals(holder.ProcessName, "codex", StringComparison.OrdinalIgnoreCase)) return result;
            string command = "";
            using (var search = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + holder.Pid))
            using (var rows = search.Get()) foreach (ManagementObject row in rows) using (row) command = Convert.ToString(row["CommandLine"]);
            if (String.IsNullOrWhiteSpace(command)) return result;
            var origin = SessionOrigin.Inspect(holder.Pid, parents); result.Origin = origin.Name;
            if (origin.Remote) {
                result.Host = "remote";
                result.Reason = "此对话在其它远程终端运行，请先在原远程终端结束后，再接管原对话";
                return result;
            }
            if (Regex.IsMatch(command, @"(?:^|\s)(?:app-server|mcp-server)(?:\s|$)", RegexOptions.IgnoreCase)) {
                result.Host = "shared";
                if (result.Origin == SessionOrigin.CommandLine) result.Origin = "Codex 后台服务";      // the shared background service, with no window of its own
                result.Reason = "此对话由共享后台占用。请先在电脑原应用中结束并关闭此对话，释放后手机才能接管同一段历史；仅切换页面可能不会释放";
                return result;
            }
            result.Host = "cli";
            result.CanTakeover = true; result.Reason = "接手会结束电脑上的这个 CLI 会话，正在执行的任务可能中断";
        } catch { /* Unknown ownership cannot authorize process termination. */ }
        return result;
    }
    public void Refresh() {
        lock (gate) { if (refreshing || DateTime.UtcNow < next) return; refreshing = true; next = DateTime.UtcNow.AddSeconds(5); }
        Task.Run(delegate {
            var fresh = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
            try {
                string folder = Path.Combine(Home, "thread-writer-locks");
                if (Directory.Exists(folder)) {
                    var parents = SessionOrigin.Parents();
                    foreach (string path in Directory.GetFiles(folder, "*.lock")) {
                        string id = Path.GetFileNameWithoutExtension(path);
                        if (SessionLocks.IsUuid(id)) fresh[id] = Inspect(id, parents);
                    }
                }
                lock (gate) cached = fresh;
            } catch { /* Keep the last verified snapshot; actions always recheck. */ }
            finally { lock (gate) refreshing = false; }
        });
    }
    public Owner Get(string id) {
        lock (gate) { Owner owner; return cached.TryGetValue(id, out owner) ? owner : new Owner { Known = !File.Exists(LockPath(id)), Reason = "正在确认电脑会话的归属，请稍后刷新" }; }
    }
    public static void Takeover(string id, IEnumerable<int> protectedRoots, Dictionary<int, int> parents) {
        SessionLocks.Takeover(() => Inspect(id, parents), LockPath(id), protectedRoots, parents,
            "无法安全结束这个电脑会话，请先在电脑结束，或在手机新开副本",
            "电脑会话没有结束，已取消接手",
            "写入锁尚未释放，已取消接手，请稍后重试");
    }
}
}
