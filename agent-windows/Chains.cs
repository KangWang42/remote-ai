using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// Work passed on from one conversation to another when the first has done its part: "when this one stops, give
/// what it said last to that one, with these words of mine". So one tool can write and another review, and with a
/// second rule the other way round the two go back and forth. Each rule carries how many times it may still run,
/// so that two conversations cannot keep each other going without end. This keeps the rules (in one file of the
/// data folder, the words encrypted for the Windows account); the agent does the passing on.
public sealed class Chains {
    public const int Most = 20, MostRounds = 10;
    public sealed class Item {
        public string Id = "", FromTool = "", From = "", FromTitle = "", ToTool = "", To = "", ToTitle = "", Text = "", LastNote = "";
        public int Left, Rounds, Runs;          // how many times it may still run, of how many it was set for; how often it ran
        public long LastAt;
        public bool LastOk;
        internal Item Copy() { return (Item)MemberwiseClone(); }
    }
    readonly string file;
    readonly object gate = new object();
    readonly List<Item> items = new List<Item>();
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    public event Action Changed;
    const string Uuid = @"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z";

    public Chains(string file = null) {
        this.file = file;
        if (file == null || !File.Exists(file)) return;
        try {
            foreach (object one in (System.Collections.IEnumerable)json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8))) {
                var kept = one as Dictionary<string, object>;
                if (kept == null) continue;
                Func<string, string> word = name => { object value; return kept.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; };
                Func<string, long> number = name => { long value; return Int64.TryParse(word(name), out value) ? value : 0; };
                var item = new Item { Id = word("id"), FromTool = word("from_tool"), From = word("from"), FromTitle = word("from_title"), ToTool = word("to_tool"), To = word("to"), ToTitle = word("to_title"),
                    Left = (int)number("left"), Rounds = (int)number("rounds"), Runs = (int)number("runs"), LastAt = number("last_at"), LastOk = word("last_ok") == "True", LastNote = word("last_note") };
                try { item.Text = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(word("text")), null, DataProtectionScope.CurrentUser)); }
                catch (FormatException) { continue; } catch (CryptographicException) { continue; }
                if (Regex.IsMatch(item.Id, @"\A[a-f0-9]{16,32}\z")) items.Add(item);
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (InvalidCastException) { }
    }
    void Save() {
        if (file == null) return;
        try {
            var kept = items.Select(item => new Dictionary<string, object> { { "id", item.Id }, { "from_tool", item.FromTool }, { "from", item.From }, { "from_title", item.FromTitle }, { "to_tool", item.ToTool }, { "to", item.To },
                { "to_title", item.ToTitle }, { "left", item.Left }, { "rounds", item.Rounds }, { "runs", item.Runs }, { "last_at", item.LastAt }, { "last_ok", item.LastOk }, { "last_note", item.LastNote },
                { "text", Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(item.Text), null, DataProtectionScope.CurrentUser)) } }).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string fresh = file + ".tmp";
            File.WriteAllText(fresh, json.Serialize(kept), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(fresh, file, null); else File.Move(fresh, file);
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (CryptographicException) { }
    }
    void Tell() { var told = Changed; if (told != null) { try { told(); } catch { } } }

    public Item[] All() { lock (gate) return items.Select(item => item.Copy()).ToArray(); }
    /// As the phone is told of one.
    public static Dictionary<string, object> Shown(Item item) {
        return new Dictionary<string, object> { { "id", item.Id }, { "from", item.From }, { "from_tool", item.FromTool }, { "from_title", item.FromTitle }, { "to", item.To }, { "to_tool", item.ToTool }, { "to_title", item.ToTitle },
            { "text", item.Text }, { "left", item.Left }, { "rounds", item.Rounds }, { "runs", item.Runs }, { "last", item.LastAt == 0 ? null : new Dictionary<string, object> { { "at", item.LastAt }, { "ok", item.LastOk }, { "note", item.LastNote } } } };
    }
    /// Sets one: a new one under a new number, or the one of that number anew (its rounds begin again).
    public Item Set(IDictionary<string, object> given) {
        Func<string, string> word = name => { object value; return given.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; };
        int rounds; Int32.TryParse(word("rounds"), out rounds);
        var item = new Item { Id = word("id"), FromTool = word("from_tool"), From = word("from"), FromTitle = word("from_title"), ToTool = word("to_tool"), To = word("to"), ToTitle = word("to_title"), Text = word("text").Trim(), Rounds = rounds, Left = rounds };
        if (!Regex.IsMatch(item.Id, @"\A[a-f0-9]{16,32}\z")) throw new ArgumentException("编号无效");
        foreach (string tool in new[] { item.FromTool, item.ToTool }) if (tool != "claude" && tool != "codex") throw new ArgumentException("工具无效");
        if (!Regex.IsMatch(item.From, Uuid) || !Regex.IsMatch(item.To, Uuid)) throw new ArgumentException("请选择两段对话");
        if (item.FromTool == item.ToTool && String.Equals(item.From, item.To, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("不能接力给同一段对话");
        if (item.Text.Length == 0 || item.Text.Length > 1000) throw new ArgumentException("要说的话需为 1 至 1000 字");
        if (rounds < 1 || rounds > MostRounds) throw new ArgumentException("次数需在 1 到 " + MostRounds + " 之间");
        if (item.FromTitle.Length > 80) item.FromTitle = item.FromTitle.Substring(0, 80);
        if (item.ToTitle.Length > 80) item.ToTitle = item.ToTitle.Substring(0, 80);
        lock (gate) {
            int known = items.FindIndex(one => one.Id == item.Id);
            if (known < 0 && items.Count >= Most) throw new InvalidOperationException("最多保存 " + Most + " 条接力");
            if (known >= 0) items[known] = item; else items.Add(item);
            Save();
        }
        Tell();
        return item.Copy();
    }
    public bool Remove(string id) {
        bool gone;
        lock (gate) { gone = items.RemoveAll(item => item.Id == id) > 0; if (gone) Save(); }
        if (gone) Tell();
        return gone;
    }
    /// The rules that run now that a conversation has stopped. Each has one run taken off at once, so that it
    /// runs once for this stop however long the passing on takes.
    public Item[] After(string tool, string session) {
        var run = new List<Item>();
        lock (gate) {
            foreach (Item item in items.Where(one => one.Left > 0 && one.FromTool == tool && String.Equals(one.From, session, StringComparison.OrdinalIgnoreCase))) { item.Left--; run.Add(item.Copy()); }
            if (run.Count > 0) Save();
        }
        if (run.Count > 0) Tell();
        return run.ToArray();
    }
    public void Done(string id, bool ok, string note) {
        lock (gate) {
            Item item = items.FirstOrDefault(one => one.Id == id);
            if (item == null) return;
            item.Runs++; item.LastAt = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; item.LastOk = ok; item.LastNote = (note ?? "").Length > 200 ? note.Substring(0, 200) : note ?? "";
            Save();
        }
        Tell();
    }
}
}
