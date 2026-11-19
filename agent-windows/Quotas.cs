using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// How much of each tool's usage limits the owner's account has used, as the tools themselves tell it; only the
/// share used of each window and when it starts anew leave the computer, never anything of the account.
/// - Claude Code tells it with every turn: in its stream-json (`rate_limit_event`, `unifiedWindows` with
///   `utilization` 0..1 and `resetsAt`; seen on 2.1.322), so a conversation the phone runs gives it (ClaudeChat); and to
///   its status line (`rate_limits.five_hour` and `seven_day` with `used_percentage` and `resets_at`, per its docs), so
///   every conversation open in a terminal or an editor gives it through this program's status line (`StatusLine`).
///   Only an account with a subscription is told it, and only once it has been answered once.
/// - Codex is asked (`account/rateLimits/read` of `codex app-server`): only an account signed in with ChatGPT has it;
///   `primary` and `secondary`, each with `usedPercent`, `windowDurationMins` and `resetsAt`.
/// - Antigravity CLI tells of none.
/// What each source learns is written to `quotas/<tool>.json` in the program's data folder, so that the status line,
/// which runs as a program of its own, and the conversations give it to the same place; a report reads it from there.
public sealed class Quotas {
    public sealed class Window {
        public string Name = "";        // five_hour, seven_day, or the length in minutes the tool gives ("300m")
        public double Used;             // percent, 0..100
        public long Resets;             // seconds since 1970, 0 where not told
    }
    static readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly string folder;
    readonly object gate = new object();
    DateTime nextCodex = DateTime.MinValue;
    bool askingCodex;

    public Quotas(string dataFolder) { folder = Path.Combine(dataFolder, "quotas"); }
    string FileOf(string tool) { return Path.Combine(folder, tool + ".json"); }

