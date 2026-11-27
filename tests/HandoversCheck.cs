using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RemoteCli;

static class HandoversCheck {
    static void Require(bool ok, string why) { if (!ok) throw new Exception("FAILED: " + why); }
    static Dictionary<string, object> Msg(string role, string text) {
        return new Dictionary<string, object> { { "role", role }, { "text", text } };
    }

    static int Main(string[] args) {
        string dir = args[0];
        string file = Path.Combine(dir, "handovers.json");
        var h = new Handovers(file);

        // Basic chain A (claude) -> B (codex) -> C (agy)
        h.Add("codex", "B", "claude", "A", "最初的标题");
        h.Add("agy", "C", "codex", "B", "第二个标题");

        Require(h.GoneOn("claude", "A"), "A is gone on");
        Require(h.GoneOn("codex", "B"), "B is gone on");
        Require(!h.GoneOn("agy", "C"), "C is latest");

        Require(h.Title("agy", "C") == "最初的标题", "C keeps A's title: " + h.Title("agy", "C"));
        Require(h.Title("codex", "B") == "最初的标题", "B keeps A's title");
        Require(h.Title("claude", "A") == "最初的标题", "A belongs to thread titled 最初的标题");

        var turns = h.TurnsOf("agy", "C");
        Require(turns.Count == 3 && turns[0].Session == "A" && turns[1].Session == "B" && turns[2].Session == "C", "turns oldest first");
        Require(h.PreviousSession("agy", "C", "claude") == "A", "previous session for claude");
        Require(h.PreviousSession("agy", "C", "codex") == "B", "previous session for codex");
        Require(h.PreviousSession("agy", "C", "agy") == "", "no prior agy session besides current");

        // Switch-back thread: X (claude) -> Y (codex) -> X (claude) -> Y (codex)
        h.Add("codex", "Y", "claude", "X", "往返测试");
        h.Add("claude", "X", "codex", "Y", "ignore");
        Require(h.GoneOn("codex", "Y"), "Y is gone on after switch back to X");
        Require(!h.GoneOn("claude", "X"), "X is active after switch back");
        Require(h.Title("claude", "X") == "往返测试", "X keeps original title");
        Require(h.PreviousSession("claude", "X", "codex") == "Y", "Y available when switching back to codex");

        h.Add("codex", "Y", "claude", "X", "ignore");
        Require(h.GoneOn("claude", "X"), "X is gone on after second switch to Y");
        Require(!h.GoneOn("codex", "Y"), "Y is active again");
        var rt = h.TurnsOf("codex", "Y");
        Require(rt.Count == 4 && rt[0].Session == "X" && rt[1].Session == "Y" && rt[2].Session == "X" && rt[3].Session == "Y", "4 chronological turns");

        // Reject cross-thread collision and self-links
        h.Add("claude", "A", "codex", "Y", "cross-thread");
        Require(h.TurnsOf("codex", "Y").Count == 4, "cross-thread collision rejected");
        h.Add("claude", "Z", "claude", "Z", "self");
        Require(h.TurnsOf("claude", "Z").Count == 0, "self-link rejected");

        // Reload from disk preserves threads
        var h2 = new Handovers(file);
        Require(h2.GoneOn("claude", "a"), "case-insensitive GoneOn after reload");
        Require(h2.Title("agy", "c") == "最初的标题", "title after reload");
        Require(h2.TurnsOf("codex", "y").Count == 4, "round-trip turns preserved after reload");

        // V1 links migration on load
        string v1File = Path.Combine(dir, "v1-handovers.json");
        File.WriteAllText(v1File, "{\"links\":[{\"tool\":\"codex\",\"session\":\"s2\",\"from_tool\":\"claude\",\"from\":\"s1\",\"title\":\"旧版链\"},{\"tool\":\"agy\",\"session\":\"s3\",\"from_tool\":\"codex\",\"from\":\"s2\",\"title\":\"旧版链\"}]}", new UTF8Encoding(false));
        var hv1 = new Handovers(v1File);
        var vt = hv1.TurnsOf("agy", "s3");
        Require(vt.Count == 3 && vt[0].Session == "s1" && vt[1].Session == "s2" && vt[2].Session == "s3", "v1 links migrated to turns");
        Require(hv1.GoneOn("claude", "s1") && hv1.GoneOn("codex", "s2") && !hv1.GoneOn("agy", "s3"), "v1 GoneOn preserved");

        // SplitVisits checks
        var m0 = Msg("user", "最初的问题");
        var m1 = Msg("assistant", "初步分析");
        var m2 = Msg("user", "【对话交接】期间进展\n\n【现在的要求】\n继续核对");
        var m3 = Msg("assistant", "已核对完毕");
        var m4 = Msg("user", "【对话交接】又一段进展\n\n【现在的要求】\n收尾");
        var m5 = Msg("assistant", "已收尾");

        // Exact 2 visits
        var exact = Handovers.SplitVisits(new List<Dictionary<string, object>> { m0, m1, m2, m3 }, 2, false, false);
        Require(exact.Count == 2 && exact[0].Count == 2 && exact[1].Count == 2 && ReferenceEquals(exact[1][0], m2), "SplitVisits exact 2 visits");

        // Unflushed latest visit (align start)
        var unflushed = Handovers.SplitVisits(new List<Dictionary<string, object>> { m0, m1 }, 2, false, false);
        Require(unflushed.Count == 2 && unflushed[0].Count == 2 && unflushed[1].Count == 0, "SplitVisits unflushed latest visit aligns start");

        // Head-truncated oldest visit (align end)
        var trunc = Handovers.SplitVisits(new List<Dictionary<string, object>> { m2, m3 }, 2, false, true);
        Require(trunc.Count == 2 && trunc[0].Count == 0 && trunc[1].Count == 2, "SplitVisits head-truncated aligns end");

        // Unflushed 2nd visit of non-root session (startsWithHandover = true -> align start!)
        var bUnflushed = Handovers.SplitVisits(new List<Dictionary<string, object>> { m2, m3 }, 2, true, false);
        Require(bUnflushed.Count == 2 && bUnflushed[0].Count == 2 && bUnflushed[1].Count == 0, "SplitVisits non-root unflushed aligns start");

        // Excess chunks merged into last slot
        var excess = Handovers.SplitVisits(new List<Dictionary<string, object>> { m0, m1, m2, m3, m4, m5 }, 2, false, false);
        Require(excess.Count == 2 && excess[0].Count == 2 && excess[1].Count == 4 && ReferenceEquals(excess[1][3], m5), "SplitVisits excess chunks merge into last");

        // StripAsk & IsHandoverMessage
        string wrapped = "【对话交接】下面是进展\n\n" + Handovers.AskMarker + "\n继续排查报错";
        Require(Handovers.IsHandoverMessage(wrapped), "IsHandoverMessage true");
        Require(Handovers.StripAsk(wrapped) == "继续排查报错", "StripAsk extracts owner message");
        Require(Handovers.StripAsk("普通消息") == "普通消息", "StripAsk keeps normal message");
        string standalone = "【对话交接】独立交接\n\n我的补充：\n看这里";
        Require(!Handovers.IsHandoverMessage(standalone), "IsHandoverMessage false for standalone handoff");
        Require(Handovers.StripAsk(standalone) == standalone, "StripAsk keeps standalone handoff");

        // Cap at Handovers.Most
        for (int i = 0; i < Handovers.Most + 10; i++) h2.Add("codex", "s" + i, "claude", "f" + i, "t" + i);
        var h3 = new Handovers(file);
        Require(!h3.GoneOn("claude", "f0"), "oldest thread evicted beyond cap");
        Require(h3.GoneOn("claude", "f" + (Handovers.Most + 9)), "newest thread kept");

        Console.WriteLine("HandoversCheck ok");
        return 0;
    }
}
