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
public sealed class Peers : IDisposable {
    public const string ServerName = "remote_ai";
    public const string ToolName = "ask_ai";
    public const string Flag = "--mcp";
    public const int DefaultTimeoutSeconds = 240;
    const int MaxFrameBytes = 16 * 1024 * 1024;
    const int MaxQuestionChars = 16000;
    const int MaxAnswerChars = 24000;

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
        public string Token = "", CallerTool = "", Folder = "", ConfigFile = "";
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
    readonly object gate = new object();
    readonly Dictionary<string, Seat> seats = new Dictionary<string, Seat>();
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    volatile bool disposed;

    public string Pipe { get { return pipe; } }

    public Peers(string pipeName, Func<string, string> findTool, Quotas quotas, string configDir, string program = null, int timeoutSeconds = DefaultTimeoutSeconds) {
        pipe = pipeName;
        this.findTool = findTool ?? (_ => null);
        this.quotas = quotas;
        this.configDir = configDir;
        this.program = String.IsNullOrWhiteSpace(program) ? DefaultProgram() : program;
        this.timeoutSeconds = Math.Max(2, timeoutSeconds);
        new Thread(Listen) { IsBackground = true, Name = "peers" }.Start();
    }

    static string DefaultProgram() {
        string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RemoteCliAgent.exe");
        return File.Exists(beside) ? beside : null;
    }

