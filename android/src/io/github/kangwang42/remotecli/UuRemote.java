package io.github.kangwang42.remotecli;

import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.net.Uri;
import android.widget.Toast;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.view.View;
import java.util.function.IntConsumer;
import org.json.JSONObject;

/** Optional, user-initiated desktop access, independent of the terminal connection. */
final class UuRemote {
    private static final String PACKAGE = "com.netease.uuremote";
    private final MainActivity activity;
    private final Kit kit;
    private final DesktopStore store;
    UuRemote(MainActivity activity, Kit kit) { this.activity = activity; this.kit = kit; store = new DesktopStore(activity); }
    private void say(String text) { Toast.makeText(activity, text, Toast.LENGTH_LONG).show(); }
    private Intent intent(String url) { return new Intent(Intent.ACTION_VIEW, Uri.parse(url)).setPackage(PACKAGE); }
    private void choices(String title, String note, String[] labels, String[] notes, int danger, IntConsumer picked) {
        LinearLayout content = kit.column();
        content.setPadding(kit.dp(12), kit.dp(8), kit.dp(12), kit.dp(8));
        content.addView(kit.text(note, 14, kit.MUTED), kit.below(0));
        ScrollView scroll = new ScrollView(activity) {
            @Override protected void onMeasure(int width, int height) {
                int cap = (int) (getResources().getDisplayMetrics().heightPixels * .65f);
                super.onMeasure(width, View.MeasureSpec.makeMeasureSpec(cap, View.MeasureSpec.AT_MOST));
            }
        };
        scroll.addView(content);
        AlertDialog dialog = new AlertDialog.Builder(activity).setTitle(title).setView(scroll).setNegativeButton("返回", null).create();
        for (int i = 0; i < labels.length; i++) {
            final int choice = i;
            android.widget.TextView button = kit.button(labels[i], 1, () -> { dialog.dismiss(); picked.accept(choice); });
            if (i == danger) button.setTextColor(kit.BAD);
            content.addView(button, kit.below(12));
            if (notes != null && notes[i] != null) content.addView(kit.text(notes[i], 12.5f, kit.MUTED), kit.below(3));
        }
        dialog.show();
    }
    private boolean available() {
        try {
            if (intent("uuremote://external").resolveActivity(activity.getPackageManager()) == null) throw new IllegalStateException();
            PackageInfo info = activity.getPackageManager().getPackageInfo(PACKAGE, 0);
            String[] version = (info.versionName == null ? "" : info.versionName).split("\\.");
            if (version.length < 3) throw new IllegalStateException();
            int major = Integer.parseInt(version[0]), minor = Integer.parseInt(version[1]);
            if (major < 4 || (major == 4 && minor < 42)) throw new IllegalStateException();
            return true;
        } catch (Exception missing) {
            new AlertDialog.Builder(activity).setTitle("需要 UU 远程 4.42.0 或更新版本")
                .setMessage("请先在手机安装或更新 UU 远程，登录账号，并确认目标 Windows 或 Mac 电脑允许被控。")
                .setNegativeButton("返回", null).setPositiveButton("打开官网下载页", (d, w) -> {
                    try { activity.startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse("https://uuyc.163.com/"))); }
                    catch (Exception unavailable) { say("请在浏览器访问 uuyc.163.com。"); }
                }).show();
            return false;
        }
    }
    void menu(String url) {
        JSONObject computer = activity.computerByUrl(url);
        if (computer == null) { say("请先添加这台电脑。"); return; }
        String record = computer.optString("id"), name = computer.optString("name");
        String heading = name.isEmpty() ? "这台电脑的桌面" : name + " · 桌面";
        JSONObject pending = store.pending();
        if (pending.length() > 0) {
            boolean fresh = DesktopProtocol.fresh(pending.optLong("created"), System.currentTimeMillis());
            String text = fresh ? "正在等待 UU 返回。若 UU 正在显示提示，请先完成确认。" : "尚未确认上次操作的结果，可以结束等待后重试。";
            choices(heading, text, new String[]{"结束等待"}, new String[]{"只结束本应用的等待，不会断开 UU 远控"}, -1, picked -> {
                if (store.clearPending()) menu(url); else say("未能结束等待，请重试。");
            });
            return;
        }
        boolean bound = store.bound(record);
        String note = store.notice(record);
        if (note.isEmpty()) note = bound ? "通过 UU 打开桌面；终端离线时也可尝试。" : "请在 UU 中选择与这条记录对应的 Windows 或 Mac 电脑。";
        String[] labels = bound ? new String[]{"打开桌面", "小窗查看", "重新关联", "复制运维入口", "移除关联"}
            : new String[]{"关联 UU 远程", "复制运维入口"};
        String[] notes = bound ? new String[]{"在 UU 中全屏操作", "尝试画中画，连接结果由 UU 返回", "在 UU 中重新选择这台电脑", "粘贴到同一手机的探针 App；不含连接密码", "只移除此手机的关联，不撤销 UU 设备授权"}
            : new String[]{"需要手机 UU 4.42.0 或更新版本", "用于同一手机的探针 App，不含连接密码"};
        choices(heading, note, labels, notes, bound ? 4 : -1, picked -> {
            if (bound && picked < 2) launch(record, picked == 1);
            else if ((!bound && picked == 0) || (bound && picked == 2)) {
                new AlertDialog.Builder(activity).setTitle("关联" + (name.isEmpty() ? "这台电脑" : "“" + name + "”"))
                    .setMessage("接下来在 UU 中选择同一台电脑。关联只保存在这部手机，之后可随时移除。")
                    .setNegativeButton("取消", null).setPositiveButton("选择设备", (d, w) -> bind(record)).show();
            } else if ((!bound && picked == 1) || (bound && picked == 3)) {
                ClipboardManager clipboard = (ClipboardManager) activity.getSystemService(android.content.Context.CLIPBOARD_SERVICE);
                clipboard.setPrimaryClip(ClipData.newPlainText("Remote AI 运维入口", "remoteai://computer?id=" + record));
                say("已复制，可粘贴到这部手机的探针 App。");
            } else new AlertDialog.Builder(activity).setTitle("移除此手机的桌面关联？")
                .setMessage("不会结束 UU 远控，也不会撤销 UU 账号里的设备授权。")
                .setNegativeButton("取消", null).setPositiveButton("移除", (d, w) -> { store.remove(record); say("已移除关联。"); }).show();
        });
    }
    private void bind(String record) {
        if (!available() || !canBegin()) return;
        String nonce = DesktopProtocol.id();
        if (!store.begin(record, "bind", nonce)) { say("无法保存关联请求，请重试。"); return; }
        send(DesktopProtocol.bind(nonce), record, true);
    }
    private boolean canBegin() {
        if (store.pending().length() == 0) return true;
        say("请先完成或结束上次 UU 请求的等待。"); return false;
    }
    private void launch(String record, boolean small) {
        if (!available() || !canBegin()) return;
        String device;
        try { device = store.read(record); }
        catch (Exception inaccessible) { say("无法读取桌面关联，请重新关联。"); return; }
        String nonce = DesktopProtocol.id();
        if (small && !store.begin(record, "control", nonce)) { say("无法保存远控请求，请重试。"); return; }
        send(DesktopProtocol.control(device, small, nonce), record, small);
    }
    private void send(String url, String record, boolean waits) {
        try {
            activity.startActivity(intent(url));
            store.notice(record, waits ? "已交给 UU，等待返回结果。" : "已打开 UU 远程；全屏连接结果请在 UU 中查看。");
        } catch (Exception unavailable) {
            if (waits) store.clearPending();
            store.notice(record, "未能打开 UU 远程，请确认安装和版本。");
            say(store.notice(record));
        }
    }
    void forget(String record) { store.remove(record); }
    /** Returns true only for our callback scheme; callbacks never enter QR pairing. */
    boolean receive(Uri uri, boolean cold) {
        if (uri == null || !"remoteai-uu".equals(uri.getScheme())) return false;
        JSONObject pending = store.pending();
        String record = pending.optString("record"), operation = pending.optString("operation");
        DesktopProtocol.Result result = DesktopProtocol.receive(uri.toString(), operation, pending.optString("nonce"), pending.optLong("created"), System.currentTimeMillis());
        JSONObject computer = activity.computerById(record);
        if (result == null || computer == null) {
            if (cold) activity.home("");
            say("此次 UU 回调无效或已过期，请从电脑的桌面入口重新操作。");
            return true;
        }
        if (!store.clearPending()) { say("无法确认此次请求，请重试。"); if (cold) activity.home(""); return true; }
        String note;
        if (!result.error.isEmpty()) {
            if ("device_not_found".equals(result.error)) store.remove(record);
            note = DesktopProtocol.message(result.error);
        } else if ("bind".equals(operation)) {
            try { store.save(record, result.device); note = "已关联所选设备，可以打开桌面。"; }
            catch (Exception failed) { note = "无法安全保存关联，请重新关联。"; }
        } else note = "UU 已开启小窗；后续连接状态请在 UU 中查看。";
        store.notice(record, note);
        if (cold) activity.manage(computer.optString("url"));
        say(note);
        return true;
    }
}
