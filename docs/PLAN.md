# 下一轮计划：工具适配层、Antigravity 补齐、额度、多 AI 协作

2026-11-20 起草。这份计划回答五件事：电脑上开着的 Antigravity CLI（agy）为什么仍然“修不好”、额度能否显示、几个 AI 能否共同完成一项工作、还有哪些交互要改、从目前的使用看还缺什么。每一阶段写明依据、做法、涉及的文件和验收办法；做完一项就把状态改到 `docs/ROADMAP.md`。

原则沿用 `AGENTS.md`：一项能力一个模块，状态放在对象里，改动带测试和文档。另加一条：动手之前先看有没有现成的开源实现或官方接口，能复用就不自己造。

## 现状核查（2026-11-20，本机）

| 项目 | 看到的事实 |
| --- | --- |
| 安装的电脑端 | 1.7.20，2026-11-19 19:35 安装 |
| 正在运行的 agy | 1.5.12，在 Positron 的 PowerShell 终端里运行，对话属于 `E:\01_网站\05-探针`（不在手机项目列表里，是“项目外”对话），数据库状态 `RUNNING` |
| 电脑端能否识别它 | 能。用当前源码的 `Antigravity.Summaries` 和 `AgySessions.Inspect` 只读探测：识别为在用、来源 Positron、执行中、可接管 |
| 能对它做什么 | 只能“接管”（结束 Positron 里的进程，在手机终端恢复历史）。`Ways.cs` 对 agy 的发送、停止、回答确认都直接写了“不支持”，因为 agy 没有接入钩子（`Reach`），也没有对话驱动（`Chats.cs`） |
| 手机发消息的记录 | `outbox/` 里 7 条都已交付，全部是 Claude Code；没有滞留 |
| 代码体量 | `RemoteCliAgent.cs` 180 KB、`agent-linux/agent.py` 212 KB、`web/app.js` 108 KB、`Workbench.java` 73 KB、`MainActivity.java` 62 KB。工具相关的判断以 `tool == "agy"` 之类的分支散在 `RemoteCliAgent.cs` 各处 |
| 发版节奏 | 11-12 到 11-19 装了 1.7.10 到 1.7.20 共 8 个版本，多数是修正；手机上的验收在 ROADMAP 里一直是“未完成” |

结论：1.7.20 修的是“识别”，用户要的是“像 Claude Code 一样不打断地发消息、停止、回答确认”。这一步做不了，不是识别问题，而是 agy 还没有接入任何控制通道。

## agy 自己提供的接口（`agy --help`，1.5.12）

| 接口 | 用途 | 对应本项目已有的做法 |
| --- | --- | --- |
| 钩子 `PreToolUse`、`PostToolUse`、`Stop`（全局文件 `~/.gemini/config/hooks.json`，项目里 `.agents/hooks.json`；写在 `antigravity-cli/settings.json` 里的不会加载） | 执行中留言、一步之前停止、回答确认 | `Reach.cs` 对 Claude Code、Codex 的做法 |
| `--input-format stream-json --output-format stream-json` | 程序持有的对话，逐条输入、逐事件输出 | `ClaudeChat`（Claude Code stream-json） |
| `agy acp` | Agent Client Protocol 的 stdio 服务 | 新的通用驱动（见阶段 1） |
| `agy remote-control` | Google 自己的远程连接，目前未注册 | 不使用，避免第二个写入者 |

钩子的配置格式与 Claude Code 不同：每个钩子在顶层有自己的名字，`{"remote-ai": {"PreToolUse": [...]}}`，不能整个包在 `"hooks"` 下（程序里的报错原文）。输出字段里有 `decision` 和 `continue`，没有看到 `additionalContext`，执行中留言能否像 Claude Code 那样在一步之前交付，要实测。

## 阶段 1：工具适配层，并把 agy 补到与 Claude Code 同等

先整理结构再加能力，否则 agy 的发送、停止、额度又会以新分支散进 `RemoteCliAgent.cs`。

