using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RemoteCli;

/// Work several AIs do together (agent-windows/Teams.cs), with its members played: what each is told and when, how
/// what they say is read, and what is kept when the program starts again.
public static class TeamsCheck {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    static readonly List<string[]> told = new List<string[]>();         // [terminal, tool or "", words]
    static readonly Dictionary<string, string> tools = new Dictionary<string, string>();
    static readonly List<string> stopped = new List<string>();
    static string changes = "";
    static int made;
    static Teams.Hands Hands() {
        return new Teams.Hands {
            Begin = (tool, dir, words, read) => { string t = "t" + (++made); tools[t] = tool + (read ? ":read" : ""); told.Add(new[] { t, tool, words }); return t; },
            Say = (terminal, words) => { if (terminal == "gone") throw new InvalidOperationException("这个成员的对话已经结束"); told.Add(new[] { terminal, "", words }); },
            Stop = terminal => stopped.Add(terminal),
            Changes = dir => changes };
    }
    static string Last(string terminal) { return told.Last(x => x[0] == terminal)[2]; }
    static Teams.Team Team(Teams teams, string id) { return teams.All().Single(t => t.Id == id); }

    public static int Main() {
        string folder = Path.Combine(Path.GetTempPath(), "remote-ai-teams-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "teams.dat");
        try {
            // ---- what is read of what the members say
            Require(Teams.Verdict("REVIEW: PASS\n没有问题") == true && Teams.Verdict("**REVIEW: CHANGES**\n1. …") == false && Teams.Verdict("\n\n  review：pass") == true && Teams.Verdict("看起来不错") == null, "the verdict, in the first lines");
            var tasks = Teams.Assigned("先这样分：\n- ASSIGN codex: 写测试\n**ASSIGN** agy：查文档\nASSIGN codex: 再跑一遍\nASSIGN gemini: 不在队里", new[] { "codex", "agy" });
            Require(tasks.Count == 2 && tasks["codex"] == "写测试\n再跑一遍" && tasks["agy"] == "查文档", "the tasks, of the members it has only");
            Require(Teams.Finished("都做完了。\nDONE: 已完成登录页") == "已完成登录页" && Teams.Finished("ASSIGN codex: x") == null, "done, with its summary");

            var teams = new Teams(file, Hands());
            // ---- refused
            foreach (var bad in new[] {
                new Dictionary<string, object> { { "id", "x" }, { "mode", "review" }, { "dir", "p" }, { "goal", "g" }, { "tools", new object[] { "claude", "codex" } } },
                new Dictionary<string, object> { { "id", "a1a1a1a1a1a1a1a1" }, { "mode", "vote" }, { "dir", "p" }, { "goal", "g" }, { "tools", new object[] { "claude", "codex" } } },
                new Dictionary<string, object> { { "id", "a1a1a1a1a1a1a1a1" }, { "mode", "review" }, { "dir", "p" }, { "goal", " " }, { "tools", new object[] { "claude", "codex" } } },
                new Dictionary<string, object> { { "id", "a1a1a1a1a1a1a1a1" }, { "mode", "review" }, { "dir", "p" }, { "goal", "g" }, { "tools", new object[] { "claude" } } },
                new Dictionary<string, object> { { "id", "a1a1a1a1a1a1a1a1" }, { "mode", "lead" }, { "dir", "p" }, { "goal", "g" }, { "tools", new object[] { "claude", "claude" } } } }) {
                bool refused = false; try { teams.Start(bad); } catch (ArgumentException) { refused = true; }
                Require(refused, "refused: " + string.Join(",", bad.Select(p => p.Key + "=" + p.Value)));
            }

            // ---- 写审: the writer works, the reviewer reads what changed, its findings go back, until it passes
            string review = "b2b2b2b2b2b2b2b2";
            var started = teams.Start(new Dictionary<string, object> { { "id", review }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "给结算页加上优惠券" }, { "tools", new object[] { "claude", "codex" } }, { "rounds", 3 } });
            Require(started.State == "running" && started.Members["writer"] == "t1" && tools["t1"] == "claude" && Last("t1").Contains("给结算页加上优惠券") && Last("t1").Contains("Codex") && started.Awaited.SequenceEqual(new[] { "writer" }), "the writer begins with the goal");
            Require(teams.Start(new Dictionary<string, object> { { "id", review }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "给结算页加上优惠券" }, { "tools", new object[] { "claude", "codex" } } }).Id == review && made == 1, "asked again under the same number: once");
            changes = "文件：\n M checkout.js\n\n+ coupon()";
            teams.Turned("t1", "done", "加好了 coupon()");
            var team = Team(teams, review);
            Require(team.Members["reviewer"] == "t2" && tools["t2"] == "codex:read" && Last("t2").Contains("加好了 coupon()") && Last("t2").Contains("+ coupon()") && Last("t2").Contains("REVIEW: PASS") && team.Awaited.SequenceEqual(new[] { "reviewer" }), "the reviewer, read only, sees the account and the changes");
            teams.Turned("t1", "done", "a turn of the owner's own");
            Require(Team(teams, review).Awaited.SequenceEqual(new[] { "reviewer" }), "a turn nobody awaited changes nothing");
            teams.Turned("t2", "done", "REVIEW: CHANGES\n1. 金额要四舍五入");
            team = Team(teams, review);
            Require(team.Round == 2 && Last("t1").Contains("金额要四舍五入") && Last("t1").Contains("第 2 轮") && team.Awaited.SequenceEqual(new[] { "writer" }), "the findings go back to the writer");
            teams.Turned("t1", "done", "改好了");
            teams.Turned("t2", "done", "**REVIEW: PASS**\n可以了");
            team = Team(teams, review);
            Require(team.State == "done" && team.Note.Contains("审查通过") && team.Awaited.Count == 0 && made == 2, "it passes: done");
            Require(team.Log.Any(s => s.Who == "reviewer" && s.Text.Contains("可以了")), "what each said is in its log");

            // ---- the rounds are spent
            string spent = "c3c3c3c3c3c3c3c3";
            teams.Start(new Dictionary<string, object> { { "id", spent }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "重写" }, { "tools", new object[] { "agy", "claude" } }, { "rounds", 1 } });
            string writer = Team(teams, spent).Members["writer"];
            teams.Turned(writer, "done", "写好了");
            teams.Turned(Team(teams, spent).Members["reviewer"], "done", "REVIEW: CHANGES\n还不行");
            Require(Team(teams, spent).State == "done" && Team(teams, spent).Note.Contains("已到 1 轮"), "no more rounds than were set");

