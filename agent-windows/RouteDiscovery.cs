using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// Stable host identity persisted across restarts and route changes so clients can verify direct endpoints
/// before sending credentials (docs/PROTOCOL.md).
public static class EnvironmentIdentity {
    static readonly Regex ValidId = new Regex(@"\A[a-f0-9]{32}\z", RegexOptions.Compiled);
    static readonly object gate = new object();

    public static string LoadOrCreate(string dataDir) {
        if (String.IsNullOrWhiteSpace(dataDir)) return NewId();
        string path = Path.Combine(dataDir, "environment-id.txt");
        lock (gate) {
            try {
                if (File.Exists(path)) {
                    string kept = File.ReadAllText(path, Encoding.UTF8).Trim().ToLowerInvariant();
                    if (ValidId.IsMatch(kept)) return kept;
                }
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            string created = NewId();
            try {
                Directory.CreateDirectory(dataDir);
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, created + "\n", new UTF8Encoding(false));
                if (!File.Exists(path)) {
                    try { File.Move(tmp, path); return created; }
                    catch (IOException) { try { File.Delete(tmp); } catch { } }
                } else {
                    try { File.Delete(tmp); } catch { }
                }
                string winner = File.ReadAllText(path, Encoding.UTF8).Trim().ToLowerInvariant();
                if (ValidId.IsMatch(winner)) return winner;
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return created;
        }
    }

    static string NewId() {
        byte[] bytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}

/// One advertised route through which a client can reach this computer.
public sealed class RouteInfo {
    public string Kind = "";
    public string Url = "";
    public int Priority = 50;
    public bool LocalAuth;

    public Dictionary<string, object> ToWire() {
        var map = new Dictionary<string, object> {
            { "kind", Kind },
            { "url", Url },
            { "priority", Priority }
        };
        if (LocalAuth) map["local_auth"] = true;
        return map;
    }
}

/// Tailscale detection that avoids CGNAT collisions (such as Cloudflare WARP sharing 100.64.0.0/10):
/// an interface is treated as Tailscale only when its name/description matches Tailscale or it carries
/// Tailscale's unique IPv6 ULA prefix (fd7a:115c:a1e0::/48), or when `tailscale status --json` confirms
/// BackendState == "Running".
public static class TailscaleDiscovery {
    public sealed class Status {
        public bool Running;
        public string Ipv4 = "";
        public string MagicDns = "";
    }

    public static bool IsCgnatIpv4(IPAddress address) {
        if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] == 100 && (b[1] & 0xC0) == 64;
    }

    public static bool IsTailscaleIpv6(IPAddress address) {
        if (address == null || address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        byte[] b = address.GetAddressBytes();
        return b.Length == 16 && b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
    }

    public static bool IsTailscaleInterface(string name, string description, IEnumerable<IPAddress> addresses) {
        string text = ((name ?? "") + " " + (description ?? "")).ToLowerInvariant();
        if (text.Contains("tailscale")) return true;
        return addresses != null && addresses.Any(IsTailscaleIpv6);
    }

    public static string InterfaceIpv4() {
        try {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)) {
                var ips = nic.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToArray();
                if (!IsTailscaleInterface(nic.Name, nic.Description, ips)) continue;
                var v4 = ips.FirstOrDefault(IsCgnatIpv4);
                if (v4 != null) return v4.ToString();
            }
        } catch (NetworkInformationException) { } catch (PlatformNotSupportedException) { }
        return "";
    }

    public static Status ParseStatusJson(string raw) {
        var result = new Status();
        if (String.IsNullOrWhiteSpace(raw)) return result;
        try {
            var json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
            var root = json.Deserialize<Dictionary<string, object>>(raw);
            if (root == null) return result;
            object state;
            if (!root.TryGetValue("BackendState", out state) || !String.Equals(Convert.ToString(state), "Running", StringComparison.OrdinalIgnoreCase))
                return result;
            result.Running = true;
            object selfObj;
            var self = root.TryGetValue("Self", out selfObj) ? selfObj as Dictionary<string, object> : null;
            if (self == null) return result;
            object dnsObj;
            if (self.TryGetValue("DNSName", out dnsObj) && dnsObj != null) {
                string dns = Convert.ToString(dnsObj).Trim().TrimEnd('.').ToLowerInvariant();
                if (dns.EndsWith(".ts.net", StringComparison.Ordinal) && Regex.IsMatch(dns, @"\A[a-z0-9.-]{4,200}\z"))
                    result.MagicDns = dns;
            }
            object ipsObj;
            var ips = self.TryGetValue("TailscaleIPs", out ipsObj) ? ipsObj as System.Collections.IEnumerable : null;
            if (ips != null) {
                foreach (object one in ips) {
                    IPAddress parsed;
                    if (IPAddress.TryParse(Convert.ToString(one ?? "").Trim(), out parsed) && IsCgnatIpv4(parsed)) {
                        result.Ipv4 = parsed.ToString();
                        break;
                    }
                }
            }
        } catch (Exception) { }
        return result;
    }

