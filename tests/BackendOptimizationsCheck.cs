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

/// Regression tests for the backend runtime optimizations inspired by agy-fast:
/// 1. ProcessText: surrogate-safe slicing, UTF-8 byte-bounded head-tail folding, ANSI/spinner normalization,
///    and mixed UTF-8 / Windows codepage per-line decoding.
/// 2. WorkspaceSnapshot: compact <=1500B snapshot with Git summary, directory layout, scripts, and non-workspace guard.
/// 3. GitReview & Worktrees: porcelain v2 status (clean, unborn, staged/unstaged, renamed, untracked, non-repo)
///    and stat-first ChangesIn output.
/// 4. Chats.Shown: ANSI/spinner cleaning + head-tail line/char folding.
/// 5. Peers: agy 168s timeout cap, <=8600B UTF-8 answer folding, workspace snapshot injection, and Content-Length MCP framing.
/// 6. Reach.Hook: completes immediately on a full JSON object even when stdin stays open without EOF.
public static class BackendOptimizationsCheck {
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    static void Require(bool yes, string why) { if (!yes) throw new Exception("FAIL: " + why); }
    static object At(object from, string key) { var d = from as Dictionary<string, object>; object v; return d != null && d.TryGetValue(key, out v) ? v : null; }
    static string Text(object from, string key) { return At(from, key) as string ?? ""; }

