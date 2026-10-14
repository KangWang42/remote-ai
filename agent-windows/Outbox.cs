using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// What the phone has said to conversations on this computer and they have not taken yet: one item for each
/// message, in the order they were said. Each is kept in a file of its own until it is settled, so that this
/// program starting again loses none, and each can be asked after by its number, taken back, or held while its
/// conversation is told to stop.
///
/// An item is `queued` (it waits for its conversation: the tool's hooks hand it over when they next run, or the
/// conversation is woken for it), `paused` (its conversation was told to stop: it waits for its owner to say whether
/// it is still to be sent), `sending` (reserved by a courier), `handed` (the tool has it) or `withdrawn` (its owner took it back). The words are kept
/// encrypted for this Windows account and are dropped from the file as soon as the item is settled; what stays of
/// a settled item, for a day, is its number and how it ended.
public sealed class Outbox {
    public const string Queued = "queued", Paused = "paused", Sending = "sending", Handed = "handed", Withdrawn = "withdrawn";
    public static readonly TimeSpan Waits = TimeSpan.FromDays(7), Remembered = TimeSpan.FromDays(1);

    public sealed class Item {
        public string Id = "", Tool = "", Session = "", Text = "", State = Queued;
        public DateTime At, Settled;
        public long Order;
        public bool Open { get { return State == Queued || State == Paused || State == Sending; } }
        internal Item Copy() { return (Item)MemberwiseClone(); }
    }

    readonly string folder;                 // null: kept in memory only
    readonly object gate = new object();
    readonly List<Item> items = new List<Item>();
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    long last;
    /// Something was added, handed over, held or taken back.
    public event Action Changed;

