package io.github.kangwang42.remotecli;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;

/** Standalone unit tests for RouteSelector: 500ms head start, identity guard, backoff, cooldown, merge. */
public final class RouteSelectorCheck {
    private static void expect(boolean ok, String name) {
        if (!ok) throw new AssertionError("FAIL: " + name);
    }

    public static void main(String[] args) {
        String env = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        String wrongEnv = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        RouteSelector.Route lan = new RouteSelector.Route("lan", "http://192.168.1.20:8722", 10, true);
        RouteSelector.Route ts = new RouteSelector.Route("tailscale", "http://100.88.1.5:8722", 20, true);
        RouteSelector.Route relay = new RouteSelector.Route("public", "https://relay.example/c/0123456789abcdef0123", 50, false);

        // 1. Fast LAN wins over relay
        RouteSelector selector = new RouteSelector();
        RouteSelector.Selection s1 = selector.selectBest(Arrays.asList(lan, relay), env, (route, timeoutMs) -> {
            if ("lan".equals(route.kind)) return RouteSelector.ProbeResult.ok(env, true, 8, env);
            return RouteSelector.ProbeResult.ok(env, true, 60, env);
        }, 1_000L, 120L, 600);
        expect(s1 != null && "lan".equals(s1.route.kind) && !s1.fallback, "fast LAN wins");

        // 2. Dead/slow LAN defers after head-start and immediately falls back to relay without waiting full timeout
        long start = System.currentTimeMillis();
        RouteSelector.Selection s2 = selector.selectBest(Arrays.asList(lan, relay), env, (route, timeoutMs) -> {
            if ("lan".equals(route.kind)) {
                try { Thread.sleep(1500); } catch (InterruptedException ignored) { }
                return RouteSelector.ProbeResult.unreachable();
            }
            return RouteSelector.ProbeResult.ok(env, true, 30, env);
        }, 2_000L, 120L, 1200);
        long elapsed = System.currentTimeMillis() - start;
        expect(s2 != null && "public".equals(s2.route.kind), "falls back to relay when LAN is slow");
        expect(elapsed < 800, "fallback must not block on full 1.2s LAN timeout, elapsed=" + elapsed);
        expect(selector.isBackedOff(lan.url, 2_000L + 10_000L), "silent LAN enters backoff");
        expect(!selector.isBackedOff(lan.url, 2_000L + RouteSelector.SILENT_BACKOFF_BASE_MS + 1L), "backoff expires after base window");

        // 3. Identity mismatch on LAN is strictly refused and falls through to verified relay
        RouteSelector idSelector = new RouteSelector();
        RouteSelector.Selection s3 = idSelector.selectBest(Arrays.asList(lan, relay), env, (route, timeoutMs) -> {
            if ("lan".equals(route.kind)) return RouteSelector.ProbeResult.ok(wrongEnv, true, 5, env);
            return RouteSelector.ProbeResult.ok(env, true, 40, env);
        }, 3_000L, 120L, 600);
        expect(s3 != null && "public".equals(s3.route.kind), "wrong environment_id on LAN must be refused");

        // 4. Cooldown prevents flapping after a failed switch; network change resets silent backoff
        RouteSelector flap = new RouteSelector();
        flap.recordSwitchFailure(lan.url, 10_000L);
        expect(flap.isInCooldown(lan.url, 10_000L + 60_000L), "cooldown active within 5 min");
        List<RouteSelector.Route> upgrades = flap.higherPriorityCandidates(relay, Arrays.asList(lan, ts, relay), 10_000L + 60_000L);
        expect(upgrades.size() == 1 && "tailscale".equals(upgrades.get(0).kind), "cooled-down LAN skipped, Tailscale eligible");
        expect(!flap.isInCooldown(lan.url, 10_000L + RouteSelector.COOLDOWN_MS + 1L), "cooldown clears after 5 min");

        long gen = flap.generation();
        flap.recordSilentFailure(ts.url, 20_000L, gen);
        expect(flap.isBackedOff(ts.url, 25_000L), "Tailscale backed off");
        flap.onNetworkChanged();
        expect(!flap.isBackedOff(ts.url, 25_000L), "network change clears silent backoff");
        flap.recordSilentFailure(ts.url, 25_000L, gen); // stale generation must be ignored
        expect(!flap.isBackedOff(ts.url, 26_000L), "stale probe failure from old generation ignored");

        // 5. Loopback routes rejected & HTTPS downgrade prevented without local credential
        RouteSelector.Route loopback = new RouteSelector.Route("lan", "http://127.0.0.1:8722", 1, true);
        List<RouteSelector.Route> mergedNoCred = RouteSelector.mergeRoutes(
            Collections.singletonList(relay),
            Arrays.asList(loopback, lan, ts),
            false
        );
        expect(mergedNoCred.size() == 1 && "public".equals(mergedNoCred.get(0).kind), "no unauthenticated HTTP downgrade & loopback rejected");

        List<RouteSelector.Route> mergedWithCred = RouteSelector.mergeRoutes(
            Collections.singletonList(relay),
            Arrays.asList(loopback, lan, ts),
            true
        );
        expect(mergedWithCred.size() == 3 && "lan".equals(mergedWithCred.get(0).kind) && "tailscale".equals(mergedWithCred.get(1).kind), "learned direct routes ordered ahead of relay");

        // 6. URL classification
        expect("public".equals(RouteSelector.classifyUrl("https://r.example/c/0123456789abcdef0123")), "classify public");
        expect("tunnel".equals(RouteSelector.classifyUrl("https://abc-123.trycloudflare.com")), "classify tunnel");
        expect("tailscale".equals(RouteSelector.classifyUrl("http://100.77.1.9:8722")), "classify tailscale IP");
        expect("tailscale".equals(RouteSelector.classifyUrl("http://dev.tail.ts.net:8722")), "classify MagicDNS");
        expect("lan".equals(RouteSelector.classifyUrl("http://192.168.1.5:8722")), "classify LAN");

        System.out.println("RouteSelector: 500ms head-start, identity verification, backoff, cooldown and route merge passed");
    }
}
