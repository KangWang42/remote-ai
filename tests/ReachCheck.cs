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
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null); idle = 0;

                // ---- words left for a conversation that sits idle, also one that has told of nothing: its Stop hook gives
                // them to it when it next stops, whoever is at the computer; what nothing comes for is taken back
                string asleep = Guid.NewGuid().ToString();
                reach.Leave("claude", asleep, "把结论再写短一点");
                Require(claude(asleep) != null && claude(asleep).Status == "idle", "it is known as one that sits idle");
                bool? came = null;
                var waits = Task.Run(() => { came = reach.Taken("claude", asleep, 20); });
                var woke = Run("claude", Event("Stop", asleep, ",\"last_assistant_message\":\"…\""));
                string given = Answer(woke, 5000, "the words left are given at the stop");
                Require(given.Contains("\"decision\":\"block\"") && given.Contains(Reach.FromPhone) && given.Contains("把结论再写短一点"), "given as the user's words: " + given);
                Require(waits.Wait(3000) && came == true, "and whoever left them is told at once that they were taken");
                reach.Leave("claude", asleep, "没有人来取");
                var began = DateTime.UtcNow;
                Require(!reach.Taken("claude", asleep, 1) && (DateTime.UtcNow - began).TotalSeconds < 3, "what nothing comes for is taken back");
                Require(Answer(Run("claude", Event("Stop", asleep)), 5000, "nothing lies there any more") == "", "and is not given later");
                Answer(Run("claude", Event("SessionEnd", asleep)), 5000, "ended");

                // ---- a conversation that ends is forgotten
                Answer(Run("claude", Event("SessionEnd", one)), 5000, "ended");
                Require(claude(one) == null && codex(two) != null, "only the one that ended");
                Require(told >= 12, "what changes is told at once: " + told);
                Require(reach.Heard("claude") && reach.Heard("codex") && !reach.Heard("other"), "a tool that has told of something is known to reach this program");
            }
            Require(Answer(Run("claude", Event("Stop", Guid.NewGuid().ToString())), 5000, "nobody listens") == "", "with nothing listening the hook ends at once and says nothing");

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
            Require(((IEnumerable)hooks["Stop"]).Cast<object>().Count() == 2 && ((IEnumerable)hooks["PreToolUse"]).Cast<object>().Count() == 1 && hooks.Count == 6, "the person's own hooks stay, this program's stand beside them: " + String.Join(",", hooks.Keys));
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
            Require(!ReachHooks.Installed("claude") && hooks.Count == 2 && ((IEnumerable)hooks["Stop"]).Cast<object>().Count() == 1 && File.ReadAllText(settings, Encoding.UTF8).Contains("echo mine") && !File.ReadAllText(settings, Encoding.UTF8).Contains("RemoteCli.reach"), "taken out again, the person's own untouched");
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
