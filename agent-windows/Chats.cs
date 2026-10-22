using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// What runs one of the phone's terminals: a pseudo console (PseudoTerminal), or a conversation this computer runs
/// through its tool's structured mode (Chat). The agent numbers, keeps and relays what either gives in the same way.
public interface IRunning : IDisposable {
    int Id { get; }
    bool Closed { get; }
    bool Drained { get; }
    int? ExitCode { get; }
    int Cols { get; }
    int Rows { get; }
    void Write(string text);
    void Resize(int cols, int rows);
}

/// A conversation of Claude Code or Codex that this computer runs for the phone through the tool's own structured
/// mode: Claude Code's `stream-json` (what its SDK uses) and Codex's `app-server` (what its editor extension and desktop
/// app use). The phone sees it as a terminal whose output is not a screen but the conversation's events, one JSON
/// object a line (docs/PROTOCOL.md, "Chats"): what is said as it is written, each step as it starts, what it prints
/// and how it ended, what it asks and how that was answered, and how the conversation stands. What is said to it, a
/// stop and an answer go straight to the tool. The conversation is the tool's own, saved where its other windows
/// find it, and can be gone on with in a terminal or a desktop app once this has closed.
public abstract class Chat : IRunning {
    public const int OutputMost = 60000;       // of one step's output, what is passed on; the rest is only counted
    public static readonly string[] Decisions = { "allow", "always", "deny" };
    protected readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
    protected readonly object gate = new object();
    readonly Action<string> emit;
    Process process;
    Stream input;
    bool disposed;
    int? exit;
    readonly Timer flusher;
    // a run of pieces of one text, gathered so that a fast writer gives a few lines a second rather than one a word
    string gatherKind = "", gatherId = "";
    readonly StringBuilder gathered = new StringBuilder();
    readonly Dictionary<string, int> printed = new Dictionary<string, int>();     // step -> how much of its output was passed on
    protected readonly Dictionary<string, Asked> asks = new Dictionary<string, Asked>();
    protected bool stopping;

    public string Tool { get; private set; }
    public string Folder { get; private set; }
    /// The conversation's id in its tool, once it is known.
    public string Session { get; protected set; }
    /// starting, busy (at work), idle, confirm (it asks), ended.
    public string Status { get; private set; }
    /// What it said last, one line, for the lists.
    public string Said { get; private set; }
    /// What it asks now, for the lists: its words, or "" when it asks nothing.
    public string Asking { get { lock (gate) return asks.Count == 0 ? "" : asks.Values.First().Text; } }

    public int Id { get { try { return process.Id; } catch { return 0; } } }
    public bool Closed { get; private set; }
    public bool Drained { get; private set; }
    public int? ExitCode { get { return exit; } }
    public int Cols { get { return 80; } }
    public int Rows { get { return 24; } }

    protected sealed class Asked {
        public string Id, Text; public object Request; public string Kind = ""; public bool Always;
        public List<string> Choices = new List<string>();
    }

    protected Chat(string tool, string folder, Action<string> emit) {
        Tool = tool; Folder = folder; this.emit = emit; Status = "starting"; Said = ""; Session = "";
        flusher = new Timer(_ => { lock (gate) Flush(); }, null, 120, 120);
    }

    /// Starts the conversation: the saved one `session` gone on with (`resume`), else a new one (named `session`
    /// where the tool lets its id be chosen). `mode` is the computer's widest allowed way of working (full, edit,
    /// read), as for its terminals.
    public static Chat Open(string tool, string launcher, string folder, string session, bool resume, string mode, Action<string> emit) {
        Chat chat;
        if (tool == "claude") chat = new ClaudeChat(folder, emit);
        else if (tool == "codex") chat = new CodexChat(folder, emit);
        else throw new ArgumentException("这个工具不支持对话");
        try { chat.Begin(launcher, session ?? "", resume, mode ?? ""); }
        catch { chat.Dispose(); throw; }
        return chat;
    }

    protected abstract void Begin(string launcher, string session, bool resume, string mode);
    /// Hands what was written on the phone to the conversation: at once when it waits, else as soon as the tool takes
    /// it while it works. `id` is the phone's number for it, given back with the message once the tool has it.
    public abstract void Say(string text, string id);
    /// Stops what it is at now. The conversation stays, and is told the next thing as before.
    public abstract void Stop();
    /// Answers what it asks: allow, always (allow, and from now on the same without asking), deny; or, where it
    /// asks a question with choices, "choice:<n>".
    public void Answer(string ask, string decision) {
        Asked asked;
        lock (gate) {
            if (!asks.TryGetValue(ask, out asked)) throw new InvalidOperationException("这个确认请求已经处理过了");
            int choice;
            bool chosen = decision.StartsWith("choice:", StringComparison.Ordinal) && Int32.TryParse(decision.Substring(7), out choice) && choice >= 0 && choice < asked.Choices.Count;
            if (!chosen && Array.IndexOf(Decisions, decision) < 0) throw new ArgumentException("回答无效");
            if (decision == "always" && !asked.Always) throw new ArgumentException("这个请求不能设为总是允许");
            asks.Remove(ask);
            Event(new Dictionary<string, object> { { "t", "answered" }, { "id", ask }, { "decision", decision } });
            if (asks.Count == 0 && Status == "confirm") State("busy");
        }
        Reply(asked, decision);
    }
    protected abstract void Reply(Asked asked, string decision);

