using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RemoteCli {
/// A change of line the phone asked for, from the asking to its end. The new line is made ready beside the one in
/// use. The phone is then handed its address and a sign-in that works once, and the agent turns to it. When the phone
/// has come through the new line, that line is kept; when it has not within the time allowed, the program goes back
/// to the line it had, which was left as it was until then. So a phone far from the computer is not shut out by a
/// line it cannot reach.
sealed class LineChange {
    public string To = "", State = "preparing", Note = "", Key = "";
    public string Asked = "", Taken = "", Kept = "";    // the operations that asked for it, took the new line, and kept it
    public DateTime Until = DateTime.MaxValue;          // when a line that is ready and not taken, or taken and not kept, is given up
    // the new line: what is kept in the settings, where the agent reports, and what the phone is handed
    public string Mode = "", RelayId = "", OwnServer = "", Name = "", Server = "", Password = "", Address = "", Ticket = "";
    public Process Relay, Tunnel;       // what was started for it beside the line in use
    public bool Widened;                // the program's relay was started again to listen on the local network too
    public bool Moved;                  // the agent reports to the new line
    // the line that was in use, to go back to
    public string WasMode = "", WasServer = "", WasPassword = "", WasSaid = "";
    public DateTime Ended = DateTime.MaxValue;          // when it came to nothing; why is told for a while after
    public bool WasTrouble;
    public bool Open { get { return State == "preparing" || State == "ready" || State == "moving" || State == "trial"; } }
}

/// What the phone may ask of the program itself, beside its terminals: to be told a few settings and the lines there
/// are, to change one of those settings, and to turn to another line (docs/PROTOCOL.md, "The program itself").
/// Relays are added, and passwords and the port changed, at the computer only: none of that is typed into a phone
/// whose words travel through the line in use.
public sealed partial class App {
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly string[] Rights = { "full", "edit", "read" }, Protocols = { "auto", "http2" };
    const int TakeSeconds = 45;         // how long a line that is ready waits for the phone to take it
    // How long the phone has to come through the new line. REMOTECLI_TRIAL names another time, for a test.
    static readonly int TrialSeconds = Seconds("REMOTECLI_TRIAL", 120);
    static int Seconds(string variable, int otherwise) { int given; return Int32.TryParse(Environment.GetEnvironmentVariable(variable), out given) && given >= 3 && given <= 600 ? given : otherwise; }
    readonly object changing = new object();
    LineChange change;                  // the change in progress, or the last one and what came of it
    bool Changing { get { lock (changing) return change != null && change.Open; } }

    // ---------- settings: changed here, from the window and from the phone alike
    bool Autostart {
        get { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue("RemoteCli") != null; }
        set { using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) { if (value) key.SetValue("RemoteCli", "\"" + Application.ExecutablePath + "\" --hidden"); else key.DeleteValue("RemoteCli", false); } }
    }
    // Whether Claude Code and Codex run this program's hooks (Reach.cs): the phone then sees the conversations they
    // have open on this computer, wherever they run, and can answer what they ask and tell them what to do next.
    // The hooks go into the tools' own settings, for each tool that has been used on this computer.
    // The first time the program runs it switches this on by itself; after that the switch says what its owner wants,
    // and while it is on the hooks are kept in place: put back when something took them out, or the program moved.
    static readonly string[] Hooked = { "claude", "codex" };
    static readonly Dictionary<string, string> ToolNames = new Dictionary<string, string> { { "claude", "Claude Code" }, { "codex", "Codex" } };
    static bool Used(string tool) { try { return Directory.Exists(Path.GetDirectoryName(ReachHooks.FileOf(tool))); } catch (ArgumentException) { return false; } }
    static string HookProgram { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RemoteCliAgent.exe"); } }
    bool Reaching {
        get { return Convert.ToString(config["Reach"]) == "True"; }
        set {
            if (value && !File.Exists(HookProgram)) throw new InvalidOperationException("没有找到 RemoteCliAgent.exe，请重新安装电脑端");
            foreach (string tool in Hooked.Where(tool => Used(tool) || !value)) ReachHooks.Set(tool, value, HookProgram, agent.Reach.Pipe);
            Keep("Reach", value);
        }
    }
    // How long a tool that stops while nobody is at the computer waits for the phone's message (Reach.Waits): not at
    // all, unless its owner chooses a time. The agent reads the setting where a tool stops.
    static readonly string[] WaitTitles = { "不等", "1 分钟", "5 分钟", "15 分钟", "30 分钟" };
    const string WaitFor = "不等：对话做完就停下，电脑上看不到任何变化。手机发的消息由电脑端送进这段对话，Codex 约一两秒，Claude Code 十秒左右。\n"
        + "选了时间：电脑无人操作满 1 分钟后，对话做完先等这么久，这期间手机发的消息立即送达；\n"
        + "等的时候工具显示钩子正在运行（Claude Code 里写作“" + Reach.Waiting + "”），碰一下键盘或鼠标就结束。";
    int Waiting {
        get { return Reach.Wait(config["ReachWait"]); }
        set { Keep("ReachWait", (object)value); }       // as a setting: with an int, Keep(url, port) of the tunnel would be meant
    }
    // How each tool that is used on this computer stands, while the switch is on: "good" (its hooks are this program's
    // and it has told of a conversation through them), "ready" (they are, and it has yet to), "failed" (they are not,
    // and could not be written). A tool that has told once is known to: Codex runs new hooks only once its user has
    // said in Codex that they are trusted.
    List<Dictionary<string, object>> ReachStates() {
        var states = new List<Dictionary<string, object>>();
        if (agent == null || !Reaching) return states;
        var told = (config["ReachHeard"] as System.Collections.IEnumerable ?? new object[0]).Cast<object>().Select(Convert.ToString).ToList();
        foreach (string tool in Hooked.Where(Used)) {
            bool there = ReachHooks.Installed(tool, HookProgram, agent.Reach.Pipe);
            if (!there) { try { ReachHooks.Set(tool, true, HookProgram, agent.Reach.Pipe); there = true; } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (InvalidOperationException) { } }
            if (there && agent.Reach.Heard(tool) && !told.Contains(tool)) { told.Add(tool); Keep("ReachHeard", told.Cast<object>().ToArray()); }
            states.Add(new Dictionary<string, object> { { "tool", tool }, { "name", ToolNames[tool] }, { "state", !there ? "failed" : told.Contains(tool) ? "good" : "ready" } });
        }
        return states;
    }
    // The same in a line for the window: every tool in good state is said in the colour of what is well. At the side
    // of the window, which is seen on every page, stand the tools that are connected, and only those.
    void ShowReach() {
        if (reachRow == null) return;
        var states = ReachStates();
        toolsSeen = DateTime.UtcNow;
        var good = states.Where(s => Convert.ToString(s["state"]) == "good").Select(s => Convert.ToString(s["name"])).ToArray();
        sideTools = good.Length == 0 ? "" : String.Join(" · ", good) + " 已连接";
        ShowSide();
        string line = !Reaching ? "电脑上开着的 Claude Code 和 Codex 对话，可在手机上查看、确认和发消息，电脑上照常使用"
            : states.Count == 0 ? "这台电脑上还没有用过 Claude Code 或 Codex"
            : String.Join(" · ", states.Select(s => s["name"] + (Convert.ToString(s["state"]) == "good" ? " 状态良好" : Convert.ToString(s["state"]) == "failed" ? " 的配置写不进去" : Convert.ToString(s["tool"]) == "codex" ? " 已就绪，首次需在 Codex 里确认信任" : " 已就绪")));
        bool well = states.Count > 0 && states.All(s => Convert.ToString(s["state"]) == "good"), bad = states.Any(s => Convert.ToString(s["state"]) == "failed");
        reachRow.Say(line, well ? Theme.Good : bad ? Theme.Bad : Theme.Muted);
    }
    void Set(string name, object value) {
        string word = value as string;
        if (name == "rights" && Array.IndexOf(Rights, word) >= 0) Keep("RemoteMaxMode", word);
        // Another protocol is used by the next tunnel; the one in use is left to whoever changes it at the computer.
        else if (name == "tunnel" && Array.IndexOf(Protocols, word) >= 0) Keep("TunnelProtocol", word);
        else if (name == "autostart" && value is bool) Autostart = (bool)value;
        else if (name == "update" && value is bool) Keep("AutoUpdate", (bool)value);
        else if (name == "wait" && word != null && Array.IndexOf(Reach.Waits.Select(seconds => seconds.ToString()).ToArray(), word) >= 0) Waiting = Int32.Parse(word);
        else if (name == "reach" && value is bool) {
            try { Reaching = (bool)value; }
            catch (IOException) { throw new InvalidOperationException("写不了 Claude Code 或 Codex 的配置文件，没有改动"); }
            catch (UnauthorizedAccessException) { throw new InvalidOperationException("写不了 Claude Code 或 Codex 的配置文件，没有改动"); }
        }
        else throw new ArgumentException("没有这项设置，或它的值无效");
        if (InvokeRequired) BeginInvoke(new Action(() => Applied(name))); else Applied(name);
    }
    void Applied(string name) { ShowSettings(); if (name == "update" && autoUpdateSwitch.On) CheckForUpdate(false); }
    void ShowSettings() {
        rights.Chosen = Math.Max(0, Array.IndexOf(Rights, Convert.ToString(config["RemoteMaxMode"])));
        protocol.Chosen = Math.Max(0, Array.IndexOf(Protocols, Convert.ToString(config["TunnelProtocol"])));
        autostartSwitch.On = Autostart;
        autoUpdateSwitch.On = Convert.ToString(config["AutoUpdate"]) == "True";
        reachSwitch.On = Reaching;
        waitPick.Set(WaitTitles[Array.IndexOf(Reach.Waits, Waiting)]);
        ShowReach();
    }

    // ---------- what the phone is told
    // A line is named to the phone by a word that says nothing of where it leads: a relay for everyone keeps its address to itself.
    static string LineId(Relay relay) {
        using (var hash = SHA256.Create()) return "r" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(relay.Id)), 0, 6).Replace("-", "").ToLowerInvariant();
    }
    Dictionary<string, object> About() {
        string mode, inUse;
        lock (saving) { mode = Mode; inUse = Convert.ToString(config["Relay"]); }
        var lines = new List<object> {
            new Dictionary<string, object> { { "id", "lan" }, { "kind", "lan" }, { "current", mode == "lan" }, { "ready", true } },
            // without the tunnel program the tunnel can still be chosen: the program is fetched first
            new Dictionary<string, object> { { "id", "cloud" }, { "kind", "cloud" }, { "current", mode == "cloud" }, { "ready", File.Exists(TunnelFile) } } };
        foreach (Relay relay in Known())
            lines.Add(new Dictionary<string, object> { { "id", LineId(relay) }, { "kind", relay.Public ? "public" : "own" }, { "name", relay.Name }, { "ms", relay.Ms },
                { "current", (mode == "own" || mode == "public") && relay.Id == inUse }, { "ready", true } });
        if (DateTime.UtcNow - asked > TimeSpan.FromSeconds(60)) BeginInvoke(new Action(AskRelays));
        var now = new Dictionary<string, object> { { "state", "" }, { "to", "" }, { "note", "" }, { "left", 0 } };
        lock (changing) if (change != null && DateTime.UtcNow - change.Ended < TimeSpan.FromMinutes(5)) {
            now["state"] = change.State; now["to"] = change.To; now["note"] = change.Note;
            now["left"] = change.Open && change.Until != DateTime.MaxValue ? (int)Math.Max(0, (change.Until - DateTime.UtcNow).TotalSeconds) : 0;
        }
        return new Dictionary<string, object> { { "name", ComputerName }, { "version", Version }, { "lines", lines }, { "change", now },
            { "settings", new Dictionary<string, object> { { "rights", Convert.ToString(config["RemoteMaxMode"]) }, { "tunnel", Convert.ToString(config["TunnelProtocol"]) },
                { "autostart", Autostart }, { "update", Convert.ToString(config["AutoUpdate"]) == "True" }, { "reach", Reaching }, { "wait", Waiting.ToString() } } }, { "reach", ReachStates() } };
    }
    static string Word(Dictionary<string, object> op, string key) { object value; return op.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : ""; }
    // Called by the agent, on its thread, for each question; what takes time is done elsewhere.
    Dictionary<string, object> Itself(Dictionary<string, object> op) {
        string action = Word(op, "action");
        if (action == "settings_change") { object value; op.TryGetValue("value", out value); Set(Word(op, "name"), value); }
        else if (action == "line_switch") Turn(Word(op, "to"), Word(op, "id"));
        else if (action == "line_take") return HandOver(Word(op, "id"));
        else if (action == "line_keep") KeepLine(Word(op, "key"), Word(op, "id"));
        return About();
    }

    // ---------- another line
    // While the phone changes the line, it is not changed at the computer as well: the two would undo each other.
    bool Held() {
        if (!Changing) return false;
        Toast("手机正在切换连接线路，请稍候再操作");
        ShowMode();
        return true;
    }
    // Leaving the program ends what a change of line started beside the line in use.
    void Abandon() { var c = change; if (c != null) { Kill(c.Tunnel); Kill(c.Relay); } }
    void Turn(string to, string asked) {
        LineChange made;
        Relay relay = to == "lan" || to == "cloud" ? null : Known().FirstOrDefault(r => LineId(r) == to);
        if (relay == null && to != "lan" && to != "cloud") throw new ArgumentException("电脑上没有这条线路，请刷新后再选");
        lock (changing) {
            if (change != null && change.Open) throw new InvalidOperationException("正在切换线路，请稍候");
            string mode, inUse;
            lock (saving) { mode = Mode; inUse = Convert.ToString(config["Relay"]); }
            if (relay == null ? mode == to : (mode == "own" || mode == "public") && relay.Id == inUse) throw new InvalidOperationException("已经在使用这条线路");
            var key = new byte[16];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(key);
            change = made = new LineChange { To = to, Asked = asked, Note = "正在准备…", Key = BitConverter.ToString(key).Replace("-", "").ToLowerInvariant(),
                WasMode = mode, WasServer = Convert.ToString(config["Server"]), WasPassword = password, WasSaid = said, WasTrouble = trouble };
        }
        Task.Run(() => Prepare(made, relay));
    }
    void Note(LineChange c, string text) { lock (changing) if (c.State == "preparing") c.Note = text; }
    // The answer to an operation has reached the relay, which hands it to the phone at once.
    async Task<bool> Handed(string id) {
        for (int n = 0; n < 100; n++) { if (agent != null && agent.Reported(id)) return true; await Task.Delay(100); }
        return false;
    }
    // The program's relay is started again, for this computer alone or for the local network as well. Who is signed in stays so.
    async Task<bool> Listen(bool network) {
        Kill(relay);
        relay = StartRelay(network, Port, password);
        Remember();
        return await Up(relay, Port);
    }
    // A request to a relay that may refuse: the status it answered with, 0 when it did not answer.
    static int Status(string url, string token) {
        try {
            var request = (HttpWebRequest)WebRequest.Create(url); request.Method = "POST"; request.ContentType = "application/json"; request.Timeout = 8000;
            request.Headers["Authorization"] = "Bearer " + token;
            using (var stream = request.GetRequestStream()) stream.Write(new byte[] { (byte)'{', (byte)'}' }, 0, 2);
            using (var reply = (HttpWebResponse)request.GetResponse()) return (int)reply.StatusCode;
        } catch (WebException refused) { var reply = refused.Response as HttpWebResponse; return reply == null ? 0 : (int)reply.StatusCode; }
    }
    async Task Prepare(LineChange c, Relay relay) {
        string failed = null;       // (this compiler does not wait inside a catch)
        try {
            await Handed(c.Asked);          // the phone has its answer before anything is disturbed
            bool local = c.WasMode == "lan" || c.WasMode == "cloud";
            if (relay != null) {
                Note(c, "正在检查“" + relay.Name + "”…");
                string server, secret, wrong;
                if (relay.Public) wrong = relays.Space(relay, out server, out secret);
                else { server = relay.Url; secret = relay.Password; wrong = TerminalAgent.CheckRelay(server, secret); }
                if (wrong.Length > 0) { await Undo(c, wrong); return; }
                c.Mode = relay.Public ? "public" : "own"; c.RelayId = relay.Id; c.OwnServer = relay.Public ? "" : relay.Url; c.Name = relay.Name;
                c.Server = c.Address = server; c.Password = secret;
            } else {
                int port = Port;
                c.Mode = c.To; c.Name = c.To == "lan" ? "局域网" : "公网隧道"; c.Server = "http://127.0.0.1:" + port;
                // The password of a place at a relay for everyone is known to that relay: the program's own relay gets another.
                c.Password = c.WasMode == "public" ? NewPassword() : c.WasPassword;
                if (c.To == "cloud" && !File.Exists(TunnelFile)) {
                    Note(c, "正在下载隧道程序…");
                    await FetchTunnel(percent => Note(c, "正在下载隧道程序 " + percent + "%"));
                }
                if (!local) {
                    if (!File.Exists(Python)) { await Undo(c, "电脑上的安装不完整，本机服务无法启动"); return; }
                    if (Listening(port)) { await Undo(c, "电脑上的端口 " + port + " 已被别的程序占用，需要在电脑上换一个端口"); return; }
                    if (c.Password != c.WasPassword) try { File.Delete(Path.Combine(dataDir, "relay", "sessions.json")); } catch { }
                    c.Relay = StartRelay(c.To == "lan", port, c.Password);
                    Remember();
                    if (!await Up(c.Relay, port)) { await Undo(c, "电脑上的本机服务没有启动成功"); return; }
                } else if (c.To == "lan") {
                    // The program's relay listened for this computer alone. The tunnel stays, and the phone with it.
                    c.Widened = true;
                    if (!await Listen(true)) { await Undo(c, "电脑上的本机服务没有重新启动成功"); return; }
                }
                if (c.To == "lan") {
                    string ip = LanAddress();
                    if (ip.Length == 0) { await Undo(c, "电脑没有局域网地址，请先让电脑连上 Wi-Fi 或网线"); return; }
                    c.Address = "http://" + ip + ":" + port;
                } else {
                    Note(c, "正在建立公网隧道…");
                    Task<string> given;
                    c.Tunnel = StartTunnel(port, out given);
                    Remember();
                    c.Address = await given;
                    if (c.Address.Length == 0) { await Undo(c, "公网隧道没有建立成功，请稍后再试"); return; }
                }
            }
            // The phone is handed a sign-in that works once: the password of the new line does not travel through the old one.
            string token = Convert.ToString(Ask(c.Server + "/api/login", new Dictionary<string, object> { { "password", c.Password } }, "")["token"]);
            // The phone says through the new line that it has arrived: a relay of an earlier version cannot pass that on.
            if (relay != null && Status(c.Server + "/api/computer", token) != 400) {
                await Undo(c, "“" + c.Name + "”的版本较早，不能从手机切换过去。请先更新这个中转（在服务器上运行 sudo bash install.sh update），或在电脑上切换");
                return;
            }
            c.Ticket = Convert.ToString(Ask(c.Server + "/api/ticket", new Dictionary<string, object>(), token)["ticket"]);
            lock (changing) { if (c.State != "preparing") return; c.State = "ready"; c.Note = ""; c.Until = DateTime.UtcNow.AddSeconds(TakeSeconds); }
            while (DateTime.UtcNow < c.Until) { await Task.Delay(500); lock (changing) if (c.State != "ready") return; }
            await Undo(c, "手机没有接手新线路，仍在使用原来的线路", "ready");
        } catch (Exception error) { failed = "新线路没有准备好：" + error.Message; }
        if (failed != null) await Undo(c, failed);
    }
    // The phone takes the line that is ready: it is told where, and how to sign in there once.
    Dictionary<string, object> HandOver(string id) {
        LineChange c;
        lock (changing) {
            c = change;
            if (c == null || c.State != "ready") throw new InvalidOperationException(c != null && c.State == "failed" ? c.Note : "新线路还没有准备好");
            c.State = "moving"; c.Taken = id; c.Until = DateTime.UtcNow.AddSeconds(TrialSeconds);
        }
        Task.Run(() => Move(c));
        return new Dictionary<string, object> { { "address", c.Address }, { "ticket", c.Ticket }, { "key", c.Key }, { "seconds", TrialSeconds } };
    }
    async Task Move(LineChange c) {
        string failed = null;
        try {
            if (!await Handed(c.Taken)) { await Undo(c, "新线路没能告知手机，仍在使用原来的线路"); return; }
            await Task.Delay(300);      // the answer is on its way to the phone
            lock (changing) { if (c.State != "moving") return; }
            if (c.Server != c.WasServer) {
                // the agent signs in where it reports with the password of that place
                WritePassword(c.Password); Keep("Server", c.Server);
                if (agent != null) agent.Reset();
                c.Moved = true;
            }
            lock (changing) { if (c.State != "moving") return; c.State = "trial"; }
            Say("正在按手机的要求切换到“" + c.Name + "”，等待手机在新线路上出现…");
            while (DateTime.UtcNow < c.Until) { await Task.Delay(500); lock (changing) if (c.State != "trial") return; }
            await Undo(c, "手机没有在新线路上出现，已回到原来的线路", "trial");
        } catch (Exception error) { failed = "切换没有完成，已回到原来的线路：" + error.Message; }
        if (failed != null) await Undo(c, failed);
    }
    // The phone has come through the new line and says so, with the word it was handed: the line is kept.
    void KeepLine(string key, string id) {
        LineChange c;
        lock (changing) {
            c = change;
            if (c == null || (c.State != "trial" && c.State != "moving") || c.Key != key) throw new InvalidOperationException("没有等待确认的线路切换");
            c.State = "kept"; c.Kept = id; c.Note = "";
        }
        // From here on this is the line the program starts with.
        lock (saving) { if (c.Mode == "own") config["OwnServer"] = c.OwnServer; if (c.RelayId.Length > 0) config["Relay"] = c.RelayId; config["Mode"] = c.Mode; Save(); }
        Task.Run(() => Settle(c));
    }
    // What the line before needed is ended, and what was started for the new one becomes the program's own.
    async Task Settle(LineChange c) {
        try {
            await Handed(c.Kept);       // before the program's relay may be started again
            generation++;               // whatever the line before was still doing is over
            bool local = c.Mode == "lan" || c.Mode == "cloud", wasLocal = c.WasMode == "lan" || c.WasMode == "cloud";
            if (!local) {
                Kill(tunnel); tunnel = null; Kill(relay); relay = null;
                // as when a relay is chosen at the computer: who signed in at the program's own relay with another password signs in again there
                if (wasLocal && c.Password != c.WasPassword) try { File.Delete(Path.Combine(dataDir, "relay", "sessions.json")); } catch (IOException) { }
            }
            else if (!wasLocal) { relay = c.Relay; tunnel = c.Tunnel; }
            else if (c.Mode == "cloud") { tunnel = c.Tunnel; await Listen(false); }     // behind the tunnel, for this computer alone again
            else { Kill(tunnel); tunnel = null; }
            c.Relay = c.Tunnel = null;
            if (tunnel != null) { tunnel.Exited += Lost(generation); Keep(c.Address, Port); }
            Remember();
            BeginInvoke(new Action(() => { relayTab = false; relayNote = ""; shown = null; }));
            ShowAddress(c.Address);
            Say("已按手机的要求切换到“" + c.Name + "”" + (c.Mode == "lan" ? "。手机需与电脑在同一个 Wi-Fi 下" : c.Mode == "cloud" ? "。更新程序后地址不变" : "，地址固定不变"));
            lock (changing) if (change == c) change = null;
        } catch (Exception error) { Say("线路已切换，收尾时出错：" + error.Message + "。点“重新连接”可恢复", true); }
    }
    // The change comes to nothing: what was started for it is ended, and the agent reports where it did before.
    // `from` names the state it must still be in, for a wait that ran out while something else went on.
    async Task Undo(LineChange c, string why, string from = null) {
        lock (changing) { if (!c.Open || (from != null && c.State != from)) return; c.State = "failed"; c.Note = why; c.Ended = DateTime.UtcNow; }
        try {
            if (c.Moved) { WritePassword(c.WasPassword); Keep("Server", c.WasServer); if (agent != null) agent.Reset(); }
            Kill(c.Tunnel); Kill(c.Relay); c.Tunnel = c.Relay = null;
            if (c.Widened) await Listen(false);
            Remember();
        } catch (Exception) { }
        if (c.Moved) Say(c.WasSaid, c.WasTrouble);
    }
}
}
