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
| `bin/` | xiaohongshu-mcp 的两个程序、登录信息 `cookies.json` 和日志 | 否 |

## 第一次使用

1. 从 xiaohongshu-mcp 的 Release 下载 `xiaohongshu-mcp-windows-amd64.exe` 和 `xiaohongshu-login-windows-amd64.exe` 放进 `bin/`，核对 Release 页给出的 SHA256。
2. 在 `bin/` 里运行登录程序，用手机小红书扫码。首次运行会下载约 150 MB 的内置浏览器。登录信息保存在 `bin/cookies.json`，过期后重新扫码。
3. 同一账号在网页端只能登录一处：用这里登录后，浏览器里的小红书网页会被挤下线，手机 App 不受影响。

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

## 写法

- 跟着过往笔记的路子：先说用 AI 写代码时碰到的具体麻烦，再说这次做了什么，最后写下载方式和“数据留在自己电脑上、不用注册”这几点。反响最好的两篇是《分享一下自己的科研全流程工作项目》和《人不在电脑前，手机一样写代码》。
- 每篇只讲一个主题，封面一句话说清楚，正文和卡片里的功能说法以 `docs/GUIDE.md` 和实际代码为准。
- 截图只用模拟电脑和编出来的项目，不出现本机的真实对话、路径和账号。
- 发出去的笔记不能通过这里修改或删除，要改就在小红书 App 里改，所以发布前先看 `out/` 里的图和 `post.md` 的全文。