    // ---- the tool's program
    protected void Run(string launcher, string args) {
        var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Folder, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        if (launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) {
            info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            info.Arguments = "/d /s /c \"\"" + launcher + "\" " + args + "\"";
        } else { info.FileName = launcher; info.Arguments = args; }
        // the owner's setup, but never a coding session this program itself may have been started from
        foreach (string key in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT" }) info.EnvironmentVariables.Remove(key);
        process = Process.Start(info);
        input = process.StandardInput.BaseStream;       // (written as UTF-8 here, whatever the console's code page)
        Task.Run(() => {
            try { string line; while ((line = process.StandardOutput.ReadLine()) != null) { if (line.StartsWith("{", StringComparison.Ordinal)) Take(line); } }
            catch (IOException) { } catch (ObjectDisposedException) { }
            finally { Ended(); }
        });
        Task.Run(() => { try { string line; var last = new Queue<string>(); while ((line = process.StandardError.ReadLine()) != null) { last.Enqueue(line); if (last.Count > 6) last.Dequeue(); } lock (gate) lastErrors = String.Join("\n", last); } catch { } });
    }
    string lastErrors = "";
    void Take(string line) {
        Dictionary<string, object> message;
        try { message = json.Deserialize<Dictionary<string, object>>(line); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
        if (message == null) return;
        // a message of a shape not known here is passed over, not the end of the conversation
        try { lock (gate) Received(message); }
        catch (InvalidCastException) { } catch (KeyNotFoundException) { } catch (NullReferenceException) { } catch (FormatException) { } catch (OverflowException) { }
    }
    /// One message of the tool, under the gate.
    protected abstract void Received(Dictionary<string, object> message);
    protected void Send(object message) {
        byte[] bytes = new UTF8Encoding(false).GetBytes(json.Serialize(message) + "\n");
        lock (input) { if (Closed || disposed) throw new IOException("对话已结束"); input.Write(bytes, 0, bytes.Length); input.Flush(); }
    }
    void Ended() {
        try { process.WaitForExit(5000); exit = process.ExitCode; } catch { }
        lock (gate) {
            foreach (var asked in asks.Keys.ToArray()) Event(new Dictionary<string, object> { { "t", "answered" }, { "id", asked }, { "decision", "gone" } });
            asks.Clear();
            if (!disposed && exit.HasValue && exit.Value != 0) Note("工具已退出（代码 " + exit.Value + "）" + (lastErrors.Length > 0 ? "：" + lastErrors : ""));
            State("ended");
            Flush();
            Closed = true; Drained = true;
        }
    }
    public void Write(string text) { throw new InvalidOperationException("对话不接受终端输入"); }
    public void Resize(int cols, int rows) { }
    public void Dispose() {
        lock (gate) { if (disposed) return; disposed = true; Flush(); }
        flusher.Dispose();
        try { if (process != null && !process.HasExited) using (Process kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true })) kill.WaitForExit(10000); } catch { }
        if (process == null) { Closed = true; Drained = true; }
    }

