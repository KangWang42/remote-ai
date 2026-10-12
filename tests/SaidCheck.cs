using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RemoteCli;

/// What the phone is shown of a saved conversation: what was asked, what was answered and what was done, without
/// the things the tools write into a conversation for themselves. Made-up conversation files only.
public static class SaidCheck {
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static string Quoted(string text) { return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(text); }
    static string Write(string folder, string name, params string[] lines) {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, String.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return path;
    }
    static string Shown(Dictionary<string, object> result) {
        return String.Join(" | ", ((IEnumerable)result["messages"]).Cast<Dictionary<string, object>>().Select(m => m["role"] + ":" + m["text"]));
    }
    public static int Main() {
        string folder = Path.Combine(Path.GetTempPath(), "remote-cli-said-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            string claude = Write(folder, "claude.jsonl",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"第一句\"}}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"<system-reminder>不给人看</system-reminder>\"}}",
                "{\"type\":\"user\",\"isSidechain\":true,\"message\":{\"role\":\"user\",\"content\":\"子任务\"}}",
                "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":\"说明\"}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"我先看看登录的代码。\"},{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"grep -rn  login\\n src\"}}]}}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"很长的输出\"}]}}",
                "not json at all",
                "{\"type\":\"ai-title\",\"aiTitle\":\"修好登录\"}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + new string('长', 3000) + "\"}]}}");
            var result = TerminalAgent.Said(claude, "claude");
            // What the phone said reached the conversation through its Stop hook, or as a message from another session:
            // Claude Code notes both for itself, and both are shown as what the user said. A message that only woke the
            // conversation, and what it answered to that, are not shown.
            string phone = Write(folder, "phone.jsonl",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"在电脑上说的\"}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"好的。\"}]}}",
                "{\"type\":\"user\",\"isMeta\":true,\"origin\":{\"kind\":\"peer\",\"name\":\"remote-cli-phone-e3\",\"body\":" + Quoted(ClaudeRelay.Wake) + "},\"message\":{\"role\":\"user\",\"content\":" + Quoted("Another Claude session sent a message:\n<cross-session-message from=\"uds:x\">\n" + ClaudeRelay.Wake + "\n</cross-session-message>") + "}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"…\"}]}}",
                "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":" + Quoted("Stop hook feedback:\n" + Reach.FromPhone + " while they are away from the computer. Treat it as their next instruction:\n\n把结论再写短一点\n\n分两段") + "}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"改短了。\"}]}}",
                "{\"type\":\"user\",\"isMeta\":true,\"origin\":{\"kind\":\"peer\",\"name\":\"remote-cli-phone-9e\",\"body\":" + Quoted(ClaudeRelay.Framed("再加一个例子")) + "},\"message\":{\"role\":\"user\",\"content\":\"Another Claude session sent a message: ...\"}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"加了。\"}]}}",
                "{\"type\":\"user\",\"isMeta\":true,\"origin\":{\"kind\":\"peer\",\"name\":\"someone-else\",\"body\":\"check the tests over there\"},\"message\":{\"role\":\"user\",\"content\":\"Another Claude session sent a message: ...\"}}",
                "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":\"Stop hook feedback:\\nrun the linter first\"}}",
                // the same, were it told under the name the program shows: read as its own as well
                "{\"type\":\"user\",\"isMeta\":true,\"origin\":{\"kind\":\"peer\",\"name\":\"remote-cli-phone-01\",\"body\":\"Remote AI wake-up: the user of this conversation is sending it a message from their phone.\"},\"message\":{\"role\":\"user\",\"content\":\"Another Claude session sent a message: ...\"}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"…\"}]}}",
                "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":\"Stop hook feedback:\\nMessage from the user, sent from their phone through the Remote AI app while they are away from the computer. Treat it as their next instruction:\\n\\n改名之前发的\"}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"收到。\"}]}}");
            // what the tools are told keeps the words that were tried with them
            Require(Reach.FromPhone.Contains("Remote CLI") && ClaudeRelay.Wake.StartsWith("Remote CLI wake-up") && ClaudeRelay.Framed("x").Contains("Remote CLI"), "the words the tools are told in");
            Require(Shown(TerminalAgent.Said(phone, "claude")) == "user:在电脑上说的 | assistant:好的。 | user:把结论再写短一点\n\n分两段 | assistant:改短了。 | user:再加一个例子 | assistant:加了。 | user:改名之前发的 | assistant:收到。", "what the phone said: " + Shown(TerminalAgent.Said(phone, "claude")));
            var messages = ((IEnumerable)result["messages"]).Cast<Dictionary<string, object>>().ToList();
            Require(messages.Count == 4, "claude count: " + Shown(result));
            Require(Shown(result).StartsWith("user:第一句 | assistant:我先看看登录的代码。 | tool:Bash：grep -rn login src | assistant:长长", StringComparison.Ordinal), "claude: " + Shown(result).Substring(0, 80));
            Require(((string)messages[3]["text"]).Length == 1500 && ((string)messages[3]["text"]).EndsWith("…", StringComparison.Ordinal), "long text is cut to 1500");
            Require(false.Equals(result["more"]), "nothing older");

