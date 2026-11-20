using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// How much of each tool's usage limits the owner's account has used, as the tools themselves tell it; only the
/// usage windows and billing summary leave the computer. Account identities and credentials stay local.
/// - Claude Code is asked with `claude -p /usage --output-format json`. It returns the subscription windows
///   displayed by `/usage` without running a model turn. When this reading is unavailable Claude Code also
///   tells it in its stream-json (`rate_limit_event`, `unifiedWindows` with
///   `utilization` 0..1 and `resetsAt`; seen on 2.1.322), so a conversation the phone runs gives it (ClaudeChat); and to
///   its status line (`rate_limits.five_hour` and `seven_day` with `used_percentage` and `resets_at`, per its docs), so
///   every conversation open in a terminal or an editor gives it through this program's status line (`StatusLine`).
///   Only an account with a subscription is told it, and only once it has been answered once.
/// - Codex app-server reports its login type (`account/read`), selected provider (`config/read`), and the ChatGPT
///   windows and credits (`account/rateLimits/read`).
/// - Antigravity CLI is asked (`agy -p /usage --output-format json`): returns model groups (`groups`) and quota buckets
///   (`remaining_fraction`, `reset_time`); the primary Gemini/Neon group is reported without `group` and secondary model
///   pools carry their `group` label so the phone can keep them collapsed behind a toggle.
/// What each source learns is written to `quotas/<tool>.json` in the program's data folder, so that the status line,
/// which runs as a program of its own, and the conversations give it to the same place; a report reads it from there.
public sealed class Quotas {
    public sealed class Window {
        public string Name = "";        // five_hour, seven_day, <pool>_five_hour, or the length in minutes ("300m")
        public double Used;             // percent, 0..100
        public long Resets;             // seconds since 1970, 0 where not told
        public string Group = "";       // empty for the primary pool; short pool name for secondary pools (e.g. agy)
    }
    static readonly JavaScriptSerializer json = new JavaScriptSerializer();
    static readonly Regex ClaudeUsageRow = new Regex(
        @"^(?<window>Current session|Current week(?: \(all models\))?):\s*(?<used>\d+(?:\.\d+)?)%\s+used\s*·\s*resets\s+(?<reset>[^(\r\n]+)", RegexOptions.Multiline);
    static readonly string[] ClaudeResetFormats = { "MMM d, h:mmtt yyyy", "MMM d, htt yyyy" };
    static readonly DateTimeOffset Epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
    const string ClaudeUsageKey = "claude-usage";
    readonly string folder;
    readonly object gate = new object();
    DateTime nextClaude = DateTime.MinValue, nextCodex = DateTime.MinValue, nextAgy = DateTime.MinValue;
    bool askingClaude, askingCodex, askingAgy;

    public Quotas(string dataFolder) { folder = Path.Combine(dataFolder, "quotas"); }
    string FileOf(string tool) { return Path.Combine(folder, tool + ".json"); }

