using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RemoteCli {
/// By what the folders with conversations are used on this computer: a desktop app, an editor, a terminal; and with
/// which tool. One look at the conversations fills it (Begin, a Note for every conversation found, End); the
/// projects are told with it (Of), and so are the folders a project can still be made of (Offered).
/// The words for the kinds go to the phone as they stand (docs/PROTOCOL.md, `kinds`).
public sealed class Folders {
    public const string Terminal = "终端", Editor = "编辑器", CodexApp = "Codex 桌面版", ClaudeApp = "Claude 桌面版";
    public sealed class Use {
        public string Path = ""; public long Updated; public bool Live;
        public readonly List<string> Tools = new List<string>(), Kinds = new List<string>();
    }
    static readonly Use Unused = new Use();
    Dictionary<string, Use> known = new Dictionary<string, Use>(StringComparer.OrdinalIgnoreCase), noting;

    /// By what a Codex conversation was started, as its file says: `source` is "cli", "exec" or "vscode" (the
    /// desktop app says "vscode" too), `originator` names the program.
    public static string OfCodex(string source, string originator) {
        bool editor = (originator ?? "").IndexOf("vscode", StringComparison.OrdinalIgnoreCase) >= 0;
        bool app = source == "vscode" || (originator ?? "").IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0;
        return editor ? Editor : app ? CodexApp : Terminal;
    }
    /// By what a Claude Code conversation was started, as its file says (`entrypoint`: "cli", "claude-desktop…", "claude-vscode").
    public static string OfClaude(string entrypoint) {
        entrypoint = entrypoint ?? "";
        return entrypoint.StartsWith("claude-desktop", StringComparison.OrdinalIgnoreCase) ? ClaudeApp
            : entrypoint.IndexOf("vscode", StringComparison.OrdinalIgnoreCase) >= 0 || entrypoint.IndexOf("jetbrains", StringComparison.OrdinalIgnoreCase) >= 0 ? Editor : Terminal;
    }
    /// By what a conversation is used that a program has open, from where that program runs; `saved`: what the
    /// conversation's own file says, for a holder that tells nothing (the service the Codex programs share).
    public static string Held(string origin, string saved) {
        origin = origin ?? "";
        return origin == CodexApp || origin == ClaudeApp ? origin : origin == SessionOrigin.CommandLine ? Terminal : origin.Length == 0 || origin.EndsWith("后台服务") ? saved : Editor;
    }
    public static bool App(string kind) { return kind == CodexApp || kind == ClaudeApp; }
    /// The kinds as they are told: each once, the terminal last; a desktop app that does not run is left out where
    /// `codexApp`/`claudeApp` say so (a folder only such an app uses is then one of no kind).
    public static string[] Told(IEnumerable<string> kinds, bool codexApp = true, bool claudeApp = true) {
        return kinds.Distinct().Where(kind => kind == CodexApp ? codexApp : kind == ClaudeApp ? claudeApp : true).OrderBy(kind => kind == Terminal ? 1 : 0).ToArray();
    }
    /// Whether the desktop app of a tool runs now. (Claude's app is Claude.exe, its command line claude.exe; Codex's
    /// app is ChatGPT.exe, or Codex.exe.)
    public static bool AppRuns(string kind) {
        return kind == CodexApp ? Runs("ChatGPT", false) || Runs("Codex", true) : kind == ClaudeApp && Runs("Claude", true);
    }
    static bool Runs(string name, bool exact) {
        try {
            var found = Process.GetProcessesByName(name);
            try { return found.Any(p => !exact || p.ProcessName == name); } finally { foreach (var p in found) p.Dispose(); }
        } catch (InvalidOperationException) { return false; }
    }

    public void Begin() { noting = new Dictionary<string, Use>(StringComparer.OrdinalIgnoreCase); }
    /// A conversation was found: in that folder, of that tool, used then, held by a program or not, of that kind.
    public void Note(string folder, string tool, DateTime at, bool live, string kind) {
        folder = (folder ?? "").TrimEnd('\\', '/');
        if (folder.Length == 0 || noting == null) return;
        Use use;
        if (!noting.TryGetValue(folder, out use)) noting[folder] = use = new Use { Path = folder };
        long when = (long)(at - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        if (when > use.Updated) use.Updated = when;
        if (live) use.Live = true;
        if (!use.Tools.Contains(tool)) use.Tools.Add(tool);
        if (!String.IsNullOrEmpty(kind) && !use.Kinds.Contains(kind)) use.Kinds.Add(kind);
    }
    public void End() { if (noting != null) { known = noting; noting = null; } }
    /// What is known of a folder's use; of one without conversations, nothing.
    public Use Of(string folder) {
        Use use; var all = known;
        return all.TryGetValue((folder ?? "").TrimEnd('\\', '/'), out use) ? use : Unused;
    }
    /// The folders with conversations that pass `offer` (no project yet, and no folder of the system's), the ones in
    /// use first, then by when they were last used. `closedApps`: also the ones only a desktop app uses that does
    /// not run now; without it there is nothing of such a folder to work beside from the phone.
    public List<Dictionary<string, object>> Offered(Func<string, bool> offer, bool closedApps, int most) {
        bool codex = closedApps || AppRuns(CodexApp), claude = closedApps || AppRuns(ClaudeApp);
        var offered = new List<Dictionary<string, object>>();
        foreach (Use use in known.Values.Where(use => offer(use.Path)).OrderByDescending(use => use.Live).ThenByDescending(use => use.Updated)) {
            if (offered.Count >= most) break;
            string[] kinds = Told(use.Kinds, codex, claude);
            if (kinds.Length == 0) continue;
            try { if (!Directory.Exists(use.Path)) continue; } catch (ArgumentException) { continue; }
            offered.Add(new Dictionary<string, object> { { "path", use.Path }, { "name", System.IO.Path.GetFileName(use.Path) }, { "updated", use.Updated }, { "live", use.Live }, { "tools", String.Join(" ", use.Tools) }, { "kinds", kinds } });
        }
        return offered;
    }
}
}
