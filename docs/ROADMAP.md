# 路线与待办

这份文档记录 2026-10-13 起的一轮改版（1.6.0）：手机页面改为底部四个栏目，新增“协作”（交接对话、同时提问、接力、定时提问）和“我的”（工作统计）；以及 2026-10-14 的调整：App 和电脑页面的底栏统一为同样的四个栏目，新建对话可以选“应用”或“终端”。每一项写明现状、涉及的文件和验收办法，接手的人按“状态”一栏继续。规则见根目录 `AGENTS.md`，三方接口见 `docs/PROTOCOL.md` 的“The owner's work”。

## 界面结构

底部是四个栏目：任务、项目、协作、我的。App 自己有这四页，汇总所有电脑（原生代码）；一台电脑的页面 `web/index.html` 底部画的是同样的四个栏目。在 App 里，电脑页面上的栏目属于 App：点任何一个都回到 App 的那一页，电脑页面只是从某一栏点进去的那台电脑的部分。在电脑端窗口或浏览器里没有 App，四个栏目都由页面自己显示。

| 栏目 | App 里（所有电脑） | 一台电脑的页面 |
| --- | --- | --- |
| 任务 | `Workbench.java`：等你确认、置顶、正在运行、已打开、最近；搜索和筛选；右上角新建对话 | `web/app.js`：同样的排法 |
| 项目 | `Workbench.java`：每台电脑和它的项目，添加电脑 | `web/app.js`：项目列表、进入项目、添加项目；`web/begin.js`：新建对话 |
| 协作 | `AppPages.java`：各电脑的接力和定时提问，四个入口直接打开那台电脑的表单 | `web/together.js`：交接对话、同时提问、接力、定时提问 |
| 我的 | `AppPages.java`：统计合计、外观、更新、任务提醒 | `web/me.js`：工作统计、电脑端版本和工具；右上角进入设置 |

原生底栏在 `Tabs.java`，图标、尺寸和颜色照 `web/app.css` 的 `#tabs` 做；页面和 App 之间的约定见 `docs/PROTOCOL.md` 的 “App-wide navigation”。

“终端 / 应用”没有拆成两个栏目，仍是“任务”和“项目”里的筛选项：它们是同一批对话的两种来源，拆开会把“哪些在等我”分散到两处。

新栏目以模块加入：`app.js` 把页面的公共部分交给 `window.RemoteCliPage`（请求、弹层、对话列表、判断发送方式等），每个栏目的模块用 `RemoteCliPage.view(名称, { open, draw })` 登记自己，只管自己那一块页面。再加栏目时照此办理，并在 `index.html` 的 `#tabs` 和 `Tabs.java` 里各加一个。新建对话是同样方式加入的模块（`begin.js`，登记为 `RemoteCliPage.begin`）。

## 已完成（1.6.0）

| 项目 | 做法与文件 | 检查 |
| --- | --- | --- |
| 底部栏目 | `web/index.html`、`web/app.js`、`web/app.css`；记住上次的栏目，`?tab=` 可指定；输入时收起；等确认的数量显示在“任务”上 | `tests/tabs_ui_check.py`（三种宽度下不遮挡内容） |
| 工作统计 | `agent-windows/Stats.cs`、`agent-linux/agent.py` 的 `Stats`：读两个工具自己的记录，只给数字和文件夹名 | `tests/work_check.py`、`agent-linux/test_agent.py`（同一组编造的记录，数字逐项核对） |
| 定时提问 | `agent-windows/Schedules.cs`、`agent-linux/agent.py` 的 `Schedules` | 同上 |
| 接力 | `agent-windows/Chains.cs`；`Reach.Finished` 在对话自己停下时触发。只有 Windows（功能名 `chains`） | `tests/work_check.py` |
| 交接对话 | `RemoteCliAgent.cs` 的 `Brief`、`Tell`；`agent-linux/agent.py` 的 `handover`、`Agent.tell` | `agent-linux/test_agent.py`、`tests/tabs_ui_check.py` |
| 同时提问 | 只在页面里：对每段对话各发一次现有的发送请求 | `tests/tabs_ui_check.py` |
| 中转 | `relay/relay.py` 的 `work()`（`/api/work`，功能名 `work`、`chains`） | `relay/test_relay.py` |
| 发给手机终端里的对话 | 电脑端把消息作为粘贴文字输入到运行这段对话的手机终端（`Tell`、`Agent.tell`） | `tests/work_e2e_check.py`（真实的 Windows 电脑端和中转：统计、交接、到点提问、规则的增删）、`agent-linux/test_agent.py` |

