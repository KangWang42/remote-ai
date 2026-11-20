package io.github.kangwang42.remotecli;

import android.graphics.drawable.GradientDrawable;
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
 * Two of the app's four lists, both over every computer: 协作 (dark multi-AI pipeline hero card, 2x2 calm
 * quick actions grid, and scheduled collaboration cards) and 我的 (quotas, today's KPI summary, connected
 * computers, and app settings). Each computer answers independently with its own cookie.
 */
final class AppPages {
    private static final int TINT_WARN = 0xff9a5b00, TINT_RELAY = 0xff5536c9, TINT_LOOK = 0xffc02669, TINT_AGY = 0xff5536c9;

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
        LinearLayout page = kit.page(16);
        View refreshAction = kit.circleAction(R.drawable.ic_refresh, "刷新", false, () -> activity.appPage(tab));
        page.addView(kit.head(mine ? "我的" : "协作", kit.text(mine ? "设备连接与个人偏好" : "交接上下文，让对话接着做", 14, kit.MUTED), refreshAction));
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
    private final java.util.Set<String> expandedQuotas = new java.util.HashSet<>();
    /** The tools' limits, first on 我的: primary pool per tool by default; secondary pools toggle open on tap. */
    void drawQuotas() {
        if (quotaCard == null) return;
        quotaCard.removeAllViews();
        List<QuotaLines.Reading> shown = bench.quotas();
        quotaCard.setVisibility(shown.isEmpty() ? View.GONE : View.VISIBLE);
        long now = System.currentTimeMillis() / 1000;
        LinearLayout head = kit.row();
        head.addView(kit.semibold("额度", 15, kit.INK), new LinearLayout.LayoutParams(0, -2, 1));
        quotaCard.addView(head);
        for (QuotaLines.Reading one : shown) {
            if ("api".equals(one.billingType)) quotaLine(quotaCard, "Codex · API 余额", "请在服务商后台查看", kit.MUTED, -1);
            if ("credits".equals(one.billingType)) quotaLine(quotaCard, "Codex · 额外额度", one.balance, kit.MUTED, -1);
            if (one.windows.isEmpty() && !one.why.isEmpty()) quotaLine(quotaCard, QuotaLines.tool(one.tool), one.why, kit.MUTED, -1);
            for (QuotaLines.Window w : QuotaLines.primary(one)) {
                quotaLine(quotaCard, QuotaLines.label(one.tool, w), QuotaLines.detail(w, now), QuotaLines.high(w) ? kit.BAD : kit.MUTED, w.used);
            }
            List<QuotaLines.Window> extra = QuotaLines.extra(one);
            if (!extra.isEmpty()) {
                boolean open = expandedQuotas.contains(one.tool);
                boolean warn = !open && QuotaLines.anyHigh(extra);
                int pools = QuotaLines.extraPoolCount(extra);
                LinearLayout toggle = kit.row();
                toggle.setPadding(kit.dp(12), kit.dp(9), kit.dp(12), kit.dp(9));
                toggle.setBackground(kit.shape(kit.subSurface(), 0, 12));
                toggle.addView(kit.text("其他 " + pools + " 个池子", 12.5f, kit.MUTED), new LinearLayout.LayoutParams(0, -2, 1));
                toggle.addView(kit.semibold((warn ? "接近上限 · " : "") + (open ? "收起 ▴" : "展开 ▾"), 12.5f, warn ? kit.BAD : kit.accentInk()));
                final String toolKey = one.tool;
                kit.press(toggle, () -> {
                    if (expandedQuotas.contains(toolKey)) expandedQuotas.remove(toolKey);
                    else expandedQuotas.add(toolKey);
                    drawQuotas();
                });
                LinearLayout.LayoutParams toggleLp = new LinearLayout.LayoutParams(-1, -2);
                toggleLp.topMargin = kit.dp(10);
                quotaCard.addView(toggle, toggleLp);
                if (open) {
                    LinearLayout sub = kit.column();
                    sub.setPadding(kit.dp(12), kit.dp(2), kit.dp(2), kit.dp(2));
                    LinearLayout.LayoutParams subLp = new LinearLayout.LayoutParams(-1, -2);
                    subLp.topMargin = kit.dp(4);
                    for (QuotaLines.Window w : extra) {
                        quotaLine(sub, QuotaLines.label(one.tool, w), QuotaLines.detail(w, now), QuotaLines.high(w) ? kit.BAD : kit.MUTED, w.used);
                    }
                    quotaCard.addView(sub, subLp);
                }
            }
        }
    }
    private void quotaLine(LinearLayout host, String label, String detail, int tone, double used) {
        LinearLayout line = kit.row();
        line.setPadding(0, kit.dp(12), 0, 0);
        line.addView(kit.medium(label, 14, kit.INK), new LinearLayout.LayoutParams(0, -2, 1));
        line.addView(kit.text(detail, 12, tone));
        host.addView(line);
        if (used < 0) return;
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
        host.addView(track, bar);
    }

