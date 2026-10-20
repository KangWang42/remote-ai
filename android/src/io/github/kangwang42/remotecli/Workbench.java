package io.github.kangwang42.remotecli;

import android.graphics.drawable.GradientDrawable;
import android.view.Gravity;
import android.view.View;
import android.webkit.CookieManager;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import org.json.JSONArray;
import org.json.JSONObject;

/**
 * Two of the app's four lists, both over every computer. 任务, the app's first screen: everything by what needs the
 * user (what waits for an answer, what is pinned, what is at work, what was used lately), with one search over all
 * of it and a row of filters. 项目: each computer with its projects. Every computer is asked on its own, so one that
 * is switched off delays nothing.
 */
final class Workbench {
    /** What one computer answered last: "on", "off" or "key" (sign in again), a line about it, and its projects. */
    private static final class Snapshot {
        final String state, line;
        final List<AggregateSessions.Project> projects;
        final boolean marks;        // the computer keeps what is said of single conversations: pinned, hidden
        final Set<String> can = new HashSet<>();        // what else the computer says it does: "delete", "projects"
        Snapshot(String state, String line, List<AggregateSessions.Project> projects, boolean marks) { this.state = state; this.line = line; this.projects = projects; this.marks = marks; }
    }
    private boolean shown() { return activity.showing("home") || activity.showing("projects"); }
    /** The computers that answer now; all of them while none has been heard of yet. */
    List<String> reachable() {
        List<String> all = new ArrayList<>(), on = new ArrayList<>();
        JSONArray list = activity.computers();
        for (int i = 0; i < list.length(); i++) {
            JSONObject computer = list.optJSONObject(i);
            if (computer == null) continue;
            String url = computer.optString("url");
            Snapshot snapshot = snapshots.get(url);
            all.add(url);
            if (snapshot != null && "on".equals(snapshot.state)) on.add(url);
        }
        return snapshots.isEmpty() ? all : on;
    }
    /** A new conversation: on the computer that is online, or on the one that is chosen. Its page asks the rest (web/begin.js). */
    private void newConversation() { activity.pick("新建对话", reachable(), activity::startNew); }
    /** How many things wait for an answer, on every computer: said on the tab of 任务. */
    private int waiting() {
        int waiting = 0;
        for (Map.Entry<String, Snapshot> one : snapshots.entrySet()) if ("on".equals(one.getValue().state)) waiting += AggregateSessions.counts(one.getValue().projects, seen(one.getKey()))[0];
        return waiting;
    }
    private static final int SHOWN = 4, TASKS = 12, TALKS = 8, RECENT = 2, RECENT_ALL = 20, FOUND = 30;

    private final MainActivity activity;
    private final Kit kit;
    private final Map<String, Snapshot> snapshots = new HashMap<>();
    private final Map<String, LinearLayout> holders = new HashMap<>();
    private final Map<String, String> marks = new HashMap<>();
    private final Set<String> pending = new HashSet<>(), ending = new HashSet<>(), expanded = new HashSet<>(), collapsed = new HashSet<>(), unfolded = new HashSet<>();      // unfolded: address, line break, project
    private final Runnable tick = this::refresh;
    private JSONObject seen;
    private LinearLayout attention, recent;
    private TextView summary;
    private String attentionMark;
    private boolean recentOpen;         // "最近" shows more than its first few
    // What is searched for, and what the lists are narrowed to: a tool, and where a thing runs ("" for any).
    private String query = "", tool = "", source = "";
    private LinearLayout filters;
    private String filtersMark;
    private final Map<String, TextView> peeks = new HashMap<>();       // address, line break, terminal -> the line it last said

    Workbench(MainActivity activity, Kit kit) { this.activity = activity; this.kit = kit; }

    // ---- which finished work the user has already looked at: {address: {terminal: phase change}}
    private JSONObject seenAll() {
        if (seen == null) try { seen = new JSONObject(activity.prefs.getString("seen", "{}")); } catch (Exception broken) { seen = new JSONObject(); }
        return seen;
    }
    private Map<String, Long> seen(String url) {
        Map<String, Long> map = new HashMap<>();
        JSONObject one = seenAll().optJSONObject(url);
        if (one != null) for (java.util.Iterator<String> keys = one.keys(); keys.hasNext(); ) { String key = keys.next(); map.put(key, one.optLong(key)); }
        return map;
    }
    /** Called when a terminal is opened here, and by the terminal page while it is on screen. */
    void markSeen(String url, String id, long at) {
        if (at <= 0 || id == null || !id.matches("[a-f0-9]{16,32}")) return;
        try {
            JSONObject one = seenAll().optJSONObject(url);
            if (one == null) seenAll().put(url, one = new JSONObject());
            if (one.optLong(id) == at) return;
            one.put(id, at);
            activity.prefs.edit().putString("seen", seenAll().toString()).apply();
        } catch (Exception ignored) { }
    }
    /** Forgets terminals that no longer exist, and computers that were removed. */
    private void prune(String url, Set<String> alive) {
        JSONObject one = seenAll().optJSONObject(url);
        if (one == null) return;
        List<String> gone = new ArrayList<>();
        for (java.util.Iterator<String> keys = one.keys(); keys.hasNext(); ) { String key = keys.next(); if (!alive.contains(key)) gone.add(key); }
        if (gone.isEmpty()) return;
        for (String key : gone) one.remove(key);
        activity.prefs.edit().putString("seen", seenAll().toString()).apply();
    }
    void forget(String url) {
        snapshots.remove(url); expanded.remove(url); collapsed.remove(url);
        if (seenAll().remove(url) != null) activity.prefs.edit().putString("seen", seenAll().toString()).apply();
    }

