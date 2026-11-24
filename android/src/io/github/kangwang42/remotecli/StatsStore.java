package io.github.kangwang42.remotecli;

import android.content.Context;
import android.content.SharedPreferences;
import java.util.Collection;
import java.util.HashSet;
import java.util.Iterator;
import java.util.Map;
import java.util.Set;
import org.json.JSONArray;
import org.json.JSONObject;

/**
 * Local persistence cache for per-computer work statistics so app updates, tab switches, and
 * temporary computer restarts never wipe or reset the daily summary on Android.
 */
final class StatsStore {
    private static final String PREFS = "remote-ai-stats";
    private static final String PREFIX = "snap-";
    private final SharedPreferences prefs;

    static final class Entry {
        final JSONObject raw;
        final StatsRollup.ComputerSnapshot snapshot;
        final long savedAtMs;

        Entry(JSONObject raw, StatsRollup.ComputerSnapshot snapshot, long savedAtMs) {
            this.raw = raw;
            this.snapshot = snapshot;
            this.savedAtMs = savedAtMs;
        }
    }

    StatsStore(Context context) {
        this.prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    private static String key(String url) {
        return PREFIX + (url == null ? "" : url.trim());
    }

    void save(String url, JSONObject data, long savedAtMs) {
        if (url == null || url.isEmpty() || data == null) return;
        try {
            JSONObject envelope = new JSONObject();
            envelope.put("savedAt", savedAtMs);
            envelope.put("data", data);
            prefs.edit().putString(key(url), envelope.toString()).apply();
        } catch (Exception ignored) { }
    }

    Entry load(String url) {
        if (url == null || url.isEmpty()) return null;
        String text = prefs.getString(key(url), "");
        if (text == null || text.isEmpty()) return null;
        try {
            JSONObject envelope = new JSONObject(text);
            JSONObject data = envelope.optJSONObject("data");
            if (data == null) return null;
            long savedAt = envelope.optLong("savedAt", data.optLong("updated", 0));
            return new Entry(data, parse(data, savedAt), savedAt);
        } catch (Exception ignored) {
            return null;
        }
    }

    void prune(Collection<String> activeUrls) {
        Set<String> keep = new HashSet<>();
        if (activeUrls != null) for (String url : activeUrls) keep.add(key(url));
        SharedPreferences.Editor edit = prefs.edit();
        boolean changed = false;
        for (Map.Entry<String, ?> entry : prefs.getAll().entrySet()) {
            String k = entry.getKey();
            if (k != null && k.startsWith(PREFIX) && !keep.contains(k)) {
                edit.remove(k);
                changed = true;
            }
        }
        if (changed) edit.apply();
    }

    static StatsRollup.ComputerSnapshot parse(JSONObject data, long fallbackUpdatedMs) {
        StatsRollup.ComputerSnapshot snap = new StatsRollup.ComputerSnapshot();
        if (data == null) return snap;
        snap.streak = Math.max(0, data.optInt("streak", 0));
        snap.bestStreak = Math.max(0, data.optInt("best_streak", 0));
        snap.updatedMs = data.optLong("updated", fallbackUpdatedMs);
        JSONArray days = data.optJSONArray("days");
        if (days == null) return snap;
        for (int i = 0; i < days.length(); i++) {
            JSONObject obj = days.optJSONObject(i);
            if (obj == null) continue;
            StatsRollup.DayPoint pt = new StatsRollup.DayPoint(obj.optString("date", ""));
            pt.prompts = obj.optLong("prompts", 0);
            pt.sessions = obj.optLong("sessions", 0);
            pt.activeSec = Math.round(obj.optDouble("active", 0));
            pt.inTokens = obj.optLong("in", 0);
            pt.outTokens = obj.optLong("out", 0);
            pt.cachedTokens = obj.optLong("cached", 0);
            JSONObject tools = obj.optJSONObject("tools");
            if (tools != null) {
                Iterator<String> keys = tools.keys();
                while (keys.hasNext()) {
                    String tool = keys.next();
                    JSONObject t = tools.optJSONObject(tool);
                    if (t != null) {
                        pt.addTool(tool, t.optLong("prompts", 0), t.optLong("active", 0), t.optLong("tokens", 0), t.optLong("cached", 0));
                    }
                }
            }
            snap.days.add(pt);
        }
        return snap;
    }
}
