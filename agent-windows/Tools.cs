using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RemoteCli {
/// What differs between the programs a terminal of the phone's can run: how each is started, where it keeps a
/// conversation, how it is told whether another program has that conversation open, and how that program is ended.
/// One object per tool; the agent asks it and does not compare tool names. A tool that is added is one more class
/// here and one entry in `All` (and its lines in `Ways.Of`, its reading of the lists, its chat driver).
public abstract class Tool {
    public static readonly Tool Claude = new ClaudeTool(), Codex = new CodexTool(), Agy = new AgyTool(),
        Gemini = new GeminiTool(), OpenCode = new OpenCodeTool(), Cursor = new CursorTool(),
        Qoder = new QoderTool(), Cline = new ClineTool(), Pi = new PiTool(),
        Kiro = new KiroTool(), Copilot = new CopilotTool(), Shell = new ShellTool();
    public static readonly Tool[] All = { Claude, Codex, Agy, Gemini, OpenCode, Cursor, Qoder, Cline, Pi, Kiro, Copilot, Shell };

    /// The tool by its name, null for one that is not known.
    public static Tool Of(string name) { return All.FirstOrDefault(t => t.Name == name); }
    /// The tool by its name, refusing one that is not known.
    public static Tool Named(string name) {
        Tool tool = Of(name);
        if (tool == null) throw new ArgumentException("工具无效");
        return tool;
    }
    /// The name a person knows it by; the name as it is for one that is not known.
    public static string LabelOf(string name) { Tool tool = Of(name); return tool != null ? tool.Label : name; }

    public abstract string Name { get; }
    public abstract string Label { get; }
    /// Executable base names to probe on PATH (without .exe/.cmd).
    public virtual string[] Binaries { get { return new[] { Name }; } }
    /// Keeps conversations under an id that can be gone on with (a shell keeps none).
    public virtual bool Converses { get { return true; } }
    /// Runs as a conversation through its structured mode too (Chats.cs).
    public virtual bool Chats { get { return false; } }
    /// Supports isolating its CLI from a shared daemon (`--no-daemon`).
    public virtual bool SupportsStandalone { get { return false; } }
    /// Makes a copy of a conversation (a fork).
    public virtual bool Forks { get { return false; } }
    /// Is given the id of a new conversation when it starts, so its terminal knows it at once (Claude Code); the
    /// others name it themselves and their terminal learns it from the reading of the lists.
    public virtual bool NamedAtStart { get { return false; } }
    /// A terminal of it whose conversation has no id yet can be opened again after an update.
    public virtual bool ReturnsUnnamed { get { return true; } }
    /// Said where a terminal's conversation has no id yet and it is to be taken to the computer.
    public virtual string Unnamed { get { return "正在等待会话编号，请刷新活动后再接管"; } }

    /// Creates the structured chat driver for this tool (Chats.cs / AcpChat.cs).
    public virtual Chat CreateChat(string folder, Action<string> emit) { throw new ArgumentException("这个工具不支持对话"); }
    /// Delivers queued messages into a conversation that sits idle on this computer, waking it in place.
    public virtual void Wake(string launcher, string dataDir, string session, Reach reach, int wakeSeconds) {
        throw new InvalidOperationException(Label + " 不支持唤醒闲置对话");
    }

    /// What follows the program's path on its command line.
    public abstract string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "");
    /// The file it keeps a conversation in, as far as it can say without the reading of the lists; null where none.
    public virtual string Transcript(string session, string root) { return null; }
    /// Whether a program on this computer has the conversation open now.
    public virtual bool Open(string session) { return false; }
    /// Whether the program that has the conversation open runs under the process `root` (a terminal of the phone's).
    public virtual bool HeldUnder(string session, int root, Dictionary<int, int> parents) { return false; }
    /// Before a conversation is gone on with: no other program may have it open. With `takeover` the one that has it
    /// is ended first, where it is known for certain which one that is; `roots` are this program's own and are never
    /// ended. `held` is said where it is still open and the tool has no words of its own for that.
    public virtual void Exclusive(string session, bool takeover, int[] roots, Dictionary<int, int> parents, string held) { }
    /// Ends the program that has the conversation open; its record stays.
    public virtual void End(string session, int[] roots, Dictionary<int, int> parents) { Exclusive(session, true, roots, parents, null); }
    /// Puts the record of a conversation away.
    public virtual void Remove(string session, string root, string launcher) { throw new InvalidOperationException(Label + " 对话暂不支持从手机删除"); }
}

