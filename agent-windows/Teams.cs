using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// A piece of work several AIs do together on this computer (协作任务). Each member is a conversation the phone runs
/// (a chat, Chats.cs), started by this program in the task's project (or an isolated git worktree for `compare`): its
/// owner can open, watch, stop, answer, interject (`Say`), or continue any of them on the phone.
/// Modes:
/// - review (写审): optional read-only discussion (`discuss`), then writer implements, reviewer inspects diff read-only
///   and answers REVIEW: PASS or REVIEW: CHANGES, looping until pass or rounds spent.
/// - lead (领队): optional read-only discussion (`discuss`) where members propose plans before the leader synthesizes
///   and assigns (`ASSIGN <member>: <task>` / `DONE: <summary>`), weighing each tool's remaining quota (`Quotas.cs`).
///   A DONE is not taken at its word: it opens a turn of acceptance (phase `verify`) in which the leader inspects the
///   changes and runs the checks itself, or has another member check or fix; only `VERIFIED: <evidence>` with `DONE`
///   in that turn ends the task as done. Rounds running out pause it with the fixes the leader still wants kept
///   (“继续” adds a round), never `done`. `rounds` 0 leaves the number of rounds to the leader (自由轮数, up to
///   `MaxTotalRounds`): each round it sets that round's aim by what it has seen, and ends once the goal is met.
/// - compare (并行比稿): 2–3 distinct AIs work in parallel inside isolated Git worktrees (`Worktrees.cs`); once done,
///   the task enters `choose` so the owner can compare diffs, ask for refinements (`Say`), and pick one to apply (`Pick`)
///   or discard all, cleaning up temporary worktrees automatically.
public sealed class Teams {
    public const int Most = 10, MostRounds = 10, MaxTotalRounds = 20;
    public static readonly string[] Modes = { "review", "lead", "compare" };
    public sealed class Step { public long At; public string Who = "", Text = ""; }
    public sealed class Team {
        public string Id = "", Dir = "", Mode = "", Goal = "", Rules = "", State = "running", Note = "", Phase = "", Picked = "";
        public bool Discuss;
        public string[] Tools = new string[0];          // review: writer, reviewer; lead: leader + members; compare: candidates
        public RosterMember[] Roster = new RosterMember[0];
        public bool AutoRoster;
        public string LeaderInput = "";
        /// The rounds set (0: free, lead only, `Cap`), and the one under way.
        public int Rounds = 3, Round = 1;
        public bool Free { get { return Rounds == 0; } }
        /// The round after which no more go out without the owner: the rounds set, or the most any task may have.
        public int Cap { get { return Free ? MaxTotalRounds : Rounds; } }
        public long Created;
        public List<Step> Log = new List<Step>();
        /// Each member's conversation: role -> terminal ("writer", "reviewer", "leader", "discuss:<tool>", or tool name).
        public Dictionary<string, string> Members = new Dictionary<string, string>();
        /// Whose turn is awaited (roles), and what those who answered said in this round (lead / discuss).
        public List<string> Awaited = new List<string>();
        public Dictionary<string, string> Answers = new Dictionary<string, string>();
        internal readonly HashSet<string> Finishing = new HashSet<string>();
        /// Compare mode: per-tool worktree path, base commit SHA, summary, stat line, and diff text.
        public Dictionary<string, string> Worktrees = new Dictionary<string, string>();
        public Dictionary<string, string> BaseCommits = new Dictionary<string, string>();
        public Dictionary<string, string> Summaries = new Dictionary<string, string>();
        public Dictionary<string, string> Stats = new Dictionary<string, string>();
        public Dictionary<string, string> Diffs = new Dictionary<string, string>();
        /// Tools that failed/exhausted quota in this task (tool -> brief reason), and each role's last dispatched prompt.
        public Dictionary<string, string> Failed = new Dictionary<string, string>();
        public Dictionary<string, string> Pending = new Dictionary<string, string>();
        public bool Reminded;
        /// Lead: the rounds are spent and the leader's acceptance under way is the last one (no more tasks go out).
        public bool Last;
        /// The rounds are spent while the work still wants more: it waits, paused, for its owner to add a round
        /// ("继续"), and `Held` keeps what goes out then (role -> the leader's task for a member, or the review
        /// findings for the writer); with nothing kept the leader is asked to go on.
        public bool Spent;
        public Dictionary<string, string> Held = new Dictionary<string, string>();
        /// How often each member was sent back in this round because it handed in a note of progress, not a result.
        internal readonly Dictionary<string, int> Nudged = new Dictionary<string, int>();
        /// The model each tool's members are begun with (tool -> the tool's name of it); a tool not named uses its default.
        public Dictionary<string, string> Models = new Dictionary<string, string>();
        /// The reasoning effort each tool's members are begun with (tool -> CLI effort tier); a tool not named uses its default.
        public Dictionary<string, string> Efforts = new Dictionary<string, string>();
        /// Each role's conversation in its tool, once known (role -> Seat): it outlives the terminal, for the history.
        public Dictionary<string, Seat> Sessions = new Dictionary<string, Seat>();
        /// Every conversation that was ever a member, replaced ones too: they are listed under the task.
        public List<Seat> Past = new List<Seat>();
        /// Every terminal this task began, replaced members' too: all of them are closed when the task is removed.
        public List<string> Opened = new List<string>();
        internal Team Copy() {
            var copy = (Team)MemberwiseClone();
            copy.Log = Log.ToList(); copy.Members = new Dictionary<string, string>(Members); copy.Awaited = Awaited.ToList();
            copy.Answers = new Dictionary<string, string>(Answers); copy.Tools = Tools.ToArray(); copy.Roster = Roster.ToArray();
            copy.Worktrees = new Dictionary<string, string>(Worktrees); copy.BaseCommits = new Dictionary<string, string>(BaseCommits);
            copy.Summaries = new Dictionary<string, string>(Summaries); copy.Stats = new Dictionary<string, string>(Stats);
            copy.Diffs = new Dictionary<string, string>(Diffs);
            copy.Failed = new Dictionary<string, string>(Failed); copy.Pending = new Dictionary<string, string>(Pending); copy.Held = new Dictionary<string, string>(Held);
            copy.Models = new Dictionary<string, string>(Models); copy.Efforts = new Dictionary<string, string>(Efforts);
            copy.Sessions = new Dictionary<string, Seat>(Sessions); copy.Past = Past.ToList(); copy.Opened = Opened.ToList();
            return copy;
        }
        /// When anything last happened in it, in Unix seconds.
        public long Updated { get { return (Log.Count > 0 ? Log[Log.Count - 1].At : Created) / 1000; } }
    }
    /// A member's conversation in its tool.
    public sealed class Seat { public string Tool = "", Id = ""; }
    public sealed class RosterMember {
        public readonly string Role, Tool, Model, Effort;
        public RosterMember(string role, string tool, string model, string effort = "") {
            Role = role; Tool = tool; Model = model ?? ""; Effort = effort ?? "";
        }
        public Dictionary<string, string> Shown() {
            var shown = new Dictionary<string, string> { { "role", Role }, { "tool", Tool }, { "model", Model } };
            if (Effort.Length > 0) shown["effort"] = Effort;
            return shown;
        }
    }
    static readonly string[] MemberRoles = { "member-1", "member-2", "member-3" };
    /// A member's conversation to begin: its tool, the project, the first words, read only or not, the folder where it
    /// is not the project's own (a worktree, `compare`), the model and reasoning effort ("" for the tool's default).
    public sealed class Opening { public string Tool = "", Project = "", Words = "", Folder = "", Model = "", Effort = ""; public bool Read; }
    /// What the program does for a team: begin a member's conversation, tell it something, stop or close one, read project
    /// changes, read quota limits, list available chat tools, and manage Git worktrees for `compare`.
    public sealed class Hands {
        public Func<Opening, string> Begin;                                      // -> terminal
        public Action<string, string> Say;                                       // terminal, words
        public Action<string> Stop;                                              // terminal
        public Action<string> Close;                                             // terminal: ends it; its conversation stays in the tool's history
        public Func<string, string> Changes;                                     // project -> what changed, in words
        public Func<string, Quotas.Window[]> Limits;                             // tool -> its main windows still running
        public Func<string[]> Available;                                         // chat-capable tools installed on this computer
        public Func<string, string, string, Worktrees.Created> MakeWorktree;     // project, teamId, tool -> Created
        public Func<string, string, string> WorktreeDiff;                        // worktreePath, baseCommit -> diff
        public Func<string, string, string> WorktreeStat;                        // worktreePath, baseCommit -> stat
        public Func<string, string, string, string> ApplyWorktree;               // project, worktreePath, baseCommit -> summary
        public Action<string, string> CleanWorktrees;                            // project, teamId
    }

    readonly string file;
    readonly Hands hands;
    readonly object gate = new object();
    int opening;
    readonly List<Team> items = new List<Team>();
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
    public event Action Changed;

