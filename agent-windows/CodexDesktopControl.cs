using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
public interface IDesktopConversation : IDisposable {
    Dictionary<string, object> Inspect(string id);
    Dictionary<string, object> Send(string operationId, string text);
    Dictionary<string, object> Interrupt(string expectedTurn);
}
// The desktop's follower protocol. This never starts a backend or takes a writer lock.
// Kept separate from app-server JSON-RPC: these envelopes and versions are desktop-specific.
public sealed class CodexDesktopControl : IDesktopConversation {
    public const int MaxFrame = 16 * 1024 * 1024;
    readonly Stream stream;
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = MaxFrame, RecursionLimit = 200 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly int timeout;
    string client = "", owner = "", session = "";
    Dictionary<string, object> snapshot;
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);

    public CodexDesktopControl(Stream stream, int timeoutMilliseconds) { this.stream = stream; timeout = timeoutMilliseconds; }
    public static CodexDesktopControl Connect() {
        var pipe = new NamedPipeClientStream(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous);
        try {
            pipe.Connect(800);
            uint pid;
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out pid)) throw new IOException("无法确认 Codex 桌面通信服务");
            using (var process = Process.GetProcessById((int)pid)) {
                string exe = process.MainModule.FileName;
                // The installed Windows desktop package is the only supported router owner.
                if (!process.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
                    exe.IndexOf(@"\WindowsApps\OpenAI.Codex_26.1007.2314.0_x64__2p2nqsd0c76g0\app\", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new IOException("此 Codex 桌面版本的控制接口尚未适配");
            }
            return new CodexDesktopControl(pipe, 8000);
        } catch { pipe.Dispose(); throw; }
    }
    static string Text(Dictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) ? Convert.ToString(item) : ""; }
    static Dictionary<string, object> Obj(Dictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) ? item as Dictionary<string, object> : null; }
    int Remaining() { int left = timeout - (int)clock.ElapsedMilliseconds; if (left <= 0) throw new TimeoutException("Codex 桌面应用没有及时响应"); return left; }
    void Await(Task task) {
        if (Task.WhenAny(task, Task.Delay(Remaining())).GetAwaiter().GetResult() != task) { stream.Dispose(); throw new TimeoutException("Codex 桌面应用没有及时响应"); }
        task.GetAwaiter().GetResult();
    }
    byte[] ReadBytes(int count) {
        var bytes = new byte[count]; int offset = 0;
        while (offset < count) { var read = stream.ReadAsync(bytes, offset, count - offset); Await(read); int got = read.Result; if (got == 0) throw new EndOfStreamException(); offset += got; }
        return bytes;
    }
    Dictionary<string, object> Read() {
        uint size = BitConverter.ToUInt32(ReadBytes(4), 0);
        if (size == 0 || size > MaxFrame) throw new IOException("Codex 桌面响应长度无效");
        Dictionary<string, object> message;
        try { message = json.Deserialize<Dictionary<string, object>>(new UTF8Encoding(false, true).GetString(ReadBytes((int)size))); }
        catch (ArgumentException error) { throw new IOException("Codex 桌面响应格式无效", error); }
        catch (InvalidOperationException error) { throw new IOException("Codex 桌面响应格式无效", error); }
        if (message == null) throw new IOException("Codex 桌面响应无效");
        return message;
    }
    void Write(Dictionary<string, object> message) {
        byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(message));
        if (bytes.Length > MaxFrame) throw new ArgumentException("请求过长");
        byte[] frame = new byte[bytes.Length + 4]; Buffer.BlockCopy(BitConverter.GetBytes(bytes.Length), 0, frame, 0, 4); Buffer.BlockCopy(bytes, 0, frame, 4, bytes.Length);
        Await(stream.WriteAsync(frame, 0, frame.Length));
    }
    void Observe(Dictionary<string, object> message) {
        if (Text(message, "type") == "client-discovery-request") {
            Write(new Dictionary<string, object> { { "type", "client-discovery-response" }, { "requestId", Text(message, "requestId") }, { "response", new { canHandle = false } } });
            return;
        }
        if (Text(message, "type") != "broadcast" || Text(message, "method") != "thread-stream-state-changed" || Text(message, "sourceClientId") != owner || Text(message, "version") != "11") return;
        var parameters = Obj(message, "params"); var change = Obj(parameters, "change");
        if (Text(parameters, "conversationId") != session || Text(parameters, "hostId") != "local" || Text(change, "type") != "snapshot") return;
        var state = Obj(change, "conversationState");
        if (Text(state, "id") == session && Text(state, "hostId") == "local") snapshot = state;
    }
    Dictionary<string, object> Request(string method, int version, object parameters, string target = null) {
        string id = Guid.NewGuid().ToString("N");
        var request = new Dictionary<string, object> { { "type", "request" }, { "requestId", id }, { "sourceClientId", client }, { "method", method }, { "version", version }, { "params", parameters }, { "timeoutMs", Remaining() } };
        if (target != null) request["targetClientId"] = target;
        Write(request);
        while (true) {
            var message = Read();
            if (Text(message, "type") != "response" || Text(message, "requestId") != id) { Observe(message); continue; }
            if (Text(message, "method") != method || target != null && Text(message, "handledByClientId") != target) throw new IOException("Codex 桌面操作回执不匹配");
            // Native errors may themselves describe an unknown dispatch outcome. Never turn them into a safe-to-retry rejection.
            if (Text(message, "resultType") != "success") throw new IOException("Codex 桌面操作结果无法确认");
            return message;
        }
    }
    public Dictionary<string, object> Inspect(string id) {
        if (session.Length > 0) throw new InvalidOperationException("连接已经绑定到对话");
        Guid parsed; if (!Guid.TryParse(id, out parsed)) throw new ArgumentException("对话编号无效");
        session = id;
        client = Text(Obj(Request("initialize", 0, new { clientType = "remote-ai" }), "result"), "clientId");
        if (client.Length == 0) throw new IOException("Codex 桌面握手失败");
        var discovery = Request("thread-owner-discovery", 1, new { hostId = "local", conversationId = id });
        if (Text(Obj(discovery, "result"), "supportsUntrustedAppInput") != "True") throw new IOException("桌面对话不支持外部输入");
        owner = Text(discovery, "handledByClientId");
        if (owner.Length == 0) throw new IOException("Codex 桌面对话归属无法确认");
        Write(new Dictionary<string, object> { { "type", "broadcast" }, { "sourceClientId", client }, { "method", "thread-stream-following-changed" }, { "version", 1 }, { "targetClientIds", new[] { owner } }, { "params", new { hostId = "local", conversationId = id, following = true } } });
        while (snapshot == null) Observe(Read());
        return snapshot;
    }
    public static string Folder(Dictionary<string, object> state) { return Text(state, "cwd"); }
    public static bool Busy(Dictionary<string, object> state) { return Text(Obj(state, "threadRuntimeStatus"), "type") == "active"; }
    public static bool Ready(Dictionary<string, object> state) { return Text(state, "resumeState") == "resumed" && Text(Obj(state, "threadRuntimeStatus"), "type") == "idle"; }
    public static string ActiveTurn(Dictionary<string, object> state) {
        if (state == null) return "";
        // The versioned expected-turn endpoint bypasses goal pausing. Do not advertise an incomplete stop for an active goal.
        if (Text(Obj(state, "threadGoal"), "status") == "active") return "";
        if (!Busy(state)) return "";
        var ids = new HashSet<string>(); CollectTurns(state.ContainsKey("turns") ? state["turns"] : null, ids);
        CollectTurns(Obj(state, "turnHistory"), ids);
        return ids.Count == 1 ? ids.First() : "";
    }
    static void CollectTurns(object item, HashSet<string> ids) {
        var value = item as Dictionary<string, object>;
        if (value != null) {
            string id = Text(value, "turnId");
            if (id.Length > 0 && Text(value, "status") == "inProgress") ids.Add(id);
            foreach (var child in value.Values) CollectTurns(child, ids);
        } else if (item is System.Collections.IEnumerable && !(item is string)) foreach (var child in (System.Collections.IEnumerable)item) CollectTurns(child, ids);
    }
    public Dictionary<string, object> Send(string operationId, string text) {
        if (!System.Text.RegularExpressions.Regex.IsMatch(operationId ?? "", @"\A[a-f0-9]{16,32}\z")) throw new ArgumentException("操作编号无效");
        if (snapshot == null) throw new InvalidOperationException("先确认桌面对话归属");
        if (String.IsNullOrWhiteSpace(text) || text.Length > 8000) throw new ArgumentException("消息需为 1 至 8000 字");
        if (!Ready(snapshot)) throw new InvalidOperationException("Codex 尚未就绪，请等待当前执行或确认完成后发送");
        var result = Obj(Request("thread-follower-start-turn", 2, new { conversationId = session, turnStart = new {
            request = new { threadId = session, input = new[] { new { type = "text", text = text, text_elements = new object[0] } }, clientUserMessageId = operationId },
            context = new { inheritThreadSettings = true }
        } }, owner), "result");
        string turn = Text(Obj(Obj(result, "result"), "turn"), "id");
        if (turn.Length == 0) throw new IOException("Codex 发送回执无法确认");
        return new Dictionary<string, object> { { "status", "accepted" }, { "sent", true }, { "session", session }, { "turn", turn }, { "operation", operationId }, { "now", true }, { "transport", "codex-desktop" } };
    }
    public Dictionary<string, object> Interrupt(string expectedTurn) {
        if (String.IsNullOrEmpty(expectedTurn) || ActiveTurn(snapshot) != expectedTurn) throw new InvalidOperationException("执行轮次已变化，请刷新后再停止");
        var result = Obj(Request("thread-follower-interrupt-turn", 4, new { conversationId = session, mode = "user-stop", expectedTurnId = expectedTurn }, owner), "result");
        if (result == null || !true.Equals(result.ContainsKey("ok") ? result["ok"] : null) || Text(result, "interruptedTurnId") != expectedTurn) throw new IOException("Codex 停止回执无法确认");
        return new Dictionary<string, object> { { "status", "confirmed" }, { "session", session }, { "turn", expectedTurn }, { "goal_pause_error", Text(result, "goalPauseError") } };
    }
    public void Dispose() { stream.Dispose(); }
}

