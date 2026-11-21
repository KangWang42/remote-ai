using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using RemoteCli;

/// A conversation of the real Antigravity CLI begun as a team's member with Gemini 4 Neon at a high thinking level
/// (AgyChat, by `agy acp`, through tests/acp_record_proxy.py): the model is chosen, then the level, and the tool says
/// so, before the first message goes; the message is answered.
///     AgyOptionsLive.exe <launcher of the proxy around agy> <record file> <empty workspace>
/// Costs one short turn of the person's agy account; not in CI.
public static class AgyOptionsLive {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

    public static int Main(string[] args) {
        string launcher = args[0], record = args[1], workspace = args[2];
        var events = new List<Dictionary<string, object>>();
        Action<string> seen = text => { lock (events) foreach (string line in text.Split('\n').Where(one => one.Trim().Length > 0)) events.Add(json.Deserialize<Dictionary<string, object>>(line)); };
        using (var chat = Chat.Open("agy", launcher, workspace, "", false, "full", seen, null, null, "gemini-4-neon", "high")) {
            chat.Say("只回复两个字：好的", "o1");
            var end = DateTime.UtcNow.AddSeconds(240);
            Dictionary<string, object> turn = null;
            while (turn == null && DateTime.UtcNow < end) { lock (events) turn = events.LastOrDefault(e => (string)e["t"] == "turn"); Thread.Sleep(200); }
            Require(turn != null && (string)turn["end"] == "done", "the first message was answered: " + (turn == null ? "no end" : json.Serialize(turn)));
        }
        // what went to the tool, in order, and what it answered each
        var lines = File.ReadAllLines(record).Select(line => json.Deserialize<Dictionary<string, object>>(line))
            .Select(one => new { Way = (string)one["way"], M = json.DeserializeObject((string)one["line"]) as Dictionary<string, object> }).Where(one => one.M != null).ToList();
        var asked = lines.Where(one => one.Way == "in" && one.M.ContainsKey("method")).Select(one => one.M).ToList();
        Func<Dictionary<string, object>, string> step = m => {
            string method = (string)m["method"];
            var p = m.ContainsKey("params") ? m["params"] as Dictionary<string, object> : null;
            return method == "session/set_config_option" ? p["configId"] + "=" + p["value"] : method;
        };
        string order = String.Join(",", asked.Select(step).Where(one => one != "initialize"));
        Require(order == "session/new,model=gemini-4-neon,thought_level=high,session/prompt", "the order asked: " + order);
        var levelAsk = asked.First(m => step(m) == "thought_level=high");
        var answer = lines.First(one => one.Way == "out" && one.M.ContainsKey("id") && !one.M.ContainsKey("method") && Equals(Convert.ToString(one.M["id"]), Convert.ToString(levelAsk["id"]))).M;
        Require(!answer.ContainsKey("error"), "the level was taken: " + json.Serialize(answer));
        var options = ((answer["result"] as Dictionary<string, object>)["configOptions"] as object[]).Cast<Dictionary<string, object>>().ToList();
        string model = (string)options.First(o => (string)o["category"] == "model")["currentValue"], level = (string)options.First(o => (string)o["category"] == "thought_level")["currentValue"];
        Require(model == "gemini-4-neon" && level == "high", "the tool says it runs Neon at high: " + model + " " + level);
        Console.WriteLine("agy as a member: " + order + "; the tool says " + model + " at " + level + "; answered");
        return 0;
    }
}
