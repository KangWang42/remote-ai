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
    private final StatsStore statsStore;
    private final Map<String, LinearLayout> holders = new HashMap<>();
    private final Map<String, TextView> deviceSummaries = new HashMap<>();
    private final Map<String, JSONObject> stats = new HashMap<>();
    private final Map<String, Long> statsSavedAt = new HashMap<>();
    private final java.util.Set<String> statsLive = new java.util.HashSet<>();
    private TextView metricPrompts, metricSessions, metricMinutes, deltaPrompts, deltaSessions, deltaMinutes, coverage, streakBadge, chartInspector, weekFooter;
    private StatsCharts.DailyBarsView dailyChart;
    private LinearLayout toolLegend;
    private StatsRollup.Summary currentSummary;
    private int pickedDay = -1;
    private int generation, answered, expected;

    AppPages(MainActivity activity, Kit kit, Workbench bench) {
        this.activity = activity;
        this.kit = kit;
        this.bench = bench;
        this.statsStore = new StatsStore(activity);
    }

    View build(String tab) {
        generation++; holders.clear(); deviceSummaries.clear(); stats.clear(); statsSavedAt.clear(); statsLive.clear(); answered = 0; quotaMark = ""; pickedDay = -1; currentSummary = null;
        boolean mine = "me".equals(tab);
        LinearLayout page = kit.page(16);
        final View[] refreshHolder = new View[1];
        View refreshAction = kit.circleAction(R.drawable.ic_refresh, "刷新", false, () -> {
            Motion.spinOnce(refreshHolder[0]);
            bench.refresh();
            if (expected > 0) refresh(tab);
            else activity.appPage(tab);
        });
        refreshHolder[0] = refreshAction;
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
    private String quotaMark = "";
    private final java.util.Set<String> expandedQuotas = new java.util.HashSet<>();
    /** The tools' remaining limits, first on 我的: primary pool per tool by default; secondary pools toggle open on tap. */
    void drawQuotas() {
        if (quotaCard == null) return;
        List<QuotaLines.Reading> shown = bench.quotas();
        long nowMs = System.currentTimeMillis();
        long now = nowMs / 1000;
        StringBuilder sig = new StringBuilder();
        sig.append(now / 60).append('|');
        for (QuotaLines.Reading one : shown) {
            sig.append(one.tool).append(':').append(one.at).append(':').append(one.why).append(':')
                .append(one.billingType).append(':').append(one.balance).append(':')
                .append(expandedQuotas.contains(one.tool)).append(';');
            for (QuotaLines.Window w : one.windows) {
                sig.append(w.group).append('/').append(w.name).append('=').append(Math.round(QuotaLines.remaining(w.used))).append('@').append(w.resets).append(',');
            }
        }
        String nextMark = sig.toString();
        if (nextMark.equals(quotaMark)) return;
        quotaMark = nextMark;
        quotaCard.removeAllViews();
        quotaCard.setVisibility(shown.isEmpty() ? View.GONE : View.VISIBLE);
        if (shown.isEmpty()) return;
        LinearLayout head = kit.row();
        head.addView(kit.semibold("剩余额度", 15, kit.INK), new LinearLayout.LayoutParams(0, -2, 1));
        long newest = QuotaLines.newestAt(shown);
        String ago = AggregateSessions.ago(newest, nowMs);
        if (!ago.isEmpty()) head.addView(kit.text(ago + "更新", 12, kit.MUTED));
        quotaCard.addView(head);
        int barIndex = 0;
        for (QuotaLines.Reading one : shown) {
            if ("api".equals(one.billingType)) quotaLine(quotaCard, "Codex · API 余额", one.balance.isEmpty() ? "暂未读到余额" : one.balance, kit.MUTED, -1, -1);
            if ("credits".equals(one.billingType)) quotaLine(quotaCard, "Codex · 额外额度", one.balance, kit.MUTED, -1, -1);
            if (one.windows.isEmpty() && !one.why.isEmpty()) quotaLine(quotaCard, QuotaLines.tool(one.tool), one.why, kit.MUTED, -1, -1);
            for (QuotaLines.Window w : QuotaLines.primary(one)) {
                quotaLine(quotaCard, QuotaLines.label(one.tool, w), QuotaLines.detail(w, now), QuotaLines.high(w) ? kit.BAD : kit.MUTED, w.used, barIndex++);
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
                toggle.addView(kit.semibold((warn ? "余量不足 · " : "") + (open ? "收起 ▴" : "展开 ▾"), 12.5f, warn ? kit.BAD : kit.accentInk()));
                final String toolKey = one.tool;
                kit.press(toggle, () -> {
                    if (expandedQuotas.contains(toolKey)) expandedQuotas.remove(toolKey);
                    else expandedQuotas.add(toolKey);
                    drawQuotas();
                });
                Motion.springPress(toggle, 0.98f);
                LinearLayout.LayoutParams toggleLp = new LinearLayout.LayoutParams(-1, -2);
                toggleLp.topMargin = kit.dp(10);
                quotaCard.addView(toggle, toggleLp);
                if (open) {
                    LinearLayout sub = kit.column();
                    sub.setPadding(kit.dp(12), kit.dp(2), kit.dp(2), kit.dp(2));
                    LinearLayout.LayoutParams subLp = new LinearLayout.LayoutParams(-1, -2);
                    subLp.topMargin = kit.dp(4);
                    int extraIdx = 0;
                    for (QuotaLines.Window w : extra) {
                        quotaLine(sub, QuotaLines.label(one.tool, w), QuotaLines.detail(w, now), QuotaLines.high(w) ? kit.BAD : kit.MUTED, w.used, extraIdx++);
                    }
                    quotaCard.addView(sub, subLp);
                    Motion.riseIn(sub, kit.dp(6), 0);
                }
            }
        }
    }
    private void quotaLine(LinearLayout host, String label, String detail, int tone, double used, int barIndex) {
        LinearLayout line = kit.row();
        line.setPadding(0, kit.dp(12), 0, 0);
        line.addView(kit.medium(label, 14, kit.INK), new LinearLayout.LayoutParams(0, -2, 1));
        boolean low = used >= 0 && QuotaLines.level(used) == QuotaLines.Level.HIGH;
        if (low) {
            View dot = kit.dot(kit.BAD, 6);
            ((LinearLayout.LayoutParams) dot.getLayoutParams()).rightMargin = kit.dp(5);
            Motion.pulse(dot, 0.32f, 1.25f, 1600);
            line.addView(dot);
            line.addView(kit.semibold(detail, 12, tone));
        } else {
            line.addView(kit.text(detail, 12, tone));
        }
        host.addView(line);
        if (used < 0) return;
        LinearLayout track = new LinearLayout(activity);
        track.setBackground(kit.shape(kit.RAISED, 0, 3));
        track.setClipToOutline(true);
        QuotaLines.Level level = QuotaLines.level(used);
        int fill = level == QuotaLines.Level.HIGH ? kit.BAD : level == QuotaLines.Level.ELEVATED ? kit.BUSY : kit.ACCENT;
        float share = QuotaLines.barShare(used);
        if (share > 0f) {
            View part = new View(activity);
            part.setBackground(kit.shape(fill, 0, 3));
            track.addView(part, new LinearLayout.LayoutParams(0, -1, share));
            Motion.revealBar(part, barIndex);
        }
        if (share < 100f) track.addView(new View(activity), new LinearLayout.LayoutParams(0, -1, 100f - share));
        LinearLayout.LayoutParams bar = new LinearLayout.LayoutParams(-1, kit.dp(6));
        bar.topMargin = kit.dp(6);
        host.addView(track, bar);
    }

    private void buildMe(LinearLayout page, JSONArray computers) {
        quotaCard = kit.cardBox(22);
        quotaCard.setPadding(kit.dp(16), kit.dp(16), kit.dp(16), kit.dp(16));
        Motion.smoothLayout(quotaCard);
        page.addView(quotaCard, kit.below(18));
        drawQuotas();
        if (quotaCard.getVisibility() == View.VISIBLE) Motion.riseIn(quotaCard, kit.dp(10), 0);

        // Upgraded daily work statistics card with cached continuity + 7-day stacked trend chart
        LinearLayout summary = kit.cardBox(22);
        summary.setPadding(kit.dp(16), kit.dp(15), kit.dp(16), kit.dp(14));
        LinearLayout topRow = kit.row();
        topRow.addView(kit.semibold("工作统计", 15.5f, kit.INK));
        streakBadge = kit.semibold("", 11, kit.goodInk());
        streakBadge.setPadding(kit.dp(8), kit.dp(2), kit.dp(8), kit.dp(2));
        streakBadge.setBackground(kit.shape(kit.goodSoft(), 0, 99));
        streakBadge.setVisibility(View.GONE);
        LinearLayout.LayoutParams streakLp = new LinearLayout.LayoutParams(-2, -2);
        streakLp.leftMargin = kit.dp(8);
        topRow.addView(streakBadge, streakLp);
        topRow.addView(new View(activity), new LinearLayout.LayoutParams(0, 1, 1));
        if (expected > 0) {
            TextView detailPill = kit.semibold("详情 ›", 12.5f, kit.accentInk());
            detailPill.setGravity(Gravity.CENTER);
            detailPill.setPadding(kit.dp(10), kit.dp(4), kit.dp(10), kit.dp(4));
            detailPill.setBackground(kit.shape(kit.accentSoft(), 0, 99));
            kit.press(detailPill, () -> {
                java.util.List<String> urls = new java.util.ArrayList<>();
                JSONArray saved = activity.computers();
                for (int i = 0; i < saved.length(); i++) if (saved.optJSONObject(i) != null) urls.add(saved.optJSONObject(i).optString("url"));
                activity.pick("查看哪台电脑的工作统计", urls, url -> activity.computerPage(url, "me"));
            });
            Motion.springPress(detailPill, 0.94f);
            topRow.addView(detailPill);
        }
        summary.addView(topRow);

        coverage = kit.text(expected == 0 ? "添加电脑后显示统计" : "正在读取…", 12, kit.MUTED);
        summary.addView(coverage, kit.below(3));

        LinearLayout metrics = kit.row();
        metrics.setPadding(0, kit.dp(12), 0, kit.dp(4));
        TextView[] pCell = metricCell(metrics, "今日提问", false);
        metricPrompts = pCell[0]; deltaPrompts = pCell[1];
        TextView[] sCell = metricCell(metrics, "活跃对话", true);
        metricSessions = sCell[0]; deltaSessions = sCell[1];
        TextView[] mCell = metricCell(metrics, "工作时长", true);
        metricMinutes = mCell[0]; deltaMinutes = mCell[1];
        summary.addView(metrics);

        View chartDivider = new View(activity);
        chartDivider.setBackgroundColor(kit.dividerColor());
        summary.addView(chartDivider, new LinearLayout.LayoutParams(-1, kit.hairline()));
        ((LinearLayout.LayoutParams) chartDivider.getLayoutParams()).topMargin = kit.dp(10);

        chartInspector = kit.medium("近 7 天趋势", 12.5f, kit.INK);
        chartInspector.setVisibility(View.GONE);
        summary.addView(chartInspector, kit.below(10));

        dailyChart = new StatsCharts.DailyBarsView(activity, kit);
        dailyChart.setVisibility(View.GONE);
        dailyChart.setOnPick(idx -> {
            pickedDay = idx;
            updateInspector();
        });
        LinearLayout.LayoutParams chartLp = new LinearLayout.LayoutParams(-1, kit.dp(116));
        chartLp.topMargin = kit.dp(6);
        summary.addView(dailyChart, chartLp);

        LinearLayout footRow = kit.row();
        footRow.setPadding(0, kit.dp(8), 0, 0);
        toolLegend = kit.row();
        footRow.addView(toolLegend, new LinearLayout.LayoutParams(0, -2, 1));
        weekFooter = kit.text("", 11.5f, kit.MUTED);
        footRow.addView(weekFooter);
        footRow.setVisibility(View.GONE);
        footRow.setTag("stats-footer");
        summary.addView(footRow);

        page.addView(summary, kit.below(14));
        Motion.riseIn(summary, kit.dp(10), 45);

        // Unified "我的电脑" grouped list
        LinearLayout connections = kit.settingGroup(page, "我的电脑");
        java.util.List<String> activeUrls = new java.util.ArrayList<>();
        for (int i = 0; i < computers.length(); i++) {
            JSONObject computer = computers.optJSONObject(i);
            if (computer == null) continue;
            final String url = computer.optString("url");
            activeUrls.add(url);
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
            StatsStore.Entry cached = statsStore.load(url);
            String initialSub = MainActivity.host(url) + " · 正在读取统计…";
            if (cached != null) {
                stats.put(url, cached.raw);
                statsSavedAt.put(url, cached.savedAtMs);
                initialSub = today(cached.raw);
            }
            TextView sub = kit.line(initialSub, 12.5f, kit.MUTED, false);
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
        statsStore.prune(activeUrls);
        if (!stats.isEmpty()) sum();
        kit.setting(connections, R.drawable.ic_plus, kit.accentInk(), "添加电脑", "扫描电脑端的连接二维码", "", kit.accentInk(), activity::scan);

        // Grouped settings
        LinearLayout preferences = kit.settingGroup(page, "提醒与偏好");
        kit.settingSwitch(preferences, R.drawable.ic_bell, kit.busyInk(), "任务提醒", "需要确认或一轮完成时通知", activity.watching(), activity::toggleWatch);
        String remStatus = activity.reminderStatus();
        int remColor = "已开启".equals(remStatus) ? kit.goodInk() : "需授权".equals(remStatus) ? kit.BAD : kit.MUTED;
        kit.setting(preferences, R.drawable.ic_shield, kit.goodInk(), "提醒与权限", "通知、电池与后台联网", remStatus, remColor, activity::reminderSettings);
        kit.setting(preferences, R.drawable.ic_palette, kit.accentInk(), "外观与文字", "主题、字号与终端阅读方式", activity.appearanceName(), kit.MUTED, activity::look);
        if (expected > 0) {
            kit.setting(preferences, R.drawable.ic_settings, TINT_AGY, "模型与常用参数", "可视化设置模型、思考深度与权限模式", "", kit.MUTED, () -> {
                java.util.List<String> urls = new java.util.ArrayList<>();
                JSONArray saved = activity.computers();
                for (int i = 0; i < saved.length(); i++) if (saved.optJSONObject(i) != null) urls.add(saved.optJSONObject(i).optString("url"));
                activity.pick("选择电脑", urls, url -> activity.computerSettings(url, "part-team-defaults"));
            });
        }

        LinearLayout updates = kit.settingGroup(page, "版本与更新");
        kit.setting(updates, R.drawable.ic_update, kit.toolBadgeFg("agy"), "手机 App 更新", "检查手机安装包更新", "v" + activity.versionName(), kit.MUTED, activity::checkUpdate);
        kit.settingSwitch(updates, R.drawable.ic_refresh, kit.MUTED, "自动检查手机更新", "发现新版时提示，安装前由你确认", activity.autoUpdate(), activity::toggleAutoUpdate);
        kit.settingSwitch(updates, R.drawable.ic_update, kit.MUTED, "接收预发布版本", "关闭时只接收正式版", activity.previewUpdates(), activity::togglePreviewUpdates);
    }

    private TextView[] metricCell(LinearLayout parent, String label, boolean withDivider) {
        if (withDivider) {
            View divider = new View(activity);
            divider.setBackgroundColor(kit.dividerColor());
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(kit.hairline(), kit.dp(42));
            params.setMargins(kit.dp(6), 0, kit.dp(6), 0);
            parent.addView(divider, params);
        }
        LinearLayout cell = kit.column();
        cell.setPadding(kit.dp(4), 0, kit.dp(4), 0);
        TextView value = kit.figure("—", 21, kit.MUTED);
        value.setSingleLine(true);
        cell.addView(value);
        cell.addView(kit.text(label, 12, kit.MUTED), kit.below(2));
        TextView delta = kit.text("", 11, kit.MUTED);
        delta.setSingleLine(true);
        cell.addView(delta, kit.below(1));
        parent.addView(cell, new LinearLayout.LayoutParams(0, -2, 1));
        return new TextView[] { value, delta };
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

        // Pipeline strip: [CC] ---> [CX] ---> [AG] with live animated flow
        LinearLayout pipe = kit.row();
        pipe.addView(heroBadge("CC", kit.toolBadgeBg("claude"), kit.toolBadgeFg("claude")));
        pipe.addView(flowLine(0), flowLp());
        pipe.addView(heroBadge("CX", kit.toolBadgeBg("codex"), kit.toolBadgeFg("codex")));
        pipe.addView(flowLine(520), flowLp());
        pipe.addView(heroBadge("AG", kit.toolBadgeBg("agy"), kit.toolBadgeFg("agy")));
        card.addView(pipe);

        card.addView(kit.semibold("协作任务", 19, kit.heroFg()), kit.below(16));
        card.addView(kit.text("写审循环或分工协作，让多个 AI 配合完成一件事", 13.5f, kit.heroMuted()), kit.below(4));

        TextView cta = kit.semibold("＋ 新建协作任务", 14, kit.onSolidInk());
        cta.setGravity(Gravity.CENTER);
        cta.setMinHeight(kit.dp(48));
        cta.setBackground(kit.shape(kit.solidInk(), 0, 14));
        kit.press(cta, () -> pickTogether("协作任务", "team"));
        Motion.springPress(cta, 0.97f);
        card.addView(cta, kit.below(16));
        Motion.riseIn(card, kit.dp(10), 0);
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

    private View flowLine(long phaseOffsetMs) {
        int dashColor = Kit.tint(kit.ACCENT, Kit.light(kit.BG) ? 84 : 110);
        return new Motion.FlowLineView(activity, dashColor, kit.ACCENT, activity.getResources().getDisplayMetrics().density, phaseOffsetMs);
    }

    private LinearLayout.LayoutParams flowLp() {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, kit.dp(14), 1);
        lp.leftMargin = kit.dp(8); lp.rightMargin = kit.dp(8);
        return lp;
    }

    private JSONObject ask(String url, String cookie, String action) throws Exception {
        return bench.post(url + "/api/work", cookie, new JSONObject().put("id", UUID.randomUUID().toString().replace("-", "")).put("action", action).put("days", 7));
    }

    void refresh(String tab) {
        final int request = ++generation;
        if ("me".equals(tab)) {
            statsLive.clear();
            answered = 0;
        } else {
            stats.clear();
            answered = 0;
        }
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
                            JSONObject cached = stats.get(url);
                            if (sub != null) {
                                if (cached != null) {
                                    sub.setText("离线缓存 · " + today(cached));
                                } else {
                                    sub.setText("离线 · " + (problem.isEmpty() ? "请确认电脑在线" : problem));
                                }
                                sub.setTextColor(kit.MUTED);
                            }
                        } else {
                            long nowMs = System.currentTimeMillis();
                            stats.put(url, data);
                            statsSavedAt.put(url, nowMs);
                            statsLive.add(url);
                            statsStore.save(url, data, nowMs);
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
        Motion.springPress(btn, 0.95f);
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
        return "今天 " + day.optLong("prompts") + " 次提问 · " + day.optLong("sessions") + " 段对话 · " + StatsRollup.formatDuration(Math.round(day.optDouble("active", 0)));
    }

    private void updateInspector() {
        if (chartInspector == null || currentSummary == null || currentSummary.days.isEmpty()) return;
        int idx = pickedDay >= 0 && pickedDay < currentSummary.days.size() ? pickedDay : currentSummary.days.size() - 1;
        boolean isToday = idx == currentSummary.days.size() - 1;
        chartInspector.setText(StatsRollup.dayInspector(currentSummary.days.get(idx), isToday));
    }

    private void sum() {
        java.util.List<StatsRollup.ComputerSnapshot> snapshots = new java.util.ArrayList<>();
        long newestSaved = 0;
        for (Map.Entry<String, JSONObject> entry : stats.entrySet()) {
            Long savedAt = statsSavedAt.get(entry.getKey());
            long ts = savedAt != null ? savedAt : 0;
            if (ts > newestSaved) newestSaved = ts;
            snapshots.add(StatsStore.parse(entry.getValue(), ts));
        }
        StatsRollup.Summary summary = StatsRollup.aggregate(snapshots, 7);
        currentSummary = summary;
        boolean hasData = !stats.isEmpty() && !summary.days.isEmpty();

        if (metricPrompts != null) {
            if (hasData) Motion.countTo(metricPrompts, summary.todayPrompts); else metricPrompts.setText("—");
            metricPrompts.setTextColor(hasData ? kit.INK : kit.MUTED);
        }
        if (metricSessions != null) {
            if (hasData) Motion.countTo(metricSessions, summary.todaySessions); else metricSessions.setText("—");
            metricSessions.setTextColor(hasData ? kit.INK : kit.MUTED);
        }
        if (metricMinutes != null) {
            metricMinutes.setText(hasData ? StatsRollup.formatDuration(summary.todayActiveSec) : "—");
            metricMinutes.setTextColor(hasData ? kit.INK : kit.MUTED);
        }
        if (deltaPrompts != null) {
            deltaPrompts.setText(hasData ? StatsRollup.formatDelta(summary.todayPrompts, summary.yesterdayPrompts, summary.hasYesterday) : "");
            deltaPrompts.setTextColor(hasData && summary.hasYesterday && summary.todayPrompts > summary.yesterdayPrompts ? kit.goodInk() : kit.MUTED);
        }
        if (deltaSessions != null) {
            deltaSessions.setText(hasData ? StatsRollup.formatDelta(summary.todaySessions, summary.yesterdaySessions, summary.hasYesterday) : "");
            deltaSessions.setTextColor(hasData && summary.hasYesterday && summary.todaySessions > summary.yesterdaySessions ? kit.goodInk() : kit.MUTED);
        }
        if (deltaMinutes != null) {
            deltaMinutes.setText(hasData ? StatsRollup.formatDurationDelta(summary.todayActiveSec, summary.yesterdayActiveSec, summary.hasYesterday) : "");
            deltaMinutes.setTextColor(hasData && summary.hasYesterday && summary.todayActiveSec > summary.yesterdayActiveSec ? kit.goodInk() : kit.MUTED);
        }
        if (streakBadge != null) {
            if (hasData && summary.streak > 0) {
                streakBadge.setText("连续 " + summary.streak + " 天");
                streakBadge.setVisibility(View.VISIBLE);
            } else {
                streakBadge.setVisibility(View.GONE);
            }
        }
        if (dailyChart != null && chartInspector != null) {
            if (hasData) {
                if (pickedDay < 0 || pickedDay >= summary.days.size()) pickedDay = summary.days.size() - 1;
                chartInspector.setVisibility(View.VISIBLE);
                dailyChart.setVisibility(View.VISIBLE);
                dailyChart.setData(summary.days, summary.avgPrompts, pickedDay);
                updateInspector();
            } else {
                chartInspector.setVisibility(View.GONE);
                dailyChart.setVisibility(View.GONE);
            }
        }
        if (toolLegend != null && weekFooter != null) {
            View footRow = (View) toolLegend.getParent();
            toolLegend.removeAllViews();
            if (hasData) {
                boolean light = Kit.light(kit.BG);
                java.util.List<String> active = StatsRollup.activeTools(summary);
                for (int i = 0; i < active.size(); i++) {
                    String tool = active.get(i);
                    int dotColor = "claude".equals(tool) ? (light ? 0xffd06135 : 0xfff0936b)
                        : "codex".equals(tool) ? (light ? 0xff2563eb : 0xff6ea8fe)
                        : "agy".equals(tool) ? (light ? 0xff7c3aed : 0xffb392f0)
                        : kit.ACCENT;
                    View dot = kit.dot(dotColor, 7);
                    LinearLayout.LayoutParams dlp = (LinearLayout.LayoutParams) dot.getLayoutParams();
                    if (i > 0) dlp.leftMargin = kit.dp(10);
                    dlp.rightMargin = kit.dp(4);
                    toolLegend.addView(dot);
                    toolLegend.addView(kit.text(StatsRollup.toolShortName(tool), 11.5f, kit.MUTED));
                }
                String weekText = "近 7 天 " + summary.spanPrompts + " 次";
                if (summary.spanActiveSec > 0) weekText += " · " + StatsRollup.formatDuration(summary.spanActiveSec);
                weekFooter.setText(weekText);
                if (footRow != null) footRow.setVisibility(View.VISIBLE);
            } else if (footRow != null) {
                footRow.setVisibility(View.GONE);
            }
        }
        if (coverage != null) {
            long nowMs = System.currentTimeMillis();
            long refMs = summary.latestUpdatedMs > 0 ? summary.latestUpdatedMs : newestSaved;
            String ago = refMs > 0 ? AggregateSessions.ago(refMs, nowMs) : "";
            String suffix = ago.isEmpty() ? "" : " · " + ago + "更新";
            if (expected == 0) {
                coverage.setText("添加电脑后显示统计");
            } else if (answered < expected) {
                coverage.setText(hasData ? "显示上次统计 · 正在同步 " + statsLive.size() + "/" + expected : "读取中 " + statsLive.size() + "/" + expected);
            } else if (statsLive.isEmpty() && hasData) {
                coverage.setText("电脑离线 · 显示缓存数据" + suffix);
            } else if (statsLive.size() < expected) {
                coverage.setText("已汇总 " + stats.size() + "/" + expected + " 台" + (stats.size() > statsLive.size() ? "（含缓存）" : " · 未连接电脑未计入") + suffix);
            } else {
                coverage.setText("已汇总 " + statsLive.size() + "/" + expected + " 台" + suffix);
            }
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
            String kind = AggregateSessions.teamKind(item.optString("state", "failed"), item.optString("phase"), item.optBoolean("confirm"));
            String state = AggregateSessions.label(kind);
            String desc = state + " · " + mode + ("compare".equals(rawMode) ? "" : " · 第 " + item.optInt("round", 1) + " / " + item.optInt("rounds", 3) + " 轮");
            String note = item.optString("note");
            if (!note.isEmpty()) desc += " · " + note;
            int color = "confirm".equals(kind) ? kit.BAD : "team-verify".equals(kind) || "team-paused".equals(kind) || "team-choose".equals(kind)
                ? TINT_WARN : AggregateSessions.activeTeam(kind) ? kit.goodInk() : kit.MUTED;
            kit.setting(body, R.drawable.ic_tab_together, TINT_AGY, item.optString("goal", "协作任务"), desc, "", color,
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
        Motion.springPress(tile, 0.96f);
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
