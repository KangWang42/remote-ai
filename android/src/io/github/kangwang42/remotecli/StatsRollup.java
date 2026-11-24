package io.github.kangwang42.remotecli;

import java.text.ParseException;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Calendar;
import java.util.Collection;
import java.util.Collections;
import java.util.Date;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.TreeMap;

/**
 * Pure aggregation and formatting logic for daily work statistics across one or more computers.
 * Keeps date alignment, stacked per-tool counts, streaks, and readable unit formatting free of
 * Android framework dependencies so it can be verified directly on the host JVM.
 */
final class StatsRollup {
    private static final String[] WEEKDAYS = { "日", "一", "二", "三", "四", "五", "六" };

    static final class ToolTotals {
        long prompts, activeSec, tokens, cached;

        void add(long prompts, long activeSec, long tokens, long cached) {
            this.prompts += Math.max(0, prompts);
            this.activeSec += Math.max(0, activeSec);
            this.tokens += Math.max(0, tokens);
            this.cached += Math.max(0, cached);
        }
    }

    static final class DayPoint {
        final String date;
        long prompts, sessions, activeSec, inTokens, outTokens, cachedTokens;
        final Map<String, ToolTotals> tools = new LinkedHashMap<>();

        DayPoint(String date) { this.date = date == null ? "" : date; }

        void addTool(String tool, long prompts, long activeSec, long tokens, long cached) {
            if (tool == null || tool.isEmpty()) return;
            ToolTotals entry = tools.get(tool);
            if (entry == null) { entry = new ToolTotals(); tools.put(tool, entry); }
            entry.add(prompts, activeSec, tokens, cached);
        }

        long toolPrompts(String tool) {
            ToolTotals t = tools.get(tool);
            return t == null ? 0 : t.prompts;
        }

        long otherPrompts() {
            long known = toolPrompts("claude") + toolPrompts("codex") + toolPrompts("agy");
            return Math.max(0, prompts - known);
        }

        long totalTokens() { return inTokens + outTokens + cachedTokens; }
    }

    static final class ComputerSnapshot {
        final List<DayPoint> days = new ArrayList<>();
        int streak, bestStreak;
        long updatedMs;
    }

    static final class Summary {
        final List<DayPoint> days = new ArrayList<>();
        final Map<String, ToolTotals> tools = new LinkedHashMap<>();
        long todayPrompts, todaySessions, todayActiveSec, todayTotalTokens;
        long yesterdayPrompts, yesterdaySessions, yesterdayActiveSec;
        long spanPrompts, spanActiveSec, spanTotalTokens, spanCachedTokens, spanFreshInTokens, spanOutTokens;
        int activeDays, streak, bestStreak;
        boolean hasYesterday;
        double avgPrompts;
        long latestUpdatedMs;

        DayPoint today() { return days.isEmpty() ? null : days.get(days.size() - 1); }
    }

