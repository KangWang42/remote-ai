using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RemoteCli;

class MultiRouteCheck {
    static void Require(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); }

    static void TestIdentity(string dir) {
        string first = EnvironmentIdentity.LoadOrCreate(dir);
        string second = EnvironmentIdentity.LoadOrCreate(dir);
        Require(first.Length == 32 && first == second, "environment id must be 32-hex and stable across reads");
    }

    static void TestTailscaleDiscovery() {
        Require(TailscaleDiscovery.IsCgnatIpv4(IPAddress.Parse("100.64.0.1")), "100.64.0.1 is CGNAT");
        Require(TailscaleDiscovery.IsCgnatIpv4(IPAddress.Parse("100.127.255.254")), "100.127.255.254 is CGNAT");
        Require(!TailscaleDiscovery.IsCgnatIpv4(IPAddress.Parse("100.128.0.1")), "100.128.0.1 is outside /10");
        Require(!TailscaleDiscovery.IsCgnatIpv4(IPAddress.Parse("192.168.1.5")), "LAN is not CGNAT");

        var warpIps = new[] { IPAddress.Parse("100.90.10.2"), IPAddress.Parse("fe80::1") };
        Require(!TailscaleDiscovery.IsTailscaleInterface("CloudflareWARP", "Cloudflare WARP Tunnel", warpIps), "WARP CGNAT must not be misclassified as Tailscale");

        var tsByUla = new[] { IPAddress.Parse("100.88.1.5"), IPAddress.Parse("fd7a:115c:a1e0::1234") };
        Require(TailscaleDiscovery.IsTailscaleInterface("utun4", "Virtual Adapter", tsByUla), "fd7a:115c:a1e0::/48 identifies Tailscale even on generic adapter name");
        Require(TailscaleDiscovery.IsTailscaleInterface("Tailscale", "Tailscale Tunnel", new[] { IPAddress.Parse("100.88.1.5") }), "named Tailscale adapter recognized");

        var running = TailscaleDiscovery.ParseStatusJson("{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"my-pc.tailnet123.ts.net.\",\"TailscaleIPs\":[\"100.77.1.9\",\"fd7a:115c:a1e0::9\"]}}");
        Require(running.Running && running.Ipv4 == "100.77.1.9" && running.MagicDns == "my-pc.tailnet123.ts.net", "running tailscale status parsed");
        var stopped = TailscaleDiscovery.ParseStatusJson("{\"BackendState\":\"Stopped\",\"Self\":{\"DNSName\":\"my-pc.tailnet123.ts.net.\",\"TailscaleIPs\":[\"100.77.1.9\"]}}");
        Require(!stopped.Running, "stopped tailscale status ignored");
    }

    static void TestRouteCatalog() {
        var catalog = new RouteCatalog(
            () => "192.168.1.25",
            () => new TailscaleDiscovery.Status { Running = true, Ipv4 = "100.77.1.9", MagicDns = "dev.tail.ts.net" }
        );
        catalog.RefreshAsync(true);
        for (int i = 0; i < 40 && !catalog.CurrentTailscale().Running; i++) Thread.Sleep(25);
        var routes = catalog.Build(8722, true, "cloud", "https://abc.trycloudflare.com");
        Require(routes.Count >= 4, "expected LAN + MagicDNS + TS IP + tunnel");
        Require(routes[0].Kind == "lan" && routes[0].Url == "http://192.168.1.25:8722" && routes[0].LocalAuth, "LAN first");
        Require(routes.Any(r => r.Kind == "tailscale" && r.Url == "http://dev.tail.ts.net:8722" && r.LocalAuth), "MagicDNS included");
        Require(routes.Any(r => r.Kind == "tailscale" && r.Url == "http://100.77.1.9:8722" && r.LocalAuth), "Tailscale IP included");
        Require(routes.Last().Kind == "tunnel" && routes.Last().Url == "https://abc.trycloudflare.com", "tunnel after direct routes");

        // Public relay URLs are never exposed in advertised routes
        var pubRoutes = catalog.Build(8722, true, "public", "https://secret-public.example/c/0123456789abcdef0123");
        Require(!pubRoutes.Any(r => r.Url.Contains("secret-public")), "public relay host must stay hidden");
    }

    static void TestTunnelSupervisor(string dir) {
        var sup = new TunnelSupervisor(dir);
        DateTime t0 = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
        Require(!sup.ShouldUseHttp2("auto", t0), "auto starts with QUIC");
        Require(sup.ShouldUseHttp2("http2", t0), "explicit http2 honored");
        Require(TunnelSupervisor.BuildArguments(8722, false).Contains("--metrics 127.0.0.1:0"), "metrics flag present");
        Require(TunnelSupervisor.BuildArguments(8722, true).Contains("--protocol http2"), "http2 flag present");
        sup.ObserveLogLine("INF Starting metrics server on 127.0.0.1:41234/metrics", t0);
        Require(sup.MetricsOrigin == "http://127.0.0.1:41234", "metrics origin parsed");
        bool triggered = false;
        for (int i = 0; i < TunnelSupervisor.QuicTimeoutThreshold; i++)
            triggered |= sup.ObserveLogLine("ERR Failed to dial a quic connection error=\"timeout: no recent network activity\"", t0.AddSeconds(i * 10));
        Require(triggered, "repeated QUIC timeouts trigger HTTP/2 fallback");
        Require(sup.ShouldUseHttp2("auto", t0.AddHours(12)), "fallback persists for 24h");
        Require(!sup.ShouldUseHttp2("auto", t0.AddHours(25)), "fallback expires after 24h");
        sup.PersistHttp2Fallback(t0);
        sup.ClearHttp2Fallback();
        Require(!sup.ShouldUseHttp2("auto", t0.AddMinutes(1)), "clear removes fallback");
    }

    static void TestRelayLinkSelection() {
        var term = new TerminalAgent.LiveTerminal { Id = "0123456789abcdef0123456789abcdef", Tool = "shell", Dir = "demo" };
        for (int i = 1; i <= 5; i++) {
            term.Pending.Append("line-" + i + "\n");
            TerminalAgent.Seal(term);
        }
        var local = new RelayLink(new RelayEndpoint { Name = "local", Origin = "http://127.0.0.1:8722", Password = "local-password-123" });
        var remote = new RelayLink(new RelayEndpoint { Name = "remote", Origin = "http://127.0.0.1:8723", Password = "remote-password-123" });
        try {
            var localFirst = local.SelectChunks(term, 30);
            var remoteFirst = remote.SelectChunks(term, 30);
            Require(localFirst.Count == 5 && remoteFirst.Count == 5, "both links see all 5 chunks independently");

            local.NoteOutputAck(new Dictionary<string, object> { { term.Id, 5 } });
            remote.NoteOutputAck(new Dictionary<string, object> { { term.Id, 2 } });
            Require(local.SelectChunks(term, 30).Count == 0, "local link caught up");
            var remoteRest = remote.SelectChunks(term, 30);
            Require(remoteRest.Count == 3 && Convert.ToInt64(remoteRest[0]["seq"]) == 3, "remote link resumes from seq 3");

            long minAck = RelayLink.MinimumAck(new[] { local, remote }, term.Id, term.Seq);
            Require(minAck == 2, "minimum ack preserves chunks still needed by slower link");
            TerminalAgent.Settle(term, minAck, TerminalAgent.KeepRunning);
            Require(term.History.Count == 2 && term.Output.Count == 3, "history keeps settled chunks while output keeps unsent ones");
        } finally {
            local.Dispose();
            remote.Dispose();
        }
    }

    static int Main(string[] args) {
        string dir = args[0];
        Directory.CreateDirectory(dir);
        if (args.Length >= 6 && args[1] == "--dual-agent") {
            // Live dual-relay harness driven by tests/multi_route_check.py
            string localOrigin = args[2], localPass = args[3], remoteOrigin = args[4], remotePass = args[5];
            var agent = new TerminalAgent(dir) { Version = "test" };
            agent.RoutesProvider = () => new List<RouteInfo> {
                new RouteInfo { Kind = "lan", Url = localOrigin, Priority = 10, LocalAuth = true },
                new RouteInfo { Kind = "own", Url = remoteOrigin, Priority = 40, LocalAuth = false }
            };
            agent.SetLinks(new[] {
                new RelayEndpoint { Name = "local", Origin = localOrigin, Password = localPass },
                new RelayEndpoint { Name = "remote", Origin = remoteOrigin, Password = remotePass }
            });
            agent.Run().GetAwaiter().GetResult();
            return 0;
        }
        TestIdentity(Path.Combine(dir, "id"));
        TestTailscaleDiscovery();
        TestRouteCatalog();
        TestTunnelSupervisor(Path.Combine(dir, "tunnel"));
        TestRelayLinkSelection();
        Console.WriteLine("MultiRouteCheck unit checks passed");
        return 0;
    }
}
