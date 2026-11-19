using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using RemoteCli;

/// A conversation of the real Antigravity CLI run as the phone runs it (AgyChat, by `agy acp`), in an empty workspace:
/// a first message answered after a step, a long task stopped, and the conversation gone on with by its id.
///     AgyChatLive.exe <agy.exe> <empty workspace>
/// Costs three short turns of the person's agy account; not in CI.
public static class AgyChatLive {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    static readonly JavaScriptSerializer json = new JavaScriptSerializer();

    sealed class Seen {
        readonly List<Dictionary<string, object>> events = new List<Dictionary<string, object>>();
        public void Add(string lines) { lock (events) foreach (string line in lines.Split('\n').Where(one => one.Trim().Length > 0)) events.Add(json.Deserialize<Dictionary<string, object>>(line)); }
        public Dictionary<string, object> Until(Func<Dictionary<string, object>, bool> test, string what, int from = 0, int seconds = 180) {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end) { lock (events) { var found = events.Skip(from).LastOrDefault(test); if (found != null) return found; } Thread.Sleep(200); }
            lock (events) throw new Exception("waited in vain for " + what + ": " + String.Join(" | ", events.Skip(Math.Max(0, events.Count - 6)).Select(e => json.Serialize(e))));
        }
        public int Count { get { lock (events) return events.Count; } }
        public string Text(string kind) { lock (events) { var ids = new HashSet<string>(events.Where(e => (string)e["t"] == "item" && (string)e["kind"] == kind).Select(e => (string)e["id"])); return String.Concat(events.Where(e => e.ContainsKey("id") && ids.Contains(Convert.ToString(e["id"])) && ((string)e["t"] == "more" || (string)e["t"] == "item")).Select(e => Convert.ToString(e["text"]))); } }
    }

    public static int Main(string[] args) {
        string agy = args[0], workspace = args[1];
        File.WriteAllText(Path.Combine(workspace, "notes.txt"), "alpha\nbeta\n");
        string session;
        {
            var seen = new Seen();
            using (var chat = Chat.Open("agy", agy, workspace, "", false, "full", seen.Add)) {
                session = (string)seen.Until(e => (string)e["t"] == "session", "its id")["session"];
                seen.Until(e => (string)e["t"] == "state" && (string)e["state"] == "idle", "ready");
                chat.Say("Use a tool to read notes.txt in this folder, then reply with only its first line.", "a1");
                var turn = seen.Until(e => (string)e["t"] == "turn", "the end of the first turn");
                Require((string)turn["end"] == "done", "the first turn ended: " + json.Serialize(turn));
                seen.Until(e => (string)e["t"] == "item" && (string)e["kind"] == "step", "a step");
                Require(seen.Text("text").Contains("alpha"), "it said the first line: " + seen.Text("text"));
                Console.WriteLine("answered after a step: " + seen.Text("text").Trim());

                int n = seen.Count;
                chat.Say("Do these one at a time, each with its own tool call: read notes.txt, list this folder, read notes.txt again, list the folder again, read notes.txt once more. Then reply FINISHED.", "a2");
                seen.Until(e => (string)e["t"] == "item" && (string)e["kind"] == "step", "a step of the long task", n);
                chat.Stop();
                var stopped = seen.Until(e => (string)e["t"] == "turn", "the stopped turn", n, 60);
                Require((string)stopped["end"] == "stopped", "told to stop, the turn ends stopped: " + json.Serialize(stopped));
                Console.WriteLine("stopped: " + json.Serialize(stopped));
            }
        }
        {
            var seen = new Seen();
            using (var chat = Chat.Open("agy", agy, workspace, session, true, "full", seen.Add)) {
                Require((string)seen.Until(e => (string)e["t"] == "session", "its id again")["session"] == session, "the same conversation");
                seen.Until(e => (string)e["t"] == "state" && (string)e["state"] == "idle", "ready again");
                Require(seen.Text("user").Length == 0, "what was said before is not replayed by the driver");
                chat.Say("What was the first line you told me earlier in this conversation? Reply with only it.", "a3");
                Require((string)seen.Until(e => (string)e["t"] == "turn", "the end of the turn after loading")["end"] == "done", "answered after loading");
                Require(seen.Text("text").Contains("alpha"), "it remembers: " + seen.Text("text"));
                Console.WriteLine("gone on with: " + seen.Text("text").Trim());
            }
        }
        Console.WriteLine("agy as a conversation on the real tool: answered, stopped, gone on with");
        return 0;
    }
}
