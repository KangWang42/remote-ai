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
///   whichever answer comes first; Codex asks its hooks before it shows the question, so it is kept waiting for
///   the phone only while nobody is at the computer;
/// - what to do next. A hook that runs as the tool stops can tell it to go on with something: a message from the
///   phone is passed to it there. One sent while the tool works waits for that moment; and while nobody is at the
///   computer, a tool that stops is kept waiting for the phone's message for a while.
/// As soon as somebody touches the keyboard or the mouse, whatever was kept waiting is let go, and the tool does
/// what it would have done without this.
public sealed class Reach : IDisposable {
    public const int Unattended = 60;       // seconds without a key or the mouse after which nobody is taken to be at the computer
    public const int Hold = 1800;           // the longest a tool is kept waiting for the phone, in seconds
    public const int Patience = 2000;       // what the tools are told a hook may take, in seconds: longer than any hold
    public static readonly string[] Events = { "SessionStart", "UserPromptSubmit", "PermissionRequest", "Stop", "SessionEnd" };
    const string Uuid = @"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z";

    /// What is known of one conversation. Status: "busy" (the tool works), "confirm" (it asks for permission),
    /// "waiting" (it has stopped and is kept waiting for a message from the phone), "idle" (it has stopped).
    public sealed class Open {
        public string Tool = "", Session = "", Status = "idle", AskId = "", AskText = "", Last = "";
        public bool Answerable;             // the request can be answered from the phone (else only at the computer)
        public bool Sayable { get { return Status != "idle"; } }     // a message can be left for it: it takes it now, or when it has done what it is doing
        internal string Transcript = "", Queued, Message; internal bool? Verdict; internal DateTime Seen, AskedAt; internal int Turn; internal long AskStamp;
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
    readonly string pipe; readonly Func<int> idle;
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
    volatile bool closed;
    /// Something about a conversation changed: the phone is to be told at once.
    public event Action Changed;
    public string Pipe { get { return pipe; } }