    private void buildMe(LinearLayout page, JSONArray computers) {
        quotaCard = kit.cardBox(22);
        quotaCard.setPadding(kit.dp(16), kit.dp(16), kit.dp(16), kit.dp(16));
        page.addView(quotaCard, kit.below(18));
        drawQuotas();

        // Compact 3-metric KPI summary card for today's work
        LinearLayout summary = kit.cardBox(20);
        summary.setPadding(kit.dp(16), kit.dp(14), kit.dp(16), kit.dp(14));
        LinearLayout topRow = kit.row();
        topRow.addView(kit.semibold("今日工作", 15, kit.INK), new LinearLayout.LayoutParams(0, -2, 1));
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
        page.addView(summary, kit.below(14));

        // Unified "我的电脑" grouped list
        LinearLayout connections = kit.settingGroup(page, "我的电脑");
        for (int i = 0; i < computers.length(); i++) {
            JSONObject computer = computers.optJSONObject(i);
            if (computer == null) continue;
            final String url = computer.optString("url");
            final String name = computer.optString("name", MainActivity.host(url));
            if (connections.getChildCount() > 0) connections.addView(kit.insetDivider(64));
            LinearLayout row = kit.row();
            row.setPadding(kit.dp(14), kit.dp(12), kit.dp(12), kit.dp(12));
            row.setMinimumHeight(kit.dp(60));
            View badge = kit.tintedTile(R.drawable.ic_computer, kit.accentSoft(), kit.accentInk(), 36, 11);
            ((LinearLayout.LayoutParams) badge.getLayoutParams()).rightMargin = kit.dp(12);
            row.addView(badge);
            LinearLayout copy = kit.column();
            copy.addView(kit.line(name, 15, kit.INK, true));
            TextView sub = kit.line(MainActivity.host(url) + " · 正在读取统计…", 12.5f, kit.MUTED, false);
            copy.addView(sub, kit.below(2));
            deviceSummaries.put(url, sub);
            row.addView(copy, new LinearLayout.LayoutParams(0, -2, 1));

            TextView statChip = kit.semibold("统计", 12.5f, kit.accentInk());
            statChip.setGravity(Gravity.CENTER);
            statChip.setMinHeight(kit.dp(48));
            statChip.setPadding(kit.dp(10), 0, kit.dp(10), 0);
            kit.press(statChip, () -> activity.computerPage(url, "me"));
            row.addView(statChip);

            row.addView(kit.icon(R.drawable.ic_chevron, Kit.tint(kit.MUTED, 150), 16));
            kit.press(row, () -> activity.manage(url));
            connections.addView(row);
        }
        kit.setting(connections, R.drawable.ic_plus, kit.accentInk(), "添加电脑", "扫描电脑端的连接二维码", "", kit.accentInk(), activity::scan);

        // Grouped settings
        LinearLayout preferences = kit.settingGroup(page, "提醒与偏好");
        kit.settingSwitch(preferences, R.drawable.ic_bell, TINT_WARN, "任务提醒", "需要确认或一轮完成时通知", activity.watching(), activity::toggleWatch);
        String remStatus = activity.reminderStatus();
        int remColor = "已开启".equals(remStatus) ? kit.goodInk() : "需授权".equals(remStatus) ? kit.BAD : kit.MUTED;
        kit.setting(preferences, R.drawable.ic_shield, kit.goodInk(), "提醒与权限", "通知、电池与后台联网", remStatus, remColor, activity::reminderSettings);
        kit.setting(preferences, R.drawable.ic_palette, TINT_LOOK, "外观与文字", "主题、字号与终端阅读方式", activity.appearanceName(), kit.MUTED, activity::look);

        LinearLayout updates = kit.settingGroup(page, "版本与更新");
        kit.setting(updates, R.drawable.ic_update, TINT_AGY, "手机 App 更新", "检查手机安装包更新", "v" + activity.versionName(), kit.MUTED, activity::checkUpdate);
        kit.settingSwitch(updates, R.drawable.ic_refresh, kit.MUTED, "自动检查手机更新", "发现新版时提示，安装前由你确认", activity.autoUpdate(), activity::toggleAutoUpdate);
    }

