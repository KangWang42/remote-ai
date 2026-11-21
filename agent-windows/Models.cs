using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RemoteCli {
/// The models a team's member can be started with, as each chat tool lists them through its own structured mode
/// (`Chat.ListModels`): Claude Code's `initialize` answer (`models`), Codex's `model/list`, and what an ACP server such as
/// `agy acp` offers with a new session (`models.availableModels`, or a `configOptions` entry of category `model`).
/// Nothing is made up: a tool that lists nothing is offered with its own default only. A list is kept a while, so
/// that a phone that opens the form again does not start the tools again.
public sealed class Models {
    public sealed class Choice {
        public string Id = "", Label = "", DefaultEffort = "";
        public string[] Efforts = new string[0];
        public bool IsDefault;
    }
    public static readonly string[] Tools = { "codex", "claude", "agy" };
    public const int Most = 40, MostEfforts = 16;
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10), Retry = TimeSpan.FromMinutes(1);
    const int Patience = 15000;     // (a phone's request is held about 25 s by the relay)
    // what a model's name may be: letters, digits and the few marks the tools' names use ("gpt-5.1-codex",
    // "sonnet[1m]", "models/gemini-3-pro"); it is put on a command line and into requests as it is
    static readonly Regex Safe = new Regex(@"\A[A-Za-z0-9][A-Za-z0-9._:/\[\]-]{0,79}\z");
    static readonly Regex SafeEffort = new Regex(@"\A[a-z0-9][a-z0-9_-]{0,31}\z");
    public static bool Valid(string id) { return id != null && Safe.IsMatch(id); }
    public static bool ValidEffort(string level) { return level != null && SafeEffort.IsMatch(level); }
    public static string CleanEffort(string level) {
        string clean = (level ?? "").Trim().ToLowerInvariant();
        return ValidEffort(clean) ? clean : "";
    }
    public static string[] CleanEfforts(IEnumerable<string> found) {
        var seen = new HashSet<string>();
        var clean = new List<string>();
        foreach (string one in found ?? new string[0]) {
            string level = CleanEffort(one);
            if (level.Length == 0 || !seen.Add(level)) continue;
            clean.Add(level);
            if (clean.Count >= MostEfforts) break;
        }
        return clean.ToArray();
    }

    readonly Func<string, string> find;                                 // tool -> its program, null where it is not installed
    readonly Func<string, string, int, Choice[]> read;                  // tool, program, milliseconds -> its list, null where not known
    readonly object gate = new object();
    readonly Dictionary<string, Choice[]> kept = new Dictionary<string, Choice[]>();
    readonly Dictionary<string, DateTime> asked = new Dictionary<string, DateTime>();
    readonly Dictionary<string, Task> reading = new Dictionary<string, Task>();

    public Models(Func<string, string> find, Func<string, string, int, Choice[]> read) { this.find = find; this.read = read; }

    /// Each installed chat tool's models (`team_models`): those known, read again where they are old, waiting for
    /// a reading at most `wait` milliseconds. A tool that is not installed is not listed.
    public Dictionary<string, object> Listed(int wait = Patience) {
        var tasks = new List<Task>();
        var tools = Tools.Select(tool => new { tool, launcher = find(tool) }).Where(x => x.launcher != null).ToArray();
        lock (gate) {
            foreach (var one in tools) {
                Task running;
                if (reading.TryGetValue(one.tool, out running)) { tasks.Add(running); continue; }
                DateTime at;
                bool known = kept.ContainsKey(one.tool) && kept[one.tool] != null;
                if (asked.TryGetValue(one.tool, out at) && DateTime.UtcNow - at < (known ? Fresh : Retry)) continue;
                asked[one.tool] = DateTime.UtcNow;
                string tool = one.tool, launcher = one.launcher;
                var task = Task.Run(() => {
                    Choice[] found = null;
                    try { found = read(tool, launcher, Patience); } catch (Exception) { }
                    lock (gate) {
                        if (found != null) kept[tool] = Clean(found);
                        reading.Remove(tool);
                    }
                });
                reading[tool] = task;
                tasks.Add(task);
            }
        }
        if (tasks.Count > 0) try { Task.WaitAll(tasks.ToArray(), wait); } catch (AggregateException) { }
        var models = new Dictionary<string, object>();
        lock (gate) {
            foreach (var one in tools) {
                Choice[] list;
                models[one.tool] = (kept.TryGetValue(one.tool, out list) && list != null ? list : new Choice[0])
                    .Select(c => {
                        var shown = new Dictionary<string, object> { { "id", c.Id }, { "label", c.Label } };
                        if (c.Efforts != null && c.Efforts.Length > 0) shown["efforts"] = c.Efforts;
                        if (!String.IsNullOrEmpty(c.DefaultEffort)) shown["default_effort"] = c.DefaultEffort;
                        if (c.IsDefault) shown["is_default"] = true;
                        return shown;
                    }).ToArray();
            }
        }
        return new Dictionary<string, object> { { "models", models } };
    }

    /// A tool's list as it is offered: safe names only, each once, with a short name to show and any reasoning
    /// effort tiers the tool advertised for that model.
    public static Choice[] Clean(IEnumerable<Choice> found) {
        var seen = new HashSet<string>();
        var clean = new List<Choice>();
        bool defaultSeen = false;
        foreach (var one in found ?? new Choice[0]) {
            if (one == null || !Valid(one.Id) || !seen.Add(one.Id)) continue;
            string label = Regex.Replace((one.Label ?? "").Trim(), @"\s+", " ");
            string[] efforts = CleanEfforts(one.Efforts);
            string defaultEffort = CleanEffort(one.DefaultEffort);
            if (efforts.Length > 0 && defaultEffort.Length > 0 && Array.IndexOf(efforts, defaultEffort) < 0) defaultEffort = "";
            bool isDefault = one.IsDefault && !defaultSeen;
            if (isDefault) defaultSeen = true;
            clean.Add(new Choice {
                Id = one.Id,
                Label = label.Length == 0 ? one.Id : label.Length > 60 ? label.Substring(0, 60) : label,
                Efforts = efforts,
                DefaultEffort = defaultEffort,
                IsDefault = isDefault
            });
            if (clean.Count >= Most) break;
        }
        return clean.ToArray();
    }

    /// The models a team gives its members by tool, from what the phone sent: a known tool's safe name; an empty
    /// one is the tool's own default and is left out.
    public static Dictionary<string, string> Chosen(object given, IEnumerable<string> tools) {
        var chosen = new Dictionary<string, string>();
        var map = given as Dictionary<string, object>;
        if (given != null && map == null) throw new ArgumentException("模型选择无效");
        if (map == null) return chosen;
        var known = new HashSet<string>(tools);
        foreach (var pair in map) {
            string id = pair.Value == null ? "" : Convert.ToString(pair.Value).Trim();
            if (!known.Contains(pair.Key)) throw new ArgumentException("模型选择里有不在队伍中的 AI");
            if (id.Length == 0) continue;
            if (!Valid(id)) throw new ArgumentException("模型名称无效");
            chosen[pair.Key] = id;
        }
        return chosen;
    }

    /// The reasoning efforts a team gives its members by tool, from what the phone sent: a known tool's safe tier;
    /// an empty one is the tool's own default and is left out.
    public static Dictionary<string, string> ChosenEfforts(object given, IEnumerable<string> tools) {
        var chosen = new Dictionary<string, string>();
        var map = given as Dictionary<string, object>;
        if (given != null && map == null) throw new ArgumentException("思考深度选择无效");
        if (map == null) return chosen;
        var known = new HashSet<string>(tools);
        foreach (var pair in map) {
            string level = pair.Value == null ? "" : Convert.ToString(pair.Value).Trim().ToLowerInvariant();
            if (!known.Contains(pair.Key)) throw new ArgumentException("思考深度选择里有不在队伍中的 AI");
            if (level.Length == 0) continue;
            if (!ValidEffort(level)) throw new ArgumentException("思考深度无效");
            chosen[pair.Key] = level;
        }
        return chosen;
    }
}
}
