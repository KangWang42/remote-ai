using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RemoteCli {

/// Native Windows desktop console window hosting the unified Preact/TS workspace inside Edge WebView2.
/// Opens directly as the primary window on launch while keeping installer overhead under 800 KB.
internal sealed class DesktopConsole : Form {
    readonly string userDataDir;
    readonly Action openClassic;
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
        Action onPickFolder,
        Action onOpenInstaller,
        Action<string> onSkin,
        Action<Rectangle, FormWindowState> onSaveBounds) {
        userDataDir = Path.Combine(dataDir, "webview2");
        openClassic = onOpenClassic ?? (() => { });
        pickFolder = onPickFolder ?? (() => { });
        openInstaller = onOpenInstaller ?? (() => { });
        onSkinChanged = onSkin;
        saveBounds = onSaveBounds;

        Text = "Remote AI";
        Font = Theme.Text();
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        MinimumSize = new Size(880, 560);
        Theme.ApplyIcon(this);

        if (initialBounds.Width >= 880 && initialBounds.Height >= 560 && IsOnAnyScreen(initialBounds)) {
            StartPosition = FormStartPosition.Manual;
            Bounds = initialBounds;
            if (initialState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
        } else {
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1340, 860);
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

        // If already loaded on the same origin, jump directly in-place without reloading the WebView.
        if (initialized && webView != null && webView.CoreWebView2 != null && String.Equals(currentOrigin, cleanOrigin, StringComparison.OrdinalIgnoreCase)) {
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

            core.NavigationCompleted += (s, args) => {
                if (IsDisposed) return;
                if (webView != null) webView.Visible = true;
                if (loadingLabel != null) loadingLabel.Visible = false;
            };

            core.NewWindowRequested += (s, args) => {
                args.Handled = true;
                string uri = args.Uri ?? "";
                if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
                    if (!String.IsNullOrEmpty(currentOrigin) && uri.StartsWith(currentOrigin + "/", StringComparison.OrdinalIgnoreCase)) {
                        try { core.Navigate(uri); } catch { }
                    } else {
                        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
                    }
                }
            };

            core.WebMessageReceived += (s, args) => {
                try {
                    string msg = args.TryGetWebMessageAsString() ?? "";
                    if (msg == "open-classic") BeginInvoke(new Action(openClassic));
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
            if (rect.Width >= 880 && rect.Height >= 560) saveBounds(rect, WindowState);
        } catch { }
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
