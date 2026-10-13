using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace RemoteCli {
// Each adapter talks to the application itself, never a new CLI/app-server.
public interface IDesktopAdapter {
    string Tool { get; }
    bool Available { get; }
    /// Refuses a folder or a draft the application would not take, before anything is reserved or opened.
    void Check(string folder, string draft);
    Dictionary<string, object> Open(string folder, string draft);
}

/// An application that opens a project with a new conversation, and a draft in its box, by a link of its own
/// scheme: the entry it has for that itself. Nothing is typed into it, nothing is sent, no second backend is
/// started. One class for each application says where it is installed, what its link is and how the link is given.
public abstract class LinkedDesktopApp : IDesktopAdapter {
    public abstract string Tool { get; }
    public abstract bool Available { get; }
    protected abstract string Name { get; }                              // as its owner knows it
    protected abstract string LinkTo(string folder, string draft);
    protected abstract void Launch(string link, string folder);

    protected const string Packages = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    /// The installed desktop package of the newest version that is at least `least`: its folder and its key's name.
    /// Only package names and locations are read.
    protected static string[] Package(string pattern, Version least) {
        try {
            using (var packages = Registry.CurrentUser.OpenSubKey(Packages)) {
                if (packages == null) return null;
                var fitting = new List<KeyValuePair<Version, string>>();
                foreach (string name in packages.GetSubKeyNames()) {
                    var match = Regex.Match(name, pattern);
                    Version version;
                    if (match.Success && Version.TryParse(match.Groups[1].Value, out version) && version >= least) fitting.Add(new KeyValuePair<Version, string>(version, name));
                }
                foreach (var found in fitting.OrderByDescending(one => one.Key)) {
                    using (var package = packages.OpenSubKey(found.Value)) {
                        string root = Convert.ToString(package.GetValue("PackageRootFolder"));
                        if (!String.IsNullOrEmpty(root) && Directory.Exists(root)) return new[] { root, found.Value };
                    }
                }
            }
        } catch (System.Security.SecurityException) { } catch (UnauthorizedAccessException) { } catch (IOException) { }
        return null;
    }
    /// What every application is given: a folder by its full path, and a draft a person could have typed.
    protected static void Usual(string folder, string draft) {
        if (String.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder) || folder.Any(c => Char.IsControl(c))) throw new ArgumentException("项目文件夹无效");
        if ((draft ?? "").Length > 1000 || (draft ?? "").Any(c => Char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')) throw new ArgumentException("草稿需在 1000 字以内，且不能含控制字符");
    }
    protected static string Sized(string link) { if (link.Length > 16000) throw new ArgumentException("项目路径和草稿过长，请缩短后重试"); return link; }

    public void Check(string folder, string draft) { LinkTo(folder, draft); }
    public Dictionary<string, object> Open(string folder, string draft) {
        if (!Available) throw new InvalidOperationException("未找到支持此入口的 " + Name + " Windows 桌面应用，请安装或更新桌面应用后重启 Remote AI");
        string link = LinkTo(folder, draft);
        if (!Directory.Exists(folder)) throw new InvalidOperationException("项目文件夹已不存在");
        Launch(link, folder);
        return new Dictionary<string, object> {
            { "status", "handed_off" }, { "tool", Tool }, { "sent", false },
            { "message", "已交给 " + Name + " 桌面应用。请在电脑上确认项目和草稿，内容尚未发送。" }
        };
    }
}

public sealed class CodexDesktopAdapter : LinkedDesktopApp {
    public override string Tool { get { return "codex"; } }
    public override bool Available { get { return executable != null && File.Exists(executable); } }
    protected override string Name { get { return "Codex"; } }
    readonly string executable;
    public CodexDesktopAdapter() {
        // Installed desktop package, not the CLI on PATH.
        var package = Package(@"\AOpenAI\.Codex_(\d+\.\d+\.\d+\.\d+)_x64__2p2nqsd0c76g0\z", new Version(26, 1007, 2314, 0));
        executable = package == null ? null : Path.Combine(package[0], "app", "ChatGPT.exe");
    }

    public static string Link(string folder, string draft) {
        Usual(folder, draft);
        string link = "codex://threads/new?path=" + Uri.EscapeDataString(Path.GetFullPath(folder));
        if (!String.IsNullOrEmpty(draft)) link += "&prompt=" + Uri.EscapeDataString(draft);
        return Sized(link);
    }
    protected override string LinkTo(string folder, string draft) { return Link(folder, draft); }
    // The app's single-instance dispatcher handles project selection/creation and a new composer.
    protected override void Launch(string link, string folder) {
        using (var process = Process.Start(new ProcessStartInfo(executable, CodexQueue.Quoted(link)) { UseShellExecute = false, WorkingDirectory = folder })) { }
    }
}

/// Claude's desktop app: `claude://code/new` opens a new conversation of Claude Code in the folder it names, with
/// what `q` carries standing in its box (the app's own `ccd <folder>` gives it the same link). The link is handed
/// to Windows, which gives it to the application that registered the scheme: so the entry is offered only where
/// that is the installed Claude package.
public sealed class ClaudeDesktopAdapter : LinkedDesktopApp {
    public override string Tool { get { return "claude"; } }
    public override bool Available { get { return available; } }
    protected override string Name { get { return "Claude"; } }
    readonly bool available;
    public ClaudeDesktopAdapter() {
        var package = Package(@"\AClaude_(\d+\.\d+\.\d+\.\d+)_x64__pzs8sxrjxfjjc\z", new Version(2, 31226, 1, 0));
        available = package != null && Registered(package[1]);
    }
    // The package says that `claude:` is its own, and its owner has not given the scheme to another program.
    static bool Registered(string package) {
        try {
            string own;
            using (var declared = Registry.CurrentUser.OpenSubKey(Packages + @"\" + package + @"\Claude\Capabilities\URLAssociations")) {
                own = declared == null ? "" : Convert.ToString(declared.GetValue("claude"));
            }
            if (String.IsNullOrEmpty(own)) return false;
            using (var chosen = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\claude\UserChoice")) {
                string other = chosen == null ? "" : Convert.ToString(chosen.GetValue("ProgId"));
                return String.IsNullOrEmpty(other) || String.Equals(other, own, StringComparison.OrdinalIgnoreCase);
            }
        } catch (System.Security.SecurityException) { return false; } catch (UnauthorizedAccessException) { return false; } catch (IOException) { return false; }
    }

    public static string Link(string folder, string draft) {
        Usual(folder, draft);
        // (the application itself refuses a folder on another computer)
        if (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal)) throw new ArgumentException("Claude 桌面应用不打开网络路径上的文件夹");
        string link = "claude://code/new?folder=" + Uri.EscapeDataString(Path.GetFullPath(folder));
        if (!String.IsNullOrEmpty(draft)) link += "&q=" + Uri.EscapeDataString(draft);
        return Sized(link);
    }
    protected override string LinkTo(string folder, string draft) { return Link(folder, draft); }
    protected override void Launch(string link, string folder) {
        using (var process = Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })) { }
    }
}

