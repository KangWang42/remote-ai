# 自己部署中转

[← 回到首页](../README.md)

中转是手机和电脑之间的那一站：两边都只向它发起连接，电脑不需要公网地址。Windows 电脑端自带中转，配合公网隧道就能在外面使用，一般不需要自己部署。下面两种情况值得部署一个：

- **想要固定的地址。** 公网隧道的地址在电脑端退出或电脑重启后会变，手机要重新扫码。自己的中转地址不变，配好一次，之后随时打开就能用。
- **想要更低的延迟。** 公网隧道经过 Cloudflare 的节点，手机和电脑都在中国大陆时，两段都要绕到境外。中转放在离两边都近的服务器上，路程短得多。一次实测中，电脑和服务器都在大陆，从发出按键到看到回显：经公网隧道约 200 毫秒，经自己的中转约 50 毫秒。

整个过程分四步：在服务器上安装中转、给它配上 https、在电脑端填地址和密码、手机扫码。

## 需要准备的

| 项目 | 要求 |
| --- | --- |
| 服务器 | 一台有公网 IP 的 Linux 服务器，离手机和电脑越近越好。中转占用很小，1 核 512 MB 足够，可以和别的服务共用 |
| 系统 | 有 systemd（235 及以上）和 Python 3.9 及以上，不需要其他依赖。`python3 --version` 显示的版本偏旧时，用包管理器装一个新的，例如 `dnf install python3.11` 或 `apt install python3` |
| web 服务器 | nginx、OpenResty、Caddy 等任意一种，负责 https。用宝塔、1Panel 之类面板的服务器已经有了 |
| 端口 | 能在服务器的防火墙或云厂商的安全组里放行端口 |

经过公网的连接必须用 https：访问密码和终端里的内容都在这条连接上。不要把中转直接用 `http://` 暴露到公网。

**用域名还是用 IP。** 有域名、并且这个域名能正常解析到这台服务器，用域名最省事（下面的方式 A）。没有域名，或者服务器在中国大陆而域名没有备案（未备案的域名在大陆服务器上会被拦截），直接用服务器的 IP 地址，证书由 Let's Encrypt 签发给这个 IP（方式 B）。

## 第一步：安装中转

在服务器上用 root 运行：

```bash
curl -fsSL https://raw.githubusercontent.com/KangWang42/remote-cli/main/relay/install.sh | sudo bash
```

脚本把中转装成一个系统服务，开机自启，只监听本机的 `127.0.0.1:8722`，外面还访问不到。完成时会显示访问密码，记下它，第三步要用。

服务器访问不了 GitHub 时，在别的电脑上下载源码压缩包，传到服务器再安装：

```bash
# 在能访问 GitHub 的电脑上
curl -L -o remote-cli.tar.gz https://github.com/KangWang42/remote-cli/archive/refs/heads/main.tar.gz
scp remote-cli.tar.gz root@服务器地址:/root/

# 在服务器上
tar -xzf remote-cli.tar.gz --strip-components=2 --wildcards '*/relay/install.sh'
sudo bash install.sh --source /root/remote-cli.tar.gz
```

之后用同一个脚本查看和维护（把脚本留在服务器上，或每次用上面的 `curl … | sudo bash -s -- 命令` 形式）：

| 命令 | 作用 |
| --- | --- |
| `bash install.sh status` | 服务是否在运行、版本和端口 |
| `sudo bash install.sh password` | 再看一次访问密码 |
| `sudo bash install.sh password --new` | 换一个新密码；之前登录过的手机和电脑都要用新密码重新连接 |
| `sudo bash install.sh update` | 换成最新的中转；密码和终端记录保留 |
| `sudo bash install.sh --port 18722` | 改用别的本机端口 |
| `sudo bash install.sh uninstall` | 停止并移除；加 `--purge` 连密码和记录一起删除 |

程序在 `/opt/remote-cli`，密码在 `/etc/remote-cli/relay.env`（只有 root 能读），终端记录在 `/var/lib/remote-cli`。服务名是 `remote-cli-relay`，日志用 `journalctl -u remote-cli-relay` 查看。日志里不会出现终端的输入和输出。

服务以 systemd 临时分配的用户运行。在 systemd 早于 244 且开着 nscd（名称缓存服务）的系统上，例如 RHEL 8 及其衍生版本，服务停止后缓存里可能还留着这个用户的名字，下一次启动会失败，日志里是 `status=217/USER` 和 `User or group with specified name already exists`，要等缓存过期（最长约一小时）才能恢复。1.2.1 起安装脚本和服务本身会在停止和启动时清掉这条缓存；仍在使用更早的安装脚本时，遇到这种情况运行 `sudo nscd -i passwd; sudo nscd -i group; sudo systemctl restart remote-cli-relay` 即可恢复。

