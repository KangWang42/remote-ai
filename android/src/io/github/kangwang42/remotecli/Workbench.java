package io.github.kangwang42.remotecli;

import android.graphics.Typeface;
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
 * The app's first screen. On top, the running tasks of every computer with the ones that need the user first; below,
 * each computer with its projects. Every computer is asked on its own, so one that is switched off delays nothing.
 */
final class Workbench {
    /** What one computer answered last: "on", "off" or "key" (sign in again), a line about it, and its projects. */
    private static final class Snapshot {
        final String state, line;
        final List<AggregateSessions.Project> projects;
        Snapshot(String state, String line, List<AggregateSessions.Project> projects) { this.state = state; this.line = line; this.projects = projects; }
    }
    private static final class Task {
        final String url, computer, kind;
        final AggregateSessions.Entry entry;
        Task(String url, String computer, AggregateSessions.Entry entry, String kind) { this.url = url; this.computer = computer; this.entry = entry; this.kind = kind; }
    }
    private static final int SHOWN = 4, TASKS = 12, TALKS = 8, RECENT = 5, RECENT_ALL = 20;

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
    private boolean recentOpen;         // "最近使用" shows all of its terminals, not the first few
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

    // ---- the screen
    View build(String problem) {
        JSONArray list = activity.computers();
        LinearLayout page = kit.page(22);
        LinearLayout head = kit.row();
        head.addView(mark(42));
        LinearLayout titles = kit.column();
        titles.setPadding(kit.dp(12), 0, kit.dp(6), 0);
        titles.addView(kit.line("Remote CLI", 21, kit.INK, true));
        summary = kit.line(list.length() == 0 ? "在手机上使用电脑里的终端" : "正在连接…", 13, kit.MUTED, false);
        titles.addView(summary);
        head.addView(titles, new LinearLayout.LayoutParams(0, -2, 1));
        head.addView(kit.iconButton(R.drawable.ic_look, "外观", activity::look));
        if (list.length() > 0) head.addView(kit.iconButton(R.drawable.ic_plus, "添加电脑", this::addComputer));
        page.addView(head);
        if (!problem.isEmpty()) {
            LinearLayout banner = kit.column();
            banner.setPadding(kit.dp(16), kit.dp(14), kit.dp(16), kit.dp(14));
            banner.setBackground(kit.shape(Kit.mix(kit.BAD, kit.PANEL, .09f), Kit.mix(kit.BAD, kit.LINE, .45f), 16));
            banner.addView(kit.text(problem, 14, kit.INK));
            page.addView(banner, kit.below(16));
        }
        holders.clear(); marks.clear(); attentionMark = null;
        attention = kit.column();
        page.addView(attention);
        recent = kit.column();
        page.addView(recent);
        if (list.length() == 0) {
            page.addView(welcome(), kit.below(22));
            page.addView(kit.button("扫码添加电脑", 0, activity::scan), kit.below(22));
            page.addView(kit.button("手动输入地址", 1, () -> activity.add("")), kit.below(10));
        } else {
            page.addView(heading("电脑", list.length() + " 台"), kit.below(24));
            for (int i = 0; i < list.length(); i++) {
                JSONObject computer = list.optJSONObject(i);
                if (computer == null) continue;
                LinearLayout holder = kit.column();
                holders.put(computer.optString("url"), holder);
                page.addView(holder, kit.below(10));
                drawComputer(computer);
            }
            TextView more = kit.bold("添加电脑", 15, kit.MUTED);
            more.setGravity(Gravity.CENTER); more.setMinHeight(kit.dp(54));
            GradientDrawable dashed = kit.shape(0, 0, 16);
            dashed.setStroke(kit.dp(1), kit.LINE, kit.dp(5), kit.dp(4));
            more.setBackground(dashed);
            kit.press(more, this::addComputer);
            page.addView(more, kit.below(10));
            if (!problem.isEmpty()) page.addView(kit.button("重新扫码连接电脑", 0, activity::scan), kit.below(16));
            draw();
        }
        page.addView(kit.text("凭地址和密码即可操作这台电脑上的终端，请妥善保管。", 12.5f, kit.MUTED), kit.below(22));
        TextView version = kit.text("版本 " + activity.versionName() + " · 检查更新", 13, kit.ACCENT);
        version.setMinHeight(kit.dp(44)); version.setGravity(Gravity.CENTER_VERTICAL);
        kit.press(version, activity::checkUpdate);
        page.addView(version);
        if (list.length() > 0) {
            TextView remind = kit.text(activity.watching() ? "任务提醒：已开启 · 离开 App 后，有任务等你确认或完成时通知你" : "任务提醒：未开启 · 点这里开启", 13, kit.ACCENT);
            remind.setMinHeight(kit.dp(44)); remind.setGravity(Gravity.CENTER_VERTICAL);
            kit.press(remind, activity::toggleWatch);
            page.addView(remind);
        }
        activity.message = kit.text("", 13, kit.MUTED);
        page.addView(activity.message);
        return page;
    }
    /** The app's mark: a prompt on a rounded tile. */
    private View mark(int size) {
        TextView tile = kit.bold(">_", size * .36f, kit.ACCENT);
        tile.setTypeface(Typeface.create(Typeface.MONOSPACE, Typeface.BOLD)); tile.setGravity(Gravity.CENTER);
        GradientDrawable back = new GradientDrawable(GradientDrawable.Orientation.TL_BR, new int[]{kit.RAISED, kit.PANEL});
        back.setCornerRadius(kit.dp(size * .3f)); back.setStroke(kit.dp(1), kit.LINE);
        tile.setBackground(back);
        tile.setLayoutParams(new LinearLayout.LayoutParams(kit.dp(size), kit.dp(size)));
        return tile;
    }
    private View welcome() {
        LinearLayout card = kit.column();
        card.setPadding(kit.dp(18), kit.dp(18), kit.dp(18), kit.dp(20)); card.setBackground(kit.shape(kit.PANEL, kit.LINE, 20));
        card.addView(kit.bold("连接第一台电脑", 17, kit.INK));
        String[] steps = { "在电脑上安装并打开 Remote CLI", "点下面的“扫码添加电脑”，对准电脑窗口里的二维码", "连上后选一个项目，新建 Claude Code、Codex 或 PowerShell 终端" };
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
    private boolean folded(String section) { return activity.prefs.getBoolean("fold-" + section, false); }
    private void fold(String section) { activity.prefs.edit().putBoolean("fold-" + section, !folded(section)).apply(); draw(); }
    private void addComputer() {
        kit.sheet("添加电脑", "", new String[]{"扫码添加", "手动输入地址"}, new String[]{"对准电脑端窗口里的二维码", "填写电脑端窗口里显示的地址和密码"}, -1,
            which -> { if (which == 0) activity.scan(); else activity.add(""); });
    }

    // ---- asking the computers
    /** Asks every computer what is running; repeats while this screen is shown. */
    void refresh() {
        activity.ticker.removeCallbacks(tick);
        if (!activity.showing("home")) return;
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
                    snapshots.put(url, answer == null ? new Snapshot(s, l, Collections.<AggregateSessions.Project>emptyList()) : read(url, answer));
                    if (!activity.showing("home")) return;
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
        if (device == null || !device.optBoolean("online")) return new Snapshot("off", "电脑端程序没有在运行", none);
        if (!device.optBoolean("enabled")) return new Snapshot("off", "电脑端暂停了手机访问", none);
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
                made.said = entry.optString("said");
                made.shell = device.optString("shell");
                made.used = terminal ? (made.at > 0 ? made.at : entry.optLong("created")) : entry.optLong("updated");
                entries.add(made);
            }
        }
        prune(url, alive);
        List<AggregateSessions.Project> groups = AggregateSessions.projects(names, entries);
        int[] counts = AggregateSessions.counts(groups, seen(url));
        List<String> parts = new ArrayList<>();
        if (counts[0] > 0) parts.add(counts[0] + " 个等你确认");
        if (counts[1] > 0) parts.add(counts[1] + " 个已完成");
        if (counts[2] > 0) parts.add(counts[2] + " 个在执行");
        String line = !parts.isEmpty() ? android.text.TextUtils.join(" · ", parts) : counts[3] > 0 ? "在线 · " + counts[3] + " 个终端等待输入" : "在线 · 没有任务在运行";
        return new Snapshot("on", line, groups);
    }

    // ---- drawing; a part is rebuilt only when what it shows has changed, so a press or a scroll is not interrupted
    private int color(String kind) {
        return "confirm".equals(kind) ? kit.BAD : "done".equals(kind) ? kit.GOOD : "busy".equals(kind) || "pc-busy".equals(kind) ? kit.BUSY
            : "starting".equals(kind) || "pc-idle".equals(kind) ? kit.ACCENT : kit.MUTED;
    }
    private int toolIcon(String tool) { return "claude".equals(tool) ? R.drawable.ic_claude : "codex".equals(tool) ? R.drawable.ic_codex : R.drawable.ic_shell; }
    private int toolColor(String tool) { return "claude".equals(tool) ? 0xffd98a5f : "codex".equals(tool) ? kit.ACCENT : kit.GOOD; }

    /** The summary under the title and the tasks of all computers. */
    private void draw() {
        if (attention == null) return;
        JSONArray list = activity.computers();
        List<Task> tasks = new ArrayList<>(), recentTasks = new ArrayList<>();
        int online = 0, known = 0;
        int[] total = new int[4];
        StringBuilder mark = new StringBuilder();
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
                for (AggregateSessions.Entry entry : project.active) tasks.add(new Task(url, name, entry, entry.kind(looked)));
                for (AggregateSessions.Entry entry : project.ended) recentTasks.add(new Task(url, name, entry, "ended"));
            }
        }
        if (known > 0) {
            List<String> parts = new ArrayList<>();
            parts.add(list.length() == 1 ? (online == 1 ? "电脑在线" : "电脑离线") : online + " / " + list.length() + " 台在线");
            if (total[0] > 0) parts.add(total[0] + " 个等你确认");
            if (total[1] > 0) parts.add(total[1] + " 个已完成");
            if (total[2] > 0) parts.add(total[2] + " 个在执行");
            summary.setText(android.text.TextUtils.join(" · ", parts));
            summary.setTextColor(total[0] > 0 ? kit.BAD : kit.MUTED);
        }
        Collections.sort(tasks, (a, b) -> AggregateSessions.rank(a.kind) != AggregateSessions.rank(b.kind) ? AggregateSessions.rank(a.kind) - AggregateSessions.rank(b.kind) : Long.compare(b.entry.at, a.entry.at));
        Collections.sort(recentTasks, (a, b) -> Long.compare(AggregateSessions.recent(b.entry), AggregateSessions.recent(a.entry)));
        for (Task task : tasks) mark.append(task.url).append('\n').append(task.entry.id).append('\n').append(task.kind).append('\n').append(task.entry.shownTitle()).append('\n').append(task.computer).append('\n').append(ending.contains(task.url + '\n' + task.entry.id)).append('\n');
        for (Task task : recentTasks) mark.append(task.url).append('\n').append(task.entry.id).append('\n').append(task.kind).append('\n').append(task.entry.shownTitle()).append('\n').append(task.computer).append('\n').append(AggregateSessions.ago(AggregateSessions.recent(task.entry), System.currentTimeMillis())).append('\n');
        mark.append(recentOpen).append(folded("active")).append(folded("recent"));
        if (mark.toString().equals(attentionMark)) {
            // the cards stay; only what each terminal last said is written anew
            List<Task> all = new ArrayList<>(tasks); all.addAll(recentTasks);
            for (Task task : all) {
                TextView peek = peeks.get(task.url + '\n' + task.entry.id);
                if (peek == null) continue;
                if (!task.entry.said.contentEquals(peek.getText())) peek.setText(task.entry.said);
                peek.setVisibility(task.entry.said.isEmpty() ? View.GONE : View.VISIBLE);
            }
            return;
        }
        attentionMark = mark.toString();
        peeks.clear();
        attention.removeAllViews();
        boolean several = list.length() > 1;
        if (!tasks.isEmpty()) {
            List<String> parts = new ArrayList<>();
            if (total[0] > 0) parts.add(total[0] + " 个等你确认");
            if (total[1] > 0) parts.add(total[1] + " 个已完成");
            if (total[2] > 0) parts.add(total[2] + " 个在执行");
            // Folded, the heading still says how many ask for a decision, are finished or at work.
            boolean folded = folded("active");
            attention.addView(heading("进行中", parts.isEmpty() ? tasks.size() + " 个终端在运行" : android.text.TextUtils.join(" · ", parts), folded, () -> fold("active")), kit.below(14));
            for (int i = 0; !folded && i < Math.min(TASKS, tasks.size()); i++) attention.addView(taskCard(tasks.get(i), several), kit.below(i == 0 ? 0 : 8));
            if (!folded && tasks.size() > TASKS) attention.addView(kit.text("另外 " + (tasks.size() - TASKS) + " 个在各自的项目里。", 12.5f, kit.MUTED), kit.below(8));
        }
        recent.removeAllViews();
        if (!recentTasks.isEmpty()) {
            boolean folded = folded("recent");
            recent.addView(heading("最近使用", recentTasks.size() + " 个历史终端", folded, () -> fold("recent")), kit.below(tasks.isEmpty() ? 14 : folded("active") ? 0 : 12));
            int shown = folded ? 0 : Math.min(recentOpen ? RECENT_ALL : RECENT, recentTasks.size());
            for (int i = 0; i < shown; i++) recent.addView(taskCard(recentTasks.get(i), several), kit.below(i == 0 ? 0 : 8));
            if (!folded && recentTasks.size() > RECENT) {
                TextView toggle = kit.text(recentOpen ? "只显示前 " + RECENT + " 个" : "其余 " + (recentTasks.size() - shown) + " 个", 13, kit.MUTED);
                toggle.setGravity(Gravity.CENTER); toggle.setMinHeight(kit.dp(44));
                kit.press(toggle, () -> { recentOpen = !recentOpen; draw(); });
                recent.addView(toggle, kit.below(2));
            }
            if (!folded && recentOpen && recentTasks.size() > shown) recent.addView(kit.text("另外 " + (recentTasks.size() - shown) + " 个可在对应项目中查看。", 12.5f, kit.MUTED), kit.below(4));
        }
    }
    private View taskCard(Task task, boolean several) {
        final AggregateSessions.Entry entry = task.entry;
        LinearLayout card = kit.row();
        card.setMinimumHeight(kit.dp(66)); card.setPadding(kit.dp(14), kit.dp(11), kit.dp(14), kit.dp(11));
        int edge = "confirm".equals(task.kind) ? Kit.mix(kit.BAD, kit.LINE, .55f) : "done".equals(task.kind) ? Kit.mix(kit.GOOD, kit.LINE, .45f) : kit.LINE;
        card.setBackground(kit.shape(kit.PANEL, edge, 16));
        card.addView(kit.tile(toolIcon(entry.tool), toolColor(entry.tool), 40));
        LinearLayout texts = kit.column();
        texts.setPadding(kit.dp(12), 0, 0, 0);
        texts.addView(kit.line(entry.shownTitle(), 15.5f, kit.INK, true));
        LinearLayout meta = kit.row();
        boolean closing = ending.contains(task.url + '\n' + entry.id);
        meta.addView(kit.pill(closing ? "正在结束" : AggregateSessions.label(task.kind), closing ? kit.MUTED : color(task.kind)));
        TextView where = kit.line(entry.project + (several ? " · " + task.computer : ""), 12.5f, kit.MUTED, false);
        where.setPadding(kit.dp(8), 0, 0, 0);
        meta.addView(where, new LinearLayout.LayoutParams(0, -2, 1));
        texts.addView(meta, kit.below(4));
        if (!entry.running()) {
            String when = AggregateSessions.ago(AggregateSessions.recent(entry), System.currentTimeMillis());
            if (!when.isEmpty()) texts.addView(kit.text(when + "结束", 12.5f, kit.MUTED), kit.below(4));
        }
        if (entry.terminal) {
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
            card.addView(stopButton(task));
        }
        kit.press(card, () -> {
            if (!entry.terminal) { activity.openSession(task.url, entry.project, entry.id); return; }
            markSeen(task.url, entry.id, entry.at);
            activity.openTerminal(task.url, entry.id);
        });
        return card;
    }

    private View stopButton(Task task) {
        boolean busy = ending.contains(task.url + '\n' + task.entry.id);
        View button = kit.discButton(R.drawable.ic_stop, busy ? "正在结束“" + task.entry.shownTitle() + "”" : "结束“" + task.entry.shownTitle() + "”", () -> stopTask(task));
        button.setEnabled(!busy); button.setAlpha(busy ? .4f : 1f);
        return button;
    }

    // ---- a message for a running terminal, typed here and sent without going into it
    private View sayButton(Task task) {
        return kit.discButton(R.drawable.ic_say, "给“" + task.entry.shownTitle() + "”发消息", () -> sayTask(task));
    }
    private void sayTask(Task task) {
        // what the program said last is shown, to tell which terminal this is and where it stands
        kit.write(task.entry.shownTitle(), task.entry.said.isEmpty() ? task.entry.project + " · " + AggregateSessions.label(task.kind) : task.entry.said,
            "发送消息", "发送", text -> sayRemote(task, text));
    }
    /** The relay puts the message the way the program takes pasted text and adds Enter (the operation "say"). */
    private void sayRemote(Task task, String text) {
        final String cookie = CookieManager.getInstance().getCookie(task.url);
        activity.net.execute(() -> {
            String problem = "";
            try {
                JSONObject op = new JSONObject().put("action", "say").put("id", UUID.randomUUID().toString().replace("-", "")).put("terminal", task.entry.id).put("text", text);
                JSONObject result = null;
                // the same operation asked again is the same one: a request that did not get through is made once more
                for (int attempt = 0; result == null && attempt < 3; attempt++) {
                    try { result = post(task.url + "/api/terminal", cookie, op); }
                    catch (IOException unreachable) { Thread.sleep(500); }
                }
                if (result == null) throw new Exception("连不上这台电脑，消息没有发出");
                if ("error".equals(result.optString("state"))) throw new Exception(result.optString("error", "消息没有发出"));
            } catch (Exception error) {
                String said = error.getMessage();
                // a relay of an earlier version answers so to anything it does not know
                problem = "不支持此操作".equals(said) ? "这台电脑的电脑端或中转版本较早，暂不支持在此发消息，请更新后再试" : said == null ? "消息没有发出，请稍后重试" : said;
            }
            final String message = problem;
            activity.runOnUiThread(() -> {
                android.widget.Toast.makeText(activity, message.isEmpty() ? "已发送" : message, android.widget.Toast.LENGTH_SHORT).show();
                refresh();
            });
        });
    }

    private void redraw() {
        if (!activity.showing("home")) return;
        JSONArray list = activity.computers();
        for (int i = 0; i < list.length(); i++) { JSONObject computer = list.optJSONObject(i); if (computer != null) drawComputer(computer); }
        draw();
    }

    private void stopTask(Task task) {
        final String key = task.url + '\n' + task.entry.id;
        if (ending.contains(key)) return;
        kit.sheet("结束“" + task.entry.shownTitle() + "”？", "正在执行的任务会被中断，历史终端仍会保留。", new String[]{"结束终端"}, new String[]{"结束后会从进行中移到最近使用。"}, 0,
            which -> { if (which == 0 && ending.add(key)) { redraw(); closeRemote(task, key); } });
    }
    private void closeRemote(Task task, String key) {
        final String cookie = CookieManager.getInstance().getCookie(task.url);
        activity.net.execute(() -> {
            String problem = "";
            try {
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
            } catch (Exception error) { problem = error.getMessage() == null ? "结束终端失败，请稍后重试" : error.getMessage(); }
            final String message = problem;
            activity.runOnUiThread(() -> { ending.remove(key); if (!message.isEmpty()) android.widget.Toast.makeText(activity, message, android.widget.Toast.LENGTH_SHORT).show(); redraw(); refresh(); });
        });
    }
    private JSONObject post(String address, String cookie, JSONObject payload) throws Exception {
        HttpURLConnection connection = (HttpURLConnection) new URL(address).openConnection();
        boolean whole = false;
        try {
            connection.setConnectTimeout(4000); connection.setReadTimeout(8000); connection.setUseCaches(false); connection.setDoOutput(true);
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
        String mark = name + '\n' + expanded.contains(url) + '\n' + collapsed.contains(url) + '\n' + unfolded + '\n' + ending + '\n' + (snapshot == null ? "" : snapshot.state + '\n' + snapshot.line + '\n' + AggregateSessions.signature(snapshot.projects, looked));
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
            List<AggregateSessions.Project> order = new ArrayList<>();
            for (AggregateSessions.Project project : snapshot.projects) if (!project.active.isEmpty()) order.add(project);
            int busy = order.size();
            for (AggregateSessions.Project project : snapshot.projects) if (project.active.isEmpty() && !project.ended.isEmpty()) order.add(project);
            for (AggregateSessions.Project project : snapshot.projects) if (project.active.isEmpty() && project.ended.isEmpty()) order.add(project);
            boolean all = expanded.contains(url);
            int shown = all ? order.size() : Math.max(busy, Math.min(SHOWN, order.size()));
            for (int i = 0; i < shown; i++) { card.addView(rule()); card.addView(projectRow(computer, order.get(i), looked)); }
            if (order.isEmpty()) { card.addView(rule()); card.addView(note("还没有项目。在电脑端的“项目”页添加文件夹。")); }
            else {
                // A terminal is started from here without walking to a project first.
                card.addView(rule());
                TextView fresh = kit.bold("＋ 新建终端", 13.5f, kit.ACCENT);
                fresh.setGravity(Gravity.CENTER); fresh.setMinHeight(kit.dp(46));
                kit.press(fresh, () -> activity.startNew(url));
                card.addView(fresh);
            }
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
            help.addView(kit.text("key".equals(snapshot.state) ? "登录已过期。" : "请确认电脑已开机且 Remote CLI 正在运行。退出电脑端或重启电脑后，公网隧道的地址会变更。", 12.5f, kit.MUTED), new LinearLayout.LayoutParams(0, -2, 1));
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
        int need = 0, done = 0, work = 0;
        for (AggregateSessions.Entry entry : project.active) {
            String kind = entry.kind(looked);
            if ("confirm".equals(kind)) need++;
            else if ("done".equals(kind)) done++;
            else if ("busy".equals(kind) || "starting".equals(kind) || "pc-busy".equals(kind)) work++;
        }
        boolean open = unfolded.contains(key);
        LinearLayout row = kit.row();
        row.setMinimumHeight(kit.dp(52)); row.setPadding(kit.dp(16), kit.dp(8), kit.dp(12), kit.dp(8));
        row.addView(kit.icon(R.drawable.ic_folder, open ? kit.ACCENT : kit.MUTED, 20));
        TextView name = kit.line(project.name, 15, kit.INK, open);
        name.setPadding(kit.dp(12), 0, kit.dp(8), 0);
        row.addView(name, new LinearLayout.LayoutParams(0, -2, 1));
        // each state keeps its own colour here too: red asks for a decision, green is finished work not yet looked at
        int[] counts = { need, done, work }, shades = { kit.BAD, kit.GOOD, kit.BUSY };
        String[] says = { " 个等你确认", " 个已完成", " 个在执行" };
        boolean first = true;
        for (int i = 0; i < 3; i++) {
            if (counts[i] == 0) continue;
            LinearLayout.LayoutParams gap = new LinearLayout.LayoutParams(-2, -2);
            gap.leftMargin = first ? 0 : kit.dp(6);
            row.addView(kit.pill(counts[i] + says[i], shades[i]), gap);
            first = false;
        }
        if (first) row.addView(kit.text(!project.active.isEmpty() ? project.active.size() + " 个终端开着" : !project.ended.isEmpty() ? project.ended.size() + " 个历史终端" : project.history.isEmpty() ? "还没有对话" : project.history.size() + " 段对话", 12.5f, kit.MUTED));
        View chevron = kit.icon(R.drawable.ic_chevron, Kit.tint(kit.MUTED, 150), 16);
        ((LinearLayout.LayoutParams) chevron.getLayoutParams()).leftMargin = kit.dp(6);
        chevron.setRotation(open ? 90 : 0);
        row.addView(chevron);
        kit.press(row, () -> { if (!unfolded.remove(key)) unfolded.add(key); drawComputer(computer); });
        if (!open) return row;
        LinearLayout box = kit.column();
        box.addView(row);
        LinearLayout inside = kit.column();
        inside.setPadding(kit.dp(8), kit.dp(2), kit.dp(8), kit.dp(8));
        inside.setBackground(kit.shape(kit.BG, 0, 14));
        if (!project.active.isEmpty()) inside.addView(note("进行中"));
        for (AggregateSessions.Entry entry : project.active) inside.addView(talkRow(url, entry, entry.kind(looked)));
        int endedShown = Math.min(TALKS, project.ended.size());
        if (endedShown > 0) inside.addView(note("历史终端"));
        for (int i = 0; i < endedShown; i++) inside.addView(talkRow(url, project.ended.get(i), "ended"));
        int remaining = Math.max(0, TALKS - endedShown), historyShown = Math.min(remaining, project.history.size());
        if (historyShown > 0) inside.addView(note("保存的对话"));
        for (int i = 0; i < historyShown; i++) inside.addView(talkRow(url, project.history.get(i), project.history.get(i).kind(looked)));
        int rest = project.history.size() - historyShown, hiddenEnded = project.ended.size() - endedShown;
        String more = rest + hiddenEnded > 0 ? "新建终端 · 查看更多记录" : "新建终端 · 进入项目";
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
        TextView title = kit.line(entry.shownTitle(), 14.5f, "history".equals(kind) ? kit.MUTED : kit.INK, false);
        title.setPadding(kit.dp(10), 0, kit.dp(8), 0);
        row.addView(title, new LinearLayout.LayoutParams(0, -2, 1));
        String used = AggregateSessions.ago(AggregateSessions.recent(entry), System.currentTimeMillis());
        if ("history".equals(kind) || "ended".equals(kind)) row.addView(kit.text(used.isEmpty() ? AggregateSessions.label(kind) : used + (entry.terminal ? "结束" : ""), 12.5f, kit.MUTED));
        else {
            boolean closing = ending.contains(url + '\n' + entry.id);
            row.addView(kit.pill(closing ? "正在结束" : AggregateSessions.label(kind), closing ? kit.MUTED : color(kind)));
        }
        if (entry.terminal && "running".equals(entry.state)) {
            row.setPadding(kit.dp(8), kit.dp(6), kit.dp(2), kit.dp(6));
            View stop = stopButton(new Task(url, "", entry, kind));
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
