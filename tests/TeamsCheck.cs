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
    static readonly List<string> closed = new List<string>();
    static string changes = "";
    static int made;
    static Teams.Hands Hands() {
        return new Teams.Hands {
            // what a member is begun as: "<tool>[:read][@folder][#model][~effort]"
            Begin = one => { string t = "t" + (++made); tools[t] = one.Tool + (one.Read ? ":read" : "") + (one.Folder.Length > 0 ? "@" + one.Folder : "") + (one.Model.Length > 0 ? "#" + one.Model : "") + (one.Effort.Length > 0 ? "~" + one.Effort : ""); told.Add(new[] { t, one.Tool, one.Words }); return t; },
            Say = (terminal, words) => { if (terminal == "gone") throw new InvalidOperationException("这个成员的对话已经结束"); told.Add(new[] { terminal, "", words }); },
            Stop = terminal => stopped.Add(terminal),
            Close = terminal => closed.Add(terminal),
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
        Require(Last(leader).Contains("Remote AI 按这些行派给协作成员") && Last(first).Contains("只有用户明确要求时才使用工具内置的子代理"), "leader dispatch and member-local delegation have distinct instructions");
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
        Require(team.State == "paused" && team.Spent && team.Note.Contains("已到 1 轮") && team.Note.Contains("继续") && made == before && team.Held["claude"] == "再改"
            && team.Awaited.SequenceEqual(new[] { "claude" }), "more work wanted when no round is left: paused with the fix kept, nothing given out yet");
        // the owner goes on with it, with words of their own: one more round, the kept fix goes out with them
        string capClaude = team.Members["claude"];
        teams.Say(capped, "再给一轮，把剩下的改完");
        team = Team(teams, capped);
        Require(team.State == "running" && team.Phase == "" && !team.Last && !team.Spent && team.Round == 2 && team.Rounds == 2 && team.Held.Count == 0
            && Last(capClaude).Contains("再改") && Last(capClaude).Contains("再给一轮，把剩下的改完") && team.Awaited.SequenceEqual(new[] { "claude" }), "its owner goes on with it");

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

        // As it went on 2026-11-21: one round; Antigravity handed in a note of progress while its tests ran, the leader
        // accepted too early and wanted fixes, and the task ended. Now the note sends it back, and the fixes wait.
        string broke = "2525252525252525";
        string brokeFile = Path.Combine(Path.GetDirectoryName(file), "broke.dat");
        var breaking = new Teams(brokeFile, Hands());
        breaking.Start(Lead(broke, new object[] { "codex", "claude", "agy" }, 1));
        leader = Team(breaking, broke).Members["leader"];
        breaking.Turned(leader, "done", "ASSIGN claude: 电脑端更新渠道\nASSIGN agy: 协作详情全屏，跑协作浏览器测试");
        team = Team(breaking, broke);
        string cc = team.Members["claude"], ag = team.Members["agy"];
        breaking.Turned(cc, "done", "更新渠道做好了，已提交，测试通过。");
        int toldLeader = told.Count(x => x[0] == leader);
        breaking.Turned(ag, "done", "已改好详情页。\n\n正在运行协作浏览器测试（含全屏详情、状态刷新阅读保位），待结果返回后继续核对截图与断言。");
        team = Team(breaking, broke);
        Require(team.Phase == "" && team.Awaited.SequenceEqual(new[] { "agy" }) && Last(ag) == Teams.KeepAtIt && told.Count(x => x[0] == leader) == toldLeader
            && team.Log.Any(x => x.Who == "继续" && x.Text.Contains("进度说明")), "a note of progress sends the member back; the leader is not asked yet");
        breaking.Turned(ag, "done", "测试 14 项通过，详情页全屏，返回后回到原任务。");
        team = Team(breaking, broke);
        Require(team.Phase == "verify" && team.Last && Last(leader).Contains("测试 14 项通过") && Last(leader).Contains("更新渠道做好了") && !Last(leader).Contains("正在运行协作浏览器测试"),
            "the result, not the note, goes to the acceptance");
        breaking.Turned(leader, "done", "还差两项。\nASSIGN agy: 修刷新后阅读位置\nASSIGN claude: 补渠道切换的回归测试");
        team = Team(breaking, broke);
        Require(team.State == "paused" && team.Spent && team.Held.Count == 2 && team.Held["agy"] == "修刷新后阅读位置" && Last(ag).Contains("测试 14 项通过") == false && Last(ag) != "修刷新后阅读位置",
            "the last acceptance wanting more pauses with the fixes kept, not an end");
        // kept across a restart, and 继续 there adds the round and sends the fixes (the old conversations are gone: begun anew)
        var after = Hands(); after.Say = (terminal, words) => { throw new InvalidOperationException("gone"); };
        var reborn = new Teams(brokeFile, after);
        Require(Team(reborn, broke).State == "paused" && Team(reborn, broke).Spent && Team(reborn, broke).Held["claude"] == "补渠道切换的回归测试", "what waits is kept across a restart");
        reborn.Resume(broke);
        team = Team(reborn, broke);
        Require(team.State == "running" && team.Round == 2 && team.Rounds == 2 && team.Awaited.OrderBy(x => x).SequenceEqual(new[] { "agy", "claude" })
            && Last(team.Members["agy"]).Contains("修刷新后阅读位置") && Last(team.Members["claude"]).Contains("补渠道切换的回归测试"), "继续 adds one round and sends what was kept");
        // a member that keeps handing in notes is sent back twice in a round, then its words go to the leader as they are
        Func<string> agyNow = () => Team(reborn, broke).Members["agy"];      // (each word to it begins it anew here)
        reborn.Turned(team.Members["claude"], "done", "回归测试已补，通过。");
        for (int i = 0; i < 2; i++) { reborn.Turned(agyNow(), "done", "还在跑测试，稍后回报。"); Require(Team(reborn, broke).Phase == "" && Last(agyNow()).Contains(Teams.KeepAtIt), "sent back, time " + (i + 1)); }
        reborn.Turned(agyNow(), "done", "还在跑测试，稍后回报。");
        Require(Team(reborn, broke).Phase == "decision" && Last(Team(reborn, broke).Members["leader"]).Contains("还在跑测试") && Last(Team(reborn, broke).Members["leader"]).Contains("未完成的分工"), "the third note goes to the leader, who decides (begun anew after the restart)");
        Require(!Teams.Progress("改好了。测试全部通过。") && !Teams.Progress("正在运行的测试已全部通过。\n\n改动：teams.js") && Teams.Progress("Tests are still running; I will report back once they finish."),
            "a result is a result; a note of progress in either language is one");
    }

    /// Delegation and handback verification: historical progress misjudgment, completed/blocked/retrospective-waiting
    /// contrasts, member continuation and leader notification timing, simple direct finish vs autonomous member execution.
    static void DelegationAndHandback(string folder) {
        string historical = "I'm generating the icon assets and preview sheets now; I'll inspect the renders as soon as the script finishes.";
        Require(Teams.Progress(historical), "historical progress note must be recognized as in-progress");
        Require(Teams.Progress("Unit tests passed. " + historical), "earlier passed step does not mask trailing progress note");
        Require(Teams.Progress("Running the test suite now; I'll report back as soon as it finishes."), "English in-progress variant recognized");
        // a whole reply that only says what is being checked now (a member's real reply, taken for its result once)
        Require(Teams.Progress("Checking the updated organizer UI, workbench guards, and Android aggregate checks now."), "a bare 'Checking … now.' is progress");
        Require(Teams.Progress("Now verifying the relay endpoint and the phone page."), "'Now verifying …' is progress");
        Require(Teams.Progress("I'm checking the Android aggregate now."), "'I'm checking … now' is progress");
        Require(Teams.Progress("正在检查整理页和安卓聚合测试。"), "正在检查 is progress");
        Require(!Teams.Progress("Checked the updated organizer UI, workbench guards, and Android aggregate checks: python tests/organize_ui_check.py and tests/android_aggregate_check.py passed."), "the same checks reported done are a result");
        Require(!Teams.Progress("Checking the updated organizer UI, workbench guards, and Android aggregate checks now.\n\nResults: organize_ui_check.py passed, Android aggregate check passed, no changes needed."), "the opening line followed by the results is a result");
        Require(!Teams.Progress("Checking the organizer UI now showed one overlap, which I fixed; organize_ui_check.py passed afterwards."), "a check that already ran and passed is a result");
        Require(!Teams.Progress("已检查整理页和安卓聚合测试，全部通过。"), "已检查 … 通过 is a result");

        // Contrast 1: completed reports (plain Chinese, plain English reusing the same nouns, and explicit DONE)
        Require(!Teams.Progress("已完成图标资源生成与预览检查，更新了 remote-ai.ico 和 Android 图标，python tests/icon_assets_check.py 全部通过。"), "plain Chinese completion is not progress");
        Require(!Teams.Progress("Finished generating the icon assets and preview sheets; inspected the 16px and 32px renders and icon_assets_check.py passed."), "plain English completion reusing nouns is not progress");
        Require(!Teams.Progress("图标资源和预览图已生成并核对完成。\nDONE: 已更新图标并通过 icon_assets_check.py"), "DONE completion is not progress");

        // Contrast 2: explicitly blocked reports (even when mentioning waiting/continuing)
        Require(!Teams.Progress("阻塞：缺少发布签名密钥目录，正在等用户确认路径，无法继续打包。"), "Chinese explicit blocker is not progress");
        Require(!Teams.Progress("Blocked: Chrome is missing on this machine; waiting for environment setup before running browser checks."), "English explicit blocker is not progress");
        Require(!Teams.Progress("- **受阻：** 目标目录权限不足，无法写入测试产物，需领队决定是否改用临时目录。"), "formatted blocker is not progress");

        // Contrast 3: completion reports retrospectively referencing earlier waiting
        Require(!Teams.Progress("先运行脚本生成图标，当时脚本仍在运行，等待结果返回后继续检查。\n\n结果：脚本已跑完，核对了 16px/32px 渲染图，python tests/icon_assets_check.py 全部通过。"),
            "earlier-paragraph waiting in multi-paragraph completion report is not progress");
        Require(!Teams.Progress("此前测试仍在运行，等待脚本跑完后我核对了全部渲染图并确认通过。\nDONE: 图标资源与预览图均已通过检查"),
            "retrospective waiting with DONE is not progress");
        Require(!Teams.Progress("After waiting for the script to finish, I inspected the 16px and 32px renders and confirmed icon_assets_check.py passed."),
            "single-paragraph English completion retrospectively referencing waiting is not progress");
        Require(!Teams.Progress("Tests were still running earlier, and the build was in progress, but all checks are green now."),
            "past-tense retrospective waiting is not progress");
        Require(!Teams.Progress("等脚本跑完后我核对了 16px/32px 渲染图，改动已保存且 python tests/icon_assets_check.py 全部通过。"),
            "single-paragraph Chinese completion retrospectively referencing waiting is not progress");

        var hands = Hands();
        hands.Available = () => new[] { "claude", "codex", "agy" };
        var teams = new Teams(Path.Combine(folder, "handback.dat"), hands);

        // 1) End-to-end: historical progress sentence sends member back; leader receives only final results once all members finish
        string iconId = "6161616161616161";
        var iconPreset = Lead(iconId, new object[] { "codex", "claude", "agy" }, 0);
        iconPreset["goal"] = "更新应用图标资源并验收渲染结果";
        iconPreset["roster"] = new[] { Member("leader", "codex"), Member("member-1", "claude"), Member("member-2", "agy") };
        string leader = teams.Start(iconPreset).Members["leader"];
        teams.Turned(leader, "done", "ASSIGN member-1: 核对图标资源引用测试\nASSIGN member-2: 生成全部图标资源与 16/32 像素预览图并检查渲染效果");
        var team = Team(teams, iconId);
        string m1 = team.Members["member-1"], m2 = team.Members["member-2"];
        int toldLeader = told.Count(x => x[0] == leader);
        teams.Turned(m1, "done", "已完成图标引用测试，python tests/icon_assets_check.py 全部通过。");
        team = Team(teams, iconId);
        Require(team.Phase == "" && team.Awaited.SequenceEqual(new[] { "member-2" }) && told.Count(x => x[0] == leader) == toldLeader,
            "leader waits while second member works");
        teams.Turned(m2, "done", historical);
        team = Team(teams, iconId);
        Require(team.State == "running" && team.Phase == "" && team.Awaited.SequenceEqual(new[] { "member-2" }) && Last(m2) == Teams.KeepAtIt
            && told.Count(x => x[0] == leader) == toldLeader && team.Log.Any(s => s.Who == "继续" && s.Text.Contains("进度说明")),
            "historical progress sentence keeps member working without waking leader");
        teams.Turned(m2, "done", "先运行脚本生成图标并等待预览图输出。\n\n全部图标资源和 16px/32px 预览图已生成并核对无误，python tests/icon_assets_check.py 通过。");
        team = Team(teams, iconId);
        Require(team.Phase == "verify" && team.Awaited.SequenceEqual(new[] { "leader" }) && told.Count(x => x[0] == leader) == toldLeader + 1
            && Last(leader).Contains("已完成图标引用测试") && Last(leader).Contains("16px/32px 预览图已生成")
            && !Last(leader).Contains("generating the icon assets and preview sheets now"),
            "leader receives both final results only after nudged member finishes, without the intermediate progress note");
        teams.Turned(leader, "done", "VERIFIED: 核对两项改动并复跑 python tests/icon_assets_check.py 全部通过\nDONE: 图标资源更新与验收完成");
        Require(Team(teams, iconId).State == "done" && Team(teams, iconId).Round == 1, "historical progress flow finishes cleanly after verification");

        // 2) End-to-end: explicit blocker is NOT nudged and goes to leader decision turn
        string blockedId = "6262626262626262";
        leader = teams.Start(Lead(blockedId, new object[] { "codex", "claude", "agy" }, 3)).Members["leader"];
        teams.Turned(leader, "done", "ASSIGN claude: 修复后端接口\nASSIGN agy: 跑协作浏览器检查");
        team = Team(teams, blockedId);
        string cc = team.Members["claude"], ag = team.Members["agy"];
        toldLeader = told.Count(x => x[0] == leader);
        teams.Turned(cc, "done", "后端接口已修好，单元测试 6 项通过。");
        teams.Turned(ag, "done", "Blocked: Chrome is not installed on this machine; waiting for environment path before running together_ui_check.py. Code changes in teams.js are saved.");
        team = Team(teams, blockedId);
        Require(Last(ag) != Teams.KeepAtIt && team.Phase == "decision" && team.Awaited.SequenceEqual(new[] { "leader" })
            && told.Count(x => x[0] == leader) == toldLeader + 1 && Last(leader).Contains("Blocked: Chrome") && Last(leader).Contains("未完成的分工"),
            "explicit blocker reaches leader in decision phase without being nudged");
        teams.Turned(leader, "done", "ASSIGN claude: 补跑 teams.js 相关静态检查并核对改动");
        team = Team(teams, blockedId);
        Require(team.Round == 2 && team.Phase == "" && team.Awaited.SequenceEqual(new[] { "claude" }), "leader decision dispatches follow-up into next round");
        teams.Turned(cc, "done", "已核对 teams.js 并跑完检查，无报错。");
        Require(Team(teams, blockedId).Phase == "verify", "resolved follow-up enters verification");
        teams.Turned(leader, "done", "VERIFIED: 复跑单元测试与静态检查全部通过\nDONE: 已在解决阻塞后完成验收");
        Require(Team(teams, blockedId).State == "done", "blocked task completes after leader decision and verification");

        // 2b) Explicit blocker on the last round enters decision with cap notice; leader ASSIGN is held until resumed
        string blockedLast = "6666666666666666";
        leader = teams.Start(Lead(blockedLast, new object[] { "codex", "claude" }, 1)).Members["leader"];
        teams.Turned(leader, "done", "ASSIGN claude: 运行完整打包脚本");
        teams.Turned(Team(teams, blockedLast).Members["claude"], "done", "阻塞：缺少发布签名密钥目录，无法完成签名，请领队确认路径。");
        team = Team(teams, blockedLast);
        Require(team.Phase == "decision" && team.Last && Last(leader).Contains("本轮已到上限"), "last-round blocker enters decision with cap notice");
        teams.Turned(leader, "done", "ASSIGN claude: 使用备用测试密钥完成验证");
        team = Team(teams, blockedLast);
        Require(team.State == "paused" && team.Spent && team.Held["claude"].Contains("备用测试密钥"), "last-round decision reassign holds task for owner extension");
        teams.Resume(blockedLast);
        Require(Team(teams, blockedLast).State == "running" && Team(teams, blockedLast).Round == 2 && Last(Team(teams, blockedLast).Members["claude"]).Contains("备用测试密钥"),
            "resuming held decision dispatches follow-up in added round");
        teams.Turned(Team(teams, blockedLast).Members["claude"], "done", "已用备用测试密钥验证通过。");
        teams.Turned(leader, "done", "VERIFIED: 复核验证脚本输出通过\nDONE: 已完成验证");
        Require(Team(teams, blockedLast).State == "done", "extended decision task finishes cleanly");

        // 3) End-to-end: completion report retrospectively referencing waiting goes straight to verification
        string retroId = "6363636363636363";
        leader = teams.Start(Lead(retroId, new object[] { "codex", "claude", "agy" }, 2)).Members["leader"];
        teams.Turned(leader, "done", "ASSIGN claude: 生成预览图并验收\nASSIGN agy: 跑图标检查脚本并确认通过");
        team = Team(teams, retroId);
        cc = team.Members["claude"]; ag = team.Members["agy"];
        toldLeader = told.Count(x => x[0] == leader);
        teams.Turned(cc, "done", "After waiting for the script to finish, I inspected the 16px and 32px renders and confirmed icon_assets_check.py passed.");
        Require(Last(cc) != Teams.KeepAtIt && Team(teams, retroId).Awaited.SequenceEqual(new[] { "agy" }), "English retrospective waiting completion is not nudged");
        teams.Turned(ag, "done", "等脚本跑完后我核对了全部预览图，改动已保存且 python tests/icon_assets_check.py 全部通过。");
        team = Team(teams, retroId);
        Require(Last(ag) != Teams.KeepAtIt && team.Phase == "verify" && told.Count(x => x[0] == leader) == toldLeader + 1
            && Last(leader).Contains("inspected the 16px and 32px renders") && Last(leader).Contains("核对了全部预览图"),
            "retrospective waiting completions go straight to leader verification");
        teams.Turned(leader, "done", "VERIFIED: 复核两项检查输出均已通过\nDONE: 图标与检查全部完成");
        Require(Team(teams, retroId).State == "done", "retrospective waiting task finishes cleanly");

        // 4) Simple task finishes directly without spawning any member
        string simpleId = "6464646464646464";
        var simple = Lead(simpleId, new object[] { "codex", "claude", "agy" }, 0);
        simple["goal"] = "修正 README.md 中的一处拼写错误";
        simple["roster"] = new[] { Member("leader", "codex"), Member("member-1", "claude"), Member("member-2", "agy") };
        leader = teams.Start(simple).Members["leader"];
        Require(Last(leader).Contains("简单任务由最合适的一人直接完成：你能做完就自己做"), "leader is told simple tasks can finish directly");
        int beforeSimple = made;
        teams.Turned(leader, "done", "已直接修正 README.md 拼写错误。\nDONE: README.md 拼写已修正");
        team = Team(teams, simpleId);
        Require(team.State == "running" && team.Phase == "verify" && team.Members.Count == 1 && made == beforeSimple,
            "simple task enters verification directly without spawning members");
        teams.Turned(leader, "done", "VERIFIED: git diff README.md 确认仅修改一处拼写\nDONE: README.md 拼写已修正");
        team = Team(teams, simpleId);
        Require(team.State == "done" && team.Round == 1 && team.Members.Count == 1 && made == beforeSimple,
            "simple task finishes in round 1 with zero members spawned");

        // 5) Substantive task delegated to autonomous members (auto-roster, scoped multi-line tasks, queued owner interjection)
        string subId = "6565656565656565";
        var substantive = Lead(subId, new object[] { "codex" }, 0);
        substantive["goal"] = "优化协作任务分工与交回，并补齐回归测试";
        substantive["auto_roster"] = true;
        substantive["roster"] = new[] { Member("leader", "codex") };
        leader = teams.Start(substantive).Members["leader"];
        Require(Last(leader).Contains("由成员自行调查、选择实现、修复并复测"), "leader is guided to delegate substantive work autonomously");
        teams.Turned(leader, "done", "ASSIGN member-1 claude: 负责 agent-windows/Teams.cs：\n1. 优化进度识别与阻塞交回\n2. 保持已有状态机兼容\nASSIGN member-2 agy: 负责 tests/TeamsCheck.cs：\n1. 补齐分工与交回验证用例\n2. 运行 python tests/teams_check.py 交回结果");
        team = Team(teams, subId);
        m1 = team.Members["member-1"]; m2 = team.Members["member-2"];
        Require(team.Awaited.OrderBy(x => x).SequenceEqual(new[] { "member-1", "member-2" })
            && Last(m1).Contains("优化进度识别与阻塞交回") && Last(m1).Contains("在分派范围内自主调查、选择实现、修复并复测")
            && Last(m2).Contains("补齐分工与交回验证用例") && Last(m2).Contains("在分派范围内自主调查、选择实现、修复并复测"),
            "substantive tasks reach members with full multi-line scope and autonomy guidance");
        // Untargeted owner interjection while members run in lead mode queues for leader rather than interrupting members
        teams.Say(subId, "验收时重点核对阻塞回流路径", "");
        Require(!Last(m1).Contains("阻塞回流路径") && !Last(m2).Contains("阻塞回流路径")
            && Team(teams, subId).Awaited.OrderBy(x => x).SequenceEqual(new[] { "member-1", "member-2" }),
            "untargeted interjection queues for leader without interrupting autonomous members");
        changes = "文件：\n M agent-windows/Teams.cs\n M tests/TeamsCheck.cs";
        teams.Turned(m1, "done", "已更新 agent-windows/Teams.cs 的进度识别与阻塞交回。\nDONE: Teams.cs 改动完成");
        teams.Turned(m2, "done", "已补齐 tests/TeamsCheck.cs 并运行 python tests/teams_check.py 通过。");
        team = Team(teams, subId);
        Require(team.Phase == "verify" && team.Awaited.SequenceEqual(new[] { "leader" })
            && Last(leader).Contains("Teams.cs 改动完成") && Last(leader).Contains("python tests/teams_check.py 通过")
            && Last(leader).Contains("阻塞回流路径") && Last(leader).Contains("M tests/TeamsCheck.cs"),
            "leader receives autonomous member results, queued owner guidance, and diff for acceptance");
        teams.Turned(leader, "done", "VERIFIED: 复核 diff 并复跑 python tests/teams_check.py 全部通过\nDONE: 协作分工与交回优化已完成");
        Require(Team(teams, subId).State == "done" && Team(teams, subId).Round == 1, "substantive delegated task completes in round 1");
    }

    /// How a leader is asked to give out work, at each turn it gets, and which tools it is offered. What is fixed here
    /// is what goes out and when; whom the leader picks for which work is its own judgement (`TeamsLive` on real tools).
    static void Dispatching() {
        var hands = Hands();
        hands.Available = () => new[] { "claude", "codex", "agy" };
        var teams = new Teams(null, hands);
        Func<string, string, Dictionary<string, object>> preset = (id, goal) => {
            var given = Lead(id, new object[] { "codex", "claude", "agy" }, 0);
            given["goal"] = goal;
            given["roster"] = new[] { Member("leader", "codex"), Member("member-1", "claude"), Member("member-2", "agy") };
            return given;
        };
        Func<string, string, int> at = (text, part) => text.IndexOf(part, StringComparison.Ordinal);

        // the leader settles what the user gets and its evidence before choosing anyone
        string mixedId = "7171717171717171";
        string leader = teams.Start(preset(mixedId, "设计并实现会话自动整理：先定整理规则和接口，再实现电脑端并补测试")).Members["leader"];
        string first = Last(leader);
        int product = at(first, "先确定用户要拿到的成品和验收证据"), simple = at(first, "简单任务由最合适的一人直接完成"), who = at(first, "才分工，按专长、额度、依赖和文件归属选人");
        Require(product > 0 && simple > product && who > simple, "the first prompt settles product and evidence first, then the simplest way, and splits only after");
        Require(first.Contains("协作是为了发挥各 AI 的长处，不是为了增加流程") && first.Contains("只要设计时交付设计，不改代码")
            && first.Contains("先定下接口和文件归属，再按模块派实现") && first.Contains("同一文件只交给一位成员")
            && first.Contains("请另一位 AI 只读审阅；小改动由你验收即可"), "one person, design only, split by module and review where needed are all offered");

        // mixed: the design goes out first; once accepted, the building is given out by file in the next round
        teams.Turned(leader, "done", "成品：整理规则说明和电脑端实现，验收看单元测试输出。\nASSIGN member-1: 写出整理规则、接口和文件归属，交回设计说明，不改代码");
        var team = Team(teams, mixedId);
        string m1 = team.Members["member-1"];
        Require(team.Awaited.SequenceEqual(new[] { "member-1" }) && !team.Members.ContainsKey("member-2")
            && Last(m1).Contains("派的是设计就交设计，不要顺带实现") && Last(m1).Contains("派工要求的验收证据"), "only the design goes out first, and its member is held to the design");
        teams.Turned(m1, "done", "设计说明：Organizer.cs 管队列，Organizer 接口两项，测试放 OrganizerCheck.cs。");
        team = Team(teams, mixedId);
        Require(team.Phase == "verify" && team.Round == 1 && Last(leader).Contains("成品还没齐时（例如设计已确认、实现还没做）")
            && Last(leader).Contains("发现问题或还有下一步时"), "after the design the leader is asked for the next step as well as fixes");
        teams.Turned(leader, "done", "设计可行。\nASSIGN member-1: 按设计实现 agent-windows/Organizer.cs，交回改动文件和编译结果\nASSIGN member-2: 只负责 tests/OrganizerCheck.cs，按设计写测试并交回运行输出");
        team = Team(teams, mixedId);
        string m2 = team.Members["member-2"];
        Require(team.Round == 2 && team.Awaited.OrderBy(x => x).SequenceEqual(new[] { "member-1", "member-2" })
            && Last(m1).Contains("Organizer.cs") && Last(m2).Contains("OrganizerCheck.cs"), "the building goes out in the next round, each member to its own file");
        teams.Turned(m1, "done", "已实现 Organizer.cs，编译通过。");
        int toldLeader = told.Count(x => x[0] == leader);
        teams.Turned(m2, "done", "Checking the updated organizer UI, workbench guards, and Android aggregate checks now.");
        team = Team(teams, mixedId);
        Require(Last(m2) == Teams.KeepAtIt && team.Phase == "" && team.Awaited.SequenceEqual(new[] { "member-2" }) && told.Count(x => x[0] == leader) == toldLeader,
            "a member that only says what it is checking now is sent back, and no acceptance opens");
        teams.Turned(m2, "done", "Checked the organizer UI, workbench guards and Android aggregate: OrganizerCheck.cs 6 passed, android_aggregate_check.py passed.");
        teams.Turned(leader, "done", "VERIFIED: 复跑 OrganizerCheck 6 项通过，核对 Organizer.cs 与设计一致\nDONE: 设计与实现完成");
        Require(Team(teams, mixedId).State == "done" && Team(teams, mixedId).Round == 2, "design and building end together once accepted");

        // design alone: one member, accepted on the design document without any change of code
        string designId = "7272727272727272";
        leader = teams.Start(preset(designId, "只做设计：给出对话整理模块的结构，不写代码")).Members["leader"];
        int before = made;
        teams.Turned(leader, "done", "ASSIGN member-1: 写 docs/organize-design.md，交回设计要点");
        teams.Turned(Team(teams, designId).Members["member-1"], "done", "已写 docs/organize-design.md。");
        team = Team(teams, designId);
        Require(made == before + 1 && team.Phase == "verify" && Last(leader).Contains("只交付设计时，核对设计是否覆盖目标、约束和取舍，不要求代码改动"),
            "a design alone goes to one member and is accepted as a design");
        teams.Turned(leader, "done", "VERIFIED: 查看 docs/organize-design.md，覆盖目标、接口和取舍\nDONE: 设计已交付");
        Require(Team(teams, designId).State == "done" && Team(teams, designId).Round == 1, "a design is done without a round of building");

        // a self-forming leader is offered only tools that can take work, sees every installed tool's quota, and an
        // ASSIGN to a spent tool goes nowhere
        var tight = Hands();
        tight.Available = () => new[] { "claude", "agy" };
        tight.Limits = tool => tool == "agy" ? new[] { new Quotas.Window { Name = "five_hour", Used = 100 } } : new[] { new Quotas.Window { Name = "five_hour", Used = 20 } };
        var formed = new Teams(null, tight);
        string autoId = "7373737373737373";
        var auto = Lead(autoId, new object[] { "claude" }, 0);
        auto["auto_roster"] = true;
        auto["roster"] = new[] { Member("leader", "claude") };
        leader = formed.Start(auto).Members["leader"];
        string offer = Last(leader);
        Require(offer.Contains("可以派工的工具：claude。") && offer.Contains("ASSIGN member-1 claude:") && offer.Contains("ASSIGN member-2 claude:")
            && !offer.Contains("codex:") && !offer.Contains("agy:"), "the examples name only a tool that can take work: " + offer);
        Require(offer.Contains("- Antigravity CLI：额度：5 小时窗口已用 100%，已用尽（不可派工）") && offer.Contains("- Claude Code（你）："),
            "every installed tool's quota is shown to a self-forming leader");
        formed.Turned(leader, "done", "ASSIGN member-1 agy: 写测试");
        Require(Team(formed, autoId).Members.Count == 1 && Last(leader).Contains("没有在你的回答里找到分派"), "a task for a spent tool goes nowhere");
        formed.Turned(leader, "done", "ASSIGN member-1 claude: 写测试，交回运行输出");
        Require(Team(formed, autoId).Roster.Length == 2 && Team(formed, autoId).Roster[1].Tool == "claude", "a task for a tool that can take work makes its member");

        var none = Hands();
        none.Available = () => new[] { "claude" };
        none.Limits = tool => new[] { new Quotas.Window { Name = "five_hour", Used = 100 } };
        var alone = new Teams(null, none);
        auto["id"] = "7474747474747474";
        leader = alone.Start(auto).Members["leader"];
        Require(Last(leader).Contains("现在没有可以派工的工具") && !Last(leader).Contains("ASSIGN member-1 "), "with no tool left, no example invites a task");
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

    /// 分工协作 with free rounds (`rounds` 0): the leader sets each round's aim by what it has seen; a first round that
    /// meets the goal goes straight to acceptance and ends; a later round may only re-check; rounds go on past what a
    /// fixed task may set, up to the most any task may have; kept across a restart. Fixed rounds stay as they were.
    static void FreeRounds(string folder) {
        string file = Path.Combine(folder, "free.dat");
        var teams = new Teams(file, Hands());
        // only 分工协作 leaves the rounds to the leader
        foreach (string mode in new[] { "review", "compare" }) {
            bool refused = false;
            try { teams.Start(new Dictionary<string, object> { { "id", "5a5a5a5a5a5a5a5" + (mode == "review" ? "1" : "2") }, { "mode", mode }, { "dir", "web-shop" }, { "goal", "改" }, { "tools", new object[] { "claude", "codex" } }, { "rounds", 0 } }); }
            catch (ArgumentException) { refused = true; }
            Require(refused, "free rounds are refused for " + mode);
        }
        Require(teams.Start(new Dictionary<string, object> { { "id", "5b5b5b5b5b5b5b5b" }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "改" }, { "tools", new object[] { "claude", "codex" } } }).Rounds == 3, "rounds not given: three, as before");
        teams.Stop("5b5b5b5b5b5b5b5b");

        // the first round meets the goal: straight to acceptance, and done in round 1
        string early = "5c5c5c5c5c5c5c5c";
        var started = teams.Start(Lead(early, new object[] { "codex", "claude" }, 0));
        string leader = started.Members["leader"];
        Require(started.Rounds == 0 && started.Free && Last(leader).Contains("不限轮数") && Last(leader).Contains("根据实际改动和检查结果") && Last(leader).Contains("达标即结束"),
            "the leader is told the rounds are free and that it sets each round's aim by what it has seen");
        teams.Turned(leader, "done", "ASSIGN claude: 修购物车数量");
        teams.Turned(Team(teams, early).Members["claude"], "done", "改好了，cart 测试 8 项通过");
        var team = Team(teams, early);
        Require(team.Phase == "verify" && !team.Last && Last(leader).Contains("第 1 轮（不限轮数）") && !Last(leader).Contains("最后一轮"), "its results go to the acceptance, which is not a last one");
        teams.Turned(leader, "done", "VERIFIED: 跑了 cart 测试 8 项通过，数量为 0 时有提示\nDONE: 购物车已修好");
        team = Team(teams, early);
        Require(team.State == "done" && team.Round == 1, "met in the first round: done in it");

        // a later round only re-checks: a member reads and reports, the leader accepts
        string recheck = "5d5d5d5d5d5d5d5d";
        leader = teams.Start(Lead(recheck, new object[] { "codex", "claude", "agy" }, 0)).Members["leader"];
        teams.Turned(leader, "done", "ASSIGN claude: 实现结账页");
        teams.Turned(Team(teams, recheck).Members["claude"], "done", "结账页做好了");
        Require(Last(leader).Contains("仍需执行的工作按范围交给成员"), "the acceptance is told to delegate remaining work by scope");
        teams.Turned(leader, "done", "实现看过了，只差独立复核。\nASSIGN agy: 只读复核结账页金额计算，不要改文件");
        team = Team(teams, recheck);
        string agy = team.Members["agy"];
        Require(team.Round == 2 && team.Phase == "" && team.Awaited.SequenceEqual(new[] { "agy" }) && Last(agy).Contains("只读复核结账页金额计算"), "the second round is the re-check alone");
        teams.Turned(agy, "done", "复核：金额四舍五入正确，没有问题");
        teams.Turned(leader, "done", "VERIFIED: 读了 agy 的复核并自己跑了结账测试，通过\nDONE: 结账页完成");
        Require(Team(teams, recheck).State == "done" && Team(teams, recheck).Round == 2, "accepted after the re-check");

        // free rounds go on past what a fixed task may set (10), and are kept across a restart
        string longer = "5e5e5e5e5e5e5e5e";
        leader = teams.Start(Lead(longer, new object[] { "codex", "claude" }, 0)).Members["leader"];
        teams.Turned(leader, "done", "ASSIGN claude: 第一步");
        for (int round = 1; round < 12; round++) {
            teams.Turned(Team(teams, longer).Members["claude"], "done", "第 " + round + " 步做好了");
            Require(Team(teams, longer).Phase == "verify" && !Team(teams, longer).Last, "no last acceptance in round " + round);
            teams.Turned(leader, "done", "还要继续。\nASSIGN claude: 第 " + (round + 1) + " 步");
        }
        team = Team(teams, longer);
        Require(team.State == "running" && team.Round == 12 && team.Rounds == 0, "round 12 of free rounds");
        var restarted = Hands(); restarted.Say = (terminal, words) => { throw new InvalidOperationException("gone"); };
        var again = new Teams(file, restarted);
        team = Team(again, longer);
        Require(team.State == "paused" && team.Free && team.Round == 12, "kept across a restart, free");
        again.Resume(longer);
        again.Turned(Team(again, longer).Members["claude"], "done", "第 12 步做好了");
        team = Team(again, longer);
        string reopened = team.Members["leader"];
        Require(team.Phase == "verify" && !team.Last && team.Round == 12 && Last(reopened).Contains("第 12 轮（不限轮数）"), "after the restart it goes on, still free");
        // the most any task may have: the acceptance in round 20 is the last, and wanting more there stops it
        for (int round = 12; round < 20; round++) {
            again.Turned(Team(again, longer).Members["leader"], "done", "继续。\nASSIGN claude: 第 " + (round + 1) + " 步");
            again.Turned(Team(again, longer).Members["claude"], "done", "第 " + (round + 1) + " 步做好了");
        }
        team = Team(again, longer);
        Require(team.Round == 20 && team.Last && Last(team.Members["leader"]).Contains("最多的 20 轮"), "round 20 is the last acceptance");
        Require(Equals(Teams.Shown(team)["last"], true), "clients see the final acceptance in a free-round task");
        again.Turned(team.Members["leader"], "done", "还差一点。\nASSIGN claude: 再改");
        Require(Team(again, longer).State == "stopped", "past the most rounds a task may have, it stops");
    }

    /// A team's members' conversations: known by the terminals that run them, kept when they are replaced, listed
    /// under the task until it is removed, when every terminal it began is closed; the tasks in short for the lists.
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
        string other = "4242424242424242";
        teams.Start(new Dictionary<string, object> { { "id", other }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "别的任务" }, { "tools", new object[] { "codex", "claude" } } });
        string otherWriter = Team(teams, other).Members["writer"];
        // kept, and gone with the task: the conversations are history of their own again
        var reread = new Teams(kept, Hands());
        Require(reread.TeamOf("claude", first) == id && reread.All().Single(t => t.Id == id).Sessions["writer"].Id == second, "kept across a restart");
        reread.Stop(id);
        closed.Clear();
        Require(reread.Remove(id) && reread.TeamOf("claude", first) == "" && reread.TeamOf("agy", second) == "" && reread.Cards(t => false).All(c => (string)c["id"] != id), "after the task is removed its conversations are listed by themselves");
        Require(closed.Count == 2 && closed.Contains(writer) && closed.Contains(swapped.Members["writer"]), "its terminals are closed, the replaced one too, across a restart");
        Require(!closed.Contains(otherWriter) && Team(reread, other).State == "paused" && Team(reread, other).Members["writer"] == otherWriter, "another task's terminal is left open");
        closed.Clear();
        reread.Stop(other);
        Require(reread.Remove(other) && closed.SequenceEqual(new[] { otherWriter }), "removing the other task closes only its own terminal");
    }

    static void CollaborationAttachments(string folder) {
        string relative = ".remote-ai/attachments/20261121-123456-a1b2c3.png";
        string absolute = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute));
        File.WriteAllBytes(absolute, new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a });
        var attachments = Attachments.Read(folder, new object[] { relative });
        string line = "\u9644\u56fe\uff1a" + Path.GetFullPath(absolute);
        var deliveredImages = new Dictionary<string, string[]>();
        var deliveredText = new Dictionary<string, string>();
        string closed = "";
        var hands = new Teams.Hands {
            Begin = one => {
                string terminal = "attach-" + (++made);
                tools[terminal] = one.Tool;
                deliveredImages[terminal] = (one.Attachments ?? new Attachment[0]).Select(image => image.RelativePath).ToArray();
                told.Add(new[] { terminal, one.Tool, one.Words });
                return terminal;
            },
            Say = (terminal, words) => {
                if (terminal == "gone") throw new InvalidOperationException("member is gone");
                told.Add(new[] { terminal, "", words });
            },
            SayWithAttachments = (terminal, words, images) => {
                if (terminal == closed) throw new InvalidOperationException("member is gone");
                deliveredText[terminal] = words;
                deliveredImages[terminal] = (images ?? new Attachment[0]).Select(image => image.RelativePath).ToArray();
                told.Add(new[] { terminal, "", Attachments.WithText(words, images) });
            },
            ReadAttachment = (dir, path) => Attachments.Read(folder, new[] { path })[0],
            Stop = terminal => { },
            Changes = dir => ""
        };

        string direct = "a1a1a1a1a1a1a1a1";
        var teams = new Teams(Path.Combine(folder, "attachments.dat"), hands);
        teams.Start(new Dictionary<string, object> {
            { "id", direct }, { "mode", "lead" }, { "dir", "web-shop" }, { "goal", "read screenshots" },
            { "tools", new object[] { "codex", "claude" } }, { "rounds", 2 },
            { "roster", new[] { Member("leader", "codex"), Member("member-1", "claude") } }
        });
        string directLeader = Team(teams, direct).Members["leader"];
        teams.Turned(directLeader, "done", "ASSIGN member-1: inspect the screenshot");
        string member = Team(teams, direct).Members["member-1"];
        string longText = new string('x', 3900);
        teams.Say(direct, longText, "member-1", attachments);
        string directWords = Last(member);
        Require(directWords.Contains(longText) && directWords.Contains(line) && directWords.IndexOf(line, StringComparison.Ordinal) == directWords.LastIndexOf(line, StringComparison.Ordinal),
            "targeted member receives a long message with one absolute attachment line");
        Require(deliveredImages[member].SequenceEqual(new[] { relative }) && !deliveredText[member].Contains(line),
            "team_say supplies a separate attachment to the existing member");
        closed = member;
        teams.Say(direct, "reopen with the screenshot", "member-1", attachments);
        string reopenedMember = Team(teams, direct).Members["member-1"];
        Require(reopenedMember != member && deliveredImages[reopenedMember].SequenceEqual(new[] { relative }),
            "a new member receives the attachment at conversation start");
        Require(Team(teams, direct).Log.Any(step => step.Text.Contains(line)), "team history keeps the absolute attachment path");
        var reread = new Teams(Path.Combine(folder, "attachments.dat"), hands);
        Require(Team(reread, direct).Log.Any(step => step.Text.Contains(line)), "reloaded team history keeps the path for opening the original image");

        string handoff = "b2b2b2b2b2b2b2b2";
        teams.Start(new Dictionary<string, object> {
            { "id", handoff }, { "mode", "lead" }, { "dir", "web-shop" }, { "goal", "send to leader" },
            { "tools", new object[] { "codex", "claude" } }, { "rounds", 2 },
            { "roster", new[] { Member("leader", "codex"), Member("member-1", "claude") } }
        });
        string handoffLeader = Team(teams, handoff).Members["leader"];
        teams.Turned(handoffLeader, "done", "ASSIGN member-1: inspect the screenshot");
        string handoffMember = Team(teams, handoff).Members["member-1"];
        string[] newer = Enumerable.Range(4, 4).Select(i => ".remote-ai/attachments/20261121-123456-a1b2c" + i + ".png").ToArray();
        foreach (string path in newer) File.Copy(absolute, Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));
        teams.Say(handoff, "queue this for the leader", "", attachments);
        teams.Say(handoff, "queue four more images", "", Attachments.Read(folder, newer));
        Require(!Last(handoffLeader).Contains(line), "an automatic leader handoff waits for the member batch");
        teams = new Teams(Path.Combine(folder, "attachments.dat"), hands);
        Require(Team(teams, handoff).State == "paused", "the interrupted task awaits resuming after reload");
        teams.Resume(handoff);
        teams.Turned(handoffMember, "done", "screenshot inspected");
        Require(Last(handoffLeader).Contains("queue this for the leader") && Last(handoffLeader).Contains(line),
            "a reloaded leader handoff keeps an older image as a project file reference");
        Require(deliveredImages[handoffLeader].SequenceEqual(newer) && newer.All(path => !deliveredText[handoffLeader].Contains(Path.GetFileName(path))),
            "a reloaded leader handoff sends the latest four images without duplicate path text");

        string paused = "c3c3c3c3c3c3c3c3";
        teams.Start(new Dictionary<string, object> {
            { "id", paused }, { "mode", "review" }, { "dir", "web-shop" }, { "goal", "resume with screenshot" },
            { "tools", new object[] { "claude", "codex" } }, { "rounds", 1 }
        });
        string writer = Team(teams, paused).Members["writer"];
        teams.Turned(writer, "done", "initial draft");
        string reviewer = Team(teams, paused).Members["reviewer"];
        teams.Turned(reviewer, "done", "REVIEW: CHANGES\nplease continue");
        Require(Team(teams, paused).State == "paused" && Team(teams, paused).Spent, "review task pauses with its round spent");
        teams.Say(paused, "continue with the screenshot", "", attachments);
        Require(Team(teams, paused).State == "running" && Last(writer).Contains("continue with the screenshot") && Last(writer).Contains(line),
            "a paused team_say resumes the kept work with the attachment path");
        Require(deliveredImages[writer].SequenceEqual(new[] { relative }), "paused team_say resumes the member with an attachment");
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
            Require(Team(teams, spent).State == "paused" && Team(teams, spent).Note.Contains("已到 1 轮") && Team(teams, spent).Note.Contains("未通过") && Team(teams, spent).Held["writer"].Contains("还不行"),
                "no more rounds than were set, and that is no pass: the findings wait for another round");
            teams.Resume(spent);
            Require(Team(teams, spent).State == "running" && Team(teams, spent).Round == 2 && Last(writer).Contains("还不行") && Last(writer).Contains("第 2 轮"), "继续: the writer gets the findings in a round added");
            teams.Stop(spent);

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
                && Last(wLeader).Contains("80%") && Last(wLeader).Contains("只派小而必要的任务"), "the leader sees each quota and the reduced workload for tight accounts: " + Last(wLeader));
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
            Require(lft.State == "running" && lft.Awaited.SequenceEqual(new[] { "leader" }) && Last(lfLeader).Contains("分页已修复") && Last(lfLeader).Contains("【执行失败】") && Last(lfLeader).Contains("额度已用尽") && Last(lfLeader).Contains("不可派工"), "leader receives agy result + CC failure + unavailable marker");
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
            Require(lf2t.State == "running" && lf2t.Phase == "verify" && lf2t.Last && Last(lf2Leader).Contains("日志修好了") && Last(lf2Leader).Contains("最后一轮") && Last(lf2Leader).Contains("用户追加一轮后再派给成员"),
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

            // 5b) Claude 5-hour quota failure + failover to agy clears leader/agy failed state so UI does not stay "已中断"
            Require(Teams.IsQuotaError("You've hit your 5-hour limit · resets 8pm (Asia/Shanghai)"), "5-hour limit counts as quota error");
            string qf = "1919191919191919";
            var qfPreset = Lead(qf, new object[] { "claude", "codex" }, 2);
            qfPreset["roster"] = new[] { Member("leader", "claude"), Member("member-1", "agy") };
            failTeams.Start(qfPreset);
            string qfLeader = Team(failTeams, qf).Members["leader"];
            failTeams.Turned(qfLeader, "failed", "You've hit your 5-hour limit · resets 8pm (Asia/Shanghai)");
            var qft = Team(failTeams, qf);
            Require(qft.State == "running" && qft.Roster[0].Tool == "agy" && qft.Awaited.SequenceEqual(new[] { "leader" })
                && !qft.Failed.ContainsKey("leader") && !qft.Failed.ContainsKey("agy") && qft.Failed.ContainsKey("claude"),
                "auto-promoted agy leader clears leader/agy failure while marking claude exhausted");

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
            DelegationAndHandback(folder);
            Dispatching();
            Choosing(file);
            Grouping(file);
            Roster(folder);
            FreeRounds(folder);
            CollaborationAttachments(folder);
        } catch (Exception problem) {
            Console.Error.WriteLine("teams check failed: " + problem.Message + "\n" + problem.StackTrace);
            return 1;
        } finally { try { Directory.Delete(folder, true); } catch { } }
        Console.WriteLine("teams: review, lead with acceptance, free rounds, flexible roster, discuss-first, parallel compare/pick, interject/continue, quotas, models, member sessions, cards, stop/resume and persistence ok");
        return 0;
    }
}
