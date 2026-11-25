using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// Supervises cloudflared quick tunnels with automatic QUIC -> HTTP/2 fallback (cached 24h on repeated
/// QUIC activity timeouts) and periodic readiness / zombie-tunnel detection.
public sealed class TunnelSupervisor {
    public static readonly TimeSpan Http2FallbackTtl = TimeSpan.FromHours(24);
    public const int QuicTimeoutThreshold = 8;
    public static readonly TimeSpan QuicTimeoutWindow = TimeSpan.FromMinutes(5);

    readonly string dataDir;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly object gate = new object();
    readonly List<DateTime> quicStalls = new List<DateTime>();
    string metricsOrigin = "";
    int notReadyStreak;

    public TunnelSupervisor(string dataFolder) { dataDir = dataFolder; }
    string FallbackFile { get { return Path.Combine(dataDir, "tunnel-http2.json"); } }

    /// Whether HTTP/2 should be used either because the user selected it or because QUIC repeatedly stalled within 24h.
    public bool ShouldUseHttp2(string configuredProtocol, DateTime? nowUtc = null) {
        if (String.Equals(configuredProtocol, "http2", StringComparison.OrdinalIgnoreCase)) return true;
        DateTime now = nowUtc ?? DateTime.UtcNow;
        try {
            if (!File.Exists(FallbackFile)) return false;
            var saved = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(FallbackFile, Encoding.UTF8));
            DateTime until;
            if (saved != null && saved.ContainsKey("until") && DateTime.TryParse(Convert.ToString(saved["until"]), null, System.Globalization.DateTimeStyles.RoundtripKind, out until))
                return now < until;
        } catch (Exception) { }
        return false;
    }

    public void PersistHttp2Fallback(DateTime? nowUtc = null) {
        DateTime until = (nowUtc ?? DateTime.UtcNow).Add(Http2FallbackTtl);
        try {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(FallbackFile, json.Serialize(new Dictionary<string, object> { { "until", until.ToString("o") } }), new UTF8Encoding(false));
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void ClearHttp2Fallback() {
        try { if (File.Exists(FallbackFile)) File.Delete(FallbackFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// Inspects one cloudflared stderr/stdout log line. Returns true when QUIC degradation warrants switching to HTTP/2.
    public bool ObserveLogLine(string line, DateTime? nowUtc = null) {
        if (String.IsNullOrEmpty(line)) return false;
        var metrics = Regex.Match(line, @"Starting metrics server on (127\.0\.0\.1:\d+)");
        if (metrics.Success) lock (gate) metricsOrigin = "http://" + metrics.Groups[1].Value;

        if (line.IndexOf("Switching to fallback protocol http2", StringComparison.OrdinalIgnoreCase) >= 0) {
            PersistHttp2Fallback(nowUtc);
            return false; // cloudflared already switched internally; remember for future restarts
        }

        if (line.IndexOf("timeout: no recent network activity", StringComparison.OrdinalIgnoreCase) >= 0) {
            DateTime now = nowUtc ?? DateTime.UtcNow;
            lock (gate) {
                quicStalls.Add(now);
                quicStalls.RemoveAll(t => now - t > QuicTimeoutWindow);
                if (quicStalls.Count >= QuicTimeoutThreshold) {
                    quicStalls.Clear();
                    PersistHttp2Fallback(now);
                    return true;
                }
            }
        }
        return false;
    }

    public string MetricsOrigin { get { lock (gate) return metricsOrigin; } }

    public void ResetRuntime() {
        lock (gate) { quicStalls.Clear(); metricsOrigin = ""; notReadyStreak = 0; }
    }

    public static string BuildArguments(int port, bool useHttp2, bool withMetrics = true) {
        return "tunnel --no-autoupdate"
            + (withMetrics ? " --metrics 127.0.0.1:0" : "")
            + (useHttp2 ? " --protocol http2" : "")
            + " --url http://127.0.0.1:" + port;
    }

    /// Probes cloudflared's local /ready endpoint when metrics are active. Returns true when healthy or unchecked.
    public bool CheckLocalReady() {
        string origin = MetricsOrigin;
        if (origin.Length == 0) return true; // standin or older cloudflared without metrics
        try {
            var req = (HttpWebRequest)WebRequest.Create(origin + "/ready");
            req.Timeout = 4000; req.ReadWriteTimeout = 4000; req.AllowAutoRedirect = false;
            using (var resp = (HttpWebResponse)req.GetResponse()) {
                bool ok = (int)resp.StatusCode == 200;
                lock (gate) notReadyStreak = ok ? 0 : notReadyStreak + 1;
                return ok;
            }
        } catch (WebException ex) {
            var resp = ex.Response as HttpWebResponse;
            if (resp != null && (int)resp.StatusCode == 503) {
                lock (gate) notReadyStreak++;
                return false;
            }
            return true; // transient local socket blip
        } catch (Exception) {
            return true;
        }
    }

    public bool IsStuckNotReady(int limit = 4) {
        lock (gate) return notReadyStreak >= limit;
    }
}
}
