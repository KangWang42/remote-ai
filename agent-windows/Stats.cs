using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// How the owner's work with the tools went, in numbers, read from the tools' own records on this computer:
/// what was asked and when, how long the tools worked on it, how soon they first answered, and the tokens they
/// note for each answer. By day, by tool, by project and by hour of the day. Nothing that was said is kept or
/// passed on: numbers, dates, and the names of the folders the conversations are in.
///
/// Supports Claude Code (`projects/*/*.jsonl`), Codex (`sessions/**/rollout-*.jsonl`), and Antigravity CLI
/// (`brain/*/.system_generated/logs/transcript.jsonl`). Parsed summaries are persisted in a compact ledger
/// (`stats-ledger.json` when configured) so restarts, app updates, and tool log cleanups never reset history.
public sealed class Stats {
    public const int LedgerVersion = 2;
    public const int MaxLedgerDays = 400;
    public const int MaxWaitsPerDay = 64;
    public const double LongestTurn = 3 * 3600, LongestWait = 600, MaxStepGap = 600;

    public sealed class Day {
        public int Prompts;
        public double Active;
        public long In, Out, Cached;
        public readonly List<double> Waits = new List<double>();
        public readonly HashSet<string> Sessions = new HashSet<string>();
        public readonly int[] Hours = new int[24];
    }

    sealed class Record {
        public long Stamp, Length;
        public string Tool = "", Session = "", Folder = "";
        public readonly Dictionary<string, Day> Days = new Dictionary<string, Day>(StringComparer.Ordinal);
    }

    readonly Func<string> claudeHome, codexHome, agyHome;
    readonly string ledgerPath;
    readonly Dictionary<string, Record> records = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new object();
    readonly object scanGate = new object();
    bool ledgerLoaded;
    long agyHistoryStamp, agyHistoryLength;
    Dictionary<string, string> agyWorkspaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public Stats(Func<string> claudeHome, Func<string> codexHome)
        : this(claudeHome, codexHome, null, null) { }

    public Stats(Func<string> claudeHome, Func<string> codexHome, Func<string> agyHome, string ledgerPath) {
        this.claudeHome = claudeHome;
        this.codexHome = codexHome;
        this.agyHome = agyHome;
        this.ledgerPath = ledgerPath ?? "";
    }

