using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using RemoteCli;

public static class DesktopAdapterCheck {
    sealed class App : IDesktopAdapter {
        public string Tool { get { return Named; } }
        public bool Available { get { return true; } }
        public string Named = "codex", Unfit = "";
        public int Calls; public bool Fail;
        public void Check(string folder, string draft) { if (Unfit.Length > 0 && folder.Contains(Unfit)) throw new ArgumentException("this application does not open that folder"); }
        public ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(true);
        public Dictionary<string, object> Open(string folder, string draft) {
            Interlocked.Increment(ref Calls); Entered.Set(); Release.WaitOne(4000);
            if (Fail) throw new InvalidOperationException("launch failed");
            return new Dictionary<string, object> { { "status", "handed_off" }, { "sent", false } };
        }
    }
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    static void Refused(Action action) { try { action(); } catch (InvalidOperationException) { return; } catch (ArgumentException) { return; } throw new Exception("should refuse"); }
    static object Field(object value, string name) { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
    public static void Main(string[] args) {
        string root = args[0], id = new string('a', 32), draft = "中文 &prompt=wrong # +\n\"quoted\"";
        string folder = Path.Combine(root, "中文 &项目"); Directory.CreateDirectory(folder);
        var uri = new Uri(CodexDesktopAdapter.Link(folder, draft));
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Require(query.Count == 2 && query["path"] == folder && query["prompt"] == draft, "Unicode/URL parameter isolation");
        Require(!CodexDesktopAdapter.Link(folder, "").Contains("prompt="), "empty composer");
        Refused(() => CodexDesktopAdapter.Link("relative", ""));
        Refused(() => CodexDesktopAdapter.Link(folder, new string('x', 1001)));
        Refused(() => CodexDesktopAdapter.Link(folder, "\0"));
        // Claude's desktop app: its own link for a new conversation of Claude Code in a folder, the draft in `q`
        uri = new Uri(ClaudeDesktopAdapter.Link(folder, draft));
        query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Require(uri.Scheme == "claude" && uri.Host == "code" && uri.AbsolutePath == "/new" && query.Count == 2 && query["folder"] == folder && query["q"] == draft, "Claude's link: " + uri);
        Require(!ClaudeDesktopAdapter.Link(folder, "").Contains("q="), "Claude: empty composer");
        Refused(() => ClaudeDesktopAdapter.Link("relative", ""));
        Refused(() => ClaudeDesktopAdapter.Link(@"\\server\share\project", ""));      // the application refuses a folder on another computer
        Refused(() => ClaudeDesktopAdapter.Link(folder, new string('x', 1001)));
        Refused(() => ClaudeDesktopAdapter.Link(folder, "\0"));
        var app = new App(); var service = new DesktopOperations(root, app);
        Parallel.For(0, 8, n => Require((string)service.Open(id, "codex", folder, draft)["status"] == "handed_off", "receipt"));
        Require(app.Calls == 1, "concurrent retry must not reopen");
        service = new DesktopOperations(root, app);
        service.Open(id, "codex", folder, draft);
        Require(app.Calls == 1, "restart replay");
        Refused(() => service.Open(id, "codex", folder, "different"));
        Refused(() => service.Open("../../bad", "codex", folder, draft));
        Refused(() => service.Open(new string('b', 32), "claude", folder, draft));
        string receiptPath = Path.Combine(root, "desktop-operations", id + ".json");
        var serializer = new JavaScriptSerializer();
        string saved = File.ReadAllText(receiptPath);
        Require(!saved.Contains(draft) && !saved.Contains(folder), "no stored draft/path");
        var pending = serializer.Deserialize<Dictionary<string, object>>(saved);
        pending["result"] = new Dictionary<string, object> { { "status", "unknown" }, { "sent", false } };
        File.WriteAllText(receiptPath, serializer.Serialize(pending));
        Require((string)service.Open(id, "codex", folder, draft)["status"] == "unknown" && app.Calls == 1, "crash reservation cannot dispatch again");
        File.WriteAllText(receiptPath, "{");
        Refused(() => service.Open(id, "codex", folder, draft));
        app.Fail = true;
        Refused(() => service.Open(new string('c', 32), "codex", folder, draft));
        Refused(() => service.Open(new string('c', 32), "codex", folder, draft));
        Require(app.Calls == 2, "failure receipt replay");

        app = new App(); app.Release.Reset();
        var agent = new TerminalAgent(Path.Combine(root, "agent"));
        typeof(TerminalAgent).GetField("Desktop", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(agent, new DesktopOperations(Path.Combine(root, "agent"), app));
        var prefs = new Dictionary<string, object> { { "RemoteEnabled", true }, { "RemoteDirs", new object[] { "demo=" + folder } } };
        Func<char, string, Dictionary<string, object>> op = (letter, dir) => new Dictionary<string, object> {
            { "id", new string(letter, 32) }, { "action", "app_open" }, { "tool", "codex" }, { "dir", dir }, { "draft", draft },
            { "at", (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds }
        };
        var execute = typeof(TerminalAgent).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
        Action<Dictionary<string, object>> run = operation => execute.Invoke(agent, new object[] { operation, prefs });
        var done = (Dictionary<string, Dictionary<string, object>>)Field(agent, "completed");
        run(op('d', "missing")); Require(Convert.ToString(done[new string('d', 32)]["error"]).Length > 0 && app.Calls == 0, "agent validates allowed directory");
        prefs["RemoteEnabled"] = false; run(op('e', "demo")); Require(app.Calls == 0, "remote disabled"); prefs["RemoteEnabled"] = true;
        run(op('f', "demo")); Require(app.Entered.WaitOne(2000), "async adapter started");
        Require(!done.ContainsKey(new string('f', 32)), "agent returned while app is blocked");
        run(op('f', "demo")); Require(app.Calls == 1, "in flight duplicate"); app.Release.Set();
        for (int n = 0; n < 100; n++) { lock (Field(agent, "work")) { if (done.ContainsKey(new string('f', 32))) break; } Thread.Sleep(20); }
        lock (Field(agent, "work")) Require(done.ContainsKey(new string('f', 32)) && done[new string('f', 32)].ContainsKey("result"), "async receipt completed");
        // every tool that has an adapter is opened the same way, each by its own; one that has none is refused, and
        // what an application would not take is refused before anything is reserved
        var other = new App { Named = "claude", Unfit = "项目" };
        var both = new DesktopOperations(Path.Combine(root, "agent"), app, other);
        typeof(TerminalAgent).GetField("Desktop", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(agent, both);
        Require(String.Join(",", both.Tools) == "codex,claude", "the tools whose application can be opened: " + String.Join(",", both.Tools));
        Refused(() => both.Check("gemini", folder, draft));
        var unfit = op('1', "demo"); unfit["tool"] = "claude"; run(unfit);
        Require(Convert.ToString(done[new string('1', 32)]["error"]).Contains("does not open") && other.Calls == 0 && !File.Exists(Path.Combine(root, "agent", "desktop-operations", new string('1', 32) + ".json")), "refused by its application's own rule, nothing reserved");
        other.Unfit = "";
        var claude = op('2', "demo"); claude["tool"] = "claude"; run(claude);
        for (int n = 0; n < 100; n++) { lock (Field(agent, "work")) { if (done.ContainsKey(new string('2', 32))) break; } Thread.Sleep(20); }
        lock (Field(agent, "work")) Require(other.Calls == 1 && app.Calls == 1 && done[new string('2', 32)].ContainsKey("result"), "Claude's application is opened by its own adapter");
        var unknown = op('3', "demo"); unknown["tool"] = "gemini"; run(unknown);
        Require(Convert.ToString(done[new string('3', 32)]["error"]).Contains("暂不支持") && other.Calls == 1 && app.Calls == 1, "a tool without an adapter is refused");
        Console.WriteLine("desktop adapter: URL isolation, durable replay, crash/failure, permissions and nonblocking dispatch passed");
        Console.WriteLine("installed Codex desktop entry available: " + new CodexDesktopAdapter().Available + ", Claude: " + new ClaudeDesktopAdapter().Available);
        CodexDesktopControlCheck.Run(Path.Combine(root, "native"));
    }
}
