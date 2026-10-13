using System;
using System.Collections.Generic;
using System.Linq;

namespace RemoteCli {
/// What the phone can do to a conversation that a program on this computer has open, and by which way. It is said
/// in one place, for the list the phone is shown and for carrying out what it asks, so that the two cannot differ;
/// and it is said the same for every tool, each by what is its own.
///
/// A conversation has up to three ways in. For each thing that can be done the best one that is there is used, and
/// the next one where it is not:
///   App    the application's own channel (Codex's desktop app: CodexDesktopControl)
///   Hook   the tool's hooks (Reach): what is said is left for a conversation at work and handed to it when it
///          stops, a request is answered, Claude Code is told to stop before its next step
///   Wake   the tool's own way to a conversation that sits idle: Claude Code's messages between conversations (its
///          desktop app's too), Codex's queue for its command line
/// Ending is another thing: the program that has the conversation is closed (Process), where it is known for
/// certain which one that is. Where a thing cannot be done, `Why` says so in words the phone shows as they are.
///
/// A tool or a channel that is added gets its lines in `Of`; nothing else decides any of this.
public sealed class Ways {
    public const string App = "codex-desktop", Hook = "hook", Wake = "wake", Process = "process";
    public const string CodexDesktop = "Codex 桌面版";      // by what a conversation of Codex's desktop app is told (SessionInfo.Origin)
    public static readonly string[] Things = { "say", "stop", "answer", "end" };

    /// The way used for each thing, "" where there is none.
    public string Say = "", Stop = "", Answer = "", End = "";
    /// For what cannot be done (by the names in Things): why, in words.
    public readonly Dictionary<string, string> Why = new Dictionary<string, string>();
    /// How it stands (busy, confirm, waiting, idle), whether it is in use now, and the turn the application's channel would stop.
    public string Status = "", Turn = "";
    public bool Now;
    /// Whether its tool or its application tells of it at all: of one that neither does, the phone is told nothing.
    public bool Told;
    bool channelled;

    /// As the phone is told: what it can do, by name.
    public string[] Reach { get { return !Told ? null : new[] { Say.Length > 0 ? "say" : null, Stop.Length > 0 ? "stop" : null, Answer.Length > 0 ? "answer" : null }.Where(way => way != null).ToArray(); } }
    /// The way of each thing by its name.
    public Dictionary<string, object> Named { get { return new Dictionary<string, object> { { "say", Say }, { "stop", Stop }, { "answer", Answer }, { "end", End } }; } }
    /// For a relay or a page that knows one way for a conversation only: the application's channel where it has
    /// one, else the hooks where they stop it.
    public string Transport { get { return channelled ? App : Stop == Hook ? Hook : ""; } }
    /// What is said is handed to it where it sits idle, and it is woken: that takes some seconds.
    public bool Woken { get { return Say == Wake; } }

    public static Ways Of(TerminalAgent.SessionInfo s, Reach.Open known, CodexDesktopSessions.State channel) {
        var ways = new Ways { Status = known != null ? known.Status : s.Status, Now = s.Now };
        if (!s.Live) { foreach (string thing in Things) ways.Why[thing] = "这段对话没有在电脑上开着"; return ways; }
        bool claude = s.Tool == "claude", desktop = s.Origin == CodexDesktop, asks = known != null && known.Status == "confirm";
        if (channel != null) {
            // The application says how it stands; a request that its tool's hook tells of stands before that.
            ways.Status = asks ? "confirm" : channel.Busy ? "busy" : channel.Ready ? "waiting" : "confirm";
            ways.Now = channel.Busy || ways.Status == "confirm";
            ways.Turn = channel.Turn ?? "";
            ways.channelled = true;
        }
        bool busy = ways.Status == "busy";
        ways.Told = known != null || channel != null || (s.Wakes && !desktop);

        // ---- saying something to it
        if (channel != null && channel.Ready && !asks) ways.Say = App;
        else if (known != null && known.Sayable) ways.Say = Hook;
        // (never Codex's queue for its desktop app: the queue is another program's, which would write to a conversation the app has)
        else if (s.Wakes && !desktop && !asks) ways.Say = Wake;
        else ways.Why["say"] = channel != null ? "Codex 桌面版还没有就绪：等它当前的执行或电脑上的确认结束后再发"
            : desktop ? "Codex 桌面版里的这段对话现在不能从手机发消息：它不在已添加的项目里，或桌面应用的版本还没有适配。它执行时发的消息会在它做完后收到"
            : "电脑上的 " + (claude ? "Claude Code" : "Codex 命令行") + " 现在收不到手机的消息：它执行时发的消息会在它做完后收到";

        // ---- stopping what it is at; it stays open
        if (channel != null && ways.Turn.Length > 0) ways.Stop = App;
        else if (known != null && known.Stoppable) ways.Stop = Hook;
        else ways.Why["stop"] = !busy ? "这段对话现在没有在执行"
            : channel != null ? "Codex 桌面版正在执行一个目标，或同时有几轮在进行：这种情况只能在电脑上停止"
            : claude ? "这段对话是在可以从手机停止之前打开的，在电脑上重新打开它之后才行"
            : desktop ? "Codex 桌面版里的这段对话不在已添加的项目里，或桌面应用的版本还没有适配：只能在电脑上停止"
            : "Codex 命令行的回答不能从外面单独停止";

        // ---- answering what it asks
        if (asks && known.Answerable) ways.Answer = Hook;
        else ways.Why["answer"] = asks ? "这个确认请求只能在电脑上回答" : "这段对话现在没有在等确认";

        // ---- ending the program that has it
        if (s.CanTakeover) ways.End = Process;
        else ways.Why["end"] = String.IsNullOrEmpty(s.TakeoverReason) ? "这段对话由电脑上的应用管理，请在那里关闭它" : s.TakeoverReason;
        return ways;
    }
}
}