    private TextView metricCell(LinearLayout parent, String label, boolean withDivider) {
        if (withDivider) {
            View divider = new View(activity);
            divider.setBackgroundColor(kit.dividerColor());
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(kit.hairline(), kit.dp(34));
            params.setMargins(kit.dp(6), 0, kit.dp(6), 0);
            parent.addView(divider, params);
        }
        LinearLayout cell = kit.column();
        cell.setPadding(kit.dp(4), 0, kit.dp(4), 0);
        TextView value = kit.figure("—", 23, kit.MUTED);
        cell.addView(value);
        cell.addView(kit.text(label, 12.5f, kit.MUTED), kit.below(2));
        parent.addView(cell, new LinearLayout.LayoutParams(0, -2, 1));
        return value;
    }

    private void buildTogether(LinearLayout page, JSONArray computers) {
        if (expected > 0) {
            page.addView(heroCollabCard(), kit.below(18));

            TextView quickLabel = kit.semibold("快速协作", 15, kit.INK);
            quickLabel.setPadding(kit.dp(4), 0, kit.dp(4), 0);
            page.addView(quickLabel, kit.below(20));

            actions(page,
                "交给另一个 AI", "带上上下文接着做", "handoff",
                "同时提问", "一个问题发给多段对话", "many");
            actions(page,
                "定时提问", "到点由电脑替你问", "schedule",
                "自动接力", "一轮结束后自动转交", "chain");
        } else {
            LinearLayout empty = kit.cardBox(22);
            empty.setPadding(kit.dp(20), kit.dp(24), kit.dp(20), kit.dp(24));
            empty.addView(kit.text("添加电脑后，可发起多 AI 协作任务，交接对话，或安排接力和定时提问。", 14, kit.MUTED));
            empty.addView(kit.button("扫码添加电脑", 0, activity::scan), kit.below(16));
            page.addView(empty, kit.below(18));
            return;
        }

        for (int i = 0; i < computers.length(); i++) {
            JSONObject computer = computers.optJSONObject(i);
            if (computer == null) continue;
            final String url = computer.optString("url");
            final String name = computer.optString("name", MainActivity.host(url));

            LinearLayout sectionHead = kit.row();
            sectionHead.setPadding(kit.dp(4), 0, kit.dp(4), 0);
            sectionHead.addView(kit.semibold("已安排", 15, kit.INK), new LinearLayout.LayoutParams(0, -2, 1));
            TextView who = kit.text(name, 13, kit.MUTED);
            sectionHead.addView(who);
            kit.press(sectionHead, () -> activity.computerPage(url, "together"));
            page.addView(sectionHead, kit.below(i == 0 ? 22 : 16));

            LinearLayout body = kit.column();
            LinearLayout loadingCard = kit.cardBox(22);
            TextView loading = kit.text("正在读取…", 13, kit.MUTED);
            loading.setPadding(kit.dp(16), kit.dp(16), kit.dp(16), kit.dp(16));
            loadingCard.addView(loading);
            body.addView(loadingCard);
            holders.put(url, body);
            page.addView(body, kit.below(10));
        }
    }

    /** Soft tinted hero collaboration card with CC -> CX -> AG pipeline strip and brand accent CTA. */
    private View heroCollabCard() {
        LinearLayout card = kit.column();
        card.setPadding(kit.dp(18), kit.dp(18), kit.dp(18), kit.dp(18));
        card.setBackground(kit.shape(kit.heroTintBg(), kit.heroBorder(), 24));
        kit.elevate(card, 1.5f);

        // Pipeline strip: [CC] --- [CX] --- [AG]
        LinearLayout pipe = kit.row();
        pipe.addView(heroBadge("CC", kit.toolBadgeBg("claude"), kit.toolBadgeFg("claude")));
        pipe.addView(flowLine(), flowLp());
        pipe.addView(heroBadge("CX", kit.toolBadgeBg("codex"), kit.toolBadgeFg("codex")));
        pipe.addView(flowLine(), flowLp());
        pipe.addView(heroBadge("AG", kit.toolBadgeBg("agy"), kit.toolBadgeFg("agy")));
        card.addView(pipe);

        card.addView(kit.semibold("协作任务", 19, kit.heroFg()), kit.below(16));
        card.addView(kit.text("写审循环或分工协作，让多个 AI 配合完成一件事", 13.5f, kit.heroMuted()), kit.below(4));

        TextView cta = kit.semibold("＋ 新建协作任务", 14, kit.onSolidInk());
        cta.setGravity(Gravity.CENTER);
        cta.setMinHeight(kit.dp(48));
        cta.setBackground(kit.shape(kit.solidInk(), 0, 14));
        kit.press(cta, () -> pickTogether("协作任务", "team"));
        card.addView(cta, kit.below(16));
        return card;
    }

