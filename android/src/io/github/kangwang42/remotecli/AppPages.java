package io.github.kangwang42.remotecli;

import android.graphics.Typeface;
import android.view.Gravity;
import android.view.View;
import android.webkit.CookieManager;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import org.json.JSONArray;
import org.json.JSONObject;

/**
 * Two of the app's four lists, both over every computer: 协作 (what each computer keeps of conversations working
 * together; what is set there is set on that computer's own page of it) and 我的 (the work in numbers, connected
 * computers, and the app's settings). Each computer answers independently with its own cookie.
 */
final class AppPages {
    private static final int TINT_CLAUDE = 0xffd97757, TINT_WARN = 0xffc77c1a, TINT_RELAY = 0xff7b6fd6, TINT_LOOK = 0xffd6587e, TINT_AGY = 0xff6366f1;

    private final MainActivity activity;
    private final Kit kit;
    private final Workbench bench;
    private final Map<String, LinearLayout> holders = new HashMap<>();
    private final Map<String, TextView> deviceSummaries = new HashMap<>();
    private final Map<String, JSONObject> stats = new HashMap<>();
    private TextView metricPrompts, metricSessions, metricMinutes, coverage;
    private int generation, answered, expected;

    AppPages(MainActivity activity, Kit kit, Workbench bench) { this.activity = activity; this.kit = kit; this.bench = bench; }

    View build(String tab) {
        generation++; holders.clear(); deviceSummaries.clear(); stats.clear(); answered = 0;
        boolean mine = "me".equals(tab);
        LinearLayout page = kit.page(22);
        View refreshAction = kit.iconButton(R.drawable.ic_refresh, "刷新", kit.MUTED, () -> activity.appPage(tab));
        page.addView(kit.head(mine ? "我的" : "协作", kit.text(mine ? "设备连接与个人偏好" : "交接上下文，让对话接着做", 13, kit.MUTED), refreshAction));
        JSONArray computers = activity.computers();
        expected = computers.length();

        if (mine) {
            buildMe(page, computers);
        } else {
            buildTogether(page, computers);
        }
        activity.message = kit.text("", 13, kit.MUTED);
        page.addView(activity.message, kit.below(10));
        return page;
    }

    private LinearLayout quotaCard;
    /** The tools' limits, first on 我的: a line for each window, its share used drawn under it (red above 90%). */
    void drawQuotas() {
        if (quotaCard == null) return;
        quotaCard.removeAllViews();
        List<QuotaLines.Reading> shown = bench.quotas();
        quotaCard.setVisibility(shown.isEmpty() ? View.GONE : View.VISIBLE);
        long now = System.currentTimeMillis() / 1000;
        LinearLayout head = kit.row();
        head.addView(kit.semibold("额度", 13.5f, kit.INK), new LinearLayout.LayoutParams(0, -2, 1));
        quotaCard.addView(head);
        for (QuotaLines.Reading one : shown) {
            if (one.windows.isEmpty()) { quotaLine(QuotaLines.tool(one.tool), one.why, kit.MUTED, -1); continue; }
            for (QuotaLines.Window w : one.windows) quotaLine(QuotaLines.label(one.tool, w), QuotaLines.detail(w, now), QuotaLines.high(w) ? kit.BAD : kit.MUTED, w.used);
        }
    }
    private void quotaLine(String label, String detail, int tone, double used) {
        LinearLayout line = kit.row();
        line.setPadding(0, kit.dp(12), 0, 0);
        line.addView(kit.semibold(label, 14, kit.INK), new LinearLayout.LayoutParams(0, -2, 1));
        line.addView(kit.text(detail, 12, tone));
        quotaCard.addView(line);
        if (used < 0) return;
        // the share used, as a bar
        LinearLayout track = new LinearLayout(activity);
        track.setBackground(kit.shape(kit.RAISED, 0, 3));
        track.setClipToOutline(true);
        View part = new View(activity);
        part.setBackground(kit.shape(used >= 90 ? kit.BAD : kit.ACCENT, 0, 3));
        float share = (float) Math.max(2, Math.min(100, used));
        track.addView(part, new LinearLayout.LayoutParams(0, -1, share));
        if (share < 100) track.addView(new View(activity), new LinearLayout.LayoutParams(0, -1, 100 - share));
        LinearLayout.LayoutParams bar = new LinearLayout.LayoutParams(-1, kit.dp(6));
        bar.topMargin = kit.dp(6);
        quotaCard.addView(track, bar);
    }

