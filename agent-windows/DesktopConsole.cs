using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RemoteCli {

/// Native Windows desktop console window hosting the unified Preact/TS workspace inside Edge WebView2.
/// Opens directly as the primary window on launch while keeping installer overhead under 800 KB.
internal sealed class DesktopConsole : Form {
    const int LogicalMinWidth = 920;
    const int LogicalMinHeight = 600;
    const int LogicalDefaultWidth = 1340;
    const int LogicalDefaultHeight = 860;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    readonly string userDataDir;
    readonly Action openClassic;
    readonly Action openSettings;
    readonly Action checkUpdate;
    readonly Action pickFolder;
    readonly Action openInstaller;
    readonly Action<string> onSkinChanged;
    readonly Action<Rectangle, FormWindowState> saveBounds;
    readonly Label loadingLabel;
    WebView2 webView;
    bool initialized;
    bool initializing;
    bool forceClose;
    string currentOrigin = "";
    string pendingUrl = "";

    internal static bool IsRuntimeAvailable() {
        try {
            string ver = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !String.IsNullOrEmpty(ver);
        } catch {
            return false;
        }
    }

    internal DesktopConsole(
        string dataDir,
        Rectangle initialBounds,
        FormWindowState initialState,
        Action onOpenClassic,
        Action onOpenSettings,
        Action onCheckUpdate,
        Action onPickFolder,
        Action onOpenInstaller,
        Action<string> onSkin,
        Action<Rectangle, FormWindowState> onSaveBounds) {
        userDataDir = Path.Combine(dataDir, "webview2");
        openClassic = onOpenClassic ?? (() => { });
        openSettings = onOpenSettings ?? openClassic;
        checkUpdate = onCheckUpdate ?? (() => { });
        pickFolder = onPickFolder ?? (() => { });
        openInstaller = onOpenInstaller ?? (() => { });
        onSkinChanged = onSkin;
        saveBounds = onSaveBounds;

        Text = "Remote AI";
        Font = Theme.Text();
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        Theme.ApplyIcon(this);

        // PerMonitorV2 reports window bounds in physical pixels; scale minimum/default DIPs by the
        // target monitor's DPI so high-DPI screens never collapse WebView2 into the mobile layout.
        Screen targetScreen = initialBounds.Width > 0 && IsOnAnyScreen(initialBounds)
            ? Screen.FromRectangle(initialBounds)
            : Screen.PrimaryScreen;
        Rectangle work = targetScreen.WorkingArea;
        float dpiScale = MonitorDpiScale(targetScreen);
        Size minSize = ScaledMinimumSize(work, dpiScale);
        MinimumSize = minSize;

        if (initialBounds.Width >= minSize.Width && initialBounds.Height >= minSize.Height && IsOnAnyScreen(initialBounds)) {
            StartPosition = FormStartPosition.Manual;
            Bounds = initialBounds;
            if (initialState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
        } else {
            int defW = Math.Max(minSize.Width, Math.Min((int)Math.Round(LogicalDefaultWidth * dpiScale), (int)Math.Round(work.Width * 0.92)));
            int defH = Math.Max(minSize.Height, Math.Min((int)Math.Round(LogicalDefaultHeight * dpiScale), (int)Math.Round(work.Height * 0.90)));
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(
                work.Left + Math.Max(0, (work.Width - defW) / 2),
                work.Top + Math.Max(0, (work.Height - defH) / 2),
                defW,
                defH);
        }

        loadingLabel = new Label {
            Text = "正在启动 Remote AI 总控台…",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Theme.Bg,
            ForeColor = Theme.Muted,
            Font = Theme.Text(11f)
        };
        Controls.Add(loadingLabel);

        webView = new WebView2 {
            Dock = DockStyle.Fill,
            Visible = false,
            DefaultBackgroundColor = Theme.Bg
        };
        Controls.Add(webView);

        FormClosing += (s, e) => {
            PersistWindowGeometry();
            if (!forceClose && e.CloseReason == CloseReason.UserClosing) {
                e.Cancel = true;
                Hide();
            }
        };
        ResizeEnd += (s, e) => PersistWindowGeometry();
        DpiChanged += (s, e) => {
            if (IsDisposed) return;
            Screen cur = Screen.FromControl(this);
            MinimumSize = ScaledMinimumSize(cur.WorkingArea, Math.Max(1f, e.DeviceDpiNew / 96f));
        };
        // Start initializing WebView2 immediately when the window is created so first paint is instant.
        Shown += async (s, e) => { await EnsureInitializedAsync(); };
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        Theme.Title(Handle);
    }

    internal void ApplyTheme() {
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        if (loadingLabel != null) { loadingLabel.BackColor = Theme.Bg; loadingLabel.ForeColor = Theme.Muted; }
        if (webView != null) webView.DefaultBackgroundColor = Theme.Bg;
        if (IsHandleCreated) Theme.Title(Handle);
    }

    internal void SetStatusText(string text) {
        if (IsDisposed || loadingLabel == null || !loadingLabel.Visible) return;
        loadingLabel.Text = text;
    }

    internal async void NotifyRefresh() {
        if (!initialized || webView == null || webView.CoreWebView2 == null || IsDisposed) return;
        try { await webView.ExecuteScriptAsync("window.RemoteAiConsoleRefresh && window.RemoteAiConsoleRefresh();"); } catch { }
    }

    internal async void ShowToast(string text, bool bad = false) {
        if (!initialized || webView == null || webView.CoreWebView2 == null || IsDisposed || String.IsNullOrEmpty(text)) return;
        try {
            string js = "window.RemoteAiConsoleToast && window.RemoteAiConsoleToast(" + JsQuote(text) + "," + (bad ? "true" : "false") + ");";
            await webView.ExecuteScriptAsync(js);
        } catch { }
    }

    static bool IsConsoleUrl(string uri, string origin) {
        if (String.IsNullOrEmpty(uri) || String.IsNullOrEmpty(origin)) return false;
        string cleanOrigin = origin.TrimEnd('/');
        if (uri.StartsWith(cleanOrigin + "/console/", StringComparison.OrdinalIgnoreCase)) return true;
        if (String.Equals(uri, cleanOrigin + "/console", StringComparison.OrdinalIgnoreCase)) return true;
        return uri.StartsWith(cleanOrigin + "/console?", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith(cleanOrigin + "/console#", StringComparison.OrdinalIgnoreCase);
    }

    internal void ForceClose() {
        forceClose = true;
        PersistWindowGeometry();
        try {
            if (webView != null) { webView.Dispose(); webView = null; }
        } catch { }
        try { Close(); } catch { }
    }

    internal async void NavigateTo(string origin, string ticket, string openId = "", string tab = "", string skin = "") {
        string cleanOrigin = (origin ?? "").TrimEnd('/');
        if (cleanOrigin.Length == 0) return;

        // If already loaded on the desktop console of the same origin, jump directly in-place without reloading.
        // If WebView2 ever navigated away from /console/, force a reload back into /console/.
        if (initialized && webView != null && webView.CoreWebView2 != null
            && String.Equals(currentOrigin, cleanOrigin, StringComparison.OrdinalIgnoreCase)
            && IsConsoleUrl(webView.CoreWebView2.Source, cleanOrigin)) {
            if (!String.IsNullOrEmpty(openId) || !String.IsNullOrEmpty(tab)) {
                string js = "window.RemoteAiConsoleNavigate && window.RemoteAiConsoleNavigate(" + JsQuote(openId) + "," + JsQuote(tab) + ");";
                try { await webView.ExecuteScriptAsync(js); } catch { }
            }
            return;
        }

        var qs = new StringBuilder();
        if (!String.IsNullOrEmpty(openId)) qs.Append("open=").Append(Uri.EscapeDataString(openId));
        if (!String.IsNullOrEmpty(tab)) {
            if (qs.Length > 0) qs.Append('&');
            qs.Append("tab=").Append(Uri.EscapeDataString(tab));
        }
        if (!String.IsNullOrEmpty(skin)) {
            if (qs.Length > 0) qs.Append('&');
            qs.Append("skin=").Append(Uri.EscapeDataString(skin));
        }
        string query = qs.Length > 0 ? "?" + qs.ToString() : "";
        string targetUrl = cleanOrigin + "/console/" + query + (String.IsNullOrEmpty(ticket) ? "" : "#t=" + Uri.EscapeDataString(ticket));

        currentOrigin = cleanOrigin;
        pendingUrl = targetUrl;

        if (!initialized) {
            await EnsureInitializedAsync();
        }
        if (initialized && webView != null && webView.CoreWebView2 != null && !String.IsNullOrEmpty(pendingUrl)) {
            string url = pendingUrl;
            pendingUrl = "";
            try { webView.CoreWebView2.Navigate(url); } catch { }
        }
    }

    async Task EnsureInitializedAsync() {
        if (initialized || initializing || webView == null || IsDisposed) return;
        initializing = true;
        try {
            Directory.CreateDirectory(userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            if (IsDisposed || webView == null) return;
            await webView.EnsureCoreWebView2Async(env);
            if (IsDisposed || webView == null || webView.CoreWebView2 == null) return;

            var core = webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("REMOTECLI_DEVTOOLS") == "1";
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreHostObjectsAllowed = false;

            // Keep the top-level DesktopConsole WebView2 locked to /console/ so links to /computer/, /files/,
            // or mobile / can never replace the desktop workspace with the phone UI.
            core.NavigationStarting += (s, args) => {
                string uri = args.Uri ?? "";
                if (String.IsNullOrEmpty(uri) || uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return;
                if (!String.IsNullOrEmpty(currentOrigin) && !IsConsoleUrl(uri, currentOrigin)) {
                    args.Cancel = true;
                    if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
                        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
                    }
                }
            };

            core.NavigationCompleted += (s, args) => {
                if (IsDisposed) return;
                if (webView != null) webView.Visible = true;
                if (loadingLabel != null) loadingLabel.Visible = false;
            };

            core.NewWindowRequested += (s, args) => {
                args.Handled = true;
                string uri = args.Uri ?? "";
                if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
                    try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
                }
            };

            core.WebMessageReceived += (s, args) => {
                try {
                    string msg = args.TryGetWebMessageAsString() ?? "";
                    if (msg == "open-classic") BeginInvoke(new Action(openClassic));
                    else if (msg == "open-settings") BeginInvoke(new Action(openSettings));
                    else if (msg == "check-update") BeginInvoke(new Action(checkUpdate));
                    else if (msg == "pick-folder") BeginInvoke(new Action(pickFolder));
                    else if (msg == "open-installer") BeginInvoke(new Action(openInstaller));
                    else if (msg.StartsWith("skin:", StringComparison.Ordinal) && onSkinChanged != null) {
                        string skin = msg.Substring(5).Trim();
                        BeginInvoke(new Action(() => onSkinChanged(skin)));
                    }
                } catch { }
            };

            initialized = true;
            if (!String.IsNullOrEmpty(pendingUrl)) {
                string url = pendingUrl;
                pendingUrl = "";
                try { core.Navigate(url); } catch { }
            }
        } catch (Exception error) {
            if (!IsDisposed && loadingLabel != null) {
                loadingLabel.Text = "无法初始化 Edge WebView2 总控台：" + error.Message;
            }
        } finally {
            initializing = false;
        }
    }

    void PersistWindowGeometry() {
        if (saveBounds == null) return;
        try {
            Rectangle rect = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (rect.Width >= MinimumSize.Width && rect.Height >= MinimumSize.Height) saveBounds(rect, WindowState);
        } catch { }
    }

    static Size ScaledMinimumSize(Rectangle workArea, float dpiScale) {
        float s = Math.Max(1f, dpiScale);
        int minW = Math.Min((int)Math.Round(LogicalMinWidth * s), Math.Max(640, workArea.Width));
        int minH = Math.Min((int)Math.Round(LogicalMinHeight * s), Math.Max(480, workArea.Height));
        return new Size(minW, minH);
    }

    float MonitorDpiScale(Screen screen) {
        try {
            var center = new POINT {
                X = screen.WorkingArea.Left + screen.WorkingArea.Width / 2,
                Y = screen.WorkingArea.Top + screen.WorkingArea.Height / 2
            };
            IntPtr mon = MonitorFromPoint(center, 2); // MONITOR_DEFAULTTONEAREST
            uint dx, dy;
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx >= 96) {
                return dx / 96f;
            }
        } catch { }
        try {
            using (var g = CreateGraphics()) return Math.Max(1f, g.DpiX / 96f);
        } catch { }
        return 1f;
    }

    static bool IsOnAnyScreen(Rectangle rect) {
        foreach (Screen screen in Screen.AllScreens) {
            if (screen.WorkingArea.IntersectsWith(rect)) return true;
        }
        return false;
    }

    static string JsQuote(string value) {
        if (String.IsNullOrEmpty(value)) return "''";
        return "'" + value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "") + "'";
    }
}

}