    // ---- the screens: 任务, or 项目
    View build(String tab, String problem) {
        boolean tasks = "home".equals(tab);
        JSONArray list = activity.computers();
        LinearLayout page = kit.page(22);
        summary = kit.standing(list.length() == 0 ? "还没有添加电脑" : "正在连接…");
        page.addView(kit.head(tasks ? "任务" : "项目", summary, list.length() == 0 ? null
            : tasks ? kit.iconButton(R.drawable.ic_plus, "新建对话", kit.ACCENT, this::newConversation) : kit.iconButton(R.drawable.ic_plus, "添加电脑", kit.ACCENT, this::addComputer)));
        if (!problem.isEmpty()) {
            LinearLayout banner = kit.column();
            banner.setPadding(kit.dp(16), kit.dp(14), kit.dp(16), kit.dp(14));
            banner.setBackground(kit.shape(Kit.mix(kit.BAD, kit.PANEL, .09f), Kit.mix(kit.BAD, kit.LINE, .45f), 16));
            banner.addView(kit.text(problem, 14, kit.INK));
            page.addView(banner, kit.below(16));
        }
        holders.clear(); marks.clear(); attentionMark = null; filtersMark = null; filters = null; attention = null; recent = null;
        if (list.length() == 0) {
            page.addView(welcome(), kit.below(22));
            page.addView(kit.button("扫码添加电脑", 0, activity::scan), kit.below(22));
            page.addView(kit.button("手动输入地址", 1, () -> activity.add("")), kit.below(10));
        } else if (tasks) {
            // one search over every computer's terminals and conversations, and what to tell them apart by
            page.setFocusable(true); page.setFocusableInTouchMode(true);      // the box is written in when it is touched, not as the screen opens
            android.widget.EditText find = kit.field("搜索对话、终端和项目", android.text.InputType.TYPE_CLASS_TEXT);
            find.setMinHeight(kit.dp(46)); find.setTextSize(15); find.setText(query);
            find.setImeOptions(android.view.inputmethod.EditorInfo.IME_ACTION_SEARCH);
            find.addTextChangedListener(new android.text.TextWatcher() {
                @Override public void beforeTextChanged(CharSequence text, int start, int count, int after) { }
                @Override public void onTextChanged(CharSequence text, int start, int before, int count) { }
                @Override public void afterTextChanged(android.text.Editable text) { query = text.toString(); draw(); }
            });
            page.addView(find, kit.below(16));
            filters = kit.row();
            android.widget.HorizontalScrollView across = new android.widget.HorizontalScrollView(activity);
            across.setHorizontalScrollBarEnabled(false); across.addView(filters);
            page.addView(across, kit.below(10));
            attention = kit.column();
            page.addView(attention);
            recent = kit.column();
            page.addView(recent);
            if (!problem.isEmpty()) page.addView(kit.button("重新扫码连接电脑", 0, activity::scan), kit.below(16));
        } else {
            for (int i = 0; i < list.length(); i++) {
                JSONObject computer = list.optJSONObject(i);
                if (computer == null) continue;
                LinearLayout holder = kit.column();
                holders.put(computer.optString("url"), holder);
                page.addView(holder, kit.below(i == 0 ? 16 : 10));
                drawComputer(computer);
            }
            TextView more = kit.bold("添加电脑", 15, kit.MUTED);
            more.setGravity(Gravity.CENTER); more.setMinHeight(kit.dp(54));
            GradientDrawable dashed = kit.shape(0, 0, 16);
            dashed.setStroke(kit.dp(1), kit.LINE, kit.dp(5), kit.dp(4));
            more.setBackground(dashed);
            kit.press(more, this::addComputer);
            page.addView(more, kit.below(10));
            page.addView(kit.text("凭地址和密码即可操作这台电脑上的终端，请妥善保管。", 12.5f, kit.MUTED), kit.below(22));
        }
        activity.message = kit.text("", 13, kit.MUTED);
        page.addView(activity.message);
        draw();
        return page;
    }
    private View welcome() {
        LinearLayout card = kit.column();
        card.setPadding(kit.dp(18), kit.dp(18), kit.dp(18), kit.dp(20)); card.setBackground(kit.shape(kit.PANEL, kit.LINE, 20));
        card.addView(kit.bold("连接第一台电脑", 17, kit.INK));
        String[] steps = { "在电脑上安装并打开 Remote AI", "点下面的“扫码添加电脑”，对准电脑窗口里的二维码", "连上后选一个项目，新建对话" };
        for (int i = 0; i < steps.length; i++) {
            LinearLayout row = new LinearLayout(activity);
            TextView number = kit.bold(String.valueOf(i + 1), 12.5f, kit.ACCENT);
            number.setGravity(Gravity.CENTER); number.setBackground(kit.shape(Kit.tint(kit.ACCENT, 40), 0, 12));
            row.addView(number, new LinearLayout.LayoutParams(kit.dp(24), kit.dp(24)));
            TextView step = kit.text(steps[i], 14.5f, kit.INK);
            step.setPadding(kit.dp(12), kit.dp(1), 0, 0);
            row.addView(step, new LinearLayout.LayoutParams(0, -2, 1));
            card.addView(row, kit.below(14));
        }
        return card;
    }
    private View heading(String title, String note) {
        LinearLayout row = kit.row();
        row.setPadding(kit.dp(2), 0, kit.dp(2), 0);
        row.addView(kit.bold(title, 14.5f, kit.INK), new LinearLayout.LayoutParams(0, -2, 1));
        row.addView(kit.text(note, 12.5f, kit.MUTED));
        return row;
    }
    /** The heading of a section that folds away: the whole row is pressed, and the line beside the title stays. */
    private View heading(String title, String note, boolean folded, Runnable toggle) {
        LinearLayout row = (LinearLayout) heading(title, note);
        row.setMinimumHeight(kit.dp(40));
        View chevron = kit.icon(R.drawable.ic_chevron, Kit.tint(kit.MUTED, 150), 16);
        ((LinearLayout.LayoutParams) chevron.getLayoutParams()).leftMargin = kit.dp(6);
        chevron.setRotation(folded ? 0 : 90);
        row.addView(chevron);
        row.setContentDescription((folded ? "展开" : "收起") + title + "，" + note);
        kit.press(row, toggle);
        return row;
    }
    // A folded section stays folded the next time the app is opened.
    // The overview is a triage surface. Keep the long-running and recently-used sections closed until the
    // user asks for them; otherwise a busy computer pushes the projects and the rest of the workbench below the fold.
    private boolean folded(String section) { return activity.prefs.getBoolean("fold-" + section, true); }
    private void fold(String section) { activity.prefs.edit().putBoolean("fold-" + section, !folded(section)).apply(); draw(); }
    private void addComputer() {
        kit.sheet("添加电脑", "", new String[]{"扫码添加", "手动输入地址"}, new String[]{"对准电脑端窗口里的二维码", "填写电脑端窗口里显示的地址和密码"}, -1,
            which -> { if (which == 0) activity.scan(); else activity.add(""); });
    }

    // ---- asking the computers
    /** Asks every computer what is running; repeats while this screen is shown. */
    void refresh() {
        activity.ticker.removeCallbacks(tick);
        if (!activity.onTabs()) return;
        JSONArray list = activity.computers();
        for (int i = 0; i < list.length(); i++) {
            final JSONObject computer = list.optJSONObject(i);
            if (computer == null) continue;
            final String url = computer.optString("url");
            if (!pending.add(url)) continue;
            final String cookie = CookieManager.getInstance().getCookie(url);
            activity.net.execute(() -> {
                String state = "off", line = "无法连接：电脑未开机，或地址已变更";
                JSONObject data = null;
                HttpURLConnection connection = null;
                // An answer that was read to its end leaves the connection open for the next question: over a tunnel
                // making a new one costs more than the question itself.
                boolean whole = false;
                try {
                    connection = (HttpURLConnection) new URL(url + "/api/terminal").openConnection();
                    connection.setConnectTimeout(4000); connection.setReadTimeout(8000); connection.setUseCaches(false);
                    connection.setInstanceFollowRedirects(false);
                    if (cookie != null) connection.setRequestProperty("Cookie", cookie);
                    int code = connection.getResponseCode();
                    if (code == 401) { state = "key"; line = "需要重新扫码登录"; }
                    else if (code != 200) line = "电脑没有响应（" + code + "）";
                    else {
                        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                        try (InputStream input = connection.getInputStream()) {
                            byte[] part = new byte[16384]; int n;
                            while ((n = input.read(part)) > 0) {
                                if (bytes.size() + n > 4000000) throw new Exception("返回内容过大");
                                bytes.write(part, 0, n);
                            }
                        }
                        whole = true;
                        data = new JSONObject(bytes.toString("UTF-8"));
                    }
                } catch (Exception unreachable) { /* keeps "off" */ }
                finally { if (connection != null && !whole) connection.disconnect(); }
                final JSONObject answer = data;
                final String s = state, l = line;
                activity.runOnUiThread(() -> {
                    pending.remove(url);
                    snapshots.put(url, answer == null ? new Snapshot(s, l, Collections.<AggregateSessions.Project>emptyList(), false) : read(url, answer));
                    activity.waiting(waiting());
                    if (!shown()) return;
                    drawComputer(computer); draw();
                });
            });
        }
        activity.ticker.postDelayed(tick, 6000);
    }
    void stop() { activity.ticker.removeCallbacks(tick); }