    private void buildMe(LinearLayout page, JSONArray computers) {
        quotaCard = kit.column();
        quotaCard.setPadding(kit.dp(16), kit.dp(14), kit.dp(16), kit.dp(16));
        quotaCard.setBackground(kit.shape(kit.PANEL, kit.rim(), 18));
        kit.elevate(quotaCard, 1.5f);
        page.addView(quotaCard, kit.below(16));
        drawQuotas();

        // Compact 3-metric KPI summary card for today's work
        LinearLayout summary = kit.column();
        summary.setPadding(kit.dp(16), kit.dp(14), kit.dp(16), kit.dp(14));
        summary.setBackground(kit.shape(kit.PANEL, kit.rim(), 18));
        kit.elevate(summary, 1.5f);
        LinearLayout topRow = kit.row();
        topRow.addView(kit.semibold("今日工作", 13.5f, kit.INK), new LinearLayout.LayoutParams(0, -2, 1));
        coverage = kit.text(expected == 0 ? "添加电脑后显示统计" : "正在读取…", 12, kit.MUTED);
        topRow.addView(coverage);
        summary.addView(topRow);

        LinearLayout metrics = kit.row();
        metrics.setPadding(0, kit.dp(12), 0, kit.dp(2));
        metricPrompts = metricCell(metrics, "提问", false);
        metricSessions = metricCell(metrics, "对话", true);
        metricMinutes = metricCell(metrics, "分钟", true);
        summary.addView(metrics);

        if (expected == 1) {
            JSONObject only = computers.optJSONObject(0);
            if (only != null) {
                String url = only.optString("url");
                kit.press(summary, () -> activity.computerPage(url, "me"));
            }
        }
        page.addView(summary, kit.below(16));

        // Unified "我的电脑" grouped list (merges device management + per-computer stats without duplicate cards or giant buttons)
        LinearLayout connections = kit.settingGroup(page, "我的电脑");
        for (int i = 0; i < computers.length(); i++) {
            JSONObject computer = computers.optJSONObject(i);
            if (computer == null) continue;
            final String url = computer.optString("url");
            final String name = computer.optString("name", MainActivity.host(url));
            if (connections.getChildCount() > 0) {
                View line = new View(activity);
                line.setBackgroundColor(kit.rim());
                LinearLayout.LayoutParams divider = new LinearLayout.LayoutParams(-1, kit.hairline());
                divider.setMargins(kit.dp(58), 0, 0, 0);
                connections.addView(line, divider);
            }
            LinearLayout row = kit.row();
            row.setPadding(kit.dp(14), kit.dp(11), kit.dp(12), kit.dp(11));
            row.setMinimumHeight(kit.dp(58));
            View badge = kit.tile(R.drawable.ic_computer, kit.ACCENT, 32);
            ((LinearLayout.LayoutParams) badge.getLayoutParams()).rightMargin = kit.dp(12);
            row.addView(badge);
            LinearLayout copy = kit.column();
            copy.addView(kit.line(name, 15.5f, kit.INK, true));
            TextView sub = kit.line(MainActivity.host(url) + " · 正在读取统计…", 12.5f, kit.MUTED, false);
            copy.addView(sub, kit.below(2));
            deviceSummaries.put(url, sub);
            row.addView(copy, new LinearLayout.LayoutParams(0, -2, 1));

            TextView statChip = kit.bold("统计", 12.5f, kit.ACCENT);
            statChip.setGravity(Gravity.CENTER);
            statChip.setMinHeight(kit.dp(48));
            statChip.setPadding(kit.dp(10), 0, kit.dp(10), 0);
            kit.press(statChip, () -> activity.computerPage(url, "me"));
            row.addView(statChip);

            row.addView(kit.icon(R.drawable.ic_chevron, Kit.tint(kit.MUTED, 150), 15));
            kit.press(row, () -> activity.manage(url));
            connections.addView(row);
        }
        kit.setting(connections, R.drawable.ic_plus, kit.ACCENT, "添加电脑", "扫描电脑端的连接二维码", "", kit.ACCENT, activity::scan);

        // Grouped settings with clean tinted icons and modern pill switches
        LinearLayout preferences = kit.settingGroup(page, "提醒与偏好");
        kit.settingSwitch(preferences, R.drawable.ic_bell, TINT_WARN, "任务提醒", "需要确认或一轮完成时通知", activity.watching(), activity::toggleWatch);
        String remStatus = activity.reminderStatus();
        int remColor = "已开启".equals(remStatus) ? kit.GOOD : "需授权".equals(remStatus) ? kit.BAD : kit.MUTED;
        kit.setting(preferences, R.drawable.ic_shield, kit.GOOD, "提醒与权限", "通知、电池与后台联网", remStatus, remColor, activity::reminderSettings);
        kit.setting(preferences, R.drawable.ic_palette, TINT_LOOK, "外观与文字", "主题、字号与终端阅读方式", activity.appearanceName(), kit.MUTED, activity::look);

        LinearLayout updates = kit.settingGroup(page, "版本与更新");
        kit.setting(updates, R.drawable.ic_update, TINT_AGY, "手机 App 更新", "检查手机安装包更新", "v" + activity.versionName(), kit.MUTED, activity::checkUpdate);
        kit.settingSwitch(updates, R.drawable.ic_refresh, kit.MUTED, "自动检查手机更新", "发现新版时提示，安装前由你确认", activity.autoUpdate(), activity::toggleAutoUpdate);
    }

