using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteCli {
/// How the owner's work with the tools went, in numbers, read from the tools' own records on this computer:
/// what was asked and when, how long the tools worked on it, how soon they first answered, and the tokens they
/// note for each answer. By day, by tool, by project and by hour of the day. Nothing that was said is kept or
/// passed on: numbers, dates, and the names of the folders the conversations are in.
///
/// Claude Code notes each message with its time and the usage of the answer it belongs to (several rows may carry
/// the same answer: it is counted once). Codex notes when a turn began, how long it took, and the tokens of each
/// request. A record is read anew only when it has changed.
public sealed class Stats {
    public sealed class Day {
        public int Prompts; public double Active; public long In, Out, Cached;
        public readonly List<double> Waits = new List<double>();
        public readonly HashSet<string> Sessions = new HashSet<string>();
    }
    sealed class Record {
        public long Stamp, Length; public string Tool = "", Session = "", Folder = "";
        public readonly Dictionary<string, Day> Days = new Dictionary<string, Day>();
        public readonly int[] Hours = new int[24];
    }
    public const double LongestTurn = 3 * 3600, LongestWait = 600;
    readonly Func<string> claudeHome, codexHome;
    readonly Dictionary<string, Record> records = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new object();
    public Stats(Func<string> claudeHome, Func<string> codexHome) { this.claudeHome = claudeHome; this.codexHome = codexHome; }

    static readonly Regex Stamped = new Regex("\"timestamp\":\"([0-9T:.+Z-]{19,40})\"", RegexOptions.Compiled);
    static readonly Regex Folder = new Regex("\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    static readonly Regex Answer = new Regex("\"id\":\"(msg_[A-Za-z0-9_-]+)\"", RegexOptions.Compiled);
    static long Number(string line, string name, int from = 0) {
        int at = line.IndexOf("\"" + name + "\":", from, StringComparison.Ordinal);
        if (at < 0) return 0;
        at += name.Length + 3; long value = 0; bool any = false;
        while (at < line.Length && line[at] >= '0' && line[at] <= '9') { value = value * 10 + (line[at] - '0'); at++; any = true; }
        return any ? value : 0;
    }
    static bool Time(string line, bool last, out DateTime at) {
        at = DateTime.MinValue;
        Match found = Stamped.Match(line);
        if (!found.Success) return false;
        if (last) { Match next; while ((next = found.NextMatch()).Success) found = next; }
        return DateTime.TryParse(found.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at);
    }
    static string Unescaped(string text) { return Regex.Replace(text, @"\\(.)", m => m.Groups[1].Value == "n" ? "\n" : m.Groups[1].Value); }
    static string DayOf(DateTime at) { return at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
    static Day Of(Record record, DateTime at) { string key = DayOf(at); Day day; if (!record.Days.TryGetValue(key, out day)) record.Days[key] = day = new Day(); return day; }
    static IEnumerable<string> Lines(string path) {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8)) { string line; while ((line = reader.ReadLine()) != null) yield return line; }
    }

    /// One record of Claude Code's: its prompts, the turns that followed, and the usage of each answer.
    static void Claude(string path, Record record) {
        var usage = new Dictionary<string, object[]>();        // an answer's id -> the day it was given, and its tokens
        DateTime began = DateTime.MinValue, ended = DateTime.MinValue; bool open = false, waiting = false;
        Action close = () => { if (open) Of(record, began).Active += Math.Max(0, Math.Min(LongestTurn, (ended - began).TotalSeconds)); open = false; };
        foreach (string line in Lines(path)) {
            if (line.Length < 40) continue;
            bool answer = line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) >= 0, asked = !answer && line.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) >= 0;
            if ((!answer && !asked) || line.IndexOf("\"isSidechain\":true", StringComparison.Ordinal) >= 0) continue;
            DateTime at;
            if (!Time(line, true, out at)) continue;
            if (record.Folder.Length == 0) { Match where = Folder.Match(line); if (where.Success) record.Folder = Unescaped(where.Groups[1].Value); }
            if (asked) {
                // what a tool gave back belongs to the turn that runs; what Claude Code notes for itself is nobody's prompt
                if (line.IndexOf("\"tool_use_id\"", StringComparison.Ordinal) >= 0) { if (open) ended = at; continue; }
                if (line.IndexOf("\"isMeta\":true", StringComparison.Ordinal) >= 0) continue;
                close();
                began = ended = at; open = waiting = true;
                Day day = Of(record, at); day.Prompts++; day.Sessions.Add(record.Session);
                record.Hours[at.ToLocalTime().Hour]++;
                continue;
            }
            if (open) {
                if (waiting) { double wait = (at - began).TotalSeconds; if (wait >= 0 && wait <= LongestWait) Of(record, began).Waits.Add(wait); waiting = false; }
                ended = at;
            }
            int from = line.IndexOf("\"usage\":{", StringComparison.Ordinal);
            Match id = Answer.Match(line);
            if (from < 0 || !id.Success) continue;
            usage[id.Groups[1].Value] = new object[] { DayOf(at), Number(line, "input_tokens", from), Number(line, "output_tokens", from),
                Number(line, "cache_read_input_tokens", from) + Number(line, "cache_creation_input_tokens", from) };
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
        DateTime began = DateTime.MinValue; bool open = false, waiting = false;
        foreach (string line in Lines(path)) {
            if (line.Length < 40) continue;
            if (record.Folder.Length == 0 && line.IndexOf("\"type\":\"session_meta\"", StringComparison.Ordinal) >= 0) { Match where = Folder.Match(line); if (where.Success) record.Folder = Unescaped(where.Groups[1].Value); continue; }
            bool noted = line.IndexOf("\"type\":\"event_msg\"", StringComparison.Ordinal) >= 0;
            if (!noted) {
                // the first thing that comes back after a turn began: how soon it answered
                if (waiting && line.IndexOf("\"type\":\"response_item\"", StringComparison.Ordinal) >= 0) {
                    DateTime first;
                    if (Time(line, false, out first)) { double wait = (first - began).TotalSeconds; if (wait >= 0 && wait <= LongestWait) Of(record, began).Waits.Add(wait); }
                    waiting = false;
                }
                continue;
            }
            DateTime at;
            if (!Time(line, false, out at)) continue;
            if (line.IndexOf("\"type\":\"task_started\"", StringComparison.Ordinal) >= 0) {
                began = at; open = waiting = true;
                Day day = Of(record, at); day.Prompts++; day.Sessions.Add(record.Session);
                record.Hours[at.ToLocalTime().Hour]++;
            } else if (line.IndexOf("\"type\":\"task_complete\"", StringComparison.Ordinal) >= 0 || line.IndexOf("\"type\":\"turn_aborted\"", StringComparison.Ordinal) >= 0) {
                if (open) Of(record, began).Active += Math.Max(0, Math.Min(LongestTurn, Number(line, "duration_ms") / 1000.0));
                open = waiting = false;
            } else if (line.IndexOf("\"type\":\"token_count\"", StringComparison.Ordinal) >= 0) {
                int from = line.IndexOf("\"last_token_usage\":{", StringComparison.Ordinal);
                if (from < 0) continue;
                int end = line.IndexOf('}', from);
                string last = end > from ? line.Substring(from, end - from) : line.Substring(from);
                long given = Number(last, "input_tokens"), cached = Number(last, "cached_input_tokens");
                Day day = Of(record, at); day.In += Math.Max(0, given - cached); day.Cached += cached; day.Out += Number(last, "output_tokens");
            }
        }
    }

    Record Summed(string path, string tool, string session) {
        var file = new FileInfo(path);
        Record known;
        lock (gate) if (records.TryGetValue(path, out known) && known.Stamp == file.LastWriteTimeUtc.Ticks && known.Length == file.Length) return known;
        var fresh = new Record { Stamp = file.LastWriteTimeUtc.Ticks, Length = file.Length, Tool = tool, Session = session };
        try { if (tool == "claude") Claude(path, fresh); else Codex(path, fresh); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        lock (gate) { records[path] = fresh; if (records.Count > 4000) foreach (string old in records.Keys.Take(1000).ToArray()) records.Remove(old); }
        return fresh;
    }
    IEnumerable<Record> Recent(DateTime since) {
        var found = new List<Record>();
        try {
            string projects = Path.Combine(claudeHome(), "projects");
            if (Directory.Exists(projects)) foreach (string folder in Directory.GetDirectories(projects)) foreach (string path in Directory.GetFiles(folder, "*.jsonl"))
                if (File.GetLastWriteTimeUtc(path) >= since) found.Add(Summed(path, "claude", Path.GetFileNameWithoutExtension(path)));
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try {
            string sessions = Path.Combine(codexHome(), "sessions");
            if (Directory.Exists(sessions)) foreach (string path in Directory.GetFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories))
                if (File.GetLastWriteTimeUtc(path) >= since) found.Add(Summed(path, "codex", Path.GetFileNameWithoutExtension(path)));
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return found;
    }
    static double? Middle(List<double> values) {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        return Math.Round(sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2, 1);
    }

    /// The last `days` days (today the last of them), as the phone shows them. `named`: the name a folder goes by
    /// (its project's, where it is one).
    public Dictionary<string, object> Read(int days, Func<string, string> named, DateTime? now = null) {
        days = Math.Max(1, Math.Min(90, days));
        DateTime today = (now ?? DateTime.Now).Date, first = today.AddDays(1 - days);
        var read = Recent(first.ToUniversalTime().AddDays(-1)).ToList();
        var byDay = new List<Dictionary<string, object>>();
        var tools = new Dictionary<string, long[]>(); var projects = new Dictionary<string, long[]>();       // prompts, seconds, tokens
        var hours = new int[24]; var waits = new List<double>();
        for (DateTime date = first; date <= today; date = date.AddDays(1)) {
            string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            int prompts = 0; double active = 0; long given = 0, written = 0, cached = 0; var sessions = new HashSet<string>(); var waited = new List<double>();
            foreach (Record record in read) {
                Day day;
                if (!record.Days.TryGetValue(key, out day)) continue;
                prompts += day.Prompts; active += day.Active; given += day.In; written += day.Out; cached += day.Cached;
                foreach (string session in day.Sessions) sessions.Add(record.Tool + " " + session);
                waited.AddRange(day.Waits);
                foreach (var group in new[] { new KeyValuePair<Dictionary<string, long[]>, string>(tools, record.Tool), new KeyValuePair<Dictionary<string, long[]>, string>(projects, named(record.Folder)) }) {
                    if (String.IsNullOrEmpty(group.Value)) continue;
                    long[] sum; if (!group.Key.TryGetValue(group.Value, out sum)) group.Key[group.Value] = sum = new long[3];
                    sum[0] += day.Prompts; sum[1] += (long)day.Active; sum[2] += day.In + day.Out;
                }
            }
            waits.AddRange(waited);
            byDay.Add(new Dictionary<string, object> { { "date", key }, { "prompts", prompts }, { "sessions", sessions.Count }, { "active", (long)Math.Round(active) },
                { "in", given }, { "out", written }, { "cached", cached }, { "wait", Middle(waited) } });
        }
        // (the hours of the day are of the whole span that was read, by the records that have something in it)
        foreach (Record record in read) if (record.Days.Keys.Any(key => String.CompareOrdinal(key, first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) >= 0)) for (int hour = 0; hour < 24; hour++) hours[hour] += record.Hours[hour];
        Func<Dictionary<string, long[]>, int, object[]> listed = (sums, most) => sums.OrderByDescending(p => p.Value[1]).ThenByDescending(p => p.Value[0]).Where(p => p.Value[0] > 0 || p.Value[2] > 0).Take(most)
            .Select(p => (object)new Dictionary<string, object> { { "name", p.Key }, { "prompts", p.Value[0] }, { "active", p.Value[1] }, { "tokens", p.Value[2] } }).ToArray();
        return new Dictionary<string, object> { { "ready", true }, { "days", byDay.ToArray() }, { "tools", listed(tools, 4) }, { "projects", listed(projects, 6) }, { "hours", hours }, { "wait", Middle(waits) },
            { "updated", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds } };
    }
}
}
