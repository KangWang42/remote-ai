using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using RemoteCli;

/// The phone's message and stop for a real Antigravity CLI (agy), through this program's own hook program and its
/// hooks as they are written into a workspace's .agents/hooks.json: what the model reads is tried on the real tool.
///     AgyReachLive.exe <RemoteCliAgent.exe> <agy.exe> <empty workspace>
/// Runs agy in its print mode twice, in the workspace only; it costs two short turns of the person's agy account.
public static class AgyReachLive {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }

    // agy in its print mode, its events read line by line; `seen` is told each line, as it comes
    static string Run(string agy, string workspace, string prompt, Action<string> seen) {
        var start = new ProcessStartInfo(agy, "-p \"" + prompt + "\" --output-format stream-json") { WorkingDirectory = workspace, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false) };
        using (var program = Process.Start(start)) {
            program.ErrorDataReceived += (s, e) => { };
            program.BeginErrorReadLine();
            string line, result = "";
            while ((line = program.StandardOutput.ReadLine()) != null) {
                seen(line);
                if (line.Contains("\"event\":\"result\"")) result = line;
            }
            Require(program.WaitForExit(240000), "agy did not end");
            return result;
        }
    }

    public static int Main(string[] args) {
        string agent = args[0], agy = args[1], workspace = args[2];
        string pipe = "RemoteCli.reach.live" + Guid.NewGuid().ToString("N").Substring(0, 8);
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));
        File.WriteAllText(Path.Combine(workspace, "notes.txt"), "alpha\nbeta\ngamma\n");
        // the hooks as ReachHooks writes them for agy, in this workspace only (its global file is left alone)
        string global = Path.Combine(workspace, "gemini", "config", "hooks.json");
        Environment.SetEnvironmentVariable("AGY_HOME", Path.Combine(workspace, "gemini", "antigravity-cli"));
        ReachHooks.Set("agy", true, agent, pipe);
        var all = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(global, Encoding.UTF8));
        File.WriteAllText(Path.Combine(workspace, ".agents", "hooks.json"), new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { ReachHooks.AgyName, all[ReachHooks.AgyName] } }), new UTF8Encoding(false));
        Environment.SetEnvironmentVariable("AGY_HOME", null);
        var id = new Regex("\"conversation_id\":\"([0-9a-f-]{36})\"");

        using (var reach = new Reach(pipe, () => 0, () => new Reach.Kept(), null)) {
            // ---- said from the phone while it works: taken into the turn that runs
            string session = null; bool said = false;
            string result = Run(agy, workspace, "Use a tool to read notes.txt in this folder, then reply with only its first line.", line => {
                var m = id.Match(line); if (session == null && m.Success) session = m.Groups[1].Value;
                if (!said && session != null && line.Contains("\"step_type\":\"tool\"")) {
                    var known = reach.Of("agy", session);
                    if (known != null && known.Status == "busy") { reach.Say("agy", session, "Also put the word ORANGE after that line in your reply."); said = true; }
                }
            });
            Console.WriteLine("said while it works: " + result);
            Require(said && result.Contains("ORANGE") && result.Contains("alpha"), "the phone's message was taken in the turn: " + result);

            // ---- stopped from the phone: the step is refused, and it ends after its model's call
            session = null; bool stopped = false; var began = DateTime.UtcNow;
            result = Run(agy, workspace, "Do these one at a time, each with its own tool call: read notes.txt, then list this folder, then read notes.txt again, then list the folder again. Then reply with the word FINISHED.", line => {
                var m = id.Match(line); if (session == null && m.Success) session = m.Groups[1].Value;
                if (!stopped && session != null && line.Contains("\"step_type\":\"tool\"")) {
                    var known = reach.Of("agy", session);
                    if (known != null && known.Stoppable) { reach.Stop("agy", session); stopped = true; }
                }
            });
            Console.WriteLine("stopped: " + result + " after " + (int)(DateTime.UtcNow - began).TotalSeconds + "s");
            Require(stopped && !result.Contains("FINISHED"), "told to stop, it did not go on to the end: " + result);
            Require(reach.Of("agy", session).Completion == "", "a turn that was cut is no turn finished");
        }
        Console.WriteLine("agy on the real tool: a message from the phone taken while it works, and a stop");
        return 0;
    }
}