    /// What a tool told of its limits now.
    public void Note(string tool, IList<Window> windows) { if (windows != null && windows.Count > 0) Write(tool, windows, ""); }
    /// That a tool's limits cannot be known here, in words.
    public void Unknown(string tool, string why) { Write(tool, new Window[0], why); }
    void Write(string tool, IList<Window> windows, string why) {
        var kept = new Dictionary<string, object> {
            { "tool", tool }, { "at", Milliseconds(DateTime.UtcNow) }, { "why", why ?? "" },
            { "windows", windows.Select(w => new Dictionary<string, object> { { "name", w.Name }, { "used", Math.Round(Math.Max(0, Math.Min(100, w.Used)), 1) }, { "resets", w.Resets } }).ToArray() } };
        try {
            Directory.CreateDirectory(folder);
            string fresh = FileOf(tool) + "." + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(fresh, json.Serialize(kept), new UTF8Encoding(false));
            if (File.Exists(FileOf(tool))) File.Replace(fresh, FileOf(tool), null); else File.Move(fresh, FileOf(tool));
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    /// What is known, for the computer's report: one entry for each tool, as it was last told; a window whose time
    /// is over is left out (it has started anew, and how much of it is used is not known).
    public object[] Report() {
        var all = new List<object>();
        foreach (string tool in new[] { "claude", "codex" }) {
            try {
                if (!File.Exists(FileOf(tool))) continue;
                var kept = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(FileOf(tool), Encoding.UTF8));
                long now = Milliseconds(DateTime.UtcNow) / 1000;
                object raw; kept.TryGetValue("windows", out raw);
                var windows = (raw as System.Collections.IEnumerable ?? new object[0]).Cast<object>().OfType<Dictionary<string, object>>()
                    .Where(w => { long resets = Convert.ToInt64(w["resets"]); return resets == 0 || resets > now; }).ToArray();
                all.Add(new Dictionary<string, object> { { "tool", tool }, { "at", kept["at"] }, { "why", kept["why"] }, { "windows", windows } });
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (KeyNotFoundException) { } catch (FormatException) { } catch (InvalidCastException) { }
        }
        return all.ToArray();
    }

    // ---- what the tools say, read
    /// Claude Code's `rate_limit_info` of a `rate_limit_event` in its stream-json.
    public static List<Window> FromClaudeEvent(object info) {
        var windows = new List<Window>();
        var unified = At(info, "unifiedWindows") as Dictionary<string, object>;
        if (unified != null)
            foreach (var pair in unified) {
                double used; if (!Number(At(pair.Value, "utilization"), out used)) continue;
                windows.Add(new Window { Name = pair.Key, Used = used * 100, Resets = Seconds(At(pair.Value, "resetsAt")) });
            }
        return windows;
    }
    /// What Claude Code gives its status line: `rate_limits`, each window with `used_percentage` and `resets_at`.
    public static List<Window> FromClaudeStatus(object given) {
        var windows = new List<Window>();
        var limits = At(given, "rate_limits") as Dictionary<string, object>;
        if (limits != null)
            foreach (var pair in limits) {
                double used; if (!Number(At(pair.Value, "used_percentage"), out used)) continue;
                windows.Add(new Window { Name = pair.Key, Used = used, Resets = Seconds(At(pair.Value, "resets_at")) });
            }
        return windows;
    }
    /// Codex's `rateLimits` (app-server): `primary` and `secondary`, named by their length.
    public static List<Window> FromCodex(object limits) {
        var windows = new List<Window>();
        foreach (string key in new[] { "primary", "secondary" }) {
            var one = At(limits, key);
            double used; if (one == null || !Number(At(one, "usedPercent"), out used)) continue;
            double minutes; Number(At(one, "windowDurationMins"), out minutes);
            windows.Add(new Window { Name = minutes == 300 ? "five_hour" : minutes == 10080 ? "seven_day" : minutes > 0 ? ((long)minutes) + "m" : key, Used = used, Resets = Seconds(At(one, "resetsAt")) });
        }
        return windows;
    }

    // ---- Codex is asked, now and then, beside the report: never more than once in ten minutes, one at a time
    public void AskCodex(string launcher) {
        if (String.IsNullOrEmpty(launcher)) return;
        lock (gate) { if (askingCodex || DateTime.UtcNow < nextCodex) return; askingCodex = true; nextCodex = DateTime.UtcNow.AddMinutes(10); }
        Task.Run(() => {
            try { AskCodexNow(launcher); }
            catch (Exception) { }
            finally { lock (gate) askingCodex = false; }
        });
    }
    void AskCodexNow(string launcher) {
        var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false) };
        if (launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) { info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"); info.Arguments = "/d /s /c \"\"" + launcher + "\" app-server\""; }
        else { info.FileName = launcher; info.Arguments = "app-server"; }
        using (var program = Process.Start(info)) {
            program.ErrorDataReceived += (s, e) => { }; program.BeginErrorReadLine();
            try {
                Action<string> send = line => { byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n"); program.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); program.StandardInput.BaseStream.Flush(); };
                send("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"remote-ai\",\"title\":\"Remote AI\",\"version\":\"1\"}}}");
                send("{\"method\":\"initialized\",\"params\":{}}");
                send("{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{}}");
                var read = program.StandardOutput.ReadLineAsync();
                var until = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < until) {
                    if (!read.Wait(until - DateTime.UtcNow)) break;
                    string line = read.Result; if (line == null) break;
                    Dictionary<string, object> m = null;
                    try { m = json.Deserialize<Dictionary<string, object>>(line); } catch (ArgumentException) { } catch (InvalidOperationException) { }
                    if (m != null && Convert.ToString(At(m, "id")) == "2") {
                        if (At(m, "error") != null) Unknown("codex", Convert.ToString(At(At(m, "error"), "message")).IndexOf("chatgpt", StringComparison.OrdinalIgnoreCase) >= 0 ? "Codex 用 ChatGPT 账号登录后才能读取额度" : "Codex 没有给出额度");
                        else { var windows = FromCodex(At(At(m, "result"), "rateLimits")); if (windows.Count > 0) Note("codex", windows); else Unknown("codex", "Codex 没有给出额度"); }
                        break;
                    }
                    read = program.StandardOutput.ReadLineAsync();
                }
            } finally { try { if (!program.HasExited) program.Kill(); } catch { } }
        }
    }

