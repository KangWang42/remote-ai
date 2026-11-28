using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// Lets one conversation on this computer ask another AI tool in the same project for a read-only second opinion or
/// cross-review (`ask_ai`), delivered as a local MCP server over a named pipe. Each caller conversation gets its own
/// seat (token, project folder, and cached read-only peer chats for follow-ups); peer chats are opened without MCP so
/// consultation never recurses, and all peer chats and temporary config files are disposed when the caller chat ends.
/// A conversation the phone runs may also hand work to another AI (`delegate_task`, `task_status`, `task_cancel`,
/// `task_answer`, kept by Delegations.cs); a conversation that is such a task is not offered them.
public sealed class Peers : IDisposable {
    public const string ServerName = "remote_ai";
    public const string ToolName = "ask_ai";
    public const string CollabToolName = "start_collaboration";
    public const string DelegateToolName = "delegate_task", StatusToolName = "task_status", CancelToolName = "task_cancel", AnswerToolName = "task_answer";
    static readonly string[] TaskTools = { DelegateToolName, StatusToolName, CancelToolName, AnswerToolName };
    /// Every tool this server offers.
    public static readonly string[] ToolNames = { ToolName, CollabToolName, DelegateToolName, StatusToolName, CancelToolName, AnswerToolName };
    public const string Flag = "--mcp";
    public const int DefaultTimeoutSeconds = 240;
    /// Antigravity CLI hard-aborts MCP tool calls at 180s and spills outputs over ~10 KB UTF-8 to disk.
    public const int AgyTimeoutSeconds = 168;
    public const int AgyMaxAnswerBytes = 8600;
    const int MaxFrameBytes = 16 * 1024 * 1024;
    const int MaxQuestionChars = 16000;
    const int MaxAnswerChars = 24000;

    /// Host callbacks allowing an active standalone conversation to be promoted in-place into a collaboration task
    /// (`Teams.cs`) after explicit confirmation on the user's phone.
    public sealed class CollabHooks {
        public Func<string, bool> CanPromote;
        public Func<string, string, string, string, CancellationToken, bool> Confirm;
        public Func<string, Dictionary<string, object>, CollabStarted> Promote;
    }
    public sealed class CollabStarted {
        public string TeamId = "", Prompt = "";
    }

    static readonly Dictionary<string, string> Strengths = new Dictionary<string, string> {
        { "codex", "擅长决策拍板、架构权衡、疑难排查与代码审查" },
        { "claude", "擅长复杂实现、重构思路、文档撰写与深度交叉审查" },
        { "agy", "擅长 UI 与前端设计、长上下文检索与交叉核验" } };

    sealed class Consultant {
        public readonly object TurnGate = new object();
        public Chat Chat;
        public int Turns;
    }

    sealed class Seat {
        public string Token = "", CallerTool = "", Folder = "", ConfigFile = "", TerminalId = "", ProjectDir = "";
        /// The conversation is itself a task another one gave out: it does the work, it gives none out.
        public bool Child;
        public readonly object Gate = new object();
        public readonly Dictionary<string, Consultant> Consultants = new Dictionary<string, Consultant>();
        public bool Closed;
    }

