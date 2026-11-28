using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using RemoteCli;

/// When a conversation the phone runs tells that its turn is over (Chat.Turned, which Teams.cs passes on): once a
/// turn has really ended, and not while commands it started still run in the background, with what it said at the
/// very end. The tool is the stand-in (tests/chat_standin.py), run as the computer runs the real one.
///     ChatTurnsCheck.exe <tool> <stand-in launcher, a .cmd> <empty folder>
public static class ChatTurnsCheck {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }

    public static int Main(string[] args) {
        string tool = args[0], launcher = args[1], folder = args[2];
        var turned = new List<string[]>();
        var events = new List<string>();
        using (var chat = Chat.Open(tool, launcher, folder, "", false, "full", lines => { lock (events) events.Add(lines); })) {
            chat.Turned += (how, said) => { lock (turned) { turned.Add(new[] { how, said }); Monitor.PulseAll(turned); } };
            Func<int, bool> await = count => {
                var end = DateTime.UtcNow.AddSeconds(30);
                lock (turned) while (turned.Count < count && DateTime.UtcNow < end) Monitor.Wait(turned, 200);
                return turned.Count >= count;
            };
            chat.Say("在后台跑测试", "b1");
            Require(await(1), "the turn was told at last: " + String.Join("", events));
            Thread.Sleep(1500);         // (nothing more comes)
            lock (turned) {
                Require(turned.Count == 1, "told once, not at the turn that left the tests in the background: " + turned.Count + " " + String.Join(" | ", turned.ConvertAll(t => t[0] + ":" + t[1])));
                Require(turned[0][0] == "done" && turned[0][1] == "测试全部通过，改了 checkout.js。", "with what it said at the end: " + turned[0][0] + " " + turned[0][1]);
            }
            lock (events) Require(String.Join("", events).Split(new[] { "\"t\":\"turn\"" }, StringSplitOptions.None).Length == 3, "the phone is shown both ends as they came");
            lock (events) {
                string stream = String.Join("", events);
                int first = stream.IndexOf("\"t\":\"turn\"", StringComparison.Ordinal);
                int last = stream.LastIndexOf("\"t\":\"turn\"", StringComparison.Ordinal);
                Require(!stream.Substring(first, last - first).Contains("\"state\":\"idle\""), "background work must remain busy between the two results");
                Require(stream.Substring(last).Contains("\"state\":\"idle\""), "the final result returns to idle");
            }
            chat.Dispose();
            Require(SpinWait.SpinUntil(() => chat.Closed, 5000) && chat.ExitCode == 0, "closing a live chat is an expected exit");
        }
        using (var chat = Chat.Open(tool, launcher, folder, "", false, "full", lines => {})) {
            chat.Say("standin-exit-1", "exit");
            Require(SpinWait.SpinUntil(() => chat.Closed, 5000), "tool exited");
            chat.Dispose();
            Require(chat.ExitCode == 1, "cleanup preserves the tool's own failure");
        }
        var shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        foreach (bool close in new[] { true, false }) {
            using (var terminal = new PseudoTerminal(shell + " /d /q", folder, 80, 24, lines => {})) {
                if (close) terminal.Dispose(); else terminal.Write("exit 1\r");
                if (!SpinWait.SpinUntil(() => terminal.ExitCode.HasValue, 8000) || terminal.ExitCode != (close ? 0 : 1)) {
                    Console.Error.WriteLine("ConPTY close={0}, code={1}, closed={2}", close, terminal.ExitCode, terminal.Closed);
                    return 1;
                }
            }
        }
        Console.WriteLine(tool + ": a turn that leaves commands in the background is told when the tool has gone on and ended");
        return 0;
    }
}