    public Reach(string pipeName, Func<int> idleSeconds = null) {
        pipe = pipeName; idle = idleSeconds ?? IdleSeconds;
        new Thread(Listen) { IsBackground = true, Name = "reach" }.Start();
    }
    public void Dispose() {
        closed = true;
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
    /// A message from the phone for a conversation. Says in words for the person why it cannot be passed on.
    /// Returns whether the tool takes it now (else when it has done what it is doing).
    public bool Say(string tool, string session, string text) {
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Length > 8000) throw new ArgumentException("消息需为 1 至 8000 字");
        lock (gate) {
            Open o;
            if (!open.TryGetValue(Key(tool, session), out o)) throw new InvalidOperationException("电脑上的这段对话现在联系不上，请刷新后重试");
            if (o.Status == "waiting") { o.Message = text; Monitor.PulseAll(gate); return true; }
            if (o.Status == "busy" || o.Status == "confirm") { o.Queued = o.Queued == null ? text : o.Queued + "\n\n" + text; return false; }
            throw new InvalidOperationException("这段对话已经在电脑上停下，等着在电脑上输入。它下次工作时发的消息，会在它做完后收到");
        }
    }
    /// Leaves a message for a conversation to take when it next stops, whatever it is doing now, also one that has
    /// told of nothing yet. (A conversation that sits idle is woken for it by other means: ClaudeRelay.)
    public void Leave(string tool, string session, string text) {
        lock (gate) {
            Open o; string key = Key(tool, session);
            if (!open.TryGetValue(key, out o)) open[key] = o = new Open { Tool = tool, Session = session, Seen = DateTime.UtcNow };
            o.Queued = o.Queued == null ? text : o.Queued + "\n\n" + text;
        }
    }
    /// Whether what was left has been taken: waits for that for up to `seconds`. What still lies there then is taken
    /// back (false): the conversation's hooks did not come for it, and it is to be handed over another way.
    public bool Taken(string tool, string session, int seconds) {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        lock (gate) {
            Open o;
            for (;;) {
                if (!open.TryGetValue(Key(tool, session), out o) || o.Queued == null) return true;
                if (DateTime.UtcNow >= until) { o.Queued = null; return false; }
                Monitor.Wait(gate, 250);
            }
        }
    }
    /// What stands in front of a message that a hook hands to its tool as its user's words from the phone.
    // (What the tools are told keeps the words that were tried with them, under the name the program had then:
    // how a tool takes a message hangs on its wording, and each wording has to be tried with the real tool.)
    public const string FromPhone = "Message from the user, sent from their phone through Remote CLI";
    /// The names by which what this program wrote into a conversation is known as its own: the one it tells the
    /// tools, and the one it shows.
    public static readonly string[] Names = { "Remote CLI", "Remote AI" };
    /// The phone's answer to a request for permission.
    public void Answer(string tool, string session, string ask, bool allow) {
        lock (gate) {
            Open o;
            if (!open.TryGetValue(Key(tool, session), out o) || o.Status != "confirm" || o.AskId != ask || ask.Length == 0) throw new InvalidOperationException("这个确认请求已经处理过了");
            if (!o.Answerable) throw new InvalidOperationException("这个确认请求正在电脑上等待，只能在电脑上回答");
            o.Verdict = allow; Monitor.PulseAll(gate);
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
                string[] answer = Handle(Convert.ToString(tool), (Dictionary<string, object>)hooked, () => gone || closed);
                WriteFrame(stream, Encoding.UTF8.GetBytes(json.Serialize(new Dictionary<string, object> { { "out", answer[0] }, { "code", Int32.Parse(answer[1]) } })));
                try { stream.WaitForPipeDrain(); } catch { }
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
    string[] Handle(string tool, Dictionary<string, object> hooked, Func<bool> gone) {
        string name = Text(hooked, "hook_event_name"), session = Text(hooked, "session_id");
        if ((tool != "claude" && tool != "codex") || !Regex.IsMatch(session, Uuid)) return Nothing;
        if (Scripted(tool, hooked)) return Nothing;
        Open o; bool first;
        lock (gate) {
            first = heard.Add(tool);
            string key = Key(tool, session);
            if (name == "SessionEnd") { if (open.Remove(key)) Monitor.PulseAll(gate); o = null; }
            else {
                if (!open.TryGetValue(key, out o)) open[key] = o = new Open { Tool = tool, Session = session };
                o.Seen = DateTime.UtcNow; o.Turn++;
                string transcript = Text(hooked, "transcript_path"); if (transcript.Length > 0) o.Transcript = transcript;
                if (open.Count > 200) foreach (string old in open.Where(p => DateTime.UtcNow - p.Value.Seen > TimeSpan.FromHours(24)).Select(p => p.Key).ToArray()) open.Remove(old);
            }
        }
        if (o == null || first) Tell();
        if (o == null) return Nothing;
        if (name == "SessionStart") { lock (gate) { if (o.Status != "busy") o.Status = "idle"; } Tell(); return Nothing; }
        if (name == "UserPromptSubmit") { lock (gate) { o.Status = "busy"; o.AskId = o.AskText = o.Last = ""; o.Answerable = false; Monitor.PulseAll(gate); } Tell(); return Nothing; }
        if (name == "PermissionRequest") return Asked(o, tool, hooked, gone);
        if (name == "Stop") return Stopped(o, hooked, gone);
        return Nothing;
    }
    // The tool asks for permission. The hook is kept waiting until the phone answers; or the tool lets the hook go, or
    // tells of something else (the question was answered at the computer); or, for a tool that does not show its
    // question while a hook runs, somebody is at the computer; or it has been long enough. That the conversation is
    // written to says nothing here: Claude Code writes to it while it asks.
    string[] Asked(Open o, string tool, Dictionary<string, object> hooked, Func<bool> gone) {
        bool beside = tool == "claude";         // its question is on the computer's screen while the hook runs
        string id; var began = DateTime.UtcNow; bool? verdict = null;
        lock (gate) {
            id = o.AskId = Guid.NewGuid().ToString("N").Substring(0, 12); o.AskText = Describe(hooked); o.Verdict = null;
            o.Status = "confirm"; o.Answerable = beside || idle() >= Unattended; o.AskedAt = began; o.AskStamp = Stamp(o.Transcript);
        }
        Tell();
        lock (gate) {
            for (;;) {
                if (o.AskId != id) return Nothing;                                  // the tool has gone on to something else
                if (o.Verdict.HasValue) { verdict = o.Verdict; break; }
                if (gone()) { o.AskId = o.AskText = ""; o.Answerable = false; o.Status = "busy"; break; }
                if (o.Answerable && ((!beside && idle() < Unattended) || DateTime.UtcNow - began > TimeSpan.FromSeconds(Hold))) { o.Answerable = false; if (!beside) break; }
                if (!o.Answerable && !beside) break;
                Monitor.Wait(gate, 300);
            }
            if (verdict.HasValue) { o.AskId = o.AskText = ""; o.Answerable = false; o.Status = "busy"; }
        }
        Tell();
        if (!verdict.HasValue) return Nothing;
        var decision = new Dictionary<string, object> { { "behavior", verdict.Value ? "allow" : "deny" } };
        if (!verdict.Value) decision["message"] = "The user declined this from their phone (Remote CLI).";
        return new[] { json.Serialize(new Dictionary<string, object> { { "hookSpecificOutput", new Dictionary<string, object> { { "hookEventName", "PermissionRequest" }, { "decision", decision } } } }), "0" };
    }
    // The tool has stopped. A message that waited for this goes to it now. Else, while nobody is at the computer, it
    // is kept waiting for one.
    string[] Stopped(Open o, Dictionary<string, object> hooked, Func<bool> gone) {
        string message = null; var began = DateTime.UtcNow; int turn;
        lock (gate) {
            o.Last = Short(Text(hooked, "last_assistant_message"), 300); o.AskId = o.AskText = ""; o.Answerable = false; turn = o.Turn;
            if (o.Queued != null) { message = o.Queued; o.Queued = null; Monitor.PulseAll(gate); }
            else if (idle() >= Unattended) {
                o.Status = "waiting"; o.Message = null;
                Monitor.PulseAll(gate);
            } else o.Status = "idle";
        }
        if (message == null) {
            Tell();
            lock (gate) {
                while (o.Status == "waiting" && o.Turn == turn) {
                    if (o.Message != null) { message = o.Message; o.Message = null; break; }
                    if (gone() || idle() < Unattended || DateTime.UtcNow - began > TimeSpan.FromSeconds(Hold)) { o.Status = "idle"; break; }
                    Monitor.Wait(gate, 300);
                }
            }
        }
        if (message != null) lock (gate) { if (o.Turn == turn) { o.Status = "busy"; o.Last = ""; } }
        Tell();
        if (message == null) return Nothing;
        string reason = FromPhone + " while they are away from the computer. Treat it as their next instruction:\n\n" + message;
        return new[] { json.Serialize(new Dictionary<string, object> { { "decision", "block" }, { "reason", reason } }), "0" };
    }

    /// The hook itself, as the tools run it: hands the event it was given to the program that listens on the pipe
    /// and writes back what that answers. When nothing listens, it has nothing to say and the tool goes on at once.
    public static int Hook(string tool, string pipeName) {
        try {
            byte[] hooked;
            using (var input = Console.OpenStandardInput()) using (var all = new MemoryStream()) { input.CopyTo(all); hooked = all.ToArray(); }
            string text = Encoding.UTF8.GetString(hooked).Trim().TrimStart('﻿');
            if (!text.StartsWith("{", StringComparison.Ordinal)) return 0;
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut)) {
                client.Connect(400);
                WriteFrame(client, Encoding.UTF8.GetBytes("{\"tool\":" + new JavaScriptSerializer().Serialize(tool) + ",\"event\":" + text + "}"));
                byte[] answer = ReadFrame(client);
                if (answer == null) return 0;
                var said = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(answer));
                byte[] written = Encoding.UTF8.GetBytes(Text(said, "out"));
                if (written.Length > 0) using (var output = Console.OpenStandardOutput()) { output.Write(written, 0, written.Length); output.Flush(); }
                int code; return Int32.TryParse(Text(said, "code"), out code) ? code : 0;
            }
        } catch (TimeoutException) { return 0; } catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; }
        catch (ArgumentException) { return 0; } catch (InvalidOperationException) { return 0; }
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
    static Dictionary<string, object> Entry(string tool, string program, string pipe) {
        var hook = new Dictionary<string, object> { { "type", "command" } };
        // Claude Code starts a program with its arguments as they are given; Codex takes one line for a shell.
        if (tool == "claude") { hook["command"] = program; hook["args"] = new object[] { "--hook", "claude", pipe }; }
        else hook["command"] = Plain(program) + " --hook codex " + pipe;
        hook["timeout"] = Reach.Patience;
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
            string wanted = Write(Entry(tool, program, pipe), 0);
            return Reach.Events.All(name => { object groups; return ((Dictionary<string, object>)hooks).TryGetValue(name, out groups) && Items(groups).Any(group => group is Dictionary<string, object> && Write(group, 0) == wanted); });
        } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } catch (InvalidOperationException) { return false; }
    }
    public static bool Installed(string tool) {
        try {
            object hooks; var all = Read(FileOf(tool));
            if (!all.TryGetValue("hooks", out hooks) || !(hooks is Dictionary<string, object>)) return false;
            return Reach.Events.All(name => { object groups; return ((Dictionary<string, object>)hooks).TryGetValue(name, out groups) && Items(groups).Count() > Others(groups).Count; });
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
        foreach (string name in Reach.Events.Concat(hooks.Keys.ToArray()).Distinct().ToArray()) {
            object groups; hooks.TryGetValue(name, out groups);
            var kept = Others(groups);
            if (on && Array.IndexOf(Reach.Events, name) >= 0) kept.Add(Entry(tool, program, pipe));
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