    // ---- Claude Code's status line: this program, run by Claude Code with what it shows in its status line on the
    // standard input. What it tells of the limits is kept; the line it writes back is what the owner had there
    // before (their own command, run with the same input), or else the limits in a few words.
    public const string Flag = "--statusline";
    public static int StatusLine(string dataFolder, string then) {
        string given;
        using (var input = Console.OpenStandardInput()) using (var reader = new StreamReader(input, new UTF8Encoding(false))) given = reader.ReadToEnd();
        List<Window> windows = new List<Window>();
        try { windows = FromClaudeStatus(json.DeserializeObject(given.Trim().TrimStart('﻿'))); } catch (ArgumentException) { } catch (InvalidOperationException) { }
        if (windows.Count > 0) new Quotas(dataFolder).Note("claude", windows);
        string line;
        if (!String.IsNullOrEmpty(then)) line = Run(then, given);
        else line = String.Join(" · ", windows.Select(w => Said(w.Name) + " " + Math.Round(w.Used) + "%"));
        byte[] bytes = new UTF8Encoding(false).GetBytes(line ?? "");
        using (var output = Console.OpenStandardOutput()) { output.Write(bytes, 0, bytes.Length); output.Flush(); }
        return 0;
    }
    static string Said(string name) { return name == "five_hour" ? "5 小时" : name == "seven_day" ? "本周" : name; }
    // the owner's own status line, as Claude Code would have run it: a command line, given the same input
    static string Run(string command, string given) {
        try {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/d /s /c \"" + command + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, StandardOutputEncoding = new UTF8Encoding(false) };
            using (var program = Process.Start(info)) {
                byte[] bytes = new UTF8Encoding(false).GetBytes(given);
                program.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); program.StandardInput.Close();
                var said = program.StandardOutput.ReadToEndAsync();
                if (!program.WaitForExit(5000)) { try { program.Kill(); } catch { } return ""; }
                return said.Result;
            }
        } catch (Exception) { return ""; }
    }

    static object At(object from, string key) { var d = from as Dictionary<string, object>; object v; return d != null && d.TryGetValue(key, out v) ? v : null; }
    static bool Number(object value, out double number) {
        number = 0;
        return value != null && !(value is bool) && Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
    // seconds since 1970, from a number of seconds or milliseconds, or a date in words
    static long Seconds(object value) {
        double number;
        if (Number(value, out number)) return number > 100000000000 ? (long)(number / 1000) : (long)number;
        DateTime when;
        if (value is string && DateTime.TryParse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when)) return Milliseconds(when) / 1000;
        return 0;
    }
    static long Milliseconds(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
}

