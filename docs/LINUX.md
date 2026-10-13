# Linux 电脑端

[← 回到首页](../README.md)

在 Linux 服务器或台式机上被手机远控。

Linux 上没有带窗口的程序。电脑端是一个 Python 文件，和中转一起作为你自己这个用户的后台服务运行，不需要 root，也不往系统目录里装东西。需要 Python 3.9 及以上和 systemd。

```bash
curl -fsSL https://raw.githubusercontent.com/KangWang42/remote-ai/main/agent-linux/install.sh | bash
```

这一行会：下载程序放到 `~/.local/share/remote-cli-agent`，生成一个密码，把中转、电脑端和公网隧道注册成用户服务并启动，最后在终端里显示地址、密码和二维码。手机 App 点“扫码添加电脑”扫它即可。项目文件夹可以连上后在手机里添加。

需要下载隧道程序时，安装脚本使用固定的 cloudflared 2026.10.0，并校验对应架构的 SHA-256；校验失败会删除临时文件并停止安装。已安装的 cloudflared 继续使用，不会自动替换。

要加参数时写在 `bash -s --` 后面，例如指定两个项目文件夹和手机上显示的名称：

```bash
curl -fsSL https://raw.githubusercontent.com/KangWang42/remote-ai/main/agent-linux/install.sh | bash -s -- --dir ~/projects/demo --dir ~/work=工作 --name 我的服务器
```

| 参数 | 含义 |
| --- | --- |
| `--dir 路径` 或 `--dir 路径=名称` | 允许手机使用的项目文件夹，可以写多次 |
| `--name 名称` | 手机上显示的电脑名称，不写用主机名 |
| `--port 端口` | 中转在本机使用的端口，默认 8722 |
| `--tunnel`、`--url`、`--lan` | 手机怎样连到这台电脑，见下表 |

手机怎样连到这台电脑：

| 方式 | 适合 | 说明 |
| --- | --- | --- |
| `--tunnel`（默认） | 没有域名，想从任何地方访问 | Cloudflare 的临时隧道，不用注册账号。电脑上没有 `cloudflared` 时，脚本会从 Cloudflare 的 GitHub 发布页下载到程序目录（约 40 MB）。隧道或电脑重启后地址会变，那时运行 `remote-cli pair` 重新扫码 |
| `--url https://你的域名` | 有域名，想要固定地址 | 中转只监听本机，你用 nginx、Caddy 等把域名反向代理到 `127.0.0.1:8722`（nginx 需要的几行见 [自己部署中转](SELF_HOSTING.md)） |
| `--lan` | 家里的网络或 VPN | 手机直接用 `http://这台电脑的地址:8722`。没有加密，不要用在公网上 |

装好以后用 `~/.local/bin/remote-cli` 管理（这个目录在 `PATH` 里时直接写 `remote-cli`）：

```bash
remote-cli pair        # 再显示一次地址、密码和二维码；隧道地址变了以后用它
remote-cli status      # 服务是否在运行，电脑是否在线，手机使用的地址
remote-cli update      # 下载最新的程序；密码、项目、设置和隧道地址都保留
remote-cli uninstall   # 停止并移除全部；加 --purge 连设置和密码一起删除
```

地址和密码只在终端里显示，不写进服务日志；运行情况看 `journalctl --user -u remote-cli-agent`。想先看脚本再运行，或者已经克隆了仓库，也可以直接执行 `bash agent-linux/install.sh`，参数相同。

设置在 `~/.local/state/remote-cli-agent/config.json`，改动随时生效，不用重启服务：

| 设置 | 含义 |
| --- | --- |
| `Server` | 电脑端连接的中转地址。用安装脚本时是本机的 `http://127.0.0.1:8722` |
| `PairUrl` | 手机连接中转用的地址，和 `Server` 不同时才需要 |
| `RemoteEnabled` | `true` 才接受手机的操作；改成 `false` 会结束所有手机终端 |
| `Name` | 手机上显示的电脑名称，不写用主机名 |
| `RemoteDirs` | 项目文件夹，写成 `名称=完整路径`。终端只能在这些文件夹里启动；手机上添加的项目另存在同一目录的 `terminal-projects.json` |
| `Shell` | 普通终端用的 shell，不写用 `$SHELL` |
| `RemoteMaxMode` | 从手机启动 Claude Code、Codex 时的权限上限：`read` 只读，`edit` 可改文件，不写用它们自己的默认 |

中转在别的机器上时不用安装脚本：把 `agent.py` 和 `remote-cli-agent.service` 按服务文件开头的说明放好，`Server` 填那台中转的地址即可。

和 Windows 电脑端相比：

| | Linux 电脑端 |
| --- | --- |
| 普通终端 | 有，用你的 shell，手机上显示的就是它的名字（如 bash、zsh）。中转和 App 是 1.0.0 之前的版本时仍显示为“PowerShell” |
| Claude Code、Codex | 装在这个用户下（`PATH`、`~/.local/bin`、`~/.npm-global/bin`、nvm 等位置）就会出现在手机上；可以新建对话，也可以继续项目里保存的对话 |
| 查看文件、保存到手机 | 有，只给出项目文件夹之内的内容。Word 和 PowerPoint 的带格式预览不需要装任何软件 |
| 查看电脑上打开着的对话 | 有，点开是对话窗口，可以看最近的内容；所在文件夹还不是项目的也会列出（0.5.0 起）。从手机给它发消息需要 Windows 电脑端 |
| 添加项目时列出有对话的文件夹 | 有（0.5.1 起），每个写着由终端还是编辑器在用，可搜索 |
| 按“终端 / 应用”筛选项目和对话 | 有（0.5.2 起）。Linux 上没有桌面版，编辑器里开的对话归在“应用” |
| 置顶、隐藏对话 | 有（0.5.0 起），记在电脑端的数据目录里 |
| 删除历史对话 | 有（0.5.4 起）：Claude Code 的对话文件移到用户的回收站目录（`~/.local/share/Trash`），Codex 的用 `codex archive` 归档 |
| 替你结束电脑上正在使用对话的程序、Codex 另开一份 | 没有。请先在电脑上结束使用它的程序，再在手机上继续 |
| 在电脑上打开同一个终端 | 没有专门的窗口；在电脑的浏览器里打开手机使用的那个地址、输入密码，看到的就是同一批终端 |
| 从手机更新电脑端 | 没有，在电脑上运行 `remote-cli update` |
| “我的”里的工作统计 | 有（0.6.0 起），读取这个用户的 Claude Code 和 Codex 记录 |
| “协作”里的交接对话、定时提问 | 有（0.6.0 起），消息只能发给手机终端里正在运行的对话：电脑端把它输入到那个终端 |
| “协作”里的接力 | 没有。它要靠工具的钩子知道对话何时停下，这里没有安装钩子 |
| 发现电脑上其它用过的文件夹 | 没有，项目写在 `RemoteDirs` 里或从手机添加 |

## 测试范围

Linux 电脑端在 Ubuntu 24.04、Python 3.12 上测试过：真实的中转和终端；安装脚本的三种连接方式、更新和卸载；经公网隧道地址登录、添加项目、打开终端、执行命令和列出文件。下面这些没有实际验证过：

- 装有 Claude Code 或 Codex 的 Linux：它们的启动与对话列表是用替身程序和按真实格式编写的对话文件测试的；
- 用手机真机连接 Linux 电脑端。
