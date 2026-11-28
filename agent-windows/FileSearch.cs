using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RemoteCli {
/// Finds a file of a project by the name an answer gives it (`file_find`): a conversation often names a file without
/// its folder ("Peers.cs", "chat/chat.js"), and the phone opens it from that name. The project's folder is walked
/// breadth first, nearest first, past the folders of tools and builds, within a bound of entries and time.
public static class FileSearch {
    public const int Most = 20;
    const int MostEntries = 200000;
    static readonly TimeSpan MostTime = TimeSpan.FromSeconds(3);
    static readonly HashSet<string> Skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "node_modules", "bin", "obj", "dist", "build", "target", "__pycache__", "venv" };

    /// The project's paths ('/'-separated) that end with `given`, nearest first: at most `Most`, and `more` where
    /// there were others or the walk stopped at its bound.
    public static Dictionary<string, object> Find(string root, string given) {
        string wanted = (given ?? "").Trim().Replace('\\', '/').Trim('/');
        if (wanted.Length == 0 || wanted.Length > 300 || wanted.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
            throw new ArgumentException("文件名无效");
        root = Path.GetFullPath(root).TrimEnd('\\');
        if (!Directory.Exists(root)) throw new ArgumentException("文件夹不存在");
        string tail = "/" + wanted;
        // a name that says a hidden folder ('.github/x.yml') is looked for there too
        bool hiddenWanted = wanted.Split('/').Any(part => part.StartsWith(".", StringComparison.Ordinal));
        var found = new List<string>();
        bool more = false;
        int seen = 0;
        var clock = Stopwatch.StartNew();
        var queue = new Queue<string>();
        queue.Enqueue("");
        while (queue.Count > 0) {
            string relative = queue.Dequeue();
            string folder = relative.Length == 0 ? root : root + "\\" + relative.Replace('/', '\\');
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(folder).EnumerateFileSystemInfos().ToArray(); }
            catch (UnauthorizedAccessException) { continue; } catch (IOException) { continue; }
            foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)) {
                if (++seen > MostEntries || clock.Elapsed > MostTime) return Answer(found, true);
                string path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                bool inner = (entry.Attributes & FileAttributes.Directory) != 0;
                if (inner) {
                    // links are not followed: they may lead out of the project or round in a circle
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (Skipped.Contains(entry.Name) || (entry.Name.StartsWith(".", StringComparison.Ordinal) && !hiddenWanted)) continue;
                    queue.Enqueue(path);
                } else if (String.Equals(path, wanted, StringComparison.OrdinalIgnoreCase) || ("/" + path).EndsWith(tail, StringComparison.OrdinalIgnoreCase)) {
                    if (found.Count == Most) { more = true; return Answer(found, more); }
                    found.Add(path);
                }
            }
        }
        return Answer(found, more);
    }
    static Dictionary<string, object> Answer(List<string> found, bool more) {
        return new Dictionary<string, object> { { "found", found.ToArray() }, { "more", more } };
    }
}
}
