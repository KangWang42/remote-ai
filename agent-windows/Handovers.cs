using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// A conversation that switches AI in place is one conversation to the owner: the computer tells the new AI what
/// happened since it last participated (or a full hand-over on first switch), remembers the ordered sequence of turns
/// across AIs, and resumes a tool's prior session when switching back to it. Lists show only the current turn's
/// session under the title of the first; reading or resuming reconstructs the full chronological timeline.
public sealed class Handovers {
    public const int Most = 500;
    public const int MostTurns = 50;
    public sealed class Turn { public string Tool, Session; }
    sealed class Thread {
        public string Title = "";
        public List<Turn> Turns = new List<Turn>();
    }
    sealed class Link { public string Tool, Session, FromTool, From, Title; }

    readonly string file;
    readonly object gate = new object();
    readonly List<Thread> threads = new List<Thread>();

    /// The line that sets the owner's next message apart from the hand-over summary before it.
    public const string AskMarker = "【现在的要求】";
    static readonly string AskSeparator = "\n\n" + AskMarker + "\n";

    /// True when `text` is an in-place hand-over opening message (`【对话交接】...【现在的要求】\n...`).
    public static bool IsHandoverMessage(string text) {
        return !String.IsNullOrEmpty(text)
            && text.StartsWith("【对话交接】", StringComparison.Ordinal)
            && text.IndexOf(AskSeparator, StringComparison.Ordinal) >= 0;
    }

    /// Returns the owner's own words from a hand-over opening message, or `text` unchanged where it is not one.
    public static string StripAsk(string text) {
        if (String.IsNullOrEmpty(text) || !text.StartsWith("【对话交接】", StringComparison.Ordinal)) return text ?? "";
        int at = text.IndexOf(AskSeparator, StringComparison.Ordinal);
        return at >= 0 ? text.Substring(at + AskSeparator.Length) : text;
    }

