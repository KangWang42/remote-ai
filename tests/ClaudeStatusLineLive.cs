using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using RemoteCli;

/// Claude Code's status line on the real tool: an interactive Claude Code in a console of this program's, with this
/// program's status line given for that session only (`--settings`; the owner's settings, and the hooks in them, are
/// not loaded: `--setting-sources project`), told one short thing on its smallest model. What its status line was
/// told of the limits is kept where the computer reports from. Only an account with a subscription is told of them.
///     ClaudeStatusLineLive.exe <RemoteCliAgent.exe> <claude.exe> <empty folder> [a folder Claude Code trusts already]
public static class ClaudeStatusLineLive {
    static readonly StringBuilder screen = new StringBuilder();
    static string Shown() { lock (screen) return Regex.Replace(screen.ToString(), "\x1b\\[[0-9;?]*[A-Za-z]|\x1b\\][^\x07]*\x07", " "); }
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why + "\nscreen: " + Tail(Shown())); }
    static string Tail(string text) { text = Regex.Replace(text, "\\s+", " "); return text.Length > 1500 ? text.Substring(text.Length - 1500) : text; }
    static bool Until(Func<bool> test, int seconds) { var end = DateTime.UtcNow.AddSeconds(seconds); while (DateTime.UtcNow < end) { if (test()) return true; Thread.Sleep(250); } return test(); }

    public static int Main(string[] args) {
        string agent = args[0], claude = args[1], folder = args[2], data = Path.Combine(folder, "data"), at = args.Length > 3 ? args[3] : folder;
        Directory.CreateDirectory(data);
        string settings = Path.Combine(folder, "line.json");
        // (LINE_COMMAND names another status line, to see what Claude Code gives it)
        string line = Environment.GetEnvironmentVariable("LINE_COMMAND") ?? QuotaStatusLine.Command(agent, data, "");
        File.WriteAllText(settings, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "statusLine", new Dictionary<string, object> { { "type", "command" }, { "command", line } } } }), new UTF8Encoding(false));
        foreach (string key in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT", "CLAUDE_CODE_CHILD_SESSION" }) Environment.SetEnvironmentVariable(key, null);
        string command = "\"" + claude + "\" --model haiku --setting-sources project --settings \"" + settings + "\"";
        string kept = Path.Combine(data, "quotas", "claude.json");
        using (var console = new PseudoTerminal(command, at, 120, 40, text => { lock (screen) screen.Append(text); })) {
            Action<string> type = text => { Require(!console.Closed, "Claude Code is still open"); console.Write(text); };
            // a folder it has not seen asks to be trusted; then its box waits
            // (its first choice there is "No, exit": the second is taken)
            Until(() => console.Closed || Shown().Contains("trust this folder") || Shown().Contains("for shortcuts"), 30);
            if (Shown().Contains("trust this folder")) { type("\x1b[B"); Thread.Sleep(400); type("\r"); }
            Require(Until(() => console.Closed || Shown().Contains("for shortcuts") || Shown().Contains("Try \""), 30) && !console.Closed, "its box is there");
            Thread.Sleep(1500);
            type("Reply with only the word READY.");
            Thread.Sleep(800);
            type("\r");
            Require(Until(() => File.Exists(kept), 120), "the status line kept the limits");
            Console.WriteLine("kept: " + File.ReadAllText(kept));
            Require(Until(() => Shown().Contains("5 小时") || Shown().Contains("本周"), 30), "the line on its screen says the limits");
            type("/exit\r");
            Thread.Sleep(2000);
        }
        Console.WriteLine("Claude Code's status line on the real tool: the limits kept, and shown on its screen");
        return 0;
    }
}
