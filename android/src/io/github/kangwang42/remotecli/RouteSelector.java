package io.github.kangwang42.remotecli;

import java.net.URI;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.Callable;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;

/**
 * Priority-ordered multi-route selection with a shared 500ms head start for fast local routes,
 * environment identity verification before sending credentials, exponential silent-route backoff,
 * and 5-minute anti-flapping cooldown on failed switches. Pure Java for standalone testing.
 */
final class RouteSelector {
    static final long HEAD_START_MS = 500L;
    static final int PROBE_TIMEOUT_MS = 2500;
    static final long UPGRADE_INTERVAL_MS = 60_000L;
    static final long COOLDOWN_MS = 300_000L;
    static final long SILENT_BACKOFF_BASE_MS = 60_000L;
    static final long SILENT_BACKOFF_MAX_MS = 1_800_000L;

    static final class Route {
        final String kind;
        final String url;
        final int priority;
        final boolean localAuth;

        Route(String kind, String url, int priority, boolean localAuth) {
            this.kind = normalizeKind(kind, url);
            this.url = cleanUrl(url);
            this.priority = priority >= 0 ? priority : defaultPriority(this.kind);
            this.localAuth = localAuth;
        }

        boolean isDirect() { return "lan".equals(kind) || "tailscale".equals(kind); }
        boolean isHttps() { return url.startsWith("https://"); }
    }

    static final class ProbeResult {
        final boolean reachable;
        final String environmentId;
        final boolean online;
        final long latencyMs;
        final boolean identityMismatch;

        ProbeResult(boolean reachable, String environmentId, boolean online, long latencyMs, boolean identityMismatch) {
            this.reachable = reachable;
            this.environmentId = environmentId == null ? "" : environmentId;
            this.online = online;
            this.latencyMs = latencyMs;
            this.identityMismatch = identityMismatch;
        }

        static ProbeResult unreachable() { return new ProbeResult(false, "", false, -1, false); }
        static ProbeResult ok(String envId, boolean online, long latencyMs, String expectedEnvId) {
            boolean mismatch = expectedEnvId != null && !expectedEnvId.isEmpty()
                && envId != null && !envId.isEmpty()
                && !expectedEnvId.equalsIgnoreCase(envId);
            return new ProbeResult(!mismatch, envId, online, latencyMs, mismatch);
        }
    }

    interface Prober {
        ProbeResult probe(Route route, int timeoutMs);
    }

    static final class Selection {
        final Route route;
        final ProbeResult probe;
        final boolean fallback;

        Selection(Route route, ProbeResult probe, boolean fallback) {
            this.route = route;
            this.probe = probe;
            this.fallback = fallback;
        }
    }

    private static final class SilentState {
        int failures;
        long retryAtMs;
    }

    private final Map<String, SilentState> silentRoutes = new HashMap<>();
    private final Map<String, Long> cooldownUntil = new HashMap<>();
    private long networkGeneration;
    private long lastUpgradeCheckMs;

    static int defaultPriority(String kind) {
        if ("lan".equals(kind)) return 10;
        if ("tailscale".equals(kind)) return 20;
        if ("tunnel".equals(kind)) return 30;
        if ("own".equals(kind)) return 40;
        if ("public".equals(kind)) return 50;
        return 60;
    }

    static String labelOf(String kind) {
        if ("lan".equals(kind)) return "局域网直连";
        if ("tailscale".equals(kind)) return "Tailscale";
        if ("tunnel".equals(kind)) return "公网隧道";
        if ("public".equals(kind)) return "公共中转";
        if ("own".equals(kind)) return "自有中转";
        return "直连";
    }

    static String cleanUrl(String raw) {
        if (raw == null) return "";
        String trimmed = raw.trim();
        while (trimmed.endsWith("/")) trimmed = trimmed.substring(0, trimmed.length() - 1);
        return trimmed;
    }

    /** Loopback addresses name the phone itself and must never enter a remote computer's route table. */
    static boolean isLoopbackUrl(String url) {
        try {
            URI uri = new URI(url);
            String host = uri.getHost();
            if (host == null) return true;
            String lower = host.toLowerCase(Locale.ROOT);
            return "localhost".equals(lower) || "127.0.0.1".equals(lower) || "::1".equals(lower) || "[::1]".equals(lower) || lower.startsWith("127.");
        } catch (Exception invalid) {
            return true;
        }
    }

    static boolean isValidRouteUrl(String url) {
        if (url == null || url.isEmpty() || url.length() > 260) return false;
        if (!url.startsWith("http://") && !url.startsWith("https://")) return false;
        return !isLoopbackUrl(url);
    }

