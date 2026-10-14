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

/**
 * Two of the app's four lists, both over every computer: 协作 (what each computer keeps of conversations working
 * together; what is set there is set on that computer's own page of it) and 我的 (the work in numbers, and the
 * app's settings). Each computer answers independently with its own cookie.
 */
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
        page.addView(kit.head(mine ? "我的" : "协作", kit.text(mine ? "设备连接与个人偏好" : "交接上下文，让对话接着做", 13, kit.MUTED)));
        if (!mine && activity.computers().length() > 0) {
            actions(page, "添加接力", "完成一轮后自动转交", "chain", "定时提问", "在指定时间发起提问", "schedule");
            actions(page, "交接对话", "立即交出上下文", "handoff", "同时提问", "同一个问题发给多段对话", "many");
        }
        if (mine) {
            LinearLayout reminders = kit.settingGroup(page, "提醒与后台");
            kit.settingSwitch(reminders, "任务提醒", "需要确认或一轮完成时通知", activity.watching(), activity::toggleWatch);
            kit.setting(reminders, "提醒与权限", "通知、电池与后台联网", activity.reminderStatus(), activity::reminderSettings);
            LinearLayout experience = kit.settingGroup(page, "使用偏好");
            kit.setting(experience, "外观与文字", "主题、字号与终端阅读方式", activity.appearanceName(), activity::look);
            LinearLayout updates = kit.settingGroup(page, "版本与更新");
            kit.setting(updates, "手机 App 更新", "当前 v" + activity.versionName() + " · 检查手机安装包更新", "", activity::checkUpdate);
            kit.settingSwitch(updates, "自动检查手机更新", "发现新版时提示，安装前由你确认", activity.autoUpdate(), activity::toggleAutoUpdate);
            LinearLayout connections = kit.settingGroup(page, "已连接的电脑");
            JSONArray saved = activity.computers();
            for (int i = 0; i < saved.length(); i++) { JSONObject computer = saved.optJSONObject(i); if (computer == null) continue; String url = computer.optString("url");
                kit.setting(connections, computer.optString("name", MainActivity.host(url)), MainActivity.host(url) + " · 版本与远程更新", "管理", () -> activity.manage(url));
            }
            kit.setting(connections, "添加电脑", "扫描电脑端的连接二维码", "", activity::scan);
            total = kit.text("正在读取工作统计…", 13, kit.MUTED);
            page.addView(kit.bold("工作统计", 13, kit.MUTED), kit.below(24));
            page.addView(total, kit.below(8));
        } else {
            page.addView(kit.text("选择电脑后设置对话。接力和定时提问由电脑执行，手机可以离线；电脑须保持运行。接力仅限同一台电脑。", 13, kit.MUTED), kit.below(16));
            page.addView(kit.bold("已安排的协作", 16, kit.INK), kit.below(24));
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
            // (its name leads to that computer's own page of this tab, where everything of it is seen and changed)
            LinearLayout name = kit.row();
            name.setMinimumHeight(kit.dp(32));
            name.addView(kit.line(computer.optString("name", MainActivity.host(url)), 17, kit.INK, true), new LinearLayout.LayoutParams(0, -2, 1));
            name.addView(kit.icon(R.drawable.ic_chevron, Kit.tint(kit.MUTED, 150), 16));
            kit.press(name, () -> activity.computerPage(url, tab));
            card.addView(name);
            LinearLayout body = kit.column(); body.addView(kit.text("正在连接…", 13, kit.MUTED));
            holders.put(url, body); card.addView(body, kit.below(10));
            if (!mine) {
                LinearLayout adds = kit.row();
                adds.addView(kit.button("添加接力", 1, () -> activity.together(url, "chain")), new LinearLayout.LayoutParams(0, -2, 1));
                LinearLayout.LayoutParams beside = new LinearLayout.LayoutParams(0, -2, 1);
                beside.leftMargin = kit.dp(8);
                adds.addView(kit.button("定时提问", 1, () -> activity.together(url, "schedule")), beside);
                card.addView(adds, kit.below(10));
            } else card.addView(kit.button("查看统计详情", 1, () -> activity.computerPage(url, tab)), kit.below(10));
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
                        catch (Exception unavailable) { result.put("chain_error", unavailable.getMessage() == null ? "连接失败" : unavailable.getMessage()); }
                        try { result.put("schedules", ask(url, cookie, "schedules").optJSONArray("schedules")); }
                        catch (Exception unavailable) { result.put("schedule_error", unavailable.getMessage() == null ? "连接失败" : unavailable.getMessage()); }
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
                        for (boolean chain : new boolean[]{true, false}) {
                            String failure = data.optString(chain ? "chain_error" : "schedule_error");
                            if (!failure.isEmpty()) body.addView(kit.text((chain ? "接力" : "定时提问") + "暂不可用：" + failure, 13, kit.BAD), kit.below(10));
                            else rows(body, url, data.optJSONArray(chain ? "chains" : "schedules"), chain);
                        }
                        if (data.has("chain_error") || data.has("schedule_error")) body.addView(kit.button("重新读取", 1, () -> refresh("together")), kit.below(10));
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
    private void actions(LinearLayout page, String first, String firstHint, String firstAction, String second, String secondHint, String secondAction) {
        LinearLayout row = kit.row();
        row.addView(action(first, firstHint, firstAction), new LinearLayout.LayoutParams(0, -1, 1));
        LinearLayout.LayoutParams beside = new LinearLayout.LayoutParams(0, -1, 1); beside.leftMargin = kit.dp(8);
        row.addView(action(second, secondHint, secondAction), beside); page.addView(row, kit.below(10));
    }
    private View action(String title, String hint, String what) {
        LinearLayout tile = kit.column(); tile.setPadding(kit.dp(14), kit.dp(14), kit.dp(14), kit.dp(14));
        tile.setBackground(kit.shape(kit.PANEL, kit.LINE, 16));
        tile.addView(kit.bold(title, 16, kit.INK)); tile.addView(kit.text(hint, 12, kit.MUTED), kit.below(5));
        kit.press(tile, () -> {
            java.util.List<String> urls = new java.util.ArrayList<>();
            JSONArray saved = activity.computers();
            for (int i = 0; i < saved.length(); i++) if (saved.optJSONObject(i) != null) urls.add(saved.optJSONObject(i).optString("url"));
            activity.pick(title, urls, url -> activity.together(url, what));
        });
        return tile;
    }
    private void rows(LinearLayout body, String url, JSONArray items, boolean chain) {
        int size = items == null ? 0 : items.length();
        body.addView(kit.bold((chain ? "接力 " : "定时提问 ") + size + " 条", 14, kit.INK), kit.below(10));
        if (size == 0) body.addView(kit.text(chain ? "尚未设置接力" : "尚未设置定时提问", 13, kit.MUTED));
        for (int i = 0; i < size; i++) {
            JSONObject item = items.optJSONObject(i); if (item == null) continue;
            String title = chain ? item.optString("from_title", "来源对话") + " → " + item.optString("to_title", "接手对话") : item.optString("text");
            String state = chain ? item.optInt("left") > 0 ? "等待来源完成 · 还可转交 " + item.optInt("left") + " 次" : "次数已用完"
                : item.optBoolean("on") && item.optLong("next") > 0 ? "下次 " + android.text.format.DateFormat.format("MM-dd HH:mm", item.optLong("next"))
                : "once".equals(item.optString("kind")) && item.optJSONObject("last") != null ? "已执行" : "已停用";
            JSONObject last = item.optJSONObject("last");
            String description = state + (chain ? "\n" + item.optString("text") : "\n发给：" + item.optString("title", "对话"));
            if (last != null) description += "\n" + (last.optBoolean("ok") ? "最近成功：" : "最近失败：") + last.optString("note");
            LinearLayout entry = kit.column(); body.addView(entry, kit.below(8));
            kit.setting(entry, title, description, "编辑", () -> activity.together(url, chain ? "chain" : "schedule", item.optString("id")));
        }
    }
}
