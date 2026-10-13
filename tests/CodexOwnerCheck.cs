using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
using RemoteCli;

public static class CodexOwnerCheck {
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static Process Fixture(string mode, string id) {
        var process = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
            mode + " " + id) { UseShellExecute = false, CreateNoWindow = true });
        for (int n = 0; n < 100; n++) {
            if (File.Exists(Path.Combine(CodexSessions.Home, id + ".ready"))) return process;
            if (process.HasExited) throw new Exception("Fixture exited");
            Thread.Sleep(50);
        }
        process.Kill(); throw new Exception("Fixture not ready");
    }
    public static int Main(string[] args) {
        if (args.Length == 2) {
            if (args[0] == "remote-host" || args[0] == "editor-host" || args[0] == "editor-server" || args[0] == "claude-remote-host" || args[0] == "editor-claude") {
                bool claude = args[0] == "claude-remote-host" || args[0] == "editor-claude";
                using (var writer = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), claude ? "claude.exe" : "codex.exe"), (claude ? "claude-hold " : args[0] == "editor-server" ? "app-server " : "hold ") + args[1]) { UseShellExecute = false, CreateNoWindow = true })) writer.WaitForExit();
                return 0;
            }
            if (args[0] == "claude-hold") {
                var record = new { pid = Process.GetCurrentProcess().Id, procStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc().ToString(), sessionId = args[1], cwd = CodexSessions.Home };
                File.WriteAllText(Path.Combine(CodexSessions.Home, args[1] + ".claude.json"), new JavaScriptSerializer().Serialize(record));
                File.WriteAllText(Path.Combine(CodexSessions.Home, args[1] + ".ready"), "ready");
                for (;;) Thread.Sleep(500);
            }
            string path = CodexSessions.LockPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) {
                stream.Lock(0, Int64.MaxValue);
                File.WriteAllText(Path.Combine(CodexSessions.Home, args[1] + ".ready"), "ready");
                for (;;) Thread.Sleep(500);
            }
        }
        var children = new List<Process>();
        string root = Path.Combine(Path.GetTempPath(), "remote-cli-owner-" + Guid.NewGuid().ToString("N"));
        string previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", root);
        try {
            Directory.CreateDirectory(Path.Combine(root, "thread-writer-locks"));
            string stale = Guid.NewGuid().ToString(); File.WriteAllText(CodexSessions.LockPath(stale), "");
            Require(TerminalAgent.Command("codex", @"C:\tools\codex.exe", stale, false, "read", true).Contains(" fork " + stale + " -s read-only"), "Fork command lost source or permission mode");
            Require(TerminalAgent.Command("codex", @"C:\tools\codex.cmd", stale, false, "edit").Contains(" resume " + stale + " -s workspace-write"), "Resume wrapper lost permission mode");
            string fresh = TerminalAgent.Command("claude", @"C:\tools\claude.cmd", stale, false, "edit", false, false, true);
            Require(fresh.Contains(" --session-id " + stale) && !fresh.Contains("--resume"), "New Claude session became a resume picker");
            Require(TerminalAgent.Command("codex", @"C:\tools\codex.cmd", stale, false, "read", false, true).Contains(" resume " + stale + " --no-daemon -s read-only"), "Phone Codex resume did not isolate its backend");
            var localClaude = TerminalAgent.ComputerProcess(new TerminalAgent.ComputerTakeover { Tool = "claude", Dir = root, Session = stale, Launcher = @"C:\tools\claude.cmd" }, "full");
            Require(localClaude.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) && localClaude.Arguments.Contains("--resume " + stale) && localClaude.WorkingDirectory == root, "Computer Claude takeover command was not resumable");
            var localCodex = TerminalAgent.ComputerProcess(new TerminalAgent.ComputerTakeover { Tool = "codex", Dir = root, Session = stale, Launcher = @"C:\tools\codex.exe" }, "read");
            Require(localCodex.FileName.EndsWith("codex.exe", StringComparison.OrdinalIgnoreCase) && localCodex.Arguments.Contains("resume " + stale) && localCodex.Arguments.Contains("--no-daemon"), "Computer Codex takeover did not isolate its CLI");
            var idle = CodexSessions.Inspect(stale);
            Require(idle.Known && !idle.Live && !idle.CanTakeover, "Stale lock was considered live");
            string id = Guid.NewGuid().ToString(); var cli = Fixture("hold", id); children.Add(cli);
            var live = CodexSessions.Inspect(id);
            Require(live.Known && live.Live && live.Pid == cli.Id && live.CanTakeover && live.Host == "cli", "Independent writer ownership failed: pid=" + live.Pid + " host=" + live.Host + " reason=" + live.Reason);
            var parents = new Dictionary<int, int> { { cli.Id, Process.GetCurrentProcess().Id } };
            bool refused = false;
            try { CodexSessions.Takeover(id, new[] { Process.GetCurrentProcess().Id }, parents); } catch (InvalidOperationException) { refused = true; }
            Require(refused && !cli.HasExited, "Protected phone descendant was terminated");
            CodexSessions.Takeover(id, new int[0], parents);
            Require(cli.WaitForExit(3000) && !CodexSessions.Inspect(id).Live, "Takeover did not release the exact writer");
            string sharedId = Guid.NewGuid().ToString(); var shared = Fixture("app-server", sharedId); children.Add(shared);
            var backend = CodexSessions.Inspect(sharedId);
            Require(backend.Live && backend.Pid == shared.Id && !backend.CanTakeover && backend.Host == "shared", "Shared backend was offered for termination or classified as a window");
            refused = false;
            try { CodexSessions.Takeover(sharedId, new int[0], new Dictionary<int, int>()); } catch (InvalidOperationException) { refused = true; }
            Require(refused && !shared.HasExited, "Shared backend was terminated");
            string remoteId = Guid.NewGuid().ToString();
            var host = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "RemoteCliAgent.exe"), "remote-host " + remoteId) { UseShellExecute = false, CreateNoWindow = true }); children.Add(host);
            for (int n = 0; n < 100 && !File.Exists(Path.Combine(CodexSessions.Home, remoteId + ".ready")); n++) Thread.Sleep(50);
            var remote = CodexSessions.Inspect(remoteId); children.Add(Process.GetProcessById(remote.Pid));
            Require(remote.Live && remote.Host == "remote" && !remote.CanTakeover, "Other remote terminal was classified as a computer CLI");
            refused = false;
            try { CodexSessions.Takeover(remoteId, new int[0], new Dictionary<int, int>()); } catch (InvalidOperationException) { refused = true; }
            Require(refused && !host.HasExited, "Other remote terminal was terminated");
            foreach (var hostName in new[] { new[] { "Positron", "Positron" }, new[] { "Code", "VS Code" }, new[] { "rstudio", "RStudio" } }) {
                foreach (string mode in new[] { "editor-host", "editor-server", "editor-claude" }) {
                    string editorId = Guid.NewGuid().ToString();
                    var editor = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), hostName[0] + ".exe"), mode + " " + editorId) { UseShellExecute = false, CreateNoWindow = true }); children.Add(editor);
                    for (int n = 0; n < 100 && !File.Exists(Path.Combine(root, editorId + ".ready")); n++) Thread.Sleep(50);
                    if (mode == "editor-claude") {
                        var inEditor = ClaudeSessions.Inspect(Path.Combine(root, editorId + ".claude.json"));
                        children.Add(Process.GetProcessById(inEditor.Pid));
                        Require(inEditor.Origin == hostName[1] && inEditor.CanTakeover, hostName[1] + " Claude terminal source");
                    } else {
                        var inEditor = CodexSessions.Inspect(editorId); children.Add(Process.GetProcessById(inEditor.Pid));
                        bool sharedEditor = mode == "editor-server";
                        Require(inEditor.Host == (sharedEditor ? "shared" : "cli") && inEditor.Origin == hostName[1] && inEditor.CanTakeover == !sharedEditor, hostName[1] + " source and writer ownership stay independent");
                        if (!sharedEditor) CodexSessions.Takeover(editorId, new int[0], new Dictionary<int,int>());
                    }
                }
            }
            string claudeId = Guid.NewGuid().ToString();
            var claudeWriter = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "claude.exe"), "claude-hold " + claudeId) { UseShellExecute = false, CreateNoWindow = true }); children.Add(claudeWriter);
            for (int n = 0; n < 100 && !File.Exists(Path.Combine(root, claudeId + ".ready")); n++) Thread.Sleep(50);
            string recordFile = Path.Combine(root, claudeId + ".claude.json");
            var claudeOwner = ClaudeSessions.Inspect(recordFile);
            Require(claudeOwner.Live && claudeOwner.CanTakeover && claudeOwner.Pid == claudeWriter.Id, "Claude exact fingerprint was not verified");
            File.WriteAllText(recordFile, new JavaScriptSerializer().Serialize(new { pid = claudeWriter.Id, procStart = "1", sessionId = claudeId }));
            Require(!ClaudeSessions.Inspect(recordFile).Live, "Stale Claude process fingerprint reused a PID");
            Console.WriteLine("Native checks passed: exact takeover, shared/foreign host protection, Positron source, fresh Claude command and fingerprint, isolated phone Codex.");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 1;
        } finally {
            foreach (var child in children) { try { if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); } } finally { child.Dispose(); } }
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
