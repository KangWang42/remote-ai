package io.github.kangwang42.remotecli;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
public final class WatchCheck {
    static void require(boolean condition, String message) { if (!condition) throw new AssertionError(message); }
    static Watch.Task task(String phase, String event) { return new Watch.Task("a", "标题", "demo", phase, !event.isEmpty(), event); }
    static List<Watch.Alert> look(Map<String, String> state, long now, String phase, String event) { return Watch.news(Arrays.asList(task(phase, event)), state, now); }
    public static void main(String[] args) {
        Map<String, String> state = new HashMap<>();
        require(look(state, 0, "confirm", "ask1").get(0).kind.equals("confirm"), "first pending request is immediate");
        require(look(state, 1000, "confirm", "ask1").isEmpty(), "same request is quiet");
        require(look(state, 2000, "confirm", "ask2").size() == 1, "next request has its own identity");
        require(look(state, 3000, "busy", "").isEmpty(), "answering permission is not completion");
        require(look(state, 4000, "idle", "").isEmpty(), "output silence has no completion evidence");
        require(look(state, 40000, "idle", "").isEmpty(), "long silence remains quiet");
        require(look(state, 41000, "idle", "turn1").isEmpty(), "explicit end waits for another observation");
        require(look(state, 45000, "busy", "").isEmpty(), "resumed work cancels pending end");
        require(look(state, 55000, "idle", "turn2").isEmpty(), "new end starts its own confirmation period");
        require(look(state, 64999, "idle", "turn2").isEmpty(), "no early notification");
        require(look(state, 65000, "idle", "turn2").get(0).kind.equals("done"), "stable explicit completion is reported once");
        require(look(state, 80000, "idle", "turn2").isEmpty(), "same completion is not repeated");
        state.clear();
        require(look(state, 110000, "idle", "historical").isEmpty(), "initial historical completion is baseline only");
        require(look(state, 130000, "idle", "historical").isEmpty(), "baseline remains quiet");
        System.out.println("Watch checks passed: explicit stable completion and no false completion.");
    }
}
