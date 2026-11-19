package io.github.kangwang42.remotecli;

import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * How much of each tool's usage limits is used, as the computers tell it (device.quotas, docs/PROTOCOL.md): the
 * limits are the account's, not a computer's, so of what several computers tell the newest is shown for each tool.
 * The words are those of the computer's own page (web/me.js).
 */
final class QuotaLines {
    static final class Window {
        final String name; final double used; final long resets;
        Window(String name, double used, long resets) { this.name = name; this.used = used; this.resets = resets; }
    }
    static final class Reading {
        final String tool, why; final long at; final List<Window> windows;
        Reading(String tool, long at, String why, List<Window> windows) { this.tool = tool; this.at = at; this.why = why == null ? "" : why; this.windows = windows; }
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
            if (!open.isEmpty() || !one.why.isEmpty()) shown.add(new Reading(one.tool, one.at, one.why, Collections.unmodifiableList(open)));
        }
        return shown;
    }
    static String tool(String tool) { return "claude".equals(tool) ? "Claude Code" : "codex".equals(tool) ? "Codex" : "agy".equals(tool) ? "Antigravity CLI" : tool; }
    static String window(String name) {
        if ("five_hour".equals(name)) return "5 小时";
        if ("seven_day".equals(name)) return "本周";
        if (name.matches("\\d+m")) { long minutes = Long.parseLong(name.substring(0, name.length() - 1)); return minutes >= 1440 ? Math.round(minutes / 1440.0) + " 天" : Math.round(minutes / 60.0) + " 小时"; }
        return name;
    }
    static String label(String tool, Window w) { return tool(tool) + " · " + window(w.name); }
    /** "38% · 2 小时后重置" */
    static String detail(Window w, long nowSeconds) {
        String used = Math.round(w.used) + "%";
        if (w.resets == 0) return used;
        long minutes = Math.round((w.resets - nowSeconds) / 60.0);
        if (minutes <= 0) return used + " · 已重置";
        return used + " · " + (minutes < 60 ? minutes + " 分钟" : minutes < 2880 ? Math.round(minutes / 60.0) + " 小时" : Math.round(minutes / 1440.0) + " 天") + "后重置";
    }
    static boolean high(Window w) { return w.used >= 90; }

    private QuotaLines() { }
}
