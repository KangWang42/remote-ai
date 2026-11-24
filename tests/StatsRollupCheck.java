package io.github.kangwang42.remotecli;

import java.util.Arrays;
import java.util.List;

/** Fixtures for multi-computer daily stats rollup, formatting, and streak continuity. */
public final class StatsRollupCheck {
    private static void require(boolean ok, String why) {
        if (!ok) throw new AssertionError(why);
    }

    public static void main(String[] args) {
        StatsRollup.ComputerSnapshot pc1 = new StatsRollup.ComputerSnapshot();
        pc1.streak = 5;
        pc1.bestStreak = 9;
        pc1.updatedMs = 1_795_500_000_000L;

        StatsRollup.DayPoint d1 = new StatsRollup.DayPoint("2026-11-23");
        d1.prompts = 6; d1.sessions = 2; d1.activeSec = 900; d1.inTokens = 1000; d1.outTokens = 400; d1.cachedTokens = 12000;
        d1.addTool("claude", 4, 600, 900, 10000);
        d1.addTool("codex", 2, 300, 500, 2000);
        pc1.days.add(d1);

        StatsRollup.DayPoint d2 = new StatsRollup.DayPoint("2026-11-24");
        d2.prompts = 10; d2.sessions = 3; d2.activeSec = 3900; d2.inTokens = 2000; d2.outTokens = 800; d2.cachedTokens = 40000;
        d2.addTool("claude", 6, 2100, 1600, 30000);
        d2.addTool("agy", 4, 1800, 1200, 10000);
        pc1.days.add(d2);

        StatsRollup.ComputerSnapshot pc2 = new StatsRollup.ComputerSnapshot();
        pc2.streak = 2;
        pc2.bestStreak = 4;
        pc2.updatedMs = 1_795_500_060_000L;
        StatsRollup.DayPoint d2b = new StatsRollup.DayPoint("2026-11-24");
        d2b.prompts = 3; d2b.sessions = 1; d2b.activeSec = 600; d2b.inTokens = 500; d2b.outTokens = 200; d2b.cachedTokens = 5000;
        d2b.addTool("codex", 3, 600, 700, 5000);
        pc2.days.add(d2b);

        StatsRollup.Summary sum = StatsRollup.aggregate(Arrays.asList(pc1, pc2), 7);
        require(sum.days.size() == 2, "aligned into 2 calendar dates");
        require(sum.todayPrompts == 13 && sum.todaySessions == 4 && sum.todayActiveSec == 4500, "today sums both computers");
        require(sum.hasYesterday && sum.yesterdayPrompts == 6, "yesterday preserved");
        require(sum.streak == 5 && sum.bestStreak == 9, "keeps reported ledger streak");
        require(sum.spanPrompts == 19 && sum.activeDays == 2, "span totals");

        StatsRollup.DayPoint today = sum.today();
        require(today != null && today.toolPrompts("claude") == 6 && today.toolPrompts("codex") == 3 && today.toolPrompts("agy") == 4, "per-tool daily breakdown merged");
        List<String> tools = StatsRollup.activeTools(sum);
        require(tools.equals(Arrays.asList("claude", "codex", "agy")), "active tools ordered: " + tools);

        require("0 分".equals(StatsRollup.formatDuration(0)), "zero duration");
        require("<1 分".equals(StatsRollup.formatDuration(25)), "sub-minute duration");
        require("42 分".equals(StatsRollup.formatDuration(42 * 60)), "minutes duration");
        require("1h 15m".equals(StatsRollup.formatDuration(4500)), "hour+min duration");
        require("较昨 +7".equals(StatsRollup.formatDelta(13, 6, true)), "positive delta");
        require("与昨持平".equals(StatsRollup.formatDelta(6, 6, true)), "flat delta");
        require("1.5 万".equals(StatsRollup.formatTokens(15000)), "Chinese万 token formatting");
        String inspector = StatsRollup.dayInspector(today, true);
        require(inspector.contains("今天 · 13 次提问") && inspector.contains("Claude 6") && inspector.contains("Codex 3") && inspector.contains("Agy 4"), "inspector: " + inspector);

        System.out.println("stats rollup: multi-computer alignment, per-tool stacking, streaks, and formatting verified");
    }
}