    private Snapshot read(String url, JSONObject data) {
        JSONObject device = data.optJSONObject("device");
        List<AggregateSessions.Project> none = Collections.emptyList();
        if (device == null || !device.optBoolean("online")) return new Snapshot("off", "电脑端程序没有在运行", none, false);
        if (!device.optBoolean("enabled")) return new Snapshot("off", "电脑端暂停了手机访问", none, false);
        boolean marks = false;
        JSONArray features = device.optJSONArray("features");
        for (int i = 0; features != null && i < features.length(); i++) marks |= "marks".equals(features.optString(i));
        ArrayList<String> names = new ArrayList<>();
        ArrayList<AggregateSessions.Entry> entries = new ArrayList<>();
        Set<String> alive = new HashSet<>();
        JSONArray folders = device.optJSONArray("workspaces");
        for (int i = 0; folders != null && i < folders.length(); i++) names.add(folders.optString(i));
        JSONArray projects = device.optJSONArray("projects");
        for (int i = 0; projects != null && i < projects.length(); i++) {
            JSONObject project = projects.optJSONObject(i);
            if (project != null) names.add(project.optString("name"));
        }
        for (boolean terminal : new boolean[]{true, false}) {
            JSONArray source = data.optJSONArray(terminal ? "terminals" : "sessions");
            for (int i = 0; source != null && i < source.length(); i++) {
                JSONObject entry = source.optJSONObject(i);
                if (entry == null) continue;
                if (terminal) alive.add(entry.optString("id"));
                AggregateSessions.Entry made = new AggregateSessions.Entry(terminal, entry.optString("id"), entry.optString("dir"),
                    entry.optString("title"), entry.optString("tool"), entry.optString("state"),
                    entry.optString("phase"), entry.optString("status"), entry.optBoolean("live"),
                    entry.optString("host"), entry.optString("terminal"), entry.optString("session"),
                    entry.optBoolean("done"), entry.optLong("phase_at"));
                made.said = entry.optString(terminal ? "said" : "last");
                JSONArray reach = terminal ? null : entry.optJSONArray("reach");
                made.reached = reach != null;
                made.canTakeover = entry.optBoolean("can_takeover");
                JSONObject ways = entry.optJSONObject("ways");
                made.endWay = ways == null ? null : ways.optString("end");
                made.folder = entry.optString("folder");
                made.now = terminal || entry.optBoolean("now", true);
                if (!terminal) { made.origin = entry.optString("origin"); made.kind = entry.optString("kind"); made.pinned = entry.optBoolean("pinned"); made.hidden = entry.optBoolean("hidden"); }
                for (int way = 0; reach != null && way < reach.length(); way++) made.canSay |= "say".equals(reach.optString(way));
                made.shell = device.optString("shell");
                made.used = terminal ? (made.at > 0 ? made.at : entry.optLong("created")) : entry.optLong("updated");
                entries.add(made);
            }
        }
        prune(url, alive);
        // a terminal is pinned with the conversation it runs
        Set<String> pinned = new HashSet<>();
        for (AggregateSessions.Entry entry : entries) if (!entry.terminal && entry.pinned) pinned.add(entry.tool + ':' + entry.id);
        for (AggregateSessions.Entry entry : entries) if (entry.terminal && !entry.session.isEmpty() && pinned.contains(entry.tool + ':' + entry.session)) entry.pinned = true;
        List<AggregateSessions.Project> groups = AggregateSessions.projects(names, entries);
        // with which tools and by what the computer says each project's folder is used
        for (int i = 0; projects != null && i < projects.length(); i++) {
            JSONObject project = projects.optJSONObject(i);
            if (project == null) continue;
            for (AggregateSessions.Project group : groups) if (group.name.equals(project.optString("name"))) { group.told(strings(project.optJSONArray("tools")), strings(project.optJSONArray("kinds"))); group.fixed = project.optBoolean("fixed"); }
        }
        int[] counts = AggregateSessions.counts(groups, seen(url));
        List<String> parts = new ArrayList<>();
        if (counts[0] > 0) parts.add(counts[0] + " 个等你确认");
        if (counts[1] > 0) parts.add(counts[1] + " 个已打开");
        if (counts[2] > 0) parts.add(counts[2] + " 个在执行");
        String line = !parts.isEmpty() ? android.text.TextUtils.join(" · ", parts) : counts[3] > 0 ? "在线 · " + counts[3] + " 个终端等待输入" : "在线 · 没有任务在运行";
        Snapshot read = new Snapshot("on", line, groups, marks);
        for (int i = 0; features != null && i < features.length(); i++) read.can.add(features.optString(i));
        return read;
    }

    // ---- drawing; a part is rebuilt only when what it shows has changed, so a press or a scroll is not interrupted
    private int color(String kind) {
        return "confirm".equals(kind) ? kit.BAD : "done".equals(kind) || "waiting".equals(kind) ? kit.GOOD : "busy".equals(kind) || "pc-busy".equals(kind) ? kit.BUSY
            : "starting".equals(kind) || "pc-idle".equals(kind) ? kit.ACCENT : kit.MUTED;
    }
    private int toolIcon(String tool) { return "claude".equals(tool) ? R.drawable.ic_claude : "codex".equals(tool) ? R.drawable.ic_codex : R.drawable.ic_shell; }
    private int toolColor(String tool) { return "claude".equals(tool) ? 0xffd98a5f : "codex".equals(tool) ? kit.ACCENT : kit.GOOD; }

