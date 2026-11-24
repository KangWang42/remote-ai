using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RemoteCli;

internal static class AntigravityCheck {
    static void Check(bool ok, string message) {
        if (!ok) throw new Exception(message);
    }
    static void Main(string[] args) {
        Check(TerminalAgent.Arguments("agy", "", false, "read") == "", "agy keeps user default even in read mode");
        Check(TerminalAgent.Arguments("agy", "", false, "edit") == "", "agy keeps user default even in edit mode");
        Check(TerminalAgent.Arguments("agy", "", false, "full") == "", "default mode");
        string sampleId = Guid.NewGuid().ToString();
        Check(TerminalAgent.Arguments("agy", sampleId, false, "read") == " --conversation " + sampleId, "agy resumes by --conversation without mode override");
        Check(TerminalAgent.Arguments("agy", sampleId, false, "edit") == " --conversation " + sampleId, "agy resume ignores mode override");
        Check(TerminalAgent.Arguments("agy", "", false, "full", false, false, false, "gemini-4-neon", "high") == " --model gemini-4-neon --effort high", "agy terminal passes model and effort");
        Check(TerminalAgent.Arguments("claude", "", false, "edit", false, false, false, "claude-opus-5-5", "high") == " --permission-mode acceptEdits --model claude-opus-5-5 --effort high", "claude terminal passes mode, model and effort");
        Check(TerminalAgent.Arguments("codex", "", false, "read", false, false, false, "gpt-6.2-astra", "xhigh") == " -s read-only -m gpt-6.2-astra -c model_reasoning_effort=xhigh", "codex terminal passes mode, model and effort");
        Check(TerminalAgent.ClampMode("", "edit") == "edit" && TerminalAgent.ClampMode("read", "full") == "read" && TerminalAgent.ClampMode("full", "read") == "read" && TerminalAgent.ClampMode("full", "edit") == "edit", "ClampMode respects computer ceiling");
        try {
            TerminalAgent.Arguments("agy", "", true, "read");
            throw new Exception("unexpected history selector");
        } catch (ArgumentException) {}
        try {
            TerminalAgent.Arguments("agy", sampleId, false, "read", true);
            throw new Exception("unexpected fork");
        } catch (ArgumentException) {}

        // Workspace URI parsing (plain drive letter, percent-encoded colon, UTF-8 Chinese path, invalid values).
        Check(Antigravity.WorkspacePath("[\"file:///E:/01_%E7%BD%91%E7%AB%99/10-remote-cli\"]") == @"E:\01_网站\10-remote-cli", "UTF-8 workspace URI");
        Check(Antigravity.WorkspacePath("[\"file:///e%3A/99%20Others/13%20%E5%A4%8D%E4%B9%A0\"]") == @"e:\99 Others\13 复习", "encoded colon workspace URI");
        Check(Antigravity.WorkspacePath("") == "", "empty workspace URI");
        Check(Antigravity.WorkspacePath("not-json") == "", "invalid JSON workspace URI");

        // Status & user request extraction.
        Check(Antigravity.MapStatus("CASCADE_RUN_STATUS_RUNNING", false) == "busy", "running -> busy");
        Check(Antigravity.MapStatus("CASCADE_RUN_STATUS_IDLE", true) == "busy", "not_fully_idle -> busy");
        Check(Antigravity.MapStatus("CASCADE_RUN_STATUS_WAITING_FOR_USER", false) == "confirm", "waiting -> confirm");
        Check(Antigravity.MapStatus("CASCADE_RUN_STATUS_IDLE", false) == "idle", "idle -> idle");
        Check(Antigravity.ExtractUserRequest("<USER_REQUEST>\n测试消息\n</USER_REQUEST>\n<ADDITIONAL_METADATA>\nx\n</ADDITIONAL_METADATA>") == "测试消息", "extract USER_REQUEST");

        // Fixture home: presence lock detection, annotation title (with octal UTF-8), history workspace fallback, and transcript reading.
        string fixture = Path.Combine(Path.GetTempPath(), "rcli-agy-fixture-" + Guid.NewGuid().ToString("N"));
        try {
            string id = Guid.NewGuid().ToString();
            string workspace = Path.Combine(fixture, "demo-project");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(fixture, "presence"));
            Directory.CreateDirectory(Path.Combine(fixture, "annotations"));
            string logsDir = Path.Combine(fixture, "brain", id, ".system_generated", "logs");
            Directory.CreateDirectory(logsDir);

            string lockFile = Path.Combine(fixture, "presence", id + ".lock");
            File.WriteAllText(lockFile, "");
            Check(AgySessions.Held(lockFile) == false, "unlocked presence file should report not held");
            using (var held = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                Check(AgySessions.Held(lockFile) == true, "exclusively locked presence file should report held");
            }
            Check(AgySessions.Held(lockFile) == false, "released presence file should report not held");

            // Protobuf text format with UTF-8 octal escape for "测试".
            File.WriteAllText(Path.Combine(fixture, "annotations", id + ".pbtxt"), "title:\"\\346\\265\\213\\350\\257\\225 Agy\"\n", new UTF8Encoding(false));
            Check(Antigravity.AnnotationTitle(fixture, id) == "测试 Agy", "annotation title unescape");

            File.WriteAllText(Path.Combine(fixture, "history.jsonl"),
                "{\"display\":\"hi\",\"timestamp\":1700000000000,\"workspace\":\"" + workspace.Replace("\\", "\\\\") + "\",\"conversationId\":\"" + id + "\"}\n",
                new UTF8Encoding(false));
            string transcript = Path.Combine(logsDir, "transcript.jsonl");
            File.WriteAllText(transcript,
                "{\"step_index\":0,\"type\":\"USER_INPUT\",\"created_at\":\"2026-11-19T10:00:00Z\",\"content\":\"<USER_REQUEST>\\n你好 agy\\n</USER_REQUEST>\"}\n"
                + "{\"step_index\":1,\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"2026-11-19T10:00:01Z\",\"thinking\":\"先检查文件\",\"tool_calls\":[{\"name\":\"read_file\",\"args\":{\"Path\":\"README.md\"}}],\"content\":\"已完成检查\"}\n",
                new UTF8Encoding(false));

            var summaries = Antigravity.Summaries(fixture, new[] { id });
            Check(summaries.Count == 1 && summaries[0].Id == id, "summary fallback from live lock");
            Check(summaries[0].Title == "测试 Agy", "summary title from annotation: " + summaries[0].Title);
            Check(String.Equals(summaries[0].Folder, workspace, StringComparison.OrdinalIgnoreCase), "summary folder from history: " + summaries[0].Folder);

            var said = TerminalAgent.Said(transcript, "agy");
            var messages = (List<Dictionary<string, object>>)said["messages"];
            Check(messages.Count == 4, "expected user + thought + tool + assistant, got " + messages.Count);
            Check((string)messages[0]["role"] == "user" && (string)messages[0]["text"] == "你好 agy", "agy user message");
            Check((string)messages[1]["role"] == "thought" && (string)messages[1]["text"] == "先检查文件", "agy thought");
            Check((string)messages[2]["role"] == "tool" && ((string)messages[2]["text"]).Contains("README.md"), "agy tool call");
            Check((string)messages[3]["role"] == "assistant" && (string)messages[3]["text"] == "已完成检查", "agy assistant reply");
        } finally {
            try { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); } catch { }
        }

        // Verify Ways for agy live sessions gives clear agy-specific messages and allows process end.
        var liveAgy = new TerminalAgent.SessionInfo { Id = sampleId, Tool = "agy", Dir = "demo", Title = "demo", Live = true, Host = "cli", Origin = "Positron", Status = "busy", CanTakeover = true, OwnershipKnown = true };
        var ways = Ways.Of(liveAgy, null, null);
        Check(ways.End == Ways.Process, "live agy CLI session can be ended");
        Check(ways.Why["say"].Contains("Antigravity CLI"), "agy say reason mentions Antigravity CLI");
        Check(ways.Why["stop"].Contains("Antigravity CLI"), "agy stop reason mentions Antigravity CLI");

        if (args.Length == 0) return;
        string launcher = TerminalAgent.FindTool("agy");
        Check(launcher != null, "install agy to run the optional live check");
        var output = new ManualResetEventSlim();
        using (var terminal = new PseudoTerminal(TerminalAgent.Command("agy", launcher, "", false, "read"),
                   args[0], 80, 24, text => { if (text.Length > 0) output.Set(); })) {
            Check(output.Wait(15000) && !terminal.Closed, "expected live CLI output");
            terminal.Resize(72, 24);
            terminal.Write("\x1b"); // Escape in this test terminal only.
            using (var proc = System.Diagnostics.Process.GetProcessById(terminal.Id)) {
                long start = new DateTimeOffset(proc.StartTime).ToUnixTimeMilliseconds();
                Check(Antigravity.ExternalStarts(launcher, new int[0]).Contains(start), "running agy missing");
                Check(!Antigravity.ExternalStarts(launcher, new[] {terminal.Id}).Contains(start), "owned agy counted twice");
            }
        }
        Console.WriteLine("Live agy: ConPTY output, resize, Escape and process ownership passed.");
    }
}
