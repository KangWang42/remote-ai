using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace RemoteCli {
// A real Windows terminal, with cursor movement, interactive menus and UTF-8.
public sealed class PseudoTerminal : IDisposable {
    [StructLayout(LayoutKind.Sequential)] public struct Coord { public short X, Y; public Coord(int x, int y) { X = (short)x; Y = (short)y; } }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo {
        public int cb; public string reserved, desktop, title;
        public int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
        public short showWindow, reserved2; public IntPtr reservedPtr, stdInput, stdOutput, stdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx { public StartupInfo startup; public IntPtr attributes; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process, thread; public int processId, threadId; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")] static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string cwd, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] static extern bool SetStdHandle(int kind, IntPtr handle);
    static readonly object creationGate = new object();
    readonly object gate = new object();
    IntPtr console;
    FileStream input, output;
    Process process;
    bool disposed;
    public int Id { get { return process.Id; } }
    public bool Closed { get; private set; }
    public bool Drained { get; private set; }   // the reader has delivered everything the program wrote
    public string ReadError = "";
    public int? ExitCode;
    public bool Alive { get { try { return process != null && !process.HasExited; } catch { return false; } } }
    public int Cols = 80, Rows = 24;

    public PseudoTerminal(string command, string cwd, int cols, int rows, Action<string> receive) {
        IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero, outRead = IntPtr.Zero, outWrite = IntPtr.Zero, attributes = IntPtr.Zero, environment = IntPtr.Zero;
        bool initialized = false;
        try {
            if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0) || !CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0)) throw new IOException("终端管道创建失败");
            int result = CreatePseudoConsole(new Coord(cols, rows), inRead, outWrite, 0, out console);
            if (result != 0) Marshal.ThrowExceptionForHR(result);
            input = new FileStream(new SafeFileHandle(inWrite, true), FileAccess.Write, 4096, false); inWrite = IntPtr.Zero;
            output = new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read, 4096, false); outRead = IntPtr.Zero;
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new IOException("终端属性初始化失败");
            initialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x00020016), console, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw new IOException("终端属性设置失败");
            StartupInfoEx startup = new StartupInfoEx(); startup.startup.cb = Marshal.SizeOf(typeof(StartupInfoEx)); startup.attributes = attributes;
            // Preserve the owner's CLI setup, but never inherit a parent coding session.
            var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) env[Convert.ToString(e.Key)] = Convert.ToString(e.Value);
            foreach (string key in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT" }) env.Remove(key);
            env["TERM"] = "xterm-256color"; env["COLORTERM"] = "truecolor";
            environment = Marshal.StringToHGlobalUni(String.Join("\0", env.Select(e => e.Key + "=" + e.Value)) + "\0\0");
            ProcessInfo info;
            lock (creationGate) {
                IntPtr stdin = GetStdHandle(-10), stdout = GetStdHandle(-11), stderr = GetStdHandle(-12);
                try {
                    SetStdHandle(-10, IntPtr.Zero); SetStdHandle(-11, IntPtr.Zero); SetStdHandle(-12, IntPtr.Zero);
                    if (!CreateProcess(null, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x00000400, environment, cwd, ref startup, out info))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                } finally { SetStdHandle(-10, stdin); SetStdHandle(-11, stdout); SetStdHandle(-12, stderr); }
            }
            CloseHandle(inRead); inRead = IntPtr.Zero;
            CloseHandle(outWrite); outWrite = IntPtr.Zero;
            CloseHandle(info.process); CloseHandle(info.thread);
            process = Process.GetProcessById(info.processId); Cols = cols; Rows = rows;
            Task.Run(delegate {
                byte[] bytes = new byte[8192]; char[] chars = new char[8192]; Decoder decoder = Encoding.UTF8.GetDecoder();
                try { int n; while ((n = output.Read(bytes, 0, bytes.Length)) > 0) { int count = decoder.GetChars(bytes, 0, n, chars, 0, false); if (count > 0) receive(new string(chars, 0, count)); } }
                catch (IOException ex) { ReadError = ex.Message; } catch (ObjectDisposedException) { }
                finally { Drained = true; Closed = true; }
            });
            Task.Run(delegate { try { process.WaitForExit(); ExitCode = process.ExitCode; } catch { } finally { CloseConsole(); Closed = true; } });
        } catch { Dispose(); throw; }
        finally {
            foreach (IntPtr h in new[] { inRead, inWrite, outRead, outWrite }) if (h != IntPtr.Zero) CloseHandle(h);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }
    public void Write(string text) { lock (gate) { if (disposed || Closed) throw new IOException("终端已结束"); byte[] b = Encoding.UTF8.GetBytes(text); input.Write(b, 0, b.Length); input.Flush(); } }
    public void Resize(int cols, int rows) { lock (gate) { if (console == IntPtr.Zero) return; Marshal.ThrowExceptionForHR(ResizePseudoConsole(console, new Coord(cols, rows))); Cols = cols; Rows = rows; } }
    void CloseConsole() { IntPtr h; lock (gate) { h = console; console = IntPtr.Zero; } if (h != IntPtr.Zero) ClosePseudoConsole(h); }
    public void Dispose() {
        lock (gate) { if (disposed) return; disposed = true; }
        try { if (process != null && !process.HasExited) { using (Process kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true })) kill.WaitForExit(10000); } } catch { }
        CloseConsole();
        if (input != null) input.Dispose();
        if (output != null) output.Dispose();
        Closed = true;
    }
}

