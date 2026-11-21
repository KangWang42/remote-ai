using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using RemoteCli;

public static class LiveSessionsCheck {
    static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
    public static void Main(string[] args) {
        string folder = Path.Combine(args[0], "sessions");
        var parents = new Dictionary<int, int>();
        Require(ClaudeSessions.LiveSessions(parents, folder).Count == 0, "missing registry");
        Directory.CreateDirectory(folder);
        string session = Guid.NewGuid().ToString(), host = "local_" + Guid.NewGuid().ToString();
        using (var process = Process.GetCurrentProcess()) {
            long start = process.StartTime.ToUniversalTime().ToFileTimeUtc();
            var record = new Dictionary<string, object> {
                { "pid", process.Id }, { "procStart", start.ToString() }, { "sessionId", session },
                { "entrypoint", "claude-desktop" }, { "hostSessionId", host }, { "name", "新会话" }
            };
            var json = new JavaScriptSerializer();
            string file = Path.Combine(folder, process.Id + ".json");
            File.WriteAllText(file, json.Serialize(record));
            record["procStart"] = (start - 1).ToString();
            File.WriteAllText(Path.Combine(folder, "reused-pid.json"), json.Serialize(record));
            File.WriteAllText(Path.Combine(folder, "incomplete.json"), "{");
            File.WriteAllText(Path.Combine(folder, "empty.json"), "null");
            var owner = ClaudeSessions.LiveSessions(parents, folder).Single();
            Require(owner.Live && owner.Pid == process.Id && owner.Session == session && owner.HostSessionId == host
                && owner.Name == "新会话" && owner.Origin == "Claude 桌面版", "desktop discovery before transcript");
            record["procStart"] = start.ToString();
            record.Remove("name"); record.Remove("hostSessionId");
            File.WriteAllText(file, json.Serialize(record));
            owner = ClaudeSessions.LiveSessions(parents, folder).Single();
            Require(owner.Name == "" && owner.HostSessionId == "", "older session record");
        }
        Require(SessionOrigin.HostOf("Antigravity") == "Antigravity 桌面版" && SessionOrigin.HostOf("agy") == "", "desktop vs CLI origin");
        Console.WriteLine("Live sessions: desktop metadata without a transcript, stale PID, old records and Antigravity origin passed");
    }
}
