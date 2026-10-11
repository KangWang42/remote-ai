using System;
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
        Require(AutoUpdater.Step(false, false, true) == "wait" && AutoUpdater.Step(false, false, false) == "tell", "found by itself");
        Require(AutoUpdater.Newer("v1.5.3", "1.5.2") && !AutoUpdater.Newer("v1.5.2", "1.5.2") && !AutoUpdater.Newer("1.5.1", "1.5.2") && AutoUpdater.Newer("1.10.0", "1.9.9") && !AutoUpdater.Newer("latest", "1.0.0"), "which version is newer");
        Console.WriteLine("in use now: a window of its own, at work or asking, or used a moment ago; a press on the update button is always answered");
        return 0;
    }
}
