#!/usr/bin/env python3
"""remote-cli agent for Linux: runs this computer's terminals for the phone.

    python3 agent.py                              # data in ~/.local/state/remote-cli-agent
    python3 agent.py --data /var/lib/remote-cli-agent
    python3 agent.py --set-password [folder]      # store the relay's password, read from stdin
    python3 agent.py --pair [--data folder]       # show the address, password and QR for the phone

It signs in to the relay (relay/server.py) the same way the phone does, reports terminal
state and output a few times a second, and carries out the operations the phone sends.
Only the standard library is used. Terminal input and output never reach a log.

Configuration lives in <data>/config.json:
    {"Server": "https://relay.example.com", "RemoteEnabled": true,
     "Name": "ubuntu-server", "Shell": "/bin/bash", "RemoteMaxMode": "",
     "RemoteDirs": ["演示=/home/you/演示"]}
RemoteDirs holds the folders a terminal may be started in; folders added from the phone
are kept beside them in <data>/terminal-projects.json. RemoteMaxMode limits what Claude
Code and Codex may do when started from the phone: "read", "edit" or empty for their own
default. PairUrl is the address the phone uses for the relay when it is not Server, as when
the relay runs on this computer and Server is http://127.0.0.1:8722.

Besides a shell, the phone can start Claude Code and Codex when they are installed for
this user, continue their saved conversations, and look at the files of a project.
"""
import base64
import codecs
import fcntl
import glob
import http.client
import json
import os
import pty
import re
import secrets
import select
import signal
import socket
import stat
import struct
import sys
import termios
import threading
import time
import uuid
from urllib.parse import quote, urlsplit

VERSION = "0.5.0"
ID_RE = re.compile(r"\A[a-f0-9]{16,32}\Z")
TERMINAL_RE = re.compile(r"\A[a-f0-9]{32}\Z")
SERVER_RE = re.compile(r"\Ahttps?://[^/\s]+\Z")
UUID_RE = re.compile(r"\A[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\Z")
PROJECT_ACTIONS = ("project_add", "project_remove", "project_rename")
FILE_ACTIONS = ("file_list", "file_read")
FILE_PIECE = 737280     # bytes of a file in one answer, as the Windows agent cuts them
FILE_ENTRIES = 3000     # entries of a folder in one answer
FEATURES = ["terminal-exit", "files", "peek", "marks"]
MARKS = ("pinned", "hidden")     # what the owner says of single conversations from the phone
MAX_MARKS = 300                 # of each kind; with more, the ones marked longest ago give way
SAID_MESSAGES = 40      # of a conversation, the last this many things said are shown on the phone
SAID_CHARS = 1500       # and of each, this much
# where the tools' own installers put them; a service's PATH seldom has these
TOOL_DIRS = (".local/bin", ".npm-global/bin", "bin", ".bun/bin", ".volta/bin", ".cargo/bin")
MAX_TERMINALS = 8
MAX_OWN_PROJECTS = 30
OP_TTL = 150            # seconds an operation may travel before it is refused
CHUNK_CHARS = 12000
OUTPUT_FLOOR_BYTES = 2 * 1024 * 1024  # unsent output is dropped past this; the relay dedups by seq
WRITE_WAIT = 1.0        # seconds input may wait for a program that is not reading
BATCH_BYTES = 512 * 1024
BATCH_CHUNKS = 150
STRIP_ENV = ("CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT")


class OpError(Exception):
    """A request the agent refuses; its text is shown to the person at the phone."""


def say(message):
    print(time.strftime("[%H:%M:%S] ") + message, file=sys.stderr, flush=True)


def write_private(path, text):
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w", encoding="utf-8") as stream:
        stream.write(text)


def read_password(data):
    """The relay's password, from RCLI_PASSWORD or <data>/password.txt. Never made up here:
    it has to match the relay, which makes its own when it starts."""
    given = os.environ.get("RCLI_PASSWORD", "").strip()
    if given:
        return given
    try:
        with open(os.path.join(data, "password.txt"), encoding="utf-8") as stream:
            return stream.read().strip()
    except OSError:
        return ""


def read_config(data):
    try:
        with open(os.path.join(data, "config.json"), encoding="utf-8") as stream:
            cfg = json.load(stream)
        return cfg if isinstance(cfg, dict) else {}
    except (OSError, ValueError):
        return {}


def tidy(text, limit=60):
    text = re.sub(r"<[^>]{1,40}>", " ", text or "")
    text = re.sub(r"[\x00-\x1f\x7f-\x9f]+", " ", text)
    text = " ".join(text.split())
    if len(text) > limit:
        text = text[:limit - 1] + "…"
    return text


def normal_folder(path):
    """A folder on this computer, as the phone may type it. POSIX paths, unlike the
    Windows agent's drive-letter rule."""
    path = (path or "").strip()
    if len(path) < 2 or len(path) > 240 or "*" in path or "?" in path or "\x00" in path \
            or any(ord(c) < 32 for c in path) or not path.startswith("/"):
        raise OpError("请填写电脑上的完整文件夹路径，例如 /home/you/demo")
    return os.path.normpath(path)


def parse_dirs(cfg):
    """Project folders from config.json, as name -> existing absolute path."""
    raw = cfg.get("RemoteDirs")
    out = {}
    for item in (raw if isinstance(raw, list) else [])[:60]:
        if isinstance(item, str):
            at = item.find("=")
            if at <= 0:
                continue
            name, path = item[:at].strip(), item[at + 1:].strip()
        elif isinstance(item, dict):
            name, path = str(item.get("name") or "").strip(), str(item.get("path") or "").strip()
        else:
            continue
        if not name or len(name) > 60 or len(path) > 260 or not path.startswith("/"):
            continue
        path = os.path.normpath(path)
        if os.path.isdir(path):
            out[name] = path
    return out


def find_tool(name):
    """The program `claude` or `codex` of this user, or None."""
    home = os.path.expanduser("~")
    folders = [f for f in os.environ.get("PATH", "").split(os.pathsep) if f]
    folders += [os.path.join(home, f) for f in TOOL_DIRS] + ["/usr/local/bin"]
    folders += sorted(glob.glob(os.path.join(home, ".nvm", "versions", "node", "*", "bin")), reverse=True)
    for folder in folders:
        path = os.path.join(folder, name)
        if os.path.isfile(path) and os.access(path, os.X_OK):
            return path
    return None


def tool_argv(tool, launcher, session, history, mode, fresh):
    """The command line of a tool, the same choices as the Windows agent makes."""
    if tool == "claude":
        args = ["--session-id" if fresh else "--resume", session] if session else ["--resume"] if history else []
        args += ["--permission-mode", "plan"] if mode == "read" else \
            ["--permission-mode", "acceptEdits"] if mode == "edit" else []
    else:
        args = ["resume", session] if session else ["resume", "--include-non-interactive"] if history else []
        args += ["-s", "read-only"] if mode == "read" else ["-s", "workspace-write"] if mode == "edit" else []
    return [launcher] + args


