# 小红书进度推送

Remote AI 的进展笔记在这里写成稿、画好配图，再经 [xiaohongshu-mcp](https://github.com/xpzouying/xiaohongshu-mcp)（v2.7.4，Apache-2.0）发到小红书。读取主页和发布都通过它在本机运行的服务完成，不经过第三方服务器。

| 位置 | 内容 | 进 Git |
| --- | --- | --- |
| `posts/<日期>-<主题>/post.md` | 标题、话题和正文 | 是 |
| `posts/<日期>-<主题>/cards/*.html` | 配图，每张一页，按文件名排序 | 是 |
| `posts/<日期>-<主题>/out/*.png` | 画好的配图，1080 × 1440 | 否，`render` 生成 |
| `card.css` | 配图共用的版式：暖白底、黑色粗标题、蓝色产品名、黄色标记 | 是 |
| `shots.py` → `shots/` | 手机页面截图：真实的内嵌页面加 `tools/readme_pictures.py` 里的模拟电脑，任务内容是编的 | 脚本进，截图不进 |
| `past/` | 自己已发笔记的正文、配图、互动数和评论 | 否，`past` 生成 |
| `bin/` | 登录程序、修正版服务、Go 工具链、源码、登录信息 `cookies.json` 和日志 | 否 |
| `xiaohongshu-mcp-v2.7.4-creator-session.patch` | 发布服务的修正（见下文） | 是 |

## 第一次使用

1. 从 xiaohongshu-mcp 的 Release 下载 `xiaohongshu-login-windows-amd64.exe` 放进 `bin/`，核对 Release 页给出的 SHA256。
2. 发布服务要用修正版。v2.7.4 认不出 2026 年 11 月起创作者中心的登录状态，发布必报“创作者中心登录失效”（上游 issue #919，PR #913 未合并）。`xiaohongshu-mcp-v2.7.4-creator-session.patch` 按 #919 改了两处：改用主站 `web_session` 判断创作者中心已登录；点发布后如果弹出“扫码验证”，最多等 2 分钟。编译方法：从 go.dev 下载 go.mod 要求的 Go（1.27.3）解压到 `bin/toolchain/`，然后
   ```bash
   git clone --depth 1 -b v2.7.4 https://github.com/xpzouying/xiaohongshu-mcp.git xiaohongshu/bin/src
   cd xiaohongshu/bin/src && git apply ../../xiaohongshu-mcp-v2.7.4-creator-session.patch
   GOTOOLCHAIN=local GOPATH=../gopath ../toolchain/go/bin/go.exe build -o ../xiaohongshu-mcp-windows-amd64-fixed.exe .
   ```
   上游修好后改回官方 Release，并删掉这个补丁。
3. 在 `bin/` 里运行登录程序，用手机小红书扫码。首次运行会下载约 150 MB 的内置浏览器。登录信息保存在 `bin/cookies.json`，过期后重新扫码。
4. 同一账号在网页端只能登录一处：用这里登录后，浏览器里的小红书网页会被挤下线，手机 App 不受影响。

## 写一篇

```powershell
python xiaohongshu/xhs.py serve                    # 另开一个终端，服务只监听 127.0.0.1:18060
python xiaohongshu/xhs.py past                     # 读回已发笔记，past/index.md 按日期列出互动数
python xiaohongshu/shots.py "C:\Program Files\Google\Chrome\Application\chrome.exe"
python xiaohongshu/xhs.py render xiaohongshu/posts/<篇> "C:\Program Files\Google\Chrome\Application\chrome.exe"
python xiaohongshu/xhs.py check  xiaohongshu/posts/<篇>
python xiaohongshu/xhs.py publish xiaohongshu/posts/<篇>            # 默认公开；--visibility 仅自己可见；--at 定时
```

`check` 按平台的限制核对：标题不超过 20 字（两个英文字母算一个字），正文不超过 1000 字，话题不超过 10 个，图片 1 到 18 张且每张卡片都已画出。`publish` 先跑一遍 `check`，不通过就不发。

发布一篇约 2 到 3 分钟，正文是逐字输入的，服务给整个发布留 5 分钟。话题要用小红书上已有的名字：没有同名话题时（例如 `claude`）会一直等到超时，整篇都不会发出。发布失败后先确认主页上没有多出这一篇，等服务关掉上一次的页面再重试，紧接着重试会报 `context canceled`。点发布后可能弹出“扫码验证”，用手机小红书扫服务窗口里的二维码。

## 写法

选题、口吻、配图和发布确认的步骤见项目技能 [xiaohongshu-posts](../.agents/skills/xiaohongshu-posts/SKILL.md)。发出去的笔记不能通过这里修改或删除，要改就在小红书 App 里改。