| 工作项 | 做法 | 验收 |
| --- | --- | --- |
| 1.1 工具适配层 | 新建 `agent-windows/Tools/`：每个工具一个类（`ClaudeTool`、`CodexTool`、`AgyTool`），实现同一个接口：列出对话、读记录、判定归属、启动参数、可用通道、对话驱动、额度。`RemoteCliAgent.cs` 只按接口调用，删掉散落的 `tool == ...` 分支。`Ways.Of` 改为询问工具“有哪些通道”，原因文字随工具类 | 现有检查全部通过（`ways_check.py`、`agy_check.py`、`in_use_check.py`、`chat_check.py` 等），行为不变；`RemoteCliAgent.cs` 明显变小。**第一步已完成**：`agent-windows/Tools.cs`；**第二步已完成**：记录解析抽至 `Transcripts.cs`，Windows 锁文件归属与接管抽至 `SessionLocks.cs`，结构化对话驱动创建与闲置唤醒归入 `Tools.cs`，Codex/ACP 共用 `JsonRpcChat`，`ScanAll` 按工具拆分为 `ScanClaude`/`ScanCodex`/`ScanAgy` 并共享一次进程树快照 |
| 1.2 agy 钩子 | `Reach` 增加 agy：安装时写入 `~/.gemini/config/hooks.json` 中名为 `remote-ai` 的一项（保留用户自己的钩子，卸载时只删这一项）；`Stop` 时交付排队的消息，`PreToolUse` 回答确认、按要求停止 | **已完成**：接入 `~/.gemini/config/hooks.json` 的 `remote-ai` 项，支持执行中留言（`PreInvocation`）、停止（`PreToolUse` + `PostInvocation`）和停下续做（`Stop`），在真实 `agy 1.5.12` 验证通过（`tests/agy_reach_live.py`） |
| 1.3 agy 对话驱动 | `Chats.cs` 增加 `AgyChat`。优先 `agy acp`：ACP 是公开协议，Gemini CLI、Codex（codex-acp）等也实现了它，一个 `AcpChat` 驱动以后能复用到 OpenCode、Qoder 等（中转已经认识这些工具名）；若 ACP 缺少续聊或确认，再用 stream-json | **已完成**：新增通用 `AcpChat` 与 `AgyChat`（`agy acp`），中转支持 `chat-agy`，新建对话与交接支持 agy；`tests/chat_check.py` 与真实 `agy 1.5.12`（`tests/agy_live.py`）验证通过 |
| 1.4 Linux | `agent-linux/agent.py` 同步 1.1–1.3（agy 在 Linux 上的数据目录要先核实） | `agent-linux/test_agent.py` 在 Linux 测试服务器上通过 |

完成后，Positron 里那段 agy 对话在手机上可以：执行中留言、停止、回答确认，不必再接管结束它。

## 阶段 2：额度显示

现成做法：CodexBar（开源，macOS 菜单栏）同时显示 Codex、Claude Code、Gemini 等的 5 小时和每周额度及重置时间。它的取数办法可以照搬，界面放在 Remote AI 自己的页面上。

| 工具 | 取数办法 | 状态 |
| --- | --- | --- |
| Codex | app-server 的 `account/rateLimits/read`，变化时有 `account/rateLimits/updated` 通知；返回 `primary`、`secondary` 两个窗口，各带 `usedPercent`、`windowDurationMins`、`resetsAt`。`CodexChat` 已经在用 app-server，可顺带读取 | 接口公开，可直接做 |
| Claude Code | 状态栏（statusLine）收到的 JSON 里有 `rate_limits.five_hour` 和 `seven_day`（`used_percentage`、`resets_at`），只对 Pro/Max 账户、且第一次回答之后才有，任一窗口可能缺失。做法二选一：给用户的状态栏套一层，记录额度后原样调用用户原来的命令；或在 stream-json 的事件里找额度（要实测） | 需要用户同意改状态栏配置 |
| agy | `agy -p /usage --output-format json`（不开启对话、不耗额度）返回各模型池（`groups`）的 `remaining_fraction` 与 `reset_time` | 已接入：默认只显示 Gemini/Neon 主池，其余模型池点击展开 |

