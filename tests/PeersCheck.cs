using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using RemoteCli;

/// Verifies peer AI consultation (`Peers.cs` + `Chats.cs` + `AcpChat.cs`):
/// 1. MCP stdio bridge (`--mcp <pipe> <token>`) over a real Windows named pipe: `server/discover` error, `initialize`,
///    `ping`, `tools/list` (excludes caller, embeds live quota and tight warnings, sets readOnlyHint), `tools/call`.
/// 2. Read-only enforcement: consultant opens in `read` mode without MCP, denies edits/commands automatically, allows
///    read inspection on ACP, reuses session across follow-ups, resets on `fresh: true`, refuses self-call, and cleans
///    up consultant chats + temp MCP config file when the seat is disposed.
/// 3. Chat drivers (`ClaudeChat`, `CodexChat`, `AgyChat`) inject MCP config, auto-allow `ask_ai` without bothering the
///    phone with an approval card, and format `ask_ai` steps and results cleanly.
public static class PeersCheck {
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    static void Require(bool yes, string why) { if (!yes) throw new Exception(why); }
    static object At(object from, string key) { var d = from as Dictionary<string, object>; object v; return d != null && d.TryGetValue(key, out v) ? v : null; }
    static string Text(object from, string key) { return At(from, key) as string ?? ""; }

    static Dictionary<string, object> Rpc(Process p, object request) {
        byte[] bytes = new UTF8Encoding(false).GetBytes(Json.Serialize(request) + "\n");
        p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
        p.StandardInput.BaseStream.Flush();
        string line = p.StandardOutput.ReadLine();
        Require(line != null, "MCP bridge exited before answering");
        return Json.Deserialize<Dictionary<string, object>>(line);
    }

    static string CallText(Dictionary<string, object> resp, out bool isError) {
        var result = At(resp, "result");
        isError = true.Equals(At(result, "isError"));
        var content = At(result, "content") as IEnumerable;
        var parts = content == null ? new List<string>() : content.Cast<object>().Select(one => Text(one, "text")).ToList();
        return String.Join("\n", parts);
    }