## 已完成（1.6.2）

| 项目 | 做法与文件 | 检查 |
| --- | --- | --- |
| 同一条底栏 | `android/.../Tabs.java`：四个栏目，图标和尺寸与页面一致，“任务”上显示等确认的数量，键盘弹出时收起；`Workbench.java` 把原来的一页分成“任务”和“项目”；`web/app.js` 的 `go`：在 App 里四个栏目都交给 App | `tests/begin_ui_check.py`（页面一侧，App 由替身扮演）；原生一侧只做了编译 |
| App 打开的页面回到 App | `?new=1` 的表单和 `?take=` 的对话窗口离开后回到 App 的列表（`web/app.js` 的 `leave`） | `tests/begin_ui_check.py` |
| 协作的四个入口 | App 的“协作”页：交接对话、同时提问、添加接力、添加定时提问，打开那台电脑页面上的对应表单（`?tab=together&do=…`，`web/together.js`） | `tests/begin_ui_check.py` |
| 新建对话：应用或终端 | `web/begin.js`：项目页和“任务”的表单用同一套选择；种类记在 App 里，对所有电脑生效；应用的对话在电脑上发出后自动进入 | `tests/begin_ui_check.py`、`tests/desktop_ui_check.py`、`tests/ways_ui_check.py` |
| 添加项目后进入该项目 | `web/app.js` 的 `addProject` | `tests/begin_ui_check.py` |

## 已完成（1.7.0）：手机上的对话

起因：终端在手机上好用，是因为程序由电脑端持有，每个字节实时到达，按键和确认直接送进去；“应用”的对话窗口只是旁观别的程序持有的对话，按记录文件每秒刷新一次，整条消息写完才看得到，发消息、停止、确认都走钩子或唤醒之类的旁路，新对话还要有人在电脑上按发送。两个工具都有官方的结构化接口（Claude Code 的 stream-json，即其 SDK 所用；Codex 的 app-server，即其编辑器扩展和桌面应用所用），在临时文件夹里实测都能做到终端能做的事。

| 项目 | 做法与文件 | 检查 |
| --- | --- | --- |
| 电脑端运行对话 | `agent-windows/Chats.cs`：`Chat`（事件、状态、确认）和 `ClaudeChat`、`CodexChat` 两个驱动；作为手机终端的另一种运行方式（`IRunning`），沿用编号输出、重放、多端查看、列表和更新后接回 | `tests/chat_check.py`（真实中转和电脑端，两个工具的替身 `tests/chat_standin.py`）；在真实的 Claude Code 2.1.283 和 Codex 0.163.0 上经中转和电脑端各跑通新建、确认、停止、续聊和带历史继续 |
| Linux 电脑端 | `agent-linux/agent.py` 的 `Chat`、`ClaudeChat`、`CodexChat`（0.7.0） | `agent-linux/test_agent.py` 的 `Chats`（在 Linux 测试服务器上运行） |
| 中转 | `relay/relay.py`：`form: "chat"` 的终端，`say`/`stop`/`answer`，列表的状态和确认取自电脑端；WebSocket 接受这三个操作 | `relay/test_relay.py` |
| 对话页 | `web/chat/`：边写边显示、步骤与实时输出、确认按钮、停止、继续、断线续读；连接沿用 `web/terminal/bridge.js` | `tests/chat_ui_check.py`（真实中转，电脑端由测试扮演；三种状态的截图） |
| 入口 | `web/begin.js`：“应用”点一下即开始对话，长按改为在桌面应用中打开；`web/app.js`：历史对话按所选方式继续，长按换另一种；Android 列表直接打开对话页 | `tests/chat_ui_check.py`、`tests/android_aggregate_check.py` |

## 已完成（1.7.16）：底部面板与对话菜单

起因：对话页和终端页右上角“⋯”点开是一列方框按钮（更换外观也是一列方框），协作里定时提问的表单是几个原生下拉框和输入框，交接只能交给已经开着的对话。用户要求：菜单里能设置、能把对话交给另一个 AI（例如 Codex 额度用完交给 Claude）、能挂定时提问，并且整个界面有功能性、交互性和逻辑性。

