package io.github.kangwang42.remotecli;

import android.app.Service;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.PowerManager;
import android.os.SystemClock;
import android.os.IBinder;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
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

/** Opt-in cross-device reminders. A visible foreground service retains a baseline while the app is open and
 * reports changes while it is away. Polls are serialized, retry after failures, and survive idle task lists. */
public final class Watcher extends Service {
    private static final int ONGOING = 14711;
    private static final String CHANNEL_TASKS = "tasks", STATE = "remote-cli-watch";
    private final ScheduledExecutorService worker = Executors.newSingleThreadScheduledExecutor();
    private volatile boolean active, visible;
    static void test(Context context) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel(CHANNEL_TASKS, "任务提醒", NotificationManager.IMPORTANCE_HIGH));
        manager.notify(14712, new Notification.Builder(context, CHANNEL_TASKS).setSmallIcon(R.drawable.ic_shell).setContentTitle("Remote AI 测试提醒")
            .setContentText("通知已送达。锁屏和后台效果还受系统电池策略影响。").setAutoCancel(true).setContentIntent(open(context)).build());
    }

    static void start(Context context) {
        start(context, false);
    }
    static void start(Context context, boolean visible) {
        try {
            if (!context.getSharedPreferences("remote-cli", MODE_PRIVATE).getBoolean("watch", false)) return;
            if (!context.getSystemService(NotificationManager.class).areNotificationsEnabled()) {
                context.getSharedPreferences(STATE, MODE_PRIVATE).edit().putString("error", "通知被系统关闭，请先允许通知").apply(); return;
            }
            context.startForegroundService(new Intent(context, Watcher.class).putExtra("visible", visible));
        } catch (Exception refused) {
            context.getSharedPreferences(STATE, MODE_PRIVATE).edit().putString("error", "后台监听未启动，请重新打开应用并检查提醒权限").apply();
        }
    }
    static void stop(Context context) {
        try {
            context.stopService(new Intent(context, Watcher.class));
        } catch (Exception ignored) { }
    }
    @Override public IBinder onBind(Intent intent) { return null; }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        visible = intent != null && intent.getBooleanExtra("visible", false);
        if (intent != null && "stop".equals(intent.getAction())) {
            getSharedPreferences("remote-cli", MODE_PRIVATE).edit().putBoolean("watch", false).apply(); stopSelf(); return START_NOT_STICKY;
        }
        if (!getSharedPreferences("remote-cli", MODE_PRIVATE).getBoolean("watch", false)) { stopSelf(); return START_NOT_STICKY; }
        NotificationManager manager = getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel(CHANNEL_TASKS, "任务提醒", NotificationManager.IMPORTANCE_HIGH));
        manager.createNotificationChannel(new NotificationChannel("watch", "后台连接", NotificationManager.IMPORTANCE_LOW));
        PendingIntent stop = PendingIntent.getService(this, 0, new Intent(this, Watcher.class).setAction("stop"), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        startForeground(ONGOING, new Notification.Builder(this, "watch").setSmallIcon(R.drawable.ic_shell).setContentTitle("Remote AI 正在接收任务提醒")
            .setContentText("等待确认或任务完成时通知 · 点此打开应用").setContentIntent(open(this)).setOngoing(true)
            .addAction(new Notification.Action.Builder(null, "关闭提醒", stop).build()).build());
        if (!active) {
            active = true;
            getSharedPreferences(STATE, MODE_PRIVATE).edit().remove("error").apply();
            worker.scheduleWithFixedDelay(() -> {
                PowerManager.WakeLock awake = getSystemService(PowerManager.class).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "remote-cli:watch");
                try { awake.acquire(20_000); if (active) look(this); }
                catch (Exception ignored) { /* The next scheduled look still runs after a failure. */ }
                finally { if (awake.isHeld()) awake.release(); }
            }, 0, 10, TimeUnit.SECONDS);
        }
        return START_STICKY;
    }
    @Override public void onDestroy() { active = false; worker.shutdownNow(); stopForeground(STOP_FOREGROUND_REMOVE); super.onDestroy(); }

    private static PendingIntent open(Context context) {
        Intent intent = new Intent(context, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        return PendingIntent.getActivity(context, 0, intent, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }

    private void look(Context context) throws Exception {
        SharedPreferences state = context.getSharedPreferences(STATE, Context.MODE_PRIVATE);
        JSONObject kept = new JSONObject(state.getString("phases-v2", "{}"));
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
        JSONObject now = new JSONObject();
        for (int i = 0; i < computers.length(); i++) {
            JSONObject computer = computers.optJSONObject(i), data = answers[i];
            if (computer == null) continue;
            String url = computer.optString("url");
            JSONObject device = data == null ? null : data.optJSONObject("device");
            JSONObject noted = kept.optJSONObject(url);
            if (device == null || !device.optBoolean("online") || !device.optBoolean("enabled")) {
                if (noted != null) now.put(url, noted);        // not reached this time: what was known stays
                continue;
            }
            List<Watch.Task> tasks = new ArrayList<>();
            JSONArray terminals = data.optJSONArray("terminals");
            for (int k = 0; terminals != null && k < terminals.length(); k++) {
                JSONObject t = terminals.optJSONObject(k);
                if (t == null || !("running".equals(t.optString("state")) || "starting".equals(t.optString("state")))) continue;
                tasks.add(new Watch.Task(t.optString("id"), t.optString("title"), t.optString("dir"),
                    "starting".equals(t.optString("state")) ? "starting" : t.optString("phase"), !t.optString("completion").isEmpty(),
                    "confirm".equals(t.optString("phase")) ? t.optString("phase_at") : t.optString("completion")));
            }
            // A conversation open on the computer, of which its tool tells, is watched like a terminal: one that asks,
            // and one that has stopped and waits for a message, are told.
            JSONArray sessions = data.optJSONArray("sessions");
            for (int k = 0; sessions != null && k < sessions.length(); k++) {
                JSONObject s = sessions.optJSONObject(k);
                if (s == null || !s.optBoolean("live") || !s.optString("terminal").isEmpty() || s.optJSONArray("reach") == null) continue;
                String status = s.optString("status");
                JSONObject ask = s.optJSONObject("ask");
                tasks.add(new Watch.Task(s.optString("id"), s.optString("title"), s.optString("dir"),
                    "confirm".equals(status) ? "confirm" : "busy".equals(status) ? "busy" : "idle", !s.optString("completion").isEmpty(),
                    ask == null ? s.optString("completion") : ask.optString("id")));
            }
            Map<String, String> before = new HashMap<>();
            if (noted != null) for (Iterator<String> keys = noted.keys(); keys.hasNext(); ) { String key = keys.next(); before.put(key, noted.optString(key)); }
            String name = computer.optString("name").isEmpty() ? MainActivity.host(url) : computer.optString("name");
            Map<String, String> old = new HashMap<>(before);
            List<Watch.Alert> alerts = Watch.news(tasks, before);
            if (!active) return;
            NotificationManager manager = context.getSystemService(NotificationManager.class);
            for (String id : old.keySet()) if ((old.get(id).startsWith("confirm") && !before.getOrDefault(id, "").startsWith("confirm"))
                || before.getOrDefault(id, "").startsWith("busy|") || !before.containsKey(id)) manager.cancel((url + id).hashCode());
            if (!visible) for (Watch.Alert alert : alerts) tell(context, url, name, alert);
            now.put(url, new JSONObject(before));
        }
        if (active) state.edit().putString("phases-v2", now.toString()).putLong("checked", System.currentTimeMillis()).apply();
    }

    private static void tell(Context context, String url, String computer, Watch.Alert alert) {
        boolean asks = "confirm".equals(alert.kind);
        String title = alert.title.isEmpty() ? alert.dir : alert.title;
        try {
            NotificationManager manager = context.getSystemService(NotificationManager.class);
            manager.createNotificationChannel(new NotificationChannel(CHANNEL_TASKS, "任务提醒", NotificationManager.IMPORTANCE_HIGH));
            Intent target = new Intent(context, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .setData(android.net.Uri.parse("remotecli://task/" + (url + alert.id).hashCode())).putExtra("computer", url).putExtra("task", alert.id).putExtra("project", alert.dir);
            PendingIntent tap = PendingIntent.getActivity(context, (url + alert.id).hashCode(), target, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
            Notification notification = new Notification.Builder(context, CHANNEL_TASKS).setSmallIcon(R.drawable.ic_shell)
                .setContentTitle((asks ? "等你确认 · " : "本轮回复结束 · ") + computer)
                .setContentText(title + (alert.dir.isEmpty() || alert.dir.equals(title) ? "" : " · " + alert.dir))
                .setAutoCancel(true).setContentIntent(tap).build();
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
