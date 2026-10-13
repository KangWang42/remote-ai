using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteCli {
/// Optional official CLI installers; no commands from configuration or remote requests are executed here.
internal static class ToolInstaller {
    internal static string Source(string tool) {
        if (tool == "claude") return "https://claude.ai/install.ps1";
        if (tool == "codex") return "https://chatgpt.com/codex/install.ps1";
        throw new ArgumentException("未知工具");
    }
    internal static async Task<string> Download(string tool, string directory) {
        string script = Path.Combine(directory, tool + "-install.ps1");
        Directory.CreateDirectory(directory);
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        using (var client = new HttpClient()) {
            client.Timeout = TimeSpan.FromSeconds(90);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RemoteAI-ToolInstaller/1.0");
            using (var response = await client.GetAsync(Source(tool))) {
                response.EnsureSuccessStatusCode();
                var source = response.RequestMessage.RequestUri;
                string host = new Uri(Source(tool)).Host;
                bool official = source.Host == host || tool == "codex" && source.Host == "releases.openai.com" || tool == "claude" && source.Host == "downloads.claude.ai";
                if (source.Scheme != "https" || !official) throw new IOException("安装来源不是预期的官方 HTTPS 地址");
                string text = await response.Content.ReadAsStringAsync();
                if (String.IsNullOrWhiteSpace(text) || text.Length > 2000000 || text.TrimStart().StartsWith("<")) throw new IOException("没有下载到有效的安装脚本");
                File.WriteAllText(script, text, new UTF8Encoding(true));
            }
        }
        return script;
    }
    internal static async Task<string> Install(string tool, string directory) {
        string script = await Download(tool, directory);
        var start = new ProcessStartInfo(TerminalAgent.FindTool("shell"), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = directory
        };
        using (var process = Process.Start(start)) {
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            bool ended = await Task.Run(() => process.WaitForExit(600000));
            if (!ended) { try { process.Kill(); } catch (InvalidOperationException) { } throw new IOException("安装超过 10 分钟，请检查网络后重试"); }
            string log = await output + Environment.NewLine + await error;
            File.WriteAllText(Path.Combine(directory, tool + "-install.log"), log, new UTF8Encoding(true));
            if (process.ExitCode != 0) throw new IOException("安装程序退出码 " + process.ExitCode + "，可打开安装记录查看原因");
        }
        string found = TerminalAgent.FindTool(tool);
        if (found == null) throw new IOException("安装程序已结束，但尚未找到工具。请查看安装记录，或重启 Remote AI 后重试");
        if (Path.GetExtension(found).Equals(".exe", StringComparison.OrdinalIgnoreCase)) {
            using (var check = Process.Start(new ProcessStartInfo(found, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })) {
                var output = check.StandardOutput.ReadToEndAsync(); var error = check.StandardError.ReadToEndAsync();
                if (!await Task.Run(() => check.WaitForExit(15000))) { try { check.Kill(); } catch (InvalidOperationException) { } throw new IOException("工具安装后没有响应版本检查"); }
                await output; await error;
                if (check.ExitCode != 0) throw new IOException("工具安装后未通过版本检查，请查看安装记录");
            }
        }
        return found;
    }
    internal static void Show(Form owner, string dataDir) {
        using (var dialog = new Form { Text = "安装终端工具（可选）", Width = 510, Height = 355, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog }) {
            var intro = new Label { Left = 20, Top = 18, Width = 450, Height = 65, Text = "可选安装 Claude Code 和 Codex 终端工具。接受后会联网下载并运行各自的官方安装程序，安装到当前用户目录。安装后需自行登录账号，使用费用由对应服务收取。" };
            var claude = new CheckBox { Left = 20, Top = 88, Width = 450, Text = "Claude Code（claude.ai）", Checked = TerminalAgent.FindTool("claude") == null };
            var codex = new CheckBox { Left = 20, Top = 118, Width = 450, Text = "Codex（chatgpt.com）", Checked = TerminalAgent.FindTool("codex") == null };
            claude.Enabled = claude.Checked; codex.Enabled = codex.Checked;
            if (!claude.Enabled) claude.Text += " · 已安装";
            if (!codex.Enabled) codex.Text += " · 已安装";
            var status = new Label { Left = 20, Top = 154, Width = 450, Height = 90, Text = "可以跳过，以后从“设置 → 安装终端工具”进入。" };
            var install = new Button { Left = 270, Top = 258, Width = 110, Height = 34, Text = "接受并安装" };
            install.Enabled = claude.Checked || codex.Checked;
            if (!install.Enabled) status.Text = "两项终端工具均已安装。首次使用时，在终端中运行 claude 或 codex 并登录账号。";
            var close = new Button { Left = 390, Top = 258, Width = 80, Height = 34, Text = "跳过", DialogResult = DialogResult.Cancel };
            var logs = new Button { Left = 20, Top = 258, Width = 115, Height = 34, Text = "打开安装记录" };
            string directory = Path.Combine(dataDir, "tool-install");
            logs.Click += (s, e) => { Directory.CreateDirectory(directory); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + directory + "\"") { UseShellExecute = true }); };
            bool busy = false;
            dialog.FormClosing += (s, e) => { if (busy) e.Cancel = true; };
            install.Click += async (s, e) => {
                if (!claude.Checked && !codex.Checked) { status.Text = "请选择需要安装的工具。"; return; }
                busy = true; install.Enabled = close.Enabled = claude.Enabled = codex.Enabled = false;
                string result = "";
                foreach (string tool in new[] { "claude", "codex" }) {
                    var box = tool == "claude" ? claude : codex;
                    if (!box.Checked) continue;
                    status.Text = result + "正在安装 " + (tool == "claude" ? "Claude Code" : "Codex") + "，下载可能需要几分钟…";
                    try { await Install(tool, directory); result += tool + "：安装完成，请在终端中登录。\r\n"; box.Checked = false; }
                    catch (Exception error) { result += tool + "：" + error.Message + "\r\n"; }
                }
                status.Text = result; busy = false; close.Enabled = true; close.Text = "关闭";
                install.Enabled = claude.Checked || codex.Checked;
                claude.Enabled = TerminalAgent.FindTool("claude") == null; codex.Enabled = TerminalAgent.FindTool("codex") == null;
            };
            dialog.Controls.AddRange(new Control[] { intro, claude, codex, status, install, close, logs }); dialog.CancelButton = close;
            dialog.ShowDialog(owner);
        }
    }
}
}