def files(root, relative, action, offset):
    """What a project folder holds, or a piece of one of its files. Nothing outside the
    project's own folder is given out: a link inside it that leads elsewhere is refused."""
    root = os.path.normpath(root)
    relative = str(relative or "").replace("\\", "/").strip("/")
    full = os.path.normpath(os.path.join(root, relative)) if relative else root
    inside = root.rstrip("/") + "/"
    if full != root and not full.startswith(inside):
        raise OpError("路径不在项目文件夹内")
    real_root, real = os.path.realpath(root), os.path.realpath(full)
    if real != real_root and not real.startswith(real_root.rstrip("/") + "/"):
        raise OpError("这个位置链接到项目文件夹之外，不能打开")
    shown = full[len(inside):] if full != root else ""
    try:
        if action == "file_list":
            if not os.path.isdir(full):
                raise OpError("这不是文件夹" if os.path.exists(full) else "文件夹不存在")
            entries, more = [], False
            with os.scandir(full) as listing:
                for item in listing:
                    if len(entries) >= FILE_ENTRIES:
                        more = True
                        break
                    try:
                        info = item.stat()
                    except OSError:             # a link that leads nowhere is still an entry
                        info = item.stat(follow_symlinks=False)
                    inner = stat.S_ISDIR(info.st_mode)
                    entries.append({"name": item.name, "dir": inner, "size": 0 if inner else info.st_size,
                                    "modified": int(info.st_mtime * 1000), "hidden": item.name.startswith(".")})
            return {"path": shown, "entries": entries, "more": more}
        if os.path.isdir(full):
            raise OpError("这是文件夹")
        # opened without waiting, so a pipe or a device left in a project cannot hold the agent
        fd = os.open(full, os.O_RDONLY | os.O_NONBLOCK)
        with os.fdopen(fd, "rb") as stream:
            info = os.fstat(fd)
            if not stat.S_ISREG(info.st_mode):
                raise OpError("这不是普通文件，不能打开")
            if isinstance(offset, bool) or not isinstance(offset, int) or offset < 0 or offset > info.st_size:
                raise OpError("位置无效")
            stream.seek(offset)
            data = stream.read(min(FILE_PIECE, info.st_size - offset))
        return {"path": shown, "size": info.st_size, "modified": int(info.st_mtime * 1000), "offset": offset,
                "data": base64.b64encode(data).decode("ascii"), "end": offset + len(data) >= info.st_size}
    except FileNotFoundError:
        raise OpError("文件不存在")
    except PermissionError:
        raise OpError("没有权限读取")
    except OSError:
        raise OpError("读取失败")


def read_part(path, tail, limit):
    """The beginning or the end of a file, as text, cut at whole lines where it was cut."""
    with open(path, "rb") as stream:
        size = os.fstat(stream.fileno()).st_size
        if tail and size > limit:
            stream.seek(size - limit)
            data = stream.read(limit)
            data = data[data.find(b"\n") + 1:]
        else:
            data = stream.read(limit)
            if size > limit:
                data = data[:data.rfind(b"\n") + 1]
    return data.decode("utf-8", "replace")


def rows(text):
    for line in text.split("\n"):
        if line.startswith("{"):
            try:
                row = json.loads(line)
            except ValueError:
                continue
            if isinstance(row, dict):
                yield row


def claude_title(path):
    """The name Claude Code shows for a conversation; without one, what was asked last,
    else what it began with."""
    custom = named = last = ""
    for row in rows(read_part(path, True, 256 * 1024)):
        custom = row.get("customTitle") if isinstance(row.get("customTitle"), str) else custom
        named = row.get("aiTitle") if isinstance(row.get("aiTitle"), str) else named
        last = row.get("lastPrompt") if isinstance(row.get("lastPrompt"), str) else last
    if tidy(custom or named or last):
        return tidy(custom or named or last)
    for row in rows(read_part(path, False, 768 * 1024)):
        message = row.get("message")
        if row.get("type") != "user" or row.get("isSidechain") or row.get("isMeta") or not isinstance(message, dict):
            continue
        content = message.get("content")
        if isinstance(content, list):
            content = next((p.get("text") for p in content if isinstance(p, dict) and p.get("type") == "text"), "")
        if isinstance(content, str) and tidy(content):
            return tidy(content)
    return ""


def said(path, tool):
    """The last things said in a saved conversation, oldest first: what the person asked, what
    the tool answered, and the name of each thing it did. What the tools put into a
    conversation for themselves (reminders, command output, results) is left out."""
    limit = 768 * 1024
    out = []

    def add(role, text):
        text = " ".join(text.split()) if role == "tool" else text.strip()
        if text:
            out.append({"role": role, "text": text if len(text) <= SAID_CHARS else text[:SAID_CHARS - 1] + "…"})

    def brief(name, given):
        given = given if isinstance(given, dict) else {}
        about = next((given[k] for k in ("command", "file_path", "path", "pattern", "description", "query", "url")
                      if isinstance(given.get(k), str)), "")
        return (str(name or "") + ("：" + about[:160] if about else "")).strip()

    for row in rows(read_part(path, True, limit)):
        if tool == "claude":
            message = row.get("message")
            if row.get("type") not in ("user", "assistant") or row.get("isSidechain") or row.get("isMeta") \
                    or not isinstance(message, dict):
                continue
            content = message.get("content")
            parts = [{"type": "text", "text": content}] if isinstance(content, str) else content if isinstance(content, list) else []
            for part in parts:
                if not isinstance(part, dict):
                    continue
                if part.get("type") == "text" and isinstance(part.get("text"), str):
                    if row["type"] == "assistant" or not part["text"].lstrip().startswith("<"):
                        add(row["type"], part["text"])
                elif part.get("type") == "tool_use" and row["type"] == "assistant":
                    add("tool", brief(part.get("name"), part.get("input")))
        else:
            payload = row.get("payload")
            if row.get("type") != "response_item" or not isinstance(payload, dict):
                continue
            if payload.get("type") == "message" and payload.get("role") in ("user", "assistant"):
                for part in payload.get("content") if isinstance(payload.get("content"), list) else []:
                    if isinstance(part, dict) and isinstance(part.get("text"), str) \
                            and (payload["role"] == "assistant" or not part["text"].lstrip().startswith("<")):
                        add(payload["role"], part["text"])
            elif payload.get("type") in ("function_call", "custom_tool_call", "local_shell_call"):
                add("tool", brief(payload.get("name") or "shell", None))
    more = len(out) > SAID_MESSAGES or os.path.getsize(path) > limit
    return {"messages": out[-SAID_MESSAGES:], "more": more, "updated": int(os.path.getmtime(path) * 1000)}


def parent_of(pid):
    try:
        with open("/proc/%d/stat" % pid, encoding="utf-8", errors="replace") as stream:
            return int(stream.read().rsplit(")", 1)[1].split()[1])
    except (OSError, ValueError, IndexError):
        return 0


def owner_of(pid, terminals):
    """The terminal of ours a process runs in, following its parents; None for a process
    that someone started elsewhere on this computer."""
    mine = {live.pid: live for live in terminals if not live.closed}
    for _ in range(16):
        if pid in mine:
            return mine[pid]
        pid = parent_of(pid)
        if pid <= 1:
            return None
    return None


class Marks:
    """What the owner has said of single conversations from the phone: a pinned one stands
    first in the phone's lists, a hidden one is left out of them. Kept in one small file of
    the data folder, by tool and conversation, so that every phone says the same."""

    def __init__(self, data):
        self.file = os.path.join(data, "conversation-marks.json")
        self.kept = {kind: [] for kind in MARKS}
        try:
            with open(self.file, encoding="utf-8") as stream:
                read = json.load(stream)
            for kind in MARKS:
                items = read.get(kind) if isinstance(read, dict) else None
                for key in items if isinstance(items, list) else []:
                    if isinstance(key, str) and 0 < len(key) <= 80 and key not in self.kept[kind]:
                        self.kept[kind].append(key)
                del self.kept[kind][MAX_MARKS:]
        except (OSError, ValueError):
            pass                        # a file that cannot be read marks nothing

    def has(self, kind, tool, session):
        return "%s:%s" % (tool, session) in self.kept[kind]

    def set(self, kind, tool, session, on):
        """Says that a conversation is of a kind, or no longer is; whether that changed anything."""
        if kind not in MARKS:
            raise OpError("没有这种标记")
        keys, key = self.kept[kind], "%s:%s" % (tool, session)
        if (key in keys) == on:
            return False
        if on:
            keys.append(key)
            del keys[:-MAX_MARKS]
        else:
            keys.remove(key)
        write_private(self.file + ".tmp", json.dumps(self.kept, ensure_ascii=False))
        os.replace(self.file + ".tmp", self.file)
        return True


