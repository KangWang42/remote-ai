using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using RemoteCli;

public static class AntigravityDesktopCheck {
    static void Require(bool ok, string message) {
        if (!ok) throw new Exception("FAIL: " + message);
    }

    static void Refused(Action action, string label) {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        catch (UnauthorizedAccessException) { return; }
        throw new Exception("FAIL: expected refusal for " + label);
    }

    static int FreePort() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    sealed class MockHub : IDisposable {
        readonly HttpListener listener = new HttpListener();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        readonly Thread worker;
        volatile bool stopped;

        public readonly int Port;
        public readonly string CsrfToken;
        public string ActiveFolder = "";
        public string ConvPerm = "";
        public string ConvAsk = "";
        public string ConvFilePerm = "";
        public string ConvMcp = "";
        public string ConvRunCmd = "";
        public string ConvCli = "";
        public string ConvSub = "";
        public string NextCreatedId = "";
        public bool Online = true;

        public readonly List<string> Calls = new List<string>();
        public readonly List<Dictionary<string, object>> StartRequests = new List<Dictionary<string, object>>();
        public readonly List<Dictionary<string, object>> SendRequests = new List<Dictionary<string, object>>();
        public readonly List<Dictionary<string, object>> CancelRequests = new List<Dictionary<string, object>>();
        public readonly List<Dictionary<string, object>> InteractionRequests = new List<Dictionary<string, object>>();
        public bool PermAnswered;

        public MockHub(string csrfToken) {
            Port = FreePort();
            CsrfToken = csrfToken;
            listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            listener.Start();
            worker = new Thread(Serve) { IsBackground = true };
            worker.Start();
        }

        void Serve() {
            while (!stopped) {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }
                try { Handle(ctx); }
                catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
            }
        }

        void WriteJson(HttpListenerResponse resp, int status, object payload) {
            byte[] bytes = new UTF8Encoding(false).GetBytes(json.Serialize(payload));
            resp.StatusCode = status;
            resp.ContentType = "application/json";
            resp.ContentLength64 = bytes.Length;
            resp.OutputStream.Write(bytes, 0, bytes.Length);
            resp.Close();
        }

