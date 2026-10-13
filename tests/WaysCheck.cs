using System;
using System.Collections.Generic;
using System.Linq;
using RemoteCli;

// What the phone can do to a conversation and by which way (agent-windows/Ways.cs), for each tool and each place a
// conversation runs in, as it stands: one table, so that Claude Code and Codex are seen side by side.
public static class WaysCheck {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    static TerminalAgent.SessionInfo In(string tool, string origin, bool wakes, bool ends, string reason = "") {
        return new TerminalAgent.SessionInfo { Id = Guid.NewGuid().ToString(), Tool = tool, Dir = "demo", Title = "t", Origin = origin, Live = true, Now = true, Wakes = wakes, CanTakeover = ends, TakeoverReason = reason, Status = "idle" };
    }
    static Reach.Open Hooked(string status, bool steps = false, bool held = false, bool answerable = true) { return new Reach.Open { Status = status, Steps = steps, Held = held, Answerable = answerable }; }
    static CodexDesktopSessions.State Channel(bool ready, bool busy, string turn) { return new CodexDesktopSessions.State { Folder = "x", Ready = ready, Busy = busy, Turn = turn, At = DateTime.UtcNow }; }
    static void Is(string what, Ways ways, string say, string stop, string answer, string end, string status, string reach, string transport) {
        string got = String.Join(" ", ways.Say, ways.Stop, ways.Answer, ways.End, ways.Status, ways.Reach == null ? "null" : String.Join(",", ways.Reach), ways.Transport);
        string wanted = String.Join(" ", say, stop, answer, end, status, reach, transport);
        Require(got == wanted, what + ": [" + got + "], expected [" + wanted + "]");
        // whatever cannot be done says why, and nothing that can be done does
        foreach (var thing in Ways.Things) {
            bool can = Convert.ToString(ways.Named[thing]).Length > 0;
            Require(can != ways.Why.ContainsKey(thing) && (can || ways.Why[thing].Length > 0), what + ": a reason for exactly what cannot be done (" + thing + ")");
        }
    }
    public static void Main() {
        const string A = Ways.App, H = Ways.Hook, W = Ways.Wake, P = Ways.Process, D = Ways.CodexDesktop;

        // ---- Claude Code, wherever it runs (its desktop app, an editor, a command line): the same ways
        foreach (string origin in new[] { "Claude 桌面版", "Positron", "" }) {
            bool cli = origin.Length == 0;
            string end = cli ? P : "";
            Is("claude at work, heard before its steps", Ways.Of(In("claude", origin, true, cli), Hooked("busy", steps: true), null), H, H, "", end, "busy", "say,stop", H);
            Is("claude at work, opened before the hook for its steps", Ways.Of(In("claude", origin, true, cli), Hooked("busy"), null), H, "", "", end, "busy", "say", "");
            Is("claude asking", Ways.Of(In("claude", origin, true, cli), Hooked("confirm"), null), H, "", H, end, "confirm", "say,answer", "");
            Is("claude asking, to be answered at the computer only", Ways.Of(In("claude", origin, true, cli), Hooked("confirm", answerable: false), null), H, "", "", end, "confirm", "say", "");
            Is("claude idle", Ways.Of(In("claude", origin, true, cli), Hooked("idle"), null), W, "", "", end, "idle", "say", "");
            Is("claude stopped, its hook kept for the phone", Ways.Of(In("claude", origin, true, cli), Hooked("waiting", held: true), null), H, "", "", end, "waiting", "say", "");
            Is("claude stopped, nothing kept", Ways.Of(In("claude", origin, true, cli), Hooked("waiting"), null), W, "", "", end, "waiting", "say", "");
            Is("claude that tells nothing, with an address", Ways.Of(In("claude", origin, true, cli), null, null), W, "", "", end, "idle", "say", "");
            Is("claude that tells nothing and has no address", Ways.Of(In("claude", origin, false, cli), null, null), "", "", "", end, "idle", "null", "");
        }
        var old = Ways.Of(In("claude", "", true, true), Hooked("busy"), null);
        Require(old.Why["stop"].Contains("重新打开"), "why a conversation opened before the hook cannot be stopped: " + old.Why["stop"]);

        // ---- Codex's desktop app with its own channel: the channel first, its hooks while it works
        Is("codex app ready", Ways.Of(In("codex", D, true, false), Hooked("idle"), Channel(true, false, "")), A, "", "", "", "waiting", "say", A);
        Is("codex app at work, heard by its hooks", Ways.Of(In("codex", D, true, false), Hooked("busy"), Channel(false, true, "turn-1")), H, A, "", "", "busy", "say,stop", A);
        Is("codex app at work, its hooks silent", Ways.Of(In("codex", D, true, false), null, Channel(false, true, "turn-1")), "", A, "", "", "busy", "stop", A);
        Is("codex app at work on a goal", Ways.Of(In("codex", D, true, false), Hooked("busy"), Channel(false, true, "")), H, "", "", "", "busy", "say", A);
        Is("codex app asking", Ways.Of(In("codex", D, true, false), Hooked("confirm"), Channel(false, true, "turn-1")), H, A, H, "", "confirm", "say,stop,answer", A);
        Is("codex app neither ready nor at work", Ways.Of(In("codex", D, true, false), null, Channel(false, false, "")), "", "", "", "", "confirm", "", A);
        Require(Ways.Of(In("codex", D, true, false), Hooked("busy"), Channel(false, true, "")).Why["stop"].Contains("目标"), "why a goal cannot be stopped from here");

        // ---- Codex's desktop app without it (a conversation outside the projects, another version): hooks only, never the queue
        Is("codex app without channel, at work", Ways.Of(In("codex", D, true, false), Hooked("busy"), null), H, "", "", "", "busy", "say", "");
        Is("codex app without channel, idle", Ways.Of(In("codex", D, true, false), Hooked("idle"), null), "", "", "", "", "idle", "", "");
        Is("codex app without channel, untold", Ways.Of(In("codex", D, true, false), null, null), "", "", "", "", "idle", "null", "");
        Require(Ways.Of(In("codex", D, true, false), Hooked("idle"), null).Why["say"].Contains("已添加的项目") && !Ways.Of(In("codex", D, true, false), Hooked("idle"), null).Woken, "Codex's desktop app is never told through Codex's queue, and says why it cannot be told");

        // ---- Codex's command line: hooks while it works, its queue while it sits idle; it cannot be stopped, it can be ended
        Is("codex cli at work", Ways.Of(In("codex", "", true, true), Hooked("busy"), null), H, "", "", P, "busy", "say", "");
        Is("codex cli asking", Ways.Of(In("codex", "", true, true), Hooked("confirm"), null), H, "", H, P, "confirm", "say,answer", "");
        Is("codex cli idle", Ways.Of(In("codex", "", true, true), Hooked("idle"), null), W, "", "", P, "idle", "say", "");
        Is("codex cli idle, no queue", Ways.Of(In("codex", "", false, true), Hooked("idle"), null), "", "", "", P, "idle", "", "");
        Require(Ways.Of(In("codex", "", true, true), Hooked("busy"), null).Why["stop"].Contains("Codex 命令行"), "why Codex's command line cannot be stopped");

        // ---- ending: only where the program is known for certain, with the computer's own reason where it is not
        Require(Ways.Of(In("claude", "", true, false, "其它远程终端正在使用这段对话"), Hooked("idle"), null).Why["end"] == "其它远程终端正在使用这段对话", "the computer's reason for not ending");
        Require(Ways.Of(In("claude", "Claude 桌面版", true, false), Hooked("idle"), null).Why["end"].Contains("应用"), "an application's conversation is closed there");

        // ---- one that no program has open: nothing, each with a reason
        var saved = In("claude", "", true, true); saved.Live = false;
        var none = Ways.Of(saved, null, null);
        Require(none.Reach == null && none.Named.Values.All(way => Convert.ToString(way).Length == 0) && Ways.Things.All(none.Why.ContainsKey), "a saved conversation has no way in");
        Console.WriteLine("ways: Claude Code and Codex, in an application, an editor and a command line, by how each stands");
    }
}
