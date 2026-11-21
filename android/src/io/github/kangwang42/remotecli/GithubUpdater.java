package io.github.kangwang42.remotecli;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.content.pm.PackageInfo;
import android.content.pm.PackageInstaller;
import android.os.Build;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
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
    /**
     * The repository on GitHub, under the name it is to have and the one it had: it is renamed with the product, and
     * an app installed before or after that finds its releases under either. One that is not there is passed over.
     */
    static final String[] REPOSITORIES = {"KangWang42/remote-ai", "KangWang42/remote-cli"};
    /** Whether an address is a release file of this repository. */
    static boolean ours(String url) {
        for (String name : REPOSITORIES) if (url != null && url.startsWith("https://github.com/" + name + "/releases/download/")) return true;
        return false;
    }
    private static final String ACTION = "io.github.kangwang42.remotecli.INSTALL_STATUS";
    private static final long MAX = 64L * 1024 * 1024;
    private final Activity activity; private final Listener listener; private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final BroadcastReceiver receiver = new BroadcastReceiver() { @Override public void onReceive(Context c, Intent i) { status(i); } };
    private boolean stopped, checking;

    GithubUpdater(Activity activity, Listener listener) { this.activity = activity; this.listener = listener;
        IntentFilter filter = new IntentFilter(ACTION); if (Build.VERSION.SDK_INT >= 33) activity.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED); else activity.registerReceiver(receiver, filter);
    }
    void stop() { stopped = true; try { activity.unregisterReceiver(receiver); } catch (Exception ignored) {} worker.shutdownNow(); }
    /** Looking by itself is off until its owner switches it on; looking by hand (check(true)) is always there. */
    private SharedPreferences preferences() { return activity.getPreferences(Context.MODE_PRIVATE); }
    boolean automatic() { return preferences().getBoolean("auto_update", false); }
    void automatic(boolean on) { preferences().edit().putBoolean("auto_update", on).apply(); if (on) check(false); }
    boolean includePrereleases() { return preferences().getBoolean("update_include_prereleases", false); }
    void includePrereleases(boolean on) {
        preferences().edit().putBoolean("update_include_prereleases", on).remove("github_update_check").apply();
    }
    void checkIfDue() { if (!automatic()) return; long last = preferences().getLong("github_update_check", 0); if (System.currentTimeMillis() - last < 6L * 60 * 60 * 1000) return; check(false); }
    void check(boolean manual) {
        if (checking) return;
        checking = true;
        boolean preview = includePrereleases();
        preferences().edit().putLong("github_update_check", System.currentTimeMillis()).apply();
        if (manual) listener.message("正在检查 GitHub 最新版本…");
        worker.execute(() -> {
            try {
                Release found = fetch(preview);
                activity.runOnUiThread(() -> {
                    checking = false;
                    if (stopped || preview != includePrereleases()) return;
                    if (found != null) listener.found(found);
                    else if (manual) listener.message("当前渠道已是最新版");
                });
            } catch (Exception e) {
                activity.runOnUiThread(() -> {
                    checking = false;
                    if (!stopped && manual && preview == includePrereleases()) listener.message("检查更新失败，请稍后重试");
                });
            }
        });
    }
    private void finish(String text) { activity.runOnUiThread(() -> { checking = false; if (!stopped && text.length() > 0) listener.message(text); }); }
    void install(Release release) { if (release == null) return; listener.message("正在从 GitHub 下载 v" + release.version + "…"); worker.execute(() -> { try {
            File dir = new File(activity.getCacheDir(), "updates"); if (!dir.isDirectory() && !dir.mkdirs()) throw new Exception("无法创建下载目录");
            File part = new File(dir, "remote-cli.apk.part"), apk = new File(dir, "remote-cli.apk"); download(release.url, part); if (release.sha256.isEmpty()) throw new Exception("本次发布没有提供 SHA-256，无法校验安装包"); if (!release.sha256.equals(hash(part))) throw new Exception("安装包校验未通过");
            PackageInfo info = activity.getPackageManager().getPackageArchiveInfo(part.getPath(), 0); if (info == null || !activity.getPackageName().equals(info.packageName) || code(info) <= currentCode()) throw new Exception("下载的安装包不是本应用的新版本");
            if (apk.exists()) apk.delete(); if (!part.renameTo(apk)) throw new Exception("无法保存安装包"); commit(apk);
        } catch (Exception e) { finish(e.getMessage() == null ? "更新未完成" : e.getMessage()); } }); }
    private Release fetch(boolean preview) throws Exception {
        String path = preview ? "/releases?per_page=20" : "/releases/latest";
        String current = currentVersion();
        for (String name : REPOSITORIES) {
            HttpURLConnection c = connection("https://api.github.com/repos/" + name + path);
            try {
                if (c.getResponseCode() == HttpURLConnection.HTTP_NOT_FOUND) continue;
                String body = read(c);
                JSONArray releases = preview ? new JSONArray(body) : new JSONArray().put(new JSONObject(body));
                return select(releases, current, preview);
            } finally {
                c.disconnect();
            }
        }
        return null;
    }
    static Release select(JSONArray releases, String current, boolean preview) throws Exception {
        Release best = null;
        for (int i = 0; i < releases.length(); i++) {
            JSONObject release = releases.optJSONObject(i);
            if (release == null || release.optBoolean("draft") || (!preview && release.optBoolean("prerelease"))) continue;
            String version = release.optString("tag_name", "").replaceFirst("^v", "");
            if (!newer(version, best == null ? current : best.version)) continue;
            Release candidate = androidAsset(release, version);
            if (candidate != null) best = candidate;
            else if (!preview) throw new Exception("最新发布没有匹配的 Android 安装包");
        }
        return best;
    }
    private static Release androidAsset(JSONObject release, String version) throws Exception {
        JSONArray assets = release.optJSONArray("assets");
        for (int i = 0; assets != null && i < assets.length(); i++) {
            JSONObject asset = assets.optJSONObject(i);
            if (asset == null || !"RemoteCli-Android.apk".equals(asset.optString("name"))) continue;
            String url = asset.optString("browser_download_url");
            if (!ours(url)) throw new Exception("发布文件地址无效");
            String digest = asset.optString("digest", "");
            digest = digest.startsWith("sha256:") ? digest.substring(7).toLowerCase(Locale.ROOT) : "";
            return new Release(versionCode(version), version, url, digest, release.optString("html_url", ""));
        }
        return null;
    }
    private static HttpURLConnection connection(String address) throws Exception { HttpURLConnection c = (HttpURLConnection) new URL(address).openConnection(); c.setConnectTimeout(15000); c.setReadTimeout(120000); c.setRequestProperty("User-Agent", "RemoteCli-Android-GitHub-Updater"); c.setRequestProperty("Accept", "application/vnd.github+json"); return c; }
    private static String read(HttpURLConnection c) throws Exception {
        try (InputStreamReader in = new InputStreamReader(c.getInputStream(), StandardCharsets.UTF_8)) {
            char[] buffer = new char[8192];
            StringBuilder out = new StringBuilder();
            int n;
            while ((n = in.read(buffer)) >= 0) out.append(buffer, 0, n);
            return out.toString();
        }
    }
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
