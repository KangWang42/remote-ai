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
        public int Rounds = 3, Round = 1;
        public long Created;
        public List<Step> Log = new List<Step>();
        /// Each member's conversation: role -> terminal ("writer", "reviewer", "leader", "discuss:<tool>", or tool name).
        public Dictionary<string, string> Members = new Dictionary<string, string>();
        /// Whose turn is awaited (roles), and what those who answered said in this round (lead / discuss).
        public List<string> Awaited = new List<string>();
        public Dictionary<string, string> Answers = new Dictionary<string, string>();
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
        internal Team Copy() {
            var copy = (Team)MemberwiseClone();
            copy.Log = Log.ToList(); copy.Members = new Dictionary<string, string>(Members); copy.Awaited = Awaited.ToList();
            copy.Answers = new Dictionary<string, string>(Answers); copy.Tools = Tools.ToArray();
            copy.Worktrees = new Dictionary<string, string>(Worktrees); copy.BaseCommits = new Dictionary<string, string>(BaseCommits);
            copy.Summaries = new Dictionary<string, string>(Summaries); copy.Stats = new Dictionary<string, string>(Stats);
            copy.Diffs = new Dictionary<string, string>(Diffs);
            copy.Failed = new Dictionary<string, string>(Failed); copy.Pending = new Dictionary<string, string>(Pending);
            return copy;
        }
    }
    /// What the program does for a team: begin a member's conversation (optionally in a worktree folder), tell it
    /// something, stop one, read project changes, read quota limits, list available chat tools, and manage Git worktrees for `compare`.
    public sealed class Hands {
        public Func<string, string, string, bool, string> Begin;                 // tool, project, first words, read only -> terminal
        public Func<string, string, string, bool, string, string> BeginIn;       // tool, project, first words, read only, folderOverride -> terminal
        public Action<string, string> Say;                                       // terminal, words
        public Action<string> Stop;                                              // terminal
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
            Reminded = String.Equals(word("reminded"), "True", StringComparison.OrdinalIgnoreCase),
            Tools = many("tools").Select(Convert.ToString).ToArray(),
            Members = map("members"), Answers = map("answers"), Awaited = many("awaited").Select(Convert.ToString).ToList(),
            Worktrees = map("worktrees"), BaseCommits = map("base_commits"),
            Summaries = map("summaries"), Stats = map("stats"), Diffs = map("diffs"),
            Failed = map("failed"), Pending = map("pending")
        };
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
            { "discuss", t.Discuss }, { "reminded", t.Reminded },
            { "tools", t.Tools }, { "rounds", t.Rounds }, { "round", t.Round }, { "created", t.Created },
            { "members", t.Members }, { "awaited", t.Awaited }, { "answers", t.Answers },
            { "worktrees", t.Worktrees }, { "base_commits", t.BaseCommits },
            { "summaries", t.Summaries }, { "stats", t.Stats }, { "diffs", t.Diffs },
            { "failed", t.Failed }, { "pending", t.Pending },
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

    public Team[] All() { lock (gate) return items.Select(t => t.Copy()).ToArray(); }
    /// As the phone is shown it.
    public static Dictionary<string, object> Shown(Team t) {
        var shown = new Dictionary<string, object> {
            { "id", t.Id }, { "dir", t.Dir }, { "mode", t.Mode }, { "goal", t.Goal }, { "rules", t.Rules },
            { "state", t.State }, { "note", t.Note }, { "phase", t.Phase }, { "discuss", t.Discuss }, { "picked", t.Picked },
            { "tools", t.Tools }, { "rounds", t.Rounds }, { "round", t.Round }, { "created", t.Created },
            { "members", t.Members }, { "awaited", t.Awaited.ToArray() }, { "failed", t.Failed },
            { "log", t.Log.Skip(Math.Max(0, t.Log.Count - 80)).Select(s => new Dictionary<string, object> { { "at", s.At }, { "who", s.Who }, { "text", s.Text } }).ToArray() }
        };
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

    // ---- what the members are told (read by the models: changed only together with runs on the real tools)
    public static string Label(string tool) { return RemoteCli.Tool.LabelOf(tool); }
    static readonly Dictionary<string, string> Strengths = new Dictionary<string, string> {
        { "codex", "擅长决策拍板、任务拆解、精准改代码与修测试（优先负责决策，纯检查不必浪费其额度）" },
        { "claude", "擅长主力写作（代码实现、重构与文档撰写）以及深度交叉审查" },
        { "agy", "Gemini 模型，擅长 UI 与前端界面设计、长上下文检索，也可兼顾写作与交叉检查" } };
    /// Whether any main quota window has reached 100%.
    public static bool IsExhausted(Quotas.Window[] windows) { return windows != null && windows.Any(w => Math.Round(w.Used) >= 100); }
    static readonly Regex QuotaErrorPattern = new Regex(@"(hit your limit|rate[\s_-]*limit|usage[\s_-]*limit|quota|out of credits|credit balance|too many requests|\b429\b|overloaded|额度|限额|已达上限|额度用尽|额度已用完|额度用完)", RegexOptions.IgnoreCase);
    /// Whether a member's failure message indicates quota or rate-limit exhaustion.
    public static bool IsQuotaError(string text) { return !String.IsNullOrWhiteSpace(text) && QuotaErrorPattern.IsMatch(text); }
    /// A tool's limits in words for the leader; 80%+ is tight and 100% is exhausted.
    public static string Room(Quotas.Window[] windows) {
        if (windows == null || windows.Length == 0) return "额度未知";
        var said = windows.Select(w => (w.Name == "five_hour" ? "5 小时窗口" : w.Name == "seven_day" ? "本周" : w.Name) + "已用 " + Math.Round(w.Used) + "%"
            + (w.Resets > 0 ? "（" + When(w.Resets) + " 重置）" : "")).ToArray();
        string tag = IsExhausted(windows) ? "，已用尽（不可派工）" : windows.Any(w => Math.Round(w.Used) >= 80) ? "，紧张" : "";
        return "额度：" + String.Join("，", said) + tag;
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
        if (hands.Limits == null && failed.Count == 0) return rooms;
        foreach (string tool in list) {
            string why;
            bool isFailed = failed.TryGetValue(tool, out why);
            if (hands.Limits != null) {
                string room = Room(SafeLimits(tool));
                rooms[tool] = isFailed ? room + "；当前不可用（" + why + "，请勿再派工）" : room;
            } else if (isFailed) {
                rooms[tool] = "不可用（" + why + "，请勿再派工）";
            }
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
        return rooms.Count > 0 ? "\n\n## 当前额度\n" + String.Join("\n", t.Tools.Distinct().Where(rooms.ContainsKey).Select(tool => "- " + Label(tool) + (tool == t.Tools[0] ? "（你）" : "") + "：" + rooms[tool])) : "";
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
        var members = t.Tools.Skip(1).Select(tool => "- " + tool + "：" + Label(tool) + (Strengths.ContainsKey(tool) ? "，" + Strengths[tool] : "") + room(tool)).ToArray();
        return "【协作任务 · 领队】目标：" + t.Goal + ExtraRules(t)
            + (String.IsNullOrEmpty(discussion) ? "" : "\n\n## 成员前期方案讨论（只读）\n" + Cut(discussion, 12000))
            + "\n\n你负责决策、按任务特点高效干净地分工、验收结果并在达标时结束。成员都在同一个项目文件夹中工作：\n" + String.Join("\n", members)
            + (rooms.ContainsKey(t.Tools[0]) ? "\n你自己（" + Label(t.Tools[0]) + "）的" + rooms[t.Tools[0]] : "")
            + "\n\n分工原则：\n1. 按具体任务按需派工，不必每次把所有成员都派满；不需要的成员可以不派。\n2. 并行分派时写清各自负责的文件和验收标准，避免多人改同一批文件。\n3. 需要核验时采用交叉检查（谁写不由谁审），优先让 Claude Code 或 Antigravity CLI 交叉检查，不必把纯检查浪费在 Codex 上。\n4. 结果达标后立刻结束，不要为了凑轮次空转。"
            + (rooms.Count > 0 ? "\n5. 按额度分配工作量：额度已用尽或不可用的成员不要再派工；额度紧张（任一窗口已用 80% 以上）的成员少派或不派，只给小而必要的任务，工作多交给额度充足的成员；你自己的额度紧张时，回答保持简短，把实现和检查交给成员。额度未知的成员按正常对待。" : "")
            + "\n\n分派时，在回答的最后每行写一条（成员名用上面的英文名）：\nASSIGN 成员名: 任务内容\n全部完成时，回答的最后单独一行写：\nDONE: 总结\n分派出去的工作不要自己动手做。";
    }
    public static string MemberTask(Team t, string task) {
        return "【协作任务 · 成员】领队（" + Label(t.Tools[0]) + "）分派给你的任务：\n" + task + "\n\n整体目标：" + t.Goal + "\n\n完成后简要说明你做了什么、改了哪些文件。";
    }
    public static string CompareFirst(Team t, string tool) {
        return "【协作任务 · 并行比稿】目标：" + t.Goal + ExtraRules(t)
            + "\n\n你正在该项目的独立 Git worktree 副本中工作，不会与其他候选 AI 冲突。请直接完成你的最佳实现并验证关键路径，完成后简明说明你的方案思路、改了哪些文件以及取舍理由。用户会对比各候选方案并选择一个合并。";
    }
    public static string Results(Team t, Dictionary<string, string> answers, string changes, IDictionary<string, string> rooms) {
        return "【协作任务 · 成员结果 · 第 " + t.Round + " 轮】\n\n" + String.Join("\n\n", answers.Select(p => "## " + Label(p.Key) + "\n" + Cut(p.Value, 8000)))
            + (changes.Length > 0 ? "\n\n## 文件夹中的改动\n" + Cut(changes, 20000) : "")
            + RoomsBlock(t, rooms)
            + "\n\n请检查结果，再分派下一步（ASSIGN 成员名: 任务）或结束（DONE: 总结）。";
    }
    const string Remind = "【协作任务】没有在你的回答里找到分派或结束。请在回答的最后按格式写：\nASSIGN 成员名: 任务内容（可以多行）\n或单独一行：\nDONE: 总结";
    static string Cut(string text, int most) { text = text ?? ""; return text.Length > most ? text.Substring(0, most) + "\n…（后面省略）" : text; }

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
    static readonly Regex Assign = new Regex(@"^[\s>*\-_`]*ASSIGN[*_`]*\s+[*_`]*(claude|codex|agy)[*_`]*\s*[:：]\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    static readonly Regex Done = new Regex(@"^[\s>*\-_`]*DONE[*_`]*\s*[:：]\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    /// The leader's tasks for its members (one line each; a member named twice is given both), of the members it has.
    public static Dictionary<string, string> Assigned(string said, IEnumerable<string> members) {
        var tasks = new Dictionary<string, string>();
        var known = new HashSet<string>(members);
        foreach (Match m in Assign.Matches(said ?? "")) {
            string tool = m.Groups[1].Value.ToLowerInvariant(), task = m.Groups[2].Value.Trim();
            if (!known.Contains(tool) || task.Length == 0) continue;
            tasks[tool] = tasks.ContainsKey(tool) ? tasks[tool] + "\n" + task : task;
        }
        return tasks;
    }
    /// The leader's summary where it says the work is done; null where it does not.
    public static string Finished(string said) { var m = Done.Match(said ?? ""); return m.Success ? m.Groups[1].Value.Trim() : null; }

    // ---- what the owner does
    public Team Start(Dictionary<string, object> given) {
        Func<string, string> word = name => { object value; return given.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : ""; };
        object raw; given.TryGetValue("tools", out raw);
        var tools = (raw as IEnumerable ?? new object[0]).Cast<object>().Select(Convert.ToString).ToArray();
        int rounds; Int32.TryParse(word("rounds"), out rounds);
        string rules = word("rules").Trim();
        if (rules.Length > 1000) throw new ArgumentException("分工偏好不能超过 1000 字");
        string mode = word("mode");
        bool discuss = (mode == "review" || mode == "lead") && String.Equals(word("discuss"), "True", StringComparison.OrdinalIgnoreCase);
        var team = new Team {
            Id = word("id"), Dir = word("dir"), Mode = mode, Goal = word("goal").Trim(), Rules = rules,
            Discuss = discuss, Tools = tools,
            Rounds = Math.Max(1, Math.Min(MostRounds, rounds == 0 ? (mode == "compare" ? 1 : 3) : rounds)),
            Created = Now()
        };
        if (!Regex.IsMatch(team.Id, @"\A[a-f0-9]{16,32}\z") || Array.IndexOf(Modes, team.Mode) < 0) throw new ArgumentException("协作任务无效");
        if (team.Goal.Length == 0 || team.Goal.Length > 4000) throw new ArgumentException("目标需为 1 至 4000 字");
        if (team.Mode == "review" && tools.Length != 2) throw new ArgumentException("写审需要一个写的 AI 和一个审查的 AI");
        if (team.Mode == "lead" && (tools.Length < 2 || tools.Length > 4 || tools.Skip(1).Distinct().Count() != tools.Length - 1 || tools.Skip(1).Contains(tools[0])))
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
        if (team.Discuss) {
            string[] discussants = team.Mode == "lead" ? team.Tools.Skip(1).Distinct().ToArray() : team.Tools.Distinct().ToArray();
            lock (gate) {
                team.Phase = "discuss";
                foreach (string tool in discussants) team.Awaited.Add("discuss:" + tool);
                Save();
            }
            foreach (string tool in discussants) Open(team, "discuss:" + tool, tool, DiscussFirst(team, tool), true, null);
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
            team.State = "stopped"; team.Note = "已停止"; team.Awaited.Clear();
            Save();
        }
        foreach (string terminal in busy) try { hands.Stop(terminal); } catch (Exception) { }
        Tell();
    }

    /// Goes on where it was cut: every member whose turn was awaited is told to go on.
    public void Resume(string id) {
        Team team; List<KeyValuePair<string, string>> again;
        lock (gate) {
            team = items.FirstOrDefault(t => t.Id == id);
            if (team == null) throw new ArgumentException("没有找到这个协作任务");
            if (team.State != "paused") throw new InvalidOperationException("这个协作任务没有暂停");
            team.State = "running"; team.Note = "";
            again = team.Awaited.Where(role => team.Members.ContainsKey(role)).Select(role => new KeyValuePair<string, string>(role, team.Members[role])).ToList();
            Save();
        }
        foreach (var one in again) DeliverOrReopen(team, one.Key, one.Value, "继续完成上面的任务。");
        Tell();
    }

    /// Sends an owner message into a collaboration task:
    /// - while running: interjects to `target` (or currently awaited members);
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

            if (state == "running" || state == "paused") {
                if (target.Length > 0) {
                    string resolved = ResolveRole(team, target);
                    if (resolved == null) throw new ArgumentException("没有找到这个成员");
                    targets.Add(resolved);
                    // Resuming a paused task with an explicit target drops stale failed members from Awaited.
                    if (state == "paused") team.Awaited.Clear();
                    if (!team.Awaited.Contains(resolved)) team.Awaited.Add(resolved);
                } else {
                    if (state == "paused" && mode == "lead" && team.Awaited.Count > 0 && team.Awaited.All(r => r != "leader" && team.Failed.ContainsKey(ToolOf(team, r)))) {
                        team.Awaited.Clear();
                        team.Awaited.Add("leader");
                    }
                    targets.AddRange(team.Awaited.Count > 0 ? team.Awaited : new List<string> { mode == "review" ? "writer" : mode == "lead" ? "leader" : team.Tools[0] });
                    foreach (string r in targets) if (!team.Awaited.Contains(r)) team.Awaited.Add(r);
                }
                if (state == "paused") { team.State = "running"; team.Note = ""; }
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
                if (team.Round > team.Rounds) team.Rounds = Math.Min(MaxTotalRounds, team.Round + 2);
                team.State = "running"; team.Note = ""; team.Phase = ""; team.Reminded = false;
                team.Awaited.Clear(); team.Answers.Clear();
                Save();
            }
        }

        Log(team, "user", (target.Length > 0 ? "@" + Label(ToolOf(team, ResolveRole(team, target) ?? target)) + " " : "") + text);

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
                + "\n\n请检查新要求并继续分派（ASSIGN 成员名: 任务）或确认完成（DONE: 总结）。";
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

    /// Explicitly swaps a role's AI (or reassigns a member's task to another AI) mid-run or while paused.
    public Team Swap(string id, string role, string newTool, string note = "") {
        role = (role ?? "").Trim();
        newTool = (newTool ?? "").Trim().ToLowerInvariant();
        note = (note ?? "").Trim();
        if (note.Length > 1000) throw new ArgumentException("补充说明不能超过 1000 字");
        if (newTool != "claude" && newTool != "codex" && newTool != "agy") throw new ArgumentException("接手 AI 无效");
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
            if (oldTool == newTool) throw new ArgumentException("这个角色当前已经是 " + Label(newTool));
            team.Members.TryGetValue(resolved, out oldTerminal);
            team.Pending.TryGetValue(resolved, out pending);
            team.Failed.Remove(newTool);

            bool wasActive = team.Awaited.Contains(resolved) || prevState != "running";
            if (mode == "review") {
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
        string failedAwaited = team.Awaited.FirstOrDefault(r => team.Failed.ContainsKey(ToolOf(team, r)));
        if (failedAwaited != null) return failedAwaited;
        if (team.Awaited.Count > 0) return team.Awaited[0];
        if (team.Mode == "review") return "writer";
        return team.Tools.Length > 1 ? team.Tools[1] : "leader";
    }
    static void PromoteLeader(Team team, string newLeader) {
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
            return LeaderFirst(team, Rooms(team))
                + (recent.Length > 0 ? "\n\n## 此前协作进展\n" + Cut(recent, 8000) : "")
                + (changes.Length > 0 ? "\n\n## 当前工作区改动\n" + Cut(changes, 16000) : "")
                + extra;
        }
        return "【协作任务 · 接手成员任务】\n整体目标：" + team.Goal + ExtraRules(team)
            + (!String.IsNullOrEmpty(pending) ? "\n\n原分派给 " + Label(fromTool) + " 的任务：\n" + Cut(pending, 4000) : "")
            + (changes.Length > 0 ? "\n\n## 当前工作区已有改动\n" + Cut(changes, 16000) : "")
            + extra + "\n\n请基于当前进展继续完成该任务，完成后简要说明你做了什么、改了哪些文件。";
    }

    public bool Remove(string id) {
        Team team; string dir = null; bool hasWorktrees = false;
        lock (gate) {
            team = items.FirstOrDefault(t => t.Id == id);
            if (team == null) return false;
            if (team.State == "running") throw new InvalidOperationException("这个协作任务还在进行，请先停止");
            dir = team.Dir; hasWorktrees = team.Worktrees.Count > 0;
            items.Remove(team); Save();
        }
        if (hasWorktrees && hands.CleanWorktrees != null) try { hands.CleanWorktrees(dir, id); } catch (Exception) { }
        Tell();
        return true;
    }
    Team Of(string id) { lock (gate) { var t = items.FirstOrDefault(x => x.Id == id); return t == null ? null : t.Copy(); } }

    static string ResolveRole(Team team, string wanted) {
        if (String.IsNullOrEmpty(wanted)) return null;
        if (team.Members.ContainsKey(wanted)) return wanted;
        if (team.Phase == "discuss" && team.Members.ContainsKey("discuss:" + wanted)) return "discuss:" + wanted;
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
        } else if (team.Mode == "lead" && role == "leader") prompt = LeaderFirst(team, Rooms(team)) + (words == "继续完成上面的任务。" ? "" : "\n\n" + words);
        else prompt = "【协作任务】继续完成这项工作：" + team.Goal + "\n\n" + words;
        Open(team, role, tool, prompt, read, wt);
    }

    // ---- the members' turns
    void Open(Team team, string role, string tool, string words, bool read, string folderOverride) {
        lock (gate) { opening++; team.Pending[role] = Cut(words, 6000); }
        try {
            string terminal = hands.BeginIn != null
                ? hands.BeginIn(tool, team.Dir, words, read, folderOverride)
                : hands.Begin(tool, team.Dir, words, read);
            lock (gate) { team.Members[role] = terminal; if (!team.Awaited.Contains(role)) team.Awaited.Add(role); opening--; Save(); Monitor.PulseAll(gate); }
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
        lock (gate) { team.Members.TryGetValue(role, out terminal); team.Pending[role] = Cut(words, 6000); if (!team.Awaited.Contains(role)) team.Awaited.Add(role); Save(); }
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
            Save();
        }
        Log(team, role, Cut(said, 4000));
        if (team.Mode == "compare") { Compared(team, role, how, said); return; }
        if (how == "done") {
            lock (gate) { if (team.Failed.Remove(ToolOf(team, role))) Save(); }
        } else if (how == "stopped") {
            lock (gate) { team.State = "paused"; team.Note = Label(ToolOf(team, role)) + " 这一轮被停止：点“继续”接着做，或点“换 AI 接手”"; if (!team.Awaited.Contains(role)) team.Awaited.Add(role); Save(); }
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
            team.Failed[failedTool] = reason;
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
                if (team.Awaited.Any(r => r != "leader")) { Save(); }
                else {
                    answers = new Dictionary<string, string>(team.Answers);
                    team.Answers.Clear();
                    bool hasLiveMember = team.Tools.Skip(1).Any(t => !team.Failed.ContainsKey(t) && !IsExhausted(SafeLimits(t)));
                    if (!hasLiveMember) {
                        addedBackup = FallbackTool(team, failedTool, team.Tools[0]);
                        if (addedBackup != null) {
                            if (!team.Tools.Contains(addedBackup)) team.Tools = team.Tools.Concat(new[] { addedBackup }).ToArray();
                        } else {
                            pauseNoMember = true;
                            team.State = "paused";
                            team.Note = Label(failedTool) + " 失败（" + reason + "），且没有其他可用成员：可点“换 AI 接手”或稍后继续";
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
            bool anyOk = answers.Values.Any(v => !v.StartsWith("【执行失败】", StringComparison.Ordinal));
            if (anyOk) {
                if (team.Round >= team.Rounds) { End(team, "done", "已到 " + team.Rounds + " 轮，成员最后的结果没有交给领队检查"); return; }
                lock (gate) { team.Round++; Save(); }
            }
            string changes = "";
            try { changes = hands.Changes(team.Dir) ?? ""; } catch (Exception) { }
            string body = Results(team, answers, changes, Rooms(team));
            if (addedBackup != null) body += "\n\n【自动补充成员】已将 " + Label(addedBackup) + "（" + addedBackup + "）加入协作队伍，可直接用 ASSIGN " + addedBackup + ": 任务内容 改派。";
            Ask(team, "leader", team.Tools[0], body, false);
            return;
        }

        // Lead mode, leader itself failed: promote a backup AI to leader if available.
        if (team.Mode == "lead") {
            string backup = FallbackTool(team, failedTool);
            if (backup != null) {
                lock (gate) {
                    PromoteLeader(team, backup);
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
                team.Note = Label(failedTool) + "（领队）失败（" + reason + "），没有可用备用 AI：可点“换 AI 接手”或稍后继续";
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
            team.Note = Label(failedTool) + " 失败（" + reason + "），没有其他可用备用 AI：可点“换 AI 接手”指定接替，或稍后继续";
            if (!team.Awaited.Contains(role)) team.Awaited.Add(role);
            Save();
        }
        Tell();
    }

    static string ToolOf(Team team, string role) {
        if (role.StartsWith("discuss:", StringComparison.Ordinal)) return role.Substring(8);
        return role == "writer" || role == "leader" ? team.Tools[0] : role == "reviewer" ? team.Tools[1] : role;
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
        string discussion = String.Join("\n\n", collected.Select(p => "### " + Label(ToolOf(team, p.Key)) + "\n" + Cut(p.Value, 5000)));
        if (team.Mode == "review") {
            string writerTool = team.Tools[0];
            bool writerFailed; lock (gate) writerFailed = team.Failed.ContainsKey(writerTool);
            if (writerFailed) {
                string backup = FallbackTool(team, writerTool, team.Tools[1]);
                if (backup != null) {
                    lock (gate) { team.Tools[0] = backup; Save(); }
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
                    lock (gate) { PromoteLeader(team, backup); Save(); }
                    Log(team, "切换", Label(leaderTool) + " 不可用，领队已自动切换为 " + Label(backup));
                    leaderTool = backup;
                }
            }
            Open(team, "leader", leaderTool, LeaderFirst(team, Rooms(team), discussion), false, null);
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
        if (team.Round >= team.Rounds) { End(team, "done", "已到 " + team.Rounds + " 轮，最后一轮的审查意见没有处理"); return; }
        lock (gate) { team.Round++; Save(); }
        Ask(team, "writer", team.Tools[0], WriterAgain(team, said), false);
    }
    void Led(Team team, string role, string said) {
        if (role == "leader") {
            string summary = Finished(said);
            var tasks = Assigned(said, team.Tools.Skip(1));
            if (tasks.Count == 0 && summary != null) { End(team, "done", "领队确认完成：" + Cut(summary, 600)); return; }
            if (tasks.Count == 0) {
                bool reminded; lock (gate) { reminded = team.Reminded; team.Reminded = true; Save(); }
                if (reminded) { lock (gate) { team.State = "paused"; team.Note = "领队没有分派任务，也没有结束：点“继续”再问它一次"; team.Awaited.Clear(); team.Awaited.Add("leader"); Save(); } Tell(); return; }
                Ask(team, "leader", team.Tools[0], Remind, false);
                return;
            }
            lock (gate) {
                team.Reminded = false; team.Answers.Clear(); team.Awaited.Clear();
                foreach (string member in tasks.Keys) if (!team.Awaited.Contains(member)) team.Awaited.Add(member);
                Save();
            }
            foreach (var task in tasks) Ask(team, task.Key, task.Key, MemberTask(team, task.Value), false);
            return;
        }
        Dictionary<string, string> answers = null;
        lock (gate) {
            team.Answers[role] = said;
            if (team.Awaited.Any(r => r != "leader")) { Save(); return; }      // the others are still at it
            answers = new Dictionary<string, string>(team.Answers); team.Answers.Clear(); Save();
        }
        if (team.Round >= team.Rounds) { End(team, "done", "已到 " + team.Rounds + " 轮，成员最后的结果没有交给领队检查"); return; }
        lock (gate) { team.Round++; Save(); }
        string changes = "";
        try { changes = hands.Changes(team.Dir) ?? ""; } catch (Exception) { }
        Ask(team, "leader", team.Tools[0], Results(team, answers, changes, Rooms(team)), false);
    }
    static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }
}
}
