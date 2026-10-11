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
                    if (name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) || name.Equals("Codex", StringComparison.Ordinal)) result.Name = "Codex 桌面版";
                    else if (name.Equals("Claude", StringComparison.Ordinal)) result.Name = "Claude 桌面版";
                    else if (name.Equals("Positron", StringComparison.OrdinalIgnoreCase)) result.Name = "Positron";
                    else if (name.Equals("Code", StringComparison.OrdinalIgnoreCase)) result.Name = "VS Code";
                    else if (name.Equals("Cursor", StringComparison.OrdinalIgnoreCase)) result.Name = "Cursor";
                    else if (name.Equals("Windsurf", StringComparison.OrdinalIgnoreCase)) result.Name = "Windsurf";
                }
            } catch (ArgumentException) { break; }
            catch (System.ComponentModel.Win32Exception) { break; }
            pid = parent;
        }
        return result;
    }
}

// Claude session records carry an exact Windows process start fingerprint. A PID alone is insufficient.
public sealed class ClaudeSessions {
    public sealed class Owner {
        public string Session = "", Dir = "", Status = "", Host = "unknown", Origin = "", Reason = "无法核实 Claude Code 会话，请先在原终端结束后重试";
        public string Socket = "";      // where it takes messages from other sessions (Claude Code's own note), "" when it says none
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
    public static string Entry(string session) {
        try {
            foreach (string file in Directory.GetFiles(Path.Combine(Home, "sessions"), "*.json")) {
                try {
                    var record = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
                    object value;
                    if (record != null && record.TryGetValue("sessionId", out value) && Convert.ToString(value) == session) return record.TryGetValue("entrypoint", out value) ? Convert.ToString(value) : "";
                } catch (IOException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
            }
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
    public sealed class Owner {
        public bool Known, Live, CanTakeover;
        public int Pid;
        public long Started;
        public string Host = "unknown";
        public string Origin = "";
        public string Reason = "正在确认电脑会话的归属，请稍后刷新";
    }
    [StructLayout(LayoutKind.Sequential)] struct UniqueProcess { public uint pid; public System.Runtime.InteropServices.ComTypes.FILETIME started; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ProcessInfo {
        public UniqueProcess process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string service;
        public uint type, status, session;
        [MarshalAs(UnmanagedType.Bool)] public bool restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint session, uint files, string[] names, uint processes, IntPtr applications, uint services, IntPtr serviceNames);
    [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] processes, ref uint reasons);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);
    readonly object gate = new object();
    Dictionary<string, Owner> cached = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
    bool refreshing;
    DateTime next = DateTime.MinValue;
    public static string Home { get { return Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"); } }
    public static string LockPath(string id) {
        if (!Regex.IsMatch(id ?? "", @"\A[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\z")) throw new ArgumentException("会话编号无效");
        return Path.Combine(Home, "thread-writer-locks", id + ".lock");
    }
    // Existence alone is insufficient: the Rust writer locks the file's bytes.
    static bool? Held(string path) {
        try {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) {
                try { stream.Lock(0, Int64.MaxValue); stream.Unlock(0, Int64.MaxValue); return false; }
                catch (IOException ex) { return (ex.HResult & 0xffff) == 33 ? (bool?)true : null; }
            }
        } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
    }
    public static Owner Inspect(string id) {
        string path = LockPath(id); bool? held = Held(path);
        if (held == false) return new Owner { Known = true, Host = "", Reason = "" };
        if (held == null) return new Owner { Reason = "无法核实写入锁，请在电脑结束会话后重试，或在手机新开副本" };
        var result = new Owner { Known = true, Live = true, Reason = "无法确认独立 CLI 的归属，可在手机新开副本" };
        uint session;
        if (RmStartSession(out session, 0, new StringBuilder(33)) != 0) return result;
        try {
            if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return result;
            uint needed, count = 8, reasons = 0; var records = new ProcessInfo[count];
            int code = RmGetList(session, out needed, ref count, records, ref reasons);
            if (code == 234 && needed <= 256) { count = needed; records = new ProcessInfo[count]; code = RmGetList(session, out needed, ref count, records, ref reasons); }
            if (code != 0 || count != 1) return result;
            var record = records[0];
            long started = ((long)record.process.started.dwHighDateTime << 32) | (uint)record.process.started.dwLowDateTime;
            using (var process = Process.GetProcessById((int)record.process.pid)) {
                if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != started) return result;
                result.Pid = process.Id; result.Started = started;
                if (!String.Equals(process.ProcessName, "codex", StringComparison.OrdinalIgnoreCase)) return result;
                string command = "";
                using (var search = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + process.Id))
                using (var rows = search.Get()) foreach (ManagementObject row in rows) using (row) command = Convert.ToString(row["CommandLine"]);
                if (String.IsNullOrWhiteSpace(command)) return result;
                var origin = SessionOrigin.Inspect(process.Id); result.Origin = origin.Name;
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
            }
        } catch { /* Unknown ownership cannot authorize process termination. */ }
        finally { RmEndSession(session); }
        return result;
    }
    public void Refresh() {
        lock (gate) { if (refreshing || DateTime.UtcNow < next) return; refreshing = true; next = DateTime.UtcNow.AddSeconds(5); }
        Task.Run(delegate {
            var fresh = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
            try {
                string folder = Path.Combine(Home, "thread-writer-locks");
                if (Directory.Exists(folder)) foreach (string path in Directory.GetFiles(folder, "*.lock")) {
                    string id = Path.GetFileNameWithoutExtension(path);
                    if (Regex.IsMatch(id, @"\A[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\z")) fresh[id] = Inspect(id);
                }
                lock (gate) cached = fresh;
            } catch { /* Keep the last verified snapshot; actions always recheck. */ }
            finally { lock (gate) refreshing = false; }
        });
    }
    public Owner Get(string id) {
        lock (gate) { Owner owner; return cached.TryGetValue(id, out owner) ? owner : new Owner { Known = !File.Exists(LockPath(id)), Reason = "正在确认电脑会话的归属，请稍后刷新" }; }
    }
    public static bool Descends(int pid, IEnumerable<int> roots, Dictionary<int, int> parents) {
        var rootSet = new HashSet<int>(roots); var seen = new HashSet<int>();
        while (pid > 0 && seen.Add(pid)) { if (rootSet.Contains(pid)) return true; int parent; if (!parents.TryGetValue(pid, out parent)) break; pid = parent; }
        return false;
    }
    public static void Takeover(string id, IEnumerable<int> protectedRoots, Dictionary<int, int> parents) {
        Owner owner = Inspect(id);
        if (!owner.Known) throw new InvalidOperationException(owner.Reason);
        if (!owner.Live) return;
        if (!owner.CanTakeover || Descends(owner.Pid, protectedRoots, parents)) throw new InvalidOperationException("无法安全结束这个电脑会话，请先在电脑结束，或在手机新开副本");
        // Process.Kill uses the opened process handle; never invoke a global name-based kill.
        using (var process = Process.GetProcessById(owner.Pid)) {
            if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != owner.Started) throw new InvalidOperationException("会话进程已变化，请刷新后重试");
            Owner current = Inspect(id);
            if (!current.CanTakeover || current.Pid != owner.Pid || current.Started != owner.Started) throw new InvalidOperationException("会话归属已变化，请刷新后重试");
            process.Kill();
            if (!process.WaitForExit(5000)) throw new InvalidOperationException("电脑会话没有结束，已取消接手");
        }
        for (int n = 0; n < 50; n++) { if (Held(LockPath(id)) == false) return; Thread.Sleep(50); }
        throw new InvalidOperationException("写入锁尚未释放，已取消接手，请稍后重试");
    }
}
}
