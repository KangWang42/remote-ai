#!/usr/bin/env bash
# remote-cli on Linux in one command: the relay and the agent of this computer as services
# of your own user, a public address, and the code for the phone to scan.
#
#   curl -fsSL https://raw.githubusercontent.com/KangWang42/remote-ai/main/agent-linux/install.sh | bash
#   curl -fsSL .../install.sh | bash -s -- --dir ~/projects/demo --name my-server
#
#   install.sh [--dir PATH[=NAME]]... [--name NAME] [--port N] [--tunnel | --url URL | --lan]
#   install.sh pair         show the address, password and QR again
#   install.sh status       are the services running, is the computer online
#   install.sh update       fetch the newest programs; password, projects and settings stay
#   install.sh uninstall    stop and remove everything; --purge also removes settings and the password
# After installing, the same commands are available as:  ~/.local/bin/remote-cli pair
#
# How the phone reaches this computer:
#   --tunnel    (the default) a Cloudflare quick tunnel: no domain, no account. cloudflared is
#               used if installed, else fetched from Cloudflare's GitHub releases into the
#               program folder. The address changes when the tunnel or the computer restarts:
#               run "remote-cli pair" and scan again
#   --url URL   a domain of your own that a reverse proxy sends to 127.0.0.1:PORT (docs/SELF_HOSTING.md)
#   --lan       the phone uses http://<this ip>:PORT directly. Not encrypted: for a home
#               network or a VPN, not for the open internet
#
# Projects (--dir) can also be added later from the phone. Nothing is installed system-wide
# and nothing needs root. Running it again keeps the password, the projects and the settings.
set -euo pipefail

# The repository under the name it is to have and the one it had (it is renamed with the product): the first that answers.
sources=("https://codeload.github.com/KangWang42/remote-ai/tar.gz/refs/heads/main" "https://codeload.github.com/KangWang42/remote-cli/tar.gz/refs/heads/main")
# cloudflared pinned to one release, checked against the sha256 Cloudflare publishes for
# each artifact (the digest field of the release API, cross-checked 2026-10-12).
cf_version="2026.10.0"
tunnel_url="https://github.com/cloudflare/cloudflared/releases/download/${cf_version}/cloudflared-linux-"
cf_sha256_amd64="d33ff2d14475178d2012c2c56beba87389ac5ded27649519f198a7d3134a99db"
cf_sha256_arm64="e6422b9d4f72d3194bc5a38676f13667c06666523217b842a877d72a80b5ac08"
cf_sha256_arm="1dbe8e4ec17e74bb7f49cf91db6a4903bd0f9fe41984556c7503e40b765fd099"
cf_sha256_386="f6fbd789e6ce9c824d4d560cbbfad2753d55ce398ece16b4c9fbf17271dceab3"
share="$HOME/.local/share/remote-cli-agent"
state="$HOME/.local/state/remote-cli-agent"
relay_state="$HOME/.local/state/remote-cli-relay"
units="$HOME/.config/systemd/user"
command_link="$HOME/.local/bin/remote-cli"
services=(remote-cli-agent remote-cli-relay remote-cli-tunnel)
fetched=""          # a folder made for this run, removed at the end
repo=""             # where relay/, web/ and agent-linux/ are taken from

say() { printf '%s\n' "$*"; }
fail() { printf '%s\n' "$*" >&2; exit 1; }
cleanup() { [ -z "$fetched" ] || rm -rf "$fetched"; }

download() {            # download URL FILE
    if command -v curl >/dev/null; then curl -fsSL --retry 2 -o "$2" "$1"
    elif command -v wget >/dev/null; then wget -q -O "$2" "$1"
    else python3 -c 'import sys, urllib.request; urllib.request.urlretrieve(sys.argv[1], sys.argv[2])' "$1" "$2"
    fi
}

need_systemd() {
    command -v systemctl >/dev/null || fail "这台电脑没有 systemd，请按 docs/LINUX.md 的手动步骤运行。"
    systemctl --user show-environment >/dev/null 2>&1 \
        || fail "连不上当前用户的 systemd（通过 su 切换用户时常见）。请直接以这个用户登录后再运行。"
}