界面：“我的”页列出每个工具两个窗口的已用百分比和重置时间；新建对话和“交给另一个 AI”的面板上，工具名旁写剩余额度；某个工具超过 90% 时在面板顶部建议交给另一个。电脑端只把百分比和重置时间交给中转，不传账户信息。**已完成（1.8.0）**：Windows `Quotas.cs`、中转、`web/me.js`、`web/ui/work.js` 与 Android 原生 `QuotaLines.java` / `AppPages.java` 已上线并通过检查。

## 阶段 3：多个 AI 共同完成一项工作

已有的是“交接”“同时提问”“接力”“定时提问”，都是一次性的转交。缺的是一个有目标、有角色、会自己推进并在该停的地方停下的“协作任务”。

参考：claude-squad、Parallel Code、Daintree 等开源工具的共同做法是每个代理一个 git worktree 并行，最后由人挑选合并；它们也承认任务拆分、冲突和合并的判断大多仍要人来做。所以先做两种最常用、规则清楚的模式，不做全自动的“多代理自治”。

| 模式 | 流程 | 何时停 |
| --- | --- | --- |
| 写—审循环（先做） | A 写代码；结束后把改动（diff 和 A 的说明）交给另一家的 B 以只读方式审查（Claude Code `--permission-mode plan`、Codex 只读沙箱、agy `--mode plan`）；B 的意见交回 A 修改，循环 | B 回答“通过”、到达设定轮数、或任一方等确认；每轮结果推送到手机 |
| 并行比稿 | 同一任务交给两三个 AI，各在自己的 worktree 里做；手机上并排看每个方案的改动和说明，选一个合并，其余丢弃 | 都做完后等人选 |
| 领队（第二步） | 指定一个 AI 为领队，它收到目标后按固定格式给出分工（子任务、交给谁、理由，例如前端交给 Claude Code、批量改写交给 Codex、查资料交给 agy）；电脑端按分工把子任务交给各成员（会写文件的成员各用一个 worktree），做完把结果交回领队，由领队审查、合并或再派。领队通过电脑端提供的 MCP 工具（`delegate`、`status`、`result`）指挥，不直接操作别的程序 | 领队宣布完成、到达设定轮数或预算、任一成员等确认；每次分工和合并前可设为先问手机 |
| 顾问（以后） | 电脑端提供一个 MCP 服务（`ask_ai`），一个 AI 在执行中可以向另一个只读请教、复核或审查，结果直接作为步骤显示在同一个对话里 | **已完成（Windows）**：`agent-windows/Peers.cs` 通过命名管道与 `--mcp` 桥接按对话注入本地 MCP 服务（Claude Code `--mcp-config`、Codex `-c mcp_servers.remote_ai.*`、Antigravity CLI ACP `mcpServers`），被请教方默认只读且无递归，附带各工具实时额度提示，已通过 `tests/peers_check.py` |

做法：

- 新模块 `agent-windows/Teams.cs`：一个协作任务是一个对象（目标、成员、模式、轮次、状态），保存在数据目录，程序重启后继续。`Chains.cs` 的接力是它的特例，合并进来。
- 成员一律用对话驱动（`Chats.cs`）运行：轮次结束有明确事件，不靠记录静止 45 秒判断（Linux 接力目前的问题）。
- 新模块 `Worktrees.cs`：建立、列出、比较、合并、删除 worktree；同一文件夹里不允许两个会写文件的成员。
- 手机：“协作”页加“协作任务”，一页时间线显示每一轮谁说了什么、改了哪些文件；可以暂停、插话、结束。
- 中转：`/api/work` 增加 `teams`；协议写进 `docs/PROTOCOL.md`。