    private TextView metricCell(LinearLayout parent, String label, boolean withDivider) {
        if (withDivider) {
            View divider = new View(activity);
            divider.setBackgroundColor(kit.rim());
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(kit.hairline(), kit.dp(34));
            params.setMargins(kit.dp(6), 0, kit.dp(6), 0);
            parent.addView(divider, params);
        }
        LinearLayout cell = kit.column();
        cell.setPadding(kit.dp(4), 0, kit.dp(4), 0);
        TextView value = kit.figure("—", 22, kit.MUTED);
        cell.addView(value);
        cell.addView(kit.text(label, 12, kit.MUTED), kit.below(2));
        parent.addView(cell, new LinearLayout.LayoutParams(0, -2, 1));
        return value;
    }

    private void buildTogether(LinearLayout page, JSONArray computers) {
        if (expected > 0) {
            page.addView(wideAction("协作任务", "写审循环或领队分工，多 AI 配合完成", "team", TINT_AGY), kit.below(14));
            actions(page,
                "交给另一个 AI", "交接上下文接着做", "handoff", TINT_CLAUDE,
                "同时提问", "一个问题发给多段对话", "many", kit.ACCENT);
            actions(page,
                "定时提问", "到时间由电脑替你问", "schedule", TINT_WARN,
                "自动接力", "完成一轮后自动转交", "chain", TINT_RELAY);
        } else {
            page.addView(kit.text("添加电脑后，可发起多 AI 协作任务，交接对话，或安排接力和定时提问。", 13.5f, kit.MUTED), kit.below(18));
            page.addView(kit.button("扫码添加电脑", 0, activity::scan), kit.below(18));
            return;
        }
        TextView sectionLabel = kit.bold("已安排的协作", 12.5f, kit.MUTED);
        sectionLabel.setPadding(kit.dp(4), 0, kit.dp(4), 0);
        page.addView(sectionLabel, kit.below(22));
        for (int i = 0; i < computers.length(); i++) {
            JSONObject computer = computers.optJSONObject(i);
            if (computer == null) continue;
            final String url = computer.optString("url");
            LinearLayout card = kit.column();
            card.setBackground(kit.shape(kit.PANEL, kit.rim(), 16));
            card.setClipToOutline(true);
            kit.elevate(card, 1.5f);
            LinearLayout head = kit.row();
            head.setMinimumHeight(kit.dp(50));
            head.setPadding(kit.dp(14), kit.dp(8), kit.dp(12), kit.dp(8));
            View badge = kit.tile(R.drawable.ic_computer, kit.ACCENT, 28);
            ((LinearLayout.LayoutParams) badge.getLayoutParams()).rightMargin = kit.dp(10);
            head.addView(badge);
            head.addView(kit.line(computer.optString("name", MainActivity.host(url)), 15.5f, kit.INK, true), new LinearLayout.LayoutParams(0, -2, 1));
            head.addView(kit.icon(R.drawable.ic_chevron, Kit.tint(kit.MUTED, 150), 15));
            kit.press(head, () -> activity.computerPage(url, "together"));
            card.addView(head);
            LinearLayout body = kit.column();
            TextView loading = kit.text("正在读取…", 13, kit.MUTED);
            loading.setPadding(kit.dp(14), kit.dp(4), kit.dp(14), kit.dp(14));
            body.addView(loading);
            holders.put(url, body);
            card.addView(body);
            page.addView(card, kit.below(i == 0 ? 8 : 12));
        }
    }

