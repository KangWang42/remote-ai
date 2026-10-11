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
        try {
            using (var reach = new Reach(pipe, () => idle)) {
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