    /**
     * The summary under the title and the lists over all computers, by what needs the owner: what waits for an
     * answer, what is pinned, what is at work, what was used lately; or, while something is searched for, what was found.
     */
    private void draw() {
        if (summary == null) return;
        JSONArray list = activity.computers();
        List<AggregateSessions.Thing> things = new ArrayList<>();
        int online = 0, known = 0;
        int[] total = new int[4];
        for (int i = 0; i < list.length(); i++) {
            JSONObject computer = list.optJSONObject(i);
            if (computer == null) continue;
            String url = computer.optString("url"), name = computer.optString("name");
            Snapshot snapshot = snapshots.get(url);
            if (snapshot == null) continue;
            known++;
            if (!"on".equals(snapshot.state)) continue;
            online++;
            Map<String, Long> looked = seen(url);
            int[] counts = AggregateSessions.counts(snapshot.projects, looked);
            for (int k = 0; k < 4; k++) total[k] += counts[k];
            for (AggregateSessions.Project project : snapshot.projects) {
                for (AggregateSessions.Entry entry : project.active) things.add(new AggregateSessions.Thing(url, name, entry, entry.kind(looked)));
                for (AggregateSessions.Entry entry : project.ended) things.add(new AggregateSessions.Thing(url, name, entry, "ended"));
                // of the saved conversations, the ones nothing has open: one that a program holds is found under its computer
                for (AggregateSessions.Entry entry : project.history) if (!entry.live) things.add(new AggregateSessions.Thing(url, name, entry, "history"));
            }
        }
        if (known > 0) {
            List<String> parts = new ArrayList<>();
            parts.add(list.length() == 1 ? (online == 1 ? "电脑在线" : "电脑离线") : online + " / " + list.length() + " 台在线");
            if (total[0] > 0) parts.add(total[0] + " 个等你确认");
            if (total[1] > 0) parts.add(total[1] + " 个已打开");
            if (total[2] > 0) parts.add(total[2] + " 个在执行");
            kit.stands(summary, android.text.TextUtils.join(" · ", parts), total[0] > 0 ? kit.BAD : kit.MUTED, online > 0 ? kit.GOOD : kit.BAD);
        }
        if (attention == null) return;
        AggregateSessions.Board board = AggregateSessions.board(things, query, tool, source);
        drawFilters(board);
        boolean several = list.length() > 1;
        long now = System.currentTimeMillis();
        StringBuilder mark = new StringBuilder();
        List<AggregateSessions.Thing> all = new ArrayList<>();
        for (List<AggregateSessions.Thing> part : java.util.Arrays.asList(board.needs, board.pinned, board.active, board.opened, board.recent, board.found)) {
            mark.append(part.size()).append('|');
            for (AggregateSessions.Thing task : part) {
                all.add(task);
                mark.append(task.url).append('\n').append(task.entry.id).append('\n').append(task.kind).append('\n').append(task.entry.shownTitle()).append('\n').append(task.computer).append('\n')
                    .append(AggregateSessions.where(task.entry)).append('\n').append(task.entry.pinned).append(task.entry.hidden).append(canEnd(task.url, task.entry)).append(ending.contains(task.url + '\n' + task.entry.id)).append('\n');
                if (!task.running()) mark.append(AggregateSessions.ago(AggregateSessions.recent(task.entry), now)).append('\n');
            }
        }
        mark.append(recentOpen).append(folded("active")).append(folded("opened")).append(folded("recent")).append(board.searched).append(several).append(tool).append('|').append(source).append(online).append('/').append(known);
        if (mark.toString().equals(attentionMark)) {
            // the cards stay; only what each terminal last said is written anew
            for (AggregateSessions.Thing task : all) {
                TextView peek = peeks.get(task.url + '\n' + task.entry.id);
                if (peek == null) continue;
                if (!task.entry.said.contentEquals(peek.getText())) peek.setText(task.entry.said);
                peek.setVisibility(task.entry.said.isEmpty() ? View.GONE : View.VISIBLE);
            }
            return;
        }
        attentionMark = mark.toString();
        peeks.clear();
        attention.removeAllViews(); recent.removeAllViews();
        if (board.searched) {
            // everything is looked through, on every computer, what is hidden too
            attention.addView(heading("搜索结果", board.found.isEmpty() ? "没有找到" : board.found.size() + " 个"), kit.below(14));
            for (int i = 0; i < Math.min(FOUND, board.found.size()); i++) attention.addView(taskCard(board.found.get(i), several), kit.below(8));
            if (board.found.size() > FOUND) attention.addView(kit.text("另外 " + (board.found.size() - FOUND) + " 个没有列出，再输几个字可以找到。", 12.5f, kit.MUTED), kit.below(8));
            return;
        }
        section(attention, "等你确认", board.needs.size() + " 个", board.needs, board.needs.size(), several, null);
        section(attention, "置顶记录", board.pinned.size() + " 个", board.pinned, board.pinned.size(), several, null);
        // Folded, the heading still says how many are at work.
        section(attention, "正在运行", board.active.size() + " 个", board.active, TASKS, several, "active");
        if (!folded("active") && board.active.size() > TASKS) attention.addView(kit.text("另外 " + (board.active.size() - TASKS) + " 个在各自的项目里。", 12.5f, kit.MUTED), kit.below(8));
        section(attention, "已打开", board.opened.size() + " 个 · 等待输入或已闲置", board.opened, TASKS, several, "opened");
        if (!folded("opened") && board.opened.size() > TASKS) attention.addView(kit.text("另外 " + (board.opened.size() - TASKS) + " 个可以搜索到，也在对应的项目里。", 12.5f, kit.MUTED), kit.below(8));
        int shown = Math.min(recentOpen ? RECENT_ALL : RECENT, board.recent.size());
        section(recent, "最近", board.recent.size() + " 个", board.recent, shown, several, "recent");
        if (!board.recent.isEmpty() && !folded("recent") && board.recent.size() > RECENT) {
            TextView toggle = kit.text(recentOpen ? "只显示前 " + RECENT + " 个" : "其余 " + (board.recent.size() - shown) + " 个", 13, kit.MUTED);
            toggle.setGravity(Gravity.CENTER); toggle.setMinHeight(kit.dp(44));
            kit.press(toggle, () -> { recentOpen = !recentOpen; draw(); });
            recent.addView(toggle, kit.below(2));
            if (recentOpen && board.recent.size() > shown) recent.addView(kit.text("另外 " + (board.recent.size() - shown) + " 个可以搜索到，也在对应的项目里。", 12.5f, kit.MUTED), kit.below(4));
        }
        if (attention.getChildCount() > 0 || recent.getChildCount() > 0) return;
        if (!tool.isEmpty() || !source.isEmpty()) {
            TextView none = kit.text("没有符合筛选的对话或终端。", 13.5f, kit.MUTED);
            none.setGravity(Gravity.CENTER); none.setMinHeight(kit.dp(60));
            attention.addView(none, kit.below(14));
        } else if (online > 0) {
            // nothing anywhere yet: what to do first
            TextView none = kit.text("还没有对话", 14.5f, kit.MUTED);
            none.setGravity(Gravity.CENTER);
            attention.addView(none, kit.below(56));
            LinearLayout.LayoutParams middle = new LinearLayout.LayoutParams(-2, -2);
            middle.gravity = Gravity.CENTER_HORIZONTAL; middle.topMargin = kit.dp(14);
            attention.addView(kit.button("新建对话", 0, this::newConversation), middle);
        } else if (known == list.length()) {
            // no computer answers: which, and why, is said on 项目
            TextView none = kit.text(list.length() == 1 ? "电脑不在线" : "电脑都不在线", 14.5f, kit.MUTED);
            none.setGravity(Gravity.CENTER);
            attention.addView(none, kit.below(56));
            LinearLayout.LayoutParams middle = new LinearLayout.LayoutParams(-2, -2);
            middle.gravity = Gravity.CENTER_HORIZONTAL; middle.topMargin = kit.dp(14);
            attention.addView(kit.button("查看电脑", 1, () -> activity.appPage("projects")), middle);
        }
    }
    /** One of the lists: its heading, which folds the list away where `fold` names it, and the cards of its first things. */
    private void section(LinearLayout into, String title, String note, List<AggregateSessions.Thing> things, int most, boolean several, String fold) {
        if (things.isEmpty()) return;
        boolean folded = fold != null && folded(fold);
        into.addView(fold == null ? heading(title, note) : heading(title, note, folded, () -> fold(fold)), kit.below(into.getChildCount() == 0 ? 14 : 16));
        for (int i = 0; !folded && i < Math.min(most, things.size()); i++) into.addView(taskCard(things.get(i), several), kit.below(i == 0 && fold != null ? 0 : 8));
    }
    /** The row under the search: narrow the lists to a tool, and to where a thing runs. Offered only where it tells something apart. */
    private static List<String> strings(JSONArray array) {
        List<String> out = new ArrayList<>();
        for (int i = 0; array != null && i < array.length(); i++) if (!array.optString(i).isEmpty()) out.add(array.optString(i));
        return out;
    }
    private void drawFilters(AggregateSessions.Board board) {
        if (filters == null) return;
        List<String> tools = new ArrayList<>(board.tools), sources = new ArrayList<>(board.sources);
        // (a project whose folder is used by an app or in a terminal is told apart too, also while nothing of it is listed)
        for (Snapshot snapshot : snapshots.values()) for (AggregateSessions.Project project : snapshot.projects) {
            for (String known : new String[]{"claude", "codex", "opencode", "qoder", "cline"}) if (project.tools.contains(known) && !tools.contains(known)) tools.add(known);
            for (String known : new String[]{"term", "app"}) if (project.sources.contains(known) && !sources.contains(known)) sources.add(known);
        }
        if (!tool.isEmpty() && !tools.contains(tool)) tools.add(tool);
        if (!source.isEmpty() && !sources.contains(source)) sources.add(source);
        if (tools.size() < 2 && tool.isEmpty()) tools.clear();
        if (sources.size() < 2 && source.isEmpty()) sources.clear();
        String mark = tools + "|" + sources + "|" + tool + "|" + source;
        if (mark.equals(filtersMark)) return;
        filtersMark = mark;
        filters.removeAllViews();
        if (tools.isEmpty() && sources.isEmpty()) return;
        filters.addView(chip("全部", tool.isEmpty() && source.isEmpty(), () -> { tool = ""; source = ""; draw(); }));
        for (final String one : tools) filters.addView(chip(AggregateSessions.toolName(one), one.equals(tool), () -> { tool = one.equals(tool) ? "" : one; draw(); }));
        if (!tools.isEmpty() && !sources.isEmpty()) {
            View between = new View(activity);
            between.setBackgroundColor(kit.LINE);
            LinearLayout.LayoutParams thin = new LinearLayout.LayoutParams(kit.dp(1), kit.dp(18));
            thin.rightMargin = kit.dp(8);
            filters.addView(between, thin);
        }
        for (final String one : sources) filters.addView(chip(AggregateSessions.sourceLabel(one), one.equals(source), () -> { source = one.equals(source) ? "" : one; draw(); }));
    }
    private View chip(String label, boolean on, Runnable action) {
        TextView chip = on ? kit.bold(label, 13, kit.ON_ACCENT) : kit.text(label, 13, kit.MUTED);
        chip.setSingleLine(true); chip.setGravity(Gravity.CENTER); chip.setMinHeight(kit.dp(34)); chip.setPadding(kit.dp(13), 0, kit.dp(13), 0);
        chip.setBackground(kit.shape(on ? kit.ACCENT : kit.PANEL, on ? 0 : kit.LINE, 17));
        chip.setContentDescription(label + (on ? "，已选" : ""));
        kit.press(chip, action);
        LinearLayout.LayoutParams gap = new LinearLayout.LayoutParams(-2, -2);
        gap.rightMargin = kit.dp(8);
        chip.setLayoutParams(gap);
        return chip;
    }
    private View taskCard(AggregateSessions.Thing task, boolean several) {
        final AggregateSessions.Entry entry = task.entry;
        LinearLayout card = kit.row();
        card.setMinimumHeight(kit.dp(66)); card.setPadding(kit.dp(14), kit.dp(11), kit.dp(14), kit.dp(11));
        int edge = "confirm".equals(task.kind) ? Kit.mix(kit.BAD, kit.LINE, .55f) : "done".equals(task.kind) || "waiting".equals(task.kind) ? Kit.mix(kit.GOOD, kit.LINE, .45f) : kit.LINE;
        card.setBackground(kit.shape(kit.PANEL, edge, 16));
        card.addView(kit.tile(toolIcon(entry.tool), toolColor(entry.tool), 40));
        LinearLayout texts = kit.column();
        texts.setPadding(kit.dp(12), 0, 0, 0);
        texts.addView(kit.title(entry.shownTitle(), 15.5f, kit.INK, true));
        if (!entry.place().isEmpty()) texts.addView(projectTag(entry));
        LinearLayout meta = kit.row();
        boolean closing = ending.contains(task.url + '\n' + entry.id);
        meta.addView(kit.pill(closing ? "正在结束" : entry.hidden ? "已隐藏" : AggregateSessions.label(task.kind), closing ? kit.MUTED : color(task.kind)));
        if (entry.pinned) {
            LinearLayout.LayoutParams beside = new LinearLayout.LayoutParams(-2, -2);
            beside.leftMargin = kit.dp(6);
            meta.addView(kit.pill("置顶", kit.ACCENT), beside);
        }
        // by what it runs and where it is: a terminal of the phone's, the desktop app, an editor, a command line; its
        // project, or the folder a conversation is open in where there is no project
        TextView where = kit.line(AggregateSessions.location(entry) + (several ? " · " + task.computer : ""), 12.5f, kit.MUTED, false);
        where.setPadding(kit.dp(8), 0, 0, 0);
        meta.addView(where, new LinearLayout.LayoutParams(0, -2, 1));
        texts.addView(meta, kit.below(4));
        if (!entry.running()) {
            String when = AggregateSessions.ago(AggregateSessions.recent(entry), System.currentTimeMillis());
            if (!when.isEmpty()) texts.addView(kit.text(entry.terminal ? when + "结束" : entry.toolName() + " · " + when, 12.5f, kit.MUTED), kit.below(4));
        }
        if (entry.terminal || entry.reached) {
            TextView peek = kit.text(entry.said, 12.5f, kit.MUTED);
            peek.setMaxLines(2); peek.setEllipsize(android.text.TextUtils.TruncateAt.END);
            peek.setVisibility(entry.said.isEmpty() ? View.GONE : View.VISIBLE);
            texts.addView(peek, kit.below(5));
            peeks.put(task.url + '\n' + entry.id, peek);
        }
        card.addView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        if (entry.terminal && "running".equals(entry.state)) {
            // the disc ends as far from the card's edge as the tool's tile begins from the other
            card.setPadding(kit.dp(14), kit.dp(11), kit.dp(10), kit.dp(11));
            texts.setPadding(kit.dp(12), 0, kit.dp(6), 0);
            // A terminal that waits on a question is answered where the question can be read: Enter would say yes to it.
            if (!"confirm".equals(task.kind)) card.addView(sayButton(task));
        }
        // A conversation open on the computer can be left a message from here; what it asks is answered where it can
        // be read, on the page a press opens.
        if (entry.reached && entry.canSay && !"confirm".equals(task.kind)) {
            card.setPadding(kit.dp(14), kit.dp(11), kit.dp(10), kit.dp(11));
            texts.setPadding(kit.dp(12), 0, kit.dp(6), 0);
            card.addView(sayButton(task));
        }
        if (canEnd(task.url, entry)) card.addView(stopButton(task));
        kit.press(card, () -> {
            if (!entry.terminal) { activity.openSession(task.url, entry.project, entry.id); return; }
            markSeen(task.url, entry.id, entry.at);
            activity.openTerminal(task.url, entry.id);
        });
        // a held press asks what to do with it besides opening it
        card.setOnLongClickListener(v -> { manage(task); return true; });
        return card;
    }

