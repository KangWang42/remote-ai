package io.github.kangwang42.remotecli;

import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * How much of each tool's usage limits remains, calculated from what the computers report as used
 * (device.quotas, docs/PROTOCOL.md): the limits are the account's, not a computer's, so of what several
 * computers tell the newest is shown for each tool. The words match the computer's own page (web/me.js).
 */
final class QuotaLines {
    static final class Window {
        final String name, group; final double used; final long resets;
        Window(String name, double used, long resets) { this(name, used, resets, ""); }
        Window(String name, double used, long resets, String group) { this.name = name; this.used = used; this.resets = resets; this.group = group == null ? "" : group.trim(); }
    }
    static final class Reading {
        final String tool, why, billingType, balance; final long at; final List<Window> windows;
        Reading(String tool, long at, String why, List<Window> windows) { this(tool, at, why, windows, "", ""); }
        Reading(String tool, long at, String why, List<Window> windows, String billingType, String balance) {
            this.tool = tool; this.at = at; this.why = why == null ? "" : why; this.windows = windows;
            this.billingType = billingType; this.balance = balance;
        }
    }
    private static final String[] ORDER = {"claude", "codex", "agy"};

    /** Of each tool, what was told last; a window whose time is over is left out, and a tool with nothing to say. */
    static List<Reading> newest(List<Reading> all, long nowSeconds) {
        Map<String, Reading> latest = new LinkedHashMap<>();
        for (Reading one : all) {
            Reading had = latest.get(one.tool);
            if (had == null || one.at > had.at) latest.put(one.tool, one);
        }
        List<Reading> shown = new ArrayList<>();
        for (String tool : ORDER) {
            Reading one = latest.get(tool);
            if (one == null) continue;
            List<Window> open = new ArrayList<>();
            for (Window w : one.windows) if (w.resets == 0 || w.resets > nowSeconds) open.add(w);
            if (!open.isEmpty() || !one.why.isEmpty() || !one.billingType.isEmpty()) {
                shown.add(new Reading(one.tool, one.at, one.why, Collections.unmodifiableList(open), one.billingType, one.balance));
            }
        }
        return shown;
    }
    /** Windows shown by default: the tool's primary pool (without group), or all windows if none are ungrouped. */
    static List<Window> primary(Reading one) {
        List<Window> main = new ArrayList<>();
        for (Window w : one.windows) if (w.group.isEmpty()) main.add(w);
        return main.isEmpty() ? one.windows : main;
    }
    /** Secondary model pools hidden behind a toggle until the user expands them. */
    static List<Window> extra(Reading one) {
        boolean hasMain = false;
        for (Window w : one.windows) if (w.group.isEmpty()) { hasMain = true; break; }
        if (!hasMain) return Collections.emptyList();
        List<Window> more = new ArrayList<>();
        for (Window w : one.windows) if (!w.group.isEmpty()) more.add(w);
        return more;
    }
    static int extraPoolCount(List<Window> extra) {
        java.util.LinkedHashSet<String> names = new java.util.LinkedHashSet<>();
        for (Window w : extra) if (!w.group.isEmpty()) names.add(w.group);
        return names.size();
    }
    static boolean anyHigh(List<Window> windows) {
        for (Window w : windows) if (high(w)) return true;
        return false;
    }
    static String tool(String tool) { return "claude".equals(tool) ? "Claude Code" : "codex".equals(tool) ? "Codex" : "agy".equals(tool) ? "Antigravity CLI" : tool; }
    static String window(String name) {
        if ("five_hour".equals(name) || name.endsWith("_five_hour")) return "5 小时";
        if ("seven_day".equals(name) || name.endsWith("_seven_day")) return "本周";
        if (name.matches("\\d+m")) { long minutes = Long.parseLong(name.substring(0, name.length() - 1)); return minutes >= 1440 ? Math.round(minutes / 1440.0) + " 天" : Math.round(minutes / 60.0) + " 小时"; }
        return name;
    }
    static String label(String tool, Window w) { return (w.group.isEmpty() ? tool(tool) : w.group) + " · " + window(w.name); }
    /** Remaining quota percentage clamped to [0, 100], converted from the reported used share. */
    static double remaining(double used) {
        if (Double.isNaN(used)) return 0;
        return Math.max(0.0, Math.min(100.0, 100.0 - used));
    }
    /** Visual fill share for the remaining bar: 0 when exhausted, otherwise at least 2% so small remainders stay visible. */
    static float barShare(double used) {
        double left = remaining(used);
        return left <= 0 ? 0f : (float) Math.max(2.0, Math.min(100.0, left));
    }
    /** "剩余 62% · 2 小时后重置" */
    static String detail(Window w, long nowSeconds) {
        String left = "剩余 " + Math.round(remaining(w.used)) + "%";
        if (w.resets == 0) return left;
        long minutes = Math.round((w.resets - nowSeconds) / 60.0);
        if (minutes <= 0) return left + " · 已重置";
        return left + " · " + (minutes < 60 ? minutes + " 分钟" : minutes < 2880 ? Math.round(minutes / 60.0) + " 小时" : Math.round(minutes / 1440.0) + " 天") + "后重置";
    }
    /** Newest reading timestamp across all displayed tools (0 if empty). */
    static long newestAt(List<Reading> shown) {
        long at = 0;
        for (Reading one : shown) if (one.at > at) at = one.at;
        return at;
    }
    enum Level { NORMAL, ELEVATED, HIGH }
    static Level level(double used) {
        if (used >= 80) return Level.HIGH;
        return used > 50 ? Level.ELEVATED : Level.NORMAL;
    }
    static boolean high(Window w) { return level(w.used) == Level.HIGH; }

    private QuotaLines() { }
}