    private JSONObject ask(String url, String cookie, String action) throws Exception {
        return bench.post(url + "/api/work", cookie, new JSONObject().put("id", UUID.randomUUID().toString().replace("-", "")).put("action", action).put("days", 7));
    }

    void refresh(String tab) {
        final int request = ++generation;
        stats.clear(); answered = 0;
        Iterable<String> targets = "me".equals(tab) ? deviceSummaries.keySet() : holders.keySet();
        for (final String url : targets) {
            final String cookie = CookieManager.getInstance().getCookie(url);
            activity.net.execute(() -> {
                JSONObject result = null; String error = "";
                try {
                    if ("me".equals(tab)) result = ask(url, cookie, "stats");
                    else {
                        result = new JSONObject();
                        // An older computer can still provide schedules when it does not support teams or chains.
                        try { result.put("teams", ask(url, cookie, "teams").optJSONArray("teams")); }
                        catch (Exception ignored) { }
                        try { result.put("chains", ask(url, cookie, "chains").optJSONArray("chains")); }
                        catch (Exception unavailable) { result.put("chain_error", unavailable.getMessage() == null ? "连接失败" : unavailable.getMessage()); }
                        try { result.put("schedules", ask(url, cookie, "schedules").optJSONArray("schedules")); }
                        catch (Exception unavailable) { result.put("schedule_error", unavailable.getMessage() == null ? "连接失败" : unavailable.getMessage()); }
                    }
                } catch (Exception unavailable) { error = unavailable.getMessage() == null ? "连接失败，请重试" : unavailable.getMessage(); }
                final JSONObject data = result; final String problem = error;
                activity.runOnUiThread(() -> {
                    if (request != generation || !activity.showing(tab)) return;
                    answered++;
                    if ("me".equals(tab)) {
                        drawQuotas();
                        TextView sub = deviceSummaries.get(url);
                        if (data == null || !problem.isEmpty()) {
                            if (sub != null) { sub.setText("离线 · " + (problem.isEmpty() ? "请确认电脑在线" : problem)); sub.setTextColor(kit.MUTED); }
                        } else {
                            stats.put(url, data);
                            if (sub != null) { sub.setText(today(data)); sub.setTextColor(kit.MUTED); }
                        }
                        sum();
                        return;
                    }
                    LinearLayout body = holders.get(url);
                    if (body == null) return;
                    body.removeAllViews();
                    if (data == null || !problem.isEmpty()) {
                        addRowMessage(body, "暂时无法读取：" + (problem.isEmpty() ? "请确认电脑在线" : problem), kit.BAD);
                    } else {
                        JSONArray teams = data.optJSONArray("teams");
                        if (teams != null && teams.length() > 0) teamRows(body, url, teams);
                        for (boolean chain : new boolean[]{false, true}) {
                            String failure = data.optString(chain ? "chain_error" : "schedule_error");
                            if (!failure.isEmpty()) addRowMessage(body, (chain ? "接力" : "定时提问") + "暂不可用：" + failure, kit.BAD);
                            else rows(body, url, data.optJSONArray(chain ? "chains" : "schedules"), chain);
                        }
                    }
                });
            });
        }
    }

