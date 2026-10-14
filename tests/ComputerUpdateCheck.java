package io.github.kangwang42.remotecli;

public final class ComputerUpdateCheck {
    private static void require(boolean condition, String why) {
        if (!condition) throw new AssertionError(why);
    }
    public static void main(String[] args) {
        ComputerUpdate a = new ComputerUpdate(), b = new ComputerUpdate();
        require(!a.canUpdate(), "Unknown devices cannot update");
        a.device("1.6.3", "1.6.4", false, true, true);
        require(!a.canUpdate(), "Offline devices cannot update");
        a.device("1.6.3", "", true, false, true);
        require(!a.canUpdate(), "Disabled access cannot update");
        a.device("1.6.3", "", true, true, false);
        require(!a.canUpdate(), "An old agent without update capability cannot update");
        a.device("", "", true, true, true);
        require(!a.canUpdate(), "A missing version cannot confirm an installation");
        a.device("1.6.3", "", true, true, true);
        require(a.canUpdate(), "No cached newer version still permits checking and installing");
        a.begin("first", 100);
        a.begin("duplicate", 200);
        require(a.request.equals("first") && a.started == 100, "Repeated taps must preserve the operation id");
        require(!b.busy(), "Requests must belong to only one computer");
        a.receipt("queued", "");
        require(a.busy() && !a.accepted, "Queued is not an acknowledgement");
        a.receipt("done", "");
        require(a.busy() && a.accepted, "Acknowledged is not installed");
        a.unavailable("offline");
        require(a.busy() && a.accepted && a.version.equals("1.6.3"), "Restart must retain the last version and receipt");
        a.device("1.6.3", "", true, true, true);
        require(a.busy(), "Reconnect at the old version is not success");
        a.device("1.6.4", "", false, true, true);
        require(a.busy(), "Offline cached metadata cannot confirm completion");
        a.device("1.6.4", "", true, true, true);
        require(!a.busy() && a.note.contains("已更新至 v1.6.4"), "Live newer version confirms completion");
        a.begin("second", 300);
        a.device("1.6.4", "", true, true, true);
        a.expire(180299);
        require(a.busy(), "Do not expire before the deadline");
        a.expire(180300);
        require(!a.busy() && a.note.contains("尚未确认"), "Timeout must not claim success or an up-to-date installation");
        a.begin("third", 200000);
        a.receipt("error", "远控已关闭");
        require(!a.busy() && a.note.equals("远控已关闭"), "Remote rejection must be visible");
        a.begin("fourth", 200000);
        a.device("1.6.2", "", true, true, true);
        require(a.busy(), "Rollback is not a successful upgrade");
        a.device("invalid", "", true, true, true);
        require(a.busy(), "Invalid metadata is not a successful upgrade");
        a.device("1.6.10", "", true, true, true);
        require(!a.busy(), "Version segments compare numerically");
        System.out.println("computer update state checks passed");
    }
}
