using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// What the owner has said of single conversations from the phone: a pinned one stands first in the phone's lists,
/// a hidden one is left out of them. The computer keeps this, in one small file of its data folder, so that every
/// phone says the same and nothing is lost when the computer is reached by another line. A conversation is named by
/// its tool and its own id; nothing else of it is kept here.
public sealed class Marks {
    public static readonly string[] Kinds = { "pinned", "hidden" };
    public const int Most = 300;        // of each kind; with more, the ones marked longest ago give way
    readonly string file;
    readonly object gate = new object();
    readonly Dictionary<string, List<string>> kept = new Dictionary<string, List<string>>();

    public Marks(string file) {
        this.file = file;
        foreach (string kind in Kinds) kept[kind] = new List<string>();
        try {
            if (!File.Exists(file)) return;
            var read = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            foreach (string kind in Kinds) {
                object list;
                if (read == null || !read.TryGetValue(kind, out list) || list is string || !(list is IEnumerable)) continue;
                foreach (string key in ((IEnumerable)list).OfType<string>().Where(key => key.Length > 0 && key.Length <= 80).Distinct().Take(Most)) kept[kind].Add(key);
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }      // a file that cannot be read marks nothing
    }
    static string Key(string tool, string session) { return tool + ":" + session; }
    public bool Is(string kind, string tool, string session) { lock (gate) return kept[kind].Contains(Key(tool, session)); }
    /// Says that a conversation is of a kind, or no longer is. Returns whether that changed anything.
    public bool Set(string kind, string tool, string session, bool on) {
        if (Array.IndexOf(Kinds, kind) < 0) throw new ArgumentException("没有这种标记");
        lock (gate) {
            List<string> list = kept[kind]; string key = Key(tool, session);
            if (list.Contains(key) == on) return false;
            if (on) { list.Add(key); if (list.Count > Most) list.RemoveRange(0, list.Count - Most); } else list.Remove(key);
            string temporary = file + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(kept.ToDictionary(pair => pair.Key, pair => (object)pair.Value.ToArray())), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
            return true;
        }
    }
}
}
