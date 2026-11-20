using System;
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
/// (a chat, Chats.cs), started by this program in the task's project: its owner can open, watch, stop or answer any
/// of them on the phone. When a member's turn ends, what it said is passed on by the task's way of working:
/// - review (写审): the writer does the work; the reviewer, read-only, is shown what changed (the folder's `git diff`
///   where it is a repository, and the writer's own account of it) and answers REVIEW: PASS or REVIEW: CHANGES in its
///   first line; its findings go back to the writer, until it passes or the rounds are spent.
/// - lead (领队): the leader is told the goal and the members and answers with lines ASSIGN <member>: <task>, or
///   DONE: <summary>. Each task goes to that member's conversation (one for each tool); when every member given one
///   has answered, what they said goes back to the leader, until it says DONE or the rounds are spent. The leader is
///   told how much of each member's usage limits (and its own) is used, at the start and with every round's results,
///   to give less to one whose limits are near (Quotas.cs).
/// Every member works in the project's own folder: the leader is told to give out parts that do not overlap.
/// The tasks are kept in one file of the data folder, encrypted for the Windows account; a task whose members were
/// at work when this program stopped waits to be gone on with (`Resume`), since their turns were cut.
public sealed class Teams {
    public const int Most = 10, MostRounds = 10;
    public static readonly string[] Modes = { "review", "lead" };
    public sealed class Step { public long At; public string Who = "", Text = ""; }
    public sealed class Team {
        public string Id = "", Dir = "", Mode = "", Goal = "", Rules = "", State = "running", Note = "";
        public string[] Tools = new string[0];          // review: writer, reviewer; lead: leader, then the members
        public int Rounds = 3, Round = 1;
        public long Created;
        public List<Step> Log = new List<Step>();
        /// Each member's conversation: role -> terminal ("writer", "reviewer", "leader", or a member's tool).
        public Dictionary<string, string> Members = new Dictionary<string, string>();
        /// Whose turn is awaited (roles), and what those who answered said in this round (lead).
        public List<string> Awaited = new List<string>();
        public Dictionary<string, string> Answers = new Dictionary<string, string>();
        public bool Reminded;
        internal Team Copy() {
            var copy = (Team)MemberwiseClone();
            copy.Log = Log.ToList(); copy.Members = new Dictionary<string, string>(Members); copy.Awaited = Awaited.ToList(); copy.Answers = new Dictionary<string, string>(Answers); copy.Tools = Tools.ToArray();
            return copy;
        }
    }
    /// What the program does for a team: begin a member's conversation in the project with its first words (returns
    /// its terminal; `read` asks for a read-only one), tell a member's conversation something, stop one, read what
    /// changed in the project's folder, and read how much of a tool's limits is used (none given: nothing is told).
    public sealed class Hands {
        public Func<string, string, string, bool, string> Begin;        // tool, project, first words, read only -> terminal
        public Action<string, string> Say;                               // terminal, words
        public Action<string> Stop;                                      // terminal
        public Func<string, string> Changes;                             // project -> what changed, in words
        public Func<string, Quotas.Window[]> Limits;                     // tool -> its main windows still running
    }

    readonly string file;
    readonly Hands hands;
    readonly object gate = new object();
    int opening;
    readonly List<Team> items = new List<Team>();
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    public event Action Changed;

