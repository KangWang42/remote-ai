using System;
using RemoteCli;

/// What the program on the computer says about a relay of one's own: nothing when it can be used, and otherwise
/// which of the address, the certificate and the password is wrong. The addresses and the password are arguments.
public static class RelayCheck {
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static int Main(string[] args) {
        string relay = args[0], password = args[1], other = args[2], closed = args[3];
        new TerminalAgent(System.IO.Path.GetTempPath());       // as the program does on starting: modern TLS is switched on
        Require(TerminalAgent.CheckRelay(relay, password) == "", "the right password must be accepted: " + TerminalAgent.CheckRelay(relay, password));
        Require(TerminalAgent.CheckRelay(relay, password + "x").Contains("密码"), "a wrong password must be told as such: " + TerminalAgent.CheckRelay(relay, password + "x"));
        Require(TerminalAgent.CheckRelay(other, password).Contains("没有 Remote AI 中转"), "another web server is not a relay: " + TerminalAgent.CheckRelay(other, password));
        Require(TerminalAgent.CheckRelay(closed, password).Contains("连不上中转"), "a closed port must be told as such: " + TerminalAgent.CheckRelay(closed, password));
        Require(TerminalAgent.CheckRelay("http://no-such-host.invalid:8722", password).Length > 0, "an unknown name must be told");
        if (args.Length > 5) Require(TerminalAgent.CheckRelay(args[4], args[5]) == "", "the relay over https must be accepted: " + TerminalAgent.CheckRelay(args[4], args[5]));
        Console.WriteLine("relay check: accepted, wrong password, not a relay, closed port and unknown name are told apart" + (args.Length > 5 ? "; https accepted" : ""));
        return 0;
    }
}
