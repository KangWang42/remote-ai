using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
public sealed class PseudoTerminal : IRunning {
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
    bool disposed, closingProcess;
    public int Id { get { return process.Id; } }
    public bool Closed { get; private set; }
    public bool Drained { get; private set; }   // the reader has delivered everything the program wrote
    public string ReadError = "";
    public int? ExitCode { get; private set; }
    public bool Alive { get { try { return process != null && !process.HasExited; } catch { return false; } } }
    public int Cols { get; private set; }
    public int Rows { get; private set; }

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
            try {
                process = Process.GetProcessById(info.processId);
                // Keep a handle while the child is alive so its exit code remains readable after it exits.
                GC.KeepAlive(process.Handle);
            } finally { CloseHandle(info.process); CloseHandle(info.thread); }
            Cols = cols; Rows = rows;
            Task.Run(delegate {
                // Coalesce bursty ConPTY micro-reads into bounded frames (4ms / 24KB) so high-throughput builds
                // do not cause lock contention or tiny-chunk storms while keeping interactive typing < 4ms.
                byte[] bytes = new byte[8192]; char[] chars = new char[8192]; Decoder decoder = Encoding.UTF8.GetDecoder();
                var pending = new StringBuilder(4096);
                var flushGate = new object();
                bool ended = false;
                Action flushNow = () => {
                    string batch = null;
                    lock (flushGate) {
                        if (pending.Length > 0) { batch = pending.ToString(); pending.Clear(); }
                    }
                    if (batch != null) receive(batch);
                };
                using (var timer = new System.Threading.Timer(_ => flushNow(), null, Timeout.Infinite, Timeout.Infinite)) {
                    try {
                        int n;
                        while ((n = output.Read(bytes, 0, bytes.Length)) > 0) {
                            int count = decoder.GetChars(bytes, 0, n, chars, 0, false);
                            if (count <= 0) continue;
                            string immediate = null;
                            lock (flushGate) {
                                pending.Append(chars, 0, count);
                                if (pending.Length >= 24576) {
                                    immediate = pending.ToString();
                                    pending.Clear();
                                } else if (!ended) {
                                    timer.Change(4, Timeout.Infinite);
                                }
                            }
                            if (immediate != null) receive(immediate);
                        }
                    } catch (IOException ex) { ReadError = ex.Message; } catch (ObjectDisposedException) { }
                    finally {
                        lock (flushGate) ended = true;
                        timer.Change(Timeout.Infinite, Timeout.Infinite);
                        flushNow();
                        Drained = true; Closed = true;
                    }
                }
            });
            Task.Run(delegate { try { process.WaitForExit(); lock (gate) ExitCode = closingProcess ? 0 : process.ExitCode; } catch { } finally { CloseConsole(); Closed = true; } });
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
        lock (gate) { if (disposed) return; disposed = true; closingProcess = Alive; }
        if (closingProcess) ProcessText.KillProcessTree(process.Id, 10000);
        CloseConsole();
        if (input != null) input.Dispose();
        if (output != null) output.Dispose();
        Closed = true;
    }
}

