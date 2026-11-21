using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using RemoteCli;

/// A conversation of Antigravity CLI (`agy acp`, its stand-in tests/chat_standin.py) begun with a model and a thinking
/// level: the model is chosen, then the level, and only then does the first message go; a level the session does
/// not offer runs nothing; a tool without levels runs at its own. Its models are listed with their levels.
///     AcpOptionsCheck.exe <agy.cmd of the stand-in> <workspace> <record file>
public static class AcpOptionsCheck {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    static readonly JavaScriptSerializer json = new JavaScriptSerializer();
    sealed class Seen {
        readonly List<Dictionary<string, object>> events = new List<Dictionary<string, object>>();
        public void Add(string lines) { lock (events) foreach (string line in lines.Split('\n').Where(one => one.Trim().Length > 0)) events.Add(json.Deserialize<Dictionary<string, object>>(line)); }
        public Dictionary<string, object> Until(Func<Dictionary<string, object>, bool> test, string what) {
            var end = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < end) { lock (events) { var found = events.LastOrDefault(test); if (found != null) return found; } Thread.Sleep(50); }
            lock (events) throw new Exception("waited in vain for " + what + ": " + String.Join(" | ", events.Select(e => json.Serialize(e))));
        }
        public bool Any(Func<Dictionary<string, object>, bool> test) { lock (events) return events.Any(test); }
    }
    static string[] Recorded(string file) {
        if (!File.Exists(file)) return new string[0];
        return File.ReadAllLines(file).Select(line => json.Deserialize<Dictionary<string, object>>(line))
            .Select(one => one.ContainsKey("prompt") ? "prompt" : one["set"] + "=" + one["value"]).ToArray();
    }
    static string Turn(string agy, string workspace, string model, string effort, Seen seen) {
        using (var chat = Chat.Open("agy", agy, workspace, "", false, "full", seen.Add, null, null, model, effort)) {
            chat.Say("只读请教：你好", "a1");       // said at once: it waits until the conversation is set up
            return (string)seen.Until(e => (string)e["t"] == "turn", "the end of the turn")["end"];
        }
    }
    public static int Main(string[] args) {
        string agy = args[0], workspace = args[1], record = args[2];
        bool options = Environment.GetEnvironmentVariable("STANDIN_AGY_OPTIONS") == "1";
        if (options) {
            var seen = new Seen();
            Require(Turn(agy, workspace, "gemini-4-neon", "high", seen) == "done", "answered with Neon at high");
            Require(String.Join(",", Recorded(record)) == "model=gemini-4-neon,thought_level=high,prompt", "the model, then the level, then the message: " + String.Join(",", Recorded(record)));
            File.Delete(record);

            seen = new Seen();
            Require(Turn(agy, workspace, "", "low", seen) == "done", "a level without a model");
            Require(String.Join(",", Recorded(record)) == "thought_level=low,prompt", "only the level is chosen: " + String.Join(",", Recorded(record)));
            File.Delete(record);

            seen = new Seen();
            Require(Turn(agy, workspace, "gemini-4-neon", "turbo", seen) == "failed", "a level it does not offer: nothing runs");
            Require(!Recorded(record).Contains("prompt") && seen.Any(e => (string)e["t"] == "note" && ((string)e["text"]).Contains("没有提供思考深度 turbo")), "said why, and no message went: " + String.Join(",", Recorded(record)));
            File.Delete(record);

            var listed = Chat.ListModels("agy", agy, workspace, 20000);
            Require(listed != null && listed.Length == 2 && listed.All(one => String.Join(",", one.Efforts) == "low,medium,high,xhigh" && one.DefaultEffort == "medium"), "its models with its levels");
            Console.WriteLine("agy with options: the model, then the level, then the first message; a level not offered runs nothing; models listed with levels");
        } else {
            var seen = new Seen();
            Require(Turn(agy, workspace, "", "high", seen) == "done", "a tool without levels runs at its own");
            Require(String.Join(",", Recorded(record)) == "prompt" && seen.Any(e => (string)e["t"] == "note" && ((string)e["text"]).Contains("不能设置思考深度")), "said so, and went on: " + String.Join(",", Recorded(record)));
            Console.WriteLine("agy without options: a level asked for is said to be unavailable, and the message goes");
        }
        return 0;
    }
}