    static void Git(string dir, string args) {
        var p = Process.Start(new ProcessStartInfo("git", "-C \"" + dir + "\" " + args) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        });
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        Require(p.ExitCode == 0, "git " + args + " failed: " + err);
    }

    public static int Main(string[] args) {
        if (args.Length == 3 && args[0] == Peers.Flag) return Peers.Bridge(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--hook") return Reach.Hook(args[1], args[2]);
        try { return Run(args); }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message + "\n" + ex.StackTrace); return 1; }
    }

    static int Run(string[] args) {
        string root = args[0];
        string selfExe = System.Reflection.Assembly.GetEntryAssembly().Location;

        // 1. ProcessText: surrogate-safe truncation, head-tail folding (chars & UTF-8 bytes), ANSI/spinner cleaning, mixed encoding
        string emoji = "A\U0001F680B\U0001F680C";
        string sliced = ProcessText.SliceSafeChars(emoji, 2); // index 1 is high surrogate
        Require(sliced == "A" && !Char.IsHighSurrogate(sliced[sliced.Length - 1]), "SliceSafeChars never splits surrogate pair: " + sliced);
        string tailSliced = ProcessText.SliceSafeCharsTail(emoji, 2); // starts on low surrogate of second rocket
        Require(tailSliced == "C", "SliceSafeCharsTail never starts on low surrogate: " + tailSliced);

        string longLog = "ERROR: root cause at line 1\n" + String.Join("\n", Enumerable.Range(2, 200).Select(i => "middle noise line " + i)) + "\nSUMMARY: 1 failed, 19 passed";
        string foldedChars = ProcessText.FoldHeadTail(longLog, 300);
        Require(foldedChars.Length <= 300 && foldedChars.Contains("root cause at line 1") && foldedChars.Contains("1 failed, 19 passed") && foldedChars.Contains("已折叠中间"),
            "FoldHeadTail preserves head root cause and tail summary: " + foldedChars);

        string longChinese = "开头诊断：接口超时原因定位\n" + String.Join("\n", Enumerable.Range(1, 500).Select(i => "中间详细日志记录第" + i + "条内容用于测试字节截断边界")) + "\n最终结论：建议将上限收敛到 168 秒";
        string foldedBytes = ProcessText.FoldHeadTailUtf8(longChinese, 8600);
        int byteCount = Encoding.UTF8.GetByteCount(foldedBytes);
        Require(byteCount <= 8600 && foldedBytes.Contains("开头诊断") && foldedBytes.Contains("最终结论"),
            "FoldHeadTailUtf8 respects strict byte cap (" + byteCount + "B) and preserves head+tail");

        string dirtyTerm = "\x1B[31mFAIL\x1B[0m test_a\r\nspinner 10%\rspinner 50%\r\x1B[0Kdone 100%\r\n\x1B[2K\r\n\x1B[32mPASS\x1B[0m test_b\r\n";
        string cleanTerm = ProcessText.NormalizeTerminal(dirtyTerm);
        Require(cleanTerm == "FAIL test_a\ndone 100%\nPASS test_b", "NormalizeTerminal strips ANSI and collapses spinners: [" + cleanTerm + "]");

        // Mixed UTF-8 + GBK/CP936 per-line decoding
        Encoding gbk = Encoding.GetEncoding(936);
        byte[] line1 = Encoding.UTF8.GetBytes("第一行UTF8正常\n");
        byte[] line2 = gbk.GetBytes("第二行GBK报错\n");
        byte[] mixed = new byte[line1.Length + line2.Length];
        Buffer.BlockCopy(line1, 0, mixed, 0, line1.Length);
        Buffer.BlockCopy(line2, 0, mixed, line1.Length, line2.Length);
        string decoded = ProcessText.DecodeProcessOutput(mixed, gbk);
        Require(decoded.Contains("第一行UTF8正常") && decoded.Contains("第二行GBK报错"), "DecodeProcessOutput decodes mixed UTF-8 + GBK cleanly: " + decoded);

        // 2. Chats.Shown: cleans ANSI/spinners and folds middle lines while keeping head & tail
        string manyLines = "\x1B[31mError: first failure\x1B[0m\n"
            + String.Join("\n", Enumerable.Range(2, 80).Select(i => "step detail line " + i))
            + "\nFinal: 81 lines total";
        string shown = Chat.Shown(manyLines);
        Require(!shown.Contains("\x1B[") && shown.Contains("Error: first failure") && shown.Contains("Final: 81 lines total") && shown.Contains("已折叠中间"),
            "Chats.Shown cleans ANSI and head-tail folds long output: " + shown);

        // 3. WorkspaceSnapshot & GitReview porcelain v2 + Worktrees.ChangesIn stat-first
        Require(WorkspaceSnapshot.Build(Path.GetPathRoot(root)) == "", "WorkspaceSnapshot refuses filesystem root");
        Require(WorkspaceSnapshot.Build(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) == "", "WorkspaceSnapshot refuses user home directory");

        string plainDir = Path.Combine(root, "plain-proj");
        Directory.CreateDirectory(plainDir);
        var wts = new Worktrees(Path.Combine(root, "wts"));
        var nonRepoSt = GitReview.Handle(plainDir, new Dictionary<string, object> { { "action", "git_status" } }, wts);
        Require(false.Equals(nonRepoSt["is_repo"]), "non-git folder reports is_repo=false");

        // Unborn repo (git init, no commits yet)
        string unbornDir = Path.Combine(root, "unborn-repo");
        Directory.CreateDirectory(unbornDir);
        Git(unbornDir, "init");
        File.WriteAllText(Path.Combine(unbornDir, "draft.txt"), "hello\n", new UTF8Encoding(false));
        var unbornSt = GitReview.Handle(unbornDir, new Dictionary<string, object> { { "action", "git_status" } }, wts);
        Require(true.Equals(unbornSt["is_repo"]) && Convert.ToString(unbornSt["head"]) == "" && Convert.ToString(unbornSt["branch"]).Length > 0,
            "unborn repo detected with empty head and branch name");
        var unbornFiles = (Dictionary<string, object>[])unbornSt["files"];
        Require(unbornFiles.Length == 1 && Convert.ToString(unbornFiles[0]["path"]) == "draft.txt" && Convert.ToString(unbornFiles[0]["status"]) == "?"
            && false.Equals(unbornFiles[0]["staged"]) && true.Equals(unbornFiles[0]["unstaged"]), "unborn untracked file status ok");

        // Regular repo with staged rename, modified file, untracked file, and package.json
        string repo = Path.Combine(root, "snap-repo");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        Directory.CreateDirectory(Path.Combine(repo, "node_modules", "ignored-pkg"));
        File.WriteAllText(Path.Combine(repo, "src", "main.ts"), "export const v = 1;\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(repo, "old.txt"), "rename me\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(repo, "package.json"), "{\"scripts\":{\"test\":\"vitest\",\"build\":\"tsc\"}}\n", new UTF8Encoding(false));
        Git(repo, "init");
        Git(repo, "config user.email test@example.com");
        Git(repo, "config user.name Test");
        Git(repo, "add -A");
        Git(repo, "commit -m \"initial commit\"");

        File.WriteAllText(Path.Combine(repo, "src", "main.ts"), "export const v = 2;\n", new UTF8Encoding(false));
        Git(repo, "mv old.txt renamed.txt");
        File.WriteAllText(Path.Combine(repo, "with space.txt"), "space file\n", new UTF8Encoding(false));

        var st = GitReview.Handle(repo, new Dictionary<string, object> { { "action", "git_status" } }, wts);
        var map = ((Dictionary<string, object>[])st["files"]).ToDictionary(f => Convert.ToString(f["path"]));
        Require(Convert.ToString(st["head"]).Length >= 7, "porcelain v2 head sha populated");
        Require(map.ContainsKey("src/main.ts") && Convert.ToString(map["src/main.ts"]["status"]) == "M" && true.Equals(map["src/main.ts"]["unstaged"]), "porcelain v2 modified file ok");
        Require(map.ContainsKey("renamed.txt") && Convert.ToString(map["renamed.txt"]["status"]) == "R" && true.Equals(map["renamed.txt"]["staged"]), "porcelain v2 renamed file ok");
        Require(map.ContainsKey("with space.txt") && Convert.ToString(map["with space.txt"]["status"]) == "?", "porcelain v2 path with spaces ok");

        string changes = Worktrees.ChangesIn(repo);
        Require(changes.Contains("文件：") && changes.Contains("改动统计：") && changes.Contains("src/main.ts"), "ChangesIn includes stat summary before diff: " + changes);

        string snap = WorkspaceSnapshot.Build(repo);
        Require(snap.Length > 0 && Encoding.UTF8.GetByteCount(snap) <= WorkspaceSnapshot.DefaultMaxBytes, "snapshot bounded <=1500B");
        Require(snap.Contains("Git：") && snap.Contains("initial commit") && snap.Contains("src/") && !snap.Contains("node_modules") && snap.Contains("npm run test"),
            "snapshot contains git, tree (ignoring node_modules), and scripts: " + snap);

        // 4. Teams first-turn snapshot injection (first turn has snapshot, follow-up does not) + head-tail fold
        var told = new List<string>();
        int mid = 0;
        var teamsHands = new Teams.Hands {
            Begin = one => { told.Add(one.Words); return "m" + (++mid); },
            Say = (tid, words) => { told.Add(words); },
            Stop = tid => { }, Close = tid => { }, Changes = d => changes, Snapshot = d => snap
        };
        var teams = new Teams(Path.Combine(root, "teams.dat"), teamsHands);
        var started = teams.Start(new Dictionary<string, object> {
            { "id", "abcdef0123456789" }, { "mode", "lead" }, { "dir", "repo" }, { "goal", "优化后端" },
            { "tools", new object[] { "codex", "claude" } }, { "rounds", 2 }
        });
        Require(told[0].Contains("## 工作区快照（只读）") && told[0].Contains("npm run test"), "leader first prompt includes workspace snapshot");
        teams.Turned(started.Members["leader"], "done", "ASSIGN claude: 检查改动");
        Require(told[1].Contains("## 工作区快照（只读）"), "member first prompt includes workspace snapshot");
        teams.Turned(teams.All()[0].Members["claude"], "done", "首部发现A\n" + new string('x', 9000) + "\n尾部结论B");
        Require(told[2].Contains("首部发现A") && told[2].Contains("尾部结论B") && told[2].Contains("已折叠中间"),
            "leader receives head-tail folded member result");
        teams.Turned(started.Members["leader"], "done", "ASSIGN claude: 再补测一次");
        Require(!told[3].Contains("## 工作区快照（只读）"), "member follow-up prompt omits redundant snapshot");

        // 5. Peers MCP bridge: Content-Length framing support + agy timeout/overflow guards
        string pipe = Peers.PipeName(root + "-mcp");
        bool collabTimedOutFast = false;
        var collabHooks = new Peers.CollabHooks {
            CanPromote = tid => true,
            Confirm = (tid, kind, title, detail, cancel) => {
                // Verify caller is agy gets a cancel token that fires cleanly
                return false;
            },
            Promote = (tid, item) => new Peers.CollabStarted {
                TeamId = "1122334455667788",
                Prompt = "头部领队指令\n" + String.Join("\n", Enumerable.Range(1, 500).Select(i => "重复上下文说明第" + i + "行填充字节体积")) + "\nASSIGN member-1: 执行"
            }
        };
        // Test Content-Length framing and agy prompt byte ceiling on start_collaboration
        using (var peers = new Peers(pipe, t => t == "agy" ? "dummy" : null, null, Path.Combine(root, "mcp"), selfExe, 20, new Peers.CollabHooks {
            CanPromote = tid => true,
            Confirm = (tid, kind, title, detail, cancel) => true,
            Promote = collabHooks.Promote
        })) {
            var seat = peers.Offer("agy", repo, "term-agy", "repo");
            Require(seat != null, "agy seat offered");
            using (var bridge = Process.Start(new ProcessStartInfo(seat.Command, String.Join(" ", seat.Args.Select(a => "\"" + a + "\""))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            })) {
                try {
                    Func<string, string> sendFramed = jsonBody => {
                        byte[] body = Encoding.UTF8.GetBytes(jsonBody);
                        byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
                        bridge.StandardInput.BaseStream.Write(header, 0, header.Length);
                        bridge.StandardInput.BaseStream.Write(body, 0, body.Length);
                        bridge.StandardInput.BaseStream.Flush();
                        // Read Content-Length response
                        int len = 0;
                        var hdr = new StringBuilder();
                        while (true) {
                            int b = bridge.StandardOutput.BaseStream.ReadByte();
                            Require(b >= 0, "framed response EOF");
                            hdr.Append((char)b);
                            if (hdr.Length >= 4 && hdr.ToString(hdr.Length - 4, 4) == "\r\n\r\n") break;
                        }
                        foreach (string hline in hdr.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) {
                            if (hline.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                len = Int32.Parse(hline.Substring(15).Trim());
                        }
                        Require(len > 0, "framed response Content-Length > 0");
                        byte[] resp = new byte[len];
                        int got = 0;
                        while (got < len) { int n = bridge.StandardOutput.BaseStream.Read(resp, got, len - got); Require(n > 0, "framed body EOF"); got += n; }
                        return Encoding.UTF8.GetString(resp);
                    };

                    string initResp = sendFramed("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\"}}");
                    Require(initResp.Contains("2025-11-25"), "Content-Length framed initialize succeeded: " + initResp);

                    string collabResp = sendFramed("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"start_collaboration\",\"arguments\":{\"goal\":\"优化后端\"}}}");
                    var parsed = Json.Deserialize<Dictionary<string, object>>(collabResp);
                    var content = (At(At(parsed, "result"), "content") as IEnumerable).Cast<object>().First();
                    string promptOut = Text(content, "text");
                    int outBytes = Encoding.UTF8.GetByteCount(promptOut);
                    Require(outBytes <= Peers.AgyMaxAnswerBytes && promptOut.Contains("头部领队指令") && promptOut.Contains("ASSIGN member-1"),
                        "agy start_collaboration output stays <=8600B (" + outBytes + "B) and keeps tail ASSIGN directive");
                } finally {
                    seat.Dispose();
                    try { bridge.StandardInput.Close(); } catch { }
                    if (!bridge.WaitForExit(3000)) ProcessText.KillProcessTree(bridge.Id, 2000);
                }
            }
        }
        if (collabTimedOutFast) { }

        // 6. Reach.Hook returns immediately on complete JSON object even when stdin is NOT closed
        string reachPipe = "RemoteCli.reach.optcheck." + Guid.NewGuid().ToString("N").Substring(0, 8);
        using (var reach = new Reach(reachPipe, () => 0)) {
            string sid = Guid.NewGuid().ToString();
            var sw = Stopwatch.StartNew();
            using (var hook = Process.Start(new ProcessStartInfo(selfExe, "--hook agy " + reachPipe) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
            })) {
                byte[] ev = Encoding.UTF8.GetBytes("{\"hookEventName\":\"SessionStart\",\"conversationId\":\"" + sid + "\",\"cwd\":\"C:\\\\work\"}");
                hook.StandardInput.BaseStream.Write(ev, 0, ev.Length);
                hook.StandardInput.BaseStream.Flush();
                // Deliberately DO NOT close StandardInput! Hook must still finish well before 1500ms stdin timeout.
                Require(hook.WaitForExit(4000), "Reach.Hook exited promptly without stdin EOF");
                Require(hook.ExitCode == 0, "Reach.Hook exit code 0");
                sw.Stop();
                Require(reach.Of("agy", sid) != null, "Reach server received SessionStart event from unclosed stdin");
            }
        }

        Console.WriteLine("backend_optimizations: ProcessText, WorkspaceSnapshot, GitReview v2, ChangesIn, Chats.Shown, Teams, Peers(agy/CL), Reach(no-EOF) ok");
        return 0;
    }
}
