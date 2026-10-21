using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// The conversations Claude Code and Codex have open on this computer, in whatever program runs them (a command
/// line, the desktop app, an editor), as far as the tools tell of them through their own hooks. Nothing is taken
/// from the program that has a conversation: it goes on as it is, the person at the computer uses it as before, and
/// the phone looks on. What the phone can do is what a hook can answer:
/// - a request for permission. Claude Code shows its question on the computer while the hook runs, and takes
///   whichever answer comes first: nothing of the hook is seen there. Codex asks its hooks before it shows the
///   question, so to be answered from the phone it has to be kept, showing a running hook meanwhile: that is done
///   only where its owner chose it, and only while nobody is at the computer;
/// - what to do next. A hook that runs as the tool stops can tell it to go on with something: a message from the
///   phone is passed to it there. One sent while the tool works waits for that moment; and while nobody is at the
///   computer, a tool that stops is kept waiting for the phone's message for a while, where its owner chose that.
/// As soon as somebody touches the keyboard or the mouse, whatever was kept waiting is let go, and the tool does
/// what it would have done without this.
/// A conversation in one of this program's own terminals is never kept: the phone is at it already, in that
/// terminal, where the tool shows its own question and takes what is typed.
public sealed class Reach : IDisposable {
    public const int Unattended = 60;       // seconds without a key or the mouse after which nobody is taken to be at the computer
    public const int Hold = 1800;           // the longest a request for permission is kept for the phone's answer, in seconds
    public const int Rest = 1800;           // how long a conversation that stopped while nobody was at the computer is said to wait for the phone
    /// The same, as it is now: REMOTECLI_HOLD says less, for a test.
    static int Holds { get { int said; return Int32.TryParse(Environment.GetEnvironmentVariable("REMOTECLI_HOLD"), out said) && said > 0 ? said : Hold; } }
    /// What a tool is told when nobody answered its request in that time and still nobody is at the computer. Left
    /// at its own question it could only be answered there, and its owner may be away for days: told this, it stops
    /// and says what it needs, and is told from the phone when to go on. Read by the model: changed only together
    /// with runs on the real tools.
    public const string Deferred = "The user is away from the computer and has not answered this request on their phone (Remote AI app) in time, so it is not allowed for now. "
        + "Do not try to get the same thing done another way. Stop here and say in one or two sentences what you need permission for: the user will read that on their phone and tell you when to go on.";
    /// How long a tool that stops can be kept waiting for the phone's message, in seconds, as the program's owner
    /// chooses: not at all unless they ask for it. A tool shows its hook as running all the while it is kept, and a
    /// conversation that has stopped is told from the phone all the same, by the tool's own way in (CodexQueue,
    /// ClaudeRelay): waiting only makes the words arrive at once.
    public static readonly int[] Waits = { 0, 60, 300, 900, 1800 };
    /// The choice as it is kept in the settings ("ReachWait"), read: one of Waits, none where nothing valid is kept.
    public static int Wait(object kept) { int seconds; return Int32.TryParse(Convert.ToString(kept), out seconds) && Array.IndexOf(Waits, seconds) >= 0 ? seconds : 0; }
    /// What the program's owner lets a hook keep its tool for, while nobody is at the computer. A tool that is kept
    /// shows a running hook and nothing else, so the computer's screen says in words what waits (KeptNow, Notice).
    public sealed class Kept {
        public int AtStop;              // a tool that stops waits so many seconds for the phone's message: not at all, unless chosen
        public bool Requests = true;    // a request for permission of a tool that asks its hooks first (Codex) waits for the phone's
                                        // answer: without that the phone cannot answer it at all. Its owner can switch it off.
        /// As the settings keep it: "ReachWait" and "ReachAsk".
        public static Kept From(IDictionary<string, object> settings) {
            object wait = null, ask = null;
            if (settings != null) { settings.TryGetValue("ReachWait", out wait); settings.TryGetValue("ReachAsk", out ask); }
            return new Kept { AtStop = Wait(wait), Requests = ask is bool ? (bool)ask : !String.Equals(Convert.ToString(ask), "false", StringComparison.OrdinalIgnoreCase) };
        }
    }
    /// What Claude Code shows on its screen while its Stop hook runs, in place of "running stop hook". It is read by
    /// the person at the computer, not by the model, and is seen for a moment at every stop.
    public const string Waiting = "Remote AI 查收手机消息";
    public const int Patience = 2000;       // what the tools are told a hook may take, in seconds: longer than any hold
    public static readonly string[] Events = { "SessionStart", "UserPromptSubmit", "PermissionRequest", "Stop", "SessionEnd" };
    /// Claude Code is heard before each step it takes as well: there it can be told to stop (Stop). Answered with
    /// `continue: false` it ends its turn and stays open, in whatever program has it (tried on Claude Code 2.1.283).
    /// Codex has no such answer; its desktop app is told by its own channel (CodexDesktopControl).
    public const string Step = "PreToolUse";
    // Claude emits a completion hook after a permission prompt has been answered in its own window.  Listening to
    // it closes a phone card immediately instead of waiting for the next user turn.  Codex has no equivalent hook in
    // its supported contract, so its native channel remains the source of truth.
    public static string[] EventsOf(string tool) { return tool == "claude" ? Events.Concat(new[] { Step, "PostToolUse", "PostToolUseFailure", "StopFailure" }).ToArray() : Events; }
    /// What Claude Code shows where it was stopped. Read by the person at the computer, not by the model.
    public const string StoppedFromPhone = "已从手机停止（Remote AI）";
    const string Uuid = @"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z";

