using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using RemoteCli;

/// Conversations that the tools' hooks tell of (agent-windows/Reach.cs), with made-up events handed to the real hook
/// program the way Claude Code and Codex hand them over: who is asked a request for permission and when, what
/// becomes of a message from the phone, and what is written into the tools' settings.
///     ReachCheck.exe <RemoteCliAgent.exe>
public static class ReachCheck {
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static string agent, pipe, transcript;
    sealed class Hooked { public Process Program; public Task<string> Said; }
    static string Quoted(string text) { return new JavaScriptSerializer().Serialize(text); }
    static string Event(string name, string session, string more = "") {
        return "{\"hook_event_name\":\"" + name + "\",\"session_id\":\"" + session + "\",\"transcript_path\":" + Quoted(transcript) + ",\"cwd\":\"C:\\\\work\"" + more + "}";
    }
    static string Request(string session, string command, string why) {
        return Event("PermissionRequest", session, ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":" + Quoted(command) + ",\"description\":" + Quoted(why) + "}");
    }
    // The hook, started as a tool starts it: the event on its standard input, its answer on its standard output.
    static Hooked Run(string tool, string hooked) {
        var program = Process.Start(new ProcessStartInfo(agent, "--hook " + tool + " " + pipe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false) });
        byte[] bytes = new UTF8Encoding(false).GetBytes(hooked);
        program.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); program.StandardInput.Close();
        return new Hooked { Program = program, Said = program.StandardOutput.ReadToEndAsync() };
    }
    static string Answer(Hooked hook, int within, string what) {
        Require(hook.Program.WaitForExit(within), what + ": the hook did not end");
        Require(hook.Program.ExitCode == 0, what + ": the hook ended with " + hook.Program.ExitCode);
        return hook.Said.Result;
    }
    static bool Until(Func<bool> test, int within) {
        var until = DateTime.UtcNow.AddMilliseconds(within);
        while (DateTime.UtcNow < until) { if (test()) return true; Thread.Sleep(40); }
        return test();
    }
    static Dictionary<string, object> entry0(Dictionary<string, object> hooks, string name, string program) {
        return ((IEnumerable)hooks[name]).Cast<object>().Select(group => (Dictionary<string, object>)((IEnumerable)((Dictionary<string, object>)group)["hooks"]).Cast<object>().First()).Single(hook => Convert.ToString(hook["command"]) == program);
    }
    static string Refused(Action act) { try { act(); return ""; } catch (InvalidOperationException error) { return error.Message; } catch (ArgumentException error) { return error.Message; } }

    public static int Main(string[] args) {
        agent = args[0];
        string folder = Path.Combine(Path.GetTempPath(), "remote-cli-reach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        transcript = Path.Combine(folder, "conversation.jsonl"); File.WriteAllText(transcript, "{}\n");
        pipe = "RemoteCli.reach.check" + Guid.NewGuid().ToString("N").Substring(0, 8);
        int idle = 0, told = 0;       // idle: seconds nobody has touched the computer, as the check says it
        int wait = 300;               // how long a tool that stops is kept waiting, as its owner would have chosen
        bool asks = true;             // whether a request of Codex's is kept for the phone, as its owner would have chosen
        bool inTerminal = false;      // whether the hooks run in one of the program's own terminals, as the check says it
        int hookProcess = 0;          // the process the last hook was, as the pipe told it
        try {
            using (var reach = new Reach(pipe, () => idle, () => new Reach.Kept { AtStop = wait, Requests = asks }, (process, tool, session) => { hookProcess = process; return inTerminal; })) {
                reach.Changed += () => Interlocked.Increment(ref told);
                string one = Guid.NewGuid().ToString();
                Func<string, Reach.Open> claude = session => reach.Of("claude", session), codex = session => reach.Of("codex", session);

                // ---- nothing is known of a conversation until its tool tells of it; other words are not events
                Require(claude(one) == null && !reach.Heard("claude"), "a conversation nobody told of");
                Require(Answer(Run("other", Event("Stop", one)), 5000, "another tool") == "" && claude(one) == null, "a tool that is neither is not listened to");
                Require(Answer(Run("claude", "not an event"), 5000, "not an event") == "", "words that are no event get no answer");
                Require(Answer(Run("claude", Event("SessionStart", one)), 5000, "start") == "" && claude(one).Status == "idle", "a conversation that started waits");
                Require(Answer(Run("claude", Event("UserPromptSubmit", one)), 5000, "prompt") == "" && claude(one).Status == "busy", "one that was told something works");

                // ---- Claude Code shows its question on the computer while the hook runs: the phone may answer whoever is at the computer
                var asked = Run("claude", Request(one, "npm test", "Run the tests"));
                Require(Until(() => claude(one).Status == "confirm", 3000), "the request is known");
                var known = claude(one);
                Require(known.Answerable && known.AskText.Contains("npm test") && known.AskText.Contains("Run the tests") && known.AskId.Length == 12, "what is asked, in the tool's words: " + known.AskText);
                Require(Refused(() => reach.Answer("claude", one, "nosuchask", true)).Contains("已经处理"), "an answer to another request is refused");
                Require(!asked.Program.WaitForExit(600), "the hook waits for an answer");
                reach.Answer("claude", one, known.AskId, true);
                string verdict = Answer(asked, 3000, "allowed");
                Require(verdict.Contains("\"PermissionRequest\"") && verdict.Contains("\"behavior\":\"allow\""), "the tool is told to go ahead: " + verdict);
                Require(claude(one).Status == "busy" && claude(one).AskId == "", "and works again");
                Require(Refused(() => reach.Answer("claude", one, known.AskId, true)).Contains("已经处理"), "a request is answered once");

                // Claude Code writes to its conversation while it asks: that is not an answer
                asked = Run("claude", Request(one, "git push", ""));
                Require(Until(() => claude(one).Status == "confirm", 3000), "a second request");
                Thread.Sleep(1700); File.AppendAllText(transcript, "{\"progress\":true}\n");
                Require(!asked.Program.WaitForExit(700) && claude(one).Status == "confirm" && claude(one).Answerable, "a conversation that is written to while it asks still asks");
                // answered at the computer: the tool tells of what it does next, and the hook has nothing to say
                Answer(Run("claude", Event("UserPromptSubmit", one)), 5000, "the tool goes on");
                Require(Answer(asked, 3000, "answered at the computer") == "" && claude(one).Status == "busy" && claude(one).AskId == "", "a request answered at the computer is over on the phone too");
                // or the tool lets the hook go
                asked = Run("claude", Request(one, "git push", ""));
                Require(Until(() => claude(one).Status == "confirm", 3000), "a third request");
                asked.Program.Kill();
                Require(Until(() => claude(one).Status == "busy", 3000), "a hook the tool has let go asks nothing any more");

                // ---- Codex asks its hooks before it shows its question: with somebody at the computer the hook says nothing, at once
                string two = Guid.NewGuid().ToString();
                Answer(Run("codex", Event("UserPromptSubmit", two)), 5000, "codex prompt");
                string local = Answer(Run("codex", Request(two, "cargo build", "Build it")), 3000, "somebody is at the computer");
                Require(local == "", "the question is left to the computer");
                Require(codex(two).Status == "confirm" && !codex(two).Answerable, "the phone sees that it waits at the computer");
                Require(Refused(() => reach.Answer("codex", two, codex(two).AskId, true)).Contains("只能在电脑上"), "and cannot answer it");
                Thread.Sleep(1700); File.AppendAllText(transcript, "{\"went on\":true}\n");
                Require(codex(two).Status == "busy", "once the tool goes on, the question is over");
                // with nobody there, the phone is asked; no is said in words the tool passes on
                idle = 600;
                asked = Run("codex", Request(two, "rm -rf build", "Clean up"));
                Require(Until(() => codex(two).Status == "confirm" && codex(two).Answerable, 3000), "nobody is at the computer: the phone is asked");
                reach.Answer("codex", two, codex(two).AskId, false);
                verdict = Answer(asked, 3000, "declined");
                Require(verdict.Contains("\"behavior\":\"deny\"") && verdict.Contains("message"), "the tool is told no: " + verdict);
                // somebody comes back to the computer while the phone is asked: the question is theirs
                asked = Run("codex", Request(two, "cargo test", ""));
                Require(Until(() => codex(two).Status == "confirm" && codex(two).Answerable, 3000), "asked again");
                idle = 0;
                Require(Answer(asked, 3000, "somebody came back") == "" && !codex(two).Answerable && codex(two).Status == "confirm", "the question goes to whoever came back");

                // ---- what to do next. A tool that stops with somebody at the computer has simply stopped
                Require(Answer(Run("codex", Event("Stop", two, ",\"last_assistant_message\":\"All done.\"")), 3000, "stopped") == "", "nothing is said to a tool that stops");
                Require(codex(two).Status == "idle" && codex(two).Last == "All done." && !codex(two).Sayable, "it has stopped, and what it said last is known");
                Require(Refused(() => reach.Say("codex", two, "one more thing")).Contains("已经在电脑上停下"), "a message for a conversation that has stopped is refused in words");
                Require(Refused(() => reach.Say("codex", two, " ")).Contains("1 至 8000"), "an empty message is refused");
                // a message sent while the tool works reaches it when it stops
                Answer(Run("codex", Event("UserPromptSubmit", two)), 5000, "works again");
                Require(reach.Say("codex", two, "然后把文档也更新了") == false, "a message for a tool at work waits");
                string next = Answer(Run("codex", Event("Stop", two, ",\"last_assistant_message\":\"Built.\"")), 3000, "stopped with a message waiting");
                var went = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(next);
                Require(Convert.ToString(went["decision"]) == "block" && Convert.ToString(went["reason"]).Contains("然后把文档也更新了") && Convert.ToString(went["reason"]).Contains("phone"), "the tool is told to go on with it: " + next);
                Require(codex(two).Status == "busy", "and works on");
                // How the words are handed over hangs on the tool. Claude Code of a version that takes it is given them
                // as added context (its screen then says "Stop hook feedback", no error); an earlier one, one of an
                // unknown version, and Codex are told by a blocked stop, as before.
                Func<string, Dictionary<string, object>> answered = said => new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(said);
                var modern = answered(Reach.AtStop("claude", "2.1.283", "把结论再写短一点\n分两段"));
                var added = (Dictionary<string, object>)modern["hookSpecificOutput"];
                Require(!modern.ContainsKey("decision") && Convert.ToString(added["hookEventName"]) == "Stop" && Convert.ToString(added["additionalContext"]).StartsWith(Reach.FromPhone) && Convert.ToString(added["additionalContext"]).EndsWith("\n\n把结论再写短一点\n分两段"), "added context for a Claude Code that takes it");
                Require(answered(Reach.AtStop("claude", "3.0.0", "x")).ContainsKey("hookSpecificOutput") && answered(Reach.AtStop("claude", "2.2.0", "x")).ContainsKey("hookSpecificOutput"), "and for a later one");
                foreach (var earlier in new[] { new[] { "claude", "2.1.282" }, new[] { "claude", "" }, new[] { "claude", null }, new[] { "claude", "latest" }, new[] { "codex", "2.1.283" }, new[] { "codex", "" } }) {
                    var blocked = answered(Reach.AtStop(earlier[0], earlier[1], "x"));
                    Require(Convert.ToString(blocked["decision"]) == "block" && Convert.ToString(blocked["reason"]).EndsWith("\n\nx") && !blocked.ContainsKey("hookSpecificOutput"), "a blocked stop for " + earlier[0] + " " + earlier[1]);
                }
                // nobody at the computer: a tool that stops is kept waiting for the phone's message
                idle = 600;
                var stopped = Run("claude", Event("Stop", one, ",\"last_assistant_message\":\"Finished the tests.\""));
                Require(Until(() => claude(one).Status == "waiting", 3000) && claude(one).Sayable && claude(one).Last == "Finished the tests.", "it waits for the phone");
                Require(!stopped.Program.WaitForExit(500), "the hook is held");
                Require(reach.Say("claude", one, "run them once more") == true, "a message for a tool that waits is taken at once");
                next = Answer(stopped, 3000, "told what to do next");
                Require(next.Contains("\"block\"") && next.Contains("run them once more") && claude(one).Status == "busy", "it goes on with the message: " + next);
                // somebody comes back while it waits: it is let go, and has stopped
                stopped = Run("claude", Event("Stop", one, ",\"last_assistant_message\":\"Ran again.\""));
                Require(Until(() => claude(one).Status == "waiting", 3000), "waits again");
                idle = 5;
                Require(Answer(stopped, 3000, "somebody came back") == "" && claude(one).Status == "idle", "a tool is not kept waiting once somebody is at the computer");
                // nobody comes, and no message either: after its time it is let go, and has stopped. That time is
                // minutes, well inside what the tools are told a hook may take.
                Require(Reach.Waits.All(seconds => seconds < Reach.Patience) && Reach.Hold < Reach.Patience, "no wait is longer than the tools are told a hook may take");
                idle = 600; wait = 1;
                stopped = Run("claude", Event("Stop", one, ",\"last_assistant_message\":\"Ran a third time.\""));
                Require(Until(() => claude(one).Status == "waiting", 3000) && !stopped.Program.WaitForExit(400), "waits a third time");
                Require(Answer(stopped, 4000, "its time is over") == "" && Until(() => !claude(one).Held, 1000) && claude(one).Status == "waiting" && !claude(one).Sayable, "a tool is let go when its time is over, and it is the phone's turn still");
                Require(Refused(() => reach.Say("claude", one, "too late for the hook")).Contains("已经在电脑上停下"), "what is said to it then goes in by the tool's own way, not through the hook");
                idle = 5;
                Require(Until(() => claude(one).Status == "idle", 3000), "somebody came back to one that had stopped: it is the phone's turn no more");
                idle = 600;
                // unless its owner chose a time, a tool is not kept waiting at all: it stops as it would without this
                // program. What is kept in the settings is read as one of the times there are, else as none.
                Require(Reach.Wait(300) == 300 && Reach.Wait("900") == 900 && Reach.Wait(0) == 0 && Reach.Wait(null) == 0 && Reach.Wait("") == 0 && Reach.Wait(7) == 0 && Reach.Wait("soon") == 0, "the time as the settings keep it");
                wait = 0;
                int tellings = told;
                Require(Answer(Run("claude", Event("Stop", one, ",\"last_assistant_message\":\"Ran a fourth time.\"")), 3000, "not to wait") == "" && claude(one).Last == "Ran a fourth time.", "with no time to wait a tool that stops is let go at once");
                Require(claude(one).Status == "waiting" && !claude(one).Held && !claude(one).Sayable && told > tellings, "and it is the phone's turn all the same: it has stopped while nobody was at the computer");
                wait = 300; idle = 5; tellings = told;
                Require(Until(() => claude(one).Status == "idle", 3000) && Until(() => told > tellings, 1000), "until somebody is back at the computer, which is told");

                // ---- what a hook keeps its tool for, showing nothing but a running hook, is told apart: the computer's
                // screen is to say it in words. A request of Codex's that waits for the phone is such; one of Claude
                // Code's is not (its own question is on the screen); a stop that waits is.
                string kept = Guid.NewGuid().ToString();
                idle = 600; asks = true; wait = 300;
                var keptAsk = Run("codex", Request(kept, "git push", "Publish"));
                Require(Until(() => codex(kept) != null && codex(kept).Kept, 3000) && reach.KeptNow().Length == 1 && reach.KeptNow()[0].Session == kept && reach.KeptNow()[0].Status == "confirm", "a request of Codex's that waits for the phone is kept, to be told at the computer");
                idle = 5;
                Require(Answer(keptAsk, 3000, "somebody came back") == "" && reach.KeptNow().Length == 0, "and no longer once somebody is at the computer");
                idle = 600;
                var keptStop = Run("claude", Event("Stop", kept, ",\"last_assistant_message\":\"Done.\""));
                Require(Until(() => reach.KeptNow().Length == 1 && reach.KeptNow()[0].Status == "waiting", 3000), "a stop that waits for the phone's message is kept too");
                idle = 5;
                Require(Answer(keptStop, 3000, "somebody came back to it") == "" && reach.KeptNow().Length == 0, "until it is let go");

                // ---- nobody answers, and nobody is at the computer when the request's time is over: the tool is not
                // left at its own question, which could only be answered at the computer. It is told to stop and say
                // what it needs; its owner, away for however long, tells it from the phone when to go on.
                string late = Guid.NewGuid().ToString(), lateToo = Guid.NewGuid().ToString(), there = Guid.NewGuid().ToString();
                Environment.SetEnvironmentVariable("REMOTECLI_HOLD", "1");
                idle = 600; asks = true;
                string put = Answer(Run("codex", Request(late, "git push", "Publish")), 5000, "nobody answered Codex in time");
                Require(put.Contains("\"behavior\":\"deny\"") && put.Contains("not answered this request on their phone") && codex(late).Status == "busy" && reach.KeptNow().Length == 0, "Codex is told to stop and say what it needs: " + put);
                put = Answer(Run("claude", Request(lateToo, "npm publish", "")), 5000, "nobody answered Claude Code in time");
                Require(put.Contains("\"behavior\":\"deny\"") && put.Contains(Reach.Deferred.Substring(0, 40)) && claude(lateToo).Status == "busy", "and so is Claude Code: " + put);
                // somebody is at the computer when the time is over: the question stays theirs, as it was
                var theirs = Run("claude", Request(there, "npm publish", ""));
                Require(Until(() => claude(there) != null && claude(there).Answerable, 3000), "asked, and the phone's to answer");
                idle = 5;
                Require(Until(() => !claude(there).Answerable, 4000) && claude(there).Status == "confirm" && !theirs.Program.WaitForExit(600), "its time over with somebody at the computer: left to them, at the tool's own question");
                theirs.Program.Kill();
                Environment.SetEnvironmentVariable("REMOTECLI_HOLD", null);

                // ---- Claude Code is told to stop: it is heard before each step it takes, and answered there. One
                // that was opened before the hook for that was in its settings is not heard so, and is not offered it.
                string halting = Guid.NewGuid().ToString();
                idle = 5;
                Answer(Run("claude", Event("UserPromptSubmit", halting, ",\"prompt\":\"compare them\"")), 3000, "asked to work");
                Require(claude(halting).Status == "busy" && !claude(halting).Steps && !claude(halting).Stoppable && Refused(() => reach.Stop("claude", halting)).Contains("重新打开"), "not before it has been heard before a step");
                Require(Answer(Run("claude", Event("PreToolUse", halting, ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"echo one\"}")), 3000, "before its first step") == "" && claude(halting).Steps && claude(halting).Stoppable, "before a step nothing is said, and it can be told to stop from now on");
                reach.Stop("claude", halting);
                string halted = Answer(Run("claude", Event("PreToolUse", halting, ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"echo two\"}")), 3000, "before its next step");
                Require(halted.Contains("\"continue\":false") && halted.Contains(Reach.StoppedFromPhone), "told to stop there: " + halted);
                // Claude Code runs no hook for a turn that was stopped so: how it stands is said at once
                Require(claude(halting).Status == "idle" && !claude(halting).Stoppable, "and it has stopped, as the phone is told");
                Answer(Run("claude", Event("UserPromptSubmit", halting, ",\"prompt\":\"go on\"")), 3000, "told to go on");
                Require(Answer(Run("claude", Event("PreToolUse", halting, ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"echo three\"}")), 3000, "asked once only") == "", "it is told once");
                Answer(Run("claude", Event("Stop", halting, ",\"last_assistant_message\":\"Stopped.\"")), 3000, "it has stopped");
                Require(claude(halting).Status == "idle" && !claude(halting).Stoppable && Refused(() => reach.Stop("claude", halting)).Contains("没有在执行"), "one that is not at work has nothing to stop");
                // a stop that was asked for and not taken (the turn ended first) does not stop the next turn
                Answer(Run("claude", Event("UserPromptSubmit", halting, ",\"prompt\":\"again\"")), 3000, "at work again");
                Answer(Run("claude", Event("PreToolUse", halting, ",\"tool_name\":\"Read\",\"tool_input\":{}")), 3000, "a step");
                reach.Stop("claude", halting);
                Answer(Run("claude", Event("Stop", halting)), 3000, "ended by itself");
                Answer(Run("claude", Event("UserPromptSubmit", halting, ",\"prompt\":\"once more\"")), 3000, "the next turn");
                Require(Answer(Run("claude", Event("PreToolUse", halting, ",\"tool_name\":\"Read\",\"tool_input\":{}")), 3000, "its first step") == "", "the next turn is not stopped by what was asked of the one before");
                // a conversation nothing was heard of yet is not made known by a step alone
                Require(Answer(Run("claude", Event("PreToolUse", Guid.NewGuid().ToString(), ",\"tool_name\":\"Read\"")), 3000, "a step of one unknown") == "", "nothing is said before a step of a conversation that is not known");
                Answer(Run("claude", Event("Stop", halting)), 3000, "and it rests");

                // ---- each message is an item of its own: two said while it works are handed over in order when it
                // stops, each can be asked after by its number, and one can be taken back before that
                string several = Guid.NewGuid().ToString(), m1 = Guid.NewGuid().ToString("N"), m2 = Guid.NewGuid().ToString("N"), m3 = Guid.NewGuid().ToString("N");
                Answer(Run("codex", Event("UserPromptSubmit", several)), 3000, "at work");
                Require(!reach.Say("codex", several, "第一条", m1) && !reach.Say("codex", several, "第二条", m2) && !reach.Say("codex", several, "第三条", m3), "three wait");
                Require(String.Join(",", reach.Outbox.Of("codex", several).Select(item => item.Text)) == "第一条,第二条,第三条", "in the order they were said");
                Require(reach.Outbox.Add(m1, "codex", several, "第一条").Id == m1 && reach.Outbox.Of("codex", several).Length == 3, "said again under its number, it is the same message");
                Require(Refused(() => reach.Outbox.Add(m1, "codex", several, "别的话")).Contains("已被其它消息使用"), "a number is not used for other words");
                Require(reach.Outbox.Withdraw(m2) && !reach.Outbox.Withdraw(m2) && reach.Outbox.Find(m2).State == Outbox.Withdrawn, "one is taken back, once");
                string both = Answer(Run("codex", Event("Stop", several)), 3000, "stopped with two waiting");
                Require(both.Contains("第一条\\n\\n第三条") && !both.Contains("第二条"), "the two that still waited, in order: " + both);
                Require(reach.Outbox.Find(m1).State == Outbox.Handed && reach.Outbox.Find(m3).State == Outbox.Handed && !reach.Outbox.Withdraw(m1), "handed over: they cannot be taken back any more");

                // ---- a conversation that is told to stop has what waited for it held, not handed to it when it next
                // stops by itself; released, it waits again
                string held = Guid.NewGuid().ToString(), h1 = Guid.NewGuid().ToString("N");
                idle = 5;
                Answer(Run("claude", Event("UserPromptSubmit", held)), 3000, "at work");
                Answer(Run("claude", Event("PreToolUse", held, ",\"tool_name\":\"Read\",\"tool_input\":{}")), 3000, "a step");
                reach.Say("claude", held, "做完后再加一节", h1);
                Require(reach.Stop("claude", held, session => 0) == 0 && reach.Outbox.Find(h1).State == Outbox.Paused, "told to stop: what waited is held");
                Require(Answer(Run("claude", Event("PreToolUse", held, ",\"tool_name\":\"Read\",\"tool_input\":{}")), 3000, "its next step").Contains("\"continue\":false"), "and it stops");
                Answer(Run("claude", Event("UserPromptSubmit", held)), 3000, "its owner tells it something at the computer");
                Require(Answer(Run("claude", Event("Stop", held)), 3000, "it stops by itself") == "" && reach.Outbox.Find(h1).State == Outbox.Paused, "what is held is not handed over by itself");
                Require(reach.Outbox.Release("claude", held).Length == 1 && reach.Outbox.Find(h1).State == Outbox.Queued, "released, it waits again");
                Answer(Run("claude", Event("UserPromptSubmit", held)), 3000, "at work again");
                Require(Answer(Run("claude", Event("Stop", held)), 3000, "and is handed over at the next stop").Contains("做完后再加一节"), "handed over then");

                // ---- a stop ends the command the step is running: the shells the conversation's process started since
                // the step began, with what runs below them. What ran before, and what is no shell, is left alone.
                string cutting = Guid.NewGuid().ToString();
                Func<string, Process> start = ran => Process.Start(new ProcessStartInfo("cmd.exe", "/d /c " + ran) { UseShellExecute = false, CreateNoWindow = true });
                using (var older = start("ping -n 30 127.0.0.1 >nul")) {
                    Thread.Sleep(1500);
                    Answer(Run("claude", Event("UserPromptSubmit", cutting)), 3000, "at work");
                    Answer(Run("claude", Event("PreToolUse", cutting, ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"ping -n 30 127.0.0.1\"}")), 3000, "a long command begins");
                    using (var command = start("ping -n 30 127.0.0.1 >nul"))
                    using (var aside = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })) {
                        Require(Until(() => Running.Commands(Process.GetCurrentProcess().Id, DateTime.UtcNow.AddSeconds(-1.2)).Length >= 2, 4000), "the command and what it runs are found below this process");
                        int ended = reach.Stop("claude", cutting, session => Process.GetCurrentProcess().Id);
                        Require(ended >= 2 && command.WaitForExit(3000), "the command's shell and what ran in it are ended: " + ended);
                        Require(!older.HasExited && !aside.HasExited, "what ran before the step, and what is no shell, goes on");
                        aside.Kill();
                    }
                    older.Kill();
                }
                Require(Answer(Run("claude", Event("PreToolUse", cutting, ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"echo next\"}")), 3000, "the next step comes at once").Contains("\"continue\":false"), "and there it stops");
                // a step that was sent off to run beside the conversation is no command to end
                Answer(Run("claude", Event("UserPromptSubmit", cutting)), 3000, "at work");
                Answer(Run("claude", Event("PreToolUse", cutting, ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"npm run dev\",\"run_in_background\":true}")), 3000, "a server is started beside it");
                using (var server = start("ping -n 30 127.0.0.1 >nul")) {
                    Thread.Sleep(300);
                    Require(reach.Stop("claude", cutting, session => Process.GetCurrentProcess().Id) == 0 && !server.WaitForExit(600), "what runs beside the conversation is left running");
                    Running.End(Process.GetCurrentProcess().Id, DateTime.UtcNow.AddSeconds(-5));
                }
                Answer(Run("claude", Event("Stop", cutting)), 3000, "rests");

                // ---- where its owner switched that off, a request of Codex's is not kept: Codex asks its hooks before
                // it shows its question, and kept it shows a running hook. It shows its own question at once then, and
                // the phone is told that it asks, at the computer. Claude Code's is kept as ever: beside its own question.
                string unkept = Guid.NewGuid().ToString(), beside = Guid.NewGuid().ToString();
                idle = 600; asks = false;
                Require(Answer(Run("codex", Request(unkept, "git push", "Publish")), 3000, "Codex asked, nothing chosen") == "" && codex(unkept).Status == "confirm" && !codex(unkept).Answerable && codex(unkept).AskText.Contains("git push"), "Codex shows its own question, and the phone sees that it asks");
                Require(Refused(() => reach.Answer("codex", unkept, codex(unkept).AskId, true)).Contains("只能在电脑上"), "which the phone cannot answer");
                var shown = Run("claude", Request(beside, "npm test", ""));
                Require(Until(() => claude(beside) != null && claude(beside).Status == "confirm" && claude(beside).Answerable, 3000), "Claude Code's request is the phone's to answer all the same");
                Require(claude(beside).Beside && !claude(beside).Kept && reach.KeptNow().Length == 0, "with nothing to explain at the computer: its own question is there");
                reach.Answer("claude", beside, claude(beside).AskId, true);
                Require(Answer(shown, 3000, "allowed from the phone").Contains("\"behavior\":\"allow\""), "and is answered");
                asks = true;
                var chosen = new Dictionary<string, object> { { "ReachWait", 300 }, { "ReachAsk", true } };
                Require(Reach.Kept.From(chosen).AtStop == 300 && Reach.Kept.From(chosen).Requests && Reach.Kept.From(null).AtStop == 0 && Reach.Kept.From(null).Requests && new Reach.Kept().Requests && !Reach.Kept.From(new Dictionary<string, object> { { "ReachAsk", false } }).Requests && !Reach.Kept.From(new Dictionary<string, object> { { "ReachAsk", "False" } }).Requests, "what is kept for the phone, as the settings keep it: a request unless switched off, no wait unless chosen");

                // ---- a conversation in one of the program's own terminals is at the phone already, in that terminal:
                // nothing of it is kept, whoever is or is not at the computer and whatever time was chosen
                string inside = Guid.NewGuid().ToString(), within = Guid.NewGuid().ToString();
                idle = 600; wait = 300; asks = true; inTerminal = true;
                var own = Run("codex", Request(inside, "git push", "Publish"));
                Require(Answer(own, 3000, "asked in a terminal of the phone's") == "" && codex(inside).Status == "confirm" && !codex(inside).Answerable, "its question is the terminal's to answer: the tool shows it there");
                Require(hookProcess == own.Program.Id, "which hook it is, is read from the pipe: " + hookProcess + " / " + own.Program.Id);
                Require(Answer(Run("claude", Request(within, "npm test", "")), 3000, "Claude Code asked in such a terminal") == "" && !claude(within).Answerable, "the same for Claude Code");
                Require(Answer(Run("claude", Event("Stop", within, ",\"last_assistant_message\":\"Done here.\"")), 3000, "stopped in such a terminal") == "" && claude(within).Status == "idle" && !claude(within).Held, "and it stops as it would without this program");
                reach.Leave("claude", within, "one more thing");
                Require(Answer(Run("claude", Event("Stop", within)), 3000, "something was left for it").Contains("one more thing"), "what was left for it is handed over there too");
                inTerminal = false; idle = 5;

                // ---- a run no person sits at (a script's `claude -p`, `codex exec`, a helper thread of a conversation) is
                // never kept waiting, however long nobody has been at the computer, and is no conversation for a phone
                idle = 600;
                string script = Guid.NewGuid().ToString(), desk = Guid.NewGuid().ToString(), exec = Guid.NewGuid().ToString(), helper = Guid.NewGuid().ToString(), wasTranscript = transcript;
                string notes = Path.Combine(folder, "claude-notes");
                Directory.CreateDirectory(Path.Combine(notes, "sessions"));
                File.WriteAllText(Path.Combine(notes, "sessions", "1.json"), "{\"pid\":1,\"sessionId\":\"" + script + "\",\"kind\":\"interactive\",\"entrypoint\":\"sdk-cli\"}");
                File.WriteAllText(Path.Combine(notes, "sessions", "2.json"), "{\"pid\":2,\"sessionId\":\"" + desk + "\",\"kind\":\"interactive\",\"entrypoint\":\"claude-desktop-3p\"}");
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", notes);
                Require(ClaudeSessions.Entry(script) == "sdk-cli" && ClaudeSessions.Entry(desk) == "claude-desktop-3p" && ClaudeSessions.Entry(exec) == "", "how a session was started, as Claude Code notes it");
                Require(Answer(Run("claude", Event("Stop", script)), 5000, "a script's run of Claude Code") == "" && claude(script) == null, "a script's run is let go at once and is not listed");
                Require(Answer(Run("claude", Request(script, "rm -rf build", "Clean")), 5000, "a script's request") == "" && claude(script) == null, "what a script's run asks is the script's to answer");
                transcript = Path.Combine(folder, "exec.jsonl");
                File.WriteAllText(transcript, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"x\",\"originator\":\"codex_exec\",\"source\":\"exec\"}}\n{}\n");
                Require(Answer(Run("codex", Event("Stop", exec)), 5000, "codex exec") == "" && codex(exec) == null, "codex exec is let go at once and is not listed");
                transcript = Path.Combine(folder, "helper.jsonl");
                File.WriteAllText(transcript, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"x\",\"originator\":\"codex-tui\",\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"y\"}}}}}\n");
                Require(Answer(Run("codex", Event("Stop", helper)), 5000, "a helper thread") == "" && codex(helper) == null, "a helper thread of a conversation is let go at once");
                // a person's conversation in the desktop app is listened to as before
                transcript = wasTranscript;
                Answer(Run("claude", Event("UserPromptSubmit", desk)), 5000, "the desktop app's conversation");
                Require(claude(desk) != null && claude(desk).Status == "busy", "a conversation of the desktop app is a person's");
                Answer(Run("claude", Event("SessionEnd", desk)), 5000, "the desktop app's conversation ends");
                // ---- what the phone says while Claude Code works is handed to it before its next step, where its
                // version takes added context from a hook there; an earlier one is given it when it stops
                string steered = Guid.NewGuid().ToString(), earlierOne = Guid.NewGuid().ToString();
                idle = 5;       // (somebody is at the computer: a stop is not kept waiting)
                File.WriteAllText(Path.Combine(notes, "sessions", "3.json"), "{\"pid\":3,\"sessionId\":\"" + steered + "\",\"kind\":\"interactive\",\"entrypoint\":\"cli\",\"version\":\"2.1.283\"}");
                File.WriteAllText(Path.Combine(notes, "sessions", "4.json"), "{\"pid\":4,\"sessionId\":\"" + earlierOne + "\",\"kind\":\"interactive\",\"entrypoint\":\"cli\",\"version\":\"2.1.100\"}");
                foreach (string each in new[] { steered, earlierOne }) {
                    Answer(Run("claude", Event("UserPromptSubmit", each)), 3000, "at work");
                    Require(Answer(Run("claude", Event("PreToolUse", each, ",\"tool_name\":\"Read\",\"tool_input\":{}")), 3000, "a step, nothing said") == "", "nothing waits: nothing is said before a step");
                    reach.Say("claude", each, "顺便把标题也改了");
                }
                string between = Answer(Run("claude", Event("PreToolUse", steered, ",\"tool_name\":\"Read\",\"tool_input\":{}")), 3000, "its next step");
                var handedOver = (Dictionary<string, object>)new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(between)["hookSpecificOutput"];
                Require(Convert.ToString(handedOver["hookEventName"]) == "PreToolUse" && Convert.ToString(handedOver["additionalContext"]) == Reach.BetweenSteps + "顺便把标题也改了" && !between.Contains("continue"), "handed over between two steps: " + between);
                Require(Answer(Run("claude", Event("PreToolUse", steered, ",\"tool_name\":\"Read\",\"tool_input\":{}")), 3000, "the step after") == "" && Answer(Run("claude", Event("Stop", steered)), 3000, "its stop") == "", "once: neither at the step after nor at its stop");
                Require(Answer(Run("claude", Event("PreToolUse", earlierOne, ",\"tool_name\":\"Read\",\"tool_input\":{}")), 3000, "an earlier Claude Code's step") == "", "an earlier version is not given it at a step");
                Require(Answer(Run("claude", Event("Stop", earlierOne)), 3000, "but at its stop").Contains("顺便把标题也改了"), "it is given it when it stops");
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null); idle = 0;

                // ---- words left for a conversation that sits idle, also one that has told of nothing: its Stop hook gives
                // them to it when it next stops, whoever is at the computer; what nothing comes for is taken back
                string asleep = Guid.NewGuid().ToString();
                string left = Guid.NewGuid().ToString("N"), unfetched = Guid.NewGuid().ToString("N");
                reach.Leave("claude", asleep, "把结论再写短一点", left);
                Require(reach.Outbox.Find(left).State == Outbox.Queued, "it waits for the conversation under its number");
                bool? came = null;
                var waits = Task.Run(() => { came = reach.Taken(left, 20); });
                var woke = Run("claude", Event("Stop", asleep, ",\"last_assistant_message\":\"…\""));
                string given = Answer(woke, 5000, "the words left are given at the stop");
                Require(given.Contains("\"decision\":\"block\"") && given.Contains(Reach.FromPhone) && given.Contains("把结论再写短一点"), "given as the user's words: " + given);
                Require(waits.Wait(3000) && came == true, "and whoever left them is told at once that they were taken");
                Require(reach.Outbox.Find(left).State == Outbox.Handed && reach.Outbox.Find(left).Text == "", "handed over, and its words are kept no longer");
                reach.Leave("claude", asleep, "没有人来取", unfetched);
                var began = DateTime.UtcNow;
                Require(!reach.Taken(unfetched, 1) && (DateTime.UtcNow - began).TotalSeconds < 3, "what nothing comes for is taken back");
                Require(Answer(Run("claude", Event("Stop", asleep)), 5000, "nothing lies there any more") == "", "and is not given later");
                Answer(Run("claude", Event("SessionEnd", asleep)), 5000, "ended");

                // ---- a conversation that ends is forgotten
                Answer(Run("claude", Event("SessionEnd", one)), 5000, "ended");
                Require(claude(one) == null && codex(two) != null, "only the one that ended");
                Require(told >= 12, "what changes is told at once: " + told);
                Require(reach.Heard("claude") && reach.Heard("codex") && !reach.Heard("other"), "a tool that has told of something is known to reach this program");
            }
            Require(Answer(Run("claude", Event("Stop", Guid.NewGuid().ToString())), 5000, "nobody listens") == "", "with nothing listening the hook ends at once and says nothing");
            Require(Answer(Run("codex", Request(Guid.NewGuid().ToString(), "git push", "")), 5000, "nobody listens to a request") == "", "a request too, when the program was never there");

            // ---- what the phone said and a conversation has not taken yet is kept on disk, encrypted: the program
            // starting again loses none of it, and what is settled keeps no words
            string stored = Path.Combine(folder, "outbox"), surviving = Guid.NewGuid().ToString(), s1 = Guid.NewGuid().ToString("N"), s2 = Guid.NewGuid().ToString("N");
            using (var earlier = new Reach(pipe, () => 5, null, null, new Outbox(stored))) {
                Answer(Run("codex", Event("UserPromptSubmit", surviving)), 3000, "at work");
                earlier.Say("codex", surviving, "重启之前说的第一句", s1); earlier.Say("codex", surviving, "重启之前说的第二句", s2);
                Require(Directory.GetFiles(stored, "*.json").Length == 2 && !Directory.GetFiles(stored).Any(file => File.ReadAllText(file).Contains("重启之前") || File.ReadAllText(file).Contains("\\u91cd")), "kept in files, and not in plain words");
            }
            using (var later = new Reach(pipe, () => 5, null, null, new Outbox(stored))) {
                Require(String.Join(",", later.Outbox.Of("codex", surviving).Select(item => item.Text)) == "重启之前说的第一句,重启之前说的第二句", "they are there again, in order");
                string after = Answer(Run("codex", Event("Stop", surviving)), 3000, "the conversation stops after the program came back");
                Require(after.Contains("重启之前说的第一句\\n\\n重启之前说的第二句"), "and is handed what waited: " + after);
                Require(later.Outbox.Find(s1).State == Outbox.Handed && !Directory.GetFiles(stored).Any(file => File.ReadAllText(file).Contains("\"text\"")), "settled: its words are dropped from the file");
            }
            Require(new Outbox(stored).Find(s2).State == Outbox.Handed && new Outbox(stored).Of("codex", surviving).Length == 0, "how it ended is remembered");

            // ---- the program is started again while it keeps a request for the phone (an update, asked for from the
            // phone): the request is not lost to the tool's own question. Its hook waits and hands it over anew.
            string through = Guid.NewGuid().ToString();
            Hooked carried;
            using (var earlier = new Reach(pipe, () => 600)) {
                carried = Run("codex", Request(through, "git push", "Publish"));
                Require(Until(() => earlier.Of("codex", through) != null && earlier.Of("codex", through).Answerable, 3000), "kept by the program as it was");
            }
            Require(!carried.Program.WaitForExit(2500), "the program has closed: the hook waits for it to be there again");
            using (var later = new Reach(pipe, () => 600)) {
                Require(Until(() => later.Of("codex", through) != null && later.Of("codex", through).Answerable && later.Of("codex", through).AskText.Contains("git push"), 6000), "the program is there again and has the request");
                later.Answer("codex", through, later.Of("codex", through).AskId, true);
                Require(Answer(carried, 3000, "answered after the program came back").Contains("\"behavior\":\"allow\""), "and the phone's answer reaches the tool");
            }

            // ---- the tools' settings: only this program's entries are added and taken out
            string claudeHome = Path.Combine(folder, "claude"), codexHome = Path.Combine(folder, "codex");
            Directory.CreateDirectory(claudeHome); Directory.CreateDirectory(codexHome);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeHome); Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            string settings = Path.Combine(claudeHome, "settings.json"), codexHooks = Path.Combine(codexHome, "hooks.json");
            string before = "{\n  \"model\": \"opus\",\n  \"note\": \"保留原样\",\n  \"number\": 1.5,\n  \"hooks\": {\n    \"Stop\": [{\"hooks\": [{\"type\": \"command\", \"command\": \"echo mine\"}]}],\n    \"PreToolUse\": [{\"matcher\": \"Bash\", \"hooks\": [{\"type\": \"command\", \"command\": \"check.sh\"}]}]\n  }\n}\n";
            File.WriteAllText(settings, before, new UTF8Encoding(false));
            Require(!ReachHooks.Installed("claude") && !ReachHooks.Installed("codex"), "nothing is installed to begin with");
            string program = @"C:\Program Files\Remote CLI\RemoteCliAgent.exe";
            ReachHooks.Set("claude", true, program, "RemoteCli.reach.abc123");
            ReachHooks.Set("claude", true, program, "RemoteCli.reach.abc123");          // twice is once
            Require(ReachHooks.Installed("claude"), "installed for Claude Code");
            Require(ReachHooks.Installed("claude", program, "RemoteCli.reach.abc123") && !ReachHooks.Installed("claude", program, "RemoteCli.reach.other1") && !ReachHooks.Installed("claude", @"D:\moved\RemoteCliAgent.exe", "RemoteCli.reach.abc123"), "and known to be for this program at this pipe, not for another");
            ReachHooks.Set("claude", true, @"D:\moved\RemoteCliAgent.exe", "RemoteCli.reach.abc123");       // the program moved: its hooks follow it
            Require(ReachHooks.Installed("claude", @"D:\moved\RemoteCliAgent.exe", "RemoteCli.reach.abc123") && !File.ReadAllText(settings, Encoding.UTF8).Contains("Program Files"), "hooks for where the program was are replaced");
            ReachHooks.Set("claude", true, program, "RemoteCli.reach.abc123");
            var read = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(settings, Encoding.UTF8));
            var hooks = (Dictionary<string, object>)read["hooks"];
            Require(Convert.ToString(read["model"]) == "opus" && Convert.ToString(read["note"]) == "保留原样" && Convert.ToString(read["number"], System.Globalization.CultureInfo.InvariantCulture) == "1.5", "what else is in the file stays");
            Require(File.ReadAllText(settings, Encoding.UTF8).Contains("保留原样"), "letters of any language are written as they are");
            // (the person had a hook of their own before each step, and one at a stop: this program's stand beside both)
            Require(((IEnumerable)hooks["Stop"]).Cast<object>().Count() == 2 && ((IEnumerable)hooks["PreToolUse"]).Cast<object>().Count() == 2 && hooks.Count == 6, "the person's own hooks stay, this program's stand beside them: " + String.Join(",", hooks.Keys));
            Require(Reach.EventsOf("claude").Contains(Reach.Step) && !Reach.EventsOf("codex").Contains(Reach.Step) && Convert.ToInt32(entry0(hooks, Reach.Step, program)["timeout"]) == 10 && Convert.ToString(entry0(hooks, Reach.Step, program)["statusMessage"]) == "Remote AI", "Claude Code is heard before each step, briefly; Codex is not");
            var mine = (Dictionary<string, object>)((IEnumerable)((Dictionary<string, object>)((IEnumerable)hooks["PermissionRequest"]).Cast<object>().Single())["hooks"]).Cast<object>().Single();
            Require(Convert.ToString(mine["command"]) == program && String.Join(" ", ((IEnumerable)mine["args"]).Cast<object>()) == "--hook claude RemoteCli.reach.abc123" && Convert.ToInt32(mine["timeout"]) == Reach.Patience, "Claude Code is given the program and its arguments apart");
            // the one hook that can run for minutes says on Claude Code's screen what it is doing; the others say nothing
            Func<string, Dictionary<string, object>> entry = name => ((IEnumerable)hooks[name]).Cast<object>().Select(group => (Dictionary<string, object>)((IEnumerable)((Dictionary<string, object>)group)["hooks"]).Cast<object>().First()).Single(hook => Convert.ToString(hook["command"]) == program);
            Require(Convert.ToString(entry("Stop")["statusMessage"]) == Reach.Waiting && Reach.Events.Where(name => name != "Stop").All(name => !entry(name).ContainsKey("statusMessage")), "the hook at a stop says what it waits for");
            Require(File.ReadAllText(settings, Encoding.UTF8).Contains(Reach.Waiting), "in the words as they are, not as escapes");
            Require(File.ReadAllText(settings + ".before-remotecli", Encoding.UTF8) == before, "the file as it was is kept beside it");
            ReachHooks.Set("claude", false, program, "RemoteCli.reach.abc123");
            read = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(settings, Encoding.UTF8));
            hooks = (Dictionary<string, object>)read["hooks"];
            Require(!ReachHooks.Installed("claude") && hooks.Count == 2 && ((IEnumerable)hooks["Stop"]).Cast<object>().Count() == 1 && ((IEnumerable)hooks["PreToolUse"]).Cast<object>().Count() == 1 && File.ReadAllText(settings, Encoding.UTF8).Contains("echo mine") && !File.ReadAllText(settings, Encoding.UTF8).Contains("RemoteCli.reach"), "taken out again, the person's own untouched");
            // Codex: a file that is not there yet is made; the hook is one line for a shell
            ReachHooks.Set("codex", true, program, "RemoteCli.reach.abc123");
            Require(ReachHooks.Installed("codex") && !File.Exists(codexHooks + ".before-remotecli"), "installed for Codex, with nothing to keep from before");
            var line = Convert.ToString(((Dictionary<string, object>)((IEnumerable)((Dictionary<string, object>)((IEnumerable)((Dictionary<string, object>)new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(codexHooks, Encoding.UTF8))["hooks"])["Stop"]).Cast<object>().Single())["hooks"]).Cast<object>().Single())["command"]);
            Require(line == "& '" + program + "' --hook codex RemoteCli.reach.abc123", "a path with a space in it is called the way PowerShell calls one: " + line);
            Require(!File.ReadAllText(codexHooks, Encoding.UTF8).Contains("statusMessage"), "Codex's hooks are as they were: it asks anew whether a changed hook is trusted");
            ReachHooks.Set("codex", true, @"D:\Tools\RemoteCliAgent.exe", "RemoteCli.reach.abc123");
            Require(File.ReadAllText(codexHooks, Encoding.UTF8).Contains("\"D:\\\\Tools\\\\RemoteCliAgent.exe --hook codex RemoteCli.reach.abc123\"") && ReachHooks.Installed("codex", @"D:\Tools\RemoteCliAgent.exe", "RemoteCli.reach.abc123"), "one without is a command as it stands");
            ReachHooks.Set("codex", false, program, "RemoteCli.reach.abc123");
            Require(!ReachHooks.Installed("codex") && File.ReadAllText(codexHooks, Encoding.UTF8).Trim() == "{}", "and taken out");
            // a file that cannot be read is left alone
            File.WriteAllText(settings, "{ this is not json", new UTF8Encoding(false));
            Require(Refused(() => ReachHooks.Set("claude", true, program, "RemoteCli.reach.abc123")).Contains("无法读懂") && File.ReadAllText(settings) == "{ this is not json" && !ReachHooks.Installed("claude"), "a settings file that cannot be read is not written over");
        } finally { try { Directory.Delete(folder, true); } catch { } }
        Console.WriteLine("conversations told of by the tools' hooks: asked, answered, told what to do next, and their settings");
        return 0;
    }
}