    // ---- what is done with a thing besides opening it: pinned, hidden, its folder made a project, ended
    private void manage(AggregateSessions.Thing task) {
        final AggregateSessions.Entry entry = task.entry;
        Snapshot snapshot = snapshots.get(task.url);
        boolean marks = snapshot != null && snapshot.marks && !entry.conversation().isEmpty();
        final List<String> labels = new ArrayList<>(), does = new ArrayList<>();
        if (marks) { labels.add(entry.pinned ? "取消置顶" : "置顶"); does.add("pin"); }
        if (marks && "history".equals(task.kind)) { labels.add(entry.hidden ? "取消隐藏" : "从列表隐藏"); does.add("hide"); }
        if (entry.apart()) { labels.add("把 " + entry.place() + " 加为项目"); does.add("add"); }
        int danger = -1;
        if (canEnd(task.url, entry)) { danger = labels.size(); labels.add(entry.terminal ? "结束终端" : "结束电脑上的这段对话"); does.add("end"); }
        // a saved conversation of a project that nothing has open can be put away (the computer keeps it recoverable)
        if (snapshot != null && snapshot.can.contains("delete") && "history".equals(task.kind) && !entry.terminal && !entry.project.isEmpty()) { danger = labels.size(); labels.add("删除对话"); does.add("delete"); }
        if (labels.isEmpty()) return;
        kit.sheet(entry.shownTitle(), AggregateSessions.where(entry), labels.toArray(new String[0]), null, danger, which -> {
            String what = does.get(which);
            if ("pin".equals(what)) markRemote(task, true, !entry.pinned);
            else if ("hide".equals(what)) markRemote(task, false, !entry.hidden);
            else if ("add".equals(what)) addFolder(task);
            else if ("delete".equals(what)) deleteTask(task);
            else stopTask(task);
        });
    }
    private void deleteTask(AggregateSessions.Thing task) {
        kit.sheet("删除“" + task.entry.shownTitle() + "”？", "", new String[]{"删除"}, null, 0, which -> ask(task.url, "/api/conversation", "已删除", "没有删除，请稍后重试", op -> op.put("action", "delete").put("session", task.entry.id)));
    }
    /** A project besides opening it: taken from its computer's list (the folder and what is in it stay). */
    private void manageProject(String url, AggregateSessions.Project project) {
        Snapshot snapshot = snapshots.get(url);
        if (snapshot == null || project.fixed && !snapshot.can.contains("projects")) return;
        kit.sheet(project.name, "", new String[]{"移除项目"}, null, 0, which -> ask(url, "/api/terminal", "已移除", "没有移除，请稍后重试", op -> op.put("action", "project_remove").put("name", project.name)));
    }
    private interface Filled { void fill(JSONObject op) throws org.json.JSONException; }
    /** Asks a computer for one thing and says how it went. */
    private void ask(String url, String path, String done, String failed, Filled filled) {
        final String cookie = CookieManager.getInstance().getCookie(url);
        activity.net.execute(() -> {
            String problem = "";
            try {
                JSONObject op = new JSONObject().put("id", UUID.randomUUID().toString().replace("-", ""));
                filled.fill(op);
                JSONObject result = post(url + path, cookie, op);
                if ("error".equals(result.optString("state"))) throw new Exception(result.optString("error", failed));
            } catch (Exception error) {
                String said = error.getMessage();
                // a relay of an earlier version answers so to anything it does not know
                problem = "不支持此操作".equals(said) ? "这台电脑的电脑端或中转版本较早，请更新后再试" : said == null ? failed : said;
            }
            final String message = problem.isEmpty() ? done : problem;
            activity.runOnUiThread(() -> { android.widget.Toast.makeText(activity, message, android.widget.Toast.LENGTH_SHORT).show(); refresh(); });
        });
    }
    /** The computer keeps what is said of a conversation (the action "mark"); what is hidden is no longer pinned. */
    private void markRemote(AggregateSessions.Thing task, boolean pin, boolean on) {
        final String cookie = CookieManager.getInstance().getCookie(task.url);
        activity.net.execute(() -> {
            String problem = "";
            try {
                JSONObject op = new JSONObject().put("action", "mark").put("id", UUID.randomUUID().toString().replace("-", "")).put("session", task.entry.conversation());
                if (pin) op.put("pinned", on); else { op.put("hidden", on); if (on) op.put("pinned", false); }
                post(task.url + "/api/conversation", cookie, op);
            } catch (Exception error) {
                String said = error.getMessage();
                // a relay of an earlier version answers so to anything it does not know
                problem = "不支持此操作".equals(said) ? "这台电脑的电脑端或中转版本较早，暂不支持置顶和隐藏，请更新后再试" : said == null ? "没有改成，请稍后重试" : said;
            }
            final String message = problem;
            activity.runOnUiThread(() -> {
                if (!message.isEmpty()) android.widget.Toast.makeText(activity, message, android.widget.Toast.LENGTH_SHORT).show();
                else if (pin) task.entry.pinned = on;
                else { task.entry.hidden = on; if (on) task.entry.pinned = false; }
                redraw(); refresh();
            });
        });
    }
    /** The folder a conversation is open in becomes a project of its computer; the conversation is then one of that project. */
    private void addFolder(AggregateSessions.Thing task) {
        final String cookie = CookieManager.getInstance().getCookie(task.url);
        activity.net.execute(() -> {
            String problem = "";
            try {
                JSONObject op = new JSONObject().put("action", "project_add").put("id", UUID.randomUUID().toString().replace("-", "")).put("path", task.entry.folder).put("name", "").put("create", false);
                JSONObject result = post(task.url + "/api/terminal", cookie, op);
                if ("error".equals(result.optString("state"))) throw new Exception(result.optString("error", "没有加上，请稍后重试"));
            } catch (Exception error) { problem = error.getMessage() == null ? "没有加上，请稍后重试" : error.getMessage(); }
            final String message = problem.isEmpty() ? "已加为项目" : problem;
            activity.runOnUiThread(() -> { android.widget.Toast.makeText(activity, message, android.widget.Toast.LENGTH_SHORT).show(); refresh(); });
        });
    }