    // ---- what goes to the phone
    protected void Event(Dictionary<string, object> what) { Flush(); emit(json.Serialize(what) + "\n"); }
    void Flush() {
        if (gathered.Length == 0) return;
        string text = gathered.ToString(); gathered.Clear();
        emit(json.Serialize(new Dictionary<string, object> { { "t", gatherKind }, { "id", gatherId }, { "text", text } }) + "\n");
    }
    protected static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }
    protected void State(string now) {
        if (Status == now) return;
        Status = now;
        Event(new Dictionary<string, object> { { "t", "state" }, { "state", now }, { "at", Now() } });
    }
    protected void Known(string session) {
        if (String.IsNullOrEmpty(session) || session == Session) return;
        Session = session;
        Event(new Dictionary<string, object> { { "t", "session" }, { "session", session } });
    }
    /// What was said before, where an earlier conversation is gone on with: in the shape of a conversation's window
    /// (TerminalAgent.Said), so that the phone shows it above what follows.
    public void Before(List<Dictionary<string, object>> messages, bool more) {
        lock (gate) Event(new Dictionary<string, object> { { "t", "history" }, { "messages", messages }, { "more", more } });
    }
    protected void Item(string id, string kind, string text, Dictionary<string, object> more = null) {
        var item = new Dictionary<string, object> { { "t", "item" }, { "id", id }, { "kind", kind }, { "text", text ?? "" }, { "at", Now() } };
        if (more != null) foreach (var pair in more) item[pair.Key] = pair.Value;
        Event(item);
        if (kind == "text") Spoken(text);
    }
    /// More of a text as it is written ("more"), or more of what a step prints ("out").
    protected void Append(string kind, string id, string text) {
        if (String.IsNullOrEmpty(text)) return;
        if (kind == "out") {
            int had; printed.TryGetValue(id, out had);
            if (had >= OutputMost) return;
            if (had + text.Length > OutputMost) text = text.Substring(0, OutputMost - had) + "\n…";
            printed[id] = had + text.Length;
        }
        if (gathered.Length > 0 && (gatherKind != kind || gatherId != id)) Flush();
        gatherKind = kind; gatherId = id; gathered.Append(text);
        if (gathered.Length > 8000) Flush();
        if (kind == "more") Spoken(null, text);
    }
    protected bool Printed(string id) { return printed.ContainsKey(id); }
    protected void Done(string id, bool ok, string result) {
        var done = new Dictionary<string, object> { { "t", "done" }, { "id", id }, { "ok", ok } };
        if (!Printed(id) && !String.IsNullOrEmpty(result)) done["result"] = Shown(result);
        Event(done);
    }
    /// The whole of a text once it is written, where the tool says it: it replaces what came in pieces.
    protected void Settled(string id, string text) { Event(new Dictionary<string, object> { { "t", "set" }, { "id", id }, { "text", text } }); Spoken(text); }
    protected void Note(string text) { Event(new Dictionary<string, object> { { "t", "note" }, { "text", text }, { "at", Now() } }); }
    protected void TurnEnded(string how, string error) {
        var turn = new Dictionary<string, object> { { "t", "turn" }, { "end", how }, { "at", Now() } };
        if (!String.IsNullOrEmpty(error)) turn["error"] = error;
        Event(turn);
        stopping = false;
    }
    protected string Ask(object request, string kind, string title, string text, bool always, IList<string> choices = null) {
        var asked = new Asked { Id = Guid.NewGuid().ToString("N").Substring(0, 16), Request = request, Kind = kind, Text = (title + (text.Length > 0 ? "\n" + text : "")).Trim(), Always = always };
        var options = new List<object>();
        if (choices != null && choices.Count > 0) {
            asked.Choices.AddRange(choices);
            for (int i = 0; i < choices.Count; i++) options.Add(new Dictionary<string, object> { { "id", "choice:" + i }, { "label", choices[i] } });
        } else {
            options.Add(new Dictionary<string, object> { { "id", "allow" }, { "label", "允许" } });
            if (always) options.Add(new Dictionary<string, object> { { "id", "always" }, { "label", "总是允许" } });
        }
        options.Add(new Dictionary<string, object> { { "id", "deny" }, { "label", choices != null && choices.Count > 0 ? "不回答" : "拒绝" } });
        asks[asked.Id] = asked;
        Event(new Dictionary<string, object> { { "t", "ask" }, { "id", asked.Id }, { "kind", kind }, { "title", title }, { "text", Cut(text, 4000) }, { "options", options } });
        State("confirm");
        return asked.Id;
    }
    void Spoken(string whole, string more = null) {
        string text = whole ?? (Said + more);
        string line = text.Split('\n').Select(one => one.Trim()).LastOrDefault(one => one.Length > 0) ?? "";
        if (whole != null || line.Length > 0) Said = line.Length > 200 ? line.Substring(0, 200) : line;
    }
    /// What a step gave back, for a step whose output was not passed on as it came: its last lines, as a terminal
    /// would show them at its end.
    public static string Shown(string back) {
        back = (back ?? "").Replace("\r\n", "\n").Trim('\n', '\r');
        if (back.Length > 6000) back = "…\n" + back.Substring(back.Length - 6000);
        var lines = back.Split('\n');
        return lines.Length > 40 ? "…\n" + String.Join("\n", lines.Skip(lines.Length - 40)) : back;
    }
    protected static string Cut(string text, int most) { text = text ?? ""; return text.Length > most ? text.Substring(0, most) + "…" : text; }
    /// A step named as the tools' windows name it: what it is, and what for, where the tool says it.
    public static string Brief(object name, object given) {
        var input = given as Dictionary<string, object>; string about = "";
        foreach (string key in new[] { "description", "command", "file_path", "path", "pattern", "query", "url" }) { about = At(input, key) as string ?? ""; if (about.Length > 0) break; }
        return (Convert.ToString(name) + (about.Length > 0 ? "：" + (about.Length > 160 ? about.Substring(0, 160) : about) : "")).Trim();
    }
    protected static object At(object from, string key) { var d = from as Dictionary<string, object>; object v; return d != null && d.TryGetValue(key, out v) ? v : null; }
    protected static string Text(object from, string key) { return At(from, key) as string ?? ""; }
    protected static IEnumerable<object> Many(object from, string key) { var v = At(from, key) as IEnumerable; return v == null || v is string ? new object[0] : v.Cast<object>(); }
}

