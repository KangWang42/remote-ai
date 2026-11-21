using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
public static class Antigravity {
    static readonly Regex Uuid = new Regex(@"\A[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\z");
    static readonly Regex TitleLine = new Regex(@"^\s*title\s*:\s*""((?:\\.|[^""\\])*)""", RegexOptions.Multiline);
    static readonly Regex UserRequest = new Regex(@"<USER_REQUEST>\s*([\s\S]*?)\s*</USER_REQUEST>", RegexOptions.Singleline);

    public sealed class Summary {
        public string Id = "", Title = "", Folder = "", Status = "";
        public bool Named;
        public long Updated;
        public DateTime Created = DateTime.UtcNow;
    }

    public static string Home {
        get {
            string named = Environment.GetEnvironmentVariable("AGY_HOME");
            return String.IsNullOrWhiteSpace(named)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli")
                : named;
        }
    }

    public static string TranscriptPath(string id) {
        if (!Uuid.IsMatch(id ?? "")) return null;
        string path = Path.Combine(Home, "brain", id, ".system_generated", "logs", "transcript.jsonl");
        return File.Exists(path) ? path : null;
    }

    /// Parses a JSON array of workspace file URIs (e.g. ["file:///E:/dir"] or ["file:///e%3A/dir"]) into the first local folder path.
    public static string WorkspacePath(string urisJson) {
        if (String.IsNullOrWhiteSpace(urisJson)) return "";
        try {
            var list = new JavaScriptSerializer().Deserialize<object[]>(urisJson);
            if (list == null) return "";
            foreach (object item in list) {
                string raw = Convert.ToString(item ?? "").Trim();
                if (!raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) continue;
                string decoded = Uri.UnescapeDataString(raw);
                string path;
                if (decoded.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) {
                    path = decoded.Substring(8);
                    // Handle drive letter paths like "E:/foo" or "/E:/foo"
                    if (path.Length >= 3 && path[0] == '/' && path[2] == ':') path = path.Substring(1);
                    path = path.Replace('/', '\\');
                } else {
                    // UNC path: file://server/share/...
                    path = @"\\" + decoded.Substring(7).Replace('/', '\\');
                }
                path = path.TrimEnd('\\');
                if (path.Length == 2 && path[1] == ':') path += @"\";
                if (path.Length >= 3 && Path.IsPathRooted(path)) return path;
            }
        } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (UriFormatException) { }
        return "";
    }

    public static string MapStatus(string status, bool notFullyIdle) {
        status = status ?? "";
        if (status.IndexOf("WAITING", StringComparison.OrdinalIgnoreCase) >= 0) return "confirm";
        if (status.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0 || notFullyIdle) return "busy";
        return "idle";
    }

    static string UnescapePbtxt(string raw) {
        if (String.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++) {
            char c = raw[i];
            if (c != '\\' || i + 1 >= raw.Length) { sb.Append(c); continue; }
            char next = raw[++i];
            if (next == 'n') sb.Append(' ');
            else if (next == 'r') { }
            else if (next == 't') sb.Append(' ');
            else if (next == '"' || next == '\\') sb.Append(next);
            else if (next >= '0' && next <= '3' && i + 2 < raw.Length && raw[i + 1] >= '0' && raw[i + 1] <= '7' && raw[i + 2] >= '0' && raw[i + 2] <= '7') {
                // Protobuf text format escapes non-ASCII UTF-8 bytes as octal \344\270\255.
                var bytes = new List<byte>();
                i--;
                while (i + 3 < raw.Length && raw[i] == '\\' && raw[i + 1] >= '0' && raw[i + 1] <= '3' && raw[i + 2] >= '0' && raw[i + 2] <= '7' && raw[i + 3] >= '0' && raw[i + 3] <= '7') {
                    bytes.Add((byte)(((raw[i + 1] - '0') << 6) | ((raw[i + 2] - '0') << 3) | (raw[i + 3] - '0')));
                    i += 4;
                }
                i--;
                sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            } else sb.Append(next);
        }
        return sb.ToString();
    }

