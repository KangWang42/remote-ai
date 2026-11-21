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
            // what a member is begun as: "<tool>[:read][@folder][#model][~effort]"
            Begin = one => { string t = "t" + (++made); tools[t] = one.Tool + (one.Read ? ":read" : "") + (one.Folder.Length > 0 ? "@" + one.Folder : "") + (one.Model.Length > 0 ? "#" + one.Model : "") + (one.Effort.Length > 0 ? "~" + one.Effort : ""); told.Add(new[] { t, one.Tool, one.Words }); return t; },
            Say = (terminal, words) => { if (terminal == "gone") throw new InvalidOperationException("这个成员的对话已经结束"); told.Add(new[] { terminal, "", words }); },
            Stop = terminal => stopped.Add(terminal),
            Changes = dir => changes };
    }
    static string Last(string terminal) { return told.Last(x => x[0] == terminal)[2]; }
    static Teams.Team Team(Teams teams, string id) { return teams.All().Single(t => t.Id == id); }
    static Dictionary<string, object> Lead(string id, object[] tools, int rounds) {
        return new Dictionary<string, object> { { "id", id }, { "mode", "lead" }, { "dir", "web-shop" }, { "goal", "修好购物车" }, { "tools", tools }, { "rounds", rounds } };
    }
    static Dictionary<string, object> Member(string role, string tool, string model = "", string effort = "") {
        var row = new Dictionary<string, object> { { "role", role }, { "tool", tool }, { "model", model } };
        if (!String.IsNullOrEmpty(effort)) row["effort"] = effort;
        return row;
    }
    static void Roster(string folder) {
        var hands = Hands();
        hands.Available = () => new[] { "claude", "codex", "agy" };
        hands.Limits = tool => tool == "agy" ? new[] { new Quotas.Window { Name = "five_hour", Used = 100 } } : null;
        string file = Path.Combine(folder, "roster.dat");
        var teams = new Teams(file, hands);
        string id = "3333333333333333";
        var preset = Lead(id, new object[] { "codex", "claude" }, 2);
        preset["roster"] = new[] { Member("leader", "codex", "gpt-6.2-sol", "high"), Member("member-1", "claude", "sonnet"), Member("member-2", "claude", "opus") };
        string leader = teams.Start(preset).Members["leader"];
        Require(tools[leader] == "codex#gpt-6.2-sol~high", "leader starts with chosen model and reasoning effort: " + tools[leader]);
        Require(Last(leader).Contains("- member-1：Claude Code") && Last(leader).Contains("- member-2：Claude Code"), "same tool offered as distinct members");
        teams.Turned(leader, "done", "ASSIGN member-1: 查数量\nASSIGN member-2: 查结账");
        var team = Team(teams, id);
        string first = team.Members["member-1"], second = team.Members["member-2"];
        Require(first != second && tools[first] == "claude#sonnet" && tools[second] == "claude#opus", "separate conversations and model choices");
        teams.Learn(first, "claude", "12345678-1111-4111-8111-123456789abc");
        teams.Learn(second, "claude", "12345678-2222-4222-8222-123456789abc");
        teams.Say(id, "同时看看边界情况", "leader");
        teams.Turned(leader, "done", "VERIFIED: 这是提前发出的回答\nDONE: 提前完成");
        Require(Team(teams, id).Awaited.SequenceEqual(new[] { "member-1", "member-2" }), "a message to the leader cannot overtake the member batch");
        teams.Say(id, "再看空购物车", "member-2");
        Require(Last(second).Contains("再看空购物车") && !Last(first).Contains("再看空购物车"), "a message reaches only its addressed member");
        var swap = teams.Swap(id, "member-2", "claude", "", "haiku");
        string replacement = swap.Members["member-2"];
        Require(swap.Members["member-1"] == first && replacement != second && tools[replacement] == "claude#haiku", "changing one member model leaves the other member in place");
        teams.Turned(first, "done", "数量检查完毕");
        teams.Turned(replacement, "done", "结账检查完毕");
        team = Team(teams, id);
        Require(team.Phase == "verify" && team.Round == 1 && Last(leader).Contains("同时看看边界情况")
            && Last(leader).Contains("数量检查完毕") && Last(leader).Contains("结账检查完毕"), "first verification gets all results and queued user input");
        teams.Turned(leader, "done", "发现空购物车结账异常。\nASSIGN member-2: 修复空购物车结账");
        Require(Team(teams, id).Round == 2 && Last(replacement).Contains("修复空购物车结账"), "a second dispatch advances to the second round");
        teams.Turned(replacement, "done", "已修复空购物车结账");
        Require(Team(teams, id).Last && Last(leader).Contains("第 2 轮"), "last member results still enter acceptance");
        teams.Turned(leader, "done", "VERIFIED: 复测空购物车、普通购物车，均通过\nDONE: 验收完成");
        var reloaded = Team(new Teams(file, hands), id);
        Require(reloaded.State == "done" && reloaded.Round == 2 && reloaded.Roster[0].Model == "gpt-6.2-sol" && reloaded.Roster[0].Effort == "high" && reloaded.Roster[2].Model == "haiku" &&
            reloaded.Sessions["member-1"].Id == "12345678-1111-4111-8111-123456789abc", "the member identities, chosen models and leader effort persist");
        // Follow-up round after restart reopens leader with the persisted model and effort
        var reopenedHands = Hands();
        reopenedHands.Available = hands.Available;
        reopenedHands.Say = (terminal, words) => { throw new InvalidOperationException("gone"); };
        var reopened = new Teams(file, reopenedHands);
        reopened.Say(id, "再做一轮补充检查", "");
        string nextLeader = Team(reopened, id).Members["leader"];
        Require(tools[nextLeader] == "codex#gpt-6.2-sol~high", "subsequent round after restart preserves leader model and effort: " + tools[nextLeader]);
        var leaderSwapped = reopened.Swap(id, "leader", "codex", "", "gpt-6.2-astra", "xhigh");
        Require(tools[leaderSwapped.Members["leader"]] == "codex#gpt-6.2-astra~xhigh" && leaderSwapped.Roster[0].Effort == "xhigh" &&
            leaderSwapped.Log.Any(s => s.Who == "切换" && s.Text.Contains("思考深度 xhigh")), "leader swap updates model and reasoning effort");
        var modelOnlySwapped = reopened.Swap(id, "leader", "codex", "", "gpt-5.4", "");
        Require(tools[modelOnlySwapped.Members["leader"]] == "codex#gpt-5.4" && modelOnlySwapped.Roster[0].Effort == "", "switching leader model with default effort clears stale effort");

        id = "4444444444444444";
        var automatic = Lead(id, new object[] { "codex" }, 1);
        automatic["auto_roster"] = true;
        automatic["roster"] = new[] { Member("leader", "codex", "gpt-6.2-sol", "medium") };
        var started = teams.Start(automatic);
        leader = started.Members["leader"];
        Require(tools[leader] == "codex#gpt-6.2-sol~medium", "auto-roster leader also receives chosen model and effort: " + tools[leader]);
        var autoShown = (Dictionary<string, string>[])Teams.Shown(started)["roster"];
        Require(autoShown.Length == 1 && autoShown[0]["model"] == "gpt-6.2-sol" && autoShown[0]["effort"] == "medium", "auto-roster leader effort visible in detail");
        var badAuto = Lead("4545454545454545", new object[] { "codex" }, 1);
        badAuto["auto_roster"] = true;
        badAuto["roster"] = new[] { Member("leader", "codex", "gpt-6.2-sol", "bad effort;") };
        bool refusedBadEffort = false;
        try { teams.Start(badAuto); } catch (ArgumentException) { refusedBadEffort = true; }
        Require(refusedBadEffort, "invalid leader effort refused");
        Require(started.Roster.Length == 1 && Last(leader).Contains("ASSIGN member-1 codex:") && Last(leader).Contains("共用"), "leader receives rules for forming the team itself");
        teams.Turned(leader, "done", "ASSIGN member-1 claude: 检查数量\nASSIGN member-2 claude: 检查价格\nASSIGN member-3 agy: 帮忙");
        team = Team(teams, id);
        Require(team.AutoRoster && team.Roster.Length == 3 && team.Members.Count == 3 &&
            team.Roster.Skip(1).All(member => member.Tool == "claude"), "automatic assignment permits repeated tools and skips exhausted providers");
        teams.Turned(team.Members["member-1"], "done", "数量正确");
        teams.Turned(team.Members["member-2"], "done", "价格正确");
        Require(Team(teams, id).Round == 1 && Team(teams, id).Phase == "verify", "automatic one-round team awaits its final leader check");
        teams.Turned(leader, "done", "VERIFIED: 对照两项输出，数量和价格正确\nDONE: 验收完成");
        Require(Team(teams, id).State == "done", "the leader can accept a one-round auto roster");
    }

    /// 领队 accepts the work in a turn of its own, with what it checked; nothing else ends it as done.
    static void Accepting(string file) {
        Require(Teams.Verified("VERIFIED: 运行 npm test，12 项全部通过\nDONE: 好了") == "运行 npm test，12 项全部通过", "what was checked, on its line");
        Require(Teams.Verified("**VERIFIED:**\n- dotnet build 无警告\n- 打开 /cart 页面，数量可改\nDONE: 好了") == "- dotnet build 无警告\n- 打开 /cart 页面，数量可改", "what was checked, on the lines under it up to DONE");
        Require(Teams.Verified("VERIFIED: 看过了\nDONE: 好了") == null && Teams.Verified("VERIFIED:\nDONE: 好了") == null && Teams.Verified("DONE: 好了") == null, "a word or nothing is no evidence");
        var teams = new Teams(Path.Combine(Path.GetDirectoryName(file), "accepting.dat"), Hands());

        // no member was given anything: the acceptance is still a turn of its own
        string alone = "2121212121212121";
        teams.Start(Lead(alone, new object[] { "codex", "claude" }, 3));
        string leader = Team(teams, alone).Members["leader"];
        int before = made;
        teams.Turned(leader, "done", "这件事不需要改代码。\nVERIFIED: 运行 npm test 全部通过\nDONE: 已确认无需修改");
        var team = Team(teams, alone);
        Require(team.State == "running" && team.Phase == "verify" && Last(leader).Contains("领队验收") && made == before, "a DONE in the turn that gives out work opens the acceptance, even with VERIFIED in it");
        teams.Turned(leader, "done", "VERIFIED: 看过了\nDONE: 好了");
        Require(Team(teams, alone).State == "running" && Last(leader).Contains("没有找到验收证据"), "reminded once of the evidence");
        teams.Turned(leader, "done", "DONE: 好了");
        team = Team(teams, alone);
        Require(team.State == "paused" && team.Note.Contains("验收证据") && team.Awaited.SequenceEqual(new[] { "leader" }), "again without evidence: it waits for its owner, not done");
        teams.Resume(alone);
        Require(Last(leader) == "继续完成上面的任务。" && Team(teams, alone).Phase == "verify", "resumed in the acceptance");
        teams.Turned(leader, "done", "VERIFIED: 运行 npm test，34 项通过；查看 cart.js 没有改动\nDONE: 已确认无需修改");
        Require(Team(teams, alone).State == "done", "accepted with evidence");

        // the acceptance finds something: the fix goes to a member and the leader accepts again
        string fixing = "2222222222222222";
        teams.Start(Lead(fixing, new object[] { "codex", "claude", "agy" }, 4));
        leader = Team(teams, fixing).Members["leader"];
        teams.Turned(leader, "done", "ASSIGN claude: 修购物车数量");
        string claude = Team(teams, fixing).Members["claude"];
        teams.Turned(claude, "done", "改好了");
        teams.Turned(leader, "done", "跑测试发现数量为 0 时报错。\nASSIGN agy: 复现数量为 0 的报错并修好，跑一遍 cart 测试");
        team = Team(teams, fixing);
        string agy = team.Members["agy"];
        Require(team.State == "running" && team.Round == 2 && team.Phase == "" && team.Awaited.SequenceEqual(new[] { "agy" }) && Last(agy).Contains("数量为 0"), "a fix found in the acceptance starts the next member round");
        teams.Turned(agy, "done", "已修复，cart 测试 8 项通过");
        Require(Last(leader).Contains("已修复，cart 测试 8 项通过") && Last(leader).Contains("领队验收") && Team(teams, fixing).Phase == "verify", "the fix comes back to be accepted again");
        teams.Turned(leader, "done", "VERIFIED: 自己再跑 cart 测试 8 项通过，数量为 0 时显示提示\nDONE: 购物车已修好");
        Require(Team(teams, fixing).State == "done", "accepted after the fix");

        // the rounds run out: the last acceptance gives nothing out, and wanting more is no success
        string capped = "2323232323232323";
        teams.Start(Lead(capped, new object[] { "codex", "claude" }, 1));
        leader = Team(teams, capped).Members["leader"];
        teams.Turned(leader, "done", "ASSIGN claude: 修购物车");
        teams.Turned(Team(teams, capped).Members["claude"], "done", "改了一半");
        team = Team(teams, capped);
        Require(team.State == "running" && team.Phase == "verify" && team.Last && Last(leader).Contains("最后一轮"), "the rounds spent: a last acceptance");
        before = made;
        teams.Turned(leader, "done", "还没好。\nASSIGN claude: 再改");
        team = Team(teams, capped);
        Require(team.State == "stopped" && team.Note.Contains("没有通过验收") && made == before && !team.Awaited.Any(), "more work wanted when no round is left: stopped, nothing given out");
        // the owner goes on with it: a new round, out of the acceptance
        teams.Say(capped, "再给一轮，把剩下的改完");
        team = Team(teams, capped);
        Require(team.State == "running" && team.Phase == "" && !team.Last && team.Rounds >= 2, "its owner goes on with it");

        // a leader that fails in the acceptance is followed by one that is told to accept
        string handed = "2424242424242424";
        var failHands = Hands(); failHands.Available = () => new[] { "codex", "claude", "agy" };
        var handing = new Teams(null, failHands);
        handing.Start(Lead(handed, new object[] { "codex", "claude" }, 3));
        leader = Team(handing, handed).Members["leader"];
        handing.Turned(leader, "done", "DONE: 好了");
        handing.Turned(leader, "failed", "You've hit your limit");
        team = Team(handing, handed);
        Require(team.State == "running" && team.Phase == "verify" && team.Tools[0] != "codex" && Last(team.Members["leader"]).Contains("领队验收") && Last(team.Members["leader"]).Contains("额度已用尽"), "the leader that takes over is told to accept");

        // kept across a restart
        Require(new Teams(Path.Combine(Path.GetDirectoryName(file), "accepting.dat"), Hands()).All().Single(t => t.Id == capped).Phase == "", "the phase is kept");
    }

    /// The model each tool's members are begun with: given with the task and with a swap, used for every
    /// conversation of that tool (begun anew too), and refused where it is not a safe name.
    static void Choosing(string file) {
        Require(Models.Valid("gpt-5.1-codex") && Models.Valid("sonnet[1m]") && Models.Valid("models/gemini-3-pro") && Models.Valid("claude-opus-4-1-20250805"), "the tools' names");
        Require(!Models.Valid("") && !Models.Valid("-x") && !Models.Valid("a b") && !Models.Valid("x\"y") && !Models.Valid("x;rm") && !Models.Valid("x&y") && !Models.Valid(new string('a', 81)), "nothing a command line could take otherwise");
        Require(Models.ValidEffort("low") && Models.ValidEffort("xhigh") && Models.ValidEffort("ultra") && !Models.ValidEffort("") && !Models.ValidEffort("HIGH") && !Models.ValidEffort("a b") && !Models.ValidEffort("x;y"), "effort validation");
        var clean = Models.Clean(new[] {
            new Models.Choice { Id = "gpt-5", Label = "GPT-5", Efforts = new[] { "low", "HIGH", "bad value", "low", "ultra" }, DefaultEffort = "medium", IsDefault = true },
            new Models.Choice { Id = "gpt-5", Label = "again" },
            new Models.Choice { Id = "bad id" },
            new Models.Choice { Id = "o3", Efforts = new[] { "medium", "high" }, DefaultEffort = "high", IsDefault = true }
        });
        Require(clean.Length == 2 && clean[0].Label == "GPT-5" && clean[0].IsDefault && clean[0].Efforts.SequenceEqual(new[] { "low", "high", "ultra" }) && clean[0].DefaultEffort == "" && clean[1].Label == "o3" && !clean[1].IsDefault && clean[1].DefaultEffort == "high", "each once, safe names and efforts only, named by its id where it has no name");

        int asked = 0;
        var listing = new Models(tool => tool == "agy" ? null : "C:\\" + tool + ".exe", (tool, launcher, ms) => {
            asked++;
            return tool == "codex" ? new[] { new Models.Choice { Id = "gpt-5.1-codex", Label = "GPT-5.1 Codex", Efforts = new[] { "low", "medium", "high" }, DefaultEffort = "medium", IsDefault = true }, new Models.Choice { Id = "x y" } } : null;
        });
        var listed = (Dictionary<string, object>)listing.Listed()["models"];
        var codexModels = (Dictionary<string, object>[])listed["codex"];
        Require(listed.Count == 2 && !listed.ContainsKey("agy") && codexModels.Length == 1 && (string)codexModels[0]["id"] == "gpt-5.1-codex" && (string)codexModels[0]["label"] == "GPT-5.1 Codex"
            && ((string[])codexModels[0]["efforts"]).SequenceEqual(new[] { "low", "medium", "high" }) && (string)codexModels[0]["default_effort"] == "medium" && (bool)codexModels[0]["is_default"]
            && ((Dictionary<string, object>[])listed["claude"]).Length == 0 && asked == 2, "installed tools only, as each lists them with efforts; one that says nothing offers its default only");
        listing.Listed();
        Require(asked == 2, "a list is kept a while");

        var teams = new Teams(Path.Combine(Path.GetDirectoryName(file), "choosing.dat"), Hands());
        foreach (var bad in new object[] { new Dictionary<string, object> { { "codex", "gpt 5" } }, new Dictionary<string, object> { { "agy", "gemini-3-pro" } }, "gpt-5" }) {
            bool refused = false;
            try { teams.Start(new Dictionary<string, object> { { "id", "3131313131313131" }, { "mode", "review" }, { "dir", "p" }, { "goal", "g" }, { "tools", new object[] { "claude", "codex" } }, { "models", bad } }); } catch (ArgumentException) { refused = true; }
            Require(refused, "refused models: " + bad);
        }
        string chosen = "3232323232323232";
        var started = teams.Start(new Dictionary<string, object> { { "id", chosen }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "加优惠券" }, { "tools", new object[] { "claude", "codex" } },
            { "models", new Dictionary<string, object> { { "claude", "sonnet" }, { "codex", "" } } } });
        Require(started.Models.Count == 1 && started.Models["claude"] == "sonnet" && tools[started.Members["writer"]] == "claude#sonnet" && Teams.Shown(started)["models"] is Dictionary<string, string>, "the writer is begun with its model; an empty one is the default");
        teams.Turned(started.Members["writer"], "done", "加好了");
        Require(tools[Team(teams, chosen).Members["reviewer"]] == "codex:read", "the reviewer with its tool's default");
        var swapped = teams.Swap(chosen, "reviewer", "agy", "", "gemini-3-pro");
        Require(swapped.Models["agy"] == "gemini-3-pro" && tools[swapped.Members["reviewer"]] == "agy:read#gemini-3-pro" && swapped.Log.Any(s => s.Who == "切换" && s.Text.Contains("模型 gemini-3-pro")), "a swap may choose the model of the one that takes over");
        bool badSwap = false;
        try { teams.Swap(chosen, "writer", "codex", "", "a;b"); } catch (ArgumentException) { badSwap = true; }
        Require(badSwap, "a swap with an unsafe name is refused");
        // a member whose conversation is gone is begun anew with its model, also after a restart
        var again = new Teams(Path.Combine(Path.GetDirectoryName(file), "choosing.dat"), new Teams.Hands { Begin = Hands().Begin, Say = (terminal, words) => { throw new InvalidOperationException("gone"); }, Stop = t => { }, Changes = d => "" });
        Require(again.All().Single(t => t.Id == chosen).Models["agy"] == "gemini-3-pro", "kept");
        again.Say(chosen, "顺便看看金额格式", "reviewer");
        Require(tools[again.All().Single(t => t.Id == chosen).Members["reviewer"]] == "agy:read#gemini-3-pro", "begun anew with its model");
    }

    /// A team's members' conversations: known by the terminals that run them, kept when they are replaced, listed
    /// under the task until it is removed; the tasks in short for the lists.
    static void Grouping(string file) {
        string kept = Path.Combine(Path.GetDirectoryName(file), "grouping.dat");
        var teams = new Teams(kept, Hands());
        string id = "4141414141414141", first = "11111111-2222-3333-4444-555555555555", second = "66666666-7777-8888-9999-000000000000";
        teams.Start(new Dictionary<string, object> { { "id", id }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "改主题" }, { "tools", new object[] { "claude", "codex" } } });
        string writer = Team(teams, id).Members["writer"];
        teams.Learn(writer, "claude", first);
        teams.Learn("t-unknown", "claude", "aaaaaaaa-2222-3333-4444-555555555555");
        var team = Team(teams, id);
        Require(team.Sessions["writer"].Id == first && team.Sessions["writer"].Tool == "claude" && team.Past.Count == 1, "a member's conversation is known by its terminal; others' are not taken");
        Require(teams.TeamOf(writer, "claude", first) == id && teams.TeamOf("claude", first) == id && teams.TeamOf("t-x", "claude", "") == "", "listed under the task by terminal and by conversation");
        var shown = (Dictionary<string, object>)Teams.Shown(team)["member_sessions"];
        var seat = (Dictionary<string, object>)shown["writer"];
        Require((string)seat["id"] == first && (string)seat["tool"] == "claude" && ((Dictionary<string, string>)Teams.Shown(team)["members"])["writer"] == writer, "the detail: role -> terminal, and role -> conversation");
        var swapped = teams.Swap(id, "writer", "agy");
        teams.Learn(swapped.Members["writer"], "agy", second);
        team = Team(teams, id);
        Require(team.Sessions["writer"].Id == second && team.Past.Count == 2 && teams.TeamOf("claude", first) == id && teams.TeamOf(writer, "claude", first) == id, "a replaced member's conversation stays under the task");
        // the cards: newest first, in seconds, with whether a member it waits for asks for a confirmation
        var cards = teams.Cards(terminal => terminal == swapped.Members["writer"]);
        var card = cards.Single(c => (string)c["id"] == id);
        long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        Require(new[] { "id", "dir", "goal", "mode", "state", "phase", "note", "updated", "awaited", "tools", "models", "confirm" }.All(card.ContainsKey) && Math.Abs((long)card["updated"] - now) < 60
            && (bool)card["confirm"] && (string)card["goal"] == "改主题", "a card says what the lists need, its time in seconds");
        Require(!teams.Cards(terminal => false).Single(c => (string)c["id"] == id).ContainsKey("confirm"), "confirm only where one asks");
        // kept, and gone with the task: the conversations are history of their own again
        var reread = new Teams(kept, Hands());
        Require(reread.TeamOf("claude", first) == id && reread.All().Single(t => t.Id == id).Sessions["writer"].Id == second, "kept across a restart");
        reread.Stop(id);
        Require(reread.Remove(id) && reread.TeamOf("claude", first) == "" && reread.TeamOf("agy", second) == "" && reread.Cards(t => false).Length == 0, "after the task is removed its conversations are listed by themselves");
    }

    public static int Main() {
        string folder = Path.Combine(Path.GetTempPath(), "remote-ai-teams-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "teams.dat");
        try {
            // ---- what is read of what the members say
            Require(Teams.Verdict("REVIEW: PASS\n没有问题") == true && Teams.Verdict("**REVIEW: CHANGES**\n1. …") == false && Teams.Verdict("\n\n  review：pass") == true && Teams.Verdict("看起来不错") == null, "the verdict, in the first lines");
            var tasks = Teams.Assigned("先这样分：\n- ASSIGN codex: 写测试\n**ASSIGN** agy：查文档\nASSIGN codex: 再跑一遍\nASSIGN gemini: 不在队里", new[] { "codex", "agy" });
            Require(tasks.Count == 2 && tasks["codex"] == "写测试\n再跑一遍" && tasks["agy"] == "查文档", "the tasks, of the members it has only");
            tasks = Teams.Assigned("分工如下。\nASSIGN codex: 按下面的顺序做：\n1. 修额度\n\n2. 加设置\nASSIGN agy: 查文档\nVERIFIED: 不属于任务\nDONE: x", new[] { "codex", "agy" });
            Require(tasks["codex"] == "按下面的顺序做：\n1. 修额度\n\n2. 加设置" && tasks["agy"] == "查文档", "a task's list under its ASSIGN line goes with it: " + tasks["codex"]);
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
            Require(Team(teams, spent).State == "stopped" && Team(teams, spent).Note.Contains("已到 1 轮") && Team(teams, spent).Note.Contains("未通过"), "no more rounds than were set, and that is no pass");

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
            Require(Last(leader).Contains("接口好了") && Last(leader).Contains("页面好了") && Last(leader).Contains("## Codex") && Team(teams, lead).Round == 1, "acceptance is still part of the first round");
            teams.Turned(leader, "done", "ASSIGN codex: 补测试");
            Require(Last(codex).Contains("补测试") && made == 7, "a member is told again in its own conversation");
            // a member stopped from the phone: the team waits; it goes on where it was
            teams.Turned(codex, "stopped", "");
            Require(Team(teams, lead).State == "paused" && Team(teams, lead).Note.Contains("停止"), "a member stopped: paused");
            teams.Resume(lead);
            Require(Team(teams, lead).State == "running" && Last(codex) == "继续完成上面的任务。", "resumed: told to go on");
            changes = "文件：\n M login.js\n\n+ login()";
            teams.Turned(codex, "done", "测试加好了");
            team = Team(teams, lead);
            Require(team.State == "running" && team.Phase == "verify" && team.Awaited.SequenceEqual(new[] { "leader" }) && Last(leader).Contains("领队验收") && Last(leader).Contains("VERIFIED:")
                && Last(leader).Contains("+ login()") && Last(leader).Contains("运行与这次改动相关的检查") && made == 7, "member results open the leader's own acceptance, in its conversation, with what changed");
            teams.Turned(leader, "done", "DONE: 都好了");
            Require(Team(teams, lead).State == "running" && Last(leader).Contains("没有找到验收证据"), "a DONE without what was checked is not taken");
            teams.Turned(leader, "done", "我看了 login.js 并跑了测试。\nVERIFIED: npm test 全部 12 项通过；打开 login.js 确认 login() 校验了空密码\nDONE: 登录页和接口都完成了");
            team = Team(teams, lead);
            Require(team.State == "done" && team.Note.Contains("领队验收通过") && team.Note.Contains("登录页和接口都完成了") && team.Log.Any(s => s.Who == "验收" && s.Text.Contains("npm test 全部 12 项通过")), "accepted with what was checked, which is kept in the log");

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
            Require(Last(wLeader).Contains("- codex：Codex（工具默认模型）") && Last(wLeader).Contains("；额度：5 小时窗口已用 93%（") && Last(wLeader).Contains("本周已用 40%，紧张")
                && Last(wLeader).Contains("- agy：Antigravity CLI") && Last(wLeader).Contains("额度未知") && Last(wLeader).Contains("你自己（Claude Code）的额度：5 小时窗口已用 12%")
                && Last(wLeader).Contains("按额度分配工作量"), "the leader is told each member's limits and its own, a tight one called so: " + Last(wLeader));
            Require(!Last(leader).Contains("额度"), "without a way to read limits nothing of them is said");
            weighed.Turned(wLeader, "done", "ASSIGN agy: 做搜索框");
            codexUsed = 99;
            weighed.Turned(Team(weighed, w).Members["agy"], "done", "搜索框好了");
            Require(Last(wLeader).Contains("## 当前额度") && Last(wLeader).Contains("- Claude Code（你）：额度：5 小时窗口已用 12%") && Last(wLeader).Contains("- Codex：额度：5 小时窗口已用 99%"), "the results come with the limits as they are now");
            Require(Teams.Room(new[] { new Quotas.Window { Name = "seven_day", Used = 79.4 } }) == "额度：本周已用 79%" && Teams.Room(new[] { new Quotas.Window { Name = "300m", Used = 79.6 } }) == "额度：300m已用 80%，紧张" && Teams.Room(new[] { new Quotas.Window { Name = "five_hour", Used = 100 } }) == "额度：5 小时窗口已用 100%，已用尽（不可派工）" && Teams.Room(null) == "额度未知", "limits in words, tight from 80% used and exhausted at 100%");
            Require(Teams.IsQuotaError("You've hit your limit · resets 5pm") && Teams.IsQuotaError("HTTP 429 rate_limit_exceeded") && !Teams.IsQuotaError("syntax error on line 3"), "quota error detection");

            // ---- Automatic failover when CC runs out of quota (lead mode + review mode) & manual Swap + Say recovery
            var failHands = new Teams.Hands {
                Begin = Hands().Begin, Say = Hands().Say, Stop = Hands().Stop, Changes = d => changes,
                Available = () => new[] { "claude", "codex", "agy" }
            };
            var failTeams = new Teams(null, failHands);

            // 1) Lead mode (3 tools): CC runs out of quota while agy works in parallel -> stays running, waits for agy, reports CC failure to leader -> leader reassigns CC's work to agy
            string lf = "1414141414141414";
            failTeams.Start(new Dictionary<string, object> { { "id", lf }, { "mode", "lead" }, { "dir", "web-shop" }, { "goal", "修复导出和分页" }, { "tools", new object[] { "codex", "claude", "agy" } }, { "rounds", 3 } });
            string lfLeader = Team(failTeams, lf).Members["leader"];
            failTeams.Turned(lfLeader, "done", "ASSIGN claude: 修复导出\nASSIGN agy: 修复分页");
            var lft = Team(failTeams, lf);
            string lfClaude = lft.Members["claude"], lfAgy = lft.Members["agy"];
            failTeams.Turned(lfClaude, "failed", "You've hit your limit · resets 23:00");
            lft = Team(failTeams, lf);
            Require(lft.State == "running" && lft.Failed.ContainsKey("claude") && lft.Awaited.SequenceEqual(new[] { "agy" }), "lead member quota failure keeps team running and waits for parallel agy");
            failTeams.Turned(lfAgy, "done", "分页已修复");
            lft = Team(failTeams, lf);
            Require(lft.State == "running" && lft.Awaited.SequenceEqual(new[] { "leader" }) && Last(lfLeader).Contains("分页已修复") && Last(lfLeader).Contains("【执行失败】") && Last(lfLeader).Contains("额度已用尽") && Last(lfLeader).Contains("请勿再派工"), "leader receives agy result + CC failure + unavailable marker");
            failTeams.Turned(lfLeader, "done", "ASSIGN agy: 接手修复导出");
            Require(Last(lfAgy).Contains("接手修复导出") && Team(failTeams, lf).Awaited.SequenceEqual(new[] { "agy" }), "leader reassigns CC task to agy seamlessly");
            failTeams.Turned(lfAgy, "done", "导出也修好了");
            Require(Team(failTeams, lf).Phase == "verify" && Team(failTeams, lf).State == "running", "after failover, the results open the acceptance");
            failTeams.Turned(lfLeader, "done", "VERIFIED: 导出了 3 个文件并翻到第 2 页，内容和页码都正确\nDONE: 导出和分页均已修复");
            Require(Team(failTeams, lf).State == "done", "lead task finishes cleanly after failover");

            // 2) Lead mode (2 tools: codex + claude, agy installed on computer): CC fails -> agy is auto-added to roster and reported to leader without burning a round
            string lf2 = "1515151515151515";
            failTeams.Start(new Dictionary<string, object> { { "id", lf2 }, { "mode", "lead" }, { "dir", "web-shop" }, { "goal", "修日志" }, { "tools", new object[] { "codex", "claude" } }, { "rounds", 1 } });
            string lf2Leader = Team(failTeams, lf2).Members["leader"];
            failTeams.Turned(lf2Leader, "done", "ASSIGN claude: 修日志格式");
            failTeams.Turned(Team(failTeams, lf2).Members["claude"], "failed", "quota exceeded");
            var lf2t = Team(failTeams, lf2);
            Require(lf2t.State == "running" && lf2t.Round == 1 && lf2t.Tools.Contains("agy") && Last(lf2Leader).Contains("ASSIGN agy:"), "2-tool lead auto-adds available agy backup without burning round");
            failTeams.Turned(lf2Leader, "done", "ASSIGN agy: 修日志格式");
            failTeams.Turned(Team(failTeams, lf2).Members["agy"], "done", "日志修好了");
            lf2t = Team(failTeams, lf2);
            Require(lf2t.State == "running" && lf2t.Phase == "verify" && lf2t.Last && Last(lf2Leader).Contains("日志修好了") && Last(lf2Leader).Contains("最后一轮") && Last(lf2Leader).Contains("不要再写 ASSIGN"),
                "the rounds are spent: the results go to the leader for a last acceptance, not to an end");
            failTeams.Turned(lf2Leader, "done", "VERIFIED: 运行 python log_check.py，输出的时间格式正确\nDONE: 日志格式已修好");
            Require(Team(failTeams, lf2).State == "done", "auto-added backup completes work");

            // 3) Review mode: writer (claude) hits quota -> automatically switches writer to agy with handoff context and continues
            string rf = "1616161616161616";
            changes = "文件：\n M export.js\n\n+ fixExport()";
            failTeams.Start(new Dictionary<string, object> { { "id", rf }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "修复导出乱码" }, { "tools", new object[] { "claude", "codex" } }, { "rounds", 3 } });
            string rfClaude = Team(failTeams, rf).Members["writer"];
            failTeams.Turned(rfClaude, "failed", "rate limit reached");
            var rft = Team(failTeams, rf);
            Require(rft.State == "running" && rft.Tools[0] == "agy" && rft.Awaited.SequenceEqual(new[] { "writer" }) && tools[rft.Members["writer"]] == "agy" && Last(rft.Members["writer"]).Contains("fixExport()") && Last(rft.Members["writer"]).Contains("额度已用尽"), "review writer auto-switches to agy with diff context");
            failTeams.Turned(rft.Members["writer"], "done", "已修好导出乱码");
            failTeams.Turned(Team(failTeams, rf).Members["reviewer"], "done", "REVIEW: PASS\n没问题");
            Require(Team(failTeams, rf).State == "done", "review passes after automatic writer failover");

            // 4) Manual Swap mid-run: user reassigns active writer from claude to agy with custom note
            string sw = "1717171717171717";
            failTeams.Start(new Dictionary<string, object> { { "id", sw }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "改主题色" }, { "tools", new object[] { "claude", "codex" } }, { "rounds", 3 } });
            string swOld = Team(failTeams, sw).Members["writer"];
            var swapped = failTeams.Swap(sw, "writer", "agy", "顺便把暗色模式也调一下");
            Require(stopped.Contains(swOld) && swapped.State == "running" && swapped.Tools[0] == "agy" && Last(swapped.Members["writer"]).Contains("暗色模式也调一下") && swapped.Log.Any(s => s.Who == "切换"), "manual swap stops old terminal and launches chosen AI with note");

            // 5) Paused team + Say targeting leader clears stale failed member from Awaited so next round does not hang
            string ps = "1818181818181818";
            var noBackup = new Teams(null, new Teams.Hands { Begin = Hands().Begin, Say = Hands().Say, Stop = Hands().Stop, Changes = d => "" });
            noBackup.Start(new Dictionary<string, object> { { "id", ps }, { "mode", "lead" }, { "dir", "web-shop" }, { "goal", "排查报错" }, { "tools", new object[] { "codex", "claude" } }, { "rounds", 3 } });
            string psLeader = Team(noBackup, ps).Members["leader"];
            noBackup.Turned(psLeader, "done", "ASSIGN claude: 查原因");
            noBackup.Turned(Team(noBackup, ps).Members["claude"], "failed", "hit your limit");
            Require(Team(noBackup, ps).State == "paused", "pauses cleanly when no other member or backup exists");
            noBackup.Say(ps, "先直接总结一下现状", "leader");
            Require(Team(noBackup, ps).State == "running" && Team(noBackup, ps).Awaited.SequenceEqual(new[] { "leader" }), "Say on paused team clears stale failed Awaited");
            noBackup.Turned(psLeader, "done", "DONE: 已汇总现状");
            Require(Team(noBackup, ps).Phase == "verify", "accepted in a turn of its own");
            noBackup.Turned(psLeader, "done", "VERIFIED: 重新运行 run.ps1，报错不再出现，日志里没有异常\nDONE: 已汇总现状");
            Require(Team(noBackup, ps).State == "done", "paused team finishes after targeted Say without hanging");

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

            Accepting(file);
            Choosing(file);
            Grouping(file);
            Roster(folder);
        } finally { try { Directory.Delete(folder, true); } catch { } }
        Console.WriteLine("teams: review, lead with acceptance, flexible roster, discuss-first, parallel compare/pick, interject/continue, quotas, models, member sessions, cards, stop/resume and persistence ok");
        return 0;
    }
}
