using System;
using System.Collections.Generic;
using System.Linq;
using RemoteCli;

/// What the agent keeps of a terminal's output after the relay has it, and how it is given to a relay that has less.
public static class HistoryCheck {
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static TerminalAgent.LiveTerminal Terminal(params string[] pieces) {
        var t = new TerminalAgent.LiveTerminal { Id = new string('a', 32), Tool = "codex", Dir = "demo" };
        foreach (string data in pieces) t.Output.Add(new Dictionary<string, object> { { "terminal", t.Id }, { "seq", ++t.Seq }, { "data", data } });
        return t;
    }
    static string Numbers(IEnumerable<Dictionary<string, object>> pieces) { return String.Join(",", pieces.Select(p => Convert.ToString(p["seq"]))); }
    public static int Main() {
        // the relay has everything: it is kept, and nothing goes out again
        var t = Terminal("one ", "two ", "three ", "four ", "five");
        Require(!TerminalAgent.Settle(t, 3, 1000) && Numbers(t.Output) == "4,5" && Numbers(t.History) == "1,2,3", "kept: " + Numbers(t.History));
        Require(!TerminalAgent.Settle(t, 5, 1000) && t.Output.Count == 0 && Numbers(t.History) == "1,2,3,4,5", "all kept");
        Require(!TerminalAgent.Settle(t, 5, 1000) && t.Output.Count == 0, "said again, nothing changes");
        // a relay that lost its last pieces is given them again; they continue what it has
        Require(TerminalAgent.Settle(t, 3, 1000) && Numbers(t.Output) == "4,5" && Numbers(t.History) == "1,2,3", "given again: " + Numbers(t.Output));
        Require(t.Output.All(p => true.Equals(p["old"])) && !t.Output[0].ContainsKey("restart") && (string)t.Output[0]["data"] == "four ", "they continue");
        // a relay that has nothing is given all of it, from the first piece on
        TerminalAgent.Settle(t, 5, 1000);
        Require(TerminalAgent.Settle(t, 0, 1000) && Numbers(t.Output) == "1,2,3,4,5" && !t.Output[0].ContainsKey("restart"), "from the start");
        t.Output.Add(new Dictionary<string, object> { { "terminal", t.Id }, { "seq", ++t.Seq }, { "data", "six" } });       // printed meanwhile
        TerminalAgent.Settle(t, 6, 1000);
        Require(Numbers(t.History) == "1,2,3,4,5,6" && !t.History[5].ContainsKey("old"), "new output is not old");

        // history that no longer begins at the start carries the modes that the dropped output switched on
        string wide = new string('x', 400);
        t = Terminal("\x1b[?1049h\x1b[?25l" + wide, "\x1b[?1000h\x1b[?10", "00l\x1b[?2004h" + wide, "\x1b[?25", "h" + wide, "late");
        TerminalAgent.Settle(t, 6, 408);
        Require(Numbers(t.History) == "5,6" && t.HistorySize == 405, "trimmed: " + Numbers(t.History) + " " + t.HistorySize);
        Require(TerminalAgent.Settle(t, 0, 408), "another relay");
        // in the order they were last changed, then the sequence the dropped piece ended in the middle of
        string lead = "\x1b[?1049h\x1b[?25l\x1b[?1000l\x1b[?2004h\x1b[?25";
        Require(true.Equals(t.Output[0]["restart"]) && (string)t.Output[0]["data"] == lead + "h" + wide, "modes in front: " + ((string)t.Output[0]["data"]).Substring(0, 40).Replace("\x1b", "^"));
        Require(!t.Output[1].ContainsKey("restart"), "only the first piece says so");
        TerminalAgent.Settle(t, 6, 1000);
        Require(!t.History[0].ContainsKey("restart"), "once it is taken the mark goes");
        Require(TerminalAgent.Settle(t, 2, 1000) && true.Equals(t.Output[0]["restart"]) && (string)t.Output[0]["data"] == lead + "h" + wide, "and a third relay is not given the modes twice");
        // a full reset of the terminal clears them
        t = Terminal("\x1b[?1049h" + wide, "\x1b" + "c" + wide,"\x1b[?2004h" + wide, "end");
        TerminalAgent.Settle(t, 4, 10);
        TerminalAgent.Settle(t, 0, 10);
        Require((string)t.Output[0]["data"] == "\x1b[?2004hend", "after a reset: " + ((string)t.Output[0]["data"]).Replace("\x1b", "^"));
        // nothing kept (a terminal that came back after an update): the relay's gap stays as it was, nothing is invented
        t = Terminal("only");
        t.Output.Clear(); t.Seq = 40; t.BaseSeq = 40;
        Require(!TerminalAgent.Settle(t, 12, 1000) && t.Output.Count == 0, "nothing to give");
        // a brand-new RelayLink after an update has ackSeq = 0; it must NOT mark the first post-update piece as restart
        t.Output.Add(new Dictionary<string, object> { { "terminal", t.Id }, { "seq", ++t.Seq }, { "data", "after update" } });
        using (var link = new RelayLink(new RelayEndpoint { Name = "local", Origin = "http://127.0.0.1:8787", Password = "p" })) {
            var firstSend = link.SelectChunks(t, 30);
            Require(firstSend.Count == 1 && !firstSend[0].ContainsKey("restart"), "returned terminal must not wipe relay history");
            // explicit restart (e.g. recalled chat history on return) still passes through
            t.Output[0]["restart"] = true;
            var recalledSend = link.SelectChunks(t, 30);
            Require(recalledSend.Count == 1 && true.Equals(recalledSend[0]["restart"]), "explicit restart passes through");
        }
        // trimmed history from this process still carries restart + modes preamble to a relay that has less
        var trimmed = Terminal("\x1b[?1049h" + wide, "keep");
        TerminalAgent.Settle(trimmed, 2, 10);
        using (var link2 = new RelayLink(new RelayEndpoint { Name = "other", Origin = "http://127.0.0.1:8788", Password = "p" })) {
            var replayed = link2.SelectChunks(trimmed, 30);
            Require(replayed.Count == 1 && true.Equals(replayed[0]["restart"]) && ((string)replayed[0]["data"]).StartsWith("\x1b[?1049h"), "trimmed history replays with modes and restart");
        }
        Console.WriteLine("history: kept after the relay has it, given again to a relay that has less, with the terminal modes in front when it begins later");
        return 0;
    }
}
