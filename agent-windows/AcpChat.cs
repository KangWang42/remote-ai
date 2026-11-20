using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RemoteCli {
/// A conversation run through the Agent Client Protocol (ACP, agentclientprotocol.com): JSON-RPC 2.0, one message a
/// line, over the program's standard streams. Any tool that serves it is driven the same way; what differs is only
/// how its program is started (`args`). Tried on Antigravity CLI 1.5.12 (`agy acp`, docs/PROTOCOL.md, "Chats"):
/// - `initialize`, then `session/new` (cwd) or `session/load` (an id the tool knows; it replays what was said, which
///   is passed over here: the phone is shown that from the tool's own record, TerminalAgent.Recall);
/// - `session/prompt` runs a turn and is answered when it ends, with its `stopReason`; meanwhile `session/update`
///   notifications tell what is written (agent_message_chunk, agent_thought_chunk) and each step (tool_call,
///   tool_call_update);
/// - `session/cancel` (a notification) ends the turn that runs: the prompt is answered `cancelled`;
/// - `session/request_permission` asks this program whether a step may run, with the tool's own options.
/// The conversation's id is the tool's own: it is listed with the tool's other conversations and can be gone on with
/// in a terminal. ACP has no way to add words to a turn that runs: what is said meanwhile waits for its end.
public class AcpChat : JsonRpcChat {
    readonly string args;
    readonly Queue<KeyValuePair<string, string>> queued = new Queue<KeyValuePair<string, string>>();     // said while a turn runs, or before the conversation was there
    bool working, loading;
    // the text and the thought being written now: a new one begins where anything else came between
    string writing = "", thinking = "";
    readonly HashSet<string> steps = new HashSet<string>();

    public AcpChat(string tool, string folder, Action<string> emit, string args) : base(tool, folder, emit) { this.args = args; }
    protected override bool UseJsonRpcVersion { get { return true; } }

    protected override void Begin(string launcher, string session, bool resume, string mode) {
        if (!resume) session = "";
        if (session.Length > 0 && !SessionLocks.IsUuid(session)) throw new ArgumentException("对话编号无效");
        Run(launcher, args);
        // nothing of this computer's files or terminals is offered: the tool works in its folder as it would alone
        Call("initialize", new Dictionary<string, object> {
            { "protocolVersion", 1 },
            { "clientCapabilities", new Dictionary<string, object> { { "fs", new Dictionary<string, object> { { "readTextFile", false }, { "writeTextFile", false } } }, { "terminal", false } } },
            { "clientInfo", new Dictionary<string, object> { { "name", "remote-ai" }, { "title", "Remote AI" }, { "version", "1" } } } }, _ => { });
        var given = new Dictionary<string, object> { { "cwd", Folder }, { "mcpServers", new object[0] } };
        if (session.Length > 0) { given["sessionId"] = session; loading = true; }
        Call(session.Length > 0 ? "session/load" : "session/new", given, answer => {
            loading = false;
            string id = session.Length > 0 && At(answer, "error") == null ? session : Text(At(answer, "result"), "sessionId");
            if (id.Length == 0) { Note(Label + " 没有打开这段对话：" + Text(At(answer, "error"), "message")); State("ended"); return; }
            Known(id);
            if (queued.Count > 0) Prompt(); else State("idle");
        });
    }
    string Label { get { return RemoteCli.Tool.LabelOf(Tool); } }

    public override void Say(string text, string id) {
        lock (gate) {
            queued.Enqueue(new KeyValuePair<string, string>(text, id ?? ""));
            if (Session.Length == 0) { State("busy"); return; }
            if (!working) Prompt();
            else Note("消息将在这一轮结束后发出");
        }
    }
    // The next thing said goes as a turn of its own; what waits goes after it, one by one.
    void Prompt() {
        var said = queued.Dequeue();
        working = true; writing = thinking = "";
        Item("u" + Guid.NewGuid().ToString("N").Substring(0, 12), "user", said.Key, said.Value.Length > 0 ? new Dictionary<string, object> { { "op", said.Value } } : null);
        State("busy");
        Call("session/prompt", new Dictionary<string, object> { { "sessionId", Session }, { "prompt", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", said.Key } } } } }, answer => {
            working = false; writing = thinking = "";
            var error = At(answer, "error");
            string reason = Text(At(answer, "result"), "stopReason");
            TurnEnded(error != null ? "failed" : reason == "cancelled" ? "stopped" : reason == "end_turn" ? "done" : reason == "refusal" ? "failed" : "done",
                error != null ? Text(error, "message") : reason == "max_tokens" || reason == "max_turn_requests" ? "这一轮达到了长度上限" : reason == "refusal" ? "工具拒绝了这个请求" : "");
            if (asks.Count > 0) State("confirm");
            else if (queued.Count > 0 && !stopping) Prompt();
            else State("idle");
        });
    }
    public override void Stop() {
        lock (gate) {
            if (!working) throw new InvalidOperationException("它现在没有在执行");
            stopping = true;
            Notify("session/cancel", new Dictionary<string, object> { { "sessionId", Session } });
        }
    }

    protected override void Notified(string method, object p) {
        if (method != "session/update" || loading) return;       // (what a loaded conversation said is replayed: passed over)
        var u = At(p, "update");
        switch (Text(u, "sessionUpdate")) {
            case "agent_message_chunk": {
                string text = Text(At(u, "content"), "text");
                if (text.Length == 0) break;
                if (writing.Length == 0) { writing = "m" + Guid.NewGuid().ToString("N").Substring(0, 12); thinking = ""; Item(writing, "text", ""); }
                Append("more", writing, text);
                break;
            }
            case "agent_thought_chunk": {
                string text = Text(At(u, "content"), "text");
                if (text.Length == 0) break;
                if (thinking.Length == 0) { thinking = "t" + Guid.NewGuid().ToString("N").Substring(0, 12); writing = ""; Item(thinking, "thought", ""); }
                Append("more", thinking, text);
                break;
            }
            case "tool_call": Step(u); break;
            case "tool_call_update": Step(u); break;
        }
    }
    // A step, as it starts and as it goes on; once it is over, how it ended and what it gave back.
    void Step(object u) {
        string id = Text(u, "toolCallId"), status = Text(u, "status");
        if (id.Length == 0) return;
        writing = thinking = "";
        if (!steps.Contains(id)) {
            steps.Add(id);
            string kind = Text(u, "kind"), title = Text(u, "title");
            string name = kind == "execute" ? "shell" : kind == "edit" || kind == "delete" || kind == "move" ? "edit" : kind == "search" || kind == "fetch" ? "search" : kind == "read" ? "read" : "tool";
            string detail = Command(u);
            Item(id, "step", Cut(Titled(kind, title, Text(u, "name"), detail), 200), new Dictionary<string, object> { { "name", name }, { "detail", Cut(detail, 2000) }, { "state", "run" } });
        }
        string back = Given(u);
        if (status == "completed" || status == "failed") Done(id, status == "completed", back);
        else if (back.Length > 0) Append("out", id, back);
    }
    static string Command(object u) {
        var input = At(u, "rawInput") ?? At(u, "args");
        foreach (string key in new[] { "CommandLine", "command", "Command", "cmd" }) { string one = Text(input, key); if (one.Length > 0) return one; }
        return "";
    }
    static string Titled(string kind, string title, string name, string command) {
        if (kind == "execute" && command.Length > 0) return "运行：" + command;
        if (kind == "read") return "读取：" + Short(title, "File ");
        if (kind == "edit") return "修改：" + Short(title, "File ");
        if (kind == "search") return "搜索：" + title;
        return title.Length > 0 ? title : name;
    }
    // "File /C:/x/y.txt" said as y.txt
    static string Short(string title, string lead) {
        string rest = title.StartsWith(lead, StringComparison.Ordinal) ? title.Substring(lead.Length) : title;
        int cut = rest.LastIndexOfAny(new[] { '/', '\\' });
        return cut >= 0 && cut < rest.Length - 1 ? rest.Substring(cut + 1) : rest;
    }
    // What a step gave back, as ACP carries it: text, or a file's change
    static string Given(object u) {
        var parts = new List<string>();
        foreach (var one in Many(u, "content")) {
            string type = Text(one, "type");
            if (type == "content") { string text = Text(At(one, "content"), "text"); if (text.Length > 0) parts.Add(text); }
            else if (type == "diff") parts.Add(Text(one, "path") + "\n" + Text(one, "newText"));
        }
        return String.Join("\n", parts);
    }
    protected override void Requested(object id, string method, object p) {
        if (method == "session/request_permission") {
            var request = new Dictionary<string, object> { { "id", id }, { "params", p } };
            var call = At(p, "toolCall"); string kind = Text(call, "kind"), command = Command(call), title = Text(call, "title");
            bool always = Many(p, "options").Any(one => Text(one, "kind") == "allow_always");
            Ask(request, kind == "execute" ? "command" : kind == "edit" || kind == "delete" || kind == "move" ? "edit" : "tool",
                kind == "execute" ? "运行命令" : kind == "edit" ? "修改文件" : "使用工具", command.Length > 0 ? command : title, always);
            return;
        }
        // nothing of this computer's files or terminals was offered (initialize)
        Refuse(id, "Remote AI 不支持这个请求");
    }
    protected override void Reply(Asked asked, string decision) {
        var request = (Dictionary<string, object>)asked.Request;
        var options = Many(At(request, "params"), "options").ToList();
        string wanted = decision == "always" ? "allow_always" : decision == "allow" ? "allow_once" : "reject_once";
        var chosen = options.FirstOrDefault(one => Text(one, "kind") == wanted)
            ?? options.FirstOrDefault(one => Text(one, "kind").StartsWith(decision == "deny" ? "reject" : "allow", StringComparison.Ordinal));
        object outcome = chosen == null
            ? (object)new Dictionary<string, object> { { "outcome", "cancelled" } }
            : new Dictionary<string, object> { { "outcome", "selected" }, { "optionId", Text(chosen, "optionId") } };
        RespondResult(At(request, "id"), new Dictionary<string, object> { { "outcome", outcome } });
    }
}

/// Antigravity CLI's conversation, run by `agy acp`. Its own way of working stays (as in its terminals): it asks
/// where its permission settings say it asks.
public sealed class AgyChat : AcpChat {
    public AgyChat(string folder, Action<string> emit) : base("agy", folder, emit, "acp") { }
}
}
