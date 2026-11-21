using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RemoteCli {

/// Codex through its app-server: one temporary thread (`ephemeral`, read-only sandbox, no approvals), with its tools,
/// hooks, skills, memories and MCP servers turned off, and one turn whose answer follows the entry's schema.
/// `thread/tokenUsage/updated` tells the tokens the turn used (tried on Codex 0.171).
internal sealed class CodexOrganizer : IOrganizeSource {
    readonly string launcher, folder;
    readonly Func<Dictionary<string, object>[]> listed;

    internal CodexOrganizer(string launcher, string folder, Func<Dictionary<string, object>[]> listed) {
        this.launcher = launcher; this.folder = folder; this.listed = listed;
    }

    public string Label { get { return "Codex"; } }
    public Dictionary<string, object>[] Models() { return listed(); }
    public string DefaultModel(Dictionary<string, object>[] offered) {
        return OrganizeReply.Cheapest(offered, new[] { "gpt-6-luna", "luna", "mini" }, "gpt-6-luna");
    }

    public Organizer.Metadata Analyze(string context, string model) { return Turn.Analyze(launcher, folder, model, context); }

    /// A single temporary turn, with no terminal, conversation registration, or tool approvals.
    sealed class Turn : JsonRpcChat {
        static readonly string[] Disabled = {
            "apps", "code_mode", "code_mode_only", "context_management", "current_time_reminder", "deferred_executor",
            "enable_fanout", "goals", "hooks", "image_generation", "memories", "multi_agent", "multi_agent_v2", "plugins",
            "request_permissions_tool", "shell_snapshot", "shell_tool", "standalone_web_search", "token_budget",
            "tool_suggest", "unified_exec", "view_image"
        };

        readonly TaskCompletionSource<Organizer.Metadata> answer = new TaskCompletionSource<Organizer.Metadata>();
        readonly string selected, prompt;
        string thread = "", turn = "", final = "";
        long tokens;

        Turn(string folder, string model, string context) : base("codex", folder, _ => { }) {
            selected = model;
            prompt = OrganizeReply.Request(context);
        }

        internal static Organizer.Metadata Analyze(string launcher, string folder, string model, string context, int timeout = 90000) {
            if (launcher == null) throw new InvalidOperationException("电脑没有可用的 Codex");
            Directory.CreateDirectory(folder);
            using (var request = new Turn(folder, model, context)) {
                request.Begin(launcher, "", false, "read");
                var completed = request.answer.Task;
                if (Task.WhenAny(completed, Task.Delay(timeout)).GetAwaiter().GetResult() != completed) throw new TimeoutException();
                return completed.GetAwaiter().GetResult();
            }
        }

        protected override void Begin(string launcher, string session, bool resume, string mode) {
            Run(launcher, "app-server --disable hooks");
            Call("initialize", new {
                clientInfo = new { name = "remote-ai-organize", title = "Remote AI Organizer", version = "1" },
                capabilities = new { experimentalApi = true }
            }, reply => {
                if (!Accepted(reply, "无法初始化 Codex 整理进程")) return;
                Notify("initialized", new { });
                Call("config/read", new { includeLayers = false, cwd = Folder }, ReadConfig);
            });
        }

        void ReadConfig(Dictionary<string, object> reply) {
            if (!Accepted(reply, "无法读取 Codex 的工具设置")) return;
            var effective = At(At(reply, "result"), "config") as Dictionary<string, object>;
            if (effective == null) { Fail("无法确认 Codex 工具已关闭"); return; }
            var config = Disabled.ToDictionary(feature => "features." + feature, feature => (object)false);
            config["web_search"] = "disabled";
            config["cloud.skills.enabled"] = false;
            config["skills.include_instructions"] = false;
            config["tools.experimental_request_user_input.enabled"] = false;
            var servers = At(effective, "mcp_servers") as Dictionary<string, object>;
            config["mcp_servers"] = (servers ?? new Dictionary<string, object>())
                .ToDictionary(server => server.Key, server => new { enabled = false });
            Call("thread/start", new {
                cwd = Folder, model = selected, ephemeral = true, sandbox = "read-only", approvalPolicy = "never",
                baseInstructions = OrganizeReply.Instructions, developerInstructions = "",
                runtimeWorkspaceRoots = new object[0], environments = new object[0], dynamicTools = new object[0],
                selectedCapabilityRoots = new object[0], config = config
            }, StartTurn);
        }

        void StartTurn(Dictionary<string, object> reply) {
            if (!Accepted(reply, "Codex 无法创建临时整理线程")) return;
            var result = At(reply, "result");
            var started = At(result, "thread") as Dictionary<string, object>;
            thread = Text(started, "id");
            if (thread.Length == 0 || started == null || !started.ContainsKey("path") || At(started, "path") != null) {
                Fail("无法确认 Codex 整理线程为临时线程");
                return;
            }
            if (Text(result, "model") != selected) { Fail("Codex 没有采用所选的整理模型"); return; }
            Call("turn/start", new {
                threadId = thread, model = selected, input = new[] { new { type = "text", text = prompt, text_elements = new object[0] } },
                outputSchema = OrganizeReply.Schema
            }, startedTurn => {
                if (Accepted(startedTurn, "Codex 没有接受整理请求")) turn = Text(At(At(startedTurn, "result"), "turn"), "id");
            });
        }

        protected override void Notified(string method, object p) {
            if (Text(p, "threadId") != thread || thread.Length == 0) return;
            string eventTurn = Text(p, "turnId");
            if (eventTurn.Length > 0 && turn.Length > 0 && eventTurn != turn) return;
            if (method == "thread/tokenUsage/updated") {
                tokens = OrganizeReply.Number(At(At(At(p, "tokenUsage"), "total"), "totalTokens"));
            } else if (method == "item/completed") {
                var item = At(p, "item");
                if (Text(item, "type") == "agentMessage" && Text(item, "phase") != "commentary") final = Text(item, "text");
            } else if (method == "turn/completed") {
                var complete = At(p, "turn");
                if (turn.Length > 0 && Text(complete, "id") != turn) return;
                if (Text(complete, "status") != "completed") { Fail("Codex 未能完成整理，请重试"); return; }
                try { var data = OrganizeReply.Parse(final); data.Tokens = tokens; answer.TrySetResult(data); }
                catch (ArgumentException) { Fail("Codex 返回的整理内容无效，请重试"); }
                catch (InvalidOperationException) { Fail("Codex 返回的整理内容无效，请重试"); }
            }
        }

        bool Accepted(Dictionary<string, object> response, string error) {
            if (At(response, "error") == null) return true;
            Fail(error);
            return false;
        }
        void Fail(string message) { answer.TrySetException(new InvalidOperationException(message)); }
        protected override void Requested(object id, string method, object p) { Refuse(id, "整理进程不接受工具调用或权限请求"); }
        protected override void OnEnded() { base.OnEnded(); Fail("Codex 整理进程已退出"); }
        public override void Say(string text, string id) { throw new NotSupportedException(); }
        public override void Stop() { Dispose(); }
        protected override void Reply(Asked asked, string decision) { throw new NotSupportedException(); }
    }
}
}