def elsewhere(folder):
    """Whether a folder that is no project is spoken of to the phone: a full path, and not one
    of the system's own folders for passing things."""
    return isinstance(folder, str) and folder.startswith("/") and 1 < len(folder) <= 240 \
        and not any(ord(c) < 32 for c in folder) \
        and not any(folder == temp or folder.startswith(temp + "/") for temp in ("/tmp", "/var/tmp", "/dev/shm"))


class Sessions:
    """Conversations Claude Code and Codex saved on this computer for the projects, and
    which of them a program has open right now. A conversation that is open outside our
    terminals is listed as taken, so the phone does not open it a second time. One that a
    program has open in a folder that is no project is listed too, without a project
    (dir "") and with its folder: the phone can look at it, and make the folder a project."""

    def __init__(self, marks=None):
        self.marks = marks
        self.home = os.path.expanduser("~")
        self.titles = {}                # path -> (mtime, size, title)
        self.folders = {}               # codex rollout path -> the folder it worked in
        self.names = (None, {})         # codex's own index of names, by its mtime
        self.found = []

    def claude_home(self):
        """Where Claude Code keeps its conversations: its folder in the person's home, or the
        one CLAUDE_CONFIG_DIR names, as Claude Code itself reads it."""
        return os.environ.get("CLAUDE_CONFIG_DIR") or os.path.join(self.home, ".claude")

    def known(self, session, tool, name):
        return next((s for s in self.found if s["id"] == session and s["tool"] == tool and s["dir"] == name), None)

    def read(self, session, tool, name, dirs):
        """What was last said in a conversation of a project, for the phone to look at."""
        saved = self.known(session, tool, name) if UUID_RE.match(session) else None
        # one that is open in a folder that is no project is read where it is
        folder = dirs.get(name) if name else saved and saved["live"] and saved["folder"]
        if saved is None or not folder:
            raise OpError("电脑上没有找到这个对话，请刷新后重试")
        if tool == "claude":
            found = [os.path.join(self.claude_home(), "projects", re.sub(r"[^a-zA-Z0-9]", "-", folder), session + ".jsonl")]
        else:
            home = os.environ.get("CODEX_HOME") or os.path.join(self.home, ".codex")
            found = glob.glob(os.path.join(home, "sessions", "**", "rollout-*%s.jsonl" % session), recursive=True)
        try:
            return dict(said(found[0], tool), title=saved["title"])
        except (OSError, IndexError):
            raise OpError("读不到这个对话的内容")

    def scan(self, dirs, terminals):
        found = []
        try:
            found += self._claude(dirs, terminals)
        except OSError:
            pass
        try:
            found += self._codex(dirs, terminals)
        except OSError:
            pass
        for entry in found:
            for kind in MARKS:
                entry[kind] = bool(self.marks and self.marks.has(kind, entry["tool"], entry["id"]))
        # the ones a program has open and the ones their owner pinned are listed whatever their age
        found.sort(key=lambda entry: (not (entry["live"] or entry["pinned"]), -entry["updated"]))
        self.found = found
        return found

    def _title(self, path, info):
        cached = self.titles.get(path)
        if cached and cached[:2] == (info.st_mtime, info.st_size):
            return cached[2]
        try:
            title = claude_title(path)
        except OSError:
            return cached[2] if cached else ""
        if len(self.titles) > 2000:
            self.titles.clear()
        self.titles[path] = (info.st_mtime, info.st_size, title)
        return title

    def _claude_open(self, terminals):
        """Conversations Claude Code has open in a program that is not one of our terminals,
        as conversation -> the folder it works in."""
        taken = {}
        for path in glob.glob(os.path.join(self.claude_home(), "sessions", "*.json")):
            try:
                with open(path, encoding="utf-8") as stream:
                    record = json.load(stream)
                pid, session = record.get("pid"), record.get("sessionId")
                if isinstance(pid, bool) or not isinstance(pid, int) or not isinstance(session, str) \
                        or not UUID_RE.match(session):
                    continue
                with open("/proc/%d/comm" % pid, encoding="utf-8", errors="replace") as stream:
                    if stream.read().strip() not in ("claude", "node"):
                        continue
            except (OSError, ValueError, AttributeError):
                continue                # the program has left; its note stayed behind
            owner = owner_of(pid, terminals)
            if owner is None:
                taken[session] = str(record.get("cwd") or "")
            elif owner.tool == "claude":
                owner.session = session
                owner.status = record.get("status") if record.get("status") in ("idle", "busy") else ""
        return taken

    def _claude(self, dirs, terminals):
        taken = self._claude_open(terminals)
        found = []
        for name, folder in dirs.items():
            saved = os.path.join(self.claude_home(), "projects", re.sub(r"[^a-zA-Z0-9]", "-", folder))
            recent = []
            for path in glob.glob(os.path.join(saved, "*.jsonl")):
                try:
                    recent.append((os.stat(path), path))
                except OSError:
                    pass
            recent.sort(key=lambda item: item[0].st_mtime, reverse=True)
            for at, (info, path) in enumerate(recent):
                session = os.path.basename(path)[:-6]
                if not UUID_RE.match(session) or (at >= 15 and session not in taken):
                    continue
                title = self._title(path, info)
                if title:
                    found.append(self._entry(session, "claude", name, title, info, session in taken))
        listed = {entry["id"] for entry in found}
        for session, folder in taken.items():
            folder = os.path.normpath(folder) if folder else ""
            if session in listed or not elsewhere(folder) or folder in dirs.values():
                continue
            path = os.path.join(self.claude_home(), "projects", re.sub(r"[^a-zA-Z0-9]", "-", folder), session + ".jsonl")
            try:
                info = os.stat(path)
            except OSError:
                continue                # nothing has been said in it yet
            title = self._title(path, info)
            if title:
                found.append(self._entry(session, "claude", "", title, info, True, folder))
        return found

    def _codex(self, dirs, terminals):
        home = os.environ.get("CODEX_HOME") or os.path.join(self.home, ".codex")
        index = os.path.join(home, "session_index.jsonl")
        try:
            stamp = os.stat(index).st_mtime
            if stamp != self.names[0]:
                self.names = (stamp, {row["id"]: row.get("thread_name") for row in rows(read_part(index, True, 2 << 20))
                                      if isinstance(row.get("id"), str)})
        except OSError:
            pass
        root = os.path.join(home, "sessions")
        open_now = self._codex_open(root)
        recent = []
        for path in glob.glob(os.path.join(root, "**", "rollout-*.jsonl"), recursive=True):
            try:
                recent.append((os.stat(path), path))
            except OSError:
                pass
        recent.sort(key=lambda item: item[0].st_mtime, reverse=True)
        found, counts = [], {}
        for info, path in recent[:150]:
            session = os.path.basename(path)[:-6][-36:]
            if not UUID_RE.match(session):
                continue
            if path not in self.folders:
                if len(self.folders) > 3000:
                    self.folders.clear()
                try:
                    head = read_part(path, False, 16 * 1024)
                except OSError:
                    continue
                first = next(rows(head), {})
                payload = first.get("payload") if isinstance(first.get("payload"), dict) else {}
                # a helper that a conversation started for itself is part of that conversation
                self.folders[path] = "" if '"parent_thread_id"' in head else str(payload.get("cwd") or "")
            folder = self.folders[path]
            name = next((n for n, p in dirs.items() if folder and os.path.normpath(folder) == p), None)
            pid = open_now.get(path)
            owner = owner_of(pid, terminals) if pid else None
            live = pid is not None and owner is None
            if name is None:
                # one that a program has open in a folder that is no project is listed with its folder
                if live and elsewhere(os.path.normpath(folder) if folder else ""):
                    title = tidy(str(self.names[1].get(session) or "")) \
                        or "Codex 对话 " + time.strftime("%m-%d %H:%M", time.localtime(info.st_mtime))
                    found.append(self._entry(session, "codex", "", title, info, True, os.path.normpath(folder)))
                continue
            if owner is not None and owner.tool == "codex" and not owner.session:
                owner.session = session
            if not live and counts.get(name, 0) >= 15:
                continue
            if not live:
                counts[name] = counts.get(name, 0) + 1
            title = tidy(str(self.names[1].get(session) or "")) \
                or "Codex 对话 " + time.strftime("%m-%d %H:%M", time.localtime(info.st_mtime))
            found.append(self._entry(session, "codex", name, title, info, live))
        return found

    def _codex_open(self, root):
        """Conversation files a Codex of this user holds open, as path -> process."""
        held = {}
        for comm in glob.glob("/proc/[0-9]*/comm"):
            try:
                with open(comm, encoding="utf-8", errors="replace") as stream:
                    if not stream.read().strip().startswith(("codex", "node")):
                        continue
                folder = os.path.dirname(comm)
                for fd in os.listdir(os.path.join(folder, "fd")):
                    target = os.readlink(os.path.join(folder, "fd", fd))
                    if target.startswith(root + "/") and target.endswith(".jsonl"):
                        held[target] = int(os.path.basename(folder))
            except (OSError, ValueError):
                continue                # another user's process, or one that has just left
        return held

    @staticmethod
    def _entry(session, tool, name, title, info, live, folder=""):
        return {"id": session, "tool": tool, "dir": name, "folder": folder, "title": title, "updated": int(info.st_mtime * 1000),
                "live": live, "host": "cli" if live else "", "status": "", "can_takeover": False,
                "ownership_known": True, "takeover_reason": "请先在电脑上结束正在使用这个对话的程序" if live else ""}


