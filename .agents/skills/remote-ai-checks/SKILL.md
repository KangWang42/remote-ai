---
name: remote-ai-checks
description: 为 Remote AI 的协作会话、应用图标和局域网或公网隧道改动选择并运行对应的项目回归检查。
---

从仓库根目录执行。先看实际改动和已有测试结果，按受影响的行为选入口。构建与参数见 [开发说明](../../../docs/DEVELOPMENT.md)。同一次构建可以供多项检查复用。

| 改动 | 检查入口与判定 |
| --- | --- |
| 协作成员与终端归属 | `python tests/teams_check.py` 覆盖成员更换和持久化。涉及关闭真实终端时，将新编译的 Windows 程序目录传给 `python tests/teams_e2e_check.py <目录>`，同时检查任务成员关闭、独立会话可用、历史可读。 |
| 协作详情页 | `python tests/together_ui_check.py <Chrome路径> <截图目录>`。区分读取失败后的重试和另一设备删除后的退出。 |
| 应用图标 | `python tests/icon_assets_check.py` 核对平台资源及引用。图稿变动时看生成器输出的 Android 蒙版与 Windows 16、32 像素预览。打包入口变动时检查构建后的资源。 |
| 局域网与切换 | `python tests/lan_tools_check.py` 检查地址选择。涉及手机切换线路时运行 `tests/phone_line_check.py`，该测试使用隔离数据和模拟隧道。 |
| 公网隧道 | `tests/tunnel_keep_check.py` 检查重启后地址可用。输入或传输改动用 `tests/tunnel_check.py` 对真实公网地址发送测试命令，核对 HTTP 和 WebSocket 各自收到的回显。`--local` 用于定位本机链路。 |

协作验收时由领队核对成员报告对应的文件、构建和结果，复跑关键入口。后续局部修订只重跑受影响的检查。挂起的测试应等到输出结果，再判定是否达标。
