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
/// Questions the owner set to be asked at a time: once, every day at an hour, or every so many minutes. Each
/// names the conversation it is for and the words. This keeps them (in one file of the data folder, the words
/// encrypted for the Windows account), says which are due, and notes how each asking went; the agent does the
/// asking, by the way it hands the phone's own messages to a conversation.
public sealed class Schedules {
    public const string Once = "once", Daily = "daily", Every = "every";
    public const int Most = 30;
    public sealed class Item {
        public string Id = "", Tool = "", Session = "", Title = "", Text = "", Kind = Once, Time = "";      // Title: the conversation's, as it was when this was set
        public long At, Next, LastAt;           // milliseconds since 1970: when a single one is due; when it is due next; when it was last asked
        public int Minutes, Runs;
        public bool On = true, LastOk;
        public string LastNote = "";
        internal Item Copy() { return (Item)MemberwiseClone(); }
    }
    readonly string file;
    readonly object gate = new object();
    readonly List<Item> items = new List<Item>();
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    public event Action Changed;

    static long Ms(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    static DateTime Utc(long ms) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms); }

    public Schedules(string file = null) {
        this.file = file;
        if (file == null || !File.Exists(file)) return;
        try {
            foreach (object one in (System.Collections.IEnumerable)json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8))) {
                var kept = one as Dictionary<string, object>;
                if (kept == null) continue;
                Func<string, string> word = name => { object value; return kept.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; };
                Func<string, long> number = name => { long value; return Int64.TryParse(word(name), out value) ? value : 0; };
                var item = new Item { Id = word("id"), Tool = word("tool"), Session = word("session"), Title = word("title"), Kind = word("kind"), Time = word("time"), At = number("at"), Next = number("next"),
                    LastAt = number("last_at"), Minutes = (int)number("minutes"), Runs = (int)number("runs"), On = word("on") == "True", LastOk = word("last_ok") == "True", LastNote = word("last_note") };
                try { item.Text = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(word("text")), null, DataProtectionScope.CurrentUser)); }
                catch (FormatException) { continue; } catch (CryptographicException) { continue; }
                if (Regex.IsMatch(item.Id, @"\A[a-f0-9]{16,32}\z")) items.Add(item);
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (InvalidCastException) { }
    }
    void Save() {
        if (file == null) return;
        try {
            var kept = items.Select(item => new Dictionary<string, object> { { "id", item.Id }, { "tool", item.Tool }, { "session", item.Session }, { "title", item.Title }, { "kind", item.Kind }, { "time", item.Time },
                { "at", item.At }, { "next", item.Next }, { "last_at", item.LastAt }, { "minutes", item.Minutes }, { "runs", item.Runs }, { "on", item.On }, { "last_ok", item.LastOk }, { "last_note", item.LastNote },
                { "text", Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(item.Text), null, DataProtectionScope.CurrentUser)) } }).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string fresh = file + ".tmp";
            File.WriteAllText(fresh, json.Serialize(kept), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(fresh, file, null); else File.Move(fresh, file);
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (CryptographicException) { }
    }
    void Tell() { var told = Changed; if (told != null) { try { told(); } catch { } } }

    /// When an item is due next after `now`: 0 for a single one whose time has passed, or one that is off.
    public static long NextOf(Item item, DateTime now) {
        if (!item.On) return 0;
        if (item.Kind == Once) return item.At > Ms(now.ToUniversalTime()) ? item.At : 0;
        if (item.Kind == Every) return Ms(now.ToUniversalTime().AddMinutes(item.Minutes));
        DateTime local = now.ToLocalTime(), at = local.Date.Add(TimeSpan.ParseExact(item.Time, @"hh\:mm", CultureInfo.InvariantCulture));
        if (at <= local) at = at.AddDays(1);
        return Ms(at.ToUniversalTime());
    }

    public Item[] All() { lock (gate) return items.Select(item => item.Copy()).ToArray(); }
    /// As the phone is told of one.
    public static Dictionary<string, object> Shown(Item item) {
        return new Dictionary<string, object> { { "id", item.Id }, { "tool", item.Tool }, { "session", item.Session }, { "title", item.Title }, { "text", item.Text }, { "kind", item.Kind }, { "time", item.Time },
            { "at", item.At }, { "minutes", item.Minutes }, { "on", item.On }, { "next", item.Next }, { "runs", item.Runs }, { "last", item.LastAt == 0 ? null : new Dictionary<string, object> { { "at", item.LastAt }, { "ok", item.LastOk }, { "note", item.LastNote } } } };
    }

    /// Sets one: a new one under a new number, or the one of that number anew. Says in words what is wrong with it.
    public Item Set(IDictionary<string, object> given, DateTime now) {
        Func<string, string> word = name => { object value; return given.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; };
        var item = new Item { Id = word("id"), Tool = word("tool"), Session = word("session"), Title = word("title"), Text = word("text").Trim(), Kind = word("kind"), Time = word("time"), On = word("on") != "False" };
        long at; int minutes;
        Int64.TryParse(word("at"), out at); Int32.TryParse(word("minutes"), out minutes);
        item.At = at; item.Minutes = minutes;
        if (item.Title.Length > 80) item.Title = item.Title.Substring(0, 80);
        if (!Regex.IsMatch(item.Id, @"\A[a-f0-9]{16,32}\z")) throw new ArgumentException("编号无效");
        if (item.Tool != "claude" && item.Tool != "codex") throw new ArgumentException("工具无效");
        if (!Regex.IsMatch(item.Session, @"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z")) throw new ArgumentException("请选择要提问的对话");
        if (item.Text.Length == 0 || item.Text.Length > 2000) throw new ArgumentException("要问的话需为 1 至 2000 字");
        TimeSpan time;
        if (item.Kind == Once) { if (item.On && (item.At <= Ms(now.ToUniversalTime()) || item.At > Ms(now.ToUniversalTime().AddDays(366)))) throw new ArgumentException("时间需在现在之后、一年之内"); }
        else if (item.Kind == Daily) { if (!TimeSpan.TryParseExact(item.Time, @"hh\:mm", CultureInfo.InvariantCulture, out time)) throw new ArgumentException("每天的时间需写成 09:30 这样"); }
        else if (item.Kind == Every) { if (item.Minutes < 5 || item.Minutes > 10080) throw new ArgumentException("间隔需在 5 分钟到 7 天之间"); }
        else throw new ArgumentException("重复方式无效");
        item.Next = NextOf(item, now);
        lock (gate) {
            int known = items.FindIndex(one => one.Id == item.Id);
            if (known < 0 && items.Count >= Most) throw new InvalidOperationException("最多保存 " + Most + " 个定时提问");
            if (known >= 0) { item.Runs = items[known].Runs; item.LastAt = items[known].LastAt; item.LastOk = items[known].LastOk; item.LastNote = items[known].LastNote; items[known] = item; }
            else items.Add(item);
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
    /// What is to be asked now. Each is moved on to its next time at once (a single one is switched off), so that
    /// it is asked once however long the asking takes.
    public Item[] Due(DateTime now) {
        var due = new List<Item>();
        lock (gate) {
            long at = Ms(now.ToUniversalTime());
            foreach (Item item in items.Where(one => one.On && one.Next > 0 && one.Next <= at)) {
                due.Add(item.Copy());
                if (item.Kind == Once) { item.On = false; item.Next = 0; } else item.Next = NextOf(item, now);
            }
            if (due.Count > 0) Save();
        }
        if (due.Count > 0) Tell();
        return due.ToArray();
    }
    /// How an asking went, for its owner to read.
    public void Done(string id, bool ok, string note, DateTime now) {
        lock (gate) {
            Item item = items.FirstOrDefault(one => one.Id == id);
            if (item == null) return;
            item.Runs++; item.LastAt = Ms(now.ToUniversalTime()); item.LastOk = ok; item.LastNote = (note ?? "").Length > 200 ? note.Substring(0, 200) : note ?? "";
            Save();
        }
        Tell();
    }
}
}