def check_op(op, need_terminal=True):
    """Shape and freshness of an operation, before anything is carried out."""
    op_id = op.get("id")
    terminal = op.get("terminal")
    if not isinstance(op_id, str) or not ID_RE.match(op_id):
        raise OpError("操作编号无效")
    if need_terminal and (not isinstance(terminal, str) or not TERMINAL_RE.match(terminal)):
        raise OpError("操作编号无效")
    at = op.get("at")
    if isinstance(at, str):
        try:
            at = float(at)
        except ValueError:
            at = None
    if isinstance(at, bool) or not isinstance(at, (int, float)) or abs(time.time() - at) > OP_TTL:
        raise OpError("操作已过期")
    return op_id, terminal if isinstance(terminal, str) else ""


class Http:
    """One persistent connection, used by one thread. The report loop and the held pull
    each have their own, so a held request never blocks a report."""

    def __init__(self):
        self.lock = threading.Lock()
        self.conn = None
        self.key = None

    def close(self):
        conn, self.conn = self.conn, None
        if conn is not None:
            try:
                conn.close()
            except OSError:
                pass

    def post(self, url, payload, token="", timeout=20):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        parts = urlsplit(url)
        key = (parts.scheme, parts.netloc.lower())
        path = parts.path or "/"
        if parts.query:
            path += "?" + parts.query
        headers = {"Content-Type": "application/json", "Accept": "application/json"}
        if token:
            headers["Authorization"] = "Bearer " + token
        with self.lock:
            last = None
            for attempt in range(2):        # a fresh connection once, after a dropped one
                if self.conn is not None and key != self.key:
                    self.close()
                self.key = key
                if self.conn is None:
                    if parts.scheme == "https":
                        self.conn = http.client.HTTPSConnection(parts.hostname, parts.port or 443, timeout=timeout)
                    else:
                        self.conn = http.client.HTTPConnection(parts.hostname, parts.port or 80, timeout=timeout)
                self.conn.timeout = timeout
                try:
                    self.conn.request("POST", path, body=body, headers=headers)
                    response = self.conn.getresponse()
                    return response.status, response.read()
                except (http.client.HTTPException, OSError) as error:
                    last = error
                    self.close()
            raise last


class Live:
    """One terminal: the child process, its pty, and the output waiting to be numbered."""

    def __init__(self, terminal_id, project, cwd, cols, rows, argv, wake, tool="shell", session=""):
        self.id = terminal_id
        self.project = project          # the project's name, which a rename follows
        self.dir = cwd                  # its folder on this computer
        self.tool = tool
        self.session = session          # the conversation a tool shows, once it is known
        self.status = ""                # busy or idle, where the tool says so itself
        self.wake = wake
        self.lock = threading.Lock()
        self.pending = []               # read from the program, not yet numbered
        self.output = []                # numbered, not yet acknowledged by the relay
        self.out_bytes = 0              # bytes in `output`, maintained by seal/trim
        self.seq = 0
        self.closing_at = None          # TERM sent; KILL follows if this lapses three seconds
        self.closed = False             # the process has been reaped
        self.drained = False            # the reader delivered everything
        self.exit_code = None
        self.closed_at = None
        self.told_closed = False        # the relay has accepted a report that said it ended
        self.decoder = codecs.getincrementaldecoder("utf-8")(errors="replace")
        pid, fd = pty.fork()
        if pid == 0:                    # child: pty.fork already made it a session leader
            try:
                os.chdir(cwd)
                env = dict(os.environ)
                for key in STRIP_ENV:
                    env.pop(key, None)
                env["TERM"] = "xterm-256color"
                env["COLORTERM"] = "truecolor"
                if not any("utf-8" in env.get(k, "").lower() for k in ("LANG", "LC_ALL")):
                    env["LANG"] = "C.UTF-8"  # 中文输入编辑需要 UTF-8 locale；systemd 服务常无 LANG
                beside = os.path.dirname(argv[0])   # a tool finds its own runtime (node) next to itself
                if tool != "shell" and beside not in env.get("PATH", "").split(os.pathsep):
                    env["PATH"] = beside + os.pathsep + env.get("PATH", "/usr/local/bin:/usr/bin:/bin")
                os.execvpe(argv[0], argv, env)
            except BaseException:
                os._exit(127)
        self.pid = pid
        self.fd = fd
        os.set_blocking(fd, False)      # input never waits on a program that is not reading
        self.resize(cols, rows)         # the kernel sends SIGWINCH to the foreground group
        threading.Thread(target=self._read, daemon=True).start()
        threading.Thread(target=self._wait, daemon=True).start()

    def _read(self):
        try:
            while True:
                select.select([self.fd], [], [])
                try:
                    data = os.read(self.fd, 8192)
                except BlockingIOError:
                    continue
                if not data:
                    break
                text = self.decoder.decode(data)
                if text:
                    with self.lock:
                        self.pending.append(text)
                    self.wake()
        except (OSError, ValueError):
            pass                        # the terminal was closed or the program left
        finally:
            tail = self.decoder.decode(b"", final=True)
            with self.lock:
                if tail:
                    self.pending.append(tail)
                self.drained = True
            self.wake()

    def _wait(self):
        try:
            _, status = os.waitpid(self.pid, 0)
        except (ChildProcessError, OSError):
            status = 0
        if os.WIFSIGNALED(status):
            code = 128 + os.WTERMSIG(status)
        elif os.WIFEXITED(status):
            code = os.WEXITSTATUS(status)
        else:
            code = 1
        with self.lock:
            self.exit_code = code
            self.closed = True
            self.closed_at = time.time()
        self.wake()

    def write(self, text):
        # The pty does not block: a program that has stopped reading would otherwise hold
        # this write, and with it every other terminal, for as long as it liked. Each piece
        # is written under the lock, so the fd cannot be closed and reused in between.
        data = memoryview(text.encode("utf-8"))
        deadline = time.monotonic() + WRITE_WAIT
        while data:
            with self.lock:
                fd = self.fd
                if fd < 0:
                    raise OpError("终端已结束")
                try:
                    data = data[os.write(fd, data):]
                    continue
                except BlockingIOError:
                    pass
                except OSError:
                    raise OpError("终端已结束")
            left = deadline - time.monotonic()
            if left <= 0:
                raise OpError("终端里的程序暂时没有读取输入，请稍后再试")
            try:
                select.select([], [fd], [], left)
            except (OSError, ValueError):
                raise OpError("终端已结束")

    def resize(self, cols, rows):
        try:
            fcntl.ioctl(self.fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))
        except OSError:
            pass
        self.cols, self.rows = cols, rows

    def begin_close(self):
        """Sends TERM now; the report loop escalates to KILL later, outside the state lock."""
        try:
            os.killpg(self.pid, signal.SIGTERM)
        except (ProcessLookupError, PermissionError, OSError):
            pass
        if self.closing_at is None:
            self.closing_at = time.time()

    def escalate(self):
        if self.closed or self.fd < 0:
            return
        if self.closing_at is None or time.time() - self.closing_at <= 3:
            return
        try:
            os.killpg(self.pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError, OSError):
            pass

    def kill(self):
        try:
            os.killpg(self.pid, signal.SIGTERM)
        except (ProcessLookupError, PermissionError, OSError):
            pass
        deadline = time.time() + 3
        while time.time() < deadline and not self.closed:
            time.sleep(0.05)
        if not self.closed:
            try:
                os.killpg(self.pid, signal.SIGKILL)
            except (ProcessLookupError, PermissionError, OSError):
                pass

    def trim(self, upto):
        with self.lock:
            kept = []
            for chunk in self.output:
                if chunk["seq"] > upto:
                    kept.append(chunk)
                else:
                    self.out_bytes -= len(chunk["data"])
            self.output = kept

    def can_forget(self):
        with self.lock:
            return (self.closed and self.told_closed and not self.pending and not self.output
                    and (self.drained or (self.closed_at is not None and time.time() - self.closed_at > 15)))

    def dispose(self):
        if self.fd >= 0:
            try:
                os.close(self.fd)
            except OSError:
                pass
            self.fd = -1


