using System;
using System.Collections.Generic;
using System.Linq;

namespace RemoteCli {

/// Claude Code in print mode (tried on 2.1.322): `--safe-mode` leaves out CLAUDE.md, skills, plugins, hooks and MCP
/// servers (about 1,300 input tokens a request instead of 12,000 with the owner's CLAUDE.md), `--tools ""` offers
/// no tool, `--no-session-persistence` keeps no conversation, and `--json-schema` returns the entry as
/// `structured_output`, with `usage` and `is_error` beside it (`--output-format json`).
internal sealed class ClaudeOrganizer : IOrganizeSource {
    const int Timeout = 120000;
    readonly string launcher, folder;
    readonly Func<Dictionary<string, object>[]> listed;

    internal ClaudeOrganizer(string launcher, string folder, Func<Dictionary<string, object>[]> listed) {
        this.launcher = launcher; this.folder = folder; this.listed = listed;
    }

    public string Label { get { return "Claude Code"; } }
    public Dictionary<string, object>[] Models() {
        var offered = listed();
        // Claude Code takes the name "haiku" for its smallest model even where it lists the models by other names
        return offered.Any(m => Organizer.Text(m, "id") == "haiku") ? offered
            : new[] { new Dictionary<string, object> { { "id", "haiku" }, { "label", "Haiku" } } }.Concat(offered).ToArray();
    }
    public string DefaultModel(Dictionary<string, object>[] offered) { return "haiku"; }

    internal static string[] Arguments(string model) {
        return new[] { "-p", "--safe-mode", "--model", model, "--tools", "", "--strict-mcp-config", "--disable-slash-commands",
            "--no-session-persistence", "--system-prompt", OrganizeReply.Instructions, "--json-schema", OrganizeReply.SchemaText,
            "--output-format", "json" };
    }

    public Organizer.Metadata Analyze(string context, string model) {
        var answer = OrganizeProcess.Json(OrganizeProcess.Run(launcher, Arguments(model), folder, OrganizeReply.Request(context), Timeout, Label), Label);
        object failed, output;
        if (answer.TryGetValue("is_error", out failed) && true.Equals(failed))
            throw new InvalidOperationException("Claude Code 没有完成整理：" + OrganizeReply.Limit(Organizer.Text(answer, "result"), 200));
        if (!answer.TryGetValue("structured_output", out output)) throw new InvalidOperationException("Claude Code 没有返回整理结果，请重试");
        var data = OrganizeReply.Parse(output);
        data.Tokens = Tokens(answer);
        return data;
    }

    static long Tokens(Dictionary<string, object> answer) {
        object usage;
        var used = answer.TryGetValue("usage", out usage) ? usage as Dictionary<string, object> : null;
        if (used == null) return 0;
        return new[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens" }
            .Sum(key => { object n; return used.TryGetValue(key, out n) ? OrganizeReply.Number(n) : 0; });
    }
}
}
