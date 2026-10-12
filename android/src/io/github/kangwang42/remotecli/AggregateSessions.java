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
        boolean reached, canSay, canTakeover;
        boolean canEnd(boolean supported) {
            return terminal ? "running".equals(state) : supported && live && canTakeover && attached.isEmpty();
        }
        /**
         * Whether a conversation that a program on the computer holds is in use now (the computer says: at work, asking,
         * in a window of its own, or used a moment ago). A desktop app keeps every conversation it has loaded held; one
         * that is held and not in use is no task. A computer that does not say takes every held one for one in use.
         */
        boolean now = true;
        String origin = "";     // where a conversation on the computer runs, as the computer says it: "Codex 桌面版", "命令行", "VS Code"
        String kind = "";       // by what it is used, as the computer says it: "终端", "Codex 桌面版", "Claude 桌面版", "编辑器"; "" where it does not say
        long used;              // latest terminal phase change or saved conversation update; 0 when not known
        /** The folder of a conversation that is open where there is no project, or of the terminal that took it on (the project is then ""). */
        String folder = "";
        /** What the owner said of the conversation, kept by the computer: it stands first, or is left out of the lists. */
        boolean pinned, hidden;
        boolean apart() { return project.isEmpty() && !folder.isEmpty(); }
        /** Where it is, in a word: its project, or the name of the folder it is open in. */
        String place() {
            if (!project.isEmpty() || folder.isEmpty()) return project;
            String path = folder.replaceAll("[\\\\/]+$", "");
            int cut = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
            return cut >= 0 && cut < path.length() - 1 ? path.substring(cut + 1) : path;
        }
        /**
         * Where it runs: "term" (a terminal: one of the phone's, or a command line on the computer) or "app" (a desktop
         * app, an editor). One that no program has open is told by what started it; "" where the computer does not say.
         */
        String source() {
            if (terminal) return "term";
            // the computer says by what it is used (a command line inside an editor's terminal is a terminal's)
            if (!live || !kind.isEmpty()) return sourceOfKind(kind);
            return origin.contains("桌面版") || origin.contains("后台服务") || "shared".equals(host) || (!origin.isEmpty() && !"命令行".equals(origin) && !"电脑独立终端".equals(origin)) ? "app" : "term";
        }
        /** The conversation a mark is kept of: a terminal's own, or the saved one itself; "" for a terminal without one. */
        String conversation() { return terminal ? session : id; }
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
            return terminal ? "running".equals(state) || "starting".equals(state) : live && now && ("cli".equals(host) || reached);
        }
        /**
         * A phone terminal is confirm, done, busy, starting, idle or ended; a saved conversation is pc-busy, pc-idle,
         * held (a program has it and nobody is at it), locked, remote, unknown or history, and one whose tool tells of it is confirm, waiting (it has stopped and
         * waits for a message from the phone), busy or stopped. seen maps a terminal to the phase change the user has already looked at:
         * finished work is "done" until then, as in the pages.
         */
        String kind(Map<String, Long> seen) {
            if (!terminal) {
                if (!live) return "history";
                if (!now) return "held";
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
            case "held": return "电脑上闲置";
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
    static String sourceLabel(String source) { return "term".equals(source) ? "终端" : "app".equals(source) ? "应用" : ""; }
    /** Every word the computer has for what uses a folder or a conversation is an app's, but the terminal's. */
    static String sourceOfKind(String kind) { return kind == null || kind.isEmpty() ? "" : "终端".equals(kind) ? "term" : "app"; }
    /** By what a thing runs and where it is, in the words of a card: "手机终端 · thesis", "Codex 桌面版 · notes（未加入项目）". */
    static String where(Entry entry) {
        // a terminal of the phone's; the program that has a conversation open (and whether it is an editor's terminal);
        // for one that is only saved, by what it was started
        String runs = entry.terminal ? (entry.running() ? "手机终端" : "")
            : !entry.live ? entry.kind
            : "电脑独立终端".equals(entry.origin) ? ""
            : entry.origin + ("终端".equals(entry.kind) && !entry.origin.isEmpty() && !"命令行".equals(entry.origin) && !entry.origin.contains("桌面版") ? " 的终端" : "");
        String place = entry.place() + (entry.apart() ? "（未加入项目）" : "");
        return runs.isEmpty() ? place : place.isEmpty() ? runs : runs + " · " + place;
    }

    /** An entry on one of the computers, as the workbench lists it, and how it stands (kind). */
    static final class Thing {
        final String url, computer, kind;
        final Entry entry;
        Thing(String url, String computer, Entry entry, String kind) { this.url = url; this.computer = computer; this.entry = entry; this.kind = kind; }
        boolean running() { return entry.running(); }
        /** What a search looks through. */
        String words() {
            return (entry.shownTitle() + '\n' + entry.place() + '\n' + entry.toolName() + '\n' + sourceLabel(entry.source()) + '\n' + (entry.terminal && entry.running() ? "手机终端\n" : "") + entry.origin + '\n' + entry.kind + '\n' + computer + '\n' + label(kind)
                + (entry.apart() ? "\n未加入项目" : "") + (entry.pinned ? "\n置顶" : "") + (entry.hidden ? "\n已隐藏" : "")).toLowerCase(java.util.Locale.ROOT);
        }
    }
    static boolean needs(String kind) { return "confirm".equals(kind) || "done".equals(kind) || "waiting".equals(kind); }
    /**
     * The workbench's lists, by what needs the owner: what waits for an answer, what is pinned, what is at work, what
     * was used lately. With words to search for there is one list instead (found): everything that has all of them,
     * what is hidden too. tool and source narrow every list ("" for any).
     */
    static final class Board {
        final List<Thing> needs = new ArrayList<>(), pinned = new ArrayList<>(), active = new ArrayList<>(), recent = new ArrayList<>(), found = new ArrayList<>();
        boolean searched;
        final Set<String> tools = new java.util.LinkedHashSet<>(), sources = new java.util.LinkedHashSet<>();     // what there is to tell apart by, before narrowing
    }
    static Board board(List<Thing> things, String query, String tool, String source) {
        Board board = new Board();
        // an ended terminal whose conversation is listed is found there, to be gone on with: it is not listed twice
        Set<String> saved = new HashSet<>();
        for (Thing thing : things) if (!thing.entry.terminal) saved.add(thing.url + '\n' + thing.entry.tool + '\n' + thing.entry.id);
        List<String> words = new ArrayList<>();
        for (String word : query.trim().toLowerCase(java.util.Locale.ROOT).split("\\s+")) if (!word.isEmpty()) words.add(word);
        board.searched = !words.isEmpty();
        for (String known : new String[]{"claude", "codex", "shell"}) for (Thing thing : things) if (known.equals(thing.entry.tool)) { board.tools.add(known); break; }
        for (String known : new String[]{"term", "app"}) for (Thing thing : things) if (known.equals(thing.entry.source())) { board.sources.add(known); break; }
        for (Thing thing : things) {
            Entry entry = thing.entry;
            if (!tool.isEmpty() && !tool.equals(entry.tool) || !source.isEmpty() && !source.equals(entry.source())) continue;
            if (entry.terminal && !entry.running() && !entry.session.isEmpty() && saved.contains(thing.url + '\n' + entry.tool + '\n' + entry.session)) continue;
            boolean past = "ended".equals(thing.kind) || "history".equals(thing.kind);
            if (board.searched) {
                String all = thing.words();
                boolean has = true;
                for (String word : words) has &= all.contains(word);
                if (has) board.found.add(thing);
            }
            else if (thing.running() && needs(thing.kind)) board.needs.add(thing);
            else if (entry.pinned && (thing.running() || past)) board.pinned.add(thing);
            else if (thing.running()) board.active.add(thing);
            else if (past && !entry.hidden) board.recent.add(thing);
        }
        Comparator<Thing> standing = (a, b) -> rank(a.kind) != rank(b.kind) ? rank(a.kind) - rank(b.kind) : Long.compare(recent(b.entry), recent(a.entry));
        Comparator<Thing> lately = (a, b) -> Long.compare(recent(b.entry), recent(a.entry));
        // what is open comes before what is only saved
        Comparator<Thing> open = (a, b) -> a.running() != b.running() ? (a.running() ? -1 : 1) : a.running() ? standing.compare(a, b) : lately.compare(a, b);
        Collections.sort(board.needs, standing); Collections.sort(board.active, standing);
        Collections.sort(board.pinned, open); Collections.sort(board.found, open); Collections.sort(board.recent, lately);
        return board;
    }

    static final class Project {
        final String name;
        final List<Entry> active = new ArrayList<>(), ended = new ArrayList<>(), history = new ArrayList<>();
        /** With which tools and where its folder is used: as the computer says of the folder, and by what is in it now. */
        final Set<String> tools = new HashSet<>(), sources = new HashSet<>();
        boolean fixed;          // it stands in the computer's own settings: changed from the phone only where the computer says so
        Project(String name) { this.name = name; }
        /** By what it is used, in the words of the filters: "终端", "应用", "终端 · 应用"; "" where nothing says. */
        String uses() { return (sources.contains("term") ? "终端" : "") + (sources.contains("term") && sources.contains("app") ? " · " : "") + (sources.contains("app") ? "应用" : ""); }
        /** What the computer says of the folder: the tools, and by what it is used ("终端", "Codex 桌面版", …). */
        void told(List<String> tools, List<String> kinds) {
            this.tools.addAll(tools);
            for (String kind : kinds) if (!sourceOfKind(kind).isEmpty()) sources.add(sourceOfKind(kind));
        }
        /** Whether it is one of that tool and that source ("" for any). */
        boolean passes(String tool, String source) { return (tool.isEmpty() || tools.contains(tool)) && (source.isEmpty() || sources.contains(source)); }
    }
    static List<Project> projects(List<String> folders, List<Entry> entries) {
        LinkedHashMap<String, Project> groups = new LinkedHashMap<>();
        for (String name : folders) if (!name.isEmpty() && !groups.containsKey(name)) groups.put(name, new Project(name));
        Set<String> phoneSessions = new HashSet<>();
        for (Entry entry : entries) if (entry.terminal && entry.running() && !entry.session.isEmpty()) phoneSessions.add(entry.tool + ":" + entry.session);
        for (Entry entry : entries) {
            // what is open where there is no project stands in a group without a name, the last one
            if (entry.project.isEmpty()) continue;
            if (!groups.containsKey(entry.project)) groups.put(entry.project, new Project(entry.project));
            groups.get(entry.project).tools.add(entry.tool);
            if (!entry.source().isEmpty()) groups.get(entry.project).sources.add(entry.source());
            if (!entry.terminal && (!entry.attached.isEmpty() || phoneSessions.contains(entry.tool + ":" + entry.id))) continue;
            Project group = groups.get(entry.project);
            if (entry.terminal && !entry.running()) group.ended.add(entry);
            else if (entry.running()) group.active.add(entry); else group.history.add(entry);
        }
        Project apart = new Project("");
        for (Entry entry : entries) {
            if (!entry.apart() || !entry.terminal && (!entry.live || !entry.attached.isEmpty() || phoneSessions.contains(entry.tool + ":" + entry.id))) continue;
            (entry.running() ? apart.active : entry.terminal ? apart.ended : apart.history).add(entry);
        }
        if (!apart.active.isEmpty() || !apart.ended.isEmpty() || !apart.history.isEmpty()) groups.put("", apart);
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
                    add(key, entry.folder); key.append(entry.pinned ? 'p' : '-').append(entry.hidden ? 'h' : '-').append(entry.canTakeover ? 'e' : '-');
                    if (entries == project.history || entries == project.ended) add(key, ago(recent(entry), System.currentTimeMillis()));
                }
            }
        }
        return key.toString();
    }
    private static void add(StringBuilder key, String value) { key.append(value.length()).append(':').append(value); }
}