def seal(live):
    """Turns pending text into numbered pieces, so a busy screen is not held back by the
    per-request piece limit. Only the report thread calls this. Pieces the relay has not
    confirmed are dropped once they outgrow OUTPUT_FLOOR_BYTES: the relay deduplicates by
    seq, so a reader that asks past dropped output gets its reset flag."""
    with live.lock:
        text = "".join(live.pending)
        live.pending = []
        while text:
            live.seq += 1
            chunk = text[:CHUNK_CHARS]
            live.out_bytes += len(chunk)
            live.output.append({"terminal": live.id, "seq": live.seq, "data": chunk})
            text = text[CHUNK_CHARS:]
        while live.out_bytes > OUTPUT_FLOOR_BYTES and len(live.output) > 1:
            live.out_bytes -= len(live.output[0]["data"])
            del live.output[0]


def output_batch(lives):
    """A report carries at most 150 pieces or about half a megabyte, whichever comes first."""
    batch, size = [], 0
    for live in lives:
        with live.lock:
            chunks = live.output[:30]
        for chunk in chunks:
            encoded = len(json.dumps(chunk, ensure_ascii=False).encode("utf-8"))
            if batch and size + encoded > BATCH_BYTES:
                return batch
            batch.append(chunk)
            size += encoded
            if len(batch) >= BATCH_CHUNKS:
                return batch
    return batch


class Projects:
    """Folders a terminal may start in: the config's own list, plus folders added from the phone."""

    def __init__(self, data):
        self.file = os.path.join(data, "terminal-projects.json")
        self.own = self._load()

    def _load(self):
        try:
            with open(self.file, encoding="utf-8") as stream:
                items = json.load(stream)
            return [{"name": str(i["name"]), "path": str(i["path"])} for i in items
                    if isinstance(i, dict) and i.get("name") and i.get("path")]
        except (OSError, ValueError, KeyError, TypeError):
            return []

    def _save(self):
        write_private(self.file + ".tmp", json.dumps(self.own, ensure_ascii=False))
        os.replace(self.file + ".tmp", self.file)

    def all(self, cfg):
        dirs = parse_dirs(cfg)
        for item in self.own:
            if item["name"] not in dirs:
                path = os.path.normpath(item["path"])
                if path.startswith("/") and os.path.isdir(path):
                    dirs[item["name"]] = path
        return dirs

    def entries(self, cfg, dirs):
        fixed = parse_dirs(cfg)
        out = [{"name": n, "path": p, "fixed": True, "exists": True} for n, p in fixed.items()]
        out += [{"name": i["name"], "path": i["path"], "fixed": False, "exists": os.path.isdir(i["path"])}
                for i in self.own if i["name"] not in fixed]
        return out

    def change(self, op, action, cfg, terminals):
        fixed = parse_dirs(cfg)
        name = tidy(str(op.get("name") or ""), 40)
        if action == "project_add":
            path = normal_folder(str(op.get("path") or ""))
            if any(existing == path for existing in fixed.values()) \
                    or any(existing["path"] == path for existing in self.own):
                raise OpError("这个文件夹已经在项目里")
            if len(self.own) >= MAX_OWN_PROJECTS:
                raise OpError("项目已达 %d 个，请先移除不用的" % MAX_OWN_PROJECTS)
            if not os.path.isdir(path):
                if op.get("create") is not True:
                    raise OpError("电脑上没有这个文件夹")
                parent = os.path.dirname(path.rstrip("/"))
                if not parent or not os.path.isdir(parent):
                    raise OpError("上一级文件夹不存在，不能新建")
                os.mkdir(path, 0o755)
            if not name:
                name = tidy(os.path.basename(path.rstrip("/")) or path, 40)
            wanted, n = name, 1
            while name in fixed or any(i["name"] == name for i in self.own):
                n += 1
                name = "%s %d" % (wanted, n)
            self.own.append({"name": name, "path": path})
        else:
            at = next((i for i, item in enumerate(self.own) if item["name"] == name), -1)
            if at < 0:
                raise OpError("这个项目写在电脑的配置文件里，请在电脑上修改"
                              if name in fixed else "没有这个项目")
            if action == "project_remove":
                if any(t.project == name and not t.closed for t in terminals.values()):
                    raise OpError("这个项目还有终端在运行，请先结束")
                del self.own[at]
            else:
                to = tidy(str(op.get("to") or ""), 40)
                if not to:
                    raise OpError("名称需为 1 至 40 字")
                if to != name and (to in fixed or any(i["name"] == to for i in self.own)):
                    raise OpError("已有同名项目")
                self.own[at] = {"name": to, "path": self.own[at]["path"]}
                for t in terminals.values():
                    if t.project == name:
                        t.project = to
        self._save()


