package io.github.kangwang42.remotecli;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageInfo;
import android.content.pm.PackageInstaller;
import android.os.Build;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.security.MessageDigest;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import org.json.JSONArray;
import org.json.JSONObject;

/** Checks GitHub Releases and hands a verified APK to Android's package installer. */
final class GithubUpdater {
    interface Listener { void found(Release release); void message(String text); }
    static final class Release { final int code; final String version, url, sha256, page;
        Release(int code, String version, String url, String sha256, String page) { this.code = code; this.version = version; this.url = url; this.sha256 = sha256; this.page = page; }
    }
    private static final String API = "https://api.github.com/repos/KangWang42/remote-cli/releases/latest";
    private static final String ACTION = "io.github.kangwang42.remotecli.INSTALL_STATUS";
    private static final long MAX = 64L * 1024 * 1024;
    private final Activity activity; private final Listener listener; private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final BroadcastReceiver receiver = new BroadcastReceiver() { @Override public void onReceive(Context c, Intent i) { status(i); } };
    private boolean stopped, checking; private Release latest;

    GithubUpdater(Activity activity, Listener listener) { this.activity = activity; this.listener = listener;
        IntentFilter filter = new IntentFilter(ACTION); if (Build.VERSION.SDK_INT >= 33) activity.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED); else activity.registerReceiver(receiver, filter);
    }
    void stop() { stopped = true; try { activity.unregisterReceiver(receiver); } catch (Exception ignored) {} worker.shutdownNow(); }
    /** Looking by itself is off until its owner switches it on; looking by hand (check(true)) is always there. */
    boolean automatic() { return activity.getPreferences(Context.MODE_PRIVATE).getBoolean("auto_update", false); }
    void automatic(boolean on) { activity.getPreferences(Context.MODE_PRIVATE).edit().putBoolean("auto_update", on).apply(); if (on) check(false); }
    void checkIfDue() { if (!automatic()) return; long last = activity.getPreferences(Context.MODE_PRIVATE).getLong("github_update_check", 0); if (System.currentTimeMillis() - last < 6L * 60 * 60 * 1000) return; check(false); }
    void check(boolean manual) { if (checking) return; checking = true; activity.getPreferences(Context.MODE_PRIVATE).edit().putLong("github_update_check", System.currentTimeMillis()).apply(); if (manual) listener.message("正在检查 GitHub 最新版本…");
        worker.execute(() -> { try { Release found = fetch(); latest = found; if (found != null) activity.runOnUiThread(() -> { checking = false; if (!stopped) listener.found(found); }); else finish("已是最新版"); } catch (Exception e) { finish(manual ? "检查更新失败，请稍后重试" : ""); } }); }
    private void finish(String text) { activity.runOnUiThread(() -> { checking = false; if (!stopped && text.length() > 0) listener.message(text); }); }
    void install(Release release) { if (release == null) return; listener.message("正在从 GitHub 下载 v" + release.version + "…"); worker.execute(() -> { try {
            File dir = new File(activity.getCacheDir(), "updates"); if (!dir.isDirectory() && !dir.mkdirs()) throw new Exception("无法创建下载目录");
            File part = new File(dir, "remote-cli.apk.part"), apk = new File(dir, "remote-cli.apk"); download(release.url, part); if (!release.sha256.isEmpty() && !release.sha256.equals(hash(part))) throw new Exception("安装包校验未通过");
            PackageInfo info = activity.getPackageManager().getPackageArchiveInfo(part.getPath(), 0); if (info == null || !activity.getPackageName().equals(info.packageName) || code(info) <= currentCode()) throw new Exception("下载的安装包不是本应用的新版本");
            if (apk.exists()) apk.delete(); if (!part.renameTo(apk)) throw new Exception("无法保存安装包"); commit(apk);
        } catch (Exception e) { finish(e.getMessage() == null ? "更新未完成" : e.getMessage()); } }); }
    private Release fetch() throws Exception { HttpURLConnection c = connection(API); JSONObject root = new JSONObject(read(c)); String tag = root.optString("tag_name", "").replaceFirst("^v", ""); if (tag.isEmpty() || !newer(tag, currentVersion())) return null; int code = versionCode(tag); JSONArray assets = root.optJSONArray("assets");
        for (int i = 0; assets != null && i < assets.length(); i++) { JSONObject a = assets.getJSONObject(i); if (!"RemoteCli-Android.apk".equals(a.optString("name"))) continue; String url = a.optString("browser_download_url"); if (!url.startsWith("https://github.com/KangWang42/remote-cli/releases/download/")) throw new Exception("发布文件地址无效"); String digest = a.optString("digest", ""); if (digest.startsWith("sha256:")) digest = digest.substring(7).toLowerCase(Locale.ROOT); else digest = ""; return new Release(code, tag, url, digest, root.optString("html_url", "")); }
        throw new Exception("最新发布没有匹配的 Android 安装包"); }
    private static HttpURLConnection connection(String address) throws Exception { HttpURLConnection c = (HttpURLConnection) new URL(address).openConnection(); c.setConnectTimeout(15000); c.setReadTimeout(120000); c.setRequestProperty("User-Agent", "RemoteCli-Android-GitHub-Updater"); c.setRequestProperty("Accept", "application/vnd.github+json"); return c; }
    private static String read(HttpURLConnection c) throws Exception { try (InputStream in = c.getInputStream()) { byte[] b = new byte[8192]; StringBuilder out = new StringBuilder(); int n; while ((n = in.read(b)) >= 0) out.append(new String(b, 0, n, "UTF-8")); return out.toString(); } finally { c.disconnect(); } }
    private static void download(String address, File target) throws Exception { HttpURLConnection c = connection(address); try (InputStream in = c.getInputStream(); OutputStream out = new FileOutputStream(target)) { byte[] b = new byte[65536]; long total = 0; int n; while ((n = in.read(b)) >= 0) { total += n; if (total > MAX) throw new Exception("安装包过大"); out.write(b, 0, n); } } finally { c.disconnect(); } }
    private void commit(File apk) throws Exception {
        PackageInstaller installer = activity.getPackageManager().getPackageInstaller();
        PackageInstaller.SessionParams p = new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
        p.setAppPackageName(activity.getPackageName()); p.setSize(apk.length());
        if (Build.VERSION.SDK_INT >= 31) p.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED);
        int id = installer.createSession(p);
        PackageInstaller.Session session = installer.openSession(id); boolean committed = false;
        try {
            // Android requires every stream returned by openWrite() to be closed before commit.
            try (InputStream in = new FileInputStream(apk); OutputStream out = session.openWrite("base.apk", 0, apk.length())) {
                byte[] b = new byte[65536]; int n;
                while ((n = in.read(b)) >= 0) out.write(b, 0, n);
                session.fsync(out);
            }
            Intent status = new Intent(ACTION).setPackage(activity.getPackageName());
            int flags = PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= 31 ? PendingIntent.FLAG_MUTABLE : 0);
            session.commit(PendingIntent.getBroadcast(activity, id, status, flags).getIntentSender());
            committed = true;
            activity.runOnUiThread(() -> { if (!stopped) listener.message("正在交给系统安装…"); });
        } finally {
            if (!committed) try { session.abandon(); } catch (Exception ignored) {}
            session.close();
        }
    }
    @SuppressWarnings("deprecation") private void status(Intent i) { int s = i.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE); if (s == PackageInstaller.STATUS_PENDING_USER_ACTION) { Intent confirm = i.getParcelableExtra(Intent.EXTRA_INTENT); try { activity.startActivity(confirm); } catch (Exception e) { listener.message("无法打开系统安装确认"); } return; } listener.message(s == PackageInstaller.STATUS_SUCCESS ? "安装完成，请重新打开 Remote AI" : "安装未完成"); }
    private int currentCode() { try { return code(activity.getPackageManager().getPackageInfo(activity.getPackageName(), 0)); } catch (Exception e) { return 0; } }
    private String currentVersion() { try { String v = activity.getPackageManager().getPackageInfo(activity.getPackageName(), 0).versionName; return v == null ? "0.0.0" : v; } catch (Exception e) { return "0.0.0"; } }
    private static int code(PackageInfo p) { return Build.VERSION.SDK_INT >= 28 ? (int) p.getLongVersionCode() : p.versionCode; }
    private static int versionCode(String v) { String[] x = v.split("\\."); return Integer.parseInt(x[0]) * 10000 + (x.length > 1 ? Integer.parseInt(x[1]) : 0) * 100 + (x.length > 2 ? Integer.parseInt(x[2].replaceAll("[^0-9].*", "")) : 0); }
    private static boolean newer(String a, String b) { try { return versionCode(a) > versionCode(b); } catch (Exception e) { return false; } }
    private static String hash(File f) throws Exception { MessageDigest d = MessageDigest.getInstance("SHA-256"); try (InputStream in = new FileInputStream(f)) { byte[] b = new byte[65536]; int n; while ((n = in.read(b)) >= 0) d.update(b, 0, n); } StringBuilder out = new StringBuilder(); for (byte b : d.digest()) out.append(String.format(Locale.ROOT, "%02x", b)); return out.toString(); }
}
