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
    readonly SemaphoreSlim reportWake = new SemaphoreSlim(0, 1);

    HttpClient client;
    DateTime nextLogin = DateTime.MinValue;
    DateTime lastSuccessUtc = DateTime.MinValue;
    int loginFailures;
    int transportFailures;
    volatile bool acceptsGzip;
    string sentSessions = "";
    DateTime sessionsSent = DateTime.MinValue;
    bool disposed;

    public bool IsValid { get { return ValidOrigin.IsMatch(Origin) && Password.Length > 0; } }
    public bool AcceptsGzip { get { return acceptsGzip; } set { acceptsGzip = value; } }
    public CancellationToken Token { get { return cancel.Token; } }
    public bool Active { get { return !disposed && !cancel.IsCancellationRequested; } }
    public HttpClient Client { get { lock (stateGate) return client; } }
    public DateTime LastSuccessUtc { get { lock (stateGate) return lastSuccessUtc; } }

    public bool RecentlyHealthy(DateTime nowUtc, int windowSeconds = 25) {
        lock (stateGate) return lastSuccessUtc != DateTime.MinValue && (nowUtc - lastSuccessUtc).TotalSeconds <= windowSeconds;
    }

    public void WakeReport() {
        try { if (!disposed && reportWake.CurrentCount == 0) reportWake.Release(); }
        catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }

    public async Task<bool> WaitReportAsync(int milliseconds) {
        if (disposed || cancel.IsCancellationRequested) return false;
        try { return await reportWake.WaitAsync(milliseconds, cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    public void RecordSuccess(DateTime nowUtc) {
        lock (stateGate) {
            lastSuccessUtc = nowUtc;
            loginFailures = 0;
            transportFailures = 0;
        }
    }

    /// Records a transport timeout or network error; drops the HttpClient pool after 2 consecutive failures
    /// so half-open TCP sockets after sleep or Wi-Fi roaming recover cleanly.
    public void RecordFailure() {
        bool dropPool = false;
        lock (stateGate) {
            transportFailures++;
            if (transportFailures >= 2) {
                transportFailures = 0;
                dropPool = true;
            }
        }
        if (dropPool) ResetAuth();
    }

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
        bool replay = from < t.History.Count;
        // Only synthesize a restart reset when replaying trimmed history from this process (`firstSeq > t.BaseSeq + 1`).
        // A terminal returned after an update starts with `BaseSeq = kept["seq"]` and empty History; marking its first
        // post-update chunk as `restart` before the relay acks would wipe all pre-update history stored on the relay.
        bool gap = replay && from == 0 && firstSeq > have + 1 && firstSeq > t.BaseSeq + 1;
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
            loginFailures = 0;
            transportFailures = 0;
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
            int backoff = loginFailures == 0 ? 2 : loginFailures == 1 ? 4 : loginFailures == 2 ? 8 : 15;
            loginFailures = Math.Min(loginFailures + 1, 4);
            nextLogin = DateTime.UtcNow.AddSeconds(backoff);
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
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using (var r = await next.PostAsync(Origin + "/api/login", content, timeout.Token).ConfigureAwait(false)) {
                    if (!r.IsSuccessStatusCode) {
                        next.Dispose();
                        return false;
                    }
                }
            }
            HttpClient old = null;
            lock (stateGate) {
                if (disposed) { next.Dispose(); return false; }
                old = client;
                client = next;
                acceptsGzip = false;
                loginFailures = 0;
                transportFailures = 0;
                nextLogin = DateTime.MinValue;
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
        try { reportWake.Dispose(); } catch { }
        try { cancel.Dispose(); } catch { }
        if (old != null) try { old.Dispose(); } catch { }
    }
}
}