class Agent:
    def __init__(self, data, password):
        self.data = data
        self.password = password
        self.instance = secrets.token_hex(16)
        self.projects = Projects(data)
        self.marks = Marks(data)
        self.sessions = Sessions(self.marks)
        self.sessions_at = 0.0          # when the conversations were last looked up
        self.sessions_sent = ("", 0.0)  # what the relay was last told, and when
        self.tools = (["shell"], 0.0)
        self.terminals = {}
        self.completed = {}
        self.reported = set()
        self.work = threading.Lock()
        self.wakeup = threading.Event()
        self.stop = threading.Event()
        self.http = Http()
        self.pull_http = Http()
        self.server = ""
        self.token = ""
        self.next_login = 0.0
        self.last_activity = time.time()
        self.last_input = 0.0
        self.soon = False
        self.paired = False
        self.unreachable = False        # said once, not at every attempt

    # ---- wiring
    def wake(self):
        self.wakeup.set()

    def stop_run(self, *_):
        self.stop.set()
        self.wakeup.set()

    def shell(self, cfg):
        shell = str(cfg.get("Shell") or "").strip() or os.environ.get("SHELL") or "/bin/bash"
        return shell if os.path.isfile(shell) else "/bin/bash"

    # ---- protocol
    def login(self):
        if not SERVER_RE.match(self.server):
            return False
        # A refused password is tried again once a minute: the relay locks sign-in after a
        # few wrong ones. A relay that cannot be reached yet (both just started, the network
        # is not up) is tried again soon.
        self.next_login = time.time() + 60
        try:
            status, body = self.http.post(self.server + "/api/login", {"password": self.password})
            token = json.loads(body.decode("utf-8")).get("token") if status == 200 else None
        except (OSError, http.client.HTTPException, ValueError):
            self.next_login = time.time() + 5
            if not self.unreachable:
                self.unreachable = True
                say("连不上中转 %s，稍后重试" % self.server)
            return False
        self.unreachable = False
        if not isinstance(token, str) or len(token) < 32:
            say("中转拒绝了登录（HTTP %d），请核对密码；一分钟后重试" % status)
            return False
        self.token = token
        say("已连接中转 " + self.server)
        return True

    def report(self):
        cfg = read_config(self.data)
        server = str(cfg.get("Server") or "").strip().rstrip("/")
        if server != self.server:
            self.server, self.token = server, ""
            self.http.close()
            self.pull_http.close()
        if not self.server or not self.token:
            # Even with nothing going out, pending must be sealed: it is the only buffer
            # without a bound, and the floor on output carries the agent through a long
            # relay outage.
            with self.work:
                for live in self.terminals.values():
                    seal(live)
            if not self.server or (time.time() < self.next_login or not self.login()):
                return
        if not self.paired:
            self.paired = True
            if sys.stderr.isatty():
                print_pairing(pairing_address(cfg, self.server), self.password, pairing_name(cfg))
            else:                               # a service: this output is kept in the journal
                say("配对信息不写入日志；在终端运行 agent.py --pair --data %s 查看地址、密码和二维码" % self.data)
        with self.work:
            enabled = cfg.get("RemoteEnabled") is True
            if not enabled:
                for live in self.terminals.values():
                    if not live.closed:
                        live.begin_close()
            dirs = self.projects.all(cfg)
            for live in self.terminals.values():
                live.escalate()
            for live in self.terminals.values():
                seal(live)
            output = output_batch(list(self.terminals.values()))
            if output:
                self.last_activity = time.time()
            # Pieces of files are large: a report carries as many as fit, the rest go with the next.
            acks, room = [], 3 * FILE_PIECE
            for key, item in self.completed.items():
                if key in self.reported:
                    continue
                room -= len(item.get("result", {}).get("data", ""))
                if acks and room < 0:
                    break
                acks.append(item)
                if len(acks) >= 200:
                    break
            now = time.time()
            if now - self.tools[1] > 30:
                self.tools = ([t for t in ("claude", "codex") if find_tool(t)] + ["shell"], now)
            if now - self.sessions_at > 3:
                self.sessions_at = now
                self.sessions.scan(dirs, list(self.terminals.values()))
            payload = {
                "info": {"instance": self.instance, "enabled": enabled, "tools": self.tools[0],
                         "features": FEATURES, "version": VERSION, "newer": "",
                         "shell": os.path.basename(self.shell(cfg)),
                         "workspaces": list(dirs.keys()),
                         "projects": self.projects.entries(cfg, dirs)},
                "terminals": [{"id": live.id, "state": "closed" if live.closed else "running",
                               "cols": live.cols, "rows": live.rows, "session": live.session,
                               "status": "" if live.closed else live.status, "exit_code": live.exit_code,
                               "error": "程序退出（代码 %d），请检查终端画面中的原因" % live.exit_code
                                        if live.exit_code else ""} for live in self.terminals.values()],
                "output": output, "acks": acks}
            # A program may end while this report travels: it is forgotten only after a
            # report that said so, or the phone would show it running for ever.
            ended = [item["id"] for item in payload["terminals"] if item["state"] == "closed"]
            # The list of conversations is long: it goes out when it changed, and now and then
            # in case the relay restarted.
            listed = json.dumps(self.sessions.found, ensure_ascii=False, sort_keys=True)
            listing = listed != self.sessions_sent[0] or now - self.sessions_sent[1] > 10
            if listing:
                payload["sessions"] = self.sessions.found
        try:
            status, body = self.http.post(self.server + "/api/terminal/agent", payload, self.token)
        except (OSError, http.client.HTTPException):
            return
        if status == 401:
            self.token = ""
            return
        if status != 200:
            return
        try:
            result = json.loads(body.decode("utf-8") or "{}")
        except ValueError:
            return
        if not isinstance(result, dict):
            return
        with self.work:
            if listing:
                self.sessions_sent = (listed, now)
            for key in ended:
                if key in self.terminals:
                    self.terminals[key].told_closed = True
            for ack in acks:
                self.reported.add(ack["id"])
                ack.pop("result", None)         # handed over once: nothing of a file is kept
            ops = result.get("operations")
            if isinstance(ops, list):
                for op in ops:
                    if isinstance(op, dict):
                        self.execute(op, cfg, dirs)
            upto_map = result.get("output_ack")
            if isinstance(upto_map, dict):
                for live in self.terminals.values():
                    upto = upto_map.get(live.id)
                    if isinstance(upto, int) and not isinstance(upto, bool):
                        live.trim(upto)
            if len(self.completed) > 4000:
                for key in list(self.completed)[:1000]:
                    del self.completed[key]
                    self.reported.discard(key)
            for key in [k for k, live in self.terminals.items() if live.can_forget()]:
                self.terminals.pop(key).dispose()

    def execute(self, op, cfg, dirs):
        """Carries out one operation. Called with self.work held, from the report loop or
        the held pull; the same id is never carried out twice."""
        try:
            op_id, terminal = check_op(op, need_terminal=op.get("action") not in
                                       PROJECT_ACTIONS + FILE_ACTIONS + ("session_read", "session_mark"))
        except OpError as error:
            say("拒绝操作：%s" % error)
            return
        if op_id in self.completed:
            self.reported.discard(op_id)        # the ack may have been lost: send it again
            return
        error, result = "", None
        try:
            if cfg.get("RemoteEnabled") is not True:
                raise OpError("电脑远控已关闭")
            action = str(op.get("action") or "")
            if action in PROJECT_ACTIONS:
                self.projects.change(op, action, cfg, self.terminals)
            elif action in FILE_ACTIONS:
                root = dirs.get(str(op.get("dir") or ""))
                if root is None:
                    raise OpError("没有这个项目")
                result = files(root, op.get("path", ""), action, op.get("offset", 0))
            elif action == "session_read":
                result = self.sessions.read(str(op.get("session") or ""), str(op.get("tool") or ""),
                                            str(op.get("dir") or ""), dirs)
            elif action == "session_mark":
                # pinned or hidden, or no longer: of a conversation this computer listed
                session, tool = str(op.get("session") or ""), str(op.get("tool") or "")
                if not any(s["id"] == session and s["tool"] == tool for s in self.sessions.found):
                    raise OpError("电脑上没有找到这个对话，请刷新后重试")
                for kind in MARKS:
                    if isinstance(op.get(kind), bool):
                        self.marks.set(kind, tool, session, op[kind])
                self.sessions_at, result = 0.0, {}      # the next report lists the conversations as they now stand
            elif action == "start":
                self.start_terminal(op, dirs, terminal)
            else:
                live = self.terminals.get(terminal)
                if live is None or live.closed:
                    raise OpError("终端已结束")
                # a terminal can always be ended; typing into it needs its folder still allowed
                if action != "close" and live.dir not in dirs.values():
                    raise OpError("目录不再获允许")
                if action == "input":
                    data = op.get("data")
                    if not isinstance(data, str) or not data or len(data) > 16000:
                        raise OpError("输入无效")
                    self.last_input = time.time()
                    live.write(data)
                elif action == "resize":
                    cols, rows = op.get("cols"), op.get("rows")
                    if not all(isinstance(v, int) and not isinstance(v, bool) for v in (cols, rows)) \
                            or not (20 <= cols <= 240 and 6 <= rows <= 100):
                        raise OpError("尺寸无效")
                    live.resize(cols, rows)
                elif action == "close":
                    live.begin_close()
                else:
                    raise OpError("操作无效")
        except OpError as exc:
            error = str(exc)
        except Exception as exc:                # never leak a path or a stack to the phone
            error = "终端操作失败：" + type(exc).__name__
        self.completed[op_id] = {"id": op_id, "error": error}
        if result is not None and not error:
            self.completed[op_id]["result"] = result
        self.reported.discard(op_id)
        self.last_activity = time.time()
        self.soon = True
        if error:
            say("操作 %s 失败：%s" % (op_id[:8], error))

    def start_terminal(self, op, dirs, terminal):
        if terminal in self.terminals:
            return                              # already started; the operation is a repeat
        tool = str(op.get("tool") or "")
        if tool not in ("claude", "codex", "shell"):
            raise OpError("电脑没有这个工具")
        name = str(op.get("dir") or "")
        if name not in dirs:
            raise OpError("目录未获允许")
        cwd = dirs[name]
        if not os.path.isdir(cwd):
            raise OpError("电脑上没有这个文件夹")
        if sum(1 for live in self.terminals.values() if not live.closed) >= MAX_TERMINALS:
            raise OpError("最多同时运行 %d 个终端，请先结束一个" % MAX_TERMINALS)
        cfg = read_config(self.data)
        session = ""
        if tool == "shell":
            argv = [self.shell(cfg)]
        else:
            # Ending a program that runs elsewhere on this computer, or copying its
            # conversation, needs proof of who owns it; this agent does not do either.
            if op.get("takeover") is True or op.get("fork") is True:
                raise OpError("这台电脑不支持接管或副本，请先在电脑上结束原来的程序")
            launcher = find_tool(tool)
            if launcher is None:
                raise OpError("电脑没有这个工具")
            session, history = str(op.get("session") or ""), op.get("history") is True
            if session:
                self.sessions.scan(dirs, list(self.terminals.values()))     # as it is now, not seconds ago
                saved = self.sessions.known(session, tool, name) if UUID_RE.match(session) else None
                if saved is None:
                    raise OpError("电脑上没有找到这个对话，请刷新后重试")
                if saved["live"] or any(t.session == session and not t.closed for t in self.terminals.values()):
                    raise OpError("这个对话正在被使用，请先结束使用它的程序")
            fresh = tool == "claude" and not session and not history
            if fresh:
                session = str(uuid.uuid4())     # named by us, so the terminal knows its conversation at once
            argv = tool_argv(tool, launcher, session, history, str(cfg.get("RemoteMaxMode") or ""), fresh)
        self.terminals[terminal] = Live(terminal, name, cwd, 80, 24, argv, self.wake, tool, session)

    # ---- loops
    def listen(self):
        """A request the relay holds open until the phone asks for something, so a key
        press does not wait for the next report."""
        while not self.stop.is_set():
            token = self.token
            if not token or not self.server:
                self.stop.wait(1)
                continue
            started = time.monotonic()
            waiting = brought = False
            try:
                status, body = self.pull_http.post(
                    self.server + "/api/terminal/agent/pull",
                    {"instance": self.instance, "wait": 12}, token, timeout=20)
                if status == 401:
                    self.token = ""
                elif status == 200:
                    waiting = True
                    result = json.loads(body.decode("utf-8") or "{}")
                    ops = result.get("operations")
                    if isinstance(ops, list) and ops:
                        brought = True
                        cfg = read_config(self.data)
                        with self.work:
                            dirs = self.projects.all(cfg)
                            for op in ops:
                                if isinstance(op, dict):
                                    self.execute(op, cfg, dirs)
                        self.wake()
            except (OSError, http.client.HTTPException, ValueError):
                waiting = False
            # An answer that came back at once must not become a busy loop. One that brought something is asked
            # for again at once: in a drag or fast typing the next is already waiting.
            if not waiting or (not brought and time.monotonic() - started < 0.04):
                self.stop.wait(0.04 if waiting else 2)

    def run(self):
        threading.Thread(target=self.listen, daemon=True).start()
        while not self.stop.is_set():
            try:
                self.report()
            except Exception as exc:            # network and login failures are retried
                say("报告失败：%s" % type(exc).__name__)
            pause = 0.02 if self.soon else 0.25 if time.time() - self.last_activity < 20 else 0.7
            self.soon = False
            if self.wakeup.wait(pause):
                self.wakeup.clear()
                if time.time() - self.last_input < 0.15:
                    time.sleep(0.012)
        for live in self.terminals.values():
            if not live.closed:
                live.kill()
            live.dispose()
        self.http.close()
        self.pull_http.close()
        return 0


