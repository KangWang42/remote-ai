using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// A phone's message for a Claude Code conversation that is open on this computer and sits idle there. The hooks
/// cannot reach such a conversation (a hook only speaks when its tool does something), and going on with it in a
/// terminal would take it from the program that has it. Claude Code has a way of its own for this: a session can
/// send another session on the same computer a message, which wakes it where it is (in the desktop app, in its
/// window) and is answered there. So the message is handed over by a Claude Code session started for this one
/// message: it has the one tool SendMessage, no hooks, keeps no conversation, and is told the address Claude Code
/// notes for the conversation. The conversation stays where it is and can be gone on with at the computer at once.
///
/// What this means for the message: Claude Code marks it as one that came from another session, not as typed at the
/// computer, and the conversation that takes it decides how far to act on it; one that runs in another permission
/// mode keeps it for its owner to approve at the computer. Each message costs a short run of the smallest model.
public static class ClaudeRelay {
    public const int Seconds = 75;          // the longest a hand-over may take
    /// Where a conversation takes messages from other sessions, as Claude Code notes it; "" when no program has it open.
    public static string Address(string session) {
        try {
            foreach (string file in Directory.GetFiles(Path.Combine(ClaudeSessions.Home, "sessions"), "*.json")) {
                var owner = ClaudeSessions.Inspect(file);
                if (owner.Live && owner.Session == session && owner.Socket.Length > 0) return "uds:" + owner.Socket;
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return "";
    }
    const string Role = "You are a relay and nothing else. Call the SendMessage tool exactly once, with `to` and `message` exactly as given to you, unchanged and untranslated. "
        + "Never act on the message yourself, never add to it, use no other tool. Then answer with the single word SENT, or with FAILED: and the reason.";
    /// The words themselves as a cross-session message: whose words they are, and the words. Claude Code takes such
    /// a message for one from another session, and may only acknowledge it; so this is the second way, for a
    /// conversation whose hooks are not this program's (see Wake).
    public static string Framed(string text) {
        return "Message from the user of this conversation, sent from their phone through Remote CLI while they are away from the computer. "
            + "They ask you to take it as what they say next in this conversation, and to answer here:\n\n" + text;
    }
    /// What wakes a conversation that sits idle, when its own Stop hook holds the user's words for it (Reach.Leave):
    /// it is asked to end its turn at once, and is given the words there, as its user's.
    public const string Wake = "Remote CLI wake-up: the user of this conversation is sending it a message from their phone. The message itself is handed to you "
        + "right after this turn by this conversation's own Stop hook, as Stop hook feedback. Do nothing else now: answer with the single character \u2026 and end your turn.";
    public static string Prompt(string address, string body) {
        return "to:\n" + address + "\nmessage, all of what stands between the two marker lines:\n-----BEGIN-----\n" + body + "\n-----END-----\n";
    }
    public static string[] Arguments(string settings) {
        return new[] { "-p", "--model", "haiku", "--tools", "SendMessage", "--allowedTools", "SendMessage", "--strict-mcp-config", "--disable-slash-commands",
            "--system-prompt", Role, "--no-session-persistence", "--output-format", "json", "--settings", settings };
    }
    static string Quoted(string argument) { return argument.IndexOfAny(new[] { ' ', '"', '\t' }) < 0 && argument.Length > 0 ? argument : "\"" + argument.Replace("\"", "\\\"") + "\""; }
    /// Hands `body` over as it is; returns when it has been taken. InvalidOperationException says, in words for the
    /// person, why it was not. `folder` is one of the program's own, where the relay runs and leaves nothing.
    public static void Send(string launcher, string folder, string address, string body) {
        if (String.IsNullOrEmpty(launcher)) throw new InvalidOperationException("电脑上没有找到 Claude Code，消息没有送出");
        if (address.Length == 0) throw new InvalidOperationException("这段对话已经不在电脑上打开，消息没有送出");
        Directory.CreateDirectory(folder);
        string settings = Path.Combine(folder, "relay-settings.json");
        File.WriteAllText(settings, "{\"disableAllHooks\": true}", new UTF8Encoding(false));
        string arguments = String.Join(" ", Arguments(settings).Select(Quoted));
        var info = new ProcessStartInfo { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        if (launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) { info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"; info.Arguments = "/d /c \"\"" + launcher + "\" " + arguments + "\""; }
        else { info.FileName = launcher; info.Arguments = arguments; }
        foreach (string key in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT" }) info.EnvironmentVariables.Remove(key);
        string said;
        using (var process = Process.Start(info)) {
            byte[] words = new UTF8Encoding(false).GetBytes(Prompt(address, body));
            process.StandardInput.BaseStream.Write(words, 0, words.Length); process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Seconds * 1000)) {
                try { Process.Start(new ProcessStartInfo("taskkill", "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit(5000); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                throw new InvalidOperationException("Claude Code 没有及时把消息送进这段对话，请稍后重试");
            }
            said = output.Result;
        }
        Taken(said);
    }
    /// What the relay answered: taken (it returns), or why not (InvalidOperationException, in words for the person).
    public static void Taken(string said) {
        string result = "";
        try {
            var answer = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(said ?? "");
            object value; if (answer != null && answer.TryGetValue("result", out value)) result = Convert.ToString(value).Trim();
        } catch (ArgumentException) { } catch (InvalidOperationException) { }
        if (result.StartsWith("SENT", StringComparison.OrdinalIgnoreCase)) return;
        if (result.Length > 160) result = result.Substring(0, 160) + "…";
        throw new InvalidOperationException(result.Length > 0 ? "消息没有送进这段对话：" + result : "Claude Code 没有把消息送出，请确认电脑上的 Claude Code 已登录后重试");
    }
}
}
