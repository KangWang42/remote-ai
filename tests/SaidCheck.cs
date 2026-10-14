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
            Require(TerminalAgent.CompletedTurn("{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn1\"}}\n") == "turn1", "explicit completed turn identity");
            Require(TerminalAgent.CompletedTurn("{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn1\"}}\n{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn2\"}}\n") == "", "new work invalidates prior completion");
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
                // handed over at a stop as added context (a Claude Code that takes it notes that as an attachment): the user's
                // words all the same; what another hook added, or a Stop hook of someone else's, is none of the phone's
                "{\"type\":\"user\",\"isMeta\":true,\"origin\":{\"kind\":\"peer\",\"name\":\"remote-cli-phone-02\",\"body\":" + Quoted(ClaudeRelay.Wake) + "},\"message\":{\"role\":\"user\",\"content\":\"Another Claude session sent a message: ...\"}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Understood.\"}]}}",
                "{\"type\":\"attachment\",\"attachment\":{\"type\":\"hook_success\",\"hookName\":\"Stop\",\"content\":\"\",\"stdout\":\"{}\"}}",
                "{\"type\":\"attachment\",\"attachment\":{\"type\":\"hook_additional_context\",\"hookName\":\"Stop\",\"content\":[" + Quoted(Reach.FromPhone + ". It is the next message of the user of this conversation:\n\n作为上下文交来的\n第二行") + "]}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"照做了。\"}]}}",
                // handed over between two steps (Claude Code notes the hook by its event and the step's tool): the user's words too;
                // what somebody else's hook added before a step is not
                "{\"type\":\"attachment\",\"attachment\":{\"type\":\"hook_additional_context\",\"hookName\":\"PreToolUse:PowerShell\",\"hookEvent\":\"PreToolUse\",\"toolUseID\":\"toolu_1\",\"content\":[" + Quoted(Reach.BetweenSteps + "两步之间交来的") + "]}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"好，改了。\"}]}}",
                "{\"type\":\"attachment\",\"attachment\":{\"type\":\"hook_additional_context\",\"hookName\":\"PreToolUse:Bash\",\"hookEvent\":\"PreToolUse\",\"content\":[\"lint first\n\nplease\"]}}",
                "{\"type\":\"attachment\",\"attachment\":{\"type\":\"hook_additional_context\",\"hookName\":\"PostToolUse\",\"content\":[" + Quoted(Reach.FromPhone + ":\n\n别的钩子加的") + "]}}",
                "{\"type\":\"attachment\",\"attachment\":{\"type\":\"hook_additional_context\",\"hookName\":\"Stop\",\"content\":[\"The linter still fails.\\n\\nfix it\"]}}",
                // written while the program went by its earlier name: read as its own as well
                "{\"type\":\"user\",\"isMeta\":true,\"origin\":{\"kind\":\"peer\",\"name\":\"remote-cli-phone-01\",\"body\":\"Remote CLI wake-up: the user of this conversation is sending it a message from their phone.\"},\"message\":{\"role\":\"user\",\"content\":\"Another Claude session sent a message: ...\"}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"…\"}]}}",
                "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":\"Stop hook feedback:\\nMessage from the user, sent from their phone through Remote CLI while they are away from the computer. Treat it as their next instruction:\\n\\n改名之前发的\"}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"收到。\"}]}}");
            // what the tools are told names the program as it is called now (tried with the real tools in these words)
            Require(Reach.FromPhone.Contains("Remote AI") && ClaudeRelay.Wake.StartsWith("Remote AI wake-up") && ClaudeRelay.Framed("x").Contains("Remote AI"), "the words the tools are told in");
            Require(Shown(TerminalAgent.Said(phone, "claude")) == "user:在电脑上说的 | assistant:好的。 | user:把结论再写短一点\n\n分两段 | assistant:改短了。 | user:再加一个例子 | assistant:加了。 | user:作为上下文交来的\n第二行 | assistant:照做了。 | user:两步之间交来的 | assistant:好，改了。 | user:改名之前发的 | assistant:收到。", "what the phone said: " + Shown(TerminalAgent.Said(phone, "claude")));
            var messages = ((IEnumerable)result["messages"]).Cast<Dictionary<string, object>>().ToList();
            Require(messages.Count == 4, "claude count: " + Shown(result));
            Require(Shown(result).StartsWith("user:第一句 | assistant:我先看看登录的代码。 | tool:Bash：grep -rn login src | assistant:长长", StringComparison.Ordinal), "claude: " + Shown(result).Substring(0, 80));
            Require((string)messages[3]["text"] == new string('长', 3000) && !messages[3].ContainsKey("truncated"), "the full long reply is retained");
            foreach (string hook in new[] { "\"hookName\":\"Stop:remote-ai\"", "\"hookName\":\"other\",\"hookEvent\":\"Stop\"" }) {
                foreach (bool array in new[] { false, true }) {
                    string context = Quoted(Reach.FromPhone + ":\n\n真正的手机问题");
                    string modern = Write(folder, "modern.jsonl",
                        "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"content\":" + Quoted(ClaudeRelay.Wake) + "}}",
                        "{\"type\":\"assistant\",\"message\":{\"content\":\"唤醒回复\"}}",
                        "{\"type\":\"attachment\",\"timestamp\":\"2026-10-14T01:00:00Z\",\"attachment\":{\"type\":\"hook_additional_context\"," + hook + ",\"content\":" + (array ? "[" + context + "]" : context) + "}}",
                        "{\"type\":\"assistant\",\"message\":{\"content\":\"完整回答\"}}");
                    var modernRead = TerminalAgent.Said(modern, "claude");
                    Require(Shown(modernRead) == "user:真正的手机问题 | assistant:完整回答" && Convert.ToInt64(modernRead["asked"]) > 0, "modern Stop context must unhide real replies and record the phone turn");
                }
            }
            string huge = Write(folder, "huge.jsonl", Enumerable.Range(0, 4).Select(n => "{\"type\":\"assistant\",\"message\":{\"content\":" + Quoted(new string('长', 140000) + n) + "}}").ToArray());
            var hugeRead = TerminalAgent.Said(huge, "claude");
            var hugeMessages = ((IEnumerable)hugeRead["messages"]).Cast<Dictionary<string, object>>().ToList();
            Require(true.Equals(hugeRead["more"]) && hugeMessages.Count == 2 && hugeMessages.All(m => true.Equals(m["truncated"])), "bounded very large responses explicitly report partial content");
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

            // A conversation that works without a word of answer between its steps: what it says of its work is
            // kept by Claude Code as "thinking" (a short summary, or nothing), by Codex as the summary of its
            // reasoning. That is shown, as said; a step is shown by what it is for where the tool says so.
            string silent = Write(folder, "silent.jsonl",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"比一下两个版本\"}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"abc\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Bash\",\"input\":{\"command\":\"python compare.py --a 4.0 --b 2.0\",\"description\":\"对比 4.0 和 2.0 的结果\"}}]}}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"t1\",\"type\":\"tool_result\",\"content\":\"step 1 same\\nstep 2 same\\nstep 3 differs\\r\\n\\n\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"已对比 4.0 和 2.0：差别在第三步。\",\"signature\":\"abc\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t2\",\"name\":\"Read\",\"input\":{\"file_path\":\"step3.R\"}}]}}",
                // what came back is long, and its row is not read: its first lines are taken all the same
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"t2\",\"type\":\"tool_result\",\"content\":[{\"type\":\"text\",\"text\":\"     1\\tlibrary(\\\"dplyr\\\")\\n     2\\t# 第三步 \\u4e2d\\n" + String.Concat(Enumerable.Range(3, 400).Select(n => "     " + n + "\\tx <- " + n + "\\n")) + "\"}]}]}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t3\",\"name\":\"Bash\",\"input\":{\"command\":\"Rscript step3.R\"}}]}}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"t3\",\"type\":\"tool_result\",\"content\":\"\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"" + new string('想', 700) + "\"}]}}",
                "{\"type\":\"assistant\",\"isSidechain\":true,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"子任务里的\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"差别在第三步。\"}]}}");
            var worked = ((IEnumerable)TerminalAgent.Said(silent, "claude")["messages"]).Cast<Dictionary<string, object>>().ToList();
            Require(String.Join(" | ", worked.Select(m => m["role"] + ":" + (((string)m["text"]).Length > 30 ? ((string)m["text"]).Length + " letters" : m["text"]))) ==
                "user:比一下两个版本 | tool:Bash：对比 4.0 和 2.0 的结果 | thought:已对比 4.0 和 2.0：差别在第三步。 | tool:Read：step3.R | tool:Bash：Rscript step3.R | thought:700 letters | assistant:差别在第三步。",
                "what it said of its work, and its steps by what they are for: " + String.Join(" | ", worked.Select(m => m["role"] + ":" + m["text"])).Substring(0, 200));
            // a step as a terminal would show it: the command itself where the step is told by what it is for, and
            // the first lines of what came back (six at most; nothing where nothing came back)
            Func<Dictionary<string, object>, string, string> of = (m, key) => m.ContainsKey(key) ? (string)m[key] : null;
            Require(of(worked[1], "detail") == "python compare.py --a 4.0 --b 2.0" && of(worked[1], "result") == "step 1 same\nstep 2 same\nstep 3 differs", "the command and what came back: " + of(worked[1], "detail") + " / " + of(worked[1], "result"));
            Require(of(worked[3], "detail") == null && of(worked[3], "result") == "     1\tlibrary(\"dplyr\")\n     2\t# 第三步 中\n     3\tx <- 3\n     4\tx <- 4\n     5\tx <- 5\n     6\tx <- 6\n…", "the first lines of a long one, from a row that is not read: " + of(worked[3], "result"));
            Require(of(worked[4], "detail") == null && of(worked[4], "result") == null && !worked[2].ContainsKey("result"), "a step told by its command has no second line; nothing came back, nothing is shown");
            // of a long conversation only the latest steps carry what came back
            var long_ = new List<string> { "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"开始\"}}" };
            for (int n = 1; n <= 60; n++) {
                long_.Add("{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"s" + n + "\",\"name\":\"Bash\",\"input\":{\"command\":\"step " + n + "\"}}]}}");
                long_.Add("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"s" + n + "\",\"type\":\"tool_result\",\"content\":\"done " + n + "\"}]}}");
                if (n % 20 == 0) long_.Add("{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"到第 " + n + " 步了\"}]}}");
            }
            var many_ = ((IEnumerable)TerminalAgent.Said(Write(folder, "long.jsonl", long_.ToArray()), "claude")["messages"]).Cast<Dictionary<string, object>>().Where(m => (string)m["role"] == "tool").ToList();
            Require(many_.Count == 60 && many_.Count(m => m.ContainsKey("result")) == TerminalAgent.Results && !many_[19].ContainsKey("result") && (string)many_[20]["result"] == "done 21" && (string)many_[59]["result"] == "done 60", "what came back, for the last forty steps: " + many_.Count(m => m.ContainsKey("result")));
            Require(TerminalAgent.First("a\n" + new string('x', 300) + "\nb") == "a\n" + new string('x', 200) + "…\nb" && TerminalAgent.First(" \n\n") == "" && TerminalAgent.First(null) == "" && TerminalAgent.Begins("{\"type\":\"tool_result\",\"content\":\"a\\\\b \\\"c\\\"\"}", 1) == "a\\b \"c\"" && TerminalAgent.Begins("{\"type\":\"tool_result\"}", 1) == "", "the first lines, and the beginning of a row");
            string summed = Write(folder, "summed.jsonl",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"比一下\"}]}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"**对比两个版本**\"},{\"type\":\"summary_text\",\"text\":\"先看输出。\"}],\"encrypted_content\":\"x\"}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\",\"summary\":[]}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"exec\",\"status\":\"completed\"}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"commentary\",\"content\":[{\"type\":\"output_text\",\"text\":\"已对比。\"}]}}");
            Require(Shown(TerminalAgent.Said(summed, "codex")) == "user:比一下 | thought:**对比两个版本**\n\n先看输出。 | tool:exec | assistant:已对比。", "Codex's summary of its reasoning: " + Shown(TerminalAgent.Said(summed, "codex")));
            // Codex's newer way of working wraps every step in a call named "exec". What the step was, it notes for its
            // own window as the step ends: the command that ran, the files that were read or changed. Those are shown.
            string stepped = Write(folder, "stepped.jsonl",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"比一下\"}]}}",
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\",\"content\":[{\"type\":\"text\",\"text\":\"比一下\"}]}}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"exec\",\"status\":\"completed\",\"input\":\"const r = await tools.shell({});\"}}",
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"CommandExecution\",\"command\":[\"powershell.exe\",\"-Command\",\"Rscript compare.R\"],\"parsed_cmd\":[{\"type\":\"unknown\",\"cmd\":\"Rscript compare.R\"}],\"aggregated_output\":\"ok\",\"exit_code\":0}}}",
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"CommandExecution\",\"command\":[\"powershell.exe\",\"-Command\",\"Get-Content a.R\"],\"parsed_cmd\":[{\"type\":\"read\",\"cmd\":\"Get-Content a.R\",\"name\":\"a.R\",\"path\":\"C:\\\\work\\\\a.R\"}]}}}",
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"CommandExecution\",\"command\":[\"git\",\"status\"],\"parsed_cmd\":[],\"aggregated_output\":\"fatal: not a git repository\\n\",\"exit_code\":128}}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"exec\",\"status\":\"completed\"}}",
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"FileChange\",\"changes\":{\"C:\\\\work\\\\a.R\":{\"type\":\"update\",\"unified_diff\":\"@@\"},\"C:\\\\work\\\\src\\\\b.R\":{\"type\":\"add\",\"content\":\"x\"}},\"status\":\"completed\"}}}",
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"Reasoning\",\"summary_text\":[],\"raw_content\":[]}}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"request_user_input\",\"arguments\":\"{}\"}}",
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"AgentMessage\",\"phase\":\"final_answer\",\"content\":[{\"type\":\"text\",\"text\":\"差别在第三步。\"}]}}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\",\"content\":[{\"type\":\"output_text\",\"text\":\"差别在第三步。\"}]}}");
            Require(Shown(TerminalAgent.Said(stepped, "codex")) == "user:比一下 | tool:运行：Rscript compare.R | tool:读取：a.R | tool:运行：git status | tool:修改：a.R、b.R | tool:request_user_input | assistant:差别在第三步。", "Codex's steps by what they were: " + Shown(TerminalAgent.Said(stepped, "codex")));
            var ran = ((IEnumerable)TerminalAgent.Said(stepped, "codex")["messages"]).Cast<Dictionary<string, object>>().ToList();
            Require((string)ran[1]["result"] == "ok" && (string)ran[3]["result"] == "（退出码 128）\nfatal: not a git repository" && !ran[2].ContainsKey("result") && !ran[4].ContainsKey("result"), "what came back from Codex's commands, and how one that failed ended");
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
