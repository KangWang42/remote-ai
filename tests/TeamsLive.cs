using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RemoteCli;

/// Several AIs at one piece of work on the real tools (Teams.cs, the members run as the phone runs them, Chats.cs), in
/// a small git repository made for it: what the members are told is tried with the models that read it.
///     TeamsLive.exe review|lead <empty folder> <tool> <tool> [<tool>...]
/// With TEAMS_TIGHT=<tool>, the leader is told that member's limits are nearly spent (the others' are not), and the
/// run passes only where that member is given no task or one task in the whole run.
/// With TEAMS_MODELS=<tool>=<model>,... those tools' members are begun with those models (names as `team_models`
/// lists them). In lead mode it passes only where the leader accepted the work with what it checked (`VERIFIED`).
/// With TEAMS_AUTO=1 (lead) the first tool leads alone and forms its team from the tools named (自动组队).
/// With TEAMS_SCENARIO=design-build (lead) the work is an interface design and its implementation in a small todo
/// page, the goal not saying who does what: it passes where the design document is written, `node test_todo.js`
/// passes and the leader accepted it. Whom the leader picked, the bounds of each task and what was asked back are
/// read from the transcript, written beside the folder as `<folder>.transcript.md` (every prompt and every answer).
/// Costs a few turns of each tool's account; not in CI.
public static class TeamsLive {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    static string Git(string folder, string args) {
        var info = new ProcessStartInfo("git", "-C \"" + folder + "\" " + args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false) };
        using (var p = Process.Start(info)) { string said = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); return said; }
    }
    static int Node(string folder, string script, out string said) {
        var info = new ProcessStartInfo("node", script) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false) };
        using (var p = Process.Start(info)) { said = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(); return p.ExitCode; }
    }
    static void Write(string folder, string name, string text) {
        string path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text.Replace("\r\n", "\n"), new UTF8Encoding(false));
    }
    const string DesignBuildGoal = "给待办页加“全部 / 未完成 / 已完成”筛选。需要两样成品：1）界面设计，写在 docs/filter-design.md：筛选控件放在哪里、各项文字、选中状态怎么显示、筛选后为空时显示什么；"
        + "2）按设计实现：todo.js 的 visible(items, filter) 和 todo.html 的筛选栏，并在 test_todo.js 里补上三种筛选的测试，用 node test_todo.js 验证。";
    static void DesignBuildProject(string folder) {
        Write(folder, "todo.html", @"<!doctype html>
<html lang=""zh-CN"">
<head><meta charset=""utf-8""><title>待办</title></head>
<body>
  <h1>待办</h1>
  <ul id=""list""></ul>
  <script src=""todo.js""></script>
</body>
</html>
");
        Write(folder, "todo.js", @"const items = [
  { text: '买牛奶', done: false },
  { text: '交报告', done: true },
  { text: '订机票', done: false },
];

function visible(list, filter) {
  return list;
}

function render(filter) {
  document.getElementById('list').innerHTML = visible(items, filter).map(item => '<li>' + item.text + '</li>').join('');
}

if (typeof document !== 'undefined') render('all');
if (typeof module !== 'undefined') module.exports = { items, visible };
");
        Write(folder, "test_todo.js", @"const assert = require('assert');
const { items, visible } = require('./todo.js');

assert.strictEqual(visible(items, 'all').length, 3);
console.log('ok');
");
        Write(folder, "README.md", "# 待办页\n\n打开 todo.html 查看，`node test_todo.js` 运行测试。\n");
    }
    public static int Main(string[] args) {
        string mode = args[0], folder = Path.GetFullPath(args[1]);
        var tools = args.Skip(2).ToArray();
        string scenario = Environment.GetEnvironmentVariable("TEAMS_SCENARIO") ?? "";
        bool auto = mode == "lead" && Environment.GetEnvironmentVariable("TEAMS_AUTO") == "1";
        bool designBuild = mode == "lead" && scenario == "design-build";
        Directory.CreateDirectory(folder);
        if (designBuild) DesignBuildProject(folder);
        else {
            File.WriteAllText(Path.Combine(folder, "add.py"), "def add(a, b):\n    return a - b\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "README.md"), "# demo\n", new UTF8Encoding(false));
        }
        Git(folder, "init -q"); Git(folder, "add -A"); Git(folder, "-c user.name=t -c user.email=t@t commit -q -m start");
        foreach (string key in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT", "CLAUDE_CODE_CHILD_SESSION" }) Environment.SetEnvironmentVariable(key, null);
        var chats = new Dictionary<string, Chat>();
        var transcript = new StringBuilder();
        Action<string, string> note = (head, text) => { lock (transcript) transcript.Append("\n## " + head + " · " + DateTime.Now.ToString("HH:mm:ss") + "\n\n" + text + "\n"); };
        string tight = Environment.GetEnvironmentVariable("TEAMS_TIGHT") ?? "";
        Teams teams = null;
        var hands = new Teams.Hands {
            Begin = one => {
                string id = Guid.NewGuid().ToString("N").Substring(0, 8), tool = one.Tool, words = one.Words; bool read = one.Read;
                var chat = Chat.Open(tool, TerminalAgent.FindTool(tool), folder, "", false, read ? "read" : "full", text => { }, null, null, one.Model, one.Effort);
                chat.Turned += (how, said) => {
                    note("回答 " + id + " (" + tool + ", " + how + ")", said);
                    Console.WriteLine("[" + tool + "] " + how + ": " + said.Replace("\n", " ").Substring(0, Math.Min(200, said.Length)));
                    teams.Turned(id, how, said);
                };
                lock (chats) chats[id] = chat;
                note("开始 " + id + " (" + tool + (read ? ", 只读" : "") + (one.Model.Length > 0 ? ", " + one.Model : "") + ")", words);
                chat.Say(words, null, one.Attachments);
                Console.WriteLine("[begin " + tool + (read ? " read" : "") + (one.Model.Length > 0 ? " " + one.Model : "") + "]");
                return id;
            },
            Say = (terminal, words) => {
                Chat chat; lock (chats) chat = chats[terminal];
                note("告诉 " + terminal + " (" + chat.Tool + ")", words);
                chat.Say(words, null);
                Console.WriteLine("[told " + chat.Tool + "] " + words.Replace("\n", " ").Substring(0, Math.Min(120, words.Length)));
            },
            SayWithAttachments = (terminal, words, attachments) => {
                Chat chat; lock (chats) chat = chats[terminal];
                string shown = Attachments.WithText(words, attachments);
                note("告诉 " + terminal + " (" + chat.Tool + ")", shown);
                chat.Say(words, null, attachments);
                Console.WriteLine("[told " + chat.Tool + "] " + shown.Replace("\n", " ").Substring(0, Math.Min(120, shown.Length)));
            },
            Stop = terminal => { },
            Limits = tool => new[] { new Quotas.Window { Name = "five_hour", Used = tool == tight ? 96 : 15 }, new Quotas.Window { Name = "seven_day", Used = tool == tight ? 88 : 30 } },
            Changes = dir => { string files = Git(folder, "status --porcelain --untracked-files=all"); return files.Trim().Length == 0 ? "" : "文件：\n" + files + "\n" + Git(folder, "-c core.quotepath=false diff HEAD --no-color"); } };
        if (auto) hands.Available = () => tools.Distinct().ToArray();
        teams = new Teams(null, hands);
        string goal = designBuild ? DesignBuildGoal
            : mode == "review" ? "修复 add.py 里 add 函数的错误（它应该返回两数之和），并新建 test_add.py，用 assert 写两个简单的测试。不要改动其它文件。"
            : "在这个项目里：1）修复 add.py 里 add 函数的错误（应返回两数之和）；2）在 README.md 里加一段“用法”说明 add 函数怎么用。两件事分给不同的成员做。";
        var models = (Environment.GetEnvironmentVariable("TEAMS_MODELS") ?? "").Split(',').Select(x => x.Split('=')).Where(x => x.Length == 2)
            .ToDictionary(x => x[0].Trim(), x => (object)x[1].Trim());
        var given = new Dictionary<string, object> { { "id", Guid.NewGuid().ToString("N") }, { "mode", mode }, { "dir", "demo" }, { "goal", goal }, { "tools", tools }, { "rounds", designBuild ? 4 : 3 }, { "models", models } };
        if (auto) {
            given["auto_roster"] = true;
            given["roster"] = new[] { new Dictionary<string, object> { { "role", "leader" }, { "tool", tools[0] }, { "model", models.ContainsKey(tools[0]) ? models[tools[0]] : "" } } };
        }
        var team = teams.Start(given);
        var deadline = DateTime.UtcNow.AddMinutes(designBuild ? 45 : 20);
        while (DateTime.UtcNow < deadline) {
            team = teams.All().Single(t => t.Id == team.Id);
            if (team.State != "running") break;
            Thread.Sleep(2000);
        }
        foreach (var step in team.Log) Console.WriteLine("  log " + step.Who + ": " + step.Text.Replace("\n", " ").Substring(0, Math.Min(160, step.Text.Length)));
        lock (chats) foreach (var chat in chats.Values) chat.Dispose();
        string roles = String.Join("\n", team.Members.Select(p => "- " + p.Key + " → " + p.Value + (team.Roster.Any(r => r.Role == p.Key) ? " (" + team.Roster.First(r => r.Role == p.Key).Tool + ")" : "")));
        string ending = "state " + team.State + " · " + team.Note + " · round " + team.Round;
        note("结束", ending + "\n\n角色与对话：\n" + roles + "\n\n文件夹改动：\n" + Git(folder, "status --porcelain --untracked-files=all"));
        File.WriteAllText(folder.TrimEnd('\\', '/') + ".transcript.md", "# 协作实测记录\n\n目标：" + goal + "\n" + transcript, new UTF8Encoding(false));
        Console.WriteLine(ending);
        Require(team.State == "done", "it ended: " + team.State + " " + team.Note);
        if (designBuild) {
            Require(File.Exists(Path.Combine(folder, "docs", "filter-design.md")), "the design was written");
            string tested;
            Require(Node(folder, "test_todo.js", out tested) == 0, "node test_todo.js passes: " + tested);
        } else {
            string added = File.ReadAllText(Path.Combine(folder, "add.py"));
            Require(added.Contains("a + b"), "the work was done: " + added);
        }
        if (mode == "lead") Require(team.Log.Any(step => step.Who == "验收" && step.Text != "领队表示已完成，开始单独验收"), "the leader accepted it with what it checked");
        if (tight.Length > 0) {
            int given2 = team.Log.Count(step => step.Who == tight);
            Console.WriteLine("steps of the tight member " + tight + ": " + given2);
            Require(given2 <= 2, "the member whose limits are nearly spent was given little: " + given2 + " steps");
        }
        Console.WriteLine("teams on the real tools: " + mode + " with " + String.Join(", ", tools) + " went to its end");
        return 0;
    }
}
