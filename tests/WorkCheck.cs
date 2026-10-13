using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RemoteCli;

// The owner's work: its numbers read from made-up records of both tools (agent-windows/Stats.cs), the questions set
// to be asked at a time (Schedules.cs) and what is passed on when a conversation stops (Chains.cs).
//     WorkCheck.exe <empty folder>
public static class WorkCheck {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    static string Refused(Action act) { try { act(); return ""; } catch (InvalidOperationException error) { return error.Message; } catch (ArgumentException error) { return error.Message; } }
    static string Utc(DateTime local) { return local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }
    static long Ms(DateTime local) { return (long)(local.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

    public static void Main(string[] args) {
        string root = args[0], claude = Path.Combine(root, "claude"), codex = Path.Combine(root, "codex");
        DateTime today = DateTime.Now.Date, yesterday = today.AddDays(-1);

        // ---- Claude Code: two prompts yesterday in one conversation. The first is answered after 4 s and worked on for
        // 90 s (a step and what it gave back belong to the turn); its answer is noted in two rows and counted once.
        Directory.CreateDirectory(Path.Combine(claude, "projects", "C--work-shop"));
        DateTime a = yesterday.AddHours(9);
        string usage = "\"usage\":{\"input_tokens\":100,\"cache_creation_input_tokens\":30,\"cache_read_input_tokens\":70,\"output_tokens\":40}";
        File.WriteAllLines(Path.Combine(claude, "projects", "C--work-shop", "11111111-2222-3333-4444-555555555555.jsonl"), new[] {
            "{\"type\":\"user\",\"isSidechain\":false,\"cwd\":\"C:\\\\work\\\\shop\",\"message\":{\"role\":\"user\",\"content\":\"fix it\"},\"timestamp\":\"" + Utc(a) + "\",\"uuid\":\"u1\"}",
            "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"id\":\"msg_one\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"looking\"}]," + usage + "},\"timestamp\":\"" + Utc(a.AddSeconds(4)) + "\"}",
            "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"id\":\"msg_one\",\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Bash\",\"input\":{}}]," + usage + "},\"timestamp\":\"" + Utc(a.AddSeconds(5)) + "\"}",
            "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"ok\"}]},\"timestamp\":\"" + Utc(a.AddSeconds(60)) + "\"}",
            "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"id\":\"msg_two\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"usage\":{\"input_tokens\":10,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":200,\"output_tokens\":5}},\"timestamp\":\"" + Utc(a.AddSeconds(90)) + "\"}",
            "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":\"a note of its own\"},\"timestamp\":\"" + Utc(a.AddSeconds(95)) + "\"}",
            "{\"type\":\"user\",\"isSidechain\":true,\"message\":{\"role\":\"user\",\"content\":\"a helper's\"},\"timestamp\":\"" + Utc(a.AddSeconds(96)) + "\"}",
            "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"and the tests\"},\"timestamp\":\"" + Utc(a.AddHours(2)) + "\",\"uuid\":\"u2\"}",
            "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"id\":\"msg_three\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"usage\":{\"input_tokens\":1,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":2}},\"timestamp\":\"" + Utc(a.AddHours(2).AddSeconds(10)) + "\"}",
        }, new UTF8Encoding(false));
        // ---- Codex: one turn today, 30 s long, first answered after 3 s, two requests with their tokens
        Directory.CreateDirectory(Path.Combine(codex, "sessions", "2026", "10", "13"));
        DateTime b = today.AddHours(8);
        File.WriteAllLines(Path.Combine(codex, "sessions", "2026", "10", "13", "rollout-2026-10-13T08-00-00-66666666-7777-8888-9999-000000000000.jsonl"), new[] {
            "{\"timestamp\":\"" + Utc(b) + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"66666666-7777-8888-9999-000000000000\",\"cwd\":\"D:\\\\notes\\\\thesis\"}}",
            "{\"timestamp\":\"" + Utc(b.AddSeconds(1)) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"t\",\"started_at\":1}}",
            "{\"timestamp\":\"" + Utc(b.AddSeconds(4)) + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\"}}",
            "{\"timestamp\":\"" + Utc(b.AddSeconds(9)) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":9000,\"output_tokens\":900},\"last_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":600,\"output_tokens\":50,\"reasoning_output_tokens\":20,\"total_tokens\":1050},\"model_context_window\":1}}}",
            "{\"timestamp\":\"" + Utc(b.AddSeconds(20)) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":9000,\"output_tokens\":900},\"last_token_usage\":{\"input_tokens\":2000,\"cached_input_tokens\":1500,\"output_tokens\":70,\"reasoning_output_tokens\":0,\"total_tokens\":2070},\"model_context_window\":1}}}",
            "{\"timestamp\":\"" + Utc(b.AddSeconds(31)) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"t\",\"started_at\":1,\"duration_ms\":30000}}",
        }, new UTF8Encoding(false));

        var stats = new Stats(() => claude, () => codex);
        Func<string, string> named = folder => folder.EndsWith("shop", StringComparison.OrdinalIgnoreCase) ? "web-shop" : Path.GetFileName(folder);
        var read = stats.Read(7, named);
        var days = ((object[])read["days"]).Cast<Dictionary<string, object>>().ToList();
        Require(days.Count == 7 && (string)days[6]["date"] == today.ToString("yyyy-MM-dd") && days.Take(5).All(d => (int)d["prompts"] == 0 && (long)d["in"] == 0), "seven days, today the last, empty ones with nothing");
        var y = days[5]; var t = days[6];
        Require((int)y["prompts"] == 2 && (int)y["sessions"] == 1 && (long)y["active"] == 100, "Claude Code yesterday: two prompts in one conversation, 90 s and 10 s at work: " + y["prompts"] + " " + y["sessions"] + " " + y["active"]);
        Require((long)y["in"] == 111 && (long)y["out"] == 47 && (long)y["cached"] == 300, "an answer noted in two rows is counted once: " + y["in"] + " " + y["out"] + " " + y["cached"]);
        Require(Convert.ToDouble(y["wait"]) == 7, "first answered after 4 s and after 10 s: the middle of the two: " + y["wait"]);
        Require((int)t["prompts"] == 1 && (long)t["active"] == 30 && (long)t["in"] == 900 && (long)t["cached"] == 2100 && (long)t["out"] == 120 && Convert.ToDouble(t["wait"]) == 3, "Codex today: " + t["prompts"] + " " + t["active"] + " " + t["in"] + " " + t["cached"] + " " + t["out"] + " " + t["wait"]);
        var tools = ((object[])read["tools"]).Cast<Dictionary<string, object>>().ToDictionary(x => (string)x["name"]);
        var projects = ((object[])read["projects"]).Cast<Dictionary<string, object>>().Select(x => (string)x["name"] + ":" + x["prompts"] + ":" + x["active"]).ToArray();
        Require((long)tools["claude"]["prompts"] == 2 && (long)tools["codex"]["tokens"] == 1020 && String.Join(",", projects) == "web-shop:2:100,thesis:1:30", "by tool and by project, the project by its name: " + String.Join(",", projects));
        var hours = (int[])read["hours"];
        Require(hours[9] == 1 && hours[11] == 1 && hours[8] == 1 && hours.Sum() == 3, "by hour of the day");
        Require(((object[])stats.Read(1, named)["days"]).Length == 1 && (int)((Dictionary<string, object>)((object[])stats.Read(1, named)["days"])[0])["prompts"] == 1, "one day: today only");

        // ---- questions asked at a time
        string kept = Path.Combine(root, "schedules.json");
        var schedules = new Schedules(kept);
        DateTime now = today.AddHours(10);
        Func<string, string, Dictionary<string, object>> item = (id, kind) => new Dictionary<string, object> { { "id", id }, { "tool", "claude" }, { "session", "11111111-2222-3333-4444-555555555555" }, { "title", "甲" }, { "text", "进展如何？" }, { "kind", kind },
            { "time", "09:30" }, { "minutes", 30 }, { "at", Ms(now.AddMinutes(20)) } };
        string daily = new string('a', 32), every = new string('b', 32), once = new string('c', 32);
        Require(schedules.Set(item(daily, "daily"), now).Next == Ms(today.AddDays(1).AddHours(9.5)), "every day at 09:30: past that today, so tomorrow");
        Require(schedules.Set(item(every, "every"), now).Next == Ms(now.AddMinutes(30)) && schedules.Set(item(once, "once"), now).Next == Ms(now.AddMinutes(20)), "every half hour; once in twenty minutes");
        var late = item(new string('d', 32), "once"); late["at"] = Ms(now.AddMinutes(-1));
        var wordy = item(new string('d', 32), "every"); wordy["minutes"] = 2;
        var silent = item(new string('d', 32), "daily"); silent["text"] = " ";
        Require(Refused(() => schedules.Set(late, now)).Contains("现在之后") && Refused(() => schedules.Set(wordy, now)).Contains("5 分钟") && Refused(() => schedules.Set(silent, now)).Contains("1 至 2000") && schedules.All().Length == 3, "what cannot be set says why");
        Require(schedules.Due(now.AddMinutes(10)).Length == 0, "nothing is due yet");
        var due = schedules.Due(now.AddMinutes(31));
        Require(String.Join(",", due.Select(d => d.Kind).OrderBy(k => k)) == "every,once" && schedules.Due(now.AddMinutes(31)).Length == 0, "what is due is given once");
        Require(!schedules.All().First(s => s.Id == once).On && schedules.All().First(s => s.Id == every).Next == Ms(now.AddMinutes(61)), "a single one is switched off, one that repeats is moved on");
        schedules.Done(every, false, "这段对话没有在电脑上开着", now.AddMinutes(31));
        Require(!File.ReadAllText(kept).Contains("进展如何") && !File.ReadAllText(kept).Contains("\\u8fdb"), "kept in a file, the words not in plain sight");
        var again = new Schedules(kept).All();
        Require(again.Length == 3 && again.First(s => s.Id == every).LastNote.Contains("没有在电脑上开着") && again.First(s => s.Id == every).Runs == 1 && again.First(s => s.Id == daily).Text == "进展如何？", "there again after a restart, with how the last asking went");
        Require(schedules.Remove(daily) && !schedules.Remove(daily) && new Schedules(kept).All().Length == 2, "removed for good");

        // ---- passed on when a conversation stops: as many times as it was set for, and no more
        string rules = Path.Combine(root, "chains.json"), one = "11111111-2222-3333-4444-555555555555", other = "66666666-7777-8888-9999-000000000000";
        var chains = new Chains(rules);
        Func<string, string, string, int, Dictionary<string, object>> rule = (id, from, to, rounds) => new Dictionary<string, object> { { "id", id }, { "from_tool", "codex" }, { "from", from }, { "to_tool", "claude" }, { "to", to }, { "text", "请审查它的改动" }, { "rounds", rounds } };
        string review = new string('e', 32);
        chains.Set(rule(review, other, one, 2));
        Require(Refused(() => chains.Set(rule(new string('f', 32), other, one, 0))).Contains("1 到") && Refused(() => chains.Set(rule(new string('f', 32), other, one, 11))).Contains("1 到"), "rounds are bounded");
        var same = rule(new string('f', 32), one, one, 1); same["from_tool"] = "claude";
        Require(Refused(() => chains.Set(same)).Contains("同一段"), "not to itself");
        Require(chains.After("claude", other).Length == 0 && chains.After("codex", one).Length == 0, "only for the conversation it names, of that tool");
        Require(chains.After("codex", other).Length == 1 && chains.All()[0].Left == 1 && chains.After("codex", other.ToUpperInvariant()).Length == 1 && chains.After("codex", other).Length == 0, "twice, then no more");
        chains.Done(review, true, "已交给对话");
        var afterwards = new Chains(rules).All();
        Require(afterwards.Length == 1 && afterwards[0].Left == 0 && afterwards[0].Runs == 1 && afterwards[0].Text == "请审查它的改动" && !File.ReadAllText(rules).Contains("审查"), "kept, with what is left of its rounds; the words not in plain sight");
        chains.Set(rule(review, other, one, 3));
        Require(chains.All()[0].Left == 3 && chains.Remove(review) && chains.All().Length == 0, "set anew, its rounds begin again");
        Console.WriteLine("work: numbers from both tools' records, questions asked at a time, and what is passed on when a conversation stops");
    }
}
