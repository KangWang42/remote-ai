using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// Configuration for a single relay endpoint that TerminalAgent connects to.
public sealed class RelayEndpoint {
    public string Name = "primary";
    public string Origin = "";
    public string Password = "";

    public string Key { get { return (Name ?? "") + "|" + (Origin ?? "") + "|" + (Password ?? ""); } }
}

/// Independent transport state for one relay endpoint (local loopback relay or remote public/own relay).
/// Keeping per-link cursors, login state, gzip negotiation and acked sequence numbers here allows
/// multiple relays to stay in sync with the same TerminalAgent without consuming each other's output.
public sealed class RelayLink : IDisposable {
    static readonly Regex ValidOrigin = new Regex(@"\Ahttps?://[^/\s]+(/c/[a-f0-9]{20})?\z", RegexOptions.Compiled);
    public readonly string Name;
    public readonly string Origin;
    public readonly string Password;
    public readonly string Key;

    readonly object stateGate = new object();
    readonly Dictionary<string, long> ackSeq = new Dictionary<string, long>();
    readonly HashSet<string> reported = new HashSet<string>();
    readonly CancellationTokenSource cancel = new CancellationTokenSource();

    HttpClient client;
    DateTime nextLogin = DateTime.MinValue;
    volatile bool acceptsGzip;
    string sentSessions = "";
    DateTime sessionsSent = DateTime.MinValue;
    bool disposed;

    public bool IsValid { get { return ValidOrigin.IsMatch(Origin) && Password.Length > 0; } }
    public bool AcceptsGzip { get { return acceptsGzip; } set { acceptsGzip = value; } }
    public CancellationToken Token { get { return cancel.Token; } }
    public bool Active { get { return !disposed && !cancel.IsCancellationRequested; } }
    public HttpClient Client { get { lock (stateGate) return client; } }

    public RelayLink(RelayEndpoint endpoint) {
        Name = endpoint.Name ?? "primary";
        Origin = (endpoint.Origin ?? "").Trim().TrimEnd('/');
        Password = endpoint.Password ?? "";
        Key = endpoint.Key;
    }

    public bool HasReported(string opId) {
        lock (stateGate) return reported.Contains(opId);
    }

    public void MarkReported(string opId) {
        lock (stateGate) {
            reported.Add(opId);
            if (reported.Count > 5000) reported.Clear();
        }
    }

    public void ClearReported(string opId) {
        lock (stateGate) reported.Remove(opId);
    }

    public bool NeedsSessions(string serialized, DateTime nowUtc) {
        lock (stateGate) return serialized != sentSessions || nowUtc - sessionsSent > TimeSpan.FromSeconds(10);
    }

    public void MarkSessionsSent(string serialized, DateTime nowUtc) {
        lock (stateGate) { sentSessions = serialized; sessionsSent = nowUtc; }
    }

    public void ResetSessionCache() {
        lock (stateGate) { sentSessions = ""; sessionsSent = DateTime.MinValue; }
    }

    public long AckedSeq(string terminalId) {
        lock (stateGate) {
            long value;
            return ackSeq.TryGetValue(terminalId, out value) ? value : 0;
        }
    }

    public void NoteOutputAck(Dictionary<string, object> ack) {
        if (ack == null) return;
        lock (stateGate) {
            foreach (var pair in ack) {
                long seq;
                if (Int64.TryParse(Convert.ToString(pair.Value), out seq) && seq >= 0) ackSeq[pair.Key] = seq;
            }
        }
    }

    public void ForgetTerminal(string terminalId) {
        lock (stateGate) ackSeq.Remove(terminalId);
    }