    static string FindCli() {
        string[] candidates = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe")
        };
        foreach (string path in candidates) if (File.Exists(path)) return path;
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathEnv.Split(Path.PathSeparator)) {
            if (String.IsNullOrWhiteSpace(dir)) continue;
            try {
                string file = Path.Combine(dir.Trim(), "tailscale.exe");
                if (File.Exists(file)) return file;
            } catch (ArgumentException) { }
        }
        return null;
    }

    public static Status ProbeCli(int timeoutMs = 3500) {
        string cli = FindCli();
        if (cli == null) return new Status();
        Process process = null;
        try {
            process = new Process {
                StartInfo = new ProcessStartInfo(cli, "status --json") {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };
            process.Start();
            var readTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs)) {
                try { process.Kill(); } catch { }
                return new Status();
            }
            if (process.ExitCode != 0) return new Status();
            string output = readTask.Wait(1000) ? readTask.Result : "";
            return ParseStatusJson(output);
        } catch (Exception) {
            return new Status();
        } finally {
            if (process != null) try { process.Dispose(); } catch { }
        }
    }
}

/// Maintains an ordered, deduplicated route list for this computer without blocking hot paths on CLI discovery.
public sealed class RouteCatalog {
    readonly object gate = new object();
    TailscaleDiscovery.Status tailscale = new TailscaleDiscovery.Status();
    DateTime lastProbe = DateTime.MinValue;
    int probing;
    readonly Func<string> lanAddress;
    readonly Func<TailscaleDiscovery.Status> probeTailscale;

    public RouteCatalog(Func<string> lanProvider = null, Func<TailscaleDiscovery.Status> tailscaleProvider = null) {
        lanAddress = lanProvider ?? (() => "");
        probeTailscale = tailscaleProvider ?? (() => TailscaleDiscovery.ProbeCli());
    }

    public void RefreshAsync(bool force = false) {
        if (!force && DateTime.UtcNow - lastProbe < TimeSpan.FromSeconds(45)) return;
        if (Interlocked.CompareExchange(ref probing, 1, 0) != 0) return;
        Task.Run(() => {
            try {
                var found = probeTailscale();
                lock (gate) { tailscale = found ?? new TailscaleDiscovery.Status(); lastProbe = DateTime.UtcNow; }
            } catch (Exception) {
                lock (gate) { lastProbe = DateTime.UtcNow; }
            } finally {
                Interlocked.Exchange(ref probing, 0);
            }
        });
    }

    public TailscaleDiscovery.Status CurrentTailscale() {
        lock (gate) return tailscale;
    }

    /// Builds ordered routes: LAN (10) -> Tailscale (20) -> Tunnel (30) -> Own Relay (40).
    /// Public relays are intentionally excluded so their underlying host stays private to the QR code.
    public List<RouteInfo> Build(int localPort, bool localListening, string remoteKind = "", string remoteUrl = "") {
        RefreshAsync(false);
        var routes = new List<RouteInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Action<string, string, int, bool> add = (kind, url, priority, localAuth) => {
            string clean = (url ?? "").Trim().TrimEnd('/');
            if (clean.Length == 0 || !seen.Add(clean)) return;
            routes.Add(new RouteInfo { Kind = kind, Url = clean, Priority = priority, LocalAuth = localAuth });
        };

        if (localListening && localPort > 0) {
            string lan = (lanAddress() ?? "").Trim();
            if (lan.Length > 0) add("lan", "http://" + lan + ":" + localPort, 10, true);
            string tsIp = TailscaleDiscovery.InterfaceIpv4();
            TailscaleDiscovery.Status ts;
            lock (gate) ts = tailscale;
            if (ts != null && ts.Running) {
                if (ts.Ipv4.Length > 0) tsIp = ts.Ipv4;
                if (ts.MagicDns.Length > 0) add("tailscale", "http://" + ts.MagicDns + ":" + localPort, 19, true);
            }
            if (tsIp.Length > 0) add("tailscale", "http://" + tsIp + ":" + localPort, 20, true);
        }

        if (!String.IsNullOrWhiteSpace(remoteUrl)) {
            if (remoteKind == "cloud" || remoteKind == "tunnel") add("tunnel", remoteUrl, 30, true);
            else if (remoteKind == "own") add("own", remoteUrl, 40, false);
        }

        routes.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : StringComparer.Ordinal.Compare(a.Url, b.Url));
        return routes;
    }
}
}
