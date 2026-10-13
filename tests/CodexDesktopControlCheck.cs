using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using RemoteCli;

public static class CodexDesktopControlCheck {
    const string Session = "12345678-1234-1234-1234-123456789abc";
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Fails<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    static Dictionary<string, object> Object(object value) { return (Dictionary<string, object>)value; }
    sealed class Conversation : IDesktopConversation {
        public string Folder; public int Sends, Stops;
        public readonly ManualResetEvent Inspected = new ManualResetEvent(false), Release = new ManualResetEvent(true);
        public Dictionary<string, object> Inspect(string id) { Inspected.Set(); Release.WaitOne(3000); return new Dictionary<string, object> { { "cwd", Folder } }; }
        public Dictionary<string, object> Send(string id, string text) { Sends++; return new Dictionary<string, object> { { "status", "accepted" }, { "sent", true }, { "turn", "turn-1" } }; }
        public Dictionary<string, object> Interrupt(string expectedTurn) { Stops++; return new Dictionary<string, object> { { "status", "confirmed" }, { "turn", expectedTurn } }; }
        public void Dispose() { }
    }
    static object Field(object value, string name) { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
    static void Agent(string root) {
        Directory.CreateDirectory(root);
        var conversation = new Conversation { Folder = root };
        var agent = new TerminalAgent(root, () => conversation);
        var sessions = (List<TerminalAgent.SessionInfo>)Field(agent, "sessions");
        sessions.Add(new TerminalAgent.SessionInfo { Id = Session, Tool = "codex", Dir = "demo", ControlFolder = root, Live = true });
        var prefs = new Dictionary<string, object> { { "RemoteEnabled", true }, { "RemoteDirs", new[] { "demo=" + root } } };
        Action save = () => File.WriteAllText(Path.Combine(root, "config.json"), Json.Serialize(prefs)); save();
        var execute = typeof(TerminalAgent).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
        Func<Dictionary<string, object>, Dictionary<string, object>> run = op => {
            execute.Invoke(agent, new object[] { op, prefs });
            for (int n = 0; n < 150; n++) {
                lock (Field(agent, "work")) {
                    var done = (Dictionary<string, Dictionary<string, object>>)Field(agent, "completed");
                    if (done.ContainsKey((string)op["id"]) && !((HashSet<string>)Field(agent, "relaying")).Contains((string)op["id"])) return done[(string)op["id"]];
                }
                Thread.Sleep(20);
            }
            throw new Exception("native agent timed out");
        };
        Func<Dictionary<string, object>> request = () => new Dictionary<string, object> {
            { "id", Guid.NewGuid().ToString("N") }, { "action", "session_say" }, { "tool", "codex" }, { "session", Session },
            { "dir", "demo" }, { "transport", "codex-desktop" }, { "text", "test" }, { "at", (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds }
        };
        var first = request(); Require((string)Object(run(first)["result"])["status"] == "accepted" && conversation.Sends == 1, "agent native dispatch");
        first["receipt"] = true; run(first); Require(conversation.Sends == 1, "agent queries durable result");
        first["text"] = "changed"; Require(((string)run(first)["error"]).Length > 0 && conversation.Sends == 1, "completed id cannot mask changed payload");
        var missing = request(); missing["dir"] = "other"; Require(((string)run(missing)["error"]).Length > 0, "native project allowlist");
        conversation.Folder = root + "-other"; Require(((string)run(request())["error"]).Length > 0 && conversation.Sends == 1, "snapshot cwd mismatch"); conversation.Folder = root;
        var query = request(); query["receipt"] = true; Require((string)Object(run(query)["result"])["status"] == "unknown" && conversation.Sends == 1, "unknown query cannot send");
        var stop = request(); stop["action"] = "session_stop"; stop["turn"] = "turn-1"; stop.Remove("text");
        Require((string)Object(run(stop)["result"])["status"] == "confirmed" && conversation.Stops == 1, "stop uses adapter, not process kill");
        conversation.Inspected.Reset(); conversation.Release.Reset(); var blocked = request();
        var task = Task.Run(() => run(blocked)); Require(conversation.Inspected.WaitOne(1000), "inspect started asynchronously");
        prefs["RemoteEnabled"] = false; save(); conversation.Release.Set();
        Require(((string)task.Result["error"]).Length > 0 && conversation.Sends == 1, "remote disable during inspect prevents dispatch");
        prefs["RemoteEnabled"] = true; save();
        var list = typeof(TerminalAgent).GetMethod("Listed", BindingFlags.Instance | BindingFlags.NonPublic);
        // The ways into a conversation fall back on one another, each tool by what is its own. Codex's desktop app is
        // never told through Codex's queue (that is another program's, which would write to a conversation the app
        // has): without the app's channel nothing is sent to one that sits idle. Claude Code's conversations, the
        // desktop app's too, take a message where they sit idle by Claude Code's own way between conversations.
        sessions[0].Tool = "codex"; sessions[0].Origin = "Codex 桌面版"; sessions[0].Wakes = true;
        var shown = list.Invoke(agent, new object[] { sessions[0] });
        Require(!true.Equals(shown.GetType().GetProperty("wake").GetValue(shown, null)), "Codex's desktop app is not told through Codex's queue");
        var fallback = request(); fallback["tool"] = "codex"; fallback.Remove("transport");
        Require(((string)run(fallback)["error"]).Contains("不能从手机发消息") && conversation.Sends == 1, "and without its channel nothing is dispatched to one that sits idle");
        sessions[0].Tool = "claude"; sessions[0].Origin = "Claude 桌面版"; sessions[0].Wakes = true;
        shown = list.Invoke(agent, new object[] { sessions[0] });
        Require(true.Equals(shown.GetType().GetProperty("wake").GetValue(shown, null)) && ((string[])shown.GetType().GetProperty("reach").GetValue(shown, null)).Contains("say") && (string)shown.GetType().GetProperty("transport").GetValue(shown, null) == "", "Claude's desktop app is told where it sits idle, by Claude Code's own way");
        sessions[0].Tool = "codex"; sessions[0].Origin = "Codex 桌面版";
        // While the application works, its channel takes no message: what is said is left for the conversation
        // through its hooks, as for any conversation at work, whether the phone asked by the channel's name or not.
        // The listing says the way of each thing, and why for what cannot be done.
        var states = (Dictionary<string, CodexDesktopSessions.State>)Field(Field(agent, "NativeDesktop"), "states");
        states[Session] = new CodexDesktopSessions.State { Folder = root, Busy = true, Ready = false, Turn = "turn-9", At = DateTime.UtcNow };
        var heard = (Dictionary<string, Reach.Open>)Field(agent.Reach, "open");
        heard["codex " + Session.ToLowerInvariant()] = new Reach.Open { Tool = "codex", Session = Session, Status = "busy", Seen = DateTime.UtcNow };
        shown = list.Invoke(agent, new object[] { sessions[0] });
        var ways = (Dictionary<string, object>)shown.GetType().GetProperty("ways").GetValue(shown, null);
        var why = (Dictionary<string, string>)shown.GetType().GetProperty("why").GetValue(shown, null);
        Require((string)ways["say"] == "hook" && (string)ways["stop"] == "codex-desktop" && (string)ways["end"] == "" && !why.ContainsKey("say") && !why.ContainsKey("stop") && why["end"].Length > 0 && (string)shown.GetType().GetProperty("transport").GetValue(shown, null) == "codex-desktop",
            "at work: said through its hooks, stopped by the application's channel: " + Json.Serialize(ways) + Json.Serialize(why));
        foreach (bool named in new[] { true, false }) {
            var left = request(); if (!named) left.Remove("transport");
            var answer = Object(run(left)["result"]);
            Require((string)answer["status"] == "accepted" && true.Equals(answer["sent"]) && false.Equals(answer["now"]) && (string)answer["way"] == "hook" && conversation.Sends == 1, "left for it through its hooks, the channel not asked: " + Json.Serialize(answer));
        }
        states[Session].Busy = false; states[Session].Ready = true; states[Session].Turn = ""; heard.Clear();
        shown = list.Invoke(agent, new object[] { sessions[0] });
        Require((string)((Dictionary<string, object>)shown.GetType().GetProperty("ways").GetValue(shown, null))["say"] == "codex-desktop", "ready again: by the application's channel");
        var unnamed = request(); unnamed.Remove("transport");
        Require(((string)run(unnamed)["error"]).Contains("再发一次") && conversation.Sends == 1, "the channel is only used where it was asked for by name, with a receipt");
        states.Clear();
        Console.WriteLine("native agent: async dispatch, receipt identity, project/cwd checks and permission revocation passed");
    }
    sealed class Peer : Stream {
        readonly Queue<byte> received = new Queue<byte>();
        public readonly List<Dictionary<string, object>> Sent = new List<Dictionary<string, object>>();
        public bool Busy, WrongOwner, WrongThread, WrongVersion, NativeError, Disconnect, BadReceipt, Stall;
        public int InvalidLength;
        public bool Closed;
        public void Frame(object value) { byte[] body = Encoding.UTF8.GetBytes(Json.Serialize(value)); foreach (byte b in BitConverter.GetBytes(body.Length).Concat(body)) received.Enqueue(b); }
        public override void Write(byte[] buffer, int offset, int count) {
            int size = BitConverter.ToInt32(buffer, offset);
            Require(count == size + 4, "single complete outgoing frame");
            var request = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(buffer, offset + 4, size)); Sent.Add(request);
            if (Stall) return;
            if (InvalidLength != 0) { foreach (byte b in BitConverter.GetBytes(InvalidLength)) received.Enqueue(b); return; }
            string method = request.ContainsKey("method") ? (string)request["method"] : "";
            if (method == "thread-stream-following-changed") {
                Frame(new { type = "client-discovery-request", requestId = "discovery" });
                Frame(new { type = "broadcast", method = "thread-stream-state-changed", version = WrongVersion ? 12 : 11, sourceClientId = WrongOwner ? "wrong" : "owner", @params = new {
                    conversationId = Session, hostId = "local", change = new { type = "snapshot", revision = 1, conversationState = new {
                        id = WrongThread ? "other" : Session, hostId = "local", cwd = @"E:\中文项目", resumeState = "resumed",
                        threadRuntimeStatus = new { type = Busy ? "active" : "idle" },
                        turnHistory = new { kind = "canonical", history = new { turns = new[] { new { turnId = "turn-1", status = Busy ? "inProgress" : "completed" } } } }
                    } }
                } }); return;
            }
            if ((string)request["type"] != "request") return;
            bool mutation = method == "thread-follower-start-turn" || method == "thread-follower-interrupt-turn";
            if (mutation && Disconnect) return;
            object result = method == "initialize" ? (object)new { clientId = "client" } : method == "thread-owner-discovery" ? (object)new { supportsUntrustedAppInput = true } :
                method == "thread-follower-start-turn" ? (object)new { result = new { turn = new { id = BadReceipt ? "" : "turn-2" } } } : (object)new { ok = true, interruptedTurnId = BadReceipt ? "other" : "turn-1" };
            Frame(new { type = "response", requestId = "unrelated", method = "ignored" });
            Frame(new { type = "response", requestId = request["requestId"], method = method, handledByClientId = "owner", resultType = NativeError && mutation ? "error" : "success", result = result });
        }
        public override int Read(byte[] buffer, int offset, int count) {
            int take = Math.Min(Math.Min(count, 3), received.Count); // split both length headers and multibyte text
            for (int i = 0; i < take; i++) buffer[offset + i] = received.Dequeue(); return take;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation) {
            return Stall ? new TaskCompletionSource<int>().Task : Task.FromResult(Read(buffer, offset, count));
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellation) { Write(buffer, offset, count); return Task.FromResult(0); }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
        public override bool CanRead { get { return true; } } public override bool CanWrite { get { return true; } } public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() {} public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); } public override void SetLength(long length) { throw new NotSupportedException(); }
    }
    public static void Run(string root) {
        var peer = new Peer();
        using (var control = new CodexDesktopControl(peer, 1000)) {
            var state = control.Inspect(Session); Require(CodexDesktopControl.Folder(state) == @"E:\中文项目" && CodexDesktopControl.Ready(state), "fragmented UTF8 snapshot");
            var result = control.Send(new string('a', 32), "中文消息"); Require((string)result["turn"] == "turn-2" && true.Equals(result["sent"]), "native turn receipt");
            var request = peer.Sent.Single(r => r.ContainsKey("method") && (string)r["method"] == "thread-follower-start-turn");
            Require((int)request["version"] == 2 && (string)request["targetClientId"] == "owner", "owner-targeted version");
            var start = Object(Object(request["params"])["turnStart"]);
            Require(true.Equals(Object(start["context"])["inheritThreadSettings"]), "inherit desktop settings");
            Require((string)Object(start["request"])["clientUserMessageId"] == new string('a', 32), "operation identity");
            Require(peer.Sent.Any(r => (string)r["type"] == "client-discovery-response"), "discovery must not stall other clients");
            Fails<InvalidOperationException>(() => control.Inspect(Session));
        }
        peer = new Peer { Busy = true };
        using (var control = new CodexDesktopControl(peer, 1000)) {
            var state = control.Inspect(Session); Require(CodexDesktopControl.ActiveTurn(state) == "turn-1", "active canonical turn");
            state["threadGoal"] = new Dictionary<string, object> { { "status", "active" } };
            Require(CodexDesktopControl.ActiveTurn(state) == "", "do not advertise incomplete stop of active goal"); state.Remove("threadGoal");
            Fails<InvalidOperationException>(() => control.Send(new string('a', 32), "busy"));
            Fails<InvalidOperationException>(() => control.Interrupt("older-turn"));
            Require(!peer.Sent.Any(r => r.ContainsKey("targetClientId")), "busy send and stale stop never dispatched");
            Require((string)control.Interrupt("turn-1")["status"] == "confirmed", "stop only expected turn");
        }
        foreach (var broken in new[] { new Peer { WrongOwner = true }, new Peer { WrongThread = true }, new Peer { WrongVersion = true }, new Peer { InvalidLength = -1 } })
            using (var control = new CodexDesktopControl(broken, 1000)) Fails<IOException>(() => control.Inspect(Session));
        peer = new Peer { Stall = true };
        using (var control = new CodexDesktopControl(peer, 40)) Fails<TimeoutException>(() => control.Inspect(Session));
        Require(peer.Closed, "timeout closes stream");
        foreach (var broken in new[] { new Peer { NativeError = true }, new Peer { Disconnect = true }, new Peer { BadReceipt = true } }) {
            string id = Guid.NewGuid().ToString("N"); int called = 0;
            Func<Dictionary<string, object>> send = () => { called++; using (var control = new CodexDesktopControl(broken, 1000)) { control.Inspect(Session); return control.Send(id, "test"); } };
            var ledger = new DesktopOperations(root);
            var identity = new[] { "say", Session, "test" };
            Require((string)ledger.Run(id, identity, send)["status"] == "unknown", "ambiguous dispatch remains unknown");
            Require((string)new DesktopOperations(root).Run(id, identity, send)["status"] == "unknown" && called == 1, "restart cannot resend");
            Fails<InvalidOperationException>(() => ledger.Run(id, new[] { "changed" }, send));
        }
        int calls = 0;
        var absent = new DesktopOperations(root).Run(Guid.NewGuid().ToString("N"), new[] { "query" }, () => { calls++; return null; }, true, true);
        Require((string)absent["status"] == "unknown" && calls == 0, "receipt query never executes absent operation");
        Console.WriteLine("Codex native control: framing, owner/version isolation, busy/stale turn, timeout, ambiguous receipts and restart replay passed");
        Agent(Path.Combine(root, "agent"));
    }
}
