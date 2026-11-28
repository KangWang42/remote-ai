using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RemoteCli {
/// Work a conversation the phone runs hands to another AI on this computer by itself (`delegate_task`, offered to it
/// by Peers.cs). Each task is a conversation of its own, begun by this program, that the phone can open; it runs while
/// the conversation that gave it out goes on. When it ends its result is told to that conversation: at once where it
/// waits, else as soon as its turn ends, the results of several that ended meanwhile in one message. A question the
/// task asks with choices is told the same way, and may be answered by that conversation (`task_answer`) or on the
/// phone. Stopping the conversation that gave the work out stops its tasks; so does its end.
public sealed class Delegations {
    /// Tasks of one conversation at work at once.
    public const int MostRunning = 6;
    /// Of the results told in one message, how much is given in full; the rest is read with `task_status`.
    public const int ResultChars = 12000;
    const int TaskChars = 20000, TitleChars = 40, StatusChars = 24000;

    public sealed class Hands {
        /// Begins the task's conversation and says its first words; throws ArgumentException or
        /// InvalidOperationException with words for the AI that gave the work out.
        public Func<Opening, Began> Begin;
        /// How a conversation the phone runs stands: starting, busy, confirm, idle; "" where it has ended or is gone.
        public Func<string, string> StateOf;
        public Action<string, string> Say;
        public Action<string> Stop;
        public Action<string> Close;
        /// Answers what a conversation asks (Chat.Answer); false where that is no longer asked.
        public Func<string, string, string, bool> Answer;
        /// Shows a task's card in the conversation that gave it out (a `task` event of its stream).
        public Action<string, Dictionary<string, object>> Card;
    }
    public sealed class Opening {
        public string Parent = "", ParentTool = "", Tool = "", Model = "", Words = "", Title = "";
        public bool Worktree;
    }
    public sealed class Began { public string Terminal = "", Worktree = "", Branch = ""; }
    public sealed class Task {
        public string Id = "", Parent = "", ParentTool = "", Child = "", Tool = "", Model = "", Title = "", Worktree = "", Branch = "";
        /// running, waiting (it asks a question), completed, failed, cancelled
        public string State = "running";
        public string Result = "", Error = "";
        public long Started, Ended;
        /// What it asks while `waiting`: the request, its words and its choices.
        public string Ask = "", Question = "";
        public List<string> Choices = new List<string>();
        /// Its result was told to the conversation that gave it out, or read by it.
        public bool Delivered;
    }

    readonly Hands hands;
    readonly object gate = new object();
    readonly Dictionary<string, Task> tasks = new Dictionary<string, Task>();
    /// Per conversation that gave work out: what is to be told to it once its turn ends (task ids, in order).
    readonly Dictionary<string, List<string>> owed = new Dictionary<string, List<string>>();
    readonly Dictionary<string, List<string>> asked = new Dictionary<string, List<string>>();
    public event Action Changed;

    public Delegations(Hands hands) { this.hands = hands; }

    public static readonly string[] Isolations = { "shared", "worktree" };

    // ---- what the AI that gives work out asks for (Peers.cs)
    public Dictionary<string, object> Start(string parent, string parentTool, IDictionary<string, object> args) {
        string tool = Word(args, "tool").ToLowerInvariant(), words = Word(args, "task"), title = Line(Word(args, "title")), model = Word(args, "model");
        string isolation = Word(args, "isolation").ToLowerInvariant();
        if (isolation.Length == 0) isolation = "shared";
        if (words.Length == 0) return Fail("请在 task 里写清子任务：背景、范围、负责的文件和要交回的验收证据");
        if (words.Length > TaskChars) return Fail("子任务说明太长（最多 " + TaskChars + " 字），请精简或改为指向项目里的文件");
        if (Array.IndexOf(Isolations, isolation) < 0) return Fail("isolation 只能是 shared 或 worktree");
        if (model.Length > 0 && !Models.Valid(model)) return Fail("模型名称无效");
        if (title.Length == 0) title = Line(words);
        if (title.Length > TitleChars) title = title.Substring(0, TitleChars - 1) + "…";
        lock (gate) {
            if (tasks.Values.Count(t => t.Parent == parent && Open(t)) >= MostRunning)
                return Fail("这段对话已有 " + MostRunning + " 个子任务在进行，请等其中一些完成或用 task_cancel 取消后再派");
        }
        var task = new Task { Id = NewId(), Parent = parent, ParentTool = parentTool ?? "", Tool = tool, Model = model, Title = title, Started = Now() };
        Began began;
        try {
            began = hands.Begin(new Opening { Parent = parent, ParentTool = task.ParentTool, Tool = tool, Model = model, Title = title, Worktree = isolation == "worktree",
                Words = Brief(task, words, isolation == "worktree") });
        } catch (ArgumentException ex) { return Fail(ex.Message); }
        catch (InvalidOperationException ex) { return Fail(ex.Message); }
        task.Child = began.Terminal; task.Worktree = began.Worktree ?? ""; task.Branch = began.Branch ?? "";
        lock (gate) tasks[task.Id] = task;
        Show(task);
        var back = Shown(task);
        back["next"] = "完成后结果会自动作为一条消息送回这段对话。不要查询或等待它；继续做其它事，没有其它事就结束本轮回答。";
        return Ok(back);
    }

    public Dictionary<string, object> Status(string parent, string id) {
        Task task = Mine(parent, id);
        if (task == null) return Fail("没有找到这个子任务：" + id);
        string childState = hands.StateOf(task.Child);
        lock (gate) {
            Settle(task, childState);
            var back = Shown(task);
            if (task.State == "completed" || task.State == "failed") {
                if (task.Result.Length > 0) back["result"] = ProcessText.FoldHeadTail(task.Result, StatusChars);
                task.Delivered = true;
            }
            if (task.State == "running") back["next"] = "子任务还在进行，完成后结果会自动送回，不必再查询。";
            return Ok(back);
        }
    }

    public Dictionary<string, object> Cancel(string parent, string id, string reason) {
        Task task = Mine(parent, id);
        if (task == null) return Fail("没有找到这个子任务：" + id);
        bool stopping;
        lock (gate) {
            stopping = Open(task);
            if (stopping) { task.State = "cancelled"; task.Error = Line(reason); task.Ended = Now(); task.Delivered = true; Forget(task); }
        }
        if (stopping) { Halt(task); Show(task); }
        return Ok(Shown(task));
    }

    /// Answers the question a task asks, by the number of a choice (from 1) or its words.
    public Dictionary<string, object> Answer(string parent, string id, string answer) {
        Task task = Mine(parent, id);
        if (task == null) return Fail("没有找到这个子任务：" + id);
        string ask, decision = null, childState = hands.StateOf(task.Child);
        lock (gate) {
            Settle(task, childState);
            if (task.State != "waiting" || task.Ask.Length == 0) return Fail("这个子任务现在没有在等回答");
            ask = task.Ask;
            string given = (answer ?? "").Trim();
            int number;
            if (Int32.TryParse(given, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= task.Choices.Count) decision = "choice:" + (number - 1);
            else {
                int at = task.Choices.FindIndex(c => String.Equals(c.Trim(), given, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) decision = "choice:" + at;
            }
            if (decision == null) return Fail("answer 要写选项的序号（1 至 " + task.Choices.Count + "）或选项原文：" + Choices(task));
        }
        if (!hands.Answer(task.Child, ask, decision)) return Fail("这个问题已经回答过或不再需要回答");
        lock (gate) { task.State = "running"; task.Ask = ""; task.Question = ""; task.Choices.Clear(); }
        Show(task);
        return Ok(Shown(task));
    }

    // ---- what the conversations do (RemoteCliAgent.Heard)
    /// A turn of a conversation the phone runs ended. A task's: its result is kept and told. One that gave work out:
    /// what is owed to it is told now; stopped, its tasks are stopped too.
    public void Turned(string terminal, string how, string said) {
        Task ended = null; bool parent;
        lock (gate) {
            ended = tasks.Values.FirstOrDefault(t => t.Child == terminal && Open(t));
            if (ended != null) {
                ended.State = how == "done" ? "completed" : how == "stopped" ? "cancelled" : "failed";
                ended.Result = (said ?? "").Trim();
                if (ended.State == "failed") ended.Error = ended.Result.Length > 0 ? Line(ended.Result) : "子任务出错";
                if (ended.State == "cancelled") ended.Error = "在手机上被停止";
                ended.Ended = Now(); ended.Ask = ""; ended.Question = ""; ended.Choices.Clear();
                Owe(ended.Parent, ended.Id, false);
            }
            parent = tasks.Values.Any(t => t.Parent == terminal);
        }
        if (ended != null) {
            Show(ended);
            // its conversation is closed once it has given its result: the record stays and can be opened again
            try { hands.Close(ended.Child); } catch (Exception) { }
            if (Idle(ended.Parent)) Deliver(ended.Parent);
        }
        if (!parent) return;
        if (how == "stopped") StopAll(terminal, "原对话被停止");
        Deliver(terminal);
    }

    /// A conversation asks something. A task's question with choices is told to the one that gave it out.
    public void Asked(string terminal, string ask, string kind, string title, string text, IList<string> choices) {
        Task task;
        lock (gate) {
            task = tasks.Values.FirstOrDefault(t => t.Child == terminal && Open(t));
            if (task == null || kind != "question" || choices == null || choices.Count == 0) return;
            task.State = "waiting"; task.Ask = ask; task.Question = Line((title ?? "") + " " + (text ?? ""), 600);
            task.Choices = choices.ToList();
            Owe(task.Parent, task.Id, true);
        }
        Show(task);
        if (Idle(task.Parent)) Deliver(task.Parent);
    }

    /// Now and then: a task whose conversation ended by itself failed; the tasks of a conversation that ended stop;
    /// a question answered on the phone is no longer waited for.
    public void Sweep() {
        var gone = new List<Task>(); var parents = new HashSet<string>(); var moved = new List<Task>();
        // how the conversations stand is asked outside the gate: the agent answers it under its own lock
        Task[] all;
        lock (gate) all = tasks.Values.ToArray();
        var states = new Dictionary<string, string>();
        foreach (string terminal in all.SelectMany(t => new[] { t.Parent, t.Child }).Distinct()) states[terminal] = hands.StateOf(terminal) ?? "";
        Func<string, string> stateOf = terminal => { string s; return states.TryGetValue(terminal, out s) ? s : ""; };
        lock (gate) {
            foreach (var task in tasks.Values.Where(t => Open(t) && states.ContainsKey(t.Child)).ToArray()) {
                if (stateOf(task.Parent).Length == 0) { parents.Add(task.Parent); continue; }
                string state = stateOf(task.Child);
                if (state.Length == 0) {
                    task.State = "failed"; task.Error = "子任务的对话已经结束"; task.Ended = Now();
                    Owe(task.Parent, task.Id, false); gone.Add(task);
                } else if (task.State == "waiting" && state != "confirm") {
                    task.State = "running"; task.Ask = ""; task.Question = ""; task.Choices.Clear(); moved.Add(task);
                }
            }
            // what is owed to a conversation that ended is dropped with its tasks once they are over
            foreach (var done in tasks.Values.Where(t => !Open(t) && states.ContainsKey(t.Parent) && stateOf(t.Parent).Length == 0).Select(t => t.Id).ToArray()) { Forget(tasks[done]); tasks.Remove(done); }
        }
        foreach (string parent in parents) StopAll(parent, "原对话已结束");
        foreach (var task in gone.Concat(moved)) Show(task);
        foreach (string parent in gone.Select(t => t.Parent).Distinct()) if (Idle(parent)) Deliver(parent);
    }

    // ---- what the phone lists
    /// The task a conversation runs, where it is one: shown in the lists under the one that gave it out.
    public Task Of(string terminal) {
        lock (gate) return tasks.Values.FirstOrDefault(t => t.Child == terminal);
    }
    public Task[] Given(string parent) {
        lock (gate) return tasks.Values.Where(t => t.Parent == parent).OrderBy(t => t.Started).ToArray();
    }

    // ---- inside
    static bool Open(Task t) { return t.State == "running" || t.State == "waiting"; }
    Task Mine(string parent, string id) {
        lock (gate) { Task task; return tasks.TryGetValue((id ?? "").Trim(), out task) && task.Parent == parent ? task : null; }
    }
    void Settle(Task task, string childState) {
        if (task.State == "waiting" && childState != "confirm") { task.State = "running"; task.Ask = ""; task.Question = ""; task.Choices.Clear(); }
    }
    bool Idle(string terminal) { string state = hands.StateOf(terminal); return state == "idle"; }
    void Owe(string parent, string id, bool question) {
        var into = question ? asked : owed;
        List<string> list;
        if (!into.TryGetValue(parent, out list)) into[parent] = list = new List<string>();
        if (!list.Contains(id)) list.Add(id);
    }
    void Forget(Task task) {
        List<string> list;
        if (owed.TryGetValue(task.Parent, out list)) list.Remove(task.Id);
        if (asked.TryGetValue(task.Parent, out list)) list.Remove(task.Id);
    }
    void StopAll(string parent, string why) {
        Task[] stopping;
        lock (gate) {
            stopping = tasks.Values.Where(t => t.Parent == parent && Open(t)).ToArray();
            foreach (var task in stopping) { task.State = "cancelled"; task.Error = why; task.Ended = Now(); task.Delivered = true; Forget(task); }
        }
        foreach (var task in stopping) { Halt(task); Show(task); }
    }
    void Halt(Task task) {
        try { hands.Stop(task.Child); } catch (Exception) { }
        try { hands.Close(task.Child); } catch (Exception) { }
    }
    /// Tells a conversation what is owed to it: the results of its tasks that ended and the questions they ask.
    void Deliver(string parent) {
        string words;
        lock (gate) {
            List<string> results, questions;
            owed.TryGetValue(parent, out results); asked.TryGetValue(parent, out questions);
            var done = (results ?? new List<string>()).Where(tasks.ContainsKey).Select(id => tasks[id]).Where(t => !t.Delivered).ToList();
            var waiting = (questions ?? new List<string>()).Where(tasks.ContainsKey).Select(id => tasks[id]).Where(t => t.State == "waiting").ToList();
            owed.Remove(parent); asked.Remove(parent);
            if (done.Count == 0 && waiting.Count == 0) return;
            foreach (var task in done) task.Delivered = true;
            int still = tasks.Values.Count(t => t.Parent == parent && t.State == "running");
            words = Told(done, waiting, still);
        }
        try { hands.Say(parent, words); } catch (Exception) { }
    }
    /// The message that brings results and questions back. It says whose they are, so that it is not taken for the
    /// user's own words.
    static string Told(List<Task> done, List<Task> waiting, int still) {
        var text = new StringBuilder(done.Count > 0 ? "【子任务结果】以下是你派出的子任务的结果，由 Remote AI 送回，不是用户的新要求。" : "【子任务提问】你派出的子任务在等回答，由 Remote AI 转来，不是用户的新要求。");
        int room = ResultChars;
        foreach (var task in done) {
            text.Append("\n\n## 子任务 " + task.Id + "（" + Who(task) + "）" + Ending(task));
            if (task.Worktree.Length > 0) text.Append("\n改动在独立工作树 " + task.Worktree + (task.Branch.Length > 0 ? "（分支 " + task.Branch + "）" : "") + "，主目录未改动；是否合入由你检查后决定。");
            string result = task.Result.Trim();
            if (result.Length == 0) continue;
            if (result.Length <= room) { text.Append("\n" + result); room -= result.Length; }
            else if (room == ResultChars) { text.Append("\n" + ProcessText.FoldHeadTail(result, room) + "\n（中间已省略，全文用 task_status 读取，task_id: " + task.Id + "）"); room = 0; }
            else text.Append("\n结果较长，用 task_status 读取（task_id: " + task.Id + "）。");
        }
        foreach (var task in waiting) {
            text.Append("\n\n## 子任务 " + task.Id + "（" + Who(task) + "）在等回答\n" + task.Question + "\n选项：" + Choices(task));
        }
        if (waiting.Count > 0)
            text.Append("\n\n答案能从任务和用户的要求推出时，用 task_answer 回答（task_id 和选项序号）；推不出时先问用户。用户也可以在手机上直接回答。");
        if (still > 0) text.Append("\n\n还有 " + still + " 个子任务在进行，完成后会再送来，不必等待或查询。");
        if (done.Count > 0) text.Append("\n\n请据此继续原来的工作。");
        return text.ToString();
    }
    static string Who(Task task) { return Tool.LabelOf(task.Tool) + (task.Model.Length > 0 ? " · " + task.Model : "") + " · " + task.Title; }
    static string Ending(Task task) {
        string took = Took(task);
        if (task.State == "completed") return "已完成" + (took.Length > 0 ? "，用时 " + took : "");
        if (task.State == "cancelled") return "已停止" + (task.Error.Length > 0 ? "：" + task.Error : "");
        return "失败" + (task.Error.Length > 0 ? "：" + task.Error : "");
    }
    static string Took(Task task) {
        if (task.Ended <= task.Started) return "";
        long seconds = (task.Ended - task.Started) / 1000;
        return seconds < 60 ? seconds + " 秒" : seconds / 60 + " 分 " + seconds % 60 + " 秒";
    }
    static string Choices(Task task) {
        return String.Join("；", task.Choices.Select((c, i) => (i + 1) + ". " + c));
    }
    static readonly Dictionary<string, string> States = new Dictionary<string, string> {
        { "running", "进行中" }, { "waiting", "在等回答" }, { "completed", "已完成" }, { "failed", "失败" }, { "cancelled", "已停止" } };
    /// What a task tool (`delegate_task`, `task_status`, `task_cancel`, `task_answer`) answers, in words the AI reads.
    public static string Said(string tool, Dictionary<string, object> back) {
        Func<string, string> word = key => back.ContainsKey(key) && back[key] != null ? Convert.ToString(back[key], CultureInfo.InvariantCulture) : "";
        string id = word("task_id"), state = word("state"), who = Tool.LabelOf(word("tool")) + (word("model").Length > 0 ? " · " + word("model") : "") + " · " + word("title");
        string stateWords = States.ContainsKey(state) ? States[state] : state;
        var text = new StringBuilder();
        if (tool == Peers.DelegateToolName) {
            text.Append("已派出子任务 " + id + "（" + who + "）。");
            if (word("worktree").Length > 0) text.Append("它在独立工作树 " + word("worktree") + "（分支 " + word("branch") + "）里工作。");
        } else if (tool == Peers.AnswerToolName) {
            text.Append("已回答子任务 " + id + " 的问题，它会接着做。");
        } else {
            text.Append("子任务 " + id + "（" + who + "）：" + stateWords + (word("error").Length > 0 ? "（" + word("error") + "）" : "") + "。");
            if (state == "waiting") text.Append("\n问题：" + word("question") + "\n选项：" + String.Join("；", ((IEnumerable<string>)back["choices"]).Select((c, i) => (i + 1) + ". " + c)));
            if (word("result").Length > 0) text.Append("\n\n" + word("result"));
        }
        if (word("next").Length > 0) text.Append("\n" + word("next"));
        return text.ToString();
    }
    /// What the AI and the phone are shown of a task.
    static Dictionary<string, object> Shown(Task task) {
        var shown = new Dictionary<string, object> {
            { "task_id", task.Id }, { "tool", task.Tool }, { "title", task.Title }, { "state", task.State }, { "terminal", task.Child },
            { "started", task.Started } };
        if (task.Model.Length > 0) shown["model"] = task.Model;
        if (task.Ended > 0) shown["ended"] = task.Ended;
        if (task.Error.Length > 0) shown["error"] = task.Error;
        if (task.Worktree.Length > 0) { shown["worktree"] = task.Worktree; shown["branch"] = task.Branch; }
        if (task.State == "waiting") { shown["question"] = task.Question; shown["choices"] = task.Choices.ToArray(); }
        return shown;
    }
    void Show(Task task) {
        Dictionary<string, object> card;
        lock (gate) {
            card = Shown(task);
            card["t"] = "task";
            card["id"] = "task-" + task.Id;
            string said = task.State == "completed" ? Line(task.Result, 200) : "";
            if (said.Length > 0) card["said"] = said;
        }
        try { hands.Card(task.Parent, card); } catch (Exception) { }
        var changed = Changed;
        if (changed != null) try { changed(); } catch (Exception) { }
    }
    /// The first words the task's conversation is given: the task, and how it is to work and report.
    static string Brief(Task task, string words, bool worktree) {
        return "【子任务】" + Tool.LabelOf(task.ParentTool) + " 在另一段对话里把这项工作派给你。你看不到那段对话，下面就是全部要求：\n\n" + words
            + (worktree ? "\n\n你在这个项目的独立 Git 工作树里工作，改动不会影响主目录；不要合并到其它分支。" : "")
            + "\n\n请自己完成，不要再派出子任务。做完后，在回答的最后写清结果、改动的文件和验证证据；需要用户决定的事写明后结束本轮。";
    }
    static string Word(IDictionary<string, object> args, string key) {
        object value; return args != null && args.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim() : "";
    }
    static string Line(string text, int most = 200) {
        string first = (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim().TrimStart('#', '-', '*', ' ')).FirstOrDefault(l => l.Length > 0) ?? "";
        return first.Length > most ? first.Substring(0, most - 1) + "…" : first;
    }
    static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 8); }
    static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }
    static Dictionary<string, object> Ok(Dictionary<string, object> back) { back["ok"] = true; return back; }
    static Dictionary<string, object> Fail(string error) { return new Dictionary<string, object> { { "ok", false }, { "error", error } }; }
}
}
