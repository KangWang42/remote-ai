using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using RemoteCli;

static class OrganizeCheck {
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    static void Check(bool condition, string what) {
        if (!condition) throw new Exception(what);
    }

    static readonly string[] Exchange = { "为社区图书馆设计雨水收集与灌溉方案，需要考虑屋顶面积、储水量和预算。",
        "建议用储水桶收集屋顶雨水，再用滴灌浇灌花坛。储水桶放在落水管下方，加装初期雨水弃流装置和防蚊盖；滴灌管沿花坛边缘铺设，用定时器在清晨浇水。下一步测量屋顶面积并确认预算，之后再定储水桶容量和管线走向。" };

    static Organizer.Source Fixture(string project, int number, DateTime at, bool named = false, string[] said = null) {
        string id = new Guid(number, 0, 0, new byte[8]).ToString();
        string folder = Path.Combine(ClaudeSessions.Home, "projects", TerminalAgent.ProjectFolder(project));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, id + ".jsonl");
        var rows = (said ?? Exchange).Select((text, i) => Json.Serialize(new { type = i % 2 == 0 ? "user" : "assistant", timestamp = at.ToString("o"), cwd = project,
            message = new { role = i % 2 == 0 ? "user" : "assistant", content = text } })).ToList();
        if (named) rows.Add(Json.Serialize(new { type = "custom-title", customTitle = "工具里的人工名称" }));
        File.WriteAllLines(file, rows, new UTF8Encoding(false));
        File.SetCreationTimeUtc(file, at);
        File.SetLastWriteTimeUtc(file, at);
        return new Organizer.Source { Id = id, Tool = "claude", Dir = "demo", Folder = project, File = file,
            Title = named ? "工具里的人工名称" : "原始名称", Named = named, Updated = Organizer.Now(at) };
    }

    static void Append(Organizer.Source source, string text, DateTime at) {
        File.AppendAllText(source.File, Json.Serialize(new { type = "user", timestamp = at.ToString("o"), message = new { role = "user", content = text } }) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(source.File, at);
    }

    static Dictionary<string, object> Op(Organizer organizer, string verb, Organizer.Source source = null, params object[] fields) {
        var request = new Dictionary<string, object> { { "verb", verb } };
        if (source != null) { request["session"] = source.Id; request["tool"] = source.Tool; }
        for (int i = 0; i < fields.Length; i += 2) request[(string)fields[i]] = fields[i + 1];
        return organizer.Operate(request);
    }

    static DateTime Moment(long milliseconds) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds); }
    static int Number(Dictionary<string, object> values, string field) { return Convert.ToInt32(values[field]); }
    static Dictionary<string, object> Row(Organizer organizer, Organizer.Source source) {
        return ((Dictionary<string, object>[])Op(organizer, "list", source)["items"]).Single();
    }
    static void Refused<T>(Action act, string what) where T : Exception {
        try { act(); } catch (T) { return; }
        throw new Exception(what);
    }

    /// A source that answers at once and counts what it is asked.
    sealed class Fake : IOrganizeSource {
        public int Calls;
        public Func<string, string, Organizer.Metadata> Answer;
        public string Label { get { return "样例工具"; } }
        public Dictionary<string, object>[] Models() {
            return new[] { new Dictionary<string, object> { { "id", "large" }, { "label", "大模型" } }, new Dictionary<string, object> { { "id", "small" }, { "label", "小模型" } } };
        }
        public string DefaultModel(Dictionary<string, object>[] offered) { return OrganizeReply.Cheapest(offered, new[] { "small" }, "large"); }
        public Organizer.Metadata Analyze(string context, string model) { Interlocked.Increment(ref Calls); return Answer(context, model); }
    }

    static Organizer.Metadata Result() {
        return new Organizer.Metadata { Title = "图书馆雨水灌溉", Summary = "汇总雨水收集方案", Tags = new[] { "雨水", "灌溉" }, FollowUp = true, Tokens = 100 };
    }

    static void Offline(string root, string project) {
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(root, "codex"));
        var at = DateTime.UtcNow.AddDays(-1);
        var first = Fixture(project, 1, at, true);
        var second = Fixture(project, 2, at);
        foreach (int number in Enumerable.Range(3, 130)) Fixture(project, number, at);
        var projects = new Dictionary<string, string> { { "demo", project } };
        Func<long, Organizer.Source[]> read = since => OrganizeHistory.List(projects, new TerminalAgent.SessionInfo[0], since);
        string file = Path.Combine(root, "queue.json");
        var fake = new Fake();
        int opened = 0;
        var entered = new ManualResetEventSlim();
        var released = new ManualResetEventSlim();
        fake.Answer = (context, model) => {
            Check(model == "small", "the cheapest model was not chosen by default");
            if (context.Contains("中断这一次")) throw new TimeoutException();
            if (context.Contains("等测试放行")) {
                entered.Set();
                Check(released.Wait(5000), "the test did not release the request");
            }
            return Result();
        };
        Func<Organizer.Choice, IOrganizeSource> source = choice => {
            Interlocked.Increment(ref opened);
            Check(choice.Kind == "codex", "a source other than the chosen one was opened");
            return fake;
        };
        Func<Organizer> open = () => new Organizer(file, read, s => File.Exists(s.File), _ => false, () => new[] { "codex", "agy" }, source);

        // Off by default: nothing is opened, queued or sent, also with automatic mode on and an old queue on disk.
        File.WriteAllText(file, Json.Serialize(new { AutoSince = 1, Model = "gpt-6-luna", Entries = new Dictionary<string, object> {
            { second.Key, new { Source = second, State = Organizer.Queued, Automatic = false } } } }), new UTF8Encoding(false));
        var organizer = open();
        var status = Op(organizer, "status");
        Check((string)status["source"] == "off" && (string)status["source_tool"] == "" && !(bool)status["model_available"], "organizing is not off by default");
        Check(Number(status, "queued") == 0, "an earlier queue still waits while organizing is off");
        Refused<InvalidOperationException>(() => Op(organizer, "start", first), "a conversation was queued while organizing is off");
        Refused<InvalidOperationException>(() => Op(organizer, "start", null, "scope", "all"), "all conversations were queued while organizing is off");
        Op(organizer, "settings", null, "auto", true);
        organizer.Tick();
        organizer.Advance();
        Thread.Sleep(200);
        Check(opened == 0 && fake.Calls == 0 && Number(Op(organizer, "status"), "calls_today") == 0, "organizing called a model while it was off");
        Check((string)Row(organizer, second)["state"] == "pending", "an earlier queue was not set back while off");
        Op(organizer, "settings", null, "auto", false);
        File.Delete(file);
        organizer = open();
        Console.WriteLine("off by default: 0 sources opened, 0 requests, 0 queued (old queue and automatic mode on)");

        // Choosing a source: only installed tools, only listed models, the cheapest first; an API key is never shown.
        Refused<ArgumentException>(() => Op(organizer, "settings", null, "source", "tool"), "a tool source without a tool was accepted");
        Refused<ArgumentException>(() => Op(organizer, "settings", null, "source", "tool", "source_tool", "claude"), "a tool that is not installed was accepted");
        Refused<ArgumentException>(() => Op(organizer, "settings", null, "source", "api"), "an API source without an address was accepted");
        status = Op(organizer, "settings", null, "source", "tool", "source_tool", "codex");
        Check((string)status["source"] == "tool" && (string)status["source_tool"] == "codex" && (string)status["model"] == "small" && (bool)status["model_available"], "tool source or default model wrong");
        Check(((Dictionary<string, object>[])status["tools"]).Select(t => (string)t["id"]).SequenceEqual(new[] { "codex", "agy" }), "tools that are not installed were offered");
        Refused<ArgumentException>(() => Op(organizer, "settings", null, "model", "unknown"), "an unlisted model was accepted");
        Check((string)Op(organizer, "settings", null, "model", "large")["model"] == "large", "the chosen model was not kept");
        Op(organizer, "settings", null, "model", "small");
        const string secret = "sk-local-test-7f3a";
        var asApi = new Organizer(Path.Combine(root, "api.json"), read, _ => true, _ => false, () => new string[0], c => new ApiOrganizer(c.Url, c.Key));
        status = Op(asApi, "settings", null, "source", "api", "api_url", "https://api.example.test/v1", "api_key", secret, "model", "mini-1");
        Check((bool)status["api_key_set"] && (string)status["model"] == "mini-1" && (bool)status["model_available"], "API settings were not kept");
        Check(!Json.Serialize(status).Contains(secret) && !Json.Serialize(Op(asApi, "status")).Contains(secret), "the API key was returned");
        Check(!File.ReadAllText(Path.Combine(root, "api.json"), Encoding.UTF8).Contains(secret), "the API key was stored as it is");
        Check(!(bool)Op(asApi, "settings", null, "api_key", "")["api_key_set"], "the API key could not be removed");
        Console.WriteLine("settings: installed tools only, listed models only, cheapest by default, API key stored protected and reported as set");

        // The queue: paging, one run at a time, restart, failure and retry, manual titles and follow-up.
        Check(Number(Op(organizer, "list", null, "offset", 120), "total") == 132, "history enumeration follows the homepage limit");
        Check(((Dictionary<string, object>[])Op(organizer, "list", null, "offset", 120)["items"]).Length == 12, "pagination missed older records");
        Append(first, "等测试放行", at);
        first.Updated = Organizer.Now(at);
        Op(organizer, "start", first);
        organizer = open();
        var worker = Task.Run(() => organizer.Advance());
        Check(entered.Wait(5000), "queued work did not survive reopening the store");
        try {
            var competing = Task.Run(() => organizer.Advance());
            var duplicates = Task.Run(() => Op(organizer, "start", first));
            var rename = Task.Run(() => Op(organizer, "edit", first, "title", "运行期间手工起名"));
            Task.WaitAll(competing, duplicates, rename);
            Check(Number(duplicates.Result, "queued") == 0 && fake.Calls == 1, "same conversation started more than once");
            Check((string)Row(organizer, first)["state"] == "running", "in-flight item not shown as running");
            Check((string)Row(open(), first)["state"] == Organizer.Queued, "interrupted in-flight work will not resume after restart");
        } finally { released.Set(); }
        worker.GetAwaiter().GetResult();
        Check(organizer.Title("claude", first.Id, first.Title, true) == "运行期间手工起名", "manual title overwritten");
        Check((bool)Row(organizer, first)["follow_up"], "metadata was not written");
        Op(organizer, "edit", first, "follow_up", false);
        Check(!(bool)Row(organizer, first)["follow_up"], "manual follow-up did not take precedence over the model");
        organizer = open();
        Check(Number(Op(organizer, "start", first), "queued") == 0 && fake.Calls == 1, "unchanged conversation was sent again");
        Append(second, "中断这一次", at);
        Op(organizer, "start", second);
        organizer.Advance();
        Check((string)Row(organizer, second)["state"] == Organizer.Failed && ((string)Row(organizer, second)["error"]).Contains("样例工具"), "failure incorrectly marked as successful");
        fake.Answer = (context, model) => Result();
        organizer = open();
        Check(Number(Op(organizer, "retry", second), "queued") == 1, "failed item cannot be retried");
        organizer.Advance();
        Check((string)Row(organizer, second)["state"] == Organizer.Done, "retry did not complete");
        Check(organizer.Title("claude", second.Id, second.Title, true) == second.Title, "native title overwritten");
        status = Op(organizer, "status");
        Check(Number(status, "calls_today") == 3 && Convert.ToInt64(status["tokens_today"]) == 200, "calls or tokens of today were not counted: " + Json.Serialize(status));

        // Unchanged content is not sent again; changed content is. Short conversations and a team's are left out of "all".
        var shortOne = Fixture(project, 300, at, said: new[] { "你好", "你好！" });
        var teamOne = Fixture(project, 301, at, said: new[] { OrganizeHistory.TeamMark + " · 成员】领队分派给你的任务：检查雨水方案的测试并补齐缺少的部分。", "已检查雨水方案的测试，补齐了缺少的两个用例。" });
        var count = Op(organizer, "count", null, "scope", "all");
        Check(Number(count, "total") == 134 && Number(count, "needed") == 130, "count is wrong: " + Json.Serialize(count));
        File.SetLastWriteTimeUtc(first.File, at.AddMinutes(1));
        Check(Number(Op(organizer, "count", null, "scope", "all"), "needed") == 130, "a conversation whose file was touched without new content counted as changed");
        Append(first, "屋顶面积量好了，一共 86 平方米，请据此算储水桶容量。", at.AddMinutes(2));
        Check(Number(Op(organizer, "count", null, "scope", "all"), "needed") == 131, "changed content was not counted");
        int before = fake.Calls;
        Check(Number(Op(organizer, "start", null, "scope", "all"), "queued") == 131, "organize all did not queue what count said");
        for (int i = 0; i < 131; i++) organizer.Advance();
        Check(fake.Calls == before + 131 && (string)Row(organizer, shortOne)["state"] == "pending" && (string)Row(organizer, teamOne)["state"] == "pending", "short or team conversations were sent");
        Check(Number(Op(organizer, "count", null, "scope", "all"), "needed") == 0 && Number(Op(organizer, "start", null, "scope", "all"), "queued") == 0, "unchanged conversations were queued again");
        Check(Number(Op(organizer, "start", shortOne), "queued") == 1, "a short conversation asked for by name was refused");
        organizer.Advance();
        Console.WriteLine("unchanged: a second start, count and 'all' send nothing whose content was sent already (" + fake.Calls + " requests in all for 134 conversations, 2 of them short or a team's); a touched file without new content is skipped, changed content is sent again");

        // Automatic: only conversations quiet for ten minutes, not busy, and changed since they were last sent.
        long current = Organizer.Now(DateTime.UtcNow);
        var automaticFake = new Fake { Answer = (context, model) => Result() };
        var busy = new HashSet<string>();
        var automatic = new Organizer(Path.Combine(root, "automatic.json"), read, _ => true, s => busy.Contains(s.Key), () => new[] { "codex" }, c => automaticFake, () => current);
        Op(automatic, "settings", null, "source", "tool", "source_tool", "codex", "auto", true);
        var fresh = Fixture(project, 200, DateTime.UtcNow.AddSeconds(1));
        var working = Fixture(project, 201, DateTime.UtcNow.AddSeconds(1));
        busy.Add(working.Key);
        automatic.Advance();
        Check(automaticFake.Calls == 0, "a conversation was organized before it had been quiet for ten minutes");
        current += Organizer.Rest + 2000;
        automatic.Advance();
        Check((string)Row(automatic, fresh)["state"] == Organizer.Done && (string)Row(automatic, working)["state"] == "pending", "quiet conversation not organized, or a busy one was");
        Check((string)Row(automatic, first)["state"] == "pending" && automaticFake.Calls == 1, "a conversation not written to since automatic mode began was organized");
        current += 16000;
        automatic.Advance();
        Check(automaticFake.Calls == 1, "unchanged automatic work ran again");
        Append(fresh, "再补充一下：灌溉时间改成清晨。", Moment(current - 1000));
        current += 16000;
        automatic.Advance();
        Check(automaticFake.Calls == 1, "a conversation written to a moment ago was organized at once");
        current += Organizer.Rest + 2000;
        automatic.Advance();
        Check(automaticFake.Calls == 2, "changed content was not organized after it had been quiet");
        Op(automatic, "settings", null, "source", "off");
        Append(fresh, "又一次补充。", Moment(current));
        current += Organizer.Rest * 2;
        automatic.Tick();
        automatic.Advance();
        Thread.Sleep(200);
        Check(automaticFake.Calls == 2, "automatic mode called a model after organizing was turned off");
        Console.WriteLine("automatic: waits 10 minutes of quiet, skips busy and unchanged conversations, stops when turned off");

        // What is sent: the first request and the last messages, within the limit.
        var longOne = Fixture(project, 400, at, said: Enumerable.Range(0, 30).Select(i => (i == 0 ? "最初的需求：整理实验记录。" : "第 " + i + " 条 ") + new string('字', 900)).ToArray());
        var reading = OrganizeHistory.Read(longOne);
        var parts = Json.Deserialize<List<Dictionary<string, object>>>(reading.Context);
        Check(((string)parts[0]["text"]).StartsWith("最初的需求") && (string)parts[1]["role"] == "note" && ((string)parts.Last()["text"]).StartsWith("第 29 条"), "context does not hold the first request and the latest messages");
        Check(parts.Count <= 2 + OrganizeHistory.RecentMessages && parts.Sum(x => ((string)x["text"]).Length) <= OrganizeHistory.MostChars + 20, "context is larger than its limit");
        Console.WriteLine("context: first request + note + last " + OrganizeHistory.RecentMessages + " messages at most (" + (parts.Count - 2) + " here), " + reading.Context.Length + " characters for 30 messages of 900+ characters");

        ApiStandIn(root, project, read);
        Console.WriteLine("Windows organizer passed");
    }

    /// The API source against a local OpenAI-compatible stand-in, through the organizer.
    static void ApiStandIn(string root, string project, Func<long, Organizer.Source[]> read) {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string asked = "";
        var server = Task.Run(() => {
            using (var client = listener.AcceptTcpClient())
            using (var stream = client.GetStream()) {
                var head = new StringBuilder();
                var reader = new BinaryReader(stream);
                while (!head.ToString().EndsWith("\r\n\r\n")) head.Append((char)reader.ReadByte());
                int length = Int32.Parse(head.ToString().Split('\n').First(l => l.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim());
                asked = head + Encoding.UTF8.GetString(reader.ReadBytes(length));
                string content = "```json\n{\"title\":\"替身整理的标题\",\"summary\":\"替身服务返回的摘要\",\"tags\":[\"替身\"],\"follow_up\":false}\n```";
                byte[] body = Encoding.UTF8.GetBytes(Json.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } }, usage = new { prompt_tokens = 300, completion_tokens = 21, total_tokens = 321 } }));
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(header, 0, header.Length);
                stream.Write(body, 0, body.Length);
            }
        });
        var source = Fixture(project, 500, DateTime.UtcNow.AddDays(-1));
        var organizer = new Organizer(Path.Combine(root, "api-standin.json"), read, _ => true, _ => false, () => new string[0],
            c => OrganizeSources.Open(c, _ => null, _ => new Dictionary<string, object>[0], Path.Combine(root, "organize")));
        Op(organizer, "settings", null, "source", "api", "api_url", "http://127.0.0.1:" + port + "/v1/", "api_key", "sk-standin-42", "model", "small-chat");
        Op(organizer, "start", source);
        organizer.Advance();
        Check(server.Wait(10000), "the stand-in was not called");
        listener.Stop();
        var row = Row(organizer, source);
        Check((string)row["state"] == Organizer.Done && (string)row["title"] == "替身整理的标题" && (string)row["model"] == "small-chat", "API result not used: " + Json.Serialize(row));
        Check(asked.StartsWith("POST /v1/chat/completions ") && asked.Contains("Authorization: Bearer sk-standin-42") && asked.Contains("\"model\":\"small-chat\""), "API request has the wrong shape");
        Check(!asked.Contains("\"tools\"") && asked.Contains("社区图书馆"), "API request carried tools or missed the conversation");
        var status = Op(organizer, "status");
        Check(Number(status, "calls_today") == 1 && Convert.ToInt64(status["tokens_today"]) == 321, "API usage was not counted");
        Console.WriteLine("API stand-in: POST /v1/chat/completions with bearer key, model small-chat -> " + Json.Serialize(row["title"]) + ", tokens_today " + status["tokens_today"]);
    }

    /// Each tool installed on this computer, once, on a made-up conversation.
    static void Live(string root, string project) {
        var source = Fixture(project, 1, DateTime.UtcNow.AddHours(-1));
        // the fixture is read from its own file; the tools run with their owner's own settings and sign-in
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
        Environment.SetEnvironmentVariable("AGY_HOME", null);
        string scratch = Path.Combine(root, "isolated-model");
        string sessions = Path.Combine(CodexSessions.Home, "sessions");
        var before = new HashSet<string>(Directory.Exists(sessions) ? Directory.GetFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories) : new string[0]);
        Directory.CreateDirectory(Path.Combine(root, "models"));
        var models = new Models(TerminalAgent.FindTool, (tool, launcher, ms) => Chat.ListModels(tool, launcher, Path.Combine(root, "models"), ms));
        Func<string, Dictionary<string, object>[]> listed = tool => (Dictionary<string, object>[])((Dictionary<string, object>)models.Listed(only: tool)["models"])[tool];
        var installed = OrganizeSources.Tools.Where(t => TerminalAgent.FindTool(t) != null).ToArray();
        foreach (string tool in OrganizeSources.Tools) {
            if (!installed.Contains(tool)) { Console.WriteLine(tool + ": not installed on this computer"); continue; }
            var organizer = new Organizer(Path.Combine(root, "live-" + tool + ".json"), _ => new[] { source }, _ => true, _ => false, () => installed,
                c => OrganizeSources.Open(c, TerminalAgent.FindTool, listed, scratch));
            var status = Op(organizer, "settings", null, "source", "tool", "source_tool", tool);
            var started = DateTime.UtcNow;
            Op(organizer, "start", source);
            organizer.Advance();
            var row = Row(organizer, source);
            status = Op(organizer, "status");
            Console.WriteLine(tool + " (" + status["model"] + ", " + (int)(DateTime.UtcNow - started).TotalSeconds + " s, calls " + status["calls_today"] + ", tokens " + (status.ContainsKey("tokens_today") ? status["tokens_today"] : "-") + "): "
                + Json.Serialize(new { state = row["state"], title = row["title"], summary = row["summary"], tags = row["tags"], follow_up = row["follow_up"], error = row["error"] }));
            Check((string)row["state"] == Organizer.Done, tool + ": " + Organizer.Text(row, "error"));
            Check(Organizer.Text(row, "title").Contains("雨水") || Organizer.Text(row, "summary").Contains("雨水"), tool + ": response is unrelated to the example");
            Check(Number(Op(organizer, "start", source), "queued") == 0, tool + ": unchanged conversation was queued again");
        }
        if (Directory.Exists(sessions)) foreach (string path in Directory.GetFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories).Where(p => !before.Contains(p)))
            Check(Transcripts.ReadPart(path, false, 160 * 1024).IndexOf("isolated-model", StringComparison.OrdinalIgnoreCase) < 0, "temporary Codex conversation was persisted");
    }

    static int Main(string[] args) {
        Console.OutputEncoding = new UTF8Encoding(false);
        string root = args[0], project = Path.Combine(root, "sample-project");
        Directory.CreateDirectory(project);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(root, "claude"));
        Environment.SetEnvironmentVariable("AGY_HOME", Path.Combine(root, "agy"));
        try {
            if (args.Length > 1 && args[1] == "live") Live(root, project);
            else Offline(root, project);
            return 0;
        } catch (Exception failure) {
            Console.Error.WriteLine(failure.GetType().Name + ": " + failure.Message + Environment.NewLine + failure.StackTrace);
            return 1;
        }
    }
}