sealed class ClaudeTool : Tool {
    public override string Name { get { return "claude"; } }
    public override string Label { get { return "Claude Code"; } }
    public override bool Chats { get { return true; } }
    public override bool NamedAtStart { get { return true; } }
    public override Chat CreateChat(string folder, Action<string> emit) { return new ClaudeChat(folder, emit); }
    public override void Wake(string launcher, string dataDir, string session, Reach reach, int wakeSeconds) {
        var waiting = reach.Outbox.Of(Name, session).Where(item => item.State == Outbox.Queued).ToArray();
        if (waiting.Length == 0) return;
        string folder = Path.Combine(dataDir, "relay", "remote-cli-phone");       // Claude Code names the sender after this folder
        // (the hooks are there once Claude Code has told something through them, or stand in its settings:
        // a conversation opened before they did takes the words the other way, after the wait)
        bool hooks = reach.Heard(Name) || ReachHooks.Installed(Name);
        if (hooks) ClaudeRelay.Send(launcher, folder, ClaudeRelay.Address(session), ClaudeRelay.Wake);
        int patience = wakeSeconds;
        foreach (var item in waiting) {
            if (hooks && reach.Taken(item.Id, patience)) continue;
            patience = 0;       // its hooks did not come: the rest is not waited for either
            reach.Outbox.Deliver(item.Id, text => ClaudeRelay.Send(launcher, folder, ClaudeRelay.Address(session), ClaudeRelay.Framed(text)));
        }
    }

    public override string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "") {
        return (session.Length > 0 ? (fresh ? " --session-id " : " --resume ") + session : history ? " --resume" : "")
            + (mode == "read" ? " --permission-mode plan" : mode == "edit" ? " --permission-mode acceptEdits" : "")
            + (!String.IsNullOrEmpty(model) ? " --model " + model : "")
            + (!String.IsNullOrEmpty(effort) ? " --effort " + effort : "");
    }
    public override string Transcript(string session, string root) {
        string file = Path.Combine(ClaudeSessions.Home, "projects", TerminalAgent.ProjectFolder(root), session + ".jsonl");
        return File.Exists(file) ? file : null;
    }
    static IEnumerable<KeyValuePair<string, ClaudeSessions.Owner>> Owners(string session) {
        string folder = Path.Combine(ClaudeSessions.Home, "sessions");
        if (!Directory.Exists(folder)) return new KeyValuePair<string, ClaudeSessions.Owner>[0];
        return Directory.GetFiles(folder, "*.json").Select(file => new KeyValuePair<string, ClaudeSessions.Owner>(file, ClaudeSessions.Inspect(file)))
            .Where(item => item.Value.Live && item.Value.Session == session);
    }
    public override bool Open(string session) { return Owners(session).Any(); }
    public override bool HeldUnder(string session, int root, Dictionary<int, int> parents) {
        var owner = Owners(session).Select(item => item.Value).FirstOrDefault();
        return owner != null && SessionOrigin.Descends(owner.Pid, new[] { root }, parents);
    }
    // Only the verified writer of the conversation is ended, and only where it is the one program that has it.
    public override void Exclusive(string session, bool takeover, int[] roots, Dictionary<int, int> parents, string held) {
        var owners = Owners(session).ToArray();
        if (owners.Length > 1) throw new InvalidOperationException(ClaudeSessions.MultipleReason);
        foreach (var item in owners) {
            string file = item.Key; var owner = item.Value;
            if (!takeover || !owner.CanTakeover || SessionOrigin.Descends(owner.Pid, roots, parents)) throw new InvalidOperationException(owner.Reason);
            using (var process = Process.GetProcessById(owner.Pid)) {
                var current = ClaudeSessions.Inspect(file);
                if (!current.CanTakeover || current.Session != session || current.Pid != owner.Pid || current.Started != owner.Started || process.StartTime.ToUniversalTime().ToFileTimeUtc() != owner.Started) throw new InvalidOperationException("Claude Code 会话归属已变化，请刷新后重试");
                process.Kill();
                if (!process.WaitForExit(5000)) throw new InvalidOperationException("原 Claude Code 会话尚未结束，已取消接管");
            }
        }
    }
    public override void Remove(string session, string root, string launcher) {
        Removal.Claude(Path.Combine(ClaudeSessions.Home, "projects", TerminalAgent.ProjectFolder(root), session + ".jsonl"));
    }
}

