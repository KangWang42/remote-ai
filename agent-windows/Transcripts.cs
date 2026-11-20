using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// Reads saved conversation transcripts (Claude Code, Codex, Antigravity CLI) for phone preview, chat recall, title
/// extraction and explicit completion tokens. What the tools write for themselves (system reminders, large tool outputs,
/// sidechains) is excluded or trimmed while preserving complete normal replies.
public static class Transcripts {
    /// Of how many steps, counted from the last, what came back is given.
    public const int Results = 40;

    static long Milliseconds(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    static string Get(IDictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : ""; }
    static object At(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) ? v : null; }

    public static string ReadPart(string path, bool tail, int limit) {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            if (tail && stream.Length > limit) stream.Seek(-limit, SeekOrigin.End);
            byte[] bytes = new byte[(int)Math.Min(limit, stream.Length - stream.Position)]; int read = 0, n;
            while (read < bytes.Length && (n = stream.Read(bytes, read, bytes.Length - read)) > 0) read += n;
            return Encoding.UTF8.GetString(bytes, 0, read);
        }
    }

    // The value of a JSON string field, found without parsing lines that may be megabytes long or cut off.
    public static string JsonText(string text, string field, bool last) {
        var matches = Regex.Matches(text, field + @"\s*""((?:[^""\\]|\\.)*)""");
        if (matches.Count == 0) return "";
        try { return new JavaScriptSerializer().Deserialize<string>("\"" + matches[last ? matches.Count - 1 : 0].Groups[1].Value + "\"") ?? ""; } catch { return ""; }
    }

    public static string Tidy(string title) {
        title = Regex.Replace(Regex.Replace(title ?? "", @"<[^>]{1,40}>", " "), @"[\s\p{C}]+", " ").Trim();
        if (title.Length > 60) title = title.Substring(0, Char.IsHighSurrogate(title[58]) ? 58 : 59) + "…";
        return title;
    }

    // The name the tool itself shows for a conversation; without one, what the owner last asked.
    public static string ClaudeTitleOf(string path) { string same; return ClaudeTitleOf(path, out same); }
    // same tells copies of one conversation apart from others: Claude Code writes a branch or a second run of the
    // same request as a file of its own. It is the name where the conversation has one, else what it began with;
    // the last request alone ("continue") is shared by conversations that have nothing to do with each other.
    public static string ClaudeTitleOf(string path, out string same) {
        string tail = ReadPart(path, true, 256 * 1024);
        string title = JsonText(tail, @"""customTitle"":", true);
        if (title.Length == 0) title = JsonText(tail, @"""aiTitle"":", true);
        same = Tidy(title);
        if (title.Length > 0) return same;
        title = JsonText(tail, @"""lastPrompt"":", true);
        string head = ReadPart(path, false, 768 * 1024);
        string began = JsonText(head, @"""role"":""user"",""content"":", false);
        if (began.Length == 0) began = JsonText(head, @"""operation"":""enqueue"",[^\n]{0,200}?""content"":", false);
        same = began.Length > 0 ? "began " + Tidy(began) : "";
        return Tidy(title.Length > 0 ? title : began);
    }

    public static string CompletedTurn(string record) {
        string completedTurn = "";
        var reader = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        foreach (string line in (record ?? "").Split('\n')) {
            if (!line.StartsWith("{", StringComparison.Ordinal) || line.Length > 1024 * 1024) continue;
            try {
                var row = reader.Deserialize<Dictionary<string, object>>(line); object value;
                var payload = row.TryGetValue("payload", out value) ? value as Dictionary<string, object> : null;
                if (payload == null) continue;
                string type = Get(payload, "type");
                if (Get(row, "type") == "event_msg") {
                    if (type == "task_started" || type == "turn_aborted") completedTurn = "";
                    else if (type == "task_complete") completedTurn = Get(payload, "turn_id");
                } else if (Get(row, "type") == "response_item" && type == "message" && Get(payload, "role") == "user") completedTurn = "";
            } catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
        return completedTurn;
    }

    sealed class Collector {
        const int Chars = 128 * 1024;
        public readonly List<Dictionary<string, object>> Messages = new List<Dictionary<string, object>>();
        public readonly Dictionary<string, Dictionary<string, object>> Steps = new Dictionary<string, Dictionary<string, object>>();
        public bool Woken;
        public long Began;

        public void Add(string role, string text) {
            text = role == "tool" ? Regex.Replace(text ?? "", @"\s+", " ").Trim() : (text ?? "").Trim();
            if (text.Length == 0) return;
            int room = role == "tool" ? 1500 : Chars;
            bool truncated = text.Length > room;
            if (text.Length > room) text = text.Substring(0, Char.IsHighSurrogate(text[room - 2]) ? room - 2 : room - 1) + "…";
            Messages.Add(new Dictionary<string, object> { { "role", role }, { "text", text } });
            if (truncated) Messages[Messages.Count - 1]["truncated"] = true;
        }
        public static string Brief(object name, object given) {
            var input = given as Dictionary<string, object>; string about = "";
            foreach (string key in new[] { "description", "toolSummary", "command", "CommandLine", "file_path", "path", "Path", "pattern", "query", "url" }) {
                about = At(input, key) as string ?? "";
                if (about.Length > 0) break;
            }
            return (Convert.ToString(name) + (about.Length > 0 ? "：" + (about.Length > 160 ? about.Substring(0, 160) : about) : "")).Trim();
        }
        public Dictionary<string, object> Step(string text, string detail) {
            int before = Messages.Count;
            Add("tool", text);
            if (Messages.Count == before) return null;
            var made = Messages[Messages.Count - 1];
            detail = (detail ?? "").Trim();
            if (detail.Length > 0 && text.IndexOf(detail.Length > 40 ? detail.Substring(0, 40) : detail, StringComparison.Ordinal) < 0)
                made["detail"] = detail.Length > 400 ? detail.Substring(0, 400) + "…" : detail;
            return made;
        }
        public static void Came(Dictionary<string, object> made, string back) {
            string first = First(back);
            if (made != null && first.Length > 0) made["result"] = first;
        }
        public void Stamped(Dictionary<string, object> written) {
            DateTime when;
            string raw = (At(written, "timestamp") as string) ?? (At(written, "created_at") as string);
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when)) Began = Milliseconds(when);
        }
    }

    static void ParseAgy(Collector c, Dictionary<string, object> row, string type) {
        if (type == "USER_INPUT") {
            string text = Antigravity.ExtractUserRequest(At(row, "content") as string);
            if (text.Length > 0) { c.Add("user", text); c.Stamped(row); }
        } else if (type == "PLANNER_RESPONSE") {
            string thinking = At(row, "thinking") as string;
            if (!String.IsNullOrWhiteSpace(thinking)) c.Add("thought", thinking);
            var calls = At(row, "tool_calls") as IEnumerable;
            if (calls != null && !(At(row, "tool_calls") is string)) {
                foreach (object one in calls) {
                    var call = one as Dictionary<string, object>;
                    var args = At(call, "args") as Dictionary<string, object>;
                    string name = At(call, "name") as string;
                    if (!String.IsNullOrEmpty(name)) c.Step(Collector.Brief(name, args), At(args, "CommandLine") as string ?? At(args, "Path") as string);
                }
            }
            string reply = At(row, "content") as string;
            if (!String.IsNullOrWhiteSpace(reply)) c.Add("assistant", reply);
        }
    }

    static void ParseClaude(Collector c, Dictionary<string, object> row, string type) {
        var message = At(row, "message") as Dictionary<string, object>;
        if (type == "attachment") {
            // What a hook handed the conversation as added context: the phone's words, where they came that
            // way, when it stopped or between two of its steps.
            var attached = At(row, "attachment") as Dictionary<string, object>;
            object contextValue = At(attached, "content");
            var handed = contextValue is string ? new object[] { contextValue } : contextValue as IEnumerable;
            string hookName = At(attached, "hookName") as string ?? "";
            if (handed == null || !"hook_additional_context".Equals(At(attached, "type")) || !(hookName == "Stop" || hookName.StartsWith("Stop:", StringComparison.Ordinal) || "Stop".Equals(At(attached, "hookEvent")) || Reach.Step.Equals(At(attached, "hookEvent")))) return;
            foreach (object one in handed) {
                string context = one as string ?? ""; int from = context.IndexOf("\n\n", StringComparison.Ordinal);
                if (from < 0 || context.IndexOf("from their phone", StringComparison.Ordinal) < 0 || !Reach.Names.Any(name => context.IndexOf(name, StringComparison.Ordinal) >= 0)) continue;
                c.Add("user", context.Substring(from + 2)); c.Woken = false; c.Stamped(row);
            }
            return;
        }
        if ((type != "user" && type != "assistant") || true.Equals(At(row, "isSidechain")) || message == null) return;
        if (true.Equals(At(row, "isMeta"))) {
            // What Claude Code notes for itself is left out, but for what the phone said: it reached the
            // conversation through a hook or as a message from another session, and is the user's all the same.
            var origin = At(row, "origin") as Dictionary<string, object>;
            string noted = At(message, "content") as string ?? "", body = At(origin, "body") as string ?? noted;
            if (type != "user" || body.IndexOf("from their phone", StringComparison.Ordinal) < 0 || !Reach.Names.Any(name => body.IndexOf(name, StringComparison.Ordinal) >= 0)) return;
            if (Reach.Names.Any(name => body.StartsWith(name + " wake-up", StringComparison.Ordinal))) { c.Woken = true; return; }       // only woke it: the words follow
            int words = body.IndexOf("\n\n", StringComparison.Ordinal);
            if (words >= 0) { c.Add("user", body.Substring(words + 2)); c.Woken = false; c.Stamped(row); }
            return;
        }
        if (c.Woken && type == "assistant") return;         // what it answered to being woken, before the words reached it
        c.Woken = false;
        object content = At(message, "content");
        IEnumerable parts = content is string ? new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", content } } } : content as IEnumerable;
        if (parts == null) return;
        foreach (object item in parts) {
            var part = item as Dictionary<string, object>; string kind = At(part, "type") as string, text = At(part, "text") as string;
            if (kind == "text" && text != null) { if (type == "assistant" || !text.TrimStart().StartsWith("<", StringComparison.Ordinal)) { c.Add(type, text); if (type == "user") c.Stamped(row); } }
            else if (kind == "tool_use" && type == "assistant") {
                var made = c.Step(Collector.Brief(At(part, "name"), At(part, "input")), At(At(part, "input") as Dictionary<string, object>, "command") as string);
                string id = At(part, "id") as string;
                if (made != null && !String.IsNullOrEmpty(id)) c.Steps[id] = made;
            }
            else if (kind == "tool_result" && type == "user") {
                Dictionary<string, object> asked; string id = At(part, "tool_use_id") as string ?? "";
                if (!c.Steps.TryGetValue(id, out asked)) continue;
                object back = At(part, "content");
                Collector.Came(asked, back as string ?? (back is IEnumerable ? String.Join("\n", ((IEnumerable)back).Cast<object>().Select(one => At(one as Dictionary<string, object>, "text") as string ?? "")) : ""));
            }
            // what it says of its work between the steps, where Claude Code keeps a summary of it
            else if (kind == "thinking" && type == "assistant") c.Add("thought", At(part, "thinking") as string);
        }
    }

    static void ParseCodex(Collector c, Dictionary<string, object> row, string type, bool itemized) {
        var payload = At(row, "payload") as Dictionary<string, object>;
        if (payload == null) return;
        string kind = At(payload, "type") as string, role = At(payload, "role") as string;
        if (type == "event_msg" && kind == "item_completed") {
            var item = At(payload, "item") as Dictionary<string, object>; string what = At(item, "type") as string;
            if (what == "CommandExecution") {
                var parsed = (At(item, "parsed_cmd") as IEnumerable ?? new object[0]).Cast<object>().Select(one => one as Dictionary<string, object>).Where(one => one != null).ToList();
                var reads = parsed.Where(one => "read".Equals(At(one, "type"))).Select(one => At(one, "name") as string ?? At(one, "path") as string ?? "").Where(one => one.Length > 0).ToList();
                string ran = parsed.Select(one => At(one, "cmd") as string ?? "").FirstOrDefault(one => one.Length > 0)
                    ?? String.Join(" ", (At(item, "command") as IEnumerable ?? new object[0]).Cast<object>().Select(Convert.ToString));
                if (parsed.Count > 0 && reads.Count == parsed.Count) c.Add("tool", "读取：" + String.Join("、", reads.Take(3)) + (reads.Count > 3 ? " 等 " + reads.Count + " 个文件" : ""));
                else if (ran.Length > 0) {
                    string code = Convert.ToString(At(item, "exit_code") ?? "");
                    Collector.Came(c.Step("运行：" + (ran.Length > 160 ? ran.Substring(0, 160) : ran), ran.Length > 160 ? ran : ""), (code.Length > 0 && code != "0" ? "（退出码 " + code + "）\n" : "") + (At(item, "aggregated_output") as string ?? ""));
                }
            } else if (what == "FileChange") {
                var changed = At(item, "changes") as Dictionary<string, object>;
                var names = changed == null ? new List<string>() : changed.Keys.Select(one => one.TrimEnd('/', '\\')).Select(one => one.Substring(one.LastIndexOfAny(new[] { '/', '\\' }) + 1)).Where(one => one.Length > 0).ToList();
                if (names.Count > 0) c.Add("tool", "修改：" + String.Join("、", names.Take(3)) + (names.Count > 3 ? " 等 " + names.Count + " 个文件" : ""));
            }
            return;
        }
        if (type != "response_item") return;
        if (kind == "message" && (role == "user" || role == "assistant")) {
            var parts = At(payload, "content") as IEnumerable;
            if (parts == null || At(payload, "content") is string) return;
            foreach (object item in parts) {
                string text = At(item as Dictionary<string, object>, "text") as string;
                if (text != null && (role == "assistant" || !text.TrimStart().StartsWith("<", StringComparison.Ordinal))) { c.Add(role, text); if (role == "user") c.Stamped(row); }
            }
        }
        else if (kind == "function_call" || kind == "custom_tool_call" || kind == "local_shell_call") {
            string called = At(payload, "name") as string ?? "shell";
            if (!(itemized && (kind == "local_shell_call" || called == "exec" || called == "shell" || called == "local_shell"))) c.Add("tool", Collector.Brief(called, null));
        }
        else if (kind == "reasoning") {
            // the summary Codex keeps of its reasoning: what it says of its work between the steps
            var summary = At(payload, "summary") as IEnumerable;
            if (summary != null && !(At(payload, "summary") is string)) c.Add("thought", String.Join("\n\n", summary.Cast<object>().Select(one => At(one as Dictionary<string, object>, "text") as string).Where(one => !String.IsNullOrWhiteSpace(one))));
        }
    }

    // The last things said in a saved conversation, oldest first, for the phone to look at before anything is done to
    // the program that has it open: what the person asked, what the tool answered, and the name of each thing it did.
    public static Dictionary<string, object> Said(string path, string tool) {
        const int limit = 4 * 1024 * 1024, most = 40, runs = 30;
        var reader = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        var c = new Collector();
        string read = ReadPart(path, true, limit);
        // Codex notes each step for its own window as it ends (the command that ran, the files changed). Where it
        // does, the steps are told by those, and the calls that only wrap them ("exec", "shell") are passed over.
        bool itemized = tool == "codex" && read.IndexOf("\"item_completed\"", StringComparison.Ordinal) >= 0;
        foreach (string line in read.Split('\n')) {
            if (!line.StartsWith("{", StringComparison.Ordinal)) continue;
            // What a tool gave back fills most of a long conversation: of such a row only the beginning is taken, unread.
            if (tool != "agy" && line.Length > 4096 && line.IndexOf(tool == "claude" ? "\"type\":\"tool_result\"" : "\"type\":\"function_call_output\"", 0, 1200, StringComparison.Ordinal) >= 0) {
                if (tool == "claude") {
                    var whose = Regex.Match(line.Substring(0, 1200), "\"tool_use_id\":\"([^\"]+)\"");
                    Dictionary<string, object> asked;
                    if (whose.Success && c.Steps.TryGetValue(whose.Groups[1].Value, out asked)) Collector.Came(asked, Begins(line, line.IndexOf("\"type\":\"tool_result\"", StringComparison.Ordinal)));
                }
                continue;
            }
            if (tool == "agy" && line.IndexOf("\"type\":\"GENERIC\"", 0, Math.Min(line.Length, 200), StringComparison.Ordinal) >= 0) continue;
            Dictionary<string, object> row;
            try { row = reader.Deserialize<Dictionary<string, object>>(line); } catch (Exception) { continue; }
            string type = At(row, "type") as string;
            if (tool == "agy") ParseAgy(c, row, type);
            else if (tool == "claude") ParseClaude(c, row, type);
            else ParseCodex(c, row, type, itemized);
        }
        var file = new FileInfo(path);
        var kept = Talk(c.Messages, most, runs);
        // Keep complete normal replies while bounding the relay payload, including JSON escaping.
        // Drop older turns before shortening a reply; exceptionally large single blocks are explicitly marked.
        int letters = kept.Sum(m => Convert.ToString(m["text"]).Length);
        var sizes = kept.Select(m => Encoding.UTF8.GetByteCount(reader.Serialize(m)) + 1).ToArray();
        int bytes = sizes.Sum(), removed = 0;
        while (removed < kept.Count - 1 && (letters > 256 * 1024 || bytes > 3 * 1024 * 1024)) {
            letters -= Convert.ToString(kept[removed]["text"]).Length; bytes -= sizes[removed++];
        }
        if (removed > 0) kept.RemoveRange(0, removed);
        int withResult = 0;
        for (int i = kept.Count - 1; i >= 0; i--) if (kept[i].ContainsKey("result") && ++withResult > Results) kept[i].Remove("result");
        return new Dictionary<string, object> { { "messages", kept }, { "more", kept.Count < c.Messages.Count || file.Length > limit }, { "updated", Milliseconds(file.LastWriteTimeUtc) }, { "asked", c.Began } };
    }

    /// The first lines of what came back from a step, as a terminal shows them: six lines at most, none longer than
    /// two hundred letters, and a mark where there is more.
    public static string First(string back) {
        back = (back ?? "").Replace("\r\n", "\n").TrimStart('\n', '\r').TrimEnd();
        if (back.Length == 0) return "";
        var lines = back.Split('\n'); var kept = new List<string>(); int letters = 0;
        foreach (string one in lines) {
            if (kept.Count == 6 || letters > 500) break;
            string cut = one.TrimEnd();
            if (cut.Length > 200) cut = cut.Substring(0, Char.IsHighSurrogate(cut[199]) ? 199 : 200) + "…";
            kept.Add(cut); letters += cut.Length;
        }
        return String.Join("\n", kept) + (kept.Count < lines.Length ? "\n…" : "");
    }

    /// The beginning of what a tool gave back, taken from a row without reading the row: the text that follows
    /// `"content":` after `from` (a string, or the first text of a list), as far as a few hundred letters go.
    public static string Begins(string row, int from) {
        if (from < 0) return "";
        int at = row.IndexOf("\"content\":", from, StringComparison.Ordinal);
        if (at < 0) return "";
        at += 10;
        if (at < row.Length && row[at] == '[') { at = row.IndexOf("\"text\":\"", at, StringComparison.Ordinal); if (at < 0 || at > from + 400) return ""; at += 7; }
        if (at >= row.Length || row[at] != '"') return "";
        var text = new StringBuilder();
        for (int i = at + 1; i < row.Length && text.Length < 1600; i++) {
            char c = row[i];
            if (c == '"') break;
            if (c != '\\') { text.Append(c); continue; }
            if (++i >= row.Length) break;
            switch (row[i]) {
                case 'n': text.Append('\n'); break;
                case 't': text.Append('\t'); break;
                case 'r': break;
                case 'b': case 'f': break;
                case 'u':
                    int code;
                    if (i + 4 < row.Length && Int32.TryParse(row.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) { text.Append((char)code); i += 4; }
                    break;
                default: text.Append(row[i]); break;
            }
        }
        return text.ToString();
    }

    /// The last `most` things said by the user and the tool, oldest first, with what the tool did between them.
    public static List<Dictionary<string, object>> Talk(List<Dictionary<string, object>> said, int most, int runs) {
        var kept = new List<Dictionary<string, object>>();
        int talk = 0, run = 0, passed = 0;
        Action earlier = () => { if (passed > 0) kept.Add(new Dictionary<string, object> { { "role", "tool" }, { "text", "更早的 " + passed + " 个操作" } }); passed = 0; };
        for (int i = said.Count - 1; i >= 0; i--) {
            if ((string)said[i]["role"] == "tool") { if (++run > runs) { passed++; continue; } }
            else { earlier(); run = 0; if (++talk > most) break; }
            kept.Add(said[i]);
        }
        earlier();
        kept.Reverse();
        return kept;
    }
}
}
