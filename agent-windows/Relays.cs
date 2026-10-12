using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// A relay the program can connect through: one for everyone, from the published list, or one of the owner's own
/// that was saved on this computer.
public sealed class Relay {
    public bool Public;
    public string Name = "", Url = "";
    public string Key = "";         // what a relay for everyone asks for before it makes a space, when it asks
    public string Password = "";    // of one's own relay
    public int Ms = -2;             // how long it took to answer: -2 not asked yet, -1 no answer
    public string Id { get { return (Public ? "public " : "own ") + Url; } }
}

/// The relays known on this computer. Those for everyone come from a list published with the project: a file that
/// names them, read again now and then, so that a relay is added, changed or taken away without a new version of the
/// program; an empty list means there are none. The owner's own relays are kept here with their passwords, protected
/// for this Windows account, and so is what each relay for everyone gave this computer.
public sealed class Relays {
    // Asked in this order: the repository under the name it is to have, then under the one it had (AutoUpdater.Repositories);
    // of each, the second address serves the same file where the first cannot be reached.
    static readonly string[] Sources = { "https://raw.githubusercontent.com/KangWang42/remote-ai/main/public-relays.json", "https://cdn.jsdelivr.net/gh/KangWang42/remote-ai@main/public-relays.json",
        "https://raw.githubusercontent.com/KangWang42/remote-cli/main/public-relays.json", "https://cdn.jsdelivr.net/gh/KangWang42/remote-cli@main/public-relays.json" };
    const string Address = @"\Ahttps://[^/\s]+\z", Any = @"\Ahttps?://[^/\s]+\z", Local = @"\Ahttps?://(127\.0\.0\.1|localhost)(:\d+)?\z";
    readonly string dataDir;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly object gate = new object();
    public Relays(string dataFolder) { dataDir = dataFolder; }
    string ListFile { get { return Path.Combine(dataDir, "public-relays.json"); } }
    string OwnFile { get { return Path.Combine(dataDir, "relays.dpapi"); } }
    string SpacesFile { get { return Path.Combine(dataDir, "spaces.dpapi"); } }

