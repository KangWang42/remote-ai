package io.github.kangwang42.remotecli;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Which tasks a person should be told about while the app is not on the screen: one that has begun to wait on a
 * question, and one that has finished what it was doing. Nothing here touches Android, so it can be checked on its own.
 */
final class Watch {
    /** A terminal that runs on a computer, as its overview lists it. */
    static final class Task {
        final String id, title, dir, phase, event;
        final boolean done;
        Task(String id, String title, String dir, String phase, boolean done) {
            this(id, title, dir, phase, done, "");
        }
        Task(String id, String title, String dir, String phase, boolean done, String event) {
            this.id = id; this.title = title; this.dir = dir; this.phase = phase == null || phase.isEmpty() ? "idle" : phase; this.done = done; this.event = event;
        }
    }
    /** Something to tell: kind is "confirm" or "done". */
    static final class Alert {
        final String id, kind, title, dir;
        Alert(String id, String kind, String title, String dir) { this.id = id; this.kind = kind; this.title = title; this.dir = dir; }
    }

    /**
     * What has changed since the last look at one computer. phases holds each task's phase from that look and is
     * brought up to date. New pending questions are reported even on the first look. Historical idle tasks are
     * silent. Request identities distinguish consecutive questions between two polls; completion identities let
     * a short intervening turn be reported even when both polls saw an idle task.
     */
    static List<Alert> news(List<Task> tasks, Map<String, String> phases) {
        List<Alert> alerts = new ArrayList<>();
        Set<String> present = new HashSet<>();
        for (Task task : tasks) {
            present.add(task.id);
            String token = task.phase + (task.event.isEmpty() ? "" : "|" + task.event);
            String before = phases.put(task.id, token);
            if (token.equals(before)) continue;
            if ("confirm".equals(task.phase)) alerts.add(new Alert(task.id, "confirm", task.title, task.dir));
            else if ("idle".equals(task.phase) && task.done && before != null && (before.startsWith("busy") || before.startsWith("confirm") || before.startsWith("starting") || (!task.event.isEmpty() && before.startsWith("idle|"))))
                alerts.add(new Alert(task.id, "done", task.title, task.dir));
        }
        phases.keySet().retainAll(present);
        return alerts;
    }
}