public sealed class TerminalAgent {
    string Origin = "";
    readonly string dataDir;
    public readonly string EnvironmentId;
    public Func<List<RouteInfo>> RoutesProvider;
    public TerminalAgent(string dataFolder = null, Func<IDesktopConversation> desktopConversation = null, AntigravityDesktopClient agyDesktopClient = null) {
        dataDir = dataFolder ?? DefaultData;
        EnvironmentId = EnvironmentIdentity.LoadOrCreate(dataDir);
        AgyDesktopClient = agyDesktopClient ?? new AntigravityDesktopClient();
        AgyDesktop = new AntigravityDesktopSessions(AgyDesktopClient);
        AgyDesktop.Changed += () => { sessionsDirty = true; Wake(); };
        Desktop = new DesktopOperations(dataDir, new CodexDesktopAdapter(), new ClaudeDesktopAdapter(), new AntigravityDesktopAdapter(AgyDesktopClient));
        OpenDesktopConversation = desktopConversation ?? (() => CodexDesktopControl.Connect());
        NativeDesktop = new CodexDesktopSessions(OpenDesktopConversation);
        NativeDesktop.Changed += () => { sessionsDirty = true; Wake(); };
        Quotas = new Quotas(dataDir);
        Delegations = new Delegations(new Delegations.Hands {
            Begin = BeginTask, StateOf = StateOf, Say = SayToMember, Stop = StopMember, Close = CloseMember, Answer = AnswerIn, Card = CardIn });
        Delegations.Changed += () => { soon = true; Wake(); };
        Peers = new Peers(RemoteCli.Peers.PipeName(dataDir), FindTool, Quotas, Path.Combine(dataDir, "mcp"), null, RemoteCli.Peers.DefaultTimeoutSeconds, new Peers.CollabHooks {
            CanPromote = CanPromoteTerminal,
            Confirm = ConfirmInTerminal,
            Promote = PromoteFromMcp
        }, Delegations);
        Reach = new Reach(RemoteCli.Reach.PipeName(dataDir), null, KeptForPhone, InOwnTerminal, new Outbox(Path.Combine(dataDir, "outbox"))); Reach.Changed += () => { sessionsDirty = true; Wake(); }; Reach.Outbox.Changed += () => { sessionsDirty = true; Wake(); };
        Stats = new Stats(() => ClaudeSessions.Home, () => CodexSessions.Home, () => Antigravity.Home, Path.Combine(dataDir, "stats-ledger.json"));
        Schedules = new Schedules(Path.Combine(dataDir, "schedules.json"));
        Chains = new Chains(Path.Combine(dataDir, "chains.json"));
        Worktrees = new Worktrees(Path.Combine(dataDir, "worktrees"));
        Models = new Models(FindTool, (tool, launcher, ms) => {
            string scratch = Path.Combine(dataDir, "models");
            Directory.CreateDirectory(scratch);
            return Chat.ListModels(tool, launcher, scratch, ms);
        });
        Teams = new Teams(Path.Combine(dataDir, "teams.dat"), new Teams.Hands {
            Begin = BeginMember, Say = SayToMember, SayWithAttachments = SayToMember, ReadAttachment = ReadQueuedAttachment,
            Stop = StopMember, Close = CloseMember, Restart = RestartMember, Changes = ChangesIn, Snapshot = SnapshotIn, Limits = Quotas.Of, Available = AvailableChatTools,
            MakeWorktree = MakeTeamWorktree, WorktreeDiff = Worktrees.Diff, WorktreeStat = Worktrees.Stat, ApplyWorktree = ApplyTeamWorktree, CleanWorktrees = CleanTeamWorktrees });
        Teams.Changed += Wake;
        Reach.Finished += (tool, session) => { lock (rested) rested.Enqueue(new[] { tool, session }); Wake(); };
        Marks = new Marks(Path.Combine(dataDir, "conversation-marks.json"));
        Handovers = new Handovers(Path.Combine(dataDir, "handovers.json"));
        string organizing = Path.Combine(dataDir, "organize");
        Organizer = new Organizer(Path.Combine(dataDir, "conversation-organize.json"),
            since => OrganizeHistory.List(AllDirs(Preferences()), sessions, since, organizing), OrganizeReadable, OrganizeBusy,
            AvailableChatTools, choice => OrganizeSources.Open(choice, FindTool, OrganizeModels, organizing));
        Organizer.Changed += () => { sessionsDirty = true; Wake(); };
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
    static readonly string[] ComputerActions = { "settings_read", "settings_change", "line_switch", "line_take", "line_keep", "routes_read" };
    static readonly string[] GitActions = { "git_status", "git_file_diff", "git_commit", "git_push", "git_worktree_list", "git_worktree_apply", "git_worktree_remove", "git_checkpoint", "git_branches", "git_checkout", "git_checkpoints_list", "git_checkpoint_restore", "project_scripts" };
    readonly DesktopOperations Desktop;
    readonly Func<IDesktopConversation> OpenDesktopConversation;
    readonly CodexDesktopSessions NativeDesktop;
    readonly AntigravityDesktopClient AgyDesktopClient;
    readonly AntigravityDesktopSessions AgyDesktop;
    readonly Dictionary<string, Antigravity.Summary> newAgySessions = new Dictionary<string, Antigravity.Summary>(StringComparer.OrdinalIgnoreCase);
    /// Whether the relay has been given the answer to this operation.
    public bool Reported(string id) {
        lock (work) {
            if (reported.Contains(id)) return true;
            string source;
            RelayLink[] current = ActiveLinks();
            if (opSource.TryGetValue(id, out source)) {
                var owner = current.FirstOrDefault(l => l.Key == source);
                return owner != null && owner.HasReported(id);
            }
            return current.Any(l => l.HasReported(id));
        }
    }
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
            var open = terminals.Values.Where(t => !t.Pty.Closed).Select(t => new Dictionary<string, object> { { "id", t.Id }, { "tool", t.Tool }, { "dir", t.Dir }, { "folder", t.Folder }, { "session", t.Chat != null ? t.Chat.Session : t.Session }, { "seq", t.Seq }, { "cols", t.Pty.Cols }, { "rows", t.Pty.Rows }, { "form", t.Chat != null ? "chat" : "" }, { "model", t.Model }, { "effort", t.Effort } }).ToArray();
            try { File.WriteAllText(RestartFile, json.Serialize(new Dictionary<string, object> { { "instance", instance }, { "at", DateTime.UtcNow.ToString("o") }, { "terminals", open } }), new UTF8Encoding(false)); } catch { }
        }
    }
    // A terminal that was open before the restart: Claude Code and Codex go on with the conversation they had, a shell
    // starts afresh in its folder. Its output continues the numbering, so the relay and the phone take it as the same terminal.
    void Return(Dictionary<string, object> kept, Dictionary<string, object> prefs) {
        string id = Get(kept, "id"), tool = Get(kept, "tool"), dir = Get(kept, "dir"), session = Get(kept, "session");
        var dirs = AllDirs(prefs);
        Tool kind = Tool.Of(tool);
        if (kind == null || (!kind.ReturnsUnnamed && session.Length == 0)) return; // an unnamed one cannot be found again
        string launcher = FindTool(tool);
        long baseSeq = Convert.ToInt64(kept["seq"]);
        var live = new LiveTerminal { Id = id, Tool = tool, Dir = dir, Folder = dir.Length == 0 ? Get(kept, "folder") : "", Session = session, Seq = baseSeq, BaseSeq = baseSeq };
        string where = Place(live, dirs);
        if (!Regex.IsMatch(id, @"\A[a-f0-9]{32}\z") || terminals.ContainsKey(id) || launcher == null || where == null || !Directory.Exists(where) || !HasTerminalSlot()) return;
        if (Get(kept, "form") == "chat") {
            // a conversation goes on where it was; one whose tool had not named it yet cannot be found again
            if (session.Length == 0 || !kind.Chats) return;
            string model = Get(kept, "model");
            live.Model = Models.Valid(model) ? model : "";
            live.Effort = Models.CleanEffort(Get(kept, "effort"));
            live.Pty = Chat.Open(tool, launcher, where, session, true, Get(prefs, "RemoteMaxMode"), Emitted(live), Quotas, Peers.Offer(tool, where, live.Id, live.Dir), live.Model, live.Effort);
            Heard(live);
            // Recall saved transcript messages before the update note so returned chats keep full history
            // (and automatically heal chats whose relay buffer was wiped by an earlier update).
            bool recalled = Recall(live, where);
            lock (gate) {
                live.Pending.Append(json.Serialize(new Dictionary<string, object> { { "t", "note" }, { "text", "Remote AI 已更新，对话已接回" }, { "at", Milliseconds(DateTime.UtcNow) } }) + "\n");
                if (recalled) {
                    Seal(live);
                    if (live.Output.Count > 0) live.Output[0]["restart"] = true;
                }
            }
            terminals[id] = live;
            return;
        }
        live.Pending.Append("\r\n\x1b[2m" + (!kind.Converses ? "Remote AI 已更新，这是重新打开的 PowerShell。" : session.Length > 0 ? "Remote AI 已更新，正在接回原来的对话…" : "Remote AI 已更新；原对话保存在电脑上，可从历史对话继续。") + "\x1b[0m\r\n");
        live.Pty = new PseudoTerminal(Command(tool, launcher, session, false, Get(prefs, "RemoteMaxMode"), false, kind.SupportsStandalone && SupportsStandalone(launcher), false), where,
            Math.Max(20, Math.Min(240, Convert.ToInt32(kept["cols"]))), Math.Max(6, Math.Min(100, Convert.ToInt32(kept["rows"]))), Emitted(live));
        terminals[id] = live;
    }
    // What a terminal's program gives is kept for the next report, which goes out at once.
    Action<string> Emitted(LiveTerminal live) {
        int generation = live.Generation;
        return text => {
            lock (gate) { if (live.Generation != generation) return; live.Pending.Append(text); live.Printed = DateTime.UtcNow; }
            Wake();
        };
    }
    /// What was said in a saved conversation (or its whole handover line) before it is gone on with in a chat.
    /// Returns whether any saved messages were found and emitted.
    bool Recall(LiveTerminal live, string root) { return Recall(live, root, live.Tool, live.Session, true); }
    bool Recall(LiveTerminal live, string root, string tool, string session, bool own) {
        bool more;
        var messages = LineMessages(root, tool, session, own ? "" : live.Tool, out more);
        if (!messages.Any(m => Convert.ToString(m["role"]) != "switch")) return false;
        live.Chat.Before(messages, more);
        return true;
    }
    /// Splits every turn of a handover line into its own message list, reading each unique transcript at most once.
    List<List<Dictionary<string, object>>> SliceTurns(string root, List<RemoteCli.Handovers.Turn> turns, out bool firstMore) {
        firstMore = false;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstAt = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < turns.Count; i++) {
            if (String.IsNullOrEmpty(turns[i].Session)) continue;
            string key = turns[i].Tool + "\0" + turns[i].Session.ToLowerInvariant();
            int count;
            counts.TryGetValue(key, out count);
            counts[key] = count + 1;
            if (!firstAt.ContainsKey(key)) firstAt[key] = i;
        }
        var visitsByKey = new Dictionary<string, List<List<Dictionary<string, object>>>>(StringComparer.Ordinal);
        var moreByKey = new Dictionary<string, bool>(StringComparer.Ordinal);
        var nextIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in counts) {
            int sep = kv.Key.IndexOf('\0');
            string t = kv.Key.Substring(0, sep);
            string s = turns[firstAt[kv.Key]].Session;
            var empty = new List<List<Dictionary<string, object>>>();
            for (int k = 0; k < kv.Value; k++) empty.Add(new List<Dictionary<string, object>>());
            try {
                string file = ConversationFile(t, s, root);
                if (file == null) { visitsByKey[kv.Key] = empty; moreByKey[kv.Key] = false; continue; }
                var said = Said(file, t);
                var raw = (List<Dictionary<string, object>>)said["messages"];
                bool rawMore = (bool)said["more"];
                visitsByKey[kv.Key] = RemoteCli.Handovers.SplitVisits(raw, kv.Value, firstAt[kv.Key] > 0, rawMore);
                moreByKey[kv.Key] = rawMore;
            } catch (IOException) { visitsByKey[kv.Key] = empty; moreByKey[kv.Key] = false; }
            catch (UnauthorizedAccessException) { visitsByKey[kv.Key] = empty; moreByKey[kv.Key] = false; }
        }
        var sliced = new List<List<Dictionary<string, object>>>(turns.Count);
        for (int i = 0; i < turns.Count; i++) {
            if (String.IsNullOrEmpty(turns[i].Session)) { sliced.Add(new List<Dictionary<string, object>>()); continue; }
            string key = turns[i].Tool + "\0" + turns[i].Session.ToLowerInvariant();
            int idx;
            nextIndex.TryGetValue(key, out idx);
            nextIndex[key] = idx + 1;
            var list = visitsByKey[key];
            sliced.Add(idx < list.Count ? list[idx] : new List<Dictionary<string, object>>());
            if (i == 0) firstMore = moreByKey[key];
        }
        return sliced;
    }
    /// Collects what was said in every conversation of a handover line, oldest first, with a `switch` row where
    /// another AI took over (and a trailing `switch` row for `nextTool` when a new handover starts now).
    List<Dictionary<string, object>> LineMessages(string root, string tool, string session, string nextTool, out bool more) {
        var turns = Handovers.TurnsOf(tool, session);
        if (turns.Count == 0 && !String.IsNullOrEmpty(tool) && !String.IsNullOrEmpty(session))
            turns.Add(new RemoteCli.Handovers.Turn { Tool = tool, Session = session });
        var slices = SliceTurns(root, turns, out more);
        var messages = new List<Dictionary<string, object>>();
        for (int n = 0; n < turns.Count; n++) {
            if (n > 0) messages.Add(new Dictionary<string, object> { { "role", "switch" }, { "tool", turns[n].Tool }, { "text", Label(turns[n].Tool) } });
            messages.AddRange(slices[n]);
        }
        if (!String.IsNullOrEmpty(nextTool))
            messages.Add(new Dictionary<string, object> { { "role", "switch" }, { "tool", nextTool }, { "text", Label(nextTool) } });
        return messages;
    }
    /// Collects only what happened since `targetTool:targetSession` paused (when resuming an earlier participant).
    List<Dictionary<string, object>> DeltaMessages(string root, string fromTool, string fromSession, string targetTool, string targetSession, out List<string> betweenTools) {
        betweenTools = new List<string>();
        var turns = Handovers.TurnsOf(fromTool, fromSession);
        int lastOwn = -1;
        for (int i = turns.Count - 1; i >= 0; i--) {
            if (turns[i].Tool == targetTool && String.Equals(turns[i].Session, targetSession, StringComparison.OrdinalIgnoreCase)) {
                lastOwn = i;
                break;
            }
        }
        if (lastOwn < 0 || lastOwn >= turns.Count - 1) return new List<Dictionary<string, object>>();
        bool unused;
        var slices = SliceTurns(root, turns, out unused);
        var delta = new List<Dictionary<string, object>>();
        for (int i = lastOwn + 1; i < turns.Count; i++) {
            if (!betweenTools.Contains(turns[i].Tool)) betweenTools.Add(turns[i].Tool);
            delta.AddRange(slices[i]);
        }
        return delta;
    }
    /// The file a tool keeps a conversation in; null where there is none.
    string ConversationFile(string tool, string session, string root) {
        string file;
        Tool kind = Tool.Of(tool);
        if (kind == null) return null;
        // the reading of the lists may have found it already (Codex keeps its conversations by day)
        if (kind == Tool.Codex && codexFiles.TryGetValue(session, out file) && File.Exists(file)) return file;
        file = kind.Transcript(session, root);
        if (file != null && kind == Tool.Codex) codexFiles[session] = file;
        return file != null && File.Exists(file) ? file : null;
    }
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
    string instance = Guid.NewGuid().ToString("N");       // the same again after a restart for an update
    readonly Dictionary<string, LiveTerminal> terminals = new Dictionary<string, LiveTerminal>();
    readonly Dictionary<string, Dictionary<string, object>> completed = new Dictionary<string, Dictionary<string, object>>();
    readonly HashSet<string> reported = new HashSet<string>();
    readonly Dictionary<string, string> opSource = new Dictionary<string, string>();
    readonly object linksGate = new object();
    readonly List<RelayLink> links = new List<RelayLink>();
    bool explicitLinks;
    bool running;
    // How long a woken conversation is given to stop and take the words left for it. REMOTECLI_WAKE names another time, for a test.
    static readonly int WakeSeconds = WakeTime();
    static int WakeTime() { int given; return Int32.TryParse(Environment.GetEnvironmentVariable("REMOTECLI_WAKE"), out given) && given >= 1 && given <= 120 ? given : 40; }
    readonly HashSet<string> relaying = new HashSet<string>();       // messages on their way into an idle conversation (ClaudeRelay)
    readonly object gate = new object();
    public sealed class LiveTerminal {
        public string Id, Tool, Dir, Session = "", Status = "", Title = ""; public IRunning Pty; public long Seq, BaseSeq;
        /// The model and reasoning effort a conversation was begun with (a team's member, Models.cs); "" for its tool's default.
        public string Model = "", Effort = "";
        /// How widely a conversation may work (full, edit, read), as it was begun: kept when it is started anew.
        public string Mode = "";
        /// The conversation this one goes on from when another AI took over in-place, until its session id is linked.
        public SessionInfo HandedFrom;
        /// How often its program was started anew in this terminal (RestartMember): what an earlier one still says is dropped.
        public int Generation;
        /// A conversation run through its tool's structured mode (Chats.cs) rather than a screen; null for a screen.
        public Chat Chat { get { return Pty as Chat; } }
        // A terminal runs in a project (Dir). One that took on a conversation that was open in a folder that is no
        // project runs in that folder instead: it has no project ("") and says the folder.
        public string Folder = "";
        /// Optional isolated Git worktree for this session/terminal.
        public string WorktreeId = "", WorktreePath = "", Branch = "";
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
    const int MaxTerminals = 16;

    bool HasTerminalSlot() { return terminals.Values.Count(t => !t.Pty.Closed) < MaxTerminals; }

    void RequireTerminalSlot() {
        if (!HasTerminalSlot()) throw new InvalidOperationException("最多同时运行 " + MaxTerminals + " 个终端，请先结束一个");
    }
    /// The line that sets the owner's message apart from the hand-over summary before it; the UI shows what follows it.
    internal const string HandoverAsk = Handovers.AskMarker;
    /// Locate the source conversation for an in-place AI switch (`from`, `from_tool`): listed, in the same project,
    /// and not a member of a collaboration task. The new chat begins with the owner's next message (`text`).
    SessionInfo Handed(Dictionary<string, object> op, string tool, string dir, string session) {
        if (Get(op, "form") != "chat" || session.Length > 0 || op.ContainsKey("reopen") || dir.Length == 0) throw new ArgumentException("换 AI 的选项无效");
        if (Get(op, "text").Trim().Length == 0) throw new ArgumentException("请写下要交给它的话");
        string fromTool = Get(op, "from_tool"), fromId = Get(op, "from");
        var from = sessions.FirstOrDefault(s => s.Tool == fromTool && String.Equals(s.Id, fromId, StringComparison.OrdinalIgnoreCase));
        if (from == null) throw new ArgumentException("电脑上没有找到原来的对话，请刷新后重试");
        if (from.Dir != dir) throw new ArgumentException("只能在同一个项目里换 AI");
        if (Teams.TeamOf("", from.Tool, from.Id).Length > 0) throw new InvalidOperationException("协作任务中的对话请在协作里换 AI");
        return from;
    }
    /// Locate the idle conversation whose view or model is changing, before ending its process.
    LiveTerminal Reopening(Dictionary<string, object> op, string tool, string dir) {
        LiveTerminal left;
        if (!terminals.TryGetValue(Get(op, "reopen"), out left) || left.Pty.Closed) throw new InvalidOperationException("原对话已结束，请刷新后重试");
        bool tune = op.ContainsKey("model");
        if (left.Tool != tool || left.Dir != dir) throw new ArgumentException("原对话的工具或目录已变更");
        if (tune) {
            if (left.Chat == null || Get(op, "form") != "chat" || left.Chat.Status != "idle") throw new ArgumentException("请等对话空闲后再切换模型");
            string model = Get(op, "model"), effort = Get(op, "effort");
            if ((model.Length > 0 && !Models.Valid(model)) || (effort.Length > 0 && !Models.ValidEffort(effort))) throw new ArgumentException("模型或思考深度无效");
        } else {
            if (tool != "claude" && tool != "codex") throw new ArgumentException("切换视图的选项无效");
            if ((left.Chat != null) == (Get(op, "form") == "chat")) throw new ArgumentException("这段对话已经是这种视图");
        }
        string session = left.Chat != null ? left.Chat.Session : left.Session;
        if (session.Length == 0) throw new InvalidOperationException(Label(tool) + " 还没有给这段对话编号，发一条消息后再切换");
        if (Teams.TeamOf(left.Id, left.Tool, session).Length > 0) throw new InvalidOperationException("协作任务中的对话不能切换视图");
        string status = left.Chat != null ? left.Chat.Status : left.Status;
        if (status == "busy" || status == "confirm") throw new InvalidOperationException("对话正在执行或等待确认，请等这一轮结束或先停止");
        return left;
    }
    /// Ends the terminal a reopened conversation leaves, and waits until no program of this computer holds the
    /// conversation any more: a conversation has one writer at a time.
    void Leave(LiveTerminal left, Tool kind, string session) {
        left.Pty.Dispose();         // (ends its whole process tree before it returns)
        for (int tries = 0; ; tries++) {
            try { kind.Exclusive(session, false, OwnRoots(), Parents(), null); return; }
            catch (InvalidOperationException) { if (tries >= 15) throw; Thread.Sleep(200); }
        }
    }
    public sealed class ComputerTakeover {
        public string Tool, Dir, Session, Launcher;         // Dir: the folder the window opens in
        public bool PhoneTerminal;
    }
    // Dir: the project a conversation belongs to. One that a program has open in a folder that is no project has none
    // ("") and says the folder instead (Folder): the phone can look at it, answer it and tell it things, and its folder
    // can be made a project from there.
    // Live: a program on this computer holds it. Now: it is in use (InUse); only then is it something that is going on.
    // Wakes: a message for it can be handed to it where it is even while it sits idle (ClaudeRelay, CodexQueue).
    public sealed class SessionInfo { public bool Now, Wakes, MultipleOwners, Named; public string Kind = ""; public string Id, Tool, Dir, Folder = "", ControlFolder = "", Title, Status = "", Host = "", Origin = ""; public long Updated; public DateTime Created; public bool Live, CanTakeover, OwnershipKnown; public string TakeoverReason = "", Same = ""; }
    public static string ObservedStatus(SessionInfo s, Reach.Open known) {
        // Hooks are keyed by conversation, not process: an idle copy cannot stop the busy copy.
        if (s.MultipleOwners && s.Status == "busy" && (known == null || known.Status == "idle" || known.Status == "waiting")) return s.Status;
        return known != null ? known.Status : s.Status;
    }
    /// The conversations the tools' own hooks tell of (Reach.cs). The window around this agent may put the hooks into
    /// the tools' settings; whether or not it has, this listens for them.
    public readonly Reach Reach;
    /// How much of each tool's usage limits the owner's account has used (Quotas.cs).
    public readonly Quotas Quotas;
    public string DataFolder { get { return dataDir; } }
    /// What the owner said of single conversations from the phone: pinned, hidden (Marks.cs).
    public readonly Marks Marks;
    /// Which conversation another AI continues from which (Handovers.cs): shown as one conversation.
    public readonly Handovers Handovers;
    readonly Organizer Organizer;

    Dictionary<string, object>[] OrganizeModels(string tool) {
        var byTool = (Dictionary<string, object>)Models.Listed(only: tool)["models"];
        object listed;
        return byTool.TryGetValue(tool, out listed) ? (Dictionary<string, object>[])listed : new Dictionary<string, object>[0];
    }

    bool OrganizeReadable(Organizer.Source source) {
        var prefs = Preferences();
        if (Get(prefs, "RemoteEnabled") != "True") return false;
        if (AllDirs(prefs).Values.Any(folder => SamePath(folder, source.Folder))) return true;
        return source.Dir.Length == 0 && sessions.Any(s => s.Tool == source.Tool && s.Id == source.Id && s.Dir.Length == 0 && SamePath(s.Folder, source.Folder));
    }

    bool OrganizeBusy(Organizer.Source source) {
        var known = Reach.Of(source.Tool, source.Id);
        if (known != null && (known.Status == "busy" || known.Status == "confirm")) return true;
        lock (work) {
            if (terminals.Values.Any(t => t.Tool == source.Tool && (t.Session == source.Id || (t.Chat != null && t.Chat.Session == source.Id))
                    && !t.Pty.Closed && (t.Status == "busy" || t.Status == "confirm" || (t.Chat != null && t.Chat.Status == "busy")))) return true;
        }
        var saved = sessions.FirstOrDefault(s => s.Tool == source.Tool && s.Id == source.Id);
        if (saved == null || !saved.Live) return false;
        var native = NativeState(saved);
        return native != null ? native.Busy : saved.Status == "busy" || saved.Status == "confirm";
    }
    // A conversation as the phone is told of it: what the scan found, and where a hook or its application has told
    // more, how it stands now, what it asks, what it last said, and what the phone can do to it and by which way
    // (Ways.cs: `reach` by name, `ways` for each thing, `why` for what cannot be done now).
    object Listed(SessionInfo s) {
        var native = NativeState(s);
        if (native != null) Reach.ObserveDesktop(s.Id, native.Ready, native.Takes, native.At);
        var known = s.Live ? Reach.Of(s.Tool, s.Id) : null;
        var agy = AgyState(s);
        var ways = Ways.Of(s, known, native, agy);
        bool asks = ways.Status == "confirm";
        var organize = Organizer.ForList(s.Tool, s.Id, s.Title, s.Named);
        return new { id = s.Id, tool = s.Tool, dir = s.Dir, folder = s.Folder, kind = s.Kind, pinned = Marks.Is("pinned", s.Tool, s.Id), hidden = Marks.Is("hidden", s.Tool, s.Id), now = ways.Now, title = s.Title, organize = organize, updated = s.Updated, live = s.Live, host = s.Host, origin = s.Origin, status = ways.Status, can_takeover = s.CanTakeover, ownership_known = s.OwnershipKnown, takeover_reason = s.TakeoverReason,
            reach = ways.Reach, ways = s.Live ? ways.Named : null,
            // (only what is of use now: why it cannot be told or ended, why it cannot be stopped while it works, why it cannot be answered while it asks)
            why = s.Live ? ways.Why.Where(one => one.Key == "say" || one.Key == "end" || (one.Key == "stop" && ways.Status == "busy") || (one.Key == "answer" && asks)).ToDictionary(one => one.Key, one => one.Value) : null,
            transport = ways.Transport, turn = ways.Turn, wake = ways.Woken, completion = native != null && !native.CompletionReady ? "" : CompletionOf(s.Tool, s.Id, ways.Status),
            // the operation it came of, where it began in a desktop application that was opened for it from the phone
            opened = s.Live || AgyAppSession(s) ? Desktop.CameOf(s.Tool, s.Id, s.ControlFolder, s.Created, s.Live && Ways.InApplication(s.Origin)) : "",
            // what the phone said to it and it has not taken yet, each with its number: waiting, or held since it was told to stop
            queued = Reach.Outbox.Of(s.Tool, s.Id).Select(item => new { id = item.Id, text = item.Text.Length > 200 ? item.Text.Substring(0, 199) + "…" : item.Text, state = item.State, at = Milliseconds(item.At) }).ToArray(),
            ask = agy != null ? AgyAsk(agy) : known != null && known.Status == "confirm" ? new { id = known.AskId, text = known.AskText, options = known.AskOptions } : null, last = known != null ? known.Last : "",
            // the collaboration task it is or was a member of, while the task is kept ("" for none)
            team = Teams.TeamOf(s.Tool, s.Id) };
    }
    static bool AgyAppSession(SessionInfo s) { return s.Tool == "agy" && (s.Kind == Ways.AgyDesktop || s.Origin == Ways.AgyDesktop && s.Host == "shared"); }
    AntigravityDesktopClient.State AgyState(SessionInfo s) { return s.Live && AgyAppSession(s) ? AgyDesktop.Get(s.Id) : null; }
    void RefreshAgy(string session) {
        AgyDesktop.Refresh(new[] { session }.Concat(AgyDesktop.All().Select(s => s.Id)));
    }
    static object AgyAsk(AntigravityDesktopClient.State s) {
        return s != null && s.Waiting && !String.IsNullOrEmpty(s.AskId) ? new { id = s.AskId, kind = s.AskKind, text = s.AskText, options = s.AskOptions } : null;
    }
    Ways WaysOf(SessionInfo s, Reach.Open known) { return Ways.Of(s, known, NativeState(s), AgyState(s)); }
    readonly Dictionary<string, string[]> completions = new Dictionary<string, string[]>();
    string CompletionOf(string tool, string session, string status) {
        if (String.IsNullOrEmpty(session) || status == "busy" || status == "confirm" || Reach.Outbox.Of(tool, session).Length > 0) return "";
        var known = Reach.Of(tool, session);
        if (known != null && (known.Status == "busy" || known.Status == "confirm")) return "";
        if (tool != "codex") return known == null ? "" : known.Completion;
        // Codex's explicit turn events, not file modification times or pauses in terminal output.
        string file;
        if (!codexFiles.TryGetValue(session, out file)) return "";
        try {
            var info = new FileInfo(file);
            string stamp = info.Length + "." + info.LastWriteTimeUtc.Ticks; string[] cached;
            if (completions.TryGetValue(file, out cached) && cached[0] == stamp) return cached[1];
            string completedTurn = CompletedTurn(ReadPart(file, true, 256 * 1024));
            if (completions.Count > 200) completions.Clear();
            completions[file] = new[] { stamp, completedTurn };
            return completedTurn;
        } catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; }
    }
    public static string CompletedTurn(string record) { return Transcripts.CompletedTurn(record); }
    // How a message stands, for the phone: the same answer whatever carries it, and for one that is asked after again.
    static Dictionary<string, object> Told(Outbox.Item item, string way) {
        return new Dictionary<string, object> { { "status", "accepted" }, { "sent", true }, { "now", item.State == Outbox.Handed }, { "way", way }, { "state", item.State }, { "message", item.Id } };
    }
    readonly Dictionary<string, object> awakening = new Dictionary<string, object>();      // one at a time for each conversation
    readonly HashSet<string> retried = new HashSet<string>();                              // items this run has tried to hand over by itself
    /// Hands what waits for a conversation that sits idle to it, by its tool's own way (Tools.cs): it is woken where
    /// it is, and stays where it is. Throws what the tool refuses, in words for the person.
    void Awaken(string tool, string session) {
        object one;
        lock (awakening) { string key = tool + " " + session.ToLowerInvariant(); if (!awakening.TryGetValue(key, out one)) awakening[key] = one = new object(); }
        lock (one) Tool.Named(tool).Wake(FindTool(tool), dataDir, session, Reach, WakeSeconds);
    }
    /// What waited when this program stopped, or was released while nothing was at hand to carry it, is handed over
    /// once the conversations have been looked at: each item is tried once by this, and stays for the phone to see
    /// if that fails.
    void Recover() {
        foreach (string[] pending in Reach.Outbox.Pending()) {
            string tool = pending[0], session = pending[1];
            SessionInfo found; Outbox.Item[] waiting;
            lock (work) {
                found = sessions.FirstOrDefault(s => s.Tool == tool && s.Live && String.Equals(s.Id, session, StringComparison.OrdinalIgnoreCase));
                waiting = Reach.Outbox.Of(tool, session).Where(item => item.State == Outbox.Queued && DateTime.UtcNow - item.At > TimeSpan.FromSeconds(WakeSeconds + 30) && !relaying.Contains(item.Id) && !retried.Contains(item.Id)).ToArray();
                if (found == null || waiting.Length == 0 || WaysOf(found, Reach.Of(tool, found.Id)).Say != Ways.Wake) continue;
                foreach (var item in waiting) retried.Add(item.Id);
            }
            try { Awaken(tool, found.Id); } catch (InvalidOperationException) { } catch (IOException) { } catch (System.ComponentModel.Win32Exception) { }
        }
    }
    // ---- the owner's work: in numbers, asked at a set time, handed from one conversation to another
    /// How the work with the tools went, by day, tool, project and hour (Stats.cs).
    public readonly Stats Stats;
    /// The questions set to be asked at a time (Schedules.cs).
    public readonly Schedules Schedules;
    /// What is passed on from one conversation to another when the first has stopped (Chains.cs).
    public readonly Chains Chains;
    /// Work several AIs do together (Teams.cs): their members are conversations of the phone's that this program begins.
    public readonly Teams Teams;
    /// The models a team's members can be begun with, as the tools list them (Models.cs).
    public readonly Models Models;
    /// Isolated Git worktrees for parallel comparison tasks (Worktrees.cs).
    public readonly Worktrees Worktrees;
    /// Local MCP service that lets an active conversation ask another AI in the same project for read-only advice (Peers.cs).
    public readonly Peers Peers;
    /// Work a conversation the phone runs hands to another AI by itself (Delegations.cs): each task is a conversation
    /// of the phone's that this program begins.
    public readonly Delegations Delegations;
    // ---- the tasks a conversation gives out: conversations the phone runs, begun by this program
    /// Begins a task's conversation beside the one that gives it out: in its folder (or worktree), never working more
    /// widely than it, or in a new worktree of the project where that is asked.
    Delegations.Began BeginTask(Delegations.Opening one) {
        var prefs = Preferences(); var dirs = AllDirs(prefs);
        Tool kind = Tool.Of(one.Tool);
        if (kind == null || !kind.Chats) throw new ArgumentException("不能派给 " + one.Tool + "：它不在可派的 AI 里");
        string launcher = FindTool(one.Tool);
        if (launcher == null) throw new ArgumentException("电脑上没有安装 " + kind.Label);
        lock (work) {
            LiveTerminal parent;
            if (!terminals.TryGetValue(one.Parent, out parent) || parent.Pty.Closed || parent.Chat == null) throw new InvalidOperationException("原对话已结束");
            RequireTerminalSlot();
            string mode = ClampMode(parent.Mode, Get(prefs, "RemoteMaxMode"));
            var live = new LiveTerminal { Id = Guid.NewGuid().ToString("N"), Tool = one.Tool, Dir = parent.Dir, Folder = parent.Folder, Model = one.Model, Mode = mode, Title = one.Title };
            if (one.Worktree) {
                if (parent.Dir.Length == 0 || !dirs.ContainsKey(parent.Dir)) throw new ArgumentException("这段对话不在项目里，不能用独立工作树");
                var made = Worktrees.CreateSessionWorktree(dirs[parent.Dir], live.Id.Substring(0, 12));
                live.WorktreeId = made.Id; live.WorktreePath = made.Path; live.Branch = made.Branch;
            } else { live.WorktreeId = parent.WorktreeId; live.WorktreePath = parent.WorktreePath; live.Branch = parent.Branch; }
            string place = Place(live, dirs);
            if (place == null || !Directory.Exists(place)) throw new InvalidOperationException("原对话的文件夹已不存在");
            live.Pty = Chat.Open(one.Tool, launcher, place, "", false, mode, Emitted(live), Quotas, Peers.Offer(one.Tool, place, live.Id, live.Dir, true), one.Model, "");
            Heard(live);
            terminals[live.Id] = live;
            live.Chat.Say(one.Words, null);
            soon = true;
            return new Delegations.Began { Terminal = live.Id, Worktree = one.Worktree ? live.WorktreePath : "", Branch = one.Worktree ? live.Branch : "" };
        }
    }
    /// How a conversation the phone runs stands now; "" where it has ended or is gone.
    /// For the lists: of a conversation that is a task, the one that gave it out (`parent`) or the task's id; "" else.
    string TaskOf(string terminal, string which) {
        var task = Delegations.Of(terminal);
        return task == null ? "" : which == "parent" ? task.Parent : task.Id;
    }
    string StateOf(string terminal) {
        lock (work) {
            LiveTerminal live;
            if (!terminals.TryGetValue(terminal ?? "", out live) || live.Pty.Closed || live.Chat == null) return "";
            return live.Chat.Status == "ended" ? "" : live.Chat.Status;
        }
    }
    bool AnswerIn(string terminal, string ask, string decision) {
        try { Member(terminal).Answer(ask, decision); return true; }
        catch (InvalidOperationException) { return false; }
    }
    void CardIn(string terminal, Dictionary<string, object> card) {
        Chat chat = null;
        lock (work) { LiveTerminal live; if (terminals.TryGetValue(terminal ?? "", out live) && !live.Pty.Closed) chat = live.Chat; }
        if (chat != null) chat.Card(card);
    }
    // ---- the members of a team: conversations the phone runs, begun and told by this program
    string BeginMember(Teams.Opening one) {
        var prefs = Preferences(); var dirs = AllDirs(prefs);
        if (!dirs.ContainsKey(one.Project)) throw new ArgumentException("目录未获允许");
        Tool kind = Tool.Named(one.Tool);
        if (!kind.Chats) throw new ArgumentException(kind.Label + " 不能作为协作成员");
        string launcher = FindTool(one.Tool);
        if (launcher == null) throw new ArgumentException("电脑上没有 " + kind.Label);
        lock (work) {
            RequireTerminalSlot();
            // read only where it is asked for, and never wider than its owner allows
            string widest = Get(prefs, "RemoteMaxMode"), mode = one.Read ? "read" : widest;
            string first = one.Words.Split('\n')[0].Trim();
            bool resume = one.Session.Length > 0;
            var live = new LiveTerminal { Id = Guid.NewGuid().ToString("N"), Tool = one.Tool, Dir = one.Project, Session = one.Session, Model = one.Model, Effort = one.Effort, Mode = mode, Title = first.Length > 60 ? first.Substring(0, 59) + "…" : first };
            string place = one.Folder.Length > 0 ? one.Folder : Place(live, dirs);
            live.Pty = Chat.Open(one.Tool, launcher, place, one.Session, resume, mode, Emitted(live), Quotas, one.Read ? null : Peers.Offer(one.Tool, place), one.Model, one.Effort);
            Heard(live);
            terminals[live.Id] = live;
            if (resume) Recall(live, place);
            if (one.Words.Length > 0 || (one.Attachments != null && one.Attachments.Count > 0)) live.Chat.Say(one.Words, null, one.Attachments);
            return live.Id;
        }
    }
    Worktrees.Created MakeTeamWorktree(string dir, string teamId, string tool) {
        string folder;
        if (!AllDirs(Preferences()).TryGetValue(dir, out folder)) throw new ArgumentException("目录未获允许");
        return Worktrees.Create(folder, teamId, tool);
    }
    string ApplyTeamWorktree(string dir, string wtPath, string baseCommit) {
        string folder;
        if (!AllDirs(Preferences()).TryGetValue(dir, out folder)) throw new ArgumentException("目录未获允许");
        return Worktrees.Apply(folder, wtPath, baseCommit);
    }
    void CleanTeamWorktrees(string dir, string teamId) {
        string folder = null;
        AllDirs(Preferences()).TryGetValue(dir, out folder);
        Worktrees.RemoveAll(folder, teamId);
    }
    Chat Member(string terminal) {
        LiveTerminal live;
        lock (work) if (!terminals.TryGetValue(terminal, out live) || live.Pty.Closed || live.Chat == null) throw new InvalidOperationException("这个成员的对话已经结束");
        return live.Chat;
    }
    void SayToMember(string terminal, string words) { Member(terminal).Say(words, null); }
    void SayToMember(string terminal, string words, IList<Attachment> attachments) { Member(terminal).Say(words, null, attachments); }
    Attachment ReadQueuedAttachment(string project, string path) {
        string folder;
        if (!AllDirs(Preferences()).TryGetValue(project, out folder)) return null;
        try { return Attachments.Read(folder, new[] { path })[0]; }
        catch (ArgumentException) { return null; }
    }
    void StopMember(string terminal) { try { Member(terminal).Stop(); } catch (InvalidOperationException) { } }
    /// Starts the program of a conversation the phone runs anew in the same terminal, going on with the same
    /// conversation: an account signed in or a quota renewed since is what it works with then. `model`/`effort`: null
    /// keeps what it had. False where the terminal is gone or its tool has not named the conversation yet.
    bool RestartMember(string terminal, string model, string effort) {
        lock (work) {
            LiveTerminal live;
            if (!terminals.TryGetValue(terminal, out live) || live.Chat == null || live.Pty.Closed) return false;
            var old = live.Chat;
            string session = old.Session.Length > 0 ? old.Session : live.Session, launcher = FindTool(live.Tool);
            if (session.Length == 0) return false;
            if (launcher == null) throw new InvalidOperationException("电脑上没有 " + Label(live.Tool));
            if (model != null && model.Length > 0 && !Models.Valid(model)) throw new ArgumentException("模型名称无效");
            effort = effort == null ? null : Models.CleanEffort(effort);
            // never wider than its owner allows now
            string widest = Get(Preferences(), "RemoteMaxMode"), mode = live.Mode == "read" ? "read" : widest;
            string place = old.Folder;
            lock (gate) live.Generation++;          // what the program that ends now still says is not passed on
            old.Dispose();
            live.Session = session;
            if (model != null) live.Model = model;
            if (effort != null) live.Effort = effort;
            live.Mode = mode;
            var emit = Emitted(live);
            emit(json.Serialize(new Dictionary<string, object> { { "t", "note" }, { "text", "已重新启动 " + Label(live.Tool) + (live.Model.Length > 0 ? " · " + live.Model : "") }, { "at", Milliseconds(DateTime.UtcNow) } }) + "\n");
            string promoteId = Teams.TeamOf(live.Id, live.Tool, session).Length > 0 ? "" : live.Id;
            live.Pty = Chat.Open(live.Tool, launcher, place, session, true, mode, emit, Quotas, mode == "read" ? null : Peers.Offer(live.Tool, place, promoteId, live.Dir, Delegations.Of(live.Id) != null), live.Model, live.Effort);
            Heard(live);
            soon = true;
            return true;
        }
    }
    /// Closes a member's terminal as the phone's "close" does; one already closed is left as it is.
    void CloseMember(string terminal) {
        lock (work) {
            LiveTerminal live;
            if (!terminals.TryGetValue(terminal, out live) || live.Pty.Closed) return;
            live.Pty.Dispose();
            soon = true; nextScan = DateTime.UtcNow.AddSeconds(1.5);
        }
    }
    /// What changed in a project's folder, as git says it: the files, and the changes themselves (a folder that is
    /// no repository says nothing).
    string ChangesIn(string dir) {
        string folder;
        return AllDirs(Preferences()).TryGetValue(dir, out folder) ? RemoteCli.Worktrees.ChangesIn(folder) : "";
    }
    /// Fast read-only workspace snapshot for first-turn collaboration prompts (Teams.cs).
    string SnapshotIn(string dir) {
        string folder;
        return AllDirs(Preferences()).TryGetValue(dir, out folder) ? WorkspaceSnapshot.Build(folder) : "";
    }
    readonly HashSet<string> autoCpRunning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    void ScheduleAutoCheckpoint(string folder, string tool) {
        if (String.IsNullOrWhiteSpace(folder)) return;
        lock (autoCpRunning) {
            if (!autoCpRunning.Add(folder)) return;
        }
        Task.Run(() => {
            try { Worktrees.CaptureAutoCheckpoint(folder, "auto · " + tool); }
            catch { }
            finally { lock (autoCpRunning) autoCpRunning.Remove(folder); }
        });
    }
    /// A conversation the phone runs tells a team when a turn of it ends, and captures a low-overhead hidden Git checkpoint.
    void Heard(LiveTerminal live) {
        var chat = live.Chat;
        if (chat != null) chat.AskedFor += (ask, kind, title, text) => {
            if (live.Chat == chat) Delegations.Asked(live.Id, ask, kind, title, text, chat.ChoicesOf(ask));
        };
        if (chat != null) chat.Turned += (how, words) => {
            if (live.Chat == chat) {
                Teams.Turned(live.Id, how, words);
                Delegations.Turned(live.Id, how, words);
                if ((how == "done" || how == "idle") && live.Mode != "read" && !String.IsNullOrWhiteSpace(live.Folder))
                    ScheduleAutoCheckpoint(live.Folder, live.Tool);
            }
        };
    }
    bool CanPromoteTerminal(string terminalId) {
        if (String.IsNullOrEmpty(terminalId)) return false;
        string tool, session;
        lock (work) {
            LiveTerminal live;
            if (!terminals.TryGetValue(terminalId, out live) || live.Pty.Closed || live.Chat == null) return false;
            if (String.IsNullOrEmpty(live.Dir) || live.Mode == "read") return false;
            tool = live.Tool;
            session = live.Chat.Session.Length > 0 ? live.Chat.Session : live.Session;
        }
        return Teams.TeamOf(terminalId, tool, session).Length == 0;
    }
    bool ConfirmInTerminal(string terminalId, string kind, string title, string detail, CancellationToken cancel) {
        Chat chat = null;
        lock (work) {
            LiveTerminal live;
            if (terminals.TryGetValue(terminalId, out live) && !live.Pty.Closed) chat = live.Chat;
        }
        if (chat == null) return false;
        Wake();
        bool allowed = chat.Confirm(kind, title, detail, cancel);
        Wake();
        return allowed;
    }
    string ExtractConversationContext(string tool, string session, string dir, string folder) {
        if (String.IsNullOrEmpty(session)) return "";
        try {
            string file = ConversationFile(tool, session, folder);
            if (file == null) return "";
            var read = Said(file, tool);
            var said = ((IEnumerable)read["messages"]).Cast<Dictionary<string, object>>().ToList();
            if (said.Count == 0) return "";
            Func<string, int, string> cut = (text, most) => ProcessText.TruncateSafe((text ?? "").Trim(), most, "…");
            var asked = said.Where(m => Convert.ToString(m["role"]) == "user").Select(m => Convert.ToString(m["text"])).Where(s => !String.IsNullOrWhiteSpace(s)).ToList();
            string answered = said.Where(m => Convert.ToString(m["role"]) == "assistant").Select(m => Convert.ToString(m["text"])).LastOrDefault(s => !String.IsNullOrWhiteSpace(s)) ?? "";
            var steps = said.Where(m => Convert.ToString(m["role"]) == "tool").Select(m => Convert.ToString(m["text"])).Where(s => !String.IsNullOrWhiteSpace(s)).ToList();
            var sb = new StringBuilder();
            if (asked.Count > 0) sb.AppendLine("最初要求：" + cut(asked[0], 600));
            if (asked.Count > 1) sb.AppendLine("最近要求：" + cut(asked[asked.Count - 1], 600));
            if (answered.Length > 0) sb.AppendLine("最近结论：" + cut(answered, 1800));
            if (steps.Count > 0) sb.AppendLine("最近操作：\n" + String.Join("\n", steps.Skip(Math.Max(0, steps.Count - 6)).Select(step => "- " + cut(step, 140))));
            return sb.ToString().Trim();
        } catch (Exception) { return ""; }
    }
    Peers.CollabStarted PromoteFromMcp(string terminalId, Dictionary<string, object> item) {
        Teams.SourceChat source;
        Chat chat;
        string folder = "";
        lock (work) {
            LiveTerminal live;
            if (!terminals.TryGetValue(terminalId, out live) || live.Pty.Closed || live.Chat == null)
                throw new InvalidOperationException("当前对话已结束");
            if (String.IsNullOrEmpty(live.Dir))
                throw new InvalidOperationException("当前对话不在项目目录中，无法转为协作");
            if (live.Mode == "read")
                throw new InvalidOperationException("只读对话无法转为协作");
            chat = live.Chat;
            folder = chat.Folder;
            source = new Teams.SourceChat {
                Terminal = live.Id,
                Tool = live.Tool,
                Dir = live.Dir,
                Session = chat.Session.Length > 0 ? chat.Session : live.Session,
                Model = live.Model,
                Effort = live.Effort,
                InTurn = true
            };
        }
        if (String.IsNullOrWhiteSpace(Get(item, "context")))
            source.Context = ExtractConversationContext(source.Tool, source.Session, source.Dir, folder);
        string prompt;
        var team = Teams.Promote(item, source, out prompt);
        chat.Notice("已转为协作任务（" + (team.Mode == "review" ? "写审" : "分工协作") + "）");
        Wake();
        return new Peers.CollabStarted { TeamId = team.Id, Prompt = prompt };
    }
    string[] AvailableChatTools() { return new[] { "claude", "codex", "agy" }.Where(t => Tool.Named(t).Chats && FindTool(t) != null).ToArray(); }
    readonly Queue<string[]> rested = new Queue<string[]>();       // conversations that have just stopped by themselves: tool, number
    static readonly string[] WorkActions = { "work_stats", "work_schedules", "work_schedule_set", "work_schedule_remove", "work_handoff", "work_brief", "work_chains", "work_chain_set", "work_chain_remove",
        "work_teams", "work_team_start", "work_team_stop", "work_team_resume", "work_team_remove", "work_team_say", "work_team_pick", "work_team_swap", "work_team_models" };
    /// One rule runs: the last answer of the conversation that stopped goes to the other one, with its owner's words.
    void Pass(Chains.Item rule, Dictionary<string, object> prefs) {
        bool ok = false; string note;
        try {
            var from = sessions.FirstOrDefault(s => s.Tool == rule.FromTool && s.Id == rule.From);
            var to = Taking(rule.ToTool, rule.To);
            if (from == null) note = "没有找到做完的那段对话";
            else if (to == null) note = "接手的对话没有在电脑上开着";
            else {
                var read = ReadConversation(new Dictionary<string, object> { { "session", from.Id }, { "tool", from.Tool }, { "dir", from.Dir } }, prefs);
                string last = ((System.Collections.IEnumerable)read["messages"]).Cast<Dictionary<string, object>>().Where(m => Convert.ToString(m["role"]) == "assistant").Select(m => Convert.ToString(m["text"])).LastOrDefault() ?? "";
                if (last.Length > 3000) last = last.Substring(0, 3000) + "…";
                note = Tell(to, "【对话接力】另一段对话（" + Label(from.Tool) + "，“" + from.Title + "”）刚做完一步。" + (last.Length > 0 ? "它最后的回答：\n\n" + last + "\n\n" : "\n\n") + "我的要求：\n" + rule.Text, prefs, out ok);
            }
        } catch (Exception problem) { note = problem is ArgumentException || problem is InvalidOperationException ? problem.Message : "没有发出"; }
        Chains.Done(rule.Id, ok, note);
    }
    static string Label(string tool) { return Tool.LabelOf(tool); }
    // The name a folder goes by in the numbers: its project's where it is one (or lies in one), else its own.
    string NamedFolder(string folder, Dictionary<string, string> dirs) {
        if (String.IsNullOrEmpty(folder)) return "";
        string plain = folder.TrimEnd('\\', '/');
        var within = dirs.Where(d => SamePath(d.Value, plain) || plain.StartsWith(d.Value.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase)).OrderByDescending(d => d.Value.Length).FirstOrDefault();
        if (within.Key != null) return within.Key;
        try { return Path.GetFileName(plain); } catch (ArgumentException) { return ""; }
    }
    // The terminal of the phone's that runs a conversation now, or null.
    LiveTerminal OwnTerminal(string tool, string session) {
        return terminals.Values.FirstOrDefault(t => !t.Pty.Closed && t.Tool == tool && String.Equals(t.Session, session, StringComparison.OrdinalIgnoreCase));
    }
    /// A conversation that something can be said to: one this computer lists that a program has open, or that runs
    /// in one of the phone's own terminals. Null where there is none.
    SessionInfo Taking(string tool, string session) {
        return sessions.FirstOrDefault(s => s.Tool == tool && s.Id == session && (s.Live || OwnTerminal(tool, session) != null));
    }
    /// Says something to a conversation, as the phone would. One that runs in a terminal of the phone's is told
    /// there, the way its page sends a message: the words as pasted text, then Enter. One that a program on this
    /// computer has open is told by the way that is there now (Ways). Returns in words how it went; `ok` false
    /// where it could not be said.
    string Tell(SessionInfo to, string text, Dictionary<string, object> prefs, out bool ok) {
        LiveTerminal own = OwnTerminal(to.Tool, to.Id);
        if (own != null && own.Chat != null) {
            // a conversation run through its tool's structured mode is told through it, at work or not
            own.Chat.Say(text.Trim(), null);
            lastInput = DateTime.UtcNow;
            ok = true;
            return "已发到手机上的对话";
        }
        if (own != null) {
            // Enter would say yes to a question it waits on: that is answered where the question is read
            var asks = Reach.Of(to.Tool, to.Id);
            if (asks != null && asks.Status == "confirm") { ok = false; return "这个终端正在等待确认，没有发出"; }
            own.Pty.Write("\u001b[200~" + text.Replace("\r\n", "\n").Replace('\r', '\n').Trim() + "\u001b[201~\r");
            lastInput = DateTime.UtcNow;
            ok = true;
            return "已发到手机终端";
        }
        string id = Guid.NewGuid().ToString("N");
        var ways = WaysOf(to, Reach.Of(to.Tool, to.Id));
        var op = new Dictionary<string, object> { { "id", id }, { "action", "session_say" }, { "tool", to.Tool }, { "session", to.Id }, { "dir", to.Dir }, { "text", text },
            { "at", (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds } };
        if (ways.Say == Ways.App) op["transport"] = Ways.App;
        Execute(op, prefs);
        Dictionary<string, object> done;
        if (completed.TryGetValue(id, out done)) {
            string error = Get(done, "error");
            completed.Remove(id); reported.Remove(id);         // (asked here, not by a relay: there is nobody to report it to)
            ok = error.Length == 0;
            return ok ? "已交给对话" : error;
        }
        ok = true;      // on its way: an idle conversation is being woken for it, or the application's channel has it
        return "正在送达";
    }
    /// What a conversation is handed over with: what its owner asked first and last, what the tool answered last and
    /// did last, taken from its record. Written as its owner's own words to the conversation that takes over.
    internal static string Brief(SessionInfo from, Dictionary<string, object> read, string note) {
        var said = ((System.Collections.IEnumerable)read["messages"]).Cast<Dictionary<string, object>>().ToList();
        Func<string, int, string> cut = (text, most) => { text = (text ?? "").Trim(); return text.Length <= most ? text : text.Substring(0, Char.IsHighSurrogate(text[most - 1]) ? most - 1 : most) + "…"; };
        var asked = said.Where(m => Convert.ToString(m["role"]) == "user").Select(m => Handovers.StripAsk(Convert.ToString(m["text"]))).ToList();
        string answered = said.Where(m => Convert.ToString(m["role"]) == "assistant").Select(m => Convert.ToString(m["text"])).LastOrDefault() ?? "";
        var steps = said.Where(m => Convert.ToString(m["role"]) == "tool").ToList();
        var brief = new StringBuilder("【对话交接】下面是我在另一段对话里的进展（" + Label(from.Tool) + "，“" + cut(from.Title, 100) + "”" + (from.Dir.Length > 0 ? "，项目 " + cut(from.Dir, 100) : "") + "），请接着处理。\n");
        brief.Append("原对话 ID：" + cut(from.Id, 128) + "\n");
        if (asked.Count > 0) brief.Append("\n我最初的要求：\n" + cut(asked[0], 800) + "\n");
        if (asked.Count > 1) brief.Append("\n我最近的要求：\n" + cut(asked[asked.Count - 1], 800) + "\n");
        if (answered.Length > 0) brief.Append("\n它最后的回答：\n" + cut(answered, 2000) + "\n");
        if (steps.Count > 0) {
            brief.Append("\n它最近做的操作：\n");
            foreach (var step in steps.Skip(Math.Max(0, steps.Count - 8))) {
                brief.Append("- " + cut(Get(step, "text"), 160) + "\n");
                string result = cut(Get(step, "result"), 160);
                brief.Append(result.Length == 0 ? "  记录中没有返回内容\n" : "  返回摘录：" + result.Replace("\n", "\n  ") + "\n");
            }
        }
        if (!String.IsNullOrWhiteSpace(note)) brief.Append("\n我的补充：\n" + cut(note, 1000) + "\n");
        return brief.ToString().TrimEnd();
    }
    /// Short resume brief when switching back to an AI that paused earlier in the same handover thread:
    /// summarizes only what happened while other AI(s) handled it since this session last paused.
    internal static string ResumeBrief(SessionInfo from, string betweenTools, List<Dictionary<string, object>> deltaMessages) {
        Func<string, int, string> cut = (text, most) => { text = (text ?? "").Trim(); return text.Length <= most ? text : text.Substring(0, Char.IsHighSurrogate(text[most - 1]) ? most - 1 : most) + "…"; };
        var asked = deltaMessages.Where(m => Convert.ToString(m["role"]) == "user").Select(m => Handovers.StripAsk(Convert.ToString(m["text"]))).Where(s => !String.IsNullOrWhiteSpace(s)).ToList();
        string answered = deltaMessages.Where(m => Convert.ToString(m["role"]) == "assistant").Select(m => Convert.ToString(m["text"])).LastOrDefault() ?? "";
        var steps = deltaMessages.Where(m => Convert.ToString(m["role"]) == "tool").ToList();
        var brief = new StringBuilder("【对话交接】上面你暂停之后，我切到 " + betweenTools + " 继续处理了一段" + (from.Dir.Length > 0 ? "（项目 " + cut(from.Dir, 100) + "）" : "") + "，现在切回你这里接着做。\n");
        if (asked.Count == 1) brief.Append("\n这段期间我的要求：\n" + cut(asked[0], 800) + "\n");
        else if (asked.Count > 1) {
            brief.Append("\n这段期间我最初的要求：\n" + cut(asked[0], 800) + "\n");
            brief.Append("\n这段期间我最近的要求：\n" + cut(asked[asked.Count - 1], 800) + "\n");
        }
        if (answered.Length > 0) brief.Append("\n对方最后的回答：\n" + cut(answered, 2000) + "\n");
        if (steps.Count > 0) {
            brief.Append("\n对方最近做的操作：\n");
            foreach (var step in steps.Skip(Math.Max(0, steps.Count - 8))) {
                brief.Append("- " + cut(Get(step, "text"), 160) + "\n");
                string result = cut(Get(step, "result"), 160);
                brief.Append(result.Length == 0 ? "  记录中没有返回内容\n" : "  返回摘录：" + result.Replace("\n", "\n  ") + "\n");
            }
        }
        return brief.ToString().TrimEnd();
    }
    DateTime nextAsking = DateTime.MinValue;
    /// Asks what is due (called with `work` held, as often as the agent looks around).
    void Ask(Dictionary<string, object> prefs) {
        // what follows on a conversation that has just stopped is started at once
        string[][] stopped;
        lock (rested) { stopped = rested.ToArray(); rested.Clear(); }
        if (Get(prefs, "RemoteEnabled") == "True") foreach (var one in stopped) foreach (var rule in Chains.After(one[0], one[1])) Pass(rule, prefs);
        if (DateTime.UtcNow < nextAsking) return;
        nextAsking = DateTime.UtcNow.AddSeconds(15);
        if (Get(prefs, "RemoteEnabled") != "True") return;
        foreach (var due in Schedules.Due(DateTime.Now)) {
            bool ok = false; string note;
            var to = Taking(due.Tool, due.Session);
            if (to == null) note = "这段对话没有在电脑上开着";
            else { try { note = Tell(to, due.Text, prefs, out ok); } catch (Exception problem) { note = problem is ArgumentException || problem is InvalidOperationException ? problem.Message : "没有发出"; } }
            Schedules.Done(due.Id, ok, note, DateTime.Now);
        }
    }
    Dictionary<string, object> Work(string action, Dictionary<string, object> op, Dictionary<string, object> prefs) {
        if (action == "work_schedules") return new Dictionary<string, object> { { "schedules", Schedules.All().Select(Schedules.Shown).ToArray() } };
        if (action == "work_schedule_set") {
            // for a conversation this computer lists: its title is kept with it, to name it by later
            var item = (op.ContainsKey("item") ? op["item"] : null) as Dictionary<string, object>;
            if (item == null) throw new ArgumentException("定时提问无效");
            var of = sessions.FirstOrDefault(s => s.Tool == Get(item, "tool") && s.Id == Get(item, "session"));
            if (of == null) throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
            item["title"] = of.Title;
            return new Dictionary<string, object> { { "schedule", Schedules.Shown(Schedules.Set(item, DateTime.Now)) } };
        }
        if (action == "work_schedule_remove") {
            if (!Schedules.Remove(Get(op, "schedule"))) throw new ArgumentException("没有找到这个定时提问");
            return new Dictionary<string, object>();
        }
        if (action == "work_chains") return new Dictionary<string, object> { { "chains", Chains.All().Select(Chains.Shown).ToArray() } };
        if (action == "work_chain_set") {
            var item = (op.ContainsKey("item") ? op["item"] : null) as Dictionary<string, object>;
            if (item == null) throw new ArgumentException("接力无效");
            var first = sessions.FirstOrDefault(s => s.Tool == Get(item, "from_tool") && s.Id == Get(item, "from"));
            var then = sessions.FirstOrDefault(s => s.Tool == Get(item, "to_tool") && s.Id == Get(item, "to"));
            if (first == null || then == null) throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
            item["from_title"] = first.Title; item["to_title"] = then.Title;
            return new Dictionary<string, object> { { "chain", Chains.Shown(Chains.Set(item)) } };
        }
        if (action == "work_chain_remove") {
            if (!Chains.Remove(Get(op, "chain"))) throw new ArgumentException("没有找到这条接力");
            return new Dictionary<string, object>();
        }
        if (action == "work_teams") return new Dictionary<string, object> { { "teams", Teams.All().Select(Teams.Shown).ToArray() } };
        if (action == "work_team_start") {
            var item = (op.ContainsKey("item") ? op["item"] : null) as Dictionary<string, object>;
            if (item == null) throw new ArgumentException("协作任务无效");
            string fromTerminal = Get(item, "from_terminal").Trim();
            if (fromTerminal.Length > 0) {
                LiveTerminal live;
                if (!terminals.TryGetValue(fromTerminal, out live) || live.Pty.Closed || live.Chat == null)
                    throw new InvalidOperationException("原对话已结束，无法转为协作");
                if (live.Mode == "read")
                    throw new InvalidOperationException("只读对话无法转为协作");
                if (String.IsNullOrEmpty(live.Dir))
                    throw new InvalidOperationException("这段对话不在项目里，无法转为协作");
                if (live.Chat.Status == "busy" || live.Chat.Status == "confirm")
                    throw new InvalidOperationException("对话正在执行或等待确认，请等本轮结束后再转为协作");
                string session = live.Chat.Session.Length > 0 ? live.Chat.Session : live.Session;
                var source = new Teams.SourceChat {
                    Terminal = live.Id,
                    Tool = live.Tool,
                    Dir = live.Dir,
                    Session = session,
                    Model = live.Model,
                    Effort = live.Effort,
                    InTurn = false
                };
                if (String.IsNullOrWhiteSpace(Get(item, "context")))
                    source.Context = ExtractConversationContext(source.Tool, source.Session, source.Dir, live.Chat.Folder);
                string unused;
                var promoted = Teams.Promote(item, source, out unused);
                live.Chat.Notice("已转为协作任务（" + (promoted.Mode == "review" ? "写审" : "分工协作") + "）");
                Wake();
                return new Dictionary<string, object> { { "team", Teams.Shown(promoted) } };
            }
            return new Dictionary<string, object> { { "team", Teams.Shown(Teams.Start(item)) } };
        }
        if (action == "work_team_stop") { Teams.Stop(Get(op, "team")); return new Dictionary<string, object>(); }
        if (action == "work_team_resume") { Teams.Resume(Get(op, "team")); return new Dictionary<string, object>(); }
        if (action == "work_team_say") {
            string teamId = Get(op, "team");
            string dirName = Teams.DirectoryOf(teamId), folder;
            if (!AllDirs(prefs).TryGetValue(dirName, out folder)) throw new ArgumentException("\u76ee\u5f55\u672a\u88ab\u5141\u8bb8");
            var attachments = Attachments.Read(folder, op.ContainsKey("attachments") ? op["attachments"] : null);
            return new Dictionary<string, object> { { "team", Teams.Shown(Teams.Say(teamId, Get(op, "text"), Get(op, "target"), attachments)) } };
        }
        if (action == "work_team_pick") { return new Dictionary<string, object> { { "team", Teams.Shown(Teams.Pick(Get(op, "team"), Get(op, "tool"))) } }; }
        if (action == "work_team_swap") { return new Dictionary<string, object> { { "team", Teams.Shown(Teams.Swap(Get(op, "team"), Get(op, "role"), Get(op, "tool"), Get(op, "note"), op.ContainsKey("model") ? Get(op, "model") : null, op.ContainsKey("effort") ? Get(op, "effort") : null)) } }; }
        if (action == "work_team_remove") { if (!Teams.Remove(Get(op, "team"))) throw new ArgumentException("没有找到这个协作任务"); return new Dictionary<string, object>(); }
        if (action == "work_brief") {
            // what a hand-over would say, for the phone to show, or to begin a new conversation with
            var from = sessions.FirstOrDefault(s => s.Tool == Get(op, "from_tool") && s.Id == Get(op, "from"));
            if (from == null) throw new ArgumentException("电脑上没有找到要交接的对话，请刷新后重试");
            var read = ReadConversation(new Dictionary<string, object> { { "session", from.Id }, { "tool", from.Tool }, { "dir", from.Dir } }, prefs);
            return new Dictionary<string, object> { { "text", Brief(from, read, Get(op, "note")) }, { "tool", from.Tool }, { "dir", from.Dir }, { "title", from.Title } };
        }
        if (action == "work_handoff") {
            var from = sessions.FirstOrDefault(s => s.Tool == Get(op, "from_tool") && s.Id == Get(op, "from"));
            var to = Taking(Get(op, "to_tool"), Get(op, "to"));
            if (from == null) throw new ArgumentException("电脑上没有找到要交接的对话，请刷新后重试");
            if (to == null) throw new InvalidOperationException("接手的对话没有在电脑上开着");
            if (from.Tool == to.Tool && from.Id == to.Id) throw new ArgumentException("不能交接给同一段对话");
            var read = ReadConversation(new Dictionary<string, object> { { "session", from.Id }, { "tool", from.Tool }, { "dir", from.Dir } }, prefs);
            string brief = Brief(from, read, Get(op, "note"));
            bool ok; string note = Tell(to, brief, prefs, out ok);
            if (!ok) throw new InvalidOperationException(note);
            return new Dictionary<string, object> { { "status", "accepted" }, { "note", note }, { "length", brief.Length } };
        }
        throw new ArgumentException("不支持此操作");
    }
    CodexDesktopSessions.State NativeState(SessionInfo s) {
        var state = s.Live && !String.IsNullOrEmpty(s.ControlFolder) && s.Tool == "codex" ? NativeDesktop.Get(s.Id) : null;
        return state != null && SamePath(state.Folder, s.ControlFolder) ? state : null;
    }
    // An existing conversation grants access to that conversation only. Its folder comes from
    // the computer's inventory, never a path supplied by the phone. Projects still govern files/new tasks.
    string DesktopFolder(SessionInfo s, Dictionary<string, object> prefs) {
        string root;
        if (s.Dir.Length > 0) {
            if (!AllDirs(prefs).TryGetValue(s.Dir, out root) || !SamePath(root, s.ControlFolder))
                throw new InvalidOperationException("电脑远控或项目权限已变化");
        } else if (AgyAppSession(s)) {
            // Live Antigravity Desktop conversations are addressed by cascadeId through the verified local Hub;
            // their workspace URI may be empty (playground/quick chat) or point to a moved/deleted folder.
            root = s.Folder ?? "";
            if (!s.Live || !SamePath(root, s.ControlFolder))
                throw new InvalidOperationException("无法确认电脑上原对话的工作目录，请刷新后重试");
        } else {
            root = s.Folder;
            if (!s.Live || String.IsNullOrEmpty(root) || !Path.IsPathRooted(root) || !Directory.Exists(root) || !SamePath(root, s.ControlFolder))
                throw new InvalidOperationException("无法确认电脑上原对话的工作目录，请刷新后重试");
        }
        return root;
    }
    void ValidateDesktopConversation(string session, string dir, string root, string server, string tool = "codex") {
        lock (work) {
            // Read settings without Preferences' connection-reset side effects on this worker.
            var fresh = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dataDir, "config.json"), Encoding.UTF8));
            if (Get(fresh, "RemoteEnabled") != "True" || Get(fresh, "Server") != server)
                throw new InvalidOperationException("电脑远控或项目权限已变化");
            var current = sessions.FirstOrDefault(s => s.Tool == tool && s.Id == session && s.Dir == dir && s.Live && (tool != "agy" || AgyAppSession(s)));
            if (current == null || !SamePath(DesktopFolder(current, fresh), root))
                throw new InvalidOperationException("电脑上的原对话或工作目录已变化，请刷新后重试");
        }
    }
    sealed class TitleCache { public long Stamp, Length; public string Title, Same, Kind; public bool Named; }
    readonly Dictionary<string, string> codexFiles = new Dictionary<string, string>();       // a Codex conversation -> its file
    readonly Dictionary<string, TitleCache> titles = new Dictionary<string, TitleCache>();
    readonly CodexSessions codexSessions = new CodexSessions();
    readonly AgySessions agySessions = new AgySessions();
    readonly Dictionary<string, string> codexFolders = new Dictionary<string, string>();
    Dictionary<string, string> codexNames = new Dictionary<string, string>();
    long codexNamesStamp;
    List<SessionInfo> sessions = new List<SessionInfo>();
    List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
    readonly Dictionary<string, string> claudeFolders = new Dictionary<string, string>();
    // By what a conversation was started, as its own file says (Folders.OfCodex, Folders.OfClaude): read with the
    // folder, kept by the same key, forgotten with it.
    readonly Dictionary<string, string> usedBy = new Dictionary<string, string>();
    readonly Dictionary<string, string> codexOrigins = new Dictionary<string, string>();
    // By what every folder with conversations is used: filled by each look at the conversations.
    readonly Folders folders = new Folders();
    public Folders Folders { get { return folders; } }
    /// The folders a project can still be made of. `closedApps`: also those only a desktop app uses that does not
    /// run now (at the computer they are added all the same; the phone has nothing of them to work beside).
    public List<Dictionary<string, object>> Offer(Dictionary<string, string> dirs, bool closedApps, int most) {
        var offered = folders.Offered(path => Elsewhere(path) && !dirs.Values.Any(d => SamePath(d, path)), closedApps, most);
        foreach (var one in offered) one["name"] = Tidy((string)one["name"]);
        return offered;
    }
    public Dictionary<string, string> ProjectFolders() { return AllDirs(Preferences()); }
    DateTime sessionsSent = DateTime.MinValue, sessionsChecked = DateTime.MinValue;
    volatile bool sessionsDirty = true;
    readonly object prefsGate = new object();
    string prefsStamp = ""; Dictionary<string, object> cachedPrefs;
    string ownProjectsStamp = ""; List<KeyValuePair<string, string>> cachedOwnProjects;
    // Released when a program wrote to its terminal or the relay holds a new operation: the next report goes out at once.
    readonly SemaphoreSlim wake = new SemaphoreSlim(0);
    bool soon;
    // Terminals and finished operations are worked on by the report loop and by the held request; one at a time.
    readonly object work = new object();
    readonly FileTransfers uploads = new FileTransfers();
    DateTime lastInput = DateTime.MinValue, lastScan = DateTime.MinValue, toolsAt = DateTime.MinValue;
    string[] tools = new string[0];
    long[] externalAgy = new long[0];
    void Wake() {
        try { if (wake.CurrentCount == 0) wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
        foreach (var link in ActiveLinks()) link.WakeReport();
    }
    DateTime nextScan = DateTime.MinValue, lastActivity = DateTime.MinValue;
    const string Uuid = @"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z";

    // Two things are said of a conversation that a program on this computer has: that the program holds it (Live:
    // nothing else can go on with it until that program lets go), and that it is in use now (Now). They differ
    // because a desktop app keeps every conversation it has loaded held by a process, shown or not, for hours.
    // In use is: at work, asking, or kept waiting for the phone (its tool says so, through its hooks or its own
    // note); or in a window of its own, a command line, which is open for as long as its program runs; or used in
    // the last few minutes. Claude Code and Codex are judged by the same rule.
    public const int RecentMinutes = 10;
    public static bool InUse(string host, string origin, string status, Reach.Open told, long used, DateTime now) {
        string stands = told != null ? told.Status : status;
        if (stands == "busy" || stands == "confirm" || stands == "waiting") return true;
        bool window = host == "cli" && (origin ?? "").IndexOf("桌面版", StringComparison.Ordinal) < 0 && (origin ?? "").IndexOf("后台服务", StringComparison.Ordinal) < 0;
        return window || Milliseconds(now) - used < RecentMinutes * 60000L;
    }

    public static Dictionary<int, int> Parents() { return SessionOrigin.Parents(); }
    static string ReadPart(string path, bool tail, int limit) { return Transcripts.ReadPart(path, tail, limit); }
    static string JsonText(string text, string field, bool last) { return Transcripts.JsonText(text, field, last); }
    public static string Tidy(string title) { return Transcripts.Tidy(title); }
    public static string ClaudeTitleOf(string path) { return Transcripts.ClaudeTitleOf(path); }
    public static string ClaudeTitleOf(string path, out string same) { return Transcripts.ClaudeTitleOf(path, out same); }
    string ClaudeTitle(FileInfo file, out string same) {
        TitleCache cached; same = "";
        if (titles.TryGetValue(file.FullName, out cached) && cached.Stamp == file.LastWriteTimeUtc.Ticks && cached.Length == file.Length) { same = cached.Same; return cached.Title; }
        string title;
        bool named;
        try { title = Transcripts.ClaudeTitleOf(file.FullName, out same, out named); } catch (IOException) { if (cached != null) same = cached.Same; return cached != null ? cached.Title : ""; } catch (UnauthorizedAccessException) { return ""; }
        titles[file.FullName] = new TitleCache { Stamp = file.LastWriteTimeUtc.Ticks, Length = file.Length, Title = title, Same = same, Named = named };
        return title;
    }
    bool ClaudeNamed(FileInfo file) {
        TitleCache cached;
        return titles.TryGetValue(file.FullName, out cached) && cached.Named;
    }
    /// By what a Claude Code conversation was started: every row of its file says so. Read once for a file.
    string ClaudeKind(FileInfo file) {
        TitleCache cached;
        if (!titles.TryGetValue(file.FullName, out cached)) return Folders.Terminal;
        if (cached.Kind == null) { try { cached.Kind = Folders.OfClaude(JsonText(ReadPart(file.FullName, false, 64 * 1024), @"""entrypoint"":", false)); } catch (IOException) { return Folders.Terminal; } catch (UnauthorizedAccessException) { return Folders.Terminal; } }
        return cached.Kind;
    }
    // One conversation is listed once: of its copies, those a program has open, else the one used last.
    public static List<SessionInfo> Single(IEnumerable<SessionInfo> found) {
        return found.GroupBy(s => s.Same.Length > 0 ? s.Tool + "\n" + s.Dir + "\n" + s.Same : s.Id)
            .SelectMany(copies => copies.Any(s => s.Live) ? copies.Where(s => s.Live) : copies.OrderByDescending(s => s.Updated).Take(1)).ToList();
    }
    // Where Claude Code keeps its conversations and the notes of the programs that have them open: its own folder
    // in the person's home, or the one CLAUDE_CONFIG_DIR names, as Claude Code itself reads it.
    public static string ClaudeHome { get { return ClaudeSessions.Home; } }
    public static string ProjectFolder(string path) { return Regex.Replace(path, "[^a-zA-Z0-9]", "-"); }
    // A folder that is no project is spoken of to the phone unless it is one of the system's own for passing things.
    static bool Elsewhere(string folder) { return folder.Length >= 3 && folder.IndexOf(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase) < 0 && !folder.EndsWith(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase); }
    /// The folder a terminal runs in: its project's, or its own where it has no project. Null when that is gone.
    static string Place(LiveTerminal terminal, Dictionary<string, string> dirs) {
        if (!String.IsNullOrEmpty(terminal.WorktreePath) && Directory.Exists(terminal.WorktreePath)) return terminal.WorktreePath;
        string path;
        if (terminal.Dir.Length == 0) return terminal.Folder.Length > 0 ? terminal.Folder : null;
        return dirs.TryGetValue(terminal.Dir, out path) ? path : null;
    }
    static bool SamePath(string a, string b) { return String.Equals((a ?? "").TrimEnd('\\', '/'), (b ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
    static long Milliseconds(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    // Reading the conversation lists can take seconds. The reports start it beside themselves, with the terminals as
    // they were at that moment, so that no key and no line of output waits for it; one reading at a time.
    readonly object scanGate = new object();
    int scanning;
    public List<SessionInfo> Scan(Dictionary<string, string> dirs, LiveTerminal[] running = null) { lock (scanGate) return ScanAll(dirs, running ?? terminals.Values.ToArray()); }
    // Conversations of the allowed folders: what each tool saved, and which of them a program on this computer has open.
    List<SessionInfo> ScanAll(Dictionary<string, string> dirs, LiveTerminal[] running) {
        var found = new List<SessionInfo>();
        var ownerCounts = new Dictionary<string, int>();
        var parents = Parents();
        // every folder a conversation is found in is noted, a project or not, with by what it is used
        folders.Begin();
        Action<string, string, DateTime, bool, string> note = folders.Note;
        ScanClaude(dirs, running, parents, found, ownerCounts, note);
        ScanCodex(dirs, running, parents, found, note);
        ScanAgy(dirs, running, parents, found, note);
        foreach (SessionInfo held in found.Where(s => s.Live)) {
            int count;
            held.MultipleOwners = held.Tool == "claude" && ownerCounts.TryGetValue(held.Id, out count) && count > 1;
            if (held.MultipleOwners) { held.CanTakeover = false; held.TakeoverReason = ClaudeSessions.MultipleReason; }
            held.Now = InUse(held.Host, held.Origin, ObservedStatus(held, Reach.Of(held.Tool, held.Id)), null, held.Updated, DateTime.UtcNow);
        }
        // Associate a phone terminal only with the verified writer process in its own process tree.
        foreach (LiveTerminal t in running.Where(t => t.Tool == "codex" && t.Session.Length == 0 && !t.Pty.Closed)) {
            var mine = found.Where(s => s.Tool == "codex" && s.Dir == t.Dir && !running.Any(o => o.Session == s.Id)
                && codexSessions.Get(s.Id).Pid > 0 && SessionOrigin.Descends(codexSessions.Get(s.Id).Pid, new[] { t.Pty.Id }, parents)).ToArray();
            if (mine.Length == 1) t.Session = mine[0].Id;
        }
        // Keep the exact member conversations available from task details, even when their opening messages match.
        var memberSessions = new HashSet<string>(Teams.All().SelectMany(team => team.Sessions.Values)
            .Select(member => member.Tool + ":" + member.Id), StringComparer.OrdinalIgnoreCase);
        var members = found.Where(s => memberSessions.Contains(s.Tool + ":" + s.Id))
            .GroupBy(s => s.Tool + ":" + s.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(s => s.Updated).First());
        // A line of conversations handed from one AI to another is listed once: its latest, under its first name.
        foreach (var s in found) { string first = Handovers.Title(s.Tool, s.Id); if (first.Length > 0) s.Title = first; }
        var others = Single(found.Where(s => !memberSessions.Contains(s.Tool + ":" + s.Id) && !Handovers.GoneOn(s.Tool, s.Id)))
            .OrderByDescending(s => s.Live || Marks.Is("pinned", s.Tool, s.Id)).ThenByDescending(s => s.Updated);
        sessions = members.Concat(others).Take(200).ToList();
        foreach (var s in sessions) { string root; s.ControlFolder = dirs.TryGetValue(s.Dir, out root) ? root : s.Folder; }       // the folder it is in: its project's, or the one it was found open in
        NativeDesktop.Refresh(sessions.Where(s => s.Live && s.Tool == "codex" && s.Origin == Ways.CodexDesktop).Select(s => s.Id));
        folders.End();
        candidates = Offer(dirs, false, 40);
        // (what the folders are used by is read with the folders, and forgotten with them)
        if (claudeFolders.Count > 500 || codexFolders.Count > 3000) { claudeFolders.Clear(); codexFolders.Clear(); usedBy.Clear(); codexOrigins.Clear(); codexFiles.Clear(); }
        if (titles.Count > 600) titles.Clear();
        return sessions;
    }
    void ScanClaude(Dictionary<string, string> dirs, LiveTerminal[] running, Dictionary<int, int> parents, List<SessionInfo> found, Dictionary<string, int> ownerCounts, Action<string, string, DateTime, bool, string> note) {
        var open = new Dictionary<string, ClaudeSessions.Owner>();
        try {
            foreach (var source in ClaudeSessions.LiveSessions(parents)) {
                try {
                    int pid = source.Pid; string session = source.Session;
                    int count; ownerCounts.TryGetValue(session, out count); ownerCounts[session] = count + 1;
                    LiveTerminal owner = null; int at = pid;
                    for (int depth = 0; depth < 12 && owner == null; depth++) {
                        owner = running.FirstOrDefault(t => !t.Pty.Closed && t.Pty.Id == at);
                        int parent; if (!parents.TryGetValue(at, out parent) || parent == at || parent == 0) break; at = parent;
                    }
                    if (owner != null) { owner.Session = session; if (owner.Chat == null) owner.Status = source.Status; }
                    else {
                        ClaudeSessions.Owner prior;
                        open[session] = open.TryGetValue(session, out prior) ? ClaudeSessions.Preferred(new[] { prior, source }) : source;
                        note(source.Dir, "claude", DateTime.FromFileTimeUtc(source.Started), true, Folders.Held(source.Origin, Folders.OfClaude(source.Entry)));
                    }
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
                    string kind = live ? Folders.Held(source.Origin, ClaudeKind(file)) : ClaudeKind(file);
                    note(dir.Value, "claude", file.LastWriteTimeUtc, live, kind);
                    found.Add(new SessionInfo { Id = id, Tool = "claude", Dir = dir.Key, Kind = kind, Title = title, Named = ClaudeNamed(file), Updated = Milliseconds(file.LastWriteTimeUtc), Created = file.CreationTimeUtc, Live = live, Host = live ? source.Host : "", Origin = live ? source.Origin : "", Wakes = live && source.Socket.Length > 0, CanTakeover = live && source.CanTakeover, OwnershipKnown = !live || source.CanTakeover || source.Host == "remote", Status = live ? source.Status : "", TakeoverReason = live ? source.Reason : "", Same = same });
                }
            } catch { }
        }
        // A verified owner can precede the first transcript or its title, inside or outside a registered project.
        foreach (var held in open.Where(o => !found.Any(s => s.Tool == "claude" && s.Id == o.Key))) {
            try {
                string where = held.Value.Dir ?? "", same;
                string project = dirs.FirstOrDefault(d => SamePath(d.Value, where)).Key;
                if (project == null && !Elsewhere(where)) continue;
                var file = new FileInfo(Path.Combine(ClaudeHome, "projects", ProjectFolder(where), held.Key + ".jsonl"));
                string title = file.Exists ? ClaudeTitle(file, out same) : "";
                var source = held.Value;
                DateTime created = file.Exists ? file.CreationTimeUtc : DateTime.FromFileTimeUtc(source.Started);
                bool named = title.Length > 0 ? ClaudeNamed(file) : !String.IsNullOrWhiteSpace(source.Name);
                if (title.Length == 0) title = named ? source.Name : "Claude 对话 " + created.ToLocalTime().ToString("MM-dd HH:mm");
                string kind = file.Exists ? ClaudeKind(file) : Folders.OfClaude(source.Entry);
                found.Add(new SessionInfo { Id = held.Key, Tool = "claude", Dir = project ?? "", Folder = project == null ? where : "", Kind = Folders.Held(source.Origin, kind), Title = title, Named = named, Updated = Milliseconds(file.Exists ? file.LastWriteTimeUtc : created), Created = created, Live = true, Wakes = source.Socket.Length > 0, Host = source.Host, Origin = source.Origin, CanTakeover = source.CanTakeover, OwnershipKnown = source.CanTakeover || source.Host == "remote", Status = source.Status, TakeoverReason = source.Reason, Same = "" });
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
        }
        // and the ones a terminal of the phone's has taken on from there: listed so that the terminal keeps the conversation's name and marks
        foreach (LiveTerminal own in running.Where(t => t.Tool == "claude" && t.Folder.Length > 0 && t.Session.Length > 0 && !t.Pty.Closed && !found.Any(s => s.Tool == "claude" && s.Id == t.Session))) {
            try {
                string same;
                var file = new FileInfo(Path.Combine(ClaudeHome, "projects", ProjectFolder(own.Folder), own.Session + ".jsonl"));
                string title = file.Exists ? ClaudeTitle(file, out same) : "";
                if (title.Length > 0) found.Add(new SessionInfo { Id = own.Session, Tool = "claude", Dir = "", Folder = own.Folder, Title = title, Named = ClaudeNamed(file), Updated = Milliseconds(file.LastWriteTimeUtc), Created = file.CreationTimeUtc, Live = false, OwnershipKnown = true, Same = "" });
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
        }
        try {
            // Other folders Claude Code has worked in: its folder names lose characters, the real path is inside a conversation.
            foreach (var folder in new DirectoryInfo(Path.Combine(ClaudeHome, "projects")).GetDirectories()
                    .Select(d => new { d.Name, Newest = d.GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() })
                    .Where(d => d.Newest != null).OrderByDescending(d => d.Newest.LastWriteTimeUtc).Take(80)) {
                string path;
                if (!claudeFolders.TryGetValue(folder.Name, out path)) {
                    string head;
                    try { head = ReadPart(folder.Newest.FullName, false, 1024 * 1024); } catch (IOException) { continue; }
                    path = JsonText(head, @"""cwd"":", false);
                    if (path.Length > 0) { claudeFolders[folder.Name] = path; usedBy["claude\n" + folder.Name] = Folders.OfClaude(JsonText(head, @"""entrypoint"":", false)); }
                }
                string claudeKind;
                if (path.Length > 0 && ProjectFolder(path) == folder.Name) note(path, "claude", folder.Newest.LastWriteTimeUtc, false, usedBy.TryGetValue("claude\n" + folder.Name, out claudeKind) ? claudeKind : Folders.Terminal);
            }
        } catch { }
    }
    void ScanCodex(Dictionary<string, string> dirs, LiveTerminal[] running, Dictionary<int, int> parents, List<SessionInfo> found, Action<string, string, DateTime, bool, string> note) {
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
            bool queues = CodexQueue.Supported(FindTool("codex"));
            int listed = 0;
            if (root.Exists) foreach (FileInfo file in root.GetFiles("rollout-*.jsonl", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).Take(1500)) {
                string name = Path.GetFileNameWithoutExtension(file.Name), id = name.Length > 36 ? name.Substring(name.Length - 36) : "", folder, codexKind;
                if (!Regex.IsMatch(id, Uuid)) continue;
                if (!codexFolders.TryGetValue(file.FullName, out folder)) {
                    string head;
                    // An earlier Codex wrote its instructions before the folder: where the first part does not name it, more is read.
                    try { head = ReadPart(file.FullName, false, 16 * 1024); if (JsonText(head, @"""cwd"":", false).Length == 0) head = ReadPart(file.FullName, false, 160 * 1024); } catch (IOException) { continue; }
                    // A helper that a conversation started for itself is part of that conversation, not one to continue.
                    codexFolders[file.FullName] = folder = Regex.IsMatch(head, @"""parent_thread_id""\s*:\s*""") ? "" : JsonText(head, @"""cwd"":", false);
                    string source = JsonText(head, @"""source"":", false), originator = JsonText(head, @"""originator"":", false);
                    usedBy[file.FullName] = Folders.OfCodex(source, originator);
                    codexOrigins[file.FullName] = Folders.CodexOrigin("", source, originator);
                }
                if (folder.Length == 0) continue;
                codexFiles[id] = file.FullName;
                if (!usedBy.TryGetValue(file.FullName, out codexKind)) codexKind = Folders.Terminal;
                // Keep live conversations regardless of age; older closed conversations only contribute project suggestions.
                string dir = dirs.Where(d => SamePath(d.Value, folder)).Select(d => d.Key).FirstOrDefault();
                var owner = codexSessions.Get(id);
                string origin = owner.Origin, clientOrigin;
                if ((owner.Origin.Length == 0 || owner.Origin == "Codex 后台服务") && codexOrigins.TryGetValue(file.FullName, out clientOrigin) && clientOrigin.Length > 0) {
                    // Keep ownership immutable: this only supplies the display source, never a PID or kill permission.
                    origin = clientOrigin;
                }
                bool live = owner.Live;
                if (++listed > 150 && !live) { note(folder, "codex", file.LastWriteTimeUtc, false, codexKind); continue; }
                bool phone = owner.Pid > 0 && SessionOrigin.Descends(owner.Pid, running.Select(t => t.Pty.Id), parents);
                string title; codexNames.TryGetValue(id, out title); title = Tidy(title);
                if (live && !phone) codexKind = Folders.Held(origin, codexKind);
                note(folder, "codex", file.LastWriteTimeUtc, live, codexKind);
                if (dir == null) {
                    // one that a program has open in a folder that is no project is listed with its folder
                    string where = folder;
                    // (one that a terminal of the phone's has taken on from there is listed too, as that terminal's)
                    bool taken = running.Any(t => t.Tool == "codex" && t.Session == id && t.Folder.Length > 0 && !t.Pty.Closed);
                    bool used = live && !phone;
                    if ((used || taken) && Elsewhere(where)) found.Add(new SessionInfo { Id = id, Tool = "codex", Dir = "", Folder = where, Kind = codexKind, Named = title.Length > 0, Title = title.Length > 0 ? title : "Codex 对话 " + file.CreationTime.ToString("MM-dd HH:mm"), Updated = Milliseconds(file.LastWriteTimeUtc), Created = file.CreationTimeUtc, Live = live && !phone, Wakes = live && !phone && queues, Host = live && !phone ? owner.Host : "", Origin = live && !phone ? origin : "", OwnershipKnown = owner.Known, CanTakeover = live && owner.CanTakeover && !phone, TakeoverReason = owner.Reason, Same = "" });
                    continue;
                }
                int count; counts.TryGetValue(dir, out count);
                if (!live && count >= 15) continue;
                if (!live) counts[dir] = count + 1;
                found.Add(new SessionInfo { Id = id, Tool = "codex", Dir = dir, Kind = codexKind, Named = title.Length > 0, Title = title.Length > 0 ? title : "Codex 对话 " + file.CreationTime.ToString("MM-dd HH:mm"), Updated = Milliseconds(file.LastWriteTimeUtc), Created = file.CreationTimeUtc, Live = live, Wakes = live && !phone && queues, Host = live ? owner.Host : "", Origin = live ? origin : "", OwnershipKnown = owner.Known, CanTakeover = live && owner.CanTakeover && !phone, TakeoverReason = live ? owner.Reason : "", Same = title });
            }
        } catch { }
    }
    void ScanAgy(Dictionary<string, string> dirs, LiveTerminal[] running, Dictionary<int, int> parents, List<SessionInfo> found, Action<string, string, DateTime, bool, string> note) {
        try {
            agySessions.Refresh();
            var agyCounts = new Dictionary<string, int>();
            foreach (var item in Antigravity.Summaries(null, agySessions.LiveIds())) {
                var owner = agySessions.Get(item.Id);
                bool live = owner.Live;
                LiveTerminal phoneOwner = live && owner.Pid > 0
                    ? running.FirstOrDefault(t => !t.Pty.Closed && SessionOrigin.Descends(owner.Pid, new[] { t.Pty.Id }, parents))
                    : null;
                if (phoneOwner != null) {
                    phoneOwner.Session = item.Id;
                    if (phoneOwner.Chat == null && item.Status.Length > 0) phoneOwner.Status = item.Status;
                }
                bool external = live && phoneOwner == null;
                string origin = external ? owner.Origin : "";
                string agyKind = external ? Folders.Held(origin, Folders.Terminal) : Folders.Terminal;
                string dir = item.Folder.Length > 0 ? dirs.Where(d => SamePath(d.Value, item.Folder)).Select(d => d.Key).FirstOrDefault() : null;
                if (item.Folder.Length > 0) note(item.Folder, "agy", item.Created, external, agyKind);
                if (dir == null) {
                    bool taken = running.Any(t => t.Tool == "agy" && t.Session == item.Id && t.Folder.Length > 0 && !t.Pty.Closed);
                    if ((external || taken) && Elsewhere(item.Folder))
                        found.Add(new SessionInfo { Id = item.Id, Tool = "agy", Dir = "", Folder = item.Folder, Kind = agyKind, Title = item.Title, Named = item.Named, Updated = item.Updated, Created = item.Created, Live = external, Host = external ? owner.Host : "", Origin = origin, Status = external ? item.Status : "", OwnershipKnown = owner.Known, CanTakeover = external && owner.CanTakeover, TakeoverReason = external ? owner.Reason : "", Same = "" });
                    continue;
                }
                int count; agyCounts.TryGetValue(dir, out count);
                if (!live && count >= 15) continue;
                if (!live) agyCounts[dir] = count + 1;
                found.Add(new SessionInfo { Id = item.Id, Tool = "agy", Dir = dir, Kind = agyKind, Title = item.Title, Named = item.Named, Updated = item.Updated, Created = item.Created, Live = external, Host = external ? owner.Host : "", Origin = origin, Status = external ? item.Status : "", OwnershipKnown = owner.Known, CanTakeover = external && owner.CanTakeover, TakeoverReason = external ? owner.Reason : "", Same = item.Title });
            }
        } catch { }
        var desktopItems = AgyDesktopClient.Summaries().ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        AgyDesktop.Refresh();
        lock (newAgySessions) {
            foreach (var item in newAgySessions.Values.ToArray()) {
                if (DateTime.UtcNow - item.Created > DesktopOperations.Looked || desktopItems.ContainsKey(item.Id) && !String.IsNullOrEmpty(desktopItems[item.Id].Folder))
                    newAgySessions.Remove(item.Id);
                else desktopItems[item.Id] = item;
            }
        }
        var listed = new HashSet<string>(found.Where(s => s.Tool == "agy").Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var item in desktopItems.Values) {
            if (listed.Contains(item.Id)) continue;
            var state = AgyDesktop.Get(item.Id);
            string folder = !String.IsNullOrEmpty(item.Folder) ? item.Folder : (state != null ? state.Folder : "");
            bool live = state != null && state.Live && SamePath(state.Folder, folder);
            if (String.IsNullOrEmpty(folder) && !live) continue;
            string dir = folder.Length > 0 ? dirs.FirstOrDefault(d => SamePath(d.Value, folder)).Key : null;
            if (folder.Length > 0) note(folder, "agy", item.Created, live, Ways.AgyDesktop);
            if (dir == null && !(live && (folder.Length == 0 || Elsewhere(folder)))) continue;
            found.Add(new SessionInfo { Id = item.Id, Tool = "agy", Dir = dir ?? "", Folder = dir == null ? folder : "",
                Kind = Ways.AgyDesktop, Origin = Ways.AgyDesktop, Title = String.IsNullOrEmpty(item.Title) ? "Antigravity 桌面对话" : item.Title,
                Named = item.Named, Created = item.Created, Updated = item.Updated, Live = live, Host = live ? "shared" : "",
                OwnershipKnown = true, Status = live ? state.Status : "", TakeoverReason = "请在 Antigravity 桌面版关闭这段对话" });
        }
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
        int total = t.Pending.Length;
        if (total == 0) return;
        for (int pos = 0; pos < total; ) {
            int n = Math.Min(12000, total - pos);
            if (pos + n < total && Char.IsHighSurrogate(t.Pending[pos + n - 1])) n--;
            t.Output.Add(new Dictionary<string, object> { { "terminal", t.Id }, { "seq", ++t.Seq }, { "data", t.Pending.ToString(pos, n) } });
            pos += n;
        }
        t.Pending.Clear();
    }
    static string Get(IDictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : ""; }
    static IEnumerable<object> List(IDictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>() : new object[0]; }
    // Settings and state live in %LOCALAPPDATA%\RemoteCli; REMOTECLI_DATA points a second copy (or a test) elsewhere.
    public static string DefaultData { get { string wanted = Environment.GetEnvironmentVariable("REMOTECLI_DATA"); return !String.IsNullOrWhiteSpace(wanted) ? wanted : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCli"); } }
    // config.json: Server (address of the relay), RemoteEnabled, RemoteMaxMode (full, edit or read) and RemoteDirs ("name=folder").
    // Whether a hook runs in one of this program's own terminals: started, however many steps down, by this program,
    // or reporting the conversation one of the terminals is known to run (a tool may run its hooks from a service
    // of its own, which this program did not start).
    bool InOwnTerminal(int hook, string tool, string session) {
        if (SessionOrigin.Descends(hook, Process.GetCurrentProcess().Id)) return true;
        lock (work) return terminals.Values.Any(t => !t.Pty.Closed && t.Tool == tool && String.Equals(t.Session, session, StringComparison.OrdinalIgnoreCase));
    }
    // What a tool is kept for the phone for, as the settings have it now. Asked by the hooks' threads: read on its
    // own, without what Preferences does besides. Nothing is kept where the settings cannot be read.
    Reach.Kept KeptForPhone() {
        try { return RemoteCli.Reach.Kept.From(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dataDir, "config.json"), Encoding.UTF8))); }
        catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
        return new Reach.Kept();
    }
    Dictionary<string, object> Preferences() {
        string path = Path.Combine(dataDir, "config.json");
        lock (prefsGate) {
            var info = new FileInfo(path);
            string stamp = info.Exists ? info.Length + "." + info.LastWriteTimeUtc.Ticks : "";
            Dictionary<string, object> prefs;
            if (cachedPrefs != null && stamp.Length > 0 && stamp == prefsStamp) prefs = cachedPrefs;
            else {
                prefs = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                cachedPrefs = prefs; prefsStamp = stamp;
            }
            string server = Get(prefs, "Server").TrimEnd('/');
            if (server != Origin) { if (Origin.Length > 0) Reset(); Origin = server; }       // another relay: sign in there at once
            return prefs;
        }
    }
    internal static string FindTool(string name) {
        if (name == "shell") return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        // where PATH does not name it, where each tool's own installer puts it (agy: %LOCALAPPDATA%gyin)
        Tool kind = Tool.Of(name);
        string[] binaries = kind != null ? kind.Binaries : new[] { name };
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var searchDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Concat(new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(profile, ".local", "bin"),
            Path.Combine(profile, ".cargo", "bin"),
            Path.Combine(profile, ".opencode", "bin"),
            Path.Combine(localApp, "Programs", "OpenAI", "Codex", "bin"),
            Path.Combine(localApp, "agy", "bin"),
            Path.Combine(localApp, "Programs", "cursor", "resources", "app", "bin")
        });
        foreach (string bin in binaries)
            foreach (string folder in searchDirs)
                foreach (string ext in new[] { ".exe", ".cmd" }) { try { string path = Path.Combine(folder.Trim(), bin + ext); if (File.Exists(path)) return path; } catch { } }
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
        lock (prefsGate) {
            var info = new FileInfo(ProjectsFile);
            string stamp = info.Exists ? info.Length + "." + info.LastWriteTimeUtc.Ticks : "missing";
            if (cachedOwnProjects != null && stamp == ownProjectsStamp) return new List<KeyValuePair<string, string>>(cachedOwnProjects);
            var own = new List<KeyValuePair<string, string>>();
            try {
                if (info.Exists) foreach (var item in new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(ProjectsFile, Encoding.UTF8)))
                    if (Get(item, "name").Length > 0 && Get(item, "path").Length > 0 && !own.Any(p => p.Key == Get(item, "name"))) own.Add(new KeyValuePair<string, string>(Get(item, "name"), Get(item, "path")));
            } catch (IOException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
            cachedOwnProjects = own; ownProjectsStamp = stamp;
            return new List<KeyValuePair<string, string>>(own);
        }
    }
    void SaveProjects(List<KeyValuePair<string, string>> own) {
        string temporary = ProjectsFile + ".tmp";
        File.WriteAllText(temporary, json.Serialize(own.Select(p => new Dictionary<string, object> { { "name", p.Key }, { "path", p.Value } }).ToArray()), new UTF8Encoding(false));
        if (File.Exists(ProjectsFile)) File.Replace(temporary, ProjectsFile, null); else File.Move(temporary, ProjectsFile);
        lock (prefsGate) { cachedOwnProjects = null; ownProjectsStamp = ""; }
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
    internal static string RealPath(string path) {
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
    public static Dictionary<string, object> Said(string path, string tool) { return Transcripts.Said(path, tool); }
    /// Of how many steps, counted from the last, what came back is given.
    public const int Results = Transcripts.Results;
    public static string First(string back) { return Transcripts.First(back); }
    public static string Begins(string row, int from) { return Transcripts.Begins(row, from); }
    public static List<Dictionary<string, object>> Talk(List<Dictionary<string, object>> said, int most, int runs) { return Transcripts.Talk(said, most, runs); }
    static readonly Regex ValidSessionId = new Regex(@"\A[a-zA-Z0-9._:-]{4,128}\z", RegexOptions.Compiled);
    static bool IsValidSessionId(string session) {
        return !String.IsNullOrEmpty(session) && ValidSessionId.IsMatch(session) && session.IndexOf("..", StringComparison.Ordinal) < 0;
    }
    Dictionary<string, object> ReadConversation(Dictionary<string, object> op, Dictionary<string, object> prefs) {
        string session = Get(op, "session"), tool = Get(op, "tool"), dir = Get(op, "dir"), root;
        SessionInfo saved;
        lock (work) {
            saved = sessions.FirstOrDefault(s => String.Equals(s.Id, session, StringComparison.OrdinalIgnoreCase) && s.Tool == tool && s.Dir == dir)
                ?? sessions.FirstOrDefault(s => String.Equals(s.Id, session, StringComparison.OrdinalIgnoreCase) && s.Tool == tool);
        }
        if (saved == null || !IsValidSessionId(session)) throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
        // Fall back to the saved session's directory or folder if the client sent a stale or empty dir.
        string effectiveDir = dir.Length > 0 ? dir : saved.Dir;
        var allowedDirs = AllDirs(prefs);
        if (effectiveDir.Length > 0 && allowedDirs.TryGetValue(effectiveDir, out root)) { }
        else if (saved.Dir.Length > 0 && allowedDirs.TryGetValue(saved.Dir, out root)) { }
        else if (saved.Folder.Length > 0 || AgyAppSession(saved)) root = saved.Folder;
        else throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
        try {
            string title = Organizer.Title(saved.Tool, saved.Id, saved.Title, saved.Named);
            if (AgyAppSession(saved)) {
                var read = AgyDesktopClient.ReadConversation(session, Get(op, "since"));
                read["title"] = title;
                if (!read.ContainsKey("ask")) read["ask"] = AgyAsk(AgyState(saved));
                return read;
            }
            string file = ConversationFile(tool, session, root);
            if (file == null && !Tool.Named(tool).Converses) {
                return new Dictionary<string, object> {
                    { "messages", new object[0] }, { "more", false }, { "stamp", "0.0" },
                    { "title", title }, { "status", WaysOf(saved, Reach.Of(tool, session)).Status },
                    { "unsupported", true }
                };
            }
            if (file == null && tool == "claude" && saved.Live && ClaudeSessions.LiveSessions().Any(owner => owner.Session == session && SamePath(root, owner.Dir)))
                return new Dictionary<string, object> { { "messages", new object[0] }, { "more", false }, { "stamp", "0.0" },
                    { "title", title }, { "status", WaysOf(saved, Reach.Of(tool, session)).Status } };
            if (file == null) throw new FileNotFoundException();
            // A page that follows a conversation asks again and again: it says what it has (`since`), and is given
            // the conversation anew only when its file has changed. How it stands goes with either answer.
            var info = new FileInfo(file);
            var turns = Handovers.TurnsOf(tool, session);
            string stamp;
            if (turns.Count > 1) {
                var stampParts = new List<string>();
                var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var turn in turns) {
                    if (turn.Session.Length == 0) continue;
                    string key = turn.Tool + ":" + turn.Session.ToLowerInvariant();
                    if (!seenFiles.Add(key)) continue;
                    try {
                        string tf = ConversationFile(turn.Tool, turn.Session, root);
                        if (tf == null) continue;
                        var ti = new FileInfo(tf);
                        stampParts.Add(ti.Length + "." + ti.LastWriteTimeUtc.Ticks);
                    } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                if (stampParts.Count == 0) stampParts.Add(info.Length + "." + info.LastWriteTimeUtc.Ticks);
                stamp = String.Join(",", stampParts) + ".h" + turns.Count;
            } else {
                stamp = info.Length + "." + info.LastWriteTimeUtc.Ticks;
            }
            string since = Get(op, "since");
            var known = Reach.Of(tool, session);
            Dictionary<string, object> result;
            if (since.Length > 0 && since == stamp) result = new Dictionary<string, object> { { "same", true } };
            else if (turns.Count > 1) {
                bool more;
                var messages = LineMessages(root, tool, session, "", out more);
                result = new Dictionary<string, object> { { "messages", messages }, { "more", more } };
            } else result = Said(file, tool);
            result["stamp"] = stamp; result["title"] = title; result["status"] = ObservedStatus(saved, known);
            var native = NativeState(saved);
            if (native != null) result["status"] = native.Busy ? "busy" : native.Ready ? "waiting" : "confirm";
            return result;
        }
        catch (IOException) { throw new ArgumentException("读不到这个对话的内容"); }
        catch (UnauthorizedAccessException) { throw new ArgumentException("读不到这个对话的内容"); }
    }
    /// A project as the phone is told of it: with which tools and by what its folder is used, as its conversations say.
    Dictionary<string, object> Project(string name, string path, bool settled, bool exists) {
        var use = folders.Of(path);
        return new Dictionary<string, object> { { "name", name }, { "path", path }, { "fixed", settled }, { "exists", exists }, { "tools", String.Join(" ", use.Tools) }, { "kinds", Folders.Told(use.Kinds) } };
    }
    /// The projects that were added in the window are the window's to keep: it removes or renames one when asked
    /// (action, name, new name), and says whether it had it. Without a window they stand in a file of settings and
    /// are changed there.
    public Func<string, string, string, bool> Settled;
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
            if (at < 0 && settings.ContainsKey(name) && Settled != null) {
                // one that was added in the window: the window keeps it, and changes it when it is asked to
                string renamed = Tidy(Get(op, "to"));
                if (action == "project_remove") { if (terminals.Values.Any(t => t.Dir == name && !t.Pty.Closed)) throw new InvalidOperationException("这个项目还有终端在运行，请先结束"); }
                else if (renamed.Length == 0 || renamed.Length > 40 || renamed.Contains("=")) throw new ArgumentException("名称需为 1 至 40 字，且不含等号");
                else if (renamed != name && (settings.ContainsKey(renamed) || own.Any(p => p.Key == renamed))) throw new ArgumentException("已有同名项目");
                if (!Settled(action, name, renamed)) throw new ArgumentException("没有这个项目");
                if (action == "project_rename") foreach (var t in terminals.Values.Concat(ended).Where(t => t.Dir == name)) t.Dir = renamed;
                nextScan = DateTime.MinValue;
                return;
            }
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
    /// This program's own processes, which a takeover never ends: the phone's terminals and itself.
    int[] OwnRoots() { return terminals.Values.Select(t => t.Pty.Id).Concat(new[] { Process.GetCurrentProcess().Id }).ToArray(); }
    public static string Command(string tool, string launcher, string session, bool history, string mode, bool fork = false, bool noDaemon = false, bool fresh = false, string model = "", string effort = "") {
        Tool.Named(tool);
        if (launcher == null || launcher.IndexOfAny(new[] { '"', '%', '\r', '\n' }) >= 0) throw new ArgumentException("工具路径无效");
        if (!String.IsNullOrEmpty(session) && !Regex.IsMatch(session, @"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z")) throw new ArgumentException("历史会话编号无效");
        string args = Arguments(tool, session, history, mode, fork, noDaemon, fresh, model, effort);
        return LaunchCommand(launcher, args);
    }
    public static string Arguments(string tool, string session, bool history, string mode, bool fork = false, bool noDaemon = false, bool fresh = false, string model = "", string effort = "") {
        return Tool.Named(tool).Arguments(session ?? "", history, mode, fork, noDaemon, fresh, model, effort);
    }
    public static string ClampMode(string requested, string maxMode) {
        int maxRank = ModeRank(maxMode);
        if (maxRank < 0) maxRank = 2;
        string clean = (requested ?? "").Trim().ToLowerInvariant();
        if (clean.Length == 0) return ModeName(maxRank);
        int askRank = ModeRank(clean);
        if (askRank < 0) throw new ArgumentException("权限模式无效");
        return ModeName(Math.Min(askRank, maxRank));
    }
    static int ModeRank(string mode) {
        if (mode == "read") return 0;
        if (mode == "edit") return 1;
        if (mode == "full") return 2;
        return -1;
    }
    static string ModeName(int rank) { return rank <= 0 ? "read" : rank == 1 ? "edit" : "full"; }
    public static ProcessStartInfo ComputerProcess(ComputerTakeover takeover, string mode) {
        if (takeover == null || String.IsNullOrEmpty(takeover.Launcher)) throw new InvalidOperationException("电脑上没有找到这个工具");
        string args = Arguments(takeover.Tool, takeover.Session, false, mode, false, Tool.Named(takeover.Tool).SupportsStandalone, false);
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
    RelayLink[] ActiveLinks() {
        lock (linksGate) return links.Where(l => l.Active).ToArray();
    }

    /// Replaces the set of active relay endpoints while keeping unchanged links (such as the permanent local relay) connected.
    public void SetLinks(IEnumerable<RelayEndpoint> endpoints) {
        var wanted = (endpoints ?? new RelayEndpoint[0]).Where(e => e != null && !String.IsNullOrWhiteSpace(e.Origin) && !String.IsNullOrWhiteSpace(e.Password)).ToList();
        var added = new List<RelayLink>();
        var removed = new List<RelayLink>();
        lock (linksGate) {
            explicitLinks = true;
            var keep = new List<RelayLink>();
            foreach (var endpoint in wanted) {
                var existing = links.FirstOrDefault(l => l.Active && l.Key == endpoint.Key);
                if (existing != null) keep.Add(existing);
                else {
                    var created = new RelayLink(endpoint);
                    if (created.IsValid) { keep.Add(created); added.Add(created); }
                    else created.Dispose();
                }
            }
            foreach (var old in links) if (!keep.Contains(old)) removed.Add(old);
            links.Clear();
            links.AddRange(keep);
        }
        foreach (var old in removed) old.Dispose();
        if (running) foreach (var created in added) StartLinkWorkers(created);
        sessionsDirty = true;
        soon = true;
        Wake();
    }

    void SyncDefaultLink() {
        lock (linksGate) if (explicitLinks) return;
        string secret = "";
        try {
            byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(dataDir, "password.dpapi")), null, DataProtectionScope.CurrentUser);
            try { secret = Encoding.UTF8.GetString(bytes); } finally { Array.Clear(bytes, 0, bytes.Length); }
        } catch { }
        var endpoint = new RelayEndpoint { Name = "primary", Origin = Origin, Password = secret };
        RelayLink created = null;
        var removed = new List<RelayLink>();
        lock (linksGate) {
            if (explicitLinks) return;
            if (links.Count == 1 && links[0].Active && links[0].Key == endpoint.Key) return;
            foreach (var old in links) removed.Add(old);
            links.Clear();
            if (!String.IsNullOrWhiteSpace(endpoint.Origin) && endpoint.Password.Length > 0) {
                var next = new RelayLink(endpoint);
                if (next.IsValid) { links.Add(next); created = next; }
                else next.Dispose();
            }
        }
        foreach (var old in removed) old.Dispose();
        if (running && created != null) StartLinkWorkers(created);
    }

    /// Drops stale HTTP sockets/backoffs across all links (used after Windows sleep/resume or network change).
    public void RefreshConnections() {
        sessionsDirty = true;
        foreach (var link in ActiveLinks()) {
            link.ResetAuth();
            link.WakeReport();
        }
        soon = true;
        Wake();
    }

    // The address or the password changed: sign in again at once instead of after the usual pause.
    public void Reset() {
        sessionsDirty = true;
        bool isExplicit;
        lock (linksGate) isExplicit = explicitLinks;
        if (!isExplicit) {
            List<RelayLink> removed;
            lock (linksGate) { removed = new List<RelayLink>(links); links.Clear(); }
            foreach (var old in removed) old.Dispose();
        } else {
            foreach (var link in ActiveLinks()) link.ResetAuth();
        }
        soon = true;
        Wake();
    }
    static HttpContent JsonBody(string text, bool compress) {
        if (!compress || text.Length < 2048) return new StringContent(text, Encoding.UTF8, "application/json");
        byte[] raw = Encoding.UTF8.GetBytes(text);
        using (var buffer = new MemoryStream()) {
            using (var gz = new GZipStream(buffer, CompressionLevel.Fastest, true)) gz.Write(raw, 0, raw.Length);
            var content = new ByteArrayContent(buffer.ToArray());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            content.Headers.ContentEncoding.Add("gzip");
            return content;
        }
    }
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
                if ((int)reply.StatusCode != 200 || !text.Contains("\"signed_in\"")) return "这个地址上没有 Remote AI 中转，请核对地址和端口";
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
                var list = active.Select(t => new[] { t.Tool, t.Dir.Length > 0 ? t.Dir : Path.GetFileName(t.Folder), t.Status ?? "", t.Started.ToString("o"), sessions.Where(s => s.Id == t.Session).Select(s => s.Title).FirstOrDefault() ?? "", "手机 Remote AI", "phone", t.Id }).ToList();
                // Held but idle conversations remain visible without being counted as executing.
                // Scan excludes this agent's Claude processes. A remaining Claude row is an
                // independent process even when a phone terminal resumed the same conversation.
                list.AddRange(sessions.Where(s => s.Live && (s.Tool == "claude" || !active.Any(t => t.Session == s.Id))).Select(s => new[] { s.Tool, s.Dir.Length > 0 ? s.Dir : Path.GetFileName(s.Folder), s.Now ? ObservedStatus(s, Reach.Of(s.Tool, s.Id)) : "idle", s.Created.ToString("o"), s.Title, s.Origin, s.Host }));
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
            Tool kind = Tool.Named(live.Tool);
            if (!kind.Converses) {
                live.Pty.Dispose();
                return new ComputerTakeover { Tool = live.Tool, Dir = where, Session = "", Launcher = FindTool(live.Tool), PhoneTerminal = true };
            }
            if (!kind.NamedAtStart && live.Session.Length == 0) { Scan(dirs); }
            string session = live.Session;
            if (session.Length == 0) throw new InvalidOperationException(kind.Unnamed);
            if (!kind.HeldUnder(session, live.Pty.Id, Parents())) throw new InvalidOperationException("无法确认这个 " + kind.Label + " 进程属于手机终端，已取消接管");
            live.Pty.Dispose();
            for (int n = 0; n < 60 && kind.Open(session); n++) Thread.Sleep(50);
            if (kind.Open(session)) throw new InvalidOperationException("原 " + kind.Label + " 进程没有及时结束，已取消接管");
            return new ComputerTakeover { Tool = live.Tool, Dir = where, Session = session, Launcher = FindTool(live.Tool), PhoneTerminal = true };
        }
    }
    /// Runs a blocking operation off the agent's report lock with optional concurrency limit and consistent completion reporting.
    void RunAsync(string id, Func<Dictionary<string, object>> run, Func<Exception, string> describeError, bool rescan = false, TimeSpan? rescanDelay = null, bool markActivity = false, bool limited = true) {
        if (limited && relaying.Count >= 4) throw new InvalidOperationException("电脑正在处理其它操作，请稍后再试");
        if (!relaying.Add(id)) return;
        Task.Run(() => {
            string failed = ""; Dictionary<string, object> answer = null;
            try { answer = run(); }
            catch (Exception problem) { failed = describeError(problem); }
            lock (work) {
                completed[id] = new Dictionary<string, object> { { "id", id }, { "error", failed } };
                if (answer != null && failed.Length == 0) completed[id]["result"] = answer;
                relaying.Remove(id); reported.Remove(id);
                foreach (var link in ActiveLinks()) link.ClearReported(id);
                soon = true;
                if (markActivity) lastActivity = DateTime.UtcNow;
                if (rescan) nextScan = rescanDelay.HasValue ? DateTime.UtcNow + rescanDelay.Value : DateTime.MinValue;
            }
            Wake();
        });
    }
    static string DesktopError(Exception problem) {
        return problem is InvalidOperationException || problem is ArgumentException ? problem.Message : "桌面操作结果无法确认，请检查电脑上的应用";
    }
    void Execute(Dictionary<string, object> op, Dictionary<string, object> prefs) {
        string id = Get(op, "id"), terminal = Get(op, "terminal"), action = Get(op, "action");
        string transport = Get(op, "transport");
        bool agyControl = (action == "session_say" || action == "session_stop" || action == "session_answer")
            && (transport == Ways.AgyApp || (Get(op, "tool") == "agy" && sessions.Any(s => s.Id == Get(op, "session") && AgyAppSession(s))));
        bool nativeControl = Get(op, "transport") == Ways.App && (action == "session_say" || action == "session_stop");
        // Native requests keep their durable identity even when the current route becomes a hook.
        // Resolve that fallback inside Desktop.Run, after replay/query and before dispatch.
        // (A message said again under its number is answered by how it stands now: the Outbox knows, also once the
        // answer kept here has gone out and been dropped. While the answer is still kept, it is simply given again:
        // the relay hands an operation over until its answer has arrived.)
        Dictionary<string, object> kept;
        bool saidBefore = action == "session_say" && Reach.Outbox.Find(id) != null && !(completed.TryGetValue(id, out kept) && kept.ContainsKey("result"));
        if (completed.ContainsKey(id) && action != "app_open" && !nativeControl && !agyControl && !saidBefore) {
            reported.Remove(id);
            foreach (var link in ActiveLinks()) link.ClearReported(id);
            return;
        }
        if (relaying.Contains(id)) return;       // being handed over: answered when that is done
        string error = ""; Dictionary<string, object> result = null;
        try {
            bool project = action == "project_add" || action == "project_remove" || action == "project_rename", file = action == "file_list" || action == "file_read" || action == "file_find" || FileTransfers.Handles(action), update = action == "update";
            bool desktop = action == "app_open", organize = action == "session_organize";
            bool peek = action == "session_read" || action == "session_say" || action == "session_withdraw" || action == "session_release" || action == "session_stop" || action == "session_answer" || action == "session_mark" || action == "session_delete" || action == "session_end";
            bool computer = Array.IndexOf(ComputerActions, action) >= 0, ownWork = Array.IndexOf(WorkActions, action) >= 0, git = Array.IndexOf(GitActions, action) >= 0;
            if (!Regex.IsMatch(id, @"\A[a-f0-9]{16,32}\z")) throw new ArgumentException("操作编号无效");
            if (!ownWork && action.StartsWith("work_", StringComparison.Ordinal)) throw new ArgumentException("这台电脑的电脑端版本不支持这项操作，请更新电脑端");
            if (!project && !file && !update && !peek && !computer && !ownWork && !git && !desktop && !organize && !Regex.IsMatch(terminal, @"\A[a-f0-9]{32}\z")) throw new ArgumentException("操作编号无效");
            double at; if (!Double.TryParse(Get(op, "at"), out at) || Math.Abs((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds - at) > 300) throw new ArgumentException("操作已过期（请检查电脑与服务器时间是否同步）");
            if (Get(prefs, "RemoteEnabled") != "True") throw new InvalidOperationException("电脑远控已关闭");
            if (op.ContainsKey("receipt") && (!(op["receipt"] is bool) || true.Equals(op["receipt"]) && !nativeControl && !agyControl))
                throw new ArgumentException("回执查询无效");
            if (project) ChangeProjects(op, prefs, action);
            else if (update) { if (UpdateRequested == null) throw new InvalidOperationException("这台电脑运行的是不带自动更新的后台程序"); UpdateRequested(); }
            else if (action == "work_stats") {
                // read from the tools' records beside the terminals' work: the first reading of many records takes a while
                int days; if (!Int32.TryParse(Get(op, "days"), out days)) days = 14;
                var dirs = AllDirs(prefs);
                RunAsync(id, () => Stats.Read(days, folder => NamedFolder(folder, dirs)), _ => "统计没有读出来，请稍后再试");
                return;
            }
            else if (action == "work_team_models") {
                // each tool is started for a moment to say what it offers, beside the terminals' work
                RunAsync(id, () => Models.Listed(), _ => "没有读出可选的模型，请稍后再试");
                return;
            }
            else if (organize) {
                RunAsync(id, () => Organizer.Operate(op), problem => problem is ArgumentException || problem is InvalidOperationException
                    ? problem.Message : "电脑无法读取或保存对话整理记录");
                return;
            }
            else if (ownWork) result = Work(action, op, prefs);
            else if (git) {
                string root;
                if (!AllDirs(prefs).TryGetValue(Get(op, "dir"), out root) || !Directory.Exists(root)) throw new ArgumentException("项目目录未获允许或不存在");
                RunAsync(id, () => GitReview.Handle(root, op, Worktrees), problem => problem is ArgumentException || problem is InvalidOperationException ? problem.Message : "Git 操作失败：" + problem.Message, limited: false);
                return;
            }
            else if (computer) { if (Computer == null) throw new InvalidOperationException("这台电脑运行的后台程序没有可在手机上更改的设置"); result = Computer(op); }
            else if (desktop) {
                string tool = Get(op, "tool"), folder, draft = Get(op, "draft");
                if (!Desktop.Available(tool)) throw new InvalidOperationException("这台电脑暂不支持此桌面应用入口");
                if (!AllDirs(prefs).TryGetValue(Get(op, "dir"), out folder) || !Directory.Exists(folder)) throw new ArgumentException("项目文件夹未获允许或已不存在");
                Desktop.Check(tool, folder, draft); // validate before reserving the operation
                RunAsync(id, () => {
                    var opened = Desktop.Open(id, tool, folder, draft);
                    string created = Get(opened, "session");
                    if (tool == "agy" && SessionLocks.IsUuid(created)) {
                        lock (newAgySessions) newAgySessions[created] = new Antigravity.Summary { Id = created, Folder = folder, Updated = Milliseconds(DateTime.UtcNow) };
                        RefreshAgy(created);
                    }
                    return opened;
                }, DesktopError, rescan: true);
                return;
            }
            else if (agyControl) {
                if (transport.Length > 0 && transport != Ways.AgyApp) throw new InvalidOperationException("这段对话的桌面连接已变化，请刷新后重试");
                string session = Get(op, "session"), dir = Get(op, "dir"), text = Get(op, "text"), ask = Get(op, "ask"), choice = Get(op, "choice");
                string scope = Get(op, "scope"), server = Get(prefs, "Server");
                if (scope.Length == 0) scope = "once";
                object allowValue;
                bool allow = op.TryGetValue("allow", out allowValue) && true.Equals(allowValue);
                if (Get(op, "tool") != "agy" || !SessionLocks.IsUuid(session)) throw new ArgumentException("桌面对话编号无效");
                if (action == "session_answer") {
                    if (!(allowValue is bool) || !Regex.IsMatch(ask, @"\A[a-f0-9]{8,32}\z")) throw new ArgumentException("回答无效");
                    if (scope != "once" && !(allow && scope == "always")) throw new ArgumentException("授权范围无效");
                    if (op.ContainsKey("choice") && (scope != "once" || !(allow ? Regex.IsMatch(choice, @"\Achoice:(0|[1-9][0-9]{0,2})\z") : choice == "deny")))
                        throw new ArgumentException("回答选项无效");
                }
                bool query = true.Equals(op.ContainsKey("receipt") ? op["receipt"] : null);
                RunAsync(id, () => Desktop.Run(id, new[] { action, "agy", session, dir, text, ask, allow.ToString(), scope, choice }, () => {
                    var saved = sessions.FirstOrDefault(s => AgyAppSession(s) && s.Id == session && s.Dir == dir && s.Live);
                    if (saved == null) throw new InvalidOperationException("电脑上没有找到这个桌面对话，请刷新后重试");
                    string root = DesktopFolder(saved, prefs);
                    ValidateDesktopConversation(session, dir, root, server, "agy");
                    var state = AgyDesktopClient.Inspect(session);
                    if (!state.Live || !SamePath(state.Folder, root)) throw new InvalidOperationException("桌面对话的工作目录已变化");
                    ValidateDesktopConversation(session, dir, root, server, "agy");
                    var ways = Ways.Of(saved, null, null, state);
                    string verb = action.Substring("session_".Length);
                    if (!Ways.AgyApp.Equals(ways.Named[verb])) throw new InvalidOperationException(ways.Why[verb]);
                    Dictionary<string, object> answer;
                    if (verb == "answer") {
                        string selection = choice.Length > 0 ? choice : allow ? scope : "deny";
                        var options = state.AskOptions.OfType<Dictionary<string, object>>();
                        if (state.AskId != ask || !(options.Any(o => Get(o, "id") == selection) || (!allow && state.AskKind == "question")))
                            throw new InvalidOperationException("确认请求或回答选项已变化，请刷新后重试");
                        answer = AgyDesktopClient.Answer(session, ask, allow, scope, choice);
                    } else answer = verb == "say" ? AgyDesktopClient.Send(session, id, text, state.LastModel) : AgyDesktopClient.Stop(session);
                    RefreshAgy(session);
                    return answer;
                }, true, query), DesktopError, rescan: true);
                return;
            }
            else if (nativeControl) {
                string session = Get(op, "session"), dir = Get(op, "dir"), text = Get(op, "text"), expectedTurn = Get(op, "turn"), root;
                var saved = sessions.FirstOrDefault(s => s.Tool == "codex" && s.Id == session && s.Dir == dir);
                if (Get(op, "tool") != "codex" || saved == null || !SessionLocks.IsUuid(session)) throw new ArgumentException("电脑上没有找到这个对话");
                root = DesktopFolder(saved, prefs);
                bool queryOnly = Get(op, "receipt") == "True";
                string server = Get(prefs, "Server");
                RunAsync(id, () => Desktop.Run(id, new[] { action, "codex", session, dir, text, expectedTurn }, () => {
                    ValidateDesktopConversation(session, dir, root, server);
                    if (action == "session_say") {
                        lock (work) {
                            var current = sessions.FirstOrDefault(s => s.Tool == "codex" && s.Id == session && s.Live && s.Dir == dir);
                            if (current != null && SamePath(current.ControlFolder, root) && WaysOf(current, Reach.Of("codex", session)).Say == Ways.Hook) {
                                ValidateDesktopConversation(session, dir, root, server);
                                return new Dictionary<string, object> { { "status", "accepted" }, { "sent", true }, { "now", Reach.Say("codex", session, text, id) }, { "way", Ways.Hook }, { "state", Outbox.Queued }, { "message", id } };
                            }
                        }
                    }
                    using (var control = OpenDesktopConversation()) {
                        var state = control.Inspect(session);
                        if (!SamePath(CodexDesktopControl.Folder(state), root)) throw new InvalidOperationException("桌面对话的工作目录已变化");
                        ValidateDesktopConversation(session, dir, root, server);
                        if (action == "session_say") return control.Send(id, text);
                        var stopped = control.Interrupt(expectedTurn);
                        Reach.Outbox.Pause("codex", session);
                        return stopped;
                    }
                }, true, queryOnly), DesktopError, rescan: true);
                return;
            }
            else if (action == "session_stop") {
                // Without an application's channel: Claude Code, told before its next step. It ends its turn there and stays open.
                string session = Get(op, "session"), tool = Get(op, "tool");
                var working = sessions.FirstOrDefault(s => s.Id == session && s.Tool == tool && s.Live);
                if (working == null) throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
                var ways = WaysOf(working, Reach.Of(tool, session));
                if (ways.Stop != Ways.Hook) throw new InvalidOperationException(ways.Stop.Length > 0 ? "这段对话的停止方式已变化，请刷新后重试" : ways.Why["stop"]);
                Reach.Stop(tool, session);
                result = new Dictionary<string, object> { { "status", "asked" }, { "transport", Ways.Hook } };
            }
            else if (action == "session_read") {
                // Always read transcripts off `lock (work)` so large JSONL files or slow disks never stall heartbeats.
                var opCopy = new Dictionary<string, object>(op);
                bool isAgy = Get(opCopy, "tool") == "agy" && sessions.Any(s => s.Id == Get(opCopy, "session") && AgyAppSession(s));
                RunAsync(id, () => ReadConversation(opCopy, prefs),
                    isAgy ? (Func<Exception, string>)DesktopError : (problem => problem is ArgumentException || problem is InvalidOperationException ? problem.Message : "读不到这个对话的内容"),
                    limited: false);
                return;
            }
            else if (action == "session_mark") {
                // pinned or hidden, or no longer: of a conversation this computer listed
                string session = Get(op, "session"), tool = Get(op, "tool");
                if (!sessions.Any(s => s.Id == session && s.Tool == tool)) throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
                foreach (string kind in Marks.Kinds.Where(op.ContainsKey)) Marks.Set(kind, tool, session, Get(op, kind) == "True");
                sessionsDirty = true;
                result = new Dictionary<string, object>();
            }
            else if (action == "session_delete") {
                // Put away (Removal.cs): only one this computer listed, of a project, that no program has open and no
                // terminal of the phone's runs. What its owner said of it (pinned, hidden) goes with it.
                string session = Get(op, "session"), tool = Get(op, "tool"), root;
                var gone = sessions.FirstOrDefault(s => s.Id == session && s.Tool == tool);
                if (gone == null || !Regex.IsMatch(session, Uuid) || !AllDirs(prefs).TryGetValue(gone.Dir, out root)) throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
                if (gone.Live || terminals.Values.Any(t => t.Session == session && !t.Pty.Closed)) throw new InvalidOperationException("这段对话正开着，请先结束它再删除");
                try {
                    Tool.Named(tool).Remove(session, root, FindTool(tool));
                }
                catch (FileNotFoundException) { throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试"); }
                catch (UnauthorizedAccessException) { throw new InvalidOperationException("没有权限移走这段对话的文件"); }
                foreach (string kind in Marks.Kinds) Marks.Set(kind, tool, session, false);
                sessions = sessions.Where(s => !(s.Id == session && s.Tool == tool)).ToList();
                sessionsDirty = true;
                nextScan = DateTime.MinValue;
                result = new Dictionary<string, object>();
            }
            else if (action == "session_end") {
                // The program that has the conversation on this computer is ended: it stops at once, whatever it was
                // at, and is no longer open anywhere. Its record stays, to be gone on with. Only one this computer
                // listed as open, whose program is known for certain and is not one of this program's own.
                string session = Get(op, "session"), tool = Get(op, "tool");
                var open = sessions.FirstOrDefault(s => s.Id == session && s.Tool == tool && s.Live);
                if (open == null || !Regex.IsMatch(session, Uuid)) throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
                var ways = WaysOf(open, Reach.Of(tool, session));
                if (ways.End != Ways.Process) throw new InvalidOperationException(ways.Why["end"]);
                Tool.Named(tool).End(session, OwnRoots(), Parents());
                nextScan = DateTime.MinValue;
                result = new Dictionary<string, object>();
            }
            else if (peek) {
                // a conversation a program on this computer has open: only one this computer listed, in a project or where it was found open
                string session = Get(op, "session"), tool = Get(op, "tool");
                // (what waits for a conversation can be taken back also once no program has it open any more)
                if (action != "session_withdraw" && !sessions.Any(s => s.Id == session && s.Tool == tool && s.Live && (s.Dir.Length == 0 ? s.Folder.Length > 0 : AllDirs(prefs).ContainsKey(s.Dir)))) throw new ArgumentException("电脑上没有找到这个对话，请刷新后重试");
                if (action == "session_say") {
                    var listed = sessions.First(s => s.Id == session && s.Tool == tool && s.Live);
                    // Said before under this number (the answer was lost on its way, or this program started again):
                    // how it stands is answered, and nothing is sent anew.
                    var before = Reach.Outbox.Find(id);
                    if (before != null) {
                        if (before.Tool != tool || !String.Equals(before.Session, session, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("操作编号已被其它消息使用");
                        result = Told(before, before.State == Outbox.Handed ? "" : Ways.Hook);
                    } else {
                    // By the way that is there now (Ways.cs). The application's channel is asked for by name, with a
                    // receipt (above); here are the tool's hooks, and the tool's own way to one that sits idle.
                    var ways = WaysOf(listed, Reach.Of(tool, session));
                    if (ways.Say.Length == 0) throw new InvalidOperationException(ways.Why["say"]);
                    if (ways.Say == Ways.App) throw new InvalidOperationException("这段对话的发送方式已变化，请再发一次");
                    if (ways.Say == Ways.Wake) {
                        // It sits idle: the message is handed to it where it is. That takes some seconds, so it is done
                        // beside the terminals' work, and the operation is answered when it has been taken or has failed.
                        string text = (Get(op, "text") ?? "").Trim();
                        if (text.Length == 0 || text.Length > 8000) throw new ArgumentException("消息需为 1 至 8000 字");
                        Reach.Leave(tool, session, text, id);
                        RunAsync(id, () => {
                            try { Awaken(tool, session); }
                            catch (Exception) {
                                // what could not be handed over does not stay waiting (unless its hooks came for it meanwhile)
                                if (Reach.Outbox.Forget(id)) throw;
                            }
                            var answer = Told(Reach.Outbox.Find(id) ?? new Outbox.Item { Id = id, State = Outbox.Handed }, Ways.Wake);
                            answer["woke"] = true;
                            return answer;
                        }, problem => problem is InvalidOperationException ? problem.Message : "消息没有送出：电脑上启动不了这个工具",
                            rescan: true, rescanDelay: TimeSpan.FromSeconds(1), markActivity: true, limited: false);
                        return;
                    }
                    bool now = Reach.Say(tool, session, Get(op, "text"), id);
                    result = Told(Reach.Outbox.Find(id), Ways.Hook); result["now"] = now;       // (one that waits for a message takes it within a moment)
                    }
                }
                else if (action == "session_withdraw") {
                    // a message that still waits, or is held, is taken back: it is not handed over any more
                    var item = Reach.Outbox.Find(Get(op, "message"));
                    if (item == null || item.Tool != tool || !String.Equals(item.Session, session, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("没有找到这条消息，请刷新后重试");
                    if (!Reach.Outbox.Withdraw(item.Id)) throw new InvalidOperationException("这条消息已经交给对话，撤不回了");
                    result = new Dictionary<string, object> { { "state", Outbox.Withdrawn }, { "message", item.Id } };
                }
                else if (action == "session_release") {
                    // What was held when the conversation was told to stop is to go after all: it waits again, and is
                    // handed over by the way that is there now. Where there is none, it stays held, with the reason.
                    var listed = sessions.First(s => s.Id == session && s.Tool == tool && s.Live);
                    var ways = WaysOf(listed, Reach.Of(tool, session));
                    if (ways.Say.Length == 0) throw new InvalidOperationException(ways.Why["say"]);
                    if (ways.Say == Ways.App) throw new InvalidOperationException("Codex 桌面版现在空闲：请把这几条消息撤回后直接发送");
                    var released = Reach.Outbox.Release(tool, session);
                    if (released.Length == 0) throw new InvalidOperationException("没有暂停的消息");
                    if (ways.Say == Ways.Wake) {
                        foreach (var item in released) retried.Add(item.Id);
                        Task.Run(() => { try { Awaken(tool, session); } catch (InvalidOperationException) { } catch (IOException) { } catch (System.ComponentModel.Win32Exception) { } lock (work) { soon = true; nextScan = DateTime.UtcNow.AddSeconds(1); } Wake(); });
                    }
                    result = new Dictionary<string, object> { { "released", released.Length }, { "way", ways.Say } };
                }
                else { Reach.Answer(tool, session, Get(op, "ask"), Get(op, "allow") == "True", Get(op, "scope").Length == 0 ? "once" : Get(op, "scope")); result = new Dictionary<string, object> { { "status", "accepted" } }; }
            }
            else if (file) {
                string root;
                if (!AllDirs(prefs).TryGetValue(Get(op, "dir"), out root)) throw new ArgumentException("目录未获允许");
                result = FileTransfers.Handles(action) ? uploads.Run(root, op) : action == "file_find" ? FileSearch.Find(root, Get(op, "path")) : Files(root, Get(op, "path"), action, op.ContainsKey("offset") ? Convert.ToInt64(op["offset"]) : 0);
            }
            else if (action == "start") {
                if (terminals.ContainsKey(terminal)) return;
                string tool = Get(op, "tool"), dir = Get(op, "dir"), previous = Get(op, "previous"), session = Get(op, "session");
                // The same conversation shown the other way: what it goes on with is what the terminal it leaves had.
                LiveTerminal left = op.ContainsKey("reopen") ? Reopening(op, tool, dir) : null;
                if (left != null) {
                    session = left.Chat != null ? left.Chat.Session : left.Session;
                    if (!op.ContainsKey("model")) { op["model"] = left.Model; op["effort"] = left.Effort; }
                    op["mode"] = left.Mode;
                }
                // Another AI goes on with a conversation in-place: its source, and the phone's terminal running it if any.
                SessionInfo handed = (op.ContainsKey("from") || op.ContainsKey("from_tool")) ? Handed(op, tool, dir, session) : null;
                LiveTerminal handedRuns = handed == null ? null : terminals.Values.FirstOrDefault(t => !t.Pty.Closed && t.Tool == handed.Tool
                    && String.Equals(t.Chat != null ? t.Chat.Session : t.Session, handed.Id, StringComparison.OrdinalIgnoreCase));
                if (handedRuns != null) {
                    string was = handedRuns.Chat != null ? handedRuns.Chat.Status : handedRuns.Status;
                    if (was == "busy" || was == "confirm") throw new InvalidOperationException("对话正在执行或等待确认，请等这一轮结束或先停止");
                }
                if (left == null && handedRuns == null) RequireTerminalSlot();
                Tool kind = Tool.Named(tool);
                if (Get(op, "history") == "True" && session.Length == 0 && previous.Length == 0) throw new ArgumentException("请选择具体历史对话，或重新新建对话");
                bool fork = Get(op, "fork") == "True";
                if (fork && (!kind.Forks || session.Length == 0 || Get(op, "takeover") == "True")) throw new ArgumentException("副本选项无效");
                var dirs = AllDirs(prefs);
                // In a project; or, to go on with a conversation that is open in a folder that is no project, in that
                // folder (the relay says which, from what this computer listed).
                string folder = left != null ? left.Folder : dir.Length == 0 && (session.Length > 0 || previous.Length > 0) && kind.Converses ? NormalFolder(Get(op, "folder")) : "";
                if (dir.Length == 0 ? folder.Length == 0 || !Directory.Exists(folder) : !dirs.ContainsKey(dir)) throw new ArgumentException("目录未获允许");
                string launcher = FindTool(tool);
                if (launcher == null) throw new ArgumentException("电脑未安装这个工具");
                if (left != null) {
                    // its record is what the other way reads; the terminal it leaves is ended only once that is certain
                    if (ConversationFile(tool, session, Place(left, dirs)) == null) throw new InvalidOperationException(Label(tool) + " 还没有保存这段对话，发一条消息后再切换");
                    Leave(left, kind, session);
                } else if (session.Length > 0) {
                    var scanned = Scan(dirs);
                    bool found = scanned.Any(s => String.Equals(s.Id, session, StringComparison.OrdinalIgnoreCase) && s.Tool == tool
                        && (s.Dir == dir || (dir.Length == 0 && SamePath(s.Folder, folder)) || (dir.Length > 0 && dirs.ContainsKey(dir) && SamePath(s.Folder, dirs[dir]))));
                    if (!found) throw new ArgumentException("电脑上没有找到这个对话");
                    // a copy leaves the original where it is
                    if (!fork) kind.Exclusive(session, Get(op, "takeover") == "True", OwnRoots(), Parents(), null);
                } else if (previous.Length > 0) {
                    if (!Regex.IsMatch(previous, @"\A[a-f0-9]{16,32}\z")) throw new ArgumentException("历史编号无效");
                    session = List(prefs, "RemoteSessions").Select(Convert.ToString).Where(s => s.StartsWith(previous + "|", StringComparison.Ordinal)).Select(s => s.Substring(previous.Length + 1)).FirstOrDefault() ?? "";
                    if (session.Length == 0) throw new ArgumentException("旧对话没有会话编号，请使用历史对话入口");
                    kind.Exclusive(session, false, OwnRoots(), Parents(), "原对话仍被占用，请从项目中选择接管原对话");
                } else if (handed != null && Get(op, "form") == "chat") {
                    string candidate = Handovers.PreviousSession(handed.Tool, handed.Id, tool);
                    string handedPlace = handedRuns != null && !String.IsNullOrEmpty(handedRuns.WorktreePath) && Directory.Exists(handedRuns.WorktreePath) ? handedRuns.WorktreePath : dirs[dir];
                    if (candidate.Length > 0 && ConversationFile(tool, candidate, handedPlace) != null
                        && !terminals.Values.Any(t => !t.Pty.Closed && t.Tool == tool && String.Equals(t.Chat != null ? t.Chat.Session : t.Session, candidate, StringComparison.OrdinalIgnoreCase))) {
                        try { kind.Exclusive(candidate, false, OwnRoots(), Parents(), null); session = candidate; }
                        catch (InvalidOperationException) { }
                    }
                }
                bool fresh = kind.NamedAtStart && session.Length == 0 && Get(op, "history") != "True";
                if (fresh) session = Guid.NewGuid().ToString();
                var live = new LiveTerminal { Id = terminal, Tool = tool, Dir = dir, Folder = folder, Session = fork ? "" : session };
                if (left != null) { live.WorktreeId = left.WorktreeId; live.WorktreePath = left.WorktreePath; live.Branch = left.Branch; }
                else if (Get(op, "worktree") == "True" && dir.Length > 0) {
                    var createdWt = Worktrees.CreateSessionWorktree(dirs[dir], terminal.Substring(0, 12), Get(op, "base_branch"), Get(op, "branch"));
                    live.WorktreeId = createdWt.Id;
                    live.WorktreePath = createdWt.Path;
                    live.Branch = createdWt.Branch;
                }
                string requestedMode = Get(op, "mode").Trim().ToLowerInvariant();
                string maxMode = Get(prefs, "RemoteMaxMode");
                if (Get(op, "form") == "chat") {
                    // A conversation run through its tool's structured mode: what it says and does is given as events, and
                    // the first thing to say, where there is one, is said at once (Chats.cs).
                    if (!kind.Chats) throw new ArgumentException("这个工具不支持对话");
                    if (fork || Get(op, "history") == "True") throw new ArgumentException("对话选项无效");
                    bool resume = !fresh && session.Length > 0;
                    string place = Place(live, dirs);
                    // the model, effort and permission mode asked for; a conversation gone on with without one keeps what it had here
                    var before = resume && !op.ContainsKey("model") ? terminals.Values.Concat(ended).LastOrDefault(t => t.Chat != null && t.Tool == tool && String.Equals(t.Session, session, StringComparison.OrdinalIgnoreCase)) : null;
                    live.Model = before != null ? before.Model : Get(op, "model").Trim();
                    live.Effort = before != null ? before.Effort : Models.CleanEffort(Get(op, "effort"));
                    if (live.Model.Length > 0 && !Models.Valid(live.Model)) throw new ArgumentException("模型名称无效");
                    if (Get(op, "effort").Trim().Length > 0 && live.Effort.Length == 0) throw new ArgumentException("思考深度无效");
                    live.Mode = requestedMode.Length > 0 ? ClampMode(requestedMode, maxMode) : (before != null && before.Mode == "read" ? "read" : ClampMode("", maxMode));
                    string first = Get(op, "text").Trim();
                    string handedTitle = "";
                    if (handed != null) {
                        handedTitle = Handovers.Title(handed.Tool, handed.Id);
                        if (handedTitle.Length == 0) handedTitle = handed.Title;
                        live.Title = handedTitle;
                        if (handedRuns != null) {
                            live.WorktreeId = handedRuns.WorktreeId; live.WorktreePath = handedRuns.WorktreePath; live.Branch = handedRuns.Branch;
                            place = Place(live, dirs);
                        }
                        List<string> betweenTools = null;
                        var delta = resume ? DeltaMessages(place, handed.Tool, handed.Id, tool, session, out betweenTools) : null;
                        if (delta != null && delta.Count > 0) {
                            first = ResumeBrief(handed, String.Join("、", betweenTools.Select(Label)), delta) + "\n\n" + HandoverAsk + "\n" + first;
                        } else {
                            // Read the source conversation before disposing its terminal or opening the new AI.
                            var read = ReadConversation(new Dictionary<string, object> { { "session", handed.Id }, { "tool", handed.Tool }, { "dir", handed.Dir } }, prefs);
                            first = Brief(handed, read, "") + "\n\n" + HandoverAsk + "\n" + first;
                        }
                    }
                    live.Pty = Chat.Open(tool, launcher, place, session, resume, live.Mode, Emitted(live), Quotas, live.Mode == "read" ? null : Peers.Offer(tool, place, live.Id, live.Dir), live.Model, live.Effort);
                    Heard(live);
                    if (handedRuns != null) { handedRuns.Pty.Dispose(); terminals.Remove(handedRuns.Id); }
                    if (handed != null) {
                        foreach (string stale in terminals.Where(kv => kv.Key != terminal && kv.Value.Pty.Closed && kv.Value.Tool == handed.Tool && String.Equals(kv.Value.Session, handed.Id, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToArray()) {
                            terminals[stale].Pty.Dispose();
                            terminals.Remove(stale);
                        }
                        ended.RemoveAll(e => e.Tool == handed.Tool && String.Equals(e.Session, handed.Id, StringComparison.OrdinalIgnoreCase));
                    }
                    if (resume) {
                        foreach (string stale in terminals.Where(kv => kv.Key != terminal && kv.Value.Pty.Closed && kv.Value.Tool == tool && String.Equals(kv.Value.Session, session, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToArray()) {
                            terminals[stale].Pty.Dispose();
                            terminals.Remove(stale);
                        }
                        ended.RemoveAll(e => e.Tool == tool && String.Equals(e.Session, session, StringComparison.OrdinalIgnoreCase));
                    }
                    terminals[terminal] = live;
                    if (handed != null) {
                        Recall(live, place, handed.Tool, handed.Id, false);
                        if (live.Session.Length > 0) {
                            Handovers.Add(live.Tool, live.Session, handed.Tool, handed.Id, handedTitle);
                            sessions = sessions.Where(s => !Handovers.GoneOn(s.Tool, s.Id)).ToList();
                            sessionsDirty = true;
                            nextScan = DateTime.MinValue;
                        } else live.HandedFrom = handed;
                    } else if (resume) {
                        Recall(live, place);
                    }
                    if (first.Length > 0) live.Chat.Say(first, id);
                    // a team's member gone on with here is that member again (Teams.Rejoin)
                    if (resume && handed == null) { string chatSession = session; bool told = first.Length > 0; Task.Run(() => Teams.Rejoin(terminal, tool, chatSession, told)); }
                } else {
                    live.Model = Get(op, "model").Trim();
                    live.Effort = Models.CleanEffort(Get(op, "effort"));
                    if (live.Model.Length > 0 && !Models.Valid(live.Model)) throw new ArgumentException("模型名称无效");
                    if (Get(op, "effort").Trim().Length > 0 && live.Effort.Length == 0) throw new ArgumentException("思考深度无效");
                    live.Mode = ClampMode(requestedMode, maxMode);
                    live.Pty = new PseudoTerminal(Command(tool, launcher, session, Get(op, "history") == "True", live.Mode, fork, kind.SupportsStandalone && SupportsStandalone(launcher), fresh, live.Model, live.Effort), Place(live, dirs), 80, 24, Emitted(live));
                    terminals[terminal] = live;
                }
            } else {
                LiveTerminal live;
                if (!terminals.TryGetValue(terminal, out live) || live.Pty.Closed) throw new ArgumentException("终端已结束");
                if (Place(live, AllDirs(prefs)) == null) throw new ArgumentException("目录不再获允许");
                if (live.Chat != null) {
                    // a conversation: told something, stopped, answered, or ended; it has no screen to type into
                    lastInput = DateTime.UtcNow;
                    if (action == "say") {
                        string text = Get(op, "text").Trim();
                        object paths;
                        IList<Attachment> attachments = null;
                        if (op.TryGetValue("attachments", out paths) && paths != null) {
                            string folder;
                            if (!AllDirs(prefs).TryGetValue(live.Dir, out folder)) throw new ArgumentException("目录未被允许");
                            attachments = Attachments.Read(folder, paths);
                        }
                        if ((text.Length == 0 && (attachments == null || attachments.Count == 0)) || text.Length > 8000)
                            throw new ArgumentException("请输入文字或添加图片，文字最多 8000 字");
                        live.Chat.Say(text, id, attachments);
                        Teams.OwnerSpoke(live.Id);
                    }
                    else if (action == "stop") live.Chat.Stop();
                    else if (action == "answer") live.Chat.Answer(Get(op, "ask"), Get(op, "decision"));
                    else if (action == "close") { live.Pty.Dispose(); Teams.Closed(live.Id); }
                    else throw new ArgumentException("对话不接受这个操作");
                }
                else if (action == "input") { lastInput = DateTime.UtcNow; string text = Get(op, "data"); if (text.Length == 0 || text.Length > 16000) throw new ArgumentException("输入无效"); live.Pty.Write(text); }
                else if (action == "resize") { int cols = Convert.ToInt32(op["cols"]), rows = Convert.ToInt32(op["rows"]); if (cols < 20 || cols > 240 || rows < 6 || rows > 100) throw new ArgumentException("尺寸无效"); live.Pty.Resize(cols, rows); }
                else if (action == "close") live.Pty.Dispose();
                else throw new ArgumentException("操作无效");
            }
        } catch (Exception ex) { error = ex is ArgumentException || ex is InvalidOperationException || ex is IOException ? ex.Message : "终端操作失败：" + ex.GetType().Name; }
        completed[id] = new Dictionary<string, object> { { "id", id }, { "error", error } };
        if (result != null && error.Length == 0) completed[id]["result"] = result;
        reported.Remove(id);        // (answered anew, where it was asked again under its number: the answer is to go out again)
        foreach (var link in ActiveLinks()) link.ClearReported(id);
        lastActivity = DateTime.UtcNow; soon = true; if (action == "start" || action == "close") nextScan = DateTime.UtcNow.AddSeconds(1.5);
        if (action.StartsWith("project_", StringComparison.Ordinal)) nextScan = DateTime.MinValue;
    }
    sealed class LinkSnapshot {
        public Dictionary<string, object> Prefs;
        public object Info;
        public object[] TerminalList;
        public object[] Listed;
        public string ListedText;
        public DateTime At;
    }
    volatile LinkSnapshot latestSnapshot;
    object[] cachedListed = new object[0];
    string cachedListedText = "[]";

    async Task<bool> ReportLinkAsync(RelayLink link, LinkSnapshot snap, JavaScriptSerializer linkJson) {
        if (!link.Active || snap == null) return false;
        if (link.Client == null && !await link.EnsureLoginAsync(linkJson).ConfigureAwait(false)) return false;
        HttpClient current = link.Client;
        if (current == null) return false;
        string body;
        bool listing;
        Dictionary<string, object>[] acknowledgments;
        DateTime nowUtc = DateTime.UtcNow;
        lock (work) {
            List<Dictionary<string, object>> output;
            lock (gate) output = OutputBatch(terminals.Values.Concat(ended).SelectMany(t => link.SelectChunks(t, 30)));
            if (output.Count > 0) lastActivity = DateTime.UtcNow;
            int room = 3 * FilePiece, taken = 0;
            acknowledgments = completed.Where(p => {
                if (link.HasReported(p.Key)) return false;
                string owner;
                return !opSource.TryGetValue(p.Key, out owner) || owner == link.Key;
            }).Take(200).Select(p => p.Value).TakeWhile(a => {
                object answer; Dictionary<string, object> piece = a.TryGetValue("result", out answer) ? answer as Dictionary<string, object> : null;
                room -= piece != null && piece.ContainsKey("data") ? ((string)piece["data"]).Length : 0;
                return taken++ == 0 || room >= 0;
            }).ToArray();
            var payload = new Dictionary<string, object> {
                { "info", snap.Info },
                { "terminals", snap.TerminalList },
                { "output", output },
                { "acks", acknowledgments }
            };
            listing = link.NeedsSessions(snap.ListedText, nowUtc);
            if (listing) payload["sessions"] = snap.Listed;
            body = linkJson.Serialize(payload);
        }
        var request = new HttpRequestMessage(HttpMethod.Post, link.Origin + "/api/terminal/agent");
        request.Headers.TryAddWithoutValidation("Origin", link.Origin);
        request.Content = JsonBody(body, link.AcceptsGzip);
        try {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(link.Token)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using (request) using (var response = await current.SendAsync(request, timeout.Token).ConfigureAwait(false)) {
                    if ((int)response.StatusCode == 401) { link.ResetAuth(); await link.EnsureLoginAsync(linkJson).ConfigureAwait(false); return false; }
                    if (!response.IsSuccessStatusCode) { link.RecordFailure(); return false; }
                    IEnumerable<string> acceptEnc;
                    if (!link.AcceptsGzip && response.Headers.TryGetValues("X-RemoteCli-Accept-Encoding", out acceptEnc) && acceptEnc.Any(v => v.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0))
                        link.AcceptsGzip = true;
                    string answer = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    bool morePending = false;
                    lock (work) {
                        var result = linkJson.Deserialize<Dictionary<string, object>>(answer);
                        foreach (var a in acknowledgments) {
                            string opId = Convert.ToString(a["id"]);
                            link.MarkReported(opId);
                            reported.Add(opId);
                            if (a.Remove("result") && Convert.ToString(a["error"]).Length == 0) a["error"] = "电脑的回答没有送到，请重试";
                        }
                        if (listing) link.MarkSessionsSent(snap.ListedText, DateTime.UtcNow);
                        var ack = result != null && result.ContainsKey("output_ack") ? result["output_ack"] as Dictionary<string, object> : null;
                        if (ack != null) link.NoteOutputAck(ack);
                        var named = result != null && result.ContainsKey("titles") ? result["titles"] as Dictionary<string, object> : null;
                        if (named != null) foreach (var t in terminals.Values.Concat(ended)) { object title; if (named.TryGetValue(t.Id, out title)) t.Title = Convert.ToString(title); }
                        foreach (object item in List(result, "operations")) {
                            var op = item as Dictionary<string, object>;
                            if (op == null) continue;
                            string opId = Get(op, "id");
                            if (opId.Length > 0 && !opSource.ContainsKey(opId)) opSource[opId] = link.Key;
                            Execute(op, snap.Prefs);
                            morePending = true;
                        }
                    }
                    link.RecordSuccess(DateTime.UtcNow);
                    SettleHealthyLinks();
                    if (morePending) Wake();
                    return true;
                }
            }
        } catch (Exception) {
            if (link.Active) link.RecordFailure();
            return false;
        }
    }

    void SettleHealthyLinks() {
        var currentLinks = ActiveLinks();
        DateTime nowUtc = DateTime.UtcNow;
        var healthyLinks = currentLinks.Where(l => l.Active && l.RecentlyHealthy(nowUtc)).ToList();
        if (healthyLinks.Count == 0) return;

        lock (work) {
            lock (gate) {
                foreach (var t in terminals.Values.Concat(ended)) {
                    long have = RelayLink.MinimumAck(healthyLinks, t.Id, t.Seq);
                    if (Settle(t, have, t.Pty.Closed ? KeepEnded : KeepRunning)) soon = true;
                }
            }
            if (completed.Count > 4000) foreach (string key in completed.Keys.Take(1000).ToArray()) { completed.Remove(key); reported.Remove(key); opSource.Remove(key); }
            // A terminal is forgotten only after its last output has been read from the program and stored by the relay.
            foreach (var t in terminals.Values.Where(t => t.Pty.Closed && t.ClosedSeen == DateTime.MinValue)) t.ClosedSeen = DateTime.UtcNow;
            string[] finished; lock (gate) finished = terminals.Where(t => t.Value.Pty.Closed && (t.Value.Pty.Drained || DateTime.UtcNow - t.Value.ClosedSeen > TimeSpan.FromSeconds(15)) && t.Value.Output.Count == 0 && t.Value.Pending.Length == 0).Select(t => t.Key).ToArray();
            foreach (string key in finished) {
                var over = terminals[key]; over.Pty.Dispose(); terminals.Remove(key);
                ended.RemoveAll(e => e.Session.Length > 0 && e.Tool == over.Tool && e.Session == over.Session);
                if (over.Session.Length > 0 && Handovers.GoneOn(over.Tool, over.Session)) {
                    foreach (var link in currentLinks) link.ForgetTerminal(over.Id);
                    continue;
                }
                lock (gate) Settle(over, over.Seq, KeepEnded);
                ended.Add(over); if (ended.Count > EndedMost) {
                    var dropped = ended[0]; ended.RemoveAt(0);
                    foreach (var link in currentLinks) link.ForgetTerminal(dropped.Id);
                }
            }
        }
    }

    Task Tick() {
        uploads.Expire();
        Dictionary<string, object> prefs = Preferences();
        if (Get(prefs, "RemoteEnabled") == "True") Organizer.Tick();
        SyncDefaultLink();
        var currentLinks = ActiveLinks();
        if (currentLinks.Length == 0) return Task.FromResult(0);
        object info;
        object[] terminalList;
        object[] listed;
        string listedText;
        DateTime nowUtc = DateTime.UtcNow;
        lock (work) {
            if (returning != null) { var back = returning; returning = null; foreach (var kept in back) { try { Return(kept, prefs); } catch { } } nextScan = DateTime.UtcNow.AddSeconds(3); }
            bool enabled = Get(prefs, "RemoteEnabled") == "True";
            try { Ask(prefs); } catch (Exception) { }       // what was set to be asked at this time
            if (!enabled) foreach (var t in terminals.Values.Where(t => !t.Pty.Closed)) t.Pty.Dispose();
            var settings = Dirs(prefs);
            var own = OwnProjects();
            var allowed = new Dictionary<string, string>(settings);
            foreach (var p in own) { try { if (!allowed.ContainsKey(p.Key) && Directory.Exists(p.Value)) allowed[p.Key] = p.Value; } catch { } }
            // Reading the conversation lists takes a moment; it waits while someone is typing.
            if (DateTime.UtcNow >= nextScan && (DateTime.UtcNow - lastInput > TimeSpan.FromSeconds(2) || DateTime.UtcNow - lastScan > TimeSpan.FromSeconds(20))) {
                nextScan = DateTime.UtcNow.AddSeconds(5); lastScan = DateTime.UtcNow;
                if (!enabled) { sessions = new List<SessionInfo>(); candidates = new List<Dictionary<string, object>>(); sessionsDirty = true; }
                else if (Interlocked.CompareExchange(ref scanning, 1, 0) == 0) {
                    var runningTerms = terminals.Values.ToArray();
                    var reading = Task.Run(() => { try { Scan(allowed, runningTerms); Recover(); } catch { } finally { sessionsDirty = true; scanning = 0; Wake(); } });
                }
            }
            if (DateTime.UtcNow - toolsAt > TimeSpan.FromSeconds(30)) {
                tools = Tool.All.Select(t => t.Name).Where(t => FindTool(t) != null).ToArray();
                externalAgy = enabled ? Antigravity.ExternalStarts(FindTool("agy"), terminals.Values.Where(t => !t.Pty.Closed).Select(t => t.Pty.Id).ToArray(), agySessions.LivePids()) : new long[0];
                toolsAt = DateTime.UtcNow;
                if (enabled && tools.Contains("claude")) Quotas.AskClaude(FindTool("claude"));
                if (enabled && tools.Contains("codex")) Quotas.AskCodex(FindTool("codex"));      // (at most once in ten minutes)
                if (enabled && tools.Contains("agy")) Quotas.AskAgy(FindTool("agy"));            // (at most once in ten minutes)
            }
            // An ended terminal whose conversation runs in a terminal again or was handed over is no longer one of the recent ones.
            ended.RemoveAll(e => e.Session.Length > 0 && (Handovers.GoneOn(e.Tool, e.Session)
                || terminals.Values.Any(t => (!t.Pty.Closed && t.Tool == e.Tool && String.Equals(t.Session, e.Session, StringComparison.OrdinalIgnoreCase))
                    || (t.HandedFrom != null && t.HandedFrom.Tool == e.Tool && String.Equals(t.HandedFrom.Id, e.Session, StringComparison.OrdinalIgnoreCase)))));
            // a conversation says itself how it stands and which it is
            foreach (var t in terminals.Values.Where(t => t.Chat != null)) { t.Status = t.Chat.Status == "ended" ? "" : t.Chat.Status; if (t.Chat.Session.Length > 0) t.Session = t.Chat.Session; }
            // A conversation another AI continues in-place is linked once its tool assigns a session id.
            foreach (var t in terminals.Values.Where(t => t.HandedFrom != null && t.Session.Length > 0)) {
                string firstTitle = t.Title.Length > 0 ? t.Title : (Handovers.Title(t.HandedFrom.Tool, t.HandedFrom.Id).Length > 0 ? Handovers.Title(t.HandedFrom.Tool, t.HandedFrom.Id) : t.HandedFrom.Title);
                Handovers.Add(t.Tool, t.Session, t.HandedFrom.Tool, t.HandedFrom.Id, firstTitle);
                t.HandedFrom = null;
                sessions = sessions.Where(s => !Handovers.GoneOn(s.Tool, s.Id)).ToList();
                sessionsDirty = true;
                nextScan = DateTime.MinValue;
            }
            // a team's member says which conversation it is, kept with the task for its history (Teams.Learn)
            foreach (var t in terminals.Values.Concat(ended).Where(t => t.Session.Length > 0)) Teams.Learn(t.Id, t.Tool, t.Session);
            Delegations.Sweep();
            lock (gate) foreach (var t in terminals.Values) Seal(t);
            var advertisedRoutes = enabled && RoutesProvider != null ? RoutesProvider().Select(r => r.ToWire()).ToArray() : new object[0];
            info = new {
                instance = instance, environment_id = EnvironmentId, enabled = enabled, workspaces = allowed.Keys.ToArray(), tools = tools,
                agy_external = enabled ? externalAgy : new long[0], version = Version, newer = Newer, shell = "PowerShell",
                max_mode = ClampMode("", Get(prefs, "RemoteMaxMode")),
                features = new[] { "codex-fork", "codex-takeover", "terminal-exit", "files", "file-upload", "file-find", "attachments", "peek", "reach", "marks", "delete", "end", "outbox", "work", "chains", "brief", "schedule-days", "codex-desktop-control", "agy-desktop-control", "chat", "chat-agy", "chat-params", "teams", "teams-v2", "teams-models", "teams-grouped", "teams-roster", "teams-effort", "teams-flex-rounds", "teams-promote", "chat-model", "handover", "reopen", "organize", "routes", "git", "worktree", "tasks" }
                    .Concat(Desktop.Tools.Select(tool => tool + "-desktop-open"))
                    .Concat(Settled != null ? new[] { "projects" } : new string[0])
                    .Concat(UpdateRequested != null ? new[] { "update" } : new string[0])
                    .Concat(Computer != null ? new[] { "settings" } : new string[0]).ToArray(),
                projects = settings.Select(d => Project(d.Key, d.Value, true, true))
                    .Concat(own.Where(p => !settings.ContainsKey(p.Key)).Select(p => Project(p.Key, p.Value, false, allowed.ContainsKey(p.Key)))).ToArray(),
                candidates = candidates.ToArray(), quotas = enabled ? Quotas.Report() : new object[0],
                team_cards = enabled ? Teams.Cards(term => { LiveTerminal one; return terminals.TryGetValue(term, out one) && one.Status == "confirm"; }) : new Dictionary<string, object>[0],
                routes = advertisedRoutes
            };
            terminalList = terminals.Values.Concat(ended).Select(t => (object)new {
                id = t.Id, state = t.Pty.Closed ? "closed" : "running", cols = t.Pty.Cols, rows = t.Pty.Rows, session = t.Session, status = t.Status, exit_code = t.Pty.ExitCode,
                error = t.Pty.ExitCode.HasValue && t.Pty.ExitCode.Value != 0 ? "程序退出（代码 " + t.Pty.ExitCode.Value + "），请检查终端画面中的原因" : "",
                tool = t.Tool, dir = t.Dir, folder = t.Folder, created = Milliseconds(t.Started), title = t.Title, completion = CompletionOf(t.Tool, t.Session, t.Status),
                form = t.Chat != null ? "chat" : "", said = t.Chat != null ? t.Chat.Said : "", ask = t.Chat != null ? t.Chat.Asking : "",
                model = t.Model ?? "", effort = t.Effort ?? "", mode = t.Mode ?? "",
                branch = t.Branch ?? "", worktree = t.WorktreeId ?? "",
                team = t.Chat != null ? Teams.TeamOf(t.Id, t.Tool, t.Session) : "",
                parent = TaskOf(t.Id, "parent"), task = TaskOf(t.Id, "id")
            }).ToArray();
            bool needCheck = sessionsDirty || nowUtc - sessionsChecked > TimeSpan.FromSeconds(1.5) || nowUtc - sessionsSent > TimeSpan.FromSeconds(10);
            if (needCheck) {
                sessionsDirty = false; sessionsChecked = nowUtc;
                cachedListed = sessions.Select(Listed).Cast<object>().ToArray();
                cachedListedText = json.Serialize(cachedListed);
                sessionsSent = nowUtc;
            }
            listed = cachedListed;
            listedText = cachedListedText;
        }
        latestSnapshot = new LinkSnapshot {
            Prefs = prefs, Info = info, TerminalList = terminalList, Listed = listed, ListedText = listedText, At = nowUtc
        };
        foreach (var link in currentLinks) link.WakeReport();
        return Task.FromResult(0);
    }

    void StartLinkWorkers(RelayLink link) {
        Task.Run(() => ReportLinkLoop(link));
        Task.Run(() => ListenLink(link));
    }

    /// Runs an isolated heartbeat/report loop per relay link so a slow or broken remote relay never stalls local/LAN links.
    async Task ReportLinkLoop(RelayLink link) {
        var linkJson = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        while (link.Active) {
            bool ok = false;
            try {
                var snap = latestSnapshot;
                if (snap != null) ok = await ReportLinkAsync(link, snap, linkJson).ConfigureAwait(false);
            } catch { }
            if (!link.Active) break;
            int pause = !ok ? 1000 : soon ? 25 : DateTime.UtcNow - lastActivity < TimeSpan.FromSeconds(20) ? 250 : 900;
            bool woken = await link.WaitReportAsync(pause).ConfigureAwait(false);
            if (woken && DateTime.UtcNow - lastInput > TimeSpan.FromMilliseconds(150)) {
                try { await Task.Delay(12, link.Token).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    // A request the relay holds open until the phone asks for something. What it brings is carried out at once, so
    // a key press does not wait for the next report; the report then only takes the result back.
    async Task ListenLink(RelayLink link) {
        var reader = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        while (link.Active) {
            var started = DateTime.UtcNow; bool waiting = false, brought = false;
            try {
                HttpClient current = link.Client;
                if (current != null) {
                    var request = new HttpRequestMessage(HttpMethod.Post, link.Origin + "/api/terminal/agent/pull");
                    request.Headers.TryAddWithoutValidation("Origin", link.Origin);
                    request.Content = new StringContent(reader.Serialize(new Dictionary<string, object> { { "instance", instance }, { "wait", 12 } }), Encoding.UTF8, "application/json");
                    using (request) using (var response = await current.SendAsync(request, link.Token).ConfigureAwait(false)) {
                        if ((int)response.StatusCode == 401) {
                            link.ResetAuth();
                            link.WakeReport();
                        } else if (response.IsSuccessStatusCode) {
                            var result = reader.Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                            waiting = true;
                            var operations = List(result, "operations").OfType<Dictionary<string, object>>().ToArray();
                            if (operations.Length > 0) {
                                brought = true;
                                Dictionary<string, object> prefs = Preferences();
                                lock (work) foreach (var op in operations) {
                                    string opId = Get(op, "id");
                                    if (opId.Length > 0 && !opSource.ContainsKey(opId)) opSource[opId] = link.Key;
                                    Execute(op, prefs);
                                }
                            }
                            if (operations.Length > 0 || Get(result, "operations") == "True") Wake();
                        }
                    }
                }
            } catch {
                if (link.Active) link.RecordFailure();
            }
            if (!link.Active) break;
            if (!waiting || (!brought && DateTime.UtcNow - started < TimeSpan.FromMilliseconds(40))) {
                try { await Task.Delay(waiting ? 40 : 2000, link.Token).ConfigureAwait(false); } catch { break; }
            }
        }
    }
    public async Task Run() {
        Stats.WarmAsync(30);
        running = true;
        foreach (var link in ActiveLinks()) StartLinkWorkers(link);
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
        // --statusline <data folder> [--then <the owner's own command, base64>]: run by Claude Code as its status line (Quotas.cs)
        if (args.Length >= 2 && args[0] == RemoteCli.Quotas.Flag) {
            string then = "";
            if (args.Length == 4 && args[2] == "--then") { try { then = Encoding.UTF8.GetString(Convert.FromBase64String(args[3])); } catch (FormatException) { } }
            return RemoteCli.Quotas.StatusLine(args[1], then);
        }
        // --hook <tool> <pipe>: run by Claude Code or Codex as one of their hooks (Reach.cs)
        if (args.Length == 3 && args[0] == "--hook") return RemoteCli.Reach.Hook(args[1], args[2]);
        // --mcp <pipe> <token>: run by a chat tool as its local MCP server for asking another AI (Peers.cs)
        if (args.Length == 3 && args[0] == RemoteCli.Peers.Flag) return RemoteCli.Peers.Bridge(args[1], args[2]);
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