    private boolean canEnd(String url, AggregateSessions.Entry entry) {
        Snapshot snapshot = snapshots.get(url);
        return snapshot != null && "on".equals(snapshot.state) && entry.canEnd(snapshot.can.contains("end"));
    }
    private View projectTag(AggregateSessions.Entry entry) {
        TextView tag = kit.bold(entry.place() + (entry.apart() ? " · 未加入项目" : ""), 12, kit.INK);
        tag.setMaxLines(2); tag.setEllipsize(android.text.TextUtils.TruncateAt.END);
        tag.setPadding(kit.dp(7), kit.dp(2), kit.dp(7), kit.dp(2));
        tag.setBackground(kit.shape(kit.RAISED, 0, 7));
        LinearLayout.LayoutParams space = new LinearLayout.LayoutParams(-2, -2);
        space.topMargin = kit.dp(4);
        tag.setLayoutParams(space);
        tag.setContentDescription((entry.apart() ? "文件夹：" : "项目：") + entry.place());
        return tag;
    }
    private View stopButton(AggregateSessions.Thing task) {
        boolean busy = ending.contains(task.url + '\n' + task.entry.id);
        View button = kit.discButton(R.drawable.ic_stop, busy ? "正在结束“" + task.entry.shownTitle() + "”" : "结束“" + task.entry.shownTitle() + "”", () -> stopTask(task));
        button.setEnabled(!busy); button.setAlpha(busy ? .4f : 1f);
        return button;
    }

    // ---- a message for a running terminal, typed here and sent without going into it
    private View sayButton(AggregateSessions.Thing task) {
        return kit.discButton(R.drawable.ic_say, "给“" + task.entry.shownTitle() + "”发消息", () -> sayTask(task));
    }
    private void sayTask(AggregateSessions.Thing task) {
        // what the program said last is shown, to tell which terminal this is and where it stands
        kit.write(task.entry.shownTitle(), task.entry.said.isEmpty() ? task.entry.project + " · " + AggregateSessions.label(task.kind) : task.entry.said,
            "发送消息", "发送", text -> sayRemote(task, text));
    }
    /** The relay puts the message the way the program takes pasted text and adds Enter (the operation "say"). */
    private void sayRemote(AggregateSessions.Thing task, String text) {
        final String cookie = CookieManager.getInstance().getCookie(task.url);
        activity.net.execute(() -> {
            String problem = "";
            try {
                JSONObject op = new JSONObject().put("action", "say").put("id", UUID.randomUUID().toString().replace("-", "")).put("text", text);
                // a conversation that is open on the computer is told through the program that has it; a terminal through its own
                op.put(task.entry.terminal ? "terminal" : "session", task.entry.id);
                JSONObject result = null;
                // the same operation asked again is the same one: a request that did not get through is made once more
                for (int attempt = 0; result == null && attempt < 3; attempt++) {
                    // a conversation that sits idle is handed the message where it is, which takes some seconds: the answer is waited for
                    try { result = post(task.url + (task.entry.terminal ? "/api/terminal" : "/api/conversation"), cookie, op, task.entry.terminal ? 8000 : 95000); }
                    catch (IOException unreachable) { Thread.sleep(500); }
                }
                if (result == null) throw new Exception("连不上这台电脑，消息没有发出");
                if ("error".equals(result.optString("state"))) throw new Exception(result.optString("error", "消息没有发出"));
            } catch (Exception error) {
                String said = error.getMessage();
                // a relay of an earlier version answers so to anything it does not know
                problem = "不支持此操作".equals(said) ? "这台电脑的电脑端或中转版本较早，暂不支持在此发消息，请更新后再试" : said == null ? "消息没有发出，请稍后重试" : said;
            }
            final String message = problem.isEmpty() ? "已发送" : problem;
            activity.runOnUiThread(() -> {
                android.widget.Toast.makeText(activity, message, android.widget.Toast.LENGTH_SHORT).show();
                refresh();
            });
        });
    }

    private void redraw() {
        if (!shown()) return;
        JSONArray list = activity.computers();
        for (int i = 0; i < list.length(); i++) { JSONObject computer = list.optJSONObject(i); if (computer != null) drawComputer(computer); }
        draw();
    }

