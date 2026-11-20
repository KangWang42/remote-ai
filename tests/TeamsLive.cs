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
/// Costs a few turns of each tool's account; not in CI.
public static class TeamsLive {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    static string Git(string folder, string args) {
        var info = new ProcessStartInfo("git", "-C \"" + folder + "\" " + args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false) };
        using (var p = Process.Start(info)) { string said = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); return said; }
    }
    public static int Main(string[] args) {
        string mode = args[0], folder = args[1];
        var tools = args.Skip(2).ToArray();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "add.py"), "def add(a, b):\n    return a - b\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "README.md"), "# demo\n", new UTF8Encoding(false));
        Git(folder, "init -q"); Git(folder, "add -A"); Git(folder, "-c user.name=t -c user.email=t@t commit -q -m start");
        foreach (string key in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT", "CLAUDE_CODE_CHILD_SESSION" }) Environment.SetEnvironmentVariable(key, null);
        var chats = new Dictionary<string, Chat>();
        string tight = Environment.GetEnvironmentVariable("TEAMS_TIGHT") ?? "";
        Teams teams = null;
        var hands = new Teams.Hands {
            Begin = (tool, dir, words, read) => {
                string id = Guid.NewGuid().ToString("N");
                var chat = Chat.Open(tool, TerminalAgent.FindTool(tool), folder, "", false, read ? "read" : "full", text => { });
                chat.Turned += (how, said) => { Console.WriteLine("[" + tool + "] " + how + ": " + said.Replace("\n", " ").Substring(0, Math.Min(200, said.Length))); teams.Turned(id, how, said); };
                lock (chats) chats[id] = chat;
                chat.Say(words, null);
                Console.WriteLine("[begin " + tool + (read ? " read" : "") + "]");
                return id;
            },
            Say = (terminal, words) => { Chat chat; lock (chats) chat = chats[terminal]; chat.Say(words, null); Console.WriteLine("[told " + chat.Tool + "] " + words.Replace("\n", " ").Substring(0, Math.Min(120, words.Length))); },
            Stop = terminal => { },
            Limits = tool => new[] { new Quotas.Window { Name = "five_hour", Used = tool == tight ? 96 : 15 }, new Quotas.Window { Name = "seven_day", Used = tool == tight ? 88 : 30 } },
            Changes = dir => { string files = Git(folder, "status --porcelain --untracked-files=all"); return files.Trim().Length == 0 ? "" : "文件：\n" + files + "\n" + Git(folder, "-c core.quotepath=false diff HEAD --no-color"); } };
        teams = new Teams(null, hands);
        string goal = mode == "review" ? "修复 add.py 里 add 函数的错误（它应该返回两数之和），并新建 test_add.py，用 assert 写两个简单的测试。不要改动其它文件。"
            : "在这个项目里：1）修复 add.py 里 add 函数的错误（应返回两数之和）；2）在 README.md 里加一段“用法”说明 add 函数怎么用。两件事分给不同的成员做。";
        var team = teams.Start(new Dictionary<string, object> { { "id", Guid.NewGuid().ToString("N") }, { "mode", mode }, { "dir", "demo" }, { "goal", goal }, { "tools", tools }, { "rounds", 3 } });
        var deadline = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < deadline) {
            team = teams.All().Single(t => t.Id == team.Id);
            if (team.State != "running") break;
            Thread.Sleep(2000);
        }
        foreach (var step in team.Log) Console.WriteLine("  log " + step.Who + ": " + step.Text.Replace("\n", " ").Substring(0, Math.Min(160, step.Text.Length)));
        lock (chats) foreach (var chat in chats.Values) chat.Dispose();
        Console.WriteLine("state " + team.State + " · " + team.Note + " · round " + team.Round);
        Require(team.State == "done", "it ended: " + team.State + " " + team.Note);
        string added = File.ReadAllText(Path.Combine(folder, "add.py"));
        Require(added.Contains("a + b"), "the work was done: " + added);
        if (tight.Length > 0) {
            int given = team.Log.Count(step => step.Who == tight);
            Console.WriteLine("steps of the tight member " + tight + ": " + given);
            Require(given <= 2, "the member whose limits are nearly spent was given little: " + given + " steps");
        }
        Console.WriteLine("teams on the real tools: " + mode + " with " + String.Join(", ", tools) + " went to its end");
        return 0;
    }
}