    static readonly Regex Stamped = new Regex("\"timestamp\":\"([0-9T:.+Z-]{19,40})\"", RegexOptions.Compiled);
    static readonly Regex CreatedAt = new Regex("\"created_at\":\"([0-9T:.+Z-]{19,40})\"", RegexOptions.Compiled);
    static readonly Regex Folder = new Regex("\"(?:cwd|workingDirectory)\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    static readonly Regex Answer = new Regex("\"id\":\"(msg_[A-Za-z0-9_-]+)\"", RegexOptions.Compiled);
    static readonly Regex AgyConvId = new Regex("\"conversationId\":\"([0-9a-fA-F-]{36})\"", RegexOptions.Compiled);
    static readonly Regex AgyWorkspace = new Regex("\"workspace\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    static readonly Regex UuidFolder = new Regex(@"\A[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\z", RegexOptions.Compiled);

    static long Number(string line, string name, int from = 0) {
        int at = line.IndexOf("\"" + name + "\":", from, StringComparison.Ordinal);
        if (at < 0) return 0;
        at += name.Length + 3;
        while (at < line.Length && line[at] == ' ') at++;
        long value = 0; bool any = false;
        while (at < line.Length && line[at] >= '0' && line[at] <= '9') { value = value * 10 + (line[at] - '0'); at++; any = true; }
        return any ? value : 0;
    }

    static bool Time(Regex pattern, string line, bool last, out DateTime at) {
        at = DateTime.MinValue;
        Match found = pattern.Match(line);
        if (!found.Success) return false;
        if (last) { Match next; while ((next = found.NextMatch()).Success) found = next; }
        return DateTime.TryParse(found.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at);
    }

    static string Unescaped(string text) {
        return Regex.Replace(text, @"\\(.)", m => m.Groups[1].Value == "n" ? "\n" : m.Groups[1].Value);
    }

    static string DayOf(DateTime at) { return at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

    static Day Of(Record record, DateTime at) {
        string key = DayOf(at);
        Day day;
        if (!record.Days.TryGetValue(key, out day)) record.Days[key] = day = new Day();
        return day;
    }

    static void AddWait(Day day, double wait) {
        if (wait < 0 || wait > LongestWait) return;
        if (day.Waits.Count < MaxWaitsPerDay) day.Waits.Add(Math.Round(wait, 1));
    }

    static IEnumerable<string> Lines(string path) {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8)) {
            string line;
            while ((line = reader.ReadLine()) != null) yield return line;
        }
    }

    /// One record of Claude Code's: its prompts, the turns that followed, and the usage of each answer.
    static void Claude(string path, Record record) {
        var usage = new Dictionary<string, object[]>();        // an answer's id -> the day it was given, and its tokens
        DateTime began = DateTime.MinValue, lastAt = DateTime.MinValue;
        double turnActive = 0;
        bool open = false, waiting = false;
        Action close = () => {
            if (open) Of(record, began).Active += Math.Max(0, Math.Min(LongestTurn, turnActive));
            open = false; turnActive = 0;
        };
        foreach (string line in Lines(path)) {
            if (line.Length < 40) continue;
            if (record.Folder.Length == 0 && (line.IndexOf("\"cwd\":", StringComparison.Ordinal) >= 0 || line.IndexOf("\"workingDirectory\":", StringComparison.Ordinal) >= 0)) {
                Match where = Folder.Match(line);
                if (where.Success) record.Folder = Unescaped(where.Groups[1].Value);
            }
            bool answer = line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) >= 0;
            bool asked = !answer && line.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) >= 0;
            if ((!answer && !asked) || line.IndexOf("\"isSidechain\":true", StringComparison.Ordinal) >= 0) continue;
            DateTime at;
            if (!Time(Stamped, line, true, out at)) continue;
            if (asked) {
                // what a tool gave back belongs to the turn that runs; what Claude Code notes for itself is nobody's prompt
                if (line.IndexOf("\"tool_use_id\"", StringComparison.Ordinal) >= 0) {
                    if (open) {
                        double step = (at - lastAt).TotalSeconds;
                        if (step > 0) turnActive = Math.Min(LongestTurn, turnActive + Math.Min(MaxStepGap, step));
                        if (at > lastAt) lastAt = at;
                    }
                    continue;
                }
                if (line.IndexOf("\"isMeta\":true", StringComparison.Ordinal) >= 0) continue;
                close();
                began = lastAt = at; open = waiting = true; turnActive = 0;
                Day day = Of(record, at);
                day.Prompts++;
                day.Sessions.Add(record.Session);
                day.Hours[at.ToLocalTime().Hour]++;
                continue;
            }
            if (open) {
                if (waiting) { AddWait(Of(record, began), (at - began).TotalSeconds); waiting = false; }
                double step = (at - lastAt).TotalSeconds;
                if (step > 0) turnActive = Math.Min(LongestTurn, turnActive + Math.Min(MaxStepGap, step));
                if (at > lastAt) lastAt = at;
            }
            int from = line.IndexOf("\"usage\":{", StringComparison.Ordinal);
            Match id = Answer.Match(line);
            if (from < 0 || !id.Success) continue;
            usage[id.Groups[1].Value] = new object[] {
                DayOf(at),
                Number(line, "input_tokens", from),
                Number(line, "output_tokens", from),
                Number(line, "cache_read_input_tokens", from) + Number(line, "cache_creation_input_tokens", from)
            };
        }
        close();
        foreach (var one in usage.Values) {
            Day day; string key = (string)one[0];
            if (!record.Days.TryGetValue(key, out day)) record.Days[key] = day = new Day();
            day.In += (long)one[1]; day.Out += (long)one[2]; day.Cached += (long)one[3];
        }
    }

    /// One record of Codex's: its turns with how long each took, and the tokens of each request.
    static void Codex(string path, Record record) {
        DateTime began = DateTime.MinValue;
        bool open = false, waiting = false;
        string lastTokenSig = "";
        foreach (string line in Lines(path)) {
            if (line.Length < 40) continue;
            if (record.Folder.Length == 0 && line.IndexOf("\"type\":\"session_meta\"", StringComparison.Ordinal) >= 0) {
                Match where = Folder.Match(line);
                if (where.Success) record.Folder = Unescaped(where.Groups[1].Value);
                continue;
            }
            bool noted = line.IndexOf("\"type\":\"event_msg\"", StringComparison.Ordinal) >= 0;
            if (!noted) {
                // the first thing that comes back after a turn began: how soon it answered
                if (waiting && line.IndexOf("\"type\":\"response_item\"", StringComparison.Ordinal) >= 0) {
                    DateTime first;
                    if (Time(Stamped, line, false, out first)) AddWait(Of(record, began), (first - began).TotalSeconds);
                    waiting = false;
                }
                continue;
            }
            DateTime at;
            if (!Time(Stamped, line, false, out at)) continue;
            if (line.IndexOf("\"type\":\"task_started\"", StringComparison.Ordinal) >= 0) {
                began = at; open = waiting = true;
                Day day = Of(record, at);
                day.Prompts++;
                day.Sessions.Add(record.Session);
                day.Hours[at.ToLocalTime().Hour]++;
            } else if (line.IndexOf("\"type\":\"task_complete\"", StringComparison.Ordinal) >= 0 || line.IndexOf("\"type\":\"turn_aborted\"", StringComparison.Ordinal) >= 0) {
                if (open) {
                    if (waiting) {
                        long ttft = Number(line, "time_to_first_token_ms");
                        if (ttft > 0) AddWait(Of(record, began), ttft / 1000.0);
                    }
                    long durMs = Number(line, "duration_ms");
                    double seconds = durMs > 0 ? durMs / 1000.0 : Math.Min(MaxStepGap, Math.Max(0, (at - began).TotalSeconds));
                    Of(record, began).Active += Math.Max(0, Math.Min(LongestTurn, seconds));
                }
                open = waiting = false;
            } else if (line.IndexOf("\"type\":\"token_count\"", StringComparison.Ordinal) >= 0) {
                int from = line.IndexOf("\"last_token_usage\":{", StringComparison.Ordinal);
                if (from < 0) continue;
                int end = line.IndexOf('}', from);
                string last = end > from ? line.Substring(from, end - from) : line.Substring(from);
                long given = Number(last, "input_tokens"), cached = Number(last, "cached_input_tokens"), output = Number(last, "output_tokens");
                int totFrom = line.IndexOf("\"total_token_usage\":{", StringComparison.Ordinal);
                long totIn = 0, totOut = 0;
                if (totFrom >= 0) {
                    int totEnd = line.IndexOf('}', totFrom);
                    string tot = totEnd > totFrom ? line.Substring(totFrom, totEnd - totFrom) : line.Substring(totFrom);
                    totIn = Number(tot, "input_tokens"); totOut = Number(tot, "output_tokens");
                }
                string sig = totIn + ":" + totOut + ":" + given + ":" + cached + ":" + output;
                if (sig == lastTokenSig) continue; // skip duplicate Codex token_count snapshots
                lastTokenSig = sig;
                Day day = Of(record, at);
                day.In += Math.Max(0, given - cached);
                day.Cached += cached;
                day.Out += output;
            }
        }
    }

    /// One record of Antigravity CLI's: user prompts, planner responses with token usage, and tool steps.
    static void Agy(string path, Record record) {
        DateTime began = DateTime.MinValue, lastAt = DateTime.MinValue;
        double turnActive = 0;
        bool open = false, waiting = false;
        Action close = () => {
            if (open) Of(record, began).Active += Math.Max(0, Math.Min(LongestTurn, turnActive));
            open = false; turnActive = 0;
        };
        foreach (string line in Lines(path)) {
            if (line.Length < 40) continue;
            bool isUser = line.IndexOf("\"type\":\"USER_INPUT\"", StringComparison.Ordinal) >= 0;
            bool isPlanner = !isUser && line.IndexOf("\"type\":\"PLANNER_RESPONSE\"", StringComparison.Ordinal) >= 0;
            bool isGeneric = !isUser && !isPlanner && line.IndexOf("\"type\":\"GENERIC\"", StringComparison.Ordinal) >= 0;
            if (!isUser && !isPlanner && !isGeneric) continue;
            DateTime at;
            if (!Time(CreatedAt, line, false, out at)) continue;
            if (isUser) {
                if (line.IndexOf("\"source\":\"", StringComparison.Ordinal) >= 0 && line.IndexOf("\"source\":\"USER_EXPLICIT\"", StringComparison.Ordinal) < 0) continue;
                close();
                began = lastAt = at; open = waiting = true; turnActive = 0;
                Day day = Of(record, at);
                day.Prompts++;
                day.Sessions.Add(record.Session);
                day.Hours[at.ToLocalTime().Hour]++;
                continue;
            }
            if (open) {
                if (isPlanner && waiting) { AddWait(Of(record, began), (at - began).TotalSeconds); waiting = false; }
                double step = (at - lastAt).TotalSeconds;
                if (step > 0) turnActive = Math.Min(LongestTurn, turnActive + Math.Min(MaxStepGap, step));
                if (at > lastAt) lastAt = at;
            }
            if (isPlanner) {
                long given = Number(line, "input_tokens");
                long cached = Number(line, "cache_read_tokens");
                long output = Number(line, "output_tokens");
                if (given > 0 || cached > 0 || output > 0) {
                    Day day = Of(record, at);
                    day.In += given;
                    day.Cached += cached;
                    day.Out += output;
                }
            }
        }
        close();
    }

    Dictionary<string, string> AgyWorkspaces(string home) {
        if (String.IsNullOrWhiteSpace(home)) return agyWorkspaces;
        string history = Path.Combine(home, "history.jsonl");
        try {
            if (!File.Exists(history)) return agyWorkspaces;
            var fi = new FileInfo(history);
            lock (gate) {
                if (fi.LastWriteTimeUtc.Ticks == agyHistoryStamp && fi.Length == agyHistoryLength) return agyWorkspaces;
            }
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in Lines(history)) {
                if (line.IndexOf("\"conversationId\"", StringComparison.Ordinal) < 0) continue;
                Match cid = AgyConvId.Match(line);
                if (!cid.Success) continue;
                Match ws = AgyWorkspace.Match(line);
                if (!ws.Success) continue;
                string folder = Unescaped(ws.Groups[1].Value);
                if (folder.Length > 0) map[cid.Groups[1].Value] = folder;
            }
            lock (gate) {
                agyHistoryStamp = fi.LastWriteTimeUtc.Ticks;
                agyHistoryLength = fi.Length;
                agyWorkspaces = map;
                return agyWorkspaces;
            }
        } catch (IOException) { return agyWorkspaces; } catch (UnauthorizedAccessException) { return agyWorkspaces; }
    }

    static void MergeMissingHistory(Record fresh, Record previous) {
        if (previous == null) return;
        if (fresh.Folder.Length == 0 && previous.Folder.Length > 0) fresh.Folder = previous.Folder;
        // If a transcript was compacted or truncated, keep earlier dates that were already captured in the ledger.
        foreach (var pair in previous.Days) {
            if (!fresh.Days.ContainsKey(pair.Key)) fresh.Days[pair.Key] = CloneDay(pair.Value, fresh.Session);
        }
    }

    static Day CloneDay(Day src, string session) {
        var copy = new Day { Prompts = src.Prompts, Active = src.Active, In = src.In, Out = src.Out, Cached = src.Cached };
        copy.Waits.AddRange(src.Waits);
        foreach (string s in src.Sessions) copy.Sessions.Add(s);
        if (copy.Sessions.Count == 0 && copy.Prompts > 0 && !String.IsNullOrEmpty(session)) copy.Sessions.Add(session);
        Array.Copy(src.Hours, copy.Hours, 24);
        return copy;
    }

    bool Summed(string path, string tool, string session, string fallbackFolder) {
        var file = new FileInfo(path);
        Record known;
        lock (gate) {
            if (records.TryGetValue(path, out known) && known.Stamp == file.LastWriteTimeUtc.Ticks && known.Length == file.Length) {
                if (known.Folder.Length == 0 && !String.IsNullOrEmpty(fallbackFolder)) { known.Folder = fallbackFolder; return true; }
                return false;
            }
        }
        var fresh = new Record { Stamp = file.LastWriteTimeUtc.Ticks, Length = file.Length, Tool = tool, Session = session, Folder = fallbackFolder ?? "" };
        try {
            if (tool == "claude") Claude(path, fresh);
            else if (tool == "codex") Codex(path, fresh);
            else if (tool == "agy") Agy(path, fresh);
        } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        lock (gate) {
            Record previous;
            if (records.TryGetValue(path, out previous) && file.Length < previous.Length) MergeMissingHistory(fresh, previous);
            else if (previous != null && fresh.Folder.Length == 0 && previous.Folder.Length > 0) fresh.Folder = previous.Folder;
            records[path] = fresh;
        }
        return true;
    }

    void Scan(DateTime since) {
        EnsureLedgerLoaded();
        bool dirty = false;
        try {
            string home = claudeHome != null ? claudeHome() : null;
            string projects = String.IsNullOrEmpty(home) ? null : Path.Combine(home, "projects");
            if (!String.IsNullOrEmpty(projects) && Directory.Exists(projects)) {
                foreach (string folder in Directory.GetDirectories(projects)) {
                    foreach (string path in Directory.GetFiles(folder, "*.jsonl")) {
                        if (File.GetLastWriteTimeUtc(path) >= since) {
                            if (Summed(path, "claude", Path.GetFileNameWithoutExtension(path), "")) dirty = true;
                        }
                    }
                }
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }

        try {
            string home = codexHome != null ? codexHome() : null;
            string sessions = String.IsNullOrEmpty(home) ? null : Path.Combine(home, "sessions");
            if (!String.IsNullOrEmpty(sessions) && Directory.Exists(sessions)) {
                foreach (string path in Directory.GetFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories)) {
                    if (File.GetLastWriteTimeUtc(path) >= since) {
                        if (Summed(path, "codex", Path.GetFileNameWithoutExtension(path), "")) dirty = true;
                    }
                }
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }

        try {
            string home = agyHome != null ? agyHome() : null;
            string brain = String.IsNullOrEmpty(home) ? null : Path.Combine(home, "brain");
            if (!String.IsNullOrEmpty(brain) && Directory.Exists(brain)) {
                var workspaces = AgyWorkspaces(home);
                foreach (string folder in Directory.GetDirectories(brain)) {
                    string id = Path.GetFileName(folder);
                    if (!UuidFolder.IsMatch(id)) continue;
                    string path = Path.Combine(folder, ".system_generated", "logs", "transcript.jsonl");
                    if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < since) continue;
                    string ws; workspaces.TryGetValue(id, out ws);
                    if (Summed(path, "agy", id, ws ?? "")) dirty = true;
                }
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }

        if (dirty) SaveLedger();
    }

    void EnsureLedgerLoaded() {
        lock (gate) {
            if (ledgerLoaded) return;
            ledgerLoaded = true;
        }
        if (String.IsNullOrWhiteSpace(ledgerPath) || !File.Exists(ledgerPath)) return;
        try {
            string raw = File.ReadAllText(ledgerPath, Encoding.UTF8);
            var js = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            var root = js.Deserialize<Dictionary<string, object>>(raw);
            if (root == null || ToInt(root, "version") != LedgerVersion) return;
            var map = AsMap(root, "records");
            if (map == null) return;
            var loaded = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in map) {
                var rMap = entry.Value as Dictionary<string, object>;
                if (rMap == null) continue;
                var rec = new Record {
                    Stamp = ToLong(rMap, "stamp"),
                    Length = ToLong(rMap, "length"),
                    Tool = ToText(rMap, "tool"),
                    Session = ToText(rMap, "session"),
                    Folder = ToText(rMap, "folder")
                };
                var daysMap = AsMap(rMap, "days");
                if (daysMap != null) {
                    foreach (var dEntry in daysMap) {
                        var dMap = dEntry.Value as Dictionary<string, object>;
                        if (dMap == null) continue;
                        var day = new Day {
                            Prompts = ToInt(dMap, "prompts"),
                            Active = ToDouble(dMap, "active"),
                            In = ToLong(dMap, "in"),
                            Out = ToLong(dMap, "out"),
                            Cached = ToLong(dMap, "cached")
                        };
                        if (day.Prompts > 0 && rec.Session.Length > 0) day.Sessions.Add(rec.Session);
                        object wObj;
                        if (dMap.TryGetValue("waits", out wObj)) {
                            var wList = wObj as System.Collections.IEnumerable;
                            if (wList != null && !(wObj is string)) {
                                foreach (object item in wList) {
                                    if (day.Waits.Count >= MaxWaitsPerDay) break;
                                    try { day.Waits.Add(Convert.ToDouble(item, CultureInfo.InvariantCulture)); } catch { }
                                }
                            }
                        }
                        object hObj;
                        if (dMap.TryGetValue("hours", out hObj)) {
                            var hList = hObj as System.Collections.IEnumerable;
                            if (hList != null && !(hObj is string)) {
                                int idx = 0;
                                foreach (object item in hList) {
                                    if (idx >= 24) break;
                                    try { day.Hours[idx] = Convert.ToInt32(item, CultureInfo.InvariantCulture); } catch { }
                                    idx++;
                                }
                            }
                        }
                        rec.Days[dEntry.Key] = day;
                    }
                }
                if (rec.Tool.Length > 0) loaded[entry.Key] = rec;
            }
            lock (gate) {
                foreach (var pair in loaded) if (!records.ContainsKey(pair.Key)) records[pair.Key] = pair.Value;
            }
        } catch { }
    }

    void SaveLedger() {
        if (String.IsNullOrWhiteSpace(ledgerPath)) return;
        string cutoff = DateTime.Now.Date.AddDays(-MaxLedgerDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var serialized = new Dictionary<string, object>();
        lock (gate) {
            foreach (var entry in records) {
                var rec = entry.Value;
                var keptDays = new Dictionary<string, object>();
                foreach (var d in rec.Days) {
                    if (String.CompareOrdinal(d.Key, cutoff) < 0) continue;
                    var day = d.Value;
                    if (day.Prompts == 0 && day.In == 0 && day.Out == 0 && day.Cached == 0 && day.Active <= 0) continue;
                    var dObj = new Dictionary<string, object> {
                        { "prompts", day.Prompts },
                        { "active", Math.Round(day.Active, 1) },
                        { "in", day.In },
                        { "out", day.Out },
                        { "cached", day.Cached }
                    };
                    if (day.Waits.Count > 0) dObj["waits"] = day.Waits.ToArray();
                    if (day.Hours.Any(h => h > 0)) dObj["hours"] = day.Hours;
                    keptDays[d.Key] = dObj;
                }
                if (keptDays.Count == 0 && rec.Stamp == 0) continue;
                serialized[entry.Key] = new Dictionary<string, object> {
                    { "stamp", rec.Stamp },
                    { "length", rec.Length },
                    { "tool", rec.Tool },
                    { "session", rec.Session },
                    { "folder", rec.Folder },
                    { "days", keptDays }
                };
            }
        }
        try {
            string dir = Path.GetDirectoryName(ledgerPath);
            if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var js = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            string payload = js.Serialize(new Dictionary<string, object> {
                { "version", LedgerVersion },
                { "updated", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds },
                { "records", serialized }
            });
            string tmp = ledgerPath + ".tmp";
            File.WriteAllText(tmp, payload, new UTF8Encoding(false));
            File.Copy(tmp, ledgerPath, true);
            try { File.Delete(tmp); } catch { }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static Dictionary<string, object> AsMap(Dictionary<string, object> map, string key) {
        object val;
        return map != null && map.TryGetValue(key, out val) ? val as Dictionary<string, object> : null;
    }
    static string ToText(Dictionary<string, object> map, string key) {
        object val;
        return map != null && map.TryGetValue(key, out val) && val != null ? Convert.ToString(val, CultureInfo.InvariantCulture) : "";
    }
    static int ToInt(Dictionary<string, object> map, string key) {
        object val;
        if (map == null || !map.TryGetValue(key, out val) || val == null) return 0;
        try { return Convert.ToInt32(val, CultureInfo.InvariantCulture); } catch { return 0; }
    }
    static long ToLong(Dictionary<string, object> map, string key) {
        object val;
        if (map == null || !map.TryGetValue(key, out val) || val == null) return 0;
        try { return Convert.ToInt64(val, CultureInfo.InvariantCulture); } catch { return 0; }
    }
    static double ToDouble(Dictionary<string, object> map, string key) {
        object val;
        if (map == null || !map.TryGetValue(key, out val) || val == null) return 0;
        try { return Convert.ToDouble(val, CultureInfo.InvariantCulture); } catch { return 0; }
    }

    static double? Middle(List<double> values) {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        return Math.Round(sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2, 1);
    }

    /// Pre-warms the ledger in the background after startup/update so the first stats query answers immediately.
    public void WarmAsync(int days = 30) {
        Task.Run(() => {
            try {
                DateTime since = DateTime.Now.Date.AddDays(1 - Math.Max(1, Math.Min(90, days))).ToUniversalTime().AddDays(-1);
                lock (scanGate) Scan(since);
            } catch { }
        });
    }

    /// The last `days` days (today the last of them), as the phone shows them. `named`: the name a folder goes by
    /// (its project's, where it is one).
    public Dictionary<string, object> Read(int days, Func<string, string> named, DateTime? now = null) {
        days = Math.Max(1, Math.Min(90, days));
        DateTime today = (now ?? DateTime.Now).Date, first = today.AddDays(1 - days);
        string todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (scanGate) Scan(first.ToUniversalTime().AddDays(-1));

        List<Record> snapshot;
        lock (gate) snapshot = records.Values.ToList();

        var byDay = new List<Dictionary<string, object>>();
        var tools = new Dictionary<string, long[]>(StringComparer.Ordinal);       // prompts, seconds, fresh+out tokens, cached
        var projects = new Dictionary<string, long[]>(StringComparer.Ordinal);    // prompts, seconds, fresh+out tokens, cached
        var hours = new int[24];
        var waits = new List<double>();

        for (DateTime date = first; date <= today; date = date.AddDays(1)) {
            string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            int prompts = 0; double active = 0; long given = 0, written = 0, cached = 0;
            var sessions = new HashSet<string>();
            var waited = new List<double>();
            var dayTools = new Dictionary<string, long[]>(StringComparer.Ordinal);
            foreach (Record record in snapshot) {
                Day day;
                if (!record.Days.TryGetValue(key, out day)) continue;
                prompts += day.Prompts; active += day.Active; given += day.In; written += day.Out; cached += day.Cached;
                foreach (string session in day.Sessions) sessions.Add(record.Tool + " " + session);
                waited.AddRange(day.Waits);
                for (int h = 0; h < 24; h++) hours[h] += day.Hours[h];

                if (!String.IsNullOrEmpty(record.Tool) && (day.Prompts > 0 || day.In > 0 || day.Out > 0 || day.Cached > 0)) {
                    long[] dt;
                    if (!dayTools.TryGetValue(record.Tool, out dt)) dayTools[record.Tool] = dt = new long[4];
                    dt[0] += day.Prompts; dt[1] += (long)Math.Round(day.Active); dt[2] += day.In + day.Out; dt[3] += day.Cached;
                }

                foreach (var group in new[] {
                    new KeyValuePair<Dictionary<string, long[]>, string>(tools, record.Tool),
                    new KeyValuePair<Dictionary<string, long[]>, string>(projects, named(record.Folder))
                }) {
                    if (String.IsNullOrEmpty(group.Value)) continue;
                    long[] sum;
                    if (!group.Key.TryGetValue(group.Value, out sum)) group.Key[group.Value] = sum = new long[4];
                    sum[0] += day.Prompts; sum[1] += (long)day.Active; sum[2] += day.In + day.Out; sum[3] += day.Cached;
                }
            }
            waits.AddRange(waited);
            var dayToolMap = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var dt in dayTools) {
                dayToolMap[dt.Key] = new Dictionary<string, object> {
                    { "prompts", dt.Value[0] },
                    { "active", dt.Value[1] },
                    { "tokens", dt.Value[2] },
                    { "cached", dt.Value[3] }
                };
            }
            byDay.Add(new Dictionary<string, object> {
                { "date", key }, { "prompts", prompts }, { "sessions", sessions.Count }, { "active", (long)Math.Round(active) },
                { "in", given }, { "out", written }, { "cached", cached }, { "wait", Middle(waited) }, { "tools", dayToolMap }
            });
        }

        // Compute continuous streak & history span across the whole ledger (up to today).
        var activeDates = new HashSet<string>(StringComparer.Ordinal);
        foreach (Record record in snapshot) {
            foreach (var pair in record.Days) {
                if (pair.Value.Prompts > 0 && String.CompareOrdinal(pair.Key, todayKey) <= 0) activeDates.Add(pair.Key);
            }
        }
        var sortedActive = activeDates.OrderBy(d => d, StringComparer.Ordinal).ToList();
        string firstDate = sortedActive.Count > 0 ? sortedActive[0] : "";
        int streak = 0, bestStreak = 0;
        DateTime cursor = activeDates.Contains(todayKey) ? today : today.AddDays(-1);
        while (activeDates.Contains(cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) {
            streak++;
            cursor = cursor.AddDays(-1);
        }
        DateTime prevParsed = DateTime.MinValue;
        int run = 0;
        foreach (string key in sortedActive) {
            DateTime parsed;
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) continue;
            run = (prevParsed != DateTime.MinValue && (parsed - prevParsed).Days == 1) ? run + 1 : 1;
            if (run > bestStreak) bestStreak = run;
            prevParsed = parsed;
        }

        var sources = new List<string> { "claude", "codex" };
        if (agyHome != null) sources.Add("agy");

        Func<Dictionary<string, long[]>, int, object[]> listed = (sums, most) => sums
            .OrderByDescending(p => p.Value[1]).ThenByDescending(p => p.Value[0])
            .Where(p => p.Value[0] > 0 || p.Value[2] > 0 || p.Value[3] > 0)
            .Take(most)
            .Select(p => (object)new Dictionary<string, object> {
                { "name", p.Key }, { "prompts", p.Value[0] }, { "active", p.Value[1] }, { "tokens", p.Value[2] }, { "cached", p.Value[3] }
            }).ToArray();

        return new Dictionary<string, object> {
            { "ready", true },
            { "days", byDay.ToArray() },
            { "tools", listed(tools, 4) },
            { "projects", listed(projects, 6) },
            { "hours", hours },
            { "wait", Middle(waits) },
            { "streak", streak },
            { "best_streak", bestStreak },
            { "first_date", firstDate },
            { "ledger_days", activeDates.Count },
            { "sources", sources.ToArray() },
            { "updated", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds }
        };
    }
}
}