# Sets repo to the folder that holds relay/, web/ and agent-linux/: the clone this file is
# in, else a copy of the repository fetched for this run.
find_source() {
    local file="${BASH_SOURCE[0]:-}" folder=""
    if [ -n "$file" ] && [ -f "$file" ]; then
        folder="$(cd "$(dirname "$file")/.." && pwd)"
    fi
    if [ -n "$folder" ] && [ -f "$folder/relay/server.py" ] && [ -d "$folder/web" ] && [ -f "$folder/agent-linux/agent.py" ]; then
        repo="$folder"
        return
    fi
    command -v tar >/dev/null || fail "需要 tar 来解开下载的程序。"
    fetched="$(mktemp -d)"
    say "正在下载 remote-cli…"
    got=""
    for source_url in "${sources[@]}"; do
        if download "$source_url" "$fetched/source.tar.gz" 2>/dev/null; then got=1; break; fi
    done
    [ -n "$got" ] || fail "下载程序失败：${sources[*]}"
    tar -xzf "$fetched/source.tar.gz" -C "$fetched" --strip-components=1
    repo="$fetched"
}

# One value of config.json, printed; empty when it is not there.
setting() {
    python3 - "$state/config.json" "$1" <<'PY'
import json, sys
try:
    value = json.load(open(sys.argv[1], encoding="utf-8")).get(sys.argv[2], "")
except (OSError, ValueError):
    value = ""
print(value if isinstance(value, (str, int)) and not isinstance(value, bool) else "")
PY
}

set_pair_url() {
    python3 - "$state/config.json" "$1" <<'PY'
import json, os, sys
path = sys.argv[1]
cfg = json.load(open(path, encoding="utf-8"))
if cfg.get("PairUrl") != sys.argv[2]:
    cfg["PairUrl"] = sys.argv[2]
    with open(path + ".tmp", "w", encoding="utf-8") as stream:
        json.dump(cfg, stream, ensure_ascii=False, indent=1)
    os.chmod(path + ".tmp", 0o600)
    os.replace(path + ".tmp", path)
PY
}

# The address the running tunnel announced, waited for up to 60 seconds. The tunnel writes
# its own log file, emptied each time it starts, so an address from before a restart is
# never shown and nothing depends on the journal being readable.
tunnel_address() {
    local found="" waited
    for waited in $(seq 60); do
        found="$(grep -o 'https://[a-z0-9-]*\.trycloudflare\.com' "$relay_state/tunnel.log" 2>/dev/null | tail -1 || true)"
        [ -n "$found" ] && break
        [ "$waited" = 3 ] && say "正在等待公网地址，通常十秒以内…" >&2
        sleep 1
    done
    printf '%s' "$found"
}

pair() {
    [ -f "$share/agent.py" ] || fail "还没有安装。"
    if systemctl --user is-active --quiet remote-cli-tunnel 2>/dev/null; then
        local address; address="$(tunnel_address)"
        [ -n "$address" ] || fail "公网通道一分钟内没有给出地址（需要能访问 Cloudflare）。原因见：tail -n 30 $relay_state/tunnel.log；也可以改用 --lan 或 --url 重新运行。"
        set_pair_url "$address"
    fi
    python3 "$share/agent.py" --pair --data "$state"
}

status() {
    need_systemd
    local unit
    for unit in "${services[@]}"; do
        [ -f "$units/$unit.service" ] && say "$unit: $(systemctl --user is-active "$unit" 2>/dev/null || true)"
    done
    [ -f "$state/config.json" ] || { say "还没有安装。"; return; }
    say "手机使用的地址：$(setting PairUrl)"
    python3 - "$(setting Server)" "$state/password.txt" <<'PY'
import json, sys, urllib.request
def call(path, data=None, token=""):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    body = json.dumps(data).encode() if data is not None else None
    with urllib.request.urlopen(urllib.request.Request(sys.argv[1] + path, data=body, headers=headers), timeout=10) as answer:
        return json.load(answer)
try:
    token = call("/api/login", {"password": open(sys.argv[2], encoding="utf-8").read().strip()})["token"]
    device = call("/api/terminal", token=token)["device"]
    print("电脑：%s，可用工具：%s，项目：%s" % ("在线" if device.get("online") else "不在线",
          "、".join(device.get("tools") or []) or "无", "、".join(device.get("workspaces") or []) or "无"))
except Exception as error:
    print("问不到中转：%s" % error)
PY
}

