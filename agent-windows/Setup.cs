using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RemoteCli {
/// The installer: unpacks the program into a folder of your choice, adds shortcuts and an entry under "Apps"
/// for removing it. The default folder needs no administrator rights. `--uninstall` undoes all of it.
///   --quiet            no questions and no start afterwards
///   --dir <folder>     where to install (default: the folder of an earlier installation, else %LOCALAPPDATA%\Programs\RemoteCli)
public static class Setup {
    const string Version = "1.5.10", Name = "Remote AI";
    // The names it had before. Their shortcuts are taken away when this one is installed or removed; the folder, the
    // registry keys and the files keep their names, so that an installation is found and replaced whatever it was called.
    static readonly string[] EarlierNames = { "Remote CLI" };
    // REMOTECLI_SETUP_SANDBOX=1 is for the tests: its own registry entry and shortcut names, and no look at a running
    // copy, so trying the installer never touches a real installation.
    static readonly bool Sandbox = Environment.GetEnvironmentVariable("REMOTECLI_SETUP_SANDBOX") == "1";
    static readonly string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteCli" + (Sandbox ? "-Sandbox" : "");
    static string LinkName { get { return Name + (Sandbox ? " sandbox" : "") + ".lnk"; } }
    // What the installer puts into its folder; nothing else in that folder is ever touched.
    static readonly string[] OwnFolders = { "python", "relay", "web" };
    static readonly string[] OwnFiles = { "RemoteCli.exe", "RemoteCliAgent.exe", "Uninstall.exe", "LICENSE", "THIRD_PARTY.md", "README.md" };
    static string Data { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCli"); } }
    static string[] Shortcuts { get { return new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), LinkName), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LinkName) }; } }
    static string[] EarlierShortcuts { get { return EarlierNames.Select(name => name + (Sandbox ? " sandbox" : "") + ".lnk").SelectMany(link => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), link), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), link) }).ToArray(); } }

    static string Installed() {
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey)) return key == null ? "" : Convert.ToString(key.GetValue("InstallLocation") ?? "");
    }
    static string DefaultTarget() {
        string earlier = Installed();
        return earlier.Length > 0 ? earlier : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RemoteCli");
    }
    /// The folder the program goes into. A folder that already holds other things gets a "RemoteCli" folder inside it.
    public static string Resolve(string chosen) {
        string path = Path.GetFullPath(chosen.Trim().Trim('"')).TrimEnd('\\');
        if (path.Length < 3) path += "\\";                                   // a drive root such as D:
        bool ours = File.Exists(Path.Combine(path, "RemoteCli.exe"));
        bool empty = !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();
        return ours || empty ? path : Path.Combine(path, "RemoteCli");
    }
    static void RemoveOwn(string folder) {
        foreach (string name in OwnFolders) { string path = Path.Combine(folder, name); if (Directory.Exists(path)) Directory.Delete(path, true); }
        foreach (string name in OwnFiles) { string path = Path.Combine(folder, name); if (File.Exists(path)) File.Delete(path); }
    }
    static bool Running() {
        if (Sandbox) return false;
        bool created;
        using (var probe = new Mutex(false, "Local\\RemoteCliApp", out created)) return !created;
    }
    static void Shortcut(string file, string target) {
        Type type = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(type);
        object link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { file });
        Type kind = link.GetType();
        kind.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
        kind.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
        kind.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "在手机上使用这台电脑的终端" });
        kind.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
    }

    /// The window that asks where to install. Returns the chosen folder, or null when cancelled.
    /// With a file name it only draws itself into that picture, for the documentation.
    static string Ask(string suggested, string picture = null) {
        using (var form = new Form { Text = "安装 " + Name, ClientSize = new Size(520, 250), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                                     StartPosition = FormStartPosition.CenterScreen, BackColor = Theme.Bg, ForeColor = Theme.Ink, Font = new Font("Microsoft YaHei UI", 9f), AutoScaleMode = AutoScaleMode.None, Icon = Theme.MarkIcon(32) }) {
            form.HandleCreated += (s, e) => Theme.Title(form.Handle);
            Action<Control, int, int, int, int> place = (control, x, y, w, h) => { control.SetBounds(x, y, w, h); form.Controls.Add(control); };
            place(new PictureBox { Image = Theme.Mark(96), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Bg }, 24, 22, 48, 48);
            place(Theme.Label(Name + " " + Version, Theme.Ink, Theme.Bg, 14f, true), 86, 20, 400, 28);
            place(Theme.Label("在手机上使用这台电脑的终端、Claude Code 和 Codex", Theme.Muted, Theme.Bg), 86, 50, 410, 20);
            place(Theme.Label("安装位置", Theme.Muted, Theme.Bg, 0, true), 24, 96, 200, 18);
            var field = new Field(); field.BackColor = Theme.Bg; field.Box.Text = suggested;
            place(field, 24, 118, 376, 32);
            var browse = Theme.Button("浏览…");
            place(browse, 408, 117, 88, 34);
            place(Theme.Label("默认位置不需要管理员权限。选了已有内容的文件夹时，会在里面新建 RemoteCli 文件夹。\n设置和密码保存在别处，升级和换位置都会保留。", Theme.Muted, Theme.Bg, 8.25f), 24, 158, 472, 36);
            var install = Theme.Button("安装", true); install.DialogResult = DialogResult.OK;
            var cancel = Theme.Button("取消"); cancel.DialogResult = DialogResult.Cancel;
            place(install, 300, 202, 96, 34); place(cancel, 404, 202, 92, 34);
            form.AcceptButton = install; form.CancelButton = cancel;
            browse.Click += (s, e) => { using (var dialog = new FolderBrowserDialog { Description = "选择安装位置", SelectedPath = Directory.Exists(field.Box.Text) ? field.Box.Text : "" }) if (dialog.ShowDialog(form) == DialogResult.OK) field.Box.Text = Resolve(dialog.SelectedPath); };
            using (var g = form.CreateGraphics()) { float factor = g.DpiX / 96f; if (factor > 1.01f) form.Scale(new SizeF(factor, factor)); }
            if (picture != null) {
                // drawn off the screen: nobody at the computer is to take it for the installer
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false;
                form.Show();
                var until = DateTime.UtcNow.AddSeconds(1.5);
                while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(30); }
                Rectangle client = form.RectangleToScreen(form.ClientRectangle);
                using (var whole = new Bitmap(form.Width, form.Height)) using (var shown = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) {
                    form.DrawToBitmap(whole, new Rectangle(0, 0, form.Width, form.Height));
                    using (var g = Graphics.FromImage(shown)) g.DrawImage(whole, -(client.X - form.Left), -(client.Y - form.Top));
                    shown.Save(picture, System.Drawing.Imaging.ImageFormat.Png);
                }
                return null;
            }
            for (;;) {
                if (form.ShowDialog() != DialogResult.OK) return null;
                try { return Resolve(field.Box.Text); } catch (Exception) { MessageBox.Show(form, "这个位置无效，请重新选择。", "安装 " + Name); }
            }
        }
    }
    static int Install(bool quiet, string wanted, bool launch) {
        string target = wanted.Length > 0 ? Resolve(wanted) : DefaultTarget();
        if (!quiet) { target = Ask(target); if (target == null) return 1; }
        if (Running()) { if (!quiet) MessageBox.Show(Name + " 正在运行。请先在托盘图标上点右键选“退出”，再重新运行安装程序。", "安装 " + Name); return 1; }
        string earlier = Installed();
        try {
            Directory.CreateDirectory(target);
            if (earlier.Length > 0 && Directory.Exists(earlier) && !String.Equals(earlier.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) {
                RemoveOwn(earlier);                                           // moved: the earlier copy goes, its folder only if nothing else is in it
                if (!Directory.EnumerateFileSystemEntries(earlier).Any()) Directory.Delete(earlier);
            }
            RemoveOwn(target);
            using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read)) {
                string root = Path.GetFullPath(target).TrimEnd('\\') + Path.DirectorySeparatorChar;
                foreach (ZipArchiveEntry entry in archive.Entries) {
                    string path = Path.GetFullPath(Path.Combine(target, entry.FullName));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装包内容无效");
                    if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    entry.ExtractToFile(path, true);
                }
            }
        } catch (UnauthorizedAccessException) {
            if (!quiet) MessageBox.Show("没有权限写入\n" + target + "\n\n请换一个位置，或右键安装程序选“以管理员身份运行”。", "安装 " + Name);
            return 2;
        }
        string program = Path.Combine(target, "RemoteCli.exe"), remover = Path.Combine(target, "Uninstall.exe");
        File.Copy(Application.ExecutablePath, remover, true);
        foreach (string file in Shortcuts) Shortcut(file, program);
        foreach (string file in EarlierShortcuts) try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey)) {
            key.SetValue("DisplayName", Name); key.SetValue("DisplayVersion", Version); key.SetValue("Publisher", "remote-cli");
            key.SetValue("InstallLocation", target); key.SetValue("UninstallString", "\"" + remover + "\" --uninstall");
            key.SetValue("DisplayIcon", program);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        // "Start with Windows" points at the program's path; keep it right after a move.
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            if (!Sandbox && run != null && run.GetValue("RemoteCli") != null) run.SetValue("RemoteCli", "\"" + program + "\" --hidden");
        if (!quiet || launch) Process.Start(new ProcessStartInfo(program) { WorkingDirectory = target, UseShellExecute = true });
        return 0;
    }
    static void WaitFor(int pid) {
        try { using (var owner = Process.GetProcessById(pid)) owner.WaitForExit(); } catch (ArgumentException) { }
    }
    static int Uninstall(bool quiet) {
        // The copy that runs as Uninstall.exe sits in the installation folder.
        string here = Path.GetDirectoryName(Application.ExecutablePath), target = File.Exists(Path.Combine(here, "RemoteCli.exe")) ? here : Installed();
        if (target.Length == 0 || !Directory.Exists(target)) { if (!quiet) MessageBox.Show("没有找到已安装的 " + Name + "。", "卸载"); return 1; }
        if (!quiet && MessageBox.Show("卸载 " + Name + "？\n" + target, "卸载", MessageBoxButtons.OKCancel) != DialogResult.OK) return 1;
        if (Running()) { if (!quiet) MessageBox.Show(Name + " 正在运行。请先在托盘图标上点右键选“退出”。", "卸载"); return 1; }
        foreach (string file in Shortcuts.Concat(EarlierShortcuts)) try { File.Delete(file); } catch { }
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) if (run != null && !Sandbox) run.DeleteValue("RemoteCli", false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        bool all = !quiet && Directory.Exists(Data) && MessageBox.Show("同时删除设置、密码和下载的隧道程序吗？\n" + Data, "卸载", MessageBoxButtons.YesNo) == DialogResult.Yes;
        if (all) try { Directory.Delete(Data, true); } catch { }
        foreach (string name in OwnFolders) try { string path = Path.Combine(target, name); if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        foreach (string name in OwnFiles.Where(n => n != "Uninstall.exe")) try { File.Delete(Path.Combine(target, name)); } catch { }
        // This program runs from the folder it removes: a command window removes it, and the folder if nothing else is in it, after it has exited.
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del /q \"" + Path.Combine(target, "Uninstall.exe") + "\" & rd \"" + target + "\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        if (!quiet) MessageBox.Show(Name + " 已卸载。", "卸载");
        return 0;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [STAThread] public static int Main(string[] args) {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        bool quiet = Array.IndexOf(args, "--quiet") >= 0;
        int at = Array.IndexOf(args, "--dir"), shot = Array.IndexOf(args, "--screenshot"), wait = Array.IndexOf(args, "--wait-pid");
        bool launch = Array.IndexOf(args, "--launch") >= 0;
        try {
            if (shot >= 0 && shot + 1 < args.Length) { Ask(@"C:\Users\you\AppData\Local\Programs\RemoteCli", args[shot + 1]); return 0; }      // a neutral path for a published picture
            int pid;
            if (wait >= 0 && wait + 1 < args.Length && Int32.TryParse(args[wait + 1], out pid)) WaitFor(pid);
            return Array.IndexOf(args, "--uninstall") >= 0 ? Uninstall(quiet) : Install(quiet, at >= 0 && at + 1 < args.Length ? args[at + 1] : "", launch);
        } catch (Exception error) { if (!quiet) MessageBox.Show("没有完成：" + error.Message, Name); return 2; }
    }
}
}