    static String normalizeKind(String kind, String url) {
        if ("lan".equals(kind) || "tailscale".equals(kind) || "tunnel".equals(kind) || "own".equals(kind) || "public".equals(kind)) return kind;
        return classifyUrl(url);
    }

    static String classifyUrl(String url) {
        try {
            URI uri = new URI(cleanUrl(url));
            String host = uri.getHost() == null ? "" : uri.getHost().toLowerCase(Locale.ROOT);
            String path = uri.getPath() == null ? "" : uri.getPath();
            if (path.matches("/c/[a-f0-9]{20}")) return "public";
            if (host.endsWith(".trycloudflare.com")) return "tunnel";
            if (host.endsWith(".ts.net") || isCgnatHost(host)) return "tailscale";
            if (isPrivateLanHost(host)) return "lan";
            return "own";
        } catch (Exception invalid) {
            return "own";
        }
    }

    private static boolean isCgnatHost(String host) {
        String[] parts = host.split("\\.");
        if (parts.length != 4) return false;
        try {
            int a = Integer.parseInt(parts[0]), b = Integer.parseInt(parts[1]);
            return a == 100 && b >= 64 && b <= 127;
        } catch (NumberFormatException ignored) { return false; }
    }

    private static boolean isPrivateLanHost(String host) {
        String[] parts = host.split("\\.");
        if (parts.length != 4) return false;
        try {
            int a = Integer.parseInt(parts[0]), b = Integer.parseInt(parts[1]);
            return a == 10 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168);
        } catch (NumberFormatException ignored) { return false; }
    }

    /**
     * Merges saved and learned routes into a deduplicated, priority-sorted list while filtering out loopback
     * and refusing insecure downgrade of HTTPS-only credentials to unknown plain-HTTP non-local routes.
     */
    static List<Route> mergeRoutes(List<Route> existing, List<Route> learned, boolean hasLocalCredential) {
        Map<String, Route> byUrl = new HashMap<>();
        if (existing != null) {
            for (Route r : existing) {
                if (r == null || !isValidRouteUrl(r.url)) continue;
                byUrl.put(r.url, r);
            }
        }
        if (learned != null) {
            for (Route r : learned) {
                if (r == null || !isValidRouteUrl(r.url)) continue;
                if (r.localAuth && !hasLocalCredential && !r.isHttps() && !byUrl.containsKey(r.url)) {
                    // Keep the discovered direct route metadata only when we have a dedicated local credential or already saved it.
                    continue;
                }
                byUrl.put(r.url, r);
            }
        }
        List<Route> out = new ArrayList<>(byUrl.values());
        Collections.sort(out, new Comparator<Route>() {
            @Override public int compare(Route a, Route b) {
                if (a.priority != b.priority) return Integer.compare(a.priority, b.priority);
                return a.url.compareTo(b.url);
            }
        });
        return out;
    }

    synchronized void onNetworkChanged() {
        networkGeneration++;
        silentRoutes.clear();
        lastUpgradeCheckMs = 0L;
    }

    synchronized long generation() { return networkGeneration; }

    synchronized void recordSilentFailure(String url, long nowMs, long generation) {
        if (generation != networkGeneration) return;
        SilentState state = silentRoutes.get(url);
        if (state == null) { state = new SilentState(); silentRoutes.put(url, state); }
        state.failures++;
        long shift = Math.min(state.failures - 1, 10);
        long delay = Math.min(SILENT_BACKOFF_MAX_MS, SILENT_BACKOFF_BASE_MS << shift);
        state.retryAtMs = nowMs + delay;
    }

    synchronized void recordProbeSuccess(String url) {
        silentRoutes.remove(url);
    }

    synchronized void recordSwitchFailure(String url, long nowMs) {
        cooldownUntil.put(url, nowMs + COOLDOWN_MS);
    }

    synchronized boolean isInCooldown(String url, long nowMs) {
        Long until = cooldownUntil.get(url);
        if (until == null) return false;
        if (nowMs >= until) { cooldownUntil.remove(url); return false; }
        return true;
    }

    synchronized boolean isBackedOff(String url, long nowMs) {
        SilentState state = silentRoutes.get(url);
        return state != null && nowMs < state.retryAtMs;
    }

    synchronized boolean shouldCheckUpgrade(long nowMs) {
        return nowMs - lastUpgradeCheckMs >= UPGRADE_INTERVAL_MS;
    }

    synchronized void markUpgradeChecked(long nowMs) {
        lastUpgradeCheckMs = nowMs;
    }

    synchronized List<Route> higherPriorityCandidates(Route active, List<Route> all, long nowMs) {
        List<Route> result = new ArrayList<>();
        if (active == null || all == null) return result;
        for (Route r : all) {
            if (r == null || r.url.equals(active.url)) continue;
            if (r.priority >= active.priority) continue;
            if (isInCooldown(r.url, nowMs) || isBackedOff(r.url, nowMs)) continue;
            result.add(r);
        }
        return result;
    }

    /**
     * Evaluates ordered routes in parallel with a shared 500ms head-start window for higher-priority routes.
     * Dead LAN routes are deferred after 500ms so fallback to Tailscale or relay happens immediately without
     * blocking for the full 2.5s socket timeout.
     */
    Selection selectBest(List<Route> routes, String expectedEnvironmentId, final Prober prober, long nowMs) {
        return selectBest(routes, expectedEnvironmentId, prober, nowMs, HEAD_START_MS, PROBE_TIMEOUT_MS);
    }

    Selection selectBest(List<Route> routes, String expectedEnvironmentId, final Prober prober, long nowMs, long headStartMs, int probeTimeoutMs) {
        if (routes == null || routes.isEmpty()) return null;
        if (routes.size() == 1) return new Selection(routes.get(0), ProbeResult.unreachable(), true);

        List<Route> candidates = new ArrayList<>();
        for (Route r : routes) if (!isInCooldown(r.url, nowMs)) candidates.add(r);
        if (candidates.isEmpty()) candidates.addAll(routes);

        long gen = generation();
        ExecutorService pool = Executors.newFixedThreadPool(Math.min(candidates.size(), 4));
        Map<String, Future<ProbeResult>> probes = new HashMap<>();
        try {
            for (final Route route : candidates) {
                probes.put(route.url, pool.submit(new Callable<ProbeResult>() {
                    @Override public ProbeResult call() {
                        try {
                            ProbeResult res = prober.probe(route, probeTimeoutMs);
                            return res == null ? ProbeResult.unreachable() : res;
                        } catch (Exception failed) {
                            return ProbeResult.unreachable();
                        }
                    }
                }));
            }

            long headStartDeadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(Math.max(1L, headStartMs));
            List<Route> deferred = new ArrayList<>();
            Selection offlineCandidate = null;
            Set<String> rejectedIds = new HashSet<>();

            for (int i = 0; i < candidates.size(); i++) {
                Route route = candidates.get(i);
                Future<ProbeResult> future = probes.get(route.url);
                boolean hasFallbackAfter = i < candidates.size() - 1;
                long waitNs = hasFallbackAfter ? Math.max(0L, headStartDeadline - System.nanoTime())
                    : TimeUnit.MILLISECONDS.toNanos(probeTimeoutMs);
                try {
                    ProbeResult res = future.get(waitNs, TimeUnit.NANOSECONDS);
                    if (res.identityMismatch) {
                        rejectedIds.add(route.url);
                        continue;
                    }
                    if (res.reachable) {
                        recordProbeSuccess(route.url);
                        if (res.online) return new Selection(route, res, false);
                        if (offlineCandidate == null) offlineCandidate = new Selection(route, res, false);
                    }
                } catch (TimeoutException slow) {
                    deferred.add(route);
                    recordSilentFailure(route.url, nowMs, gen);
                } catch (Exception ignored) { }
            }

            if (offlineCandidate != null) return offlineCandidate;

            // All fast routes failed or returned offline: collect any late answers from deferred routes.
            long remainingMs = Math.max(100L, probeTimeoutMs - headStartMs);
            long finalDeadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(remainingMs);
            for (Route route : deferred) {
                Future<ProbeResult> future = probes.get(route.url);
                long waitNs = Math.max(0L, finalDeadline - System.nanoTime());
                try {
                    ProbeResult res = future.get(waitNs, TimeUnit.NANOSECONDS);
                    if (res.identityMismatch) { rejectedIds.add(route.url); continue; }
                    if (res.reachable) {
                        recordProbeSuccess(route.url);
                        if (res.online) return new Selection(route, res, false);
                        if (offlineCandidate == null) offlineCandidate = new Selection(route, res, false);
                    }
                } catch (Exception ignored) { }
            }

            if (offlineCandidate != null) return offlineCandidate;
            for (Route r : candidates) if (!rejectedIds.contains(r.url)) return new Selection(r, ProbeResult.unreachable(), true);
            return null;
        } finally {
            for (Future<ProbeResult> f : probes.values()) f.cancel(true);
            pool.shutdownNow();
        }
    }
}
