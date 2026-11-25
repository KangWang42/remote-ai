package io.github.kangwang42.remotecli;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkRequest;
import android.os.Build;
import android.os.SystemClock;
import android.webkit.CookieManager;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ExecutorService;
import org.json.JSONArray;
import org.json.JSONObject;

/**
 * Coordinates multi-route probing, authenticated direct-route learning, automatic cookie login for
 * learned LAN/Tailscale routes, and background upgrades on network changes.
 */
final class RouteCoordinator {
    private final Context context;
    private final RouteStore store;
    private final ExecutorService net;
    private final Map<String, RouteSelector> selectors = new HashMap<>();
    private final Set<String> learning = new HashSet<>();
    private final Set<String> upgrading = new HashSet<>();
    private ConnectivityManager.NetworkCallback networkCallback;
    private volatile Runnable networkListener;

    RouteCoordinator(Context context, ExecutorService net) {
        this.context = context.getApplicationContext();
        this.store = new RouteStore(this.context);
        this.net = net;
    }

    void setNetworkChangeListener(Runnable listener) {
        this.networkListener = listener;
    }

    void startMonitoring() {
        if (networkCallback != null || Build.VERSION.SDK_INT < 21) return;
        try {
            ConnectivityManager cm = (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
            if (cm == null) return;
            networkCallback = new ConnectivityManager.NetworkCallback() {
                @Override public void onAvailable(Network network) { onNetworkChanged(); }
                @Override public void onLost(Network network) { onNetworkChanged(); }
            };
            cm.registerNetworkCallback(new NetworkRequest.Builder().build(), networkCallback);
        } catch (Exception ignored) {
            networkCallback = null;
        }
    }

    void stopMonitoring() {
        if (networkCallback == null || Build.VERSION.SDK_INT < 21) return;
        try {
            ConnectivityManager cm = (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
            if (cm != null) cm.unregisterNetworkCallback(networkCallback);
        } catch (Exception ignored) { }
        networkCallback = null;
    }

    void onNetworkChanged() {
        synchronized (this) {
            for (RouteSelector selector : selectors.values()) selector.onNetworkChanged();
        }
        Runnable listener = networkListener;
        if (listener != null) listener.run();
    }

    /** Re-probes all multi-route computers in the background and runs `onSwitched` when any active route changes. */
    void reprobeAll(final List<String[]> records, final Runnable onSwitched) {
        if (records == null || records.isEmpty()) return;
        net.execute(() -> {
            boolean changed = false;
            for (String[] pair : records) {
                if (pair == null || pair.length < 2) continue;
                String id = pair[0], savedUrl = pair[1];
                if (id == null || id.isEmpty() || savedUrl == null || savedUrl.isEmpty()) continue;
                if (store.routes(id, savedUrl).size() <= 1) continue;
                String before = activeOrigin(id, savedUrl);
                String after = resolveBestOrigin(id, savedUrl, true);
                if (!after.equals(before)) changed = true;
            }
            if (changed && onSwitched != null) onSwitched.run();
        });
    }

    private synchronized RouteSelector selectorFor(String recordId) {
        RouteSelector s = selectors.get(recordId);
        if (s == null) { s = new RouteSelector(); selectors.put(recordId, s); }
        return s;
    }

    String activeOrigin(String recordId, String savedUrl) {
        if (recordId == null || recordId.isEmpty()) return savedUrl;
        String active = store.activeUrl(recordId, savedUrl);
        List<RouteSelector.Route> known = store.routes(recordId, savedUrl);
        for (RouteSelector.Route r : known) if (r.url.equals(active)) return active;
        return savedUrl;
    }

    String activeRouteSummary(String recordId, String savedUrl) {
        String active = activeOrigin(recordId, savedUrl);
        List<RouteSelector.Route> known = store.routes(recordId, savedUrl);
        String kind = RouteSelector.classifyUrl(active);
        for (RouteSelector.Route r : known) if (r.url.equals(active)) { kind = r.kind; break; }
        String label = RouteSelector.labelOf(kind);
        return known.size() > 1 ? label + " · 共 " + known.size() + " 条路由自动选路" : label;
    }

    void forget(String recordId) {
        if (recordId == null || recordId.isEmpty()) return;
        store.forget(recordId);
        synchronized (this) {
            selectors.remove(recordId);
            learning.remove(recordId);
            upgrading.remove(recordId);
        }
    }

    /** Selects the best reachable origin (500ms head start for LAN/Tailscale) and prepares its auth cookie. */
    String resolveBestOrigin(String recordId, String savedUrl, boolean forceReprobe) {
        if (recordId == null || recordId.isEmpty()) return savedUrl;
        List<RouteSelector.Route> routes = store.routes(recordId, savedUrl);
        if (routes.size() <= 1) return savedUrl;
        String current = activeOrigin(recordId, savedUrl);
        if (!forceReprobe && !current.isEmpty()) {
            // Fast path: keep current route unless a periodic upgrade check is due or it failed.
            return current;
        }
        final String expectedEnv = store.environmentId(recordId);
        RouteSelector selector = selectorFor(recordId);
        RouteSelector.Selection chosen = selector.selectBest(routes, expectedEnv,
            (route, timeoutMs) -> probeRoute(route, expectedEnv, timeoutMs), SystemClock.elapsedRealtime());
        if (chosen == null || chosen.route == null) return savedUrl;
        String target = chosen.route.url;
        if (chosen.route.localAuth && !ensureDirectCookie(recordId, target)) {
            selector.recordSwitchFailure(target, SystemClock.elapsedRealtime());
            return savedUrl;
        }
        store.setActiveUrl(recordId, target);
        return target;
    }

    /** Records a transport failure on `failedOrigin` and immediately tries to fail over to another route. */
    String failover(String recordId, String savedUrl, String failedOrigin) {
        if (recordId == null || recordId.isEmpty()) return savedUrl;
        RouteSelector selector = selectorFor(recordId);
        selector.recordSwitchFailure(failedOrigin, SystemClock.elapsedRealtime());
        return resolveBestOrigin(recordId, savedUrl, true);
    }

    /** Updates route knowledge from /api/terminal device metadata and schedules background upgrades. */
    void learnFromDevice(final String recordId, final String savedUrl, final String usedOrigin, JSONObject device) {
        if (recordId == null || recordId.isEmpty() || device == null) return;
        String envId = device.optString("environment_id");
        if (!envId.isEmpty()) store.saveEnvironmentId(recordId, envId);
        JSONArray rawRoutes = device.optJSONArray("routes");
        if (rawRoutes == null || rawRoutes.length() == 0) return;

        final List<RouteSelector.Route> learned = RouteStore.parseWireRoutes(rawRoutes);
        boolean hasDirect = false;
        java.util.List<RouteSelector.Route> safeRoutes = new java.util.ArrayList<>();
        for (RouteSelector.Route r : learned) {
            if (r.localAuth) hasDirect = true;
            else safeRoutes.add(r);
        }

        if (hasDirect && !store.hasLocalPassword(recordId)) {
            // Always persist non-local-auth routes (e.g. updated tunnel/relay URLs) right away.
            if (!safeRoutes.isEmpty()) store.saveRoutes(recordId, savedUrl, safeRoutes);
            boolean shouldFetch;
            synchronized (this) { shouldFetch = learning.add(recordId); }
            if (shouldFetch) {
                net.execute(() -> {
                    try {
                        fetchDirectRoutesAndSecret(recordId, savedUrl, usedOrigin);
                    } finally {
                        synchronized (RouteCoordinator.this) { learning.remove(recordId); }
                    }
                });
            }
            return;
        }

        store.saveRoutes(recordId, savedUrl, learned);
        maybeUpgradeInBackground(recordId, savedUrl, usedOrigin);
    }

    void maybeUpgradeInBackground(final String recordId, final String savedUrl, final String currentOrigin) {
        if (recordId == null || recordId.isEmpty()) return;
        final RouteSelector selector = selectorFor(recordId);
        long now = SystemClock.elapsedRealtime();
        if (!selector.shouldCheckUpgrade(now)) return;
        List<RouteSelector.Route> all = store.routes(recordId, savedUrl);
        if (all.size() <= 1) return;
        RouteSelector.Route active = null;
        for (RouteSelector.Route r : all) if (r.url.equals(currentOrigin)) { active = r; break; }
        if (active == null) active = new RouteSelector.Route(RouteSelector.classifyUrl(currentOrigin), currentOrigin, RouteSelector.defaultPriority(RouteSelector.classifyUrl(currentOrigin)), false);
        final List<RouteSelector.Route> better = selector.higherPriorityCandidates(active, all, now);
        if (better.isEmpty()) { selector.markUpgradeChecked(now); return; }

        synchronized (this) { if (!upgrading.add(recordId)) return; }
        selector.markUpgradeChecked(now);
        final long gen = selector.generation();
        final String expectedEnv = store.environmentId(recordId);
        net.execute(() -> {
            try {
                for (RouteSelector.Route candidate : better) {
                    RouteSelector.ProbeResult res = probeRoute(candidate, expectedEnv, RouteSelector.PROBE_TIMEOUT_MS);
                    if (res.reachable && !res.identityMismatch && res.online) {
                        if (!candidate.localAuth || ensureDirectCookie(recordId, candidate.url)) {
                            selector.recordProbeSuccess(candidate.url);
                            store.setActiveUrl(recordId, candidate.url);
                            return;
                        }
                        selector.recordSwitchFailure(candidate.url, SystemClock.elapsedRealtime());
                    } else if (!res.identityMismatch) {
                        selector.recordSilentFailure(candidate.url, SystemClock.elapsedRealtime(), gen);
                    }
                }
            } finally {
                synchronized (RouteCoordinator.this) { upgrading.remove(recordId); }
            }
        });
    }

    private void fetchDirectRoutesAndSecret(String recordId, String savedUrl, String usedOrigin) {
        String cookie = CookieManager.getInstance().getCookie(usedOrigin);
        if (cookie == null || cookie.isEmpty()) return;
        HttpURLConnection conn = null;
        try {
            JSONObject body = new JSONObject()
                .put("id", UUID.randomUUID().toString().replace("-", ""))
                .put("action", "routes_read");
            byte[] payload = body.toString().getBytes(StandardCharsets.UTF_8);
            conn = (HttpURLConnection) new URL(usedOrigin + "/api/computer").openConnection();
            conn.setRequestMethod("POST");
            conn.setDoOutput(true);
            conn.setConnectTimeout(5000);
            conn.setReadTimeout(10000);
            conn.setInstanceFollowRedirects(false);
            conn.setRequestProperty("Content-Type", "application/json");
            conn.setRequestProperty("Cookie", cookie);
            try (OutputStream out = conn.getOutputStream()) { out.write(payload); }
            if (conn.getResponseCode() != 200) return;
            JSONObject reply = new JSONObject(readSmallBody(conn, 65536));
            String envId = reply.optString("environment_id");
            if (!envId.isEmpty()) store.saveEnvironmentId(recordId, envId);
            String localPassword = reply.optString("local_password");
            if (!localPassword.isEmpty()) store.saveLocalPassword(recordId, localPassword);
            List<RouteSelector.Route> learned = RouteStore.parseWireRoutes(reply.optJSONArray("routes"));
            if (!learned.isEmpty()) store.saveRoutes(recordId, savedUrl, learned);
            maybeUpgradeInBackground(recordId, savedUrl, usedOrigin);
        } catch (Exception ignored) {
            // Older relays or offline computers simply skip direct route learning.
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    /** Ensures the direct origin has a valid session cookie using the encrypted local credential. */
    boolean ensureDirectCookie(String recordId, String origin) {
        CookieManager cookies = CookieManager.getInstance();
        String existing = cookies.getCookie(origin);
        if (existing != null && existing.contains("rcli=")) return true;
        String secret = store.readLocalPassword(recordId);
        if (secret.isEmpty()) return false;
        HttpURLConnection conn = null;
        try {
            byte[] payload = new JSONObject().put("password", secret).toString().getBytes(StandardCharsets.UTF_8);
            conn = (HttpURLConnection) new URL(origin + "/api/login").openConnection();
            conn.setRequestMethod("POST");
            conn.setDoOutput(true);
            conn.setConnectTimeout(2500);
            conn.setReadTimeout(3000);
            conn.setInstanceFollowRedirects(false);
            conn.setRequestProperty("Content-Type", "application/json");
            try (OutputStream out = conn.getOutputStream()) { out.write(payload); }
            if (conn.getResponseCode() != 200) return false;
            String setCookie = conn.getHeaderField("Set-Cookie");
            if (setCookie != null && !setCookie.isEmpty()) {
                cookies.setCookie(origin, setCookie);
                cookies.flush();
                return true;
            }
            return false;
        } catch (Exception failed) {
            return false;
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    static RouteSelector.ProbeResult probeRoute(RouteSelector.Route route, String expectedEnvId, int timeoutMs) {
        if (route == null || !RouteSelector.isValidRouteUrl(route.url)) return RouteSelector.ProbeResult.unreachable();
        long started = SystemClock.elapsedRealtime();
        HttpURLConnection conn = null;
        try {
            conn = (HttpURLConnection) new URL(route.url + "/.well-known/remote-ai").openConnection();
            conn.setConnectTimeout(timeoutMs);
            conn.setReadTimeout(timeoutMs);
            conn.setInstanceFollowRedirects(false);
            conn.setUseCaches(false);
            int code = conn.getResponseCode();
            if (code != 200) return RouteSelector.ProbeResult.unreachable();
            JSONObject doc = new JSONObject(readSmallBody(conn, 16384));
            if (!"remote-ai".equals(doc.optString("service"))) return RouteSelector.ProbeResult.unreachable();
            long elapsed = Math.max(1L, SystemClock.elapsedRealtime() - started);
            return RouteSelector.ProbeResult.ok(doc.optString("environment_id"), doc.optBoolean("online"), elapsed, expectedEnvId);
        } catch (Exception unreachable) {
            return RouteSelector.ProbeResult.unreachable();
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    private static String readSmallBody(HttpURLConnection conn, int limit) throws Exception {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        try (InputStream input = conn.getInputStream()) {
            byte[] buf = new byte[4096];
            int n;
            while ((n = input.read(buf)) > 0) {
                if (bytes.size() + n > limit) throw new IllegalStateException("response too large");
                bytes.write(buf, 0, n);
            }
        }
        return bytes.toString("UTF-8");
    }
}