sealed class CodexTool : Tool {
    public override string Name { get { return "codex"; } }
    public override string Label { get { return "Codex"; } }
    public override bool Chats { get { return true; } }
    public override bool SupportsStandalone { get { return true; } }
    public override bool Forks { get { return true; } }
    public override Chat CreateChat(string folder, Action<string> emit) { return new CodexChat(folder, emit); }
    public override void Wake(string launcher, string dataDir, string session, Reach reach, int wakeSeconds) {
        var waiting = reach.Outbox.Of(Name, session).Where(item => item.State == Outbox.Queued).ToArray();
        foreach (var item in waiting) reach.Outbox.Deliver(item.Id, text => CodexQueue.Send(launcher, session, text));
    }

    public override string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "") {
        return (session.Length > 0 ? (fork ? " fork " : " resume ") + session : history ? " resume --include-non-interactive" : "")
            + (noDaemon ? " --no-daemon" : "") + (mode == "read" ? " -s read-only" : mode == "edit" ? " -s workspace-write" : "")
            + (!String.IsNullOrEmpty(model) ? " -m " + model : "")
            + (!String.IsNullOrEmpty(effort) ? " -c model_reasoning_effort=" + effort : "");
    }
    public override string Transcript(string session, string root) {
        string folder = Path.Combine(CodexSessions.Home, "sessions");
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "rollout-*" + session + ".jsonl", SearchOption.AllDirectories).FirstOrDefault() : null;
    }
    public override bool Open(string session) { return CodexSessions.Inspect(session).Live; }
    public override bool HeldUnder(string session, int root, Dictionary<int, int> parents) {
        var owner = CodexSessions.Inspect(session, parents);
        return owner.Live && owner.Pid > 0 && SessionOrigin.Descends(owner.Pid, new[] { root }, parents);
    }
    public override void Exclusive(string session, bool takeover, int[] roots, Dictionary<int, int> parents, string held) {
        if (takeover) CodexSessions.Takeover(session, roots, parents);
        var owner = CodexSessions.Inspect(session, parents);
        if (!owner.Known || owner.Live) throw new InvalidOperationException(held ?? "原 Codex 对话仍被占用，请先在原应用结束并关闭它，再接管同一段历史；不会自动新建副本");
    }
    public override void End(string session, int[] roots, Dictionary<int, int> parents) { CodexSessions.Takeover(session, roots, parents); }
    public override void Remove(string session, string root, string launcher) { Removal.Codex(launcher, session); }
}

sealed class AgyTool : Tool {
    public override string Name { get { return "agy"; } }
    public override string Label { get { return "Antigravity CLI"; } }
    public override bool Chats { get { return true; } }     // by its ACP server (AcpChat.cs)
    public override bool ReturnsUnnamed { get { return false; } }
    public override string Unnamed { get { return "请先在 Antigravity 终端内开始对话，或通过“在电脑上打开”直接使用"; } }
    public override Chat CreateChat(string folder, Action<string> emit) { return new AgyChat(folder, emit); }

