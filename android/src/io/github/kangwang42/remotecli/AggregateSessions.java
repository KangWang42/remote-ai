package io.github.kangwang42.remotecli;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/** One computer's project inventory. Kept independent of Android views for regression checks. */
final class AggregateSessions {
    static final class Entry {
        final boolean terminal, live, done;
        final long at;
        final String id, project, title, tool, state, phase, status, host, attached, session;
        String said = "";       // one line of what a running terminal last said; changes often and is left out of signatures
        /**
         * A conversation that a program on the computer has open, of which its tool tells (the relay's `reach`): it
         * is at work there like a terminal, and stands among the tasks by how it stands. canSay: a message can be left for it.
         */
        boolean reached, canSay;
        String origin = "";     // where a conversation on the computer runs, as the computer says it: "Codex 桌面版", "命令行", "VS Code"
        long used;              // latest terminal phase change or saved conversation update; 0 when not known
        Entry(boolean terminal, String id, String project, String title, String tool, String state,
              String phase, String status, boolean live, String host, String attached, String session) {
            this(terminal, id, project, title, tool, state, phase, status, live, host, attached, session, false, 0);
        }
        /** done: the terminal finished a piece of work before it went idle; at: when its phase last changed. */
        Entry(boolean terminal, String id, String project, String title, String tool, String state,
              String phase, String status, boolean live, String host, String attached, String session, boolean done, long at) {
            this.terminal = terminal; this.id = id; this.project = project; this.title = title;
            this.tool = tool; this.state = state; this.phase = phase; this.status = status;
            this.live = live; this.host = host; this.attached = attached; this.session = session;
            this.done = done; this.at = at;
        }
        boolean running() {
            return terminal ? "running".equals(state) || "starting".equals(state) : live && ("cli".equals(host) || reached);
        }
        /**
         * A phone terminal is confirm, done, busy, starting, idle or ended; a saved conversation is pc-busy, pc-idle,
         * locked, remote, unknown or history, and one whose tool tells of it is confirm, waiting (it has stopped and
         * waits for a message from the phone), busy or stopped. seen maps a terminal to the phase change the user has already looked at:
         * finished work is "done" until then, as in the pages.
         */
        String kind(Map<String, Long> seen) {
            if (!terminal) {
                if (!live) return "history";
                if (reached) return "confirm".equals(status) ? "confirm" : "waiting".equals(status) ? "waiting" : "busy".equals(status) ? "busy" : "stopped";
                if ("shared".equals(host)) return "locked";
                if ("remote".equals(host)) return "remote";
                if (!"cli".equals(host)) return "unknown";
                return "busy".equals(status) ? "pc-busy" : "pc-idle";
            }
            if ("starting".equals(state)) return "starting";
            if (!running()) return "ended";
            String value = phase.isEmpty() ? ("busy".equals(status) ? "busy" : "idle") : phase;
            if ("confirm".equals(value) || "busy".equals(value) || "done".equals(value) || "starting".equals(value)) return value;
            Long looked = seen == null ? null : seen.get(id);
            return done && (looked == null || looked != at) ? "done" : "idle";
        }
        String label() { return AggregateSessions.label(kind(null)); }
        int rank() { return AggregateSessions.rank(kind(null)); }
        /** What the computer calls its plain terminal ("PowerShell", "bash"); empty from a computer that does not say. */
        String shell = "";
        String toolName() { return "claude".equals(tool) ? "Claude Code" : "codex".equals(tool) ? "Codex" : shell.isEmpty() ? "PowerShell" : shell; }
        String shownTitle() { return title.isEmpty() ? toolName() : title; }
    }
    static String label(String kind) {
        switch (kind) {
            case "confirm": return "等你确认";
            case "done": return "已完成";
            case "waiting": return "等你的消息";
            case "stopped": return "已停下";
            case "busy": return "正在执行";
            case "starting": return "正在启动";
            case "idle": return "等待输入";
            case "ended": return "已结束";
            case "pc-busy": return "电脑上正在执行";
            case "pc-idle": return "电脑上打开着";
            case "locked": return "被电脑上的应用占用";
            case "remote": return "被另一个远程终端占用";
            case "unknown": return "被电脑上的程序占用";
            default: return "历史对话";
        }
    }
    /** What needs the user comes first, in the order the pages use. */
    static int rank(String kind) {
        int at = Arrays.asList("confirm", "done", "waiting", "busy", "starting", "idle", "stopped", "pc-busy", "pc-idle").indexOf(kind);
        return at < 0 ? 9 : at;
    }
    /** How long ago, in the words the pages use; nothing for a time that is not known. */
    static String ago(long then, long now) {
        if (then <= 0) return "";
        long seconds = Math.max(0, (now - then) / 1000);
        return seconds < 90 ? "刚刚" : seconds < 3600 ? Math.round(seconds / 60.0) + " 分钟前" : seconds < 86400 ? Math.round(seconds / 3600.0) + " 小时前" : Math.round(seconds / 86400.0) + " 天前";
    }
    static final class Project {
        final String name;
        final List<Entry> active = new ArrayList<>(), ended = new ArrayList<>(), history = new ArrayList<>();
        Project(String name) { this.name = name; }
    }
    static List<Project> projects(List<String> folders, List<Entry> entries) {
        LinkedHashMap<String, Project> groups = new LinkedHashMap<>();
        for (String name : folders) if (!name.isEmpty() && !groups.containsKey(name)) groups.put(name, new Project(name));
        Set<String> phoneSessions = new HashSet<>();
        for (Entry entry : entries) if (entry.terminal && entry.running() && !entry.session.isEmpty()) phoneSessions.add(entry.tool + ":" + entry.session);
        for (Entry entry : entries) {
            if (entry.project.isEmpty()) continue;
            if (!groups.containsKey(entry.project)) groups.put(entry.project, new Project(entry.project));
            if (!entry.terminal && (!entry.attached.isEmpty() || phoneSessions.contains(entry.tool + ":" + entry.id))) continue;
            Project group = groups.get(entry.project);
            if (entry.terminal && !entry.running()) group.ended.add(entry);
            else if (entry.running()) group.active.add(entry); else group.history.add(entry);
        }
        for (Project group : groups.values()) {
            Collections.sort(group.active, Comparator.comparingInt(Entry::rank));
            Collections.sort(group.ended, (a, b) -> Long.compare(recent(b), recent(a)));
            Collections.sort(group.history, (a, b) -> Long.compare(b.used, a.used));       // stable: without times the order stays as sent
        }
        return new ArrayList<>(groups.values());
    }
    static long recent(Entry entry) { return entry.used > 0 ? entry.used : entry.at; }
    /** How many running tasks are confirm, done, busy (starting included) and open in all. */
    static int[] counts(List<Project> projects, Map<String, Long> seen) {
        int[] counts = new int[4];
        for (Project project : projects) for (Entry entry : project.active) {
            String kind = entry.kind(seen);
            if ("confirm".equals(kind)) counts[0]++;
            else if ("done".equals(kind) || "waiting".equals(kind)) counts[1]++;
            else if ("busy".equals(kind) || "starting".equals(kind) || "pc-busy".equals(kind)) counts[2]++;
            counts[3]++;
        }
        return counts;
    }
    static String terminalPath(String id) {
        if (id == null || !id.matches("[a-f0-9]{32}")) throw new IllegalArgumentException("终端编号无效");
        return "/terminal/?id=" + id;
    }
    static String signature(List<Project> projects) { return signature(projects, null); }
    static String signature(List<Project> projects, Map<String, Long> seen) {
        StringBuilder key = new StringBuilder();
        for (Project project : projects) {
            add(key, project.name);
            for (List<Entry> entries : Arrays.asList(project.active, project.ended, project.history)) {
                key.append(entries.size()).append(':');
                for (Entry entry : entries) {
                    add(key, entry.id); add(key, entry.shownTitle()); add(key, entry.tool);
                    add(key, label(entry.kind(seen))); add(key, entry.host); add(key, entry.origin);
                    if (entries == project.history || entries == project.ended) add(key, ago(recent(entry), System.currentTimeMillis()));
                }
            }
        }
        return key.toString();
    }
    private static void add(StringBuilder key, String value) { key.append(value.length()).append(':').append(value); }
}