            // ---- 领队: tasks go out at once, the results go back when every member has answered, until it says DONE
            string lead = "d4d4d4d4d4d4d4d4";
            teams.Start(new Dictionary<string, object> { { "id", lead }, { "mode", "lead" }, { "dir", "web-shop" }, { "goal", "做一个登录页" }, { "rules", "UI 交给 Gemini，逻辑交给 Claude" }, { "tools", new object[] { "claude", "codex", "agy" } }, { "rounds", 4 } });
            string leader = Team(teams, lead).Members["leader"];
            Require(Last(leader).Contains("ASSIGN 成员名") && Last(leader).Contains("- codex：Codex") && Last(leader).Contains("- agy：Antigravity CLI") && !Last(leader).Contains("- claude") && Last(leader).Contains("UI 交给 Gemini，逻辑交给 Claude"), "the leader is told its members by name and custom rules, without itself");
            teams.Turned(leader, "done", "我先想一下。");
            Require(Last(leader).Contains("没有在你的回答里找到分派"), "once reminded of the form");
            teams.Turned(leader, "done", "ASSIGN codex: 写登录接口\nASSIGN agy: 做登录页面");
            team = Team(teams, lead);
            string codex = team.Members["codex"], agy = team.Members["agy"];
            Require(tools[codex] == "codex" && tools[agy] == "agy" && Last(codex).Contains("写登录接口") && Last(agy).Contains("做登录页面") && team.Awaited.OrderBy(x => x).SequenceEqual(new[] { "agy", "codex" }), "each member is given its task");
            teams.Turned(codex, "done", "接口好了");
            Require(Team(teams, lead).Awaited.SequenceEqual(new[] { "agy" }) && !Last(leader).Contains("接口好了"), "the leader waits for every member");
            teams.Turned(agy, "done", "页面好了");
            Require(Last(leader).Contains("接口好了") && Last(leader).Contains("页面好了") && Last(leader).Contains("## Codex") && Team(teams, lead).Round == 2, "what they said goes back together");
            teams.Turned(leader, "done", "ASSIGN codex: 补测试");
            Require(Last(codex).Contains("补测试") && made == 7, "a member is told again in its own conversation");
            // a member stopped from the phone: the team waits; it goes on where it was
            teams.Turned(codex, "stopped", "");
            Require(Team(teams, lead).State == "paused" && Team(teams, lead).Note.Contains("停止"), "a member stopped: paused");
            teams.Resume(lead);
            Require(Team(teams, lead).State == "running" && Last(codex) == "继续完成上面的任务。", "resumed: told to go on");
            teams.Turned(codex, "done", "测试加好了");
            teams.Turned(leader, "done", "检查过了。\nDONE: 登录页和接口都完成了");
            Require(Team(teams, lead).State == "done" && Team(teams, lead).Note.Contains("登录页和接口都完成了"), "the leader says it is done");

