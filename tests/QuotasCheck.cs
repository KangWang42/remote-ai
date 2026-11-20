using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using RemoteCli;

/// How much of each tool's limits is used (agent-windows/Quotas.cs): what the tools say, read; what is kept and
/// reported; Claude Code's status line run as Claude Code runs it; and the status line put into its settings and
/// taken out, the owner's own kept.
///     QuotasCheck.exe <RemoteCliAgent.exe>
public static class QuotasCheck {
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    static readonly JavaScriptSerializer json = new JavaScriptSerializer();
    static long Now { get { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; } }

    public static int Main(string[] args) {
        string agent = args[0], folder = Path.Combine(Path.GetTempPath(), "remote-ai-quotas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            // ---- what the tools say, read. Claude Code's event as it came from 2.1.322
            var claude = Quotas.FromClaudeEvent(json.DeserializeObject("{\"status\":\"allowed\",\"resetsAt\":1795126800,\"rateLimitType\":\"five_hour\",\"overageStatus\":\"rejected\",\"isUsingOverage\":false,\"unifiedWindows\":{\"five_hour\":{\"utilization\":0.38,\"resetsAt\":1795126800,\"status\":\"allowed\"},\"seven_day\":{\"utilization\":0.09,\"resetsAt\":1795665600,\"status\":\"allowed\"}}}"));
            Require(claude.Count == 2 && claude[0].Name == "five_hour" && Math.Abs(claude[0].Used - 38) < 0.01 && claude[0].Resets == 1795126800 && claude[1].Name == "seven_day" && Math.Abs(claude[1].Used - 9) < 0.01, "Claude Code's event");
            var status = Quotas.FromClaudeStatus(json.DeserializeObject("{\"model\":{\"id\":\"x\"},\"rate_limits\":{\"five_hour\":{\"used_percentage\":23.5,\"resets_at\":1795126800},\"seven_day\":{\"used_percentage\":41,\"resets_at\":1795665600}}}"));
            Require(status.Count == 2 && Math.Abs(status[0].Used - 23.5) < 0.01 && status[1].Used == 41 && status[1].Resets == 1795665600, "Claude Code's status line");
            Require(Quotas.FromClaudeStatus(json.DeserializeObject("{\"model\":{\"id\":\"x\"}}")).Count == 0, "a status line without limits (an account without a subscription) tells of none");
            var codex = Quotas.FromCodex(json.DeserializeObject("{\"primary\":{\"usedPercent\":12,\"windowDurationMins\":300,\"resetsAt\":1795126800},\"secondary\":{\"usedPercent\":64.5,\"windowDurationMins\":10080,\"resetsAt\":1795665600000}}"));
            Require(codex.Count == 2 && codex[0].Name == "five_hour" && codex[1].Name == "seven_day" && codex[1].Resets == 1795665600 && Math.Abs(codex[1].Used - 64.5) < 0.01, "Codex's limits, a time in milliseconds read as seconds");
            Require(Quotas.FromCodex(json.DeserializeObject("{\"primary\":{\"usedPercent\":5,\"windowDurationMins\":15},\"secondary\":null}"))[0].Name == "15m", "a window of another length is named by it");
            // Antigravity CLI's `agy -p /usage --output-format json`: primary Gemini/Neon pool first (5h before weekly),
            // secondary pools kept with their group names so the phone can fold them behind a toggle.
            var agy = Quotas.FromAgy(json.DeserializeObject("{\"status\":\"SUCCESS\",\"command\":{\"name\":\"usage\",\"data\":{\"groups\":["
                + "{\"name\":\"Claude and GPT models\",\"description\":\"Claude Opus, GPT-OSS\",\"buckets\":[{\"id\":\"3p-weekly\",\"window\":\"weekly\",\"remaining_fraction\":0.6,\"reset_time\":\"2026-11-27T04:33:08Z\"},{\"id\":\"3p-5h\",\"window\":\"5h\",\"remaining_fraction\":0.9,\"reset_time\":\"2026-11-20T09:33:08Z\"}]},"
                + "{\"name\":\"Gemini Models\",\"description\":\"Models within this group: Gemini 4 Neon, Gemini 4 Hydrogen\",\"buckets\":[{\"id\":\"gemini-weekly\",\"window\":\"weekly\",\"remaining_fraction\":0.95,\"reset_time\":\"2026-11-27T04:43:04Z\"},{\"id\":\"gemini-5h\",\"window\":\"5h\",\"remaining_fraction\":0.82,\"reset_time\":\"2026-11-20T09:43:04Z\"}]},"
                + "{\"name\":\"Gemini 4 Argon\",\"description\":\"Argon\",\"buckets\":[{\"id\":\"gemini-argon-wtu-weekly\",\"window\":\"weekly\",\"remaining_fraction\":0.25,\"reset_time\":\"2026-11-27T04:34:41Z\"}]}]}}}"));
            Require(agy.Count == 5, "all five agy buckets parsed: " + agy.Count);
            Require(agy[0].Name == "five_hour" && agy[0].Group == "" && Math.Abs(agy[0].Used - 18) < 0.01 && agy[0].Resets > 0, "Neon primary 5h first: " + agy[0].Name + "/" + agy[0].Used);
            Require(agy[1].Name == "seven_day" && agy[1].Group == "" && Math.Abs(agy[1].Used - 5) < 0.01, "Neon primary weekly second");
            Require(agy[2].Name == "3p_five_hour" && agy[2].Group == "Claude / GPT" && Math.Abs(agy[2].Used - 10) < 0.01, "secondary 3p 5h");
            Require(agy[3].Name == "3p_seven_day" && agy[3].Group == "Claude / GPT" && Math.Abs(agy[3].Used - 40) < 0.01, "secondary 3p weekly");
            Require(agy[4].Name == "gemini_argon_wtu_seven_day" && agy[4].Group == "Gemini 4 Argon" && Math.Abs(agy[4].Used - 75) < 0.01, "secondary Argon weekly");

            // ---- kept and reported; a window whose time is over is left out
            var quotas = new Quotas(folder);
            quotas.Note("claude", new[] { new Quotas.Window { Name = "five_hour", Used = 38, Resets = Now + 3600 }, new Quotas.Window { Name = "seven_day", Used = 120, Resets = Now - 10 } });
            quotas.Unknown("codex", "Codex 用 ChatGPT 账号登录后才能读取额度");
            quotas.Note("agy", new[] { new Quotas.Window { Name = "five_hour", Used = 12, Resets = Now + 3600 }, new Quotas.Window { Name = "3p_five_hour", Group = "Claude / GPT", Used = 44, Resets = Now + 3600 } });
            var report = quotas.Report().Cast<Dictionary<string, object>>().ToList();
            Require(report.Count == 3 && (string)report[0]["tool"] == "claude" && ((object[])report[0]["windows"]).Length == 1, "the window whose time is over is left out: " + json.Serialize(report));
            Require((string)report[1]["why"] == "Codex 用 ChatGPT 账号登录后才能读取额度" && ((object[])report[1]["windows"]).Length == 0, "why one is not known");
            var agyReported = (object[])report[2]["windows"];
            Require((string)report[2]["tool"] == "agy" && agyReported.Length == 2 && !((Dictionary<string, object>)agyReported[0]).ContainsKey("group") && (string)((Dictionary<string, object>)agyReported[1])["group"] == "Claude / GPT", "agy primary ungrouped, secondary grouped");
            Require(!json.Serialize(report).Contains("token") && !json.Serialize(report).Contains("account"), "nothing of the account");

            // ---- AskAgyNow against stand-in launchers: parses JSON + passes AGY_CLI_DISABLE_AUTO_UPDATE=true, and records why on API key exit code 2
            string agyOk = Path.Combine(folder, "fake-agy-ok.cmd");
            string agyJson = Path.Combine(folder, "fake-agy.json");
            long future = Now + 7200;
            File.WriteAllText(agyJson, "{\"status\":\"SUCCESS\",\"command\":{\"name\":\"usage\",\"data\":{\"groups\":[{\"name\":\"Gemini Models\",\"description\":\"Gemini 4 Neon\",\"buckets\":[{\"id\":\"gemini-5h\",\"window\":\"5h\",\"remaining_fraction\":0.75,\"reset_time\":" + future + "}]},{\"name\":\"Gemini 4 Argon\",\"buckets\":[{\"id\":\"gemini-argon-wtu-weekly\",\"window\":\"weekly\",\"remaining_fraction\":0.5,\"reset_time\":" + future + "}]}]}}}", new UTF8Encoding(false));
            File.WriteAllText(agyOk, "@echo off\r\nif not \"%AGY_CLI_DISABLE_AUTO_UPDATE%\"==\"true\" exit /b 9\r\ntype \"" + agyJson + "\"\r\n", Encoding.ASCII);
            var agyBox = new Quotas(Path.Combine(folder, "agy-box"));
            agyBox.AskAgyNow(agyOk);
            var agyBoxReport = agyBox.Report().Cast<Dictionary<string, object>>().Single();
            Require((string)agyBoxReport["tool"] == "agy" && ((object[])agyBoxReport["windows"]).Length == 2, "AskAgyNow records parsed windows: " + json.Serialize(agyBoxReport));
            string agyKey = Path.Combine(folder, "fake-agy-key.cmd");
            File.WriteAllText(agyKey, "@echo off\r\necho No account quota with GEMINI_API_KEY 1>&2\r\nexit /b 2\r\n", Encoding.ASCII);
            agyBox.AskAgyNow(agyKey);
            agyBoxReport = agyBox.Report().Cast<Dictionary<string, object>>().Single();
            Require(((object[])agyBoxReport["windows"]).Length == 0 && ((string)agyBoxReport["why"]).Contains("Google 账号"), "AskAgyNow records why for API key login: " + json.Serialize(agyBoxReport));

            // ---- the status line as Claude Code runs it: its input on the standard input, the line on the standard output
            string data = Path.Combine(folder, "data");
            Func<string, string[], string> run = (input, extra) => {
                var start = new ProcessStartInfo(agent, Quotas.Flag + " \"" + data + "\"" + (extra.Length > 0 ? " " + String.Join(" ", extra) : "")) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false) };
                using (var hook = Process.Start(start)) {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(input);
                    hook.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); hook.StandardInput.Close();
                    string said = hook.StandardOutput.ReadToEnd();
                    Require(hook.WaitForExit(10000) && hook.ExitCode == 0, "the status line ended");
                    return said;
                }
            };
            string line = run("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":23.5,\"resets_at\":" + (Now + 3600) + "},\"seven_day\":{\"used_percentage\":41,\"resets_at\":" + (Now + 86400) + "}}}", new string[0]);
            Require(line == "5 小时 24% · 本周 41%", "the line it shows: " + line);
            var seen = new Quotas(data).Report().Cast<Dictionary<string, object>>().Single();
            Require((string)seen["tool"] == "claude" && ((object[])seen["windows"]).Length == 2, "what it told is kept where the computer reports from");
            string own = "echo mine";
            line = run("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":50,\"resets_at\":" + (Now + 3600) + "}}}", new[] { "--then", Convert.ToBase64String(Encoding.UTF8.GetBytes(own)) });
            Require(line.Trim() == "mine", "the owner's own status line is what shows: " + line);
            Require(Math.Abs(Convert.ToDouble(((Dictionary<string, object>)((object[])new Quotas(data).Report().Cast<Dictionary<string, object>>().Single()["windows"])[0])["used"]) - 50) < 0.01, "and the limits are kept all the same");
            Require(run("not json", new string[0]) == "", "words that are no status say nothing");

            // ---- into Claude Code's settings and out again; the owner's own line is kept and put back
            string home = Path.Combine(folder, "claude"); Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", home);
            // the program where an owner might have put it: a folder with a space in its name
            string program = Path.Combine(folder, "Remote AI", "RemoteCliAgent.exe"), settings = Path.Combine(home, "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(program)); File.Copy(agent, program);
            File.WriteAllText(settings, "{\n  \"model\": \"opus\"\n}\n", new UTF8Encoding(false));
            Require(!QuotaStatusLine.Installed(program, data), "none to begin with");
            QuotaStatusLine.Set(true, program, data);
            var read = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(settings, Encoding.UTF8));
            string command = (string)((Dictionary<string, object>)read["statusLine"])["command"];
            // one line for any shell (Claude Code on Windows runs it with PowerShell): nothing in quotes, no path with a space
            Require(!command.Contains("\"") && !command.Contains("'") && !command.Contains("Remote AI") && command.Contains(" --statusline ") && (string)read["model"] == "opus" && QuotaStatusLine.Installed(program, data), "put in, as one plain line: " + command);
            string runs = command.Substring(0, command.IndexOf(" --statusline ", StringComparison.Ordinal));
            Require(File.Exists(runs.Replace('/', '\\')), "what it runs is there: " + runs);
            Require(!QuotaStatusLine.Command(program, Path.Combine(folder, "with space"), "").Contains("with space"), "a data folder with a space is named by its short name");
            Require(QuotaStatusLine.Copy(data) == Path.Combine(data, "statusline", "RemoteCliAgent.exe"), "where a copy of the program would be kept, for a drive without short names");
            QuotaStatusLine.Set(false, program, data);
            read = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(settings, Encoding.UTF8));
            Require(!read.ContainsKey("statusLine") && (string)read["model"] == "opus", "taken out, nothing in its place");
            File.WriteAllText(settings, "{\n  \"statusLine\": {\"type\": \"command\", \"command\": \"bash ~/.claude/line.sh\", \"padding\": 1}\n}\n", new UTF8Encoding(false));
            QuotaStatusLine.Set(true, program, data);
            QuotaStatusLine.Set(true, program, data);
            var line2 = (Dictionary<string, object>)json.Deserialize<Dictionary<string, object>>(File.ReadAllText(settings, Encoding.UTF8))["statusLine"];
            Require(((string)line2["command"]).Contains("--then ") && Convert.ToInt32(line2["padding"]) == 1 && QuotaStatusLine.Installed(program, data), "the owner's own line is run after it, once: " + line2["command"]);
            string moved = Path.Combine(folder, "moved", "RemoteCliAgent.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)); File.Copy(agent, moved);
            Require(!QuotaStatusLine.Installed(moved, data), "another place of the program is not this one");
            QuotaStatusLine.Set(true, moved, data);
            line2 = (Dictionary<string, object>)json.Deserialize<Dictionary<string, object>>(File.ReadAllText(settings, Encoding.UTF8))["statusLine"];
            Require(((string)line2["command"]).StartsWith(moved.Replace('\\', '/')) && ((string)line2["command"]).Contains("--then "), "moved: the owner's line is still kept");
            QuotaStatusLine.Set(false, moved, data);
            line2 = (Dictionary<string, object>)json.Deserialize<Dictionary<string, object>>(File.ReadAllText(settings, Encoding.UTF8))["statusLine"];
            Require((string)line2["command"] == "bash ~/.claude/line.sh", "taken out: the owner's own line is back");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
        } finally { try { Directory.Delete(folder, true); } catch { } }
        Console.WriteLine("quotas: read from the three tools, kept and reported, Claude Code's status line run and put into its settings and out");
        return 0;
    }
}
