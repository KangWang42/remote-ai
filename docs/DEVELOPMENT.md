# 组成、构建和测试

[← 回到首页](../README.md)

给想读代码、自己构建或参与修改的人。

## 组成

```
手机 App  ──HTTP(S)──▶  中转 relay（电脑上自带，或你自己的服务器）  ◀──HTTP(S)──  电脑端程序（运行终端）
```

| 目录 | 内容 |
| --- | --- |
| `agent-windows/` | 电脑端：`RemoteCliApp.cs`（窗口、托盘、连接方式）、`RemoteCliAgent.cs`（终端与对话扫描）、`Relays.cs`（公共中转清单和保存的中转）、`Remote.cs`（手机可以向电脑端程序本身提出的要求：读取和更改几项设置、切换连接线路）、`Controls.cs`（外观和控件）、`QrCode.cs`、`Setup.cs`（安装程序）。C#，用 Windows 自带的编译器构建 |
| `agent-linux/` | Linux 电脑端：`agent.py`（终端、对话扫描、查看文件），一个文件，只用 Python 标准库；`install.sh` 一条命令装好中转和电脑端；`remote-cli-agent.service` 是 systemd 用户服务 |
| `relay/` | 中转：`server.py`（登录、HTTP，以及公共中转里每台电脑各自的空间）、`relay.py`（终端状态与输出的转发：一台电脑一个 `Relay` 对象）。只用 Python 标准库；`install.sh` 把它装成服务器上的系统服务 |
| `android/` | 安卓 App：`MainActivity`（各界面之间的跳转、扫码回调、语音识别）、`Workbench`（工作台）、`Kit`（配色和界面部件）、`AggregateSessions`（任务分组与排序，不依赖 Android，可单独检查） |
| `web/` | App 里一台电脑的列表页和终端页（xterm.js），由中转提供、嵌在 App 内显示；随电脑端一起更新 |
| `tools/` | `package_windows.py`（安装程序）、`window_pictures.py`（文档里的电脑端截图）、`relay_list.py`（生成公共中转清单里不以明文出现的条目） |
| `docs/PROTOCOL.md` | 三方之间的接口，想写别的客户端或别的系统的电脑端看这里 |

电脑和手机都只向中转发起请求，电脑不需要公网地址或端口映射。

## 自己构建

```powershell
# 电脑端（两个 exe），只需要 Windows 自带的 .NET Framework 4.x
powershell -ExecutionPolicy Bypass -File agent-windows\build.ps1

# 安装程序：会从 python.org 下载官方的嵌入式 Python 并核对哈希
python tools\package_windows.py

# 安卓：需要 JDK 11+ 和 Android SDK（build-tools 36.0.0、platforms/android-36），不需要 Gradle
powershell -ExecutionPolicy Bypass -File android\build.ps1 -Jdk <JDK 目录> -Sdk <SDK 目录>
```

安卓的签名密钥在第一次构建时生成，保存在仓库之外的 `~/.remote-cli-signing`。请保留它：换一把密钥签名的 App 不能覆盖安装原来的。