// Reserve before opening the UI. A crash between dispatch and receipt is unknown, never replayed.
// Store only fingerprints and receipts, never drafts or project paths.
public sealed class DesktopOperations {
    readonly string directory;
    readonly Dictionary<string, IDesktopAdapter> adapters;
    readonly object gate = new object();
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    public DesktopOperations(string dataDir, params IDesktopAdapter[] adapters) {
        directory = Path.Combine(dataDir, "desktop-operations");
        this.adapters = adapters.ToDictionary(a => a.Tool, StringComparer.Ordinal);
    }
    public bool Available(string tool) { IDesktopAdapter adapter; return adapters.TryGetValue(tool ?? "", out adapter) && adapter.Available; }
    /// The tools whose application can be opened from the phone here.
    public string[] Tools { get { return adapters.Values.Where(a => a.Available).Select(a => a.Tool).ToArray(); } }
    public void Check(string tool, string folder, string draft) { adapters[tool].Check(folder, draft); }
    public Dictionary<string, object> Open(string id, string tool, string folder, string draft) {
        IDesktopAdapter adapter;
        if (!adapters.TryGetValue(tool ?? "", out adapter)) throw new InvalidOperationException("这个桌面应用暂不支持从手机打开项目和草稿");
        return Run(id, new[] { tool, folder, draft ?? "" }, () => {
            if (!adapter.Available) throw new InvalidOperationException("桌面应用入口不可用，请在电脑上安装或更新应用后重启 Remote AI");
            return adapter.Open(folder, draft);
        }, false);
    }
    public Dictionary<string, object> Run(string id, string[] identity, Func<Dictionary<string, object>> action, bool control = true, bool queryOnly = false) {
        if (!Regex.IsMatch(id ?? "", @"\A[a-f0-9]{16,32}\z")) throw new ArgumentException("操作编号无效");
        lock (gate) {
            string fingerprint;
            using (var hash = SHA256.Create()) fingerprint = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(json.Serialize(identity))));
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, id + ".json");
            if (File.Exists(file)) return Read(file, fingerprint);
            if (queryOnly) return new Dictionary<string, object> { { "status", "unknown" }, { "sent", null }, { "message", "电脑没有本次操作的回执，请先检查原对话。" } };
            var receipt = new Dictionary<string, object> { { "fingerprint", fingerprint }, { "result", new Dictionary<string, object> {
                { "status", "unknown" }, { "sent", control ? (object)null : false }, { "message", "上次操作的结果未知，未重复执行。请先检查电脑上的应用。" }
            } } };
            try {
                using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var bytes = Encoding.UTF8.GetBytes(json.Serialize(receipt)); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
            } catch (IOException) { if (File.Exists(file)) return Read(file, fingerprint); throw; }
            try { receipt["result"] = action(); }
            catch (Exception error) {
                // A transport failure after dispatch can have succeeded. Its reservation remains unknown.
                if (!control || error is ArgumentException || error is InvalidOperationException)
                    receipt["error"] = error is ArgumentException || error is InvalidOperationException ? error.Message : "无法打开桌面应用，请在电脑上检查应用状态";
            }
            string temporary = file + ".tmp";
            try {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) {
                    var bytes = Encoding.UTF8.GetBytes(json.Serialize(receipt)); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                File.Replace(temporary, file, null);
            } catch (IOException) { return Read(file, fingerprint); }
            return Read(file, fingerprint);
        }
    }
    Dictionary<string, object> Read(string file, string fingerprint) {
        Dictionary<string, object> receipt;
        try { receipt = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8)); }
        catch (Exception) { throw new InvalidOperationException("上次操作记录无法读取，未重复打开。请先检查电脑上的应用"); }
        if (receipt == null || !receipt.ContainsKey("fingerprint") || Convert.ToString(receipt["fingerprint"]) != fingerprint) throw new InvalidOperationException("操作编号已被其它操作使用");
        if (receipt.ContainsKey("error")) throw new InvalidOperationException(Convert.ToString(receipt["error"]));
        if (!receipt.ContainsKey("result") || !(receipt["result"] is Dictionary<string, object>)) throw new InvalidOperationException("操作结果无法确认，未重复打开");
        return (Dictionary<string, object>)receipt["result"];
    }
}
}
