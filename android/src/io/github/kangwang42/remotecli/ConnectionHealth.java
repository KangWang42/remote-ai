package io.github.kangwang42.remotecli;

/**
 * A dead Cloudflare tunnel goes straight to pairing. Anything else is waited out for about half a minute: the
 * program on the computer restarts when it updates itself, and its pages carry on by themselves afterwards.
 */
final class ConnectionHealth {
    static final int PATIENCE = 10;      // checks three seconds apart, each waiting up to three more
    static final long SILENT_LOCAL_MS = 500L;
    static final long SILENT_REMOTE_MS = 2500L;
    private int failures;
    private boolean active = true, checking;
    private long lastFrameMs;
    boolean isActive() { return active; }
    void retire() { active = false; }
    void noteFrame(long nowMs) { lastFrameMs = nowMs; }
    boolean beginCheck() {
        if (!active || checking) return false;
        checking = true;
        return true;
    }
    boolean sample(boolean reachable, int status) {
        checking = false;
        if (!active) return false;
        if (reachable) { failures = 0; return false; }
        return status == 530 || ++failures >= PATIENCE;
    }
    /** Detects half-open WebSockets after mobile suspension when HTTP answers healthy but socket stays silent. */
    boolean isSilentSocket(boolean directRoute, long nowMs, boolean httpHealthy) {
        if (!active || !httpHealthy || lastFrameMs <= 0L) return false;
        long threshold = directRoute ? SILENT_LOCAL_MS : SILENT_REMOTE_MS;
        return nowMs - lastFrameMs >= threshold;
    }
    static boolean httpError(boolean mainFrame, boolean relayRequest, int status) {
        return status == 530 ? mainFrame || relayRequest : status >= 500 && mainFrame;
    }
}