    /// Pipe name for one data folder, matching Reach's per-installation naming.
    public static string PipeName(string dataFolder) {
        using (var sha = SHA256.Create())
            return "RemoteCli.peers." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(dataFolder).ToLowerInvariant())), 0, 6).Replace("-", "").ToLowerInvariant();
    }

    readonly string pipe;
    readonly Func<string, string> findTool;
    readonly Quotas quotas;
    readonly string configDir;
    readonly string program;
    readonly int timeoutSeconds;
    readonly CollabHooks collab;
    readonly Delegations delegations;
    readonly object gate = new object();
    readonly Dictionary<string, Seat> seats = new Dictionary<string, Seat>();
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    volatile bool disposed;

    public string Pipe { get { return pipe; } }

    public Peers(string pipeName, Func<string, string> findTool, Quotas quotas, string configDir, string program = null, int timeoutSeconds = DefaultTimeoutSeconds, CollabHooks collab = null, Delegations delegations = null) {
        pipe = pipeName;
        this.findTool = findTool ?? (_ => null);
        this.quotas = quotas;
        this.configDir = configDir;
        this.program = String.IsNullOrWhiteSpace(program) ? DefaultProgram() : program;
        this.timeoutSeconds = Math.Max(2, timeoutSeconds);
        this.collab = collab;
        this.delegations = delegations;
        new Thread(Listen) { IsBackground = true, Name = "peers" }.Start();
    }

    static string DefaultProgram() {
        string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RemoteCliAgent.exe");
        return File.Exists(beside) ? beside : null;
    }

    /// Offers `remote_ai` MCP tools (`ask_ai`; for project chats the phone runs `start_collaboration`; for chats the
    /// phone runs the task tools) to a conversation of `callerTool` working in `folder`. `child`: it is a task itself.
    public Chat.McpServer Offer(string callerTool, string folder, string terminalId = "", string projectDir = "", bool child = false) {
        if (disposed || String.IsNullOrEmpty(program) || !File.Exists(program) || String.IsNullOrEmpty(folder)) return null;
        bool hasPeers = AvailablePeers(callerTool).Length > 0;
        bool hasPromote = !child && collab != null && !String.IsNullOrEmpty(terminalId) && !String.IsNullOrEmpty(projectDir) && AvailableTools().Length > 0;
        bool hasTasks = !child && delegations != null && !String.IsNullOrEmpty(terminalId) && AvailableTools().Length > 0;
        if (!hasPeers && !hasPromote && !hasTasks) return null;
        string token = Guid.NewGuid().ToString("N");
        string[] args = { Flag, pipe, token };
        string configFile = WriteClaudeConfig(token, program, args);
        var seat = new Seat { Token = token, CallerTool = callerTool ?? "", Folder = folder, ConfigFile = configFile, TerminalId = terminalId ?? "", ProjectDir = projectDir ?? "", Child = child };
        lock (gate) {
            if (disposed) { DeleteQuietly(configFile); return null; }
            seats[token] = seat;
        }
        return new Chat.McpServer(ServerName, program, args, configFile, () => Withdraw(token));
    }

    string WriteClaudeConfig(string token, string cmd, string[] args) {
        if (String.IsNullOrEmpty(configDir)) return "";
        try {
            Directory.CreateDirectory(configDir);
            string file = Path.Combine(configDir, token + ".json");
            var payload = new Dictionary<string, object> {
                { "mcpServers", new Dictionary<string, object> {
                    { ServerName, new Dictionary<string, object> { { "command", cmd }, { "args", args } } } } } };
            File.WriteAllText(file, json.Serialize(payload), new UTF8Encoding(false));
            return file;
        } catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; }
    }

    void Withdraw(string token) {
        Seat seat;
        lock (gate) {
            if (!seats.TryGetValue(token, out seat)) return;
            seats.Remove(token);
        }
        CloseSeat(seat);
    }

    static void CloseSeat(Seat seat) {
        Chat[] open;
        lock (seat.Gate) {
            seat.Closed = true;
            open = seat.Consultants.Values.Select(c => c.Chat).Where(c => c != null).ToArray();
            seat.Consultants.Clear();
        }
        foreach (Chat chat in open) { try { chat.Dispose(); } catch (Exception) { } }
        DeleteQuietly(seat.ConfigFile);
    }

    static void DeleteQuietly(string file) {
        if (String.IsNullOrEmpty(file)) return;
        try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        Seat[] active;
        lock (gate) { active = seats.Values.ToArray(); seats.Clear(); }
        foreach (Seat seat in active) CloseSeat(seat);
        try { using (var poke = new NamedPipeClientStream(".", pipe, PipeDirection.InOut)) poke.Connect(100); } catch (Exception) { }
    }

    string[] AvailableTools() {
        return Tool.All.Where(t => t.Chats && !String.IsNullOrEmpty(findTool(t.Name))).Select(t => t.Name).ToArray();
    }
    string[] AvailablePeers(string callerTool) {
        return AvailableTools().Where(t => t != callerTool).ToArray();
    }
    bool CanDelegate(Seat seat) {
        return delegations != null && !seat.Child && !String.IsNullOrEmpty(seat.TerminalId) && AvailableTools().Length > 0;
    }
    bool CanPromoteSeat(Seat seat) {
        return !seat.Child && collab != null && collab.Promote != null && !String.IsNullOrEmpty(seat.TerminalId) && !String.IsNullOrEmpty(seat.ProjectDir)
            && AvailableTools().Length > 0 && (collab.CanPromote == null || collab.CanPromote(seat.TerminalId));
    }

    /// Tool definitions returned to MCP `tools/list`, with live peer availability, quota hints, and collaboration promotion.
    object[] ToolDefinitions(Seat seat) {
        var defs = new List<object>();
        string[] peers = AvailablePeers(seat.CallerTool);
        if (peers.Length > 0) {
            var lines = ToolLines(peers);
            string description = "向同一项目中的另一位 AI 只读请教或请求交叉审查（例如架构权衡、疑难排查、方案复核或代码审查）。"
                + "对方默认只读，只给意见、不修改文件；同一段对话内再次请教同一 AI 会延续上下文，传入 fresh=true 可开启新上下文。\n"
                + "当前可请教的 AI：\n" + String.Join("\n", lines)
                + "\n标记“紧张”的 AI 额度已用 80% 以上，尽量减少调用或保持问题简短。";
            var schema = new Dictionary<string, object> {
                { "type", "object" },
                { "properties", new Dictionary<string, object> {
                    { "tool", new Dictionary<string, object> { { "type", "string" }, { "enum", peers }, { "description", "要请教的 AI 工具名：" + String.Join(" / ", peers) } } },
                    { "question", new Dictionary<string, object> { { "type", "string" }, { "description", "具体问题、背景、涉及的文件路径及希望对方重点核验或拍板的点" } } },
                    { "fresh", new Dictionary<string, object> { { "type", "boolean" }, { "description", "为 true 时不延续本次对话里与该 AI 之前的请教记录，另起一段新的只读上下文（默认 false）" } } } } },
                { "required", new[] { "tool", "question" } },
                { "additionalProperties", false } };
            var annotations = new Dictionary<string, object> {
                { "title", "请教另一位 AI（只读）" },
                { "readOnlyHint", true },
                { "destructiveHint", false },
                { "idempotentHint", false },
                { "openWorldHint", false } };
            defs.Add(new Dictionary<string, object> {
                { "name", ToolName },
                { "description", description },
                { "inputSchema", schema },
                { "annotations", annotations } });
        }
        if (CanPromoteSeat(seat)) {
            string[] allTools = AvailableTools();
            string desc = "将当前对话原地升级为多 AI 协作任务（保留当前对话全部上下文，当前对话担任分工协作的领队或写审的写手）。"
                + "仅在用户明确要求开启协作任务时调用，不要未经用户请求擅自发起；只是把一项工作交给另一位 AI，用 delegate_task。调用时会先在用户手机上弹出确认卡片，用户允许后才正式开启。\n"
                + "默认方式为分工协作（mode=\"lead\"，自动组队，自由轮数）。请务必在 context 中精炼总结前期已与用户确认的背景、方案、约束、关键文件路径和已完成进展，以便系统同步给新加入的成员。\n"
                + "当前可用工具：" + String.Join(" / ", allTools.Select(t => t + "（" + Tool.LabelOf(t) + "）")) + "。";
            var schema = new Dictionary<string, object> {
                { "type", "object" },
                { "properties", new Dictionary<string, object> {
                    { "goal", new Dictionary<string, object> { { "type", "string" }, { "description", "本次协作要完成的总目标（1 至 4000 字）" } } },
                    { "mode", new Dictionary<string, object> { { "type", "string" }, { "enum", new[] { "lead", "review" } }, { "description", "协作方式：lead 为分工协作（默认，当前对话当领队）；review 为写审（当前对话当写手，另一个 AI 只读审查）" } } },
                    { "context", new Dictionary<string, object> { { "type", "string" }, { "description", "前期对话中已确认的需求细节、方案决策、约束边界、关键文件路径与已有进展；会自动同步给首次加入的成员" } } },
                    { "members", new Dictionary<string, object> { { "type", "array" }, { "items", new Dictionary<string, object> { { "type", "string" }, { "enum", allTools } } }, { "description", "仅 lead 模式选填：手动指定 1 至 3 个执行成员的工具名；省略则由领队自动组队" } } },
                    { "reviewer", new Dictionary<string, object> { { "type", "string" }, { "enum", allTools }, { "description", "仅 review 模式选填：负责只读审查的 AI 工具名；省略则自动选另一位可用 AI" } } },
                    { "rounds", new Dictionary<string, object> { { "type", "integer" }, { "description", "轮数上限：lead 模式默认 0（自由轮数，最多 20 轮），也可传 1–10；review 模式默认 3（1–10）" } } },
                    { "rules", new Dictionary<string, object> { { "type", "string" }, { "description", "选填：分工偏好或额外协作约定（最多 1000 字）" } } } } },
                { "required", new[] { "goal" } },
                { "additionalProperties", false } };
            var annotations = new Dictionary<string, object> {
                { "title", "将当前对话转为协作任务" },
                { "readOnlyHint", false },
                { "destructiveHint", false },
                { "idempotentHint", false },
                { "openWorldHint", false } };
            defs.Add(new Dictionary<string, object> {
                { "name", CollabToolName },
                { "description", desc },
                { "inputSchema", schema },
                { "annotations", annotations } });
        }
        if (CanDelegate(seat)) defs.AddRange(TaskToolDefinitions(AvailableTools()));
        return defs.ToArray();
    }

    /// One line per tool for the lists the AI reads: its name, what it is good at, and how much of its limits is left.
    List<string> ToolLines(IEnumerable<string> tools) {
        var lines = new List<string>();
        foreach (string tool in tools) {
            string strength = Strengths.ContainsKey(tool) ? "，" + Strengths[tool] : "";
            string quota = "";
            if (quotas != null) {
                Quotas.Window[] windows = null;
                try { windows = quotas.Of(tool); } catch (Exception) { }
                string room = Teams.Room(windows);
                if (room != "额度未知") quota = "；" + room + (room.EndsWith("紧张", StringComparison.Ordinal) ? "（非必要请勿调用）" : "");
            }
            lines.Add("- " + tool + "（" + Tool.LabelOf(tool) + strength + quota + "）");
        }
        return lines;
    }

    /// The task tools. Their descriptions are the rules the AI works by: hand work out where another tool's strength,
    /// parallel parts or a separate worktree help, do simple work itself, never wait on a task or poll it.
    object[] TaskToolDefinitions(string[] tools) {
        Func<Dictionary<string, object>, string[], Dictionary<string, object>> schema = (properties, required) => new Dictionary<string, object> {
            { "type", "object" }, { "properties", properties }, { "required", required }, { "additionalProperties", false } };
        Func<string, string, Dictionary<string, object>> text = (type, about) => new Dictionary<string, object> { { "type", type }, { "description", about } };
        Func<string, bool, bool, Dictionary<string, object>> marks = (title, readOnly, destructive) => new Dictionary<string, object> {
            { "title", title }, { "readOnlyHint", readOnly }, { "destructiveHint", destructive }, { "idempotentHint", readOnly }, { "openWorldHint", false } };
        var taskId = text("string", "delegate_task 返回的 task_id");
        string delegateAbout = "派一位 AI 在这台电脑上独立完成一项子任务。它是一段新的对话，看不到你的对话历史，task 里要写清背景、范围、负责的文件和要交回的验收证据。"
            + "调用会立即返回 task_id，子任务在后台进行，完成后结果会作为一条消息自动送回这段对话：不要查询或等待，继续做其它事，没有其它事就结束本轮回答。\n"
            + "适合：需要另一种工具的长处；几块互不依赖的工作要并行；需要独立的 Git 工作树（isolation=worktree，改动不进主目录）。\n"
            + "不适合：简单的活自己做，不为分工而拆分；同一工具、同一模型的小活用你自带的子代理或自己做；审查意见里的小问题自己改。"
            + "每次复审都派一个新的子任务，写明要点、已有发现和异议。用户明确要求“协作任务”时才用 start_collaboration。\n"
            + "子任务的权限不超过本对话；本对话被停止或结束时，子任务一并停止。\n"
            + "可派的 AI：\n" + String.Join("\n", ToolLines(tools));
        return new object[] {
            new Dictionary<string, object> { { "name", DelegateToolName }, { "description", delegateAbout },
                { "inputSchema", schema(new Dictionary<string, object> {
                    { "tool", new Dictionary<string, object> { { "type", "string" }, { "enum", tools }, { "description", "做这项子任务的 AI：" + String.Join(" / ", tools) } } },
                    { "task", text("string", "完整的任务说明：背景与已定约束、成品与范围（负责哪些文件、不碰哪些）、接口约定、已有线索、要交回的验收证据") },
                    { "title", text("string", "卡片上显示的简短标题，不超过 20 字") },
                    { "model", text("string", "选填：该工具提供的模型名；省略用工具默认模型") },
                    { "isolation", new Dictionary<string, object> { { "type", "string" }, { "enum", Delegations.Isolations }, { "description", "shared（默认）：在本项目目录里工作；worktree：在新的独立 Git 工作树里工作，改动不进主目录" } } } },
                    new[] { "tool", "task" }) },
                { "annotations", marks("派出子任务", false, false) } },
            new Dictionary<string, object> { { "name", StatusToolName },
                { "description", "读取你派出的一个子任务的状态和结果。只在送回的消息说“用 task_status 读取”或用户问起时用；不要用来轮询进行中的子任务。" },
                { "inputSchema", schema(new Dictionary<string, object> { { "task_id", taskId } }, new[] { "task_id" }) },
                { "annotations", marks("查看子任务", true, false) } },
            new Dictionary<string, object> { { "name", CancelToolName },
                { "description", "停止你派出的一个子任务：不再需要、要换人重做或用户要求少派时。已经结束的子任务保留结果。" },
                { "inputSchema", schema(new Dictionary<string, object> { { "task_id", taskId }, { "reason", text("string", "选填：停止的原因") } }, new[] { "task_id" }) },
                { "annotations", marks("停止子任务", false, true) } },
            new Dictionary<string, object> { { "name", AnswerToolName },
                { "description", "回答子任务提出的问题（送回的消息会列出问题和选项）。答案能从任务和用户的要求推出时才代答，推不出时先问用户。" },
                { "inputSchema", schema(new Dictionary<string, object> { { "task_id", taskId }, { "answer", text("string", "选项的序号（从 1 开始）或选项原文") } }, new[] { "task_id", "answer" }) },
                { "annotations", marks("回答子任务的问题", false, false) } } };
    }

    /// A call of a task tool, given to Delegations; what comes back is said in words the AI reads.
    Dictionary<string, object> TaskCall(Seat seat, string name, Dictionary<string, object> args) {
        if (!CanDelegate(seat)) return Fail("这段对话不能派出子任务");
        args = args ?? new Dictionary<string, object>();
        string id = Text(args, "task_id");
        Dictionary<string, object> back =
            name == DelegateToolName ? delegations.Start(seat.TerminalId, seat.CallerTool, args)
            : name == StatusToolName ? delegations.Status(seat.TerminalId, id)
            : name == CancelToolName ? delegations.Cancel(seat.TerminalId, id, Text(args, "reason"))
            : name == AnswerToolName ? delegations.Answer(seat.TerminalId, id, Convert.ToString(At(args, "answer") ?? ""))
            : Fail("不支持的工具：" + name);
        if (!true.Equals(At(back, "ok"))) return back;
        string said = Delegations.Said(name, back);
        if (String.Equals(seat.CallerTool, "agy", StringComparison.OrdinalIgnoreCase) && Encoding.UTF8.GetByteCount(said) > AgyMaxAnswerBytes)
            said = ProcessText.FoldHeadTailUtf8(said, AgyMaxAnswerBytes);
        return new Dictionary<string, object> { { "ok", true }, { "text", said } };
    }

    Dictionary<string, object> Ask(Seat seat, string peerTool, string question, bool fresh, CancellationToken cancel) {
        peerTool = (peerTool ?? "").Trim().ToLowerInvariant();
        question = (question ?? "").Trim();
        if (question.Length == 0) return Fail("请提供要请教的具体问题");
        if (question.Length > MaxQuestionChars) question = ProcessText.TruncateSafe(question, MaxQuestionChars, "\n…（问题过长，后文已省略）");
        Tool kind = Tool.Of(peerTool);
        if (kind == null || !kind.Chats) return Fail("不支持请教工具：" + peerTool);
        if (peerTool == seat.CallerTool) return Fail("不能请教自己，请选择另一位 AI");
        string launcher = findTool(peerTool);
        if (String.IsNullOrEmpty(launcher)) return Fail("电脑上没有安装 " + kind.Label);

        Consultant consultant;
        lock (seat.Gate) {
            if (seat.Closed || disposed) return Fail("原对话已结束");
            if (!seat.Consultants.TryGetValue(peerTool, out consultant)) seat.Consultants[peerTool] = consultant = new Consultant();
        }

        int effectiveTimeout = String.Equals(seat.CallerTool, "agy", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(timeoutSeconds, AgyTimeoutSeconds)
            : timeoutSeconds;

        lock (consultant.TurnGate) {
            if (seat.Closed || disposed || cancel.IsCancellationRequested) return Fail("请教已取消");
            if (fresh && consultant.Chat != null) {
                try { consultant.Chat.Dispose(); } catch (Exception) { }
                consultant.Chat = null; consultant.Turns = 0;
            }
            if (consultant.Chat == null || consultant.Chat.Closed) {
                consultant.Turns = 0;
                try { consultant.Chat = OpenReadOnly(peerTool, launcher, seat.Folder); }
                catch (Exception ex) { return Fail("未能启动 " + kind.Label + "：" + ex.Message); }
            }
            Chat chat = consultant.Chat;
            string snapshot = consultant.Turns == 0 ? WorkspaceSnapshot.Build(seat.Folder) : "";
            string snapshotBlock = snapshot.Length > 0 ? "\n\n【工作区快照（只读）】\n" + snapshot : "";
            string prompt = consultant.Turns == 0
                ? "【只读请教】同一项目里的另一位 AI（" + Tool.LabelOf(seat.CallerTool) + "）向你请教以下问题。请只读查看所需文件并直接给出简明、具体、可操作的意见；不要修改任何文件，不要执行会改动状态的命令，也不要等待额外确认。" + snapshotBlock + "\n\n" + question
                : "【继续请教】\n" + question;
            consultant.Turns++;

            var done = new ManualResetEventSlim(false);
            string endHow = "", endSaid = "";
            Action<string, string> onTurned = (how, said) => { endHow = how; endSaid = said ?? ""; done.Set(); };
            chat.Turned += onTurned;
            try {
                try { chat.Say(prompt, null); }
                catch (Exception ex) {
                    ResetConsultant(consultant);
                    return Fail(kind.Label + " 没有收下问题：" + ex.Message);
                }
                DateTime deadline = DateTime.UtcNow.AddSeconds(effectiveTimeout);
                while (!done.Wait(200)) {
                    if (seat.Closed || disposed || cancel.IsCancellationRequested) {
                        try { chat.Stop(); } catch (Exception) { }
                        return Fail("请教已取消");
                    }
                    if (chat.Closed) {
                        ResetConsultant(consultant);
                        string why = !String.IsNullOrEmpty(chat.LastError) ? chat.LastError : "已提前退出";
                        return Fail(kind.Label + " " + why);
                    }
                    if (DateTime.UtcNow >= deadline) {
                        try { chat.Stop(); } catch (Exception) { }
                        ResetConsultant(consultant);
                        return Fail(kind.Label + " 回答超时（超过 " + effectiveTimeout + " 秒）");
                    }
                }
            } finally {
                chat.Turned -= onTurned;
                done.Dispose();
            }

            if (endHow != "done") {
                string err = !String.IsNullOrEmpty(chat.LastError) ? chat.LastError : endHow == "stopped" ? "被停止" : "执行出错";
                if (chat.Closed) ResetConsultant(consultant);
                return Fail(kind.Label + " 未能完成回答：" + err);
            }
            string answer = (endSaid ?? "").Trim();
            if (answer.Length == 0) return Fail(kind.Label + " 没有给出文字回答");
            if (String.Equals(seat.CallerTool, "agy", StringComparison.OrdinalIgnoreCase) && Encoding.UTF8.GetByteCount(answer) > AgyMaxAnswerBytes)
                answer = ProcessText.FoldHeadTailUtf8(answer, AgyMaxAnswerBytes);
            else if (answer.Length > MaxAnswerChars)
                answer = ProcessText.FoldHeadTail(answer, MaxAnswerChars);
            return new Dictionary<string, object> { { "ok", true }, { "text", answer } };
        }
    }

    static void ResetConsultant(Consultant consultant) {
        if (consultant.Chat != null) { try { consultant.Chat.Dispose(); } catch (Exception) { } }
        consultant.Chat = null; consultant.Turns = 0;
    }

    /// Opens a strictly read-only peer chat with no MCP server (preventing recursion) and automatic permission
    /// answers (allowing only read/search inspection, denying all edits, commands, plans, and user prompts).
    Chat OpenReadOnly(string peerTool, string launcher, string folder) {
        Chat chat = Chat.Open(peerTool, launcher, folder, "", false, "read", _ => { }, quotas, null);
        chat.AskedFor += (askId, kind, title, text) => {
            string decision = (title == "读取文件" || title == "搜索" || kind == "read" || kind == "search") ? "allow" : "deny";
            try { chat.Answer(askId, decision); } catch (Exception) { }
        };
        return chat;
    }

    Dictionary<string, object> StartCollaboration(Seat seat, Dictionary<string, object> args, CancellationToken cancel) {
        if (!CanPromoteSeat(seat)) return Fail("当前对话无法转为协作任务（可能已在协作中、不在项目目录或已结束）");
        args = args ?? new Dictionary<string, object>();
        string goal = Text(args, "goal").Trim();
        if (goal.Length == 0) return Fail("请提供协作目标（goal）");
        if (goal.Length > 4000) goal = goal.Substring(0, 4000);
        string mode = Text(args, "mode").Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = "lead";
        if (mode != "lead" && mode != "review") return Fail("对话转为协作仅支持分工协作（lead）或写审（review）");
        string context = Text(args, "context").Trim();
        if (context.Length > 6000) context = context.Substring(0, 6000);
        string rules = Text(args, "rules").Trim();
        if (rules.Length > 1000) rules = rules.Substring(0, 1000);

        string[] available = AvailableTools();
        if (!available.Contains(seat.CallerTool)) return Fail("当前工具不支持参与协作");

        int rounds = mode == "lead" ? 0 : 3;
        object rawRounds = At(args, "rounds");
        if (rawRounds != null) {
            int parsed;
            if (!Int32.TryParse(Convert.ToString(rawRounds, CultureInfo.InvariantCulture), out parsed)) return Fail("轮数无效");
            if (mode == "lead") {
                if (parsed != 0 && (parsed < 1 || parsed > Teams.MostRounds)) return Fail("分工协作轮数需为 0（自由轮数）或 1 至 10");
            } else {
                if (parsed < 1 || parsed > Teams.MostRounds) return Fail("写审轮数需在 1 至 10 之间");
            }
            rounds = parsed;
        }

        var item = new Dictionary<string, object> {
            { "id", Guid.NewGuid().ToString("N") },
            { "mode", mode },
            { "dir", seat.ProjectDir },
            { "goal", goal },
            { "rounds", rounds }
        };
        if (context.Length > 0) item["context"] = context;
        if (rules.Length > 0) item["rules"] = rules;

        var detail = new StringBuilder();
        if (mode == "lead") {
            var members = new List<string>();
            object rawMembers = At(args, "members");
            var memberEnum = rawMembers as IEnumerable;
            if (memberEnum != null && !(rawMembers is string)) {
                foreach (object one in memberEnum) {
                    string t = Convert.ToString(one ?? "").Trim().ToLowerInvariant();
                    if (t.Length == 0) continue;
                    if (!available.Contains(t)) return Fail("电脑上不可用的协作成员工具：" + t);
                    members.Add(t);
                }
            }
            if (members.Count > 3) return Fail("最多指定 3 个执行成员");
            bool autoRoster = members.Count == 0;
            var roster = new List<Dictionary<string, object>> {
                new Dictionary<string, object> { { "role", "leader" }, { "tool", seat.CallerTool }, { "model", "" } }
            };
            for (int i = 0; i < members.Count; i++)
                roster.Add(new Dictionary<string, object> { { "role", "member-" + (i + 1) }, { "tool", members[i] }, { "model", "" } });
            item["tools"] = roster.Select(r => (string)r["tool"]).ToArray();
            item["roster"] = roster.ToArray();
            item["auto_roster"] = autoRoster;

            string roundText = rounds == 0 ? "自由轮数" : rounds + " 轮";
            detail.AppendLine("方式：分工协作（" + (autoRoster ? "自动组队" : "指定成员") + " · " + roundText + "）");
            detail.AppendLine("项目：" + seat.ProjectDir);
            detail.AppendLine("领队：当前对话（" + Tool.LabelOf(seat.CallerTool) + "）");
            detail.AppendLine(autoRoster
                ? "成员：领队按任务动态创建（可用：" + String.Join("、", available.Select(Tool.LabelOf)) + "）"
                : "成员：" + String.Join("、", members.Select(Tool.LabelOf)));
        } else {
            string reviewer = Text(args, "reviewer").Trim().ToLowerInvariant();
            if (reviewer.Length == 0) reviewer = available.FirstOrDefault(t => t != seat.CallerTool) ?? seat.CallerTool;
            if (!available.Contains(reviewer)) return Fail("电脑上不可用的审查工具：" + reviewer);
            item["tools"] = new[] { seat.CallerTool, reviewer };
            detail.AppendLine("方式：写审（" + rounds + " 轮）");
            detail.AppendLine("项目：" + seat.ProjectDir);
            detail.AppendLine("写手：当前对话（" + Tool.LabelOf(seat.CallerTool) + "）");
            detail.AppendLine("审查：" + Tool.LabelOf(reviewer) + "（只读）");
        }
        detail.AppendLine("目标：" + (goal.Length > 400 ? goal.Substring(0, 400) + "…" : goal));
        if (rules.Length > 0) detail.AppendLine("偏好：" + (rules.Length > 200 ? rules.Substring(0, 200) + "…" : rules));

        bool isAgyCaller = String.Equals(seat.CallerTool, "agy", StringComparison.OrdinalIgnoreCase);
        if (collab.Confirm != null) {
            bool allowed;
            bool timedOut = false;
            if (isAgyCaller) {
                int confirmTimeout = Math.Min(timeoutSeconds, AgyTimeoutSeconds);
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(confirmTimeout)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeoutCts.Token)) {
                    allowed = collab.Confirm(seat.TerminalId, "collab", "将当前对话转为协作任务？", detail.ToString().TrimEnd(), linked.Token);
                    timedOut = !allowed && timeoutCts.IsCancellationRequested && !cancel.IsCancellationRequested;
                }
            } else {
                allowed = collab.Confirm(seat.TerminalId, "collab", "将当前对话转为协作任务？", detail.ToString().TrimEnd(), cancel);
            }
            if (!allowed) {
                if (timedOut) return Fail("手机确认等待超时，请重新发起协作或提醒用户在手机上确认。");
                return Fail(cancel.IsCancellationRequested ? "开启协作已取消" : "用户拒绝了开启协作，请继续以普通对话处理。");
            }
        }
        if (seat.Closed || disposed || cancel.IsCancellationRequested) return Fail("开启协作已取消");
        try {
            CollabStarted started = collab.Promote(seat.TerminalId, item);
            if (started == null || String.IsNullOrEmpty(started.TeamId)) return Fail("未能开启协作任务");
            string promptText = started.Prompt ?? "";
            if (isAgyCaller && Encoding.UTF8.GetByteCount(promptText) > AgyMaxAnswerBytes)
                promptText = ProcessText.FoldHeadTailUtf8(promptText, AgyMaxAnswerBytes);
            return new Dictionary<string, object> { { "ok", true }, { "text", promptText } };
        } catch (Exception ex) {
            string why = ex is ArgumentException || ex is InvalidOperationException ? ex.Message : "电脑端开启协作失败";
            return Fail(why);
        }
    }

    static Dictionary<string, object> Fail(string error) {
        return new Dictionary<string, object> { { "ok", false }, { "error", error } };
    }

    // ---- named pipe server
    void Listen() {
        while (!disposed) {
            NamedPipeServerStream stream = null;
            try {
                stream = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0);
                stream.WaitForConnection();
                if (disposed) { stream.Dispose(); return; }
                var accepted = stream; stream = null;
                Task.Run(() => Serve(accepted));
            } catch (IOException) { if (stream != null) stream.Dispose(); Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { if (stream != null) stream.Dispose(); Thread.Sleep(2000); }
            catch (ObjectDisposedException) { return; }
        }
    }

    void Serve(NamedPipeServerStream stream) {
        using (stream)
        using (var cancel = new CancellationTokenSource()) {
            try {
                byte[] raw = ReadFrame(stream);
                if (raw == null) return;
                var req = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(raw));
                string op = Text(req, "op"), token = Text(req, "token");
                Seat seat;
                lock (gate) seats.TryGetValue(token, out seat);
                Dictionary<string, object> reply;
                if (seat == null || seat.Closed) {
                    reply = op == "list"
                        ? new Dictionary<string, object> { { "ok", true }, { "tools", new object[0] } }
                        : Fail("当前对话已结束，无法请教其他 AI");
                } else if (op == "list") {
                    reply = new Dictionary<string, object> { { "ok", true }, { "tools", ToolDefinitions(seat) } };
                } else if (op == "task") {
                    reply = TaskCall(seat, Text(req, "name"), At(req, "args") as Dictionary<string, object>);
                } else if (op == "call" || op == "collab") {
                    // Watch for client disconnect (e.g. caller stopped/cancelled the MCP call) so the peer/confirmation stops too.
                    using (var stopWatch = new CancellationTokenSource()) {
                        var probe = new byte[1];
                        var watching = stream.ReadAsync(probe, 0, 1, stopWatch.Token).ContinueWith(t => {
                            if (!stopWatch.IsCancellationRequested && (t.IsFaulted || (!t.IsCanceled && t.Result == 0))) cancel.Cancel();
                        }, TaskContinuationOptions.ExecuteSynchronously);
                        reply = op == "call"
                            ? Ask(seat, Text(req, "tool"), Text(req, "question"), true.Equals(At(req, "fresh")), cancel.Token)
                            : StartCollaboration(seat, At(req, "args") as Dictionary<string, object>, cancel.Token);
                        stopWatch.Cancel();
                    }
                } else {
                    reply = Fail("不支持的请求");
                }
                if (!cancel.IsCancellationRequested) WriteFrame(stream, Encoding.UTF8.GetBytes(json.Serialize(reply)));
            } catch (IOException) { } catch (ObjectDisposedException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
    }

    // ---- stdio MCP bridge (`RemoteCliAgent.exe --mcp <pipe> <token>`)
    public static int Bridge(string pipeName, string token) {
        var ser = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        var writeLock = new object();
        var inflightLock = new object();
        var inflight = new Dictionary<string, NamedPipeClientStream>();
        var utf8 = new UTF8Encoding(false);
        Stream stdout = Console.OpenStandardOutput();

        Action<object, bool> send = (message, useContentLength) => {
            string body = ser.Serialize(message);
            byte[] payloadBytes = utf8.GetBytes(body);
            byte[] frame;
            if (useContentLength) {
                byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + payloadBytes.Length + "\r\n\r\n");
                frame = new byte[header.Length + payloadBytes.Length];
                Buffer.BlockCopy(header, 0, frame, 0, header.Length);
                Buffer.BlockCopy(payloadBytes, 0, frame, header.Length, payloadBytes.Length);
            } else {
                frame = new byte[payloadBytes.Length + 1];
                Buffer.BlockCopy(payloadBytes, 0, frame, 0, payloadBytes.Length);
                frame[payloadBytes.Length] = (byte)'\n';
            }
            lock (writeLock) {
                try { stdout.Write(frame, 0, frame.Length); stdout.Flush(); }
                catch (IOException) { } catch (ObjectDisposedException) { }
            }
        };
        Action<object, object, bool> reply = (id, result, cl) => send(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", result } }, cl);
        Action<object, int, string, bool> fail = (id, code, message, cl) => send(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } } }, cl);

        Func<string, Dictionary<string, object>, int, Dictionary<string, object>> pipeCall = (key, payload, timeoutMs) => {
            NamedPipeClientStream client = null;
            try {
                client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                client.Connect(timeoutMs);
                if (key != null) lock (inflightLock) inflight[key] = client;
                WriteFrame(client, utf8.GetBytes(ser.Serialize(payload)));
                byte[] back = ReadFrame(client);
                if (back == null) return null;
                return ser.Deserialize<Dictionary<string, object>>(utf8.GetString(back));
            } catch (TimeoutException) { return null; }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            finally {
                if (key != null) lock (inflightLock) inflight.Remove(key);
                if (client != null) try { client.Dispose(); } catch (Exception) { }
            }
        };

        try {
            using (var stdin = Console.OpenStandardInput()) {
                string raw;
                bool useContentLength;
                while (ReadMcpMessage(stdin, utf8, out raw, out useContentLength)) {
                    raw = (raw ?? "").Trim();
                    if (raw.Length == 0) continue;
                    Dictionary<string, object> msg;
                    try { msg = ser.Deserialize<Dictionary<string, object>>(raw); }
                    catch (ArgumentException) { continue; } catch (InvalidOperationException) { continue; }
                    if (msg == null) continue;
                    bool hasId = msg.ContainsKey("id");
                    object id = hasId ? msg["id"] : null;
                    string method = Text(msg, "method");
                    var p = At(msg, "params");

                    if (!hasId) {
                        if (method == "notifications/cancelled") {
                            string rid = Convert.ToString(At(p, "requestId") ?? "");
                            NamedPipeClientStream active = null;
                            lock (inflightLock) inflight.TryGetValue(rid, out active);
                            if (active != null) try { active.Dispose(); } catch (Exception) { }
                        }
                        continue;
                    }

                    bool framed = useContentLength;
                    if (method == "initialize") {
                        string version = Text(p, "protocolVersion");
                        if (version.Length == 0) version = "2025-03-26";
                        reply(id, new Dictionary<string, object> {
                            { "protocolVersion", version },
                            { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                            { "serverInfo", new Dictionary<string, object> { { "name", "remote-ai" }, { "version", "1" } } } }, framed);
                    } else if (method == "ping") {
                        reply(id, new Dictionary<string, object>(), framed);
                    } else if (method == "resources/list") {
                        reply(id, new Dictionary<string, object> { { "resources", new object[0] } }, framed);
                    } else if (method == "prompts/list") {
                        reply(id, new Dictionary<string, object> { { "prompts", new object[0] } }, framed);
                    } else if (method == "tools/list") {
                        var back = pipeCall(null, new Dictionary<string, object> { { "op", "list" }, { "token", token } }, 3000);
                        object tools = back != null ? At(back, "tools") : null;
                        reply(id, new Dictionary<string, object> { { "tools", tools ?? new object[0] } }, framed);
                    } else if (method == "tools/call") {
                        string name = Text(p, "name");
                        var args = At(p, "arguments");
                        if (Array.IndexOf(ToolNames, name) < 0) {
                            reply(id, ToolResult("不支持的工具：" + name, true), framed);
                            continue;
                        }
                        string reqKey = Convert.ToString(id);
                        var reqPayload = name == ToolName
                            ? new Dictionary<string, object> {
                                { "op", "call" }, { "token", token },
                                { "tool", Text(args, "tool") }, { "question", Text(args, "question") },
                                { "fresh", true.Equals(At(args, "fresh")) } }
                            : new Dictionary<string, object> {
                                { "op", name == CollabToolName ? "collab" : "task" }, { "token", token }, { "name", name },
                                { "args", args as Dictionary<string, object> ?? new Dictionary<string, object>() } };
                        string fallbackErr = name == ToolName ? "请教失败" : name == CollabToolName ? "开启协作失败" : "子任务操作失败";
                        Task.Run(() => {
                            var back = pipeCall(reqKey, reqPayload, 3000);
                            if (back == null) reply(id, ToolResult("电脑端协作服务暂时不可用或调用已取消", true), framed);
                            else if (true.Equals(At(back, "ok"))) reply(id, ToolResult(Text(back, "text"), false), framed);
                            else reply(id, ToolResult(Text(back, "error").Length > 0 ? Text(back, "error") : fallbackErr, true), framed);
                        });
                    } else {
                        fail(id, -32601, "Method not found", framed);
                    }
                }
            }
        } catch (IOException) { } catch (ObjectDisposedException) { }
        finally {
            NamedPipeClientStream[] open;
            lock (inflightLock) { open = inflight.Values.ToArray(); inflight.Clear(); }
            foreach (var c in open) try { c.Dispose(); } catch (Exception) { }
            lock (writeLock) { try { stdout.Flush(); } catch { } }
        }
        return 0;
    }

    /// Reads one MCP JSON-RPC message from stdio supporting both NDJSON and LSP-style Content-Length framing.
    static bool ReadMcpMessage(Stream stdin, Encoding utf8, out string jsonText, out bool useContentLength) {
        jsonText = "";
        useContentLength = false;
        while (true) {
            string line = ReadAsciiOrUtf8Line(stdin, utf8);
            if (line == null) return false;
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)) {
                int contentLength = -1;
                string header = trimmed;
                while (header != null && header.Trim().Length > 0) {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) {
                        int parsed;
                        if (Int32.TryParse(header.Substring("Content-Length:".Length).Trim(), out parsed)) contentLength = parsed;
                    }
                    header = ReadAsciiOrUtf8Line(stdin, utf8);
                }
                if (contentLength <= 0 || contentLength > MaxFrameBytes) continue;
                byte[] body = new byte[contentLength];
                int got = 0, n;
                while (got < contentLength && (n = stdin.Read(body, got, contentLength - got)) > 0) got += n;
                if (got < contentLength) return false;
                jsonText = utf8.GetString(body, 0, got);
                useContentLength = true;
                return true;
            }
            jsonText = trimmed;
            useContentLength = false;
            return true;
        }
    }

    static string ReadAsciiOrUtf8Line(Stream stream, Encoding utf8) {
        var bytes = new List<byte>(256);
        int b;
        while ((b = stream.ReadByte()) >= 0) {
            if (b == '\n') break;
            if (b == '\r') continue;
            bytes.Add((byte)b);
            if (bytes.Count > MaxFrameBytes) break;
        }
        if (b < 0 && bytes.Count == 0) return null;
        return utf8.GetString(bytes.ToArray());
    }

    static Dictionary<string, object> ToolResult(string text, bool isError) {
        return new Dictionary<string, object> {
            { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", text ?? "" } } } },
            { "isError", isError } };
    }

    // ---- framing and JSON helpers
    static byte[] ReadFrame(Stream stream) {
        var head = new byte[4]; int got = 0, n;
        while (got < 4 && (n = stream.Read(head, got, 4 - got)) > 0) got += n;
        if (got < 4) return null;
        int length = BitConverter.ToInt32(head, 0);
        if (length < 0 || length > MaxFrameBytes) return null;
        var body = new byte[length]; got = 0;
        while (got < length && (n = stream.Read(body, got, length - got)) > 0) got += n;
        return got == length ? body : null;
    }
    static void WriteFrame(Stream stream, byte[] body) {
        byte[] head = BitConverter.GetBytes(body.Length);
        stream.Write(head, 0, 4); stream.Write(body, 0, body.Length); stream.Flush();
    }
    static object At(object from, string key) { var d = from as Dictionary<string, object>; object v; return d != null && d.TryGetValue(key, out v) ? v : null; }
    static string Text(object from, string key) { return At(from, key) as string ?? ""; }

    /// Which of this server's tools a chat driver's step calls, by the name its tool gives it (`mcp__remote_ai__ask_ai`,
    /// `ask_ai`, `remote_ai_ask_ai`); "" for none.
    public static string OwnTool(string name) {
        name = name ?? "";
        foreach (string one in ToolNames)
            if (name == one || name.EndsWith("__" + one, StringComparison.Ordinal) || name.EndsWith("_" + one, StringComparison.Ordinal)) return one;
        return "";
    }
    /// The title of a step that calls one of this server's tools (`tool`, from OwnTool), and what it shows unfolded.
    public static string StepOf(string tool, object input, out string detail) {
        string task = Text(input, "task_id");
        switch (tool) {
            case ToolName: detail = Text(input, "question"); return StepTitle(Text(input, "tool"), detail);
            case CollabToolName: detail = Text(input, "goal"); return CollabStepTitle(Text(input, "mode"), detail);
            case DelegateToolName: {
                detail = Text(input, "task");
                string title = Text(input, "title").Trim();
                if (title.Length == 0) title = detail.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                if (title.Length > 60) title = title.Substring(0, 60) + "…";
                return "派给 " + Tool.LabelOf(Text(input, "tool")) + (title.Length > 0 ? "：" + title : "");
            }
            case StatusToolName: detail = ""; return "查看子任务 " + task;
            case CancelToolName: detail = Text(input, "reason"); return "停止子任务 " + task;
            case AnswerToolName: detail = Convert.ToString(At(input, "answer") ?? ""); return "回答子任务 " + task + (detail.Length > 0 ? "：" + detail : "");
        }
        detail = ""; return tool;
    }
    /// Formats a step title for an `ask_ai` tool call across chat drivers.
    public static string StepTitle(string peerTool, string question) {
        string who = String.IsNullOrEmpty(peerTool) ? "其他 AI" : Tool.LabelOf(peerTool);
        string first = (question ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (first.Length > 140) first = first.Substring(0, 140) + "…";
        return "请教 " + who + (first.Length > 0 ? "：" + first : "");
    }
    /// Formats a step title for a `start_collaboration` tool call across chat drivers.
    public static string CollabStepTitle(string mode, string goal) {
        string kind = String.Equals(mode, "review", StringComparison.OrdinalIgnoreCase) ? "写审" : "分工协作";
        string first = (goal ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (first.Length > 140) first = first.Substring(0, 140) + "…";
        return "发起协作 · " + kind + (first.Length > 0 ? "：" + first : "");
    }
}
}