def pairing_name(cfg):
    return tidy(str(cfg.get("Name") or socket.gethostname()), 60)


def pairing_address(cfg, server):
    """Where the phone reaches the relay. It differs from Server when the relay runs on
    this computer: the agent talks to it locally, the phone through PairUrl."""
    public = str(cfg.get("PairUrl") or "").strip().rstrip("/")
    return public if SERVER_RE.match(public) else server


def show_pairing(data):
    """--pair: what the phone needs to add this computer, for the person at the console."""
    cfg = read_config(data)
    server = str(cfg.get("Server") or "").strip().rstrip("/")
    password = read_password(data)
    if not SERVER_RE.match(server) or not password:
        say("缺少中转地址或密码：先在 %s 的 config.json 填写 Server，并用 --set-password 存入密码" % data)
        return 2
    print_pairing(pairing_address(cfg, server), password, pairing_name(cfg))
    return 0


QR_ECC = (None, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26)    # error correction bytes of one block, level M
QR_BLOCKS = (None, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5)


def qr_code(text):
    """The QR code of a short text as rows of dark (True) and light modules, without the
    quiet zone; None when the text is too long. Byte mode, error correction level M,
    versions 1 to 10 (213 bytes): the same code agent-windows/QrCode.cs draws."""
    data = text.encode("utf-8")

    def raw_modules(version):
        result = (16 * version + 128) * version + 64
        if version >= 2:
            align = version // 7 + 2
            result -= (25 * align - 10) * align - 55
            if version >= 7:
                result -= 36
        return result

    def multiply(x, y):                 # in the field the code's error correction uses
        z = 0
        for i in range(7, -1, -1):
            z = (z << 1) ^ ((z >> 7) * 0x11D)
            z ^= ((y >> i) & 1) * x
        return z & 0xFF

    for version in range(1, 11):
        capacity = raw_modules(version) // 8 - QR_ECC[version] * QR_BLOCKS[version]
        count = 8 if version <= 9 else 16
        if 4 + count + len(data) * 8 <= capacity * 8:
            break
    else:
        return None

    # data bits: mode, length, bytes, terminator, padding
    bits = [0, 1, 0, 0] + [(len(data) >> i) & 1 for i in range(count - 1, -1, -1)]
    for byte in data:
        bits += [(byte >> i) & 1 for i in range(7, -1, -1)]
    bits += [0] * min(4, capacity * 8 - len(bits))
    bits += [0] * (-len(bits) % 8)
    words = [int("".join(map(str, bits[i:i + 8])), 2) for i in range(0, len(bits), 8)]
    pad = 0xEC
    while len(words) < capacity:
        words.append(pad)
        pad ^= 0xEC ^ 0x11

    # error correction blocks, interleaved
    blocks, ecc, raw = QR_BLOCKS[version], QR_ECC[version], raw_modules(version) // 8
    short_blocks, short_length = blocks - raw % blocks, raw // blocks
    divisor, root = [0] * (ecc - 1) + [1], 1
    for _ in range(ecc):
        for j in range(ecc):
            divisor[j] = multiply(divisor[j], root)
            if j + 1 < ecc:
                divisor[j] ^= divisor[j + 1]
        root = multiply(root, 2)
    parts, at = [], 0
    for i in range(blocks):
        length = short_length - ecc + (0 if i < short_blocks else 1)
        part = words[at:at + length]
        at += length
        rest = [0] * ecc
        for byte in part:
            factor = byte ^ rest[0]
            rest = rest[1:] + [0]
            rest = [r ^ multiply(d, factor) for r, d in zip(rest, divisor)]
        parts.append((part, rest))
    stream = []
    for i in range(short_length - ecc + 1):
        stream += [part[i] for part, _ in parts if i < len(part)]
    for i in range(ecc):
        stream += [rest[i] for _, rest in parts]

    # function patterns
    size = version * 4 + 17
    dark = [[False] * size for _ in range(size)]
    locked = [[False] * size for _ in range(size)]

    def put(x, y, value):
        dark[y][x] = bool(value)
        locked[y][x] = True

    for i in range(size):
        put(6, i, i % 2 == 0)
        put(i, 6, i % 2 == 0)
    for cx, cy in ((3, 3), (size - 4, 3), (3, size - 4)):
        for dy in range(-4, 5):
            for dx in range(-4, 5):
                if 0 <= cx + dx < size and 0 <= cy + dy < size:
                    put(cx + dx, cy + dy, max(abs(dx), abs(dy)) not in (2, 4))
    if version > 1:
        number = version // 7 + 2
        step = (version * 4 + number * 2 + 1) // (number * 2 - 2) * 2
        places = [6] + [size - 7 - step * i for i in range(number - 2, -1, -1)]
        for i, px in enumerate(places):
            for j, py in enumerate(places):
                if (i, j) in ((0, 0), (0, number - 1), (number - 1, 0)):
                    continue
                for dy in range(-2, 3):
                    for dx in range(-2, 3):
                        put(px + dx, py + dy, max(abs(dx), abs(dy)) != 1)

    def put_format(mask):
        rem = mask                      # level M has format bits 00
        for _ in range(10):
            rem = (rem << 1) ^ ((rem >> 9) * 0x537)
        value = (mask << 10 | rem) ^ 0x5412
        bit = lambda i: (value >> i) & 1
        for i in range(6):
            put(8, i, bit(i))
        put(8, 7, bit(6))
        put(8, 8, bit(7))
        put(7, 8, bit(8))
        for i in range(9, 15):
            put(14 - i, 8, bit(i))
        for i in range(8):
            put(size - 1 - i, 8, bit(i))
        for i in range(8, 15):
            put(8, size - 15 + i, bit(i))
        put(8, size - 8, True)

    put_format(0)                       # reserves the format area; the real mask is written below
    if version >= 7:
        rem = version
        for _ in range(12):
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25)
        value = version << 12 | rem
        for i in range(18):
            a, b = size - 11 + i % 3, i // 3
            put(a, b, (value >> i) & 1)
            put(b, a, (value >> i) & 1)

    # data modules in the zigzag order
    index, right = 0, size - 1
    while right >= 1:
        if right == 6:
            right = 5
        for vert in range(size):
            for j in range(2):
                x = right - j
                y = size - 1 - vert if (right + 1) & 2 == 0 else vert
                if not locked[y][x] and index < len(stream) * 8:
                    dark[y][x] = bool((stream[index >> 3] >> (7 - (index & 7))) & 1)
                    index += 1
        right -= 2

    masks = (lambda x, y: (x + y) % 2 == 0, lambda x, y: y % 2 == 0, lambda x, y: x % 3 == 0,
             lambda x, y: (x + y) % 3 == 0, lambda x, y: (x // 3 + y // 2) % 2 == 0,
             lambda x, y: x * y % 2 + x * y % 3 == 0, lambda x, y: (x * y % 2 + x * y % 3) % 2 == 0,
             lambda x, y: ((x + y) % 2 + x * y % 3) % 2 == 0)

    def apply(mask):                    # applying the same mask again removes it
        for y in range(size):
            for x in range(size):
                if not locked[y][x] and masks[mask](x, y):
                    dark[y][x] = not dark[y][x]

    def penalty():
        """Runs of one colour, finder-like sequences, 2x2 blocks and the balance of dark
        and light: the lower the easier to scan."""
        result, columns = 0, [list(column) for column in zip(*dark)]
        finder = [True, False, True, True, True, False, True, False, False, False, False]
        for line in dark + columns:
            run = 1
            for i in range(1, size):
                if line[i] == line[i - 1]:
                    run += 1
                    result += 3 if run == 5 else 1 if run > 5 else 0
                else:
                    run = 1
            for begin in range(size - 10):
                piece = line[begin:begin + 11]
                result += 40 * ((piece == finder) + (piece == finder[::-1]))
        for y in range(size - 1):
            for x in range(size - 1):
                if dark[y][x] == dark[y][x + 1] == dark[y + 1][x] == dark[y + 1][x + 1]:
                    result += 3
        total = sum(map(sum, dark))
        return result + 10 * (abs(total * 20 - size * size * 10) // (size * size))

    best, lowest = 0, None
    for mask in range(8):
        apply(mask)
        put_format(mask)
        score = penalty()
        if lowest is None or score < lowest:
            best, lowest = mask, score
        apply(mask)
    apply(best)
    put_format(best)
    return dark


def qr_text(code, margin=3):
    """A code drawn with half-height blocks, two rows of modules to a line of text, black on
    white whatever colours the terminal has."""
    size = len(code) + 2 * margin
    module = lambda x, y: 0 <= y - margin < len(code) and 0 <= x - margin < len(code) and code[y - margin][x - margin]
    lines = []
    for y in range(0, size, 2):
        line = "".join(" \u2584\u2580\u2588"[2 * module(x, y) + module(x, y + 1)] for x in range(size))
        lines.append("\x1b[30;47m" + line + "\x1b[0m")
    return "\n".join(lines) + "\n"


def print_pairing(server, password, name):
    """The same code the Windows program shows as a picture, drawn in the console."""
    payload = "remotecli://connect?u=%s&p=%s" % (quote(server, safe=""), quote(password, safe=""))
    code = qr_code(payload + "&n=" + quote(name, safe="")) or qr_code(payload)    # a long name is left out
    print("\n配对：手机 App 点“扫码添加电脑”，扫下面的二维码，或手动输入地址与密码。", file=sys.stderr)
    print("  地址：%s\n  名称：%s\n  密码：%s\n" % (server, name, password), file=sys.stderr)
    if code:
        sys.stderr.write(qr_text(code))
        print("\n二维码显示不全时把终端窗口拉大或缩小字体；扫不了就在 App 里选“手动输入地址”。", file=sys.stderr)
    else:
        print("地址太长，画不成二维码，请在 App 里选“手动输入地址”。", file=sys.stderr)
    sys.stderr.flush()


def set_password(folder):
    """Stores the relay's password for this agent, the way --set-password does on Windows."""
    secret = sys.stdin.readline().strip()
    if not secret:
        return 2
    os.makedirs(folder, mode=0o700, exist_ok=True)
    write_private(os.path.join(folder, "password.txt"), secret + "\n")
    print("密码已写入 %s（权限 600）" % os.path.join(folder, "password.txt"))
    return 0


def main():
    argv = sys.argv[1:]
    data = os.environ.get("REMOTECLI_DATA") or \
        os.path.join(os.path.expanduser("~"), ".local", "state", "remote-cli-agent")
    if "--data" in argv:
        at = argv.index("--data")
        if at + 1 < len(argv):
            data = argv[at + 1]
    if argv and argv[0] == "--set-password":
        return set_password(argv[1] if len(argv) > 1 else data)
    if "--pair" in argv:
        return show_pairing(data)
    os.makedirs(data, mode=0o700, exist_ok=True)
    # One agent per data directory (the Windows agent's mutex): a second instance would
    # report with its own id and the relay would keep closing the first one's terminals.
    # The Rust agent takes the same lock, so the two implementations guard each other.
    lock = open(os.path.join(data, "agent.lock"), "w")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        say("此数据目录已有 agent 在运行（agent.lock 被占用），退出")
        return 3
    password = read_password(data)
    if not password:
        say("缺少中转密码：运行 python3 agent.py --set-password %s 并输入中转的密码，"
            "或设置环境变量 RCLI_PASSWORD" % data)
        return 2
    say("remote-cli agent (Linux) %s，数据目录 %s" % (VERSION, data))
    agent = Agent(data, password)
    signal.signal(signal.SIGTERM, agent.stop_run)
    signal.signal(signal.SIGINT, agent.stop_run)
    return agent.run()


if __name__ == "__main__":
    sys.exit(main())