    /// What a tool told of its limits now.
    public void Note(string tool, IList<Window> windows) { if (windows != null && windows.Count > 0) Write(tool, windows, ""); }
    /// That a tool's limits cannot be known here, in words.
    public void Unknown(string tool, string why) { Write(tool, new Window[0], why); }
    void Write(string tool, IList<Window> windows, string why, Dictionary<string, object> billing = null) {
        var kept = new Dictionary<string, object> {
            { "tool", tool }, { "at", Milliseconds(DateTime.UtcNow) }, { "why", why ?? "" },
            { "windows", windows.Select(w => {
                var row = new Dictionary<string, object> { { "name", w.Name }, { "used", Math.Round(Math.Max(0, Math.Min(100, w.Used)), 1) }, { "resets", w.Resets } };
                if (!String.IsNullOrEmpty(w.Group)) row["group"] = w.Group;
                return row;
            }).ToArray() } };
        if (billing != null && billing.Count > 0) kept["billing"] = billing;
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
        foreach (string tool in new[] { "claude", "codex", "agy" }) {
            var reading = Kept(tool);
            if (reading != null) all.Add(reading);
        }
        return all.ToArray();
    }
    /// A tool's main windows that are still running (an Antigravity CLI pool of other models left out), for what the
    /// program itself weighs; none where nothing is known.
    public Window[] Of(string tool) {
        var windows = At(Kept(tool), "windows") as Dictionary<string, object>[];
        if (windows == null) return new Window[0];
        return windows.Where(w => !w.ContainsKey("group")).Select(w => new Window { Name = Convert.ToString(w["name"]), Used = Convert.ToDouble(w["used"], CultureInfo.InvariantCulture), Resets = Convert.ToInt64(w["resets"]) }).ToArray();
    }
    // what was last written of a tool, its windows whose time is not over; null where nothing can be read
    Dictionary<string, object> Kept(string tool) {
        if (tool == "claude") {
            var subscription = ReadKept(ClaudeUsageKey);
            var windows = At(subscription, "windows") as Dictionary<string, object>[];
            long captured;
            if (windows != null && windows.Length > 0 &&
                Int64.TryParse(Convert.ToString(At(subscription, "at")), out captured) &&
                captured >= Milliseconds(DateTime.UtcNow.AddMinutes(-15))) {
                subscription["tool"] = tool;
                return subscription;
            }
        }
        return ReadKept(tool);
    }
    Dictionary<string, object> ReadKept(string tool) {
        try {
            if (!File.Exists(FileOf(tool))) return null;
            var kept = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(FileOf(tool), Encoding.UTF8));
            long now = Milliseconds(DateTime.UtcNow) / 1000;
            object raw; kept.TryGetValue("windows", out raw);
            var windows = (raw as System.Collections.IEnumerable ?? new object[0]).Cast<object>().OfType<Dictionary<string, object>>()
                .Where(w => { long resets = Convert.ToInt64(w["resets"]); return resets == 0 || resets > now; }).ToArray();
            var reading = new Dictionary<string, object> { { "tool", tool }, { "at", kept["at"] }, { "why", kept["why"] }, { "windows", windows } };
            var billing = At(kept, "billing") as Dictionary<string, object>;
            if (tool == "codex" && billing != null) reading["billing"] = billing;
            return reading;
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (KeyNotFoundException) { } catch (FormatException) { } catch (InvalidCastException) { }
        return null;
    }

    // ---- what the tools say, read
    public static List<Window> FromClaudeUsage(object reply, DateTimeOffset now, TimeZoneInfo zone) {
        var windows = new List<Window>();
        if (Convert.ToString(At(reply, "local_command")) != "usage" || Object.Equals(At(reply, "is_error"), true)) return windows;
        DateTime local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        foreach (Match match in ClaudeUsageRow.Matches(Convert.ToString(At(reply, "result")) ?? "")) {
            double used;
            if (!Number(match.Groups["used"].Value, out used) || used < 0 || used > 100) continue;
            bool session = match.Groups["window"].Value == "Current session";
            string reset = match.Groups["reset"].Value.Trim();
            DateTime date;
            if (!DateTime.TryParseExact(reset + " " + local.Year, ClaudeResetFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)) continue;
            // On December 31 a reset date in January belongs to the next year.
            if (date < local.AddMonths(-6)) date = date.AddYears(1);
            if (zone.IsInvalidTime(date)) continue;
            var instant = new DateTimeOffset(date, zone.GetUtcOffset(date));
            if (instant <= now || instant > now.AddDays(session ? 1 : 8)) continue;
            windows.Add(new Window { Name = session ? "five_hour" : "seven_day", Used = used, Resets = (long)(instant - Epoch).TotalSeconds });
        }
        return windows;
    }

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

