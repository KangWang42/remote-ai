#!/usr/bin/env bash
# The relay alone, as a service on a server of your own: a fixed address for the phone, and a shorter way than a
# Cloudflare tunnel when the phone and the computer are both close to the server.
#
#   curl -fsSL https://raw.githubusercontent.com/KangWang42/remote-ai/main/relay/install.sh | sudo bash
#   sudo bash install.sh [--port N] [--source remote-cli.tar.gz] [--public [--key WORD] [--spaces N]]
#
#   install.sh status       is the service running, which version, which port
#   install.sh password     show the password the computer and the phone sign in with; --new replaces it
#   install.sh update       put the newest relay in place; the password and the terminals' records stay
#   install.sh uninstall    stop and remove the service and the program; --purge also removes the password and records
#
# --port N       the port on this machine, 8722 unless given. The relay listens on 127.0.0.1 only: a web server
#                in front of it supplies https (docs/SELF_HOSTING.md)
# --source FILE  take the program from a source archive that is already on the server, for a server that cannot
#                reach GitHub: download https://github.com/KangWang42/remote-ai/archive/refs/heads/main.tar.gz
#                elsewhere and copy it over
# --public       a relay for everyone instead of one's own: it has no password; every computer that asks is given a
#                place of its own, with a password only that computer and its phones know (docs/SELF_HOSTING.md).
#                --key WORD lets only programs that give this word have a place; --spaces N is how many computers
#                at most (100 unless given)
#
# Needs root, systemd 235 or newer and Python 3.9 or newer. Installs nothing else. Running it again keeps the
# password and the port.
set -euo pipefail

# The repository under the name it is to have and the one it had (it is renamed with the product): the first that
# answers is used. RCLI_SOURCE names another address instead.
sources=("https://codeload.github.com/KangWang42/remote-ai/tar.gz/refs/heads/main" "https://codeload.github.com/KangWang42/remote-cli/tar.gz/refs/heads/main")
[ -z "${RCLI_SOURCE:-}" ] || sources=("$RCLI_SOURCE")
root=/opt/remote-cli
settings=/etc/remote-cli
unit=/etc/systemd/system/remote-cli-relay.service
service=remote-cli-relay.service
port=""
archive=""
purge=""
renew=""
public=""
key=""
spaces=""
fetched=""          # a folder made for this run, removed at the end
source=""           # the folder that holds relay/ and web/

say() { printf '%s\n' "$*"; }
fail() { printf '%s\n' "$*" >&2; exit 1; }
cleanup() { [ -z "$fetched" ] || rm -rf "$fetched"; }
trap cleanup EXIT

# The newest Python on this server that is new enough.
find_python() {
    local name
    for name in python3.13 python3.12 python3.11 python3.10 python3.9 python3; do
        if command -v "$name" >/dev/null && "$name" -c 'import sys; raise SystemExit(sys.version_info < (3, 9))' 2>/dev/null; then
            command -v "$name"
            return
        fi
    done
    return 1
}

# Sets source to the folder that holds relay/ and web/: the clone this file is in, a given archive, or a fresh download.
find_source() {
    local file="${BASH_SOURCE[0]:-}" folder=""
    if [ -z "$archive" ] && [ -n "$file" ] && [ -f "$file" ]; then
        folder="$(cd "$(dirname "$file")/.." && pwd)"
        if [ -f "$folder/relay/server.py" ] && [ -f "$folder/web/index.html" ]; then
            source="$folder"
            return
        fi
    fi
    # beside the program, not in /tmp: that is often a small memory disk
    mkdir -p "$root"
    rm -rf "$root"/.fetch.*
    fetched="$(mktemp -d "$root/.fetch.XXXXXX")"
    if [ -z "$archive" ]; then
        archive="$fetched/source.tar.gz"
        say "正在下载 remote-cli…"
        got=""
        for source_url in "${sources[@]}"; do
            if command -v curl >/dev/null; then curl -fsSL --retry 2 -o "$archive" "$source_url"
            else python3 -c 'import sys, urllib.request; urllib.request.urlretrieve(sys.argv[1], sys.argv[2])' "$source_url" "$archive"
            fi && { got=1; break; }
        done
        [ -n "$got" ] || fail "下载失败：${sources[*]}
服务器访问不了 GitHub 时，在别的电脑上下载 https://github.com/KangWang42/remote-ai/archive/refs/heads/main.tar.gz，传到服务器后用 --source 指定它。"
    fi
    [ -f "$archive" ] || fail "找不到源码压缩包：$archive"
    mkdir -p "$fetched/source"
    tar -xzf "$archive" -C "$fetched/source" --strip-components=1 || fail "解不开源码压缩包：$archive"
    [ -f "$fetched/source/relay/server.py" ] && [ -f "$fetched/source/web/index.html" ] || fail "压缩包里没有 relay/ 和 web/：$archive"
    source="$fetched/source"
}

