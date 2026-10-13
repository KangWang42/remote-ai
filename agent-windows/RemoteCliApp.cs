using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RemoteCli {
/// The program on the computer: a small window and a tray icon around the relay, the optional tunnel and the
/// terminal agent. Everything it starts ends when it exits.
public sealed partial class App : Form {
    const string Version = "1.6.2";
    // cloudflared pinned to one release, checked against the sha256 Cloudflare publishes
    // for the artifact (the digest field of the release API, cross-checked 2026-10-12).
    const string TunnelDownload = "https://github.com/cloudflare/cloudflared/releases/download/2026.10.0/cloudflared-windows-amd64.exe";
    const long TunnelBytes = 55365048;
    const string TunnelSha256 = "86aee4017b26625cee8484c113558f48effa4cd47f7aa05fcf425604e5d2b23c";
    readonly string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    readonly string dataDir = TerminalAgent.DefaultData;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    Dictionary<string, object> config;
    string password = "", address = "";
    Process relay, tunnel, kept;
    string keptUrl = "";
    TerminalAgent agent;
    int generation;

    readonly StatusLine status = new StatusLine(), sideStatus = new StatusLine(), sideAbove = new StatusLine();
    // What the side of the window says: how the phone's access stands and, when a tool is connected, which.
    string sideLine = "正在启动…", sideTools = ""; Color sideColour = Theme.Busy; DateTime toolsSeen;
    readonly Segmented modePick = new Segmented();
    readonly Label modeAbout = new Label(), qrHint = new Label(), toast = new Label(), activityNote = new Label();
    readonly Field addressField = new Field(), passwordField = new Field();
    TextBox addressBox { get { return addressField.Box; } }
    TextBox passwordBox { get { return passwordField.Box; } }
    readonly PictureBox qr = new PictureBox();
    readonly RowList folders = new RowList(), activity = new RowList();
    // What the list of folders shows: the projects, or the folders with conversations that are no projects yet
    // (those used in a terminal; those used by a desktop app or an editor).
    readonly Segmented projectPick = new Segmented { Items = new[] { "已添加", "终端项目", "应用项目" } };
    string offeredMark = "";
    readonly Switch enabledSwitch = new Switch(), autostartSwitch = new Switch(), autoUpdateSwitch = new Switch(), reachSwitch = new Switch(), askSwitch = new Switch();
    SettingRow reachRow, updateRow;
    readonly Segmented rights = new Segmented { Items = new[] { "默认", "自动改文件", "只读" } }, protocol = new Segmented { Items = new[] { "自动", "HTTP/2" } };
    readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 10 * 60 * 1000 }, quietTimer = new System.Windows.Forms.Timer { Interval = 5000 };
    ReleaseUpdate waitingUpdate;
    readonly List<Panel> pages = new List<Panel>();
    readonly List<NavItem> nav = new List<NavItem>();
    readonly System.Windows.Forms.Timer toastTimer = new System.Windows.Forms.Timer(), activityTimer = new System.Windows.Forms.Timer();
    RoundButton updateButton, tunnelButton, relayGo, changePassword;
    Label codeAbout;
    readonly Picker relayPick = new Picker(), skinPick = new Picker(), waitPick = new Picker();
    readonly ToolTip tips = new ToolTip { AutoPopDelay = 30000, InitialDelay = 300 };
    Relays relays;
    Relay shown;                    // the relay the field in the relay tab shows while one is being chosen
    readonly Dictionary<string, int> answers = new Dictionary<string, int>();      // how long each relay took to answer, when it was asked last
    DateTime asked = DateTime.MinValue, listRead = DateTime.MinValue;
    // relayTab: a tab of relays is open while another way is in use; everyone: it is the one of the relays for
    // everyone, not of one's own; relayTrouble: what it last said is a problem; relayBusy: a relay is being checked
    bool relayTab, everyone, relayTrouble, relayBusy;
    string relayNote = "", said = "";       // said: the line of status shown last
    Dictionary<Control, int[]> parts;       // which colour of the skin each control has
    // A window that a test presses stays off the screen and takes no keys: someone at the computer must not mistake it for the program.
    static bool offstage;
    protected override bool ShowWithoutActivation { get { return offstage; } }
    string activityMark = "";
    bool trouble, preparing = true;
    static string ComputerName = Environment.MachineName;
    readonly NotifyIcon tray = new NotifyIcon();
    bool quitting, hinted, updating;
    ReleaseUpdate availableUpdate;

    string ConfigFile { get { return Path.Combine(dataDir, "config.json"); } }
    string PasswordFile { get { return Path.Combine(dataDir, "password.dpapi"); } }
    string TunnelFile { get { return Path.Combine(dataDir, "cloudflared.exe"); } }
    string ChildrenFile { get { return Path.Combine(dataDir, "children.json"); } }
    string KeptFile { get { return Path.Combine(dataDir, "tunnel.json"); } }
    string Mode { get { return Convert.ToString(config["Mode"]); } }
    int Port { get { return Convert.ToInt32(config["Port"]); } }

    // ---------- settings
    void LoadSettings() {
        Directory.CreateDirectory(dataDir);
        try { config = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigFile, Encoding.UTF8)); } catch { config = null; }
        if (config == null) config = new Dictionary<string, object>();
        foreach (var item in new Dictionary<string, object> { { "Mode", "lan" }, { "Port", 8722 }, { "Server", "" }, { "OwnServer", "" }, { "RemoteEnabled", true },
            { "RemoteMaxMode", "full" }, { "TunnelProtocol", "auto" }, { "AutoUpdate", false }, { "RemoteDirs", new object[0] }, { "UpdateCheckUtc", "" }, { "Relay", "" }, { "Skin", Theme.Default }, { "Reach", "" }, { "ReachHeard", new object[0] }, { "ReachWait", 0 }, { "ReachAsk", true }, { "ToolInstallSuggested", "" } })
            if (!config.ContainsKey(item.Key) || config[item.Key] == null) config[item.Key] = item.Value;
        if (Array.IndexOf(new[] { "lan", "cloud", "own", "public" }, Mode) < 0) config["Mode"] = "lan";
        try { password = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(PasswordFile), null, DataProtectionScope.CurrentUser)); } catch { password = ""; }
        if (password.Length == 0) SetPassword(NewPassword());
    }
    // Settings are changed from the window and from the thread that connects, at a start with one's own relay at the
    // same moment: a change and the writing of the file are one step, one at a time. Every setting is there from
    // LoadSettings on, so a change never adds one while another thread reads.
    readonly object saving = new object();
    void Save() {
        lock (saving) {
            string temporary = ConfigFile + ".tmp";
            File.WriteAllText(temporary, json.Serialize(config), new UTF8Encoding(false));
            if (File.Exists(ConfigFile)) File.Replace(temporary, ConfigFile, null); else File.Move(temporary, ConfigFile);
        }
    }
    void Keep(string key, object value) { lock (saving) { config[key] = value; Save(); } }
    static string NewPassword() {
        byte[] bytes = new byte[10];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        string hex = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        return String.Join("-", Enumerable.Range(0, 4).Select(i => hex.Substring(i * 5, 5)));
    }
    void SetPassword(string value) {
        WritePassword(value);
        // Phones that signed in with the old password must sign in again.
        try { File.Delete(Path.Combine(dataDir, "relay", "sessions.json")); } catch { }
    }
    // The password the agent signs in with where it reports. Who is signed in at the program's own relay is left alone.
    void WritePassword(string value) {
        password = value;
        File.WriteAllBytes(PasswordFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }
    List<string> Dirs() { return ((System.Collections.IEnumerable)config["RemoteDirs"]).Cast<object>().Select(Convert.ToString).ToList(); }

    // ---------- the programs this one starts
    void Remember() {
        var beside = change;
        var ids = new[] { relay, tunnel, beside == null ? null : beside.Relay, beside == null ? null : beside.Tunnel }.Where(p => p != null).Select(p => { try { return p.HasExited ? 0 : p.Id; } catch { return 0; } }).Where(id => id != 0).ToArray();
        try { File.WriteAllText(ChildrenFile, json.Serialize(ids)); } catch { }
    }
    static void Kill(Process process) { try { if (process != null && !process.HasExited) { process.Kill(); process.WaitForExit(3000); } } catch { } }
    // A copy that crashed may have left its relay or tunnel running; they hold the port.
    void KillLeftovers() {
        try {
            foreach (object id in json.Deserialize<object[]>(File.ReadAllText(ChildrenFile)))
                try { var p = Process.GetProcessById(Convert.ToInt32(id)); if (kept != null && p.Id == kept.Id) continue; if (p.ProcessName == "python" || p.ProcessName == "cloudflared") Kill(p); } catch { }
        } catch { }
    }
    // An update leaves the tunnel running: the new version takes it over, so the phone keeps the address it knows.
    void StopChildren() { if (!updating) { Kill(tunnel); tunnel = null; } Kill(relay); relay = null; Remember(); }
    void Keep(string url, int port) {
        try {
            File.WriteAllText(KeptFile, json.Serialize(new Dictionary<string, object> { { "Pid", tunnel.Id }, { "Started", tunnel.StartTime.ToFileTimeUtc().ToString() }, { "Url", url },
                { "Port", port }, { "Protocol", Convert.ToString(config["TunnelProtocol"]) } }));
        } catch { }
    }
    // The tunnel an update or a crash of this program left running, when it still fits the settings.
    Process Kept() {
        try {
            var was = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(KeptFile));
            var p = Process.GetProcessById(Convert.ToInt32(was["Pid"]));
            if (p.ProcessName != "cloudflared" || p.StartTime.ToFileTimeUtc().ToString() != Convert.ToString(was["Started"])) return null;
            if (Mode != "cloud" || Convert.ToInt32(was["Port"]) != Port || Convert.ToString(was["Protocol"]) != Convert.ToString(config["TunnelProtocol"])) return null;
            keptUrl = Convert.ToString(was["Url"]);
            return p;
        } catch { return null; }
    }
    // Cloudflare answers 530 for an address whose tunnel is gone; anything else, even no answer, leaves it in use.
    static bool Gone(string url) {
        try {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url + "/api/session"); request.Timeout = 8000;
            using (request.GetResponse()) return false;
        } catch (WebException error) { var reply = error.Response as HttpWebResponse; return reply != null && (int)reply.StatusCode == 530; }
        catch { return false; }
    }
    static bool Listening(int port) {
        try { using (var client = new TcpClient()) { var attempt = client.BeginConnect("127.0.0.1", port, null, null); bool ok = attempt.AsyncWaitHandle.WaitOne(400) && client.Connected; return ok; } } catch { return false; }
    }
    // REMOTECLI_LAN names the address the program's relay listens on for the local network, which is then also the
    // one the phone is given: a test names 127.0.0.1, so that nothing is opened to the network it runs in.
    static readonly string LanOnly = (Environment.GetEnvironmentVariable("REMOTECLI_LAN") ?? "").Trim();
    static string LanAddress() {
        if (LanOnly.Length > 0) return LanOnly;
        return LanNetwork.Address();
    }

    void Say(string text, bool problem = false) {
        if (InvokeRequired) { BeginInvoke(new Action(() => Say(text, problem))); return; }
        said = text;
        status.Set(text, problem ? Theme.Bad : text.StartsWith("正在") ? Theme.Busy : Theme.Good);
        trouble = problem; preparing = !problem && text.StartsWith("正在");
        if (nav.Count > 0) RefreshActivity();
        tray.Text = ("Remote AI：" + text).Length > 60 ? ("Remote AI：" + text).Substring(0, 60) : "Remote AI：" + text;
    }
    void ShowAddress(string where) {
        if (InvokeRequired) { BeginInvoke(new Action(() => ShowAddress(where))); return; }
        address = where;
        Image old = qr.Image;
        string link = "remotecli://connect?u=" + Uri.EscapeDataString(where) + "&p=" + Uri.EscapeDataString(password);
        // The computer's name lets a phone that knows several computers tell them apart; it is left out when the code would not fit.
        qr.Image = where.Length > 0 ? (Picture(link + "&n=" + Uri.EscapeDataString(ComputerName), 6) ?? Picture(link, 6)) : null;
        qr.Visible = qr.Image != null; qrHint.Visible = qr.Image == null;
        if (old != null) old.Dispose();
        ShowMode();
    }
    public static Bitmap Picture(string text, int scale) {
        bool[,] dark = QrCode.Encode(text);
        if (dark == null) return null;
        int size = dark.GetLength(0), quiet = 4;
        var picture = new Bitmap((size + quiet * 2) * scale, (size + quiet * 2) * scale);
        using (var g = Graphics.FromImage(picture)) {
            g.Clear(Color.White);
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) if (dark[y, x]) g.FillRectangle(Brushes.Black, (x + quiet) * scale, (y + quiet) * scale, scale, scale);
        }
        return picture;
    }

    string Python { get { return Path.Combine(appDir, "python", "python.exe"); } }
    // The program's own relay: for this computer alone (behind the tunnel), or for the local network as well.
    Process StartRelay(bool network, int port, string secret) {
        var start = new ProcessStartInfo(Python, "\"" + Path.Combine(appDir, "relay", "server.py") + "\" --host " + (!network ? "127.0.0.1" : LanOnly.Length > 0 ? LanOnly : "0.0.0.0") + " --port " + port
            + " --data \"" + Path.Combine(dataDir, "relay") + "\" --web \"" + Path.Combine(appDir, "web") + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = appDir };
        start.EnvironmentVariables["RCLI_PASSWORD"] = secret;
        start.EnvironmentVariables["PYTHONUTF8"] = "1";
        return Process.Start(start);
    }
    async Task<bool> Up(Process process, int port) {
        for (int n = 0; n < 40 && !Listening(port); n++) { await Task.Delay(250); if (process.HasExited) break; }
        return Listening(port);
    }
    // A tunnel to the program's own relay. `address` comes to the address it was given, or to nothing when it ended
    // or gave none in forty seconds.
    Process StartTunnel(int port, out Task<string> address) {
        var found = new TaskCompletionSource<string>();
        var run = new ProcessStartInfo(TunnelFile, "tunnel --no-autoupdate" + (Convert.ToString(config["TunnelProtocol"]) == "http2" ? " --protocol http2" : "") + " --url http://127.0.0.1:" + port) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = dataDir };
        var process = new Process { StartInfo = run, EnableRaisingEvents = true };
        DataReceivedEventHandler look = (s, e) => { if (e.Data == null) return; var m = Regex.Match(e.Data, @"https://[a-z0-9-]+\.trycloudflare\.com"); if (m.Success) found.TrySetResult(m.Value); };
        process.ErrorDataReceived += look; process.OutputDataReceived += look;
        process.Exited += (s, e) => found.TrySetResult("");
        process.Start(); process.BeginErrorReadLine(); process.BeginOutputReadLine();
        address = Task.WhenAny(found.Task, Task.Delay(40000)).ContinueWith(first => found.Task.IsCompleted ? found.Task.Result : "");
        return process;
    }
    // What is said when the tunnel in use ends by itself. `mine` is the generation it belongs to: one that was
    // stopped on purpose belongs to an earlier one.
    EventHandler Lost(int mine) {
        return (s, e) => { if (mine == generation && !quitting) { ShowAddress(""); Say("公网隧道断开了，点“重新连接”。经常断开时到“设置”把隧道协议改为 HTTP/2", true); } };
    }

    // Starts what the chosen way of connecting needs. Runs off the window's thread.
    async Task Connect() {
        int mine = ++generation;
        Process held = kept; kept = null;      // only the first start takes a tunnel over; "reconnect" always makes a new one
        ShowAddress("");
        StopChildren();
        string mode = Mode;
        try {
            if (mode == "own" || mode == "public") {
                Relay chosen = Remembered();
                string server, wrong = "";
                if (mode == "public") {
                    // A chosen for everyone gave this computer a place of its own; one it removed meanwhile is asked for again.
                    if (chosen == null) { Say("该公共中转已从列表中移除，请在“公共中转”中另选一个", true); return; }
                    Say("正在连接公共中转…");
                    string secret;
                    wrong = relays.Space(chosen, out server, out secret);
                    if (mine != generation) return;
                    if (wrong.Length > 0) { Say(wrong, true); return; }
                    if (secret != password) SetPassword(secret);
                } else {
                    server = Relays.Clean(Convert.ToString(config["OwnServer"]), true);
                    if (server.Length == 0) { Say("尚未选择中转，请在“我的中转”中选择或添加", true); return; }
                }
                Keep("Server", server);
                if (agent != null) agent.Reset();
                ShowAddress(server);
                if (mode == "own") {
                    // Whether it works is found out here and said: the address, the certificate or the password.
                    Say("正在连接中转…");
                    wrong = TerminalAgent.CheckRelay(server, password);
                    if (mine != generation) return;
                }
                string name = chosen != null ? chosen.Name : new Uri(server).Authority;
                Say(wrong.Length > 0 ? wrong : mode == "public" ? "已连上公共中转“" + name + "”，地址固定不变。手机 App 扫码连接" : "已连上中转“" + name + "”，地址固定不变。手机 App 扫码或输入地址连接", wrong.Length > 0);
                return;
            }
            if (!File.Exists(Python)) { Say("安装不完整：缺少 " + Python, true); return; }
            int port = Port;
            if (Listening(port)) { Say("端口 " + port + " 已被别的程序占用，请到“设置”里换一个端口", true); return; }
            relay = StartRelay(mode == "lan", port, password);
            Remember();
            bool up = await Up(relay, port);
            if (mine != generation) return;
            if (!up) { Say("本机服务没有启动成功", true); return; }
            Keep("Server", "http://127.0.0.1:" + port);
            if (agent != null) agent.Reset();
            if (mode == "lan") {
                string ip = LanAddress();
                if (ip.Length == 0) { Say("未找到局域网地址，请先连接 Wi-Fi 或网线", true); return; }
                ShowAddress("http://" + ip + ":" + port);
                Say("局域网直连已就绪，用 App 扫码。连接失败可点“连接诊断”");
                return;
            }
            if (!File.Exists(TunnelFile)) { Say("尚未下载隧道程序，请点下方的“下载隧道程序”", true); return; }
            EventHandler lost = Lost(mine);
            if (held != null && !held.HasExited && !Gone(keptUrl)) {
                if (mine != generation) return;
                tunnel = held; tunnel.Exited += lost; tunnel.EnableRaisingEvents = true;
                Remember();
                ShowAddress(keptUrl);
                Say("公网隧道已就绪，地址和上次相同，手机不用重新扫码");
                return;
            }
            Say("正在建立公网隧道…");
            Task<string> given;
            tunnel = StartTunnel(port, out given);
            tunnel.Exited += lost;
            Remember();
            string url = await given;
            if (mine != generation) return;
            if (url.Length == 0) { Kill(tunnel); Say("公网隧道没有建立成功，请检查网络后重试", true); return; }
            Keep(url, port);
            ShowAddress(url);
            Say("公网隧道已就绪。手机在任何网络下用 App 扫码；更新程序后地址不变");
        } catch (Exception error) { Say("启动失败：" + error.Message, true); }
        finally { if (held != null && held != tunnel) Kill(held); }
    }
    void Reconnect() { Task.Run(() => Connect()); }
    // "重新连接" does what the tab shown says: with the relay tab open while another way is in use, it connects to
    // the relay the field shows, as "连接" beside the field does.
    void Again() {
        if (Held()) return;
        if (!relayTab) { Reconnect(); return; }
        Relay relay = Showing();
        if (relay != null) Use(relay); else relayPick.Open();
    }

    async void DownloadTunnel(object sender, EventArgs e) {
        if (Held()) return;
        if (File.Exists(TunnelFile)) { Reconnect(); return; }
        if (!Confirm("下载隧道程序？", "从 Cloudflare 的 GitHub 发布页下载 cloudflared（约 60 MB），用来建立临时公网地址，不需要账号。", "下载", false)) return;
        var button = (Button)sender;
        button.Enabled = false;
        try {
            await FetchTunnel(percent => Say("正在下载隧道程序 " + percent + "%"));
            Reconnect();
        } catch (Exception error) {
            Say("下载失败：" + error.Message + "。也可以自己下载 cloudflared-windows-amd64.exe，改名为 cloudflared.exe 放到 " + dataDir, true);
        } finally { button.Enabled = true; }
    }
    // cloudflared, from Cloudflare's release page, into the data folder. Throws when it could not be had whole.
    async Task FetchTunnel(Action<int> progress) {
        try {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new WebClient()) {
                client.DownloadProgressChanged += (s, p) => progress(p.ProgressPercentage);
                await client.DownloadFileTaskAsync(new Uri(TunnelDownload), TunnelFile + ".part");
            }
            var part = new FileInfo(TunnelFile + ".part");
            if (part.Length != TunnelBytes) throw new IOException("cloudflared 大小不符，已阻止安装未经验证的程序。");
            string hex;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = part.OpenRead()) {
                hex = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
            if (!string.Equals(hex, TunnelSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("cloudflared 校验不符，已阻止安装未经验证的程序。请检查网络环境后重试。");
            File.Move(TunnelFile + ".part", TunnelFile);
        } catch (Exception) {
            try { File.Delete(TunnelFile + ".part"); } catch { }
            throw;
        }
    }

    // ---------- window
    const int SideWidth = 212, PageWidth = 708, WindowHeight = 600;
    T Place<T>(Control parent, T control, int x, int y, int w, int h) where T : Control {
        control.SetBounds(x, y, w, h);
        parent.Controls.Add(control);
        return control;
    }
    Label Note(Control parent, string text, int x, int y, int w, int h, Color color, float size = 0, bool bold = false) {
        return Place(parent, Theme.Label(text, color, parent.BackColor, size, bold), x, y, w, h);
    }
    RoundButton Action(Control parent, string text, string glyph, ButtonKind kind, int x, int y, int w, int h, EventHandler click) {
        var button = Place(parent, new RoundButton { Text = text, Glyph = glyph, Kind = kind }, x, y, w, h);
        if (click != null) button.Click += click;
        return button;
    }
    Panel Page(string title, string about) {
        var page = Place(this, new Panel { BackColor = Theme.Bg, Visible = false }, SideWidth, 0, PageWidth, WindowHeight);
        Note(page, title, 28, 24, 420, 30, Theme.Ink, 15f, true);
        Note(page, about, 28, 56, 652, 20, Theme.Muted);
        pages.Add(page);
        return page;
    }
    void Go(int index) {
        for (int i = 0; i < pages.Count; i++) { pages[i].Visible = i == index; nav[i].Chosen = i == index; }
        if (index == 2) RefreshActivity();
        if (index == 1) { offeredMark = ""; ShowProjects(); }
    }
    // A short confirmation that fades by itself, e.g. after copying.
    void Toast(string text) {
        toast.Text = text;
        using (var g = CreateGraphics()) toast.Width = (int)g.MeasureString(text, toast.Font).Width + (int)(36 * g.DpiX / 96f);
        toast.Left = pages[0].Left + (pages[0].Width - toast.Width) / 2;
        toast.Visible = true; toast.BringToFront();
        toastTimer.Stop(); toastTimer.Start();
    }
    void Copy(string text, string what) {
        if (String.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); Toast("已复制" + what); } catch (ExternalException) { Toast("复制失败，请再试一次"); }
    }
    // ---------- relays
    // Relays are of two kinds, each with a tab of its own: those for everyone, from the published list, and the
    // owner's own, saved here. The tab's field shows one relay of its kind; a press lists them with how fast each
    // answers, and among one's own a relay is added or taken away there. However many relays there come to be, the
    // window keeps its four ways of connecting. Choosing a relay checks it before anything is changed: the way in use
    // goes on until the relay is reached and takes the password.
    bool RelayMode { get { return Mode == "own" || Mode == "public"; } }
    void RelaySays(string text, bool problem) { relayNote = text; relayTrouble = problem; ShowMode(); }
    void Put(string where, string secret) { addressBox.Text = where; passwordBox.Text = secret; }
    List<Relay> Known() {
        var all = relays.Published(); all.AddRange(relays.Own());
        lock (answers) foreach (Relay relay in all) { int ms; if (answers.TryGetValue(relay.Id, out ms)) relay.Ms = ms; }
        return all;
    }
    Relay Remembered() { string id = Convert.ToString(config["Relay"]); return id.Length == 0 ? null : Known().FirstOrDefault(r => r.Id == id); }
    // The relay the field shows: the one in use; or, while one is being chosen, the one chosen last, else the one
    // that was used last if it is of the tab's kind, else the first of that kind.
    Relay Showing() {
        if (RelayMode && !relayTab) return Remembered() ?? new Relay { Public = Mode == "public", Name = Mode == "public" ? "已从列表中移除" : Convert.ToString(config["OwnServer"]), Url = Mode == "public" ? "" : Convert.ToString(config["OwnServer"]), Password = password };
        var kind = Known().Where(r => r.Public == everyone).ToList();
        // (a relay that is being added is not among them yet, and is shown all the same)
        if (shown != null && shown.Public != everyone) shown = null;
        if (shown != null) { Relay now = kind.FirstOrDefault(r => r.Id == shown.Id); if (now != null) shown = now; }
        string last = Convert.ToString(config["Relay"]);
        return shown ?? kind.FirstOrDefault(r => r.Id == last) ?? kind.FirstOrDefault();
    }
    // Which tab of relays is looked at, and whether its kind is the way in use.
    void Look(bool forEveryone) { everyone = forEveryone; relayTab = Mode != (forEveryone ? "public" : "own"); }
    static string Took(Relay relay) { return relay.Ms >= 0 ? relay.Ms + " ms" : relay.Ms == -1 ? "连不上" : ""; }
    // Asks every relay how fast it answers, beside what the window is doing; the answers appear as they come.
    void AskRelays() {
        asked = DateTime.UtcNow;
        foreach (Relay each in Known()) {
            Relay relay = each;
            Task.Run(() => { int ms = Relays.Ask(relay.Url); try { BeginInvoke(new Action(() => { lock (answers) answers[relay.Id] = ms; if (modePick.Chosen >= 2) ShowMode(); })); } catch (InvalidOperationException) { } });
        }
    }
    void ReadList() {
        listRead = DateTime.UtcNow;
        Task.Run(() => { relays.Refresh(); try { BeginInvoke(new Action(() => { ShowMode(); if (modePick.Chosen >= 2) AskRelays(); })); } catch (InvalidOperationException) { } });
    }
    // The relays of the tab's kind, to choose one from; among one's own, also adding one and taking one away.
    void ListRelays() {
        if (DateTime.UtcNow - asked > TimeSpan.FromSeconds(60)) AskRelays();
        var menu = Theme.Menu();
        var kind = Known().Where(r => r.Public == everyone).ToList();
        string inUse = RelayMode ? Convert.ToString(config["Relay"]) : "";
        foreach (Relay each in kind) {
            Relay relay = each;
            var item = new ToolStripMenuItem(relay.Name) { Checked = relay.Id == inUse, ShortcutKeyDisplayString = Took(relay), ShowShortcutKeys = true };
            item.Click += (s, e) => Use(relay);
            menu.Items.Add(item);
        }
        if (!everyone) {
            if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            var add = new ToolStripMenuItem("添加自己的中转…");
            add.Click += (s, e) => {
                var typed = Dialog("添加自己的中转", "填写服务器上中转的地址和密码。检查通过后保存并切换。", "添加并连接", false,
                    new[] { "地址，例如 https://cli.example.com 或 https://1.2.3.4:8443", "密码（服务器上 sudo bash install.sh password 显示的）", "名称（可不填）" }, draft);
                if (typed != null) { draft = typed; AddRelay(typed[0], typed[1], typed[2]); }
            };
            menu.Items.Add(add);
            if (kind.Count > 0) {
                var remove = new ToolStripMenuItem("移除我的中转");
                var inner = (ToolStripDropDownMenu)remove.DropDown;
                inner.Renderer = menu.Renderer; inner.ShowImageMargin = false; inner.BackColor = Theme.Panel; inner.ForeColor = Theme.Ink; inner.Font = menu.Font;
                foreach (Relay each in kind) {
                    Relay relay = each;
                    remove.DropDownItems.Add(relay.Name, null, (s, e) => RemoveRelay(relay));
                }
                menu.Items.Add(remove);
            }
        }
        if (menu.Items.Count == 0) { menu.Dispose(); return; }
        menu.MinimumSize = new Size(relayPick.Width, 0);
        menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(relayPick, new Point(0, relayPick.Height + 2));
    }
    string[] draft;         // what was typed for a relay that could not be added yet, to go on from
    void AddRelay(string address, string secret, string name) {
        string url = Relays.Clean(address, true);
        secret = (secret ?? "").Trim(); name = TerminalAgent.Tidy(name);
        Look(false);
        if (url.Length == 0) { RelaySays("地址需完整填写，以 https:// 开头，例如 https://cli.example.com 或 https://1.2.3.4:8443", true); return; }
        if (secret.Length == 0) { RelaySays("请填写中转的密码：在服务器上运行 sudo bash install.sh password 可以查看", true); return; }
        Use(new Relay { Url = url, Name = name.Length > 0 && name.Length <= 20 ? name : new Uri(url).Authority, Password = secret });
    }
    void RemoveRelay(Relay relay) {
        if (Mode == "own" && Convert.ToString(config["Relay"]) == relay.Id) { Toast("该中转正在使用，请先切换到其他连接方式再移除"); return; }
        if (!Confirm("移除“" + relay.Name + "”？", "仅删除本机保存的地址和密码，服务器上的中转不受影响。", "移除", true)) return;
        relays.Remove(relay.Url);
        if (shown != null && shown.Id == relay.Id) shown = null;
        ShowMode();
    }
    void Use(Relay relay) {
        if (relayBusy || Held()) return;
        relayBusy = true; Look(relay.Public); shown = relay;
        RelaySays("正在检查“" + relay.Name + "”…", false);
        Task.Run(() => {
            string server, secret, wrong;
            if (relay.Public) wrong = relays.Space(relay, out server, out secret);
            else { server = relay.Url; secret = relay.Password; wrong = TerminalAgent.CheckRelay(server, secret); }
            BeginInvoke(new Action(() => {
                relayBusy = false;
                if (wrong.Length > 0) { RelaySays(wrong + (RelayMode ? "。仍在使用原来的中转" : "。当前连接方式未改变"), true); if (RelayMode) shown = null; return; }
                if (!relay.Public) { relays.Save(relay); draft = null; }
                lock (saving) { if (!relay.Public) config["OwnServer"] = relay.Url; config["Relay"] = relay.Id; Keep("Mode", relay.Public ? "public" : "own"); }
                relayTab = false; relayNote = ""; shown = null;
                SetPassword(secret); ShowMode(); Reconnect();
            }));
        });
    }
    // Before relays were kept as a list there was one, with its password in a file of its own: it becomes the first of the list.
    void TakeOver() {
        string server = Relays.Clean(Convert.ToString(config["OwnServer"]), true), secret = "";
        try { secret = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(dataDir, "own-password.dpapi")), null, DataProtectionScope.CurrentUser)); } catch { }
        if (secret.Length == 0 && Mode == "own") secret = password;
        if (relays.HasOwn || server.Length == 0 || secret.Length == 0) return;
        var relay = new Relay { Url = server, Name = new Uri(server).Authority, Password = secret };
        relays.Save(relay);
        if (Convert.ToString(config["Relay"]).Length == 0) Keep("Relay", relay.Id);
    }
    // What is shown instead of the address and the password of a relay for everyone: the phone is given them by the code alone.
    bool Hidden { get { if (modePick.Chosen != 2) return false; Relay relay = Showing(); return relay != null && relay.Public; } }
    void ShowMode() {
        if (RelayMode && !relayTab) everyone = Mode == "public";
        int tab = relayTab || RelayMode ? (everyone ? 2 : 3) : Mode == "lan" ? 0 : 1;
        modePick.Chosen = tab;
        Relay relay = tab >= 2 ? Showing() : null;
        bool inUse = RelayMode && !relayTab;
        modeAbout.ForeColor = tab >= 2 && relayNote.Length > 0 ? (relayTrouble ? Theme.Bad : Theme.Busy) : Theme.Muted;
        modeAbout.Text = tab == 0 ? "需手机与电脑能够互通。校园网可能隔离设备，代理和防火墙也可能影响；连接失败可点“连接诊断”。"
            : tab == 1 ? "不需要服务器和账号，手机在任何网络都能连。更新程序后地址不变；退出程序或重启电脑后地址会变，手机需要重新扫码。"
            : relayNote.Length > 0 ? relayNote
            : tab == 2 ? (relay == null ? "暂时没有可用的公共中转。可以使用公网隧道，或在“我的中转”中添加自己的中转。"
                : inUse ? "正在使用公共中转，地址固定。终端内容经由该中转服务器传输；如有顾虑，请使用自己的中转。"
                : "无需配置，地址固定。在下方选一条线路后点“连接”；连接成功之前，当前连接方式保持不变。")
            : relay == null ? "尚未添加中转。可在下方添加自己服务器上的中转，部署方法见文档“自己部署中转”。"
            : inUse ? "正在使用自己的中转，地址固定。可在下方切换或添加中转。"
            : "选择一个中转后点“连接”，也可以在下方添加新的中转。连接成功之前，当前连接方式保持不变。";
        tunnelButton.Visible = tab == 1; relayPick.Visible = relayGo.Visible = tab >= 2; changePassword.Visible = tab < 2;
        relayPick.Set(relay != null ? relay.Name : tab == 2 ? "暂无公共中转" : "添加自己的中转…", relay == null ? "" : Took(relay), relay != null && relay.Ms == -1 ? 2 : 0);
        relayGo.Enabled = !relayBusy && (relay != null || tab == 3);
        // The two boxes show what the phone is to use; of a relay for everyone they show neither.
        if (tab < 2) Put(address, password);
        else if (relay == null) Put("", "");
        else if (relay.Public) Put("公共中转 · " + relay.Name, "不显示，请用手机扫码连接");
        else Put(relay.Url, relay.Password);
        codeAbout.Text = Mode == "public" ? "公共中转仅支持扫码连接" : "或在 App 里输入右边的地址和密码";
        tunnelButton.Text = File.Exists(TunnelFile) ? "重新建立隧道" : "下载隧道程序（约 60 MB）";
        tunnelButton.Glyph = File.Exists(TunnelFile) ? Theme.IconRefresh : Theme.IconDownload;
    }
    // Another skin for the window that is open: every control is given the same part of the new palette.
    void Wear(string name) {
        Theme.Use(name);
        if (parts != null) Theme.Paint(parts);
        if (IsHandleCreated) Theme.Title(Handle);
        skinPick.Set(Theme.Titles[Array.IndexOf(Theme.Names, Theme.Skin)]);
        ShowProjects(); activityMark = "\n"; RefreshActivity();
        Say(said.Length > 0 ? said : "正在启动…", trouble);
        ShowMode();
        Invalidate(true);
    }
    Action projectsShown = () => { };
    void ShowProjects() {
        var lines = Dirs();
        nav[1].SetBadge(lines.Count > 0 ? lines.Count.ToString() : "");
        if (projectPick.Chosen == 0) {
            folders.EmptyTitle = "还没有项目文件夹"; folders.EmptyAbout = "把文件夹拖到这里，或点下面的“添加文件夹”。\n添加后在手机上就能看到这个项目。";
            folders.Show(lines.Select(line => {
                int at = line.IndexOf('=');
                string name = at > 0 ? line.Substring(0, at) : line, path = at > 0 ? line.Substring(at + 1) : "";
                bool there = false; try { there = Directory.Exists(path); } catch { }
                return new Row { Glyph = Theme.IconFolder, Title = name, About = path, Tag = there ? "" : "文件夹不存在", TagColour = Theme.Bad, Value = path };
            }));
        } else ShowOffered(lines);
        projectsShown();
    }
    /// The folders with conversations of Claude Code or Codex that are no projects yet: those used in a terminal,
    /// or those used by a desktop app or an editor (also while that app is closed: here they are only added).
    public static IEnumerable<Row> OfferedRows(IEnumerable<Dictionary<string, object>> offered, bool apps) {
        foreach (var one in offered) {
            var kinds = ((string[])one["kinds"]).Where(kind => (kind != Folders.Terminal) == apps).ToArray();
            if (kinds.Length == 0) continue;
            string tools = String.Join(" · ", Convert.ToString(one["tools"]).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(tool => tool == "claude" ? "Claude Code" : tool == "codex" ? "Codex" : tool));
            yield return new Row { Glyph = apps ? Theme.IconFolder : Theme.IconTerminal, Title = Convert.ToString(one["name"]), About = Convert.ToString(one["path"]), Tag = apps ? String.Join(" · ", kinds) : tools, Value = Convert.ToString(one["path"]) };
        }
    }
    void ShowOffered(List<string> lines) {
        bool apps = projectPick.Chosen == 2;
        var dirs = new Dictionary<string, string>();
        foreach (string line in lines) { int at = line.IndexOf('='); if (at > 0) dirs[line.Substring(0, at)] = line.Substring(at + 1); }
        var rows = agent == null ? new List<Row>() : OfferedRows(agent.Offer(dirs, true, 300), apps).ToList();
        folders.EmptyTitle = apps ? "没有可添加的应用项目" : "没有可添加的终端项目";
        folders.EmptyAbout = apps ? "在 Codex 或 Claude 桌面版、编辑器里用过的文件夹会列在这里。" : "在终端里用过 Claude Code 或 Codex 的文件夹会列在这里。";
        // (looked at every few seconds while it is shown: drawn anew only when it changed, so that what is chosen stays chosen)
        string mark = projectPick.Chosen + "\n" + String.Join("\n", rows.Select(row => row.Value + "\t" + row.Tag));
        if (mark == offeredMark) return;
        offeredMark = mark;
        folders.Show(rows);
    }
    void AddProject(string path) { AddProject(path, false); }
    void AddProject(string path, bool quiet) {
        var list = Dirs();
        string full; try { full = Path.GetFullPath(path).TrimEnd('\\'); } catch { return; }
        if (!Directory.Exists(full)) return;
        if (list.Any(line => String.Equals(line.Substring(line.IndexOf('=') + 1).TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))) { if (!quiet) Toast("这个文件夹已经在列表里"); return; }
        string name = new DirectoryInfo(full).Name.Replace("=", " ");
        if (name.Length == 0 || name.EndsWith(":") || name.EndsWith("\\")) name = "磁盘 " + full.Substring(0, 1);
        string wanted = name;
        for (int n = 2; list.Any(line => line.StartsWith(name + "=")); n++) name = wanted + " " + n;
        list.Add(name + "=" + full);
        Keep("RemoteDirs", list.ToArray()); offeredMark = ""; ShowProjects();
        if (projectPick.Chosen == 0) folders.SelectedIndex = folders.Items.Count - 1;
        if (!quiet) Toast("已添加 " + name);
    }
    static string Since(DateTime started) {
        TimeSpan gone = DateTime.UtcNow - started;
        return gone.TotalMinutes < 1 ? "刚刚开始" : gone.TotalHours < 1 ? (int)gone.TotalMinutes + " 分钟前开始" : gone.TotalDays < 1 ? (int)gone.TotalHours + " 小时前开始" : (int)gone.TotalDays + " 天前开始";
    }
    void RefreshActivity() {
        List<string[]> running = agent == null ? new List<string[]>() : agent.Running();
        if (running == null) return;
        string mark = String.Join("|", running.Select(r => String.Join(",", r)));
        // what is listed is open or at work: the terminals shared with the phone, and the conversations in use on the computer
        int phones = running.Count(r => r[6] == "phone"), own = running.Count - phones, working = running.Count(r => r[2] == "busy" || r[2] == "confirm");
        nav[2].SetBadge(running.Count > 0 ? running.Count.ToString() : "");
        bool allowed = Convert.ToString(config["RemoteEnabled"]) == "True";
        sideLine = trouble ? "还不能连接" : preparing ? "正在准备…" : !allowed ? "已暂停手机访问" : running.Count == 0 ? "手机可以连接" : working > 0 ? running.Count + " 个开着 · " + working + " 个在执行" : running.Count + " 个开着";
        sideColour = trouble ? Theme.Bad : preparing || !allowed ? Theme.Busy : Theme.Good;
        // the tools' settings are looked at again now and then: something else may have taken the hooks out
        if (reachRow != null && DateTime.UtcNow - toolsSeen > TimeSpan.FromSeconds(30)) ShowReach(); else ShowSide();
        if (mark == activityMark && running.Count > 0 && !pages[2].Visible) return;
        activityMark = mark;
        activity.Show(running.Select(r => {
            DateTime started; DateTime.TryParse(r[3], null, System.Globalization.DateTimeStyles.RoundtripKind, out started);
            string tool = r[0] == "claude" ? "Claude Code" : r[0] == "codex" ? "Codex" : "PowerShell";
            // how it stands first; for one that only is open, where: shared with the phone, or on the computer alone
            string tag = r[2] == "confirm" ? "等待确认" : r[2] == "busy" ? "正在执行" : r[2] == "waiting" ? "等手机的消息" : r[6] == "phone" ? "手机和电脑共用" : "电脑上开着";
            return new Row { Glyph = Theme.IconTerminal, Title = (r[4].Length > 0 ? r[4] : tool) + " · " + r[1], About = (r[5].Length > 0 ? r[5] + " · " : "") + tool + (r[6] == "phone" ? " · 双击在电脑上打开 · " + Since(started.ToUniversalTime()) : ""), Value = r,
                             Tag = tag, TagColour = r[2] == "confirm" ? Theme.Bad : r[2] == "busy" ? Theme.Busy : Theme.Good };
        }));
        activityNote.Text = running.Count == 0 ? "还没有检测到打开的终端或对话。" : phones + " 个共用终端 · " + own + " 段电脑上的对话。包含闲置和未加入项目的已打开对话。";
    }
    // One line, in the lower place; with a tool connected two, the tools under the phone's access.
    void ShowSide() {
        bool two = sideTools.Length > 0;
        sideAbove.Visible = two;
        var small = two ? sideAbove.Tag as Font : sideAbove.Font;
        if (!ReferenceEquals(sideStatus.Font, small)) sideStatus.Font = small;
        if (two) { sideAbove.Set(sideLine, sideColour); sideStatus.Set(sideTools, Theme.Good); } else sideStatus.Set(sideLine, sideColour);
    }
    // A window on this computer for the very pages the phone uses. The terminals in it are the same ones the phone
    // shows, so a terminal can be looked at and typed into from both at once, and nothing has to be handed over.
    // The window signs in with a ticket the relay gives out for one use: the password is never part of an address.
    void OpenWindow(string terminal) {
        string origin = Convert.ToString(config["Server"]).TrimEnd('/');
        if (origin.Length == 0 || password.Length == 0) { Toast("电脑后台还没有准备好，请稍后再试"); return; }
        Task.Run(() => {
            try {
                string token = Convert.ToString(Ask(origin + "/api/login", new Dictionary<string, object> { { "password", password } }, "")["token"]);
                string ticket = Convert.ToString(Ask(origin + "/api/ticket", new Dictionary<string, object>(), token)["ticket"]);
                string url = origin + "/" + (terminal != null ? "?open=" + terminal : "") + "#t=" + Uri.EscapeDataString(ticket);
                string browser = AppBrowser();
                // A browser's "app" window has no address bar and keeps its sign-in in a folder of its own.
                if (browser != null) Process.Start(new ProcessStartInfo(browser, "--app=\"" + url + "\" --user-data-dir=\"" + Path.Combine(dataDir, "window") + "\" --window-size=480,880 --no-first-run --no-default-browser-check") { UseShellExecute = false });
                else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            } catch (Exception error) {
                BeginInvoke(new Action(() => Toast("窗口没有打开：" + error.Message)));
            }
        });
    }
    Dictionary<string, object> Ask(string url, Dictionary<string, object> payload, string token) {
        var request = (HttpWebRequest)WebRequest.Create(url); request.Method = "POST"; request.ContentType = "application/json"; request.Timeout = 8000;
        if (token.Length > 0) request.Headers["Authorization"] = "Bearer " + token;
        byte[] body = Encoding.UTF8.GetBytes(json.Serialize(payload));
        using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
        using (var reply = request.GetResponse()) using (var reader = new StreamReader(reply.GetResponseStream(), Encoding.UTF8))
            return json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
    }
    static string AppBrowser() {
        foreach (string root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            foreach (string inner in new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" }) {
                if (String.IsNullOrEmpty(root)) continue;
                string file = Path.Combine(root, inner);
                if (File.Exists(file)) return file;
            }
        return null;
    }
    void OpenActivity() {
        var row = activity.Selected; var raw = row == null ? null : row.Value as string[];
        if (raw == null || raw.Length < 8) { Toast("请先在列表中选择一个终端"); return; }
        if (raw[6] != "phone") { Toast("这段对话在电脑的独立窗口中运行，请在该窗口中使用"); return; }
        OpenWindow(raw[7]);
    }
    void TakeoverActivity() {
        var row = activity.Selected; var raw = row == null ? null : row.Value as string[];
        if (raw == null || raw.Length < 8 || raw[6] != "phone") { Toast("请先在列表中选择一个手机和电脑共用的终端"); return; }
        if (agent == null) { Toast("电脑后台还没有准备好，请稍后再试"); return; }
        string tool = raw[0] == "claude" ? "Claude Code" : raw[0] == "codex" ? "Codex" : "PowerShell";
        if (raw[0] == "shell") { Toast("PowerShell 没有可恢复的会话，不能转为独立窗口；可通过“在电脑上打开”直接使用"); return; }
        if (!Confirm("转成电脑上的独立窗口？", "这个终端会先结束，然后在电脑的命令行窗口里接着同一段对话，正在执行的任务会中断，手机上不再能看到它。\n仅需在电脑上使用时，选“在电脑上打开”即可，无需转换。", "结束它并转成独立窗口", true)) return;
        activity.Enabled = false;
        Task.Run(() => {
            try {
                var takeover = agent.TakeoverForComputer(raw[7]);
                BeginInvoke(new Action(() => {
                    activity.Enabled = true;
                    try { Process.Start(TerminalAgent.ComputerProcess(takeover, Convert.ToString(config["RemoteMaxMode"]))); Toast("已转成电脑上的独立窗口：" + tool); RefreshActivity(); }
                    catch (Exception error) { Toast("电脑终端没有打开：" + error.Message); }
                }));
            } catch (Exception error) {
                BeginInvoke(new Action(() => { activity.Enabled = true; Toast(error.Message); RefreshActivity(); }));
            }
        });
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Title(Handle); }
    public App() {
        LoadSettings();
        relays = new Relays(dataDir);
        TakeOver();
        kept = Kept();
        KillLeftovers();
        Text = "Remote AI"; Font = Theme.Text();
        AutoScaleMode = AutoScaleMode.None;      // positions below are for 100%; the whole window is scaled once at the end
        ClientSize = new Size(SideWidth + PageWidth, WindowHeight); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg; ForeColor = Theme.Ink;
        Icon = Theme.MarkIcon(32);

        // ---- side bar
        var side = Place(this, new Panel { BackColor = Theme.Side }, 0, 0, SideWidth, WindowHeight);
        Place(side, new PictureBox { Image = Theme.Mark(88), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Side }, 20, 24, 40, 40);
        Note(side, "Remote AI", 70, 24, 130, 22, Theme.Ink, 11.5f, true);
        Note(side, "v" + Version, 71, 46, 130, 18, Theme.Faint, 8.25f);
        string[] names = { "连接", "项目", "活动", "设置" }, glyphs = { Theme.IconPhone, Theme.IconFolder, Theme.IconPulse, Theme.IconGear };
        for (int i = 0; i < names.Length; i++) {
            int index = i;
            var item = Place(side, new NavItem { Title = names[i], Glyph = glyphs[i] }, 12, 92 + i * 46, SideWidth - 24, 40);
            item.Click += (s, e) => Go(index);
            nav.Add(item);
        }
        Place(side, sideAbove, 18, WindowHeight - 106, SideWidth - 30, 22); sideAbove.BackColor = Theme.Side; sideAbove.Visible = false;
        sideAbove.Font = Theme.Text(); sideAbove.Tag = Theme.Text(8.25f);     // the line of the tools is the longer one, in smaller letters
        Place(side, sideStatus, 18, WindowHeight - 84, SideWidth - 30, 22); sideStatus.BackColor = Theme.Side; sideStatus.Font = sideAbove.Font; ShowSide();
        Note(side, "关闭窗口后仍在托盘运行", 20, WindowHeight - 58, SideWidth - 30, 18, Theme.Faint, 8.25f);
        Action(side, "退出", Theme.IconPower, ButtonKind.Ghost, 12, WindowHeight - 38, 76, 28, (s, e) => Quit()).BackColor = Theme.Side;

        // ---- page: connect
        var connect = Page("连接手机", "在手机 App 里点“扫码连接”，扫下面的二维码。");
        Place(connect, status, 28, 82, 652, 26); status.Font = Theme.Text(9.5f, true); status.Set("正在启动…", Theme.Busy);
        var code = Place(connect, new Card(), 28, 122, 252, 318);
        var tile = Place(code, new Card { Fill = Color.White }, 22, 22, 208, 208);
        Place(tile, qr, 10, 10, 188, 188); qr.SizeMode = PictureBoxSizeMode.Zoom; qr.BackColor = Color.White;
        Place(tile, qrHint, 10, 84, 188, 40); qrHint.BackColor = Color.White; qrHint.ForeColor = Color.FromArgb(120, 124, 140); qrHint.TextAlign = ContentAlignment.MiddleCenter; qrHint.Text = "正在准备…";
        var caption = Note(code, "用手机 App 扫码", 12, 242, 228, 22, Theme.Ink, 10f, true); caption.TextAlign = ContentAlignment.MiddleCenter;
        codeAbout = Note(code, "或在 App 里输入右边的地址和密码", 12, 266, 228, 18, Theme.Muted, 8.5f); codeAbout.TextAlign = ContentAlignment.MiddleCenter;
        var keys = Note(code, Theme.IconLock + "  请勿向他人提供地址和密码", 12, 288, 228, 18, Theme.Faint, 8.25f); keys.TextAlign = ContentAlignment.MiddleCenter; keys.Font = new Font(Theme.Icons, 8.25f);
        keys.Paint += (s, e) => { };

        var way = Place(connect, new Card(), 296, 122, 384, 318);
        Note(way, "连接方式", 20, 16, 200, 18, Theme.Muted, 8.5f, true);
        Place(way, modePick, 20, 40, 344, 36); modePick.Items = new[] { "局域网", "公网隧道", "公共中转", "我的中转" };
        Place(way, modeAbout, 20, 84, 344, 38); modeAbout.BackColor = Theme.Panel; modeAbout.ForeColor = Theme.Muted; modeAbout.Font = Theme.Text(8.5f);
        tunnelButton = Action(way, "", "", ButtonKind.Normal, 20, 126, 230, 32, DownloadTunnel);
        Place(way, relayPick, 20, 126, 250, 32); relayPick.Opening += (s, e) => ListRelays();
        relayGo = Action(way, "连接", Theme.IconRefresh, ButtonKind.Primary, 278, 126, 86, 32, (s, e) => Again());
        modePick.Changed += (s, e) => {
            relayNote = "";
            // A tab of relays is a place to choose in: nothing is stopped or started by opening it.
            if (modePick.Chosen >= 2) { Look(modePick.Chosen == 2); shown = null; ShowMode(); AskRelays(); return; }
            relayTab = false; shown = null;
            string wanted = modePick.Chosen == 0 ? "lan" : "cloud";
            if (wanted == Mode) { ShowMode(); return; }       // back from only looking at that tab: what is running stays
            if (Held()) return;
            // The password of a place at a relay for everyone is known to that relay: this computer's own relay gets another.
            if (Mode == "public") SetPassword(NewPassword());
            Keep("Mode", wanted); ShowMode(); Reconnect();
        };
        Note(way, "地址", 20, 172, 200, 18, Theme.Muted, 8.5f, true);
        Place(way, addressField, 20, 194, 256, 34); addressBox.ReadOnly = true;
        Action(way, "复制", Theme.IconCopy, ButtonKind.Normal, 284, 194, 80, 34, (s, e) => { if (Hidden) Toast("公共中转的地址和密码不显示，请用手机扫码连接"); else Copy(addressBox.Text, "地址"); });
        Note(way, "密码", 20, 238, 200, 18, Theme.Muted, 8.5f, true);
        Place(way, passwordField, 20, 260, 256, 34); passwordBox.ReadOnly = true; passwordBox.Font = new Font("Consolas", 10f);
        Action(way, "复制", Theme.IconCopy, ButtonKind.Normal, 284, 260, 80, 34, (s, e) => { if (Hidden) Toast("公共中转的地址和密码不显示，请用手机扫码连接"); else Copy(passwordBox.Text, "密码"); });
        Action(connect, "重新连接", Theme.IconRefresh, ButtonKind.Normal, 28, 456, 116, 34, (s, e) => Again());
        Action(connect, "连接诊断", Theme.IconPulse, ButtonKind.Normal, 290, 456, 124, 34, async (s, e) => {
            var button = (Control)s; button.Enabled = false;
            bool lan = Mode == "lan"; int checkedPort = Port;
            try { string result = await Task.Run(() => LanNetwork.Diagnose(lan, checkedPort, LanAddress())); MessageBox.Show(this, result, "局域网连接诊断", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch (Exception error) { Toast("诊断失败：" + error.Message); }
            finally { button.Enabled = true; }
        });
        // A relay decides its password itself: this button is for the program's own relay, on the local network and through the tunnel.
        changePassword = Action(connect, "换一个密码", Theme.IconLock, ButtonKind.Ghost, 152, 456, 124, 34, (s, e) => {
            if (Held()) return;
            if (!Confirm("换一个密码？", "换密码后，已连接的手机需要重新扫码。", "换密码", false)) return;
            SetPassword(NewPassword()); Reconnect(); Toast("密码已更新");
        });
        Note(connect, "手机只能在“项目”中添加的文件夹内启动终端；终端启动后拥有与本机操作相同的权限，可以访问整台电脑。", 28, 506, 652, 36, Theme.Faint, 8.5f);

        // ---- page: projects
        var projects = Page("项目文件夹", "手机只能在这些文件夹里新建终端和继续对话。可以把文件夹直接拖进来。");
        Place(projects, projectPick, 28, 92, 300, 34);
        var listCard = Place(projects, new Card(), 28, 136, 652, 366);
        Place(listCard, folders, 8, 8, 636, 350);
        var browse = Action(projects, "添加文件夹", Theme.IconAdd, ButtonKind.Primary, 28, 518, 128, 36, (s, e) => {
            using (var dialog = new FolderBrowserDialog { Description = "选择一个项目文件夹" }) if (dialog.ShowDialog(this) == DialogResult.OK) AddProject(dialog.SelectedPath);
        });
        // of the folders that are offered: the one chosen, or all of them
        var take = Action(projects, "添加", Theme.IconAdd, ButtonKind.Primary, 28, 518, 128, 36, (s, e) => { var row = folders.Selected; if (row != null) AddProject(Convert.ToString(row.Value)); });
        var takeAll = Action(projects, "全部添加", Theme.IconCheck, ButtonKind.Normal, 164, 518, 100, 36, (s, e) => {
            var offered = folders.Rows.Select(row => Convert.ToString(row.Value)).ToArray();
            foreach (string path in offered) AddProject(path, true);
            if (offered.Length > 0) Toast("已添加 " + offered.Length + " 个项目");
        });
        var rename = Action(projects, "重命名", Theme.IconEdit, ButtonKind.Normal, 164, 518, 100, 36, (s, e) => {
            int at = folders.SelectedIndex; if (at < 0) return;
            var list = Dirs(); string path = list[at].Substring(list[at].IndexOf('=') + 1);
            string name = Ask("项目名称", "手机上显示的名称，不改动文件夹本身。", folders.Rows[at].Title).Trim().Replace("=", " ");
            if (name.Length == 0 || name.Length > 40) return;
            if (list.Where((line, i) => i != at).Any(line => line.StartsWith(name + "="))) { Toast("已有同名项目"); return; }
            list[at] = name + "=" + path; Keep("RemoteDirs", list.ToArray()); ShowProjects();
        });
        var open = Action(projects, "打开文件夹", Theme.IconOpen, ButtonKind.Normal, 272, 518, 124, 36, (s, e) => {
            var row = folders.Selected; if (row == null) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + row.Value + "\"")); } catch (Exception) { Toast("打不开这个文件夹"); }
        });
        var remove = Action(projects, "移除", Theme.IconDelete, ButtonKind.Danger, 588, 518, 92, 36, (s, e) => {
            int at = folders.SelectedIndex; if (at < 0) return;
            if (!Confirm("移除“" + folders.Rows[at].Title + "”？", "仅取消手机对该文件夹的访问，文件夹及其中的对话不会被删除。", "移除", true)) return;
            var list = Dirs(); list.RemoveAt(at); Keep("RemoteDirs", list.ToArray()); ShowProjects();
        });
        EventHandler chosen = (s, e) => {
            bool added = projectPick.Chosen == 0, one = folders.SelectedIndex >= 0;
            browse.Visible = rename.Visible = remove.Visible = added; take.Visible = takeAll.Visible = !added;
            rename.Enabled = remove.Enabled = added && one; open.Enabled = one; take.Enabled = !added && one; takeAll.Enabled = !added && folders.Rows.Count > 0;
        };
        folders.SelectedIndexChanged += chosen; chosen(null, EventArgs.Empty);
        folders.DoubleClick += (s, e) => { if (projectPick.Chosen == 0) open.PerformClick(); else take.PerformClick(); };
        projectPick.Changed += (s, e) => { offeredMark = ""; ShowProjects(); chosen(null, EventArgs.Empty); };
        projectsShown = () => chosen(null, EventArgs.Empty);
        foreach (Control target in new Control[] { projects, listCard, folders }) {
            target.AllowDrop = true;
            target.DragEnter += (s, e) => { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None; };
            target.DragDrop += (s, e) => { var dropped = e.Data.GetData(DataFormats.FileDrop) as string[]; if (dropped != null) foreach (string path in dropped) AddProject(path); };
        }
        ShowProjects();

        // ---- page: activity
        var activityPage = Page("活动", "正在执行或正开着的终端和对话。共用终端双击可在电脑上打开，手机端仍可查看和输入，无需交接。");
        var liveCard = Place(activityPage, new Card(), 28, 92, 652, 362);
        Place(liveCard, activity, 8, 8, 636, 346);
        activity.EmptyTitle = "现在没有开着的终端或对话"; activity.EmptyAbout = "点下面的“新建或查看全部”，或在手机上选一个项目新建终端，\n它就会出现在这里，两边都能用。";
        activity.DoubleClick += (s, e) => OpenActivity();
        activity.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenActivity(); } };
        Action(activityPage, "在电脑上打开", Theme.IconTerminal, ButtonKind.Primary, 28, 468, 150, 36, (s, e) => OpenActivity());
        Action(activityPage, "新建或查看全部", Theme.IconOpen, ButtonKind.Normal, 188, 468, 164, 36, (s, e) => OpenWindow(null));
        Action(activityPage, "转成独立窗口", "", ButtonKind.Ghost, 362, 468, 130, 36, (s, e) => TakeoverActivity());
        Place(activityPage, activityNote, 28, 518, 652, 20); activityNote.BackColor = Theme.Bg; activityNote.ForeColor = Theme.Muted;

        // ---- page: settings
        var settings = Page("设置", "这些设置只影响这台电脑。");
        var rows = Place(settings, new Card(), 28, 84, 652, 11 * 41 + 12);
        autoUpdateSwitch.Changed += (s, e) => Set("update", autoUpdateSwitch.On);
        enabledSwitch.On = Convert.ToString(config["RemoteEnabled"]) == "True";
        enabledSwitch.Changed += (s, e) => { Keep("RemoteEnabled", enabledSwitch.On); RefreshActivity(); Toast(enabledSwitch.On ? "已允许手机访问" : "已暂停手机访问，立即生效"); };
        autostartSwitch.Changed += (s, e) => Set("autostart", autostartSwitch.On);
        // what cannot be done (a settings file of a tool that cannot be read) is said, and the switch shows what is
        reachSwitch.Changed += (s, e) => { try { Set("reach", reachSwitch.On); } catch (InvalidOperationException error) { ShowSettings(); MessageBox.Show(this, error.Message, "Remote AI", MessageBoxButtons.OK, MessageBoxIcon.Information); } };
        rights.Changed += (s, e) => { Set("rights", Rights[rights.Chosen]); Toast("对之后新开的终端生效"); };
        // With the tunnel in use, another protocol means another tunnel.
        protocol.Changed += (s, e) => { if (Mode == "cloud" && Held()) { ShowSettings(); return; } Set("tunnel", Protocols[protocol.Chosen]); if (Mode == "cloud") Reconnect(); };
        ShowSettings();
        var portBox = new Field(); portBox.Box.Text = Port.ToString(); portBox.Box.MaxLength = 5;
        EventHandler port = (s, e) => {
            int wanted;
            if (!Int32.TryParse(portBox.Box.Text.Trim(), out wanted) || wanted < 1024 || wanted > 65535) { portBox.Box.Text = Port.ToString(); Toast("端口要在 1024 到 65535 之间"); return; }
            if (wanted == Port) return;
            if (Held()) { portBox.Box.Text = Port.ToString(); return; }
            Keep("Port", wanted); Toast("端口已改为 " + wanted); if (!RelayMode) Reconnect();
        };
        portBox.Box.Leave += port; portBox.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; port(s, e); } };
        skinPick.Set(Theme.Titles[Array.IndexOf(Theme.Names, Theme.Default)]);
        skinPick.Opening += (s, e) => {
            var menu = Theme.Menu();
            for (int i = 0; i < Theme.Names.Length; i++) {
                string name = Theme.Names[i];
                var item = new ToolStripMenuItem(Theme.Titles[i]) { Checked = name == Theme.Skin };
                item.Click += (c, a) => { Keep("Skin", name); Wear(name); };
                menu.Items.Add(item);
            }
            menu.MinimumSize = new Size(skinPick.Width, 0);
            menu.Closed += (c, a) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(skinPick, new Point(0, skinPick.Height + 2));
        };
        waitPick.Opening += (s, e) => {
            var menu = Theme.Menu();
            for (int i = 0; i < Reach.Waits.Length; i++) {
                int seconds = Reach.Waits[i];
                var item = new ToolStripMenuItem(WaitTitles[i]) { Checked = seconds == Waiting };
                item.Click += (c, a) => Set("wait", seconds.ToString());
                menu.Items.Add(item);
            }
            menu.MinimumSize = new Size(waitPick.Width, 0);
            menu.Closed += (c, a) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(waitPick, new Point(0, waitPick.Height + 2));
        };
        var waitRow = new SettingRow("对话停下后等手机消息", "等的时候手机发的消息立即送达，工具显示钩子在运行", waitPick, 170, 32);
        waitRow.Tip(tips, WaitFor);
        askSwitch.Changed += (s, e) => Set("ask", askSwitch.On);
        var askRow = new SettingRow("Codex 的确认请求交给手机", "无人操作电脑时由手机回答，屏幕右下角有说明；关闭后只在电脑上回答", askSwitch, 46, 26);
        askRow.Tip(tips, AskFor);
        updateButton = new RoundButton { Text = "检查更新", Glyph = Theme.IconRefresh };
        updateButton.Click += (s, e) => CheckForUpdate(true);
        var all = new[] {
            new SettingRow("允许手机访问", "关闭后手机立即不能操作，正在运行的终端不受影响", enabledSwitch, 46, 26),
            new SettingRow("登录 Windows 后自动启动", "开机后在托盘里运行，不弹出窗口", autostartSwitch, 46, 26),
            reachRow = new SettingRow("手机处理电脑上的对话", "电脑上开着的 Claude Code 和 Codex 对话，可在手机上查看、确认和发消息，电脑上照常使用", reachSwitch, 46, 26),
            askRow,
            waitRow,
            new SettingRow("新终端的权限", "Claude Code 和 Codex 从手机启动时用什么权限模式", rights, 250, 32),
            new SettingRow("公网隧道的协议", "隧道经常断开或出现 1033 时改用 HTTP/2", protocol, 170, 32),
            new SettingRow("本机端口", "局域网直连和公网隧道使用，被占用时换一个", portBox, 96, 32),
            new SettingRow("外观", "和手机 App 相同的几种配色，默认与 App 一致", skinPick, 170, 32),
            new SettingRow("自动检查更新", "开启后定期检查，有新版本时提示", autoUpdateSwitch, 46, 26),
            (updateRow = new SettingRow("版本 " + Version, "从 GitHub 发布页检查并安装新版本；手机上也可以发起", updateButton, 150, 32)) };
        for (int i = 0; i < all.Length; i++) Place(rows, all[i], 2, 6 + i * 41, 648, 41);
        Action(settings, "打开数据文件夹", Theme.IconOpen, ButtonKind.Normal, 28, 552, 150, 36, (s, e) => { try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dataDir + "\"")); } catch (Exception) { } });
        Action(settings, "项目主页", Theme.IconGlobe, ButtonKind.Ghost, 186, 552, 110, 36, (s, e) => { try { Process.Start(new ProcessStartInfo("https://github.com/KangWang42/remote-ai") { UseShellExecute = true }); } catch (Exception) { } });
        Action(settings, "安装终端工具", Theme.IconTerminal, ButtonKind.Normal, 310, 552, 150, 36, (s, e) => ToolInstaller.Show(this, dataDir));
        Action(settings, "退出 Remote AI", Theme.IconPower, ButtonKind.Danger, 530, 552, 150, 36, (s, e) => Quit());

        // ---- toast, tray, timers
        Place(this, toast, 0, WindowHeight - 58, 200, 34);
        toast.Visible = false; toast.BackColor = Theme.Ink; toast.ForeColor = Theme.OnAccent; toast.TextAlign = ContentAlignment.MiddleCenter; toast.Font = Theme.Text(9f, true);
        toastTimer.Interval = 1600; toastTimer.Tick += (s, e) => { toastTimer.Stop(); toast.Visible = false; };
        activityTimer.Interval = 2000; activityTimer.Tick += (s, e) => { RefreshActivity(); if (pages.Count > 1 && pages[1].Visible && projectPick.Chosen != 0) ShowProjects(); };
        tray.Icon = Theme.MarkIcon(32); tray.Visible = true; tray.Text = "Remote AI";
        tray.DoubleClick += (s, e) => Reveal();
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("显示窗口", null, (s, e) => Reveal());
        tray.ContextMenuStrip.Items.Add("复制地址", null, (s, e) => { if (address.Length > 0 && Mode != "public") try { Clipboard.SetText(address); } catch (ExternalException) { } });
        tray.ContextMenuStrip.Items.Add("重新连接", null, (s, e) => Again());
        tray.ContextMenuStrip.Items.Add("-");
        tray.ContextMenuStrip.Items.Add("退出", null, (s, e) => Quit());
        FormClosing += (s, e) => {
            if (quitting || e.CloseReason != CloseReason.UserClosing) { Shutdown(); return; }
            e.Cancel = true; Hide();
            if (!hinted) { hinted = true; tray.ShowBalloonTip(4000, "Remote AI 仍在运行", "手机仍可连接。如需停止，请在托盘图标上点右键选“退出”。", ToolTipIcon.Info); }
        };
        ShowMode();
        Go(Dirs().Count == 0 ? 1 : 0);        // a first start begins where something has to be done
        // The window was made in the default skin, whose colours all differ: which of them each control has is noted
        // now, and the skin chosen in the settings is put on.
        parts = Theme.Parts(this);
        if (Convert.ToString(config["Skin"]) != Theme.Default) Wear(Convert.ToString(config["Skin"]));
        using (var g = CreateGraphics()) {
            float factor = g.DpiX / 96f;
            if (factor > 1.01f) { Scale(new SizeF(factor, factor)); folders.ItemHeight = (int)(54 * factor); activity.ItemHeight = (int)(54 * factor); }
        }
        Shown += (s, e) => {
            agent = new TerminalAgent(dataDir) { Version = Version };
            // The phone may ask for the update: it is looked for and installed at once, whatever the terminals are doing.
            agent.UpdateRequested = () => BeginInvoke(new Action(() => CheckForUpdate(false, true)));
            agent.Computer = Itself;
            // a project that was added in this window is removed or renamed from the phone too
            agent.Settled = (action, name, to) => {
                var list = Dirs(); int at = list.FindIndex(line => line.StartsWith(name + "=", StringComparison.Ordinal));
                if (at < 0) return false;
                if (action == "project_remove") list.RemoveAt(at); else list[at] = to + "=" + list[at].Substring(list[at].IndexOf('=') + 1);
                Keep("RemoteDirs", list.ToArray());
                Action shown = () => { offeredMark = ""; ShowProjects(); };
                if (InvokeRequired) BeginInvoke(shown); else shown();
                return true;
            };
            // The first time, the tools are given this program's hooks without being asked for: a second copy or a
            // test, which has a data folder of its own, leaves the tools' settings alone until it is told.
            if (Convert.ToString(config["Reach"]).Length == 0 && !offstage && String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REMOTECLI_DATA"))) {
                try { Reaching = true; } catch (InvalidOperationException) { } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            // a tool that tells of a conversation for the first time is in good state from then on
            agent.Reach.Changed += () => { if (IsHandleCreated && Reaching && Hooked.Any(tool => agent.Reach.Heard(tool))) try { BeginInvoke(new Action(ShowReach)); } catch (InvalidOperationException) { } };
            agent.Reach.Changed += () => { if (IsHandleCreated) try { BeginInvoke(new Action(ShowKept)); } catch (InvalidOperationException) { } };
            ShowSettings();
            // An update that could not be installed leaves the program as it was and starts it again: why it was not
            // installed is said here, once. What an installation set aside and could not remove yet is removed now.
            try {
                string left = Path.Combine(dataDir, "update-failed.txt");
                if (File.Exists(left) && !offstage) {
                    string[] told = File.ReadAllText(left, Encoding.UTF8).Split(new[] { '\n' }, 2);
                    File.Delete(left);
                    string words = "v" + told[0].Trim() + " 没有装上，仍在使用 v" + Version + (told.Length > 1 ? "：" + told[1].Trim() : "");
                    Say(words, true);
                    try { tray.ShowBalloonTip(8000, "Remote AI 更新没有完成", words, ToolTipIcon.Warning); } catch (InvalidOperationException) { }
                }
                foreach (string aside in new[] { ".previous", ".incoming" }) if (Directory.Exists(Path.Combine(appDir, aside))) Directory.Delete(Path.Combine(appDir, aside), true);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            updateTimer.Tick += (t, a) => { CheckForUpdate(false); if (DateTime.UtcNow - listRead > TimeSpan.FromHours(6)) ReadList(); };
            ReadList();
            quietTimer.Tick += async (t, a) => {
                if (waitingUpdate == null || !updateButton.Enabled) return;
                if (agent != null && !agent.Quiet(20)) return;        // a tool is working or someone is typing: later
                var found = waitingUpdate; waitingUpdate = null;
                await InstallUpdate(found);
            };
            updateTimer.Start(); quietTimer.Start();
            Task.Run(async () => { await Connect(); await agent.Run(); });
            activityTimer.Start();
            if (!offstage && !Environment.GetCommandLineArgs().Contains("--hidden") && String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REMOTECLI_DATA")) && Convert.ToString(config["ToolInstallSuggested"]).Length == 0) {
                Keep("ToolInstallSuggested", true);
                if (TerminalAgent.FindTool("claude") == null || TerminalAgent.FindTool("codex") == null)
                    BeginInvoke(new Action(() => ToolInstaller.Show(this, dataDir)));
            }
            CheckForUpdate(false);
        };
    }
    // manual: the button in this window. remote: the phone asked. Neither: the program looks by itself every ten
    // minutes, where that is switched on; switched off, it looks only when it is asked to.
    string toldUpdate = "";
    async void CheckForUpdate(bool manual, bool remote = false) {
        if (updateButton == null || !updateButton.Enabled) return;
        string step = AutoUpdater.Step(manual, remote, Convert.ToString(config["AutoUpdate"]) == "True");
        if (step == "none") return;
        if (!manual && !remote) {
            DateTime previous;
            if (config.ContainsKey("UpdateCheckUtc") && DateTime.TryParse(Convert.ToString(config["UpdateCheckUtc"]), null, System.Globalization.DateTimeStyles.RoundtripKind, out previous) && DateTime.UtcNow - previous < TimeSpan.FromMinutes(9)) return;
            Keep("UpdateCheckUtc", DateTime.UtcNow.ToString("o"));
        }
        updateButton.Enabled = false; if (manual) Toast("正在检查 GitHub 最新版本…");
        try {
            ReleaseUpdate found = await AutoUpdater.CheckAsync(Version); availableUpdate = found;
            if (agent != null) agent.Newer = found == null ? "" : found.Version;
            if (found == null) { updateButton.Text = "已是最新版"; updateButton.Glyph = Theme.IconCheck; if (manual) Toast("已是最新版"); return; }
            updateButton.Text = "更新到 v" + found.Version; updateButton.Kind = ButtonKind.Primary; updateButton.Glyph = Theme.IconDownload; nav[3].SetBadge("新");
            if (step == "install") { updateButton.Enabled = true; await InstallUpdate(found); }
            else if (step == "ask") {
                // a terminal that is at work is said: the restart cuts into what it is doing, and it goes on with its conversation afterwards
                bool working = agent != null && !agent.Quiet(20);
                if (Confirm("发现新版本 v" + found.Version, (working ? "有终端正在工作，更新会打断它手上的这一步。" : "") + "现在下载并重启更新吗？手机会断开几秒，终端随后接回原对话，公网隧道的地址不变。", "下载并更新", false)) { waitingUpdate = null; updateButton.Enabled = true; await InstallUpdate(found); }
            }
            // found by itself: said once for a version, in the window and at the tray; nothing is installed until someone asks
            else if (toldUpdate != found.Version) {
                toldUpdate = found.Version;
                Toast("发现新版本 v" + found.Version);
                if (!Visible) try { tray.ShowBalloonTip(6000, "Remote AI 有新版本 v" + found.Version, "打开窗口，在“设置”里更新。", ToolTipIcon.Info); } catch (InvalidOperationException) { }
            }
            ShowUpdate();
        } catch (Exception error) { updateButton.Text = "检查更新"; if (manual) Toast("检查更新失败：" + error.Message); }
        finally { updateButton.Enabled = true; }
    }
    // What the row of the version says: that there is a newer one, or that one waits for the phone to finish changing the line.
    void ShowUpdate() {
        if (updateRow == null) return;
        if (waitingUpdate != null) updateRow.Say("v" + waitingUpdate.Version + " 在手机切换线路完成后安装", Theme.Busy);
        else if (availableUpdate != null) updateRow.Say("有新版本 v" + availableUpdate.Version, Theme.Busy);
        else updateRow.Say("从 GitHub 发布页检查并安装；手机上也可以发起", Theme.Muted);
    }
    async Task InstallUpdate(ReleaseUpdate found) {
        if (Changing) { waitingUpdate = found; ShowUpdate(); Toast("手机正在切换连接线路，切换完成后自动更新"); return; }      // the program does not restart under a phone that is changing the line
        updateButton.Enabled = false; updateButton.Text = "下载中…"; Say("正在从 GitHub 下载 v" + found.Version + "…");
        try {
            string file = await AutoUpdater.DownloadAsync(found, Path.Combine(dataDir, "updates"));
            string folder = appDir;
            Process.Start(new ProcessStartInfo(file, "--quiet --dir \"" + folder + "\" --wait-pid " + Process.GetCurrentProcess().Id + " --launch") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = folder });
            if (agent != null) agent.KeepForRestart();
            updating = Mode == "cloud" && address.Length > 0; quitting = true; Shutdown(); Application.Exit();
        } catch (Exception error) { updateButton.Enabled = true; updateButton.Text = "更新失败"; Say("更新失败：" + error.Message, true); }
    }
    void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    void Shutdown() { generation++; tray.Visible = false; StopChildren(); Abandon(); }
    void Quit() {
        if (!Confirm("退出 Remote AI？", "退出后手机不能再连接，正在运行的终端会结束。", "退出", true)) return;
        quitting = true; Shutdown();
        Environment.Exit(0);
    }

    // A small window in the program's own look: a question with two answers, optionally with lines to fill in, each
    // under its label when it has one. Returns what was filled in, or null when the question was not agreed to.
    string[] Dialog(string title, string text, string yes, bool danger, string[] labels, string[] values) {
        using (var dialog = new Form { Text = "Remote AI", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                                       MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Theme.Text(), BackColor = Theme.Bg, ForeColor = Theme.Ink, AutoScaleMode = AutoScaleMode.None, Icon = Icon }) {
            dialog.HandleCreated += (s, e) => Theme.Title(dialog.Handle);
            Place(dialog, Theme.Label(title, Theme.Ink, Theme.Bg, 11.5f, true), 24, 22, 372, 26);
            Place(dialog, Theme.Label(text, Theme.Muted, Theme.Bg), 24, 52, 372, 40);
            var boxes = new List<Field>();
            int y = 96;
            for (int i = 0; labels != null && i < labels.Length; i++) {
                if (labels[i].Length > 0) { Place(dialog, Theme.Label(labels[i], Theme.Muted, Theme.Bg, 8.5f), 24, y, 372, 18); y += 22; }
                var box = new Field(); box.BackColor = Theme.Bg; box.Box.Text = values != null && i < values.Length ? values[i] ?? "" : "";
                Place(dialog, box, 24, y, 372, 34); boxes.Add(box);
                y += 44;
            }
            y = boxes.Count == 0 ? 108 : y + 6;
            dialog.ClientSize = new Size(420, y + 50);
            var ok = new RoundButton { Text = yes, Kind = danger ? ButtonKind.Danger : ButtonKind.Primary, DialogResult = DialogResult.OK };
            var cancel = new RoundButton { Text = "取消", DialogResult = DialogResult.Cancel };
            int wide = yes.Length > 4 ? 128 : 96;
            Place(dialog, cancel, 396 - wide - 8 - 96, y, 96, 34); Place(dialog, ok, 396 - wide, y, wide, 34);
            dialog.AcceptButton = ok; dialog.CancelButton = cancel;
            using (var g = dialog.CreateGraphics()) { float factor = g.DpiX / 96f; if (factor > 1.01f) dialog.Scale(new SizeF(factor, factor)); }
            if (boxes.Count > 0) dialog.Shown += (s, e) => { boxes[0].Box.Focus(); boxes[0].Box.SelectAll(); };
            return dialog.ShowDialog(Visible ? this : null) == DialogResult.OK ? boxes.Select(b => b.Box.Text).ToArray() : null;
        }
    }
    bool Confirm(string title, string text, string yes, bool danger) { return Dialog(title, text, yes, danger, null, null) != null; }
    string Ask(string title, string text, string value) { var filled = Dialog(title, text, "确定", false, new[] { "" }, new[] { value }); return filled == null ? "" : filled[0]; }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [STAThread] public static int Main(string[] args) {
        // --qr <text> <file.png>: writes the picture the window would show; used by the tests.
        if (args.Length == 3 && args[0] == "--qr") { using (var picture = Picture(args[1], 8)) { if (picture == null) return 2; picture.Save(args[2], ImageFormat.Png); } return 0; }
        bool created;
        // One copy per data folder: a copy pointed at another folder with REMOTECLI_DATA (a test, a second setup) may run beside it.
        string elsewhere = Environment.GetEnvironmentVariable("REMOTECLI_DATA");
        using (var single = new Mutex(true, "Local\\RemoteCliApp" + (String.IsNullOrWhiteSpace(elsewhere) ? "" : "-" + Math.Abs(elsewhere.ToLowerInvariant().GetHashCode())), out created)) {
            if (!created) { MessageBox.Show("Remote AI 已经在运行，请看屏幕右下角的托盘图标。", "Remote AI"); return 0; }
            ServicePointManager.DefaultConnectionLimit = 8;      // the held request and the reports run side by side
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length >= 2 && args[0] == "--screenshot") ComputerName = "my-computer";       // a published picture must not carry this computer's name
            var window = new App();
            // --screenshot <file.png>: draws the window into a picture after a moment and exits; used for the documentation.
            if (args.Length >= 2 && args[0] == "--screenshot") {      // an optional third argument chooses the page (0 to 3)
                // Such a window is never one to use: it stays off the screen, also when only a picture is asked for.
                offstage = true; window.StartPosition = FormStartPosition.Manual; window.Location = new Point(-30000, -30000); window.ShowInTaskbar = false;
                window.Show();
                if (args.Length >= 3) { int page; if (Int32.TryParse(args[2], out page)) window.Go(page); }
                Action<double> wait = seconds => { var end = DateTime.UtcNow.AddSeconds(seconds); while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(30); } };
                // "own" after the page opens the tab of one's own relays as a click would; an address and a password
                // after it are added as the window's own form adds them, "- - again" presses "重新连接" instead.
                // "everyone" opens the tab of the relays for everyone, and "public" also takes the first of them.
                // What came of it is written beside the picture, for the tests.
                if (args.Length >= 4 && (args[3] == "own" || args[3] == "public" || args[3] == "everyone")) {
                    wait(5);
                    window.modePick.Choose(args[3] == "own" ? 3 : 2);
                    if (args[3] == "public") { Relay first = window.Known().FirstOrDefault(r => r.Public); if (first != null) window.Use(first); }
                    else if (args.Length >= 7 && args[6] == "again") window.Again();
                    else if (args.Length >= 6) window.AddRelay(args[4], args[5], "");
                    wait(1);
                    for (int n = 0; n < 60 && window.relayBusy; n++) wait(0.5);
                    wait(6);
                    Relay showing = window.modePick.Chosen >= 2 ? window.Showing() : null;
                    File.WriteAllText(args[1] + ".txt", String.Join("\n", "mode=" + window.Mode, "tab=" + window.modePick.Chosen, "status=" + window.said, "about=" + window.modeAbout.Text,
                        "own_relay=" + (window.relay != null && !window.relay.HasExited ? "running" : "stopped"), "field=" + window.relayPick.Title, "showing=" + (showing == null ? "" : showing.Id),
                        "address=" + window.addressBox.Text, "password_shown=" + (window.passwordBox.Text == window.password), "code=" + (window.qr.Image != null),
                        "server=" + Convert.ToString(window.config["Server"]), "saved=" + window.relays.Own().Count, "published=" + window.relays.Published().Count,
                        "new_password_button=" + window.changePassword.Visible, "skin=" + Theme.Skin, "side=" + window.sideLine, "tools=" + window.sideTools,
                        "side_lines=" + (window.sideAbove.Visible ? window.sideAbove.Line + "|" : "") + window.sideStatus.Line), new UTF8Encoding(false));
                }
                // "terminal" or "apps" after the page of projects opens that list of folders a project can be made of, once
                // the conversations have been looked at; "all" after it adds them all. What is listed is written beside the picture.
                if (args.Length >= 4 && (args[3] == "terminal" || args[3] == "apps")) {
                    wait(9);
                    window.projectPick.Choose(args[3] == "apps" ? 2 : 1);
                    wait(1);
                    var listed = window.folders.Rows.Select(row => row.Title + "\t" + row.Tag + "\t" + row.About).ToArray();
                    if (args.Length >= 5 && args[4] == "all") { foreach (string path in window.folders.Rows.Select(row => Convert.ToString(row.Value)).ToArray()) window.AddProject(path, true); wait(1); }
                    File.WriteAllText(args[1] + ".txt", String.Join("\n", listed) + "\n--\n" + String.Join("\n", window.Dirs()) + "\n--\n" + String.Join("\n", window.folders.Rows.Select(row => row.Title)), new UTF8Encoding(false));
                }
                var until = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(30); }
                using (var picture = new Bitmap(window.ClientSize.Width, window.ClientSize.Height)) {
                    using (var g = Graphics.FromImage(picture)) g.Clear(Theme.Bg);
                    Rectangle client = window.RectangleToScreen(window.ClientRectangle);
                    using (var whole = new Bitmap(window.Width, window.Height)) {
                        window.DrawToBitmap(whole, new Rectangle(0, 0, window.Width, window.Height));
                        using (var g = Graphics.FromImage(picture)) g.DrawImage(whole, -(client.X - window.Left), -(client.Y - window.Top));
                    }
                    picture.Save(args[1], ImageFormat.Png);
                }
                window.quitting = true; window.Shutdown();
                Environment.Exit(0);
            }
            // --offstage: the program runs as usual where nobody sees it or can press it, for a test that plays the phone.
            if (args.Contains("--offstage")) { offstage = true; window.StartPosition = FormStartPosition.Manual; window.Location = new Point(-30000, -30000); window.ShowInTaskbar = false; window.tray.Visible = false; }
            if (args.Contains("--hidden")) { window.WindowState = FormWindowState.Minimized; window.ShowInTaskbar = false; window.Shown += (s, e) => { window.Hide(); window.ShowInTaskbar = true; }; }
            Application.Run(window);
        }
        return 0;
    }
}
}