## 第二步：配置 https

### 方式 A：有域名

把一个域名（例如 `cli.example.com`）解析到这台服务器，用面板或你平时的方式给它申请好证书，然后让它把所有请求转给中转。nginx 和 OpenResty 的站点配置：

```nginx
server {
    listen 443 ssl;
    server_name cli.example.com;
    http2 on;
    ssl_certificate     /证书所在位置/fullchain.pem;
    ssl_certificate_key /证书所在位置/privkey.pem;
    client_max_body_size 5m;

    location / {
        proxy_pass http://127.0.0.1:8722;
        proxy_http_version 1.1;
        proxy_set_header Host $http_host;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_buffering off;
        proxy_request_buffering off;
        proxy_read_timeout 3700s;
    }
}
```

每一行的用处：

- `Upgrade` 和 `Connection` 两行让终端使用的 WebSocket 连接能够通过；`proxy_read_timeout` 要长于一小时，中转每小时会主动续接一次连接。
- `proxy_buffering off` 让输出一产生就送到手机，不在 web 服务器里攒着。
- `X-Forwarded-For` 让中转知道真实的来访地址，密码连续输错时只锁定那个地址；`X-Forwarded-Proto` 让登录状态只在 https 下使用。
- `client_max_body_size` 给电脑端上传的文件片段留出余量。

nginx 低于 1.25 时没有 `http2 on;` 这个写法，删掉这一行即可。在面板里操作时，新建一个“反向代理”站点指向 `http://127.0.0.1:8722`，再把上面 `location /` 里的几行补进它的配置。重载 web 服务器后，中转的地址就是 `https://cli.example.com`。

### 方式 B：没有域名，用服务器的 IP

Let's Encrypt 可以给 IP 地址签发证书。这种证书有效期只有 6 天多，所以要配好自动续期。下面用 `1.2.3.4` 代表服务器的公网 IP，中转对外用 8443 端口。

**1. 放行端口。** 在防火墙或安全组的入方向放行 TCP 80 和 8443。80 端口只在签发和续期证书时由 Let's Encrypt 来访问，8443 是手机和电脑连接用的。

**2. 加一个站点配置。** 在 nginx 的站点配置目录（常见的是 `/etc/nginx/conf.d/`；1Panel 是 `/opt/1panel/www/conf.d/`）新建 `remote-cli.conf`，先只写 80 端口的部分：

```nginx
server {
    listen 80;
    server_name 1.2.3.4;
    location ^~ /.well-known/acme-challenge/ {
        root /var/www/remote-cli-acme;
        default_type text/plain;
    }
    location / {
        return 301 https://$host:8443$request_uri;
    }
}
```

建好校验用的目录，检查配置并重载：

```bash
mkdir -p /var/www/remote-cli-acme
nginx -t && nginx -s reload
```

web 服务器跑在容器里时（1Panel 的 OpenResty 就是），配置里的路径要写容器内的路径，命令要在容器里执行。以 1Panel 为例：宿主机的 `/opt/1panel/www` 在容器里是 `/www`，所以目录建在 `/opt/1panel/www/sites/remote-cli/acme`，配置里写 `root /www/sites/remote-cli/acme;`，重载用 `docker exec <容器名> openresty -t && docker exec <容器名> openresty -s reload`。