    static string Text(IDictionary<string, object> from, string key) { object value; return from != null && from.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : ""; }
    /// The address of a relay as it is kept: without anything after the host. A relay for everyone must be reached
    /// by https (one on this computer, in a test, may use http); how the owner reaches their own is theirs to decide.
    public static string Clean(string typed, bool own = false) {
        string url = (typed ?? "").Trim().TrimEnd('/');
        return Regex.IsMatch(url, own ? Any : Address) || Regex.IsMatch(url, Local) ? url : "";
    }
    // The list is a public file. An address written out in it is in front of everyone who opens or searches it; in the
    // field "hidden" it is not (tools/relay_list.py writes that field). This keeps an address from a look and from a
    // search. It is no secret from someone who reads this program, which has to know the address to connect.
    static readonly byte[] Pad = SHA256.Create().ComputeHash(Encoding.ASCII.GetBytes("remote-cli public relays"));
    Dictionary<string, object> Reveal(string hidden) {
        try {
            byte[] bytes = Convert.FromBase64String(hidden.Replace('-', '+').Replace('_', '/') + new string('=', (4 - hidden.Length % 4) % 4));
            for (int i = 0; i < bytes.Length; i++) bytes[i] ^= Pad[i % Pad.Length];
            return json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes));
        } catch (Exception) { return null; }
    }
    /// The relays a published list names; null when the text is not such a list.
    public List<Relay> Read(string text) {
        try {
            var list = json.Deserialize<Dictionary<string, object>>(text);
            object named;
            if (list == null || !list.TryGetValue("relays", out named) || !(named is System.Collections.IEnumerable) || named is string) return null;
            var found = new List<Relay>();
            foreach (object item in (System.Collections.IEnumerable)named) {
                var entry = item as Dictionary<string, object>;
                // An entry names its relay in the open ("url", "key"), or in a field that is not readable at a glance.
                var inner = Text(entry, "hidden").Length > 0 ? Reveal(Text(entry, "hidden")) : entry;
                string url = Clean(Text(inner, "url")), name = Regex.Replace(Text(entry, "name"), @"[\s\p{C}]+", " ").Trim();
                if (url.Length == 0 || found.Any(r => r.Url == url) || found.Count >= 12) continue;
                // Without a name it is called what it is; its address is never shown in its place.
                found.Add(new Relay { Public = true, Url = url, Name = name.Length > 0 && name.Length <= 20 ? name : "公共中转", Key = Text(inner, "key") });
            }
            return found;
        } catch (Exception) { return null; }
    }
    /// The relays for everyone, as the list said when it was last read.
    public List<Relay> Published() {
        try { return Read(File.ReadAllText(ListFile, Encoding.UTF8)) ?? new List<Relay>(); } catch (IOException) { return new List<Relay>(); } catch (UnauthorizedAccessException) { return new List<Relay>(); }
    }
    /// Reads the published list again. Returns whether it names other relays than before; a list that cannot be had
    /// leaves the one that was read last.
    public bool Refresh() {
        // REMOTECLI_RELAYS names another list, a file or an address: for a test, or for someone who publishes their own.
        string other = Environment.GetEnvironmentVariable("REMOTECLI_RELAYS");
        foreach (string source in String.IsNullOrWhiteSpace(other) ? Sources : new[] { other.Trim() }) {
            string text;
            try {
                if (Regex.IsMatch(source, @"\Ahttps?://")) {
                    var request = (HttpWebRequest)WebRequest.Create(source);
                    request.Timeout = 10000; request.ReadWriteTimeout = 10000; request.UserAgent = "RemoteCli";
                    using (var reply = (HttpWebResponse)request.GetResponse()) using (var reader = new StreamReader(reply.GetResponseStream(), Encoding.UTF8)) text = reader.ReadToEnd();
                } else text = File.ReadAllText(source, Encoding.UTF8);
            } catch (Exception) { continue; }
            if (text.Length > 100000 || Read(text) == null) continue;
            string before = String.Join("\n", Published().Select(r => r.Url + " " + r.Name + " " + r.Key));
            lock (gate) { File.WriteAllText(ListFile + ".tmp", text, new UTF8Encoding(false)); if (File.Exists(ListFile)) File.Replace(ListFile + ".tmp", ListFile, null); else File.Move(ListFile + ".tmp", ListFile); }
            return before != String.Join("\n", Published().Select(r => r.Url + " " + r.Name + " " + r.Key));
        }
        return false;
    }

    // What is kept with passwords in it is protected for this Windows account, as the program's own password is.
    List<Dictionary<string, object>> Kept(string file) {
        try { return json.Deserialize<List<Dictionary<string, object>>>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser))) ?? new List<Dictionary<string, object>>(); }
        catch (Exception) { return new List<Dictionary<string, object>>(); }
    }
    void Keep(string file, List<Dictionary<string, object>> items) {
        File.WriteAllBytes(file + ".tmp", ProtectedData.Protect(Encoding.UTF8.GetBytes(json.Serialize(items)), null, DataProtectionScope.CurrentUser));
        if (File.Exists(file)) File.Replace(file + ".tmp", file, null); else File.Move(file + ".tmp", file);
    }
    /// The owner's own relays, in the order they were saved.
    public List<Relay> Own() {
        lock (gate) return Kept(OwnFile).Select(item => new Relay { Url = Clean(Text(item, "url"), true), Name = Text(item, "name"), Password = Text(item, "password") }).Where(r => r.Url.Length > 0).ToList();
    }
    /// Saves one of the owner's relays; one with the same address is replaced and keeps its place.
    public void Save(Relay relay) {
        lock (gate) {
            var items = Kept(OwnFile);
            var entry = new Dictionary<string, object> { { "url", relay.Url }, { "name", relay.Name }, { "password", relay.Password } };
            int at = items.FindIndex(item => Text(item, "url") == relay.Url);
            if (at >= 0) items[at] = entry; else items.Add(entry);
            Keep(OwnFile, items);
        }
    }
    public void Remove(string url) { lock (gate) { var items = Kept(OwnFile); if (items.RemoveAll(item => Text(item, "url") == url) > 0) Keep(OwnFile, items); } }
    public bool HasOwn { get { return File.Exists(OwnFile); } }

    /// How long a relay takes to answer, in milliseconds; -1 when it does not answer as a relay does.
    public static int Ask(string url) {
        try {
            var request = (HttpWebRequest)WebRequest.Create(url + "/api/session");
            request.Timeout = 6000; request.ReadWriteTimeout = 6000; request.AllowAutoRedirect = false;
            var watch = Stopwatch.StartNew();
            using (var reply = (HttpWebResponse)request.GetResponse()) using (var reader = new StreamReader(reply.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd().Contains("\"signed_in\"") ? (int)Math.Max(1, watch.ElapsedMilliseconds) : -1;
        } catch (Exception) { return -1; }
    }
    // A request to a relay for everyone: the status it answered with (0 when it could not be reached) and what it said.
    int Call(string url, string body, out Dictionary<string, object> said) {
        said = null;
        try {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 10000; request.ReadWriteTimeout = 10000; request.AllowAutoRedirect = false;
            if (body != null) {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.Method = "POST"; request.ContentType = "application/json"; request.ContentLength = bytes.Length;
                using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            HttpWebResponse reply;
            try { reply = (HttpWebResponse)request.GetResponse(); }
            catch (WebException refused) { reply = refused.Response as HttpWebResponse; if (reply == null) return 0; }
            using (reply) using (var reader = new StreamReader(reply.GetResponseStream(), Encoding.UTF8)) {
                try { said = json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd()); } catch (Exception) { }
                return (int)reply.StatusCode;
            }
        } catch (Exception) { return 0; }
    }
    /// The place this computer has at a relay for everyone: its address there and its password. The one it was given
    /// before is used while the relay still has it; a relay that removed it (nobody used it for a month) is asked for
    /// a new one. Returns nothing when that worked, and otherwise what is wrong.
    public string Space(Relay relay, out string server, out string password) {
        server = password = "";
        List<Dictionary<string, object>> items;
        lock (gate) items = Kept(SpacesFile);
        var mine = items.FirstOrDefault(item => Text(item, "url") == relay.Url);
        Dictionary<string, object> said;
        if (mine != null && Regex.IsMatch(Text(mine, "space"), @"\A/c/[a-f0-9]{20}\z")) {
            int there = Call(relay.Url + Text(mine, "space") + "/api/session", null, out said);
            if (there == 0) return "连不上公共中转“" + relay.Name + "”，请稍后再试，或换一个中转";
            if (there == 200 && Call(relay.Url + Text(mine, "space") + "/api/login", json.Serialize(new Dictionary<string, object> { { "password", Text(mine, "password") } }), out said) == 200) {
                server = relay.Url + Text(mine, "space"); password = Text(mine, "password");
                return "";
            }
        }
        int made = Call(relay.Url + "/api/space", json.Serialize(new Dictionary<string, object> { { "key", relay.Key } }), out said);
        if (made == 0) return "连不上公共中转“" + relay.Name + "”，请稍后再试，或换一个中转";
        string space = Text(said, "space"), secret = Text(said, "password");
        if (made != 200 || !Regex.IsMatch(space, @"\A/c/[a-f0-9]{20}\z") || secret.Length < 12)
            return Text(said, "error").Length > 0 ? Text(said, "error") : "公共中转“" + relay.Name + "”没有正常回应（" + made + "），请换一个中转";
        lock (gate) {
            items = Kept(SpacesFile);
            items.RemoveAll(item => Text(item, "url") == relay.Url);
            items.Add(new Dictionary<string, object> { { "url", relay.Url }, { "space", space }, { "password", secret } });
            Keep(SpacesFile, items);
        }
        server = relay.Url + space; password = secret;
        return "";
    }
}
}