    public static int Main(string[] args) {
        // When launched by tests or drivers as the MCP stdio bridge:
        if (args.Length == 3 && args[0] == Peers.Flag) return Peers.Bridge(args[1], args[2]);
        try { return RunTests(args); }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message + "\n" + ex.StackTrace); return 1; }
    }

    static int RunTests(string[] args) {

        string root = args[0], claudeCmd = args[1], codexCmd = args[2], agyCmd = args[3], workDir = args[4];
        string selfExe = System.Reflection.Assembly.GetEntryAssembly().Location;
        var launchers = new Dictionary<string, string> { { "claude", claudeCmd }, { "codex", codexCmd }, { "agy", agyCmd } };
        var quotas = new Quotas(root);
        quotas.Note("codex", new List<Quotas.Window> { new Quotas.Window { Name = "five_hour", Used = 15, Resets = 0 } });
        quotas.Note("agy", new List<Quotas.Window> { new Quotas.Window { Name = "five_hour", Used = 92, Resets = 0 } });

        string pipe = Peers.PipeName(root);
        string mcpDir = Path.Combine(root, "mcp");
        using (var peers = new Peers(pipe, t => launchers.ContainsKey(t) ? launchers[t] : null, quotas, mcpDir, selfExe, 20)) {
            // 1. Offer a seat for Claude Code, verify temp MCP config file and bridge handshake
            var seat = peers.Offer("claude", workDir);
            Require(seat != null, "seat offered");
            Require(File.Exists(seat.ConfigFile), "Claude MCP config file created");
            string cfgText = File.ReadAllText(seat.ConfigFile, Encoding.UTF8);
            Require(cfgText.Contains(Peers.ServerName) && cfgText.Contains(pipe), "Claude MCP config contains server and pipe: " + cfgText);

            var bridgeInfo = new ProcessStartInfo(seat.Command, String.Join(" ", seat.Args.Select(a => "\"" + a + "\""))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            using (var bridge = Process.Start(bridgeInfo)) {
                try {
                    // server/discover -> -32601
                    var disc = Rpc(bridge, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", "probe-1" }, { "method", "server/discover" }, { "params", new Dictionary<string, object>() } });
                    Require(Convert.ToInt32(At(At(disc, "error"), "code")) == -32601, "unknown method returns -32601");

                    // initialize
                    var init = Rpc(bridge, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 1 }, { "method", "initialize" }, { "params", new Dictionary<string, object> { { "protocolVersion", "2025-11-25" } } } });
                    Require(Text(At(init, "result"), "protocolVersion") == "2025-11-25", "protocolVersion echoed");

                    // notifications/initialized (no response expected) + ping
                    byte[] notif = new UTF8Encoding(false).GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n");
                    bridge.StandardInput.BaseStream.Write(notif, 0, notif.Length); bridge.StandardInput.BaseStream.Flush();
                    var pong = Rpc(bridge, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 2 }, { "method", "ping" } });
                    Require(At(pong, "result") != null, "ping answered after notification");

                    // tools/list: excludes caller ("claude"), includes codex & agy with quota hints & readOnlyHint
                    var list = Rpc(bridge, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 3 }, { "method", "tools/list" } });
                    var tools = (At(At(list, "result"), "tools") as IEnumerable).Cast<object>().ToList();
                    Require(tools.Count == 1 && Text(tools[0], "name") == Peers.ToolName, "ask_ai listed");
                    string desc = Text(tools[0], "description");
                    Require(desc.Contains("codex") && desc.Contains("agy") && !desc.Contains("- claude"), "peers list excludes caller: " + desc);
                    Require(desc.Contains("已用 15%") && desc.Contains("已用 92%") && desc.Contains("紧张"), "quota & tight hint included: " + desc);
                    Require(true.Equals(At(At(tools[0], "annotations"), "readOnlyHint")), "readOnlyHint set");

                    // tools/call: self-call refused
                    bool isErr;
                    string selfResp = CallText(Rpc(bridge, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 4 }, { "method", "tools/call" }, { "params", new Dictionary<string, object> { { "name", "ask_ai" }, { "arguments", new Dictionary<string, object> { { "tool", "claude" }, { "question", "hi" } } } } } }), out isErr);
                    Require(isErr && selfResp.Contains("不能请教自己"), "self-call refused: " + selfResp);

                    // tools/call: ask codex (read-only sandbox + edit denied automatically)
                    string r1 = CallText(Rpc(bridge, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 5 }, { "method", "tools/call" }, { "params", new Dictionary<string, object> { { "name", "ask_ai" }, { "arguments", new Dictionary<string, object> { { "tool", "codex" }, { "question", "要求改文件，看看锁设计" } } } } } }), out isErr);
                    Require(!isErr && r1.Contains("sandbox=read-only") && r1.Contains("第 1 轮") && r1.Contains("已拒绝修改"), "codex read-only turn 1: " + r1);

                    // tools/call: follow-up to codex reuses same consultant session (turn 2)
                    string r2 = CallText(Rpc(bridge, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 6 }, { "method", "tools/call" }, { "params", new Dictionary<string, object> { { "name", "ask_ai" }, { "arguments", new Dictionary<string, object> { { "tool", "codex" }, { "question", "再追问一个细节" } } } } } }), out isErr);
                    Require(!isErr && r2.Contains("第 2 轮"), "codex session reused: " + r2);

                    // tools/call: fresh=true starts a fresh codex session (back to turn 1)
                    string r3 = CallText(Rpc(bridge, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 7 }, { "method", "tools/call" }, { "params", new Dictionary<string, object> { { "name", "ask_ai" }, { "arguments", new Dictionary<string, object> { { "tool", "codex" }, { "question", "全新问题" }, { "fresh", true } } } } } }), out isErr);
                    Require(!isErr && r3.Contains("第 1 轮"), "codex fresh session reset: " + r3);

                    // tools/call: ask agy with read permission (allowed) and edit permission (denied)
                    string a1 = CallText(Rpc(bridge, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 8 }, { "method", "tools/call" }, { "params", new Dictionary<string, object> { { "name", "ask_ai" }, { "arguments", new Dictionary<string, object> { { "tool", "agy" }, { "question", "读取授权，检查 UI 结构" } } } } } }), out isErr);
                    Require(!isErr && a1.Contains("允许读取") && a1.Contains("第 1 轮"), "agy read allowed: " + a1);
                    string a2 = CallText(Rpc(bridge, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 9 }, { "method", "tools/call" }, { "params", new Dictionary<string, object> { { "name", "ask_ai" }, { "arguments", new Dictionary<string, object> { { "tool", "agy" }, { "question", "要求改文件，检查边距" } } } } } }), out isErr);
                    Require(!isErr && a2.Contains("已拒绝修改") && a2.Contains("第 2 轮"), "agy edit denied & session reused: " + a2);

                    // Disposing the seat cleans up config file and closes consultant chats; subsequent call fails cleanly
                    string cfgFile = seat.ConfigFile;
                    seat.Dispose();
                    Require(!File.Exists(cfgFile), "Claude MCP config file deleted on seat dispose");
                    string after = CallText(Rpc(bridge, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 10 }, { "method", "tools/call" }, { "params", new Dictionary<string, object> { { "name", "ask_ai" }, { "arguments", new Dictionary<string, object> { { "tool", "codex" }, { "question", "还能问吗" } } } } } }), out isErr);
                    Require(isErr && after.Contains("原对话已结束") || after.Contains("当前对话已结束"), "call after seat dispose refused: " + after);
                } finally {
                    try { bridge.StandardInput.Close(); } catch { }
                    if (!bridge.WaitForExit(3000)) try { bridge.Kill(); } catch { }
                }
            }

            // 2. Driver integration: ClaudeChat, CodexChat, AgyChat auto-allow ask_ai & format step cards
            foreach (string tool in new[] { "claude", "codex", "agy" }) {
                var events = new List<string>();
                var done = new ManualResetEventSlim(false);
                var offered = peers.Offer(tool, workDir);
                Require(offered != null, tool + ": seat offered");
                string cfgPath = offered.ConfigFile;
                using (var chat = Chat.Open(tool, launchers[tool], workDir, "", false, "full", line => { lock (events) events.Add(line); }, quotas, offered)) {
                    chat.Turned += (how, said) => { if (how == "done") done.Set(); };
                    chat.Say("请帮我调用请教验证一下", "p1");
                    Require(done.Wait(15000), tool + ": turn with ask_ai completed: " + String.Join("", events));
                }
                Require(!File.Exists(cfgPath), tool + ": seat disposed when chat disposed");
                string all = String.Join("", events);
                Require(!all.Contains("\"t\":\"ask\""), tool + ": ask_ai ran without prompting the phone: " + all);
                Require(all.Contains("请教 "), tool + ": step formatted with 请教 title: " + all);
                if (tool == "codex") Require(all.Contains("方案可行，建议拆出接口。"), "codex: MCP text content extracted cleanly: " + all);
                if (tool == "agy") Require(all.Contains("mcpCount=1"), "agy: ACP session/new received mcpServers: " + all);
            }
        }

        // 3. In-chat collaboration promotion (`start_collaboration`): listed when CollabHooks allows it, requires
        //    explicit phone confirmation, defaults to lead + auto_roster + free rounds, and works even on single-tool hosts.
        string pipe2 = Peers.PipeName(root + "-collab");
        bool canPromote = true;
        bool confirmAnswer = false;
        string lastConfirmTitle = "", lastConfirmDetail = "";
        Dictionary<string, object> promotedItem = null;
        var hooks = new Peers.CollabHooks {
            CanPromote = tid => canPromote && tid == "term-1",
            Confirm = (tid, kind, title, detail, cancel) => {
                lastConfirmTitle = title; lastConfirmDetail = detail;
                return confirmAnswer;
            },
            Promote = (tid, item) => {
                promotedItem = item;
                canPromote = false;
                return new Peers.CollabStarted { TeamId = "aabbccddeeff00112233445566778899", Prompt = "【对话已转为协作任务】请输出 ASSIGN" };
            }
        };
        // Single-tool host (only claude installed): ask_ai omitted, start_collaboration still offered!
        using (var singlePeers = new Peers(pipe2, t => t == "claude" ? claudeCmd : null, quotas, mcpDir, selfExe, 20, hooks)) {
            var seat2 = singlePeers.Offer("claude", workDir, "term-1", "demo-proj");
            Require(seat2 != null, "single-tool promotable chat still receives MCP server");
            var bInfo = new ProcessStartInfo(seat2.Command, String.Join(" ", seat2.Args.Select(a => "\"" + a + "\""))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            using (var bridge2 = Process.Start(bInfo)) {
                try {
                    Rpc(bridge2, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 1 }, { "method", "initialize" }, { "params", new Dictionary<string, object> { { "protocolVersion", "2025-11-25" } } } });
                    var list2 = Rpc(bridge2, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 2 }, { "method", "tools/list" } });
                    var tools2 = (At(At(list2, "result"), "tools") as IEnumerable).Cast<object>().ToList();
                    Require(tools2.Count == 1 && Text(tools2[0], "name") == Peers.CollabToolName, "single-tool host lists only start_collaboration");

                    // Denied confirmation -> Promote NOT called
                    bool isErr2;
                    confirmAnswer = false;
                    string denied = CallText(Rpc(bridge2, new Dictionary<string, object> {
                        { "jsonrpc", "2.0" }, { "id", 3 }, { "method", "tools/call" },
                        { "params", new Dictionary<string, object> {
                            { "name", Peers.CollabToolName },
                            { "arguments", new Dictionary<string, object> { { "goal", "重构导出模块" }, { "context", "已确认保持 CSV 字段顺序不变" } } } } } }), out isErr2);
                    Require(isErr2 && denied.Contains("拒绝") && promotedItem == null, "denied confirmation blocks promotion: " + denied);
                    Require(lastConfirmTitle.Contains("转为协作") && lastConfirmDetail.Contains("分工协作（自动组队 · 自由轮数）") && lastConfirmDetail.Contains("重构导出模块"),
                        "confirmation card shows clear Chinese defaults: " + lastConfirmDetail);

                    // Allowed confirmation -> Promote called with defaults (lead, auto_roster=true, rounds=0)
                    confirmAnswer = true;
                    string allowed = CallText(Rpc(bridge2, new Dictionary<string, object> {
                        { "jsonrpc", "2.0" }, { "id", 4 }, { "method", "tools/call" },
                        { "params", new Dictionary<string, object> {
                            { "name", Peers.CollabToolName },
                            { "arguments", new Dictionary<string, object> { { "goal", "重构导出模块" }, { "context", "已确认保持 CSV 字段顺序不变" } } } } } }), out isErr2);
                    Require(!isErr2 && allowed.Contains("对话已转为协作任务") && promotedItem != null, "allowed confirmation promotes chat: " + allowed);
                    Require(Text(promotedItem, "mode") == "lead" && true.Equals(At(promotedItem, "auto_roster"))
                        && Convert.ToInt32(At(promotedItem, "rounds")) == 0 && Text(promotedItem, "context").Contains("CSV"),
                        "default promotion uses lead + auto_roster + free rounds + context");

                    // After promotion, CanPromote returns false so start_collaboration disappears from tools/list
                    var listAfter = Rpc(bridge2, new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 5 }, { "method", "tools/list" } });
                    var toolsAfter = (At(At(listAfter, "result"), "tools") as IEnumerable).Cast<object>().ToList();
                    Require(toolsAfter.Count == 0, "start_collaboration omitted once chat is already in a team");
                } finally {
                    seat2.Dispose();
                    try { bridge2.StandardInput.Close(); } catch { }
                    if (!bridge2.WaitForExit(3000)) try { bridge2.Kill(); } catch { }
                }
            }
        }

        // 4. Chat.Confirm end-to-end on a live Chat: emits ask card without `always`, unblocks on Answer("allow"/"deny"),
        //    and withdraws cleanly on cancellation.
        var confirmEvents = new List<string>();
        using (var chat = Chat.Open("claude", claudeCmd, workDir, "", false, "full", line => { lock (confirmEvents) confirmEvents.Add(line); }, quotas, null)) {
            string askId = null;
            using (var cts = new CancellationTokenSource()) {
                bool? result = null;
                var worker = new Thread(() => { result = chat.Confirm("collab", "将当前对话转为协作任务？", "目标：测试确认", cts.Token, 10); });
                worker.Start();
                for (int i = 0; i < 40 && askId == null; i++) {
                    Thread.Sleep(50);
                    lock (confirmEvents) {
                        var askLine = confirmEvents.FirstOrDefault(l => l.Contains("\"t\":\"ask\""));
                        if (askLine != null) askId = Text(Json.Deserialize<Dictionary<string, object>>(askLine), "id");
                    }
                }
                Require(!String.IsNullOrEmpty(askId) && chat.Status == "confirm", "Confirm emits ask event and sets confirm state");
                string askJson;
                lock (confirmEvents) askJson = confirmEvents.First(l => l.Contains("\"t\":\"ask\""));
                Require(!askJson.Contains("\"always\""), "Confirm never offers permanent always option: " + askJson);
                chat.Answer(askId, "allow");
                Require(worker.Join(3000) && result == true, "Confirm returns true when answered allow");
            }
            using (var cts2 = new CancellationTokenSource()) {
                bool? result2 = null;
                var worker2 = new Thread(() => { result2 = chat.Confirm("collab", "取消测试", "目标：取消", cts2.Token, 10); });
                worker2.Start();
                Thread.Sleep(150);
                cts2.Cancel();
                Require(worker2.Join(3000) && result2 == false, "Confirm returns false and withdraws ask card on cancel");
            }
        }

        Console.WriteLine("peers: MCP bridge, read-only guards, session reuse, quotas, start_collaboration confirm, cleanup and all 3 chat drivers ok");
        return 0;
    }
}
