using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    static Organizer.Source Fixture(string project, int number, DateTime at, bool named = false) {
        string id = new Guid(number, 0, 0, new byte[8]).ToString();
        string folder = Path.Combine(ClaudeSessions.Home, "projects", TerminalAgent.ProjectFolder(project));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, id + ".jsonl");
        var rows = new List<string> {
            Json.Serialize(new { type = "user", timestamp = at.ToString("o"), cwd = project,
                message = new { role = "user", content = "为社区图书馆设计雨水收集与灌溉方案。" } }),
            Json.Serialize(new { type = "assistant", timestamp = at.ToString("o"),
                message = new { role = "assistant", content = "建议用储水桶和滴灌，下一步测量屋顶面积并确认预算。" } })
        };
        if (named) rows.Add(Json.Serialize(new { type = "custom-title", customTitle = "工具里的人工名称" }));
        File.WriteAllLines(file, rows, new UTF8Encoding(false));
        File.SetCreationTimeUtc(file, at);
        File.SetLastWriteTimeUtc(file, at);
        return new Organizer.Source { Id = id, Tool = "claude", Dir = "demo", Folder = project, File = file,
            Title = named ? "工具里的人工名称" : "原始名称", Named = named, Created = Organizer.Now(at), Updated = Organizer.Now(at) };
    }

    static Dictionary<string, object> Op(Organizer organizer, string verb, Organizer.Source source = null, string field = null, object value = null) {
        var request = new Dictionary<string, object> { { "verb", verb } };
        if (source != null) { request["session"] = source.Id; request["tool"] = source.Tool; }
        if (field != null) request[field] = value;
        return organizer.Operate(request);
    }

    static int Number(Dictionary<string, object> values, string field) { return Convert.ToInt32(values[field]); }
    static Dictionary<string, object> Row(Organizer organizer, Organizer.Source source) {
        return ((Dictionary<string, object>[])Op(organizer, "list", source)["items"]).Single();
    }

    static Dictionary<string, object>[] ModelsFound() {
        return new[] { new Dictionary<string, object> { { "id", Organizer.DefaultModel }, { "label", "GPT-6 Luna" } } };
    }

    static Organizer.Metadata Result() {
        return new Organizer.Metadata { Title = "图书馆雨水灌溉", Summary = "汇总雨水收集方案", Tags = new[] { "雨水", "灌溉" }, FollowUp = true };
    }

    static void Offline(string root, string project) {
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(root, "codex"));
        var at = DateTime.UtcNow.AddDays(-1);
        var first = Fixture(project, 1, at, true);
        var second = Fixture(project, 2, at);
        var sources = Enumerable.Range(3, 130).Select(number => Fixture(project, number, at))
            .Concat(new[] { first, second }).ToArray();
        var projects = new Dictionary<string, string> { { "demo", project } };
        Func<long, Organizer.Source[]> read = since => OrganizeHistory.List(projects, new TerminalAgent.SessionInfo[0], since);
        string file = Path.Combine(root, "queue.json");
        int calls = 0;
        var entered = new ManualResetEventSlim();
        var released = new ManualResetEventSlim();
        Func<Organizer.Source, string, Organizer.Metadata> classify = (s, model) => {
            Check(model == Organizer.DefaultModel, "wrong default model");
            Interlocked.Increment(ref calls);
            if (s.Id == first.Id) {
                entered.Set();
                Check(released.Wait(5000), "the test did not release the request");
            } else if (s.Id == second.Id && calls == 2) throw new TimeoutException();
            return Result();
        };
        Func<Organizer> open = () => new Organizer(file, read, s => File.Exists(s.File), _ => false, ModelsFound, classify);
        var organizer = open();
        Check(!(bool)Op(organizer, "status")["auto"], "automatic mode is enabled on first run");
        Check(Number(Op(organizer, "list", field: "offset", value: 120), "total") == 132, "history enumeration follows the homepage limit");
        Check(((Dictionary<string, object>[])Op(organizer, "list", field: "offset", value: 120)["items"]).Length == 12, "pagination missed older records");
        Op(organizer, "start", first);
        organizer = open();
        var worker = Task.Run(() => organizer.Advance());
        Check(entered.Wait(5000), "queued work did not survive reopening the store");
        try {
            var competing = Task.Run(() => organizer.Advance());
            var duplicates = Task.Run(() => Op(organizer, "start", first));
            var rename = Task.Run(() => Op(organizer, "edit", first, "title", "运行期间手工起名"));
            Task.WaitAll(competing, duplicates, rename);
            Check(Number(duplicates.Result, "queued") == 0 && calls == 1, "same conversation started more than once");
            Check((string)Row(organizer, first)["state"] == "running", "in-flight item not shown as running");
            Check((string)Row(open(), first)["state"] == Organizer.Queued, "interrupted in-flight work will not resume after restart");
        } finally { released.Set(); }
        worker.GetAwaiter().GetResult();
        Check(organizer.Title("claude", first.Id, first.Title, true) == "运行期间手工起名", "manual title overwritten");
        Check((bool)Row(organizer, first)["follow_up"], "metadata was not written");
        organizer = open();
        Check(Number(Op(organizer, "start", first), "queued") == 0 && calls == 1, "completed item ran again after reopening");
        Op(organizer, "start", second);
        organizer.Advance();
        Check((string)Row(organizer, second)["state"] == Organizer.Failed, "failure incorrectly marked as successful");
        organizer = open();
        Check(Number(Op(organizer, "retry", second), "queued") == 1, "failed item cannot be retried");
        organizer.Advance();
        Check((string)Row(organizer, second)["state"] == Organizer.Done, "retry did not complete");
        Check(organizer.Title("claude", second.Id, second.Title, true) == second.Title, "native title overwritten");
        var saved = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
        var entries = (Dictionary<string, object>)saved["Entries"];
        ((Dictionary<string, object>)entries[second.Key]).Remove("ManualFollowUp");
        File.WriteAllText(file, Json.Serialize(saved), new UTF8Encoding(false));
        organizer = open();
        Check((bool)Row(organizer, second)["follow_up"], "old records lost the model's follow-up value");
        Check(Number(Op(organizer, "start", field: "scope", value: "all"), "queued") == 130, "batch missed older entries");

        long current = Organizer.Now(DateTime.UtcNow);
        var automatic = new Organizer(Path.Combine(root, "automatic.json"), read, _ => true, _ => false, ModelsFound,
            (s, model) => { Interlocked.Increment(ref calls); return Result(); }, () => current);
        Op(automatic, "settings", field: "auto", value: true);
        var fresh = Fixture(project, 200, DateTime.UtcNow.AddSeconds(1));
        File.SetLastWriteTimeUtc(first.File, DateTime.UtcNow.AddSeconds(1));
        automatic.Advance();
        Check((string)Row(automatic, fresh)["state"] == Organizer.Done, "new conversation was not automatically organized");
        Check((string)Row(automatic, first)["state"] == "pending", "resumed older conversation was automatically organized");
        current += 16000;
        automatic.Advance();
        Check(calls == 4, "completed automatic work ran again");
        Console.WriteLine("Windows organizer passed: 132 readable files, concurrent enqueue, restart, failure/retry, manual and native titles, automatic eligibility");
    }

    static void Live(string root, string project) {
        var source = Fixture(project, 1, DateTime.UtcNow);
        string state = Path.Combine(root, "live.json");
        string scratch = Path.Combine(root, "isolated-model");
        string sessions = Path.Combine(CodexSessions.Home, "sessions");
        var before = new HashSet<string>(Directory.Exists(sessions) ? Directory.GetFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories) : new string[0]);
        int calls = 0;
        var organizer = new Organizer(state, _ => new[] { source }, _ => true, _ => false, ModelsFound, (s, model) => {
            calls++;
            return CodexOrganizer.Analyze(TerminalAgent.FindTool("codex"), scratch, model, OrganizeHistory.Context(s));
        });
        Op(organizer, "start", source);
        organizer.Advance();
        var row = Row(organizer, source);
        Check((string)row["state"] == Organizer.Done, Organizer.Text(row, "error"));
        Check((bool)row["follow_up"] && ((string[])row["tags"]).Length > 0, "real response missed follow-up or topics");
        Check(Organizer.Text(row, "title").Contains("雨水") && Organizer.Text(row, "summary").Contains("雨水"), "response is unrelated to the isolated example");
        Check(Number(Op(organizer, "start", source), "queued") == 0 && calls == 1, "live request was repeated");
        if (Directory.Exists(sessions)) foreach (string path in Directory.GetFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories).Where(p => !before.Contains(p)))
            Check(Transcripts.ReadPart(path, false, 160 * 1024).IndexOf("isolated-model", StringComparison.OrdinalIgnoreCase) < 0, "temporary model conversation was persisted");
        Console.WriteLine(Json.Serialize(row));
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
