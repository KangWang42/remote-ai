using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace RemoteCli {

internal static class OrganizeHistory {
    internal static Organizer.Source[] List(IDictionary<string, string> projects, IEnumerable<TerminalAgent.SessionInfo> visible, long since) {
        var found = new List<Organizer.Source>();
        var outside = visible.Where(s => s.Dir.Length == 0 && s.Folder.Length > 0).ToArray();
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects) folders[PathKey(project.Value)] = project.Key;
        foreach (var s in outside) {
            if (!folders.ContainsKey(PathKey(s.Folder))) folders.Add(PathKey(s.Folder), "");
        }
        var named = CodexNames();
        string cp = Path.Combine(ClaudeSessions.Home, "projects");
        foreach (var folder in folders) {
            foreach (var file in Files(Path.Combine(cp, TerminalAgent.ProjectFolder(folder.Key)), "*.jsonl", SearchOption.TopDirectoryOnly)) {
                string id = Path.GetFileNameWithoutExtension(file.Name);
                if (!SessionLocks.IsUuid(id) || !Eligible(file, since) || (folder.Value.Length == 0 && !Visible(outside, "claude", id, folder.Key))) continue;
                Add(found, file, "claude", id, folder.Value, folder.Key, "", since);
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
                Add(found, file, "codex", id, dir, folder, title ?? "", since, first);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (var item in Antigravity.Summaries(null, visible.Where(s => s.Tool == "agy" && s.Live).Select(s => s.Id), all: true)) {
            string folder = PathKey(item.Folder), dir;
            if (!folders.TryGetValue(folder, out dir) || (dir.Length == 0 && !Visible(outside, "agy", item.Id, folder))) continue;
            string path = Antigravity.TranscriptPath(item.Id);
            if (path != null) Add(found, new FileInfo(path), "agy", item.Id, dir, folder, item.Title, since, named: item.Named);
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

    static void Add(List<Organizer.Source> found, FileInfo file, string tool, string id, string dir, string folder, string title,
            long since, string first = null, bool named = false) {
        try {
            if (!Eligible(file, since)) return;
            long created = Organizer.Now(file.CreationTimeUtc);
            if (since > 0) {
                if (created < since) return;
                if (first == null) first = Transcripts.ReadPart(file.FullName, false, 64 * 1024).Split('\n')[0];
                DateTime parsed;
                if (DateTime.TryParse(Transcripts.JsonText(first, @"""timestamp"":", false), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
                    created = Math.Min(created, Organizer.Now(parsed));
                if (created < since) return;
            }
            if (tool == "codex") named = title.Length > 0;
            if (tool == "claude") title = Transcripts.ClaudeTitleOf(file.FullName, out named);
            if (title.Length == 0) title = Tool.Named(tool).Label + " 对话 " + file.CreationTime.ToString("MM-dd HH:mm");
            found.Add(new Organizer.Source { Tool = tool, Id = id, Dir = dir, Folder = folder, File = file.FullName,
                Title = title, Named = named, Updated = Organizer.Now(file.LastWriteTimeUtc), Created = created });
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static bool HasExchange(Organizer.Source source) {
        try {
            var messages = Messages(source);
            return messages.Any(m => Organizer.Text(m, "role") == "user") && messages.Any(m => Organizer.Text(m, "role") == "assistant");
        } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
    }

    internal static string Context(Organizer.Source source) {
        var messages = Messages(source);
        if (!messages.Any(m => Organizer.Text(m, "role") == "user")) throw new InvalidOperationException("对话还没有可整理的用户消息");
        int remaining = 18000;
        var kept = new List<object>();
        for (int i = messages.Length - 1; i >= 0 && remaining > 0; i--) {
            string text = Organizer.Text(messages[i], "text");
            int count = Math.Min(Math.Min(text.Length, 4000), remaining);
            if (count > 0 && Char.IsHighSurrogate(text[count - 1])) count--;
            if (count == 0) continue;
            kept.Add(new { role = Organizer.Text(messages[i], "role"), text = text.Substring(0, count) });
            remaining -= count;
        }
        kept.Reverse();
        return new JavaScriptSerializer().Serialize(kept);
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