    public Outbox(string folder = null) {
        this.folder = folder;
        if (folder == null) return;
        try {
            if (!Directory.Exists(folder)) return;
            foreach (string file in Directory.GetFiles(folder, "*.json")) {
                Item item = Read(file);
                // what cannot be read, or has waited or been remembered long enough, is cleared away
                if (item == null || DateTime.UtcNow - (item.Open ? item.At : item.Settled) > (item.Open ? Waits : Remembered)) { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } continue; }
                // An interrupted courier may have delivered: never resend it automatically after a restart.
                if (item.State == Sending) { item.State = Paused; Write(item); }
                items.Add(item);
            }
            items.Sort((a, b) => a.Order.CompareTo(b.Order));
            if (items.Count > 0) last = items[items.Count - 1].Order;
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static bool Same(Item item, string tool, string session) { return item.Tool == tool && String.Equals(item.Session, session, StringComparison.OrdinalIgnoreCase); }
    void Tell() { var told = Changed; if (told != null) { try { told(); } catch { } } }

    /// A message for a conversation. Said again under the same number it is the same message, and nothing is added:
    /// the item as it stands is returned. A number that was used for other words is refused.
    public Item Add(string id, string tool, string session, string text) {
        if (!Regex.IsMatch(id ?? "", @"\A[a-f0-9]{16,32}\z")) throw new ArgumentException("操作编号无效");
        Item made;
        lock (gate) {
            Item known = items.FirstOrDefault(item => item.Id == id);
            if (known != null) {
                if (!Same(known, tool, session) || (known.Open && known.Text != text)) throw new InvalidOperationException("操作编号已被其它消息使用");
                return known.Copy();
            }
            made = new Item { Id = id, Tool = tool, Session = session, Text = text, At = DateTime.UtcNow, Order = ++last };
            items.Add(made); Write(made);
            // (settled ones that have been remembered long enough make room)
            foreach (Item old in items.Where(item => !item.Open && DateTime.UtcNow - item.Settled > Remembered).ToArray()) { items.Remove(old); Delete(old); }
            made = made.Copy();
        }
        Tell();
        return made;
    }
    /// The item of that number, or null.
    public Item Find(string id) { lock (gate) { Item known = items.FirstOrDefault(item => item.Id == id); return known == null ? null : known.Copy(); } }
    /// What waits for a conversation or is held for it, in the order it was said: what the phone shows.
    public Item[] Of(string tool, string session) { lock (gate) return items.Where(item => item.Open && Same(item, tool, session)).Select(item => item.Copy()).ToArray(); }
    /// Whether anything waits to be handed to a conversation (held ones do not).
    public bool Waiting(string tool, string session) { lock (gate) return items.Any(item => item.State == Queued && Same(item, tool, session)); }
    /// The conversations something waits for.
    public string[][] Pending() { lock (gate) return items.Where(item => item.State == Queued).Select(item => item.Tool + "\n" + item.Session.ToLowerInvariant()).Distinct().Select(key => key.Split('\n')).ToArray(); }

    Item[] Settle(Func<Item, bool> which, string from, string to) {
        Item[] changed;
        lock (gate) {
            changed = items.Where(item => (from == null ? item.Open : item.State == from) && which(item)).ToArray();
            foreach (Item item in changed) {
                item.State = to;
                if (!item.Open) item.Settled = DateTime.UtcNow;
            }
            var given = changed.Select(item => item.Copy()).ToArray();
            foreach (Item item in changed) { if (!item.Open) item.Text = ""; Write(item); }
            changed = given;
            System.Threading.Monitor.PulseAll(gate);
        }
        if (changed.Length > 0) Tell();
        return changed;
    }
    /// Everything that waits for a conversation is handed to it now: returned in order, with its words.
    public Item[] Hand(string tool, string session) { return Settle(item => Same(item, tool, session), Queued, Handed); }
    /// One item has been handed over (by another way than the hooks). False: it no longer waited.
    public bool Hand(string id) { return Settle(item => item.Id == id, Queued, Handed).Length > 0; }
    /// Reserve a message against concurrent hooks. Only a successful courier acknowledges delivery.
    public bool Deliver(string id, Action<string> send) {
        Item[] reserved = Settle(item => item.Id == id, Queued, Sending);
        if (reserved.Length == 0) return false;
        try { send(reserved[0].Text); }
        catch { Settle(item => item.Id == id, Sending, Paused); throw; }
        Settle(item => item.Id == id, Sending, Handed);
        return true;
    }
    /// The conversation is told to stop: what waited for it is held until its owner says more. How many.
    public int Pause(string tool, string session) { return Settle(item => Same(item, tool, session), Queued, Paused).Length; }
    /// What was held for a conversation waits for it again: returned in order.
    public Item[] Release(string tool, string session) { return Settle(item => Same(item, tool, session), Paused, Queued); }
    /// Its owner takes a message back. False: it was handed over already, or is not known.
    public bool Withdraw(string id) { return Settle(item => item.Id == id && item.State != Sending, null, Withdrawn).Length > 0; }
    /// An item that could not be handed over is dropped altogether, as if it had not been said. False: it does not
    /// wait any more (it was handed over, or taken back), and stays as it is.
    public bool Forget(string id) {
        lock (gate) {
            Item known = items.FirstOrDefault(item => item.Id == id);
            if (known == null || !known.Open || known.State == Sending) return false;
            items.Remove(known); Delete(known);
        }
        Tell();
        return true;
    }
    /// Waits until an item no longer waits (it was handed over, held or taken back), for up to `seconds`. False: it
    /// still waits.
    public bool Settled(string id, int seconds) {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        lock (gate) {
            for (;;) {
                Item known = items.FirstOrDefault(item => item.Id == id);
                if (known == null || known.State != Queued) return true;
                if (DateTime.UtcNow >= until) return false;
                System.Threading.Monitor.Wait(gate, 250);
            }
        }
    }

    // ---- on disk: one file for each item, written anew whenever it changes
    string FileOf(Item item) { return Path.Combine(folder, item.Id + ".json"); }
    void Delete(Item item) { if (folder == null) return; try { File.Delete(FileOf(item)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    void Write(Item item) {
        if (folder == null) return;
        try {
            Directory.CreateDirectory(folder);
            var kept = new Dictionary<string, object> { { "id", item.Id }, { "tool", item.Tool }, { "session", item.Session }, { "state", item.State }, { "order", item.Order },
                { "at", item.At.ToString("o") }, { "settled", item.Settled.ToString("o") } };
            if (item.Open) kept["text"] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(item.Text), null, DataProtectionScope.CurrentUser));
            string file = FileOf(item), fresh = file + ".tmp";
            File.WriteAllText(fresh, json.Serialize(kept), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(fresh, file, null); else File.Move(fresh, file);
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (CryptographicException) { }
    }
    Item Read(string file) {
        try {
            var kept = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            Func<string, string> word = name => { object value; return kept != null && kept.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : ""; };
            var item = new Item { Id = word("id"), Tool = word("tool"), Session = word("session"), State = word("state") };
            if (!Regex.IsMatch(item.Id, @"\A[a-f0-9]{16,32}\z") || Path.GetFileNameWithoutExtension(file) != item.Id || Array.IndexOf(new[] { Queued, Paused, Sending, Handed, Withdrawn }, item.State) < 0) return null;
            if (!Int64.TryParse(word("order"), out item.Order)) return null;
            DateTime.TryParse(word("at"), null, System.Globalization.DateTimeStyles.RoundtripKind, out item.At);
            DateTime.TryParse(word("settled"), null, System.Globalization.DateTimeStyles.RoundtripKind, out item.Settled);
            if (item.Open) item.Text = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(word("text")), null, DataProtectionScope.CurrentUser));
            return item;
        } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; } catch (FormatException) { return null; } catch (CryptographicException) { return null; }
    }
}
}