    public Handovers(string file) {
        this.file = file;
        try {
            if (!File.Exists(file)) return;
            var read = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            if (read == null) return;
            object list;
            if (read.TryGetValue("threads", out list) && !(list is string) && list is IEnumerable) {
                foreach (var one in ((IEnumerable)list).OfType<Dictionary<string, object>>()) {
                    var turns = new List<Turn>();
                    object rawTurns;
                    if (one.TryGetValue("turns", out rawTurns) && !(rawTurns is string) && rawTurns is IEnumerable) {
                        foreach (var item in ((IEnumerable)rawTurns).OfType<Dictionary<string, object>>()) {
                            var t = new Turn { Tool = Text(item, "tool"), Session = Text(item, "session") };
                            if (ValidEndpoint(t.Tool, t.Session)) turns.Add(t);
                        }
                    }
                    if (turns.Count < 2) continue;
                    if (turns.Count > MostTurns) turns.RemoveRange(0, turns.Count - MostTurns);
                    threads.Add(new Thread { Title = Text(one, "title").Trim(), Turns = turns });
                }
                if (threads.Count > Most) threads.RemoveRange(0, threads.Count - Most);
                return;
            }
            if (read.TryGetValue("links", out list) && !(list is string) && list is IEnumerable) {
                foreach (var one in ((IEnumerable)list).OfType<Dictionary<string, object>>()) {
                    var link = new Link { Tool = Text(one, "tool"), Session = Text(one, "session"), FromTool = Text(one, "from_tool"), From = Text(one, "from"), Title = Text(one, "title") };
                    Attach(link.Tool, link.Session, link.FromTool, link.From, link.Title);
                }
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }      // a file that cannot be read links nothing
    }

    static string Text(Dictionary<string, object> from, string key) { object v; return from.TryGetValue(key, out v) ? Convert.ToString(v) ?? "" : ""; }
    static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    static bool Match(Turn t, string tool, string session) { return t.Tool == tool && Same(t.Session, session); }
    static bool ValidEndpoint(string tool, string session) {
        return !String.IsNullOrEmpty(tool) && tool.Length <= 20 && !String.IsNullOrEmpty(session) && session.Length <= 128;
    }

    Thread FindThreadLocked(string tool, string session) {
        if (String.IsNullOrEmpty(tool) || String.IsNullOrEmpty(session)) return null;
        for (int i = threads.Count - 1; i >= 0; i--) {
            var th = threads[i];
            if (th.Turns.Count > 0 && Match(th.Turns[th.Turns.Count - 1], tool, session)) return th;
        }
        for (int i = threads.Count - 1; i >= 0; i--) {
            var th = threads[i];
            if (th.Turns.Any(t => Match(t, tool, session))) return th;
        }
        return null;
    }

    bool Attach(string tool, string session, string fromTool, string from, string title) {
        if (!ValidEndpoint(tool, session) || !ValidEndpoint(fromTool, from)) return false;
        if (tool == fromTool && Same(session, from)) return false;
        var existing = FindThreadLocked(fromTool, from);
        var targetOwner = FindThreadLocked(tool, session);
        if (targetOwner != null && targetOwner != existing) return false;
        if (existing != null) {
            var last = existing.Turns[existing.Turns.Count - 1];
            if (Match(last, tool, session)) return false;
            existing.Turns.Add(new Turn { Tool = tool, Session = session });
            if (existing.Turns.Count > MostTurns) existing.Turns.RemoveRange(0, existing.Turns.Count - MostTurns);
            threads.Remove(existing);
            threads.Add(existing);
        } else {
            threads.Add(new Thread {
                Title = (title ?? "").Trim(),
                Turns = new List<Turn> { new Turn { Tool = fromTool, Session = from }, new Turn { Tool = tool, Session = session } }
            });
        }
        if (threads.Count > Most) threads.RemoveRange(0, threads.Count - Most);
        return true;
    }

    /// Says that `tool`/`session` continues the conversation from `fromTool`/`from`, which was called `title`.
    public void Add(string tool, string session, string fromTool, string from, string title) {
        lock (gate) {
            if (Attach(tool ?? "", session ?? "", fromTool ?? "", from ?? "", title)) Save();
        }
    }

    void Save() {
        string temporary = file + ".tmp";
        var rows = threads.Select(th => new Dictionary<string, object> {
            { "title", th.Title },
            { "turns", th.Turns.Select(t => new Dictionary<string, object> { { "tool", t.Tool }, { "session", t.Session } }).ToArray() }
        }).ToArray();
        File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "threads", rows } }), new UTF8Encoding(false));
        if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
    }

    /// Ordered turns (oldest first) of the handover thread containing `tool`/`session`, or empty when standalone.
    public List<Turn> TurnsOf(string tool, string session) {
        lock (gate) {
            var th = FindThreadLocked(tool, session);
            return th == null ? new List<Turn>() : th.Turns.Select(t => new Turn { Tool = t.Tool, Session = t.Session }).ToList();
        }
    }

    /// Most recent prior session id used by `targetTool` in the same handover thread as `fromTool`/`fromSession`, or "".
    public string PreviousSession(string fromTool, string fromSession, string targetTool) {
        if (String.IsNullOrEmpty(targetTool)) return "";
        lock (gate) {
            var th = FindThreadLocked(fromTool, fromSession);
            if (th == null) return "";
            for (int i = th.Turns.Count - 1; i >= 0; i--) {
                var t = th.Turns[i];
                if (t.Tool == targetTool && !(t.Tool == fromTool && Same(t.Session, fromSession))) return t.Session;
            }
            return "";
        }
    }

    /// Whether `tool`/`session` is superseded in its handover thread (another turn follows it).
    public bool GoneOn(string tool, string session) {
        lock (gate) {
            var th = FindThreadLocked(tool, session);
            if (th == null || th.Turns.Count == 0) return false;
            return !Match(th.Turns[th.Turns.Count - 1], tool, session);
        }
    }

    /// The name of the thread a conversation belongs to: that of its first conversation; "" when standalone.
    public string Title(string tool, string session) {
        lock (gate) {
            var th = FindThreadLocked(tool, session);
            return th == null ? "" : th.Title;
        }
    }

    /// Splits a single session's saved messages into per-visit slices in chronological order, using in-place
    /// handover markers (`【对话交接】...【现在的要求】\n...`) as visit boundaries.
    public static List<List<Dictionary<string, object>>> SplitVisits(List<Dictionary<string, object>> messages, int expectedVisits, bool startsWithHandover, bool truncated) {
        var result = new List<List<Dictionary<string, object>>>();
        int count = Math.Max(1, expectedVisits);
        for (int i = 0; i < count; i++) result.Add(new List<Dictionary<string, object>>());
        if (messages == null || messages.Count == 0) return result;
        if (count == 1) {
            result[0].AddRange(messages);
            return result;
        }

        var chunks = new List<List<Dictionary<string, object>>>();
        var cur = new List<Dictionary<string, object>>();
        foreach (var m in messages) {
            if (Convert.ToString(m["role"]) == "user" && IsHandoverMessage(Convert.ToString(m["text"])) && cur.Count > 0) {
                chunks.Add(cur);
                cur = new List<Dictionary<string, object>>();
            }
            cur.Add(m);
        }
        if (cur.Count > 0) chunks.Add(cur);

        if (chunks.Count == count) {
            for (int i = 0; i < count; i++) result[i] = chunks[i];
            return result;
        }
        if (chunks.Count > count) {
            for (int i = 0; i < count - 1; i++) result[i] = chunks[i];
            for (int i = count - 1; i < chunks.Count; i++) result[count - 1].AddRange(chunks[i]);
            return result;
        }

        bool alignEnd = chunks.Count > 0 && (truncated || (!startsWithHandover
            && Convert.ToString(chunks[0][0]["role"]) == "user"
            && IsHandoverMessage(Convert.ToString(chunks[0][0]["text"]))));
        int offset = alignEnd ? count - chunks.Count : 0;
        for (int i = 0; i < chunks.Count; i++) result[offset + i] = chunks[i];
        return result;
    }
}
}