public sealed class TerminalAgent {
    string Origin = "";
    readonly string dataDir;
    public TerminalAgent(string dataFolder = null) {
        dataDir = dataFolder ?? DefaultData;
        Reach = new Reach(RemoteCli.Reach.PipeName(dataDir)); Reach.Changed += Wake;
        Marks = new Marks(Path.Combine(dataDir, "conversation-marks.json"));
        // A program built without a target framework starts with TLS 1.0 only, which a relay of one's own behind
        // https refuses. TLS 1.3 is asked for where this Windows has it.
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
        catch (NotSupportedException) { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
        // A program that restarted for an update left word of its terminals: it comes back as the same computer and
        // opens them again, so the phone's pages go on where they were.
        try {
            var kept = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(RestartFile, Encoding.UTF8));
            File.Delete(RestartFile);
            if (DateTime.UtcNow - DateTime.Parse(Get(kept, "at"), null, System.Globalization.DateTimeStyles.RoundtripKind) < TimeSpan.FromMinutes(15) && Regex.IsMatch(Get(kept, "instance"), @"\A[a-f0-9]{32}\z")) {
                instance = Get(kept, "instance");
                returning = List(kept, "terminals").OfType<Dictionary<string, object>>().ToList();
            }
        } catch { }
    }
    string RestartFile { get { return Path.Combine(dataDir, "restart.json"); } }
    List<Dictionary<string, object>> returning;
    /// What the window around this agent offers: its version, a newer one it knows of, and a way to be told to update.
    public string Version = "", Newer = "";
    public Action UpdateRequested;
    /// What the window answers about itself: a few of its settings and the line it is reached by (docs/PROTOCOL.md,
    /// "The program itself"). It is given the operation and returns the answer; an ArgumentException or an
    /// InvalidOperationException refuses, with words for the person. It is called while the agent is at work on its
    /// terminals, so it answers at once and does what takes time elsewhere.
    public Func<Dictionary<string, object>, Dictionary<string, object>> Computer;
    static readonly string[] ComputerActions = { "settings_read", "settings_change", "line_switch", "line_take", "line_keep" };
    /// Whether the relay has been given the answer to this operation.
    public bool Reported(string id) { lock (work) return reported.Contains(id); }
    /// Whether nothing has been typed or printed for a while and no tool says it is working: a moment to restart in.
    public bool Quiet(int seconds) {
        lock (work) {
            var since = DateTime.UtcNow.AddSeconds(-seconds);
            return lastInput < since && terminals.Values.Where(t => !t.Pty.Closed).All(t => t.Status != "busy" && t.Printed < since);
        }
    }
    /// Before a restart for an update: writes down which terminals are open and what each of them runs.
    public void KeepForRestart() {
        lock (work) lock (gate) {
            foreach (var t in terminals.Values) Seal(t);
            var open = terminals.Values.Where(t => !t.Pty.Closed).Select(t => new Dictionary<string, object> { { "id", t.Id }, { "tool", t.Tool }, { "dir", t.Dir }, { "folder", t.Folder }, { "session", t.Session }, { "seq", t.Seq }, { "cols", t.Pty.Cols }, { "rows", t.Pty.Rows } }).ToArray();
            try { File.WriteAllText(RestartFile, json.Serialize(new Dictionary<string, object> { { "instance", instance }, { "at", DateTime.UtcNow.ToString("o") }, { "terminals", open } }), new UTF8Encoding(false)); } catch { }
        }
    }
    // A terminal that was open before the restart: Claude Code and Codex go on with the conversation they had, a shell
    // starts afresh in its folder. Its output continues the numbering, so the relay and the phone take it as the same terminal.
    void Return(Dictionary<string, object> kept, Dictionary<string, object> prefs) {
        string id = Get(kept, "id"), tool = Get(kept, "tool"), dir = Get(kept, "dir"), session = Get(kept, "session");
        var dirs = AllDirs(prefs);
        string launcher = FindTool(tool);
        var live = new LiveTerminal { Id = id, Tool = tool, Dir = dir, Folder = dir.Length == 0 ? Get(kept, "folder") : "", Session = session, Seq = Convert.ToInt64(kept["seq"]) };
        string where = Place(live, dirs);
        if (!Regex.IsMatch(id, @"\A[a-f0-9]{32}\z") || terminals.ContainsKey(id) || launcher == null || where == null || !Directory.Exists(where)) return;
        live.Pending.Append("\r\n\x1b[2m" + (tool == "shell" ? "Remote CLI 已更新，这是重新打开的 PowerShell。" : session.Length > 0 ? "Remote CLI 已更新，正在接回原来的对话…" : "Remote CLI 已更新；原对话保存在电脑上，可从历史对话继续。") + "\x1b[0m\r\n");
        live.Pty = new PseudoTerminal(Command(tool, launcher, session, false, Get(prefs, "RemoteMaxMode"), false, tool == "codex" && SupportsStandalone(launcher), false), where,
            Math.Max(20, Math.Min(240, Convert.ToInt32(kept["cols"]))), Math.Max(6, Math.Min(100, Convert.ToInt32(kept["rows"]))), text => {
                lock (gate) { live.Pending.Append(text); live.Printed = DateTime.UtcNow; }
                Wake();
            });
        terminals[id] = live;
    }
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
    string instance = Guid.NewGuid().ToString("N");       // the same again after a restart for an update
    readonly Dictionary<string, LiveTerminal> terminals = new Dictionary<string, LiveTerminal>();
    readonly Dictionary<string, Dictionary<string, object>> completed = new Dictionary<string, Dictionary<string, object>>();
    readonly HashSet<string> reported = new HashSet<string>();
    readonly object gate = new object();
    HttpClient client;
    DateTime nextLogin = DateTime.MinValue;
    public sealed class LiveTerminal {
        public string Id, Tool, Dir, Session = "", Status = "", Title = ""; public PseudoTerminal Pty; public long Seq;
        // A terminal runs in a project (Dir). One that took on a conversation that was open in a folder that is no
        // project runs in that folder instead: it has no project ("") and says the folder.
        public string Folder = "";
        public DateTime Started = DateTime.UtcNow, ClosedSeen = DateTime.MinValue, Printed = DateTime.MinValue;
        public StringBuilder Pending = new StringBuilder();   // read from the program, not yet numbered
        public List<Dictionary<string, object>> Output = new List<Dictionary<string, object>>();
        // What the relay has been given, the most recent of it: a relay that has less is given it again.
        public List<Dictionary<string, object>> History = new List<Dictionary<string, object>>();
        public int HistorySize; public long Led;               // Led: the piece that was given the modes in front of it
        public List<KeyValuePair<int, bool>> Modes = new List<KeyValuePair<int, bool>>();    // in force before the first piece of History
        public string ModesRest = "";
    }
    // Terminals that ended, the last few: the phone lists them as recently used, at whichever relay the computer uses.
    readonly List<LiveTerminal> ended = new List<LiveTerminal>();
    public const int KeepRunning = 600000, KeepEnded = 120000, EndedMost = 12;
    public sealed class ComputerTakeover {
        public string Tool, Dir, Session, Launcher;         // Dir: the folder the window opens in
        public bool PhoneTerminal;
    }
    // Dir: the project a conversation belongs to. One that a program has open in a folder that is no project has none
    // ("") and says the folder instead (Folder): the phone can look at it, answer it and tell it things, and its folder
    // can be made a project from there.
    public sealed class SessionInfo { public string Id, Tool, Dir, Folder = "", Title, Status = "", Host = "", Origin = ""; public long Updated; public DateTime Created; public bool Live, CanTakeover, OwnershipKnown; public string TakeoverReason = "", Same = ""; }
    /// The conversations the tools' own hooks tell of (Reach.cs). The window around this agent may put the hooks into
    /// the tools' settings; whether or not it has, this listens for them.
    public readonly Reach Reach;
    /// What the owner said of single conversations from the phone: pinned, hidden (Marks.cs).
    public readonly Marks Marks;
    // A conversation as the phone is told of it: what the scan found, and where a hook has told more, how it stands
    // now, what it asks, what it last said, and what the phone can do to it ("say", "answer").
    object Listed(SessionInfo s) {
        var known = s.Live ? Reach.Of(s.Tool, s.Id) : null;
        string status = known != null ? known.Status : s.Status;
        return new { id = s.Id, tool = s.Tool, dir = s.Dir, folder = s.Folder, pinned = Marks.Is("pinned", s.Tool, s.Id), hidden = Marks.Is("hidden", s.Tool, s.Id), title = s.Title, updated = s.Updated, live = s.Live, host = s.Host, origin = s.Origin, status = status, can_takeover = s.CanTakeover, ownership_known = s.OwnershipKnown, takeover_reason = s.TakeoverReason,
            reach = known == null ? null : new[] { known.Sayable ? "say" : null, known.Status == "confirm" && known.Answerable ? "answer" : null }.Where(way => way != null).ToArray(),
            ask = known != null && known.Status == "confirm" ? new { id = known.AskId, text = known.AskText } : null, last = known != null ? known.Last : "" };
    }
    sealed class TitleCache { public long Stamp, Length; public string Title, Same; }
    readonly Dictionary<string, TitleCache> titles = new Dictionary<string, TitleCache>();
    readonly CodexSessions codexSessions = new CodexSessions();
    readonly Dictionary<string, string> codexFolders = new Dictionary<string, string>();
    Dictionary<string, string> codexNames = new Dictionary<string, string>();
    long codexNamesStamp;
    List<SessionInfo> sessions = new List<SessionInfo>();
    List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
    readonly Dictionary<string, string> claudeFolders = new Dictionary<string, string>();
    string sentSessions = ""; DateTime sessionsSent = DateTime.MinValue;
    // Released when a program wrote to its terminal or the relay holds a new operation: the next report goes out at once.
    readonly SemaphoreSlim wake = new SemaphoreSlim(0);
    bool soon;
    // Terminals and finished operations are worked on by the report loop and by the held request; one at a time.
    readonly object work = new object();
    DateTime lastInput = DateTime.MinValue, lastScan = DateTime.MinValue, toolsAt = DateTime.MinValue;
    string[] tools = new string[0];
    void Wake() { try { if (wake.CurrentCount == 0) wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }
    DateTime nextScan = DateTime.MinValue, lastActivity = DateTime.MinValue;
    const string Uuid = @"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ProcessEntry {
        public uint size, usage, processId; public IntPtr heap; public uint module, threads, parentId; public int priority; public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string file;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")] static extern bool CloseSnapshot(IntPtr handle);
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
    static string ReadPart(string path, bool tail, int limit) {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            if (tail && stream.Length > limit) stream.Seek(-limit, SeekOrigin.End);
            byte[] bytes = new byte[(int)Math.Min(limit, stream.Length - stream.Position)]; int read = 0, n;
            while (read < bytes.Length && (n = stream.Read(bytes, read, bytes.Length - read)) > 0) read += n;
            return Encoding.UTF8.GetString(bytes, 0, read);
        }
    }
    // The value of a JSON string field, found without parsing lines that may be megabytes long or cut off.
    static string JsonText(string text, string field, bool last) {
        var matches = Regex.Matches(text, field + @"\s*""((?:[^""\\]|\\.)*)""");
        if (matches.Count == 0) return "";
        try { return new JavaScriptSerializer().Deserialize<string>("\"" + matches[last ? matches.Count - 1 : 0].Groups[1].Value + "\"") ?? ""; } catch { return ""; }
    }
    public static string Tidy(string title) {
        title = Regex.Replace(Regex.Replace(title ?? "", @"<[^>]{1,40}>", " "), @"[\s\p{C}]+", " ").Trim();
        if (title.Length > 60) title = title.Substring(0, Char.IsHighSurrogate(title[58]) ? 58 : 59) + "…";
        return title;
    }
    // The name the tool itself shows for a conversation; without one, what the owner last asked.
    public static string ClaudeTitleOf(string path) { string same; return ClaudeTitleOf(path, out same); }
    // same tells copies of one conversation apart from others: Claude Code writes a branch or a second run of the
    // same request as a file of its own. It is the name where the conversation has one, else what it began with;
    // the last request alone ("continue") is shared by conversations that have nothing to do with each other.
    public static string ClaudeTitleOf(string path, out string same) {
        string tail = ReadPart(path, true, 256 * 1024);
        string title = JsonText(tail, @"""customTitle"":", true);
        if (title.Length == 0) title = JsonText(tail, @"""aiTitle"":", true);
        same = Tidy(title);
        if (title.Length > 0) return same;
        title = JsonText(tail, @"""lastPrompt"":", true);
        string head = ReadPart(path, false, 768 * 1024);
        string began = JsonText(head, @"""role"":""user"",""content"":", false);
        if (began.Length == 0) began = JsonText(head, @"""operation"":""enqueue"",[^\n]{0,200}?""content"":", false);
        same = began.Length > 0 ? "began " + Tidy(began) : "";
        return Tidy(title.Length > 0 ? title : began);
    }
    string ClaudeTitle(FileInfo file, out string same) {
        TitleCache cached; same = "";
        if (titles.TryGetValue(file.FullName, out cached) && cached.Stamp == file.LastWriteTimeUtc.Ticks && cached.Length == file.Length) { same = cached.Same; return cached.Title; }
        string title;
        try { title = ClaudeTitleOf(file.FullName, out same); } catch (IOException) { if (cached != null) same = cached.Same; return cached != null ? cached.Title : ""; } catch (UnauthorizedAccessException) { return ""; }
        titles[file.FullName] = new TitleCache { Stamp = file.LastWriteTimeUtc.Ticks, Length = file.Length, Title = title, Same = same };
        return title;
    }
    // One conversation is listed once: of its copies, those a program has open, else the one used last.
    public static List<SessionInfo> Single(IEnumerable<SessionInfo> found) {
        return found.GroupBy(s => s.Same.Length > 0 ? s.Tool + "\n" + s.Dir + "\n" + s.Same : s.Id)
            .SelectMany(copies => copies.Any(s => s.Live) ? copies.Where(s => s.Live) : copies.OrderByDescending(s => s.Updated).Take(1)).ToList();
    }
    // Where Claude Code keeps its conversations and the notes of the programs that have them open: its own folder
    // in the person's home, or the one CLAUDE_CONFIG_DIR names, as Claude Code itself reads it.
    public static string ClaudeHome {
        get {
            string named = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return String.IsNullOrWhiteSpace(named) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : named;
        }
    }
    public static string ProjectFolder(string path) { return Regex.Replace(path, "[^a-zA-Z0-9]", "-"); }
    // A folder that is no project is spoken of to the phone unless it is one of the system's own for passing things.
    static bool Elsewhere(string folder) { return folder.Length >= 3 && folder.IndexOf(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase) < 0 && !folder.EndsWith(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase); }
    /// The folder a terminal runs in: its project's, or its own where it has no project. Null when that is gone.
    static string Place(LiveTerminal terminal, Dictionary<string, string> dirs) {
        string path;
        if (terminal.Dir.Length == 0) return terminal.Folder.Length > 0 ? terminal.Folder : null;
        return dirs.TryGetValue(terminal.Dir, out path) ? path : null;
    }
    static bool SamePath(string a, string b) { return String.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
    static long Milliseconds(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    // Reading the conversation lists can take seconds. The reports start it beside themselves, with the terminals as
    // they were at that moment, so that no key and no line of output waits for it; one reading at a time.
    readonly object scanGate = new object();
    int scanning;
    public List<SessionInfo> Scan(Dictionary<string, string> dirs, LiveTerminal[] running = null) { lock (scanGate) return ScanAll(dirs, running ?? terminals.Values.ToArray()); }
    // Conversations of the allowed folders: what each tool saved, and which of them a program on this computer has open.
    List<SessionInfo> ScanAll(Dictionary<string, string> dirs, LiveTerminal[] running) {
        var found = new List<SessionInfo>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var open = new Dictionary<string, ClaudeSessions.Owner>();
        var seen = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);   // folders that are not projects
        Action<string, string, DateTime, bool> note = delegate(string folder, string tool, DateTime at, bool live) {
            folder = (folder ?? "").TrimEnd('\\', '/');
            if (!Elsewhere(folder) || dirs.Values.Any(d => SamePath(d, folder))) return;
            Dictionary<string, object> item;
            if (!seen.TryGetValue(folder, out item)) seen[folder] = item = new Dictionary<string, object> { { "path", folder }, { "name", Tidy(Path.GetFileName(folder)) }, { "updated", 0L }, { "live", false }, { "tools", "" } };
            if (Milliseconds(at) > (long)item["updated"]) item["updated"] = Milliseconds(at);
            if (live) item["live"] = true;
            if (((string)item["tools"]).IndexOf(tool, StringComparison.Ordinal) < 0) item["tools"] = ((string)item["tools"] + " " + tool).Trim();
        };
        try {
            var parents = Parents();
            foreach (string file in Directory.GetFiles(Path.Combine(ClaudeHome, "sessions"), "*.json")) {
                try {
                    var source = ClaudeSessions.Inspect(file);
                    if (!source.Live) continue;
                    int pid = source.Pid; string session = source.Session;
                    LiveTerminal owner = null; int at = pid;
                    for (int depth = 0; depth < 12 && owner == null; depth++) {
                        owner = running.FirstOrDefault(t => !t.Pty.Closed && t.Pty.Id == at);
                        int parent; if (!parents.TryGetValue(at, out parent) || parent == at || parent == 0) break; at = parent;
                    }
                    if (owner != null) { owner.Session = session; owner.Status = source.Status; }
                    else { open[session] = source; note(source.Dir, "claude", File.GetLastWriteTimeUtc(file), true); }
                } catch { }
            }
        } catch { }
        foreach (var dir in dirs) {
            try {
                var folder = new DirectoryInfo(Path.Combine(ClaudeHome, "projects", ProjectFolder(dir.Value)));
                if (!folder.Exists) continue;
                var recent = folder.GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
                var recentNames = new HashSet<string>(recent.Take(15).Select(f => f.Name));
                foreach (FileInfo file in recent.Where(f => recentNames.Contains(f.Name) || open.ContainsKey(Path.GetFileNameWithoutExtension(f.Name)))) {
                    string id = Path.GetFileNameWithoutExtension(file.Name), title, same;
                    if (!Regex.IsMatch(id, Uuid) || (title = ClaudeTitle(file, out same)).Length == 0) continue;
                    ClaudeSessions.Owner source; bool live = open.TryGetValue(id, out source);
                    found.Add(new SessionInfo { Id = id, Tool = "claude", Dir = dir.Key, Title = title, Updated = Milliseconds(file.LastWriteTimeUtc), Created = file.CreationTimeUtc, Live = live, Host = live ? source.Host : "", Origin = live ? source.Origin : "", CanTakeover = live && source.CanTakeover, OwnershipKnown = !live || source.CanTakeover || source.Host == "remote", Status = live ? source.Status : "", TakeoverReason = live ? source.Reason : "", Same = same });
                }
            } catch { }
        }
        // Conversations Claude Code has open in folders that are no projects: listed with their folder.
        foreach (var held in open.Where(o => !found.Any(s => s.Tool == "claude" && s.Id == o.Key))) {
            try {
                string where = (held.Value.Dir ?? "").TrimEnd('\\', '/'), same;
                if (!Elsewhere(where) || dirs.Values.Any(d => SamePath(d, where)) || !Regex.IsMatch(held.Key, Uuid)) continue;
                var file = new FileInfo(Path.Combine(ClaudeHome, "projects", ProjectFolder(where), held.Key + ".jsonl"));
                string title = file.Exists ? ClaudeTitle(file, out same) : "";
                if (title.Length == 0) continue;        // nothing has been said in it yet
                var source = held.Value;
                found.Add(new SessionInfo { Id = held.Key, Tool = "claude", Dir = "", Folder = where, Title = title, Updated = Milliseconds(file.LastWriteTimeUtc), Created = file.CreationTimeUtc, Live = true, Host = source.Host, Origin = source.Origin, CanTakeover = source.CanTakeover, OwnershipKnown = source.CanTakeover || source.Host == "remote", Status = source.Status, TakeoverReason = source.Reason, Same = "" });
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
        }
        // and the ones a terminal of the phone's has taken on from there: listed so that the terminal keeps the conversation's name and marks
        foreach (LiveTerminal own in running.Where(t => t.Tool == "claude" && t.Folder.Length > 0 && t.Session.Length > 0 && !t.Pty.Closed && !found.Any(s => s.Tool == "claude" && s.Id == t.Session))) {
            try {
                string same;
                var file = new FileInfo(Path.Combine(ClaudeHome, "projects", ProjectFolder(own.Folder), own.Session + ".jsonl"));
                string title = file.Exists ? ClaudeTitle(file, out same) : "";
                if (title.Length > 0) found.Add(new SessionInfo { Id = own.Session, Tool = "claude", Dir = "", Folder = own.Folder, Title = title, Updated = Milliseconds(file.LastWriteTimeUtc), Created = file.CreationTimeUtc, Live = false, OwnershipKnown = true, Same = "" });
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
        }
        try {
            // Other folders Claude Code has worked in: its folder names lose characters, the real path is inside a conversation.
            foreach (var folder in new DirectoryInfo(Path.Combine(ClaudeHome, "projects")).GetDirectories()
                    .Select(d => new { d.Name, Newest = d.GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() })
                    .Where(d => d.Newest != null).OrderByDescending(d => d.Newest.LastWriteTimeUtc).Take(25)) {
                string path;
                if (!claudeFolders.TryGetValue(folder.Name, out path)) {
                    try { path = JsonText(ReadPart(folder.Newest.FullName, false, 1024 * 1024), @"""cwd"":", false); } catch (IOException) { continue; }
                    if (path.Length > 0) claudeFolders[folder.Name] = path;
                }
                if (path.Length > 0 && ProjectFolder(path) == folder.Name) note(path, "claude", folder.Newest.LastWriteTimeUtc, false);
            }
        } catch { }
        try {
            var index = new FileInfo(Path.Combine(CodexSessions.Home, "session_index.jsonl"));
            if (index.Exists && index.LastWriteTimeUtc.Ticks != codexNamesStamp) {
                var names = new Dictionary<string, string>();
                foreach (string line in ReadPart(index.FullName, true, 2 * 1024 * 1024).Split('\n')) {
                    try { var row = json.Deserialize<Dictionary<string, object>>(line); if (Get(row, "id").Length > 0) names[Get(row, "id")] = Get(row, "thread_name"); } catch { }
                }
                codexNames = names; codexNamesStamp = index.LastWriteTimeUtc.Ticks;
            }
            var root = new DirectoryInfo(Path.Combine(CodexSessions.Home, "sessions"));
            var counts = new Dictionary<string, int>();
            codexSessions.Refresh();
            var ownerParents = Parents();
            if (root.Exists) foreach (FileInfo file in root.GetFiles("rollout-*.jsonl", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).Take(150)) {
                string name = Path.GetFileNameWithoutExtension(file.Name), id = name.Length > 36 ? name.Substring(name.Length - 36) : "", folder;
                if (!Regex.IsMatch(id, Uuid)) continue;
                if (!codexFolders.TryGetValue(file.FullName, out folder)) {
                    string head;
                    try { head = ReadPart(file.FullName, false, 16 * 1024); } catch (IOException) { continue; }
                    // A helper that a conversation started for itself is part of that conversation, not one to continue.
                    codexFolders[file.FullName] = folder = Regex.IsMatch(head, @"""parent_thread_id""\s*:\s*""") ? "" : JsonText(head, @"""cwd"":", false);
                }
                if (folder.Length == 0) continue;
                string dir = dirs.Where(d => SamePath(d.Value, folder)).Select(d => d.Key).FirstOrDefault();
                var owner = codexSessions.Get(id);
                bool live = owner.Live;
                bool phone = owner.Pid > 0 && CodexSessions.Descends(owner.Pid, running.Select(t => t.Pty.Id), ownerParents);
                string title; codexNames.TryGetValue(id, out title); title = Tidy(title);
                if (dir == null) {
                    note(folder, "codex", file.LastWriteTimeUtc, live);
                    // one that a program has open in a folder that is no project is listed with its folder
                    string where = folder.TrimEnd('\\', '/');
                    // (one that a terminal of the phone's has taken on from there is listed too, as that terminal's)
                    bool taken = running.Any(t => t.Tool == "codex" && t.Session == id && t.Folder.Length > 0 && !t.Pty.Closed);
                    if ((live && !phone || taken) && Elsewhere(where)) found.Add(new SessionInfo { Id = id, Tool = "codex", Dir = "", Folder = where, Title = title.Length > 0 ? title : "Codex 对话 " + file.CreationTime.ToString("MM-dd HH:mm"), Updated = Milliseconds(file.LastWriteTimeUtc), Created = file.CreationTimeUtc, Live = live && !phone, Host = live && !phone ? owner.Host : "", Origin = live && !phone ? owner.Origin : "", OwnershipKnown = owner.Known, CanTakeover = live && owner.CanTakeover && !phone, TakeoverReason = owner.Reason, Same = "" });
                    continue;
                }
                int count; counts.TryGetValue(dir, out count);
                if (!live && count >= 15) continue;
                if (!live) counts[dir] = count + 1;
                found.Add(new SessionInfo { Id = id, Tool = "codex", Dir = dir, Title = title.Length > 0 ? title : "Codex 对话 " + file.CreationTime.ToString("MM-dd HH:mm"), Updated = Milliseconds(file.LastWriteTimeUtc), Created = file.CreationTimeUtc, Live = live, Host = live ? owner.Host : "", Origin = live ? owner.Origin : "", OwnershipKnown = owner.Known, CanTakeover = live && owner.CanTakeover && !phone, TakeoverReason = owner.Reason, Same = title });
            }
        } catch { }
        // Associate a phone terminal only with the verified writer process in its own process tree.
        var codexParents = Parents();
        foreach (LiveTerminal t in running.Where(t => t.Tool == "codex" && t.Session.Length == 0 && !t.Pty.Closed)) {
            var mine = found.Where(s => s.Tool == "codex" && s.Dir == t.Dir && !running.Any(o => o.Session == s.Id)
                && codexSessions.Get(s.Id).Pid > 0 && CodexSessions.Descends(codexSessions.Get(s.Id).Pid, new[] { t.Pty.Id }, codexParents)).ToArray();
            if (mine.Length == 1) t.Session = mine[0].Id;
        }
        // the ones a program has open and the ones their owner pinned are listed whatever their age
        sessions = Single(found).OrderByDescending(s => s.Live || Marks.Is("pinned", s.Tool, s.Id)).ThenByDescending(s => s.Updated).Take(200).ToList();
        candidates = seen.Values.Where(c => { try { return Directory.Exists((string)c["path"]); } catch { return false; } })
            .OrderByDescending(c => (bool)c["live"]).ThenByDescending(c => (long)c["updated"]).Take(12).ToList();
        if (claudeFolders.Count > 500) claudeFolders.Clear();
        if (titles.Count > 600) titles.Clear();
        if (codexFolders.Count > 3000) codexFolders.Clear();
        return sessions;
    }
    // A program says once that it draws on the alternate screen, reads the mouse or takes pasted text in brackets.
    // When the output that said so is dropped from the history kept here, the switch is remembered, in the order
    // the relay keeps them (relay.py, _switch), and written in front of a history that begins later.
    static readonly Regex Switch = new Regex(@"\x1b\[\?([0-9;]{1,40})([hl])|\x1bc"), SwitchCut = new Regex(@"\x1b(?:\[(?:\?[0-9;]{0,40})?)?\z");
    static readonly HashSet<int> Switches = new HashSet<int> { 1, 7, 9, 25, 47, 1000, 1002, 1003, 1004, 1005, 1006, 1007, 1015, 1016, 1047, 1049, 2004 };
    static void Follow(LiveTerminal t, string data) {
        data = t.ModesRest + data;
        Match cut = SwitchCut.Match(data);
        int end = cut.Success ? cut.Index : data.Length;
        foreach (Match found in Switch.Matches(data.Substring(0, end))) {
            if (!found.Groups[2].Success) { t.Modes.Clear(); continue; }      // a full reset of the terminal
            foreach (string number in found.Groups[1].Value.Split(';')) {
                int mode;
                if (!Int32.TryParse(number, out mode) || !Switches.Contains(mode)) continue;
                t.Modes.RemoveAll(m => m.Key == mode);
                t.Modes.Add(new KeyValuePair<int, bool>(mode, found.Groups[2].Value == "h"));
            }
        }
        t.ModesRest = data.Substring(end);
    }
    static long Number(Dictionary<string, object> piece) { return Convert.ToInt64(piece["seq"]); }
    /// What a relay says it has of a terminal's output, up to the piece `have`. Pieces it has are kept as history,
    /// the last `keep` characters of them. A relay that has less than it was given (the computer turned to another
    /// relay, or this one lost what it had not written down) is given the history again from where it is; when the
    /// history begins later than that, its first piece says so and carries the terminal modes in force (and the
    /// beginning of a sequence that the piece before it ended in the middle of).
    /// Returns whether history goes out again.
    public static bool Settle(LiveTerminal t, long have, int keep) {
        int done = 0;
        while (done < t.Output.Count && Number(t.Output[done]) <= have) done++;
        foreach (var piece in t.Output.Take(done)) { piece.Remove("restart"); t.History.Add(piece); t.HistorySize += ((string)piece["data"]).Length; }
        t.Output.RemoveRange(0, done);
        long next = t.Output.Count > 0 ? Number(t.Output[0]) : t.Seq + 1;
        int from = t.History.FindIndex(piece => Number(piece) > have);
        bool again = have < next - 1 && from >= 0;
        if (again) {
            var pieces = t.History.GetRange(from, t.History.Count - from);
            t.History.RemoveRange(from, pieces.Count);
            foreach (var piece in pieces) { piece["old"] = true; t.HistorySize -= ((string)piece["data"]).Length; }
            t.Output.InsertRange(0, pieces);
            var first = pieces[0];
            if (Number(first) != have + 1) {
                if (t.Led != Number(first)) { first["data"] = String.Concat(t.Modes.Select(m => "\x1b[?" + m.Key + (m.Value ? "h" : "l"))) + t.ModesRest + (string)first["data"]; t.Led = Number(first); }
                first["restart"] = true;
            }
        }
        int drop = 0, size = t.HistorySize;
        while (size > keep && drop < t.History.Count - 1) { string data = (string)t.History[drop++]["data"]; Follow(t, data); size -= data.Length; }
        t.History.RemoveRange(0, drop); t.HistorySize = size;
        return again;
    }
    // Many small reads become few numbered pieces, so a busy screen is not held back by the per-request piece limit.
    public static void Seal(LiveTerminal t) {
        while (t.Pending.Length > 0) {
            int n = Math.Min(12000, t.Pending.Length);
            if (n < t.Pending.Length && Char.IsHighSurrogate(t.Pending[n - 1])) n--;
            t.Output.Add(new Dictionary<string, object> { { "terminal", t.Id }, { "seq", ++t.Seq }, { "data", t.Pending.ToString(0, n) } });
            t.Pending.Remove(0, n);
        }
    }
    static string Get(IDictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : ""; }
    static IEnumerable<object> List(IDictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>() : new object[0]; }
    // Settings and state live in %LOCALAPPDATA%\RemoteCli; REMOTECLI_DATA points a second copy (or a test) elsewhere.
    public static string DefaultData { get { string wanted = Environment.GetEnvironmentVariable("REMOTECLI_DATA"); return !String.IsNullOrWhiteSpace(wanted) ? wanted : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCli"); } }
    // config.json: Server (address of the relay), RemoteEnabled, RemoteMaxMode (full, edit or read) and RemoteDirs ("name=folder").
    Dictionary<string, object> Preferences() {
        var prefs = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dataDir, "config.json"), Encoding.UTF8));
        string server = Get(prefs, "Server").TrimEnd('/');
        if (server != Origin) { if (Origin.Length > 0) Reset(); Origin = server; }       // another relay: sign in there at once
        return prefs;
    }
    static string FindTool(string name) {
        if (name == "shell") return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Concat(new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm") }))
            foreach (string ext in new[] { ".exe", ".cmd" }) { try { string path = Path.Combine(folder.Trim(), name + ext); if (File.Exists(path)) return path; } catch { } }
        return null;
    }
    static Dictionary<string, string> Dirs(Dictionary<string, object> prefs) {
        var dirs = new Dictionary<string, string>();
        foreach (object raw in List(prefs, "RemoteDirs")) { string line = Convert.ToString(raw); int at = line.IndexOf('='); if (at <= 0) continue; string path = line.Substring(at + 1).Trim(), name = line.Substring(0, at).Trim(); if (Directory.Exists(path)) dirs[name] = Path.GetFullPath(path); }
        return dirs;
    }
    public List<Dictionary<string, object>> Candidates { get { return candidates; } }
    string ProjectsFile { get { return Path.Combine(dataDir, "terminal-projects.json"); } }
    // Folders added from the phone, beside the ones set in the desktop widget's settings.
    List<KeyValuePair<string, string>> OwnProjects() {
        var own = new List<KeyValuePair<string, string>>();
        try {
            if (File.Exists(ProjectsFile)) foreach (var item in json.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(ProjectsFile, Encoding.UTF8)))
                if (Get(item, "name").Length > 0 && Get(item, "path").Length > 0 && !own.Any(p => p.Key == Get(item, "name"))) own.Add(new KeyValuePair<string, string>(Get(item, "name"), Get(item, "path")));
        } catch (IOException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
        return own;
    }
    void SaveProjects(List<KeyValuePair<string, string>> own) {
        string temporary = ProjectsFile + ".tmp";
        File.WriteAllText(temporary, json.Serialize(own.Select(p => new Dictionary<string, object> { { "name", p.Key }, { "path", p.Value } }).ToArray()), new UTF8Encoding(false));
        if (File.Exists(ProjectsFile)) File.Replace(temporary, ProjectsFile, null); else File.Move(temporary, ProjectsFile);
    }
    public Dictionary<string, string> AllDirs(Dictionary<string, object> prefs) {
        var dirs = Dirs(prefs);
        foreach (var p in OwnProjects()) { try { if (!dirs.ContainsKey(p.Key) && Directory.Exists(p.Value)) dirs[p.Key] = p.Value; } catch { } }
        return dirs;
    }
    public static string NormalFolder(string path) {
        path = (path ?? "").Trim();
        if (path.Length < 3 || path.Length > 240 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOfAny(new[] { '*', '?' }) >= 0 || !Regex.IsMatch(path, @"\A[A-Za-z]:[\\/]"))
            throw new ArgumentException("请填写电脑上的完整文件夹路径，例如 E:\\项目\\组会");
        path = Path.GetFullPath(path).TrimEnd('\\');
        return path.Length == 2 ? path + "\\" : path;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, StringBuilder path, uint size, uint flags);
    // Where a path really is, after every link and junction on the way has been followed.
    static string RealPath(string path) {
        using (var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
            if (handle.IsInvalid) throw new FileNotFoundException();
            var name = new StringBuilder(2048);
            uint length = GetFinalPathNameByHandleW(handle, name, 2048, 0);
            if (length == 0 || length >= 2048) throw new IOException();
            string real = name.ToString();
            return (real.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + real.Substring(8) : real.StartsWith(@"\\?\", StringComparison.Ordinal) ? real.Substring(4) : real).TrimEnd('\\');
        }
    }
    public const int FilePiece = 737280;        // a multiple of three bytes, so that the pieces can be put together as text
    // What a project folder holds, or a piece of one of its files, for the file pages on the phone. Nothing outside the
    // project's own folder is given out: a link inside it that leads elsewhere on the computer is refused.
    public static Dictionary<string, object> Files(string root, string relative, string action, long offset) {
        try {
            root = Path.GetFullPath(root).TrimEnd('\\');
            relative = (relative ?? "").Replace('/', '\\').Trim('\\');
            string full = relative.Length == 0 ? root : Path.GetFullPath(root + "\\" + relative);       // also right for a whole drive
            if (full != root && !full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("路径不在项目文件夹内");
            string realRoot = RealPath(root + "\\"), real = RealPath(full.Length == root.Length ? root + "\\" : full);
            if (!String.Equals(real, realRoot, StringComparison.OrdinalIgnoreCase) && !real.StartsWith(realRoot + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("这个位置链接到项目文件夹之外，不能打开");
            string shown = full.Length > root.Length ? full.Substring(root.Length + 1).Replace('\\', '/') : "";
            if (action == "file_list") {
                var folder = new DirectoryInfo(full);
                if (!folder.Exists) throw new ArgumentException("这不是文件夹");
                var entries = new List<object>(); bool more = false;
                foreach (FileSystemInfo item in folder.EnumerateFileSystemInfos()) {
                    if (entries.Count >= 3000) { more = true; break; }
                    bool inner = (item.Attributes & FileAttributes.Directory) != 0;
                    entries.Add(new Dictionary<string, object> { { "name", item.Name }, { "dir", inner }, { "size", inner ? 0 : ((FileInfo)item).Length }, { "modified", Milliseconds(item.LastWriteTimeUtc) },
                        { "hidden", (item.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || item.Name.StartsWith(".", StringComparison.Ordinal) } });
                }
                return new Dictionary<string, object> { { "path", shown }, { "entries", entries }, { "more", more } };
            }
            if (Directory.Exists(full)) throw new ArgumentException("这是文件夹");
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                if (offset < 0 || offset > stream.Length) throw new ArgumentException("位置无效");
                stream.Seek(offset, SeekOrigin.Begin);
                byte[] bytes = new byte[(int)Math.Min(FilePiece, stream.Length - offset)]; int read = 0, n;
                while (read < bytes.Length && (n = stream.Read(bytes, read, bytes.Length - read)) > 0) read += n;
                return new Dictionary<string, object> { { "path", shown }, { "size", stream.Length }, { "modified", Milliseconds(File.GetLastWriteTimeUtc(full)) }, { "offset", offset },
                    { "data", Convert.ToBase64String(bytes, 0, read) }, { "end", offset + read >= stream.Length } };
            }
        }
        catch (FileNotFoundException) { throw new ArgumentException("文件不存在"); }
        catch (DirectoryNotFoundException) { throw new ArgumentException("文件夹不存在"); }
        catch (UnauthorizedAccessException) { throw new ArgumentException("没有权限读取"); }
        catch (PathTooLongException) { throw new ArgumentException("路径太长"); }
        catch (NotSupportedException) { throw new ArgumentException("路径无效"); }
        catch (IOException) { throw new ArgumentException("读取失败，文件可能正被占用"); }
    }
    // The last things said in a saved conversation, oldest first, for the phone to look at before anything is done to
    // the program that has it open: what the person asked, what the tool answered, and the name of each thing it did.
    // What the tools put into a conversation for themselves (reminders, command output, results) is left out.
    public static Dictionary<string, object> Said(string path, string tool) {
        const int limit = 768 * 1024, most = 40, chars = 1500;
        var reader = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        var said = new List<Dictionary<string, object>>();
        Func<Dictionary<string, object>, string, object> at = (d, k) => { object v; return d != null && d.TryGetValue(k, out v) ? v : null; };
        Action<string, string> add = (role, text) => {
            text = role == "tool" ? Regex.Replace(text ?? "", @"\s+", " ").Trim() : (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > chars) text = text.Substring(0, Char.IsHighSurrogate(text[chars - 2]) ? chars - 2 : chars - 1) + "…";
            said.Add(new Dictionary<string, object> { { "role", role }, { "text", text } });
        };
        Func<object, object, string> brief = (name, given) => {
            var input = given as Dictionary<string, object>; string about = "";
            foreach (string key in new[] { "command", "file_path", "path", "pattern", "description", "query", "url" }) { about = at(input, key) as string ?? ""; if (about.Length > 0) break; }
            return (Convert.ToString(name) + (about.Length > 0 ? "：" + (about.Length > 160 ? about.Substring(0, 160) : about) : "")).Trim();
        };
        foreach (string line in ReadPart(path, true, limit).Split('\n')) {
            if (!line.StartsWith("{", StringComparison.Ordinal)) continue;
            Dictionary<string, object> row;
            try { row = reader.Deserialize<Dictionary<string, object>>(line); } catch (Exception) { continue; }
            string type = at(row, "type") as string;
            if (tool == "claude") {
                var message = at(row, "message") as Dictionary<string, object>;
                if ((type != "user" && type != "assistant") || true.Equals(at(row, "isSidechain")) || true.Equals(at(row, "isMeta")) || message == null) continue;
                object content = at(message, "content");
                IEnumerable parts = content is string ? new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", content } } } : content as IEnumerable;
                if (parts == null) continue;
                foreach (object item in parts) {
                    var part = item as Dictionary<string, object>; string kind = at(part, "type") as string, text = at(part, "text") as string;
                    if (kind == "text" && text != null) { if (type == "assistant" || !text.TrimStart().StartsWith("<", StringComparison.Ordinal)) add(type, text); }
                    else if (kind == "tool_use" && type == "assistant") add("tool", brief(at(part, "name"), at(part, "input")));
                }
            } else {
                var payload = at(row, "payload") as Dictionary<string, object>;
                if (type != "response_item" || payload == null) continue;
                string kind = at(payload, "type") as string, role = at(payload, "role") as string;
                if (kind == "message" && (role == "user" || role == "assistant")) {
                    var parts = at(payload, "content") as IEnumerable;
                    if (parts == null || at(payload, "content") is string) continue;
                    foreach (object item in parts) {
                        string text = at(item as Dictionary<string, object>, "text") as string;
                        if (text != null && (role == "assistant" || !text.TrimStart().StartsWith("<", StringComparison.Ordinal))) add(role, text);
                    }
                }
                else if (kind == "function_call" || kind == "custom_tool_call" || kind == "local_shell_call") add("tool", brief(at(payload, "name") ?? "shell", null));
            }
        }
        var file = new FileInfo(path);
        return new Dictionary<string, object> { { "messages", said.Skip(Math.Max(0, said.Count - most)).ToList() }, { "more", said.Count > most || file.Length > limit }, { "updated", Milliseconds(file.LastWriteTimeUtc) } };
    }
    Dictionary<string, object> ReadConversation(Dictionary<string, object> op, Dictionary<string, object> prefs) {
        string session = Get(op, "session"), tool = Get(op, "tool"), dir = Get(op, "dir"), root;
        var saved = sessions.FirstOrDefault(s => s.Id == session && s.Tool == tool && s.Dir == dir);
        if (saved == null || !Regex.IsMatch(session, Uuid)) throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
        // one that is open in a folder that is no project is read where it is
        if (dir.Length == 0 && saved.Folder.Length > 0) root = saved.Folder;
        else if (!AllDirs(prefs).TryGetValue(dir, out root)) throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
        try {
            string file = tool == "claude"
                ? Path.Combine(ClaudeHome, "projects", ProjectFolder(root), session + ".jsonl")
                : Directory.GetFiles(Path.Combine(CodexSessions.Home, "sessions"), "rollout-*" + session + ".jsonl", SearchOption.AllDirectories).FirstOrDefault();
            if (file == null || !File.Exists(file)) throw new FileNotFoundException();
            var result = Said(file, tool); result["title"] = saved.Title;
            return result;
        }
        catch (IOException) { throw new ArgumentException("读不到这个对话的内容"); }
        catch (UnauthorizedAccessException) { throw new ArgumentException("读不到这个对话的内容"); }
    }
    public void ChangeProjects(Dictionary<string, object> op, Dictionary<string, object> prefs, string action) {
        var settings = Dirs(prefs); var own = OwnProjects();
        string name = Tidy(Get(op, "name"));
        if (action == "project_add") {
            string path = NormalFolder(Get(op, "path"));
            if (settings.Values.Concat(own.Select(p => p.Value)).Any(p => SamePath(p, path))) throw new ArgumentException("这个文件夹已经在项目里");
            if (own.Count >= 30) throw new InvalidOperationException("项目已达 30 个，请先移除不用的");
            if (!Directory.Exists(path)) {
                if (Get(op, "create") != "True") throw new ArgumentException("电脑上没有这个文件夹");
                string parent = Path.GetDirectoryName(path);
                if (String.IsNullOrEmpty(parent) || !Directory.Exists(parent)) throw new ArgumentException("上一级文件夹不存在，不能新建");
                Directory.CreateDirectory(path);
            }
            if (name.Length == 0) name = Tidy(Path.GetFileName(path));
            if (name.Length == 0) name = path;
            if (name.Length > 40) name = name.Substring(0, 40);
            string wanted = name;
            for (int n = 2; settings.ContainsKey(name) || own.Any(p => p.Key == name); n++) name = wanted + " " + n;
            own.Add(new KeyValuePair<string, string>(name, path));
        } else {
            int at = own.FindIndex(p => p.Key == name);
            if (at < 0) throw new ArgumentException(settings.ContainsKey(name) ? "这个项目写在电脑的配置文件里，请在电脑上修改" : "没有这个项目");
            if (action == "project_remove") {
                if (terminals.Values.Any(t => t.Dir == name && !t.Pty.Closed)) throw new InvalidOperationException("这个项目还有终端在运行，请先结束");
                own.RemoveAt(at);
            } else {
                string to = Tidy(Get(op, "to"));
                if (to.Length == 0 || to.Length > 40) throw new ArgumentException("名称需为 1 至 40 字");
                if (to != name && (settings.ContainsKey(to) || own.Any(p => p.Key == to))) throw new ArgumentException("已有同名项目");
                own[at] = new KeyValuePair<string, string>(to, own[at].Value);
                foreach (var t in terminals.Values.Concat(ended).Where(t => t.Dir == name)) t.Dir = to;
            }
        }
        SaveProjects(own);
        nextScan = DateTime.MinValue;
    }
    // End only the verified conversation writer before resuming the original session.
    void ClaudeExclusive(string session, bool takeover) {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parents = Parents();
        string folder = Path.Combine(ClaudeHome, "sessions");
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.GetFiles(folder, "*.json")) {
            var owner = ClaudeSessions.Inspect(file);
            if (!owner.Live || owner.Session != session) continue;
            var roots = terminals.Values.Select(t => t.Pty.Id).Concat(new[] { Process.GetCurrentProcess().Id });
            if (!takeover || !owner.CanTakeover || CodexSessions.Descends(owner.Pid, roots, parents)) throw new InvalidOperationException(owner.Reason);
            using (var process = Process.GetProcessById(owner.Pid)) {
                var current = ClaudeSessions.Inspect(file);
                if (!current.CanTakeover || current.Session != session || current.Pid != owner.Pid || current.Started != owner.Started || process.StartTime.ToUniversalTime().ToFileTimeUtc() != owner.Started) throw new InvalidOperationException("Claude Code 会话归属已变化，请刷新后重试");
                process.Kill();
                if (!process.WaitForExit(5000)) throw new InvalidOperationException("原 Claude Code 会话尚未结束，已取消接管");
            }
        }
    }
    public static string Command(string tool, string launcher, string session, bool history, string mode, bool fork = false, bool noDaemon = false, bool fresh = false) {
        if (tool != "claude" && tool != "codex" && tool != "shell") throw new ArgumentException("工具无效");
        if (launcher == null || launcher.IndexOfAny(new[] { '"', '%', '\r', '\n' }) >= 0) throw new ArgumentException("工具路径无效");
        if (!String.IsNullOrEmpty(session) && !Regex.IsMatch(session, @"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z")) throw new ArgumentException("历史会话编号无效");
        string args = Arguments(tool, session, history, mode, fork, noDaemon, fresh);
        return LaunchCommand(launcher, args);
    }
    public static string Arguments(string tool, string session, bool history, string mode, bool fork = false, bool noDaemon = false, bool fresh = false) {
        string args = "";
        if (tool == "claude") args = (session.Length > 0 ? (fresh ? " --session-id " : " --resume ") + session : history ? " --resume" : "") + (mode == "read" ? " --permission-mode plan" : mode == "edit" ? " --permission-mode acceptEdits" : "");
        if (tool == "codex") args = (session.Length > 0 ? (fork ? " fork " : " resume ") + session : history ? " resume --include-non-interactive" : "") + (noDaemon ? " --no-daemon" : "") + (mode == "read" ? " -s read-only" : mode == "edit" ? " -s workspace-write" : "");
        if (tool == "shell") args = " -NoLogo -NoExit";
        return args;
    }
    public static ProcessStartInfo ComputerProcess(ComputerTakeover takeover, string mode) {
        if (takeover == null || String.IsNullOrEmpty(takeover.Launcher)) throw new InvalidOperationException("电脑上没有找到这个工具");
        string args = Arguments(takeover.Tool, takeover.Session, false, mode, false, takeover.Tool == "codex", false);
        var info = new ProcessStartInfo();
        info.WorkingDirectory = takeover.Dir; info.UseShellExecute = false; info.CreateNoWindow = false;
        if (takeover.Launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = "/d /k \"\"" + takeover.Launcher + "\"" + args + "\"";
        } else { info.FileName = takeover.Launcher; info.Arguments = args.TrimStart(); }
        foreach (string key in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT" }) info.EnvironmentVariables.Remove(key);
        return info;
    }
    static string LaunchCommand(string launcher, string args) {
        if (launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return "\"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") + "\" /d /s /c \"\"" + launcher + "\"" + args + "\"";
        return "\"" + launcher + "\"" + args;
    }
    readonly Dictionary<string, bool> standaloneTools = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    bool SupportsStandalone(string launcher) {
        bool supported;
        if (standaloneTools.TryGetValue(launcher, out supported)) return supported;
        supported = false;
        try {
            string command = LaunchCommand(launcher, " --help"); int end = command.IndexOf('"', 1);
            using (var process = Process.Start(new ProcessStartInfo(command.Substring(1, end - 1), command.Substring(end + 1).Trim()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })) {
                Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(5000)) process.Kill();
                else supported = process.ExitCode == 0 && output.Result.Contains("--no-daemon");
            }
        } catch { }
        standaloneTools[launcher] = supported;
        return supported;
    }
    public static List<Dictionary<string, object>> OutputBatch(IEnumerable<Dictionary<string, object>> chunks) {
        // ANSI escapes expand in JSON. Bound encoded bytes, not the number of characters.
        var serializer = new JavaScriptSerializer();
        var batch = new List<Dictionary<string, object>>();
        int bytes = 0;
        foreach (var chunk in chunks) {
            int size = Encoding.UTF8.GetByteCount(serializer.Serialize(chunk)) + 1;
            if (bytes + size > 512 * 1024) break;
            batch.Add(chunk); bytes += size;
            if (batch.Count >= 150) break;
        }
        return batch;
    }
    // The address or the password changed: sign in again at once instead of after the usual pause.
    public void Reset() { nextLogin = DateTime.MinValue; client = null; sentSessions = ""; }
    /// Whether a relay of one's own can be used: reached at this address, and taking this password. Says nothing
    /// when it can, and otherwise what is wrong and what to do about it.
    public static string CheckRelay(string server, string password) {
        Func<string, string, HttpWebResponse> ask = (path, body) => {
            var request = (HttpWebRequest)WebRequest.Create(server + path);
            request.Timeout = 10000; request.ReadWriteTimeout = 10000; request.AllowAutoRedirect = false;
            if (body != null) {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.Method = "POST"; request.ContentType = "application/json"; request.ContentLength = bytes.Length;
                using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            try { return (HttpWebResponse)request.GetResponse(); }
            catch (WebException refused) { if (refused.Response is HttpWebResponse) return (HttpWebResponse)refused.Response; throw; }
        };
        try {
            using (var reply = ask("/api/session", null)) {
                string text; using (var reader = new StreamReader(reply.GetResponseStream(), Encoding.UTF8)) text = reader.ReadToEnd();
                if ((int)reply.StatusCode != 200 || !text.Contains("\"signed_in\"")) return "这个地址上没有 Remote CLI 中转，请核对地址和端口";
            }
            using (var reply = ask("/api/login", new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "password", password } }))) {
                int code = (int)reply.StatusCode;
                if (code == 200) return "";
                if (code == 401) return "中转不接受这个密码。请核对“密码”一栏，要填服务器上 sudo bash install.sh password 显示的那个";
                if (code == 429) return "密码错了太多次，中转暂时不再接受登录，请 15 分钟后再试";
                return "中转没有正常回应（" + code + "），请检查服务器上的中转和反向代理";
            }
        } catch (WebException error) {
            if (error.Status == WebExceptionStatus.TrustFailure) return "中转的 https 证书无效或已过期，请在服务器上更新证书";
            if (error.Status == WebExceptionStatus.SecureChannelFailure) return "和中转建立 https 连接失败，请检查服务器的证书和 TLS 设置";
            if (error.Status == WebExceptionStatus.NameResolutionFailure) return "找不到这个地址，请核对域名";
            if (error.Status == WebExceptionStatus.Timeout || error.Status == WebExceptionStatus.ConnectFailure) return "连不上中转。请确认服务器开着，端口已在防火墙或安全组放行";
            return "连不上中转：" + error.Message;
        } catch (Exception error) { return "连不上中转：" + error.Message; }
    }
    /// Same session inventory as the phone: tool, project, status, time, title, origin, host.
    public List<string[]> Running() {
        try {
            lock (work) {
                var active = terminals.Values.Where(t => t.Pty != null && !t.Pty.Closed).ToArray();
                var list = active.Select(t => new[] { t.Tool, t.Dir.Length > 0 ? t.Dir : Path.GetFileName(t.Folder), t.Status ?? "", t.Started.ToString("o"), sessions.Where(s => s.Id == t.Session).Select(s => s.Title).FirstOrDefault() ?? "", "手机 Remote CLI", "phone", t.Id }).ToList();
                // A writer lock is not evidence of a visible computer window. Only an
                // independently owned CLI belongs in the desktop activity list; shared
                // app-server and foreign/unknown locks remain available in the phone's
                // locked-history section for fork/retry actions.
                list.AddRange(sessions.Where(s => s.Live && s.Host == "cli" && !active.Any(t => t.Session == s.Id)).Select(s => new[] { s.Tool, s.Dir.Length > 0 ? s.Dir : Path.GetFileName(s.Folder), s.Status, s.Created.ToString("o"), s.Title, s.Origin, s.Host }));
                return list;
            }
        } catch (InvalidOperationException) { return null; }      // the list changed while it was read: the caller keeps what it showed
    }
    public ComputerTakeover TakeoverForComputer(string terminalId) {
        if (!Regex.IsMatch(terminalId ?? "", @"\A[a-f0-9]{32}\z")) throw new ArgumentException("终端编号无效");
        lock (work) {
            LiveTerminal live;
            if (!terminals.TryGetValue(terminalId, out live) || live.Pty == null || live.Pty.Closed) throw new InvalidOperationException("手机终端已经结束，请刷新活动列表");
            var prefs = Preferences();
            var dirs = AllDirs(prefs);
            string where = Place(live, dirs);
            if (where == null) throw new InvalidOperationException("这个项目已经从电脑端移除");
            if (live.Tool == "codex" && live.Session.Length == 0) { Scan(dirs); }
            string session = live.Session;
            if ((live.Tool == "claude" || live.Tool == "codex") && session.Length == 0) throw new InvalidOperationException("正在等待会话编号，请刷新活动后再接管");
            if (live.Tool == "codex") {
                var owner = CodexSessions.Inspect(session);
                if (!owner.Live || owner.Pid <= 0 || !CodexSessions.Descends(owner.Pid, new[] { live.Pty.Id }, Parents())) throw new InvalidOperationException("无法确认这个 Codex 进程属于手机终端，已取消接管");
            }
            if (live.Tool == "claude") {
                string folder = Path.Combine(ClaudeHome, "sessions");
                var source = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").Select(ClaudeSessions.Inspect).FirstOrDefault(s => s.Live && s.Session == session) : null;
                if (source == null || !CodexSessions.Descends(source.Pid, new[] { live.Pty.Id }, Parents())) throw new InvalidOperationException("无法确认这个 Claude Code 进程属于手机终端，已取消接管");
            }
            if (live.Tool != "claude" && live.Tool != "codex") {
                live.Pty.Dispose();
                return new ComputerTakeover { Tool = live.Tool, Dir = where, Session = "", Launcher = FindTool(live.Tool), PhoneTerminal = true };
            }
            live.Pty.Dispose();
            for (int n = 0; n < 60; n++) {
                if (live.Tool == "codex" && !CodexSessions.Inspect(session).Live) break;
                if (live.Tool == "claude" && !Directory.GetFiles(Path.Combine(ClaudeHome, "sessions"), "*.json").Select(ClaudeSessions.Inspect).Any(s => s.Live && s.Session == session)) break;
                Thread.Sleep(50);
            }
            if (live.Tool == "codex" && CodexSessions.Inspect(session).Live) throw new InvalidOperationException("原 Codex 进程没有及时结束，已取消接管");
            return new ComputerTakeover { Tool = live.Tool, Dir = where, Session = session, Launcher = FindTool(live.Tool), PhoneTerminal = true };
        }
    }
    async Task<bool> Login(Dictionary<string, object> prefs) {
        if (DateTime.UtcNow < nextLogin) return false;
        nextLogin = DateTime.UtcNow.AddSeconds(60);
        if (client != null) client.Dispose();
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() }) { Timeout = TimeSpan.FromSeconds(20) };
        // The address of a relay, or of this computer's place at a relay for everyone (https://relay/c/<id>).
        if (!Regex.IsMatch(Origin, @"\Ahttps?://[^/\s]+(/c/[a-f0-9]{20})?\z")) return false;
        var endpoint = ServicePointManager.FindServicePoint(new Uri(Origin));
        endpoint.UseNagleAlgorithm = false;
        endpoint.Expect100Continue = false;  // small JSON reports can send their body with the headers
        // The password is kept encrypted for this Windows account (see --set-password).
        byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(dataDir, "password.dpapi")), null, DataProtectionScope.CurrentUser);
        try {
            var content = new StringContent(json.Serialize(new Dictionary<string, object> { { "password", Encoding.UTF8.GetString(bytes) } }), Encoding.UTF8, "application/json");
            using (var r = await client.PostAsync(Origin + "/api/login", content))
                return r.IsSuccessStatusCode;
        } finally { Array.Clear(bytes, 0, bytes.Length); }
    }
    void Execute(Dictionary<string, object> op, Dictionary<string, object> prefs) {
        string id = Get(op, "id"), terminal = Get(op, "terminal"), action = Get(op, "action");
        if (completed.ContainsKey(id)) { reported.Remove(id); return; }
        string error = ""; Dictionary<string, object> result = null;
        try {
            bool project = action == "project_add" || action == "project_remove" || action == "project_rename", file = action == "file_list" || action == "file_read", update = action == "update";
            bool peek = action == "session_read" || action == "session_say" || action == "session_answer" || action == "session_mark";
            bool computer = Array.IndexOf(ComputerActions, action) >= 0;
            if (!Regex.IsMatch(id, @"\A[a-f0-9]{16,32}\z") || (!project && !file && !update && !peek && !computer && !Regex.IsMatch(terminal, @"\A[a-f0-9]{32}\z"))) throw new ArgumentException("操作编号无效");
            double at; if (!Double.TryParse(Get(op, "at"), out at) || Math.Abs((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds - at) > 150) throw new ArgumentException("操作已过期");
            if (Get(prefs, "RemoteEnabled") != "True") throw new InvalidOperationException("电脑远控已关闭");
            if (project) ChangeProjects(op, prefs, action);
            else if (update) { if (UpdateRequested == null) throw new InvalidOperationException("这台电脑运行的是不带自动更新的后台程序"); UpdateRequested(); }
            else if (computer) { if (Computer == null) throw new InvalidOperationException("这台电脑运行的后台程序没有可在手机上更改的设置"); result = Computer(op); }
            else if (action == "session_read") result = ReadConversation(op, prefs);
            else if (action == "session_mark") {
                // pinned or hidden, or no longer: of a conversation this computer listed
                string session = Get(op, "session"), tool = Get(op, "tool");
                if (!sessions.Any(s => s.Id == session && s.Tool == tool)) throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
                foreach (string kind in Marks.Kinds.Where(op.ContainsKey)) Marks.Set(kind, tool, session, Get(op, kind) == "True");
                result = new Dictionary<string, object>();
            }
            else if (peek) {
                // a conversation a program on this computer has open: only one this computer listed, in a project or where it was found open
                string session = Get(op, "session"), tool = Get(op, "tool");
                if (!sessions.Any(s => s.Id == session && s.Tool == tool && s.Live && (s.Dir.Length == 0 ? s.Folder.Length > 0 : AllDirs(prefs).ContainsKey(s.Dir)))) throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
                if (action == "session_say") result = new Dictionary<string, object> { { "now", Reach.Say(tool, session, Get(op, "text")) } };
                else { Reach.Answer(tool, session, Get(op, "ask"), Get(op, "allow") == "True"); result = new Dictionary<string, object>(); }
            }
            else if (file) {
                string root;
                if (!AllDirs(prefs).TryGetValue(Get(op, "dir"), out root)) throw new ArgumentException("目录未获允许");
                result = Files(root, Get(op, "path"), action, op.ContainsKey("offset") ? Convert.ToInt64(op["offset"]) : 0);
            }
            else if (action == "start") {
                if (terminals.ContainsKey(terminal)) return;
                if (terminals.Values.Count(t => !t.Pty.Closed) >= 8) throw new InvalidOperationException("最多同时运行 8 个终端");
                string tool = Get(op, "tool"), dir = Get(op, "dir"), previous = Get(op, "previous"), session = Get(op, "session");
                if (Get(op, "history") == "True" && session.Length == 0 && previous.Length == 0) throw new ArgumentException("请选择具体历史对话，或重新新建对话");
                bool fork = Get(op, "fork") == "True";
                if (fork && (tool != "codex" || session.Length == 0 || Get(op, "takeover") == "True")) throw new ArgumentException("副本选项无效");
                var dirs = AllDirs(prefs);
                // In a project; or, to go on with a conversation that is open in a folder that is no project, in that
                // folder (the relay says which, from what this computer listed).
                string folder = dir.Length == 0 && (session.Length > 0 || previous.Length > 0) && (tool == "claude" || tool == "codex") ? NormalFolder(Get(op, "folder")) : "";
                if (dir.Length == 0 ? folder.Length == 0 || !Directory.Exists(folder) : !dirs.ContainsKey(dir)) throw new ArgumentException("目录未获允许");
                string launcher = FindTool(tool);
                if (launcher == null) throw new ArgumentException("电脑未安装这个工具");
                if (session.Length > 0) {
                    if (!Scan(dirs).Any(s => s.Id == session && s.Tool == tool && s.Dir == dir && (dir.Length > 0 || SamePath(s.Folder, folder)))) throw new ArgumentException("电脑上没有找到这个对话");
                    if (tool == "claude") ClaudeExclusive(session, Get(op, "takeover") == "True");
                    if (tool == "codex" && !fork) {
                        var roots = terminals.Values.Select(t => t.Pty.Id).Concat(new[] { Process.GetCurrentProcess().Id }).ToArray();
                        if (Get(op, "takeover") == "True") CodexSessions.Takeover(session, roots, Parents());
                        var owner = CodexSessions.Inspect(session);
                        if (!owner.Known || owner.Live) throw new InvalidOperationException("原 Codex 对话仍被占用，请先在原应用结束并关闭它，再接管同一段历史；不会自动新建副本");
                    }
                } else if (previous.Length > 0) {
                    if (!Regex.IsMatch(previous, @"\A[a-f0-9]{16,32}\z")) throw new ArgumentException("历史编号无效");
                    session = List(prefs, "RemoteSessions").Select(Convert.ToString).Where(s => s.StartsWith(previous + "|", StringComparison.Ordinal)).Select(s => s.Substring(previous.Length + 1)).FirstOrDefault() ?? "";
                    if (session.Length == 0) throw new ArgumentException("旧对话没有会话编号，请使用历史对话入口");
                    if (tool == "claude") ClaudeExclusive(session, false);
                    else if (tool == "codex") { var owner = CodexSessions.Inspect(session); if (!owner.Known || owner.Live) throw new InvalidOperationException("原对话仍被占用，请从项目中选择接管原对话"); }
                }
                bool fresh = tool == "claude" && session.Length == 0 && Get(op, "history") != "True";
                if (fresh) session = Guid.NewGuid().ToString();
                var live = new LiveTerminal { Id = terminal, Tool = tool, Dir = dir, Folder = folder, Session = fork ? "" : session };
                live.Pty = new PseudoTerminal(Command(tool, launcher, session, Get(op, "history") == "True", Get(prefs, "RemoteMaxMode"), fork, tool == "codex" && SupportsStandalone(launcher), fresh), Place(live, dirs), 80, 24, text => {
                    lock (gate) { live.Pending.Append(text); live.Printed = DateTime.UtcNow; }
                    Wake();
                });
                terminals[terminal] = live;
            } else {
                LiveTerminal live;
                if (!terminals.TryGetValue(terminal, out live) || live.Pty.Closed) throw new ArgumentException("终端已结束");
                if (Place(live, AllDirs(prefs)) == null) throw new ArgumentException("目录不再获允许");
                if (action == "input") { lastInput = DateTime.UtcNow; string text = Get(op, "data"); if (text.Length == 0 || text.Length > 16000) throw new ArgumentException("输入无效"); live.Pty.Write(text); }
                else if (action == "resize") { int cols = Convert.ToInt32(op["cols"]), rows = Convert.ToInt32(op["rows"]); if (cols < 20 || cols > 240 || rows < 6 || rows > 100) throw new ArgumentException("尺寸无效"); live.Pty.Resize(cols, rows); }
                else if (action == "close") live.Pty.Dispose();
                else throw new ArgumentException("操作无效");
            }
        } catch (Exception ex) { error = ex is ArgumentException || ex is InvalidOperationException || ex is IOException ? ex.Message : "终端操作失败：" + ex.GetType().Name; }
        completed[id] = new Dictionary<string, object> { { "id", id }, { "error", error } };
        if (result != null && error.Length == 0) completed[id]["result"] = result;
        lastActivity = DateTime.UtcNow; soon = true; if (action == "start" || action == "close") nextScan = DateTime.UtcNow.AddSeconds(1.5);
        if (action.StartsWith("project_", StringComparison.Ordinal)) nextScan = DateTime.MinValue;
    }
    async Task Tick() {
        Dictionary<string, object> prefs = Preferences();
        if (client == null && !await Login(prefs)) return;
        string body, listedText; bool listing; Dictionary<string, object>[] acknowledgments;
        lock (work) {
            if (returning != null) { var back = returning; returning = null; foreach (var kept in back) { try { Return(kept, prefs); } catch { } } nextScan = DateTime.UtcNow.AddSeconds(3); }
            bool enabled = Get(prefs, "RemoteEnabled") == "True";
            if (!enabled) foreach (var t in terminals.Values.Where(t => !t.Pty.Closed)) t.Pty.Dispose();
            List<Dictionary<string, object>> output;
            var allowed = AllDirs(prefs);
            var settings = Dirs(prefs);
            // Reading the conversation lists takes a moment; it waits while someone is typing.
            if (DateTime.UtcNow >= nextScan && (DateTime.UtcNow - lastInput > TimeSpan.FromSeconds(2) || DateTime.UtcNow - lastScan > TimeSpan.FromSeconds(20))) {
                nextScan = DateTime.UtcNow.AddSeconds(5); lastScan = DateTime.UtcNow;
                if (!enabled) { sessions = new List<SessionInfo>(); candidates = new List<Dictionary<string, object>>(); }
                else if (Interlocked.CompareExchange(ref scanning, 1, 0) == 0) {
                    var running = terminals.Values.ToArray();
                    var reading = Task.Run(() => { try { Scan(allowed, running); } catch { } finally { scanning = 0; Wake(); } });
                }
            }
            if (DateTime.UtcNow - toolsAt > TimeSpan.FromSeconds(30)) { tools = new[] { "claude", "codex", "shell" }.Where(t => FindTool(t) != null).ToArray(); toolsAt = DateTime.UtcNow; }
            // An ended terminal whose conversation runs in a terminal again is no longer one of the recent ones.
            ended.RemoveAll(e => e.Session.Length > 0 && terminals.Values.Any(t => !t.Pty.Closed && t.Tool == e.Tool && t.Session == e.Session));
            lock (gate) { foreach (var t in terminals.Values) Seal(t); output = OutputBatch(terminals.Values.Concat(ended).SelectMany(t => t.Output.Take(30))); }
            if (output.Count > 0) lastActivity = DateTime.UtcNow;
            // Pieces of files are large: a report carries as many as fit, the rest go with the next.
            int room = 3 * FilePiece, taken = 0;
            acknowledgments = completed.Where(p => !reported.Contains(p.Key)).Take(200).Select(p => p.Value).TakeWhile(a => {
                object answer; Dictionary<string, object> piece = a.TryGetValue("result", out answer) ? answer as Dictionary<string, object> : null;
                room -= piece != null && piece.ContainsKey("data") ? ((string)piece["data"]).Length : 0;
                return taken++ == 0 || room >= 0;
            }).ToArray();
            var payload = new Dictionary<string, object> {
                { "info", new { instance = instance, enabled = enabled, workspaces = allowed.Keys.ToArray(), tools = tools, version = Version, newer = Newer, shell = "PowerShell",
                    features = new[] { "codex-fork", "codex-takeover", "terminal-exit", "files", "peek", "reach", "marks" }.Concat(UpdateRequested != null ? new[] { "update" } : new string[0]).Concat(Computer != null ? new[] { "settings" } : new string[0]).ToArray(),
                    projects = settings.Select(d => new { name = d.Key, path = d.Value, @fixed = true, exists = true })
                        .Concat(OwnProjects().Where(p => !settings.ContainsKey(p.Key)).Select(p => new { name = p.Key, path = p.Value, @fixed = false, exists = allowed.ContainsKey(p.Key) })).ToArray(),
                    candidates = candidates.ToArray() } },
                // Every terminal says what it is, so that a relay that did not start it can list it: the computer may turn to another relay.
                { "terminals", terminals.Values.Concat(ended).Select(t => new { id = t.Id, state = t.Pty.Closed ? "closed" : "running", cols = t.Pty.Cols, rows = t.Pty.Rows, session = t.Session, status = t.Status, exit_code = t.Pty.ExitCode, error = t.Pty.ExitCode.HasValue && t.Pty.ExitCode.Value != 0 ? "程序退出（代码 " + t.Pty.ExitCode.Value + "），请检查终端画面中的原因" : "",
                    tool = t.Tool, dir = t.Dir, folder = t.Folder, created = Milliseconds(t.Started), title = t.Title }).ToArray() },
                { "output", output }, { "acks", acknowledgments }
            };
            // The list of conversations is long: it goes out when it changed, and now and then in case the relay restarted.
            var listed = sessions.Select(Listed).ToArray();
            listedText = json.Serialize(listed);
            listing = listedText != sentSessions || DateTime.UtcNow - sessionsSent > TimeSpan.FromSeconds(10);
            if (listing) payload["sessions"] = listed;
            body = json.Serialize(payload);
        }
        var request = new HttpRequestMessage(HttpMethod.Post, Origin + "/api/terminal/agent");
        request.Headers.TryAddWithoutValidation("Origin", Origin); request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using (request) using (var response = await client.SendAsync(request)) {
            if ((int)response.StatusCode == 401) { await Login(prefs); return; }
            if (!response.IsSuccessStatusCode) return;
            string answer = await response.Content.ReadAsStringAsync();
            lock (work) {
                var result = json.Deserialize<Dictionary<string, object>>(answer);
                // An answer is handed over once and not kept. When the relay asks for it again, it never had it (the
                // computer had turned to another relay): it is told so, not given an empty answer.
                foreach (var a in acknowledgments) { reported.Add(Convert.ToString(a["id"])); if (a.Remove("result") && Convert.ToString(a["error"]).Length == 0) a["error"] = "电脑的回答没有送到，请重试"; }
                if (listing) { sentSessions = listedText; sessionsSent = DateTime.UtcNow; }
                var ack = result.ContainsKey("output_ack") ? result["output_ack"] as Dictionary<string, object> : null;
                if (ack != null) lock (gate) foreach (var t in terminals.Values.Concat(ended)) { object seq; if (ack.TryGetValue(t.Id, out seq) && Settle(t, Convert.ToInt64(seq), t.Pty.Closed ? KeepEnded : KeepRunning)) soon = true; }
                var named = result.ContainsKey("titles") ? result["titles"] as Dictionary<string, object> : null;
                if (named != null) foreach (var t in terminals.Values.Concat(ended)) { object title; if (named.TryGetValue(t.Id, out title)) t.Title = Convert.ToString(title); }
                foreach (object item in List(result, "operations")) { var op = item as Dictionary<string, object>; if (op != null) Execute(op, prefs); }
                if (completed.Count > 4000) foreach (string key in completed.Keys.Take(1000).ToArray()) { completed.Remove(key); reported.Remove(key); }
                // A terminal is forgotten only after its last output has been read from the program and stored by the relay.
                foreach (var t in terminals.Values.Where(t => t.Pty.Closed && t.ClosedSeen == DateTime.MinValue)) t.ClosedSeen = DateTime.UtcNow;
                string[] finished; lock (gate) finished = terminals.Where(t => t.Value.Pty.Closed && (t.Value.Pty.Drained || DateTime.UtcNow - t.Value.ClosedSeen > TimeSpan.FromSeconds(15)) && t.Value.Output.Count == 0 && t.Value.Pending.Length == 0).Select(t => t.Key).ToArray();
                foreach (string key in finished) {
                    var over = terminals[key]; over.Pty.Dispose(); terminals.Remove(key);
                    ended.RemoveAll(e => e.Session.Length > 0 && e.Tool == over.Tool && e.Session == over.Session);
                    lock (gate) Settle(over, over.Seq, KeepEnded);
                    ended.Add(over); if (ended.Count > EndedMost) ended.RemoveAt(0);
                }
            }
        }
    }
    // A request the relay holds open until the phone asks for something. What it brings is carried out at once, so
    // a key press does not wait for the next report; the report then only takes the result back.
    async Task Listen() {
        var reader = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        for (;;) {
            var started = DateTime.UtcNow; bool waiting = false, brought = false;
            try {
                HttpClient current = client;
                if (current != null) {
                    var request = new HttpRequestMessage(HttpMethod.Post, Origin + "/api/terminal/agent/pull");
                    request.Headers.TryAddWithoutValidation("Origin", Origin);
                    request.Content = new StringContent(reader.Serialize(new Dictionary<string, object> { { "instance", instance }, { "wait", 12 } }), Encoding.UTF8, "application/json");
                    using (request) using (var response = await current.SendAsync(request)) {
                        if (response.IsSuccessStatusCode) {
                            var result = reader.Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync());
                            waiting = true;
                            var operations = List(result, "operations").OfType<Dictionary<string, object>>().ToArray();
                            if (operations.Length > 0) {
                                brought = true;
                                Dictionary<string, object> prefs = Preferences();
                                lock (work) foreach (var op in operations) Execute(op, prefs);
                            }
                            if (operations.Length > 0 || Get(result, "operations") == "True") Wake();
                        }
                    }
                }
            } catch { }
            // An answer that came back at once without being a held request (not signed in yet, an older relay) must not become a busy loop.
            // One that brought something is asked for again at once: in a drag or fast typing the next is already waiting.
            if (!waiting || (!brought && DateTime.UtcNow - started < TimeSpan.FromMilliseconds(40))) await Task.Delay(waiting ? 40 : 2000);
        }
    }
    public async Task Run() {
        Task listening = Listen();
        for (;;) {
            try { await Tick(); } catch { /* Retry network and login failures without logging credentials or terminal input. */ }
            int pause = soon ? 20 : DateTime.UtcNow - lastActivity < TimeSpan.FromSeconds(20) ? 250 : 700;
            soon = false;
            bool woken = false, failed = false;
            try { woken = await wake.WaitAsync(pause); } catch { failed = true; }
            if (failed) await Task.Delay(pause);
            if (woken && DateTime.UtcNow - lastInput > TimeSpan.FromMilliseconds(150)) await Task.Delay(12);
        }
    }
    [STAThread] public static int Main(string[] args) {
        // --set-password reads the password from standard input and stores it encrypted for this Windows account.
        if (args.Length > 0 && args[0] == "--set-password") {
            string folder = args.Length > 1 ? args[1] : DefaultData;
            string secret = (Console.In.ReadLine() ?? "").Trim();
            if (secret.Length == 0) return 2;
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "password.dpapi"), ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));
            return 0;
        }
        // --hook <tool> <pipe>: run by Claude Code or Codex as one of their hooks (Reach.cs)
        if (args.Length == 3 && args[0] == "--hook") return RemoteCli.Reach.Hook(args[1], args[2]);
        string data = args.Length > 1 && args[0] == "--data" ? args[1] : null;
        bool created;
        using (var singleton = new Mutex(true, "Local\\RemoteCliAgent" + (data == null ? "" : "-" + Math.Abs(data.ToLowerInvariant().GetHashCode())), out created)) {
            if (!created) return 0;
            ServicePointManager.DefaultConnectionLimit = 8;   // the held request and the reports run side by side
            try { new TerminalAgent(data).Run().GetAwaiter().GetResult(); } finally { singleton.ReleaseMutex(); }
        }
        return 0;
    }
}
}
