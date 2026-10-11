using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RemoteCli {
/// A phone's message for a Codex conversation that is open on this computer and sits idle there. Codex has a command
/// of its own for this, `codex queue --thread <id> --message <text>`: the message is queued for the conversation, and
/// the Codex that has it open takes it as what its user says next and answers where it is. The conversation is not
/// taken from its program, and can be gone on with at the computer at once.
public static class CodexQueue {
    public const int Seconds = 30;
    static readonly Dictionary<string, string> programs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, bool> able = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    /// The program itself. A launcher that is a script (npm's codex.cmd) would be given the message through the
    /// command interpreter, which reads signs in it as its own: the program it starts is found beside it instead.
    public static string Program(string launcher) {
        if (String.IsNullOrEmpty(launcher)) return null;
        if (launcher.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return launcher;
        lock (programs) {
            string found;
            if (programs.TryGetValue(launcher, out found)) return found;
            try {
                string inside = Path.Combine(Path.GetDirectoryName(launcher), "node_modules", "@openai", "codex");
                found = Directory.Exists(inside) ? Directory.GetFiles(inside, "codex.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
            } catch (IOException) { found = null; } catch (UnauthorizedAccessException) { found = null; }
            programs[launcher] = found;
            return found;
        }
    }
    /// Whether the Codex on this computer has the command (it came with later versions). Asked once of each program.
    public static bool Supported(string launcher) {
        string program = Program(launcher);
        if (program == null) return false;
        lock (able) {
            bool known;
            if (able.TryGetValue(program, out known)) return known;
            try {
                string said; int code = Run(program, "queue --help", 8, out said);
                known = code == 0 && said.IndexOf("--thread", StringComparison.Ordinal) >= 0;
            } catch (System.ComponentModel.Win32Exception) { known = false; } catch (InvalidOperationException) { known = false; }
            able[program] = known;
            return known;
        }
    }
    /// One argument, as a Windows program reads its command line: quotes and the backslashes in front of them kept.
    public static string Quoted(string argument) {
        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in argument) {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { quoted.Append('\\', 2 * slashes + 1); quoted.Append('"'); slashes = 0; continue; }
            quoted.Append('\\', slashes); slashes = 0; quoted.Append(c);
        }
        quoted.Append('\\', 2 * slashes);
        return quoted.Append('"').ToString();
    }
    static int Run(string program, string arguments, int seconds, out string said) {
        var info = new ProcessStartInfo(program, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using (var process = Process.Start(info)) {
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(seconds * 1000)) { try { process.Kill(); } catch (InvalidOperationException) { } said = ""; return -1; }
            said = output.Result + errors.Result;
            return process.ExitCode;
        }
    }
    /// Queues the message; returns when Codex has taken it into its queue. InvalidOperationException says, in words
    /// for the person, why it was not.
    public static void Send(string launcher, string session, string text) {
        string program = Program(launcher);
        if (program == null) throw new InvalidOperationException("电脑上没有找到 Codex，消息没有送出");
        string said; int code;
        try { code = Run(program, "queue --thread " + Quoted(session) + " --message " + Quoted(text), Seconds, out said); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("消息没有送出：电脑上启动不了 Codex"); }
        if (code == 0 && said.IndexOf("Queued", StringComparison.OrdinalIgnoreCase) >= 0) return;
        said = said.Trim();
        if (said.Length > 160) said = said.Substring(said.Length - 160);
        throw new InvalidOperationException(code == -1 ? "Codex 没有及时接下这条消息，请稍后重试" : "消息没有送进这段对话" + (said.Length > 0 ? "：" + said : ""));
    }
}
}