    private View heroBadge(String mono, int bg, int fg) {
        TextView badge = kit.bold(mono, 12, fg);
        badge.setGravity(Gravity.CENTER);
        badge.setIncludeFontPadding(false);
        badge.setBackground(kit.shape(bg, 0, 12));
        badge.setLayoutParams(new LinearLayout.LayoutParams(kit.dp(40), kit.dp(40)));
        return badge;
    }

    private View flowLine() {
        View line = new View(activity);
        GradientDrawable dash = new GradientDrawable();
        dash.setShape(GradientDrawable.LINE);
        dash.setStroke(kit.dp(2), Kit.tint(kit.ACCENT, Kit.light(kit.BG) ? 84 : 110), kit.dp(5), kit.dp(4));
        line.setBackground(dash);
        line.setLayerType(View.LAYER_TYPE_SOFTWARE, null);
        return line;
    }

    private LinearLayout.LayoutParams flowLp() {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, kit.dp(4), 1);
        lp.leftMargin = kit.dp(8); lp.rightMargin = kit.dp(8);
        return lp;
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
                        LinearLayout errCard = kit.cardBox(22);
                        addRowMessage(errCard, "暂时无法读取：" + (problem.isEmpty() ? "请确认电脑在线" : problem), kit.BAD);
                        body.addView(errCard);
                    } else {
                        JSONArray teams = data.optJSONArray("teams");
                        JSONArray chains = data.optJSONArray("chains");
                        JSONArray schedules = data.optJSONArray("schedules");
                        String chainErr = data.optString("chain_error");
                        String schedErr = data.optString("schedule_error");
                        int count = (teams == null ? 0 : teams.length()) + (chains == null ? 0 : chains.length()) + (schedules == null ? 0 : schedules.length());
                        if (count == 0 && chainErr.isEmpty() && schedErr.isEmpty()) {
                            body.addView(emptyScheduledCard(url));
                        } else {
                            LinearLayout card = kit.cardBox(22);
                            if (teams != null && teams.length() > 0) teamRows(card, url, teams);
                            if (!schedErr.isEmpty()) addRowMessage(card, "定时提问暂不可用：" + schedErr, kit.BAD);
                            else if (schedules != null && schedules.length() > 0) rows(card, url, schedules, false);
                            if (!chainErr.isEmpty()) addRowMessage(card, "接力暂不可用：" + chainErr, kit.BAD);
                            else if (chains != null && chains.length() > 0) rows(card, url, chains, true);
                            body.addView(card);
                        }
                    }
                });
            });
        }
    }

    /** Crisp dashed empty state card with quick-add pill buttons matching the design. */
    private View emptyScheduledCard(String url) {
        LinearLayout card = kit.column();
        card.setGravity(Gravity.CENTER_HORIZONTAL);
        card.setPadding(kit.dp(18), kit.dp(22), kit.dp(18), kit.dp(22));
        GradientDrawable dashed = kit.shape(kit.PANEL, 0, 22);
        dashed.setStroke(kit.dp(1), Kit.light(kit.BG) ? 0xffd5d9e0 : kit.LINE, kit.dp(6), kit.dp(4));
        card.setBackground(dashed);

        card.addView(kit.semibold("还没有安排的协作", 14.5f, kit.INK));
        card.addView(kit.text("定时提问和自动接力会出现在这里", 12.5f, kit.MUTED), kit.below(4));

        LinearLayout pills = kit.row();
        pills.setGravity(Gravity.CENTER_HORIZONTAL);
        pills.addView(emptyQuickPill("＋ 定时提问", () -> activity.together(url, "schedule")));
        View second = emptyQuickPill("＋ 自动接力", () -> activity.together(url, "chain"));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-2, -2);
        lp.leftMargin = kit.dp(8);
        pills.addView(second, lp);
        LinearLayout.LayoutParams pLp = new LinearLayout.LayoutParams(-2, -2);
        pLp.topMargin = kit.dp(14);
        card.addView(pills, pLp);
        return card;
    }

    private View emptyQuickPill(String label, Runnable action) {
        TextView btn = kit.medium(label, 13, kit.INK);
        btn.setGravity(Gravity.CENTER);
        btn.setMinHeight(kit.dp(48));
        btn.setPadding(kit.dp(14), 0, kit.dp(14), 0);
        btn.setBackground(kit.shape(kit.PANEL, kit.cardBorder(), 99));
        kit.press(btn, action);
        return btn;
    }

    private void addRowMessage(LinearLayout body, String text, int color) {
        TextView note = kit.text(text, 13, color);
        note.setPadding(kit.dp(16), kit.dp(12), kit.dp(16), kit.dp(12));
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

    private void pickTogether(String title, String what) {
        java.util.List<String> urls = new java.util.ArrayList<>();
        JSONArray saved = activity.computers();
        for (int i = 0; i < saved.length(); i++) if (saved.optJSONObject(i) != null) urls.add(saved.optJSONObject(i).optString("url"));
        activity.pick(title, urls, url -> activity.together(url, what));
    }

    private void teamRows(LinearLayout body, String url, JSONArray items) {
        for (int i = 0; i < items.length(); i++) {
            JSONObject item = items.optJSONObject(i); if (item == null) continue;
            String rawMode = item.optString("mode");
            String mode = "lead".equals(rawMode) ? "分工协作" : "compare".equals(rawMode) ? "比稿" : "写审";
            String rawState = item.optString("state");
            String phase = item.optString("phase");
            boolean confirm = item.optBoolean("confirm");
            String state = confirm ? "等你确认"
                : "running".equals(rawState) ? ("verify".equals(phase) ? "领队验收" : "discuss".equals(phase) ? "讨论方案" : "进行中")
                : "choose".equals(rawState) ? "待选方案" : "paused".equals(rawState) ? "已暂停"
                : "done".equals(rawState) ? "已完成" : "stopped".equals(rawState) ? "已停止" : "没有开始";
            String desc = state + " · " + mode + ("compare".equals(rawMode) ? "" : " · 第 " + item.optInt("round", 1) + " / " + item.optInt("rounds", 3) + " 轮");
            String note = item.optString("note");
            if (!note.isEmpty()) desc += " · " + note;
            kit.setting(body, R.drawable.ic_tab_together, TINT_AGY, item.optString("goal", "协作任务"), desc, "",
                confirm ? kit.BAD : "running".equals(rawState) ? ("verify".equals(phase) ? TINT_WARN : kit.goodInk()) : ("paused".equals(rawState) || "choose".equals(rawState)) ? TINT_WARN : kit.MUTED,
                () -> activity.together(url, "team", item.optString("id")));
        }
    }

    private void actions(LinearLayout page, String first, String firstHint, String firstAction,
                         String second, String secondHint, String secondAction) {
        LinearLayout row = kit.row();
        row.addView(action(first, firstHint, firstAction), new LinearLayout.LayoutParams(0, -1, 1));
        LinearLayout.LayoutParams beside = new LinearLayout.LayoutParams(0, -1, 1); beside.leftMargin = kit.dp(10);
        row.addView(action(second, secondHint, secondAction), beside);
        page.addView(row, kit.below(10));
    }

    private View action(String title, String hint, String what) {
        LinearLayout tile = kit.cardBox(20);
        tile.setPadding(kit.dp(14), kit.dp(14), kit.dp(14), kit.dp(14));
        tile.setMinimumHeight(kit.dp(120));
        int icon = what.equals("chain") ? R.drawable.ic_tab_together : what.equals("schedule") ? R.drawable.ic_clock
            : what.equals("handoff") ? R.drawable.ic_handoff : R.drawable.ic_say;
        tile.addView(kit.tintedTile(icon, kit.RAISED, kit.INK, 38, 11));
        View spacer = new View(activity);
        tile.addView(spacer, new LinearLayout.LayoutParams(-1, 0, 1));
        tile.addView(kit.semibold(title, 15, kit.INK), kit.below(12));
        tile.addView(kit.text(hint, 12.5f, kit.MUTED), kit.below(3));
        kit.press(tile, () -> pickTogether(title, what));
        return tile;
    }

    private void rows(LinearLayout body, String url, JSONArray items, boolean chain) {
        int size = items == null ? 0 : items.length();
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
