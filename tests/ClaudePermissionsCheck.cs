using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using RemoteCli;

class ClaudePermissionsCheck {
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static void Require(bool okay, string why) { if (!okay) throw new Exception(why); }
    static Dictionary<string, object> Suggestion(object rule, string destination) {
        return new Dictionary<string, object> { { "type", "addRules" }, { "behavior", "allow" },
            { "destination", destination }, { "rules", new object[] { rule } } };
    }
    static Dictionary<string, object> Map(object value) { return (Dictionary<string, object>)value; }
    public static void Main(string[] args) {
        var cases = (object[])Json.DeserializeObject(File.ReadAllText(args[0], Encoding.UTF8));
        foreach (var item in cases) {
            var example = Map(item);
            var rule = new Dictionary<string, object> { { "toolName", example["tool"] } };
            if (example.ContainsKey("content")) rule["ruleContent"] = example["content"];
            var suggestion = Suggestion(rule, "localSettings");
            var suggestions = new object[] { suggestion, new Dictionary<string, object> { { "type", "setMode" }, { "mode", "acceptEdits" }, { "destination", "userSettings" } } };
            string original = Json.Serialize(suggestions), name = (string)example["name"];
            bool valid = (bool)example["valid"];
            Require((ClaudePermissions.Select(suggestions, false) != null) == valid, name + ": selection");
            var events = new List<Dictionary<string, object>>();
            using (var chat = new ClaudeChat(".", line => events.Add(Map(Json.DeserializeObject(line)))))
            using (var sent = new MemoryStream()) {
                typeof(Chat).GetField("input", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(chat, sent);
                var request = new Dictionary<string, object> { { "subtype", "can_use_tool" }, { "tool_name", example["tool"] },
                    { "input", new Dictionary<string, object> { { "command", "requested operation" } } }, { "permission_suggestions", suggestions } };
                var message = new Dictionary<string, object> { { "type", "control_request" }, { "request_id", "request-1" }, { "request", request } };
                typeof(ClaudeChat).GetMethod("Received", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(chat, new object[] { message });
                var ask = events.Last(e => (string)e["t"] == "ask");
                var options = ((IEnumerable)ask["options"]).Cast<object>().Select(o => (string)Map(o)["id"]).ToArray();
                Require(options.Contains("always") == valid, name + ": displayed options");
                if (valid) {
                    Require(((string)ask["text"]).Contains(ClaudePermissions.Detail(suggestion)), name + ": complete scope shown");
                    chat.Answer((string)ask["id"], "always");
                } else {
                    bool refused = false;
                    try { chat.Answer((string)ask["id"], "always"); } catch (ArgumentException) { refused = true; }
                    Require(refused && sent.Length == 0, name + ": forged always refused without replying");
                    chat.Answer((string)ask["id"], "allow");
                }
                var answer = Map(Map(Map(Json.DeserializeObject(Encoding.UTF8.GetString(sent.ToArray())))["response"])["response"]);
                Require((string)answer["behavior"] == "allow", name + ": single action remains available");
                if (valid) Require(Json.Serialize(answer["updatedPermissions"]) == Json.Serialize(new[] { suggestion }), name + ": only displayed suggestion returned intact");
                else Require(!answer.ContainsKey("updatedPermissions"), name + ": single use saves nothing");
            }
            Require(Json.Serialize(suggestions) == original, name + ": source suggestion unchanged");
        }
        var exact = new Dictionary<string, object> { { "toolName", "Bash" }, { "ruleContent", "npm test" } };
        var session = Suggestion(exact, "session");
        Require(ClaudePermissions.Select(new[] { session }, false) == null && ClaudePermissions.Select(new[] { session }, true) == session, "session only in chats");
        Require(ClaudePermissions.Detail(session).StartsWith("仅本次会话"), "session scope is explicit");
        foreach (string destination in new[] { "userSettings", "projectSettings", "unknown" })
            Require(ClaudePermissions.Select(new[] { Suggestion(exact, destination) }, true) == null, "no other settings scope: " + destination);
        var bad = Suggestion(new Dictionary<string, object> { { "toolName", "Bash" }, { "ruleContent", "mv *.csv backup/" } }, "localSettings");
        var safe = Suggestion(exact, "localSettings");
        Require(ClaudePermissions.Select(new[] { bad, safe }, false) == safe, "first eligible complete suggestion");
        safe["rules"] = new object[] { exact, ((object[])bad["rules"])[0] };
        Require(ClaudePermissions.Select(new[] { safe }, true) == null, "a mixed suggestion is never partially saved");
        safe["rules"] = new object[] { exact }; safe["type"] = "replaceRules";
        Require(ClaudePermissions.Select(new[] { safe }, true) == null, "replacement denied");
        safe["type"] = "addRules"; safe["behavior"] = "deny";
        Require(ClaudePermissions.Select(new[] { safe }, true) == null, "only allow suggestions");
        safe["behavior"] = "allow"; safe["rules"] = Enumerable.Repeat<object>(exact, 5).ToArray();
        Require(ClaudePermissions.Select(new[] { safe }, true) == null, "scope with too many rules is not offered");
        safe["rules"] = new object[] { new Dictionary<string, object> { { "toolName", "Bash" }, { "ruleContent", new string('x', 700) } } };
        Require(ClaudePermissions.Select(new[] { safe }, true) == null, "scope that cannot be shown is not offered");
        Require(ClaudePermissions.Select("bad", true) == null && ClaudePermissions.Select(new object[] { null, "bad" }, true) == null, "malformed input denied");
        Console.WriteLine("Claude permissions: " + cases.Length + " shared cases, chat decisions, scope and source preservation passed");
    }
}
