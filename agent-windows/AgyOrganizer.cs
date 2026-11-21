using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RemoteCli {

/// Antigravity CLI in print mode (tried on 1.5.13): `-p=<prompt>` runs one turn, `--json-schema <file>` returns the
/// entry as `structured_output` with `status` and `usage.total_tokens` (`--output-format json`), and a settings
/// override with `toolPermission: "strict"` keeps tool steps from being approved by themselves (the owner may have
/// `always-proceed`). agy has no switch for its hooks or for keeping the conversation: the conversation stays in agy's
/// own records under the organizer's folder, which the organizer's catalog leaves out (OrganizeHistory.List).
internal sealed class AgyOrganizer : IOrganizeSource {
    const int Timeout = 120000;
    readonly string launcher, folder;
    readonly Func<Dictionary<string, object>[]> listed;

    internal AgyOrganizer(string launcher, string folder, Func<Dictionary<string, object>[]> listed) {
        this.launcher = launcher; this.folder = folder; this.listed = listed;
    }

    public string Label { get { return "Antigravity CLI"; } }
    public Dictionary<string, object>[] Models() { return listed(); }
    public string DefaultModel(Dictionary<string, object>[] offered) {
        return OrganizeReply.Cheapest(offered, new[] { "flash-low", "flash", "-low" }, "gemini-3.8-flash-low");
    }

    public Organizer.Metadata Analyze(string context, string model) {
        Directory.CreateDirectory(folder);
        string schema = Path.Combine(folder, "agy-schema.json"), settings = Path.Combine(folder, "agy-settings.json");
        File.WriteAllText(schema, OrganizeReply.SchemaText, new UTF8Encoding(false));
        File.WriteAllText(settings, "{\"toolPermission\":\"strict\"}", new UTF8Encoding(false));
        // agy reads its prompt from the command line; the context is JSON, so its line breaks are escaped already
        string prompt = OrganizeReply.Instructions + " " + OrganizeReply.Request(context).Replace('\n', ' ');
        var arguments = new[] { "-p=" + prompt, "--model", model, "--disable-slash-commands", "--settings", settings,
            "--json-schema", schema, "--output-format", "json", "--print-timeout", Timeout / 1000 + "s" };
        var answer = OrganizeProcess.Json(OrganizeProcess.Run(launcher, arguments, folder, "", Timeout + 10000, Label), Label);
        object output;
        if (Organizer.Text(answer, "status") != "SUCCESS" || !answer.TryGetValue("structured_output", out output))
            throw new InvalidOperationException("Antigravity CLI 没有完成整理，请重试");
        var data = OrganizeReply.Parse(output);
        object usage, total;
        var used = answer.TryGetValue("usage", out usage) ? usage as Dictionary<string, object> : null;
        data.Tokens = used != null && used.TryGetValue("total_tokens", out total) ? OrganizeReply.Number(total) : 0;
        return data;
    }
}
}