    public static string AnnotationTitle(string home, string id) {
        try {
            string path = Path.Combine(home, "annotations", id + ".pbtxt");
            if (!File.Exists(path)) return "";
            string text = File.ReadAllText(path, Encoding.UTF8);
            var m = TitleLine.Match(text);
            return m.Success ? CleanTitle(UnescapePbtxt(m.Groups[1].Value)) : "";
        } catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; }
    }

    public static string ExtractUserRequest(string content) {
        if (String.IsNullOrWhiteSpace(content)) return "";
        var m = UserRequest.Match(content);
        string text = m.Success ? m.Groups[1].Value : content;
        return text.Trim();
    }

    internal static string CleanTitle(string text) {
        if (String.IsNullOrWhiteSpace(text)) return "";
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 72 ? text.Substring(0, 71) + "…" : text;
    }

    static string TranscriptTitle(string home, string id) {
        try {
            string path = Path.Combine(home, "brain", id, ".system_generated", "logs", "transcript.jsonl");
            if (!File.Exists(path)) return "";
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                var js = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                for (int n = 0; n < 8; n++) {
                    string line = reader.ReadLine();
                    if (line == null) break;
                    if (line.IndexOf("\"USER_INPUT\"", StringComparison.Ordinal) < 0) continue;
                    var row = js.Deserialize<Dictionary<string, object>>(line);
                    object content;
                    if (row != null && row.TryGetValue("content", out content)) {
                        string req = CleanTitle(ExtractUserRequest(Convert.ToString(content ?? "")));
                        if (req.Length > 0) return req;
                    }
                }
            }
        } catch { }
        return "";
    }

    static Dictionary<string, string> HistoryWorkspaces(string home) {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try {
            string path = Path.Combine(home, "history.jsonl");
            if (!File.Exists(path)) return map;
            var js = new JavaScriptSerializer();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                if (stream.Length > 2 * 1024 * 1024) stream.Seek(-2 * 1024 * 1024, SeekOrigin.End);
                using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                    string line;
                    while ((line = reader.ReadLine()) != null) {
                        if (line.IndexOf("\"conversationId\"", StringComparison.Ordinal) < 0) continue;
                        try {
                            var row = js.Deserialize<Dictionary<string, object>>(line);
                            object cid, ws;
                            if (row != null && row.TryGetValue("conversationId", out cid) && row.TryGetValue("workspace", out ws)) {
                                string id = Convert.ToString(cid ?? ""), folder = Convert.ToString(ws ?? "").Trim();
                                if (Uuid.IsMatch(id) && folder.Length >= 3) map[id] = folder;
                            }
                        } catch { }
                    }
                }
            }
        } catch { }
        return map;
    }

    internal static long Milliseconds(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

    internal static DateTime ParseTimestamp(string raw, DateTime fallback) {
        DateTime dt;
        if (!String.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt)) return dt;
        return fallback;
    }

    // Minimal P/Invoke to Windows' built-in winsqlite3.dll (x64) for read-only access to conversation_summaries.db.
    const int SQLITE_OPEN_READONLY = 0x00000001, SQLITE_OPEN_NOMUTEX = 0x00008000, SQLITE_ROW = 100;
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int numBytes, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_column_int(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_finalize(IntPtr stmt);

    static byte[] Utf8Z(string text) {
        byte[] raw = Encoding.UTF8.GetBytes(text);
        byte[] terminated = new byte[raw.Length + 1];
        Buffer.BlockCopy(raw, 0, terminated, 0, raw.Length);
        return terminated;
    }

    static string ColumnText(IntPtr stmt, int col) {
        IntPtr ptr = sqlite3_column_text(stmt, col);
        if (ptr == IntPtr.Zero) return "";
        int len = sqlite3_column_bytes(stmt, col);
        if (len <= 0) return "";
        byte[] buf = new byte[len];
        Marshal.Copy(ptr, buf, 0, len);
        return Encoding.UTF8.GetString(buf);
    }

    static bool TableHasColumn(IntPtr db, string column) {
        IntPtr stmt = IntPtr.Zero;
        try {
            if (sqlite3_prepare_v2(db, Utf8Z("PRAGMA table_info(conversation_summaries)"), -1, out stmt, IntPtr.Zero) == 0 && stmt != IntPtr.Zero) {
                while (sqlite3_step(stmt) == SQLITE_ROW) {
                    if (String.Equals(ColumnText(stmt, 1), column, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        } finally {
            if (stmt != IntPtr.Zero) try { sqlite3_finalize(stmt); } catch { }
        }
        return false;
    }

    /// Reads conversation summaries from conversation_summaries.db with optional CLI/desktop source filtering.
    internal static Dictionary<string, Summary> ReadSummariesDb(string home, bool excludeDesktop, bool excludeCli, bool all) {
        var map = new Dictionary<string, Summary>(StringComparer.OrdinalIgnoreCase);
        string dbPath = Path.Combine(home, "conversation_summaries.db");
        if (!File.Exists(dbPath)) return map;
        IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
        try {
            if (sqlite3_open_v2(Utf8Z(dbPath), out db, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX, IntPtr.Zero) == 0 && db != IntPtr.Zero) {
                sqlite3_busy_timeout(db, 400);
                var where = new List<string> { "parent_conversation_id = ''", "killed = 0" };
                if (excludeDesktop && TableHasColumn(db, "app_data_dir")) where.Add("app_data_dir != 'antigravity'");
                if (excludeCli) {
                    if (TableHasColumn(db, "source")) where.Add("source != 'CORTEX_TRAJECTORY_SOURCE_CLI'");
                    if (TableHasColumn(db, "app_data_dir")) where.Add("app_data_dir != 'antigravity-cli'");
                }
                string sql = "SELECT conversation_id, title, preview, last_modified_time, workspace_uris, status, not_fully_idle "
                    + "FROM conversation_summaries WHERE " + String.Join(" AND ", where.ToArray())
                    + " ORDER BY last_modified_time DESC" + (all ? "" : " LIMIT 200");
                if (sqlite3_prepare_v2(db, Utf8Z(sql), -1, out stmt, IntPtr.Zero) == 0 && stmt != IntPtr.Zero) {
                    while (sqlite3_step(stmt) == SQLITE_ROW) {
                        string id = ColumnText(stmt, 0).Trim();
                        if (!Uuid.IsMatch(id)) continue;
                        string title = CleanTitle(ColumnText(stmt, 1));
                        string preview = CleanTitle(ColumnText(stmt, 2));
                        DateTime modified = ParseTimestamp(ColumnText(stmt, 3), DateTime.UtcNow);
                        string folder = WorkspacePath(ColumnText(stmt, 4));
                        string status = MapStatus(ColumnText(stmt, 5), sqlite3_column_int(stmt, 6) != 0);
                        if (title.Length == 0) title = AnnotationTitle(home, id);
                        bool named = title.Length > 0;
                        if (title.Length == 0) title = preview;
                        if (title.Length == 0) title = TranscriptTitle(home, id);
                        map[id] = new Summary {
                            Id = id,
                            Title = title,
                            Named = named,
                            Folder = folder,
                            Status = status,
                            Updated = Milliseconds(modified),
                            Created = modified
                        };
                    }
                }
            }
        } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { } catch (BadImageFormatException) { } catch (IOException) { }
        finally {
            if (stmt != IntPtr.Zero) try { sqlite3_finalize(stmt); } catch { }
            if (db != IntPtr.Zero) try { sqlite3_close_v2(db); } catch { }
        }
        return map;
    }

    /// Reads recent Antigravity CLI conversations from conversation_summaries.db and supplements with live lock files.
    public static List<Summary> Summaries(string homeOverride = null, IEnumerable<string> liveIds = null, bool all = false) {
        string home = String.IsNullOrWhiteSpace(homeOverride) ? Home : homeOverride;
        var map = ReadSummariesDb(home, true, false, all);
        // Supplement missing folders or brand-new live conversations that have not flushed to conversation_summaries.db yet.
        var history = (liveIds != null && liveIds.Any(id => !map.ContainsKey(id))) || map.Values.Any(s => s.Folder.Length == 0)
            ? HistoryWorkspaces(home) : null;
        if (liveIds != null) {
            foreach (string id in liveIds) {
                if (!Uuid.IsMatch(id ?? "") || map.ContainsKey(id)) continue;
                string folder = "";
                if (history != null) history.TryGetValue(id, out folder);
                string title = AnnotationTitle(home, id);
                bool named = title.Length > 0;
                if (title.Length == 0) title = TranscriptTitle(home, id);
                DateTime when = DateTime.UtcNow;
                string lockPath = Path.Combine(home, "presence", id + ".lock");
                try { if (File.Exists(lockPath)) when = File.GetLastWriteTimeUtc(lockPath); } catch (IOException) { }
                map[id] = new Summary {
                    Id = id,
                    Title = title.Length > 0 ? title : "Antigravity 对话 " + when.ToLocalTime().ToString("MM-dd HH:mm"),
                    Named = named,
                    Folder = folder ?? "",
                    Status = "idle",
                    Updated = Milliseconds(when),
                    Created = when
                };
            }
        }
        if (history != null) {
            foreach (var s in map.Values) {
                if (s.Folder.Length == 0) { string ws; if (history.TryGetValue(s.Id, out ws)) s.Folder = ws; }
            }
        }
        foreach (var s in map.Values) {
            if (s.Title.Length == 0) s.Title = "Antigravity 对话 " + s.Created.ToLocalTime().ToString("MM-dd HH:mm");
        }
        return map.Values.OrderByDescending(s => s.Updated).ToList();
    }

    // Independent consoles with no bound conversation yet. Report only the start time,
    // excluding Remote AI's own managed terminals and any agy process already represented as a live session.
    internal static long[] ExternalStarts(string executable, int[] ownedRoots, IEnumerable<int> knownPids = null) {
        var starts = new List<long>();
        if (String.IsNullOrEmpty(executable)) return starts.ToArray();
        var parents = SessionOrigin.Parents();
        var known = knownPids != null ? new HashSet<int>(knownPids) : null;
        using (var current = Process.GetCurrentProcess()) {
            foreach (var process in Process.GetProcessesByName("agy")) using (process) {
                try {
                    if (process.SessionId != current.SessionId || !String.Equals(executable, process.MainModule.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (known != null && known.Contains(process.Id)) continue;
                    if (ownedRoots.Contains(process.Id) || SessionOrigin.Descends(process.Id, ownedRoots, parents)) continue;
                    starts.Add(new DateTimeOffset(process.StartTime).ToUnixTimeMilliseconds());
                } catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { }
            }
        }
        return starts.OrderByDescending(time => time).Take(16).ToArray();
    }
}

/// Read-only ownership checks and verified takeover for Antigravity CLI conversations via presence/<id>.lock.
public sealed class AgySessions {
    public sealed class Owner : LockOwner { }

    readonly object gate = new object();
    Dictionary<string, Owner> cached;
    bool refreshing;
    DateTime next = DateTime.MinValue;

    public static string LockPath(string id, string homeOverride = null) {
        if (!SessionLocks.IsUuid(id)) throw new ArgumentException("会话编号无效");
        string home = String.IsNullOrWhiteSpace(homeOverride) ? Antigravity.Home : homeOverride;
        return Path.Combine(home, "presence", id + ".lock");
    }

    /// True if an agy process currently holds the exclusive lock on presence/<id>.lock.
    public static bool? Held(string path) { return SessionLocks.Held(path); }

    public static Owner Inspect(string id, Dictionary<int, int> parents = null, string homeOverride = null) {
        string path = LockPath(id, homeOverride);
        bool? held = SessionLocks.Held(path);
        if (held == false) return new Owner { Known = true, Host = "", Reason = "" };
        if (held == null) return new Owner { Reason = "无法核实 Antigravity CLI 锁文件，请在电脑结束会话后重试" };
        var result = new Owner { Known = true, Live = true, Reason = "无法确认电脑上 Antigravity CLI 进程的归属，请先在原终端结束后重试" };
        try {
            var holder = SessionLocks.SingleHolder(path);
            if (holder == null) return result;
            result.Pid = holder.Pid; result.Started = holder.Started;
            if (!String.Equals(holder.ProcessName, "agy", StringComparison.OrdinalIgnoreCase)) return result;
            var origin = SessionOrigin.Inspect(holder.Pid, parents);
            result.Origin = origin.Name;
            if (origin.Remote) {
                result.Host = "remote";
                result.Reason = "此对话在其它远程终端运行，请先在原远程终端结束后，再接管原对话";
                return result;
            }
            result.Host = "cli";
            result.CanTakeover = true;
            result.Reason = "接管会结束原终端中的这段 Antigravity CLI 对话，再在手机恢复同一段历史";
        } catch { /* Unknown ownership cannot authorize process termination. */ }
        return result;
    }

    static Dictionary<string, Owner> Snapshot(string homeOverride = null) {
        var fresh = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
        try {
            string home = String.IsNullOrWhiteSpace(homeOverride) ? Antigravity.Home : homeOverride;
            string folder = Path.Combine(home, "presence");
            if (!Directory.Exists(folder)) return fresh;
            var parents = SessionOrigin.Parents();
            foreach (string path in Directory.GetFiles(folder, "*.lock")) {
                string id = Path.GetFileNameWithoutExtension(path);
                if (SessionLocks.IsUuid(id)) fresh[id] = Inspect(id, parents, home);
            }
        } catch { }
        return fresh;
    }

    public void Refresh() {
        bool first;
        lock (gate) {
            first = cached == null;
            if (!first && (refreshing || DateTime.UtcNow < next)) return;
            refreshing = true; next = DateTime.UtcNow.AddSeconds(5);
        }
        if (first) {
            try {
                var fresh = Snapshot();
                lock (gate) cached = fresh;
            } finally { lock (gate) refreshing = false; }
            return;
        }
        Task.Run(delegate {
            try {
                var fresh = Snapshot();
                lock (gate) cached = fresh;
            } catch { /* Keep the last verified snapshot; actions always recheck. */ }
            finally { lock (gate) refreshing = false; }
        });
    }

    public Owner Get(string id) {
        lock (gate) {
            Owner owner;
            if (cached != null && cached.TryGetValue(id, out owner)) return owner;
            return new Owner { Known = !File.Exists(LockPath(id)), Reason = "正在确认电脑会话的归属，请稍后刷新" };
        }
    }

    public string[] LiveIds() {
        lock (gate) return cached == null ? new string[0] : cached.Where(kv => kv.Value.Live).Select(kv => kv.Key).ToArray();
    }

    public int[] LivePids() {
        lock (gate) return cached == null ? new int[0] : cached.Values.Where(v => v.Live && v.Pid > 0).Select(v => v.Pid).Distinct().ToArray();
    }

    public static void Takeover(string id, IEnumerable<int> protectedRoots, Dictionary<int, int> parents, string homeOverride = null) {
        SessionLocks.Takeover(() => Inspect(id, parents, homeOverride), LockPath(id, homeOverride), protectedRoots, parents,
            "无法安全结束这个电脑会话，请先在原终端结束后重试",
            "电脑会话没有结束，已取消接管",
            "Antigravity CLI 会话锁尚未释放，已取消接管，请稍后重试");
    }
}
}