# Nothing on a server where the relay is not installed yet: asking sed for a file that is not there would end the
# script without a word.
# The service runs as a user that systemd makes for it while it runs (DynamicUser). A system that keeps looked-up
# names for a while (nscd) may still know that user's name after the service has stopped; a systemd before 244 then
# takes the name for someone else's and does not start the service ("status=217/USER"), for as long as the name is
# kept, which can be an hour. The names are forgotten here before every start, and by the service whenever it stops.
forget_names() {
    command -v nscd >/dev/null 2>&1 || return 0
    nscd -i passwd >/dev/null 2>&1
    nscd -i group >/dev/null 2>&1
    return 0
}
current_port() { [ -f "$unit" ] || return 0; sed -n 's/.*--port \([0-9][0-9]*\) .*/\1/p' "$unit" | head -1; }

# Named so that it does not stand in for the system's own "install", which it uses.
set_up() {
    [ "$(id -u)" = 0 ] || fail "请用 root 运行（前面加 sudo）。"
    command -v systemctl >/dev/null || fail "这台服务器没有 systemd。可以按 docs/SELF_HOSTING.md 手动运行 relay/server.py。"
    local version python release
    version="$(systemctl --version | sed -n '1s/^systemd \([0-9]*\).*/\1/p')"
    [ "${version:-0}" -ge 235 ] || fail "systemd 版本太旧（需要 235 及以上）。可以按 docs/SELF_HOSTING.md 手动运行 relay/server.py。"
    python="$(find_python)" || fail "需要 Python 3.9 及以上。请先用系统的包管理器安装，例如 dnf install python3.11 或 apt install python3。"
    [ -n "$port" ] || port="$(current_port)"
    [ -n "$port" ] || port=8722
    find_source
    release="$(sed -n 's/^VERSION = "\(.*\)"$/\1/p' "$source/relay/server.py" | head -1)"
    [ -n "$release" ] || fail "读不出中转的版本号。"

    mkdir -p "$root/releases/$release.new"
    cp -r "$source/web" "$root/releases/$release.new/web"
    mkdir "$root/releases/$release.new/relay"
    cp "$source/relay/server.py" "$source/relay/relay.py" "$source/relay/screen.py" "$source/relay/websocket.py" "$root/releases/$release.new/relay/"
    rm -rf "$root/releases/$release"
    mv "$root/releases/$release.new" "$root/releases/$release"
    ln -sfn "$root/releases/$release" "$root/current"
    # the release before this one stays, to go back to; older ones are removed
    ls -1dt "$root"/releases/*/ | tail -n +3 | xargs -r rm -rf

    install -d -m 700 "$settings"
    local fresh=""
    if [ ! -s "$settings/relay.env" ]; then
        fresh=1
        if [ -n "$public" ]; then (umask 077; printf 'RCLI_PUBLIC=1\n' > "$settings/relay.env")
        else (umask 077; printf 'RCLI_PASSWORD=%s\n' "$("$python" -c 'import secrets; print(secrets.token_urlsafe(24))')" > "$settings/relay.env"); fi
    fi
    # What kind of relay it is and its limits are kept beside the password; given again, they replace what was kept.
    if [ -n "$public" ] && ! grep -q '^RCLI_PUBLIC=1$' "$settings/relay.env"; then printf 'RCLI_PUBLIC=1\n' >> "$settings/relay.env"; fi
    if [ -n "$key" ]; then sed -i '/^RCLI_JOIN_KEY=/d' "$settings/relay.env"; printf 'RCLI_JOIN_KEY=%s\n' "$key" >> "$settings/relay.env"; fi
    if [ -n "$spaces" ]; then sed -i '/^RCLI_SPACES=/d' "$settings/relay.env"; printf 'RCLI_SPACES=%s\n' "$spaces" >> "$settings/relay.env"; fi
    chmod 600 "$settings/relay.env"
    cat > "$unit" <<UNIT
[Unit]
Description=Remote AI relay (listens on this machine only; a web server in front supplies https)
After=network.target

[Service]
DynamicUser=yes
StateDirectory=remote-cli
EnvironmentFile=$settings/relay.env
Environment=PYTHONUTF8=1
ExecStart=$python $root/current/relay/server.py --host 127.0.0.1 --port $port --data /var/lib/remote-cli --web $root/current/web
ExecStopPost=+-/bin/sh -c 'command -v nscd >/dev/null 2>&1 && { nscd -i passwd; nscd -i group; }; exit 0'
Restart=on-failure
RestartSec=3
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
UNIT
    systemctl daemon-reload
    systemctl enable "$service" >/dev/null 2>&1
    systemctl stop "$service" >/dev/null 2>&1 || true
    forget_names
    systemctl start "$service"
    local tries
    for tries in 1 2 3 4 5 6 7 8 9 10; do
        if "$python" -c 'import sys, urllib.request; urllib.request.urlopen("http://127.0.0.1:%s/api/session" % sys.argv[1], timeout=2)' "$port" 2>/dev/null; then
            if grep -q '^RCLI_PUBLIC=1$' "$settings/relay.env"; then
                say "公共中转 $release 已在运行，监听 127.0.0.1:$port。它没有统一的密码：每台电脑由电脑端自动申请自己的空间。"
            else
                say "中转 $release 已在运行，监听 127.0.0.1:$port。"
                # The password is shown when it was just made. An update keeps it and does not put it on the screen again.
                if [ -n "$fresh" ]; then say "访问密码：$(sed -n 's/^RCLI_PASSWORD=//p' "$settings/relay.env")"
                else say "访问密码没有变；要查看时运行 install.sh password。"; fi
            fi
            say "下一步：让一个 web 服务器用 https 把请求转给 127.0.0.1:$port，见 docs/SELF_HOSTING.md。"
            return
        fi
        sleep 1
    done
    fail "中转没有启动成功。用 journalctl -u $service -n 30 查看原因。"
}

status() {
    local port
    port="$(current_port)"
    [ -n "$port" ] || fail "中转还没有安装。"
    say "服务：$(systemctl is-active "$service" 2>/dev/null || true)，开机自启：$(systemctl is-enabled "$service" 2>/dev/null || true)"
    say "版本：$(sed -n 's/^VERSION = "\(.*\)"$/\1/p' "$root/current/relay/server.py" 2>/dev/null | head -1)，监听 127.0.0.1:$port"
    if grep -q '^RCLI_PUBLIC=1$' "$settings/relay.env" 2>/dev/null; then
        local folder=/var/lib/private/remote-cli/spaces
        [ -d "$folder" ] || folder=/var/lib/remote-cli/spaces
        say "公共中转：现在有 $(find "$folder" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | wc -l) 台电脑的空间，$(grep -q '^RCLI_JOIN_KEY=.' "$settings/relay.env" && echo 需要口令 || echo 不需要口令)"
    fi
}

password() {
    [ "$(id -u)" = 0 ] || fail "请用 root 运行（前面加 sudo）。"
    [ -s "$settings/relay.env" ] || fail "中转还没有安装。"
    if grep -q '^RCLI_PUBLIC=1$' "$settings/relay.env"; then
        say "公共中转没有统一的密码：每台电脑的密码由它自己的电脑端申请和保管。"
        return
    fi
    if [ -n "$renew" ]; then
        local python
        python="$(find_python)" || fail "需要 Python 3.9 及以上。"
        systemctl stop "$service"
        (umask 077; printf 'RCLI_PASSWORD=%s\n' "$("$python" -c 'import secrets; print(secrets.token_urlsafe(24))')" > "$settings/relay.env")
        # whoever signed in with the old password signs in again
        rm -f /var/lib/remote-cli/sessions.json /var/lib/private/remote-cli/sessions.json
        forget_names
        systemctl start "$service"
        say "已换成新密码。电脑端和手机都要用它重新连接："
    fi
    sed -n 's/^RCLI_PASSWORD=//p' "$settings/relay.env"
}

uninstall() {
    [ "$(id -u)" = 0 ] || fail "请用 root 运行（前面加 sudo）。"
    systemctl disable --now "$service" >/dev/null 2>&1 || true
    rm -f "$unit"
    systemctl daemon-reload
    rm -rf "$root/releases" "$root/current"
    rmdir "$root" 2>/dev/null || true
    if [ -n "$purge" ]; then
        rm -rf "$settings" /var/lib/remote-cli /var/lib/private/remote-cli
        say "中转已移除，密码和终端记录也已删除。"
    else
        say "中转已移除。密码（$settings）和终端记录（/var/lib/remote-cli）保留着，加 --purge 一并删除。"
    fi
}

action=install
while [ $# -gt 0 ]; do
    case "$1" in
        status|password|update|uninstall) action="$1" ;;
        --port) port="${2:-}"; shift; case "$port" in ''|*[!0-9]*) fail "--port 后面要跟端口号。" ;; esac ;;
        --source) archive="${2:-}"; shift; [ -n "$archive" ] || fail "--source 后面要跟压缩包的路径。" ;;
        --purge) purge=1 ;;
        --new) renew=1 ;;
        --public) public=1 ;;
        --key) key="${2:-}"; shift; case "$key" in ''|*[!A-Za-z0-9._-]*) fail "--key 后面要跟口令，只用字母、数字和 . _ -。" ;; esac ;;
        --spaces) spaces="${2:-}"; shift; case "$spaces" in ''|*[!0-9]*) fail "--spaces 后面要跟数量。" ;; esac ;;
        -h|--help) sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) fail "不认识的参数：$1" ;;
    esac
    shift
done
case "$action" in
    install|update) set_up ;;
    status) status ;;
    password) password ;;
    uninstall) uninstall ;;
esac
