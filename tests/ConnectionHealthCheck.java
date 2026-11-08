package io.github.kangwang42.remotecli;

/** Regression cases for dead tunnels, transient failures and unrelated WebView resources. */
public final class ConnectionHealthCheck {
    private static void expect(boolean value, String name) {
        if (!value) throw new AssertionError(name);
    }
    public static void main(String[] args) {
        expect(ConnectionHealth.httpError(true, false, 530), "1033 tunnel page returns to pairing");
        expect(ConnectionHealth.httpError(false, true, 530), "API tunnel error returns to pairing");
        expect(!ConnectionHealth.httpError(false, true, 502) && ConnectionHealth.httpError(true, false, 502), "a restarting computer answers 502 to its pages' requests: waited out");
        expect(!ConnectionHealth.httpError(false, false, 530), "unrelated resource must not disconnect");
        expect(!ConnectionHealth.httpError(false, true, 401), "expired login remains on login page");
        expect(!ConnectionHealth.httpError(true, false, 404), "missing page is not a tunnel failure");
        ConnectionHealth health = new ConnectionHealth();
        expect(!health.sample(false, 0), "one timeout is retried");
        expect(!health.sample(true, 200), "healthy response resets failures");
        expect(!health.sample(false, 0), "timeout after recovery is retried");
        for (int n = 2; n < ConnectionHealth.PATIENCE; n++) expect(!health.sample(false, 0), "a restart of the computer's program is waited out");
        expect(health.sample(false, 0), "a computer that stays away returns to pairing");
        expect(new ConnectionHealth().sample(false, 530), "dead tunnel returns without waiting again");
        ConnectionHealth fresh = new ConnectionHealth();
        expect(!fresh.sample(true, 200), "new scan starts healthy");
        for (int n = 1; n < ConnectionHealth.PATIENCE; n++) expect(!fresh.sample(false, 200), "invalid service response is retried");
        expect(fresh.sample(false, 200), "repeated HTML response returns to pairing");
        ConnectionHealth oldPage = new ConnectionHealth();
        expect(oldPage.beginCheck(), "first check starts");
        expect(!oldPage.beginCheck(), "overlapping check is suppressed");
        oldPage.retire();
        ConnectionHealth nextPage = new ConnectionHealth();
        expect(nextPage.beginCheck(), "new page does not wait for old request");
        expect(!oldPage.isActive(), "late WebView error cannot affect new page");
        expect(!oldPage.sample(false, 530), "late failed check cannot disconnect new page");
        expect(!oldPage.beginCheck(), "retired page cannot restart polling");
        expect(!nextPage.beginCheck(), "old response does not release new page's pending check");
        expect(!nextPage.sample(true, 200), "new connection completes normally");
        expect(nextPage.beginCheck(), "monitoring continues after a successful check");
        nextPage.retire();
        ConnectionHealth retry = new ConnectionHealth();
        expect(retry.beginCheck(), "retry starts a fresh monitor even at the same address");
        expect(!nextPage.sample(false, 530), "old attempt cannot interrupt retry");
        expect(!retry.sample(false, 0), "retry gets a fresh failure allowance");
        System.out.println("Connection health: tunnel HTTP errors, failure recovery, login and resource isolation passed");
    }
}