    public void NoteCodex(object limits, string login = "") {
        if (login == "apiKey") {
            Write("codex", new Window[0], "", new Dictionary<string, object> { { "type", "api" } });
            return;
        }
        if (limits == null) {
            Unknown("codex", login == "chatgpt" ? "暂时无法读取账号限额" : "登录 Codex 后可查看限额或计费信息");
            return;
        }
        var windows = FromCodex(limits);
        var credits = At(limits, "credits");
        var billing = new Dictionary<string, object>();
        if (Object.Equals(At(credits, "unlimited"), true)) {
            billing["type"] = "credits";
            billing["unlimited"] = true;
        } else {
            string balance = At(credits, "balance") as string;
            decimal amount;
            if (balance != null && balance.Length <= 24 &&
                Decimal.TryParse(balance, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount) && amount >= 0) {
                billing["type"] = "credits";
                billing["balance"] = amount.ToString(CultureInfo.InvariantCulture);
            }
        }
        Write("codex", windows, windows.Count == 0 && billing.Count == 0 ? "Codex 没有给出账号限额" : "", billing);
    }
    /// Antigravity CLI's `agy -p /usage --output-format json`: model groups (`groups`) and their quota buckets.
    /// The primary Gemini/Neon pool is reported without a `Group` so the phone shows it directly; secondary model
    /// pools keep a short `Group` label so the phone can fold them behind a toggle.
    public static List<Window> FromAgy(object given) {
        var windows = new List<Window>();
        var data = At(At(given, "command"), "data") ?? given;
        var rawGroups = (At(data, "groups") as System.Collections.IEnumerable ?? new object[0]).Cast<object>().OfType<Dictionary<string, object>>().ToList();
        var parsed = new List<Tuple<string, string, List<Dictionary<string, object>>>>();
        foreach (var g in rawGroups) {
            string name = Convert.ToString(At(g, "name") ?? "").Trim();
            string desc = Convert.ToString(At(g, "description") ?? "");
            var buckets = (At(g, "buckets") as System.Collections.IEnumerable ?? new object[0]).Cast<object>().OfType<Dictionary<string, object>>()
                .Where(b => { double rem; return Number(At(b, "remaining_fraction"), out rem) && rem >= 0 && rem <= 1; }).ToList();
            if (buckets.Count > 0) parsed.Add(Tuple.Create(name, desc, buckets));
        }
        if (parsed.Count == 0) return windows;
        int primary = parsed.FindIndex(g => g.Item2.IndexOf("neon", StringComparison.OrdinalIgnoreCase) >= 0);
        if (primary < 0) primary = parsed.FindIndex(g => String.Equals(g.Item1, "Gemini Models", StringComparison.OrdinalIgnoreCase));
        if (primary < 0) primary = parsed.FindIndex(g => g.Item3.Any(b => {
            string id = Convert.ToString(At(b, "id") ?? "");
            return String.Equals(id, "gemini-5h", StringComparison.OrdinalIgnoreCase) || String.Equals(id, "gemini-weekly", StringComparison.OrdinalIgnoreCase);
        }));
        if (primary < 0) primary = 0;

        var order = new List<int> { primary };
        for (int i = 0; i < parsed.Count; i++) if (i != primary) order.Add(i);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (int index in order) {
            bool isPrimary = index == primary;
            string groupLabel = isPrimary ? "" : AgyGroupLabel(parsed[index].Item1);
            var buckets = parsed[index].Item3
                .Select((b, pos) => Tuple.Create(b, pos, AgyWindowKind(Convert.ToString(At(b, "window") ?? ""), Convert.ToString(At(b, "id") ?? ""))))
                .OrderBy(t => t.Item3 == "5h" ? 0 : t.Item3 == "weekly" ? 1 : 2).ThenBy(t => t.Item2);
            foreach (var item in buckets) {
                var b = item.Item1;
                string kind = item.Item3, id = Convert.ToString(At(b, "id") ?? "");
                double remaining; if (!Number(At(b, "remaining_fraction"), out remaining)) continue;
                string wname = AgyWindowName(id, kind, isPrimary, taken);
                windows.Add(new Window { Name = wname, Group = groupLabel, Used = (1.0 - Math.Max(0, Math.Min(1, remaining))) * 100.0, Resets = Seconds(At(b, "reset_time")) });
            }
        }
        return windows;
    }
    static string AgyWindowKind(string window, string id) {
        if (String.Equals(window, "5h", StringComparison.OrdinalIgnoreCase) || id.EndsWith("-5h", StringComparison.OrdinalIgnoreCase) || id.EndsWith("_5h", StringComparison.OrdinalIgnoreCase)) return "5h";
        if (String.Equals(window, "weekly", StringComparison.OrdinalIgnoreCase) || id.EndsWith("-weekly", StringComparison.OrdinalIgnoreCase) || id.EndsWith("_weekly", StringComparison.OrdinalIgnoreCase)) return "weekly";
        return "";
    }
    static string AgyGroupLabel(string name) {
        if (String.Equals(name, "Claude and GPT models", StringComparison.OrdinalIgnoreCase)) return "Claude / GPT";
        string clean = (name ?? "").Trim();
        if (clean.Length == 0) return "其他池子";
        return clean.Length > 24 ? clean.Substring(0, 24).TrimEnd() : clean;
    }
    static string AgyWindowName(string id, string kind, bool isPrimary, HashSet<string> taken) {
        string candidate;
        if (isPrimary && kind == "5h") candidate = "five_hour";
        else if (isPrimary && kind == "weekly") candidate = "seven_day";
        else {
            string suffix = kind == "5h" ? "_five_hour" : kind == "weekly" ? "_seven_day" : "";
            string stem = Slug(id);
            foreach (string tail in new[] { "_five_hour", "_seven_day", "_weekly", "_5h" }) {
                if (suffix.Length > 0 && stem.EndsWith(tail, StringComparison.Ordinal) && stem.Length > tail.Length) {
                    stem = stem.Substring(0, stem.Length - tail.Length).TrimEnd('_');
                    break;
                }
            }
            int limit = Math.Max(1, 32 - suffix.Length);
            if (stem.Length > limit) stem = stem.Substring(0, limit).TrimEnd('_');
            if (stem.Length == 0) stem = "pool";
            candidate = stem + suffix;
        }
        if (taken.Add(candidate)) return candidate;
        for (int n = 2; n < 20; n++) {
            string alt = candidate.Length > 29 ? candidate.Substring(0, 29) + "_" + n : candidate + "_" + n;
            if (taken.Add(alt)) return alt;
        }
        return candidate;
    }
    static string Slug(string raw) {
        var sb = new StringBuilder();
        bool prevUnderscore = true;
        foreach (char ch in (raw ?? "").ToLowerInvariant()) {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) { sb.Append(ch); prevUnderscore = false; }
            else if (!prevUnderscore) { sb.Append('_'); prevUnderscore = true; }
        }
        return sb.ToString().Trim('_');
    }

    // ---- Account usage is refreshed beside the report without running a model.
    public void AskClaude(string launcher) {
        if (String.IsNullOrEmpty(launcher)) return;
        lock (gate) {
            if (askingClaude || DateTime.UtcNow < nextClaude) return;
            askingClaude = true;
            nextClaude = DateTime.UtcNow.AddMinutes(5);
        }
        Task.Run(() => {
            try { AskClaudeNow(launcher); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            finally { lock (gate) askingClaude = false; }
        });
    }
    public void AskClaudeNow(string launcher) {
        const string usage = "--print --output-format json --no-session-persistence --setting-sources user --strict-mcp-config /usage";
        Directory.CreateDirectory(folder);
        var info = new ProcessStartInfo {
            FileName = launcher, Arguments = usage, WorkingDirectory = folder,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false)
        };
        if (launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || launcher.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) {
            info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            info.Arguments = "/d /s /c \"\"" + launcher + "\" " + usage + "\"";
        }
        foreach (string key in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT" }) info.EnvironmentVariables.Remove(key);
        using (var program = Process.Start(info)) {
            try {
                program.StandardInput.Close();
                program.ErrorDataReceived += (sender, args) => { };
                program.BeginErrorReadLine();
                var output = program.StandardOutput.ReadToEndAsync();
                if (!program.WaitForExit(20000) || program.ExitCode != 0 || !output.Wait(2000)) return;
                var windows = FromClaudeUsage(ParseJsonPayload(output.Result), DateTimeOffset.UtcNow, TimeZoneInfo.Local);
                if (windows.Count > 0) Note(ClaudeUsageKey, windows);
            } finally {
                if (!program.HasExited) try { program.Kill(); } catch (InvalidOperationException) { }
            }
        }
    }
    public void AskCodex(string launcher) {
        if (String.IsNullOrEmpty(launcher)) return;
        lock (gate) { if (askingCodex || DateTime.UtcNow < nextCodex) return; askingCodex = true; nextCodex = DateTime.UtcNow.AddMinutes(10); }
        Task.Run(() => {
            try { AskCodexNow(launcher); }
            catch (Exception) { }
            finally { lock (gate) askingCodex = false; }
        });
    }
    public void AskAgy(string launcher) {
        if (String.IsNullOrEmpty(launcher)) return;
        lock (gate) { if (askingAgy || DateTime.UtcNow < nextAgy) return; askingAgy = true; nextAgy = DateTime.UtcNow.AddMinutes(10); }
        Task.Run(() => {
            try { AskAgyNow(launcher); }
            catch (Exception) { }
            finally { lock (gate) askingAgy = false; }
        });
    }
    public void AskAgyNow(string launcher) {
        if (String.IsNullOrEmpty(launcher)) return;
        var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        info.EnvironmentVariables["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
        if (launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || launcher.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) {
            info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            info.Arguments = "/d /s /c \"\"" + launcher + "\" -p /usage --output-format json\"";
        } else {
            info.FileName = launcher;
            info.Arguments = "-p /usage --output-format json";
        }
        try { if (Console.InputEncoding.GetPreamble().Length > 0) Console.InputEncoding = new UTF8Encoding(false); } catch (IOException) { }
        using (var program = Process.Start(info)) {
            try {
                program.StandardInput.Close();
                var stdout = program.StandardOutput.ReadToEndAsync();
                var stderr = program.StandardError.ReadToEndAsync();
                if (!program.WaitForExit(30000)) { try { program.Kill(); } catch { } return; }
                string text = (stdout.Wait(2000) ? stdout.Result : "") ?? "";
                string err = (stderr.Wait(2000) ? stderr.Result : "") ?? "";
                object parsed = ParseJsonPayload(text);
                if (parsed != null) {
                    var windows = FromAgy(parsed);
                    if (windows.Count > 0) { Note("agy", windows); return; }
                    if (program.ExitCode == 0) { Unknown("agy", "Antigravity CLI 没有给出额度"); return; }
                }
                string combined = (text + "\n" + err);
                if (program.ExitCode == 2 || combined.IndexOf("GEMINI_API_KEY", StringComparison.OrdinalIgnoreCase) >= 0 || combined.IndexOf("api key", StringComparison.OrdinalIgnoreCase) >= 0 || combined.IndexOf("no account quota", StringComparison.OrdinalIgnoreCase) >= 0) {
                    Unknown("agy", "Antigravity CLI 用 Google 账号登录后才能读取额度");
                } else if (program.ExitCode != 0 && (combined.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0 || combined.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0 || combined.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0)) {
                    Unknown("agy", "Antigravity CLI 登录后才能读取额度");
                }
            } finally { try { if (!program.HasExited) program.Kill(); } catch { } }
        }
    }
    static object ParseJsonPayload(string text) {
        string clean = (text ?? "").Trim().TrimStart('\uFEFF');
        if (clean.Length == 0) return null;
        try { return json.DeserializeObject(clean); } catch (ArgumentException) { } catch (InvalidOperationException) { }
        int start = clean.IndexOf('{');
        int end = clean.LastIndexOf('}');
        if (start >= 0 && end > start) {
            try { return json.DeserializeObject(clean.Substring(start, end - start + 1)); } catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
        return null;
    }
    void AskCodexNow(string launcher) {
        var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false) };
        if (launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) { info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"); info.Arguments = "/d /s /c \"\"" + launcher + "\" app-server\""; }
        else { info.FileName = launcher; info.Arguments = "app-server"; }
        try { if (Console.InputEncoding.GetPreamble().Length > 0) Console.InputEncoding = new UTF8Encoding(false); } catch (IOException) { }
        using (var program = Process.Start(info)) {
            program.ErrorDataReceived += (s, e) => { }; program.BeginErrorReadLine();
            try {
                Action<string> send = line => { byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n"); program.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); program.StandardInput.BaseStream.Flush(); };
                send("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"remote-ai\",\"title\":\"Remote AI\",\"version\":\"1\"}}}");
                send("{\"method\":\"initialized\",\"params\":{}}");
                send("{\"id\":2,\"method\":\"account/read\",\"params\":{\"refreshToken\":false}}");
                send("{\"id\":3,\"method\":\"account/rateLimits/read\",\"params\":{}}");
                var read = program.StandardOutput.ReadLineAsync();
                var until = DateTime.UtcNow.AddSeconds(20);
                bool accountRead = false, limitsRead = false, providerRead = true, externalProvider = false;
                string login = "";
                object limits = null;
                while (DateTime.UtcNow < until) {
                    if (!read.Wait(until - DateTime.UtcNow)) break;
                    string line = read.Result; if (line == null) break;
                    Dictionary<string, object> m = null;
                    try { m = json.Deserialize<Dictionary<string, object>>(line); } catch (ArgumentException) { } catch (InvalidOperationException) { }
                    object result = At(m, "result");
                    switch (Convert.ToString(At(m, "id"))) {
                        case "2":
                            accountRead = true;
                            login = Convert.ToString(At(At(result, "account"), "type"));
                            if (login.Length == 0 && Object.Equals(At(result, "requiresOpenaiAuth"), false)) {
                                providerRead = false;
                                send("{\"id\":4,\"method\":\"config/read\",\"params\":{\"includeLayers\":false}}");
                            }
                            break;
                        case "3": limitsRead = true; limits = At(result, "rateLimits"); break;
                        case "4":
                            providerRead = true;
                            var config = At(result, "config");
                            string provider = Convert.ToString(At(config, "model_provider"));
                            Uri endpoint;
                            externalProvider = Uri.TryCreate(Convert.ToString(At(At(At(config, "model_providers"), provider), "base_url")), UriKind.Absolute, out endpoint)
                                && (endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp) && !endpoint.IsLoopback;
                            break;
                    }
                    if (accountRead && limitsRead && providerRead) break;
                    read = program.StandardOutput.ReadLineAsync();
                }
                if (login.Length == 0 && externalProvider) login = "apiKey";
                if (accountRead || limitsRead) NoteCodex(limits, login);
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
            try { if (Console.InputEncoding.GetPreamble().Length > 0) Console.InputEncoding = new UTF8Encoding(false); } catch (IOException) { }
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
