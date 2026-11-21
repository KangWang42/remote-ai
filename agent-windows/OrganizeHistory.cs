using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace RemoteCli {

/// The saved conversations the organizer can read, and what of one is sent to a model.
internal static class OrganizeHistory {
    /// What is sent of a conversation: its first request and its last few messages, within a fixed size.
    internal const int FirstChars = 1500, RecentMessages = 6, MessageChars = 1200, MostChars = 6000;
    /// A conversation with less said than this in all is not worth a request of its own.
    internal const int ShortChars = 80;
    /// Conversations a team's members and lead are given begin with this (Teams.cs).
    internal const string TeamMark = "【协作任务";

    internal sealed class Reading {
        public string Context = "", Hash = "";
        public bool Short, Team;
    }

    /// Conversations of the projects and of the visible folders outside them, written to since `since` (0: all).
    /// `own` is the organizer's working folder: what a tool keeps of its requests there is never listed.
    internal static Organizer.Source[] List(IDictionary<string, string> projects, IEnumerable<TerminalAgent.SessionInfo> visible, long since, string own = "") {
        var found = new List<Organizer.Source>();
        var outside = visible.Where(s => s.Dir.Length == 0 && s.Folder.Length > 0).ToArray();
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects) folders[PathKey(project.Value)] = project.Key;
        foreach (var s in outside) {
            if (!folders.ContainsKey(PathKey(s.Folder))) folders.Add(PathKey(s.Folder), "");
        }
        if (!String.IsNullOrEmpty(own)) folders.Remove(PathKey(own));
        var named = CodexNames();
        string cp = Path.Combine(ClaudeSessions.Home, "projects");
        foreach (var folder in folders) {
            foreach (var file in Files(Path.Combine(cp, TerminalAgent.ProjectFolder(folder.Key)), "*.jsonl", SearchOption.TopDirectoryOnly)) {
                string id = Path.GetFileNameWithoutExtension(file.Name);
                if (!SessionLocks.IsUuid(id) || !Eligible(file, since) || (folder.Value.Length == 0 && !Visible(outside, "claude", id, folder.Key))) continue;
                Add(found, file, "claude", id, folder.Value, folder.Key, "");
            }
        }
        foreach (var file in Files(Path.Combine(CodexSessions.Home, "sessions"), "rollout-*.jsonl", SearchOption.AllDirectories)) {
            string name = Path.GetFileNameWithoutExtension(file.Name);
            string id = name.Length > 36 ? name.Substring(name.Length - 36) : "";
            if (!SessionLocks.IsUuid(id) || !Eligible(file, since)) continue;
            try {
                string first = Transcripts.ReadPart(file.FullName, false, 160 * 1024).Split('\n')[0];
                if (Transcripts.JsonText(first, @"""parent_thread_id"":", false).Length > 0) continue;
                string folder = PathKey(Transcripts.JsonText(first, @"""cwd"":", false)), dir, title;
                if (!folders.TryGetValue(folder, out dir) || (dir.Length == 0 && !Visible(outside, "codex", id, folder))) continue;
                named.TryGetValue(id, out title);
                Add(found, file, "codex", id, dir, folder, title ?? "");
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (var item in Antigravity.Summaries(null, visible.Where(s => s.Tool == "agy" && s.Live).Select(s => s.Id), all: true)) {
            string folder = PathKey(item.Folder), dir;
            if (!folders.TryGetValue(folder, out dir) || (dir.Length == 0 && !Visible(outside, "agy", item.Id, folder))) continue;
            string path = Antigravity.TranscriptPath(item.Id);
            if (path != null && Eligible(new FileInfo(path), since)) Add(found, new FileInfo(path), "agy", item.Id, dir, folder, item.Title, item.Named);
        }
        return found.GroupBy(s => s.Key).Select(g => g.OrderByDescending(s => s.Updated).First())
            .OrderByDescending(s => s.Updated).ThenBy(s => s.Key, StringComparer.Ordinal).ToArray();
    }

    internal static string PathKey(string path) {
        return (path ?? "").TrimEnd('\\', '/').Replace('/', '\\');
    }

    static bool Visible(TerminalAgent.SessionInfo[] outside, string tool, string id, string folder) {
        return outside.Any(s => s.Tool == tool && String.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)
            && String.Equals(PathKey(s.Folder), folder, StringComparison.OrdinalIgnoreCase));
    }

    static FileInfo[] Files(string path, string pattern, SearchOption scope) {
        try { var dir = new DirectoryInfo(path); return dir.Exists ? dir.GetFiles(pattern, scope) : new FileInfo[0]; }
        catch (IOException) { return new FileInfo[0]; }
        catch (UnauthorizedAccessException) { return new FileInfo[0]; }
    }

    static bool Eligible(FileInfo file, long since) {
        try { return file.Exists && file.Length > 0 && (since == 0 || Organizer.Now(file.LastWriteTimeUtc) >= since); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static Dictionary<string, string> CodexNames() {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try {
            var index = Path.Combine(CodexSessions.Home, "session_index.jsonl");
            if (!File.Exists(index)) return names;
            var reader = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            using (var stream = File.Open(index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var text = new StreamReader(stream, Encoding.UTF8)) {
                string line;
                while ((line = text.ReadLine()) != null) {
                    try {
                        var row = reader.Deserialize<Dictionary<string, object>>(line);
                        string id = Organizer.Text(row, "id");
                        if (SessionLocks.IsUuid(id)) names[id] = Transcripts.Tidy(Organizer.Text(row, "thread_name"));
                    } catch (ArgumentException) { } catch (InvalidOperationException) { }
                }
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return names;
    }

    static void Add(List<Organizer.Source> found, FileInfo file, string tool, string id, string dir, string folder, string title, bool named = false) {
        try {
            if (tool == "codex") named = title.Length > 0;
            if (tool == "claude") title = Transcripts.ClaudeTitleOf(file.FullName, out named);
            if (title.Length == 0) title = Tool.Named(tool).Label + " 对话 " + file.CreationTime.ToString("MM-dd HH:mm");
            found.Add(new Organizer.Source { Tool = tool, Id = id, Dir = dir, Folder = folder, File = file.FullName,
                Title = title, Named = named, Updated = Organizer.Now(file.LastWriteTimeUtc) });
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// What is sent of a conversation, and what decides whether it is sent at all. InvalidOperationException where
    /// it has nothing its user said.
    internal static Reading Read(Organizer.Source source) {
        var messages = Messages(source);
        if (!messages.Any(m => Organizer.Text(m, "role") == "user")) throw new InvalidOperationException("对话还没有可整理的用户消息");
        var first = messages.First(m => Organizer.Text(m, "role") == "user");
        int start = Math.Max(Array.IndexOf(messages, first) + 1, messages.Length - RecentMessages);
        var kept = new List<object> { Part(first, FirstChars) };
        if (start > Array.IndexOf(messages, first) + 1)
            kept.Add(new { role = "note", text = "中间省略 " + (start - Array.IndexOf(messages, first) - 1) + " 条消息" });
        int remaining = MostChars - FirstChars;
        var recent = new List<object>();
        for (int i = messages.Length - 1; i >= start && remaining > 0; i--) {
            string text = Organizer.Text(messages[i], "text");
            int count = Math.Min(Math.Min(text.Length, MessageChars), remaining);
            if (count == 0) continue;
            recent.Add(Part(messages[i], count));
            remaining -= count;
        }
        recent.Reverse();
        kept.AddRange(recent);
        string context = new JavaScriptSerializer().Serialize(kept);
        return new Reading {
            Context = context, Hash = Hash(context),
            Short = !messages.Any(m => Organizer.Text(m, "role") == "assistant") || messages.Sum(m => Organizer.Text(m, "text").Length) < ShortChars,
            Team = Organizer.Text(first, "text").TrimStart().StartsWith(TeamMark, StringComparison.Ordinal)
        };
    }

    static object Part(Dictionary<string, object> message, int most) {
        string text = Organizer.Text(message, "text");
        return new { role = Organizer.Text(message, "role"), text = OrganizeReply.Limit(text, Math.Min(text.Length, most)) };
    }

    static string Hash(string text) {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 16).Replace("-", "").ToLowerInvariant();
    }

    static Dictionary<string, object>[] Messages(Organizer.Source source) {
        return ((List<Dictionary<string, object>>)Transcripts.Said(source.File, source.Tool)["messages"])
            .Where(m => {
                string role = Organizer.Text(m, "role");
                return (role == "user" || role == "assistant") && !String.IsNullOrWhiteSpace(Organizer.Text(m, "text"));
            }).ToArray();
    }
}
}
