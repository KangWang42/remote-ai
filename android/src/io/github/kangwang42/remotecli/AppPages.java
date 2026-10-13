package io.github.kangwang42.remotecli;

import android.view.View;
import android.webkit.CookieManager;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.util.HashMap;
import java.util.Map;
import java.util.UUID;
import org.json.JSONArray;
import org.json.JSONObject;

/** App-wide collaboration and settings. Each computer answers independently with its own cookie. */
final class AppPages {
    private final MainActivity activity;
    private final Kit kit;
    private final Workbench bench;
    private final Map<String, LinearLayout> holders = new HashMap<>();
    private final Map<String, JSONObject> stats = new HashMap<>();
    private TextView total;
    private int generation, answered, expected;

    AppPages(MainActivity activity, Kit kit, Workbench bench) { this.activity = activity; this.kit = kit; this.bench = bench; }

    View build(String tab) {
        generation++; holders.clear(); stats.clear(); answered = 0;
        boolean mine = "me".equals(tab);
        LinearLayout page = kit.page(22);
        page.addView(kit.bold(mine ? "我的" : "协作", 24, kit.INK));
        page.addView(kit.text(mine ? "所有电脑的工作统计与 App 设置" : "在这里查看所有电脑的接力与定时提问。", 14, kit.MUTED), kit.below(8));
        if (mine) {
            total = kit.text("正在读取统计…", 16, kit.INK);
            page.addView(total, kit.below(18));
            page.addView(kit.button("外观", 1, activity::look), kit.below(16));
            page.addView(kit.button("版本 " + activity.versionName() + " · 检查更新", 1, activity::checkUpdate), kit.below(8));
            page.addView(kit.button("自动检查更新：" + (activity.autoUpdate() ? "已开启" : "未开启"), 1, activity::toggleAutoUpdate), kit.below(8));
            page.addView(kit.button("任务提醒：" + (activity.watching() ? "已开启" : "未开启"), 1, activity::toggleWatch), kit.below(8));
        } else {
            page.addView(kit.text("接力怎么用\n1. 在同一台电脑上打开两段 Claude Code 或 Codex 对话，在电脑端“设置”开启“手机处理电脑上的对话”。\n2. 在下面选择电脑，设置来源、接手对话和要做的事。\n3. 来源对话下一次完成一轮后，自动把最后的回答和你的要求发给接手对话。", 14, kit.INK), kit.below(18));
            page.addView(kit.text("例如：Claude Code 写实现，Codex 审查。一次规则只负责单向转交；需要往返时另建反向规则。电脑须保持运行，暂不支持跨电脑接力。", 13, kit.MUTED), kit.below(8));
        }
        JSONArray computers = activity.computers(); expected = computers.length();
        if (expected == 0) {
            if (mine) total.setText("添加电脑后显示工作统计");
            page.addView(kit.button("扫码添加电脑", 0, activity::scan), kit.below(22));
        }
        for (int i = 0; i < computers.length(); i++) {
            JSONObject computer = computers.optJSONObject(i);
            if (computer == null) continue;
            String url = computer.optString("url");
            LinearLayout card = kit.column();
            card.setPadding(kit.dp(16), kit.dp(16), kit.dp(16), kit.dp(16));
            card.setBackground(kit.shape(kit.PANEL, kit.LINE, 16));
            card.addView(kit.bold(computer.optString("name", MainActivity.host(url)), 17, kit.INK));
            LinearLayout body = kit.column(); body.addView(kit.text("正在连接…", 13, kit.MUTED));
            holders.put(url, body); card.addView(body, kit.below(10));
            if (!mine) card.addView(kit.button("添加接力", 0, () -> activity.newChain(url)), kit.below(10));
            card.addView(kit.button(mine ? "查看统计详情" : "设置接力 / 定时提问 / 交接", 1, () -> activity.computerPage(url, tab)), kit.below(10));
            page.addView(card, kit.below(16));
        }
        page.addView(kit.button("刷新", 1, () -> activity.appPage(tab)), kit.below(16));
        activity.message = kit.text("", 13, kit.MUTED); page.addView(activity.message);
        return page;
    }