uninstall() {
    need_systemd
    local unit
    for unit in "${services[@]}"; do
        systemctl --user disable --now "$unit" >/dev/null 2>&1 || true
        rm -f "$units/$unit.service"
    done
    systemctl --user daemon-reload
    rm -rf "$share"
    rm -f "$command_link"
    if [ "${1:-}" = "--purge" ]; then
        rm -rf "$state" "$relay_state"
        say "已卸载，设置、密码和终端记录也已删除。"
    else
        say "已卸载。设置和密码留在 $state 和 $relay_state，再次安装会沿用；加 --purge 一并删除。"
    fi
}

# cloudflared: the one on this computer, else Cloudflare's own build for this processor,
# kept in the program folder.
tunnel_program() {
    if command -v cloudflared >/dev/null; then command -v cloudflared; return; fi
    local file="$share/bin/cloudflared" kind sum
    if [ ! -x "$file" ]; then
        case "$(uname -m)" in
            x86_64|amd64) kind="amd64" ;;
            aarch64|arm64) kind="arm64" ;;
            armv7l|armv6l) kind="arm" ;;
            i386|i686) kind="386" ;;
            *) fail "没有适合这种处理器（$(uname -m)）的 cloudflared，请改用 --url 或 --lan。" ;;
        esac
        say "正在下载公网通道程序 cloudflared（约 40 MB，只需一次）…" >&2
        mkdir -p "$share/bin"
        download "$tunnel_url$kind" "$file.part" \
            || fail "下载 cloudflared 失败（需要能访问 GitHub）。可以改用 --lan，或有域名时用 --url。"
        case "$kind" in
            amd64) sum="$cf_sha256_amd64" ;;
            arm64) sum="$cf_sha256_arm64" ;;
            arm) sum="$cf_sha256_arm" ;;
            386) sum="$cf_sha256_386" ;;
        esac
        echo "$sum  $file.part" | sha256sum -c - >/dev/null 2>&1 \
            || { rm -f "$file.part"; fail "cloudflared 校验不符，已阻止安装未经验证的程序。请检查网络环境后重试。"; }
        chmod 755 "$file.part"
        "$file.part" --version >/dev/null 2>&1 || { rm -f "$file.part"; fail "下载到的 cloudflared 不能运行。"; }
        mv "$file.part" "$file"
    fi
    printf '%s' "$file"
}

