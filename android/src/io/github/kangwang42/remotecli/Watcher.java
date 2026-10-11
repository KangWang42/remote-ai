package io.github.kangwang42.remotecli;

import android.app.AlarmManager;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;
import android.os.PowerManager;
import android.os.SystemClock;
import android.webkit.CookieManager;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import org.json.JSONArray;
import org.json.JSONObject;

/**
 * Looks at the computers now and then while the app is not on the screen, and tells the person when a task begins to
 * wait on a question or finishes: one notification for each, and none that stays. The system wakes the app for each
 * look, about once a minute (less often while the phone sleeps deeply); nothing of the app runs in between, so there
 * is no service and no notification saying that one runs. A look asks each computer for its overview, the same
 * request the workbench makes; nothing is sent to any other service. The looks begin when the app leaves the screen,
 * only when the person turned reminders on, and end when the app comes back, when nothing has been running for five
 * minutes, or after eight hours.
 */
public final class Watcher extends BroadcastReceiver {
    private static final long FIRST = 20_000, EVERY = 60_000, LONGEST = 8 * 3600_000L;
    private static final int QUIET_LOOKS = 5;
    private static final String CHANNEL_TASKS = "tasks", STATE = "remote-cli-watch";

    static void start(Context context) {
        try {
            NotificationManager manager = context.getSystemService(NotificationManager.class);
            manager.createNotificationChannel(new NotificationChannel(CHANNEL_TASKS, "任务提醒", NotificationManager.IMPORTANCE_HIGH));
            manager.deleteNotificationChannel("watch");       // of the notification that stayed while a service ran, in earlier versions
            // What each task was doing is kept between the looks: the app is not running then.
            context.getSharedPreferences(STATE, Context.MODE_PRIVATE).edit().clear().putLong("began", System.currentTimeMillis()).apply();
            schedule(context, FIRST);
        } catch (Exception refused) { /* the system does not allow it right now: no reminders this time */ }
    }
    static void stop(Context context) {
        try {
            context.getSystemService(AlarmManager.class).cancel(wake(context));
            context.getSharedPreferences(STATE, Context.MODE_PRIVATE).edit().clear().apply();
        } catch (Exception ignored) { }
    }
    private static PendingIntent wake(Context context) {
        return PendingIntent.getBroadcast(context, 0, new Intent(context, Watcher.class), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }
    private static void schedule(Context context, long delay) {
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        long at = SystemClock.elapsedRealtime() + delay;
        try {
            if (Build.VERSION.SDK_INT < 31 || alarms.canScheduleExactAlarms()) { alarms.setExactAndAllowWhileIdle(AlarmManager.ELAPSED_REALTIME_WAKEUP, at, wake(context)); return; }
        } catch (SecurityException refused) { /* the system leaves the time to itself */ }
        alarms.setAndAllowWhileIdle(AlarmManager.ELAPSED_REALTIME_WAKEUP, at, wake(context));
    }

    @Override public void onReceive(Context given, Intent intent) {
        final Context context = given.getApplicationContext();
        final PendingResult result = goAsync();
        // The phone may go back to sleep as soon as this method returns; the look takes a few seconds more.
        final PowerManager.WakeLock awake = context.getSystemService(PowerManager.class).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "remote-cli:watch");
        try { awake.acquire(20_000); } catch (Exception refused) { /* looked at while the phone happens to be awake */ }
        new Thread(() -> {
            try { look(context); }
            catch (Exception broken) { /* looked at again at the next wake */ }
            finally {
                try { if (awake.isHeld()) awake.release(); } catch (Exception ignored) { }
                result.finish();
            }
        }, "remote-cli-watch").start();
    }