/// This program's status line in Claude Code's settings, where its owner keeps the limits seen on the phone: put in
/// where there is none; where there is one of the owner's, that one is kept (the status line runs it and shows what it
/// writes), and put back when this is taken out.
public static class QuotaStatusLine {
    const string Mine = "--statusline";
    static string Settings { get { return ReachHooks.FileOf("claude"); } }
    static string Slashes(string path) { return path.Replace('\\', '/'); }
    /// The command Claude Code is given: this program with the data folder, and the owner's own command after `--then`.
    /// It is one line for a shell, and the shell differs: Claude Code 2.1.322 on Windows runs it with PowerShell, where a
    /// program named in quotes is a string and runs only with `&` (tried), which no other shell takes. So nothing in it
    /// is in quotes: a path with a space is named by its short name; where its drive keeps no short names, the program
    /// runs from a copy of it beside the data (`Copy`), whose folder has a short name on the system drive. Only where
    /// that has a space too is it called as PowerShell calls it.
    public static string Command(string program, string dataFolder, string then) {
        return Runnable(program, dataFolder) + " " + Mine + " " + Bare(dataFolder) + (String.IsNullOrEmpty(then) ? "" : " --then " + Convert.ToBase64String(Encoding.UTF8.GetBytes(then)));
    }
    /// Where the copy of the program is kept, for a program whose own path cannot be named without a space.
    public static string Copy(string dataFolder) { return Path.Combine(dataFolder, "statusline", "RemoteCliAgent.exe"); }
    static string Runnable(string program, string dataFolder) {
        string plain = Short(program, false);
        if (plain.IndexOf(' ') < 0) return Slashes(plain);
        string copy = Short(Path.GetDirectoryName(Copy(dataFolder)), true) + "\\" + Path.GetFileName(Copy(dataFolder));
        if (copy.IndexOf(' ') < 0) return Slashes(copy);
        return "& '" + Slashes(program).Replace("'", "''") + "'";
    }
    static string Bare(string folder) { string plain = Short(folder, true); return plain.IndexOf(' ') < 0 ? Slashes(plain) : "'" + Slashes(folder).Replace("'", "''") + "'"; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);
    // a path by its short name where it has a space and its drive keeps short names (a folder is made, to have one)
    static string Short(string path, bool folder) {
        if (path.IndexOf(' ') < 0) return path;
        if (folder) try { Directory.CreateDirectory(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var named = new StringBuilder(1024);
        uint length = GetShortPathName(path, named, (uint)named.Capacity);
        return length > 0 && length < named.Capacity ? named.ToString() : path;
    }
    // The copy is made, or made anew where the program has changed since (an update): it is the program run.
    static void Refresh(string program, string dataFolder) {
        if (Short(program, false).IndexOf(' ') < 0) return;
        string copy = Copy(dataFolder);
        try {
            var from = new FileInfo(program); var to = new FileInfo(copy);
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc) return;
            Directory.CreateDirectory(Path.GetDirectoryName(copy));
            File.Copy(program, copy, true);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }      // (a status line that runs it now holds it: the next time)
    }
    static bool Fresh(string program, string dataFolder) {
        if (Short(program, false).IndexOf(' ') < 0) return true;
        try { var from = new FileInfo(program); var to = new FileInfo(Copy(dataFolder)); return to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc; }
        catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
    }
    static Dictionary<string, object> Read() {
        string file = Settings;
        if (!File.Exists(file)) return new Dictionary<string, object>();
        string text = File.ReadAllText(file, Encoding.UTF8).TrimStart('﻿');
        if (text.Trim().Length == 0) return new Dictionary<string, object>();
        var read = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
        if (read == null) throw new InvalidOperationException("settings.json 的内容无法读懂，没有改动它");
        return read;
    }
    static string CommandOf(Dictionary<string, object> all) {
        object line; var status = all.TryGetValue("statusLine", out line) ? line as Dictionary<string, object> : null;
        object command; return status != null && status.TryGetValue("command", out command) ? Convert.ToString(command) : "";
    }
    /// The owner's own command that this program's line runs after it, from a command of this program's.
    static string Then(string command) {
        int at = command.IndexOf(" --then ", StringComparison.Ordinal);
        if (at < 0) return "";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(command.Substring(at + 8).Trim())); } catch (FormatException) { return ""; }
    }
    public static bool Installed(string program, string dataFolder) {
        try { string command = CommandOf(Read()); return command.Contains(Mine) && command == Command(program, dataFolder, Then(command)) && Fresh(program, dataFolder); }
        catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } catch (InvalidOperationException) { return false; } catch (ArgumentException) { return false; }
    }
    public static void Set(bool on, string program, string dataFolder) {
        string file = Settings;
        if (!on && !File.Exists(file)) return;
        var all = Read();
        string command = CommandOf(all);
        bool ours = command.Contains(Mine);
        if (on) {
            Refresh(program, dataFolder);
            if (ours && command == Command(program, dataFolder, Then(command))) return;
            string then = ours ? Then(command) : command;
            var line = new Dictionary<string, object> { { "type", "command" }, { "command", Command(program, dataFolder, then) } };
            object kept; var before = all.TryGetValue("statusLine", out kept) ? kept as Dictionary<string, object> : null;
            if (before != null && before.ContainsKey("padding")) line["padding"] = before["padding"];
            all["statusLine"] = line;
        } else {
            if (!ours) return;
            string then = Then(command);
            if (then.Length > 0) all["statusLine"] = new Dictionary<string, object> { { "type", "command" }, { "command", then } };
            else all.Remove("statusLine");
        }
        ReachHooks.WriteSettings(file, all);
    }
}
}