    /// Offers `ask_ai` to a conversation of `callerTool` working in `folder`. Returns null when no bridge executable
    /// or no other chat-capable tool is installed on this computer.
    public Chat.McpServer Offer(string callerTool, string folder) {
        if (disposed || String.IsNullOrEmpty(program) || !File.Exists(program) || String.IsNullOrEmpty(folder)) return null;
        if (AvailablePeers(callerTool).Length == 0) return null;
        string token = Guid.NewGuid().ToString("N");
        string[] args = { Flag, pipe, token };
        string configFile = WriteClaudeConfig(token, program, args);
        var seat = new Seat { Token = token, CallerTool = callerTool ?? "", Folder = folder, ConfigFile = configFile };
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

    string[] AvailablePeers(string callerTool) {
        return Tool.All.Where(t => t.Chats && t.Name != callerTool && !String.IsNullOrEmpty(findTool(t.Name))).Select(t => t.Name).ToArray();
    }

    /// Tool definition returned to MCP `tools/list`, with live peer availability and quota hints.
    object[] ToolDefinitions(Seat seat) {
        string[] peers = AvailablePeers(seat.CallerTool);
        if (peers.Length == 0) return new object[0];
        var lines = new List<string>();
        foreach (string peer in peers) {
            string label = Tool.LabelOf(peer);
            string strength = Strengths.ContainsKey(peer) ? "，" + Strengths[peer] : "";
            string quota = "";
            if (quotas != null) {
                Quotas.Window[] windows = null;
                try { windows = quotas.Of(peer); } catch (Exception) { }
                string room = Teams.Room(windows);
                if (room != "额度未知") quota = "；" + room + (room.EndsWith("紧张", StringComparison.Ordinal) ? "（非必要请勿调用）" : "");
            }
            lines.Add("- " + peer + "（" + label + strength + quota + "）");
        }
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
        return new object[] {
            new Dictionary<string, object> {
                { "name", ToolName },
                { "description", description },
                { "inputSchema", schema },
                { "annotations", annotations } } };
    }

    Dictionary<string, object> Ask(Seat seat, string peerTool, string question, bool fresh, CancellationToken cancel) {
        peerTool = (peerTool ?? "").Trim().ToLowerInvariant();
        question = (question ?? "").Trim();
        if (question.Length == 0) return Fail("请提供要请教的具体问题");
        if (question.Length > MaxQuestionChars) question = question.Substring(0, MaxQuestionChars) + "\n…（问题过长，后文已省略）";
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
            string prompt = consultant.Turns == 0
                ? "【只读请教】同一项目里的另一位 AI（" + Tool.LabelOf(seat.CallerTool) + "）向你请教以下问题。请只读查看所需文件并直接给出简明、具体、可操作的意见；不要修改任何文件，不要执行会改动状态的命令，也不要等待额外确认。\n\n" + question
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
                DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
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
                        return Fail(kind.Label + " 回答超时（超过 " + timeoutSeconds + " 秒）");
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
            if (answer.Length > MaxAnswerChars) answer = answer.Substring(0, MaxAnswerChars) + "\n…（回答过长，后文已省略）";
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
                } else if (op == "call") {
                    // Watch for client disconnect (e.g. caller stopped/cancelled the MCP call) so the peer stops too.
                    using (var stopWatch = new CancellationTokenSource()) {
                        var probe = new byte[1];
                        var watching = stream.ReadAsync(probe, 0, 1, stopWatch.Token).ContinueWith(t => {
                            if (!stopWatch.IsCancellationRequested && (t.IsFaulted || (!t.IsCanceled && t.Result == 0))) cancel.Cancel();
                        }, TaskContinuationOptions.ExecuteSynchronously);
                        reply = Ask(seat, Text(req, "tool"), Text(req, "question"), true.Equals(At(req, "fresh")), cancel.Token);
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

        Action<object> send = message => {
            byte[] line = utf8.GetBytes(ser.Serialize(message) + "\n");
            lock (writeLock) { stdout.Write(line, 0, line.Length); stdout.Flush(); }
        };
        Action<object, object> reply = (id, result) => send(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", result } });
        Action<object, int, string> fail = (id, code, message) => send(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } } });

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
            using (var reader = new StreamReader(Console.OpenStandardInput(), utf8)) {
                string raw;
                while ((raw = reader.ReadLine()) != null) {
                    raw = raw.Trim();
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

                    if (method == "initialize") {
                        string version = Text(p, "protocolVersion");
                        if (version.Length == 0) version = "2025-03-26";
                        reply(id, new Dictionary<string, object> {
                            { "protocolVersion", version },
                            { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                            { "serverInfo", new Dictionary<string, object> { { "name", "remote-ai" }, { "version", "1" } } } });
                    } else if (method == "ping") {
                        reply(id, new Dictionary<string, object>());
                    } else if (method == "resources/list") {
                        reply(id, new Dictionary<string, object> { { "resources", new object[0] } });
                    } else if (method == "prompts/list") {
                        reply(id, new Dictionary<string, object> { { "prompts", new object[0] } });
                    } else if (method == "tools/list") {
                        var back = pipeCall(null, new Dictionary<string, object> { { "op", "list" }, { "token", token } }, 3000);
                        object tools = back != null ? At(back, "tools") : null;
                        reply(id, new Dictionary<string, object> { { "tools", tools ?? new object[0] } });
                    } else if (method == "tools/call") {
                        string name = Text(p, "name");
                        var args = At(p, "arguments");
                        if (name != ToolName) {
                            reply(id, ToolResult("不支持的工具：" + name, true));
                            continue;
                        }
                        string reqKey = Convert.ToString(id);
                        var reqPayload = new Dictionary<string, object> {
                            { "op", "call" }, { "token", token },
                            { "tool", Text(args, "tool") }, { "question", Text(args, "question") },
                            { "fresh", true.Equals(At(args, "fresh")) } };
                        Task.Run(() => {
                            var back = pipeCall(reqKey, reqPayload, 3000);
                            if (back == null) reply(id, ToolResult("电脑端协作服务暂时不可用或调用已取消", true));
                            else if (true.Equals(At(back, "ok"))) reply(id, ToolResult(Text(back, "text"), false));
                            else reply(id, ToolResult(Text(back, "error").Length > 0 ? Text(back, "error") : "请教失败", true));
                        });
                    } else {
                        fail(id, -32601, "Method not found");
                    }
                }
            }
        } catch (IOException) { } catch (ObjectDisposedException) { }
        finally {
            NamedPipeClientStream[] open;
            lock (inflightLock) { open = inflight.Values.ToArray(); inflight.Clear(); }
            foreach (var c in open) try { c.Dispose(); } catch (Exception) { }
        }
        return 0;
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

    /// Formats a step title for an `ask_ai` tool call across chat drivers.
    public static string StepTitle(string peerTool, string question) {
        string who = String.IsNullOrEmpty(peerTool) ? "其他 AI" : Tool.LabelOf(peerTool);
        string first = (question ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (first.Length > 140) first = first.Substring(0, 140) + "…";
        return "请教 " + who + (first.Length > 0 ? "：" + first : "");
    }
}
}