    private void addRowMessage(LinearLayout body, String text, int color) {
        TextView note = kit.text(text, 12.5f, color);
        note.setPadding(kit.dp(14), kit.dp(4), kit.dp(14), kit.dp(12));
        body.addView(note);
    }

    private JSONObject day(JSONObject data) {
        JSONArray days = data.optJSONArray("days");
        JSONObject last = days == null || days.length() == 0 ? null : days.optJSONObject(days.length() - 1);
        return last == null ? new JSONObject() : last;
    }

    private String today(JSONObject data) {
        JSONObject day = day(data);
        return "今天 " + day.optLong("prompts") + " 次提问 · " + day.optLong("sessions") + " 段对话 · " + Math.round(day.optDouble("active", 0) / 60) + " 分钟";
    }

    private void sum() {
        long prompts = 0, sessions = 0; double seconds = 0;
        for (JSONObject data : stats.values()) {
            JSONObject day = day(data);
            prompts += day.optLong("prompts");
            sessions += day.optLong("sessions");
            seconds += day.optDouble("active", 0);
        }
        boolean hasData = !stats.isEmpty();
        if (metricPrompts != null) {
            metricPrompts.setText(hasData ? String.valueOf(prompts) : "—");
            metricPrompts.setTextColor(hasData ? kit.INK : kit.MUTED);
        }
        if (metricSessions != null) {
            metricSessions.setText(hasData ? String.valueOf(sessions) : "—");
            metricSessions.setTextColor(hasData ? kit.INK : kit.MUTED);
        }
        if (metricMinutes != null) {
            metricMinutes.setText(hasData ? String.valueOf(Math.round(seconds / 60)) : "—");
            metricMinutes.setTextColor(hasData ? kit.INK : kit.MUTED);
        }
        if (coverage != null) {
            coverage.setText(expected == 0 ? "添加电脑后显示统计"
                : answered < expected ? "读取中 " + stats.size() + "/" + expected
                : stats.size() < expected ? "已汇总 " + stats.size() + "/" + expected + " 台 · 未连接电脑未计入"
                : "已汇总 " + stats.size() + "/" + expected + " 台");
        }
    }

    private View wideAction(String title, String hint, String what, int tint) {
        LinearLayout row = kit.row();
        row.setPadding(kit.dp(14), kit.dp(13), kit.dp(14), kit.dp(13));
        row.setBackground(kit.shape(kit.PANEL, kit.rim(), 18));
        kit.elevate(row, 1.5f);
        View badge = kit.tile(R.drawable.ic_tab_together, tint, 38);
        ((LinearLayout.LayoutParams) badge.getLayoutParams()).rightMargin = kit.dp(12);
        row.addView(badge);
        LinearLayout copy = kit.column();
        copy.addView(kit.semibold(title, 15.5f, kit.INK));
        copy.addView(kit.text(hint, 12.5f, kit.MUTED), kit.below(2));
        row.addView(copy, new LinearLayout.LayoutParams(0, -2, 1));
        row.addView(kit.icon(R.drawable.ic_chevron, Kit.tint(kit.MUTED, 150), 15));
        kit.press(row, () -> pickTogether(title, what));
        return row;
    }

    private void pickTogether(String title, String what) {
        java.util.List<String> urls = new java.util.ArrayList<>();
        JSONArray saved = activity.computers();
        for (int i = 0; i < saved.length(); i++) if (saved.optJSONObject(i) != null) urls.add(saved.optJSONObject(i).optString("url"));
        activity.pick(title, urls, url -> activity.together(url, what));
    }

