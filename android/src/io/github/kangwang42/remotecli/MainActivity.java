package io.github.kangwang42.remotecli;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.speech.RecognizerIntent;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.inputmethod.EditorInfo;
import android.webkit.CookieManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import org.json.JSONArray;
import org.json.JSONObject;

/**
 * Moves between the app's screens: the workbench (Workbench), adding a computer by scanning its code or typing its
 * address, and the pages served by the program on the chosen computer, shown full screen.
 */
public final class MainActivity extends Activity {
    private static final int SPEECH = 4103, CAMERA = 4104, NOTIFY = 4105;
    /** Colours the system bars and picks dark or light icons on them, so they stay readable on any skin. */
    private void bars(int shade) {
        boolean light = Kit.light(shade);
        getWindow().setStatusBarColor(shade); getWindow().setNavigationBarColor(shade);
        int flags = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
        View decor = getWindow().getDecorView();
        decor.setSystemUiVisibility(light ? decor.getSystemUiVisibility() | flags : decor.getSystemUiVisibility() & ~flags);
        root.setBackgroundColor(shade);
    }
    /** The app's own screens use the app's skin, whatever the page shown last looked like. */
    private void repaint() { kit.palette(Kit.shade(prefs.getString("look-skin", "paper"))); }
    /** The look: one skin for the app (workbench and lists), and for the terminal either the same or one of its own. */
    void look() {
        final String app = prefs.getString("look-skin", "paper"), terminal = prefs.getString("look-termskin", "");
        kit.sheet("外观", "对所有电脑生效。", new String[]{"App 外观", "终端外观"},
            new String[]{"工作台和列表 · 当前：" + Kit.title(app), "当前：" + (terminal.isEmpty() ? "跟随 App" : Kit.title(terminal))}, -1, which -> {
            final boolean own = which == 1;
            int extra = own ? 1 : 0;
            String[] labels = new String[Kit.NAMES.length + extra], notes = new String[labels.length];
            if (own) { labels[0] = "跟随 App 外观"; notes[0] = terminal.isEmpty() ? "当前使用" : null; }
            for (int i = 0; i < Kit.NAMES.length; i++) {
                labels[i + extra] = Kit.TITLES[i];
                notes[i + extra] = Kit.NAMES[i].equals(own ? terminal : app) ? "当前使用" : null;
            }
            kit.sheet(own ? "终端外观" : "App 外观", own ? "只用于终端画面。" : "用于工作台和每台电脑的列表；终端跟随时也用它。", labels, notes, -1, picked -> {
                if (own && picked == 0) prefs.edit().remove("look-termskin").apply();
                else prefs.edit().putString(own ? "look-termskin" : "look-skin", Kit.NAMES[picked - extra]).apply();
                home("");
            });
        });
    }

    SharedPreferences prefs;
    TextView message;
    final Handler ticker = new Handler(Looper.getMainLooper());
    final ExecutorService net = Executors.newFixedThreadPool(3);
    private Kit kit;
    private Workbench bench;
    private FrameLayout root;
    private WebView web;
    private EditText nameBox, addressBox, passwordBox;
    private GithubUpdater updater;
    private Scanner scanner;
    private String screen = "";                 // "home", "add", "scan" or "web"
    private final Runnable watch = this::checkConnection;
    private boolean fromHome, foreground, checkingConnection;
    private String currentOrigin = "";
    private String movedFrom = "", movedTo = "";       // a computer that is turning to another line: where it was, and where its page was sent
    private ConnectionHealth connectionHealth = new ConnectionHealth();
    boolean showing(String name) { return foreground && name.equals(screen); }

