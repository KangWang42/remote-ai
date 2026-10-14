package io.github.kangwang42.remotecli;

import android.app.AlertDialog;
import android.os.SystemClock;
import android.view.View;
import android.webkit.CookieManager;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import org.json.JSONArray;
import org.json.JSONObject;

/** Native computer management, reachable without opening its web workbench. */
final class ComputerManagement {
    private final MainActivity activity;
    private final Kit kit;
    private final Workbench bench;
    private final Map<String, ComputerUpdate> states = new HashMap<>();
    private final Set<String> loading = new HashSet<>();
    private String current = "";
    private LinearLayout details;
    private final Runnable tick = this::refresh;

    ComputerManagement(MainActivity activity, Kit kit, Workbench bench) {
        this.activity = activity; this.kit = kit; this.bench = bench;
    }
    View build(String url, String name) {
        stop(); current = url;
        if (!states.containsKey(url)) states.put(url, new ComputerUpdate());
        LinearLayout page = kit.page(14);
        page.addView(kit.button("‹ 返回", 2, () -> activity.home("")));
        page.addView(kit.head(name, kit.text(MainActivity.host(url), 13, kit.MUTED)), kit.below(12));
        details = kit.settingGroup(page, "电脑版本与更新");
        draw();
        LinearLayout access = kit.settingGroup(page, "使用这台电脑");
        kit.setting(access, "打开工作台", "查看这台电脑上的对话、终端和项目", "", () -> activity.open(url, ""));
        kit.setting(access, "电脑设置", "连接线路、远程访问与工具", "", () -> activity.computerSettings(url));
        LinearLayout saved = kit.settingGroup(page, "连接管理");
        kit.setting(saved, "名称与连接", "改名、重新扫码登录或从手机移除", "", () -> activity.manageConnection(url));
        return page;
    }
    private void draw() {
        ComputerUpdate state = states.get(current);
        details.removeAllViews();
        LinearLayout body = kit.column(); body.setPadding(kit.dp(16), kit.dp(16), kit.dp(16), kit.dp(16));
        details.addView(body);
        TextView standing = kit.standing(state.connection);
        kit.stands(standing, state.connection, kit.MUTED, state.online && state.enabled ? kit.GOOD : kit.BUSY);
        body.addView(standing);
        body.addView(kit.bold(state.version.isEmpty() ? "版本暂不可用" : "v" + state.version, 28, kit.INK), kit.below(12));
        body.addView(kit.text("Remote AI · 电脑端" + (!state.online && !state.version.isEmpty() ? " · 上次读取" : ""), 13, kit.MUTED), kit.below(4));
        String hint = !state.online ? "请保持电脑开机并运行 Remote AI，连接后可读取版本和发起更新。"
            : !state.enabled ? "在电脑端恢复手机访问后可发起更新。"
            : !state.supported ? "此电脑端暂不支持手机更新，请先在电脑上安装支持远程更新的版本。"
            : state.version.isEmpty() ? "电脑未提供版本号，请刷新或在电脑端检查。"
            : state.newer.isEmpty() ? "可让电脑检查新版；发现新版后会下载并安装。" : "发现新版本 v" + state.newer;
        body.addView(kit.text(hint, 14, state.newer.isEmpty() ? kit.MUTED : kit.ACCENT), kit.below(14));
        final String url = current;
        TextView update = kit.button(state.busy() ? "正在等待电脑更新…" : state.newer.isEmpty() ? "检查并更新电脑端" : "更新电脑至 v" + state.newer, 0, () -> confirm(url));
        update.setEnabled(state.canUpdate()); update.setAlpha(state.canUpdate() ? 1f : .45f);
        body.addView(update, kit.below(16));
        if (!state.note.isEmpty()) body.addView(kit.text(state.note, 13, kit.MUTED), kit.below(12));
        TextView refresh = kit.button(loading.contains(url) ? "正在读取版本…" : "刷新电脑版本", 2, this::refresh);
        refresh.setEnabled(!loading.contains(url)); body.addView(refresh, kit.below(6));
    }
    private void confirm(String url) {
        ComputerUpdate state = states.get(url);
        if (state == null || !state.canUpdate()) return;
        new AlertDialog.Builder(activity).setTitle(state.newer.isEmpty() ? "检查并更新这台电脑？" : "更新电脑至 v" + state.newer + "？")
            .setMessage("由这台电脑检查、下载并安装新版；已是最新版时不会重装。安装会重启 Remote AI，手机连接暂时中断，正在执行的终端任务可能被打断。建议先等任务完成。")
            .setNegativeButton("取消", null).setPositiveButton("检查并更新", (d, w) -> {
                state.begin(UUID.randomUUID().toString().replace("-", ""), SystemClock.elapsedRealtime());
                if (url.equals(current) && activity.showing("computer")) { draw(); refresh(); }
            }).show();
    }
    void stop() { activity.ticker.removeCallbacks(tick); }
    void refresh() {
        stop();
        if (!activity.showing("computer")) return;
        final String url = current;
        final ComputerUpdate state = states.get(url);
        if (state == null || !loading.add(url)) return;
        final String cookie = CookieManager.getInstance().getCookie(url);
        draw();
        activity.net.execute(() -> {
            JSONObject overview = null; String problem = "";
            try { overview = overview(url, cookie); }
            catch (IOException unreachable) { problem = "无法连接电脑，请确认电脑在线且地址可用"; }
            catch (Exception error) { problem = error.getMessage() == null ? "暂时无法连接电脑" : error.getMessage(); }
            final JSONObject data = overview; final String error = problem;
            activity.runOnUiThread(() -> {
                if (data == null) state.unavailable(error);
                else {
                    JSONObject device = data.optJSONObject("device");
                    if (device == null) state.unavailable("电脑未返回版本信息");
                    else {
                        JSONArray features = device.optJSONArray("features"); boolean supported = false;
                        if (features != null) for (int i = 0; i < features.length(); i++) if ("update".equals(features.optString(i))) supported = true;
                        state.device(device.optString("version"), device.optString("newer"), device.optBoolean("online"), device.optBoolean("enabled"), supported);
                    }
                }
                state.expire(SystemClock.elapsedRealtime());
                if (!activity.isDestroyed() && state.busy() && !state.accepted && state.online && state.enabled) send(url, cookie, state);
                else finished(url);
            });
        });
    }
    private void send(String url, String cookie, ComputerUpdate state) {
        final String id = state.request;
        activity.net.execute(() -> {
            JSONObject result = null; String problem = ""; boolean uncertain = false;
            try { result = bench.post(url + "/api/terminal", cookie, new JSONObject().put("action", "update").put("id", id)); }
            catch (IOException error) { uncertain = true; }
            catch (Exception error) { problem = error.getMessage() == null ? "请求失败，请刷新后重试" : error.getMessage(); }
            final JSONObject reply = result; final String error = problem; final boolean retry = uncertain;
            activity.runOnUiThread(() -> {
                if (id.equals(state.request)) {
                    if (reply != null) state.receipt(reply.optString("state"), reply.optString("error"));
                    else if (retry) state.note = "连接暂时中断，正在确认电脑是否收到请求…";
                    else state.receipt("error", error);
                }
                finished(url);
            });
        });
    }
    private void finished(String url) {
        loading.remove(url);
        if (!url.equals(current) || !activity.showing("computer")) return;
        draw();
        activity.ticker.postDelayed(tick, 5000);
    }
    private JSONObject overview(String url, String cookie) throws Exception {
        HttpURLConnection connection = (HttpURLConnection) new URL(url + "/api/terminal").openConnection();
        try {
            connection.setConnectTimeout(4000); connection.setReadTimeout(8000); connection.setUseCaches(false); connection.setInstanceFollowRedirects(false);
            if (cookie != null) connection.setRequestProperty("Cookie", cookie);
            int code = connection.getResponseCode();
            if (code == 401) throw new Exception("登录已过期，请在连接管理中重新扫码登录");
            if (code != 200) throw new Exception("暂时无法读取电脑信息（" + code + "）");
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            try (InputStream input = connection.getInputStream()) {
                byte[] part = new byte[16384]; int n;
                while ((n = input.read(part)) > 0) {
                    if (bytes.size() + n > 4000000) throw new Exception("电脑返回内容过大");
                    bytes.write(part, 0, n);
                }
            }
            return new JSONObject(bytes.toString("UTF-8"));
        } finally { connection.disconnect(); }
    }
}