    public Teams(string file, Hands hands) {
        this.file = file; this.hands = hands;
        if (file == null || !File.Exists(file)) return;
        try {
            string text = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser));
            foreach (object one in (System.Collections.IEnumerable)json.DeserializeObject(text)) {
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
        Func<string, IEnumerable<object>> many = name => { object value; return kept.TryGetValue(name, out value) && value is System.Collections.IEnumerable && !(value is string) ? ((System.Collections.IEnumerable)value).Cast<object>() : new object[0]; };
        Func<string, Dictionary<string, string>> map = name => { object value; var d = kept.TryGetValue(name, out value) ? value as Dictionary<string, object> : null; return d == null ? new Dictionary<string, string>() : d.ToDictionary(p => p.Key, p => Convert.ToString(p.Value)); };
        int number;
        var team = new Team { Id = word("id"), Dir = word("dir"), Mode = word("mode"), Goal = word("goal"), Rules = word("rules"), State = word("state"), Note = word("note"), Reminded = word("reminded") == "True",
            Tools = many("tools").Select(Convert.ToString).ToArray(), Members = map("members"), Answers = map("answers"), Awaited = many("awaited").Select(Convert.ToString).ToList() };
        team.Rounds = Int32.TryParse(word("rounds"), out number) ? number : 3; team.Round = Int32.TryParse(word("round"), out number) ? number : 1;
        long created; team.Created = Int64.TryParse(word("created"), out created) ? created : 0;
        foreach (var step in many("log").OfType<Dictionary<string, object>>()) { long at; Int64.TryParse(Convert.ToString(step["at"]), out at); team.Log.Add(new Step { At = at, Who = Convert.ToString(step["who"]), Text = Convert.ToString(step["text"]) }); }
        return Regex.IsMatch(team.Id, @"\A[a-f0-9]{16,32}\z") && Array.IndexOf(Modes, team.Mode) >= 0 ? team : null;
    }
    void Save() {
        if (file == null) return;
        var all = items.Select(t => new Dictionary<string, object> { { "id", t.Id }, { "dir", t.Dir }, { "mode", t.Mode }, { "goal", t.Goal }, { "rules", t.Rules }, { "state", t.State }, { "note", t.Note }, { "reminded", t.Reminded },
            { "tools", t.Tools }, { "rounds", t.Rounds }, { "round", t.Round }, { "created", t.Created }, { "members", t.Members }, { "awaited", t.Awaited }, { "answers", t.Answers },
            { "log", t.Log.Select(s => new Dictionary<string, object> { { "at", s.At }, { "who", s.Who }, { "text", s.Text } }).ToArray() } }).ToArray();
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
        return new Dictionary<string, object> { { "id", t.Id }, { "dir", t.Dir }, { "mode", t.Mode }, { "goal", t.Goal }, { "rules", t.Rules }, { "state", t.State }, { "note", t.Note }, { "tools", t.Tools },
            { "rounds", t.Rounds }, { "round", t.Round }, { "created", t.Created }, { "members", t.Members }, { "awaited", t.Awaited.ToArray() },
            { "log", t.Log.Skip(Math.Max(0, t.Log.Count - 60)).Select(s => new Dictionary<string, object> { { "at", s.At }, { "who", s.Who }, { "text", s.Text } }).ToArray() } };
    }