/// Claude Code in `--print` mode with JSON lines both ways, as its SDK drives it. It is told what to say on its
/// standard input, a stop as a control request (`interrupt`), and it asks this program, not a person at the
/// computer, whether a step may run (`--permission-prompt-tool stdio`: a `can_use_tool` request, answered with the
/// decision). Run on Claude Code 2.1.283 (docs/PROTOCOL.md, "Chats").
public sealed class ClaudeChat : Chat {
    public ClaudeChat(string folder, Action<string> emit) : base("claude", folder, emit) { }
    string messageId = "";
    readonly HashSet<string> streamed = new HashSet<string>();       // messages whose text came in pieces
    readonly Dictionary<int, string> blocks = new Dictionary<int, string>();     // block index -> item, in the message being written
    readonly Dictionary<string, string> steps = new Dictionary<string, string>();   // tool_use id -> item
    readonly Queue<KeyValuePair<string, string>> said = new Queue<KeyValuePair<string, string>>();   // what the phone said, and its number, until it is given back
    int requests;

    public static string Arguments(string session, bool resume, string mode) {
        return "-p --input-format stream-json --output-format stream-json --verbose --include-partial-messages --replay-user-messages --permission-prompt-tool stdio"
            + (resume ? " --resume " : " --session-id ") + session
            + (mode == "read" ? " --permission-mode plan" : mode == "edit" ? " --permission-mode acceptEdits" : "");
    }
    protected override void Begin(string launcher, string session, bool resume, string mode) {
        if (session.Length == 0) { if (resume) throw new ArgumentException("对话编号无效"); session = Guid.NewGuid().ToString(); }
        if (!Regex.IsMatch(session, @"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z")) throw new ArgumentException("对话编号无效");
        lock (gate) Known(session);
        Run(launcher, Arguments(session, resume, mode));
        Send(new Dictionary<string, object> { { "type", "control_request" }, { "request_id", "init-" + (++requests) }, { "request", new Dictionary<string, object> { { "subtype", "initialize" } } } });
        lock (gate) State("idle");
    }
    public override void Say(string text, string id) {
        lock (gate) { said.Enqueue(new KeyValuePair<string, string>(text, id ?? "")); if (Status != "confirm") State("busy"); }
        Send(new Dictionary<string, object> { { "type", "user" }, { "message", new Dictionary<string, object> { { "role", "user" }, { "content", text } } } });
    }
    public override void Stop() {
        lock (gate) { if (Status != "busy" && Status != "confirm") throw new InvalidOperationException("它现在没有在执行"); stopping = true; }
        Send(new Dictionary<string, object> { { "type", "control_request" }, { "request_id", "stop-" + (++requests) }, { "request", new Dictionary<string, object> { { "subtype", "interrupt" } } } });
    }
    protected override void Received(Dictionary<string, object> m) {
        string type = Text(m, "type");
        if (type == "system") {
            if (Text(m, "subtype") == "init") Known(Text(m, "session_id"));
            else if (Text(m, "subtype") == "status" && Text(m, "status") == "requesting" && Status == "idle") State("busy");
            return;
        }
        if (type == "stream_event") { Streamed(At(m, "event")); return; }
        if (type == "assistant") { Whole(At(m, "message")); return; }
        if (type == "user") { Came(m); return; }
        if (type == "control_request") { Permission(m); return; }
        if (type == "control_cancel_request") { Withdrawn(Text(m, "request_id")); return; }
        if (type == "result") {
            string subtype = Text(m, "subtype");
            bool ok = subtype == "success" && !true.Equals(At(m, "is_error"));
            TurnEnded(stopping && !ok ? "stopped" : ok ? "done" : "failed", ok || stopping ? "" : Text(m, "result").Length > 0 ? Cut(Text(m, "result"), 600) : subtype);
            blocks.Clear();
            State(asks.Count > 0 ? "confirm" : "idle");
        }
    }
    void Streamed(object e) {
        string type = Text(e, "type");
        if (type == "message_start") { messageId = Text(At(e, "message"), "id"); blocks.Clear(); if (Status == "idle") State("busy"); return; }
        int index = Convert.ToInt32(At(e, "index") ?? -1);
        if (type == "content_block_start") {
            var block = At(e, "content_block"); string kind = Text(block, "type");
            if (kind == "text" || kind == "thinking") {
                string id = messageId + ":" + index;
                blocks[index] = id;
                Item(id, kind == "text" ? "text" : "thought", Text(block, kind));
                streamed.Add(messageId);
            }
            return;
        }
        if (type == "content_block_delta") {
            string id; if (!blocks.TryGetValue(index, out id)) return;
            var delta = At(e, "delta");
            Append("more", id, Text(delta, "text") + Text(delta, "thinking"));
        }
    }
    // A message as a whole: its steps (and its text, where it did not come in pieces)
    void Whole(object message) {
        string id = Text(message, "id");
        foreach (object part in Many(message, "content")) {
            string kind = Text(part, "type");
            if (kind == "tool_use") {
                string use = Text(part, "id"), item = "step-" + use;
                if (steps.ContainsKey(use)) continue;
                steps[use] = item;
                var input = At(part, "input");
                var more = new Dictionary<string, object> { { "name", Text(part, "name") }, { "state", "run" } };
                string command = Text(input, "command");
                if (command.Length > 0) more["detail"] = Cut(command, 2000);
                else if (Text(part, "name") == "Edit" || Text(part, "name") == "Write" || Text(part, "name") == "MultiEdit") more["detail"] = Text(input, "file_path");
                Item(item, "step", Brief(At(part, "name"), input), more);
            } else if (kind == "text" && !streamed.Contains(id) && Text(part, "text").Trim().Length > 0) {
                Item(id + ":t" + Guid.NewGuid().ToString("N").Substring(0, 6), "text", Text(part, "text"));
            }
        }
    }
    // what the tool was given: the phone's words given back, and what came of each step
    void Came(Dictionary<string, object> m) {
        var message = At(m, "message"); object content = At(message, "content");
        if (content is string) { Spoke((string)content, m); return; }
        foreach (object part in Many(message, "content")) {
            string kind = Text(part, "type");
            if (kind == "text") { string text = Text(part, "text"); if (!text.StartsWith("[Request interrupted", StringComparison.Ordinal)) Spoke(text, m); }
            else if (kind == "tool_result") {
                string item; if (!steps.TryGetValue(Text(part, "tool_use_id"), out item)) continue;
                object back = At(part, "content");
                string text = back as string ?? String.Join("\n", (back as IEnumerable ?? new object[0]).Cast<object>().Select(one => Text(one, "text")));
                Done(item, !true.Equals(At(part, "is_error")), text);
            }
        }
    }
    void Spoke(string text, Dictionary<string, object> m) {
        if (text.Trim().Length == 0 || true.Equals(At(m, "isSynthetic"))) return;
        string from = "";
        // the oldest message of the phone's that has not come back: it is this one, where the words are the same
        if (said.Count > 0 && said.Peek().Key.Trim() == text.Trim()) from = said.Dequeue().Value;
        var more = new Dictionary<string, object>();
        if (from.Length > 0) more["op"] = from;
        string id = Text(m, "uuid");
        Item(id.Length > 0 ? "user-" + id : "user-" + Guid.NewGuid().ToString("N"), "user", text, more);
    }
    void Permission(Dictionary<string, object> m) {
        var request = At(m, "request");
        string id = Text(m, "request_id");
        if (Text(request, "subtype") != "can_use_tool") {
            // nothing else is asked of this program: it is refused, so that the tool does not wait for ever
            Respond(id, null, "不支持的请求");
            return;
        }
        string name = Text(request, "tool_name"), shown = Text(request, "display_name");
        var input = At(request, "input");
        if (name == "AskUserQuestion") {
            var questions = Many(input, "questions").ToList();
            if (questions.Count == 1 && !true.Equals(At(questions[0], "multiSelect"))) {
                var labels = Many(questions[0], "options").Select(one => Text(one, "label")).Where(one => one.Length > 0).ToList();
                if (labels.Count > 0) { Ask(m, "question", Text(questions[0], "question"), "", false, labels); return; }
            }
        }
        string title = name == "ExitPlanMode" ? "开始按计划执行？" : (shown.Length > 0 ? shown : name);
        string text = name == "ExitPlanMode" ? Text(input, "plan") : Text(input, "command").Length > 0 ? Text(input, "command") : Text(input, "file_path").Length > 0 ? Text(input, "file_path") : json.Serialize(input ?? new object());
        string about = Text(request, "description");
        if (about.Length > 0 && about != text) text = about + "\n" + text;
        var suggestion = ClaudePermissions.Select(At(request, "permission_suggestions"), true);
        if (suggestion != null) text = ClaudePermissions.Detail(suggestion) + "\n\n" + text;
        Ask(m, name == "ExitPlanMode" ? "plan" : "tool", title, text, suggestion != null);
    }
    void Withdrawn(string request) {
        foreach (var one in asks.Values.Where(a => Text(a.Request, "request_id") == request).ToArray()) {
            asks.Remove(one.Id);
            Event(new Dictionary<string, object> { { "t", "answered" }, { "id", one.Id }, { "decision", "gone" } });
        }
        if (asks.Count == 0 && Status == "confirm") State("busy");
    }
    protected override void Reply(Asked asked, string decision) {
        var m = (Dictionary<string, object>)asked.Request;
        var request = At(m, "request");
        object input = At(request, "input") ?? new Dictionary<string, object>();
        Dictionary<string, object> answer;
        if (decision.StartsWith("choice:", StringComparison.Ordinal)) {
            var question = Many(input, "questions").First();
            var given = new Dictionary<string, object>((Dictionary<string, object>)input);
            given["answers"] = new Dictionary<string, object> { { Text(question, "question"), asked.Choices[Int32.Parse(decision.Substring(7))] } };
            answer = new Dictionary<string, object> { { "behavior", "allow" }, { "updatedInput", given } };
        }
        else if (decision == "deny") answer = new Dictionary<string, object> { { "behavior", "deny" }, { "message", "用户在手机上拒绝了这一步" } };
        else {
            answer = new Dictionary<string, object> { { "behavior", "allow" }, { "updatedInput", input } };
            if (decision == "always") {
                var suggestion = ClaudePermissions.Select(At(request, "permission_suggestions"), true);
                if (suggestion == null) throw new ArgumentException("这个请求不能设为总是允许");
                answer["updatedPermissions"] = new object[] { suggestion };
            }
        }
        Respond(Text(m, "request_id"), answer, null);
    }
    void Respond(string request, object answer, string error) {
        var response = new Dictionary<string, object> { { "request_id", request } };
        if (error == null) { response["subtype"] = "success"; response["response"] = answer; }
        else { response["subtype"] = "error"; response["error"] = error; }
        Send(new Dictionary<string, object> { { "type", "control_response" }, { "response", response } });
    }
}