    /** Merges per-computer snapshots aligned by calendar date, keeping up to the last `maxDays` days. */
    static Summary aggregate(Collection<ComputerSnapshot> snapshots, int maxDays) {
        Summary out = new Summary();
        if (snapshots == null || snapshots.isEmpty()) return out;

        TreeMap<String, DayPoint> byDate = new TreeMap<>();
        int maxReportedStreak = 0, maxReportedBest = 0;
        for (ComputerSnapshot snap : snapshots) {
            if (snap == null) continue;
            if (snap.updatedMs > out.latestUpdatedMs) out.latestUpdatedMs = snap.updatedMs;
            if (snap.streak > maxReportedStreak) maxReportedStreak = snap.streak;
            if (snap.bestStreak > maxReportedBest) maxReportedBest = snap.bestStreak;
            for (DayPoint src : snap.days) {
                if (src == null || src.date.isEmpty()) continue;
                DayPoint dst = byDate.get(src.date);
                if (dst == null) { dst = new DayPoint(src.date); byDate.put(src.date, dst); }
                dst.prompts += Math.max(0, src.prompts);
                dst.sessions += Math.max(0, src.sessions);
                dst.activeSec += Math.max(0, src.activeSec);
                dst.inTokens += Math.max(0, src.inTokens);
                dst.outTokens += Math.max(0, src.outTokens);
                dst.cachedTokens += Math.max(0, src.cachedTokens);
                for (Map.Entry<String, ToolTotals> te : src.tools.entrySet()) {
                    ToolTotals tt = te.getValue();
                    if (tt != null) dst.addTool(te.getKey(), tt.prompts, tt.activeSec, tt.tokens, tt.cached);
                }
            }
        }

        List<DayPoint> all = new ArrayList<>(byDate.values());
        int keep = Math.max(1, maxDays);
        int start = Math.max(0, all.size() - keep);
        for (int i = start; i < all.size(); i++) out.days.add(all.get(i));

        for (DayPoint day : out.days) {
            out.spanPrompts += day.prompts;
            out.spanActiveSec += day.activeSec;
            out.spanFreshInTokens += day.inTokens;
            out.spanOutTokens += day.outTokens;
            out.spanCachedTokens += day.cachedTokens;
            out.spanTotalTokens += day.totalTokens();
            if (day.prompts > 0) out.activeDays++;
            for (Map.Entry<String, ToolTotals> te : day.tools.entrySet()) {
                ToolTotals tt = te.getValue();
                if (tt == null) continue;
                ToolTotals acc = out.tools.get(te.getKey());
                if (acc == null) { acc = new ToolTotals(); out.tools.put(te.getKey(), acc); }
                acc.add(tt.prompts, tt.activeSec, tt.tokens, tt.cached);
            }
        }
        if (!out.days.isEmpty()) {
            out.avgPrompts = (double) out.spanPrompts / out.days.size();
            DayPoint today = out.days.get(out.days.size() - 1);
            out.todayPrompts = today.prompts;
            out.todaySessions = today.sessions;
            out.todayActiveSec = today.activeSec;
            out.todayTotalTokens = today.totalTokens();
            if (out.days.size() > 1) {
                DayPoint y = out.days.get(out.days.size() - 2);
                out.hasYesterday = true;
                out.yesterdayPrompts = y.prompts;
                out.yesterdaySessions = y.sessions;
                out.yesterdayActiveSec = y.activeSec;
            }
        }

        int computedStreak = computeWindowStreak(out.days);
        out.streak = Math.max(maxReportedStreak, computedStreak);
        out.bestStreak = Math.max(maxReportedBest, out.streak);
        return out;
    }

    private static int computeWindowStreak(List<DayPoint> days) {
        if (days.isEmpty()) return 0;
        int idx = days.size() - 1;
        if (days.get(idx).prompts <= 0 && idx > 0) idx--;
        int run = 0;
        while (idx >= 0 && days.get(idx).prompts > 0) {
            run++;
            idx--;
        }
        return run;
    }

    /** Formats a duration in seconds compactly and readably: "0 分", "<1 分", "42 分", "1h 25m". */
    static String formatDuration(long seconds) {
        if (seconds <= 0) return "0 分";
        if (seconds < 60) return "<1 分";
        long mins = Math.round(seconds / 60.0);
        if (mins < 60) return mins + " 分";
        long hours = mins / 60;
        long rem = mins % 60;
        return rem == 0 ? hours + " 小时" : hours + "h " + rem + "m";
    }

    /** Formats large token counts cleanly in Chinese units. */
    static String formatTokens(long n) {
        if (n < 0) n = 0;
        if (n >= 100_000_000L) return trim(n / 1e8) + " 亿";
        if (n >= 10_000L) return trim(n / 1e4) + " 万";
        return String.format(Locale.US, "%,d", n);
    }

    private static String trim(double value) {
        if (value >= 100) return String.valueOf(Math.round(value));
        String s = String.format(Locale.US, "%.1f", value);
        return s.endsWith(".0") ? s.substring(0, s.length() - 2) : s;
    }