    // ---- the computers this phone knows: [{"name": ..., "url": ...}]
    JSONArray computers() {
        try { return new JSONArray(prefs.getString("computers", "[]")); } catch (Exception broken) { return new JSONArray(); }
    }
    private void store(JSONArray list) { prefs.edit().putString("computers", list.toString()).apply(); }
    /** Adds a computer or updates it: the same address, or the same name with a new address (a tunnel that restarted). */
    private void remember(String name, String url) {
        try {
            JSONArray list = computers();
            int at = -1;
            for (int i = 0; i < list.length(); i++) if (url.equals(list.getJSONObject(i).optString("url"))) at = i;
            if (at < 0 && !name.isEmpty()) for (int i = 0; i < list.length(); i++) if (name.equals(list.getJSONObject(i).optString("name"))) at = i;
            String shown = !name.isEmpty() ? name : at >= 0 ? list.getJSONObject(at).optString("name") : host(url);
            JSONObject item = new JSONObject().put("name", shown).put("url", url);
            if (at >= 0) list.put(at, item); else list.put(item);
            store(list);
        } catch (Exception ignored) { }
    }
    /** What is shown for a computer's address. A place at a relay for everyone shows none: whose relay it is, is not told. */
    static String host(String url) {
        Uri address = Uri.parse(url);
        String path = address.getPath(), h = address.getHost();
        return path != null && path.matches("/c/[a-f0-9]{20}") ? "公共中转" : h == null ? url : h;
    }

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        prefs = getSharedPreferences("remote-cli", MODE_PRIVATE);
        // An earlier version knew one computer only.
        String single = prefs.getString("server", "");
        if (!single.isEmpty() && computers().length() == 0) remember("", single);
        updater = new GithubUpdater(this, new GithubUpdater.Listener() {
            @Override public void found(GithubUpdater.Release release) { new AlertDialog.Builder(MainActivity.this).setTitle("发现 Remote AI 新版本").setMessage("GitHub 上有 v" + release.version + "，现在下载并安装吗？").setPositiveButton("更新", (d, w) -> updater.install(release)).setNegativeButton("稍后", null).show(); }
            @Override public void message(String text) {
                if (text == null || text.isEmpty()) return;
                // Inside a computer's pages there is no line of the app's own to write on.
                if (!"home".equals(screen) || message == null) android.widget.Toast.makeText(MainActivity.this, text, android.widget.Toast.LENGTH_LONG).show(); else { message.setTextColor(kit.MUTED); message.setText(text); }
            }
        });
        // Earlier versions took the skin from the page shown last; that one becomes the app's.
        if (!prefs.contains("look-skin")) {
            String seen = prefs.contains("shade") ? Kit.name(prefs.getInt("shade", 0)) : "";
            prefs.edit().putString("look-skin", seen.isEmpty() ? "paper" : seen).apply();
        }
        kit = new Kit(this);
        bench = new Workbench(this, kit);
        repaint();
        root = new FrameLayout(this);
        setContentView(root);
        bars(kit.BG);
        if (!linked(getIntent())) {
            // One computer opens at once; with several, the workbench shows all of them first.
            JSONArray list = computers();
            if (list.length() == 1) open(list.optJSONObject(0).optString("url"), "");
            else home("");
        }
        updater.checkIfDue();
    }

    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); setIntent(intent); linked(intent); }

    /** remotecli://connect?u=address&p=password&n=name, from the code on the computer's screen. */
    private boolean linked(Intent intent) { return link(intent == null ? null : intent.getData()); }
    private boolean link(Uri link) {
        if (link == null || !"remotecli".equals(link.getScheme()) || !link.isHierarchical()) return false;
        String address = clean(link.getQueryParameter("u")), password = link.getQueryParameter("p"), name = link.getQueryParameter("n");
        if (address.isEmpty()) { home("二维码里的地址无效，请在电脑上重新显示后再扫。"); return true; }
        remember(name == null ? "" : name.trim(), address);
        open(address, password == null ? "" : password);
        return true;
    }

    /**
     * "192.168.1.5:8722" and "https://x.example.com/" both become an origin; anything else is refused. A computer at
     * a relay for everyone has an address of its own there, "https://relay.example.com/c/<id>", and keeps that path.
     */
    static String clean(String typed) {
        String text = typed == null ? "" : typed.trim();
        if (text.isEmpty()) return "";
        if (!text.contains("://")) text = "http://" + text;
        Uri uri = Uri.parse(text);
        String scheme = uri.getScheme(), host = uri.getHost();
        if (host == null || host.isEmpty() || !("http".equals(scheme) || "https".equals(scheme))) return "";
        String path = uri.getPath(), space = path != null && path.matches("/c/[a-f0-9]{20}/?") ? path.substring(0, 23) : "";
        return scheme + "://" + host + (uri.getPort() > 0 ? ":" + uri.getPort() : "") + space;
    }

    private void show(View content, String name) {
        ticker.removeCallbacks(watch);
        bench.stop();
        if (web != null) { root.removeView(web); web.destroy(); web = null; }
        root.removeAllViews();
        bars(kit.BG);
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.addView(content, new ViewGroup.LayoutParams(-1, -2));
        root.addView(scroll, new FrameLayout.LayoutParams(-1, -1));
        screen = name;
    }

    // ---- the workbench: every computer and what is going on on each
    void home(String problem) {
        repaint();
        show(bench.build(problem), "home");
        bench.refresh();
    }
    boolean autoUpdate() { return updater != null && updater.automatic(); }
    void toggleAutoUpdate() { if (updater == null) return; updater.automatic(!updater.automatic()); home(""); }
    void checkUpdate() { message.setTextColor(kit.MUTED); message.setText("正在检查新版本…"); updater.check(true); }
    void manage(String url) {
        int found = -1;
        JSONArray list = computers();
        for (int i = 0; i < list.length(); i++) if (url.equals(list.optJSONObject(i).optString("url"))) found = i;
        if (found < 0) return;
        final int index = found;
        final String name = list.optJSONObject(index).optString("name");
        kit.sheet(name, host(url), new String[]{"打开这台电脑", "改名", "重新扫码登录", "从列表移除"},
            new String[]{null, null, "地址变了或登录过期时使用", "只从这部手机移除，电脑上的终端和对话不受影响"}, 3, which -> {
            if (which == 0) open(url, "");
            else if (which == 2) scan();
            else if (which == 1) {
                final EditText box = new EditText(this);
                box.setText(name); box.setSingleLine(true); box.setSelection(name.length());
                new AlertDialog.Builder(this).setTitle("电脑名称").setView(box).setNegativeButton("取消", null).setPositiveButton("保存", (d, w) -> {
                    String wanted = box.getText().toString().trim();
                    if (wanted.isEmpty() || wanted.length() > 40) return;
                    try { JSONArray now = computers(); now.getJSONObject(index).put("name", wanted); store(now); } catch (Exception ignored) { }
                    home("");
                }).show();
            } else {
                JSONArray now = computers();
                now.remove(index); store(now); bench.forget(url);
                home("");
            }
        });
    }

    String versionName() { try { return getPackageManager().getPackageInfo(getPackageName(), 0).versionName; } catch (Exception unknown) { return ""; } }

    /** The top of a screen one step below the workbench: a title, a line about it, and the way back. */
    private LinearLayout step(String title, String about) {
        LinearLayout page = kit.page(14);
        TextView back = kit.text("‹  返回", 15, kit.MUTED);
        back.setGravity(Gravity.CENTER_VERTICAL); back.setMinHeight(kit.dp(44));
        kit.press(back, () -> { endScan(); home(""); });
        page.addView(back, new LinearLayout.LayoutParams(-2, -2));
        page.addView(kit.bold(title, 23, kit.INK), kit.below(8));
        page.addView(kit.text(about, 14.5f, kit.MUTED), kit.below(6));
        return page;
    }

    // ---- adding a computer by typing
    void add(String problem) {
        repaint();
        LinearLayout page = step("手动添加电脑", "填电脑上 Remote AI 窗口里显示的地址和密码。");
        nameBox = kit.field("名称（可不填），例如 办公室电脑", InputType.TYPE_CLASS_TEXT);
        page.addView(nameBox, kit.below(22));
        addressBox = kit.field("地址，例如 192.168.1.5:8722", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        page.addView(addressBox, kit.below(10));
        passwordBox = kit.field("访问密码", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD);
        passwordBox.setImeOptions(EditorInfo.IME_ACTION_GO);
        passwordBox.setOnEditorActionListener((view, action, event) -> { connect(); return true; });
        page.addView(passwordBox, kit.below(10));
        message = kit.text(problem, 14, kit.BAD);
        page.addView(message, kit.below(12));
        page.addView(kit.button("连接", 0, this::connect), kit.below(8));
        page.addView(kit.button("改用扫码", 1, this::scan), kit.below(10));
        page.addView(kit.text("在公共网络下请用电脑端的“公网隧道”或自己的 https 中转；以 http:// 开头的地址只适合家里或办公室的 Wi-Fi。", 12.5f, kit.MUTED), kit.below(16));
        show(page, "add");
    }
    private void connect() {
        String address = clean(addressBox.getText().toString());
        if (address.isEmpty()) { message.setText("地址无效。例如 192.168.1.5:8722，或 https:// 开头的地址。"); return; }
        remember(nameBox.getText().toString().trim(), address);
        open(address, passwordBox.getText().toString().trim());
    }

    // ---- scanning the code inside the app
    void scan() {
        if (checkSelfPermission(android.Manifest.permission.CAMERA) != android.content.pm.PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{android.Manifest.permission.CAMERA}, CAMERA);
            return;
        }
        repaint();
        LinearLayout page = step("扫码添加电脑", "对准电脑端 Remote AI 窗口中的二维码，识别后自动连接。");
        final int side = Math.min(getResources().getDisplayMetrics().widthPixels - kit.dp(36), kit.dp(380));
        FrameLayout frame = new FrameLayout(this);
        frame.setBackground(kit.shape(Color.BLACK, kit.LINE, 24));
        frame.setClipToOutline(true);
        LinearLayout.LayoutParams square = new LinearLayout.LayoutParams(side, side);
        square.topMargin = kit.dp(22); square.gravity = Gravity.CENTER_HORIZONTAL;
        page.addView(frame, square);
        // four corners that mark where the code should be
        FrameLayout aim = new FrameLayout(this);
        int[] gravities = { Gravity.TOP | Gravity.START, Gravity.TOP | Gravity.END, Gravity.BOTTOM | Gravity.START, Gravity.BOTTOM | Gravity.END };
        for (int gravity : gravities) for (int part = 0; part < 2; part++) {
            View bar = new View(this);
            bar.setBackground(kit.shape(kit.ACCENT, 0, 2));
            FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(part == 0 ? kit.dp(34) : kit.dp(4), part == 0 ? kit.dp(4) : kit.dp(34), gravity);
            params.setMargins(kit.dp(40), kit.dp(40), kit.dp(40), kit.dp(40));
            aim.addView(bar, params);
        }
        page.addView(kit.button("手动输入地址", 1, () -> { endScan(); add(""); }), kit.below(22));
        page.addView(kit.text("相机画面仅用于在本机识别二维码，不保存，不上传。", 12.5f, kit.MUTED), kit.below(14));
        show(page, "scan");
        scanner = new Scanner(this, frame, value -> {
            scanner = null;
            if (!link(Uri.parse(value))) home("这不是 Remote AI 的二维码。请扫描电脑端窗口中显示的二维码。");
        }, problem -> { scanner = null; add(problem + "。可以手动输入地址和密码。"); });
        frame.post(() -> { if (scanner != null) { scanner.start(); frame.addView(aim, new FrameLayout.LayoutParams(-1, -1)); } });
    }
    private void endScan() { if (scanner != null) { scanner.stop(); scanner = null; } }
    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request, permissions, results);
        if (request == NOTIFY) {
            if (results.length == 0 || results[0] != android.content.pm.PackageManager.PERMISSION_GRANTED) {
                prefs.edit().putBoolean("watch", false).apply();
                home("没有通知权限，任务提醒没有开启。可以在系统设置里允许通知后再开。");
            }
            return;
        }
        if (request != CAMERA) return;
        if (results.length > 0 && results[0] == android.content.pm.PackageManager.PERMISSION_GRANTED) scan();
        else add("没有相机权限，不能扫码。可以在系统设置里允许，或在这里手动输入");
    }

    /** Shows the pages of the computer at this address; a password signs in first. */
    void open(String address, String password) {
        open(address, password, "");
    }
    void open(String address, String password, String project) {
        openPage(address, password, project.isEmpty() ? "/" : "/?project=" + Uri.encode(project));
    }
    /** A conversation running on the computer: its project page asks at once how to take it over. */
    void openSession(String address, String project, String session) {
        if (session == null || !session.matches("[A-Za-z0-9-]{8,64}")) { open(address, "", project); return; }
        openPage(address, "", "/?project=" + Uri.encode(project) + "&take=" + session);
    }
    /** Straight into one terminal; its page is told to come back here rather than to that computer's list. */
    /** The computer's page with the sheet that starts a terminal in one of its projects. */
    void startNew(String address) { openPage(address, "", "/?new=1"); }
    // ---- reminders: off until the person turns them on
    boolean watching() { return prefs.getBoolean("watch", false); }
    void toggleWatch() {
        boolean on = !watching();
        prefs.edit().putBoolean("watch", on).apply();
        if (on && android.os.Build.VERSION.SDK_INT >= 33
                && checkSelfPermission("android.permission.POST_NOTIFICATIONS") != android.content.pm.PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{"android.permission.POST_NOTIFICATIONS"}, NOTIFY);
        if (!on) Watcher.stop(this);
        home("");
    }
    void openTerminal(String address, String terminal) {
        try { openPage(address, "", AggregateSessions.terminalPath(terminal) + "&from=bench"); }
        catch (IllegalArgumentException invalid) { android.widget.Toast.makeText(this, invalid.getMessage(), android.widget.Toast.LENGTH_SHORT).show(); }
    }
    private void openPage(String address, String password, String path) {
        ticker.removeCallbacks(watch);
        bench.stop();
        // Back leads to the workbench when the user came from one of the app's own screens.
        if (!"web".equals(screen)) fromHome = !screen.isEmpty();
        currentOrigin = address;
        connectionHealth = new ConnectionHealth();
        prefs.edit().putString("server", address).apply();
        if (web != null) { root.removeView(web); web.destroy(); }
        root.removeAllViews();
        screen = "web";
        web = new WebView(this);
        web.setBackgroundColor(kit.BG);
        bars(kit.BG);
        WebSettings settings = web.getSettings();
        settings.setJavaScriptEnabled(true); settings.setDomStorageEnabled(true);
        settings.setAllowFileAccess(false); settings.setAllowContentAccess(false);
        settings.setTextZoom(100); settings.setSupportZoom(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        CookieManager.getInstance().setAcceptCookie(true);
        final String origin = address;
        web.addJavascriptInterface(new Bridge(), "RemoteCliNative");
        // Without a chrome client a page's confirm() and prompt() are answered "no" without being shown.
        web.setWebChromeClient(new android.webkit.WebChromeClient());
        web.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                return !request.getUrl().toString().startsWith(origin + "/");       // the pages of this computer only
            }
            @Override public void onPageStarted(WebView view, String url, android.graphics.Bitmap icon) { if (url.startsWith(origin + "/")) view.evaluateJavascript(lookScript(url.startsWith(origin + "/terminal/"), false), null); }
            @Override public void onPageFinished(WebView view, String url) { if (url.startsWith(origin + "/")) view.evaluateJavascript(lookScript(url.startsWith(origin + "/terminal/"), true), null); }
            @Override public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request.isForMainFrame()) reconnect(view, origin);
            }
            @Override public void onReceivedHttpError(WebView view, WebResourceRequest request, WebResourceResponse response) {
                String url = request.getUrl().toString();
                boolean relayRequest = url.startsWith(origin + "/api/");
                if (ConnectionHealth.httpError(request.isForMainFrame(), relayRequest, response.getStatusCode())) reconnect(view, origin);
            }
        });
        root.addView(web, new FrameLayout.LayoutParams(-1, -1));
        String target = address + path;
        if (!password.isEmpty()) target += "#p=" + Uri.encode(password);
        web.loadUrl(target);
        if (foreground) ticker.post(watch);
    }

    /** Runs independently of the page, including while a terminal is open or its WebSocket has stopped. */
    private void checkConnection() {
        ticker.removeCallbacks(watch);
        if (!foreground || web == null || !"web".equals(screen)) return;
        if (checkingConnection) { ticker.postDelayed(watch, 1000); return; }
        final WebView checked = web;
        final String origin = currentOrigin;
        final ConnectionHealth health = connectionHealth;
        checkingConnection = true;
        net.execute(() -> {
            boolean reachable = false, whole = false;       // whole: read to its end, so the connection serves the next check too
            int status = 0;
            HttpURLConnection connection = null;
            try {
                connection = (HttpURLConnection) new URL(origin + "/api/session").openConnection();
                connection.setConnectTimeout(3000); connection.setReadTimeout(3000);
                connection.setInstanceFollowRedirects(false); connection.setUseCaches(false);
                status = connection.getResponseCode();
                if (status == 200) {
                    ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                    try (InputStream input = connection.getInputStream()) {
                        byte[] part = new byte[1024]; int n;
                        while ((n = input.read(part)) > 0) {
                            if (bytes.size() + n > 16384) throw new java.io.IOException("Unexpected session response");
                            bytes.write(part, 0, n);
                        }
                    }
                    whole = true;
                    JSONObject session = new JSONObject(bytes.toString("UTF-8"));
                    reachable = session.opt("signed_in") instanceof Boolean;
                }
            } catch (Exception unreachable) { /* A second failed check returns to pairing. */ }
            finally { if (connection != null && !whole) connection.disconnect(); }
            final boolean ok = reachable;
            final int code = status;
            runOnUiThread(() -> {
                checkingConnection = false;
                if (!foreground || web != checked || !"web".equals(screen)) return;
                if (health.sample(ok, code)) reconnect(checked, origin);
                else ticker.postDelayed(watch, 3000);
            });
        });
    }

    private void reconnect(WebView failed, String origin) {
        root.post(() -> {
            // A late error from a destroyed page must not close a newly scanned connection.
            if (web != failed || !"web".equals(screen)) return;
            failed.stopLoading();
            // The line the computer was asked to turn to cannot be reached from here: the computer finds that out by
            // the phone's absence and goes back, and it is still listed under the address it had.
            if (origin.equals(movedTo)) { movedFrom = movedTo = ""; home("新线路没有连上。电脑会在 2 分钟内回到原来的线路，之后可以从这里重新打开它。"); return; }
            home("电脑连接已断开。请确认电脑上的 Remote AI 已打开，然后重新扫码连接；局域网直连时请检查是否在同一个 Wi-Fi。");
        });
    }

    /**
     * Writes the app's look into the page's own storage. Pages of a computer that has not been updated know nothing of
     * pref() and read only that storage; this keeps them in the app's look too. When such a page has already drawn
     * itself in another skin, it is loaded once more.
     */
    private String lookScript(boolean terminal, boolean drawn) {
        String own = prefs.getString("look-termskin", ""), skin = terminal && !own.isEmpty() ? own : prefs.getString("look-skin", "paper");
        StringBuilder script = new StringBuilder("(function(){try{var s=localStorage,was=s.getItem('rcli-skin')||'night';");
        for (String key : new String[]{"skin", "text", "font", "spacing", "renderer"}) {
            String value = "skin".equals(key) ? skin : prefs.getString("look-" + key, "");
            if (value.matches("[a-z0-9]{1,16}")) script.append("s.setItem('rcli-").append(key).append("','").append(value).append("');");
        }
        // A page that asks the app (window.RemoteCliNative.pref) has drawn the right skin whatever its storage held.
        if (drawn) script.append("if(was!=='").append(skin).append("'&&!(window.RemoteCliNative&&RemoteCliNative.pref)&&!location.hash)location.reload();");
        return script.append("}catch(e){}})()").toString();
    }
    private static final java.util.List<String> LOOK = java.util.Arrays.asList("skin", "termskin", "text", "font", "spacing", "renderer", "readfont", "readsize", "readline");
    private final Saver saver = new Saver(this);
    private final class Bridge {
        /** The system's speech recognizer; what was said goes into the page's message box. */
        @JavascriptInterface public void voice() { runOnUiThread(MainActivity.this::dictate); }
        /** Whether this phone has a speech recognizer of its own; without one the page does not offer the microphone. */
        @JavascriptInterface public boolean canDictate() {
            try { return new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).resolveActivity(getPackageManager()) != null; } catch (Exception unknown) { return false; }
        }
        /** The page tells the colour of its skin so the bars around it match. */
        @JavascriptInterface public void chrome(String color) {
            if (color == null || !color.matches("#[0-9a-fA-F]{6}")) return;
            final int shade = Color.parseColor(color);
            runOnUiThread(() -> { if (web == null) return; bars(shade); web.setBackgroundColor(shade); });
        }
        /** Looks for a newer version of the app now; the answer comes as a short notice or a question. */
        @JavascriptInterface public boolean autoUpdate() { return updater != null && updater.automatic(); }
        @JavascriptInterface public void setAutoUpdate(boolean on) { runOnUiThread(() -> { if (updater != null) updater.automatic(on); }); }
        @JavascriptInterface public void update() { runOnUiThread(() -> { android.widget.Toast.makeText(MainActivity.this, "正在检查新版本…", android.widget.Toast.LENGTH_SHORT).show(); updater.check(true); }); }
        @JavascriptInterface public String version() { return versionName(); }
        /**
         * The look (skin, the terminal's own skin, text size, terminal font, line spacing, drawing) is kept by the app,
         * so that it is the same on every computer.
         */
        @JavascriptInterface public String pref(String key) {
            return LOOK.contains(key) ? prefs.getString("look-" + key, "") : "";
        }
        @JavascriptInterface public void setPref(String key, String value) {
            if (value == null || !LOOK.contains(key) || !value.matches("[a-z0-9]{1,16}")) return;
            // "app" gives the terminal's own skin up again
            if ("termskin".equals(key) && "app".equals(value)) prefs.edit().remove("look-termskin").apply();
            else prefs.edit().putString("look-" + key, value).apply();
        }
        /**
         * A file from the computer is kept on the phone: the page begins it, hands over its pieces as base64 text and
         * ends it. Each answers with nothing when it went well, else with what to tell the user; the end answers
         * with where the file is.
         */
        @JavascriptInterface public String saveStart(String name) { return saver.start(name); }
        @JavascriptInterface public String savePiece(String data) { return saver.piece(data); }
        @JavascriptInterface public String saveEnd() { return saver.finish(); }
        @JavascriptInterface public void saveCancel() { saver.cancel(); }
        /** Opens the file saved last with an app of the phone. */
        @JavascriptInterface public void openSaved() { runOnUiThread(() -> { if (!saver.open()) say("手机上没有能打开这种文件的应用，文件在“下载 / RemoteCLI”里"); }); }
        /**
         * The computer made another line ready and handed its page the address there, with a sign-in that works once
         * and the word to say on arrival (docs/PROTOCOL.md, "The program itself"). The app follows: it opens the page
         * for that at the new address. The computer stays listed under its old address until the page there says the
         * line was kept, so that a line the phone cannot reach changes nothing here.
         */
        @JavascriptInterface public void moved(String address, String ticket, String key) {
            final String to = clean(address), from = currentOrigin;
            if (to.isEmpty() || ticket == null || !ticket.matches("[A-Za-z0-9_-]{16,80}") || key == null || !key.matches("[a-f0-9]{32}")) return;
            runOnUiThread(() -> { movedFrom = from; movedTo = to; openPage(to, "", "/computer/#t=" + ticket + "&keep=" + key); });
        }
        /** The page at the new address: the computer has kept the line. From now on the computer is known by it. */
        @JavascriptInterface public void kept() {
            final String at = currentOrigin;
            runOnUiThread(() -> {
                if (movedTo.isEmpty() || !movedTo.equals(at)) return;
                try {
                    JSONArray list = computers(), now = new JSONArray();
                    boolean known = false;
                    for (int i = 0; i < list.length(); i++) known |= movedTo.equals(list.getJSONObject(i).optString("url"));
                    for (int i = 0; i < list.length(); i++) {
                        JSONObject item = list.getJSONObject(i);
                        if (!movedFrom.equals(item.optString("url"))) now.put(item);
                        else if (!known) now.put(item.put("url", movedTo));        // a computer listed under both keeps the entry it has there
                    }
                    store(now); bench.forget(movedFrom);
                } catch (Exception ignored) { }
                movedFrom = movedTo = "";
            });
        }
        /** Back to the workbench. disconnect is the name older pages call. */
        @JavascriptInterface public void home() { runOnUiThread(() -> MainActivity.this.home("")); }
        @JavascriptInterface public void disconnect() { home(); }
        /** The terminal page reports the finished work it has shown, so the workbench stops listing it as new. */
        @JavascriptInterface public void seen(String terminal, String at) {
            final String origin = currentOrigin;
            runOnUiThread(() -> { try { bench.markSeen(origin, terminal, Long.parseLong(at)); } catch (Exception invalid) { /* not a time */ } });
        }
    }

    private void dictate() {
        Intent intent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
                .putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                .putExtra(RecognizerIntent.EXTRA_PROMPT, "说出要输入的内容");
        try { startActivityForResult(intent, SPEECH); }
        catch (Exception missing) { say("这台手机没有系统语音识别，请用输入法的语音键"); }
    }
    private void say(String text) { if (web != null) web.evaluateJavascript("window.TerminalUI&&TerminalUI.error(" + JSONObject.quote(text) + ")", null); }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request != SPEECH || result != RESULT_OK || data == null || web == null) return;
        ArrayList<String> heard = data.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS);
        if (heard != null && !heard.isEmpty()) web.evaluateJavascript("window.RemoteCliDictated&&RemoteCliDictated(" + JSONObject.quote(heard.get(0)) + ")", null);
    }

    @Override public void onBackPressed() {
        if ("scan".equals(screen) || "add".equals(screen)) { endScan(); home(""); return; }
        if (web != null && web.canGoBack()) { web.goBack(); return; }
        // From a computer's first page, back leads to the workbench unless the app opened straight into its only computer.
        if (web != null && (fromHome || computers().length() > 1)) { home(""); return; }
        super.onBackPressed();
    }
    @Override protected void onPause() { super.onPause(); foreground = false; if (watching() && computers().length() > 0 && !isFinishing()) Watcher.start(this); ticker.removeCallbacks(watch); bench.stop(); CookieManager.getInstance().flush(); if (web != null) web.onPause(); if (scanner != null) { endScan(); home(""); } }
    @Override protected void onResume() { super.onResume(); foreground = true; Watcher.stop(this); if (web != null) { web.onResume(); ticker.post(watch); } if ("home".equals(screen)) bench.refresh(); if (updater != null) updater.checkIfDue(); }
    @Override protected void onDestroy() { foreground = false; ticker.removeCallbacks(watch); bench.stop(); net.shutdownNow(); if (updater != null) updater.stop(); if (web != null) { web.destroy(); web = null; } super.onDestroy(); }
}