install() {
    local dirs=() name="" port="" mode="" url=""
    while [ $# -gt 0 ]; do
        case "$1" in
            --dir) [ $# -ge 2 ] || fail "--dir 后面要跟文件夹"; dirs+=("$2"); shift 2 ;;
            --name) [ $# -ge 2 ] || fail "--name 后面要跟名称"; name="$2"; shift 2 ;;
            --port) [ $# -ge 2 ] || fail "--port 后面要跟端口"; port="$2"; shift 2 ;;
            --url) [ $# -ge 2 ] || fail "--url 后面要跟地址"; mode="url"; url="${2%/}"; shift 2 ;;
            --tunnel) mode="tunnel"; shift ;;
            --lan) mode="lan"; shift ;;
            *) fail "不认识的参数：$1（--help 查看用法）" ;;
        esac
    done

    need_systemd
    command -v python3 >/dev/null || fail "需要 Python 3.9 及以上，这台电脑没有 python3。"
    python3 -c 'import sys; sys.exit(sys.version_info < (3, 9))' \
        || fail "需要 Python 3.9 及以上，这里是 $(python3 -c 'import platform; print(platform.python_version())')。"
    case "$url" in ""|http://*|https://*) ;; *) fail "--url 要以 http:// 或 https:// 开头" ;; esac

    local first="yes"; [ -f "$state/config.json" ] && first=""
    [ -n "$port" ] || port="$(setting Server | sed -n 's#^http://127\.0\.0\.1:\([0-9]*\)$#\1#p')"
    [ -n "$port" ] || port=8722
    case "$port" in ''|*[!0-9]*) fail "端口无效：$port" ;; esac
    if [ -z "$mode" ]; then
        if [ -n "$first" ]; then mode="tunnel"; else mode="keep"; fi    # run again: the way it was set up stays
    fi

    # ---- the programs
    find_source
    mkdir -p "$share" "$units" "$(dirname "$command_link")"
    mkdir -p -m 700 "$state" "$relay_state"
    rm -rf "$share/relay" "$share/web"
    cp "$repo/agent-linux/agent.py" "$share/agent.py"
    cp "$repo/agent-linux/install.sh" "$share/install.sh"
    cp -r "$repo/relay" "$repo/web" "$share/"
    find "$share" -name '__pycache__' -prune -exec rm -rf {} +
    chmod 755 "$share/install.sh"
    ln -sf "$share/install.sh" "$command_link"

    # ---- the password: made once, kept in files only this user can read, never printed here
    if [ ! -s "$state/password.txt" ]; then
        python3 -c 'import secrets; print(secrets.token_urlsafe(18))' \
            | python3 "$share/agent.py" --set-password "$state" >/dev/null
    fi
    ( umask 077; printf 'RCLI_PASSWORD=%s\n' "$(cat "$state/password.txt")" > "$relay_state/env" )

    # ---- settings: made on the first run, afterwards only what was asked for is changed
    python3 - "$state/config.json" "$port" "$name" "$mode" "$url" "${dirs[@]+"${dirs[@]}"}" <<'PY'
import json, os, socket, sys
path, port, name, mode, url, dirs = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5], sys.argv[6:]
try:
    cfg = json.load(open(path, encoding="utf-8"))
except (OSError, ValueError):
    cfg = {"RemoteEnabled": True, "RemoteDirs": []}
cfg["Server"] = "http://127.0.0.1:" + port
if name or not cfg.get("Name"):
    cfg["Name"] = name or socket.gethostname()
listed = [d for d in cfg.get("RemoteDirs", []) if isinstance(d, str)]
for item in dirs:
    folder, _, label = item.partition("=")
    folder = os.path.abspath(os.path.expanduser(folder))
    if not os.path.isdir(folder):
        sys.exit("文件夹不存在：" + folder)
    label = label or os.path.basename(folder) or folder
    if not any(d.partition("=")[2] == folder for d in listed):
        listed.append("%s=%s" % (label, folder))
cfg["RemoteDirs"] = listed
if mode == "url":
    cfg["PairUrl"] = url
elif mode == "lan":
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe.connect(("192.0.2.1", 9))          # no packet is sent: this only asks which address would be used
        address = probe.getsockname()[0]
    except OSError:
        address = "127.0.0.1"
    finally:
        probe.close()
    cfg["PairUrl"] = "http://%s:%s" % (address, port)
with open(path + ".tmp", "w", encoding="utf-8") as stream:
    json.dump(cfg, stream, ensure_ascii=False, indent=1)
os.replace(path + ".tmp", path)
PY
    chmod 600 "$state/config.json"

    # ---- services
    local listen="127.0.0.1"
    if [ "$mode" = "lan" ] || { [ "$mode" = "keep" ] && grep -q -- '--host 0.0.0.0' "$units/remote-cli-relay.service" 2>/dev/null; }; then
        listen="0.0.0.0"
    fi
    cat > "$units/remote-cli-relay.service" <<EOF