        void Handle(HttpListenerContext ctx) {
            if (!Online) {
                WriteJson(ctx.Response, 503, new Dictionary<string, object> { { "code", "unavailable" }, { "message", "offline" } });
                return;
            }
            string gotCsrf = ctx.Request.Headers["x-codeium-csrf-token"] ?? "";
            string proto = ctx.Request.Headers["Connect-Protocol-Version"] ?? "";
            if (gotCsrf != CsrfToken || proto != "1") {
                WriteJson(ctx.Response, 401, new Dictionary<string, object> {
                    { "code", "unauthenticated" },
                    { "message", "unauthenticated: missing CSRF token" }
                });
                return;
            }
            string path = ctx.Request.Url.AbsolutePath;
            const string prefix = "/exa.language_server_pb.LanguageServerService/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) {
                WriteJson(ctx.Response, 404, new Dictionary<string, object> { { "code", "unimplemented" } });
                return;
            }
            string method = path.Substring(prefix.Length);
            string bodyText;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) bodyText = reader.ReadToEnd();
            var req = String.IsNullOrWhiteSpace(bodyText)
                ? new Dictionary<string, object>()
                : json.Deserialize<Dictionary<string, object>>(bodyText) ?? new Dictionary<string, object>();

            lock (Calls) Calls.Add(method);

            if (method == "GetStatus") {
                WriteJson(ctx.Response, 200, new Dictionary<string, object>());
                return;
            }
            if (method == "GetCascadeModelConfigData") {
                WriteJson(ctx.Response, 200, new Dictionary<string, object> {
                    { "defaultOverrideModelConfig", new Dictionary<string, object> {
                        { "modelOrAlias", new Dictionary<string, object> { { "model", "MODEL_PLACEHOLDER_DEFAULT" } } }
                    } }
                });
                return;
            }
            if (method == "GetAllCascadeTrajectories") {
                string wsUri = AntigravityDesktopClient.ToFileUri(ActiveFolder);
                var summaries = new Dictionary<string, object> {
                    { ConvPerm, new Dictionary<string, object> {
                        { "summary", "桌面权限测试对话" },
                        { "stepCount", 6 },
                        { "lastModifiedTime", "2026-11-21T16:40:00Z" },
                        { "createdTime", "2026-11-21T16:35:00Z" },
                        { "trajectoryId", "11111111-1111-1111-1111-111111111111" },
                        { "status", PermAnswered ? "CASCADE_RUN_STATUS_IDLE" : "CASCADE_RUN_STATUS_RUNNING" },
                        { "workspaces", new object[] { new Dictionary<string, object> { { "workspaceFolderAbsoluteUri", wsUri } } } },
                        { "annotations", new Dictionary<string, object> { { "title", "桌面权限测试" } } }
                    } },
                    { ConvAsk, new Dictionary<string, object> {
                        { "summary", "多选提问测试" },
                        { "stepCount", 2 },
                        { "lastModifiedTime", "2026-11-21T16:39:00Z" },
                        { "createdTime", "2026-11-21T16:38:00Z" },
                        { "trajectoryId", "22222222-2222-2222-2222-222222222222" },
                        { "status", "CASCADE_RUN_STATUS_WAITING_FOR_USER" },
                        { "workspaces", new object[] { new Dictionary<string, object> { { "workspaceFolderAbsoluteUri", wsUri } } } }
                    } },
                    { ConvCli, new Dictionary<string, object> {
                        { "summary", "应被过滤的 CLI 对话" },
                        { "source", "CORTEX_TRAJECTORY_SOURCE_CLI" },
                        { "status", "CASCADE_RUN_STATUS_IDLE" },
                        { "lastModifiedTime", "2026-11-21T16:41:00Z" },
                        { "workspaces", new object[] { new Dictionary<string, object> { { "workspaceFolderAbsoluteUri", wsUri } } } }
                    } },
                    { ConvSub, new Dictionary<string, object> {
                        { "summary", "应被过滤的子对话" },
                        { "parentConversationId", ConvPerm },
                        { "status", "CASCADE_RUN_STATUS_IDLE" },
                        { "lastModifiedTime", "2026-11-21T16:41:00Z" }
                    } }
                };
                WriteJson(ctx.Response, 200, new Dictionary<string, object> { { "trajectorySummaries", summaries } });
                return;
            }
            if (method == "GetCascadeTrajectory" || method == "GetCascadeTrajectorySteps") {
                string cid = req.ContainsKey("cascadeId") ? Convert.ToString(req["cascadeId"]) : "";
                var steps = StepsFor(cid);
                if (method == "GetCascadeTrajectorySteps") {
                    int offset = req.ContainsKey("stepOffset") ? Convert.ToInt32(req["stepOffset"]) : 0;
                    var slice = offset >= steps.Count ? new List<object>() : steps.Skip(offset).ToList();
                    WriteJson(ctx.Response, 200, new Dictionary<string, object> { { "steps", slice } });
                    return;
                }
                string wsUri = AntigravityDesktopClient.ToFileUri(ActiveFolder);
                string tid = cid == ConvPerm ? "11111111-1111-1111-1111-111111111111"
                    : cid == ConvAsk ? "22222222-2222-2222-2222-222222222222"
                    : "33333333-3333-3333-3333-333333333333";
                WriteJson(ctx.Response, 200, new Dictionary<string, object> {
                    { "status", (cid == ConvPerm && PermAnswered) ? "CASCADE_RUN_STATUS_IDLE" : "CASCADE_RUN_STATUS_RUNNING" },
                    { "numTotalSteps", steps.Count },
                    { "trajectory", new Dictionary<string, object> {
                        { "trajectoryId", tid },
                        { "cascadeId", cid },
                        { "steps", steps },
                        { "metadata", new Dictionary<string, object> {
                            { "workspaces", new object[] { new Dictionary<string, object> { { "workspaceFolderAbsoluteUri", wsUri } } } }
                        } }
                    } }
                });
                return;
            }
            if (method == "StartCascade") {
                lock (StartRequests) StartRequests.Add(req);
                WriteJson(ctx.Response, 200, new Dictionary<string, object> {
                    { "cascadeId", NextCreatedId },
                    { "executionStatus", "CASCADE_RUN_STATUS_IDLE" }
                });
                return;
            }
            if (method == "SendUserCascadeMessage") {
                lock (SendRequests) SendRequests.Add(req);
                WriteJson(ctx.Response, 200, new Dictionary<string, object> {
                    { "cascadeId", req.ContainsKey("cascadeId") ? req["cascadeId"] : "" },
                    { "executionStatus", "CASCADE_RUN_STATUS_RUNNING" }
                });
                return;
            }
            if (method == "CancelCascadeInvocation") {
                lock (CancelRequests) CancelRequests.Add(req);
                WriteJson(ctx.Response, 200, new Dictionary<string, object>());
                return;
            }
            if (method == "HandleCascadeUserInteraction") {
                lock (InteractionRequests) InteractionRequests.Add(req);
                if (req.ContainsKey("cascadeId") && Convert.ToString(req["cascadeId"]) == ConvPerm) PermAnswered = true;
                WriteJson(ctx.Response, 200, new Dictionary<string, object>());
                return;
            }
            WriteJson(ctx.Response, 404, new Dictionary<string, object> { { "code", "unimplemented" } });
        }

        List<object> StepsFor(string cid) {
            if (cid == ConvPerm) {
                var list = new List<object> {
                    new Dictionary<string, object> {
                        { "type", "CORTEX_STEP_TYPE_USER_INPUT" },
                        { "status", "CORTEX_STEP_STATUS_DONE" },
                        { "metadata", new Dictionary<string, object> { { "createdAt", "2026-11-21T16:35:00Z" } } },
                        { "userInput", new Dictionary<string, object> {
                            { "items", new object[] { new Dictionary<string, object> { { "text", "请帮我检查仓库状态" } } } },
                            { "userResponse", "<USER_REQUEST>\n请帮我检查仓库状态\n</USER_REQUEST>" },
                            { "userConfig", new Dictionary<string, object> {
                                { "plannerConfig", new Dictionary<string, object> { { "planModel", "MODEL_PLACEHOLDER_CONV" } } }
                            } }
                        } }
                    },
                    new Dictionary<string, object> {
                        { "type", "CORTEX_STEP_TYPE_PLANNER_RESPONSE" },
                        { "status", "CORTEX_STEP_STATUS_DONE" },
                        { "metadata", new Dictionary<string, object> { { "createdAt", "2026-11-21T16:35:02Z" } } },
                        { "plannerResponse", new Dictionary<string, object> {
                            { "thinking", "先读取 README 并执行 git status" },
                            { "toolCalls", new object[] { new Dictionary<string, object> { { "name", "view_file" } } } }
                        } }
                    },
                    new Dictionary<string, object> {
                        { "type", "CORTEX_STEP_TYPE_VIEW_FILE" },
                        { "status", "CORTEX_STEP_STATUS_DONE" },
                        { "metadata", new Dictionary<string, object> { { "createdAt", "2026-11-21T16:35:03Z" } } },
                        { "viewFile", new Dictionary<string, object> {
                            { "absolutePathUri", "file:///E:/demo/README.md" },
                            { "content", "# Demo Project\nLine 2" }
                        } }
                    },
                    new Dictionary<string, object> {
                        { "type", "CORTEX_STEP_TYPE_RUN_COMMAND" },
                        { "status", "CORTEX_STEP_STATUS_DONE" },
                        { "metadata", new Dictionary<string, object> { { "createdAt", "2026-11-21T16:35:04Z" } } },
                        { "runCommand", new Dictionary<string, object> {
                            { "commandLine", "git status --short" },
                            { "exitCode", 0 },
                            { "combinedOutput", "M README.md\n" }
                        } }
                    },
                    new Dictionary<string, object> {
                        { "type", "CORTEX_STEP_TYPE_PLANNER_RESPONSE" },
                        { "status", "CORTEX_STEP_STATUS_DONE" },
                        { "metadata", new Dictionary<string, object> { { "createdAt", "2026-11-21T16:35:06Z" } } },
                        { "plannerResponse", new Dictionary<string, object> {
                            { "modifiedResponse", "已完成检查，准备推送。" }
                        } }
                    }
                };
                if (!PermAnswered) {
                    list.Add(new Dictionary<string, object> {
                        { "type", "CORTEX_STEP_TYPE_RUN_COMMAND" },
                        { "status", "CORTEX_STEP_STATUS_WAITING" },
                        { "metadata", new Dictionary<string, object> { { "createdAt", "2026-11-21T16:36:00Z" } } },
                        { "runCommand", new Dictionary<string, object> { { "proposedCommandLine", "git push origin main" } } },
                        { "requestedInteraction", new Dictionary<string, object> {
                            { "permission", new Dictionary<string, object> {
                                { "resource", new Dictionary<string, object> { { "action", "command" }, { "target", "git push origin main" } } },
                                { "persistSuggestionType", "PERSIST_SUGGESTION_TYPE_SUGGESTED" },
                                { "suggestedPersistPattern", "git push" }
                            } }
                        } }
                    });
                }
                return list;
            }
            if (cid == ConvAsk) {
                return new List<object> {
                    new Dictionary<string, object> {
                        { "type", "CORTEX_STEP_TYPE_USER_INPUT" },
                        { "status", "CORTEX_STEP_STATUS_DONE" },
                        { "userInput", new Dictionary<string, object> { { "userResponse", "帮我选一个方案" } } }
                    },
                    new Dictionary<string, object> {
                        { "type", "CORTEX_STEP_TYPE_ASK_QUESTION" },
                        { "status", "CORTEX_STEP_STATUS_WAITING" },
                        { "askQuestion", new Dictionary<string, object> {
                            { "questions", new object[] { new Dictionary<string, object> {
                                { "id", "q-1" },
                                { "question", "请选择部署方式" },
                                { "options", new object[] {
                                    new Dictionary<string, object> { { "id", "opt-a" }, { "text", "本地部署" }, { "description", "本机运行" } },
                                    new Dictionary<string, object> { { "id", "opt-b" }, { "text", "公网中转" }, { "description", "通过中转访问" } }
                                } }
                            } } }
                        } },
                        { "requestedInteraction", new Dictionary<string, object> {
                            { "askQuestion", new Dictionary<string, object> {
                                { "questions", new object[] { new Dictionary<string, object> {
                                    { "id", "q-1" },
                                    { "question", "请选择部署方式" },
                                    { "options", new object[] {
                                        new Dictionary<string, object> { { "id", "opt-a" }, { "text", "本地部署" }, { "description", "本机运行" } },
                                        new Dictionary<string, object> { { "id", "opt-b" }, { "text", "公网中转" }, { "description", "通过中转访问" } }
                                    } }
                                } } }
                            } }
                        } }
                    }
                };
            }
            if (cid == ConvFilePerm) {
                return new List<object> {
                    new Dictionary<string, object> {
                        { "type", "CORTEX_STEP_TYPE_VIEW_FILE" },
                        { "status", "CORTEX_STEP_STATUS_WAITING" },
                        { "requestedInteraction", new Dictionary<string, object> {
                            { "filePermission", new Dictionary<string, object> {
                                { "absolutePathUri", "file:///E:/secret/notes.txt" }
                            } }
                        } }
                    }
                };
            }
            if (cid == ConvMcp) {
                return new List<object> {
                    new Dictionary<string, object> {
                        { "type", "CORTEX_STEP_TYPE_MCP_TOOL" },
                        { "status", "CORTEX_STEP_STATUS_WAITING" },
                        { "mcpTool", new Dictionary<string, object> {
                            { "serverName", "remote_ai" },
                            { "toolCall", new Dictionary<string, object> { { "name", "ask_ai" } } }
                        } },
                        { "requestedInteraction", new Dictionary<string, object> {
                            { "mcp", new Dictionary<string, object> { { "serverName", "remote_ai" }, { "toolName", "ask_ai" } } }
                        } }
                    }
                };
            }
            if (cid == ConvRunCmd) {
                return new List<object> {
                    new Dictionary<string, object> {
                        { "type", "CORTEX_STEP_TYPE_RUN_COMMAND" },
                        { "status", "CORTEX_STEP_STATUS_WAITING" },
                        { "runCommand", new Dictionary<string, object> { { "proposedCommandLine", "npm test" } } },
                        { "requestedInteraction", new Dictionary<string, object> {
                            { "runCommand", new Dictionary<string, object> { { "proposedCommandLine", "npm test" } } }
                        } }
                    }
                };
            }
            return new List<object>();
        }

        public void Dispose() {
            stopped = true;
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
        }
    }

    static void WriteDaemon(string home, string name, int pid, int httpPort, string csrf, DateTime mtimeUtc) {
        string dir = Path.Combine(home, "daemon");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        string json = "{\"pid\":" + pid + ",\"httpPort\":" + httpPort + ",\"httpsPort\":0,\"lsVersion\":\"2.34.0\",\"csrfToken\":\"" + csrf + "\"}";
        File.WriteAllText(path, json, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, mtimeUtc);
    }

    public static void Main(string[] args) {
        string root = args[0];
        string desktopHome = Path.Combine(root, "desktop-home");
        string cliHome = Path.Combine(root, "cli-home");
        string legacyCliHome = Path.Combine(root, "legacy-cli-home");
        string offlineHome = Path.Combine(root, "offline-home");
        string projectDir = Path.Combine(root, "项目 demo");
        Directory.CreateDirectory(projectDir);

        string csrfSecret = "test-csrf-token-never-print-12345678";
        string convPerm = "aaaaaaaa-1111-4111-8111-111111111111";
        string convAsk = "bbbbbbbb-2222-4222-8222-222222222222";
        string convFilePerm = "cccccccc-3333-4333-8333-333333333333";
        string convMcp = "dddddddd-4444-4444-8444-444444444444";
        string convRunCmd = "eeeeeeee-5555-4555-8555-555555555555";
        string convOfflineDb = "11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        string convCliOnly = "22222222-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        string convSub = "33333333-cccc-4ccc-8ccc-cccccccccccc";
        string convCreated = "44444444-dddd-4ddd-8ddd-dddddddddddd";
        string convCreatedEmpty = "55555555-eeee-4eee-8eee-eeeeeeeeeeee";

        // ---- 1. CLI vs Desktop SQLite deduplication (including legacy schema compatibility)
        var cliSums = Antigravity.Summaries(cliHome);
        Require(cliSums.Count == 1 && cliSums[0].Id == convCliOnly, "CLI Summaries must exclude app_data_dir='antigravity', got count=" + cliSums.Count);
        var legacySums = Antigravity.Summaries(legacyCliHome);
        Require(legacySums.Count == 1 && legacySums[0].Id == convCliOnly, "CLI Summaries must remain compatible with DBs without app_data_dir");

        using (var hub = new MockHub(csrfSecret)) {
            hub.ActiveFolder = projectDir;
            hub.ConvPerm = convPerm;
            hub.ConvAsk = convAsk;
            hub.ConvFilePerm = convFilePerm;
            hub.ConvMcp = convMcp;
            hub.ConvRunCmd = convRunCmd;
            hub.ConvCli = convCliOnly;
            hub.ConvSub = convSub;
            hub.NextCreatedId = convCreated;

            // ---- 2. Discovery: stale dead PID, wrong-process PID, bad-CSRF newest file, and valid running file
            int livePid = 42424;
            int badCsrfPid = 42425;
            WriteDaemon(desktopHome, "hub_99991.json", 99991, hub.Port, csrfSecret, DateTime.UtcNow.AddMinutes(5));          // dead PID (newest, must skip)
            WriteDaemon(desktopHome, "hub_99992.json", 99992, hub.Port, csrfSecret, DateTime.UtcNow.AddMinutes(4));          // wrong proc (must skip)
            WriteDaemon(desktopHome, "hub_badcsrf.json", badCsrfPid, hub.Port, "wrong-csrf-token", DateTime.UtcNow.AddMinutes(3)); // 401 rejected (must skip)
            WriteDaemon(desktopHome, "hub_" + hub.Port + ".json", livePid, hub.Port, csrfSecret, DateTime.UtcNow.AddMinutes(1));   // valid!

            var client = new AntigravityDesktopClient(desktopHome, pid => pid == livePid || pid == badCsrfPid);
            Require(client.Available, "client should discover valid hub daemon");
            var ep = client.Discover(true);
            Require(ep != null && ep.Pid == livePid && ep.HttpPort == hub.Port, "discovered wrong endpoint");
            Require(!ep.ToString().Contains(csrfSecret), "Endpoint.ToString must never leak CSRF token");

            // Negative CSRF check: client pointing only to bad-CSRF daemon is unavailable (401 rejected)
            string badHome = Path.Combine(root, "bad-csrf-home");
            WriteDaemon(badHome, "hub_" + hub.Port + ".json", badCsrfPid, hub.Port, "wrong-csrf-token", DateTime.UtcNow);
            var badClient = new AntigravityDesktopClient(badHome, pid => pid == badCsrfPid);
            Require(!badClient.Available, "client with wrong CSRF must be rejected as unavailable");

            // ---- 3. Summaries: merges RPC desktop + offline SQLite desktop, excludes CLI & sub-conversation
            var deskSums = client.Summaries();
            Require(deskSums.Any(s => s.Id == convPerm && s.Title == "桌面权限测试" && s.Status == "busy"), "RPC desktop summary missing or wrong status");
            Require(deskSums.Any(s => s.Id == convAsk && s.Status == "confirm"), "RPC waiting summary missing");
            Require(deskSums.Any(s => s.Id == convOfflineDb && s.Title == "离线桌面历史"), "Offline SQLite desktop summary missing");
            Require(!deskSums.Any(s => s.Id == convCliOnly), "CLI conversation must be excluded from desktop summaries");
            Require(!deskSums.Any(s => s.Id == convSub), "Sub-conversation must be excluded from desktop summaries");

            // ---- 4. ReadConversation: steps/thinking/tool detail+result/ask + incremental since stamp
            var read = client.ReadConversation(convPerm);
            Require(false.Equals(read["same"]), "first read should not be same");
            Require((string)read["status"] == "confirm", "waiting step should yield status=confirm, got " + read["status"]);
            string stamp = Convert.ToString(read["stamp"]);
            Require(System.Text.RegularExpressions.Regex.IsMatch(stamp, @"\A[0-9.]{1,48}\z"), "stamp must match relay regex: " + stamp);
            var msgs = (List<Dictionary<string, object>>)read["messages"];
            Require(msgs.Any(m => (string)m["role"] == "user" && (string)m["text"] == "请帮我检查仓库状态"), "user message missing");
            Require(msgs.Any(m => (string)m["role"] == "thought" && ((string)m["text"]).Contains("README")), "thought missing");
            Require(msgs.Any(m => (string)m["role"] == "tool" && ((string)m["text"]).Contains("git status") && ((string)m["result"]).Contains("M README.md")), "tool step with result missing");
            Require(msgs.Any(m => (string)m["role"] == "assistant" && ((string)m["text"]).Contains("已完成检查")), "assistant reply missing");
            var askObj = read["ask"] as Dictionary<string, object>;
            Require(askObj != null && Convert.ToString(askObj["kind"]) == "command" && Convert.ToString(askObj["text"]).Contains("git push"), "ask payload missing");

            // Incremental since check
            var readSame = client.ReadConversation(convPerm, stamp);
            Require(true.Equals(readSame["same"]) && !readSame.ContainsKey("messages"), "unchanged stamp should return same=true");

            // ---- 5. Offline transcript fallback
            string logsDir = Path.Combine(offlineHome, "brain", convOfflineDb, ".system_generated", "logs");
            Directory.CreateDirectory(logsDir);
            File.WriteAllText(Path.Combine(logsDir, "transcript.jsonl"),
                "{\"step_index\":0,\"type\":\"USER_INPUT\",\"created_at\":\"2026-11-20T08:00:00Z\",\"content\":\"<USER_REQUEST>\\n离线测试问题\\n</USER_REQUEST>\"}\n"
                + "{\"step_index\":1,\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"2026-11-20T08:00:01Z\",\"thinking\":\"离线思考\",\"content\":\"离线回答\"}\n",
                new UTF8Encoding(false));
            var offlineClient = new AntigravityDesktopClient(offlineHome, pid => false);
            Require(!offlineClient.Available, "offline client should not be available");
            var offRead = offlineClient.ReadConversation(convOfflineDb);
            var offMsgs = (List<Dictionary<string, object>>)offRead["messages"];
            Require(offMsgs.Count == 3 && (string)offMsgs[0]["text"] == "离线测试问题" && (string)offMsgs[2]["text"] == "离线回答", "offline transcript fallback failed");
            var offSame = offlineClient.ReadConversation(convOfflineDb, Convert.ToString(offRead["stamp"]));
            Require(true.Equals(offSame["same"]), "offline since check failed");

            // ---- 6. Inspect & Answer for permission, askQuestion, filePermission, mcp, runCommand
            var permState = client.Inspect(convPerm);
            Require(permState.Status == "confirm" && permState.Waiting && permState.PendingStepIndex == 5, "Inspect permission state wrong");
            Require(permState.AskOptions.Length == 3, "permission should expose once/always/deny");
            Refused(() => client.Answer(convPerm, "0000000000000000", true, "once"), "mismatched askId");
            var ansPerm = client.Answer(convPerm, permState.AskId, true, "always");
            Require((string)ansPerm["status"] == "accepted" && (string)ansPerm["transport"] == "agy-desktop", "Answer permission receipt wrong");
            var lastInter = hub.InteractionRequests[hub.InteractionRequests.Count - 1];
            var interObj = (Dictionary<string, object>)lastInter["interaction"];
            var permBody = (Dictionary<string, object>)interObj["permission"];
            Require(true.Equals(permBody["allow"]) && (string)permBody["scope"] == "PERMISSION_SCOPE_WORKSPACE" && permBody.ContainsKey("persistGrants"), "permission RPC body wrong");

            // askQuestion choice:1 & cancel
            var askState = client.Inspect(convAsk);
            Require(askState.Status == "confirm" && askState.AskKind == "question" && askState.AskOptions.Length == 2, "Inspect askQuestion state wrong");
            client.Answer(convAsk, askState.AskId, true, "once", "choice:1");
            var qInter = (Dictionary<string, object>)((Dictionary<string, object>)hub.InteractionRequests[hub.InteractionRequests.Count - 1]["interaction"])["askQuestion"];
            var qResps = ((IEnumerable)qInter["responses"]).Cast<Dictionary<string, object>>().ToArray();
            var sel = ((IEnumerable)qResps[0]["selectedOptionIds"]).Cast<object>().Select(Convert.ToString).ToArray();
            Require(false.Equals(qInter["cancelled"]) && sel.Length == 1 && sel[0] == "opt-b", "askQuestion choice:1 payload wrong");

            client.Answer(convAsk, askState.AskId, false);
            var qCancel = (Dictionary<string, object>)((Dictionary<string, object>)hub.InteractionRequests[hub.InteractionRequests.Count - 1]["interaction"])["askQuestion"];
            Require(true.Equals(qCancel["cancelled"]), "askQuestion cancel payload wrong");

            // filePermission, mcp, runCommand
            var fpState = client.Inspect(convFilePerm);
            client.Answer(convFilePerm, fpState.AskId, true, "always");
            var fpBody = (Dictionary<string, object>)((Dictionary<string, object>)hub.InteractionRequests[hub.InteractionRequests.Count - 1]["interaction"])["filePermission"];
            Require(true.Equals(fpBody["allow"]) && (string)fpBody["scope"] == "PERMISSION_SCOPE_WORKSPACE" && ((string)fpBody["absolutePathUri"]).Contains("notes.txt"), "filePermission payload wrong");

            var mcpState = client.Inspect(convMcp);
            client.Answer(convMcp, mcpState.AskId, false);
            var mcpBody = (Dictionary<string, object>)((Dictionary<string, object>)hub.InteractionRequests[hub.InteractionRequests.Count - 1]["interaction"])["mcp"];
            Require(false.Equals(mcpBody["confirm"]), "mcp payload wrong");

            var rcState = client.Inspect(convRunCmd);
            client.Answer(convRunCmd, rcState.AskId, true);
            var rcBody = (Dictionary<string, object>)((Dictionary<string, object>)hub.InteractionRequests[hub.InteractionRequests.Count - 1]["interaction"])["runCommand"];
            Require(true.Equals(rcBody["confirm"]) && (string)rcBody["proposedCommandLine"] == "npm test", "runCommand payload wrong");

            // ---- 7. Send & Stop
            string sendOpId = new string('f', 32);
            var sent = client.Send(convPerm, sendOpId, "继续执行下一步");
            Require((string)sent["status"] == "accepted" && true.Equals(sent["sent"]) && (string)sent["transport"] == "agy-desktop", "Send receipt wrong");
            var sendReq = hub.SendRequests[hub.SendRequests.Count - 1];
            Require((string)sendReq["cascadeId"] == convPerm && (string)sendReq["clientMessageId"] == sendOpId, "Send RPC payload wrong");
            var sendPlanner = (Dictionary<string, object>)((Dictionary<string, object>)sendReq["cascadeConfig"])["plannerConfig"];
            var sendModel = (Dictionary<string, object>)sendPlanner["requestedModel"];
            Require((string)sendModel["model"] == "MODEL_PLACEHOLDER_CONV", "Send must inherit conversation planModel");
            Refused(() => client.Send(convPerm, sendOpId, "   "), "blank text");

            var stopped = client.Stop(convPerm);
            Require((string)stopped["status"] == "confirmed" && (string)stopped["transport"] == "agy-desktop", "Stop receipt wrong");
            Require(hub.CancelRequests.Count == 1 && (string)hub.CancelRequests[0]["cascadeId"] == convPerm, "Cancel RPC payload wrong");

            // ---- 8. CreateAndSend + AntigravityDesktopAdapter + DesktopOperations direct session binding & idempotency
            var adapter = new AntigravityDesktopAdapter(client);
            Require(adapter.Tool == "agy" && adapter.Available, "adapter should report agy available");
            string longDraft = new string('测', 2500); // > 1000 (allowed for direct RPC) and <= 8000
            adapter.Check(projectDir, longDraft);
            Refused(() => adapter.Check(projectDir, new string('x', 8001)), "draft > 8000");

            string createOpId = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
            var ops = new DesktopOperations(Path.Combine(root, "ops"), adapter);
            int startsBefore = hub.StartRequests.Count;
            int sendsBefore = hub.SendRequests.Count;
            var created = ops.Open(createOpId, "agy", projectDir, longDraft);
            Require((string)created["status"] == "created" && true.Equals(created["sent"]) && (string)created["session"] == convCreated, "CreateAndSend receipt wrong");
            Require(hub.StartRequests.Count == startsBefore + 1 && hub.SendRequests.Count == sendsBefore + 1, "StartCascade + SendUserCascadeMessage should both run once");
            Require((string)hub.SendRequests[hub.SendRequests.Count - 1]["clientMessageId"] == createOpId, "CreateAndSend should forward operationId as clientMessageId");
            var createPlanner = (Dictionary<string, object>)((Dictionary<string, object>)hub.SendRequests[hub.SendRequests.Count - 1]["cascadeConfig"])["plannerConfig"];
            var createModel = (Dictionary<string, object>)createPlanner["requestedModel"];
            Require((string)createModel["model"] == "MODEL_PLACEHOLDER_DEFAULT", "CreateAndSend must attach default model from GetCascadeModelConfigData");
            // CameOf immediately resolves the directly created session!
            Require(ops.CameOf("agy", convCreated, projectDir, DateTime.UtcNow.AddMinutes(-10), false) == createOpId, "CameOf should immediately match directly created session");
            // Replay with same opId must NOT call StartCascade again
            var replayed = ops.Open(createOpId, "agy", projectDir, longDraft);
            Require((string)replayed["session"] == convCreated && hub.StartRequests.Count == startsBefore + 1, "replay must not re-create");
            // Receipt file must not contain draft, folder path, or csrf token
            string receiptJson = File.ReadAllText(Path.Combine(root, "ops", "desktop-operations", createOpId + ".json"), Encoding.UTF8);
            Require(!receiptJson.Contains(longDraft) && !receiptJson.Contains(projectDir) && !receiptJson.Contains(csrfSecret), "receipt must not leak draft/folder/csrf");

            // Create with empty draft: StartCascade only, sent == false
            hub.NextCreatedId = convCreatedEmpty;
            string emptyOpId = "b1b2c3d4e5f60718293a4b5c6d7e8f90";
            var createdEmpty = ops.Open(emptyOpId, "agy", projectDir, "");
            Require((string)createdEmpty["status"] == "created" && false.Equals(createdEmpty["sent"]) && (string)createdEmpty["session"] == convCreatedEmpty, "empty-draft CreateAndSend receipt wrong");
            Require(hub.SendRequests.Count == sendsBefore + 1, "empty draft should not call SendUserCascadeMessage");

            // ---- 9. AntigravityDesktopSessions cache & Changed event
            var sessionsCache = new AntigravityDesktopSessions(client);
            var changedEvent = new ManualResetEventSlim(false);
            sessionsCache.Changed += () => changedEvent.Set();
            sessionsCache.Refresh();
            Require(changedEvent.Wait(5000), "sessions cache did not fire Changed");
            Require(sessionsCache.Get(convAsk) != null && sessionsCache.Get(convAsk).Status == "confirm", "cached state for convAsk wrong");
            Require(sessionsCache.All().Length == 2, "cached states should include active desktop trajectories only, got " + sessionsCache.All().Length);

            changedEvent.Reset();
            hub.Online = false;
            client.Discover(true);
            sessionsCache.Refresh();
            Require(changedEvent.Wait(5000), "sessions cache did not clear when hub went offline");
            Require(sessionsCache.All().Length == 0, "offline cache should be empty");

            // ---- 9b. Port-to-PID ownership check & zero retry on mutating writes
            var tcpProbe = new TcpListener(IPAddress.Loopback, 0);
            tcpProbe.Start();
            try {
                int ownedPort = ((IPEndPoint)tcpProbe.LocalEndpoint).Port;
                int myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                Require(AntigravityDesktopClient.IsLoopbackPortOwnedByPid(ownedPort, myPid), "loopback port owned by current PID should verify");
                Require(!AntigravityDesktopClient.IsLoopbackPortOwnedByPid(ownedPort, myPid + 123456), "loopback port must reject wrong PID");
                var epCheck = new AntigravityDesktopClient.Endpoint { Pid = myPid, HttpPort = ownedPort, HttpsPort = FreePort() };
                Require(AntigravityDesktopClient.VerifyEndpointPorts(epCheck) && epCheck.HttpPort == ownedPort && epCheck.HttpsPort == 0, "VerifyEndpointPorts should keep owned port and clear unowned port");
                var badEp = new AntigravityDesktopClient.Endpoint { Pid = myPid, HttpPort = FreePort(), HttpsPort = 0 };
                Require(!AntigravityDesktopClient.VerifyEndpointPorts(badEp), "VerifyEndpointPorts must reject endpoint with no owned port");
            } finally {
                tcpProbe.Stop();
            }

            int writeAttempts = 0;
            var writeClient = new AntigravityDesktopClient(
                desktopHome,
                pid => pid == livePid,
                (targetEp, method, payload, ms) => {
                    if (method == "GetStatus") return new Dictionary<string, object>();
                    writeAttempts++;
                    throw new IOException("simulated drop after write");
                });
            bool writeThrew = false;
            try { writeClient.CreateAndSend(projectDir, "no double create", "c1b2c3d4e5f60718293a4b5c6d7e8f90"); }
            catch (IOException) { writeThrew = true; }
            Require(writeThrew && writeAttempts == 1, "mutating write must not auto-retry on IOException, got attempts=" + writeAttempts);
        }

        Console.WriteLine("AntigravityDesktop mock suite: discovery, dedup, read/since/offline, inspect/answer, create+send, stop, cache, port-owner & no-write-retry passed");

        // ---- 10. Optional strict read-only smoke test against live language_server.exe
        if (args.Length > 1 && args[1] == "--live-ro") {
            var live = new AntigravityDesktopClient();
            Require(live.Available, "live Antigravity Desktop language_server.exe not detected");
            var ep = live.Discover(false);
            Require(ep != null && ep.Pid > 0, "live endpoint missing");
            var liveSummaries = live.Summaries();
            Require(liveSummaries.Count > 0, "live Summaries returned 0 desktop conversations");
            var first = liveSummaries[0];
            var liveRead = live.ReadConversation(first.Id);
            Require(false.Equals(liveRead["same"]), "live ReadConversation first call should return same=false");
            var liveMsgs = liveRead["messages"] as List<Dictionary<string, object>>;
            Require(liveMsgs != null, "live ReadConversation missing messages list");
            string liveStamp = Convert.ToString(liveRead["stamp"]);
            var liveSame = live.ReadConversation(first.Id, liveStamp);
            Require(true.Equals(liveSame["same"]), "live ReadConversation with unchanged stamp should return same=true");
            var liveState = live.Inspect(first.Id);
            Require(liveState != null && liveState.Id == first.Id && liveState.Live, "live Inspect failed");
            Console.WriteLine("Live RO smoke passed: " + ep + ", summaries=" + liveSummaries.Count
                + ", first=" + first.Id + " (status=" + liveState.Status + ", msgs=" + liveMsgs.Count + ")");
        }
    }
}