    /// What is known of one conversation. Status: "busy" (the tool works), "confirm" (it asks for permission),
    /// "waiting" (it has stopped while nobody was at the computer: it is the phone's turn, until somebody comes back
    /// or `Hold` has passed), "idle" (it has stopped).
    public sealed class Open {
        public string Tool = "", Session = "", Status = "idle", AskId = "", AskText = "", Last = "", Completion = "";
        public object[] AskOptions = new object[0];
        public bool Answerable;             // the request can be answered from the phone (else only at the computer)
        public bool Held;                   // while it waits, its hook is kept: a message is taken at once. Else it has
                                            // stopped for good, and is told by its tool's own way in, where it has one
        public bool Beside;                 // while it asks, its own question is on the computer's screen as well
        public bool Steps;                  // it is heard before each of its steps: it runs the hook for that (one opened
                                            // before the hook was there does not, until it is opened anew)
        internal bool StopAsked, NativeAsked;
        // The step it is at, as its hook told it: when it began, by which of the tool's own tools, and whether it was
        // sent off to run beside the conversation (that is no command to end when the conversation is told to stop).
        internal DateTime StepAt; internal string StepTool = ""; internal bool StepAside;
        /// It can be told to stop what it is at: it ends its turn before its next step, and stays open.
        public bool Stoppable { get { return Steps && Status == "busy"; } }
        /// Its tool is kept by a hook and shows nothing but that: a request that waits for the phone in place of the
        /// tool's own question, or a stop that waits for a message. Whoever looks at the computer is to be told.
        public bool Kept { get { return (Status == "confirm" && Answerable && !Beside) || (Status == "waiting" && Held); } }
        /// A message can be left for it here: it takes it now, or when it has done what it is doing.
        public bool Sayable { get { return Status == "busy" || Status == "confirm" || (Status == "waiting" && Held); } }
        internal string Transcript = "", AskKey = ""; internal bool? Verdict; internal string VerdictScope = "once"; internal Dictionary<string, object> Always = null; internal DateTime Seen, AskedAt, Stopped; internal int Turn; internal long AskStamp;
        internal Open Copy() { return (Open)MemberwiseClone(); }
    }

    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint size, tick; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput info);
    /// Seconds since a key was pressed or the mouse moved. REMOTECLI_UNATTENDED says it for a test.
    public static int IdleSeconds() {
        int said; if (Int32.TryParse(Environment.GetEnvironmentVariable("REMOTECLI_UNATTENDED"), out said)) return said;
        var info = new LastInput { size = 8 };
        return GetLastInputInfo(ref info) ? (int)(unchecked((uint)Environment.TickCount - info.tick) / 1000) : 0;
    }
    /// The pipe the hooks of one installation talk to: one per data folder, the same every time it starts.
    public static string PipeName(string dataFolder) {
        using (var sha = SHA256.Create())
            return "RemoteCli.reach." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(dataFolder).ToLowerInvariant())), 0, 6).Replace("-", "").ToLowerInvariant();
    }

    readonly object gate = new object();
    readonly Dictionary<string, Open> open = new Dictionary<string, Open>();
    readonly HashSet<string> heard = new HashSet<string>();
    /// Whether a tool has told of anything since this program started: its hooks do reach it.
    public bool Heard(string tool) { lock (gate) return heard.Contains(tool); }
    readonly string pipe; readonly Func<int> idle; readonly Func<Kept> kept; readonly Func<int, string, string, bool> own;
    /// What the phone said and the conversations have not taken yet: one item for each message (Outbox.cs).
    public readonly Outbox Outbox;
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint process);
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
    volatile bool closed;
    /// Something about a conversation changed: the phone is to be told at once.
    public event Action Changed;
    string receipt = "", receiptDetail = ""; DateTime receiptAt;
    public string[] RecentAnswer() { lock (gate) return DateTime.UtcNow - receiptAt < TimeSpan.FromSeconds(8) ? new[] { receipt, receiptDetail } : null; }
    /// A conversation has done what it was at and stopped by itself, with nothing waiting for it (its tool and its
    /// number): what is to follow on that can be started (Chains). Not one that was told to stop.
    public event Action<string, string> Finished;
    public string Pipe { get { return pipe; } }

    /// `keeps`: what a tool is kept for now, as its owner has it; asked at every request and every stop.
    /// `ownTerminal`: whether a hook (its process, its tool, its conversation) runs in one of this program's own terminals.
    /// `outbox`: where what the phone said is kept until it is taken; in memory only where none is given.
    public Reach(string pipeName, Func<int> idleSeconds = null, Func<Kept> keeps = null, Func<int, string, string, bool> ownTerminal = null, Outbox outbox = null) {
        pipe = pipeName; idle = idleSeconds ?? IdleSeconds; kept = keeps ?? (() => new Kept()); own = ownTerminal; Outbox = outbox ?? new Outbox();
        // (a hook that is kept waiting for a message looks for one when something is added)
        Outbox.Changed += () => { lock (gate) Monitor.PulseAll(gate); };
        new Thread(Listen) { IsBackground = true, Name = "reach" }.Start();
        watch = new System.Threading.Timer(Rested, null, 1000, 1000);
    }
    readonly System.Threading.Timer watch;
    // A conversation whose hook is not kept has nothing that notices somebody coming back to the computer, or its
    // time passing: this looks, once a second, and then it has simply stopped.
    void Rested(object unused) {
        bool changed = false; int away = -1;
        lock (gate) {
            if (closed) return;
            foreach (Open o in open.Values) {
                if (o.Status != "waiting" || o.Held) continue;
                if (away < 0) away = idle();
                if (away < Unattended || DateTime.UtcNow - o.Stopped > TimeSpan.FromSeconds(Rest)) { o.Status = "idle"; changed = true; }
            }
        }
        if (changed) Tell();
    }
    public void Dispose() {
        closed = true; watch.Dispose();
        lock (gate) Monitor.PulseAll(gate);
        try { using (var poke = new NamedPipeClientStream(".", pipe, PipeDirection.InOut)) poke.Connect(100); } catch { }
    }
    static string Key(string tool, string session) { return tool + " " + session.ToLowerInvariant(); }
    void Tell() { var told = Changed; if (told != null) { try { told(); } catch { } } }

    /// What is known of a conversation, or null: its tool has not told of it, or not for a day.
    public Open Of(string tool, string session) {
        lock (gate) {
            Open o;
            if (!open.TryGetValue(Key(tool, session), out o) || DateTime.UtcNow - o.Seen > TimeSpan.FromHours(24)) return null;
            // A question left to the computer is answered there, and no hook says so: the tool going on writing its conversation does.
            if (o.Status == "confirm" && !o.Answerable && DateTime.UtcNow - o.AskedAt > TimeSpan.FromSeconds(1.5) && Stamp(o.Transcript) != o.AskStamp) { o.Status = "busy"; o.AskId = o.AskText = ""; }
            return o.Copy();
        }
    }
    /// A fresh native snapshot can retire a question that was released to the desktop. A still-held hook must never
    /// be inferred answered from an application's cached state.
    public void ObserveDesktop(string session, bool ready, bool takes, DateTime at) {
        bool changed = false;
        lock (gate) {
            Open o;
            if (!open.TryGetValue(Key("codex", session), out o) || o.Status != "confirm" || o.Answerable || at < o.AskedAt.AddSeconds(2)) return;
            if (!ready && !takes) o.NativeAsked = true;
            else if (ready || o.NativeAsked) { o.Status = ready ? "idle" : "busy"; o.AskId = o.AskText = ""; o.AskOptions = new object[0]; Monitor.PulseAll(gate); changed = true; }
        }
        if (changed) Tell();
    }
    /// The tools of Claude Code's that run a command in a shell: what such a step runs can be ended from outside.
    static readonly string[] ShellTools = { "Bash", "PowerShell" };
    /// Asks a conversation at work to stop: it is told so before its next step. Says in words for the person why not.
    /// What the phone had said and it has not taken yet is held: its owner says afterwards whether it is still to go.
    /// The command its step is running is ended, as Esc would end it at the computer, so that the next step, where
    /// it stops, comes at once and not when the command has run out (Running.cs). Returns how many processes that
    /// were; `runner` says which process has the conversation (Claude Code's own note of it, where none is given).
    public int Stop(string tool, string session, Func<string, int> runner = null) {
        DateTime since; bool command;
        lock (gate) {
            Open o;
            if (!open.TryGetValue(Key(tool, session), out o) || !o.Stoppable) throw new InvalidOperationException("这段对话现在没有在执行，或它是在可以停止之前打开的（重新打开后才行）");
            o.StopAsked = true;
            since = o.StepAt; command = Array.IndexOf(ShellTools, o.StepTool) >= 0 && !o.StepAside;
        }
        Outbox.Pause(tool, session);
        if (!command || tool != "claude") return 0;
        try { return Running.End((runner ?? ClaudeSessions.ProcessOf)(session), since); } catch (Exception) { return 0; }
    }
    /// The conversations whose tool a hook keeps right now, showing nothing but a running hook (Open.Kept).
    public Open[] KeptNow() { lock (gate) return open.Values.Where(o => o.Kept).Select(o => o.Copy()).ToArray(); }
    /// A message from the phone for a conversation, under the number of the operation that carries it. Says in
    /// words for the person why it cannot be passed on. Returns whether the tool takes it now (else when it has done
    /// what it is doing). It waits in the Outbox until the tool's hooks hand it over.
    public bool Say(string tool, string session, string text, string id = null) {
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Length > 8000) throw new ArgumentException("消息需为 1 至 8000 字");
        bool now;
        lock (gate) {
            Open o;
            if (!open.TryGetValue(Key(tool, session), out o)) throw new InvalidOperationException("电脑上的这段对话现在联系不上，请刷新后重试");
            now = o.Status == "waiting" && o.Held;
            if (!now && o.Status != "busy" && o.Status != "confirm") throw new InvalidOperationException("这段对话已经在电脑上停下，等着在电脑上输入。它下次工作时发的消息，会在它做完后收到");
        }
        Outbox.Add(id ?? Guid.NewGuid().ToString("N"), tool, session, text);
        return now;
    }
    /// Leaves a message for a conversation to take when its hooks next run, whatever it is doing now, also one that
    /// has told of nothing yet. (A conversation that sits idle is woken for it by other means: ClaudeRelay.)
    public void Leave(string tool, string session, string text, string id = null) { Outbox.Add(id ?? Guid.NewGuid().ToString("N"), tool, session, text); }
    /// Whether a message that was left has been taken by the conversation's hooks (or held, or taken back): waits
    /// for that for up to `seconds`. False: it still waits; a courier must reserve it before handing it over.
    public bool Taken(string id, int seconds) { return Outbox.Settled(id, seconds); }
    static string Joined(Outbox.Item[] items) { return String.Join("\n\n", items.Select(item => item.Text)); }
    /// What stands in front of a message that a hook hands to its tool as its user's words from the phone.
    // (How a tool takes a message hangs on its wording: these words were tried with the real tools, and a change
    // of them is tried again before it goes out.)
    public const string FromPhone = "Message from the user, sent from their phone through the Remote AI app";
    /// What stands in front of the phone's words where they are handed to a tool between two of its steps. Read by
    /// the model: changed only together with runs on the real tool.
    public const string BetweenSteps = FromPhone + " while you are working on their earlier request. It is their newest message in this conversation: take it into account from here on.\n\n";
    /// The names by which what this program wrote into a conversation is known as its own: the one it has, and the
    /// one it had (conversations written then are still read).
    public static readonly string[] Names = { "Remote AI", "Remote CLI" };
    /// The phone's answer to a request for permission.
    public void Answer(string tool, string session, string ask, bool allow, string scope = "once") {
        lock (gate) {
            Open o;
            if (!open.TryGetValue(Key(tool, session), out o) || o.Status != "confirm" || o.AskId != ask || ask.Length == 0) throw new InvalidOperationException("这个确认请求已经处理过了");
            if (!o.Answerable) throw new InvalidOperationException("这个确认请求正在电脑上等待，只能在电脑上回答");
            if (o.Verdict.HasValue) throw new InvalidOperationException("这个确认请求已经处理过了");
            if (scope != "once" && scope != "always") throw new ArgumentException("授权范围无效");
            if (scope == "always" && (!allow || o.Always == null || o.Always.Count == 0)) throw new InvalidOperationException("这个工具没有提供可保存的长期授权");
            o.VerdictScope = scope; o.Verdict = allow; Monitor.PulseAll(gate);
        }
    }

    // ---- the hooks' side: one connection for each event a tool reports, held open for as long as the tool waits
    void Listen() {
        while (!closed) {
            NamedPipeServerStream stream = null;
            try {
                stream = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0);
                stream.WaitForConnection();
                if (closed) { stream.Dispose(); return; }
                var taken = stream; stream = null;
                Task.Run(() => Serve(taken));
            } catch (IOException) { if (stream != null) stream.Dispose(); Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { if (stream != null) stream.Dispose(); Thread.Sleep(2000); }      // another program of this name holds the pipe
            catch (ObjectDisposedException) { return; }
        }
    }
    static byte[] ReadFrame(Stream stream) {
        var head = new byte[4]; int got = 0, n;
        while (got < 4 && (n = stream.Read(head, got, 4 - got)) > 0) got += n;
        if (got < 4) return null;
        int size = BitConverter.ToInt32(head, 0);
        if (size < 0 || size > 64 * 1024 * 1024) return null;
        var body = new byte[size]; got = 0;
        while (got < size && (n = stream.Read(body, got, size - got)) > 0) got += n;
        return got == size ? body : null;
    }
    static void WriteFrame(Stream stream, byte[] body) {
        stream.Write(BitConverter.GetBytes(body.Length), 0, 4); stream.Write(body, 0, body.Length); stream.Flush();
    }
    void Serve(NamedPipeServerStream stream) {
        using (stream) {
            try {
                byte[] asked = ReadFrame(stream);
                if (asked == null) return;
                var said = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(asked));
                object tool, hooked;
                if (said == null || !said.TryGetValue("tool", out tool) || !said.TryGetValue("event", out hooked) || !(hooked is Dictionary<string, object>)) return;
                // The hook says nothing more. When its end of the pipe closes, the tool has stopped waiting for it.
                bool gone = false;
                var rest = new byte[1];
                try { stream.BeginRead(rest, 0, 1, done => { try { stream.EndRead(done); } catch { } gone = true; lock (gate) Monitor.PulseAll(gate); }, null); } catch { gone = true; }
                uint process = 0;       // the hook's own process, as the system tells it: from there up, what started it
                try { if (!GetNamedPipeClientProcessId(stream.SafePipeHandle.DangerousGetHandle(), out process)) process = 0; } catch (ObjectDisposedException) { }
                string[] answer = Handle(Convert.ToString(tool), (Dictionary<string, object>)hooked, () => gone || closed, (int)process);
                // `again`: this program is closing, and what it was asked has not been answered by anybody
                WriteFrame(stream, Encoding.UTF8.GetBytes(json.Serialize(new Dictionary<string, object> { { "out", answer[0] }, { "code", Int32.Parse(answer[1]) }, { "again", closed && answer[0].Length == 0 } })));
                bool delivered = true; try { stream.WaitForPipeDrain(); } catch (IOException) { delivered = false; }
                if (delivered && answer[0].Contains("\"hookEventName\":\"PermissionRequest\"") && answer[0].Contains("\"decision\"") && !answer[0].Contains(Deferred)) {
                    lock (gate) { receipt = Convert.ToString(tool) == "claude" ? "Claude Code" : "Codex"; receipt += answer[0].Contains("\"behavior\":\"allow\"") ? "：手机已允许" : "：手机已拒绝"; receiptDetail = answer[0].Contains("updatedPermissions") ? "已提交长期规则，工具将按自身权限设置处理" : "本次操作的决定已交给工具"; receiptAt = DateTime.UtcNow; }
                    Tell();
                }
            } catch (IOException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }      // a pipe that closed, or words that are not an event
        }
    }
    static string Text(Dictionary<string, object> from, string name) { object value; return from != null && from.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : ""; }
    static string Short(string text, int most) {
        text = Regex.Replace(text ?? "", @"[\p{C}-[\n]]+", " ").Trim();
        return text.Length <= most ? text : text.Substring(0, Char.IsHighSurrogate(text[most - 2]) ? most - 2 : most - 1) + "…";
    }
    /// What a tool asks permission for, in the tool's own words: what it says it wants, and the command or the file.
    public static string Describe(Dictionary<string, object> hooked) {
        string name = Text(hooked, "tool_name"); object input; hooked.TryGetValue("tool_input", out input);
        var given = input as Dictionary<string, object>;
        string what = Text(given, "command");
        if (what.Length == 0) what = Text(given, "file_path");
        if (what.Length == 0) what = Text(given, "url");
        if (what.Length == 0 && given != null) what = String.Join("，", given.Keys.Take(6));
        string why = Text(given, "description");
        return Short((why.Length > 0 ? why + "\n" : "") + (name.Length > 0 ? name + "：" : "") + what, 600);
    }
    static long Stamp(string path) { try { return path.Length > 0 ? File.GetLastWriteTimeUtc(path).Ticks : 0; } catch { return 0; } }
    static readonly string[] Nothing = { "", "0" };

    /// Whether no person sits at this run: a script's `claude -p` (Claude Code notes how a session was started; a
    /// script's is "sdk…"), a `codex exec`, or a helper thread that a Codex conversation started for itself (the
    /// first line of its record says). Such a run is answered at once and with nothing: keeping it waiting for a
    /// phone would hold up the script, and it is no conversation to show on a phone.
    public static bool Scripted(string tool, Dictionary<string, object> hooked) {
        try {
            if (tool == "claude") return ClaudeSessions.Entry(Text(hooked, "session_id")).StartsWith("sdk", StringComparison.OrdinalIgnoreCase);
            string path = Text(hooked, "transcript_path");
            if (path.Length == 0 || !File.Exists(path)) return false;
            string first;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                var head = new char[16384]; int read = reader.Read(head, 0, head.Length);
                first = new string(head, 0, Math.Max(0, read)); int end = first.IndexOf('\n'); if (end >= 0) first = first.Substring(0, end);
            }
            return Regex.IsMatch(first, @"originator.\s*:\s*.codex_exec\b|\bsource.\s*:\s*.exec\b|\bsource.\s*:\s*\{|parent_thread_id.\s*:\s*.\w");
        } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } catch (ArgumentException) { return false; }
    }

    /// One event of a tool. Returns what the hook writes for the tool to read, and the code it ends with.
    string[] Handle(string tool, Dictionary<string, object> hooked, Func<bool> gone, int process) {
        string name = Text(hooked, "hook_event_name"), session = Text(hooked, "session_id");
        if ((tool != "claude" && tool != "codex") || !Regex.IsMatch(session, Uuid)) return Nothing;
        if (name == Step) {
            // Before a step: nothing is said, unless the phone asked for a stop. This runs at every step of every
            // conversation, so it does no more than that; one that is not known yet is told of by its other events.
            bool stop = false, news = false;
            lock (gate) {
                Open stepping;
                if (!open.TryGetValue(Key(tool, session), out stepping)) return Nothing;
                stepping.Seen = stepping.StepAt = DateTime.UtcNow; stepping.StepTool = Text(hooked, "tool_name");
                object given; hooked.TryGetValue("tool_input", out given);
                stepping.StepAside = Text(given as Dictionary<string, object>, "run_in_background") == "True";
                news = !stepping.Steps || stepping.Status == "idle" || stepping.Status == "waiting";
                stepping.Steps = true;
                stepping.Completion = "";
                if (stepping.Status == "idle" || stepping.Status == "waiting") { stepping.Status = "busy"; stepping.Held = false; }
                stop = stepping.StopAsked; stepping.StopAsked = false;
                // Told to stop, it ends its turn here and runs no hook for that: how it stands is said now. Like one
                // that stopped by itself, it is the phone's turn while nobody is at the computer.
                if (stop) { stepping.Stopped = DateTime.UtcNow; stepping.Status = idle() >= Unattended ? "waiting" : "idle"; stepping.Held = false; }
            }
            if (news || stop) Tell();
            if (stop) return new[] { json.Serialize(new Dictionary<string, object> { { "continue", false }, { "stopReason", StoppedFromPhone } }), "0" };
            // What the phone said while it works is handed to it here, between two of its steps: it need not wait
            // until the whole turn is over. (Only a Claude Code that is known to take added context from a hook;
            // an earlier one is given the words when it stops, as before.)
            Version runs;
            if (!Outbox.Waiting(tool, session) || !System.Version.TryParse(ClaudeSessions.Version(session), out runs) || runs < ContextAtStop) return Nothing;
            var said = Outbox.Hand(tool, session);
            if (said.Length == 0) return Nothing;
            return new[] { json.Serialize(new Dictionary<string, object> { { "hookSpecificOutput", new Dictionary<string, object> { { "hookEventName", Step }, { "additionalContext", BetweenSteps + Joined(said) } } } }), "0" };
        }
        if (Scripted(tool, hooked)) return Nothing;
        Open o; bool first;
        lock (gate) {
            first = heard.Add(tool);
            string key = Key(tool, session);
            if (name == "SessionEnd") { Open ending; if (open.TryGetValue(key, out ending)) { ending.AskId = ""; ending.Answerable = false; open.Remove(key); Monitor.PulseAll(gate); } o = null; }
            else {
                if (!open.TryGetValue(key, out o)) open[key] = o = new Open { Tool = tool, Session = session };
                o.Seen = DateTime.UtcNow; o.Turn++;
                string transcript = Text(hooked, "transcript_path"); if (transcript.Length > 0) o.Transcript = transcript;
                if (open.Count > 200) foreach (string old in open.Where(p => DateTime.UtcNow - p.Value.Seen > TimeSpan.FromHours(24)).Select(p => p.Key).ToArray()) open.Remove(old);
            }
        }
        if (o == null || first) Tell();
        if (o == null) return Nothing;
        if (name == "SessionStart") { lock (gate) { if (o.Status != "busy") { o.Status = "idle"; o.Held = false; } } Tell(); return Nothing; }
        if (name == "UserPromptSubmit") { lock (gate) { o.Status = "busy"; o.AskId = o.AskText = o.Last = o.Completion = ""; o.AskOptions = new object[0]; o.Always = null; o.Answerable = o.Held = o.StopAsked = false; Monitor.PulseAll(gate); } Tell(); return Nothing; }
        if (tool == "claude" && name == "StopFailure") {
            Outbox.Pause(tool, session);
            lock (gate) {
                o.Status = "idle"; o.Held = o.Answerable = o.StopAsked = false;
                o.Completion = "";
                o.AskId = o.AskText = ""; o.AskOptions = new object[0]; o.Always = null; o.Verdict = null;
                o.Last = "Claude Code 本次执行失败：" + Short(Text(hooked, "error_details").Length > 0 ? Text(hooked, "error_details") : Text(hooked, "error"), 300);
                Monitor.PulseAll(gate);
            }
            Tell(); return Nothing;
        }
        if (name == "PostToolUse" || name == "PostToolUseFailure") { lock (gate) { if (o.Status == "confirm" && o.AskKey == RequestKey(hooked)) { o.Status = "busy"; o.AskId = o.AskText = ""; o.AskOptions = new object[0]; o.Always = null; o.Answerable = false; o.Verdict = null; Monitor.PulseAll(gate); } } Tell(); return Nothing; }
        if (name != "PermissionRequest" && name != "Stop") return Nothing;
        bool mine = false;
        if (own != null && process > 0) try { mine = own(process, tool, session); } catch (Exception) { }
        if (name == "PermissionRequest") return Asked(o, tool, hooked, gone, mine);
        return Stopped(o, tool, hooked, gone, mine);
    }
    // The tool asks for permission. The hook is kept waiting until the phone answers; or the tool lets the hook go, or
    // tells of something else (the question was answered at the computer); or, for a tool that does not show its
    // question while a hook runs, somebody is at the computer; or it has been long enough: then, with still nobody
    // at the computer, the tool is told to stop and say what it needs (Deferred). That the conversation is written to
    // says nothing here: Claude Code writes to it while it asks.
    // `mine`: it runs in one of this program's own terminals. Its question is the terminal's to answer.
    string[] Asked(Open o, string tool, Dictionary<string, object> hooked, Func<bool> gone, bool mine) {
        bool beside = tool == "claude" && !mine;         // its question is on the computer's screen while the hook runs
        string id; var began = DateTime.UtcNow; bool? verdict = null; bool deferred = false; Dictionary<string, object> savedPermission = null;
        lock (gate) {
            id = o.AskId = Guid.NewGuid().ToString("N").Substring(0, 12); o.AskText = Describe(hooked); o.Verdict = null; o.VerdictScope = "once";
            o.AskKey = RequestKey(hooked); o.NativeAsked = false; o.Always = tool == "claude" ? AlwaysSuggestion(hooked) : null;
            o.AskOptions = o.Always == null ? new object[] { new { id = "once", label = "允许一次", detail = "只允许这一次操作" } } : new object[] { new { id = "once", label = "允许一次", detail = "只允许这一次操作" }, new { id = "always", label = "始终允许此规则", detail = PermissionDetail(o.Always) } };
            // beside its own question the hook is not seen; a tool that asks its hooks first is kept only where its owner chose that
            o.Status = "confirm"; o.Answerable = !mine && (beside || (kept().Requests && idle() >= Unattended)); o.Beside = beside; o.AskedAt = began; o.AskStamp = Stamp(o.Transcript);
        }
        Tell();
        lock (gate) {
            for (;;) {
                if (o.AskId != id) return Nothing;                                  // the tool has gone on to something else
                if (gone()) { o.AskId = o.AskText = ""; o.Answerable = false; o.Status = "busy"; break; }
                if (o.Verdict.HasValue) { verdict = o.Verdict; if (o.VerdictScope == "always") savedPermission = o.Always; break; }
                if (o.Answerable && DateTime.UtcNow - began > TimeSpan.FromSeconds(Holds)) {
                    // its time is over. Nobody at the computer either: it is not left at a question nobody can answer
                    if (idle() >= Unattended) { verdict = false; deferred = true; break; }
                    o.Answerable = false; if (!beside) break;
                }
                if (o.Answerable && !beside && idle() < Unattended) { o.Answerable = false; break; }
                if (!o.Answerable && !beside) break;
                Monitor.Wait(gate, 300);
            }
            if (verdict.HasValue) { o.AskId = o.AskText = ""; o.AskOptions = new object[0]; o.Answerable = false; o.Status = "busy"; }
        }
        Tell();
        if (!verdict.HasValue) return Nothing;
        var decision = new Dictionary<string, object> { { "behavior", verdict.Value ? "allow" : "deny" } };
        if (verdict.Value && savedPermission != null) decision["updatedPermissions"] = new object[] { savedPermission };
        if (!verdict.Value) decision["message"] = deferred ? Deferred : "The user declined this from their phone (Remote AI app).";
        return new[] { json.Serialize(new Dictionary<string, object> { { "hookSpecificOutput", new Dictionary<string, object> { { "hookEventName", "PermissionRequest" }, { "decision", decision } } } }), "0" };
    }
    static Dictionary<string, object> AlwaysSuggestion(Dictionary<string, object> hooked) {
        object raw; if (!hooked.TryGetValue("permission_suggestions", out raw)) return null;
        var list = raw as IEnumerable; if (list == null) return null;
        foreach (var item in list) {
            var map = item as Dictionary<string, object>;
            if (map == null || Text(map, "type") != "addRules" || Text(map, "behavior") != "allow" || Text(map, "destination") != "localSettings" || !map.ContainsKey("rules") || !(map["rules"] is IEnumerable) || map["rules"] is string) continue;
            var entries = ((IEnumerable)map["rules"]).Cast<object>().ToArray();
            if (entries.Length == 0 || entries.Length > 4 || entries.Any(rule => !(rule is Dictionary<string, object>) || Text((Dictionary<string, object>)rule, "toolName").Length == 0)) continue;
            // The complete scope has to fit on the phone before it can be approved.
            if (PermissionDetail(map).Length > 600) continue;
            return map;
        }
        return null;
    }
    static string PermissionDetail(Dictionary<string, object> suggestion) {
        var rules = ((IEnumerable)suggestion["rules"]).Cast<object>().OfType<Dictionary<string, object>>().Select(rule => Text(rule, "toolName") + "(" + (Text(rule, "ruleContent").Length == 0 ? "全部操作" : Text(rule, "ruleContent")) + ")");
        return "保存到此项目的本机设置：" + String.Join("；", rules);
    }
    static object Ordered(object value) {
        var map = value as Dictionary<string, object>;
        if (map != null) return map.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => Ordered(pair.Value));
        if (value is IEnumerable && !(value is string)) return ((IEnumerable)value).Cast<object>().Select(Ordered).ToArray();
        return value;
    }
    static string RequestKey(Dictionary<string, object> hooked) { object input; hooked.TryGetValue("tool_input", out input); return Text(hooked, "tool_name") + new JavaScriptSerializer().Serialize(Ordered(input)); }
    // The tool has stopped. A message that waited for this goes to it now. Else, while nobody is at the computer, it
    // is kept waiting for one.
    /// The first Claude Code this was tried with. From it on, a Stop hook may hand Claude Code added context and the
    /// conversation goes on with it (docs: "non-error feedback that continues the conversation"); an earlier one
    /// might take no notice of that, and the words would be lost.
    public static readonly Version ContextAtStop = new Version(2, 1, 283);
    /// What a Stop hook answers to hand its tool the phone's words. Claude Code of a version that takes it is given
    /// them as added context: its screen then says "Stop hook feedback" and no error. Else the stop is blocked with
    /// the words as the reason, which every tool takes (Claude Code shows that as "Stop hook error").
    public static string AtStop(string tool, string version, string message) {
        Version runs; var json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
        if (tool == "claude" && System.Version.TryParse(version ?? "", out runs) && runs >= ContextAtStop)
            return json.Serialize(new Dictionary<string, object> { { "hookSpecificOutput", new Dictionary<string, object> { { "hookEventName", "Stop" },
                { "additionalContext", FromPhone + ". It is the next message of the user of this conversation:\n\n" + message } } } });
        return json.Serialize(new Dictionary<string, object> { { "decision", "block" }, { "reason", FromPhone + " while they are away from the computer. Treat it as their next instruction:\n\n" + message } });
    }
    string[] Stopped(Open o, string tool, Dictionary<string, object> hooked, Func<bool> gone, bool mine) {
        string message = null; var began = DateTime.UtcNow; int turn, wait = kept().AtStop; bool held = false;
        lock (gate) {
            bool interrupted = o.StopAsked;
            o.Completion = "";
            o.Last = Short(Text(hooked, "last_assistant_message"), 300); o.AskId = o.AskText = ""; o.Answerable = o.Held = o.StopAsked = false; turn = o.Turn;
            var waited = Outbox.Hand(tool, o.Session);
            if (waited.Length > 0) { message = Joined(waited); Monitor.PulseAll(gate); }
            else if (!mine && idle() >= Unattended) {
                // nobody is at the computer: it is the phone's turn. The hook is kept only where its owner chose a
                // time for that; else the tool stops as it would without this program.
                o.Status = "waiting"; o.Stopped = began; o.Held = held = wait > 0;
                Monitor.PulseAll(gate);
            } else o.Status = "idle";
            if (message == null && !interrupted) o.Completion = Guid.NewGuid().ToString("N");
        }
        if (message == null) {
            Tell();
            var finished = Finished;
            if (finished != null) { try { finished(tool, o.Session); } catch (Exception) { } }
            if (held) {
                bool let = false;
                lock (gate) {
                    while (o.Status == "waiting" && o.Held && o.Turn == turn) {
                        var said = Outbox.Hand(tool, o.Session);
                        if (said.Length > 0) { message = Joined(said); break; }
                        if (gone() || idle() < Unattended) { o.Status = "idle"; o.Held = false; break; }
                        // its time is over: the tool stops, and it is the phone's turn still
                        if (DateTime.UtcNow - began > TimeSpan.FromSeconds(wait)) { o.Held = false; let = true; break; }
                        Monitor.Wait(gate, 300);
                    }
                }
                if (let) Tell();
            }
        }
        if (message != null) lock (gate) { if (o.Turn == turn) { o.Status = "busy"; o.Last = o.Completion = ""; o.Held = false; } }
        Tell();
        if (message == null) return Nothing;
        return new[] { AtStop(tool, tool == "claude" ? ClaudeSessions.Version(Text(hooked, "session_id")) : "", message), "0" };
    }

    /// How long a hook with a request waits for the program to listen again, once it has stopped listening, in seconds.
    public const int Return = 90;
    /// The hook itself, as the tools run it: hands the event it was given to the program that listens on the pipe
    /// and writes back what that answers. When nothing listens, it has nothing to say and the tool goes on at once.
    /// A request for permission that the program had taken is another matter: the program may be starting again (an
    /// update, asked for from the phone while its owner is away), and the request would fall to the tool's own
    /// question, where the phone cannot answer it. The hook waits a while for the program and hands it over anew.
    public static int Hook(string tool, string pipeName) {
        try {
            byte[] hooked;
            using (var input = Console.OpenStandardInput()) using (var all = new MemoryStream()) { input.CopyTo(all); hooked = all.ToArray(); }
            string text = Encoding.UTF8.GetString(hooked).Trim().TrimStart('﻿');
            if (!text.StartsWith("{", StringComparison.Ordinal)) return 0;
            bool request = Regex.IsMatch(text, "\"hook_event_name\"\\s*:\\s*\"PermissionRequest\"");
            bool step = Regex.IsMatch(text, "\"hook_event_name\"\\s*:\\s*\"" + Step + "\"");       // run at every step: it waits for nobody
            DateTime? lost = null;
            for (;;) {
                bool taken = false;
                try {
                    using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut)) {
                        client.Connect(lost.HasValue ? 1000 : step ? 60 : 400);
                        WriteFrame(client, Encoding.UTF8.GetBytes("{\"tool\":" + new JavaScriptSerializer().Serialize(tool) + ",\"event\":" + text + "}"));
                        taken = true;
                        byte[] answer = ReadFrame(client);
                        if (answer != null) {
                            var said = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(answer));
                            object again;
                            if (!(said.TryGetValue("again", out again) && again is bool && (bool)again)) {
                                byte[] written = Encoding.UTF8.GetBytes(Text(said, "out"));
                                if (written.Length > 0) using (var output = Console.OpenStandardOutput()) { output.Write(written, 0, written.Length); output.Flush(); }
                                int code; return Int32.TryParse(Text(said, "code"), out code) ? code : 0;
                            }
                        }
                    }
                } catch (TimeoutException) { } catch (IOException) { }
                // nothing listened from the first: the program is not running. It listened and is gone: wait for it.
                if (!request || (!taken && !lost.HasValue)) return 0;
                if (!lost.HasValue) lost = DateTime.UtcNow;
                if (DateTime.UtcNow - lost.Value > TimeSpan.FromSeconds(Return)) return 0;
                Thread.Sleep(1000);
            }
        } catch (UnauthorizedAccessException) { return 0; } catch (ArgumentException) { return 0; } catch (InvalidOperationException) { return 0; }
    }
}

