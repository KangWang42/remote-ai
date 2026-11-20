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

            // ---- 领队 weighs the members' limits: told them at the start and with every round's results
            long later = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds + 3600;
            double codexUsed = 93;
            var limits = new Teams.Hands { Begin = Hands().Begin, Say = Hands().Say, Stop = t => { }, Changes = d => "",
                Limits = tool => tool == "codex" ? new[] { new Quotas.Window { Name = "five_hour", Used = codexUsed, Resets = later }, new Quotas.Window { Name = "seven_day", Used = 40 } }
                    : tool == "claude" ? new[] { new Quotas.Window { Name = "five_hour", Used = 12 } } : new Quotas.Window[0] };
            var weighed = new Teams(null, limits);
            string w = "0808080808080808";
            weighed.Start(new Dictionary<string, object> { { "id", w }, { "mode", "lead" }, { "dir", "web-shop" }, { "goal", "加搜索" }, { "tools", new object[] { "claude", "codex", "agy" } } });
            string wLeader = Team(weighed, w).Members["leader"];
            Require(Last(wLeader).Contains("- codex：Codex，") && Last(wLeader).Contains("；额度：5 小时窗口已用 93%（") && Last(wLeader).Contains("本周已用 40%，紧张")
                && Last(wLeader).Contains("- agy：Antigravity CLI") && Last(wLeader).Contains("额度未知") && Last(wLeader).Contains("你自己（Claude Code）的额度：5 小时窗口已用 12%")
                && Last(wLeader).Contains("按额度分配工作量"), "the leader is told each member's limits and its own, a tight one called so: " + Last(wLeader));
            Require(!Last(leader).Contains("额度"), "without a way to read limits nothing of them is said");
            weighed.Turned(wLeader, "done", "ASSIGN agy: 做搜索框");
            codexUsed = 99;
            weighed.Turned(Team(weighed, w).Members["agy"], "done", "搜索框好了");
            Require(Last(wLeader).Contains("## 当前额度") && Last(wLeader).Contains("- Claude Code（你）：额度：5 小时窗口已用 12%") && Last(wLeader).Contains("- Codex：额度：5 小时窗口已用 99%"), "the results come with the limits as they are now");
            Require(Teams.Room(new[] { new Quotas.Window { Name = "seven_day", Used = 79.4 } }) == "额度：本周已用 79%" && Teams.Room(new[] { new Quotas.Window { Name = "300m", Used = 79.6 } }) == "额度：300m已用 80%，紧张" && Teams.Room(null) == "额度未知", "limits in words, tight from 80% used");

            // ---- Item 3: user interjects mid-run (`Say`), resumes paused with guidance, and continues after `done`
            string follow = "0909090909090909";
            teams.Start(new Dictionary<string, object> { { "id", follow }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "做商品筛选" }, { "tools", new object[] { "claude", "codex" } }, { "rounds", 1 } });
            string fw = Team(teams, follow).Members["writer"];
            teams.Say(follow, "筛选条件还要支持按价格排序", "");
            Require(Last(fw).Contains("【用户插话】") && Last(fw).Contains("按价格排序"), "interject delivered to active writer");
            Require(Team(teams, follow).Log.Any(s => s.Who == "user" && s.Text.Contains("按价格排序")), "interject logged as user");
            teams.Turned(fw, "done", "已加上筛选和价格排序");
            string fr = Team(teams, follow).Members["reviewer"];
            teams.Turned(fr, "done", "REVIEW: PASS\n没问题");
            Require(Team(teams, follow).State == "done", "initial review done");
            // Continue after done: adds round budget and sends follow-up to writer
            var continued = teams.Say(follow, "再加一个库存仅看有货开关", "");
            Require(continued.State == "running" && continued.Round == 2 && continued.Rounds >= 2 && Last(fw).Contains("库存仅看有货开关"), "continued after done into next round");
            teams.Turned(fw, "done", "加上了仅看有货开关");
            teams.Turned(fr, "done", "REVIEW: PASS\n通过");
            Require(Team(teams, follow).State == "done" && Team(teams, follow).Note.Contains("第 2 轮"), "follow-up round completed");

            // ---- Item 4a: discuss first (read-only), then act (`lead` with 3 AIs)
            string disc = "1010101010101010";
            teams.Start(new Dictionary<string, object> { { "id", disc }, { "mode", "lead" }, { "dir", "web-shop" }, { "goal", "重构购物车" }, { "tools", new object[] { "codex", "claude", "agy" } }, { "discuss", true } });
            var dt = Team(teams, disc);
            Require(dt.Phase == "discuss" && dt.Awaited.OrderBy(x => x).SequenceEqual(new[] { "discuss:agy", "discuss:claude" }), "discuss phase awaits both members read-only");
            string dClaude = dt.Members["discuss:claude"], dAgy = dt.Members["discuss:agy"];
            Require(tools[dClaude] == "claude:read" && tools[dAgy] == "agy:read" && Last(dClaude).Contains("方案讨论（只读）"), "discussants run in read-only mode");
            teams.Turned(dClaude, "done", "建议拆出 CartStore 模块");
            Require(Team(teams, disc).Phase == "discuss", "still discussing while second member works");
            teams.Turned(dAgy, "done", "建议底部栏保留快速结算按钮");
            dt = Team(teams, disc);
            Require(dt.Phase == "" && dt.Members.ContainsKey("leader") && Last(dt.Members["leader"]).Contains("CartStore") && Last(dt.Members["leader"]).Contains("快速结算按钮"), "leader receives both discussion proposals before assigning");

            // ---- Item 4b: parallel compare in isolated worktrees + refine (`Say`) + pick/discard (`Pick`)
            var wtDirs = new Dictionary<string, string>();
            var appliedTo = new List<string>();
            var cleaned = new List<string>();
            var cmpHands = new Teams.Hands {
                Begin = Hands().Begin,
                BeginIn = (tool, dir, words, read, wtDir) => {
                    string t = "t" + (++made);
                    tools[t] = tool + (read ? ":read" : "") + (String.IsNullOrEmpty(wtDir) ? "" : "@" + wtDir);
                    told.Add(new[] { t, tool, words });
                    return t;
                },
                Say = Hands().Say, Stop = Hands().Stop, Changes = d => "",
                MakeWorktree = (dir, tid, tool) => { string p = "/wt/" + tid + "/" + tool; wtDirs[tid + ":" + tool] = p; return new Worktrees.Created { Path = p, BaseCommit = "abc1234" }; },
                WorktreeDiff = (p, b) => "diff for " + p,
                WorktreeStat = (p, b) => "1 个文件改动：" + Path.GetFileName(p),
                ApplyWorktree = (dir, p, b) => { appliedTo.Add(p); return "1 个文件改动"; },
                CleanWorktrees = (dir, tid) => cleaned.Add(tid)
            };
            var cmpTeams = new Teams(null, cmpHands);
            string cmpId = "1212121212121212";
            cmpTeams.Start(new Dictionary<string, object> { { "id", cmpId }, { "mode", "compare" }, { "dir", "web-shop" }, { "goal", "优化商品卡片布局" }, { "tools", new object[] { "claude", "codex", "agy" } } });
            var ct = Team(cmpTeams, cmpId);
            Require(ct.State == "running" && ct.Awaited.OrderBy(x => x).SequenceEqual(new[] { "agy", "claude", "codex" }), "compare launches all 3 candidates in parallel");
            Require(tools[ct.Members["claude"]].EndsWith("@/wt/" + cmpId + "/claude") && tools[ct.Members["codex"]].EndsWith("@/wt/" + cmpId + "/codex"), "each candidate runs in its own worktree folder");
            cmpTeams.Turned(ct.Members["claude"], "done", "Claude 方案：紧凑网格");
            cmpTeams.Turned(ct.Members["codex"], "done", "Codex 方案：分层卡片");
            Require(Team(cmpTeams, cmpId).State == "running", "still running while agy works");
            cmpTeams.Turned(ct.Members["agy"], "done", "Agy 方案：留白卡片");
            ct = Team(cmpTeams, cmpId);
            Require(ct.State == "choose" && ct.Diffs["codex"] == "diff for /wt/" + cmpId + "/codex" && ct.Summaries["agy"].Contains("留白卡片"), "all done -> choose with diffs and summaries");
            // User refines one candidate before picking
            cmpTeams.Say(cmpId, "把圆角改小一点", "agy");
            Require(Team(cmpTeams, cmpId).State == "running" && Last(ct.Members["agy"]).Contains("圆角改小一点"), "refine targeted candidate");
            cmpTeams.Turned(ct.Members["agy"], "done", "已改小圆角");
            Require(Team(cmpTeams, cmpId).State == "choose", "back to choose after refinement");
            // User picks codex -> applies worktree & cleans all worktrees
            var picked = cmpTeams.Pick(cmpId, "codex");
            Require(picked.State == "done" && picked.Picked == "codex" && appliedTo.Contains("/wt/" + cmpId + "/codex") && cleaned.Contains(cmpId), "pick applies chosen worktree and cleans up");
            bool refusedAfterPick = false;
            try { cmpTeams.Say(cmpId, "再来一次", ""); } catch (InvalidOperationException) { refusedAfterPick = true; }
            Require(refusedAfterPick, "cannot continue compare after worktrees cleaned");
            // Discard all candidates cleans up without applying
            string discCmp = "1313131313131313";
            cmpTeams.Start(new Dictionary<string, object> { { "id", discCmp }, { "mode", "compare" }, { "dir", "web-shop" }, { "goal", "实验布局" }, { "tools", new object[] { "claude", "codex" } } });
            var dc = Team(cmpTeams, discCmp);
            cmpTeams.Turned(dc.Members["claude"], "done", "方案 A");
            cmpTeams.Turned(dc.Members["codex"], "done", "方案 B");
            int appliedBefore = appliedTo.Count;
            var discarded = cmpTeams.Pick(discCmp, "");
            Require(discarded.State == "done" && discarded.Picked == "" && appliedTo.Count == appliedBefore && cleaned.Contains(discCmp), "discard cleans worktrees without applying");
        } finally { try { Directory.Delete(folder, true); } catch { } }
        Console.WriteLine("teams: review, lead, discuss-first, parallel compare/pick, interject/continue, quotas, stop/resume and persistence ok");
        return 0;
    }
}