    private void stopTask(AggregateSessions.Thing task) {
        final String key = task.url + '\n' + task.entry.id;
        if (ending.contains(key)) return;
        if (!canEnd(task.url, task.entry)) return;
        kit.sheet("结束“" + task.entry.shownTitle() + "”？", "正在执行的任务会被中断；已保存的对话记录保留。", new String[]{task.entry.terminal ? "结束终端" : "结束电脑上的这段对话"}, null, 0,
            which -> { if (which == 0 && ending.add(key)) { redraw(); closeRemote(task, key); } });
    }
    private void closeRemote(AggregateSessions.Thing task, String key) {
        final String cookie = CookieManager.getInstance().getCookie(task.url);
        activity.net.execute(() -> {
            String problem = "";
            try {
                if (!task.entry.terminal) {
                    JSONObject op = new JSONObject().put("action", "end").put("id", UUID.randomUUID().toString().replace("-", "")).put("session", task.entry.id);
                    // Conversation operations return their result once. Do not retry a timeout as a second close.
                    post(task.url + "/api/conversation", cookie, op, 40000);
                } else {
                JSONObject op = new JSONObject().put("action", "close").put("id", UUID.randomUUID().toString().replace("-", "")).put("terminal", task.entry.id);
                JSONObject result = null;
                long deadline = System.nanoTime() + 30_000_000_000L;
                for (int attempt = 0; attempt < 60 && System.nanoTime() < deadline; attempt++) {
                    try { result = post(task.url + "/api/terminal", cookie, op); }
                    catch (IOException unreachable) { Thread.sleep(400); continue; }
                    String state = result.optString("state");
                    if ("done".equals(state)) break;
                    if ("error".equals(state)) throw new Exception(result.optString("error", "终端没有结束"));
                    if (!"queued".equals(state)) throw new Exception("电脑没有响应");
                    Thread.sleep(400);
                }
                if (result == null || "queued".equals(result.optString("state"))) throw new Exception("电脑没有响应，请稍后重试");
                }
            } catch (Exception error) { problem = error.getMessage() == null ? "结束失败，请刷新后确认当前状态" : error.getMessage(); }
            final String message = problem;
            activity.runOnUiThread(() -> { ending.remove(key); if (!message.isEmpty()) android.widget.Toast.makeText(activity, message, android.widget.Toast.LENGTH_SHORT).show(); redraw(); refresh(); });
        });
    }
    JSONObject post(String address, String cookie, JSONObject payload) throws Exception { return post(address, cookie, payload, 8000); }
    /** patience: how long the answer is waited for, in milliseconds. */
    private JSONObject post(String address, String cookie, JSONObject payload, int patience) throws Exception {
        HttpURLConnection connection = (HttpURLConnection) new URL(address).openConnection();
        boolean whole = false;
        try {
            connection.setConnectTimeout(4000); connection.setReadTimeout(patience); connection.setUseCaches(false); connection.setDoOutput(true);
            connection.setInstanceFollowRedirects(false);
            connection.setRequestMethod("POST"); connection.setRequestProperty("Content-Type", "application/json");
            if (cookie != null) connection.setRequestProperty("Cookie", cookie);
            byte[] body = payload.toString().getBytes("UTF-8");
            connection.setFixedLengthStreamingMode(body.length);
            try (OutputStream output = connection.getOutputStream()) { output.write(body); }
            int code = connection.getResponseCode();
            if (code == 401) throw new Exception("登录已过期，请重新连接电脑");
            InputStream input = code >= 400 ? connection.getErrorStream() : connection.getInputStream();
            if (input == null) throw new Exception("电脑没有响应（" + code + "）");
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            try (InputStream stream = input) {
                byte[] part = new byte[4096]; int n;
                while ((n = stream.read(part)) > 0) {
                    if (bytes.size() + n > 1000000) throw new Exception("返回内容过大");
                    bytes.write(part, 0, n);
                }
            }
            whole = true;
            JSONObject result = new JSONObject(bytes.toString("UTF-8"));
            if (code != 200) throw new Exception(result.optString("error", "请求失败（" + code + "）"));
            return result;
        } finally { if (!whole) connection.disconnect(); }
    }