    private void teamRows(LinearLayout body, String url, JSONArray items) {
        View divider = new View(activity);
        divider.setBackgroundColor(kit.rim());
        body.addView(divider, new LinearLayout.LayoutParams(-1, kit.hairline()));
        for (int i = 0; i < items.length(); i++) {
            JSONObject item = items.optJSONObject(i); if (item == null) continue;
            String mode = "lead".equals(item.optString("mode")) ? "领队" : "写审";
            String rawState = item.optString("state");
            String state = "running".equals(rawState) ? "进行中" : "paused".equals(rawState) ? "已暂停" : "done".equals(rawState) ? "已完成" : "stopped".equals(rawState) ? "已停止" : "没有开始";
            String desc = state + " · " + mode + " · 第 " + item.optInt("round", 1) + " / " + item.optInt("rounds", 3) + " 轮";
            String note = item.optString("note");
            if (!note.isEmpty()) desc += " · " + note;
            kit.setting(body, R.drawable.ic_tab_together, TINT_AGY, item.optString("goal", "协作任务"), desc, "",
                "running".equals(rawState) ? kit.GOOD : "paused".equals(rawState) ? TINT_WARN : kit.MUTED,
                () -> activity.together(url, "team", item.optString("id")));
        }
    }

    private void actions(LinearLayout page, String first, String firstHint, String firstAction, int firstTint,
                         String second, String secondHint, String secondAction, int secondTint) {
        LinearLayout row = kit.row();
        row.addView(action(first, firstHint, firstAction, firstTint), new LinearLayout.LayoutParams(0, -1, 1));
        LinearLayout.LayoutParams beside = new LinearLayout.LayoutParams(0, -1, 1); beside.leftMargin = kit.dp(10);
        row.addView(action(second, secondHint, secondAction, secondTint), beside);
        page.addView(row, kit.below(10));
    }

    private View action(String title, String hint, String what, int tint) {
        LinearLayout tile = kit.column();
        tile.setPadding(kit.dp(14), kit.dp(14), kit.dp(14), kit.dp(13));
        tile.setMinimumHeight(kit.dp(104));
        int icon = what.equals("chain") ? R.drawable.ic_tab_together : what.equals("schedule") ? R.drawable.ic_clock
            : what.equals("handoff") ? R.drawable.ic_handoff : R.drawable.ic_say;
        tile.setBackground(kit.shape(kit.PANEL, kit.rim(), 18));
        kit.elevate(tile, 1.5f);
        tile.addView(kit.tile(icon, tint, 36));
        tile.addView(kit.semibold(title, 15.5f, kit.INK), kit.below(12));
        tile.addView(kit.text(hint, 12.5f, kit.MUTED), kit.below(2));
        kit.press(tile, () -> pickTogether(title, what));
        return tile;
    }

    private void rows(LinearLayout body, String url, JSONArray items, boolean chain) {
        int size = items == null ? 0 : items.length();
        View divider = new View(activity);
        divider.setBackgroundColor(kit.rim());
        body.addView(divider, new LinearLayout.LayoutParams(-1, kit.hairline()));
        if (size == 0) {
            TextView empty = kit.text(chain ? "暂无自动接力" : "暂无定时提问", 12.5f, kit.MUTED);
            empty.setPadding(kit.dp(14), kit.dp(10), kit.dp(14), kit.dp(10));
            body.addView(empty);
            return;
        }
        for (int i = 0; i < size; i++) {
            JSONObject item = items.optJSONObject(i); if (item == null) continue;
            String title = chain ? item.optString("from_title", "来源对话") + " → " + item.optString("to_title", "接手对话") : item.optString("text");
            String state = chain ? item.optInt("left") > 0 ? "还剩 " + item.optInt("left") + " 次" : "已用完"
                : item.optBoolean("on") && item.optLong("next") > 0 ? "下次 " + android.text.format.DateFormat.format("MM-dd HH:mm", item.optLong("next"))
                : "once".equals(item.optString("kind")) && item.optJSONObject("last") != null ? "已执行" : "已停用";
            JSONObject last = item.optJSONObject("last");
            String description = state + (chain ? " · " + item.optString("text") : " · " + item.optString("title", "对话"));
            if (last != null) description += " · " + (last.optBoolean("ok") ? "上次成功" : "上次失败：" + last.optString("note"));
            kit.setting(body, chain ? R.drawable.ic_tab_together : R.drawable.ic_clock, chain ? TINT_RELAY : TINT_WARN,
                title, description, "", kit.MUTED, () -> activity.together(url, chain ? "chain" : "schedule", item.optString("id")));
        }
    }
}
