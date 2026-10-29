package io.github.kangwang42.remotecli;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.NotificationManager;
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
 * Moves between the app's screens: its four lists over every computer, each under its tab at the foot (Tabs: 任务 and
 * 项目 are Workbench's, 协作 and 我的 are AppPages'), adding a computer by scanning its code or typing its address,
 * and the pages served by the program on one computer, shown full screen. Those pages draw the same four tabs at
 * their foot, and a press on one there leads to the app's list of that name (Bridge.appPage).
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
    private ComputerManagement computerManagement;
    private UuRemote uuRemote;
    private String pairingRecord = "";
    private AppPages appPages;
    private Tabs tabs;
    private String appTab = "home";                 // the tab the app's lists were left at
    private FrameLayout root;
    private WebView web;
    private EditText nameBox, addressBox, passwordBox;
    private GithubUpdater updater;
    private Scanner scanner;
    private String screen = "";                 // one of the tabs ("home", "projects", "together", "me"), "add", "scan" or "web"
    private final Runnable watch = this::checkConnection;
    private boolean foreground, checkingConnection;
    private String currentOrigin = "";
    private String movedFrom = "", movedTo = "";       // a computer that is turning to another line: where it was, and where its page was sent
    private ConnectionHealth connectionHealth = new ConnectionHealth();
    boolean showing(String name) { return foreground && name.equals(screen); }
    /** One of the app's own lists is on the screen. */
    boolean onTabs() { return foreground && Tabs.has(screen); }
    /** How many things wait for an answer on every computer: Workbench says it, the tab of 任务 shows it. */
    void waiting(int count) { tabs.count(count); }
    /** One of the computers, for what is done on one of them: asked only where there are several. */
    void pick(String title, java.util.List<String> urls, java.util.function.Consumer<String> chosen) {
        if (urls.isEmpty()) { android.widget.Toast.makeText(this, computers().length() == 0 ? "请先添加电脑" : "没有在线的电脑", android.widget.Toast.LENGTH_SHORT).show(); return; }
        if (urls.size() == 1) { chosen.accept(urls.get(0)); return; }
        String[] names = new String[urls.size()];
        JSONArray list = computers();
        for (int i = 0; i < names.length; i++) {
            names[i] = host(urls.get(i));
            for (int k = 0; k < list.length(); k++) { JSONObject one = list.optJSONObject(k); if (one != null && urls.get(i).equals(one.optString("url")) && !one.optString("name").isEmpty()) names[i] = one.optString("name"); }
        }
        kit.sheet(title, "", names, null, -1, which -> chosen.accept(urls.get(which)));
    }

    // ---- the computers this phone knows: [{"name": ..., "url": ...}]
    JSONArray computers() {
        try {
            JSONArray list = new JSONArray(prefs.getString("computers", "[]"));
            boolean changed = false;
            java.util.Set<String> ids = new java.util.HashSet<>();
            for (int i = 0; i < list.length(); i++) {
                JSONObject item = list.getJSONObject(i);
                String id = item.optString("id");
                if (!DesktopProtocol.validId(id) || !ids.add(id)) {
                    id = DesktopProtocol.id(); item.put("id", id); ids.add(id); changed = true;
                }
            }
            if (changed && !prefs.edit().putString("computers", list.toString()).commit()) throw new IllegalStateException();
            return list;
        } catch (Exception broken) { return new JSONArray(); }
    }
    private void store(JSONArray list) { prefs.edit().putString("computers", list.toString()).apply(); }
    /** Only an existing address or an explicitly chosen record may inherit a desktop binding. */
    private void remember(String name, String url) {
        try {
            JSONArray list = computers();
            String[] ids = new String[list.length()], urls = new String[list.length()];
            for (int i = 0; i < list.length(); i++) { ids[i] = list.getJSONObject(i).optString("id"); urls[i] = list.getJSONObject(i).optString("url"); }
            int at = ComputerIdentity.match(ids, urls, url, pairingRecord);
            if (at >= 0 && !pairingRecord.isEmpty() && !pairingRecord.equals(ids[at])) toastMessage("扫描的是另一条已保存电脑记录，原电脑关联保持不变。");
            String shown = !name.isEmpty() ? name : at >= 0 ? list.getJSONObject(at).optString("name") : host(url);
            JSONObject item = at >= 0 ? list.getJSONObject(at) : new JSONObject().put("id", DesktopProtocol.id());
            item.put("name", shown).put("url", url);
            if (at >= 0) list.put(at, item); else list.put(item);
            store(list);
            pairingRecord = "";
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
                if (!Tabs.has(screen) || message == null) android.widget.Toast.makeText(MainActivity.this, text, android.widget.Toast.LENGTH_LONG).show(); else { message.setTextColor(kit.MUTED); message.setText(text); }
            }
        });
        // Earlier versions took the skin from the page shown last; that one becomes the app's.
        if (!prefs.contains("look-skin")) {
            String seen = prefs.contains("shade") ? Kit.name(prefs.getInt("shade", 0)) : "";
            prefs.edit().putString("look-skin", seen.isEmpty() ? "paper" : seen).apply();
        }
        kit = new Kit(this);
        bench = new Workbench(this, kit);
        computerManagement = new ComputerManagement(this, kit, bench);
        uuRemote = new UuRemote(this, kit);
        appPages = new AppPages(this, kit, bench);
        tabs = new Tabs(this, kit);
        repaint();
        root = new FrameLayout(this);
        setContentView(root);
        bars(kit.BG);
        if (!external(getIntent(), true) && !linked(getIntent())) {
            home("");
        }
        updater.checkIfDue();
    }

    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); setIntent(intent); if (!external(intent, false)) linked(intent); }

    JSONObject computerByUrl(String url) { JSONArray list = computers(); for (int i = 0; i < list.length(); i++) { JSONObject item = list.optJSONObject(i); if (item != null && url.equals(item.optString("url"))) return item; } return null; }
    JSONObject computerById(String id) { JSONArray list = computers(); for (int i = 0; i < list.length(); i++) { JSONObject item = list.optJSONObject(i); if (item != null && DesktopProtocol.validId(id) && id.equals(item.optString("id"))) return item; } return null; }
    void desktop(String url) { uuRemote.menu(url); }
    private boolean external(Intent intent, boolean cold) {
        Uri uri = intent == null ? null : intent.getData();
        if (uri == null) return false;
        if (uuRemote.receive(uri, cold)) { intent.setData(null); return true; }
        if (!"remoteai".equals(uri.getScheme())) return false;
        JSONObject computer = null;
        try {
            java.net.URI parsed = new java.net.URI(uri.toString());
            if ("computer".equals(parsed.getRawAuthority()) && (parsed.getRawPath().isEmpty() || "/".equals(parsed.getRawPath())) && parsed.getRawFragment() == null) {
                java.util.Map<String, String> values = DesktopProtocol.query(parsed);
                if (values.size() == 1) computer = computerById(values.get("id"));
            }
        } catch (Exception invalid) { }
        intent.setData(null);
        if (computer == null) { if (cold) home(""); toastMessage("这部手机未保存对应电脑，请在 Remote AI 中重新复制运维入口。"); }
        else manage(computer.optString("url"));
        return true;
    }
    private void toastMessage(String text) { android.widget.Toast.makeText(this, text, android.widget.Toast.LENGTH_LONG).show(); }

    /** remotecli://connect?u=address&p=password&n=name, from the code on the computer's screen. */
    private boolean linked(Intent intent) {
        if (intent != null && intent.hasExtra("computer")) {
            String url = intent.getStringExtra("computer"), task = intent.getStringExtra("task");
            JSONArray saved = computers();
            for (int i = 0; i < saved.length(); i++) if (url != null && url.equals(saved.optJSONObject(i).optString("url"))) {
                if (task != null && task.matches("[a-f0-9]{32}")) openTerminal(url, task);
                else openSession(url, intent.getStringExtra("project") == null ? "" : intent.getStringExtra("project"), task);
                intent.removeExtra("computer"); return true;
            }
        }
        return link(intent == null ? null : intent.getData());
    }
    private boolean link(Uri link) {
        if (link == null || !"remotecli".equals(link.getScheme()) || !"connect".equals(link.getHost()) || !link.isHierarchical()) return false;
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
        computerManagement.stop();
        if (web != null) { root.removeView(web); filePicker.cancel(); web.destroy(); web = null; }
        root.removeAllViews();
        bars(kit.BG);
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.addView(content, new ViewGroup.LayoutParams(-1, -2));
        if (Tabs.has(name)) {
            // the tabs stay at the foot while the list above them is scrolled
            LinearLayout shell = kit.column();
            shell.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
            shell.addView(tabs.build(name, this::appPage));
            root.addView(shell, new FrameLayout.LayoutParams(-1, -1));
        } else root.addView(scroll, new FrameLayout.LayoutParams(-1, -1));
        screen = name;
    }

    // ---- the app's own lists: back to the one that was open last, or to 任务 with what went wrong
    void home(String problem) {
        pairingRecord = "";
        repaint();
        if (problem.isEmpty() && !"home".equals(appTab)) { appPage(appTab); return; }
        appTab = "home";
        show(bench.build("home", problem), "home");
        bench.refresh();
    }
    void appPage(String tab) {
        if (!Tabs.has(tab)) return;
        appTab = tab;
        if ("home".equals(tab)) { home(""); return; }
        repaint();
        boolean projects = "projects".equals(tab);
        show(projects ? bench.build(tab, "") : appPages.build(tab), tab);
        if (!projects) appPages.refresh(tab);
        bench.refresh();        // (also under 协作 and 我的: what waits for an answer is counted on the tab of 任务)
    }
    /** One computer's own page of a tab: where what the app lists of it is seen whole and changed. */
    void computerPage(String url, String tab) { openPage(url, "", "/?tab=" + tab + ("me".equals(tab) ? "&stats=1" : "")); }
    /** That computer's page of 协作 with one of its sheets open: "handoff", "many", "chain", "schedule" (web/together.js). */
    void together(String url, String what) { openPage(url, "", "/?tab=together&do=" + what); }
    void together(String url, String what, String item) { openPage(url, "", "/?tab=together&do=" + Uri.encode(what) + "&item=" + Uri.encode(item)); }
    boolean autoUpdate() { return updater != null && updater.automatic(); }
    void toggleAutoUpdate() { if (updater == null) return; updater.automatic(!updater.automatic()); home(""); }
    void checkUpdate() { message.setTextColor(kit.MUTED); message.setText("正在检查新版本…"); updater.check(true); }
    void manage(String url) {
        JSONArray list = computers();
        for (int i = 0; i < list.length(); i++) {
            JSONObject computer = list.optJSONObject(i);
            if (computer == null || !url.equals(computer.optString("url"))) continue;
            repaint();
            String name = computer.optString("name");
            show(computerManagement.build(url, name.isEmpty() ? host(url) : name), "computer");
            computerManagement.refresh();
            return;
        }
    }
    void computerSettings(String url) { openPage(url, "", "/computer/"); }
    void manageConnection(String url) {
        int found = -1;
        JSONArray list = computers();
        for (int i = 0; i < list.length(); i++) if (url.equals(list.optJSONObject(i).optString("url"))) found = i;
        if (found < 0) return;
        final int index = found;
        final String name = list.optJSONObject(index).optString("name");
        kit.sheet(name, host(url), new String[]{"打开这台电脑", "改名", "重新扫码登录", "从列表移除"},
            new String[]{null, null, "地址变了或登录过期时使用", "只从这部手机移除，电脑上的终端和对话不受影响"}, 3, which -> {
            if (which == 0) open(url, "");
            else if (which == 2) { pairingRecord = computers().optJSONObject(index).optString("id"); scan(); }
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
                JSONObject removed = now.optJSONObject(index);
                if (removed != null) uuRemote.forget(removed.optString("id"));
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
                Watcher.stop(this);
                android.widget.Toast.makeText(this, "没有通知权限，请在系统设置中允许通知", android.widget.Toast.LENGTH_LONG).show();
            }
            else Watcher.start(this, true);
            reminderSettings();
            return;
        }
        if (request != CAMERA) return;
        if (results.length > 0 && results[0] == android.content.pm.PackageManager.PERMISSION_GRANTED) scan();
        else add("没有相机权限，不能扫码。可以在系统设置里允许，或在这里手动输入");
    }

    /** Shows the pages of the computer at this address, its projects first; a password signs in first. */
    void open(String address, String password) {
        open(address, password, "");
    }
    void open(String address, String password, String project) {
        openPage(address, password, project.isEmpty() ? "/?tab=projects" : "/?project=" + Uri.encode(project));
    }
    /** A conversation running on the computer: its project page asks at once how to take it over. */
    void openSession(String address, String project, String session) {
        if (session == null || !session.matches("[A-Za-z0-9-]{8,64}")) { open(address, "", project); return; }
        openPage(address, "", "/?project=" + Uri.encode(project) + "&take=" + session);
    }
    /** The computer's page with the sheet of a new conversation: in one of its projects, in an application or in a terminal. */
    void startNew(String address) { openPage(address, "", "/?tab=tasks&new=1"); }
    // ---- reminders: off until the person turns them on
    boolean watching() { return prefs.getBoolean("watch", false); }
    String reminderStatus() {
        boolean allowed = getSystemService(NotificationManager.class).areNotificationsEnabled();
        android.app.NotificationChannel channel = getSystemService(NotificationManager.class).getNotificationChannel("tasks");
        allowed &= channel == null || channel.getImportance() > NotificationManager.IMPORTANCE_NONE;
        return watching() && allowed ? "已开启" : allowed ? "未开启" : "需授权";
    }
    String appearanceName() { return Kit.title(prefs.getString("look-skin", "paper")); }
    void toggleWatch() {
        boolean on = !watching();
        prefs.edit().putBoolean("watch", on).apply();
        if (on && android.os.Build.VERSION.SDK_INT >= 33
                && checkSelfPermission("android.permission.POST_NOTIFICATIONS") != android.content.pm.PackageManager.PERMISSION_GRANTED)
            { requestPermissions(new String[]{"android.permission.POST_NOTIFICATIONS"}, NOTIFY); return; }
        if (!on) Watcher.stop(this); else Watcher.start(this, true);
        if ("reminders".equals(screen)) reminderSettings(); else home("");
    }
    /** A plain-language checklist for Android's controls that decide whether background reminders can arrive. */
    void reminderSettings() {
        LinearLayout page = kit.page(22);
        page.addView(kit.head("提醒与权限", kit.text("这些开关由 Android 系统管理，开启后提醒更稳定。", 13, kit.MUTED)), kit.below(18));
        NotificationManager nm = getSystemService(NotificationManager.class);
        boolean notifications = nm.areNotificationsEnabled();
        boolean channel = nm.getNotificationChannel("tasks") == null || nm.getNotificationChannel("tasks").getImportance() != NotificationManager.IMPORTANCE_NONE;
        boolean battery = getSystemService(android.os.PowerManager.class).isIgnoringBatteryOptimizations(getPackageName());
        int data = getSystemService(android.net.ConnectivityManager.class).getRestrictBackgroundStatus();
        page.addView(kit.text("必要权限", 13, kit.MUTED), kit.below(4));
        page.addView(reminderRow("通知", notifications && channel ? "已允许" : "需要允许", notifications && channel ? kit.GOOD : kit.BAD,
            () -> systemSettings(new Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, getPackageName()))), kit.below(8));
        page.addView(reminderRow("任务通知类别", "声音、振动与锁屏显示", kit.MUTED,
            () -> systemSettings(new Intent(android.provider.Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS).putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, getPackageName()).putExtra(android.provider.Settings.EXTRA_CHANNEL_ID, "tasks"))), kit.below(8));
        page.addView(reminderRow("电池优化", battery ? "已不受电池优化限制" : "系统可能延迟锁屏后的提醒", battery ? kit.GOOD : kit.BUSY,
            () -> systemSettings(new Intent(android.provider.Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))), kit.below(8));
        page.addView(reminderRow("后台联网", data == android.net.ConnectivityManager.RESTRICT_BACKGROUND_STATUS_ENABLED ? "省流量模式正在限制后台联网" : "未被省流量模式限制", kit.MUTED,
            () -> systemSettings(new Intent(android.provider.Settings.ACTION_IGNORE_BACKGROUND_DATA_RESTRICTIONS_SETTINGS, Uri.parse("package:" + getPackageName())))), kit.below(8));
        page.addView(reminderRow("应用详情", "检查后台数据、自启动和省电策略", kit.ACCENT,
            () -> systemSettings(new Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + getPackageName())))), kit.below(18));
        String error = getSharedPreferences("remote-cli-watch", MODE_PRIVATE).getString("error", "");
        if (!error.isEmpty()) page.addView(kit.text(error, 13, kit.BAD), kit.below(12));
        page.addView(kit.text("开启后会保留一条“后台连接”通知，约每 10 秒检查任务。回到应用时暂停弹出任务通知，电脑上的确认被处理后会撤下旧提醒。\n\n在应用详情中将电池设为“不限制”或“允许后台活动”。部分手机还需手动开启自启动；这项设置无法自动检测。\n\n锁屏省电、断网或系统强行停止应用仍可能延迟提醒。电脑需要保持在线。", 13.5f, kit.INK), kit.below(18));
        page.addView(kit.button("发送测试通知", 1, () -> { Watcher.test(this); android.widget.Toast.makeText(this, "已请求发送测试通知，请检查通知栏", android.widget.Toast.LENGTH_LONG).show(); }), kit.below(14));
        page.addView(kit.button(watching() ? "关闭任务提醒" : "开启任务提醒", watching() ? 1 : 0, this::toggleWatch), kit.below(8));
        page.addView(kit.button("返回我的", 2, () -> appPage("me")));
        show(page, "reminders");
    }
    private void systemSettings(Intent intent) {
        try { startActivity(intent); }
        catch (android.content.ActivityNotFoundException unavailable) {
            startActivity(new Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + getPackageName())));
        }
    }
    private View reminderRow(String title, String status, int color, Runnable open) {
        LinearLayout row = kit.row(); row.setPadding(kit.dp(14), kit.dp(10), kit.dp(8), kit.dp(10)); row.setBackground(kit.shape(kit.PANEL, kit.LINE, 14));
        LinearLayout copy = kit.column(); copy.addView(kit.bold(title, 15, kit.INK)); copy.addView(kit.text(status, 12.5f, color), kit.below(3));
        row.addView(copy, new LinearLayout.LayoutParams(0, -2, 1)); row.addView(kit.button("打开设置", 2, open), new LinearLayout.LayoutParams(kit.dp(92), -2));
        return row;
    }
    /** Straight into one terminal; its page is told to come back here rather than to that computer's list. */
    void openTerminal(String address, String terminal) { openTerminal(address, terminal, false); }
    void openTerminal(String address, String terminal, boolean chat) {
        try { openPage(address, "", AggregateSessions.terminalPath(terminal, chat) + "&from=bench"); }
        catch (IllegalArgumentException invalid) { android.widget.Toast.makeText(this, invalid.getMessage(), android.widget.Toast.LENGTH_SHORT).show(); }
    }
    private void openPage(String address, String password, String path) {
        ticker.removeCallbacks(watch);
        bench.stop();
        computerManagement.stop();
        currentOrigin = address;
        connectionHealth = new ConnectionHealth();
        prefs.edit().putString("server", address).apply();
        if (web != null) { root.removeView(web); filePicker.cancel(); web.destroy(); }
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
        web.setWebChromeClient(filePicker);
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
    // (and, kept the same way for every computer: the kind of conversation its owner begins, "newkind")
    private static final java.util.List<String> LOOK = java.util.Arrays.asList("skin", "termskin", "text", "font", "spacing", "renderer", "readfont", "readsize", "readline", "newkind");
    private final Saver saver = new Saver(this);
    private final FilePicker filePicker = new FilePicker(this);
    private final class Bridge {
        @JavascriptInterface public boolean canUploadFiles() { return true; }
        // 终端按键条的自定义键：存应用偏好，App 升级与换连接线路都不丢（web 端有 localStorage 回退）。
        @JavascriptInterface public String termKeys() { return prefs.getString("term-keys", "[]"); }
        @JavascriptInterface public void termKeys(String json) {
            if (json == null || json.length() > 20000) return;
            prefs.edit().putString("term-keys", json).apply();
        }
        @JavascriptInterface public void reminders() { runOnUiThread(MainActivity.this::reminderSettings); }
        @JavascriptInterface public String reminderStatus() { return MainActivity.this.reminderStatus(); }
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
                        else if (!known) now.put(item.put("url", movedTo));
                        else { uuRemote.forget(item.optString("id")); toastMessage("新线路已有电脑记录，原记录的桌面关联已移除，请核对后重新关联。"); }
                    }
                    store(now); bench.forget(movedFrom);
                } catch (Exception ignored) { }
                movedFrom = movedTo = "";
            });
        }
        /** Back to the workbench. disconnect is the name older pages call. */
        @JavascriptInterface public void home() { runOnUiThread(() -> MainActivity.this.home("")); }
        /**
         * The tabs at the foot of a computer's page are the app's: the page asks which of them the app has lists
         * for, and a press on one of those leads to that list (by the page's name of the tab).
         */
        @JavascriptInterface public String appTabs() { return String.join(",", Tabs.NAMES); }
        @JavascriptInterface public void appPage(String tab) {
            final String key = Tabs.key(tab);
            if (key != null) runOnUiThread(() -> MainActivity.this.appPage(key));
        }
        /** The name this phone gave the computer whose pages are shown, for the line under the page's heading. */
        @JavascriptInterface public String computer() {
            JSONArray list = computers();
            for (int i = 0; i < list.length(); i++) { JSONObject one = list.optJSONObject(i); if (one != null && currentOrigin.equals(one.optString("url"))) return one.optString("name"); }
            return "";
        }
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
        if (request == FilePicker.REQUEST) { filePicker.result(result, data); return; }
        if (request != SPEECH || result != RESULT_OK || data == null || web == null) return;
        ArrayList<String> heard = data.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS);
        if (heard != null && !heard.isEmpty()) web.evaluateJavascript("window.RemoteCliDictated&&RemoteCliDictated(" + JSONObject.quote(heard.get(0)) + ")", null);
    }

    @Override public void onBackPressed() {
        if ("computer".equals(screen)) { home(""); return; }
        if ("reminders".equals(screen)) { appPage("me"); return; }
        if ("scan".equals(screen) || "add".equals(screen)) { endScan(); home(""); return; }
        if (web != null && web.canGoBack()) { web.goBack(); return; }
        if (web != null) { home(""); return; }
        if (Tabs.has(screen) && !"home".equals(screen)) { appPage("home"); return; }
        super.onBackPressed();
    }
    @Override protected void onPause() { super.onPause(); foreground = false; if (watching() && computers().length() > 0) Watcher.start(this); ticker.removeCallbacks(watch); bench.stop(); computerManagement.stop(); CookieManager.getInstance().flush(); if (web != null) web.onPause(); if (scanner != null) { endScan(); home(""); } }
    @Override protected void onResume() { super.onResume(); foreground = true; if (watching()) Watcher.start(this, true); if (web != null) { web.onResume(); ticker.post(watch); } if (Tabs.has(screen)) bench.refresh(); if ("together".equals(screen) || "me".equals(screen)) appPages.refresh(screen); if ("computer".equals(screen)) computerManagement.refresh(); if ("reminders".equals(screen)) reminderSettings(); if (updater != null) updater.checkIfDue(); }
    @Override protected void onDestroy() { foreground = false; ticker.removeCallbacks(watch); bench.stop(); computerManagement.stop(); net.shutdownNow(); if (updater != null) updater.stop(); if (web != null) { filePicker.cancel(); web.destroy(); web = null; } super.onDestroy(); }
}