[Unit]
Description=remote-cli relay (what the phone and this computer's agent both connect to)
After=network-online.target
Wants=network-online.target

[Service]
EnvironmentFile=%h/.local/state/remote-cli-relay/env
ExecStart=/usr/bin/env python3 %h/.local/share/remote-cli-agent/relay/server.py --host $listen --port $port --data %h/.local/state/remote-cli-relay --web %h/.local/share/remote-cli-agent/web
Restart=on-failure
RestartSec=3

[Install]
WantedBy=default.target
EOF
    cp "$repo/agent-linux/remote-cli-agent.service" "$units/remote-cli-agent.service"
    # An update keeps the way it was set up: with a tunnel, its service is written again
    # and restarted only when it changed, because a restart gives the tunnel a new address.
    local tunnel="" restart_tunnel=""
    if [ "$mode" = "tunnel" ] || { [ "$mode" = "keep" ] && [ -f "$units/remote-cli-tunnel.service" ]; }; then
        tunnel="yes"
        local program wanted
        program="$(tunnel_program)"
        # An empty settings file of our own: cloudflared otherwise reads ~/.cloudflared or
        # /etc/cloudflared, and a named tunnel configured there answers 404 for this one.
        : > "$relay_state/cloudflared.yml"
        wanted="[Unit]
Description=remote-cli public address (Cloudflare quick tunnel to the relay)
After=network-online.target remote-cli-relay.service
Wants=network-online.target

[Service]
ExecStartPre=/bin/sh -c ': > %h/.local/state/remote-cli-relay/tunnel.log'
ExecStart=$program tunnel --config %h/.local/state/remote-cli-relay/cloudflared.yml --no-autoupdate --logfile %h/.local/state/remote-cli-relay/tunnel.log --url http://127.0.0.1:$port
Restart=always
RestartSec=5

[Install]
WantedBy=default.target"
        if [ "$wanted" != "$(cat "$units/remote-cli-tunnel.service" 2>/dev/null || true)" ]; then
            printf '%s\n' "$wanted" > "$units/remote-cli-tunnel.service"
            restart_tunnel="yes"
        fi
    else
        systemctl --user disable --now remote-cli-tunnel >/dev/null 2>&1 || true
        rm -f "$units/remote-cli-tunnel.service"
    fi

    systemctl --user daemon-reload
    systemctl --user enable --quiet remote-cli-relay remote-cli-agent
    systemctl --user restart remote-cli-relay
    if [ -n "$tunnel" ]; then
        systemctl --user enable --quiet remote-cli-tunnel
        if [ -n "$restart_tunnel" ] || ! systemctl --user is-active --quiet remote-cli-tunnel; then
            systemctl --user restart remote-cli-tunnel
        fi
    fi
    # the agent signs in at once when the relay already answers
    python3 - "$port" <<'PY' || true
import socket, sys, time
for _ in range(50):
    try:
        socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=1).close()
        break
    except OSError:
        time.sleep(0.2)
PY
    systemctl --user restart remote-cli-agent
    sleep 2
    local unit
    for unit in remote-cli-relay remote-cli-agent; do
        systemctl --user is-active --quiet "$unit" \
            || fail "$unit 没有启动成功，原因见：journalctl --user -u $unit -n 30（端口 $port 被占用时换一个 --port）"
    done

    if ! loginctl enable-linger "$USER" >/dev/null 2>&1; then
        say "提示：要在没有人登录时也保持运行，请执行一次：sudo loginctl enable-linger $USER"
    fi

    say "已安装并启动。以后用这些命令：$command_link pair | status | update | uninstall"
    [ "${#dirs[@]}" -gt 0 ] || [ -z "$first" ] || say "还没有项目：在 App 里进入这台电脑后添加文件夹，或再运行一次并加上 --dir <文件夹>。"
    [ "$listen" = "0.0.0.0" ] && say "注意：现在是未加密的直连（http），只适合家里的网络或 VPN；防火墙需要放行 TCP $port。"
    [ -f "$units/remote-cli-tunnel.service" ] && say "公网地址在通道或电脑重启后会变，那时运行 $command_link pair 重新扫码。"
    pair
}

main() {
    trap cleanup EXIT
    case "${1:-}" in
        pair) need_systemd; pair ;;
        status) status ;;
        update) shift; install "$@" ;;
        uninstall) uninstall "${2:-}" ;;
        -h|--help) if [ -f "${BASH_SOURCE[0]:-}" ]; then sed -n '2,25p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; fi ;;
        *) install "$@" ;;
    esac
}

main "$@"