```powershell
# 测试
python -m unittest discover -s relay -p "test_*.py"
python3 -m unittest discover -s agent-linux -p "test_*.py"   # Linux 电脑端，在 Linux 上运行：真实中转、真实终端，Claude Code 用替身程序
python tests\e2e_windows.py
node tests\bridge_check.js                                # 传输选择、连接状态、延迟测量、连续输入和断线重试
node tests\terminal_scroll_check.js                       # 拖动、惯性、起滑不跳动、画面跟随手指（两侧都有内容、网络停顿时的跟随、滑到列表尽头不漂移）、固定行的识别、只在终端重画时复制画面、滑动图层中途丢失时退回按行滚动、Codex 普通/全屏模式、Claude Code 的滚轮配速、鼠标编码、回到最新和程序自己的回到末尾提示的识别、提示所在行由上面的内容补上
python tests\fullscreen_check.py <chrome.exe>             # 占满屏幕的程序：触摸拖动逐行滚动；滑动时屏幕截图与静止时的终端画面逐像素比对（每一行都在应在的位置，没有遮挡），移动图层时不重画；手机给不出滑动图层、图层中途丢失或复制画面过慢时退回按行滚动；手指点“回到最新”回到末尾，程序自己在画面上给出回到末尾的提示时（Claude Code 的 Jump to bottom）上面的内容下移一行盖住它，不留空行，没有滑动图层时才只盖住这一行，按钮按程序的方式回去；长时间运行后重新打开只读最近的输出且画面完整。真实的中转和电脑端，Codex 用替身程序；屏幕密度按手机取 2.625（`RCLI_TEST_DENSITY` 可改）；读截图需要 Pillow
python tests\phone_line_check.py [chrome.exe [输出文件夹]]  # 手机上切换线路和更改设置：新线路在原线路旁准备好，交接后手机到达才保留，没到则回到原线路；隧道、局域网、中转互相切换；带浏览器时在页面上实际点按，并从列表给终端发一条指令。先运行 tools\package_windows.py
node tests\session_state_check.js                         # 运行中的会话与保留的写入锁分开显示
python tests\android_aggregate_check.py --jdk <JDK 目录>  # 工作台的分组、排序、已完成标记和刷新判定；按“等你处理、置顶、进行中、最近”分段、搜索、按工具和运行位置筛选、未加入项目的对话
python tests\workbench_ui_check.py                         # 工作台默认折叠、最近两条和标题可读性
python tests\android_update_check.py                      # App 更新器关闭安装流的顺序
python tests\native_owner_check.py                        # 临时进程验证归属，不操作真实 Codex 会话
python tests\relay_check.py                               # 电脑端对自有中转的判断：密码对、密码错、地址不是中转、端口不通
python tests\marks_check.py                               # 对话的置顶和隐藏标记：按工具和对话记在电脑端数据目录里，重启后仍在，文件损坏时不影响使用，数量超限时最早的让位
python tests\inbox_check.py <chrome.exe> [输出文件夹]      # 手机首页：按“等你处理、置顶、进行中、最近”分段，未加入项目的对话带文件夹名列出；按工具和运行位置筛选；搜索全部对话、终端和项目；长按（手指按住和鼠标右键）出菜单且不会误开对话；置顶、隐藏、在项目里找回；未加入项目的对话直接在手机上接过来（终端开在它原来的文件夹里，不建项目），以及把文件夹加为项目。真实的中转和电脑端，工具的对话用 tests\made_up.py 编造，不读取也不启动本机真实的工具
python tests\side_status_check.py                         # 电脑端窗口左下角：手机访问状态下面列出已连接的工具（Claude Code、Codex），只列状态良好的；工具的配置用临时文件夹代替，并核对本机真实配置没有变化；先运行 tools\package_windows.py
python tests\own_mode_check.py                            # “我的中转”页签：点开不断线，检查不通过不切换也不保存并说明原因，通过才保存并切换，之后记得它；先运行 tools\package_windows.py
python tests\public_mode_check.py                         # “公共中转”页签：不用配置就列出，选中后自动申请自己的空间，不显示地址和密码；空间被清理后重新申请；电脑端在空间里开终端，别的空间看不到
python tests\reach_check.py                               # 电脑上开着的对话：工具的钩子报告的事件由谁回答、何时交给手机，手机的消息怎样送达，以及写入工具配置的条目（用虚构的事件和临时文件夹；先构建电脑端）
python tests\reach_e2e_check.py [chrome.exe [输出文件夹]]  # 同一件事经真实的中转、电脑端和钩子程序走一遍：列表、确认、发消息，以及列表页上的卡片（虚构的 Codex 目录和事件）；对话的置顶和隐藏由电脑端记住；未加入项目的对话被列出、查看、发消息，文件夹加为项目后归入该项目
python tests\session_copies_check.py                      # 同一段对话只列一次，用虚构的对话文件
python tests\said_check.py                                # 手机上看到的对话内容：问了什么、答了什么、做了什么，用虚构的对话文件
python tests\look_check.py                                # 先看对话再接手、票据登录、手机和电脑共用一个终端；真实的中转和电脑端
python tests\tunnel_check.py --local                      # 直连回显延迟
python tests\tunnel_check.py <cloudflared.exe> --protocol http2  # 公网回显延迟
python tests\tunnel_keep_check.py <cloudflared.exe>       # 程序结束后再启动，公网地址不变
python tests\smooth_check.py                           # 历史很长时按键回显和连续输出的间隔
python tests\files_check.py [chrome.exe <输出文件夹>]    # 查看文件：列目录、读文件、不出项目文件夹；带浏览器时给每种查看器截图
python tests\restart_check.py                          # 电脑端和中转重启后，终端在原编号下接回
python tests\move_check.py                             # 电脑端换到另一个中转再换回：终端、名字、画面和已结束的终端都跟着，不留下假的运行中终端
python tests\history_check.py                          # 电脑端保留已交给中转的输出，中转缺少时从缺的地方再给一遍
chrome --headless=new --window-size=1480,520 --screenshot=skins.png tests\skin_code_check.html   # 各外观下代码、diff 和 16 色的显示
python tests\setup_check.py dist\RemoteCli-Setup-x.y.z.exe      # 安装程序，沙盒方式，不碰已有安装
python tests\pages_check.py <chrome.exe> <输出文件夹>     # 用本机的中转和电脑端截取列表页和终端页；对话是编造的（tests\made_up.py），不会把本机真实的对话画进图里
java -cp .cache\zxing-core-3.5.3.jar tests\QrDecodeCheck.java <二维码.png> <内容>   # 手机端的二维码识别
```