    private JSONObject ask(String url, String cookie, String action) throws Exception {
        return bench.post(url + "/api/work", cookie, new JSONObject().put("id", UUID.randomUUID().toString().replace("-", "")).put("action", action).put("days", 7));
    }
    void refresh(String tab) {
        final int request = ++generation;
        stats.clear(); answered = 0;
        for (final String url : holders.keySet()) {
            final String cookie = CookieManager.getInstance().getCookie(url);
            activity.net.execute(() -> {
                JSONObject result = null; String error = "";
                try {
                    if ("me".equals(tab)) result = ask(url, cookie, "stats");
                    else {
                        result = new JSONObject();
                        // An older computer can still provide schedules when it does not support chains.
                        try { result.put("chains", ask(url, cookie, "chains").optJSONArray("chains")); }
                        catch (Exception unavailable) { result.put("chain_error", unavailable.getMessage()); }
                        result.put("schedules", ask(url, cookie, "schedules").optJSONArray("schedules"));
                    }
                } catch (Exception unavailable) { error = unavailable.getMessage() == null ? "连接失败，请重试" : unavailable.getMessage(); }
                final JSONObject data = result; final String problem = error;
                activity.runOnUiThread(() -> {
                    if (request != generation || !activity.showing(tab)) return;
                    LinearLayout body = holders.get(url); if (body == null) return;
                    body.removeAllViews(); answered++;
                    if (data == null || !problem.isEmpty()) {
                        body.addView(kit.text("暂时无法读取：" + (problem.isEmpty() ? "请确认电脑在线" : problem), 13, kit.BAD));
                        body.addView(kit.button("重新连接", 1, () -> activity.open(url, "")), kit.below(8));
                    } else if ("me".equals(tab)) {
                        stats.put(url, data); body.addView(kit.text(today(data), 14, kit.INK));
                    } else {
                        String chainError = data.optString("chain_error");
                        if (!chainError.isEmpty()) body.addView(kit.text("接力暂不可用：" + chainError, 13, kit.MUTED));
                        rows(body, data.optJSONArray("chains"), true);
                        rows(body, data.optJSONArray("schedules"), false);
                    }
                    if ("me".equals(tab)) sum();
                });
            });
        }
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
        for (JSONObject data : stats.values()) { JSONObject day = day(data); prompts += day.optLong("prompts"); sessions += day.optLong("sessions"); seconds += day.optDouble("active", 0); }
        total.setText("今天合计 " + prompts + " 次提问 · " + sessions + " 段对话\n" + Math.round(seconds / 60) + " 分钟（各电脑时长相加）\n已读取 " + stats.size() + "/" + expected + " 台" + (answered < expected ? " · 其余读取中" : stats.size() < expected ? " · 未连接电脑未计入" : ""));
    }
    private void rows(LinearLayout body, JSONArray items, boolean chain) {
        int size = items == null ? 0 : items.length();
        body.addView(kit.bold((chain ? "接力 " : "定时提问 ") + size + " 条", 14, kit.INK), kit.below(10));
        if (size == 0) body.addView(kit.text(chain ? "尚未设置接力" : "尚未设置定时提问", 13, kit.MUTED));
        for (int i = 0; i < size; i++) {
            JSONObject item = items.optJSONObject(i); if (item == null) continue;
            String title = chain ? item.optString("from_title", "来源对话") + " → " + item.optString("to_title", "接手对话") : item.optString("text");
            String state = chain ? "还可转交 " + item.optInt("left") + " 次" : item.optBoolean("on") ? "已启用" : "已停用";
            JSONObject last = item.optJSONObject("last");
            body.addView(kit.text(title + "\n" + state + (last == null ? "" : " · " + last.optString("note")), 14, kit.INK), kit.below(10));
        }
    }
}