    // Its own choice of mode stays: Remote AI's read/edit modes are not given to it.
    public override string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "") {
        if (history || fork || fresh) throw new ArgumentException("请在 Antigravity CLI 内选择历史对话");
        return (session.Length > 0 ? " --conversation " + session : "")
            + (!String.IsNullOrEmpty(model) ? " --model " + model : "")
            + (!String.IsNullOrEmpty(effort) ? " --effort " + effort : "");
    }
    public override string Transcript(string session, string root) { return Antigravity.TranscriptPath(session); }
    public override bool Open(string session) { return AgySessions.Inspect(session).Live; }
    public override bool HeldUnder(string session, int root, Dictionary<int, int> parents) {
        var owner = AgySessions.Inspect(session, parents);
        return owner.Live && owner.Pid > 0 && SessionOrigin.Descends(owner.Pid, new[] { root }, parents);
    }
    public override void Exclusive(string session, bool takeover, int[] roots, Dictionary<int, int> parents, string held) {
        if (takeover) AgySessions.Takeover(session, roots, parents);
        var owner = AgySessions.Inspect(session, parents);
        if (!owner.Known || owner.Live) throw new InvalidOperationException(held ?? "原 Antigravity CLI 对话仍被占用，请选择接管原对话，或在原终端结束后重试");
    }
    public override void End(string session, int[] roots, Dictionary<int, int> parents) { AgySessions.Takeover(session, roots, parents); }
}

sealed class GeminiTool : Tool {
    public override string Name { get { return "gemini"; } }
    public override string Label { get { return "Gemini CLI"; } }
    public override bool Converses { get { return false; } }
    public override string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "") {
        return (history ? " --resume" : "")
            + (mode == "read" ? " --approval-mode plan" : mode == "edit" ? " --approval-mode auto_edit" : "")
            + (!String.IsNullOrEmpty(model) ? " -m " + model : "");
    }
}

sealed class OpenCodeTool : Tool {
    public override string Name { get { return "opencode"; } }
    public override string Label { get { return "OpenCode"; } }
    public override bool Converses { get { return false; } }
    public override string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "") {
        return (!String.IsNullOrEmpty(model) ? " -m " + model : "");
    }
}

sealed class CursorTool : Tool {
    public override string Name { get { return "cursor"; } }
    public override string Label { get { return "Cursor Agent"; } }
    public override string[] Binaries { get { return new[] { "cursor-agent", "agent" }; } }
    public override bool Converses { get { return false; } }
    public override string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "") {
        return (!String.IsNullOrEmpty(model) ? " --model " + model : "");
    }
}

sealed class QoderTool : Tool {
    public override string Name { get { return "qoder"; } }
    public override string Label { get { return "Qoder"; } }
    public override string[] Binaries { get { return new[] { "qodercli", "qoder" }; } }
    public override bool Converses { get { return false; } }
    public override string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "") {
        return (mode == "read" ? " --permission-mode plan" : mode == "edit" ? " --permission-mode accept_edits" : "");
    }
}

sealed class ClineTool : Tool {
    public override string Name { get { return "cline"; } }
    public override string Label { get { return "Cline"; } }
    public override bool Converses { get { return false; } }
    public override string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "") {
        return mode == "read" ? " --plan" : "";
    }
}

sealed class PiTool : Tool {
    public override string Name { get { return "pi"; } }
    public override string Label { get { return "Pi"; } }
    public override bool Converses { get { return false; } }
    public override string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "") { return ""; }
}

sealed class KiroTool : Tool {
    public override string Name { get { return "kiro"; } }
    public override string Label { get { return "Kiro CLI"; } }
    public override string[] Binaries { get { return new[] { "kiro-cli", "kiro" }; } }
    public override bool Converses { get { return false; } }
    public override string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "") { return ""; }
}

sealed class CopilotTool : Tool {
    public override string Name { get { return "copilot"; } }
    public override string Label { get { return "Copilot CLI"; } }
    public override bool Converses { get { return false; } }
    public override string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "") { return ""; }
}

sealed class ShellTool : Tool {
    public override string Name { get { return "shell"; } }
    public override string Label { get { return "PowerShell"; } }
    public override bool Converses { get { return false; } }
    public override string Arguments(string session, bool history, string mode, bool fork, bool noDaemon, bool fresh, string model = "", string effort = "") { return " -NoLogo -NoExit"; }
}
}
