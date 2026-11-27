using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// A conversation that another AI goes on with is one conversation to the owner: the phone switched its AI, the
/// computer began a conversation of the new tool, told it what had been done (`brief`) together with the owner's next
/// message, and keeps here which conversation it goes on from. The lists show only the latest of such a line, under
/// the name of its first; going on with it shows what was said before in every one of them, oldest first.
/// One small file of the data folder; a conversation is named by its tool and its own id.
public sealed class Handovers {
    public const int Most = 500;
    public sealed class Link { public string Tool, Session, FromTool, From, Title; }
    readonly string file;
    readonly object gate = new object();
    readonly List<Link> links = new List<Link>();

    /// The line that sets the owner's next message apart from the hand-over summary before it.
    public const string AskMarker = "【现在的要求】";
    /// Returns the owner's own words from a hand-over opening message, or `text` unchanged where it is not one.
    public static string StripAsk(string text) {
        if (String.IsNullOrEmpty(text) || !text.StartsWith("【对话交接】", StringComparison.Ordinal)) return text ?? "";
        string sep = "\n\n" + AskMarker + "\n";
        int at = text.IndexOf(sep, StringComparison.Ordinal);
        return at >= 0 ? text.Substring(at + sep.Length) : text;
    }

    public Handovers(string file) {
        this.file = file;
        try {
            if (!File.Exists(file)) return;
            var read = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            object list;
            if (read == null || !read.TryGetValue("links", out list) || list is string || !(list is IEnumerable)) return;
            foreach (var one in ((IEnumerable)list).OfType<Dictionary<string, object>>()) {
                var link = new Link { Tool = Text(one, "tool"), Session = Text(one, "session"), FromTool = Text(one, "from_tool"), From = Text(one, "from"), Title = Text(one, "title") };
                if (Valid(link)) links.Add(link);
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }      // a file that cannot be read links nothing
    }
    static string Text(Dictionary<string, object> from, string key) { object v; return from.TryGetValue(key, out v) ? Convert.ToString(v) ?? "" : ""; }
    static bool Valid(Link l) {
        return l.Tool.Length > 0 && l.Tool.Length <= 20 && l.FromTool.Length > 0 && l.FromTool.Length <= 20 && l.Session.Length > 0 && l.Session.Length <= 128 && l.From.Length > 0 && l.From.Length <= 128
            && !(l.Tool == l.FromTool && l.Session == l.From);
    }
    static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    /// Says that `tool`/`session` goes on from `fromTool`/`from`, which was called `title`.
    public void Add(string tool, string session, string fromTool, string from, string title) {
        var link = new Link { Tool = tool ?? "", Session = session ?? "", FromTool = fromTool ?? "", From = from ?? "", Title = (title ?? "").Trim() };
        if (!Valid(link)) return;
        lock (gate) {
            if (links.Any(l => l.Tool == link.Tool && Same(l.Session, link.Session))) return;
            // (a line never closes on itself: the one gone on from may not already go on from this one)
            if (Line(fromTool, from).Any(l => l.FromTool == tool && Same(l.From, session))) return;
            links.Add(link);
            if (links.Count > Most) links.RemoveRange(0, links.Count - Most);
            Save();
        }
    }
    void Save() {
        string temporary = file + ".tmp";
        var rows = links.Select(l => new Dictionary<string, object> { { "tool", l.Tool }, { "session", l.Session }, { "from_tool", l.FromTool }, { "from", l.From }, { "title", l.Title } }).ToArray();
        File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "links", rows } }), new UTF8Encoding(false));
        if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
    }
    /// What a conversation goes on from, nearest first (empty where it was begun by itself).
    public List<Link> Line(string tool, string session) {
        var line = new List<Link>();
        lock (gate) {
            string t = tool, s = session;
            for (int n = 0; n < 50; n++) {
                var link = links.FirstOrDefault(l => l.Tool == t && Same(l.Session, s));
                if (link == null) break;
                line.Add(link);
                t = link.FromTool; s = link.From;
            }
        }
        return line;
    }
    /// Whether another conversation goes on from this one (it is then shown as part of that one).
    public bool GoneOn(string tool, string session) { lock (gate) return links.Any(l => l.FromTool == tool && Same(l.From, session)); }
    /// The name of the line a conversation ends: that of its first conversation; "" where it goes on from none.
    public string Title(string tool, string session) {
        var line = Line(tool, session);
        return line.Count == 0 ? "" : line[line.Count - 1].Title;
    }
}
}