| 项目 | 做法与文件 | 检查 |
| --- | --- | --- |
| 底部面板 | `web/ui/sheet.js`、`web/ui/sheet.css`：一块面板里的分步页面（向右推进、‹ 和手机返回键退回、下拉关闭），分组的行、分段选择、开关、加减、外观小图、选项胶囊、执行时有进度并在按钮上方写明失败原因的主按钮。样式都在 `.rs` 之下，不影响页面本身 | `tests/menus_ui_check.py`、`tests/together_ui_check.py`（四种宽度、深色对比度、键盘关闭后焦点回到入口） |
| 对话与协作的操作 | `web/ui/work.js`（`RemoteWork`）：交给另一个 AI（新对话或开着的对话、补充说明、预览）、某段对话的定时提问列表、定时提问编辑（快捷时间、按星期、下次时间）、选择对话的页面。对话页、终端页和协作共用 | 同上 |
| 对话页、终端页的菜单 | `web/chat/chat.js`、`web/terminal/bridge.js`：顶部是对话本身，四个常用操作，下面是重命名和阅读/显示设置；终端切换（`companion.js`）也改用面板 | `tests/menus_ui_check.py`、`tests/chat_ui_check.py`、`tests/pond_ui_check.py` |
| 协作页 | `web/together.js`：四个入口、带开关的定时提问卡片；接力、同时提问、交接都在面板里 | `tests/together_ui_check.py`、`tests/tabs_ui_check.py`、`tests/begin_ui_check.py` |
| 列表页的长按菜单和确认 | `web/app.js` 的 `ask()` 改用面板：带图标的行，结束和删除类单独一组，确认是一页加一个按钮 | `tests/ways_ui_check.py`、`tests/inbox_check.py`、`tests/chat_ui_check.py` |
| 交接内容（`brief`） | 电脑端把交接要说的话交回手机，手机用它新开对话（`start` 的 `text`）。Windows `RemoteCliAgent.cs`、Linux `agent.py`、中转 `relay.py`；`docs/PROTOCOL.md` | `relay/test_relay.py`、`agent-linux/test_agent.py` |
| 按星期的定时提问（`schedule-days`） | `Schedules.cs`、`agent.py` 的 `Schedules`、中转校验；没有这项能力的电脑端不显示星期，中转拒绝而不是每天都问 | `tests/work_check.py`、`relay/test_relay.py`、`agent-linux/test_agent.py` |
| App 原生面板 | `Kit.sheet` 改为一组带分隔线的行，当前使用的打勾，结束类单独一组；标题居中 | 只通过了编译 |

## 已完成（1.8.0）：工具适配层、Antigravity 补齐、额度显示与多 AI 协作任务

| 项目 | 做法与文件 | 检查 |
| --- | --- | --- |
| 工具适配层（第一步） | `agent-windows/Tools.cs`：`ClaudeTool`、`CodexTool`、`AgyTool`、`ShellTool` 统一启动参数、记录文件、占用判定、接管、结束和移除，`RemoteCliAgent.cs` 对应分支从 61 处减至 24 处 | `tests/chat_check.py`、`tests/ways_check.py`、`tests/agy_check.py` |
| Antigravity CLI 钩子与 ACP 对话 | `agent-windows/Reach.cs`、`Ways.cs` 接入 `~/.gemini/config/hooks.json` 的 `remote-ai` 项（执行中留言、停止、停下续做）；`Chats.cs` 新增通用 `AcpChat` 与 `AgyChat`（`agy acp`）；中转新增 `chat-agy`；新建对话与交接支持 agy | `tests/reach_check.py`、`tests/ways_check.py`、`tests/chat_check.py`、`tests/begin_ui_check.py`、`tests/agy_reach_live.py`、`tests/agy_live.py` |
| 工具用量额度显示 | `agent-windows/Quotas.cs` 采集 Claude Code（对话事件与状态栏套壳）、Codex（app-server）与 Antigravity CLI（`agy -p /usage --output-format json`，默认显示 Gemini/Neon 主池，其余模型池按需展开）的额度窗口与重置时间；中转透传；手机页 `web/me.js`、`web/ui/work.js` 与 Android 原生 `QuotaLines.java` / `AppPages.java` 顶部显示进度条（超 90% 标红） | `tests/quotas_check.py`、`relay/test_relay.py`、`tests/tabs_ui_check.py`、`tests/android_aggregate_check.py` |
| 多 AI 协作任务（写审 / 领队） | `agent-windows/Teams.cs` 实现写审（`review`，写手 + 只读审查员 + `git diff` 循环）与领队（`lead`，拆分分派 + 并行执行 + 汇总验收），加密持久化并支持重启暂停后继续；中转 `/api/work` 新增 `teams` 能力与 5 个动作；手机页 `web/teams.js`、`web/together.js` 与 Android 原生 `AppPages.java` 支持新建、查看、直达详情和启停删除 | `tests/teams_check.py`、`relay/test_relay.py`、`tests/together_ui_check.py`、`tests/android_aggregate_check.py` |