验收：真实的 Claude Code 写、Codex 审，在临时仓库里完成一个小改动并在第二轮通过；重启电脑端后任务接着进行；手机上看到每一轮。**已完成**：`agent-windows/Teams.cs` 与 `agent-windows/Worktrees.cs` 实现写审（`review`）、领队（`lead`）、先议后写（`discuss`）与独立 Git worktree 并行比稿（`compare` / `team_pick`），支持加密持久化、重启恢复、实时插话与完成后续聊（`team_say`）；中转 `/api/work`、`web/teams.js` 正序时间线与底部输入框、Android 原生 `AppPages.java` 全部接通并通过单元、真实 Git worktree、中转和无界面 Chrome UI 检查（`tests/teams_check.py`、`tests/worktrees_check.py`、`relay/test_relay.py`、`tests/together_ui_check.py`）。

## 阶段 4：交互

| 项目 | 做法 |
| --- | --- |
| 通知里直接处理 | Android 通知加“允许 / 拒绝 / 回复”按钮，等确认的请求不必打开 App |
| 每轮改动 | 一轮结束后在对话页列出改了哪些文件，点开看差异（`FileTransfers` 和项目文件浏览已有基础） |
| `/` 命令和快捷短语 | ROADMAP 已列：只列出 stream-json、app-server、ACP 实际支持的命令；常用的话可以自己添加成按钮 |
| 卡住提醒 | 执行或等确认超过设定时间时提醒（ROADMAP 后续想法 6） |
| 终端与对话互转 | 终端页加“改为对话”（ROADMAP 未完成项） |
| 同时提问后并排对比 | ROADMAP 后续想法 3，与阶段 3 的并行比稿共用一个对比页 |

## 阶段 5：工程与验收

| 项目 | 做法 |
| --- | --- |
| 拆大文件 | 阶段 1 之后继续：`RemoteCliAgent.cs` 按会话扫描、操作执行、报告拆开；`web/app.js` 按列表、项目、请求拆开；`Workbench.java`、`MainActivity.java` 同理。每次拆分只搬代码，行为不变，现有检查通过 |
| 推送代替轮询 | INTERACTION_PLAN 的 P2：Codex 桌面应用按连接订阅补丁；页面不再每秒拉记录 |
| 手机上的验收 | 不连接手机。发版说明列出这一版需要在手机上看的几项，由用户确认；页面检查在无界面 Chrome 的手机尺寸下截图比对 |
| 电脑端 | Rust 版冻结（实验性，只有普通终端）。Windows 和 Linux Python 两端以 `docs/PROTOCOL.md` 为准，逐步补一套共用的契约测试 |

## 顺序和需要用户决定的事

建议顺序：1.1 → 1.2 → 1.3 → 2（Codex 部分）→ 3（写—审循环）→ 4 → 5，阶段 5 的拆文件和手机验收穿插进行。

用户的决定（2026-11-20）：

1. Claude Code 的额度：允许给状态栏套一层。电脑端记录额度后原样调用用户原来的状态栏命令；卸载时恢复原配置。
2. 多 AI 协作：先做“写—审循环”，再做“领队”模式：由一个 AI 按各工具的特点拆分任务、指派给其他 AI，汇总结果并决定下一步（见阶段 3）。
3. Rust 版电脑端（Linux 的实验实现，只有普通终端）冻结：代码保留，新功能只做 Windows 和 Linux Python 两端。
4. 不用无线调试连接手机。手机上的验收由用户按发版说明里的几项自己看；检查仍在无界面 Chrome 和替身上做，报告里写明哪些没在手机上看过。

## 参考

- Antigravity 钩子：<https://antigravity.google/docs/hooks>
- CodexBar（额度取数方法）：<https://github.com/steipete/CodexBar>，Codex 部分 <https://raw.githubusercontent.com/steipete/CodexBar/main/docs/codex.md>
- Codex app-server 读取额度：<https://github.com/openai/codex/pull/5302>
- Claude Code 状态栏输入：<https://code.claude.com/docs/en/statusline>
- 多代理并行的开源工具综述：<https://www.augmentcode.com/tools/open-source-agent-orchestrators>
- 手机控制编码代理的开源项目（Happy、HAPI、Claude Code UI 等）：<https://codepick.dev/en/guides/mobile-ai-coding-tools-2026/>