            // ---- stopped by its owner: what is at work is stopped, and nothing more is passed on
            string halted = "e5e5e5e5e5e5e5e5";
            teams.Start(new Dictionary<string, object> { { "id", halted }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "整理代码" }, { "tools", new object[] { "codex", "claude" } } });
            string busy = Team(teams, halted).Members["writer"];
            teams.Stop(halted);
            Require(stopped.Contains(busy) && Team(teams, halted).State == "stopped", "stopped");
            int before = told.Count;
            teams.Turned(busy, "done", "做完了");
            Require(told.Count == before, "a stopped team passes nothing on");
            bool refusedRemove = false;
            string open = "f6f6f6f6f6f6f6f6";
            teams.Start(new Dictionary<string, object> { { "id", open }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "还在做" }, { "tools", new object[] { "codex", "claude" } } });
            try { teams.Remove(open); } catch (InvalidOperationException) { refusedRemove = true; }
            Require(refusedRemove && teams.Remove(halted), "one at work is not removed; one that stopped is");

            // ---- the program starts again: the tasks are there; one whose members were at work waits to go on
            var again = new Teams(file, Hands());
            var kept = again.All();
            Require(kept.Any(t => t.Id == review && t.State == "done") && kept.Single(t => t.Id == open).State == "paused" && kept.Single(t => t.Id == open).Note.Contains("重新启动"), "kept, and paused where it was at work");
            Require(!File.ReadAllText(file).Contains("还在做"), "kept encrypted");
            // its member's conversation is gone: it is begun anew
            var restarted = kept.Single(t => t.Id == open);
            Require(restarted.Members["writer"].Length > 0, "its member is known");
            int had = made;
            // (the member's terminal of before no longer answers)
            var gone = new Teams(file, new Teams.Hands { Begin = Hands().Begin, Say = (terminal, words) => { throw new InvalidOperationException("这个成员的对话已经结束"); }, Stop = t => { }, Changes = d => "" });
            gone.Resume(open);
            Require(made == had + 1 && gone.All().Single(t => t.Id == open).State == "running" && gone.All().Single(t => t.Id == open).Members["writer"] == "t" + made, "a member whose conversation is gone is begun anew");

            // a reviewer whose conversation is gone gets the diff and verdict format again
            string revLost = "0707070707070707";
            teams.Start(new Dictionary<string, object> { { "id", revLost }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "修缓存" }, { "tools", new object[] { "claude", "codex" } }, { "rounds", 2 } });
            teams.Turned(Team(teams, revLost).Members["writer"], "done", "修好了缓存");
            changes = "文件：\n M cache.js\n\n+ ttl()";
            var revAgain = new Teams(file, new Teams.Hands { Begin = Hands().Begin, Say = (terminal, words) => { throw new InvalidOperationException("这个成员的对话已经结束"); }, Stop = t => { }, Changes = d => changes });
            revAgain.Resume(revLost);
            string revTerminal = revAgain.All().Single(t => t.Id == revLost).Members["reviewer"];
            Require(tools[revTerminal] == "codex:read" && Last(revTerminal).Contains("REVIEW: PASS") && Last(revTerminal).Contains("+ ttl()"), "a restarted reviewer keeps read-only mode, diff and format");
        } finally { try { Directory.Delete(folder, true); } catch { } }
        Console.WriteLine("teams: a writer and a reviewer until it passes, a leader and its members until it is done; stopped, resumed and kept");
        return 0;
    }
}