/// Codex through `codex app-server`: JSON-RPC on its standard input and output, one conversation ("thread") a
/// program here. What is said starts a turn (`turn/start`), or joins the one at work (`turn/steer`); a stop is
/// `turn/interrupt`; the steps it wants to run and the files it wants to change are asked of this program as
/// requests, answered with the decision. Run on Codex 0.163.0 (docs/PROTOCOL.md, "Chats").
public sealed class CodexChat : Chat {
    public CodexChat(string folder, Action<string> emit) : base("codex", folder, emit) { }
    int next;
    readonly Dictionary<int, Action<Dictionary<string, object>>> waiting = new Dictionary<int, Action<Dictionary<string, object>>>();
    string turn = "";
    string mode = "";
    readonly List<KeyValuePair<string, string>> early = new List<KeyValuePair<string, string>>();     // said before the conversation was there

    protected override void Begin(string launcher, string session, bool resume, string mode) {
        this.mode = mode;
        if (!resume) session = "";
        if (session.Length > 0 && !Regex.IsMatch(session, @"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z")) throw new ArgumentException("对话编号无效");
        Run(launcher, "app-server");
        Call("initialize", new Dictionary<string, object> { { "clientInfo", new Dictionary<string, object> { { "name", "remote-ai" }, { "title", "Remote AI" }, { "version", "1" } } } }, _ => { });
        Send(new Dictionary<string, object> { { "method", "initialized" }, { "params", new Dictionary<string, object>() } });
        var given = new Dictionary<string, object> { { "cwd", Folder } };
        if (mode == "read") given["sandbox"] = "read-only"; else if (mode == "edit") given["sandbox"] = "workspace-write";
        if (session.Length > 0) given["threadId"] = session;
        Call(session.Length > 0 ? "thread/resume" : "thread/start", given, answer => {
            string thread = Text(At(At(answer, "result"), "thread"), "id");
            if (thread.Length == 0) { Note("Codex 没有打开这段对话：" + Text(At(answer, "error"), "message")); State("ended"); return; }
            Known(thread);
            State(Status == "busy" ? "busy" : "idle");
            foreach (var one in early.ToArray()) Start(one.Key, one.Value);
            early.Clear();
        });
    }
    void Call(string method, object given, Action<Dictionary<string, object>> answered) {
        int id;
        lock (gate) { id = ++next; waiting[id] = answered; }
        Send(new Dictionary<string, object> { { "id", id }, { "method", method }, { "params", given } });
    }
    static List<object> Words(string text) { return new List<object> { new Dictionary<string, object> { { "type", "text" }, { "text", text }, { "text_elements", new object[0] } } }; }
    public override void Say(string text, string id) {
        lock (gate) {
            if (Session.Length == 0) { early.Add(new KeyValuePair<string, string>(text, id ?? "")); State("busy"); return; }
            if (turn.Length > 0) {
                // it is at work: the words join the turn, as its own window's box does
                Call("turn/steer", new Dictionary<string, object> { { "threadId", Session }, { "expectedTurnId", turn }, { "input", Words(text) }, { "clientUserMessageId", Client(id) } }, answer => {
                    if (At(answer, "error") != null) Start(text, id);       // the turn had just ended: a new one
                });
                return;
            }
            Start(text, id);
        }
    }
    static string Client(string id) { return String.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id; }
    void Start(string text, string id) {
        State("busy");
        Call("turn/start", new Dictionary<string, object> { { "threadId", Session }, { "input", Words(text) }, { "clientUserMessageId", Client(id) } }, answer => {
            var error = At(answer, "error");
            if (error != null) { Note("Codex 没有收下这句话：" + Text(error, "message")); State(turn.Length > 0 ? "busy" : "idle"); return; }
            string started = Text(At(At(answer, "result"), "turn"), "id");
            if (started.Length > 0 && turn.Length == 0) turn = started;
        });
    }
    public override void Stop() {
        lock (gate) {
            if (turn.Length == 0) throw new InvalidOperationException("它现在没有在执行");
            stopping = true;
            Call("turn/interrupt", new Dictionary<string, object> { { "threadId", Session }, { "turnId", turn } }, _ => { });
        }
    }
    protected override void Received(Dictionary<string, object> m) {
        object id = At(m, "id"); string method = Text(m, "method");
        if (id != null && method.Length == 0) {
            int number; Action<Dictionary<string, object>> answered;
            if (Int32.TryParse(Convert.ToString(id), out number) && waiting.TryGetValue(number, out answered)) { waiting.Remove(number); answered(m); }
            return;
        }
        var p = At(m, "params");
        if (id != null) { Requested(id, method, p); return; }
        switch (method) {
            case "turn/started": turn = Text(At(p, "turn"), "id"); State(asks.Count > 0 ? "confirm" : "busy"); break;
            case "turn/completed": {
                var done = At(p, "turn"); string status = Text(done, "status");
                if (Text(done, "id") == turn || turn.Length == 0) turn = "";
                TurnEnded(status == "completed" ? "done" : status == "interrupted" ? "stopped" : "failed", status == "failed" ? Text(At(done, "error"), "message") : "");
                State(asks.Count > 0 ? "confirm" : turn.Length > 0 ? "busy" : "idle");
                break;
            }
            case "item/started": Started(At(p, "item")); break;
            case "item/completed": Completed(At(p, "item")); break;
            case "item/agentMessage/delta": Append("more", Text(p, "itemId"), Text(p, "delta")); break;
            case "item/reasoning/summaryTextDelta": Thought(Text(p, "itemId"), Text(p, "delta")); break;
            case "item/commandExecution/outputDelta": case "item/fileChange/outputDelta": Append("out", Text(p, "itemId"), Text(p, "delta")); break;
            case "serverRequest/resolved": {
                // answered elsewhere, or no longer wanted
                string request = Convert.ToString(At(p, "requestId"));
                foreach (var one in asks.Values.Where(a => Convert.ToString(At(a.Request, "id")) == request).ToArray()) {
                    asks.Remove(one.Id);
                    Event(new Dictionary<string, object> { { "t", "answered" }, { "id", one.Id }, { "decision", "gone" } });
                }
                if (asks.Count == 0 && Status == "confirm") State(turn.Length > 0 ? "busy" : "idle");
                break;
            }
            case "error": Note(Text(At(p, "error"), "message").Length > 0 ? Text(At(p, "error"), "message") : "Codex 出错"); break;
        }
    }
    readonly HashSet<string> thoughts = new HashSet<string>();
    void Thought(string item, string text) {
        if (!thoughts.Contains(item)) { thoughts.Add(item); Item(item, "thought", ""); }
        Append("more", item, text);
    }
    void Started(object item) {
        string type = Text(item, "type"), id = Text(item, "id");
        if (type == "userMessage") {
            string text = String.Join("\n", Many(item, "content").Where(one => Text(one, "type") == "text").Select(one => Text(one, "text")));
            var more = new Dictionary<string, object>();
            if (Text(item, "clientId").Length > 0) more["op"] = Text(item, "clientId");
            if (text.Length > 0) Item(id, "user", text, more);
        }
        else if (type == "agentMessage") Item(id, "text", Text(item, "text"));
        else if (type == "commandExecution") {
            var actions = Many(item, "commandActions").ToList();
            string shortCommand = actions.Select(one => Text(one, "command")).FirstOrDefault(one => one.Length > 0) ?? Text(item, "command");
            var reads = actions.Where(one => Text(one, "type") == "read").Select(one => Text(one, "name").Length > 0 ? Text(one, "name") : Text(one, "path")).Where(one => one.Length > 0).ToList();
            string title = actions.Count > 0 && reads.Count == actions.Count ? "读取：" + String.Join("、", reads.Take(3)) + (reads.Count > 3 ? " 等 " + reads.Count + " 个文件" : "") : "运行：" + Cut(shortCommand, 160);
            Item(id, "step", title, new Dictionary<string, object> { { "name", "shell" }, { "detail", Cut(shortCommand, 2000) }, { "state", "run" } });
        }
        else if (type == "fileChange") {
            var names = Many(item, "changes").Select(one => Text(one, "path")).Where(one => one.Length > 0).Select(one => one.Substring(one.LastIndexOfAny(new[] { '/', '\\' }) + 1)).ToList();
            Item(id, "step", "修改：" + (names.Count > 0 ? String.Join("、", names.Take(3)) + (names.Count > 3 ? " 等 " + names.Count + " 个文件" : "") : "文件"), new Dictionary<string, object> { { "name", "edit" }, { "state", "run" } });
        }
        else if (type == "mcpToolCall") Item(id, "step", Text(item, "server") + "：" + Text(item, "tool"), new Dictionary<string, object> { { "name", "mcp" }, { "state", "run" } });
        else if (type == "webSearch") Item(id, "step", "搜索：" + Text(item, "query"), new Dictionary<string, object> { { "name", "search" }, { "state", "run" } });
        else if (type == "dynamicToolCall" || type == "collabAgentToolCall") Item(id, "step", Text(item, "tool"), new Dictionary<string, object> { { "name", "tool" }, { "state", "run" } });
    }
    void Completed(object item) {
        string type = Text(item, "type"), id = Text(item, "id");
        if (type == "commandExecution") {
            object code = At(item, "exitCode"); string status = Text(item, "status");
            string back = Text(item, "aggregatedOutput");
            bool ok = status == "completed" && (code == null || Convert.ToString(code) == "0");
            Done(id, ok, (code != null && Convert.ToString(code) != "0" ? "（退出码 " + code + "）\n" : status == "declined" ? "（已拒绝）\n" : "") + back);
        }
        else if (type == "fileChange") {
            var diffs = Many(item, "changes").Select(one => Text(one, "path") + "\n" + Text(one, "diff")).ToList();
            Done(id, Text(item, "status") == "completed", String.Join("\n", diffs));
        }
        else if (type == "mcpToolCall") Done(id, Text(item, "status") == "completed", At(item, "error") != null ? Text(At(item, "error"), "message") : json.Serialize(At(item, "result") ?? ""));
        else if (type == "webSearch" || type == "dynamicToolCall" || type == "collabAgentToolCall") Done(id, Text(item, "status") != "failed", "");
        else if (type == "agentMessage") Settled(id, Text(item, "text"));
        else if (type == "reasoning" && !thoughts.Contains(id)) {
            string summary = String.Join("\n\n", Many(item, "summary").Select(one => one as string ?? Text(one, "text")).Where(one => !String.IsNullOrWhiteSpace(one)));
            if (summary.Length > 0) { thoughts.Add(id); Item(id, "thought", summary); }
        }
    }
    void Requested(object id, string method, object p) {
        var request = new Dictionary<string, object> { { "id", id }, { "method", method }, { "params", p } };
        if (method == "item/commandExecution/requestApproval" || method == "execCommandApproval") {
            string command = Text(p, "command"); var actions = Many(p, "commandActions").Select(one => Text(one, "command")).Where(one => one.Length > 0).ToList();
            string reason = Text(p, "reason");
            Ask(request, "command", "运行命令", (reason.Length > 0 ? reason + "\n" : "") + (actions.Count > 0 ? String.Join("\n", actions) : command), true);
        }
        else if (method == "item/fileChange/requestApproval" || method == "applyPatchApproval") {
            string reason = Text(p, "reason");
            Ask(request, "edit", "修改文件", reason.Length > 0 ? reason : Text(p, "grantRoot"), true);
        }
        else if (method == "item/tool/requestUserInput") {
            var questions = Many(p, "questions").ToList();
            var labels = questions.Count == 1 ? Many(questions[0], "options").Select(one => Text(one, "label")).Where(one => one.Length > 0).ToList() : new List<string>();
            if (labels.Count > 0) Ask(request, "question", Text(questions[0], "question"), "", false, labels);
            else Refuse(id, "手机上不能回答这个问题");
        }
        else Refuse(id, "Remote AI 不支持这个请求");
    }
    void Refuse(object id, string why) {
        Send(new Dictionary<string, object> { { "id", id }, { "error", new Dictionary<string, object> { { "code", -32601 }, { "message", why } } } });
    }
    protected override void Reply(Asked asked, string decision) {
        var request = (Dictionary<string, object>)asked.Request;
        string method = Text(request, "method");
        object result;
        if (decision.StartsWith("choice:", StringComparison.Ordinal)) {
            var question = Many(At(request, "params"), "questions").First();
            result = new Dictionary<string, object> { { "answers", new Dictionary<string, object> { { Text(question, "id"), new Dictionary<string, object> { { "answers", new[] { asked.Choices[Int32.Parse(decision.Substring(7))] } } } } } } };
        }
        else if (method == "item/tool/requestUserInput") result = new Dictionary<string, object> { { "answers", new Dictionary<string, object>() } };
        else if (method == "execCommandApproval" || method == "applyPatchApproval") result = new Dictionary<string, object> { { "decision", decision == "deny" ? "denied" : decision == "always" ? "approved_for_session" : "approved" } };
        else result = new Dictionary<string, object> { { "decision", decision == "deny" ? "decline" : decision == "always" ? "acceptForSession" : "accept" } };
        Send(new Dictionary<string, object> { { "id", At(request, "id") }, { "result", result } });
    }
}
}
