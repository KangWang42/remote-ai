package io.github.kangwang42.remotecli;

import android.graphics.drawable.GradientDrawable;
import android.view.Gravity;
import android.view.View;
import android.webkit.CookieManager;
import android.widget.EditText;
import android.widget.FrameLayout;
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
 * Two of the app's four lists, both over every computer. 任务, the app's first screen: computer status pill,
 * 3-column KPI bar, tool filter chips, hero running/confirm cards, and grouped session cards, with search.
 * 项目: each computer as a dark hero status card with its grouped active projects and collapsible session drawers.
 */
final class Workbench {
    /** What one computer answered last: "on", "off" or "key" (sign in again), a line about it, and its projects. */
    private static final class Snapshot {
        final String state, line;
        final List<AggregateSessions.Project> projects;
        final boolean marks;        // the computer keeps what is said of single conversations: pinned, hidden
        final Set<String> can = new HashSet<>();        // what else the computer says it does: "delete", "projects"
        final List<Long> agyStarts = new ArrayList<>();
        final List<QuotaLines.Reading> quotas = new ArrayList<>();      // how much of each tool's limits is used (device.quotas)
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
    private final Set<String> pending = new HashSet<>(), ending = new HashSet<>(), expanded = new HashSet<>(), collapsed = new HashSet<>(), unfolded = new HashSet<>();
    private final Runnable tick = this::refresh;
    private JSONObject seen;
    private LinearLayout attention, recent, statusPillHost;
    private LinearLayout[] metricCells;
    private TextView[] metricLabels, metrics;
    private View[] metricDots;
    private TextView summary, pillName, pillState;
    private View pillDotWrap;
    private TextView external;
    private EditText searchBox;
    private boolean searchOpen;
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
        LinearLayout page = kit.page(16);
        holders.clear(); marks.clear(); attentionMark = null; filtersMark = null; filters = null; attention = null; recent = null;
        metrics = null; metricCells = null; metricLabels = null; metricDots = null; external = null;
        summary = null; statusPillHost = null; pillName = null; pillState = null; pillDotWrap = null; searchBox = null;

        if (tasks) {
            if (list.length() > 0) {
                // Top utility row: Computer status pill on left, Search & New Task circular buttons on right
                LinearLayout topBar = kit.row();
                statusPillHost = buildStatusPill(list);
                LinearLayout pillHolder = kit.row();
                pillHolder.addView(statusPillHost);
                topBar.addView(pillHolder, new LinearLayout.LayoutParams(0, -2, 1));
                View searchBtn = kit.circleAction(R.drawable.ic_search, "搜索对话、终端和项目", false, this::toggleSearch);
                topBar.addView(searchBtn);
                View addBtn = kit.circleAction(R.drawable.ic_plus, "新建对话", true, this::newConversation);
                LinearLayout.LayoutParams addLp = (LinearLayout.LayoutParams) addBtn.getLayoutParams();
                addLp.leftMargin = kit.dp(2);
                topBar.addView(addBtn, addLp);
                page.addView(topBar);

                TextView heroTitle = kit.bold("任务", 31, kit.INK);
                heroTitle.setIncludeFontPadding(false);
                heroTitle.setLetterSpacing(-0.02f);
                page.addView(heroTitle, kit.below(10));
            } else {
                summary = kit.standing("还没有添加电脑");
                page.addView(kit.head("任务", summary));
            }
        } else {
            summary = kit.standing(list.length() == 0 ? "还没有添加电脑" : "正在连接…");
            View addBtn = list.length() == 0 ? null : kit.circleAction(R.drawable.ic_plus, "添加电脑", true, this::addComputer);
            page.addView(kit.head("项目", list.length() == 0 ? summary : null, addBtn));
        }

        if (!problem.isEmpty()) {
            LinearLayout banner = kit.column();
            banner.setPadding(kit.dp(16), kit.dp(14), kit.dp(16), kit.dp(14));
            banner.setBackground(kit.shape(kit.badSoft(), Kit.mix(kit.BAD, kit.LINE, .45f), 18));
            banner.addView(kit.text(problem, 14, kit.INK));
            page.addView(banner, kit.below(14));
        }