    /// Selects up to `limit` unacknowledged output chunks for this link from the terminal's retained
    /// History + Output buffers without mutating the terminal's shared lists.
    public List<Dictionary<string, object>> SelectChunks(TerminalAgent.LiveTerminal t, int limit = 30) {
        long have = AckedSeq(t.Id);
        var all = new List<Dictionary<string, object>>(t.History.Count + t.Output.Count);
        all.AddRange(t.History);
        all.AddRange(t.Output);
        if (all.Count == 0) return new List<Dictionary<string, object>>();

        int from = -1;
        for (int i = 0; i < all.Count; i++) {
            if (Convert.ToInt64(all[i]["seq"]) > have) { from = i; break; }
        }
        if (from < 0) return new List<Dictionary<string, object>>();

        var result = new List<Dictionary<string, object>>();
        long firstSeq = Convert.ToInt64(all[from]["seq"]);
        bool gap = from == 0 && firstSeq > have + 1;
        bool replay = from < t.History.Count;
        int count = Math.Min(limit, all.Count - from);
        for (int i = 0; i < count; i++) {
            var src = all[from + i];
            var copy = new Dictionary<string, object> {
                { "terminal", src["terminal"] },
                { "seq", src["seq"] },
                { "data", src["data"] }
            };
            if (replay && (from + i) < t.History.Count) copy["old"] = true;
            if (i == 0 && gap) {
                string preamble = String.Concat(t.Modes.Select(m => "\x1b[?" + m.Key + (m.Value ? "h" : "l"))) + t.ModesRest;
                if (preamble.Length > 0 && t.Led != firstSeq) copy["data"] = preamble + Convert.ToString(copy["data"]);
                copy["restart"] = true;
            } else if (src.ContainsKey("restart") && true.Equals(src["restart"])) {
                copy["restart"] = true;
            }
            if (src.ContainsKey("old") && true.Equals(src["old"])) copy["old"] = true;
            result.Add(copy);
        }
        return result;
    }

    /// Computes the minimum acked seq across all active links so TerminalAgent can safely trim shared output.
    public static long MinimumAck(IList<RelayLink> links, string terminalId, long fallbackSeq) {
        if (links == null || links.Count == 0) return fallbackSeq;
        long min = long.MaxValue;
        bool any = false;
        foreach (var link in links) {
            if (link == null || !link.Active) continue;
            long seq = link.AckedSeq(terminalId);
            if (seq < min) min = seq;
            any = true;
        }
        return any ? min : fallbackSeq;
    }

    public void ResetAuth() {
        HttpClient old;
        lock (stateGate) {
            nextLogin = DateTime.MinValue;
            acceptsGzip = false;
            sentSessions = "";
            sessionsSent = DateTime.MinValue;
            old = client;
            client = null;
        }
        if (old != null) try { old.Dispose(); } catch { }
    }

    public async Task<bool> EnsureLoginAsync(JavaScriptSerializer json) {
        if (!IsValid || !Active) return false;
        lock (stateGate) {
            if (client != null) return true;
            if (DateTime.UtcNow < nextLogin) return false;
            nextLogin = DateTime.UtcNow.AddSeconds(15);
        }
        var next = new HttpClient(new HttpClientHandler {
            AllowAutoRedirect = false,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = TimeSpan.FromSeconds(20) };
        try {
            var endpoint = ServicePointManager.FindServicePoint(new Uri(Origin));
            endpoint.UseNagleAlgorithm = false;
            endpoint.Expect100Continue = false;
            var content = new StringContent(json.Serialize(new Dictionary<string, object> { { "password", Password } }), Encoding.UTF8, "application/json");
            using (var r = await next.PostAsync(Origin + "/api/login", content, Token).ConfigureAwait(false)) {
                if (!r.IsSuccessStatusCode) {
                    next.Dispose();
                    return false;
                }
            }
            HttpClient old = null;
            lock (stateGate) {
                if (disposed) { next.Dispose(); return false; }
                old = client;
                client = next;
                acceptsGzip = false;
            }
            if (old != null) try { old.Dispose(); } catch { }
            return true;
        } catch (Exception) {
            try { next.Dispose(); } catch { }
            return false;
        }
    }

    public void Dispose() {
        HttpClient old;
        lock (stateGate) {
            if (disposed) return;
            disposed = true;
            old = client;
            client = null;
        }
        try { cancel.Cancel(); } catch { }
        try { cancel.Dispose(); } catch { }
        if (old != null) try { old.Dispose(); } catch { }
    }
}
}