    // ---- what the members are told (read by the models: changed only together with runs on the real tools)
    public static string Label(string tool) { return RemoteCli.Tool.LabelOf(tool); }
    static readonly Dictionary<string, string> Strengths = new Dictionary<string, string> {
        { "codex", "擅长决策拍板、任务拆解、精准改代码与修测试（优先负责决策，纯检查不必浪费其额度）" },
        { "claude", "擅长主力写作（代码实现、重构与文档撰写）以及深度交叉审查" },
        { "agy", "Gemini 模型，擅长 UI 与前端界面设计、长上下文检索，也可兼顾写作与交叉检查" } };
    /// A tool's limits in words for the leader; a window used for 80% or more makes it tight.
    public static string Room(Quotas.Window[] windows) {
        if (windows == null || windows.Length == 0) return "额度未知";
        var said = windows.Select(w => (w.Name == "five_hour" ? "5 小时窗口" : w.Name == "seven_day" ? "本周" : w.Name) + "已用 " + Math.Round(w.Used) + "%"
            + (w.Resets > 0 ? "（" + When(w.Resets) + " 重置）" : "")).ToArray();
        return "额度：" + String.Join("，", said) + (windows.Any(w => Math.Round(w.Used) >= 80) ? "，紧张" : "");
    }
    static string When(long seconds) {
        DateTime at = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
        return at.ToString(at.Date == DateTime.Now.Date ? "HH:mm" : "M月d日 HH:mm", CultureInfo.InvariantCulture);
    }
    /// Each tool of a team's limits in words; none where the program does not read limits.
    Dictionary<string, string> Rooms(Team t) {
        var rooms = new Dictionary<string, string>();
        if (hands.Limits == null) return rooms;
        foreach (string tool in t.Tools.Distinct()) {
            Quotas.Window[] windows = null;
            try { windows = hands.Limits(tool); } catch (Exception) { }
            rooms[tool] = Room(windows);
        }
        return rooms;
    }
    static string ExtraRules(Team t) { return String.IsNullOrEmpty(t.Rules) ? "" : "\n\n【分工偏好】\n" + Cut(t.Rules, 1000); }
    public static string WriterFirst(Team t) {
        return "【协作任务 · 写】" + t.Goal + ExtraRules(t)
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
    public static string LeaderFirst(Team t, IDictionary<string, string> rooms) {
        Func<string, string> room = tool => rooms.ContainsKey(tool) ? "；" + rooms[tool] : "";
        var members = t.Tools.Skip(1).Select(tool => "- " + tool + "：" + Label(tool) + (Strengths.ContainsKey(tool) ? "，" + Strengths[tool] : "") + room(tool)).ToArray();
        return "【协作任务 · 领队】目标：" + t.Goal + ExtraRules(t)
            + "\n\n你负责决策、按任务特点高效干净地分工、验收结果并在达标时结束。成员都在同一个项目文件夹中工作：\n" + String.Join("\n", members)
            + (rooms.ContainsKey(t.Tools[0]) ? "\n你自己（" + Label(t.Tools[0]) + "）的" + rooms[t.Tools[0]] : "")
            + "\n\n分工原则：\n1. 按具体任务按需派工，不必每次把所有成员都派满；不需要的成员可以不派。\n2. 并行分派时写清各自负责的文件和验收标准，避免多人改同一批文件。\n3. 需要核验时采用交叉检查（谁写不由谁审），优先让 Claude Code 或 Antigravity CLI 交叉检查，不必把纯检查浪费在 Codex 上。\n4. 结果达标后立刻结束，不要为了凑轮次空转。"
            + (rooms.Count > 0 ? "\n5. 按额度分配工作量：额度紧张（任一窗口已用 80% 以上）的成员少派或不派，只给小而必要的任务，工作多交给额度充足的成员；你自己的额度紧张时，回答保持简短，把实现和检查交给成员。额度未知的成员按正常对待。" : "")
            + "\n\n分派时，在回答的最后每行写一条（成员名用上面的英文名）：\nASSIGN 成员名: 任务内容\n全部完成时，回答的最后单独一行写：\nDONE: 总结\n分派出去的工作不要自己动手做。";
    }
    public static string MemberTask(Team t, string task) {
        return "【协作任务 · 成员】领队（" + Label(t.Tools[0]) + "）分派给你的任务：\n" + task + "\n\n整体目标：" + t.Goal + "\n\n完成后简要说明你做了什么、改了哪些文件。";
    }
    public static string Results(Team t, Dictionary<string, string> answers, string changes, IDictionary<string, string> rooms) {
        return "【协作任务 · 成员结果 · 第 " + t.Round + " 轮】\n\n" + String.Join("\n\n", answers.Select(p => "## " + Label(p.Key) + "\n" + Cut(p.Value, 8000)))
            + (changes.Length > 0 ? "\n\n## 文件夹中的改动\n" + Cut(changes, 20000) : "")
            + (rooms.Count > 0 ? "\n\n## 当前额度\n" + String.Join("\n", t.Tools.Distinct().Where(rooms.ContainsKey).Select(tool => "- " + Label(tool) + (tool == t.Tools[0] ? "（你）" : "") + "：" + rooms[tool])) : "")
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
        var tools = (raw as System.Collections.IEnumerable ?? new object[0]).Cast<object>().Select(Convert.ToString).ToArray();
        int rounds; Int32.TryParse(word("rounds"), out rounds);
        string rules = word("rules").Trim();
        if (rules.Length > 1000) throw new ArgumentException("分工偏好不能超过 1000 字");
        var team = new Team { Id = word("id"), Dir = word("dir"), Mode = word("mode"), Goal = word("goal").Trim(), Rules = rules, Tools = tools, Rounds = Math.Max(1, Math.Min(MostRounds, rounds == 0 ? 3 : rounds)), Created = Now() };
        if (!Regex.IsMatch(team.Id, @"\A[a-f0-9]{16,32}\z") || Array.IndexOf(Modes, team.Mode) < 0) throw new ArgumentException("协作任务无效");
        if (team.Goal.Length == 0 || team.Goal.Length > 4000) throw new ArgumentException("目标需为 1 至 4000 字");
        if (team.Mode == "review" ? tools.Length != 2 : tools.Length < 2 || tools.Length > 4 || tools.Skip(1).Distinct().Count() != tools.Length - 1 || tools.Skip(1).Contains(tools[0]))
            throw new ArgumentException(team.Mode == "review" ? "写审需要一个写的 AI 和一个审查的 AI" : "领队需要一个领队和 1 至 3 个不同的成员");
        lock (gate) {
            var same = items.FirstOrDefault(t => t.Id == team.Id);
            if (same != null) return same.Copy();       // asked again under the same number
            if (items.Count(t => t.State == "running" || t.State == "paused") >= 3) throw new InvalidOperationException("最多同时进行 3 个协作任务，请先结束一个");
            items.Add(team);
            while (items.Count > Most) items.Remove(items.Where(t => t.State != "running" && t.State != "paused").OrderBy(t => t.Created).FirstOrDefault() ?? items[0]);
        }
        string role = team.Mode == "review" ? "writer" : "leader";
        Log(team, "目标", team.Goal);
        Open(team, role, team.Tools[0], team.Mode == "review" ? WriterFirst(team) : LeaderFirst(team, Rooms(team)), false);
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
        foreach (var one in again) {
            try { hands.Say(one.Value, "继续完成上面的任务。"); }
            catch (Exception) {
                // its conversation is gone: begun anew with what it was doing
                string tool = one.Key == "writer" ? team.Tools[0] : one.Key == "reviewer" ? team.Tools[1] : one.Key == "leader" ? team.Tools[0] : one.Key;
                string prompt;
                if (team.Mode == "review" && one.Key == "writer") prompt = WriterFirst(team);
                else if (team.Mode == "review" && one.Key == "reviewer") {
                    string changes = "";
                    try { changes = hands.Changes(team.Dir) ?? ""; } catch (Exception) { }
                    prompt = ReviewerAsk(team, "（电脑端重启后继续审查）", changes);
                } else if (team.Mode == "lead" && one.Key == "leader") prompt = LeaderFirst(team, Rooms(team));
                else prompt = "【协作任务】继续完成这项工作：" + team.Goal;
                Open(team, one.Key, tool, prompt, one.Key == "reviewer");
            }
        }
        Tell();
    }
    public bool Remove(string id) {
        Team team;
        lock (gate) {
            team = items.FirstOrDefault(t => t.Id == id);
            if (team == null) return false;
            if (team.State == "running") throw new InvalidOperationException("这个协作任务还在进行，请先停止");
            items.Remove(team); Save();
        }
        Tell();
        return true;
    }
    Team Of(string id) { lock (gate) { var t = items.FirstOrDefault(x => x.Id == id); return t == null ? null : t.Copy(); } }

    // ---- the members' turns
    /// A member's conversation began: its terminal is the role's.
    void Open(Team team, string role, string tool, string words, bool read) {
        lock (gate) opening++;
        try {
            string terminal = hands.Begin(tool, team.Dir, words, read);
            lock (gate) { team.Members[role] = terminal; if (!team.Awaited.Contains(role)) team.Awaited.Add(role); opening--; Save(); Monitor.PulseAll(gate); }
            Log(team, role, "开始：" + Cut(words, 200));
        } catch (Exception problem) {
            lock (gate) { team.State = "failed"; team.Note = Label(tool) + " 没有开始：" + (problem is ArgumentException || problem is InvalidOperationException ? problem.Message : "电脑端出错"); team.Awaited.Remove(role); opening--; Save(); Monitor.PulseAll(gate); }
            Tell();
        }
    }
    void Ask(Team team, string role, string tool, string words, bool read) {
        string terminal;
        lock (gate) { team.Members.TryGetValue(role, out terminal); if (!team.Awaited.Contains(role)) team.Awaited.Add(role); Save(); }
        if (terminal != null) {
            try { hands.Say(terminal, words); Log(team, role, "收到：" + Cut(words, 200)); return; }
            catch (Exception) { }       // its conversation was closed: begun anew
        }
        Open(team, role, tool, words, read);
    }
    void Log(Team team, string who, string text) {
        lock (gate) {
            team.Log.Add(new Step { At = Now(), Who = who, Text = Cut(text, 1200) });
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
        Log(team, role, Cut(said, 1200));
        if (how != "done") {
            lock (gate) { team.State = "paused"; team.Note = Label(ToolOf(team, role)) + (how == "stopped" ? " 这一轮被停止" : " 这一轮出错") + "：点“继续”接着做"; if (!team.Awaited.Contains(role)) team.Awaited.Add(role); Save(); }
            Tell();
            return;
        }
        if (team.Mode == "review") Reviewed(team, role, said); else Led(team, role, said);
    }
    static string ToolOf(Team team, string role) { return role == "writer" || role == "leader" ? team.Tools[0] : role == "reviewer" ? team.Tools[1] : role; }

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
                team.Reminded = false; team.Answers.Clear();
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
