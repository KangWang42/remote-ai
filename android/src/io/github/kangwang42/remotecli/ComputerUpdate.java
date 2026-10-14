package io.github.kangwang42.remotecli;

/** One computer's update request. An acknowledgement is not an installation result. */
final class ComputerUpdate {
    String version = "", newer = "", connection = "正在读取电脑信息…", note = "";
    boolean online, enabled, supported, accepted;
    String request = "", before = "";
    long started;

    void device(String version, String newer, boolean online, boolean enabled, boolean supported) {
        this.version = version; this.newer = newer; this.online = online;
        this.enabled = enabled; this.supported = supported;
        connection = !online ? "电脑离线" : !enabled ? "电脑已暂停手机访问" : "电脑在线";
        if (busy() && online && newerThan(version, before)) {
            request = ""; note = "电脑已更新至 v" + version;
        }
    }
    void unavailable(String why) { online = false; connection = why; }
    boolean busy() { return !request.isEmpty(); }
    boolean canUpdate() { return online && enabled && supported && !version.isEmpty() && !busy(); }
    void begin(String id, long now) {
        if (!canUpdate()) return;
        before = version; request = id; started = now; accepted = false;
        note = "正在发送电脑更新请求…";
    }
    void receipt(String state, String error) {
        if (!busy()) return;
        if ("done".equals(state)) { accepted = true; note = "电脑已接收更新请求，正在等待版本变化。下载和重启期间可能暂时离线。"; }
        else if ("queued".equals(state)) note = "请求已排队，等待电脑接收…";
        else { request = ""; note = error.isEmpty() ? "电脑未接受更新请求，请刷新后重试" : error; }
    }
    void expire(long now) {
        if (!busy() || now - started < 180000) return;
        request = "";
        note = "尚未确认版本变化。若已是最新版，无需安装；若更新未完成，请查看电脑端的更新提示。可刷新版本后再试。";
    }
    private static boolean newerThan(String version, String before) {
        if (!version.matches("[0-9]+(\\.[0-9]+){1,3}") || !before.matches("[0-9]+(\\.[0-9]+){1,3}")) return false;
        String[] a = version.split("\\."), b = before.split("\\.");
        try {
            for (int i = 0; i < Math.max(a.length, b.length); i++) {
                long x = i < a.length ? Long.parseLong(a[i]) : 0, y = i < b.length ? Long.parseLong(b[i]) : 0;
                if (x != y) return x > y;
            }
        } catch (NumberFormatException ignored) { }
        return false;
    }
}