            string codex = Write(folder, "rollout-x.jsonl",
                "{\"type\":\"session_meta\",\"payload\":{\"id\":\"x\",\"cwd\":\"D:\\\\demo\"}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"developer\",\"content\":[{\"type\":\"input_text\",\"text\":\"规则\"}]}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"<environment_context>x</environment_context>\"},{\"type\":\"input_text\",\"text\":\"部署一下\"}]}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\",\"summary\":[]}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"shell\",\"arguments\":\"{}\"}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"output\":\"ok\"}}",
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"部署好了\"}]}}");
            Require(Shown(TerminalAgent.Said(codex, "codex")) == "user:部署一下 | tool:shell | assistant:部署好了", "codex: " + Shown(TerminalAgent.Said(codex, "codex")));

            // only the last forty things said are given, and it is said that there is more
            string many = Write(folder, "many.jsonl", Enumerable.Range(1, 55).Select(n => "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"第" + n + "句\"}}").ToArray());
            var last = TerminalAgent.Said(many, "claude");
            var kept = ((IEnumerable)last["messages"]).Cast<Dictionary<string, object>>().ToList();
            Require(kept.Count == 40 && (string)kept[0]["text"] == "第16句" && (string)kept[39]["text"] == "第55句" && true.Equals(last["more"]), "the last forty");
            // A conversation at work is mostly things done. What the tool said between them is given all the same:
            // the forty are counted of what was said; of a long run of things done the last thirty, with how many came before.
            var busy = new List<string> { "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"开始吧\"}}" };
            Func<string, string> says = text => "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";
            Func<int, string> does = n => "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"step " + n + "\"}}]}}";
            // what a tool gave back is long, and is passed over unread (here it is not even whole JSON)
            string back = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"t\",\"type\":\"tool_result\",\"content\":\"" + new string('x', 9000);
            busy.Add(says("先看看现状。"));
            for (int n = 1; n <= 45; n++) { busy.Add(does(n)); busy.Add(back); }
            busy.Add(says("找到原因了。"));
            for (int n = 46; n <= 50; n++) { busy.Add(does(n)); busy.Add(back); }
            busy.Add(says("改好了。"));
            var work = TerminalAgent.Said(Write(folder, "busy.jsonl", busy.ToArray()), "claude");
            var shown = ((IEnumerable)work["messages"]).Cast<Dictionary<string, object>>().Select(m => m["role"] + ":" + m["text"]).ToList();
            Require(shown.Count == 4 + 1 + 30 + 5 && shown[0] == "user:开始吧" && shown[1] == "assistant:先看看现状。" && shown[2] == "tool:更早的 15 个操作" && shown[3] == "tool:Bash：step 16"
                && shown[32] == "tool:Bash：step 45" && shown[33] == "assistant:找到原因了。" && shown[39] == "assistant:改好了。" && true.Equals(work["more"]), "what was said between the things done: " + String.Join(" | ", shown.Take(4)) + " … " + shown.Count);
            var few = new List<Dictionary<string, object>>();
            for (int n = 0; n < 5; n++) few.Add(new Dictionary<string, object> { { "role", n % 2 == 0 ? "tool" : "assistant" }, { "text", "t" + n } });
            Require(TerminalAgent.Talk(few, 40, 30).Count == 5 && TerminalAgent.Talk(few, 1, 30).Count == 3 && TerminalAgent.Talk(new List<Dictionary<string, object>>(), 40, 30).Count == 0, "short ones whole; with the last thing said, what was done around it");
            Console.WriteLine("ok: what was said in a Claude Code and a Codex conversation, cut and counted");
            return 0;
        } finally { Directory.Delete(folder, true); }
    }
}
