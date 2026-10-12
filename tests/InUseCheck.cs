using System;
using System.IO;
using RemoteCli;

/// Which of the conversations a program holds are in use now, and what a newer version that was found leads to.
public static class InUseCheck {
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static long Ms(DateTime at) { return (long)(at - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    public static int Main() {
        DateTime now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        long hoursAgo = Ms(now.AddHours(-3)), justNow = Ms(now.AddMinutes(-TerminalAgent.RecentMinutes + 1)), longer = Ms(now.AddMinutes(-TerminalAgent.RecentMinutes - 1));
        Func<string, Reach.Open> told = status => new Reach.Open { Status = status };
        // a command line is open for as long as its program runs, however long nothing was said in it
        Require(TerminalAgent.InUse("cli", "命令行", "idle", null, hoursAgo, now), "a window of its own");
        Require(TerminalAgent.InUse("cli", "VS Code", "", null, hoursAgo, now), "a terminal of an editor");
        // a desktop app keeps what it has loaded held by a process: that alone is no use
        Require(!TerminalAgent.InUse("cli", "Claude 桌面版", "idle", null, hoursAgo, now), "held by the desktop app, idle for hours");
        Require(!TerminalAgent.InUse("shared", "Codex 桌面版", "", null, hoursAgo, now) && !TerminalAgent.InUse("unknown", "", "", null, hoursAgo, now) && !TerminalAgent.InUse("shared", "Codex 后台服务", "", null, hoursAgo, now), "held by an app or a service");
        // at work, asking or kept waiting for the phone, as the tool's own note or its hooks say
        Require(TerminalAgent.InUse("cli", "Claude 桌面版", "busy", null, hoursAgo, now), "at work by its own note");
        foreach (string status in new[] { "busy", "confirm", "waiting" }) Require(TerminalAgent.InUse("shared", "Codex 桌面版", "", told(status), hoursAgo, now), "told: " + status);
        // what its hooks say counts before its own note
        Require(!TerminalAgent.InUse("cli", "Claude 桌面版", "busy", told("idle"), hoursAgo, now), "its hooks say it has stopped");
        // used a moment ago
        Require(TerminalAgent.InUse("cli", "Claude 桌面版", "idle", told("idle"), justNow, now) && !TerminalAgent.InUse("cli", "Claude 桌面版", "idle", told("idle"), longer, now), "used in the last minutes, and no longer");

        // a newer version: the phone's word installs, a press in the window asks on the spot whatever the automatic
        // setting, the program's own find waits for a quiet moment or only tells
        Require(AutoUpdater.Step(false, true, false) == "install" && AutoUpdater.Step(true, true, true) == "install", "the phone asked");
        Require(AutoUpdater.Step(true, false, true) == "ask" && AutoUpdater.Step(true, false, false) == "ask", "a press in the window is answered at once, with automatic updates on too");
        Require(AutoUpdater.Step(false, false, true) == "tell" && AutoUpdater.Step(false, false, false) == "none", "by itself it looks and tells only where that is switched on, and installs nothing");
        Require(AutoUpdater.Newer("v1.5.3", "1.5.2") && !AutoUpdater.Newer("v1.5.2", "1.5.2") && !AutoUpdater.Newer("1.5.1", "1.5.2") && AutoUpdater.Newer("1.10.0", "1.9.9") && !AutoUpdater.Newer("latest", "1.0.0"), "which version is newer");
        // a message for a conversation that sits idle: what the session that hands it over is given, and what its answer means
        string address = "uds:\\\\.\\pipe\\LOCAL\\cc-msg-0123", prompt = ClaudeRelay.Prompt(address, ClaudeRelay.Framed("把结论再写短一点\n分两段"));
        Require(prompt.Contains("to:\n" + address + "\n") && prompt.Contains("-----BEGIN-----\n" + ClaudeRelay.Framed("把结论再写短一点\n分两段") + "\n-----END-----"), "the address and the words, whole: " + prompt);
        // ---- by what a folder with conversations is used: said by the conversations' own files, or by the program that
        // holds one; told with each kind once, the terminal last; a desktop app that is closed is left out where asked
        string app = Folders.CodexApp, desk = Folders.ClaudeApp, term = Folders.Terminal, editor = Folders.Editor;
        Require(Folders.OfCodex("cli", "codex-tui") == term && Folders.OfCodex("exec", "codex_exec") == term && Folders.OfCodex("", "") == term, "Codex in a terminal");
        Require(Folders.OfCodex("vscode", "Codex Desktop") == app && Folders.OfCodex("vscode", "codex-tui") == app && Folders.OfCodex("", "codex_work_desktop") == app, "Codex's app says vscode of itself");
        Require(Folders.OfCodex("vscode", "codex_vscode") == editor, "Codex in an editor");
        Require(Folders.OfClaude("cli") == term && Folders.OfClaude("") == term && Folders.OfClaude("claude-desktop-3p") == desk && Folders.OfClaude("claude-vscode") == editor, "Claude Code");
        Require(String.Join(",", Folders.Told(new[] { term, app, term, desk })) == app + "," + desk + "," + term, "each once, the terminal last");
        Require(String.Join(",", Folders.Told(new[] { term, app, desk }, false, true)) == desk + "," + term, "not the app that is closed");
        Require(Folders.Told(new[] { app }, false, true).Length == 0 && Folders.Told(new[] { desk }, true, false).Length == 0 && Folders.Told(new[] { editor }, false, false).Length == 1, "a folder only a closed app uses is of no kind");
        Require(Folders.Held(app, term) == app && Folders.Held(desk, term) == desk && Folders.Held(SessionOrigin.CommandLine, app) == term, "by its holder");
        Require(Folders.Held("", app) == app && Folders.Held("Codex 后台服务", app) == app, "by its file, where the holder tells nothing");
        // under an editor: the editor's, unless it was started as a command line (that runs in the editor's terminal)
        Require(Folders.Held("VS Code", editor) == editor && Folders.Held("Positron", app) == editor && Folders.Held("Positron", term) == term && Folders.Held("VS Code", term) == term, "a terminal inside an editor is a terminal");
        // one look fills it; what it knows is that of the look that ended last
        var folders = new Folders();
        string here = Path.GetTempPath().TrimEnd('\\'), gone = Path.Combine(here, "no-such-folder-" + Guid.NewGuid().ToString("N"));
        folders.Begin();
        folders.Note(here + "\\", "codex", new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc), false, term);
        folders.Note(here, "claude", new DateTime(2026, 1, 1, 0, 0, 9, DateTimeKind.Utc), true, desk);
        folders.Note(here, "codex", new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc), false, term);
        folders.Note(gone, "codex", DateTime.UtcNow, false, term);
        Require(folders.Of(here).Tools.Count == 0, "nothing is known while the look goes on");
        folders.End();
        var use = folders.Of(here + "\\");
        Require(String.Join(" ", use.Tools) == "codex claude" && String.Join(",", Folders.Told(use.Kinds)) == desk + "," + term && use.Live && use.Updated == 1767225609000L, "what is known of a folder");
        Require(folders.Of("C:\\nowhere").Kinds.Count == 0, "of one without conversations, nothing");
        var offered = folders.Offered(path => true, true, 10);
        Require(offered.Count == 1 && (string)offered[0]["path"] == here && (string)offered[0]["tools"] == "codex claude" && ((string[])offered[0]["kinds"]).Length == 2, "the folder that is gone is not offered");
        Require(folders.Offered(path => false, true, 10).Count == 0 && folders.Offered(path => true, true, 0).Count == 0, "only what passes, and no more than asked for");
        // what only wakes a conversation carries none of the user's words: those are given to it by its own hook
        Require(ClaudeRelay.Wake.StartsWith("Remote CLI wake-up") && ClaudeRelay.Wake.Contains("Stop hook") && ClaudeRelay.Prompt(address, ClaudeRelay.Wake).Contains("-----BEGIN-----\n" + ClaudeRelay.Wake + "\n-----END-----"), "the wake-up");
        Require(ClaudeRelay.Framed("x").Contains("from their phone") && ClaudeRelay.Framed("x").EndsWith("\n\nx"), "it says whose words they are");
        string[] arguments = ClaudeRelay.Arguments("C:\\data\\relay-settings.json");
        Require(arguments[0] == "-p" && String.Join(" ", arguments).Contains("--tools SendMessage --allowedTools SendMessage --strict-mcp-config") && Array.IndexOf(arguments, "--no-session-persistence") > 0
            && arguments[Array.IndexOf(arguments, "--settings") + 1] == "C:\\data\\relay-settings.json", "one tool, no conversation kept, its own settings: " + String.Join(" ", arguments));
        ClaudeRelay.Taken("{\"type\":\"result\",\"result\":\"SENT\"}");
        Func<string, string> refused = said => { try { ClaudeRelay.Taken(said); return ""; } catch (InvalidOperationException error) { return error.Message; } };
        Require(refused("{\"result\":\"FAILED: no session at that address\"}").Contains("no session at that address") && refused("not json").Contains("没有把消息送出") && refused("{\"result\":\"\"}").Length > 0 && refused(null).Length > 0, "what was not taken says why");
        Require(ClaudeRelay.Address("11111111-2222-3333-4444-555555555555") == "", "a conversation nothing has open has no address");
        // a message for a Codex conversation is given to the program itself, whole, whatever signs it holds
        Require(CodexQueue.Quoted("say \"hi\" & echo %PATH%\nnext") == "\"say \\\"hi\\\" & echo %PATH%\nnext\"" && CodexQueue.Quoted("C:\\dir\\") == "\"C:\\dir\\\\\"" && CodexQueue.Quoted("a\\\"b") == "\"a\\\\\\\"b\"", "quoted as a program reads it: " + CodexQueue.Quoted("a\\\"b"));
        Require(CodexQueue.Program(null) == null && CodexQueue.Program("C:\\tools\\codex.exe") == "C:\\tools\\codex.exe" && CodexQueue.Program("C:\\no-such-folder\\codex.cmd") == null && !CodexQueue.Supported("C:\\no-such-folder\\codex.cmd"), "a script's launcher is not given the message; the program beside it is");
        Console.WriteLine("in use now: a window of its own, at work or asking, or used a moment ago; a press on the update button is always answered");
        return 0;
    }
}