**3. 申请证书。** 用 [acme.sh](https://github.com/acmesh-official/acme.sh)，它只是一个脚本，装在当前用户的目录里。需要带 `--cert-profile` 选项的版本，3.1.6 可用：

```bash
curl https://get.acme.sh | sh -s email=你的邮箱
~/.acme.sh/acme.sh --issue --server letsencrypt -d 1.2.3.4 \
    -w /var/www/remote-cli-acme --cert-profile shortlived --keylength ec-256 --days 3
```

`-w` 后面是第 2 步建的目录在宿主机上的路径。`--cert-profile shortlived` 是签发 IP 证书必须的，`--days 3` 让它每 3 天续一次。服务器访问不了 GitHub 时，acme.sh 的说明里有国内镜像的安装方法。

**4. 把证书交给 web 服务器。** 选一个放证书的位置，让 acme.sh 把证书装过去，并在每次续期后重载：

```bash
mkdir -p /etc/nginx/remote-cli
~/.acme.sh/acme.sh --install-cert -d 1.2.3.4 --ecc \
    --fullchain-file /etc/nginx/remote-cli/fullchain.pem \
    --key-file /etc/nginx/remote-cli/privkey.pem \
    --reloadcmd "nginx -t && nginx -s reload"
```

**5. 补上 8443 端口的部分。** 在 `remote-cli.conf` 里再加一段，然后再次 `nginx -t && nginx -s reload`：

```nginx
server {
    listen 8443 ssl;
    server_name 1.2.3.4;
    http2 on;
    ssl_certificate     /etc/nginx/remote-cli/fullchain.pem;
    ssl_certificate_key /etc/nginx/remote-cli/privkey.pem;
    client_max_body_size 5m;

    location / {
        proxy_pass http://127.0.0.1:8722;
        proxy_http_version 1.1;
        proxy_set_header Host $http_host;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_buffering off;
        proxy_request_buffering off;
        proxy_read_timeout 3700s;
    }
}
```

中转的地址就是 `https://1.2.3.4:8443`。续期由 acme.sh 安装时加的定时任务每天检查，不需要再管；`crontab -l` 能看到它。

想让对外端口和中转的本机端口用同一个数字（例如都用 8722）时，两者会冲突：先用 `sudo bash install.sh --port 18722` 把中转挪到别的本机端口，`proxy_pass` 也改成 `http://127.0.0.1:18722`。

### 检查

在任意一台电脑上访问中转的地址加 `/api/session`，例如：

```bash
curl https://1.2.3.4:8443/api/session
```

返回 `{"signed_in": false, "version": "…"}`、没有证书警告，就说明 https 和转发都通了。用浏览器打开中转的地址，会看到输入访问密码的页面。

## 第三步：电脑端填地址和密码

打开电脑上的 Remote CLI：

1. “连接方式”点“中转”。这一步只是打开页签，现在的连接不会断。
2. 点下面的选择框，选“添加自己的中转…”。
3. 填中转的地址（`https://cli.example.com` 或 `https://1.2.3.4:8443`）和第一步显示的访问密码，可以再起一个名字，点“添加并连接”。

程序会先检查这个中转：地址能不能连上、证书是否有效、密码对不对。检查通过才保存并切换过去，窗口显示“已连上中转”；没通过时原来的连接方式保持不变，并直接说明是哪一处有问题：

| 窗口里的提示 | 原因和处理 |
| --- | --- |
| 地址需完整填写 | 地址以 `https://` 开头，后面是域名或 IP，需要时带端口，例如 `https://1.2.3.4:8443` |
| 连不上中转 | 服务器没开、端口没有放行，或地址里的端口写错 |
| https 证书无效或已过期 | 证书和地址不匹配，或续期没有成功。在服务器上重新签发，并确认续期的定时任务还在 |
| 这个地址上没有 Remote CLI 中转 | 地址指到了别的网站，或 web 服务器没有把请求转给中转 |
| 中转不接受这个密码 | “密码”一栏要填服务器上 `sudo bash install.sh password` 显示的密码 |
| 密码错了太多次 | 同一个地址连续输错 6 次会被锁 15 分钟，等一会儿再试 |

Linux 电脑端用 `--url` 指定中转地址，见 [Linux 电脑端](LINUX.md)。

## 第四步：手机扫码

用 App 扫电脑端窗口里的二维码。地址和密码都在二维码里，扫一次即可；之后电脑端重启、电脑重启，地址都不变，手机不用再扫。

## 在几种连接方式和几个中转之间切换

电脑端的“连接方式”随时可以改：选“公网隧道”走 Cloudflare，选“局域网”在同一个 Wi-Fi 下直连，这两个一点就切换；选“中转”后在选择框里点要用的那个，或点“连接”“重新连接”用选择框里显示的那个。切换后手机重新扫码，App 里这台电脑的地址会换成新的。终端页右上方的连接状态栏显示当前的中转延迟，可以用它比较不同线路。

三点需要知道：

- 终端跟着电脑走。切换不会结束正在运行的终端，正在执行的任务照常进行；手机连上新线路后，“进行中”里是同一批终端，名字和最近的画面都在，“最近使用”里是这次运行期间结束的终端。再切回来也一样。
- 这需要两边都是 1.0.9 或更高版本：电脑端，以及服务器上的中转（更新方法见下一节）。中转是旧版本时，切换过去看不到之前开的终端。
- 用自己的中转时，访问密码就是中转的密码；之后切回隧道或局域网，用的也是它，手机重新扫码即可。从公共中转切回隧道或局域网时，程序会换一个新的密码。
- 添加过的中转都记在电脑上。有几台服务器时各加一次，以后在选择框里点一下就换过去，右边显示的是到每一台的延迟。

## 更新

电脑端和 App 更新之后，建议把中转也更新到同一版本：新版本的页面和终端功能由中转提供。从手机切换线路（1.2.0 起）时，要切换到的中转需为 1.2.0 或更高版本。

```bash
sudo bash install.sh update
```

## 安全

- 访问密码是唯一的门：能访问到中转地址并知道密码的人，可以操作电脑上的终端。密码由安装脚本随机生成，不要换成容易猜到的。
- 中转保存每个终端最近的输出，用来在手机重新打开时恢复画面。它们放在服务器的 `/var/lib/remote-cli`，结束的终端只保留最后一小段。不想留下记录时，卸载时加 `--purge`。
- 密码连续输错会按来访地址锁定。web 服务器的访问日志里不要记录请求内容；上面的配置没有开启额外的日志。
- 服务器上开着 WAF 时，终端里的命令和输出可能被当成攻击内容拦截，表现为终端时断时续。给中转的站点关闭 WAF 规则，或把它加入白名单。

## 公共中转：给别人用的中转

上面装的中转只服务你自己的电脑。同一个程序也可以作为公共中转运行，让别人不用服务器就能固定地址连接：

```bash
sudo bash install.sh --public              # 已经装过的服务器上再运行一次同样可以
sudo bash install.sh --public --key 口令    # 只让带着这个口令的电脑端申请
sudo bash install.sh --public --spaces 50  # 最多服务多少台电脑，默认 100
```

https 的配置与前面完全相同。它和自己的中转有这些不同：

- 没有统一的密码。每台电脑的电脑端自动申请一个只属于它的空间，地址形如 `https://中转地址/c/<20 位编号>`，密码随机生成，中转只保存它的哈希。一个空间里的终端、登录和记录，从别的空间读不到也访问不到。
- 中转的首页没有任何页面，也没有使用者列表；除了版本号和申请空间，别的请求都要先进入某个空间并登录。
- 同一个来访地址一天最多申请 6 个空间；一个月没有用过的空间自动清理。`sudo bash install.sh status` 显示现在有多少台电脑的空间。
- 终端的输入和输出经过这台服务器。运营公共中转意味着使用者把这些内容托付给你，请只在你愿意承担这一点时开放，并保持系统和中转是新的。

**让电脑端列出它。** 电脑端“中转”页签里的公共中转来自一份清单，默认是本项目仓库根目录的 [`public-relays.json`](../public-relays.json)：

```json
{"relays": [{"name": "线路 1", "url": "https://relay.example.com", "key": ""}]}
```

`name` 是显示的名字（20 字以内），`url` 是中转的 https 地址，`key` 是安装时用 `--key` 设的口令，没设就留空。

清单是公开文件，写在里面的地址任何人打开或搜索仓库都能看到。不想这样时，用 `hidden` 字段代替 `url` 和 `key`，它由下面的命令生成：

```bash
python tools/relay_list.py hide "线路 1" https://relay.example.com 口令
# {"name": "线路 1", "hidden": "…"}
```

`hidden` 只是让地址不以明文出现，防的是随手翻看、搜索和按地址扫描的程序，不是加密：电脑端要连接就必须知道地址，读过源码的人同样可以把它还原出来。电脑端窗口和 App 都不显示公共中转的地址。要让扫到地址的人不能随意申请空间，安装时用 `--key` 设一个口令，并把它一起放进 `hidden`。电脑端启动时和之后每 6 小时读一次清单，所以增加、更换或撤掉一个中转只需要改这个文件，不需要发新版本；清单为空时，电脑端只显示使用者自己的中转。想用自己的清单时，给电脑端设置环境变量 `REMOTECLI_RELAYS`，值为清单的地址或本机文件路径。

## 手动运行

不用安装脚本时，中转就是一个命令：

```bash
git clone https://github.com/KangWang42/remote-cli && cd remote-cli
RCLI_PASSWORD='一个足够长的密码' python3 relay/server.py --host 127.0.0.1 --port 8722 --data /var/lib/remote-cli
```

不设 `RCLI_PASSWORD` 时，首次启动会生成一个密码，打印出来并保存在数据目录的 `password.txt`。加 `--public` 就是公共中转（不需要密码；`RCLI_JOIN_KEY` 设口令，`--spaces` 设数量）。

没有窗口的电脑端 `RemoteCliAgent.exe` 也在安装目录里，适合只用自有中转、想自己用计划任务启动的情况；它读取 `%LOCALAPPDATA%\RemoteCli\config.json`，密码用 `RemoteCliAgent.exe --set-password` 从标准输入写入。