    public Teams(string file, Hands hands) {
        this.file = file; this.hands = hands;
        if (file == null || !File.Exists(file)) return;
        try {
            string text = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser));
            foreach (object one in (IEnumerable)json.DeserializeObject(text)) {
                var team = Read(one as Dictionary<string, object>);
                if (team == null) continue;
                // its members' turns were cut when this program stopped: it waits for its owner
                if (team.State == "running") { team.State = "paused"; team.Note = "电脑端重新启动，成员这一轮被打断：点“继续”接着做"; }
                items.Add(team);
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (CryptographicException) { } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (InvalidCastException) { }
    }
    Team Read(Dictionary<string, object> kept) {
        if (kept == null) return null;
        Func<string, string> word = name => { object value; return kept.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; };
        Func<string, IEnumerable<object>> many = name => { object value; return kept.TryGetValue(name, out value) && value is IEnumerable && !(value is string) ? ((IEnumerable)value).Cast<object>() : new object[0]; };
        Func<string, Dictionary<string, string>> map = name => { object value; var d = kept.TryGetValue(name, out value) ? value as Dictionary<string, object> : null; return d == null ? new Dictionary<string, string>() : d.ToDictionary(p => p.Key, p => Convert.ToString(p.Value)); };
        int number;
        var team = new Team {
            Id = word("id"), Dir = word("dir"), Mode = word("mode"), Goal = word("goal"), Rules = word("rules"),
            State = word("state"), Note = word("note"), Phase = word("phase"), Picked = word("picked"),
            Discuss = String.Equals(word("discuss"), "True", StringComparison.OrdinalIgnoreCase),
            AutoRoster = String.Equals(word("auto_roster"), "True", StringComparison.OrdinalIgnoreCase),
            LeaderInput = word("leader_input"),
            Reminded = String.Equals(word("reminded"), "True", StringComparison.OrdinalIgnoreCase),
            Last = String.Equals(word("last"), "True", StringComparison.OrdinalIgnoreCase),
            Spent = String.Equals(word("spent"), "True", StringComparison.OrdinalIgnoreCase), Held = map("held"),
            Tools = many("tools").Select(Convert.ToString).ToArray(),
            Members = map("members"), Answers = map("answers"), Awaited = many("awaited").Select(Convert.ToString).ToList(),
            Worktrees = map("worktrees"), BaseCommits = map("base_commits"),
            Summaries = map("summaries"), Stats = map("stats"), Diffs = map("diffs"),
            Failed = map("failed"), Pending = map("pending"),
            Models = map("models").Where(p => RemoteCli.Models.Valid(p.Value)).ToDictionary(p => p.Key, p => p.Value),
            Efforts = map("efforts").Select(p => new KeyValuePair<string, string>(p.Key, RemoteCli.Models.CleanEffort(p.Value))).Where(p => p.Value.Length > 0).ToDictionary(p => p.Key, p => p.Value),
            Past = many("past").Select(Convert.ToString).Select(SeatOf).Where(s => s != null).ToList()
        };
        // Retain the known terminals of older saved tasks even if those members are replaced after upgrading.
        team.Opened = many("opened").Select(Convert.ToString).Concat(team.Members.Values)
            .Where(t => !String.IsNullOrEmpty(t)).Distinct().ToList();
        object roster;
        if (kept.TryGetValue("roster", out roster) && roster != null)
            team.Roster = ReadRoster(roster, team.AutoRoster, true);
        foreach (var pair in map("sessions")) { var seat = SeatOf(pair.Value); if (seat != null) team.Sessions[pair.Key] = seat; }
        team.Rounds = Int32.TryParse(word("rounds"), out number) ? number : 3; team.Round = Int32.TryParse(word("round"), out number) ? number : 1;
        long created; team.Created = Int64.TryParse(word("created"), out created) ? created : 0;
        foreach (var step in many("log").OfType<Dictionary<string, object>>()) { long at; Int64.TryParse(Convert.ToString(step["at"]), out at); team.Log.Add(new Step { At = at, Who = Convert.ToString(step["who"]), Text = Convert.ToString(step["text"]) }); }
        return Regex.IsMatch(team.Id, @"\A[a-f0-9]{16,32}\z") && Array.IndexOf(Modes, team.Mode) >= 0 ? team : null;
    }
    void Save() {
        if (file == null) return;
        var all = items.Select(t => new Dictionary<string, object> {
            { "id", t.Id }, { "dir", t.Dir }, { "mode", t.Mode }, { "goal", t.Goal }, { "rules", t.Rules },
            { "state", t.State }, { "note", t.Note }, { "phase", t.Phase }, { "picked", t.Picked },
            { "discuss", t.Discuss }, { "reminded", t.Reminded }, { "last", t.Last }, { "spent", t.Spent }, { "held", t.Held },
            { "tools", t.Tools }, { "rounds", t.Rounds }, { "round", t.Round }, { "created", t.Created },
            { "roster", t.Roster.Length == 0 ? null : t.Roster.Select(member => member.Shown()).ToArray() }, { "auto_roster", t.AutoRoster },
            { "leader_input", t.LeaderInput },
            { "members", t.Members }, { "awaited", t.Awaited }, { "answers", t.Answers },
            { "worktrees", t.Worktrees }, { "base_commits", t.BaseCommits },
            { "summaries", t.Summaries }, { "stats", t.Stats }, { "diffs", t.Diffs },
            { "failed", t.Failed }, { "pending", t.Pending }, { "models", t.Models }, { "efforts", t.Efforts },
            { "sessions", t.Sessions.ToDictionary(p => p.Key, p => p.Value.Tool + ":" + p.Value.Id) }, { "past", t.Past.Select(s => s.Tool + ":" + s.Id).ToArray() }, { "opened", t.Opened },
            { "log", t.Log.Select(s => new Dictionary<string, object> { { "at", s.At }, { "who", s.Who }, { "text", s.Text } }).ToArray() }
        }).ToArray();
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string fresh = file + ".new";
            File.WriteAllBytes(fresh, ProtectedData.Protect(Encoding.UTF8.GetBytes(json.Serialize(all)), null, DataProtectionScope.CurrentUser));
            if (File.Exists(file)) File.Replace(fresh, file, null); else File.Move(fresh, file);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    void Tell() { var told = Changed; if (told != null) try { told(); } catch (Exception) { } }
    static Seat SeatOf(string kept) {
        int cut = (kept ?? "").IndexOf(':');
        if (cut <= 0) return null;
        var seat = new Seat { Tool = kept.Substring(0, cut), Id = kept.Substring(cut + 1) };
        return RemoteCli.Tool.Of(seat.Tool) != null && Regex.IsMatch(seat.Id, @"\A[A-Za-z0-9_-]{8,64}\z") ? seat : null;
    }

    public Team[] All() { lock (gate) return items.Select(t => t.Copy()).ToArray(); }
    /// As the phone is shown it.
    public static Dictionary<string, object> Shown(Team t) {
        var shown = new Dictionary<string, object> {
            { "id", t.Id }, { "dir", t.Dir }, { "mode", t.Mode }, { "goal", t.Goal }, { "rules", t.Rules },
            { "state", t.State }, { "note", t.Note }, { "phase", t.Phase }, { "discuss", t.Discuss }, { "picked", t.Picked },
            { "tools", t.Tools }, { "rounds", t.Rounds }, { "round", t.Round }, { "created", t.Created }, { "models", t.Models },
            { "members", t.Members }, { "awaited", t.Awaited.ToArray() }, { "failed", t.Failed },
            { "member_sessions", t.Sessions.ToDictionary(p => p.Key, p => (object)new Dictionary<string, object> { { "id", p.Value.Id }, { "tool", p.Value.Tool } }) },
            { "log", t.Log.Skip(Math.Max(0, t.Log.Count - 80)).Select(s => new Dictionary<string, object> { { "at", s.At }, { "who", s.Who }, { "text", s.Text } }).ToArray() }
        };
        if (t.Last) shown["last"] = true;
        if (t.Efforts.Count > 0) shown["efforts"] = new Dictionary<string, string>(t.Efforts);
        if (t.Roster.Length > 0) {
            shown["roster"] = t.Roster.Select(member => member.Shown()).ToArray();
            shown["auto_roster"] = t.AutoRoster;
        }
        if (t.Mode == "compare") {
            shown["candidates"] = t.Tools.Select(tool => new Dictionary<string, object> {
                { "tool", tool },
                { "summary", t.Summaries.ContainsKey(tool) ? t.Summaries[tool] : "" },
                { "stat", t.Stats.ContainsKey(tool) ? t.Stats[tool] : "" },
                { "diff", t.Diffs.ContainsKey(tool) ? Cut(t.Diffs[tool], 24000) : "" },
                { "ready", t.Worktrees.ContainsKey(tool) }
            }).ToArray();
        }
        return shown;
    }
    /// The tasks in short, for the lists (`team_cards`): newest first; `confirm` where a member whose turn is awaited
    /// waits for a confirmation (`asking`: terminal -> whether it does).
    public Dictionary<string, object>[] Cards(Func<string, bool> asking) {
        var cards = new List<KeyValuePair<Dictionary<string, object>, string[]>>();     // each card, and the terminals it waits for
        lock (gate) {
            foreach (var t in items.OrderByDescending(t => t.Updated)) {
                var card = new Dictionary<string, object> {
                    { "id", t.Id }, { "dir", t.Dir }, { "goal", Short(t.Goal, 200) }, { "mode", t.Mode }, { "state", t.State }, { "phase", t.Phase },
                    { "note", Short(t.Note, 200) }, { "updated", t.Updated }, { "awaited", t.Awaited.ToArray() }, { "tools", t.Tools.ToArray() },
                    { "models", new Dictionary<string, string>(t.Models) } };
                if (t.Efforts.Count > 0) card["efforts"] = new Dictionary<string, string>(t.Efforts);
                if (t.Roster.Length > 0) {
                    card["roster"] = t.Roster.Select(member => member.Shown()).ToArray();
                    card["auto_roster"] = t.AutoRoster;
                }
                cards.Add(new KeyValuePair<Dictionary<string, object>, string[]>(card,
                    t.State == "running" ? t.Awaited.Where(t.Members.ContainsKey).Select(role => t.Members[role]).ToArray() : new string[0]));
            }
        }
        foreach (var card in cards) if (asking != null && card.Value.Any(asking)) card.Key["confirm"] = true;
        return cards.Select(card => card.Key).ToArray();
    }
    /// The task a terminal is a member of, else the one its conversation is or was a member of; "" for none.
    public string TeamOf(string terminal, string tool, string session) {
        lock (gate) { var team = items.FirstOrDefault(t => t.Members.ContainsValue(terminal)); if (team != null) return team.Id; }
        return String.IsNullOrEmpty(session) ? "" : TeamOf(tool, session);
    }
    /// The task a conversation is or was a member of, "" for none: listed under it while the task is kept.
    public string TeamOf(string tool, string session) {
        lock (gate) {
            var team = items.FirstOrDefault(t => t.Past.Any(s => s.Tool == tool && String.Equals(s.Id, session, StringComparison.OrdinalIgnoreCase)));
            return team == null ? "" : team.Id;
        }
    }
    /// A member's terminal says which conversation of its tool it runs: kept with the task, also once the member is
    /// replaced or its terminal is gone (tasks kept from before this was known learn it from terminals still listed).
    public void Learn(string terminal, string tool, string session) {
        if (String.IsNullOrEmpty(session) || SeatOf(tool + ":" + session) == null) return;
        lock (gate) {
            var team = items.FirstOrDefault(t => t.Members.ContainsValue(terminal));
            if (team == null) return;
            string role = team.Members.First(p => p.Value == terminal).Key;
            Seat had;
            if (team.Sessions.TryGetValue(role, out had) && had.Tool == tool && had.Id == session) return;
            var seat = new Seat { Tool = tool, Id = session };
            team.Sessions[role] = seat;
            if (!team.Past.Any(s => s.Tool == tool && s.Id == session)) team.Past.Add(seat);
            if (team.Past.Count > 60) team.Past.RemoveRange(0, team.Past.Count - 60);
            Save();
        }
    }

    // ---- what the members are told (read by the models: changed only together with runs on the real tools)
    public static string Label(string tool) { return RemoteCli.Tool.LabelOf(tool); }
    static readonly Dictionary<string, string> Strengths = new Dictionary<string, string> {
        { "codex", "Sol 适合日常实现与任务组织，Astra 适合复杂排查和深度验收，Terra 适合范围清楚的快改与测试" },
        { "claude", "Sonnet 适合常规实现、重构与写作，Opus 适合复杂设计和审查，Haiku 适合快速整理与提取" },
        { "agy", "若使用 Gemini，Neon 和 Carbon 适合复杂代码、界面和多份材料，Argon 兼顾推理与速度，Hydrogen 适合日常实现和检查" } };
    /// Whether any main quota window has reached 100%.
    public static bool IsExhausted(Quotas.Window[] windows) { return windows != null && windows.Any(w => Math.Round(w.Used) >= 100); }
    static readonly Regex QuotaErrorPattern = new Regex(@"(hit your [\w\s-]{0,24}limit|rate[\s_-]*limit|usage[\s_-]*limit|quota|out of credits|credit balance|too many requests|\b429\b|overloaded|额度|限额|已达上限|额度用尽|额度已用完|额度用完)", RegexOptions.IgnoreCase);
    /// Whether a member's failure message indicates quota or rate-limit exhaustion.
    public static bool IsQuotaError(string text) { return !String.IsNullOrWhiteSpace(text) && QuotaErrorPattern.IsMatch(text); }
    /// A tool's limits in words for the leader; 80%+ is tight and 100% is exhausted.
    public static string Room(Quotas.Window[] windows) {
        if (windows == null || windows.Length == 0) return "额度未知";
        var said = windows.Select(w => (w.Name == "five_hour" ? "5 小时窗口" : w.Name == "seven_day" ? "本周" : w.Name) + "已用 " + Math.Round(w.Used) + "%"
            + (w.Resets > 0 ? "（" + When(w.Resets) + " 重置）" : "")).ToArray();
        string tag = IsExhausted(windows) ? "，已用尽（" + Barred + "）" : windows.Any(w => Math.Round(w.Used) >= 80) ? "，紧张" : "";
        return "额度：" + String.Join("，", said) + tag;
    }
    /// The mark on a tool's room that no work may go to it (spent or failed in this task): `AssignRoster` drops such lines.
    const string Barred = "不可派工";
    /// The tools a self-forming leader may name in an `ASSIGN`: installed, not failed and not spent, as `Rooms` found them.
    static string[] Usable(IDictionary<string, string> rooms) {
        return rooms.Where(p => !p.Value.Contains(Barred)).Select(p => p.Key).ToArray();
    }
    static string When(long seconds) {
        DateTime at = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
        return at.ToString(at.Date == DateTime.Now.Date ? "HH:mm" : "M月d日 HH:mm", CultureInfo.InvariantCulture);
    }
    Quotas.Window[] SafeLimits(string tool) {
        if (hands.Limits == null) return null;
        try { return hands.Limits(tool); } catch (Exception) { return null; }
    }
    Dictionary<string, string> Rooms(Team t) {
        var rooms = new Dictionary<string, string>();
        Dictionary<string, string> failed;
        string[] list;
        lock (gate) { failed = new Dictionary<string, string>(t.Failed); list = t.Tools.Distinct().ToArray(); }
        if (t.AutoRoster && hands.Available != null) list = list.Union(hands.Available() ?? new string[0]).ToArray();
        if (hands.Limits == null && failed.Count == 0 && !t.AutoRoster) return rooms;
        foreach (string tool in list) {
            string why;
            bool isFailed = failed.TryGetValue(tool, out why);
            if (hands.Limits != null) {
                string room = Room(SafeLimits(tool));
                rooms[tool] = isFailed ? room + "；当前不可用（" + why + "，" + Barred + "）" : room;
            } else if (isFailed) {
                rooms[tool] = "不可用（" + why + "，" + Barred + "）";
            } else if (t.AutoRoster) rooms[tool] = "额度未知";
        }
        return rooms;
    }
    /// Picks the best available backup AI when `failedTool` fails, excluding `avoidTool`, failed tools and exhausted tools.
    string FallbackTool(Team team, string failedTool, string avoidTool = "") {
        var candidates = new List<string>();
        Dictionary<string, string> failed;
        string[] current;
        lock (gate) { failed = new Dictionary<string, string>(team.Failed); current = team.Tools.ToArray(); }
        foreach (string t in current) if (!String.IsNullOrEmpty(t) && t != failedTool && t != avoidTool && !candidates.Contains(t)) candidates.Add(t);
        if (hands.Available != null) {
            try {
                foreach (string t in hands.Available() ?? new string[0])
                    if (!String.IsNullOrEmpty(t) && t != failedTool && t != avoidTool && !candidates.Contains(t)) candidates.Add(t);
            } catch (Exception) { }
        }
        var usable = new List<Tuple<string, double, int>>();
        for (int i = 0; i < candidates.Count; i++) {
            string t = candidates[i];
            if (failed.ContainsKey(t)) continue;
            var w = SafeLimits(t);
            if (IsExhausted(w)) continue;
            double score = w != null && w.Length > 0 ? w.Max(one => one.Used) : 50.0;
            usable.Add(Tuple.Create(t, score, i));
        }
        return usable.OrderBy(x => x.Item2).ThenBy(x => x.Item3).Select(x => x.Item1).FirstOrDefault();
    }
    static string ExtraRules(Team t) { return String.IsNullOrEmpty(t.Rules) ? "" : "\n\n【分工偏好】\n" + Cut(t.Rules, 1000); }
    static string RoomsBlock(Team t, IDictionary<string, string> rooms) {
        // a self-forming leader may take on any installed tool, so it sees all of them
        var tools = t.Tools.Concat(t.AutoRoster ? rooms.Keys : Enumerable.Empty<string>()).Distinct();
        return rooms.Count > 0 ? "\n\n## 当前额度\n" + String.Join("\n", tools.Where(rooms.ContainsKey).Select(tool => "- " + Label(tool) + (tool == t.Tools[0] ? "（你）" : "") + "：" + rooms[tool])) : "";
    }
    public static string DiscussFirst(Team t, string tool) {
        return "【协作任务 · 方案讨论（只读）】目标：" + t.Goal + ExtraRules(t)
            + "\n\n正式动手修改代码前，各位 AI 先只读查看相关代码并简明提出自己的方案建议（3–6 条要点：核心思路、涉及文件、潜在风险与边界条件）。不要修改任何文件。";
    }
    public static string WriterFirst(Team t, string discussion = "") {
        return "【协作任务 · 写】" + t.Goal + ExtraRules(t)
            + (String.IsNullOrEmpty(discussion) ? "" : "\n\n## 前期方案讨论汇总\n" + Cut(discussion, 12000))
            + "\n\n请直接干脆地完成任务，完成后简明说明做了什么、改了哪些文件。之后 " + Label(t.Tools[1]) + " 会只读审查你的改动，有问题再交回修改。";
    }
    public static string ReviewerAsk(Team t, string words, string changes) {
        return "【协作任务 · 审查 · 第 " + t.Round + " 轮】另一个 AI（" + Label(t.Tools[0]) + "）正在完成这项任务：\n" + t.Goal + ExtraRules(t)
            + "\n\n它这一轮的说明：\n" + Cut(words, 6000) + (changes.Length > 0 ? "\n\n改动：\n" + Cut(changes, 30000) : "")
            + "\n\n请只审查，不要修改任何文件。没有必须修改的实质问题就直接通过，不要吹毛求疵。回答的第一行只写 REVIEW: PASS 或 REVIEW: CHANGES，之后逐条列出必须修改的问题与建议。";
    }
    public static string WriterAgain(Team t, string review) {
        return "【协作任务 · 审查意见 · 第 " + t.Round + " 轮】" + Label(t.Tools[1]) + " 的审查意见：\n" + Cut(review, 12000) + "\n\n请据此修改，完成后简要说明改了什么。";
    }
    public static string LeaderFirst(Team t, IDictionary<string, string> rooms, string discussion = "") {
        Func<string, string> room = tool => rooms.ContainsKey(tool) ? "；" + rooms[tool] : "";
        var members = LeadMembers(t).Select(role => {
            string tool = ToolOf(t, role);
            var seat = RosterOf(t, role);
            string model = seat != null ? seat.Model : t.Models.ContainsKey(tool) ? t.Models[tool] : "";
            return "- " + role + "：" + Label(tool) + "（" + (model.Length > 0 ? model : "工具默认模型")
                + (seat != null && seat.Effort.Length > 0 ? " · " + seat.Effort : "") + "）" + room(tool);
        }).ToArray();
        var usable = t.AutoRoster ? Usable(rooms) : new string[0];
        var tools = t.Tools.Concat(usable).Distinct();
        string modelGuide = "\n\n## 派工参考\n" + String.Join("\n", tools.Where(Strengths.ContainsKey).Select(tool => "- " + Label(tool) + "：" + Strengths[tool]))
            + "\n按实际配置的模型和任务难度派工。普通任务可用中等思考深度，复杂排查或验收才考虑更高档位。";
        string rosterGuide = t.Roster.Length > 0 ? "\n同一工具的各成员是独立对话，共用该工具的账号额度。" : "";
        if (t.AutoRoster) rosterGuide += usable.Length == 0 ? "\n现在没有可以派工的工具，请自己完成或说明阻塞。"
            : "\n你可以自行决定派给谁和派几个。可以派工的工具：" + String.Join("、", usable)
            + "。同批可使用 member-1、member-2、member-3。示例（工具名须从上面选）：\n"
            + "ASSIGN member-1 " + usable[0] + ": 实现输入校验（负责 validate.py），交回改动文件和测试输出\n"
            + "ASSIGN member-2 " + usable[usable.Length - 1] + ": 只读核对错误信息是否覆盖空值，交回核对结论\n"
            + "已有成员可用 ASSIGN member-1: 任务内容 继续，或加工具名切换。";
        return "【协作任务 · 领队】目标：" + t.Goal + ExtraRules(t)
            + (String.IsNullOrEmpty(discussion) ? "" : "\n\n## 成员前期方案讨论（只读）\n" + Cut(discussion, 12000))
            + "\n\n你负责确定范围、处理跨成员问题、整合并验收。成员在同一个项目文件夹工作：\n" + String.Join("\n", members)
            + (rooms.ContainsKey(t.Tools[0]) ? "\n你自己（" + Label(t.Tools[0]) + "）的" + rooms[t.Tools[0]] : "")
            + rosterGuide + (t.AutoRoster ? RoomsBlock(t, rooms) : "") + modelGuide
            + "\n\n" + DispatchGuide
            + (rooms.Count > 0 ? "\n额度已用尽或不可用的工具跳过；任一窗口已用 80% 以上时只派小而必要的任务，额度未知按正常对待。你自己的额度紧张时，把实现和检查交给成员。" : "")
            + "\n\n" + RoundsGuide(t)
            + "\n\nRemote AI 按这些行派给协作成员。分派时在回答末尾为每位成员写一条，可接多行任务说明：\nASSIGN 成员名: 任务内容\n同一项工作只派一次。工具内置的子代理只在用户明确要求时用于当前角色自己的工作。"
            + "\n自行完成时在末尾写 DONE: 总结。成员交回结果或你自行完成后，由你独立验收。";
    }
    /// How the leader gives out work: first what the user is to get and what proves it; then only as much working
    /// together as the work wants (the user's word: to use each AI's strengths, not to add steps): a simple task done by
    /// the one best placed, a design delivered as a design, a split only for large work or different strengths (by
    /// strength, quota, dependency and file ownership), a review where it is needed. Who is picked stays the leader's
    /// judgement; only `TeamsLive` shows it.
    const string DispatchGuide = "## 派工\n"
        + "先确定用户要拿到的成品和验收证据：是设计（方案、接口、界面稿或文档）、可运行的改动，还是两者都要；每项成品用什么证明达标，例如测试输出、运行结果、截图或文件内容。首轮只调查到能定下这些和做法为止。\n"
        + "协作是为了发挥各 AI 的长处，不是为了增加流程：\n"
        + "- 简单任务由最合适的一人直接完成：你能做完就自己做，否则只派一位成员，不为分工而拆分。\n"
        + "- 只要设计时交付设计，不改代码。\n"
        + "- 工作量大或需要不同专长时才分工，按专长、额度、依赖和文件归属选人，同一文件只交给一位成员。设计和实现都要分给多人时，先定下接口和文件归属，再按模块派实现；互不依赖的部分同批派出，依赖他人产出的等那份产出确认后再派。\n"
        + "- 改动影响面大、有风险或你没有把握时，请另一位 AI 只读审阅；小改动由你验收即可。\n"
        + "用户指定成员执行或要求组队时按要求派出。每项派工写清成品、负责的文件和要交回的验收证据。由成员自行调查、选择实现、修复并复测。跨范围的发现和需统一决定的取舍由你处理。";
    /// How the rounds go: the leader sets each round's aim by what it has seen; free rounds end when the goal is met.
    static string RoundsGuide(Team t) {
        return "每轮根据实际改动和检查结果安排剩余工作，只差复核时由你复核或派一位只读成员。达标即结束。"
            + (t.Free ? "本任务不限轮数（最多 " + MaxTotalRounds + " 轮）。" : "本任务最多 " + t.Rounds + " 轮。");
    }
    /// The leader's turn of acceptance: it inspects what changed and runs the checks itself (or has another member
    /// check or fix); `last` where no more rounds may be given out.
    public static string VerifyAsk(Team t, string changes, IDictionary<string, string> rooms, bool last) {
        return "【协作任务 · 领队验收" + (last ? " · 最后一轮" : "") + "】"
            + "第 " + t.Round + (t.Free ? " 轮（不限轮数）。" : " 轮，共 " + t.Rounds + " 轮。")
            + (last ? (t.Free ? "已到协作任务最多的 " + MaxTotalRounds + " 轮，请做最后一次验收。" : "已到设定的 " + t.Rounds + " 轮上限，请做最后一次验收。") : "请汇总本轮结果并单独验收。")
            + "\n\n整体目标：" + Cut(t.Goal, 2000)
            + (changes.Length > 0 ? "\n\n## 文件夹中的当前改动\n" + Cut(changes, 20000) : "")
            + RoomsBlock(t, rooms)
            + "\n\n对照用户要拿到的成品和约定的验收证据，检查实际交付及跨成员影响，亲自运行与这次改动相关的检查；只交付设计时，核对设计是否覆盖目标、约束和取舍，不要求代码改动。核实用户补充要求是否已落实。小的整合问题可直接修订并复查。"
            + "成品还没齐时（例如设计已确认、实现还没做），仍需执行的工作按范围交给成员。"
            + "\n\n验收通过时，在回答的最后写两行：\nVERIFIED: 你实际做了哪些检查、看到了什么结果（写明命令或查看的文件）\nDONE: 总结"
            + (last ? "\n没有通过时，说明还差什么，并用 ASSIGN 成员名: 修改内容 写出要做的修复。轮数已经用完，这些修复先保留，用户追加一轮后再派给成员。"
                : "\n发现问题或还有下一步时，用 ASSIGN 成员名: 任务内容 派给成员，完成后会请你再次验收。");
    }
    const string VerifyRemind = "【协作任务 · 领队验收】这次回答里没有找到验收证据。请在回答的最后写：\nVERIFIED: 你实际运行的检查和看到的结果\nDONE: 总结\n还有问题时，用 ASSIGN 成员名: 修改内容 安排修复。";
    /// A member that handed in a note of progress (its turn ended while what it waits for still runs) is sent back.
    public const string KeepAtIt = "【协作任务】请等到当前操作返回，继续核对、修复并复测，完成后交回结果。确实无法继续时，写明阻塞原因和需要领队处理的事项。";
    const int MostNudges = 2;
    static readonly Regex Underway = new Regex(
        @"(?:正在|还在|仍在)(?:运行|执行|跑|等待|等(?!待)|进行|测试|构建|编译|生成|检查|核对|验证|查看)(?!的)|待.{0,24}(?:返回|完成|结束|出来|跑完)后|等.{0,24}(?:结果|完成|结束|跑完|返回)(?:后|再)|稍后(?:回报|汇报|继续|再)"
        + @"|\b(?<!was )(?<!were )(?<!had been )(?<!no longer )(?:still running|in progress|waiting for)\b"
        + @"|\b(?:will|['’]ll) (?:report|continue)\b|\b(?:once|as soon as) (?:it|they|the \w+) (?:finish(?:es)?|completes?|ends?|returns?)\b"
        // "I'm checking … now", a sentence that begins "Checking … now" with no one named, and "Now checking …"
        + @"|\b(?:I['’]m|I am|we['’]re|we are) (?:still |currently )?" + Doing + @" [^.!?\r\n]+\bnow\b"
        + @"|(?:^|(?<=[.!?;:\n]\s*))" + Doing + @" [^.!?\r\n]+\bnow\b|(?:^|(?<=[.!?;:\n]\s*))now " + Doing + @"\b", RegexOptions.IgnoreCase);
    const string Doing = @"(?:generating|running|building|testing|compiling|checking|verifying|reviewing|inspecting|looking (?:at|into)|working on)";
    static readonly Regex Blocked = new Regex(
        @"^[ \t>*_`#-]*(?:(?:阻塞|受阻|Blocked)[*_`]*[ \t]*[:：][*_` \t]*[^\s*_`]|\b(?:I|we) (?:cannot|can['’]t) (?:continue|proceed)\b|(?:我(?:们)?)?(?:目前|暂时)?无法继续)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);
    static readonly Regex CompletedWork = new Regex(
        @"已(?:经)?(?:全部|均|都)?(?:通过|完成|跑完)|(?:核对|查验|验证|检查|复核)了|(?:测试|检查)(?:全部|均|都)?通过"
        + @"|\b(?:I|we) (?:inspected|confirmed|verified)\b|\b(?<!not )(?:passed|succeeded)\b", RegexOptions.IgnoreCase);
    public static bool Progress(string said) {
        if (!String.IsNullOrEmpty(Finished(said)) || Blocked.IsMatch(said ?? "")) return false;
        var paragraphs = Regex.Split((said ?? "").Trim(), @"\n\s*\n");
        string last = paragraphs[paragraphs.Length - 1];
        if (last.Length > 400) last = last.Substring(last.Length - 400);
        var pending = Underway.Matches(last).Cast<Match>().LastOrDefault();
        return pending != null && !CompletedWork.IsMatch(last, pending.Index + pending.Length);
    }
    const string MemberGuide = "\n\n按派工交付写明的成品：派的是设计就交设计，不要顺带实现。在分派范围内自主调查、选择实现、修复并复测。跨范围发现交领队裁决，组队和派工由领队负责。只有用户明确要求时才使用工具内置的子代理。"
        + "\n等操作和检查结束再交回，写明实际产出、改动文件、关键取舍，以及派工要求的验收证据。完成时可用 DONE: 总结。确实无法继续时用“阻塞：”说明原因、已经尝试的办法和需要领队决定的事项。";
    public static string MemberTask(Team t, string task) {
        return "【协作任务 · 成员】领队（" + Label(t.Tools[0]) + "）分派给你的任务：\n" + task
            + "\n\n项目目标供参考：" + t.Goal + MemberGuide;
    }
    public static string CompareFirst(Team t, string tool) {
        return "【协作任务 · 并行比稿】目标：" + t.Goal + ExtraRules(t)
            + "\n\n你正在该项目的独立 Git worktree 副本中工作，不会与其他候选 AI 冲突。请直接完成你的最佳实现并验证关键路径，完成后简明说明你的方案思路、改了哪些文件以及取舍理由。用户会对比各候选方案并选择一个合并。";
    }
    public static string Results(Team t, Dictionary<string, string> answers, string changes, IDictionary<string, string> rooms, string guide = "") {
        return Answers(t, answers)
            + (changes.Length > 0 ? "\n\n## 文件夹中的改动\n" + Cut(changes, 20000) : "")
            + RoomsBlock(t, rooms)
            + "\n\n" + (guide.Length > 0 ? guide : "请对照用户要拿到的成品和验收证据，继续分派（ASSIGN 成员名: 任务）或确认完成（DONE: 总结）。");
    }
    const string DecideNext = "本轮仍有未完成的分工。请根据回报解决阻塞、明确范围或改派，只安排剩余工作；改派同样按专长、额度、依赖和文件归属选人。用户要的成品都有验收证据后再写 DONE: 总结。";
    const string SupplementGuide = "补充要求改变了用户要拿到的成品或验收证据时，先更新它们，再调整受影响的分工。把仍适用和本轮新增的任务尽早写成 ASSIGN 行，简单工作可直接完成。";
    static string Answers(Team t, Dictionary<string, string> answers) {
        return "【协作任务 · 成员结果 · 第 " + t.Round + " 轮】\n\n" + String.Join("\n\n", answers.Select(p => "## " + RoleLabel(t, p.Key) + "\n" + Cut(p.Value, 8000)));
    }
    const string Remind = "【协作任务】没有在你的回答里找到分派或结束。请在回答的最后按格式写：\nASSIGN 成员名: 任务内容（可以多行）\n或单独一行：\nDONE: 总结";
    static string Cut(string text, int most) { text = text ?? ""; return text.Length > most ? text.Substring(0, most) + "\n…（后面省略）" : text; }
    static string Short(string text, int most) { text = text ?? ""; return text.Length > most ? text.Substring(0, most) + "…" : text; }

    // ---- what the members say, read
    /// A review's verdict from its first line that says one: true pass, false changes, null none.
    public static bool? Verdict(string review) {
        foreach (string line in (review ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3)) {
            string plain = Regex.Replace(line, @"[*_`#>\s]", "").ToUpperInvariant();
            if (plain.StartsWith("REVIEW:PASS") || plain.StartsWith("REVIEW：PASS")) return true;
            if (plain.StartsWith("REVIEW:CHANGES") || plain.StartsWith("REVIEW：CHANGES")) return false;
        }
        return null;
    }
    static readonly Regex Assign = new Regex(@"^[\s>*\-_`]*ASSIGN[*_`]*[ \t]+[*_`]*(claude|codex|agy|member-[123])(?:[ \t]+(claude|codex|agy))?[*_`]*[ \t]*[:：][ \t]*(.+?)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    static readonly Regex Done = new Regex(@"^[\s>*\-_`]*DONE[*_`]*\s*[:：]\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    static readonly Regex AnyAssign = new Regex(@"^[\s>*\-_`]*ASSIGN\b", RegexOptions.IgnoreCase);
    /// Each ASSIGN line with the lines under it, up to the next ASSIGN, DONE or VERIFIED line: a leader often writes a
    /// task as one line and a numbered list under it, and the member has to be given all of it.
    static IEnumerable<Tuple<Match, string>> Assignments(string said) {
        var lines = (said ?? "").Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++) {
            var m = Assign.Match(lines[i]);
            if (!m.Success) continue;
            var body = new List<string> { m.Groups[3].Value.Trim() };
            int j = i + 1;
            for (; j < lines.Length && !AnyAssign.IsMatch(lines[j]) && !Done.IsMatch(lines[j]) && !Checked.IsMatch(lines[j]); j++) body.Add(lines[j].TrimEnd());
            i = j - 1;
            yield return Tuple.Create(m, Cut(String.Join("\n", body).Trim(), 8000));
        }
    }
    /// The leader's tasks for its members (a member named twice is given both), of the members it has.
    public static Dictionary<string, string> Assigned(string said, IEnumerable<string> members) {
        var tasks = new Dictionary<string, string>();
        var known = new HashSet<string>(members);
        foreach (var one in Assignments(said)) {
            Match m = one.Item1;
            string tool = m.Groups[1].Value.ToLowerInvariant(), task = one.Item2;
            if (!known.Contains(tool) || m.Groups[2].Success || task.Length == 0) continue;
            tasks[tool] = tasks.ContainsKey(tool) ? tasks[tool] + "\n" + task : task;
        }
        return tasks;
    }
    /// The leader's summary where it says the work is done; null where it does not.
    public static string Finished(string said) { var m = Done.Match(said ?? ""); return m.Success ? m.Groups[1].Value.Trim() : null; }
    static readonly Regex Checked = new Regex(@"^[\s>*\-_`]*VERIFIED[*_`]*\s*[:：]\s*(.*)$", RegexOptions.IgnoreCase);
    /// What the leader says it checked (`VERIFIED: …`, with the lines under it up to DONE or ASSIGN); null where it
    /// says nothing that could be checks and their results.
    public static string Verified(string said) {
        var lines = (said ?? "").Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++) {
            var m = Checked.Match(lines[i]);
            if (!m.Success) continue;
            var evidence = new List<string> { m.Groups[1].Value.Trim().Trim('*', '_', '`').Trim() };
            for (int j = i + 1; j < lines.Length && evidence.Count < 12 && !Done.IsMatch(lines[j]) && !Assign.IsMatch(lines[j]) && !Checked.IsMatch(lines[j]); j++)
                if (lines[j].Trim().Length > 0) evidence.Add(lines[j].Trim());
            string text = String.Join("\n", evidence.Where(x => x.Length > 0)).Trim();
            if (Regex.Replace(text, @"[\s*_`>#\-.。,，:：;；!！?？()（）]", "").Length >= 6) return text;
        }
        return null;
    }

    // ---- what the owner does
    static RosterMember[] ReadRoster(object raw, bool auto, bool stored) {
        var rows = raw as IList;
        if (rows == null || rows.Count < (auto ? 1 : 2) || rows.Count > (auto && !stored ? 1 : 4))
            throw new ArgumentException("请选择一位领队和 1 至 3 位执行成员，或让领队自动组队");
        var members = new List<RosterMember>();
        foreach (object row in rows) {
            var fields = row as Dictionary<string, object>;
            if (fields == null) throw new ArgumentException("协作成员无效");
            object role, tool, model, effort;
            fields.TryGetValue("role", out role);
            fields.TryGetValue("tool", out tool);
            fields.TryGetValue("model", out model);
            fields.TryGetValue("effort", out effort);
            string roleName = role as string, toolName = tool as string, modelName = (model as string ?? "").Trim();
            string rawEffort = effort as string ?? "", effortName = rawEffort.Trim().ToLowerInvariant();
            if ((members.Count == 0 ? roleName != "leader" : !MemberRoles.Contains(roleName)) ||
                members.Any(m => m.Role == roleName) || !RemoteCli.Models.Tools.Contains(toolName) ||
                (model != null && !(model is string)) || (modelName.Length > 0 && !RemoteCli.Models.Valid(modelName)) ||
                (effort != null && !(effort is string)) || (effortName.Length > 0 && !RemoteCli.Models.ValidEffort(effortName)))
                throw new ArgumentException(effort != null && (!(effort is string) || (effortName.Length > 0 && !RemoteCli.Models.ValidEffort(effortName))) ? "思考深度无效" : "协作成员无效");
            members.Add(new RosterMember(roleName, toolName, modelName, effortName));
        }
        return members.ToArray();
    }

    public Team Start(Dictionary<string, object> given) {
        Func<string, string> word = name => { object value; return given.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : ""; };
        object raw; given.TryGetValue("tools", out raw);
        var tools = (raw as IEnumerable ?? new object[0]).Cast<object>().Select(Convert.ToString).ToArray();
        int rounds; Int32.TryParse(word("rounds"), out rounds);
        if (given.ContainsKey("rounds") && rounds == 0 && word("mode") != "lead") throw new ArgumentException("只有分工协作可以不限轮数");
        string rules = word("rules").Trim();
        if (rules.Length > 1000) throw new ArgumentException("分工偏好不能超过 1000 字");
        string mode = word("mode");
        bool discuss = (mode == "review" || mode == "lead") && String.Equals(word("discuss"), "True", StringComparison.OrdinalIgnoreCase);
        object automatic;
        bool auto = given.TryGetValue("auto_roster", out automatic) && Object.Equals(automatic, true);
        if (automatic != null && !(automatic is bool)) throw new ArgumentException("自动组队选项无效");
        object rosterValue;
        given.TryGetValue("roster", out rosterValue);
        if ((rosterValue != null && mode != "lead") || (auto && rosterValue == null))
            throw new ArgumentException("仅分工协作支持按成员安排");
        var roster = rosterValue == null ? new RosterMember[0] : ReadRoster(rosterValue, auto, false);
        if (roster.Length > 0) {
            tools = roster.Select(member => member.Tool).ToArray();
            if (hands.Available != null && tools.Except(hands.Available() ?? new string[0]).Any())
                throw new ArgumentException("这台电脑不能让这个工具参与协作");
        }
        object models; given.TryGetValue("models", out models);
        object efforts; given.TryGetValue("efforts", out efforts);
        var team = new Team {
            Id = word("id"), Dir = word("dir"), Mode = mode, Goal = word("goal").Trim(), Rules = rules,
            Discuss = discuss, Tools = tools, Models = RemoteCli.Models.Chosen(models, tools), Efforts = RemoteCli.Models.ChosenEfforts(efforts, tools), Roster = roster, AutoRoster = auto,
            Rounds = given.ContainsKey("rounds") && rounds == 0 ? 0 : Math.Max(1, Math.Min(MostRounds, rounds == 0 ? (mode == "compare" ? 1 : 3) : rounds)),
            Created = Now()
        };
        if (!Regex.IsMatch(team.Id, @"\A[a-f0-9]{16,32}\z") || Array.IndexOf(Modes, team.Mode) < 0) throw new ArgumentException("协作任务无效");
        if (team.Goal.Length == 0 || team.Goal.Length > 4000) throw new ArgumentException("目标需为 1 至 4000 字");
        if (team.Mode == "review" && tools.Length != 2) throw new ArgumentException("写审需要一个写的 AI 和一个审查的 AI");
        if (team.Mode == "lead" && roster.Length == 0 && (tools.Length < 2 || tools.Length > 4 || tools.Distinct().Count() != tools.Length))
            throw new ArgumentException("领队需要一个领队和 1 至 3 个不同的成员");
        if (team.Mode == "compare" && (tools.Length < 2 || tools.Length > 3 || tools.Distinct().Count() != tools.Length))
            throw new ArgumentException("并行比稿需要 2 至 3 个不同的 AI");
        if (team.Mode == "compare" && hands.MakeWorktree == null)
            throw new InvalidOperationException("当前环境不支持独立工作区比稿");

        lock (gate) {
            var same = items.FirstOrDefault(t => t.Id == team.Id);
            if (same != null) return same.Copy();       // asked again under the same number
            if (items.Count(t => t.State == "running" || t.State == "paused") >= 3) throw new InvalidOperationException("最多同时进行 3 个协作任务，请先结束一个");
            items.Add(team);
            while (items.Count > Most) {
                var drop = items.Where(t => t.State != "running" && t.State != "paused" && t.State != "choose").OrderBy(t => t.Created).FirstOrDefault() ?? items[0];
                items.Remove(drop);
            }
        }

        if (team.Mode == "compare") {
            try {
                foreach (string tool in team.Tools) {
                    var created = hands.MakeWorktree(team.Dir, team.Id, tool);
                    lock (gate) { team.Worktrees[tool] = created.Path; team.BaseCommits[tool] = created.BaseCommit; team.Awaited.Add(tool); }
                }
            } catch {
                try { if (hands.CleanWorktrees != null) hands.CleanWorktrees(team.Dir, team.Id); } catch (Exception) { }
                lock (gate) { items.Remove(team); Save(); }
                throw;
            }
            Log(team, "目标", team.Goal);
            foreach (string tool in team.Tools) Open(team, tool, tool, CompareFirst(team, tool), false, team.Worktrees[tool]);
            return Of(team.Id);
        }

        Log(team, "目标", team.Goal);
        if (team.Discuss && !team.AutoRoster) {
            string[] discussants = team.Mode == "lead" ? LeadMembers(team) : team.Tools.Distinct().ToArray();
            lock (gate) {
                team.Phase = "discuss";
                foreach (string roleName in discussants) team.Awaited.Add("discuss:" + roleName);
                Save();
            }
            foreach (string roleName in discussants) {
                string tool = ToolOf(team, roleName);
                Open(team, "discuss:" + roleName, tool, DiscussFirst(team, tool), true, null);
            }
            return Of(team.Id);
        }

        string role = team.Mode == "review" ? "writer" : "leader";
        Open(team, role, team.Tools[0], team.Mode == "review" ? WriterFirst(team) : LeaderFirst(team, Rooms(team)), false, null);
        return Of(team.Id);
    }

    public void Stop(string id) {
        Team team; string[] busy;
        lock (gate) {
            team = items.FirstOrDefault(t => t.Id == id);
            if (team == null) throw new ArgumentException("没有找到这个协作任务");
            busy = team.Awaited.Select(role => team.Members.ContainsKey(role) ? team.Members[role] : "").Where(x => x.Length > 0).ToArray();
            team.State = "stopped"; team.Note = "已停止"; team.Awaited.Clear(); team.Spent = false; team.Held.Clear();
            Save();
        }
        foreach (string terminal in busy) try { hands.Stop(terminal); } catch (Exception) { }
        Tell();
    }

    /// Goes on where it was cut: every member whose turn was awaited is told to go on. Work whose rounds were spent
    /// is given one more (`Extend`).
    public void Resume(string id) {
        Team team; List<KeyValuePair<string, string>> again;
        lock (gate) {
            team = items.FirstOrDefault(t => t.Id == id);
            if (team == null) throw new ArgumentException("没有找到这个协作任务");
            if (team.State != "paused") throw new InvalidOperationException("这个协作任务没有暂停");
        }
        if (team.Spent) { Extend(team, ""); return; }
        lock (gate) {
            team.State = "running"; team.Note = "";
            foreach (string role in team.Awaited) { team.Failed.Remove(role); team.Failed.Remove(ToolOf(team, role)); }
            again = team.Awaited.Select(role => new KeyValuePair<string, string>(role, team.Members.ContainsKey(role) ? team.Members[role] : null)).ToList();
            Save();
        }
        foreach (var one in again) DeliverOrReopen(team, one.Key, one.Value, "继续完成上面的任务。");
        Tell();
    }

    /// Sends an owner message into a collaboration task:
    /// - while running: interjects to `target` (defaults to the leader in lead mode after discussion);
    /// - while paused: resumes and delivers the guidance;
    /// - while waiting to choose in `compare`: asks one or all candidates to refine in their worktrees;
    /// - after `done` or `stopped` (`review` / `lead`): starts a new follow-up round.
    public Team Say(string id, string text, string target = "") {
        text = (text ?? "").Trim();
        target = (target ?? "").Trim();
        if (text.Length == 0 || text.Length > 4000) throw new ArgumentException("消息需为 1 至 4000 字");
        Team team; string state, phase, mode; List<string> targets = new List<string>();
        lock (gate) {
            team = items.FirstOrDefault(t => t.Id == id);
            if (team == null) throw new ArgumentException("没有找到这个协作任务");
            if (team.State == "failed") throw new InvalidOperationException("这个协作任务没有启动成功，请重新新建");
            if (team.Mode == "compare" && (team.State == "done" || team.Worktrees.Count == 0))
                throw new InvalidOperationException("比稿方案已结束并清理工作区，请新建协作任务");
            state = team.State; phase = team.Phase; mode = team.Mode;

            if (state == "paused" && team.Spent) { /* its rounds were spent: the words go with the round they add (Extend) */ }
            else if (state == "running" || state == "paused") {
                if (target.Length > 0) {
                    string resolved = ResolveRole(team, target);
                    if (resolved == null) throw new ArgumentException("没有找到这个成员");
                    targets.Add(resolved);
                    // Resuming a paused task with an explicit target drops stale failed members from Awaited.
                    if (state == "paused") team.Awaited.Clear();
                    if (!team.Awaited.Contains(resolved)) team.Awaited.Add(resolved);
                } else {
                    if (state == "paused" && mode == "lead" && team.Awaited.Count > 0 && team.Awaited.All(r => r != "leader" && FailedRole(team, r))) {
                        team.Awaited.Clear();
                        team.Awaited.Add("leader");
                    }
                    if (state == "running" && mode == "lead" && phase != "discuss") targets.Add("leader");
                    else targets.AddRange(team.Awaited.Count > 0 ? team.Awaited : new List<string> { mode == "review" ? "writer" : mode == "lead" ? "leader" : team.Tools[0] });
                    foreach (string r in targets) if (!team.Awaited.Contains(r)) team.Awaited.Add(r);
                }
                if (state == "paused") {
                    team.State = "running"; team.Note = "";
                    foreach (string r in targets) { team.Failed.Remove(r); team.Failed.Remove(ToolOf(team, r)); }
                }
                if (state == "running" && mode == "lead" && targets.Remove("leader")) {
                    team.LeaderInput = Cut(team.LeaderInput.Length == 0 ? text : team.LeaderInput + "\n" + text, 12000);
                    if (MembersWorking(team)) team.Awaited.Remove("leader");
                }
                Save();
            } else if (mode == "compare" && (state == "choose" || state == "stopped")) {
                if (target.Length > 0) {
                    if (!team.Tools.Contains(target)) throw new ArgumentException("没有找到这个候选 AI");
                    targets.Add(target);
                } else {
                    targets.AddRange(team.Tools);
                }
                team.Round++;
                if (team.Round > team.Rounds) team.Rounds = Math.Min(MaxTotalRounds, team.Round);
                team.State = "running"; team.Note = "";
                team.Awaited.Clear();
                foreach (string r in targets) team.Awaited.Add(r);
                Save();
            } else {
                // `done` or `stopped` for `review` / `lead`: begin another round with the owner's follow-up
                team.Round++;
                if (!team.Free && team.Round > team.Rounds) team.Rounds = Math.Min(MaxTotalRounds, team.Round + 2);
                team.State = "running"; team.Note = ""; team.Phase = ""; team.Reminded = false; team.Last = false;
                team.Awaited.Clear(); team.Answers.Clear(); team.Failed.Clear();
                Save();
            }
        }

        Log(team, "user", (target.Length > 0 ? "@" + RoleLabel(team, ResolveRole(team, target) ?? target) + " " : "") + text);
        if (state == "paused" && team.Spent) { Extend(team, text); return Of(id); }

        if (state == "running" || state == "paused") {
            string prefix = state == "running" ? "【用户插话】\n" : "【用户补充】\n";
            foreach (string role in targets) {
                string terminal = null;
                lock (gate) team.Members.TryGetValue(role, out terminal);
                DeliverOrReopen(team, role, terminal, prefix + text);
            }
        } else if (mode == "compare") {
            string prompt = "【用户补充要求 · 第 " + team.Round + " 轮】\n" + text + "\n\n请在你当前独立工作区中继续完善方案，完成后简要说明改了什么。";
            foreach (string tool in targets) {
                string wt = null; lock (gate) team.Worktrees.TryGetValue(tool, out wt);
                Ask(team, tool, tool, prompt, false, wt);
            }
        } else if (mode == "review") {
            string prompt = "【用户新一轮要求 · 第 " + team.Round + " 轮】\n" + text
                + "\n\n请按新要求继续修改，完成后简明说明改了什么。之后 " + Label(team.Tools[1]) + " 会再次只读审查。";
            Ask(team, "writer", team.Tools[0], prompt, false, null);
        } else {
            var rooms = Rooms(team);
            string prompt = "【用户新一轮要求 · 第 " + team.Round + " 轮】\n" + text + RoomsBlock(team, rooms)
                + "\n\n" + SupplementGuide + " 完成后写 DONE: 总结。";
            Ask(team, "leader", team.Tools[0], prompt, false, null);
        }
        return Of(id);
    }

    /// Picks a winning candidate in `compare` mode and applies its worktree patch to the project folder, or discards
    /// all candidates when `tool` is empty, cleaning up all temporary worktrees.
    public Team Pick(string id, string tool) {
        tool = (tool ?? "").Trim().ToLowerInvariant();
        Team team; string wtPath = null, baseCommit = null, dir;
        lock (gate) {
            team = items.FirstOrDefault(t => t.Id == id);
            if (team == null) throw new ArgumentException("没有找到这个协作任务");
            if (team.Mode != "compare") throw new InvalidOperationException("只有并行比稿任务才可以选择方案");
            if (team.State == "running") throw new InvalidOperationException("还有 AI 正在执行，请等它们做完或先停止");
            if (team.Worktrees.Count == 0) throw new InvalidOperationException("这个比稿任务的工作区已经清理");
            dir = team.Dir;
            if (tool.Length > 0) {
                if (!team.Worktrees.TryGetValue(tool, out wtPath)) throw new ArgumentException("没有找到这个候选方案");
                team.BaseCommits.TryGetValue(tool, out baseCommit);
            }
        }
        string note;
        if (tool.Length == 0) {
            if (hands.CleanWorktrees != null) try { hands.CleanWorktrees(dir, id); } catch (Exception) { }
            note = "已放弃所有候选方案并清理工作区";
        } else {
            if (hands.ApplyWorktree == null) throw new InvalidOperationException("当前环境不支持合并工作区改动");
            string applied = hands.ApplyWorktree(dir, wtPath, baseCommit);
            if (hands.CleanWorktrees != null) try { hands.CleanWorktrees(dir, id); } catch (Exception) { }
            note = "已合并 " + Label(tool) + " 的方案（" + applied + "），其余工作区已清理";
        }
        lock (gate) {
            team.Worktrees.Clear(); team.BaseCommits.Clear();
            team.Picked = tool; team.State = "done"; team.Note = note; team.Awaited.Clear();
            Save();
        }
        Log(team, "结果", note);
        return Of(id);
    }

    /// Explicitly swaps a role's AI (or reassigns a member's task to another AI) mid-run or while paused; `model` and
    /// `effort`, where given, are what that role/AI is begun with from now on (none: as before for it, else its default).
    public Team Swap(string id, string role, string newTool, string note = "", string model = null, string effort = null) {
        role = (role ?? "").Trim();
        newTool = (newTool ?? "").Trim().ToLowerInvariant();
        note = (note ?? "").Trim();
        if (model != null) model = model.Trim();
        if (effort != null) effort = effort.Trim().ToLowerInvariant();
        if (note.Length > 1000) throw new ArgumentException("补充说明不能超过 1000 字");
        if (newTool != "claude" && newTool != "codex" && newTool != "agy") throw new ArgumentException("接手 AI 无效");
        if (!String.IsNullOrEmpty(model) && !RemoteCli.Models.Valid(model)) throw new ArgumentException("模型名称无效");
        if (!String.IsNullOrEmpty(effort) && !RemoteCli.Models.ValidEffort(effort)) throw new ArgumentException("思考深度无效");
        Team team; string resolved, oldTool, oldTerminal = null, pending = "", mode, phase, prevState;
        bool launchNow = false;
        lock (gate) {
            team = items.FirstOrDefault(t => t.Id == id);
            if (team == null) throw new ArgumentException("没有找到这个协作任务");
            if (team.State == "failed") throw new InvalidOperationException("这个协作任务没有启动成功，请重新新建");
            if (team.Mode == "compare") throw new InvalidOperationException("并行比稿不支持替换角色");
            mode = team.Mode; phase = team.Phase; prevState = team.State;
            resolved = role.Length > 0 ? ResolveRole(team, role) : DefaultSwapRole(team);
            if (resolved == null) throw new ArgumentException("没有找到这个角色");
            oldTool = ToolOf(team, resolved);
            var rosterMember = RosterOf(team, resolved);
            if (oldTool == newTool && (rosterMember == null || ((model == null || model == rosterMember.Model) && (effort == null || effort == rosterMember.Effort))))
                throw new ArgumentException("这个角色当前使用相同的工具、模型和思考深度");
            team.Members.TryGetValue(resolved, out oldTerminal);
            team.Pending.TryGetValue(resolved, out pending);
            team.Failed.Remove(newTool);
            team.Failed.Remove(resolved);
            if (rosterMember == null) {
                if (!String.IsNullOrEmpty(model)) team.Models[newTool] = model;
                if (effort != null) {
                    if (effort.Length > 0) team.Efforts[newTool] = effort; else team.Efforts.Remove(newTool);
                }
            }

            bool wasActive = team.Awaited.Contains(resolved) || prevState != "running";
            if (rosterMember != null) {
                if (phase == "discuss" && resolved != "leader") {
                    resolved = "discuss:" + rosterMember.Role;
                    team.Failed.Remove(resolved);
                    team.Members.TryGetValue(resolved, out oldTerminal);
                    team.Pending.TryGetValue(resolved, out pending);
                    wasActive = team.Awaited.Contains(resolved) || prevState != "running";
                }
                string nextModel = model ?? (oldTool == newTool ? rosterMember.Model : "");
                string nextEffort = effort ?? (oldTool == newTool && nextModel == rosterMember.Model ? rosterMember.Effort : "");
                SetRosterMember(team, rosterMember.Role, newTool, nextModel, nextEffort);
                team.Members.Remove(resolved);
                team.Sessions.Remove(resolved);
                if (wasActive) {
                    team.State = "running"; team.Note = "";
                    if (!team.Awaited.Contains(resolved)) team.Awaited.Add(resolved);
                    launchNow = true;
                }
            } else if (mode == "review") {
                int idx = resolved == "writer" ? 0 : 1;
                team.Tools[idx] = newTool;
                team.Members.Remove(resolved);
                if (phase == "discuss") {
                    string oldDiscuss = "discuss:" + oldTool, newDiscuss = "discuss:" + newTool;
                    if (team.Members.ContainsKey(oldDiscuss)) { oldTerminal = team.Members[oldDiscuss]; team.Members.Remove(oldDiscuss); }
                    if (team.Awaited.Remove(oldDiscuss) && !team.Awaited.Contains(newDiscuss)) { team.Awaited.Add(newDiscuss); resolved = newDiscuss; launchNow = true; }
                } else if (wasActive) {
                    team.State = "running"; team.Note = "";
                    team.Awaited.Clear(); team.Awaited.Add(resolved);
                    launchNow = true;
                }
            } else if (resolved == "leader") {
                PromoteLeader(team, newTool);
                team.Members.Remove("leader");
                if (wasActive) {
                    team.State = "running"; team.Note = "";
                    team.Awaited.Clear(); team.Awaited.Add("leader");
                    launchNow = true;
                }
            } else if (phase == "discuss" && resolved.StartsWith("discuss:", StringComparison.Ordinal)) {
                string newDiscuss = "discuss:" + newTool;
                if (!team.Tools.Contains(newTool)) team.Tools = team.Tools.Concat(new[] { newTool }).ToArray();
                team.Members.Remove(resolved);
                team.Awaited.Remove(resolved);
                if (!team.Awaited.Contains(newDiscuss)) team.Awaited.Add(newDiscuss);
                if (prevState != "running") { team.State = "running"; team.Note = ""; }
                resolved = newDiscuss;
                launchNow = true;
            } else {
                // Reassign a lead member's task to `newTool` (adding `newTool` to the team if needed).
                if (!team.Tools.Contains(newTool)) team.Tools = team.Tools.Concat(new[] { newTool }).ToArray();
                string nextRole = newTool == team.Tools[0] ? "leader" : newTool;
                team.Awaited.Remove(resolved);
                if (prevState == "paused") team.Awaited.Clear();
                if (!team.Awaited.Contains(nextRole)) team.Awaited.Add(nextRole);
                team.State = "running"; team.Note = "";
                resolved = nextRole;
                launchNow = true;
            }
            Save();
        }
        if (!String.IsNullOrEmpty(oldTerminal)) try { hands.Stop(oldTerminal); } catch (Exception) { }
        string summary = mode == "review"
            ? "已将" + (resolved == "reviewer" ? "审查" : "写手") + "从 " + Label(oldTool) + " 切换为 " + Label(newTool)
            : resolved == "leader" ? "已将领队从 " + Label(oldTool) + " 切换为 " + Label(newTool)
            : "已将 " + Label(oldTool) + " 的任务转交给 " + Label(newTool);
        if (!String.IsNullOrEmpty(model)) summary += "，模型 " + model;
        if (!String.IsNullOrEmpty(effort)) summary += "，思考深度 " + effort;
        if (note.Length > 0) summary += "（" + Cut(note, 120) + "）";
        Log(team, "切换", summary);
        if (!launchNow) return Of(id);

        bool readOnly = resolved == "reviewer" || resolved.StartsWith("discuss:", StringComparison.Ordinal);
        string prompt = HandoffPrompt(team, resolved, oldTool, "用户指定切换", pending, note);
        if (mode == "lead" && resolved != "leader" && !resolved.StartsWith("discuss:", StringComparison.Ordinal))
            Ask(team, resolved, newTool, prompt, false, null);
        else
            Open(team, resolved, newTool, prompt, readOnly, null);
        return Of(id);
    }

    static string DefaultSwapRole(Team team) {
        string failedAwaited = team.Awaited.FirstOrDefault(r => team.Failed.ContainsKey(r) || team.Failed.ContainsKey(ToolOf(team, r)));
        if (failedAwaited != null) return failedAwaited;
        if (team.Awaited.Count > 0) return team.Awaited[0];
        if (team.Mode == "review") return "writer";
        return LeadMembers(team).FirstOrDefault() ?? "leader";
    }
    static void PromoteLeader(Team team, string newLeader) {
        if (team.Roster.Length > 0) { SetRosterMember(team, "leader", newLeader, ""); return; }
        var next = new List<string> { newLeader };
        foreach (string t in team.Tools) if (t != newLeader && !next.Contains(t)) next.Add(t);
        team.Tools = next.ToArray();
    }
    string HandoffPrompt(Team team, string role, string fromTool, string why, string pending, string userNote) {
        string changes = "";
        try { changes = hands.Changes(team.Dir) ?? ""; } catch (Exception) { }
        string extra = (String.IsNullOrEmpty(why) ? "" : "\n\n【接手说明】原负责成员（" + Label(fromTool) + "）" + why + "，现在由你接手继续完成。")
            + (String.IsNullOrEmpty(userNote) ? "" : "\n【用户补充】" + userNote);
        if (role.StartsWith("discuss:", StringComparison.Ordinal)) return DiscussFirst(team, ToolOf(team, role)) + extra;
        if (team.Mode == "review" && role == "writer") {
            return WriterFirst(team)
                + (!String.IsNullOrEmpty(pending) && pending != WriterFirst(team) ? "\n\n## 当前待完成的任务/意见\n" + Cut(pending, 6000) : "")
                + (changes.Length > 0 ? "\n\n## 当前工作区已有改动\n" + Cut(changes, 20000) : "")
                + extra + "\n\n请基于当前工作区已有改动继续完成，不要推倒重做已经正确的部分。";
        }
        if (team.Mode == "review" && role == "reviewer") {
            return ReviewerAsk(team, "原审查 AI（" + Label(fromTool) + "）未能完成本轮审查，请你接手只读审查。" + (String.IsNullOrEmpty(userNote) ? "" : "\n用户补充：" + userNote), changes);
        }
        if (team.Mode == "lead" && role == "leader") {
            string recent;
            lock (gate) recent = String.Join("\n\n", team.Log.Skip(Math.Max(0, team.Log.Count - 10)).Select(s => "[" + s.Who + "] " + Cut(s.Text, 800)));
            bool verifying; lock (gate) verifying = team.Phase == "verify";
            return LeaderFirst(team, Rooms(team))
                + (recent.Length > 0 ? "\n\n## 此前协作进展\n" + Cut(recent, 8000) : "")
                + (verifying ? "\n\n" + VerifyAsk(team, changes, new Dictionary<string, string>(), team.Last) : changes.Length > 0 ? "\n\n## 当前工作区改动\n" + Cut(changes, 16000) : "")
                + (team.Phase == "decision" ? "\n\n" + DecideNext : "")
                + extra;
        }
        return "【协作任务 · 接手成员任务】\n整体目标：" + team.Goal + ExtraRules(team)
            + (!String.IsNullOrEmpty(pending) ? "\n\n原分派给 " + Label(fromTool) + " 的任务：\n" + Cut(pending, 4000) : "")
            + (changes.Length > 0 ? "\n\n## 当前工作区已有改动\n" + Cut(changes, 16000) : "")
            + extra + MemberGuide;
    }

    /// Removes a task that is not at work, closing every terminal it began (its conversations stay in the tools'
    /// history, listed by themselves again) and its worktrees.
    public bool Remove(string id) {
        Team team; string dir = null; bool hasWorktrees = false; string[] opened;
        lock (gate) {
            team = items.FirstOrDefault(t => t.Id == id);
            if (team == null) return false;
            if (team.State == "running") throw new InvalidOperationException("这个协作任务还在进行，请先停止");
            dir = team.Dir; hasWorktrees = team.Worktrees.Count > 0;
            opened = team.Opened.ToArray();
            items.Remove(team); Save();
        }
        if (hands.Close != null) foreach (string terminal in opened) try { hands.Close(terminal); } catch (Exception) { }
        if (hasWorktrees && hands.CleanWorktrees != null) try { hands.CleanWorktrees(dir, id); } catch (Exception) { }
        Tell();
        return true;
    }
    Team Of(string id) { lock (gate) { var t = items.FirstOrDefault(x => x.Id == id); return t == null ? null : t.Copy(); } }

    static string ResolveRole(Team team, string wanted) {
        if (String.IsNullOrEmpty(wanted)) return null;
        if (team.Members.ContainsKey(wanted)) return wanted;
        if (team.Phase == "discuss" && team.Members.ContainsKey("discuss:" + wanted)) return "discuss:" + wanted;
        if (team.Roster.Length > 0) return RosterOf(team, wanted) == null ? null : wanted;
        if (team.Mode == "review") {
            if (wanted == team.Tools[0]) return "writer";
            if (wanted == team.Tools[1]) return "reviewer";
        }
        if (team.Mode == "lead" && wanted == team.Tools[0]) return "leader";
        if (team.Tools.Contains(wanted)) return wanted;
        return null;
    }

    void DeliverOrReopen(Team team, string role, string terminal, string words) {
        if (!String.IsNullOrEmpty(terminal)) {
            try { hands.Say(terminal, words); return; }
            catch (Exception) { }
        }
        string tool = ToolOf(team, role);
        string wt = null;
        if (team.Mode == "compare") lock (gate) team.Worktrees.TryGetValue(tool, out wt);
        bool read = role == "reviewer" || role.StartsWith("discuss:", StringComparison.Ordinal);
        string prompt;
        if (role.StartsWith("discuss:", StringComparison.Ordinal)) prompt = DiscussFirst(team, tool) + "\n\n" + words;
        else if (team.Mode == "compare") prompt = CompareFirst(team, tool) + "\n\n" + words;
        else if (team.Mode == "review" && role == "writer") prompt = WriterFirst(team) + (words == "继续完成上面的任务。" ? "" : "\n\n" + words);
        else if (team.Mode == "review" && role == "reviewer") {
            string changes = "";
            try { changes = hands.Changes(team.Dir) ?? ""; } catch (Exception) { }
            prompt = ReviewerAsk(team, "（重新接续审查）" + (words == "继续完成上面的任务。" ? "" : "\n" + words), changes);
        } else if (team.Mode == "lead" && role == "leader") {
            // a new conversation of the leader's: told where the task stands, its acceptance too where that is under way
            string verify = "";
            if (team.Phase == "verify") {
                string changes = "";
                try { changes = hands.Changes(team.Dir) ?? ""; } catch (Exception) { }
                verify = "\n\n" + VerifyAsk(team, changes, Rooms(team), team.Last);
            } else if (team.Phase == "decision") {
                string pending;
                lock (gate) team.Pending.TryGetValue(role, out pending);
                verify = "\n\n" + (pending ?? DecideNext);
            }
            prompt = LeaderFirst(team, Rooms(team)) + verify + (words == "继续完成上面的任务。" ? "" : "\n\n" + words);
        }
        else {
            string pending;
            lock (gate) team.Pending.TryGetValue(role, out pending);
            prompt = (pending ?? MemberTask(team, team.Goal)) + "\n\n" + words;
        }
        Open(team, role, tool, prompt, read, wt);
    }

    // ---- the members' turns
    void Open(Team team, string role, string tool, string words, bool read, string folderOverride) {
        string model, effort;
        lock (gate) {
            opening++;
            team.Pending[role] = Cut(words, 6000);
            var member = RosterOf(team, role);
            model = member != null ? member.Model : team.Models.ContainsKey(tool) ? team.Models[tool] : "";
            effort = member != null ? member.Effort : team.Efforts.ContainsKey(tool) ? team.Efforts[tool] : "";
        }
        try {
            string terminal = hands.Begin(new Opening { Tool = tool, Project = team.Dir, Words = words, Read = read, Folder = folderOverride ?? "", Model = model ?? "", Effort = effort ?? "" });
            lock (gate) {
                team.Members[role] = terminal;
                if (!team.Opened.Contains(terminal)) team.Opened.Add(terminal);
                team.Sessions.Remove(role);
                team.Failed.Remove(role);
                team.Failed.Remove(tool);
                if (!team.Awaited.Contains(role)) team.Awaited.Add(role);
                opening--; Save(); Monitor.PulseAll(gate);
            }
            Log(team, role, "开始：" + Cut(words, 240));
        } catch (Exception problem) {
            lock (gate) {
                team.State = team.Members.Count == 0 && team.Log.Count <= 1 ? "failed" : "paused";
                team.Note = Label(tool) + " 没有开始：" + (problem is ArgumentException || problem is InvalidOperationException ? problem.Message : "电脑端出错");
                if (team.State == "failed") team.Awaited.Remove(role);
                else if (!team.Awaited.Contains(role)) team.Awaited.Add(role);
                opening--; Save(); Monitor.PulseAll(gate);
            }
            Tell();
        }
    }
    void Ask(Team team, string role, string tool, string words, bool read, string folderOverride = null) {
        string terminal;
        lock (gate) {
            team.Members.TryGetValue(role, out terminal);
            if (role == "leader" && team.LeaderInput.Length > 0) {
                words += "\n\n【用户补充要求】\n" + team.LeaderInput;
                team.LeaderInput = "";
            }
            team.Pending[role] = Cut(words, 6000);
            if (!team.Awaited.Contains(role)) team.Awaited.Add(role);
            Save();
        }
        if (terminal != null) {
            try { hands.Say(terminal, words); Log(team, role, "收到：" + Cut(words, 240)); return; }
            catch (Exception) { }       // its conversation was closed: begun anew
        }
        Open(team, role, tool, words, read, folderOverride);
    }
    void Log(Team team, string who, string text) {
        lock (gate) {
            team.Log.Add(new Step { At = Now(), Who = who, Text = Cut(text, 4000) });
            if (team.Log.Count > 200) team.Log.RemoveRange(0, team.Log.Count - 200);
            Save();
        }
        Tell();
    }
    void End(Team team, string state, string note) {
        lock (gate) { team.State = state; team.Note = note; team.Awaited.Clear(); Save(); }
        Log(team, "结果", note);
    }

    /// A member's turn ended (Chat.Turned): what it said is passed on by the team's way of working.
    public void Turned(string terminal, string how, string said) {
        Team team; string role;
        lock (gate) {
            team = items.FirstOrDefault(t => t.State == "running" && t.Members.ContainsValue(terminal));
            if (team == null && opening > 0) {
                Monitor.Wait(gate, 200);
                team = items.FirstOrDefault(t => t.State == "running" && t.Members.ContainsValue(terminal));
            }
            if (team == null) return;
            role = team.Members.First(p => p.Value == terminal).Key;
            if (!team.Awaited.Contains(role)) return;      // (a turn of its own owner's, from the phone)
            team.Awaited.Remove(role);
            if (team.Mode == "lead" && team.Phase != "discuss" && role != "leader" && how != "stopped") team.Finishing.Add(role);
            Save();
        }
        Log(team, role, Cut(said, 4000));
        if (team.Mode == "compare") { Compared(team, role, how, said); return; }
        if (how == "done") {
            lock (gate) {
                bool cleared = team.Failed.Remove(role) | team.Failed.Remove(ToolOf(team, role));
                if (cleared) Save();
            }
        } else if (how == "stopped") {
            lock (gate) { team.State = "paused"; team.Note = Label(ToolOf(team, role)) + " 这一轮被停止：点“继续”接着做，或从右上角“⋯”换 AI 接手"; if (!team.Awaited.Contains(role)) team.Awaited.Add(role); Save(); }
            Tell();
            return;
        } else {
            HandleFailure(team, role, said);
            return;
        }
        if (team.Phase == "discuss") { Discussed(team, role, said); return; }
        if (team.Mode == "review") Reviewed(team, role, said); else Led(team, role, said);
    }

    static string FailureReason(string said) {
        if (IsQuotaError(said)) return "额度已用尽";
        string oneLine = Regex.Replace((said ?? "").Trim(), @"\s+", " ");
        return oneLine.Length > 0 ? Cut(oneLine, 140) : "执行出错";
    }

    /// Handles a member failure (`how == "failed"`, e.g. quota exhausted) without breaking the whole team when a
    /// backup AI can continue: reports member failures back to the leader in `lead` mode, or auto-swaps to a backup AI.
    void HandleFailure(Team team, string role, string said) {
        string failedTool = ToolOf(team, role);
        string reason = FailureReason(said);
        string pending = "";
        lock (gate) {
            string failedKey = team.Roster.Length > 0 && reason != "额度已用尽" ? role : failedTool;
            team.Failed[failedKey] = reason;
            team.Pending.TryGetValue(role, out pending);
            Save();
        }

        // Read-only discussion: do not halt the team if one participant fails.
        if (team.Phase == "discuss") {
            Log(team, "切换", Label(failedTool) + " 讨论阶段失败（" + reason + "），其余成员继续");
            Discussed(team, role, "（" + Label(failedTool) + " 未完成讨论：" + reason + "）");
            return;
        }

        // Lead mode, worker failed: collect alongside parallel members and report to leader for reassignment.
        if (team.Mode == "lead" && role != "leader") {
            string report = "【执行失败】" + Label(failedTool) + " 这一轮失败（" + reason + "）。"
                + (!String.IsNullOrWhiteSpace(said) && said.Trim() != reason ? "\n报错信息：" + Cut(said, 600) : "")
                + (pending.Length > 0 ? "\n原分派任务：" + Cut(pending, 800) : "")
                + "\n请将该未完成任务改派给其他可用成员，不要再派给 " + Label(failedTool) + "。";
            Dictionary<string, string> answers = null;
            string addedBackup = null;
            bool pauseNoMember = false;
            lock (gate) {
                team.Answers[role] = report;
                team.Finishing.Remove(role);
                if (MembersWorking(team)) { Save(); }
                else {
                    answers = new Dictionary<string, string>(team.Answers);
                    team.Answers.Clear();
                    bool hasLiveMember = LeadMembers(team).Any(r => !FailedRole(team, r) && !IsExhausted(SafeLimits(ToolOf(team, r))));
                    if (!hasLiveMember && team.Roster.Length == 0) {
                        addedBackup = FallbackTool(team, failedTool, team.Tools[0]);
                        if (addedBackup != null) {
                            if (!team.Tools.Contains(addedBackup)) team.Tools = team.Tools.Concat(new[] { addedBackup }).ToArray();
                        } else {
                            pauseNoMember = true;
                            team.State = "paused";
                            team.Note = Label(failedTool) + " 失败（" + reason + "），且没有其他可用成员：可从右上角“⋯”换 AI 接手，或稍后继续";
                            team.Awaited.Clear();
                            team.Awaited.Add("leader");
                        }
                    }
                    Save();
                }
            }
            Log(team, "切换", addedBackup != null
                ? Label(failedTool) + " 失败（" + reason + "），已自动加入 " + Label(addedBackup) + " 并回报领队改派"
                : Label(failedTool) + " 失败（" + reason + "），已回报领队改派给其他可用成员");
            if (answers == null || pauseNoMember) { Tell(); return; }
            // a round where every member failed is not counted: the leader gives the work out again
            bool anyOk = answers.Values.Any(v => !v.StartsWith("【执行失败】", StringComparison.Ordinal));
            Collected(team, answers, anyOk, addedBackup != null ? "\n\n【自动补充成员】已将 " + Label(addedBackup) + "（" + addedBackup + "）加入协作队伍，可直接用 ASSIGN " + addedBackup + ": 任务内容 改派。" : "");
            return;
        }

        // Lead mode, leader itself failed: promote a backup AI to leader if available.
        if (team.Mode == "lead") {
            string backup = FallbackTool(team, failedTool);
            if (backup != null) {
                lock (gate) {
                    PromoteLeader(team, backup);
                    team.Failed.Remove("leader");
                    team.Failed.Remove(backup);
                    team.Members.Remove("leader");
                    team.Awaited.Clear();
                    team.Awaited.Add("leader");
                    Save();
                }
                Log(team, "切换", Label(failedTool) + "（领队）失败（" + reason + "），已自动由 " + Label(backup) + " 接任领队继续");
                Open(team, "leader", backup, HandoffPrompt(team, "leader", failedTool, "因 " + reason + " 中断", pending, ""), false, null);
                return;
            }
            lock (gate) {
                team.State = "paused";
                team.Note = Label(failedTool) + "（领队）失败（" + reason + "），没有可用备用 AI：可从右上角“⋯”换 AI 接手，或稍后继续";
                if (!team.Awaited.Contains("leader")) team.Awaited.Add("leader");
                Save();
            }
            Tell();
            return;
        }

        // Review mode: auto-swap failed writer/reviewer to an available third AI if present; otherwise pause for manual swap.
        int slot = role == "writer" ? 0 : 1;
        string other = team.Tools[1 - slot];
        string replacement = FallbackTool(team, failedTool, other);
        if (replacement != null) {
            lock (gate) {
                team.Tools[slot] = replacement;
                team.Failed.Remove(role);
                team.Failed.Remove(replacement);
                team.Members.Remove(role);
                team.Awaited.Clear();
                team.Awaited.Add(role);
                Save();
            }
            Log(team, "切换", Label(failedTool) + "（" + (role == "writer" ? "写手" : "审查") + "）失败（" + reason + "），已自动切换为 " + Label(replacement) + " 继续");
            Open(team, role, replacement, HandoffPrompt(team, role, failedTool, "因 " + reason + " 中断", pending, ""), role == "reviewer", null);
            return;
        }
        lock (gate) {
            team.State = "paused";
            team.Note = Label(failedTool) + " 失败（" + reason + "），没有其他可用备用 AI：可从右上角“⋯”换 AI 接手，或稍后继续";
            if (!team.Awaited.Contains(role)) team.Awaited.Add(role);
            Save();
        }
        Tell();
    }

    static string ToolOf(Team team, string role) {
        var member = RosterOf(team, role);
        if (member != null) return member.Tool;
        if (role.StartsWith("discuss:", StringComparison.Ordinal)) return role.Substring(8);
        return role == "writer" || role == "leader" ? team.Tools[0] : role == "reviewer" ? team.Tools[1] : role;
    }
    static string[] LeadMembers(Team team) {
        return team.Roster.Length == 0 ? team.Tools.Skip(1).ToArray() : team.Roster.Skip(1).Select(member => member.Role).ToArray();
    }
    static RosterMember RosterOf(Team team, string role) {
        string original = role.StartsWith("discuss:", StringComparison.Ordinal) ? role.Substring(8) : role;
        return team.Roster.FirstOrDefault(member => member.Role == original);
    }
    static void SetRosterMember(Team team, string role, string tool, string model, string effort = "") {
        team.Roster = team.Roster.Where(member => member.Role != role).Concat(new[] { new RosterMember(role, tool, model, effort) })
            .OrderBy(member => member.Role == "leader" ? -1 : Array.IndexOf(MemberRoles, member.Role)).ToArray();
        team.Tools = team.Roster.Select(member => member.Tool).ToArray();
    }
    static bool FailedRole(Team team, string role) {
        return team.Failed.ContainsKey(role) || team.Failed.ContainsKey(ToolOf(team, role));
    }
    static bool MembersWorking(Team team) {
        return team.Finishing.Count > 0 || team.Awaited.Any(role => role != "leader");
    }
    static string RoleLabel(Team team, string role) {
        var member = RosterOf(team, role);
        return member == null ? Label(ToolOf(team, role)) : member.Role + " · " + Label(member.Tool);
    }

    void Discussed(Team team, string role, string said) {
        Dictionary<string, string> collected = null;
        lock (gate) {
            team.Answers[role] = said;
            if (team.Awaited.Any(r => r.StartsWith("discuss:", StringComparison.Ordinal))) { Save(); return; }
            collected = new Dictionary<string, string>(team.Answers);
            team.Answers.Clear();
            team.Phase = "";
            Save();
        }
        string discussion = String.Join("\n\n", collected.Select(p => "### " + RoleLabel(team, p.Key) + "\n" + Cut(p.Value, 5000)));
        if (team.Mode == "review") {
            string writerTool = team.Tools[0];
            bool writerFailed; lock (gate) writerFailed = team.Failed.ContainsKey(writerTool);
            if (writerFailed) {
                string backup = FallbackTool(team, writerTool, team.Tools[1]);
                if (backup != null) {
                    lock (gate) { team.Tools[0] = backup; team.Failed.Remove("writer"); team.Failed.Remove(backup); Save(); }
                    Log(team, "切换", Label(writerTool) + " 讨论阶段不可用，写手已自动切换为 " + Label(backup));
                    writerTool = backup;
                }
            }
            Open(team, "writer", writerTool, WriterFirst(team, discussion), false, null);
        } else {
            string leaderTool = team.Tools[0];
            bool leaderFailed; lock (gate) leaderFailed = team.Failed.ContainsKey(leaderTool);
            if (leaderFailed) {
                string backup = FallbackTool(team, leaderTool);
                if (backup != null) {
                    lock (gate) { PromoteLeader(team, backup); team.Failed.Remove("leader"); team.Failed.Remove(backup); Save(); }
                    Log(team, "切换", Label(leaderTool) + " 不可用，领队已自动切换为 " + Label(backup));
                    leaderTool = backup;
                }
            }
            Ask(team, "leader", leaderTool, LeaderFirst(team, Rooms(team), discussion), false);
        }
    }

    void Compared(Team team, string tool, string how, string said) {
        string wt = null, baseCommit = null;
        lock (gate) { team.Worktrees.TryGetValue(tool, out wt); team.BaseCommits.TryGetValue(tool, out baseCommit); }
        string diff = "", stat = "";
        if (wt != null) {
            try { if (hands.WorktreeDiff != null) diff = hands.WorktreeDiff(wt, baseCommit) ?? ""; } catch (Exception) { }
            try { if (hands.WorktreeStat != null) stat = hands.WorktreeStat(wt, baseCommit) ?? ""; } catch (Exception) { }
        }
        bool allDone; bool anyOk;
        lock (gate) {
            team.Summaries[tool] = how == "done" ? Cut(said, 6000) : (String.IsNullOrWhiteSpace(said) ? (how == "stopped" ? "（这一轮已停止）" : "（执行出错）") : Cut(said, 6000));
            team.Diffs[tool] = Cut(diff, 30000);
            team.Stats[tool] = stat.Length > 0 ? stat : (how == "done" ? "没有文件改动" : "未完成");
            allDone = team.Awaited.Count == 0;
            anyOk = team.Summaries.Values.Any(s => !s.StartsWith("（执行出错", StringComparison.Ordinal) && !s.StartsWith("（这一轮已停止", StringComparison.Ordinal));
            if (allDone) {
                team.State = anyOk ? "choose" : "paused";
                team.Note = anyOk ? "各候选方案已完成，对比改动后选一个合并到主目录" : "候选 AI 均未完成，请检查后重试或删除";
            }
            Save();
        }
        if (allDone) Log(team, "结果", anyOk ? "候选方案已就绪，等待对比选择" : "候选方案未完成");
        else Tell();
    }

    void Reviewed(Team team, string role, string said) {
        if (role == "writer") {
            string changes = "";
            try { changes = hands.Changes(team.Dir) ?? ""; } catch (Exception) { }
            Ask(team, "reviewer", team.Tools[1], ReviewerAsk(team, said, changes), true);
            return;
        }
        bool? verdict = Verdict(said);
        if (verdict == true) { End(team, "done", "审查通过（第 " + team.Round + " 轮）"); return; }
        // (rounds running out is no pass: it waits with the findings for its owner to add a round)
        if (team.Round >= team.Rounds) { Hold(team, new Dictionary<string, string> { { "writer", said } }, "最后一轮的审查意见还没有处理，审查未通过"); return; }
        lock (gate) { team.Round++; Save(); }
        Ask(team, "writer", team.Tools[0], WriterAgain(team, said), false);
    }
    void Led(Team team, string role, string said) {
        if (role == "leader") {
            string summary = Finished(said);
            bool verifying, deciding, last, hasInput;
            lock (gate) { verifying = team.Phase == "verify"; deciding = team.Phase == "decision"; last = team.Last; hasInput = team.LeaderInput.Length > 0; }
            if (hasInput) {
                Ask(team, "leader", team.Tools[0], verifying ? VerifyAsk(team, Changes(team), Rooms(team), last)
                    : "【协作任务 · 用户补充】\n" + (deciding ? DecideNext + "\n" : "") + SupplementGuide, false);
                return;
            }
            var tasks = team.Roster.Length == 0 ? Assigned(said, LeadMembers(team)) : AssignRoster(team, said, !last);
            if (tasks.Count > 0) {
                if (last) { Hold(team, tasks, verifying ? "领队验收时仍要求修改，还没有通过验收" : "本轮仍有工作需要完成"); return; }
                lock (gate) {
                    if (verifying || deciding) team.Round++;
                    team.Phase = "";
                    team.Reminded = false; team.Answers.Clear(); team.Awaited.Clear(); team.Nudged.Clear();
                    foreach (string member in tasks.Keys) if (!team.Awaited.Contains(member)) team.Awaited.Add(member);
                    Save();
                }
                foreach (var task in tasks) Ask(team, task.Key, ToolOf(team, task.Key), MemberTask(team, task.Value), false);
                return;
            }
            // a DONE outside the acceptance turn opens it; within it, only with what was checked
            if (summary != null && !verifying) { Verify(team); return; }
            string evidence = summary != null ? Verified(said) : null;
            if (evidence != null) {
                Log(team, "验收", evidence);
                End(team, "done", "领队验收通过：" + Cut(summary, 600));
                return;
            }
            bool reminded; lock (gate) { reminded = team.Reminded; team.Reminded = true; Save(); }
            if (reminded) {
                if (last) { Hold(team, new Dictionary<string, string>(), verifying ? "领队没有给出验收证据，还没有通过验收" : "仍有未完成分工，领队未给出处理决定"); return; }
                lock (gate) { team.State = "paused"; team.Note = verifying ? "领队没有给出验收证据：点“继续”再问它一次" : "领队没有分派任务，也没有结束：点“继续”再问它一次"; team.Awaited.Clear(); team.Awaited.Add("leader"); Save(); }
                Tell();
                return;
            }
            Ask(team, "leader", team.Tools[0], verifying ? VerifyRemind : Remind, false);
            return;
        }
        if (Progress(said) && Nudge(team, role)) return;
        Dictionary<string, string> answers = null;
        lock (gate) {
            team.Answers[role] = said;
            team.Finishing.Remove(role);
            if (MembersWorking(team)) { Save(); return; }
            answers = new Dictionary<string, string>(team.Answers); team.Answers.Clear(); Save();
        }
        Collected(team, answers, true, "");
    }
    /// Sends a member back to finish (`KeepAtIt`), where it was not sent back too often in this round already.
    bool Nudge(Team team, string role) {
        string terminal; int times;
        lock (gate) {
            team.Nudged.TryGetValue(role, out times);
            if (times >= MostNudges) return false;
            team.Nudged[role] = times + 1;
            team.Members.TryGetValue(role, out terminal);
            team.Finishing.Remove(role);
            if (!team.Awaited.Contains(role)) team.Awaited.Add(role);
            Save();
        }
        Log(team, "继续", RoleLabel(team, role) + " 交回的是进度说明，已请它做完再交回");
        DeliverOrReopen(team, role, terminal, KeepAtIt);
        return true;
    }
    /// The rounds are spent while the work still wants more (`why`): it waits for its owner with `held` kept, and
    /// “继续” adds a round (`Extend`). Past the most rounds a task may have it stops, as no pass.
    void Hold(Team team, Dictionary<string, string> held, string why) {
        if (team.Round >= MaxTotalRounds) { End(team, "stopped", "已到 " + team.Round + " 轮，" + why); return; }
        lock (gate) {
            team.State = "paused"; team.Spent = true; team.Held = new Dictionary<string, string>(held);
            team.Note = "已到 " + team.Round + " 轮，" + why + "：点“继续”追加一轮" + (held.Count > 0 ? "，保留的修改随后派出" : "");
            team.Awaited.Clear();
            foreach (string role in held.Count > 0 ? held.Keys.ToArray() : new[] { "leader" }) team.Awaited.Add(role);
            Save();
        }
        Log(team, "结果", team.Note);
        Tell();
    }
    /// One more round for work whose rounds were spent: what was kept goes out, with the owner's `words` where there
    /// are any; with nothing kept the leader is asked to go on.
    void Extend(Team team, string words) {
        Dictionary<string, string> held;
        lock (gate) {
            if (!team.Spent) return;        // (given already)
            team.Round++; if (!team.Free) team.Rounds = Math.Min(MaxTotalRounds, Math.Max(team.Rounds, team.Round));
            team.State = "running"; team.Note = ""; team.Phase = ""; team.Last = false; team.Reminded = false; team.Spent = false;
            team.Answers.Clear(); team.Awaited.Clear(); team.Nudged.Clear();
            held = team.Held; team.Held = new Dictionary<string, string>();
            if (held.Count == 0) held["leader"] = "";
            foreach (string role in held.Keys) team.Awaited.Add(role);
            Save();
        }
        Log(team, "继续", "追加第 " + team.Round + " 轮");
        string added = words.Length > 0 ? "\n\n【用户补充】\n" + words : "";
        foreach (var one in held) {
            string prompt = one.Key == "writer" ? WriterAgain(team, one.Value)
                : one.Key == "leader" ? "【协作任务 · 第 " + team.Round + " 轮】用户追加了一轮。请按上一轮验收的结论继续：用 ASSIGN 成员名: 修改内容 派出还要做的修复，或自己修好后写 DONE: 总结。"
                : MemberTask(team, one.Value);
            Ask(team, one.Key, ToolOf(team, one.Key), prompt + added, false);
        }
        Tell();
    }
    Dictionary<string, string> AssignRoster(Team team, string said, bool canAssign) {
        var tasks = new Dictionary<string, string>();
        var retired = new List<string>();
        var available = hands.Available == null ? team.Tools : hands.Available() ?? new string[0];
        lock (gate) {
            foreach (var one in Assignments(said)) {
                Match match = one.Item1;
                string role = match.Groups[1].Value.ToLowerInvariant();
                if (!MemberRoles.Contains(role)) continue;
                var member = RosterOf(team, role);
                string requested = match.Groups[2].Value.ToLowerInvariant();
                string tool = requested.Length > 0 ? requested : member == null ? "" : member.Tool;
                string task = one.Item2;
                if (task.Length == 0 || (requested.Length > 0 && !team.AutoRoster) || !available.Contains(tool) ||
                    team.Failed.ContainsKey(tool) || IsExhausted(SafeLimits(tool)) ||
                    (team.Failed.ContainsKey(role) && member != null && member.Tool == tool))
                    continue;
                if (canAssign && (member == null || member.Tool != tool)) {
                    if (tasks.ContainsKey(role)) continue;
                    string old;
                    if (team.Members.TryGetValue(role, out old)) retired.Add(old);
                    team.Members.Remove(role);
                    team.Sessions.Remove(role);
                    team.Failed.Remove(role);
                    SetRosterMember(team, role, tool, "");
                }
                tasks[role] = tasks.ContainsKey(role) ? tasks[role] + "\n" + task : task;
            }
        }
        foreach (string terminal in retired) try { hands.Stop(terminal); } catch (Exception) { }
        return tasks;
    }
    /// The last member hands the batch back for verification or a decision about unfinished work.
    /// Legacy teams can retry a batch in the same round when every member failed to start the work.
    void Collected(Team team, Dictionary<string, string> answers, bool counted, string added) {
        bool deciding = answers.Values.Any(answer => !answer.StartsWith("【执行失败】", StringComparison.Ordinal)
            && String.IsNullOrEmpty(Finished(answer)) && (Blocked.IsMatch(answer) || Progress(answer)));
        bool verifying, last;
        lock (gate) {
            if (deciding || counted || team.Roster.Length > 0) {
                team.Phase = deciding ? "decision" : "verify";
                team.Last = team.Round >= team.Cap;
                team.Reminded = false;
            }
            verifying = team.Phase == "verify"; last = team.Last;
            Save();
        }
        string changes = Changes(team);
        var rooms = Rooms(team);
        string guide = deciding ? DecideNext + (last ? " 本轮已到上限，新的 ASSIGN 待用户追加一轮后发出。" : "") : "";
        string body = verifying ? Answers(team, answers) + "\n\n" + VerifyAsk(team, changes, rooms, last) : Results(team, answers, changes, rooms, guide);
        Ask(team, "leader", team.Tools[0], body + added, false);
    }
    /// The leader said the work is done: a turn of its own to accept it, with what changed in front of it.
    void Verify(Team team) {
        lock (gate) { team.Phase = "verify"; team.Last = team.Round >= team.Cap; team.Reminded = false; Save(); }
        Log(team, "验收", "领队表示已完成，开始单独验收");
        Ask(team, "leader", team.Tools[0], VerifyAsk(team, Changes(team), Rooms(team), team.Last), false);
    }
    string Changes(Team team) {
        try { return hands.Changes(team.Dir) ?? ""; }
        catch (Exception) { return ""; }
    }
    static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }
}
}
