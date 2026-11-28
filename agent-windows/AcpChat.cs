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
/// - `session/request_permission` asks this program whether a step may run, with the tool's own options;
/// - the models a session offers come with `session/new` (`configOptions` of category `model`, or the earlier
///   `models.availableModels`); one asked for is chosen before the first prompt (`session/set_config_option`, or
///   `session/set_model`), and a session that cannot take it runs nothing (Remote AI does not fall back to another);
/// - the thinking levels come the same way (`configOptions` of category `thought_level`, agy 1.5.13: low, medium, high,
///   xhigh; the same for each of its models): one asked for is chosen after the model, before the first prompt. A
///   level the session does not offer is refused like a model; a tool that has no levels at all runs at its own.
/// The conversation's id is the tool's own: it is listed with the tool's other conversations and can be gone on with
/// in a terminal. ACP has no way to add words to a turn that runs: what is said meanwhile waits for its end.
public class AcpChat : JsonRpcChat {
    readonly string args;
    sealed class Queued {
        public string Text, Id;
        public IList<Attachment> Attachments;
    }
    readonly Queue<Queued> queued = new Queue<Queued>();     // said while a turn runs, or before the conversation was there
    bool working, loading;
    bool nativeImages;
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
        object[] servers = mcp != null && !String.IsNullOrEmpty(mcp.Command)
            ? new object[] { new Dictionary<string, object> { { "name", mcp.Name }, { "command", mcp.Command }, { "args", mcp.Args }, { "env", new object[0] } } }
            : new object[0];
        var given = new Dictionary<string, object> { { "cwd", Folder }, { "mcpServers", servers } };
        if (session.Length > 0) given["sessionId"] = session;
        Call("initialize", new Dictionary<string, object> {
            { "protocolVersion", 1 },
            { "clientCapabilities", new Dictionary<string, object> { { "fs", new Dictionary<string, object> { { "readTextFile", false }, { "writeTextFile", false } } }, { "terminal", false } } },
            { "clientInfo", new Dictionary<string, object> { { "name", "remote-ai" }, { "title", "Remote AI" }, { "version", "1" } } } }, initAnswer => {
            if (At(initAnswer, "error") != null) { Unopened(Label + " 初始化失败：" + Text(At(initAnswer, "error"), "message"), queued.Count > 0); queued.Clear(); return; }
            nativeImages = true.Equals(At(At(At(At(initAnswer, "result"), "agentCapabilities"), "promptCapabilities"), "image"));
            if (session.Length > 0) loading = true;
            Call(session.Length > 0 ? "session/load" : "session/new", given, answer => {
                loading = false;
                string id = session.Length > 0 && At(answer, "error") == null ? session : Text(At(answer, "result"), "sessionId");
                if (id.Length == 0) { Unopened(Label + " 没有打开这段对话：" + Text(At(answer, "error"), "message"), queued.Count > 0); queued.Clear(); return; }
                if (listing) { Offer(Offered(At(answer, "result"))); return; }
                Known(id);
                if (model.Length == 0 && effort.Length == 0) { Ready(); return; }
                Configure(At(answer, "result"), refused => {
                    if (refused == null) { Ready(); return; }
                    // not begun with another model than the one asked for: what waits for it fails, and so does what follows
                    this.refused = refused;
                    Note(refused);
                    if (queued.Count > 0) { queued.Clear(); TurnEnded("failed", refused); }
                    State("idle");
                });
            });
        });
    }
    void Ready() { if (queued.Count > 0) Prompt(); else State("idle"); }
    string Label { get { return RemoteCli.Tool.LabelOf(Tool); } }
    string refused;     // why the model asked for could not be chosen

    /// The models a session offers: ACP's `configOptions` entry of category `model` (its options' values, also in
    /// groups), else the earlier `models.availableModels` (modelId, name); each with the thinking levels of the session.
    static List<Models.Choice> Offered(object result) {
        var config = ModelOption(result);
        var levels = Option(result, "thought_level");
        string[] efforts = levels == null ? new string[0] : Values(levels).Select(one => one.Id).ToArray();
        string current = levels == null ? "" : Text(levels, "currentValue");
        var offered = config != null ? Values(config).ToList()
            : Many(At(result, "models"), "availableModels").Select(one => new Models.Choice { Id = Text(one, "modelId"), Label = Text(one, "name") }).ToList();
        foreach (var one in offered) { one.Efforts = efforts; one.DefaultEffort = current; }
        return offered;
    }
    static object Option(object result, string category) { return Many(result, "configOptions").FirstOrDefault(one => Text(one, "category") == category); }
    static object ModelOption(object result) { return Option(result, "model"); }
    /// The model asked for, then the thinking level; `then` is given null once both are chosen, or why one was not.
    void Configure(object opened, Action<string> then) {
        if (model.Length == 0) { Think(opened, then); return; }
        Choose(opened, (refused, latest) => { if (refused != null) then(refused); else Think(At(latest, "configOptions") != null ? latest : opened, then); });
    }
    void Think(object result, Action<string> then) {
        if (effort.Length == 0) { then(null); return; }
        var levels = Option(result, "thought_level");
        if (levels == null) { Note(Label + " 不能设置思考深度，按它自己的设置运行"); then(null); return; }
        if (!Values(levels).Any(one => one.Id == effort)) { then(Label + " 没有提供思考深度 " + effort); return; }
        Call("session/set_config_option", new Dictionary<string, object> { { "sessionId", Session }, { "configId", Text(levels, "id") }, { "value", effort } }, answer => {
            var error = At(answer, "error");
            then(error == null ? null : Label + " 不能使用思考深度 " + effort + "：" + Text(error, "message"));
        });
    }
    static IEnumerable<Models.Choice> Values(object option) {
        foreach (var one in Many(option, "options")) {
            if (At(one, "options") != null) { foreach (var inner in Values(one)) yield return inner; }
            else yield return new Models.Choice { Id = Text(one, "value"), Label = Text(one, "name") };
        }
    }
    /// Chooses the model asked for, the way the session says it can be chosen (`session/set_config_option`, else
    /// `session/set_model`); `then` is given null once it is chosen, or why it was not, and the answer's result (the
    /// session's options as they are now, where it says them).
    void Choose(object result, Action<string, object> then) {
        var config = ModelOption(result);
        var offered = Offered(result);
        if (offered.Count > 0 && !offered.Any(one => one.Id == model)) { then(Label + " 没有提供模型 " + model, null); return; }
        Action<Dictionary<string, object>> answered = answer => {
            var error = At(answer, "error");
            then(error == null ? null : Label + " 不能使用模型 " + model + "：" + Text(error, "message"), At(answer, "result"));
        };
        if (config != null) Call("session/set_config_option", new Dictionary<string, object> { { "sessionId", Session }, { "configId", Text(config, "id") }, { "value", model } }, answered);
        else Call("session/set_model", new Dictionary<string, object> { { "sessionId", Session }, { "modelId", model } }, answered);
    }

    public override void Say(string text, string id, IList<Attachment> attachments = null) {
        lock (gate) {
            if (Closed || Status == "ended") throw new InvalidOperationException("对话已结束");
            if (refused != null) throw new InvalidOperationException(refused);
            queued.Enqueue(new Queued { Text = text, Id = id ?? "", Attachments = attachments });
            if (Session.Length == 0) { State("busy"); return; }
            if (!working) Prompt();
            else if (!stopping) Note("\u6d88\u606f\u5c06\u5728\u8fd9\u4e00\u8f6e\u7ed3\u675f\u540e\u5408\u5e76\u53d1\u51fa");
        }
    }
    // All messages waiting in the queue go together in one turn so stacked follow-ups never wait turn by turn.
    void Prompt() {
        var batch = new List<Queued>();
        while (queued.Count > 0) batch.Add(queued.Dequeue());
        if (batch.Count == 0) return;
        working = true; writing = thinking = "";
        var texts = new List<string>();
        var allAttachments = new List<Attachment>();
        var ops = new List<string>();
        foreach (var one in batch) {
            string piece = Attachments.WithText(one.Text, one.Attachments);
            if (piece.Length > 0) texts.Add(piece);
            if (one.Attachments != null) allAttachments.AddRange(one.Attachments);
            if (one.Id.Length > 0) ops.Add(one.Id);
        }
        string shown = String.Join("\n\n", texts.ToArray());
        Dictionary<string, object> extra = null;
        if (ops.Count > 0) {
            extra = new Dictionary<string, object> { { "op", ops[0] } };
            if (ops.Count > 1) extra["ops"] = ops.ToArray();
        }
        Item("u" + Guid.NewGuid().ToString("N").Substring(0, 12), "user", shown, extra);
        State("busy");
        object prompt = nativeImages && allAttachments.Count > 0
            ? (object)Attachments.AcpContent(shown, allAttachments)
            : new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", shown } } };
        Call("session/prompt", new Dictionary<string, object> { { "sessionId", Session }, { "prompt", prompt } }, answer => {
            working = false; writing = thinking = "";
            var error = At(answer, "error");
            string reason = Text(At(answer, "result"), "stopReason");
            string how = error != null ? "failed" : reason == "cancelled" ? "stopped" : reason == "end_turn" ? "done" : reason == "refusal" ? "failed" : "done";
            string errText = error != null ? Text(error, "message") : reason == "max_tokens" || reason == "max_turn_requests" ? "这一轮达到了长度上限" : reason == "refusal" ? "工具拒绝了这个请求" : "";
            if (how == "stopped" && queued.Count > 0) {
                TurnEndedQuiet("stopped", "");
                Prompt();
                return;
            }
            TurnEnded(how, errText);
            if (asks.Count > 0) State("confirm");
            else if (queued.Count > 0) Prompt();
            else State("idle");
        });
    }
    public override void Stop() {
        lock (gate) {
            if (!working) throw new InvalidOperationException("它现在没有在执行");
            stopping = true;
            queued.Clear();
            Notify("session/cancel", new Dictionary<string, object> { { "sessionId", Session } });
        }
        CancelConfirmations();
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
            string kind = Text(u, "kind"), title = Text(u, "title"), rawName = Text(u, "name");
            var input = At(u, "rawInput") ?? At(u, "args");
            string own = OwnTool(rawName, title, input), detail = "";
            string name = own.Length > 0 ? "mcp" : kind == "execute" ? "shell" : kind == "edit" || kind == "delete" || kind == "move" ? "edit" : kind == "search" || kind == "fetch" ? "search" : kind == "read" ? "read" : "tool";
            string shown = own.Length > 0 ? Peers.StepOf(own, input, out detail) : Titled(kind, title, rawName, detail = Command(u));
            Item(id, "step", Cut(shown, 200), new Dictionary<string, object> { { "name", name }, { "detail", Cut(detail, 2000) }, { "state", "run" } });
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
    // What a step gave back, as ACP carries it: text, a file's change, or MCP rawOutput
    static string Given(object u) {
        var parts = new List<string>();
        foreach (var one in Many(u, "content")) {
            string type = Text(one, "type");
            if (type == "content") { string text = Text(At(one, "content"), "text"); if (text.Length > 0) parts.Add(text); }
            else if (type == "text") { string text = Text(one, "text"); if (text.Length > 0) parts.Add(text); }
            else if (type == "diff") parts.Add(Text(one, "path") + "\n" + Text(one, "newText"));
        }
        if (parts.Count == 0) {
            object raw = At(u, "rawOutput");
            if (raw is string && ((string)raw).Length > 0) parts.Add((string)raw);
            else if (raw != null) {
                foreach (var one in Many(raw, "content")) { string text = Text(one, "text"); if (text.Length > 0) parts.Add(text); }
                if (parts.Count == 0) {
                    foreach (string key in new[] { "result", "output", "text", "message" }) { string text = Text(raw, key); if (text.Length > 0) { parts.Add(text); break; } }
                }
            }
        }
        return String.Join("\n", parts);
    }
    /// Which Remote AI tool (Peers.cs) a step calls: ACP carries its name in `name` or only in `title`, and some agents
    /// give neither, so the arguments of `ask_ai`, `start_collaboration` and `delegate_task` tell them apart too.
    static string OwnTool(string name, string title, object input) {
        string own = Peers.OwnTool(name);
        if (own.Length > 0) return own;
        foreach (string one in Peers.ToolNames.OrderByDescending(n => n.Length))
            if (title == one || title.IndexOf(one, StringComparison.Ordinal) >= 0) return one;
        if (Text(input, "question").Length > 0 && RemoteCli.Tool.Of(Text(input, "tool")) != null) return Peers.ToolName;
        if (Text(input, "task").Length > 0 && RemoteCli.Tool.Of(Text(input, "tool")) != null) return Peers.DelegateToolName;
        if (Text(input, "goal").Length > 0 && (Text(input, "mode") == "lead" || Text(input, "mode") == "review")) return Peers.CollabToolName;
        return "";
    }
    protected override void Requested(object id, string method, object p) {
        if (method == "session/request_permission") {
            var request = new Dictionary<string, object> { { "id", id }, { "params", p } };
            var call = At(p, "toolCall"); string kind = Text(call, "kind"), command = Command(call), title = Text(call, "title"), rawName = Text(call, "name");
            var callInput = At(call, "rawInput") ?? At(call, "args");
            if (mcp != null && OwnTool(rawName, title, callInput).Length > 0) {
                var options = Many(p, "options").ToList();
                var allow = options.FirstOrDefault(one => Text(one, "kind") == "allow_once") ?? options.FirstOrDefault(one => Text(one, "kind").StartsWith("allow", StringComparison.Ordinal));
                if (allow != null) {
                    RespondResult(id, new Dictionary<string, object> { { "outcome", new Dictionary<string, object> { { "outcome", "selected" }, { "optionId", Text(allow, "optionId") } } } });
                    return;
                }
            }
            bool always = Many(p, "options").Any(one => Text(one, "kind") == "allow_always");
            string heading = kind == "execute" ? "运行命令" : kind == "edit" || kind == "delete" || kind == "move" ? "修改文件" : kind == "read" ? "读取文件" : kind == "search" ? "搜索" : "使用工具";
            Ask(request, kind == "execute" ? "command" : kind == "edit" || kind == "delete" || kind == "move" ? "edit" : "tool",
                heading, command.Length > 0 ? command : title, always);
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