// Only short capability summaries survive a probe. Prompts, settings and history stay off the relay.
public sealed class CodexDesktopSessions {
    public sealed class State { public string Folder, Turn; public bool Ready, Busy; public DateTime At; }
    readonly object gate = new object();
    readonly Dictionary<string, State> states = new Dictionary<string, State>();
    bool refreshing;
    public State Get(string id) { lock (gate) { State state; return states.TryGetValue(id, out state) && DateTime.UtcNow - state.At < TimeSpan.FromSeconds(15) ? state : null; } }
    public void Refresh(IEnumerable<string> ids) {
        lock (gate) { if (refreshing) return; refreshing = true; }
        var wanted = ids.Distinct().Take(20).ToArray();
        Task.Run(() => {
            try {
                Parallel.ForEach(wanted, new ParallelOptions { MaxDegreeOfParallelism = 2 }, id => {
                    State state = null;
                    try { using (var control = CodexDesktopControl.Connect()) { var value = control.Inspect(id); state = new State { Folder = CodexDesktopControl.Folder(value), Ready = CodexDesktopControl.Ready(value), Busy = CodexDesktopControl.Busy(value), Turn = CodexDesktopControl.ActiveTurn(value), At = DateTime.UtcNow }; } }
                    catch (Exception) { /* No capability is inferred from an unavailable or incompatible endpoint. */ }
                    lock (gate) { if (state == null) states.Remove(id); else states[id] = state; }
                });
                lock (gate) foreach (string old in states.Keys.Where(id => !wanted.Contains(id)).ToArray()) states.Remove(old);
            } finally { lock (gate) refreshing = false; }
        });
    }
}
}
