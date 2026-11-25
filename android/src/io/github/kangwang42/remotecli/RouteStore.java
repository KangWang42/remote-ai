package io.github.kangwang42.remotecli;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import org.json.JSONArray;
import org.json.JSONObject;

/**
 * Stores learned direct routes and encrypted local credentials per saved computer record.
 * Local credentials are encrypted with Android Keystore AES-GCM tied to the computer record ID.
 */
final class RouteStore {
    private static final String ALIAS = "remote-ai-route-secrets";
    private final SharedPreferences prefs;

    RouteStore(Context context) {
        prefs = context.getSharedPreferences("remote-ai-routes", Context.MODE_PRIVATE);
    }

    private SecretKey key() throws Exception {
        KeyStore keys = KeyStore.getInstance("AndroidKeyStore");
        keys.load(null);
        if (!keys.containsAlias(ALIAS)) {
            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build());
            generator.generateKey();
        }
        return (SecretKey) keys.getKey(ALIAS, null);
    }

    String environmentId(String recordId) {
        if (recordId == null || recordId.isEmpty()) return "";
        return prefs.getString("env-" + recordId, "");
    }

    void saveEnvironmentId(String recordId, String envId) {
        if (recordId == null || recordId.isEmpty() || envId == null || !envId.matches("[a-f0-9]{32}")) return;
        if (!envId.equals(environmentId(recordId))) prefs.edit().putString("env-" + recordId, envId).apply();
    }

    String activeUrl(String recordId, String fallbackUrl) {
        if (recordId == null || recordId.isEmpty()) return fallbackUrl;
        String active = prefs.getString("active-" + recordId, "");
        return active.isEmpty() ? fallbackUrl : active;
    }

    void setActiveUrl(String recordId, String url) {
        if (recordId == null || recordId.isEmpty() || url == null || url.isEmpty()) return;
        prefs.edit().putString("active-" + recordId, url).apply();
    }

    boolean hasLocalPassword(String recordId) {
        return recordId != null && !recordId.isEmpty() && prefs.contains("secret-" + recordId);
    }

    void saveLocalPassword(String recordId, String password) {
        if (recordId == null || recordId.isEmpty() || password == null || password.isEmpty()) return;
        try {
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.ENCRYPT_MODE, key());
            cipher.updateAAD(recordId.getBytes(StandardCharsets.UTF_8));
            String encoded = Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP) + ":"
                + Base64.encodeToString(cipher.doFinal(password.getBytes(StandardCharsets.UTF_8)), Base64.NO_WRAP);
            prefs.edit().putString("secret-" + recordId, encoded).apply();
        } catch (Exception ignored) { }
    }

    String readLocalPassword(String recordId) {
        if (recordId == null || recordId.isEmpty()) return "";
        String raw = prefs.getString("secret-" + recordId, "");
        String[] parts = raw.split(":", -1);
        if (parts.length != 2) return "";
        try {
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, Base64.decode(parts[0], Base64.NO_WRAP)));
            cipher.updateAAD(recordId.getBytes(StandardCharsets.UTF_8));
            return new String(cipher.doFinal(Base64.decode(parts[1], Base64.NO_WRAP)), StandardCharsets.UTF_8);
        } catch (Exception broken) {
            return "";
        }
    }

    List<RouteSelector.Route> routes(String recordId, String primaryUrl) {
        List<RouteSelector.Route> list = new ArrayList<>();
        if (RouteSelector.isValidRouteUrl(primaryUrl)) {
            String kind = RouteSelector.classifyUrl(primaryUrl);
            list.add(new RouteSelector.Route(kind, primaryUrl, RouteSelector.defaultPriority(kind), "lan".equals(kind) || "tailscale".equals(kind)));
        }
        if (recordId == null || recordId.isEmpty()) return list;
        try {
            JSONArray saved = new JSONArray(prefs.getString("routes-" + recordId, "[]"));
            List<RouteSelector.Route> learned = new ArrayList<>();
            for (int i = 0; i < saved.length(); i++) {
                JSONObject item = saved.optJSONObject(i);
                if (item == null) continue;
                String url = item.optString("url");
                if (!RouteSelector.isValidRouteUrl(url)) continue;
                String kind = item.optString("kind");
                int priority = item.optInt("priority", RouteSelector.defaultPriority(kind));
                boolean localAuth = item.optBoolean("local_auth", false);
                learned.add(new RouteSelector.Route(kind, url, priority, localAuth));
            }
            return RouteSelector.mergeRoutes(list, learned, hasLocalPassword(recordId));
        } catch (Exception ignored) {
            return list;
        }
    }

    void saveRoutes(String recordId, String primaryUrl, List<RouteSelector.Route> learned) {
        if (recordId == null || recordId.isEmpty()) return;
        List<RouteSelector.Route> existing = routes(recordId, primaryUrl);
        List<RouteSelector.Route> merged = RouteSelector.mergeRoutes(existing, learned, hasLocalPassword(recordId));
        JSONArray array = new JSONArray();
        for (RouteSelector.Route r : merged) {
            try {
                array.put(new JSONObject().put("kind", r.kind).put("url", r.url).put("priority", r.priority).put("local_auth", r.localAuth));
            } catch (Exception ignored) { }
        }
        prefs.edit().putString("routes-" + recordId, array.toString()).apply();
    }

    void forget(String recordId) {
        if (recordId == null || recordId.isEmpty()) return;
        prefs.edit()
            .remove("env-" + recordId)
            .remove("active-" + recordId)
            .remove("routes-" + recordId)
            .remove("secret-" + recordId)
            .apply();
    }

    static List<RouteSelector.Route> parseWireRoutes(JSONArray raw) {
        if (raw == null || raw.length() == 0) return Collections.emptyList();
        List<RouteSelector.Route> out = new ArrayList<>();
        for (int i = 0; i < raw.length(); i++) {
            JSONObject item = raw.optJSONObject(i);
            if (item == null) continue;
            String url = item.optString("url");
            if (!RouteSelector.isValidRouteUrl(url)) continue;
            String kind = item.optString("kind");
            int priority = item.optInt("priority", RouteSelector.defaultPriority(kind));
            boolean localAuth = item.optBoolean("local_auth", false);
            out.add(new RouteSelector.Route(kind, url, priority, localAuth));
        }
        return out;
    }
}
