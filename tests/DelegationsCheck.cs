using System;
using System.Collections.Generic;
using System.Linq;
using RemoteCli;

/// Verifies the tasks a conversation gives out (Delegations.cs) with stand-in conversations: the task's first words,
/// results told at once to a waiting conversation and after the turn of a busy one (several in one message, long ones
/// left to `task_status`), questions with choices told and answered by number or words, stop and end of the giver
/// stopping its tasks, a task's conversation that ends by itself, the bound on running tasks, and the cards shown.
public static class DelegationsCheck {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }

    sealed class Stand {
        public readonly Dictionary<string, string> States = new Dictionary<string, string>();
        public readonly List<string[]> Said = new List<string[]>(), Opened = new List<string[]>();
        public readonly List<string> Stopped = new List<string>(), Closed = new List<string>(), Answered = new List<string>();
        public readonly List<Dictionary<string, object>> Cards = new List<Dictionary<string, object>>();
        public Delegations.Opening Last;
        int next;
        public Delegations.Hands Hands() {
            return new Delegations.Hands {
                Begin = one => {
                    if (one.Tool == "nope") throw new ArgumentException("电脑上没有安装 nope");
                    Last = one;
                    string id = "child-" + (++next);
                    States[id] = "busy";
                    Opened.Add(new[] { id, one.Tool, one.Words });
                    return new Delegations.Began { Terminal = id, Worktree = one.Worktree ? "C:/wt/" + id : "", Branch = one.Worktree ? "remote-ai/" + id : "" };
                },
                StateOf = t => States.ContainsKey(t) ? States[t] : "",
                Say = (t, words) => { if (!States.ContainsKey(t) || States[t] == "") throw new InvalidOperationException("ended"); Said.Add(new[] { t, words }); },
                Stop = t => Stopped.Add(t),
                Close = t => { Closed.Add(t); States[t] = ""; },
                Answer = (t, ask, decision) => { Answered.Add(t + " " + ask + " " + decision); return States.ContainsKey(t) && States[t] == "confirm"; },
                Card = (t, card) => Cards.Add(card) };
        }
    }
    static Dictionary<string, object> Args(params string[] pairs) {
        var args = new Dictionary<string, object>();
        for (int i = 0; i + 1 < pairs.Length; i += 2) args[pairs[i]] = pairs[i + 1];
        return args;
    }
    static string Id(Dictionary<string, object> back) { Require(true.Equals(back["ok"]), "ok: " + (back.ContainsKey("error") ? back["error"] : "")); return (string)back["task_id"]; }

    public static int Main() {
        try {
            var stand = new Stand();
            var tasks = new Delegations(stand.Hands());
            stand.States["parent"] = "busy";

            // refused: no task, bad isolation, a tool not there; nothing begun
            Require(!true.Equals(tasks.Start("parent", "codex", Args("tool", "claude"))["ok"]), "a task without words is refused");
            Require(((string)tasks.Start("parent", "codex", Args("tool", "claude", "task", "x", "isolation", "copy"))["error"]).Contains("isolation"), "isolation checked");
            Require(((string)tasks.Start("parent", "codex", Args("tool", "nope", "task", "x"))["error"]).Contains("没有安装"), "the agent's refusal is passed on");
            Require(stand.Opened.Count == 0, "nothing begun for refused tasks");

            // a task: its first words say whose it is and how to report; the answer tells not to wait
            var started = tasks.Start("parent", "codex", Args("tool", "claude", "task", "核对 docs/a.md 的条文\n第二行", "title", "核证公开法条"));
            string a = Id(started);
            Require(stand.Last.Words.StartsWith("【子任务】Codex") && stand.Last.Words.Contains("核对 docs/a.md") && stand.Last.Words.Contains("不要再派出子任务"), "first words: " + stand.Last.Words);
            Require(Delegations.Said(Peers.DelegateToolName, started).Contains("已派出子任务 " + a) && Delegations.Said(Peers.DelegateToolName, started).Contains("不要查询或等待"), "start said");
            Require(stand.Cards.Last()["state"].Equals("running") && stand.Cards.Last()["id"].Equals("task-" + a) && stand.Cards.Last()["title"].Equals("核证公开法条"), "running card");
            string b = Id(tasks.Start("parent", "codex", Args("tool", "agy", "task", "复核界面文字", "isolation", "worktree")));
            Require(stand.Last.Worktree && tasks.Of("child-2").Id == b && tasks.Of("parent") == null, "worktree asked; task found by its conversation");

            // results while the giver is busy wait for its turn to end, and go together in one message
            tasks.Turned("child-1", "done", "结论：条文一致。\n改动文件：无");
            Require(stand.Said.Count == 0 && stand.Closed.Contains("child-1"), "busy giver not told yet; task's conversation closed");
            Require(stand.Cards.Last()["state"].Equals("completed") && ((string)stand.Cards.Last()["said"]).StartsWith("结论"), "completed card");
            tasks.Turned("child-2", "failed", "构建失败：缺少依赖");
            stand.States["parent"] = "idle";
            tasks.Turned("parent", "done", "我先等结果");
            Require(stand.Said.Count == 1 && stand.Said[0][0] == "parent", "told once after the giver's turn");
            string told = stand.Said[0][1];
            Require(told.StartsWith("【子任务结果】") && told.Contains("不是用户的新要求") && told.Contains("## 子任务 " + a + "（Claude Code · 核证公开法条）已完成") && told.Contains("结论：条文一致")
                && told.Contains("## 子任务 " + b) && told.Contains("失败：构建失败") && told.Contains("独立工作树 C:/wt/child-2") && !told.Contains("还有"), "results told: " + told);
            Require(((string)tasks.Status("parent", a)["state"]) == "completed" && ((string)tasks.Status("parent", a)["result"]).Contains("条文一致"), "status gives the result");
            Require(!true.Equals(tasks.Status("other", a)["ok"]), "another conversation cannot read it");

            // a long result is folded where it is alone, and pointed to where another came first
            string c = Id(tasks.Start("parent", "codex", Args("tool", "claude", "task", "短")));
            string d = Id(tasks.Start("parent", "codex", Args("tool", "claude", "task", "长")));
            string e = Id(tasks.Start("parent", "codex", Args("tool", "claude", "task", "还在做")));
            stand.States["parent"] = "busy";
            tasks.Turned(tasks.Given("parent").First(t => t.Id == c).Child, "done", "短结果");
            tasks.Turned(tasks.Given("parent").First(t => t.Id == d).Child, "done", new string('长', Delegations.ResultChars));
            stand.States["parent"] = "idle";
            tasks.Turned("parent", "done", "");
            told = stand.Said.Last()[1];
            Require(told.Contains("短结果") && told.Contains("用 task_status 读取（task_id: " + d + "）") && told.Contains("还有 1 个子任务在进行"), "long result pointed to: " + told.Substring(0, Math.Min(told.Length, 600)));

            // a result while the giver waits is told at once
            int before = stand.Said.Count;
            tasks.Turned(tasks.Given("parent").First(t => t.Id == e).Child, "done", "做完了");
            Require(stand.Said.Count == before + 1 && stand.Said.Last()[1].Contains("做完了"), "idle giver told at once");

            // a question with choices is told and answered by number or words; a decision is not told
            string q = Id(tasks.Start("parent", "codex", Args("tool", "codex", "task", "选方案")));
            string qChild = tasks.Given("parent").First(t => t.Id == q).Child;
            stand.States[qChild] = "confirm";
            before = stand.Said.Count;
            tasks.Asked(qChild, "ask-1", "tool", "运行命令", "rm x", new string[0]);
            Require(stand.Said.Count == before, "an approval is the user's, not told to the giver");
            tasks.Asked(qChild, "ask-2", "question", "用哪种格式？", "", new[] { "CSV", "Excel" });
            Require(stand.Said.Count == before + 1 && stand.Said.Last()[1].StartsWith("【子任务提问】") && stand.Said.Last()[1].Contains("1. CSV；2. Excel") && stand.Said.Last()[1].Contains("task_answer"), "question told: " + stand.Said.Last()[1]);
            Require(((string)tasks.Answer("parent", q, "3")["error"]).Contains("1 至 2"), "answer out of range refused");
            Require(true.Equals(tasks.Answer("parent", q, "excel")["ok"]) && stand.Answered.Last() == qChild + " ask-2 choice:1", "answered by words: " + stand.Answered.Last());
            Require(!true.Equals(tasks.Answer("parent", q, "1")["ok"]), "nothing to answer now");

            // the giver stopped: its open tasks stop; ended: the rest stop and are forgotten
            string s = Id(tasks.Start("parent", "codex", Args("tool", "claude", "task", "会被停")));
            string sChild = tasks.Given("parent").First(t => t.Id == s).Child;
            tasks.Turned("parent", "stopped", "");
            Require(stand.Stopped.Contains(sChild) && stand.Closed.Contains(sChild) && stand.Stopped.Contains(qChild), "stopping the giver stops its tasks");
            Require(((string)tasks.Status("parent", s)["state"]) == "cancelled", "stopped task cancelled");

            // cancel by the giver; a task's conversation that ends by itself fails and is told
            string x = Id(tasks.Start("parent", "codex", Args("tool", "claude", "task", "取消我")));
            Require(((string)tasks.Cancel("parent", x, "不需要了")["state"]) == "cancelled", "cancelled");
            string y = Id(tasks.Start("parent", "codex", Args("tool", "claude", "task", "会意外结束")));
            stand.States[tasks.Given("parent").First(t => t.Id == y).Child] = "";
            before = stand.Said.Count;
            tasks.Sweep();
            Require(((string)tasks.Status("parent", y)["state"]) == "failed" && stand.Said.Count == before + 1 && stand.Said.Last()[1].Contains("子任务的对话已经结束"), "ended task failed and told");

            // at most MostRunning at once
            for (int i = 0; i < Delegations.MostRunning; i++) Id(tasks.Start("parent", "codex", Args("tool", "claude", "task", "并行 " + i)));
            Require(((string)tasks.Start("parent", "codex", Args("tool", "claude", "task", "多一个"))["error"]).Contains("task_cancel"), "bound on running tasks");

            // the giver ends: its tasks stop, then all of it is forgotten
            stand.States["parent"] = "";
            tasks.Sweep();
            Require(tasks.Given("parent").All(t => t.State == "cancelled" || t.State == "completed" || t.State == "failed"), "tasks of an ended conversation stopped");
            tasks.Sweep();
            Require(tasks.Given("parent").Length == 0, "forgotten once over");
            Console.WriteLine("delegations: start, cards, results told after a busy turn and at once, folding, questions and answers, stop, cancel, sweep, bound ok");
            return 0;
        } catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message + "\n" + ex.StackTrace); return 1; }
    }
}
