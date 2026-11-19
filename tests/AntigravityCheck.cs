using System;
using System.Linq;
using System.Threading;
using RemoteCli;

internal static class AntigravityCheck {
    static void Check(bool ok, string message) {
        if (!ok) throw new Exception(message);
    }
    static void Main(string[] args) {
        Check(TerminalAgent.Arguments("agy", "", false, "read") == "", "agy keeps user default even in read mode");
        Check(TerminalAgent.Arguments("agy", "", false, "edit") == "", "agy keeps user default even in edit mode");
        Check(TerminalAgent.Arguments("agy", "", false, "full") == "", "default mode");
        try {
            TerminalAgent.Arguments("agy", Guid.NewGuid().ToString(), false, "read");
            throw new Exception("unexpected conversation resume");
        } catch (ArgumentException) {}
        if (args.Length == 0) return;
        string launcher = TerminalAgent.FindTool("agy");
        Check(launcher != null, "install agy to run the optional live check");
        var output = new ManualResetEventSlim();
        using (var terminal = new PseudoTerminal(TerminalAgent.Command("agy", launcher, "", false, "read"),
                   args[0], 80, 24, text => { if (text.Length > 0) output.Set(); })) {
            Check(output.Wait(15000) && !terminal.Closed, "expected live CLI output");
            terminal.Resize(72, 24);
            terminal.Write("\x1b"); // Escape in this test terminal only.
            using (var proc = System.Diagnostics.Process.GetProcessById(terminal.Id)) {
                long start = new DateTimeOffset(proc.StartTime).ToUnixTimeMilliseconds();
                Check(Antigravity.ExternalStarts(launcher, new int[0]).Contains(start), "running agy missing");
                Check(!Antigravity.ExternalStarts(launcher, new[] {terminal.Id}).Contains(start), "owned agy counted twice");
            }
        }
        Console.WriteLine("Live agy: ConPTY output, resize, Escape and process ownership passed.");
    }
}