/// The hooks in the tools' own settings: Claude Code's settings.json and Codex's hooks.json, in the person's own
/// folders. Only the entries that are this program's are added or taken out; everything else in those files is
/// written back as it was read, and the file as it was before the first change is kept beside it.
public static class ReachHooks {
    public static string FileOf(string tool) {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (tool == "claude") return Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude"), "settings.json");
        if (tool == "codex") return Path.Combine(CodexSessions.Home, "hooks.json");
        throw new ArgumentException("工具无效");
    }
    // Codex hands its hook to PowerShell as one line. A path without a space in it is a command as it stands; one
    // with a space is called with the call operator (in double quotes alone it would be a string, not a command).
    // Both forms were tried on Codex 0.162.
    static string Plain(string path) { return path.IndexOf(' ') < 0 ? path : "& '" + path.Replace("'", "''") + "'"; }
    static bool Mine(object hook) {
        var one = hook as Dictionary<string, object>; object value;
        if (one == null) return false;
        string line = (one.TryGetValue("command", out value) ? Convert.ToString(value) : "") + " " + (one.TryGetValue("args", out value) && value is IEnumerable && !(value is string) ? String.Join(" ", ((IEnumerable)value).Cast<object>()) : "");
        return line.IndexOf("RemoteCli.reach.", StringComparison.Ordinal) >= 0 && line.IndexOf("--hook", StringComparison.Ordinal) >= 0;
    }
    static Dictionary<string, object> Entry(string tool, string name, string program, string pipe) {
        var hook = new Dictionary<string, object> { { "type", "command" } };
        // Claude Code starts a program with its arguments as they are given; Codex takes one line for a shell.
        if (tool == "claude") { hook["command"] = program; hook["args"] = new object[] { "--hook", "claude", pipe }; }
        else hook["command"] = Plain(program) + " --hook codex " + pipe;
        // Only approvals and stops can wait for the phone. SessionEnd must also fit Codex's three-second cap
        // and Claude Code's shutdown budget; the other events only report state.
        hook["timeout"] = name == "SessionEnd" ? 3 : name == "PermissionRequest" || name == "Stop" ? Reach.Patience : 10;
        if (name == Reach.Step) hook["statusMessage"] = "Remote AI";
        // The one hook that can run for minutes says what it is doing. Codex gets no status message: changing its
        // hook definition requires the user's trust again.
        if (tool == "claude" && name == "Stop") hook["statusMessage"] = Reach.Waiting;
        return new Dictionary<string, object> { { "hooks", new object[] { hook } } };
    }
    static Dictionary<string, object> Read(string file) {
        if (!File.Exists(file)) return new Dictionary<string, object>();
        string text = File.ReadAllText(file, Encoding.UTF8).TrimStart('﻿');
        if (text.Trim().Length == 0) return new Dictionary<string, object>();
        try {
            var read = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.DeserializeObject(text) as Dictionary<string, object>;
            if (read != null) return read;
        } catch (ArgumentException) { } catch (InvalidOperationException) { }
        throw new InvalidOperationException(Path.GetFileName(file) + " 的内容无法读懂，没有改动它。请先检查这个文件");
    }
    static IEnumerable<object> Items(object list) { return list is IEnumerable && !(list is string) ? ((IEnumerable)list).Cast<object>() : new object[0]; }
    // The groups of an event without those that are only this program's.
    static List<object> Others(object groups) {
        var kept = new List<object>();
        foreach (object group in Items(groups)) {
            var one = group as Dictionary<string, object>; object hooks;
            if (one != null && one.TryGetValue("hooks", out hooks) && Items(hooks).Any() && Items(hooks).All(Mine)) continue;
            kept.Add(group);
        }
        return kept;
    }
    /// Whether the tool's settings hold exactly the hooks this program would write now: for this program, at this pipe.
    public static bool Installed(string tool, string program, string pipe) {
        try {
            object hooks; var all = Read(FileOf(tool));
            if (!all.TryGetValue("hooks", out hooks) || !(hooks is Dictionary<string, object>)) return false;
            return Reach.EventsOf(tool).All(name => { object groups; string wanted = Write(Entry(tool, name, program, pipe), 0); return ((Dictionary<string, object>)hooks).TryGetValue(name, out groups) && Items(groups).Any(group => group is Dictionary<string, object> && Write(group, 0) == wanted); });
        } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } catch (InvalidOperationException) { return false; }
    }
    public static bool Installed(string tool) {
        try {
            object hooks; var all = Read(FileOf(tool));
            if (!all.TryGetValue("hooks", out hooks) || !(hooks is Dictionary<string, object>)) return false;
            return Reach.EventsOf(tool).All(name => { object groups; return ((Dictionary<string, object>)hooks).TryGetValue(name, out groups) && Items(groups).Count() > Others(groups).Count; });
        } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } catch (InvalidOperationException) { return false; }
    }
    /// Puts this program's hooks into a tool's settings, or takes them out. `program`: the program the tool is to
    /// run as its hook; `pipe`: where that reaches this one.
    public static void Set(string tool, bool on, string program, string pipe) {
        string file = FileOf(tool);
        if (!on && !File.Exists(file)) return;
        var all = Read(file);
        object existing; Dictionary<string, object> hooks = all.TryGetValue("hooks", out existing) ? existing as Dictionary<string, object> : null;
        if (hooks == null) { if (!on) return; if (existing != null) throw new InvalidOperationException(Path.GetFileName(file) + " 里的 hooks 不是预期的格式，没有改动它"); all["hooks"] = hooks = new Dictionary<string, object>(); }
        foreach (string name in Reach.EventsOf(tool).Concat(hooks.Keys.ToArray()).Distinct().ToArray()) {
            object groups; hooks.TryGetValue(name, out groups);
            var kept = Others(groups);
            if (on && Array.IndexOf(Reach.EventsOf(tool), name) >= 0) kept.Add(Entry(tool, name, program, pipe));
            if (kept.Count > 0) hooks[name] = kept.ToArray(); else hooks.Remove(name);
        }
        if (hooks.Count == 0) all.Remove("hooks");
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string before = file + ".before-remotecli";
        if (File.Exists(file) && !File.Exists(before)) File.Copy(file, before);
        string fresh = file + ".remotecli-new";
        File.WriteAllText(fresh, Write(all, 0) + "\n", new UTF8Encoding(false));
        if (File.Exists(file)) File.Replace(fresh, file, null); else File.Move(fresh, file);
    }
    // JSON as a person would lay it out, in the order it was read; letters of any language stay as they are.
    static string Write(object value, int depth) {
        string pad = new string(' ', depth * 2), inner = new string(' ', depth * 2 + 2);
        var map = value as Dictionary<string, object>;
        if (map != null) return map.Count == 0 ? "{}" : "{\n" + String.Join(",\n", map.Select(p => inner + Quote(p.Key) + ": " + Write(p.Value, depth + 1))) + "\n" + pad + "}";
        if (value == null) return "null";
        if (value is string) return Quote((string)value);
        if (value is bool) return (bool)value ? "true" : "false";
        if (value is IEnumerable) { var items = ((IEnumerable)value).Cast<object>().ToArray(); return items.Length == 0 ? "[]" : "[\n" + String.Join(",\n", items.Select(item => inner + Write(item, depth + 1))) + "\n" + pad + "]"; }
        if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
        if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }
    static string Quote(string text) {
        var quoted = new StringBuilder("\"");
        foreach (char c in text) {
            if (c == '"') quoted.Append("\\\""); else if (c == '\\') quoted.Append("\\\\"); else if (c == '\n') quoted.Append("\\n"); else if (c == '\r') quoted.Append("\\r");
            else if (c == '\t') quoted.Append("\\t"); else if (c < ' ') quoted.Append("\\u" + ((int)c).ToString("x4")); else quoted.Append(c);
        }
        return quoted.Append('"').ToString();
    }
}
}
