package io.github.kangwang42.remotecli;

/**
 * A dead Cloudflare tunnel goes straight to pairing. Anything else is waited out for about half a minute: the
 * program on the computer restarts when it updates itself, and its pages carry on by themselves afterwards.
 */
final class ConnectionHealth {
    static final int PATIENCE = 10;      // checks three seconds apart, each waiting up to three more
    private int failures;
    private boolean active = true, checking;
    boolean isActive() { return active; }
    void retire() { active = false; }
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
    static boolean httpError(boolean mainFrame, boolean relayRequest, int status) {
        return status == 530 ? mainFrame || relayRequest : status >= 500 && mainFrame;
    }
}