        if (list.length() == 0) {
            page.addView(welcome(), kit.below(20));
            page.addView(kit.button("扫码添加电脑", 0, activity::scan), kit.below(20));
            page.addView(kit.button("手动输入地址", 1, () -> activity.add("")), kit.below(10));
        } else if (tasks) {
            // 3-column segmented KPI bar matching the design
            LinearLayout monitorCard = kit.cardBox(20);
            LinearLayout monitor = kit.row();
            monitor.setPadding(kit.dp(4), kit.dp(4), kit.dp(4), kit.dp(4));
            String[] labels = {"待确认", "执行中", "已打开"};
            final String[] foldTargets = {null, "active", "opened"};
            metrics = new TextView[labels.length];
            metricCells = new LinearLayout[labels.length];
            metricLabels = new TextView[labels.length];
            metricDots = new View[labels.length];
            for (int i = 0; i < labels.length; i++) {
                LinearLayout cell = kit.column();
                cell.setPadding(kit.dp(12), kit.dp(10), kit.dp(10), kit.dp(10));
                cell.setBackground(kit.shape(0, 0, 16));
                LinearLayout labelRow = kit.row();
                View dot = kit.dot(kit.busyDot(), 6);
                LinearLayout.LayoutParams dotLp = (LinearLayout.LayoutParams) dot.getLayoutParams();
                dotLp.rightMargin = kit.dp(6);
                dot.setVisibility(View.GONE);
                labelRow.addView(dot, dotLp);
                TextView lbl = kit.text(labels[i], 12.5f, kit.MUTED);
                lbl.setSingleLine(true);
                labelRow.addView(lbl);
                cell.addView(labelRow);
                metrics[i] = kit.figure("—", 24, kit.MUTED);
                cell.addView(metrics[i], kit.below(2));
                metricCells[i] = cell;
                metricLabels[i] = lbl;
                metricDots[i] = dot;
                final String target = foldTargets[i];
                if (target != null) {
                    kit.press(cell, () -> {
                        if (folded(target)) { activity.prefs.edit().putBoolean("fold-" + target, false).apply(); draw(); }
                    });
                }
                monitor.addView(cell, new LinearLayout.LayoutParams(0, -2, 1));
            }
            monitorCard.addView(monitor);
            external = kit.medium("", 13, kit.accentInk());
            external.setMinHeight(kit.dp(48)); external.setGravity(Gravity.CENTER_VERTICAL);
            external.setPadding(kit.dp(16), kit.dp(6), kit.dp(16), kit.dp(6));
            external.setBackgroundColor(kit.accentSoft());
            external.setVisibility(View.GONE);
            monitorCard.addView(external);
            page.addView(monitorCard, kit.below(18));

            // Inline search bar (revealed by Search circle button, or stays open while query is active)
            page.setFocusable(true); page.setFocusableInTouchMode(true);
            searchBox = kit.field("搜索对话、终端和项目", android.text.InputType.TYPE_CLASS_TEXT);
            searchBox.setMinHeight(kit.dp(48)); searchBox.setTextSize(15); searchBox.setText(query);
            searchBox.setBackground(kit.shape(kit.PANEL, kit.cardBorder(), 24));
            android.graphics.drawable.Drawable searchIcon = activity.getDrawable(R.drawable.ic_search);
            if (searchIcon != null) {
                searchIcon = searchIcon.mutate();
                searchIcon.setTint(kit.MUTED);
                searchIcon.setBounds(0, 0, kit.dp(18), kit.dp(18));
                searchBox.setCompoundDrawables(searchIcon, null, null, null);
                searchBox.setCompoundDrawablePadding(kit.dp(10));
            }
            searchBox.setImeOptions(android.view.inputmethod.EditorInfo.IME_ACTION_SEARCH);
            searchBox.addTextChangedListener(new android.text.TextWatcher() {
                @Override public void beforeTextChanged(CharSequence text, int start, int count, int after) { }
                @Override public void onTextChanged(CharSequence text, int start, int before, int count) { }
                @Override public void afterTextChanged(android.text.Editable text) { query = text.toString(); draw(); }
            });
            searchBox.setVisibility(searchOpen || !query.isEmpty() ? View.VISIBLE : View.GONE);
            page.addView(searchBox, kit.below(12));

            // Horizontal agent monogram filter chips
            filters = kit.row();
            android.widget.HorizontalScrollView across = new android.widget.HorizontalScrollView(activity);
            across.setHorizontalScrollBarEnabled(false); across.addView(filters);
            page.addView(across, kit.below(12));

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
                page.addView(holder, kit.below(i == 0 ? 18 : 16));
                drawComputer(computer);
            }
            TextView more = kit.semibold("＋ 添加电脑", 14.5f, kit.MUTED);
            more.setGravity(Gravity.CENTER); more.setMinHeight(kit.dp(54));
            GradientDrawable dashed = kit.shape(kit.PANEL, 0, 20);
            dashed.setStroke(kit.dp(1), kit.LINE, kit.dp(5), kit.dp(4));
            more.setBackground(dashed);
            kit.press(more, this::addComputer);
            page.addView(more, kit.below(14));
            page.addView(kit.text("凭地址和密码即可操作这台电脑上的终端，请妥善保管。", 12.5f, kit.MUTED), kit.below(18));
        }
        activity.message = kit.text("", 13, kit.MUTED);
        page.addView(activity.message);
        draw();
        return page;
    }

    private LinearLayout buildStatusPill(JSONArray list) {
        LinearLayout wrap = kit.row();
        wrap.setMinimumHeight(kit.dp(48));
        LinearLayout pill = kit.row();
        pill.setMinimumHeight(kit.dp(42));
        pill.setPadding(kit.dp(12), kit.dp(6), kit.dp(12), kit.dp(6));
        pill.setBackground(kit.shape(kit.PANEL, kit.cardBorder(), 99));
        pillDotWrap = kit.haloDot(kit.MUTED, kit.RAISED);
        LinearLayout.LayoutParams dotLp = (LinearLayout.LayoutParams) pillDotWrap.getLayoutParams();
        dotLp.rightMargin = kit.dp(8);
        pill.addView(pillDotWrap, dotLp);

        String first = list.length() == 1 ? list.optJSONObject(0).optString("name", MainActivity.host(list.optJSONObject(0).optString("url"))) : list.length() + " 台电脑";
        pillName = kit.semibold(first, 13.5f, kit.INK);
        pillName.setSingleLine(true);
        pillName.setEllipsize(android.text.TextUtils.TruncateAt.END);
        pillName.setMaxWidth(kit.dp(135));
        pill.addView(pillName);

        pillState = kit.medium("连接中", 12.5f, kit.MUTED);
        pillState.setSingleLine(true);
        LinearLayout.LayoutParams stLp = new LinearLayout.LayoutParams(-2, -2);
        stLp.leftMargin = kit.dp(6);
        stLp.rightMargin = kit.dp(4);
        pill.addView(pillState, stLp);

        View down = kit.icon(R.drawable.ic_chevron, kit.MUTED, 14);
        down.setRotation(90);
        pill.addView(down);
        wrap.addView(pill);
        kit.press(wrap, () -> {
            JSONArray saved = activity.computers();
            if (saved.length() == 1) activity.manage(saved.optJSONObject(0).optString("url"));
            else activity.appPage("projects");
        });
        return wrap;
    }

    private void toggleSearch() {
        if (searchBox == null) return;
        if (searchBox.getVisibility() == View.VISIBLE && query.isEmpty()) {
            searchOpen = false;
            searchBox.setVisibility(View.GONE);
            searchBox.clearFocus();
        } else {
            searchOpen = true;
            searchBox.setVisibility(View.VISIBLE);
            searchBox.requestFocus();
            android.view.inputmethod.InputMethodManager imm = (android.view.inputmethod.InputMethodManager) activity.getSystemService(android.content.Context.INPUT_METHOD_SERVICE);
            if (imm != null) imm.showSoftInput(searchBox, android.view.inputmethod.InputMethodManager.SHOW_IMPLICIT);
        }
    }

    private View welcome() {
        LinearLayout card = kit.cardBox(22);
        card.setPadding(kit.dp(18), kit.dp(18), kit.dp(18), kit.dp(20));
        card.addView(kit.bold("连接第一台电脑", 17, kit.INK));
        String[] steps = { "在电脑上安装并打开 Remote AI", "点下面的“扫码添加电脑”，对准电脑窗口里的二维码", "连上后选一个项目，新建对话" };
        for (int i = 0; i < steps.length; i++) {
            LinearLayout row = new LinearLayout(activity);
            TextView number = kit.bold(String.valueOf(i + 1), 12.5f, kit.accentInk());
            number.setGravity(Gravity.CENTER); number.setBackground(kit.shape(kit.accentSoft(), 0, 12));
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
        row.setPadding(kit.dp(4), 0, kit.dp(4), 0);
        row.addView(kit.semibold(title, 15, kit.INK), new LinearLayout.LayoutParams(0, -2, 1));
        row.addView(kit.text(note, 13, kit.MUTED));
        return row;
    }
    /** The heading of a section that folds away: the whole row is pressed, and the line beside the title stays. */
    private View heading(String title, String note, boolean folded, Runnable toggle) {
        LinearLayout row = (LinearLayout) heading(title, note);
        row.setMinimumHeight(kit.dp(48));
        View chevron = kit.icon(R.drawable.ic_chevron, Kit.tint(kit.MUTED, 160), 16);
        ((LinearLayout.LayoutParams) chevron.getLayoutParams()).leftMargin = kit.dp(6);
        chevron.setRotation(folded ? 0 : 90);
        row.addView(chevron);
        row.setContentDescription((folded ? "展开" : "收起") + title + "，" + note);
        kit.press(row, toggle);
        return row;
    }
    // A folded section stays folded the next time the app is opened.
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
                    if (activity.showing("me")) activity.appPages.drawQuotas();
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
                made.chat = terminal && "chat".equals(entry.optString("form"));
                made.now = terminal || entry.optBoolean("now", true);
                if (!terminal) { made.origin = entry.optString("origin"); made.kind = entry.optString("kind"); made.pinned = entry.optBoolean("pinned"); made.hidden = entry.optBoolean("hidden"); }
                for (int way = 0; reach != null && way < reach.length(); way++) made.canSay |= "say".equals(reach.optString(way));
                made.shell = device.optString("shell");
                made.used = terminal ? (made.at > 0 ? made.at : entry.optLong("created")) : entry.optLong("updated");
                entries.add(made);
            }
        }
        prune(url, alive);
        Set<String> pinned = new HashSet<>();
        for (AggregateSessions.Entry entry : entries) if (!entry.terminal && entry.pinned) pinned.add(entry.tool + ':' + entry.id);
        for (AggregateSessions.Entry entry : entries) if (entry.terminal && !entry.session.isEmpty() && pinned.contains(entry.tool + ':' + entry.session)) entry.pinned = true;
        List<AggregateSessions.Project> groups = AggregateSessions.projects(names, entries);
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
        JSONArray starts = device.optJSONArray("agy_external");
        for (int i = 0; starts != null && i < starts.length(); i++) {
            long at = starts.optLong(i);
            if (at > 0) read.agyStarts.add(at);
        }
        for (int i = 0; features != null && i < features.length(); i++) read.can.add(features.optString(i));
        JSONArray quotas = device.optJSONArray("quotas");
        for (int i = 0; quotas != null && i < quotas.length(); i++) {
            JSONObject one = quotas.optJSONObject(i);
            if (one == null) continue;
            List<QuotaLines.Window> windows = new ArrayList<>();
            JSONArray told = one.optJSONArray("windows");
            for (int j = 0; told != null && j < told.length(); j++) {
                JSONObject w = told.optJSONObject(j);
                if (w != null) windows.add(new QuotaLines.Window(w.optString("name"), w.optDouble("used", 0), w.optLong("resets"), w.optString("group", "")));
            }
            read.quotas.add(new QuotaLines.Reading(one.optString("tool"), one.optLong("at"), one.optString("why"), windows));
        }
        return read;
    }
    /** What the computers that answer tell of the tools' limits: of each tool the newest. */
    List<QuotaLines.Reading> quotas() {
        List<QuotaLines.Reading> all = new ArrayList<>();
        for (Snapshot one : snapshots.values()) all.addAll(one.quotas);
        return QuotaLines.newest(all, System.currentTimeMillis() / 1000);
    }

    private void draw() {
        JSONArray list = activity.computers();
        List<AggregateSessions.Thing> things = new ArrayList<>();
        int online = 0, known = 0;
        List<String> agyProcesses = new ArrayList<>();
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
            for (long at : snapshot.agyStarts) agyProcesses.add(name + " · " +
                java.text.DateFormat.getDateTimeInstance(java.text.DateFormat.SHORT, java.text.DateFormat.SHORT).format(new java.util.Date(at)) + " 启动");
            Map<String, Long> looked = seen(url);
            int[] counts = AggregateSessions.counts(snapshot.projects, looked);
            for (int k = 0; k < 4; k++) total[k] += counts[k];
            for (AggregateSessions.Project project : snapshot.projects) {
                for (AggregateSessions.Entry entry : project.active) things.add(new AggregateSessions.Thing(url, name, entry, entry.kind(looked)));
                for (AggregateSessions.Entry entry : project.ended) things.add(new AggregateSessions.Thing(url, name, entry, "ended"));
                for (AggregateSessions.Entry entry : project.history) if (!entry.live) things.add(new AggregateSessions.Thing(url, name, entry, "history"));
            }
        }
        if (known > 0 && summary != null) {
            List<String> parts = new ArrayList<>();
            parts.add(list.length() == 1 ? (online == 1 ? "电脑在线" : "电脑离线") : online + " / " + list.length() + " 台在线");
            kit.stands(summary, android.text.TextUtils.join(" · ", parts), total[0] > 0 ? kit.BAD : kit.MUTED, online > 0 ? kit.goodDot() : kit.BAD);
        }
        if (known > 0 && pillState != null && pillDotWrap != null) {
            boolean anyOnline = online > 0;
            String label = list.length() == 1 ? (anyOnline ? "在线" : "离线") : online + "/" + list.length() + " 在线";
            pillState.setText(label);
            pillState.setTextColor(anyOnline ? kit.goodInk() : kit.BAD);
            pillDotWrap.setBackground(kit.shape(anyOnline ? kit.goodSoft() : kit.badSoft(), 0, 7));
            ((FrameLayout) pillDotWrap).getChildAt(0).setBackground(kit.shape(anyOnline ? kit.goodDot() : kit.BAD, 0, 4));
        }
        if (metrics != null) {
            int[] counts = {total[0], total[2], total[1]};
            for (int i = 0; i < metrics.length; i++) {
                boolean active = online > 0 && counts[i] > 0;
                metrics[i].setText(online == 0 ? "—" : String.valueOf(counts[i]));
                if (i == 0) {
                    metricCells[i].setBackground(kit.shape(active ? kit.badSoft() : 0, 0, 16));
                    metricLabels[i].setTextColor(active ? kit.BAD : kit.MUTED);
                    metrics[i].setTextColor(active ? kit.BAD : Kit.tint(kit.MUTED, 155));
                    metricDots[i].setBackground(kit.shape(kit.BAD, 0, 3));
                    metricDots[i].setVisibility(active ? View.VISIBLE : View.GONE);
                } else if (i == 1) {
                    metricCells[i].setBackground(kit.shape(active ? kit.busySoft() : 0, 0, 16));
                    metricLabels[i].setTextColor(active ? kit.busyInk() : kit.MUTED);
                    metrics[i].setTextColor(active ? kit.busyInk() : Kit.tint(kit.MUTED, 155));
                    metricDots[i].setBackground(kit.shape(kit.busyDot(), 0, 3));
                    metricDots[i].setVisibility(active ? View.VISIBLE : View.GONE);
                } else {
                    metricCells[i].setBackground(kit.shape(0, 0, 16));
                    metricLabels[i].setTextColor(kit.MUTED);
                    metrics[i].setTextColor(active ? kit.INK : Kit.tint(kit.MUTED, 155));
                    metricDots[i].setVisibility(View.GONE);
                }
            }
            external.setVisibility(agyProcesses.isEmpty() ? View.GONE : View.VISIBLE);
            if (!agyProcesses.isEmpty()) {
                external.setText("电脑上的 Antigravity CLI · " + agyProcesses.size() + " 个进程  ›");
                kit.press(external, () -> kit.sheet("电脑上的 Antigravity CLI",
                    android.text.TextUtils.join("\n", agyProcesses) + "\n\n独立进程仅显示启动时间。要在手机上输入，请从项目新建 Antigravity 终端。",
                    new String[]{"关闭"}, null, -1, which -> {}));
            }
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
            attention.addView(heading("搜索结果", board.found.isEmpty() ? "没有找到" : board.found.size() + " 个"), kit.below(14));
            if (!board.found.isEmpty()) {
                LinearLayout group = kit.cardBox(22);
                int count = Math.min(FOUND, board.found.size());
                for (int i = 0; i < count; i++) {
                    if (i > 0) group.addView(kit.insetDivider(66));
                    group.addView(compactTaskRow(board.found.get(i), several));
                }
                attention.addView(group, kit.below(10));
            }
            if (board.found.size() > FOUND) attention.addView(kit.text("另外 " + (board.found.size() - FOUND) + " 个没有列出，再输几个字可以找到。", 12.5f, kit.MUTED), kit.below(8));
            return;
        }
        heroSection(attention, "等你确认", board.needs.size() + " 个", board.needs, board.needs.size(), several, null);
        heroSection(attention, "正在运行", board.active.size() + " 个", board.active, TASKS, several, "active");
        if (!folded("active") && board.active.size() > TASKS) attention.addView(kit.text("另外 " + (board.active.size() - TASKS) + " 个在各自的项目里。", 12.5f, kit.MUTED), kit.below(8));
        groupedSection(attention, "已打开", "等待输入的排在前面", board.opened, TASKS, several, "opened", null);
        if (!folded("opened") && board.opened.size() > TASKS) attention.addView(kit.text("另外 " + (board.opened.size() - TASKS) + " 个可以搜索到，也在对应的项目里。", 12.5f, kit.MUTED), kit.below(8));
        groupedSection(attention, "置顶记录", board.pinned.size() + " 个", board.pinned, board.pinned.size(), several, null, null);
        int shown = Math.min(recentOpen ? RECENT_ALL : RECENT, board.recent.size());
        Runnable moreRecent = board.recent.size() > RECENT ? () -> { recentOpen = !recentOpen; draw(); } : null;
        String moreLabel = recentOpen ? "只显示前 " + RECENT + " 个" : "显示另外 " + (board.recent.size() - shown) + " 个";
        groupedSection(recent, "最近", board.recent.size() + " 个", board.recent, shown, several, "recent", moreRecent != null ? new Object[]{moreLabel, moreRecent} : null);
        if (!board.recent.isEmpty() && !folded("recent") && recentOpen && board.recent.size() > shown) {
            recent.addView(kit.text("另外 " + (board.recent.size() - shown) + " 个可以搜索到，也在对应的项目里。", 12.5f, kit.MUTED), kit.below(6));
        }
        if (attention.getChildCount() > 0 || recent.getChildCount() > 0) return;
        if (!tool.isEmpty() || !source.isEmpty()) {
            LinearLayout empty = kit.cardBox(22);
            empty.setPadding(kit.dp(20), kit.dp(28), kit.dp(20), kit.dp(28));
            empty.setGravity(Gravity.CENTER_HORIZONTAL);
            empty.addView(kit.medium("这里暂时没有会话", 14.5f, kit.INK));
            empty.addView(kit.text("没有符合当前筛选的对话或终端", 12.5f, kit.MUTED), kit.below(4));
            attention.addView(empty, kit.below(16));
        } else if (online > 0) {
            LinearLayout empty = kit.cardBox(22);
            empty.setPadding(kit.dp(20), kit.dp(32), kit.dp(20), kit.dp(28));
            empty.setGravity(Gravity.CENTER_HORIZONTAL);
            empty.addView(kit.semibold("还没有对话", 15.5f, kit.INK));
            empty.addView(kit.text("新建一段对话，或在电脑上打开 AI 工具即可同步到这里", 12.5f, kit.MUTED), kit.below(4));
            LinearLayout.LayoutParams middle = new LinearLayout.LayoutParams(-2, -2);
            middle.gravity = Gravity.CENTER_HORIZONTAL; middle.topMargin = kit.dp(16);
            empty.addView(kit.button("新建对话", 0, this::newConversation), middle);
            attention.addView(empty, kit.below(18));
        } else if (known == list.length()) {
            LinearLayout empty = kit.cardBox(22);
            empty.setPadding(kit.dp(20), kit.dp(32), kit.dp(20), kit.dp(28));
            empty.setGravity(Gravity.CENTER_HORIZONTAL);
            empty.addView(kit.semibold(list.length() == 1 ? "电脑不在线" : "电脑都不在线", 15.5f, kit.INK));
            LinearLayout.LayoutParams middle = new LinearLayout.LayoutParams(-2, -2);
            middle.gravity = Gravity.CENTER_HORIZONTAL; middle.topMargin = kit.dp(14);
            empty.addView(kit.button("查看电脑", 1, () -> activity.appPage("projects")), middle);
            attention.addView(empty, kit.below(18));
        }
    }

    /** Hero cards for active/confirm tasks (separate elevated 22dp cards with live status strip + actions). */
    private void heroSection(LinearLayout into, String title, String note, List<AggregateSessions.Thing> things, int most, boolean several, String fold) {
        if (things.isEmpty()) return;
        boolean folded = fold != null && folded(fold);
        into.addView(fold == null ? heading(title, note) : heading(title, note, folded, () -> fold(fold)), kit.below(into.getChildCount() == 0 ? 16 : 20));
        for (int i = 0; !folded && i < Math.min(most, things.size()); i++) {
            into.addView(heroTaskCard(things.get(i), several), kit.below(i == 0 && fold != null ? 2 : 10));
        }
    }

    /** Unified 22dp white container card for opened/pinned/recent sessions, with crisp dividers. */
    private void groupedSection(LinearLayout into, String title, String note, List<AggregateSessions.Thing> things, int most, boolean several, String fold, Object[] footer) {
        if (things.isEmpty()) return;
        boolean folded = fold != null && folded(fold);
        into.addView(fold == null ? heading(title, note) : heading(title, note, folded, () -> fold(fold)), kit.below(into.getChildCount() == 0 ? 16 : 20));
        if (folded) return;
        LinearLayout group = kit.cardBox(22);
        int count = Math.min(most, things.size());
        for (int i = 0; i < count; i++) {
            if (i > 0) group.addView(kit.insetDivider(66));
            group.addView(compactTaskRow(things.get(i), several));
        }
        if (footer != null) {
            group.addView(kit.insetDivider(0));
            TextView more = kit.medium((String) footer[0], 13.5f, kit.accentInk());
            more.setGravity(Gravity.CENTER);
            more.setMinHeight(kit.dp(48));
            kit.press(more, (Runnable) footer[1]);
            group.addView(more);
        }
        into.addView(group, kit.below(fold != null ? 2 : 10));
    }

    private static List<String> strings(JSONArray array) {
        List<String> out = new ArrayList<>();
        for (int i = 0; array != null && i < array.length(); i++) if (!array.optString(i).isEmpty()) out.add(array.optString(i));
        return out;
    }

    private void drawFilters(AggregateSessions.Board board) {
        if (filters == null) return;
        List<String> tools = new ArrayList<>(board.tools), sources = new ArrayList<>(board.sources);
        for (Snapshot snapshot : snapshots.values()) for (AggregateSessions.Project project : snapshot.projects) {
            for (String known : new String[]{"claude", "codex", "agy", "opencode", "qoder", "cline", "pi"}) if (project.tools.contains(known) && !tools.contains(known)) tools.add(known);
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
        filters.addView(filterChip("全部", "", tool.isEmpty() && source.isEmpty(), () -> { tool = ""; source = ""; draw(); }));
        for (final String one : tools) filters.addView(filterChip(AggregateSessions.toolName(one), one, one.equals(tool), () -> { tool = one.equals(tool) ? "" : one; draw(); }));
        if (!tools.isEmpty() && !sources.isEmpty()) {
            View between = new View(activity);
            between.setBackgroundColor(kit.LINE);
            LinearLayout.LayoutParams thin = new LinearLayout.LayoutParams(kit.dp(1), kit.dp(18));
            thin.rightMargin = kit.dp(8);
            filters.addView(between, thin);
        }
        for (final String one : sources) filters.addView(filterChip(AggregateSessions.sourceLabel(one), "", one.equals(source), () -> { source = one.equals(source) ? "" : one; draw(); }));
    }

    private View filterChip(String label, String toolKey, boolean on, Runnable action) {
        FrameLayout wrap = new FrameLayout(activity);
        wrap.setMinimumHeight(kit.dp(48));
        LinearLayout pill = kit.row();
        String mono = toolKey.isEmpty() ? "" : Kit.toolMono(toolKey);
        int bg = on ? kit.accentSoft() : kit.PANEL;
        int fg = on ? kit.accentInk() : kit.INK;
        int bd = on ? Kit.mix(kit.ACCENT, kit.cardBorder(), .32f) : kit.cardBorder();
        pill.setBackground(kit.shape(bg, bd, 99));
        pill.setPadding(kit.dp(mono.isEmpty() ? 16 : 7), 0, kit.dp(16), 0);
        if (!mono.isEmpty()) {
            TextView badge = kit.bold(mono, 10, kit.toolBadgeFg(toolKey));
            badge.setGravity(Gravity.CENTER);
            badge.setIncludeFontPadding(false);
            badge.setBackground(kit.shape(kit.toolBadgeBg(toolKey), on ? Kit.tint(kit.ACCENT, 40) : 0, 13));
            LinearLayout.LayoutParams bLp = new LinearLayout.LayoutParams(kit.dp(26), kit.dp(26));
            bLp.rightMargin = kit.dp(8);
            pill.addView(badge, bLp);
        }
        TextView text = on ? kit.semibold(label, 13.5f, fg) : kit.medium(label, 13.5f, fg);
        text.setSingleLine(true);
        pill.addView(text);
        wrap.addView(pill, new FrameLayout.LayoutParams(-2, kit.dp(40), Gravity.CENTER_VERTICAL));
        wrap.setContentDescription(label + (on ? "，已选" : ""));
        kit.press(wrap, action);
        LinearLayout.LayoutParams gap = new LinearLayout.LayoutParams(-2, -2);
        gap.rightMargin = kit.dp(8);
        wrap.setLayoutParams(gap);
        return wrap;
    }

    /** Hero elevated card for running & needs-confirmation sessions, faithful to the design. */
    private View heroTaskCard(AggregateSessions.Thing task, boolean several) {
        final AggregateSessions.Entry entry = task.entry;
        boolean confirm = "confirm".equals(task.kind);
        boolean closing = ending.contains(task.url + '\n' + entry.id);

        LinearLayout card = kit.cardBox(22);
        card.setPadding(kit.dp(16), kit.dp(16), kit.dp(16), kit.dp(16));

        // Header: 42dp tool monogram badge + title + project/location row
        LinearLayout header = kit.row();
        header.setGravity(Gravity.TOP);
        header.addView(kit.toolBadge(entry.displayTool(), 42));
        LinearLayout info = kit.column();
        info.setPadding(kit.dp(12), 0, 0, 0);
        info.addView(kit.title(entry.shownTitle(), 16, kit.INK, true));
        LinearLayout placeRow = kit.row();
        if (!entry.place().isEmpty()) {
            View tag = projectTag(entry);
            ((LinearLayout.LayoutParams) tag.getLayoutParams()).rightMargin = kit.dp(6);
            ((LinearLayout.LayoutParams) tag.getLayoutParams()).topMargin = 0;
            placeRow.addView(tag);
        }
        String loc = entry.toolName() + (AggregateSessions.location(entry).isEmpty() ? "" : " · " + AggregateSessions.location(entry)) + (several ? " · " + task.computer : "");
        placeRow.addView(kit.line(loc, 12.5f, kit.MUTED, false), new LinearLayout.LayoutParams(0, -2, 1));
        info.addView(placeRow, kit.below(6));
        header.addView(info, new LinearLayout.LayoutParams(0, -2, 1));
        card.addView(header);

        // Live status strip (warm amber for running, soft red for confirm)
        LinearLayout strip = kit.column();
        strip.setPadding(kit.dp(12), kit.dp(11), kit.dp(12), kit.dp(11));
        int stripBg = confirm ? kit.badSoft() : kit.busySoft();
        int stripFg = confirm ? kit.BAD : kit.busyInk();
        int dotColor = confirm ? kit.BAD : kit.busyDot();
        strip.setBackground(kit.shape(stripBg, 0, 14));
        LinearLayout stripTop = kit.row();
        View dot = kit.dot(dotColor, 8);
        ((LinearLayout.LayoutParams) dot.getLayoutParams()).rightMargin = kit.dp(8);
        stripTop.addView(dot);
        stripTop.addView(kit.semibold(closing ? "正在结束" : AggregateSessions.label(task.kind), 13, stripFg), new LinearLayout.LayoutParams(0, -2, 1));
        String when = AggregateSessions.ago(AggregateSessions.recent(entry), System.currentTimeMillis());
        if (!when.isEmpty()) stripTop.addView(kit.mono(when, 12, stripFg));
        strip.addView(stripTop);

        if (entry.terminal || entry.reached) {
            TextView peek = kit.mono(entry.said, 12, confirm ? kit.BAD : Kit.light(kit.BG) ? 0xff6e4a12 : kit.BUSY);
            peek.setSingleLine(true);
            peek.setEllipsize(android.text.TextUtils.TruncateAt.END);
            peek.setVisibility(entry.said.isEmpty() ? View.GONE : View.VISIBLE);
            strip.addView(peek, kit.below(6));
            peeks.put(task.url + '\n' + entry.id, peek);
        }
        card.addView(strip, kit.below(14));

        // Bottom action bar: primary "打开会话 ›" button + quick reply + stop
        LinearLayout actions = kit.row();
        TextView openBtn = kit.semibold(confirm ? "去确认 ›" : "打开会话 ›", 14, kit.onSolidInk());
        openBtn.setGravity(Gravity.CENTER);
        openBtn.setMinHeight(kit.dp(48));
        openBtn.setBackground(kit.shape(confirm ? kit.BAD : kit.solidInk(), 0, 14));
        kit.press(openBtn, () -> openTask(task));
        actions.addView(openBtn, new LinearLayout.LayoutParams(0, kit.dp(48), 1));

        boolean canQuickSay = !confirm && ((entry.terminal && "running".equals(entry.state)) || (entry.reached && entry.canSay));
        if (canQuickSay) {
            FrameLayout sayBtn = new FrameLayout(activity);
            sayBtn.setBackground(kit.shape(kit.RAISED, 0, 14));
            sayBtn.addView(kit.icon(R.drawable.ic_say, kit.INK, 18), new FrameLayout.LayoutParams(kit.dp(18), kit.dp(18), Gravity.CENTER));
            sayBtn.setContentDescription("给“" + entry.shownTitle() + "”发消息");
            kit.press(sayBtn, () -> sayTask(task));
            LinearLayout.LayoutParams sLp = new LinearLayout.LayoutParams(kit.dp(48), kit.dp(48));
            sLp.leftMargin = kit.dp(8);
            actions.addView(sayBtn, sLp);
        }
        if (canEnd(task.url, entry)) {
            FrameLayout stopBtn = new FrameLayout(activity);
            stopBtn.setBackground(kit.shape(kit.badSoft(), 0, 14));
            stopBtn.addView(kit.icon(R.drawable.ic_stop, kit.BAD, 16), new FrameLayout.LayoutParams(kit.dp(16), kit.dp(16), Gravity.CENTER));
            stopBtn.setContentDescription(closing ? "正在结束“" + entry.shownTitle() + "”" : "结束“" + entry.shownTitle() + "”");
            stopBtn.setEnabled(!closing);
            stopBtn.setAlpha(closing ? .4f : 1f);
            kit.press(stopBtn, () -> stopTask(task));
            LinearLayout.LayoutParams stLp = new LinearLayout.LayoutParams(kit.dp(48), kit.dp(48));
            stLp.leftMargin = kit.dp(8);
            actions.addView(stopBtn, stLp);
        }
        card.addView(actions, kit.below(12));

        kit.press(card, () -> openTask(task));
        card.setOnLongClickListener(v -> { manage(task); return true; });
        return card;
    }

    /** Clean compact row inside a grouped 22dp card for opened/pinned/recent sessions. */
    private View compactTaskRow(AggregateSessions.Thing task, boolean several) {
        final AggregateSessions.Entry entry = task.entry;
        LinearLayout row = kit.row();
        row.setMinimumHeight(kit.dp(64));
        row.setPadding(kit.dp(14), kit.dp(12), kit.dp(14), kit.dp(12));
        row.addView(kit.toolBadge(entry.displayTool(), 40));

        LinearLayout texts = kit.column();
        texts.setPadding(kit.dp(12), 0, kit.dp(8), 0);
        texts.addView(kit.title(entry.shownTitle(), 15, kit.INK, false));
        List<String> metaParts = new ArrayList<>();
        if (!entry.place().isEmpty()) metaParts.add(entry.place() + (entry.apart() ? " · 未加入项目" : ""));
        metaParts.add(entry.toolName());
        if (several) metaParts.add(task.computer);
        if (entry.pinned) metaParts.add("置顶");
        texts.addView(kit.line(android.text.TextUtils.join(" · ", metaParts), 12.5f, kit.MUTED, false), kit.below(3));

        if (entry.terminal || entry.reached) {
            TextView peek = kit.line(entry.said, 12, kit.MUTED, false);
            peek.setVisibility(entry.said.isEmpty() ? View.GONE : View.VISIBLE);
            texts.addView(peek, kit.below(3));
            peeks.put(task.url + '\n' + entry.id, peek);
        }
        row.addView(texts, new LinearLayout.LayoutParams(0, -2, 1));

        boolean closing = ending.contains(task.url + '\n' + entry.id);
        if ("history".equals(task.kind) || "ended".equals(task.kind)) {
            String when = AggregateSessions.ago(AggregateSessions.recent(entry), System.currentTimeMillis());
            row.addView(kit.text(when.isEmpty() ? AggregateSessions.label(task.kind) : when + (entry.terminal ? "结束" : ""), 12, kit.MUTED));
        } else if (closing) {
            row.addView(kit.statusPill("正在结束", kit.MUTED, kit.RAISED, 0));
        } else if ("confirm".equals(task.kind)) {
            row.addView(kit.statusPill("等你确认", kit.BAD, kit.badSoft(), kit.BAD));
        } else if ("done".equals(task.kind) || "waiting".equals(task.kind)) {
            row.addView(kit.statusPill("done".equals(task.kind) ? "已完成" : "等你回复", kit.accentInk(), kit.accentSoft(), kit.ACCENT));
        } else if ("busy".equals(task.kind) || "starting".equals(task.kind) || "pc-busy".equals(task.kind)) {
            row.addView(kit.statusPill(AggregateSessions.label(task.kind), kit.busyInk(), kit.busySoft(), kit.busyDot()));
        } else {
            row.addView(kit.statusPill(entry.hidden ? "已隐藏" : AggregateSessions.label(task.kind), kit.MUTED, kit.RAISED, 0));
        }

        kit.press(row, () -> openTask(task));
        row.setOnLongClickListener(v -> { manage(task); return true; });
        return row;
    }

    private void openTask(AggregateSessions.Thing task) {
        AggregateSessions.Entry entry = task.entry;
        if (!entry.terminal) { activity.openSession(task.url, entry.project, entry.id); return; }
        markSeen(task.url, entry.id, entry.at);
        activity.openTerminal(task.url, entry.id, entry.chat);
    }

    // ---- what is done with a thing besides opening it: pinned, hidden, its folder made a project, ended
    private void manage(AggregateSessions.Thing task) {
        final AggregateSessions.Entry entry = task.entry;
        Snapshot snapshot = snapshots.get(task.url);
        boolean marks = snapshot != null && snapshot.marks && !entry.conversation().isEmpty();
        final List<String> labels = new ArrayList<>(), does = new ArrayList<>();
        boolean canQuickSay = !"confirm".equals(task.kind) && ((entry.terminal && "running".equals(entry.state)) || (entry.reached && entry.canSay));
        if (canQuickSay) { labels.add("发消息"); does.add("say"); }
        if (marks) { labels.add(entry.pinned ? "取消置顶" : "置顶"); does.add("pin"); }
        if (marks && "history".equals(task.kind)) { labels.add(entry.hidden ? "取消隐藏" : "从列表隐藏"); does.add("hide"); }
        if (entry.apart()) { labels.add("把 " + entry.place() + " 加为项目"); does.add("add"); }
        int danger = -1;
        if (canEnd(task.url, entry)) { danger = labels.size(); labels.add(entry.terminal ? "结束终端" : "结束电脑上的这段对话"); does.add("end"); }
        if (snapshot != null && snapshot.can.contains("delete") && "history".equals(task.kind) && !entry.terminal && !entry.project.isEmpty()) { danger = labels.size(); labels.add("删除对话"); does.add("delete"); }
        if (labels.isEmpty()) return;
        kit.sheet(entry.shownTitle(), AggregateSessions.where(entry), labels.toArray(new String[0]), null, danger, which -> {
            String what = does.get(which);
            if ("say".equals(what)) sayTask(task);
            else if ("pin".equals(what)) markRemote(task, true, !entry.pinned);
            else if ("hide".equals(what)) markRemote(task, false, !entry.hidden);
            else if ("add".equals(what)) addFolder(task);
            else if ("delete".equals(what)) deleteTask(task);
            else stopTask(task);
        });
    }
    private void deleteTask(AggregateSessions.Thing task) {
        kit.sheet("删除“" + task.entry.shownTitle() + "”？", "", new String[]{"删除"}, null, 0, which -> ask(task.url, "/api/conversation", "已删除", "没有删除，请稍后重试", op -> op.put("action", "delete").put("session", task.entry.id)));
    }
    private void manageProject(String url, AggregateSessions.Project project) {
        Snapshot snapshot = snapshots.get(url);
        if (snapshot == null || project.fixed && !snapshot.can.contains("projects")) return;
        kit.sheet(project.name, "", new String[]{"移除项目"}, null, 0, which -> ask(url, "/api/terminal", "已移除", "没有移除，请稍后重试", op -> op.put("action", "project_remove").put("name", project.name)));
    }
    private interface Filled { void fill(JSONObject op) throws org.json.JSONException; }
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
                problem = "不支持此操作".equals(said) ? "这台电脑的电脑端或中转版本较早，请更新后再试" : said == null ? failed : said;
            }
            final String message = problem.isEmpty() ? done : problem;
            activity.runOnUiThread(() -> { android.widget.Toast.makeText(activity, message, android.widget.Toast.LENGTH_SHORT).show(); refresh(); });
        });
    }
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
        TextView tag = kit.medium(entry.place() + (entry.apart() ? " · 未加入项目" : ""), 12, kit.INK);
        tag.setSingleLine(true); tag.setEllipsize(android.text.TextUtils.TruncateAt.END);
        tag.setPadding(kit.dp(8), kit.dp(2), kit.dp(8), kit.dp(2));
        tag.setBackground(kit.shape(kit.RAISED, 0, 6));
        LinearLayout.LayoutParams space = new LinearLayout.LayoutParams(-2, -2);
        space.topMargin = kit.dp(4);
        tag.setLayoutParams(space);
        tag.setContentDescription((entry.apart() ? "文件夹：" : "项目：") + entry.place());
        return tag;
    }
    private View stopButton(AggregateSessions.Thing task) {
        boolean busy = ending.contains(task.url + '\n' + task.entry.id);
        View button = kit.discButton(R.drawable.ic_stop, busy ? "正在结束“" + task.entry.shownTitle() + "”" : "结束“" + task.entry.shownTitle() + "”", kit.badSoft(), kit.BAD, () -> stopTask(task));
        button.setEnabled(!busy); button.setAlpha(busy ? .4f : 1f);
        return button;
    }

    private void sayTask(AggregateSessions.Thing task) {
        kit.write(task.entry.shownTitle(), task.entry.said.isEmpty() ? task.entry.project + " · " + AggregateSessions.label(task.kind) : task.entry.said,
            "发送消息", "发送", text -> sayRemote(task, text));
    }
    private void sayRemote(AggregateSessions.Thing task, String text) {
        final String cookie = CookieManager.getInstance().getCookie(task.url);
        activity.net.execute(() -> {
            String problem = "";
            try {
                JSONObject op = new JSONObject().put("action", "say").put("id", UUID.randomUUID().toString().replace("-", "")).put("text", text);
                op.put(task.entry.terminal ? "terminal" : "session", task.entry.id);
                JSONObject result = null;
                for (int attempt = 0; result == null && attempt < 3; attempt++) {
                    try { result = post(task.url + (task.entry.terminal ? "/api/terminal" : "/api/conversation"), cookie, op, task.entry.terminal ? 8000 : 95000); }
                    catch (IOException unreachable) { Thread.sleep(500); }
                }
                if (result == null) throw new Exception("连不上这台电脑，消息没有发出");
                if ("error".equals(result.optString("state"))) throw new Exception(result.optString("error", "消息没有发出"));
            } catch (Exception error) {
                String said = error.getMessage();
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

    /** One computer: dark hero card + grouped active projects card underneath. */
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

        boolean on = snapshot != null && "on".equals(snapshot.state);
        boolean folded = collapsed.contains(url);
        int[] counts = on ? AggregateSessions.counts(snapshot.projects, looked) : new int[4];

        // Elevated computer status banner (calm surface matching paper/dark themes)
        LinearLayout hero = kit.row();
        hero.setPadding(kit.dp(14), kit.dp(14), kit.dp(10), kit.dp(14));
        hero.setBackground(kit.shape(kit.heroBg(), kit.heroBorder(), 22));
        kit.elevate(hero, 1.5f);

        FrameLayout iconWrap = new FrameLayout(activity);
        iconWrap.setBackground(kit.shape(kit.accentSoft(), 0, 14));
        iconWrap.addView(kit.icon(R.drawable.ic_computer, kit.accentInk(), 22), new FrameLayout.LayoutParams(kit.dp(22), kit.dp(22), Gravity.CENTER));
        View dot = new View(activity);
        int dotColor = snapshot == null ? kit.MUTED : !on ? ("key".equals(snapshot.state) ? kit.busyDot() : kit.BAD) : counts[0] > 0 ? kit.BAD : kit.goodDot();
        GradientDrawable dotShape = kit.shape(dotColor, 0, 6);
        dotShape.setStroke(kit.dp(2.5f), kit.heroBg());
        dot.setBackground(dotShape);
        FrameLayout.LayoutParams dotLp = new FrameLayout.LayoutParams(kit.dp(12), kit.dp(12), Gravity.BOTTOM | Gravity.END);
        dotLp.setMargins(0, 0, -kit.dp(1), -kit.dp(1));
        iconWrap.addView(dot, dotLp);
        hero.addView(iconWrap, new LinearLayout.LayoutParams(kit.dp(44), kit.dp(44)));

        LinearLayout texts = kit.column();
        texts.setPadding(kit.dp(12), 0, kit.dp(6), 0);
        LinearLayout nameRow = kit.row();
        nameRow.addView(kit.line(name, 16, kit.heroFg(), true));
        String tagText = snapshot == null ? "连接中" : on ? "在线" : "key".equals(snapshot.state) ? "需登录" : "离线";
        int tagFg = snapshot == null ? kit.MUTED : on ? kit.goodInk() : "key".equals(snapshot.state) ? kit.busyInk() : kit.BAD;
        int tagBg = snapshot == null ? kit.RAISED : on ? kit.goodSoft() : "key".equals(snapshot.state) ? kit.busySoft() : kit.badSoft();
        TextView onlineTag = kit.statusPill(tagText, tagFg, tagBg, 0);
        LinearLayout.LayoutParams tagLp = new LinearLayout.LayoutParams(-2, -2);
        tagLp.leftMargin = kit.dp(8);
        nameRow.addView(onlineTag, tagLp);
        texts.addView(nameRow);

        String status = snapshot == null ? "正在连接…" : snapshot.line;
        if (on) {
            int namedProjects = 0;
            for (AggregateSessions.Project project : snapshot.projects) if (!project.name.isEmpty()) namedProjects++;
            status = namedProjects + " 个项目 · " + status;
        }
        texts.addView(kit.line(status, 12.5f, kit.heroMuted(), false), kit.below(3));
        hero.addView(texts, new LinearLayout.LayoutParams(0, -2, 1));

        // Fold toggle + Manage options button
        FrameLayout foldBtn = new FrameLayout(activity);
        View foldDisc = new View(activity);
        foldDisc.setBackground(kit.shape(kit.heroChipBg(), 0, 12));
        foldBtn.addView(foldDisc, new FrameLayout.LayoutParams(kit.dp(38), kit.dp(38), Gravity.CENTER));
        View foldIcon = kit.icon(R.drawable.ic_chevron, kit.MUTED, 17);
        foldIcon.setRotation(folded ? 0 : 90);
        foldBtn.addView(foldIcon, new FrameLayout.LayoutParams(kit.dp(17), kit.dp(17), Gravity.CENTER));
        foldBtn.setContentDescription(folded ? "展开" + name : "收起" + name);
        kit.press(foldBtn, () -> { if (!collapsed.remove(url)) collapsed.add(url); drawComputer(computer); });
        hero.addView(foldBtn, new LinearLayout.LayoutParams(kit.dp(48), kit.dp(48)));

        FrameLayout moreBtn = new FrameLayout(activity);
        View moreDisc = new View(activity);
        moreDisc.setBackground(kit.shape(kit.heroChipBg(), 0, 12));
        moreBtn.addView(moreDisc, new FrameLayout.LayoutParams(kit.dp(38), kit.dp(38), Gravity.CENTER));
        moreBtn.addView(kit.icon(R.drawable.ic_more, kit.MUTED, 17), new FrameLayout.LayoutParams(kit.dp(17), kit.dp(17), Gravity.CENTER));
        moreBtn.setContentDescription(name + " 的选项");
        kit.press(moreBtn, () -> activity.manage(url));
        hero.addView(moreBtn, new LinearLayout.LayoutParams(kit.dp(48), kit.dp(48)));

        kit.press(hero, () -> activity.open(url, ""));
        hero.setOnLongClickListener(v -> { activity.manage(url); return true; });
        holder.addView(hero);

        if (on && !folded) {
            List<AggregateSessions.Project> order = new ArrayList<>(snapshot.projects.size());
            for (AggregateSessions.Project project : snapshot.projects) if (!project.active.isEmpty()) order.add(project);
            int busy = order.size();
            for (AggregateSessions.Project project : snapshot.projects) if (project.active.isEmpty() && !project.ended.isEmpty()) order.add(project);
            for (AggregateSessions.Project project : snapshot.projects) if (project.active.isEmpty() && project.ended.isEmpty()) order.add(project);

            holder.addView(heading("最近活跃", "按最近使用排序"), kit.below(18));

            LinearLayout card = kit.cardBox(22);
            boolean all = expanded.contains(url);
            int shown = all ? order.size() : Math.max(busy, Math.min(SHOWN, order.size()));
            for (int i = 0; i < shown; i++) {
                if (i > 0) card.addView(kit.insetDivider(0));
                card.addView(projectRow(computer, order.get(i), looked));
            }
            if (order.size() > Math.max(busy, SHOWN) || all && order.size() > SHOWN) {
                card.addView(kit.insetDivider(0));
                TextView toggle = kit.medium(all ? "收起" : "其余 " + (order.size() - shown) + " 个项目 ⌄", 13.5f, kit.accentInk());
                toggle.setGravity(Gravity.CENTER); toggle.setMinHeight(kit.dp(48));
                kit.press(toggle, () -> { if (!expanded.remove(url)) expanded.add(url); drawComputer(computer); });
                card.addView(toggle);
            }
            card.addView(kit.insetDivider(0));
            TextView fresh = kit.semibold(order.isEmpty() ? "＋ 添加项目" : "＋ 新建对话", 13.5f, kit.accentInk());
            fresh.setGravity(Gravity.CENTER); fresh.setMinHeight(kit.dp(48));
            kit.press(fresh, order.isEmpty() ? () -> activity.open(url, "") : () -> activity.startNew(url));
            card.addView(fresh);
            holder.addView(card, kit.below(10));
        } else if (!on && !folded && snapshot != null) {
            LinearLayout card = kit.cardBox(20);
            LinearLayout help = kit.row();
            help.setPadding(kit.dp(14), kit.dp(12), kit.dp(12), kit.dp(12));
            help.addView(kit.text("key".equals(snapshot.state) ? "登录已过期。" : "请确认电脑已开机且 Remote AI 正在运行。退出电脑端或重启电脑后，公网隧道的地址会变更。", 12.5f, kit.MUTED), new LinearLayout.LayoutParams(0, -2, 1));
            TextView remove = kit.semibold("移除", 13.5f, kit.BAD);
            remove.setGravity(Gravity.CENTER); remove.setMinHeight(kit.dp(48)); remove.setPadding(kit.dp(14), 0, kit.dp(14), 0);
            remove.setBackground(kit.shape(kit.badSoft(), 0, 12));
            kit.press(remove, () -> activity.confirmRemoveComputer(url));
            LinearLayout.LayoutParams removeParams = new LinearLayout.LayoutParams(-2, -2);
            removeParams.leftMargin = kit.dp(10);
            help.addView(remove, removeParams);
            TextView again = kit.semibold("重新扫码", 13.5f, kit.accentInk());
            again.setGravity(Gravity.CENTER); again.setMinHeight(kit.dp(48)); again.setPadding(kit.dp(14), 0, kit.dp(14), 0);
            again.setBackground(kit.shape(kit.accentSoft(), 0, 12));
            kit.press(again, activity::scan);
            LinearLayout.LayoutParams beside = new LinearLayout.LayoutParams(-2, -2);
            beside.leftMargin = kit.dp(8);
            help.addView(again, beside);
            card.addView(help);
            holder.addView(card, kit.below(10));
        }
    }

    private View drawerHeading(String text) {
        TextView note = kit.semibold(text, 11.5f, kit.MUTED);
        note.setLetterSpacing(0.03f);
        note.setPadding(kit.dp(10), kit.dp(10), kit.dp(10), kit.dp(4));
        return note;
    }

    /** One project row inside the grouped projects card, with inset collapsible session drawer. */
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
        waiting.sort(AggregateSessions::compareOpened);
        boolean open = unfolded.contains(key);
        LinearLayout row = kit.row();
        row.setMinimumHeight(kit.dp(60)); row.setPadding(kit.dp(14), kit.dp(12), kit.dp(14), kit.dp(12));

        int folderBg = need > 0 ? kit.badSoft() : work > 0 ? kit.busySoft() : opened > 0 || open ? kit.accentSoft() : kit.RAISED;
        int folderFg = need > 0 ? kit.BAD : work > 0 ? kit.busyInk() : opened > 0 || open ? kit.accentInk() : kit.MUTED;
        row.addView(kit.tintedTile(R.drawable.ic_folder, folderBg, folderFg, 40, 12));

        LinearLayout texts = kit.column();
        texts.setPadding(kit.dp(12), 0, kit.dp(8), 0);
        texts.addView(kit.title(project.name.isEmpty() ? "未加入项目的对话" : project.name, 15, kit.INK, true));

        LinearLayout subRow = kit.row();
        if (need > 0 || work > 0 || opened > 0) {
            int dotCol = need > 0 ? kit.BAD : work > 0 ? kit.busyDot() : kit.ACCENT;
            int hiCol = need > 0 ? kit.BAD : work > 0 ? kit.busyInk() : kit.accentInk();
            String firstLabel = need > 0 ? need + " 个等你确认" : work > 0 ? work + " 个运行中" : opened + " 个已打开";
            View statusDot = kit.dot(dotCol, 6);
            ((LinearLayout.LayoutParams) statusDot.getLayoutParams()).rightMargin = kit.dp(5);
            subRow.addView(statusDot);
            subRow.addView(kit.medium(firstLabel, 12.5f, hiCol));

            List<String> rest = new ArrayList<>();
            if (need > 0 && work > 0) rest.add(work + " 个运行中");
            if ((need > 0 || work > 0) && opened > 0) rest.add(opened + " 个已打开");
            if (!project.history.isEmpty() && need == 0 && work == 0) rest.add(project.history.size() + " 段历史");
            if (!rest.isEmpty()) subRow.addView(kit.line(" · " + android.text.TextUtils.join(" · ", rest), 12.5f, kit.MUTED, false));
        } else {
            List<String> parts = new ArrayList<>();
            parts.add(!project.ended.isEmpty() ? project.ended.size() + " 个历史终端" : project.history.isEmpty() ? "还没有对话" : project.history.size() + " 段对话");
            if (!project.uses().isEmpty()) parts.add(project.uses());
            subRow.addView(kit.line(android.text.TextUtils.join(" · ", parts), 12.5f, kit.MUTED, false));
        }
        texts.addView(subRow, kit.below(3));
        row.addView(texts, new LinearLayout.LayoutParams(0, -2, 1));

        View chevron = kit.icon(R.drawable.ic_chevron, open ? kit.INK : Kit.tint(kit.MUTED, 150), 17);
        ((LinearLayout.LayoutParams) chevron.getLayoutParams()).leftMargin = kit.dp(6);
        chevron.setRotation(open ? 90 : 0);
        row.addView(chevron);
        kit.press(row, () -> { if (!unfolded.remove(key)) unfolded.add(key); drawComputer(computer); });
        if (!project.name.isEmpty()) row.setOnLongClickListener(v -> { manageProject(url, project); return true; });
        if (!open) return row;

        LinearLayout box = kit.column();
        box.addView(row);
        LinearLayout inside = kit.column();
        inside.setPadding(kit.dp(6), kit.dp(2), kit.dp(6), kit.dp(6));
        inside.setBackground(kit.shape(kit.subSurface(), 0, 16));
        if (!confirms.isEmpty()) inside.addView(drawerHeading("等你确认"));
        for (AggregateSessions.Entry entry : confirms) inside.addView(talkRow(url, entry, "confirm", true), kit.below(2));
        if (!running.isEmpty()) inside.addView(drawerHeading("正在运行"));
        for (AggregateSessions.Entry entry : running) inside.addView(talkRow(url, entry, entry.kind(looked), true), kit.below(2));
        if (!waiting.isEmpty()) inside.addView(drawerHeading("已打开"));
        for (AggregateSessions.Entry entry : waiting) inside.addView(talkRow(url, entry, entry.kind(looked), true), kit.below(2));
        int endedShown = Math.min(TALKS, project.ended.size());
        if (endedShown > 0) inside.addView(drawerHeading("历史终端"));
        for (int i = 0; i < endedShown; i++) inside.addView(talkRow(url, project.ended.get(i), "ended", false), kit.below(2));
        List<AggregateSessions.Entry> saved = new ArrayList<>();
        for (AggregateSessions.Entry entry : project.history) if (!entry.hidden || entry.live) saved.add(entry);
        int remaining = Math.max(0, TALKS - endedShown), historyShown = Math.min(remaining, saved.size());
        if (historyShown > 0) inside.addView(drawerHeading("保存的对话"));
        for (int i = 0; i < historyShown; i++) inside.addView(talkRow(url, saved.get(i), saved.get(i).kind(looked), false), kit.below(2));
        int rest = saved.size() - historyShown, hiddenEnded = project.ended.size() - endedShown;
        String more = project.name.isEmpty() ? "查看这台电脑的全部对话" : rest + hiddenEnded > 0 ? "新建对话 · 查看更多记录" : "新建对话 · 进入项目";
        TextView enter = kit.semibold(more, 13.5f, kit.accentInk());
        enter.setGravity(Gravity.CENTER_VERTICAL); enter.setMinHeight(kit.dp(48)); enter.setPadding(kit.dp(10), 0, kit.dp(10), 0);
        kit.press(enter, () -> activity.open(url, "", project.name));
        inside.addView(enter, kit.below(2));
        LinearLayout.LayoutParams inset = new LinearLayout.LayoutParams(-1, -2);
        inset.setMargins(kit.dp(10), 0, kit.dp(10), kit.dp(10));
        box.addView(inside, inset);
        return box;
    }

    /** One conversation inside an unfolded project drawer: elevated white mini-card for live ones, quiet row for history. */
    private View talkRow(String url, AggregateSessions.Entry entry, String kind, boolean activeCard) {
        LinearLayout row = kit.row();
        row.setMinimumHeight(kit.dp(50));
        row.setPadding(kit.dp(10), kit.dp(9), kit.dp(10), kit.dp(9));
        if (activeCard) {
            row.setBackground(kit.shape(kit.PANEL, 0, 12));
            kit.elevate(row, 1f);
        }
        row.addView(kit.toolBadge(entry.displayTool(), 30));
        LinearLayout texts = kit.column();
        texts.setPadding(kit.dp(10), 0, kit.dp(8), 0);
        texts.addView(kit.title(entry.shownTitle(), 14, "history".equals(kind) ? kit.MUTED : kit.INK, false));
        row.addView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        String used = AggregateSessions.ago(AggregateSessions.recent(entry), System.currentTimeMillis());
        if ("history".equals(kind) || "ended".equals(kind)) {
            row.addView(kit.text(used.isEmpty() ? AggregateSessions.label(kind) : used + (entry.terminal ? "结束" : ""), 12, kit.MUTED));
        } else {
            boolean closing = ending.contains(url + '\n' + entry.id);
            if (closing) row.addView(kit.statusPill("正在结束", kit.MUTED, kit.RAISED, 0));
            else if ("confirm".equals(kind)) row.addView(kit.statusPill("等你确认", kit.BAD, kit.badSoft(), kit.BAD));
            else if (AggregateSessions.working(kind)) row.addView(kit.statusPill(AggregateSessions.label(kind), kit.busyInk(), kit.busySoft(), kit.busyDot()));
            else row.addView(kit.statusPill(AggregateSessions.label(kind), kit.accentInk(), kit.accentSoft(), 0));
        }
        if (canEnd(url, entry)) {
            row.setPadding(kit.dp(10), kit.dp(6), kit.dp(2), kit.dp(6));
            View stop = stopButton(new AggregateSessions.Thing(url, "", entry, kind));
            ((LinearLayout.LayoutParams) stop.getLayoutParams()).leftMargin = kit.dp(2);
            row.addView(stop);
        }
        kit.press(row, () -> {
            if (!entry.terminal) { activity.openSession(url, entry.project, entry.id); return; }
            markSeen(url, entry.id, entry.at);
            activity.openTerminal(url, entry.id, entry.chat);
        });
        return row;
    }
}
