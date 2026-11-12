using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using RemoteCli;

public static class ClaudeOwnersCheck {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    // Only the terminal transport is substituted; ownership uses real Windows processes and fingerprints.
    sealed class Transport : IRunning {
        readonly Process process;
        public Transport(Process process) { this.process = process; }
        public int Id { get { return process.Id; } }
        public bool Closed { get { return process.HasExited; } }
        public bool Drained { get { return Closed; } }
        public int? ExitCode { get { return Closed ? (int?)process.ExitCode : null; } }
        public int Cols { get { return 80; } }
        public int Rows { get { return 24; } }
        public void Write(string text) { throw new NotSupportedException(); }
        public void Resize(int cols, int rows) { }
        public void Dispose() { }
    }
    static Process Start(string exe, string args) {
        return Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exe), args) { UseShellExecute = false, CreateNoWindow = true });
    }
    public static int Main(string[] args) {
        string home = ClaudeSessions.Home, session = "11111111-2222-4333-8444-555555555555";
        if (args.Length > 0) {
            if (args[0] == "host") { using (var child = Start("claude.exe", "hold busy")) child.WaitForExit(); return 0; }
            using (var own = Process.GetCurrentProcess()) {
                File.WriteAllText(Path.Combine(home, "sessions", own.Id + ".json"), new JavaScriptSerializer().Serialize(new {
                    pid = own.Id, procStart = own.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(), sessionId = session,
                    cwd = Path.Combine(home, "work"), status = args[1], entrypoint = "cli"
                }));
                File.WriteAllText(Path.Combine(home, args[1] + ".ready"), own.Id.ToString());
                Thread.Sleep(Timeout.Infinite);
            }
            return 0;
        }
        var children = new List<Process>();
        try {
            string work = Path.Combine(home, "work"), project = Path.Combine(home, "projects", TerminalAgent.ProjectFolder(work));
            Directory.CreateDirectory(Path.Combine(home, "sessions")); Directory.CreateDirectory(work); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, session + ".jsonl"), "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"Check this project\"}}\n");
            children.Add(Start("Positron.exe", "host")); children.Add(Start("claude.exe", "hold idle"));
            foreach (string state in new[] { "busy", "idle" }) {
                string ready = Path.Combine(home, state + ".ready");
                for (int n = 0; n < 100 && !File.Exists(ready); n++) Thread.Sleep(50);
                Require(File.Exists(ready), "fixture did not start: " + state);
            }
            var busyProcess = Process.GetProcessById(Int32.Parse(File.ReadAllText(Path.Combine(home, "busy.ready")))); children.Add(busyProcess);
            var idleProcess = children[1];
            var agent = new TerminalAgent(Path.Combine(home, "agent"));
            var dirs = new Dictionary<string, string> { { "demo", work } };
            string busyFile = Path.Combine(home, "sessions", busyProcess.Id + ".json"), idleFile = Path.Combine(home, "sessions", idleProcess.Id + ".json");
            var busy = ClaudeSessions.Inspect(busyFile); var idle = ClaudeSessions.Inspect(idleFile);
            Require(busy.Live && idle.Live, "real process fingerprints");
            // A test launched from Remote AI itself has a remote ancestor above the fixture host.
            var fixtureParents = new Dictionary<int, int> { { busyProcess.Id, children[0].Id } };
            Require(ClaudeSessions.Inspect(busyFile, fixtureParents).Origin == "Positron", "nearest editor in the fixture process tree");
            Require(ClaudeSessions.Preferred(new[] { idle, busy }).Pid == busy.Pid && ClaudeSessions.Preferred(new[] { busy, idle }).Pid == busy.Pid, "busy owner must survive both enumeration orders");
            var remote = new ClaudeSessions.Owner { Live = true, Host = "remote", Status = "busy", Pid = 1 };
            var localIdle = ClaudeSessions.Inspect(idleFile, new Dictionary<int, int>());
            Require(ClaudeSessions.Preferred(new[] { remote, localIdle }).Pid == idle.Pid, "remote copy must not hide an independent local owner");
            Require(ClaudeSessions.Preferred(new[] { new ClaudeSessions.Owner { Live = false, Status = "busy" }, idle }).Pid == idle.Pid, "stale owner excluded");

            var listed = agent.Scan(dirs).Single(s => s.Id == session);
            Require(listed.Origin == busy.Origin && listed.Status == "busy" && listed.MultipleOwners, "scan must keep the busy editor owner");
            Require(!listed.CanTakeover && Ways.Of(listed, null, null).End == "", "multiple owners cannot offer single-process takeover or end");
            bool refused = false;
            try { typeof(TerminalAgent).GetMethod("ClaudeExclusive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(agent, new object[] { session, true }); }
            catch (TargetInvocationException error) { refused = error.InnerException is InvalidOperationException && error.InnerException.Message == ClaudeSessions.MultipleReason; }
            Require(refused && !busyProcess.HasExited && !idleProcess.HasExited, "duplicate takeover must refuse before terminating any process");
            var idleHook = new Reach.Open { Status = "idle" };
            Require(Ways.Of(listed, idleHook, null).Status == "busy", "phone status must not be replaced by an idle copy's hook");
            Require(Ways.Of(listed, new Reach.Open { Status = "confirm" }, null).Status == "confirm", "permission request still takes priority");

            var phone = new TerminalAgent.LiveTerminal { Id = "phone", Tool = "claude", Dir = "demo", Session = session, Status = "idle", Pty = new Transport(idleProcess), Started = DateTime.UtcNow };
            var terminals = (Dictionary<string, TerminalAgent.LiveTerminal>)typeof(TerminalAgent).GetField("terminals", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(agent);
            terminals[phone.Id] = phone;
            agent.Scan(dirs);
            var rows = agent.Running();
            Require(rows.Count == 2 && rows.Any(r => r[5] == busy.Origin && r[2] == "busy") && rows.Any(r => r[6] == "phone" && r[2] == "idle"), "same conversation must show the independent editor and idle phone terminal");
            busyProcess.Kill(); busyProcess.WaitForExit(3000);
            agent.Scan(dirs);
            Require(agent.Running().Count == 1 && agent.Running()[0][6] == "phone", "phone-only conversation must not be listed twice");
            terminals.Remove(phone.Id);
            listed = agent.Scan(dirs).Single(s => s.Id == session);
            Require(!listed.MultipleOwners && Ways.Of(listed, idleHook, null).Status == "idle", "single-process behavior unchanged");
            Console.WriteLine("Claude owners passed: real Positron process, duplicate records, idle hooks, phone overlap, exit and single-owner regression.");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message + "\n" + error.StackTrace);
            return 1;
        } finally {
            foreach (var child in children.AsEnumerable().Reverse()) { if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); } child.Dispose(); }
        }
    }
}
