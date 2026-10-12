package io.github.kangwang42.remotecli;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;

public final class AggregateSessionsCheck {
    private static final String TERMINAL = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static AggregateSessions.Entry terminal(String project, String phase, String state, String session) {
        return new AggregateSessions.Entry(true, TERMINAL, project, "手机终端", "codex", state, phase, "", false, "", "", session);
    }
    private static AggregateSessions.Entry saved(String project, String id, String host, boolean live, String attached) {
        return new AggregateSessions.Entry(false, id, project, "对话", "codex", "", "", "busy", live, host, attached, "");
    }
    private static void require(boolean ok, String reason) { if (!ok) throw new AssertionError(reason); }
    public static void main(String[] args) {
        List<AggregateSessions.Project> groups = AggregateSessions.projects(Arrays.asList("同名项目", "空项目", "同名项目"), Arrays.asList(
            saved("同名项目", "cli", "cli", true, ""),
            terminal("同名项目", "confirm", "running", "phone-session"),
            saved("同名项目", "phone-session", "remote", true, TERMINAL),
            saved("同名项目", "shared", "shared", true, ""),
            saved("同名项目", "unknown", "", true, ""),
            saved("同名项目", "past", "", false, ""),
            terminal("终端独有项目", "busy", "starting", ""),
            terminal("同名项目", "idle", "closed", "")
        ));
        require(groups.size() == 3, "Empty and terminal-only projects must survive without duplicate project rows");
        AggregateSessions.Project group = groups.get(0);
        require(group.active.size() == 2, "Shared/unknown locks and duplicate attached histories entered active count");
        require(group.history.size() == 3, "Locked histories were lost");
        require(group.ended.size() == 1 && "ended".equals(group.ended.get(0).kind(null)), "Closed terminals must remain in their own history section");
        require("等你确认".equals(group.active.get(0).label()), "Confirmation must sort before computer work");
        require("被电脑上的应用占用".equals(group.history.get(0).label()), "Shared busy lock must not display running");
        require("被电脑上的程序占用".equals(group.history.get(1).label()), "Legacy host must not imply computer CLI");
        require("历史对话".equals(group.history.get(2).label()), "Ended history label");
        require(groups.get(1).active.isEmpty(), "Empty configured project omitted or polluted");
        require("正在启动".equals(groups.get(2).active.get(0).label()), "Starting terminal must be listed");

        List<AggregateSessions.Project> other = AggregateSessions.projects(Collections.singletonList("同名项目"),
            Collections.singletonList(saved("同名项目", "other", "cli", true, "")));
        require(other.get(0).active.size() == 1 && group.active.size() == 2, "Separate computers with the same project name must remain separate");
        require(!AggregateSessions.signature(groups).equals(AggregateSessions.signature(other)), "Inventory changes must update render signature");
        List<AggregateSessions.Project> copied = AggregateSessions.projects(Arrays.asList("同名项目", "空项目", "同名项目"), Arrays.asList(
            saved("同名项目", "cli", "cli", true, ""), terminal("同名项目", "confirm", "running", "phone-session"),
            saved("同名项目", "phone-session", "remote", true, ""), saved("同名项目", "shared", "shared", true, ""),
            saved("同名项目", "unknown", "", true, ""), saved("同名项目", "past", "", false, ""),
            terminal("终端独有项目", "busy", "starting", ""), terminal("同名项目", "idle", "closed", "")));
        require(AggregateSessions.signature(groups).equals(AggregateSessions.signature(copied)), "Unchanged display must retain its views during refresh");
        require(AggregateSessions.terminalPath(TERMINAL).equals("/terminal/?id=" + TERMINAL), "Terminal link must identify the exact terminal");
        boolean refused = false;
        try { AggregateSessions.terminalPath("../?p=secret"); } catch (IllegalArgumentException expected) { refused = true; }
        require(refused, "Terminal route must reject malformed identifiers");
        AggregateSessions.Entry finished = new AggregateSessions.Entry(true, TERMINAL, "项目", "", "claude", "running", "idle", "", false, "", "", "", true, 42L);
        java.util.Map<String, Long> looked = new java.util.HashMap<>();
        require("done".equals(finished.kind(looked)), "Finished work must stay marked until it has been looked at");
        looked.put(TERMINAL, 41L);
        require("done".equals(finished.kind(looked)), "A newer phase change must be marked again");
        looked.put(TERMINAL, 42L);
        require("idle".equals(finished.kind(looked)), "Work that was looked at must fall back to waiting");
        require(AggregateSessions.rank("confirm") < AggregateSessions.rank("done") && AggregateSessions.rank("done") < AggregateSessions.rank("busy")
            && AggregateSessions.rank("idle") < AggregateSessions.rank("pc-busy"), "Attention order must match the pages");
        int[] counts = AggregateSessions.counts(groups, null);
        require(counts[0] == 1 && counts[1] == 0 && counts[2] == 2 && counts[3] == 3, "Counts must cover confirm, busy with starting, and all open tasks");
        List<AggregateSessions.Project> one = AggregateSessions.projects(Collections.singletonList("项目"), Collections.singletonList(finished));
        looked.clear();
        String unseen = AggregateSessions.signature(one, looked);
        looked.put(TERMINAL, 42L);
        require(!unseen.equals(AggregateSessions.signature(one, looked)), "Looking at finished work must redraw its row");
        // Saved conversations: the one used last comes first, and each says how long ago that was.
        AggregateSessions.Entry older = saved("项目", "older", "", false, ""), newer = saved("项目", "newer", "", false, "");
        older.used = 1_000_000L; newer.used = 9_000_000L;
        List<AggregateSessions.Project> used = AggregateSessions.projects(Collections.singletonList("项目"), Arrays.asList(older, newer));
        require("newer".equals(used.get(0).history.get(0).id), "The conversation used last must come first");
        AggregateSessions.Entry endedOlder = terminal("项目", "ended", "closed", "old-session"), endedNewer = terminal("项目", "failed", "closed", "new-session");
        endedOlder.used = 1_000_000L; endedNewer.used = 9_000_000L;
        List<AggregateSessions.Project> ended = AggregateSessions.projects(Collections.singletonList("项目"), Arrays.asList(endedOlder, endedNewer));
        require(ended.get(0).active.isEmpty() && ended.get(0).history.isEmpty() && ended.get(0).ended.size() == 2, "Stopped and failed terminals must be separate from active tasks and saved conversations");
        require(ended.get(0).ended.get(0) == endedNewer, "Recently ended terminals must sort by latest use, rather than creation order");
        require(AggregateSessions.counts(ended, null)[3] == 0, "History terminals must never add to the running count");
        require(!AggregateSessions.signature(ended).equals(AggregateSessions.signature(Collections.<AggregateSessions.Project>emptyList())), "History changes must refresh the screen");
        AggregateSessions.Entry reopened = terminal("项目", "busy", "running", "old-session");
        List<AggregateSessions.Project> withReopened = AggregateSessions.projects(Collections.singletonList("项目"), Arrays.asList(endedOlder, reopened, saved("项目", "old-session", "remote", true, TERMINAL)));
        require(withReopened.get(0).active.size() == 1 && withReopened.get(0).ended.size() == 1 && withReopened.get(0).history.isEmpty(), "Reopening a conversation must preserve the previous terminal screen and deduplicate its live session");
        long now = 1_700_000_000_000L;
        require(AggregateSessions.ago(0, now).isEmpty() && "刚刚".equals(AggregateSessions.ago(now - 30_000, now)) && "5 分钟前".equals(AggregateSessions.ago(now - 300_000, now))
            && "3 小时前".equals(AggregateSessions.ago(now - 3 * 3600_000L, now)) && "2 天前".equals(AggregateSessions.ago(now - 2 * 86400_000L, now)), "Last use must read as on the pages");
        // A conversation that the desktop app has open, of which its tool tells: it is at work like a terminal, stands
        // by how it stands, and counts with the terminals. Without that it is one a program holds, as before.
        AggregateSessions.Entry held = new AggregateSessions.Entry(false, "desk", "桌面", "桌面版对话", "codex", "", "", "busy", true, "shared", "", "");
        require(!held.running() && "locked".equals(held.kind(null)), "a conversation a program holds, of which nothing is told, is not one at work");
        String[][] stands = { { "confirm", "confirm", "等你确认" }, { "waiting", "waiting", "等你的消息" }, { "busy", "busy", "正在执行" }, { "idle", "stopped", "已停下" }, { "", "stopped", "已停下" } };
        for (String[] stand : stands) {
            AggregateSessions.Entry told = new AggregateSessions.Entry(false, "desk", "桌面", "桌面版对话", "codex", "", "", stand[0], true, "shared", "", "");
            told.reached = true;
            require(told.running() && stand[1].equals(told.kind(null)) && stand[2].equals(told.label()), "a conversation its tool tells of stands as " + stand[1] + ": " + told.kind(null));
        }
        AggregateSessions.Entry asks = new AggregateSessions.Entry(false, "a", "桌面", "问", "claude", "", "", "confirm", true, "unknown", "", "");
        AggregateSessions.Entry waits = new AggregateSessions.Entry(false, "b", "桌面", "等", "claude", "", "", "waiting", true, "unknown", "", "");
        AggregateSessions.Entry works = new AggregateSessions.Entry(false, "c", "桌面", "做", "codex", "", "", "busy", true, "shared", "", "");
        asks.reached = waits.reached = works.reached = true;
        List<AggregateSessions.Project> desk = AggregateSessions.projects(Collections.singletonList("桌面"), Arrays.asList(works, held, waits, asks));
        require(desk.get(0).active.size() == 3 && desk.get(0).history.size() == 1 && "a".equals(desk.get(0).active.get(0).id) && "b".equals(desk.get(0).active.get(1).id), "what asks comes first, then what waits for a message, then what works");
        require(Arrays.equals(AggregateSessions.counts(desk, null), new int[]{1, 1, 1, 3}), "they are counted with the terminals: " + Arrays.toString(AggregateSessions.counts(desk, null)));
        require(AggregateSessions.rank("waiting") < AggregateSessions.rank("busy") && AggregateSessions.rank("stopped") > AggregateSessions.rank("idle"), "one that waits for a message stands before those at work, one that has stopped after those that wait for input");
        // A conversation that a program holds without anybody being at it (a desktop app keeps what it has loaded): no task,
        // whatever its tool says of it; it is listed with the saved ones of its project, and says how it stands.
        AggregateSessions.Entry kept = new AggregateSessions.Entry(false, "kept", "桌面", "桌面版加载过的", "claude", "", "", "idle", true, "cli", "", "");
        kept.now = false; kept.reached = true;
        require(!kept.running() && "held".equals(kept.kind(null)) && "电脑上闲置".equals(kept.label()), "held and not in use: " + kept.kind(null));
        List<AggregateSessions.Project> withKept = AggregateSessions.projects(Collections.singletonList("桌面"), Arrays.asList(works, kept));
        require(withKept.get(0).active.size() == 1 && withKept.get(0).history.size() == 1 && AggregateSessions.counts(withKept, null)[3] == 1, "it is no task and is not counted");

        // A conversation that a program has open in a folder that is no project: it has no project, says its folder,
        // stands in a group without a name, and counts with the tasks.
        AggregateSessions.Entry outside = new AggregateSessions.Entry(false, "out", "", "整理读书笔记", "codex", "", "", "busy", true, "cli", "", "");
        outside.folder = "D:\\notes\\reading-notes\\"; outside.origin = "Codex 桌面版"; outside.reached = true; outside.used = 500;
        AggregateSessions.Entry posix = new AggregateSessions.Entry(false, "px", "", "别的", "claude", "", "", "", true, "cli", "", "");
        posix.folder = "/srv/读书 笔记";
        require(outside.apart() && "reading-notes".equals(outside.place()) && "读书 笔记".equals(posix.place()) && "app".equals(outside.source()) && "term".equals(posix.source()), "where it is and where it runs: " + outside.place() + " " + posix.place());
        require("Codex 桌面版 · reading-notes（未加入项目）".equals(AggregateSessions.where(outside)), AggregateSessions.where(outside));
        AggregateSessions.Entry lost = new AggregateSessions.Entry(false, "lost", "", "没有文件夹的", "codex", "", "", "", true, "cli", "", "");
        List<AggregateSessions.Project> withOutside = AggregateSessions.projects(Collections.singletonList("桌面"), Arrays.asList(works, outside, posix, lost));
        require(withOutside.size() == 2 && withOutside.get(1).name.isEmpty() && withOutside.get(1).active.size() == 2, "the ones outside every project stand in a group without a name, the last");
        require(AggregateSessions.counts(withOutside, null)[3] == 3 && !AggregateSessions.signature(withOutside).equals(AggregateSessions.signature(desk)), "and count with the tasks");

        // A terminal that took such a conversation on runs in its folder and has no project either: it stands in the same group.
        AggregateSessions.Entry took = new AggregateSessions.Entry(true, "d" + TERMINAL.substring(1), "", "整理读书笔记", "codex", "running", "busy", "", false, "", "", "out-2");
        took.folder = "D:\\notes\\reading-notes";
        AggregateSessions.Entry left = new AggregateSessions.Entry(true, "e" + TERMINAL.substring(1), "", "昨天接过来的", "claude", "closed", "", "", false, "", "", "");
        left.folder = "D:\\notes\\reading-notes";
        AggregateSessions.Entry its = new AggregateSessions.Entry(false, "out-2", "", "整理读书笔记", "codex", "", "", "", false, "", "", "");
        its.folder = took.folder;
        require(took.apart() && "reading-notes".equals(took.place()) && "term".equals(took.source()) && "手机终端 · reading-notes（未加入项目）".equals(AggregateSessions.where(took)), AggregateSessions.where(took));
        List<AggregateSessions.Project> taken = AggregateSessions.projects(Collections.singletonList("桌面"), Arrays.asList(took, left, its, outside));
        AggregateSessions.Project nameless = taken.get(taken.size() - 1);
        require(nameless.name.isEmpty() && nameless.active.size() == 2 && nameless.ended.size() == 1 && nameless.history.isEmpty(), "a terminal outside every project is listed, its own conversation is not listed beside it: " + nameless.active.size() + " " + nameless.ended.size() + " " + nameless.history.size());

        // The workbench's lists: what waits for an answer, what is pinned, what is at work, what was used lately.
        String pc = "https://pc.example", other2 = "https://other.example";
        AggregateSessions.Entry shell = new AggregateSessions.Entry(true, TERMINAL, "thesis", "看日志", "shell", "running", "idle", "", false, "", "", "");
        shell.used = 900;
        AggregateSessions.Entry gone = new AggregateSessions.Entry(true, "b" + TERMINAL.substring(1), "thesis", "昨天的终端", "claude", "closed", "", "", false, "", "", "past-1");
        gone.used = 300;
        AggregateSessions.Entry alone = new AggregateSessions.Entry(true, "c" + TERMINAL.substring(1), "thesis", "只有画面的终端", "shell", "closed", "", "", false, "", "", "");
        alone.used = 250;
        AggregateSessions.Entry past1 = new AggregateSessions.Entry(false, "past-1", "thesis", "摘要翻译", "claude", "", "", "", false, "", "", ""), past2 = new AggregateSessions.Entry(false, "past-2", "web-shop", "给结算页加上优惠券", "claude", "", "", "", false, "", "", ""),
            past3 = new AggregateSessions.Entry(false, "past-3", "web-shop", "旧的讨论", "codex", "", "", "", false, "", "", "");
        past1.used = 400; past2.used = 800; past3.used = 100;
        past2.pinned = true; past3.hidden = true;
        asks.used = 700; waits.used = 600; works.used = 650; works.pinned = true;
        java.util.function.Function<AggregateSessions.Entry, AggregateSessions.Thing> on = entry -> new AggregateSessions.Thing(entry == waits ? other2 : pc, entry == waits ? "台式机" : "笔记本", entry,
            entry.terminal && !entry.running() ? "ended" : entry.kind(null));
        List<AggregateSessions.Thing> things = new java.util.ArrayList<>();
        for (AggregateSessions.Entry entry : Arrays.asList(shell, gone, alone, past1, past2, past3, asks, waits, works, outside, held)) things.add(on.apply(entry));
        java.util.function.Function<List<AggregateSessions.Thing>, String> ids = list -> { StringBuilder all = new StringBuilder(); for (AggregateSessions.Thing thing : list) all.append(thing.entry.terminal ? thing.entry.shownTitle() : thing.entry.id).append(' '); return all.toString().trim(); };
        AggregateSessions.Board board = AggregateSessions.board(things, "", "", "");
        require("a b".equals(ids.apply(board.needs)), "what asks, then what waits for a message: " + ids.apply(board.needs));
        require("c past-2".equals(ids.apply(board.pinned)), "what is pinned, the open one first: " + ids.apply(board.pinned));
        require("out 看日志".equals(ids.apply(board.active)), "what is at work, by how it stands: " + ids.apply(board.active));
        // an ended terminal whose conversation is listed is not listed twice; what is hidden and what a program holds are left out
        require("past-1 只有画面的终端".equals(ids.apply(board.recent)) && !board.searched && board.found.isEmpty(), "what was used lately: " + ids.apply(board.recent));
        require("[claude, codex, shell]".equals(board.tools.toString()) && "[term, app]".equals(board.sources.toString()), board.tools + " " + board.sources);
        // narrowed by tool and by where a thing runs
        AggregateSessions.Board claudeOnly = AggregateSessions.board(things, "", "claude", "");
        require("a b".equals(ids.apply(claudeOnly.needs)) && "past-2".equals(ids.apply(claudeOnly.pinned)) && claudeOnly.active.isEmpty() && "past-1".equals(ids.apply(claudeOnly.recent)), "one tool");
        // in a terminal (one of the phone's, a command line on the computer), or in an app
        AggregateSessions.Board termOnly = AggregateSessions.board(things, "", "", "term"), appOnly = AggregateSessions.board(things, "", "", "app");
        require(ids.apply(termOnly.active).contains("看日志") && !ids.apply(termOnly.active).contains("out") && termOnly.tools.size() == 3, "what runs in a terminal: " + ids.apply(termOnly.active));
        require("out".equals(ids.apply(appOnly.active)) && !ids.apply(appOnly.recent).contains("past"), "what runs in an app: " + ids.apply(appOnly.active) + " | " + ids.apply(appOnly.recent));
        // one that no program has open is told by what started it, where the computer says so
        AggregateSessions.Entry drawn = new AggregateSessions.Entry(false, "drawn", "web-shop", "桌面版里画的", "codex", "", "", "", false, "", "", ""), typed = new AggregateSessions.Entry(false, "typed", "web-shop", "终端里写的", "codex", "", "", "", false, "", "", "");
        drawn.kind = "Codex 桌面版"; typed.kind = "终端";
        require("app".equals(drawn.source()) && "term".equals(typed.source()) && past1.source().isEmpty() && "app".equals(AggregateSessions.sourceOfKind("编辑器")), "by what started it");
        things.add(on.apply(drawn));
        require(ids.apply(AggregateSessions.board(things, "", "", "app").recent).contains("drawn") && "drawn".equals(ids.apply(AggregateSessions.board(things, "桌面版里画的 codex 桌面版", "", "").found)), "listed and found as an app's");
        things.remove(things.size() - 1);
        // a project is of the tools and kinds its folder is used by: as the computer says of the folder, and by what is in it
        List<AggregateSessions.Project> told = AggregateSessions.projects(Arrays.asList("web-shop", "thesis", "empty"), Arrays.asList(shell, past1, past2, past3));
        require(told.get(0).passes("claude", "") && told.get(0).passes("codex", "") && !told.get(0).passes("", "app") && !told.get(0).passes("", "term"), "by what is in it: the tools, and nothing of where while nothing says");
        require(told.get(1).passes("shell", "term") && told.get(1).passes("claude", "term") && !told.get(1).passes("codex", ""), "a terminal of the phone's makes it one of the terminal");
        require(told.get(2).passes("", "") && !told.get(2).passes("claude", "") && !told.get(2).passes("", "app"), "an empty one passes only where nothing is asked");
        told.get(0).told(Arrays.asList("codex"), Arrays.asList("Codex 桌面版", "终端"));
        told.get(2).told(Arrays.asList("claude"), Collections.singletonList("Claude 桌面版"));
        require(told.get(0).passes("codex", "app") && told.get(0).passes("claude", "term") && told.get(2).passes("claude", "app") && !told.get(2).passes("claude", "term") && !told.get(2).passes("codex", "app"), "as the computer says of the folder");
        // one search over all of it: every word, whatever its case, in the name, the place, the tool, the computer; the hidden too
        require("out".equals(ids.apply(AggregateSessions.board(things, " 读书 ", "", "").found)) && AggregateSessions.board(things, "读书", "", "").searched, "by a word of the name");
        require("看日志 past-1 只有画面的终端".equals(ids.apply(AggregateSessions.board(things, "THESIS", "", "").found)), "by the project, what is open first: " + ids.apply(AggregateSessions.board(things, "THESIS", "", "").found));
        require("past-3".equals(ids.apply(AggregateSessions.board(things, "旧的 codex", "", "").found)) && "b".equals(ids.apply(AggregateSessions.board(things, "台式机", "", "").found)), "the hidden one is found; and by the computer");
        require("out".equals(ids.apply(AggregateSessions.board(things, "未加入项目", "", "").found)) && AggregateSessions.board(things, "没有这样的", "", "").found.isEmpty(), "by what a card says; and nothing");
        require(AggregateSessions.board(things, "旧的", "claude", "").found.isEmpty(), "a search stays within what the filters leave");
        require("out".equals(outside.conversation()) && "past-1".equals(gone.conversation()) && shell.conversation().isEmpty(), "the conversation a mark is kept of");
        System.out.println("Aggregate checks passed: computer isolation, active/history separation, recent ordering, locks, deduplication, stable refresh and exact terminal route.");
    }
}