    private static PendingIntent open(Context context) {
        Intent intent = new Intent(context, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        return PendingIntent.getActivity(context, 0, intent, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }

    private static void look(Context context) throws Exception {
        SharedPreferences state = context.getSharedPreferences(STATE, Context.MODE_PRIVATE);
        final long began = state.getLong("began", 0);
        if (began == 0) return;       // the app is on the screen again: this wake was on its way already
        JSONObject kept = new JSONObject(state.getString("phases", "{}"));
        JSONArray computers = new JSONArray(context.getSharedPreferences("remote-cli", Context.MODE_PRIVATE).getString("computers", "[]"));
        // Every computer is asked at the same time: the system allows a wake only a few seconds.
        final JSONObject[] answers = new JSONObject[computers.length()];
        List<Thread> asking = new ArrayList<>();
        for (int i = 0; i < computers.length(); i++) {
            final int at = i;
            final JSONObject computer = computers.optJSONObject(i);
            if (computer == null) continue;
            Thread one = new Thread(() -> answers[at] = overview(computer.optString("url")));
            one.start(); asking.add(one);
        }
        long until = SystemClock.elapsedRealtime() + 9000;
        for (Thread one : asking) one.join(Math.max(1, until - SystemClock.elapsedRealtime()));
        boolean anything = false;
        JSONObject now = new JSONObject();
        for (int i = 0; i < computers.length(); i++) {
            JSONObject computer = computers.optJSONObject(i), data = answers[i];
            if (computer == null) continue;
            String url = computer.optString("url");
            JSONObject device = data == null ? null : data.optJSONObject("device");
            JSONObject noted = kept.optJSONObject(url);
            if (device == null || !device.optBoolean("online") || !device.optBoolean("enabled")) {
                if (noted != null) now.put(url, noted);        // not reached this time: what was known stays
                anything |= noted != null && noted.length() > 0;
                continue;
            }
            List<Watch.Task> tasks = new ArrayList<>();
            JSONArray terminals = data.optJSONArray("terminals");
            for (int k = 0; terminals != null && k < terminals.length(); k++) {
                JSONObject t = terminals.optJSONObject(k);
                if (t == null || !("running".equals(t.optString("state")) || "starting".equals(t.optString("state")))) continue;
                tasks.add(new Watch.Task(t.optString("id"), t.optString("title"), t.optString("dir"),
                    "starting".equals(t.optString("state")) ? "starting" : t.optString("phase"), t.optBoolean("done")));
            }
            // A conversation open on the computer, of which its tool tells, is watched like a terminal: one that asks,
            // and one that has stopped and waits for a message, are told.
            JSONArray sessions = data.optJSONArray("sessions");
            for (int k = 0; sessions != null && k < sessions.length(); k++) {
                JSONObject s = sessions.optJSONObject(k);
                if (s == null || !s.optBoolean("live") || s.optJSONArray("reach") == null) continue;
                String status = s.optString("status");
                tasks.add(new Watch.Task(s.optString("id"), s.optString("title"), s.optString("dir"),
                    "confirm".equals(status) ? "confirm" : "busy".equals(status) ? "busy" : "idle", "waiting".equals(status)));
            }
            anything |= !tasks.isEmpty();
            Map<String, String> before = new HashMap<>();
            if (noted != null) for (Iterator<String> keys = noted.keys(); keys.hasNext(); ) { String key = keys.next(); before.put(key, noted.optString(key)); }
            String name = computer.optString("name").isEmpty() ? MainActivity.host(url) : computer.optString("name");
            for (Watch.Alert alert : Watch.news(tasks, before)) tell(context, url, name, alert);
            now.put(url, new JSONObject(before));
        }
        int quiet = anything ? 0 : state.getInt("quiet", 0) + 1;
        // The app came back, or left again, while this look was under way: its own start and stop decide what follows.
        if (context.getSharedPreferences(STATE, Context.MODE_PRIVATE).getLong("began", 0) != began) return;
        if (quiet >= QUIET_LOOKS || System.currentTimeMillis() - began > LONGEST) { state.edit().clear().apply(); return; }
        state.edit().putString("phases", now.toString()).putInt("quiet", quiet).apply();
        schedule(context, EVERY);
    }

    private static void tell(Context context, String url, String computer, Watch.Alert alert) {
        boolean asks = "confirm".equals(alert.kind);
        String title = alert.title.isEmpty() ? alert.dir : alert.title;
        try {
            NotificationManager manager = context.getSystemService(NotificationManager.class);
            manager.createNotificationChannel(new NotificationChannel(CHANNEL_TASKS, "任务提醒", NotificationManager.IMPORTANCE_HIGH));
            Notification notification = new Notification.Builder(context, CHANNEL_TASKS).setSmallIcon(R.drawable.ic_shell)
                .setContentTitle((asks ? "等你确认 · " : "已完成 · ") + computer)
                .setContentText(title + (alert.dir.isEmpty() || alert.dir.equals(title) ? "" : " · " + alert.dir))
                .setAutoCancel(true).setContentIntent(open(context)).build();
            manager.notify((url + alert.id).hashCode(), notification);
        } catch (Exception refused) { /* notifications are not allowed: the person will see it in the app */ }
    }

    /** The overview of one computer, signed in with the cookie its pages keep; null when it cannot be had. */
    private static JSONObject overview(String url) {
        HttpURLConnection connection = null;
        try {
            String cookie = CookieManager.getInstance().getCookie(url);
            connection = (HttpURLConnection) new URL(url + "/api/terminal").openConnection();
            connection.setConnectTimeout(4000); connection.setReadTimeout(5000); connection.setUseCaches(false);
            connection.setInstanceFollowRedirects(false);
            if (cookie != null) connection.setRequestProperty("Cookie", cookie);
            if (connection.getResponseCode() != 200) return null;
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            try (InputStream input = connection.getInputStream()) {
                byte[] part = new byte[16384]; int n;
                while ((n = input.read(part)) > 0) {
                    if (bytes.size() + n > 4000000) return null;
                    bytes.write(part, 0, n);
                }
            }
            return new JSONObject(bytes.toString("UTF-8"));
        } catch (Exception unreachable) { return null; }
        finally { if (connection != null) connection.disconnect(); }
    }
}