    /** Formats today-vs-yesterday comparison cleanly. */
    static String formatDelta(long today, long yesterday, boolean hasYesterday) {
        if (!hasYesterday) return "今日";
        long diff = today - yesterday;
        if (diff == 0) return yesterday == 0 ? "昨 0" : "与昨持平";
        return diff > 0 ? "较昨 +" + diff : "较昨 " + diff;
    }

    static String formatDurationDelta(long todaySec, long yesterdaySec, boolean hasYesterday) {
        if (!hasYesterday) return "今日";
        long todayMin = Math.round(todaySec / 60.0);
        long yestMin = Math.round(yesterdaySec / 60.0);
        long diff = todayMin - yestMin;
        if (diff == 0) return yestMin == 0 ? "昨 0 分" : "与昨持平";
        return diff > 0 ? "较昨 +" + diff + " 分" : "较昨 " + diff + " 分";
    }

    static String shortDate(String yyyyMmDd) {
        if (yyyyMmDd == null) return "";
        String[] parts = yyyyMmDd.split("-");
        if (parts.length != 3) return yyyyMmDd;
        try {
            return Integer.parseInt(parts[1]) + "月" + Integer.parseInt(parts[2]) + "日";
        } catch (NumberFormatException ignored) {
            return yyyyMmDd;
        }
    }

    static String shortWeekday(String yyyyMmDd, boolean isToday) {
        if (isToday) return "今";
        if (yyyyMmDd == null || yyyyMmDd.length() < 10) return "";
        try {
            SimpleDateFormat fmt = new SimpleDateFormat("yyyy-MM-dd", Locale.US);
            Date parsed = fmt.parse(yyyyMmDd);
            if (parsed == null) return "";
            Calendar cal = Calendar.getInstance();
            cal.setTime(parsed);
            int dow = cal.get(Calendar.DAY_OF_WEEK); // 1=Sun..7=Sat
            return WEEKDAYS[(dow - 1) % 7];
        } catch (ParseException ignored) {
            return "";
        }
    }

    static String dayInspector(DayPoint day, boolean isToday) {
        if (day == null) return "";
        String head = isToday ? "今天" : shortDate(day.date) + " 周" + shortWeekday(day.date, false);
        if (day.prompts <= 0 && day.activeSec <= 0) return head + " · 暂无记录";
        StringBuilder sb = new StringBuilder();
        sb.append(head).append(" · ").append(day.prompts).append(" 次提问");
        if (day.activeSec > 0) sb.append(" · ").append(formatDuration(day.activeSec));
        List<String> parts = new ArrayList<>();
        if (day.toolPrompts("claude") > 0) parts.add("Claude " + day.toolPrompts("claude"));
        if (day.toolPrompts("codex") > 0) parts.add("Codex " + day.toolPrompts("codex"));
        if (day.toolPrompts("agy") > 0) parts.add("Agy " + day.toolPrompts("agy"));
        if (!parts.isEmpty()) {
            sb.append("（");
            for (int i = 0; i < parts.size(); i++) {
                if (i > 0) sb.append(" · ");
                sb.append(parts.get(i));
            }
            sb.append("）");
        }
        return sb.toString();
    }

    /** Lists tools in consistent display order when they have any activity in the summary. */
    static List<String> activeTools(Summary summary) {
        if (summary == null || summary.tools.isEmpty()) return Collections.emptyList();
        List<String> out = new ArrayList<>();
        for (String preferred : new String[] { "claude", "codex", "agy" }) {
            ToolTotals t = summary.tools.get(preferred);
            if (t != null && (t.prompts > 0 || t.tokens > 0 || t.cached > 0)) out.add(preferred);
        }
        for (Map.Entry<String, ToolTotals> entry : summary.tools.entrySet()) {
            if (!out.contains(entry.getKey()) && entry.getValue() != null && entry.getValue().prompts > 0) {
                out.add(entry.getKey());
            }
        }
        return out;
    }

    static String toolShortName(String tool) {
        if ("claude".equals(tool)) return "Claude";
        if ("codex".equals(tool)) return "Codex";
        if ("agy".equals(tool)) return "Antigravity";
        return tool == null ? "" : tool;
    }

    private StatsRollup() { }
}