    /** One computer: its name and state, then its projects, the ones with running tasks first. */
    private void drawComputer(JSONObject computer) {
        final String url = computer.optString("url");
        LinearLayout holder = holders.get(url);
        if (holder == null) return;
        Snapshot snapshot = snapshots.get(url);
        Map<String, Long> looked = seen(url);
        String name = computer.optString("name").isEmpty() ? MainActivity.host(url) : computer.optString("name");
        String mark = name + '\n' + expanded.contains(url) + '\n' + collapsed.contains(url) + '\n' + unfolded + '\n' + ending + '\n' + (snapshot == null ? "" : snapshot.state + '\n' + snapshot.line + '\n' + snapshot.can.contains("end") + '\n' + AggregateSessions.signature(snapshot.projects, looked));
        if (mark.equals(marks.get(url))) return;
        marks.put(url, mark);
        holder.removeAllViews();
        LinearLayout card = kit.column();
        card.setBackground(kit.shape(kit.PANEL, kit.LINE, 18));
        boolean on = snapshot != null && "on".equals(snapshot.state);
        int[] counts = on ? AggregateSessions.counts(snapshot.projects, looked) : new int[4];
        int shade = snapshot == null ? kit.MUTED : !on ? ("key".equals(snapshot.state) ? kit.BUSY : kit.BAD) : counts[0] > 0 ? kit.BAD : counts[2] > 0 ? kit.BUSY : kit.GOOD;
        LinearLayout head = kit.row();
        head.setMinimumHeight(kit.dp(64)); head.setPadding(kit.dp(16), kit.dp(10), kit.dp(4), kit.dp(10));
        head.addView(kit.dot(shade, 10));
        LinearLayout texts = kit.column();
        texts.setPadding(kit.dp(14), 0, kit.dp(4), 0);
        texts.addView(kit.line(name, 16.5f, kit.INK, true));
        texts.addView(kit.line(snapshot == null ? "正在连接…" : snapshot.line, 12.5f, on && counts[0] > 0 ? kit.BAD : kit.MUTED, false));
        head.addView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        boolean folded = collapsed.contains(url);
        View fold = kit.iconButton(R.drawable.ic_chevron, folded ? "展开" + name : "收起" + name, () -> { if (!collapsed.remove(url)) collapsed.add(url); drawComputer(computer); });
        ((android.widget.ImageView) ((android.widget.FrameLayout) fold).getChildAt(0)).setRotation(folded ? 0 : 90);
        head.addView(fold);
        head.addView(kit.iconButton(R.drawable.ic_more, name + " 的选项", () -> activity.manage(url)));
        kit.press(head, () -> activity.open(url, ""));
        head.setOnLongClickListener(v -> { activity.manage(url); return true; });
        card.addView(head);
        if (on && !folded) {
            List<AggregateSessions.Project> order = new ArrayList<>(), named = new ArrayList<>();
            named.addAll(snapshot.projects);
            for (AggregateSessions.Project project : named) if (!project.active.isEmpty()) order.add(project);
            int busy = order.size();
            for (AggregateSessions.Project project : named) if (project.active.isEmpty() && !project.ended.isEmpty()) order.add(project);
            for (AggregateSessions.Project project : named) if (project.active.isEmpty() && project.ended.isEmpty()) order.add(project);
            boolean all = expanded.contains(url);
            int shown = all ? order.size() : Math.max(busy, Math.min(SHOWN, order.size()));
            for (int i = 0; i < shown; i++) { card.addView(rule()); card.addView(projectRow(computer, order.get(i), looked)); }
            // (a project is added on the computer's own page of projects, which lists the folders that have conversations)
            card.addView(rule());
            TextView fresh = kit.bold(order.isEmpty() ? "＋ 添加项目" : "＋ 新建对话", 13.5f, kit.ACCENT);
            fresh.setGravity(Gravity.CENTER); fresh.setMinHeight(kit.dp(46));
            kit.press(fresh, order.isEmpty() ? () -> activity.open(url, "") : () -> activity.startNew(url));
            card.addView(fresh);
            if (order.size() > Math.max(busy, SHOWN) || all && order.size() > SHOWN) {
                card.addView(rule());
                TextView toggle = kit.text(all ? "收起" : "其余 " + (order.size() - shown) + " 个项目", 13, kit.MUTED);
                toggle.setGravity(Gravity.CENTER); toggle.setMinHeight(kit.dp(44));
                kit.press(toggle, () -> { if (!expanded.remove(url)) expanded.add(url); drawComputer(computer); });
                card.addView(toggle);
            }
        } else if (!on && !folded && snapshot != null) {
            card.addView(rule());
            LinearLayout help = kit.row();
            help.setPadding(kit.dp(16), kit.dp(10), kit.dp(12), kit.dp(12));
            help.addView(kit.text("key".equals(snapshot.state) ? "登录已过期。" : "请确认电脑已开机且 Remote AI 正在运行。退出电脑端或重启电脑后，公网隧道的地址会变更。", 12.5f, kit.MUTED), new LinearLayout.LayoutParams(0, -2, 1));
            TextView again = kit.bold("重新扫码", 13.5f, kit.ACCENT);
            again.setGravity(Gravity.CENTER); again.setMinHeight(kit.dp(40)); again.setPadding(kit.dp(14), 0, kit.dp(14), 0);
            again.setBackground(kit.shape(Kit.tint(kit.ACCENT, 34), 0, 12));
            kit.press(again, activity::scan);
            LinearLayout.LayoutParams beside = new LinearLayout.LayoutParams(-2, -2);
            beside.leftMargin = kit.dp(10);
            help.addView(again, beside);
            card.addView(help);
        }
        holder.addView(card);
    }
    private View rule() {
        View rule = new View(activity);
        rule.setBackgroundColor(kit.LINE);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, kit.dp(1));
        params.leftMargin = kit.dp(16);
        rule.setLayoutParams(params);
        return rule;
    }
    private View note(String text) {
        TextView note = kit.text(text, 13, kit.MUTED);
        note.setPadding(kit.dp(16), kit.dp(12), kit.dp(16), kit.dp(14));
        return note;
    }
    /** One project. A tap unfolds its conversations here, so that one of them is opened without a trip through that computer's pages. */
    private View projectRow(JSONObject computer, AggregateSessions.Project project, Map<String, Long> looked) {
        final String url = computer.optString("url"), key = url + '\n' + project.name;
        int need = 0, work = 0, opened = 0;
        List<AggregateSessions.Entry> confirms = new ArrayList<>(), running = new ArrayList<>(), waiting = new ArrayList<>();
        for (AggregateSessions.Entry entry : project.active) {
            String kind = entry.kind(looked);
            if ("confirm".equals(kind)) { need++; confirms.add(entry); }
            else if (AggregateSessions.working(kind)) { work++; running.add(entry); }
            else { opened++; waiting.add(entry); }
        }
        boolean open = unfolded.contains(key);
        LinearLayout row = kit.row();
        row.setMinimumHeight(kit.dp(52)); row.setPadding(kit.dp(16), kit.dp(8), kit.dp(12), kit.dp(8));
        row.addView(kit.icon(R.drawable.ic_folder, open ? kit.ACCENT : kit.MUTED, 20));
        LinearLayout texts = kit.column();
        texts.setPadding(kit.dp(12), 0, kit.dp(8), 0);
        texts.addView(kit.title(project.name.isEmpty() ? "未加入项目的对话" : project.name, 15, kit.INK, open));
        // Counts wrap under the title so mixed states cannot squeeze the project name off a phone screen.
        int[] counts = { need, work, opened };
        String[] says = { " 个等你确认", " 个正在运行", " 个已打开" };
        List<String> parts = new ArrayList<>();
        for (int i = 0; i < 3; i++) {
            if (counts[i] > 0) parts.add(counts[i] + says[i]);
        }
        if (parts.isEmpty()) parts.add(!project.ended.isEmpty() ? project.ended.size() + " 个历史终端" : project.history.isEmpty() ? "还没有对话" : project.history.size() + " 段对话");
        if (!project.uses().isEmpty()) parts.add(project.uses());
        texts.addView(kit.text(android.text.TextUtils.join(" · ", parts), 12.5f, kit.MUTED), kit.below(4));
        row.addView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        View chevron = kit.icon(R.drawable.ic_chevron, Kit.tint(kit.MUTED, 150), 16);
        ((LinearLayout.LayoutParams) chevron.getLayoutParams()).leftMargin = kit.dp(6);
        chevron.setRotation(open ? 90 : 0);
        row.addView(chevron);
        kit.press(row, () -> { if (!unfolded.remove(key)) unfolded.add(key); drawComputer(computer); });
        if (!project.name.isEmpty()) row.setOnLongClickListener(v -> { manageProject(url, project); return true; });
        if (!open) return row;
        LinearLayout box = kit.column();
        box.addView(row);
        LinearLayout inside = kit.column();
        inside.setPadding(kit.dp(8), kit.dp(2), kit.dp(8), kit.dp(8));
        inside.setBackground(kit.shape(kit.BG, 0, 14));
        if (!confirms.isEmpty()) inside.addView(note("等你确认"));
        for (AggregateSessions.Entry entry : confirms) inside.addView(talkRow(url, entry, "confirm"));
        if (!running.isEmpty()) inside.addView(note("正在运行"));
        for (AggregateSessions.Entry entry : running) inside.addView(talkRow(url, entry, entry.kind(looked)));
        if (!waiting.isEmpty()) inside.addView(note("已打开"));
        for (AggregateSessions.Entry entry : waiting) inside.addView(talkRow(url, entry, entry.kind(looked)));
        int endedShown = Math.min(TALKS, project.ended.size());
        if (endedShown > 0) inside.addView(note("历史终端"));
        for (int i = 0; i < endedShown; i++) inside.addView(talkRow(url, project.ended.get(i), "ended"));
        // what its owner hid is left out here; it is found by a search, and in the project's own page
        List<AggregateSessions.Entry> saved = new ArrayList<>();
        for (AggregateSessions.Entry entry : project.history) if (!entry.hidden || entry.live) saved.add(entry);
        int remaining = Math.max(0, TALKS - endedShown), historyShown = Math.min(remaining, saved.size());
        if (historyShown > 0) inside.addView(note("保存的对话"));
        for (int i = 0; i < historyShown; i++) inside.addView(talkRow(url, saved.get(i), saved.get(i).kind(looked)));
        int rest = saved.size() - historyShown, hiddenEnded = project.ended.size() - endedShown;
        String more = project.name.isEmpty() ? "查看这台电脑的全部对话" : rest + hiddenEnded > 0 ? "新建对话 · 查看更多记录" : "新建对话 · 进入项目";
        TextView enter = kit.bold(more, 13.5f, kit.ACCENT);
        enter.setGravity(Gravity.CENTER_VERTICAL); enter.setMinHeight(kit.dp(46)); enter.setPadding(kit.dp(8), 0, kit.dp(8), 0);
        kit.press(enter, () -> activity.open(url, "", project.name));
        inside.addView(enter);
        LinearLayout.LayoutParams inset = new LinearLayout.LayoutParams(-1, -2);
        inset.setMargins(kit.dp(10), 0, kit.dp(10), kit.dp(10));
        box.addView(inside, inset);
        return box;
    }
    /** One conversation of an unfolded project: a phone terminal is entered, a saved one is continued or taken over. */
    private View talkRow(String url, AggregateSessions.Entry entry, String kind) {
        LinearLayout row = kit.row();
        row.setMinimumHeight(kit.dp(50)); row.setPadding(kit.dp(8), kit.dp(6), kit.dp(8), kit.dp(6));
        row.addView(kit.tile(toolIcon(entry.tool), toolColor(entry.tool), 30));
        LinearLayout texts = kit.column();
        texts.setPadding(kit.dp(10), 0, kit.dp(8), 0);
        texts.addView(kit.title(entry.shownTitle(), 14.5f, "history".equals(kind) ? kit.MUTED : kit.INK, false));
        if (!entry.place().isEmpty()) texts.addView(projectTag(entry));
        row.addView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        String used = AggregateSessions.ago(AggregateSessions.recent(entry), System.currentTimeMillis());
        if ("history".equals(kind) || "ended".equals(kind)) row.addView(kit.text(used.isEmpty() ? AggregateSessions.label(kind) : used + (entry.terminal ? "结束" : ""), 12.5f, kit.MUTED));
        else {
            boolean closing = ending.contains(url + '\n' + entry.id);
            row.addView(kit.pill(closing ? "正在结束" : AggregateSessions.label(kind), closing ? kit.MUTED : color(kind)));
        }
        if (canEnd(url, entry)) {
            row.setPadding(kit.dp(8), kit.dp(6), kit.dp(2), kit.dp(6));
            View stop = stopButton(new AggregateSessions.Thing(url, "", entry, kind));
            ((LinearLayout.LayoutParams) stop.getLayoutParams()).leftMargin = kit.dp(4);
            row.addView(stop);
        }
        kit.press(row, () -> {
            if (!entry.terminal) { activity.openSession(url, entry.project, entry.id); return; }
            markSeen(url, entry.id, entry.at);
            activity.openTerminal(url, entry.id);
        });
        return row;
    }
}
