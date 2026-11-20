package io.github.kangwang42.remotecli;

import android.graphics.drawable.GradientDrawable;
import android.os.SystemClock;
import android.view.Gravity;
import android.view.View;
import android.webkit.CookieManager;
import android.widget.FrameLayout;
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
    private static final int TINT_AGY = 0xff5536c9, TINT_WARN = 0xff9a5b00;

    private final MainActivity activity;
    private final Kit kit;
    private final Workbench bench;
    private final Map<String, ComputerUpdate> states = new HashMap<>();
    private final Set<String> loading = new HashSet<>();
    private String current = "", currentName = "";
    private LinearLayout details;
    private final Runnable tick = this::refresh;

    ComputerManagement(MainActivity activity, Kit kit, Workbench bench) {
        this.activity = activity; this.kit = kit; this.bench = bench;
    }

    View build(String url, String name) {
        stop(); current = url; currentName = name;
        if (!states.containsKey(url)) states.put(url, new ComputerUpdate());
        LinearLayout page = kit.page(12);
        View refreshIcon = kit.circleAction(R.drawable.ic_refresh, "刷新电脑版本", false, this::refresh);
        page.addView(kit.navBar(name, () -> activity.home(""), refreshIcon));

        details = kit.column();
        page.addView(details, kit.below(14));
        draw();

        LinearLayout access = kit.settingGroup(page, "使用这台电脑");
        kit.setting(access, R.drawable.ic_computer, kit.accentInk(), "打开工作台", "查看这台电脑上的对话、终端和项目", "", kit.MUTED, () -> activity.open(url, ""));
        kit.setting(access, R.drawable.ic_chart, TINT_AGY, "工作统计", "查看今日与近 30 天使用趋势", "", kit.MUTED, () -> activity.computerPage(url, "me"));
        kit.setting(access, R.drawable.ic_desktop, TINT_WARN, "远程桌面", "通过 UU 远程打开桌面或小窗", "", kit.MUTED, () -> activity.desktop(url));
        kit.setting(access, R.drawable.ic_settings, kit.MUTED, "电脑设置", "连接线路、远程访问与工具", "", kit.MUTED, () -> activity.computerSettings(url));

        LinearLayout saved = kit.settingGroup(page, "连接管理");
        kit.setting(saved, R.drawable.ic_link, kit.MUTED, "名称与连接", "改名或重新扫码登录", "", kit.MUTED, () -> activity.manageConnection(url));
        kit.setting(saved, R.drawable.ic_stop, kit.BAD, "从手机移除这台电脑", "不需要电脑在线或密码，仅从手机列表删除", "", kit.BAD, () -> activity.confirmRemoveComputer(url));
        return page;
    }

    private void draw() {
        ComputerUpdate state = states.get(current);
        details.removeAllViews();
        boolean ready = state.online && state.enabled;
        LinearLayout hero = kit.column();
        hero.setPadding(kit.dp(16), kit.dp(16), kit.dp(16), kit.dp(16));
        hero.setBackground(kit.shape(kit.heroBg(), kit.heroBorder(), 22));
        kit.elevate(hero, 1.5f);

        LinearLayout top = kit.row();
        FrameLayout iconWrap = new FrameLayout(activity);
        iconWrap.setBackground(kit.shape(kit.accentSoft(), 0, 14));
        iconWrap.addView(kit.icon(R.drawable.ic_computer, kit.accentInk(), 22), new FrameLayout.LayoutParams(kit.dp(22), kit.dp(22), Gravity.CENTER));
        View dot = new View(activity);
        GradientDrawable dotShape = kit.shape(ready ? kit.goodDot() : state.online ? kit.busyDot() : kit.BAD, 0, 6);
        dotShape.setStroke(kit.dp(2.5f), kit.heroBg());
        dot.setBackground(dotShape);
        FrameLayout.LayoutParams dotLp = new FrameLayout.LayoutParams(kit.dp(12), kit.dp(12), Gravity.BOTTOM | Gravity.END);
        dotLp.setMargins(0, 0, -kit.dp(1), -kit.dp(1));
        iconWrap.addView(dot, dotLp);
        LinearLayout.LayoutParams wrapLp = new LinearLayout.LayoutParams(kit.dp(44), kit.dp(44));
        wrapLp.rightMargin = kit.dp(12);
        top.addView(iconWrap, wrapLp);

        LinearLayout meta = kit.column();
        LinearLayout titleRow = kit.row();
        titleRow.addView(kit.line(currentName, 16, kit.heroFg(), true));
        int tagFg = ready ? kit.goodInk() : state.online ? kit.busyInk() : kit.BAD;
        int tagBg = ready ? kit.goodSoft() : state.online ? kit.busySoft() : kit.badSoft();
        TextView onlineTag = kit.statusPill(state.connection, tagFg, tagBg, 0);
        LinearLayout.LayoutParams tagLp = new LinearLayout.LayoutParams(-2, -2);
        tagLp.leftMargin = kit.dp(8);
        titleRow.addView(onlineTag, tagLp);
        meta.addView(titleRow);
        meta.addView(kit.line(MainActivity.host(current), 12.5f, kit.heroMuted(), false), kit.below(2));
        top.addView(meta, new LinearLayout.LayoutParams(0, -2, 1));

        String versionLabel = state.version.isEmpty() ? "版本未知" : "v" + state.version;
        top.addView(kit.statusPill(versionLabel, kit.MUTED, kit.RAISED, 0));
        hero.addView(top);

        String hint = !state.online ? "保持电脑开机并运行 Remote AI 后，可查看版本并发起远程更新。"
            : !state.enabled ? "在电脑端恢复手机访问后可发起更新。"
            : !state.supported ? "此电脑端暂不支持手机更新，请先在电脑上安装新版。"
            : state.version.isEmpty() ? "暂未读取到版本号，点右上角刷新重试。"
            : state.newer.isEmpty() ? "电脑端版本正常，可随时检查更新。" : "发现新版本 v" + state.newer + "，可远程安装。";
        hero.addView(kit.text(hint, 13, state.newer.isEmpty() ? kit.heroMuted() : kit.accentInk()), kit.below(12));
        if (!state.note.isEmpty()) hero.addView(kit.text(state.note, 12.5f, kit.heroMuted()), kit.below(6));

        final String url = current;
        if (state.canUpdate() || state.busy()) {
            String actionLabel = state.busy() ? "正在等待电脑更新…"
                : state.newer.isEmpty() ? "检查电脑端更新" : "更新电脑至 v" + state.newer;
            TextView cta = kit.semibold(loading.contains(url) ? "正在读取版本…" : actionLabel, 14, kit.onSolidInk());
            cta.setGravity(Gravity.CENTER);
            cta.setMinHeight(kit.dp(48));
            cta.setBackground(kit.shape(kit.solidInk(), 0, 14));
            kit.press(cta, () -> { if (state.canUpdate()) confirm(url); });
            hero.addView(cta, kit.below(14));
        }
        details.addView(hero);
    }

    private void confirm(String url) {
        ComputerUpdate state = states.get(url);
        if (state == null || !state.canUpdate()) return;
        String title = state.newer.isEmpty() ? "检查并更新这台电脑？" : "更新电脑至 v" + state.newer + "？";
        String note = "由这台电脑检查、下载并安装新版；已是最新版时不会重装。安装会重启 Remote AI，手机连接暂时中断，正在执行的终端任务可能被打断。建议先等任务完成。";
        kit.confirmSheet(title, note, "检查并更新", false, () -> {
            state.begin(UUID.randomUUID().toString().replace("-", ""), SystemClock.elapsedRealtime());
            if (url.equals(current) && activity.showing("computer")) { draw(); refresh(); }
        });
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