## 未完成

| 项目 | 现状 | 怎样继续 |
| --- | --- | --- |
| 手机上的验收 | 这里没有真机和模拟器，页面只在无界面的 Chrome 里按手机尺寸检查过；原生的四个栏目、“任务”“项目”两页和“协作”页的入口只通过了编译。底部面板的下拉关闭、返回键逐步退回、键盘弹出时面板的位置，以及原生面板的新样式，都没有在手机上看过 | 在手机上看：原生底栏与页面底栏是否一致（高度、图标、字号、角标位置），在两者之间切换时的观感；底栏与系统导航键的间距、键盘弹出时的表现、各外观下的图表、日期和时间选择器 |
| 桌面应用里的对话从手机直接开始 | 1.7.0 起“应用”默认是手机上的对话，不再依赖桌面应用；长按仍可在桌面应用里打开，第一句话要在电脑上按发送 | 若仍需要：用界面自动化按下应用里的发送键，或让桌面应用载入手机上开始的对话（`codex://threads/<编号>`、`claude://resume?session=`），都要在真实应用上验证 |
| 手机上的对话在电脑上接着看 | 手机上开着时，电脑上的桌面应用或命令行若同时打开同一段，会有两个写入者 | 电脑端窗口列出手机上的对话并提供“在电脑上打开”：先关闭手机上的对话，再用上面两个链接交给桌面应用 |
| 手机上的对话的斜杠命令 | stream-json 和 app-server 都有对应接口（如 Claude Code 的 `initialize` 返回命令列表，Codex 的 `thread/compact/start`），页面还没有入口 | 在对话页输入框加 `/` 菜单，只列出两个接口实际支持的命令 |
| 终端和对话之间切换 | 对话页可以“在终端中打开”（关闭对话，在终端里继续同一段）；终端里的对话还不能直接转成对话页 | 终端页菜单加“改为对话”：结束终端，用 `form: "chat"` 带 `session` 重新打开 |
| 交接和接力文字在真实工具上的表现 | 两段文字各在真实的 Claude Code 上作为用户消息试过一次（小模型，临时文件夹），都被当作用户的要求处理；Codex 上没有试过，经钩子送达的情形也没有单独试 | 在临时文件夹里对 Codex 各试几次，并在执行中的对话上试一次经钩子送达，看工具是否照着做 |
| Linux 上的接力 | 电脑端看来源对话的记录：增长后静止 45 秒算一轮结束，接手的对话须在手机终端里。一步命令运行超过 45 秒且期间不写记录时会提前转交；契约测试用编造的记录，没有在真实工具上跑通一轮 | 在装有真实工具的 Linux 上跑通一轮；按各工具记录里的结束标记（如 Claude Code 回答的 `stop_reason`）判断，静止只作兜底 |
| 统计的增量读取 | 记录文件有变化时整个重读，结果只缓存在内存 | 记下每个文件读到的位置，只读新增部分；汇总落盘 |

## 后续想法

下一轮的分阶段计划（工具适配层、agy 补齐、额度、多 AI 协作）见 `docs/PLAN.md`。以下按“省时间”排序，都还没有做：

1. **中转服务更新。** 交给新对话（`brief`）和按星期提问（`schedule-days`）要中转认识这两项；电脑端自带的中转随安装包更新，公共中转和自建中转要单独更新，之前面板会写明需要更新。
2. **定时提问的新对话目标。** 到点在某个项目里新开一个 Claude Code 或 Codex 终端并发出第一句话。
3. **同时提问后的并排对比。** 把几段对话对同一问题的回答放在一页里对照。
4. **快捷指令。** 常用的话存成按钮，在对话页输入框上方一点就发；定时提问和交接的面板已经有固定的几句，还不能自己添加。
5. **每日简报。** 每天固定时间把各对话做了什么整理成一页，配合任务提醒推送。
6. **超时提醒。** 一段对话连续执行超过设定时间、或等确认超过设定时间时提醒。
7. **用量预警。** 当天 token 用量超过设定值时在“我的”和通知里提示。
8. **接力的条件。** 只有回答里出现某个词（例如“测试未通过”）时才触发，或交给不同的对话。
