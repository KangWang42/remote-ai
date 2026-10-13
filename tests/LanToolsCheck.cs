using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;
using RemoteCli;

class LanToolsCheck {
    static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    [STAThread] static void Main(string[] args) {
        Require(LanNetwork.Usable(IPAddress.Parse("172.30.136.72"), "Wi-Fi", "Intel AX210", NetworkInterfaceType.Wireless80211), "campus IP accepted");
        foreach (string ip in new[] { "198.18.0.1", "198.19.2.3", "127.0.0.1", "169.254.1.1", "0.0.0.0", "::1" })
            Require(!LanNetwork.Usable(IPAddress.Parse(ip), "network", "adapter", NetworkInterfaceType.Ethernet), "virtual/reserved excluded: " + ip);
        Require(!LanNetwork.Usable(IPAddress.Parse("10.0.0.1"), "FlClash", "Meta Tunnel", NetworkInterfaceType.Ethernet), "named proxy excluded even at private address");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Require(LanNetwork.Diagnose(true, port, "127.0.0.1").Contains("本机端口：可以连接"), "live listener found");
        listener.Stop();
        Require(LanNetwork.Diagnose(false, port, "").Contains("当前没有选择局域网直连"), "mode error explained");
        Require(LanNetwork.Diagnose(true, port, "").Contains("本机端口：没有响应"), "closed port explained");
        Require(ToolInstaller.Source("claude") == "https://claude.ai/install.ps1" && ToolInstaller.Source("codex") == "https://chatgpt.com/codex/install.ps1", "official sources");
        bool refused = false; try { ToolInstaller.Source("shell & bad"); } catch (ArgumentException) { refused = true; }
        Require(refused, "only known tools");
        string data = Path.Combine(args[0], "not-created");
        Application.EnableVisualStyles();
        using (var owner = new Form { Location = new Point(-30000, -30000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false }) {
            owner.Show();
            using (var timer = new Timer { Interval = 100 }) {
                timer.Tick += (s, e) => {
                    var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "安装终端工具（可选）");
                    if (dialog == null) return;
                    timer.Stop();
                    using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(Path.Combine(args[0], "tools.png")); }
                    ((Button)dialog.Controls.Cast<Control>().First(c => c.Text == "跳过")).PerformClick();
                };
                timer.Start(); ToolInstaller.Show(owner, data);
            }
        }
        Require(!Directory.Exists(data), "skipping never downloads or writes installation files");
        if (args.Length > 1 && args[1] == "--download-only") {
            foreach (string tool in new[] { "claude", "codex" }) {
                string script;
                try { script = System.Threading.Tasks.Task.Run(() => ToolInstaller.Download(tool, Path.Combine(args[0], "download-only"))).GetAwaiter().GetResult(); }
                catch (Exception error) { Console.WriteLine(tool + ": " + error.GetType().Name + " " + error.Message); Environment.Exit(1); return; }
                Require(new FileInfo(script).Length > 100, "official installer downloaded without running it");
                Console.WriteLine(tool + " official script download verified; not executed");
            }
        }
        Console.WriteLine("LAN selection and listener diagnostics; optional installer sources and skip verified");
    }
}
