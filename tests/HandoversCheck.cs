using System;
using System.IO;
using System.Linq;
using RemoteCli;

static class HandoversCheck {
    static void Require(bool ok, string why) { if (!ok) throw new Exception("FAILED: " + why); }

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
        Require(h.Title("claude", "A") == "", "A has no predecessor title");

        var line = h.Line("agy", "C");
        Require(line.Count == 2 && line[0].From == "B" && line[1].From == "A", "line nearest first");

        // Reject cycles and self-links
        h.Add("claude", "A", "agy", "C", "cycle");
        Require(h.Line("claude", "A").Count == 0, "cycle rejected");
        h.Add("claude", "X", "claude", "X", "self");
        Require(h.Line("claude", "X").Count == 0, "self-link rejected");

        // Reload from disk preserves links
        var h2 = new Handovers(file);
        Require(h2.GoneOn("claude", "a"), "case-insensitive GoneOn after reload");
        Require(h2.Title("agy", "c") == "最初的标题", "title after reload");

        // StripAsk strips handover prefix only when AskMarker is present
        string wrapped = "【对话交接】下面是进展\n\n" + Handovers.AskMarker + "\n继续排查报错";
        Require(Handovers.StripAsk(wrapped) == "继续排查报错", "StripAsk extracts owner message");
        Require(Handovers.StripAsk("普通消息") == "普通消息", "StripAsk keeps normal message");
        Require(Handovers.StripAsk("【对话交接】独立交接\n\n我的补充：\n看这里") == "【对话交接】独立交接\n\n我的补充：\n看这里", "StripAsk keeps standalone handoff");

        // Cap at Handovers.Most
        for (int i = 0; i < Handovers.Most + 10; i++) h2.Add("codex", "s" + i, "claude", "f" + i, "t" + i);
        var h3 = new Handovers(file);
        Require(!h3.GoneOn("claude", "f0"), "oldest link evicted beyond cap");
        Require(h3.GoneOn("claude", "f" + (Handovers.Most + 9)), "newest link kept");

        Console.WriteLine("HandoversCheck ok");
        return 0;
    }
}
