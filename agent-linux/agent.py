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
import binascii
import errno
import calendar
import codecs
import datetime
import fcntl
import glob
import http.client
import ipaddress
import json
import math
import os
import pty
import re
import secrets
import select
import shutil
import signal
import socket
import sqlite3
import stat
import struct
import subprocess
import sys
import termios
import threading
import time
import uuid
from types import SimpleNamespace
from urllib.parse import quote, urlsplit
import claude_permissions
import delegations as delegations_mod
import peers as peers_mod
import quotas as quotas_store
import teams
import worktrees

VERSION = "0.8.0"
ID_RE = re.compile(r"\A[a-f0-9]{16,32}\Z")
TERMINAL_RE = re.compile(r"\A[a-f0-9]{32}\Z")
SERVER_RE = re.compile(r"\Ahttps?://[^/\s]+\Z")
UUID_RE = re.compile(r"\A[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\Z")
OPENCODE_RE = re.compile(r"\Ases_[A-Za-z0-9]{10,40}\Z")   # opencode names its own conversations
CLINE_RE = re.compile(r"\A[0-9]{10,16}_[A-Za-z0-9]{4,10}\Z")   # cline: a stamp and a short tail


def pi_slug(cwd):
    """The folder name pi gives the conversations of a working path: its own leading slash
    gone, the slashes and colons left turned to single dashes, all wrapped in double dashes."""
    return "--" + re.sub(r"[/\\:]", "-", re.sub(r"^[/\\]", "", cwd)) + "--"


CHAIN_KEYS = ("id", "from", "from_tool", "from_title", "to", "to_tool", "to_title", "text",
              "left", "rounds", "runs", "last_at", "last_ok", "last_note")
CHAIN_NUMBERS, CHAIN_FLAGS = ("left", "rounds", "runs", "last_at"), ("last_ok",)
SCHEDULE_KEYS = ("id", "tool", "session", "title", "text", "kind", "time", "at", "minutes",
                 "on", "next", "runs", "last_at", "last_ok", "last_note")
SCHEDULE_NUMBERS = ("at", "minutes", "next", "runs", "last_at")
SCHEDULE_FLAGS = ("on", "last_ok")


def week_days(given):
    """Days of the week as they are given ("1,2,3,4,5", or a list of numbers): each of 1 (Monday) to 7 (Sunday)
    once, in order; all seven are every day, and so none. None where one is not a day of the week."""
    if given is None or given == "":
        return []
    words = given.split(",") if isinstance(given, str) else given if isinstance(given, list) else [given]
    days = set()
    for word in words:
        if isinstance(word, bool) or not (type(word) is int or isinstance(word, str) and word.strip().isdigit()):
            return None
        day = int(word)
        if not 1 <= day <= 7:
            return None
        days.add(day)
    return [] if len(days) == 7 else sorted(days)


def kept_rules(read, keys, numbers, flags):
    """Rules read from disk: one that is old or hand-edited and missing a field is dropped,
    rather than taking the whole feature down the first time it is shown or run."""
    kept = []
    for item in read if isinstance(read, list) else []:
        if not isinstance(item, dict) or not ID_RE.match(str(item.get("id") or "")):
            continue
        if any(key not in item for key in keys):
            continue
        if any(isinstance(item[key], bool) or not isinstance(item[key], int) for key in numbers):
            continue
        if any(not isinstance(item[key], bool) for key in flags):
            continue
        if any(not isinstance(item[key], str) for key in keys if key not in numbers and key not in flags):
            continue
        kept.append(item)
    return kept


def reply_object(body):
    """A reply from the relay as an object: a list, null or broken JSON is nothing at all.
    A proxy, a restart or a captive page can answer with any of them."""
    try:
        value = json.loads(body.decode("utf-8") if isinstance(body, bytes) else (body or "{}"))
    except (AttributeError, TypeError, UnicodeDecodeError, ValueError):
        return {}
    return value if isinstance(value, dict) else {}


def operations(body):
    """The operations one reply brought, whatever else it said."""
    given = reply_object(body).get("operations")
    return [op for op in given if isinstance(op, dict)] if isinstance(given, list) else []


def pts_device(fd):
    """The device of a terminal's screen, for finding what keeps it open. Some containers do
    not expose /dev/pts/N: the terminal still works, only the sweep of leftovers is lost."""
    try:
        number = struct.unpack("I", fcntl.ioctl(fd, 0x80045430, struct.pack("I", 0)))[0]
        return os.stat("/dev/pts/%d" % number).st_rdev
    except (OSError, ValueError, struct.error):
        return None


def started_at(pid):
    """When a process began, as the kernel tells it in /proc: a pid that was used again has
    another one. None if it is already gone."""
    try:
        with open("/proc/%d/stat" % pid, encoding="utf-8", errors="replace") as stream:
            fields = stream.read().rsplit(")", 1)[-1].split()
    except OSError:
        return None
    # The suffix starts at field 3 (state); starttime is field 22, not vsize (23).
    return fields[19] if len(fields) > 19 else None


def kill_if_started(pid, at):
    """Kills the process we looked at, not an unrelated one that now has its number."""
    if at is None or started_at(pid) != at:
        return
    try:
        os.kill(pid, signal.SIGKILL)
    except OSError:
        pass                    # it left between the looking and the killing


def private_address(url):
    """Whether an address is one of this computer's own networks: a literal of the private
    ranges or the loopback, or a name that means this machine. A name that goes elsewhere is
    not: where it points is not this computer's to know."""
    host = url.split("//", 1)[-1].split("/", 1)[0].rsplit("@", 1)[-1]
    if host.startswith("["):                # an IPv6 literal keeps its colons inside brackets
        host = host[1:host.find("]")] if "]" in host else ""
    else:
        host = host.split(":", 1)[0]
    host = host.lower()
    if not host or host == "localhost" or host.endswith(".local"):
        return True
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        return False
    return address.is_private or address.is_loopback or address.is_link_local
PROJECT_ACTIONS = ("project_add", "project_remove", "project_rename")
UPLOAD_ACTIONS = ("file_upload_start", "file_upload_chunk", "file_upload_finish", "file_upload_cancel")
FILE_ACTIONS = ("file_list", "file_read", "file_find") + UPLOAD_ACTIONS
FILE_PIECE = 737280     # bytes of a file in one answer, as the Windows agent cuts them
FILE_ENTRIES = 3000     # entries of a folder in one answer
TOOL_LABELS = {"claude": "Claude Code", "codex": "Codex", "opencode": "OpenCode", "qoder": "Qoder", "cline": "Cline", "pi": "Pi"}
FEATURES = ["terminal-exit", "files", "file-upload", "file-find", "tasks", "attachments", "peek", "marks", "delete", "work", "chat", "chains", "brief", "schedule-days", "handover", "reopen", "teams", "teams-v2", "teams-grouped",
            "teams-roster"]
# A roster picks which tools fill the roles here. Model and thinking-effort
# choices are not wired into these tools' command lines, so those flags stay
# unadvertised: the phone must not offer controls whose picks do nothing.
CLAUDE_READ_ONLY_TOOLS = frozenset(("Read", "Glob", "Grep", "LS", "NotebookRead"))
ATTACHMENT_LIMIT = 20 * 1024 * 1024
ATTACHMENT_PATH = re.compile(r"^\.remote-ai/attachments/[0-9]{8}-[0-9]{6}-[0-9a-f]{6}\.(?:png|jpg|jpeg|webp|gif)$")
# What the phone asks about the owner's work (docs/PROTOCOL.md, `/api/work`). Passing on when a conversation stops
# ("chains") is seen by watching the conversation's record grow and then go quiet — the tools' own hooks, which
# tell the Windows program of it at once, are not there on Linux.
WORK_ACTIONS = ("work_stats", "work_schedules", "work_schedule_set", "work_schedule_remove", "work_handoff", "work_brief",
                "work_chains", "work_chain_set", "work_chain_remove",
                "work_teams", "work_team_start", "work_team_stop", "work_team_resume",
                "work_team_remove", "work_team_say", "work_team_pick", "work_team_swap")
CHAIN_TOOLS = ("claude", "codex", "opencode", "qoder", "cline", "pi")   # the conversations the phone can pass between
TEAM_FEATURES = tuple(tool + "-team" for tool in CHAIN_TOOLS)
CHAIN_QUIET = 45.0     # a turn is over once the source's record has been quiet this long after growing
MAX_CHAINS = 20
LONGEST_TURN, LONGEST_WAIT = 3 * 3600, 600      # a turn is counted for no longer; a first answer later than this is none
MAX_SCHEDULES = 30
MARKS = ("pinned", "hidden")     # what the owner says of single conversations from the phone
MAX_MARKS = 300                 # of each kind; with more, the ones marked longest ago give way
SAID_MESSAGES = 40      # of a conversation, the last this many things its user and the tool said are shown on the phone
SAID_RUN = 30           # with what the tool did between them: of one unbroken run, the last this many
SAID_CHARS = 1500       # and of each, this much
SAID_NOTED = 600        # of what the tool said of its work between its steps, this much
SAID_RESULTS = 40       # of this many steps, counted from the last, the first lines of what came back are given
# where the tools' own installers put them; a service's PATH seldom has these
TOOL_DIRS = (".local/bin", ".npm-global/bin", "bin", ".bun/bin", ".volta/bin", ".cargo/bin", ".opencode/bin")
MAX_TERMINALS = 16
MAX_OWN_PROJECTS = 30
OP_TTL = 150            # seconds an operation may travel before it is refused
CHUNK_CHARS = 12000
OUTPUT_FLOOR_BYTES = 2 * 1024 * 1024  # unsent output is dropped past this; the relay dedups by seq
PENDING_BYTES = OUTPUT_FLOOR_BYTES  # the reader's buffer is bounded too, not only the sealed one
RECORDS_BYTES = 64 * 1024 * 1024     # room the work records may take before the oldest are dropped
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


def session_id_ok(tool, session):
    """A conversation id in this tool's own shape; only a tool's own are honoured."""
    if tool == "opencode":
        return bool(OPENCODE_RE.match(session))
    if tool == "cline":
        return bool(CLINE_RE.match(session))
    return bool(UUID_RE.match(session))   # claude, codex, qoder and pi name theirs a UUID


def iso_ms(stamp):
    """An ISO-8601 stamp (cline and qoder write them, in UTC) as epoch milliseconds."""
    try:
        return int(datetime.datetime.fromisoformat(str(stamp)[:23].replace("Z", "+00:00")).timestamp() * 1000)
    except (TypeError, ValueError):
        return 0


def cline_alive(pid):
    """Whether this process is a cline right now: a recorded pid may be gone, and its
    number may since have been given to something else."""
    try:
        with open("/proc/%d/cmdline" % pid, "rb") as stream:
            return b"cline" in stream.read()
    except OSError:
        return False


def opencode_db():
    """opencode's own SQLite store; XDG_DATA_HOME is honoured the way opencode honours it."""
    data = os.environ.get("XDG_DATA_HOME") or os.path.join(os.path.expanduser("~"), ".local", "share")
    return os.path.join(data, "opencode", "opencode.db")


def cline_db(name):
    """One of cline's SQLite stores under its data dir; CLINE_HOME overrides where it is."""
    root = os.environ.get("CLINE_HOME") or os.path.join(os.path.expanduser("~"), ".cline")
    return os.path.join(root, "data", "db", name)


def said_opencode(session):
    """What was last said in an opencode conversation, the same shape as said(). The store
    is a SQLite file the opencode server writes; reading it never waits for that server."""
    con = sqlite3.connect("file:%s?mode=ro" % opencode_db(), uri=True)
    try:
        con.execute("pragma busy_timeout=1000")
        rows = con.execute("select type, data from session_message where session_id = ? "
                           "order by seq", (session,)).fetchall()
    finally:
        con.close()
    out, updated = [], 0

    def add(role, text):
        text = text.strip()
        if text:
            out.append({"role": role, "text": text if len(text) <= SAID_CHARS else text[:SAID_CHARS - 1] + "…"})

    for kind, data in rows:
        try:
            payload = json.loads(data) if isinstance(data, str) else {}
        except ValueError:
            continue
        if not isinstance(payload, dict):
            continue
        created = payload.get("time", {}).get("created") if isinstance(payload.get("time"), dict) else None
        if isinstance(created, int):
            updated = max(updated, created)
        if kind == "user":
            text = payload.get("text")
            if isinstance(text, str) and not text.lstrip().startswith("<"):
                add("user", text)
        elif kind == "assistant":
            for part in payload.get("content") if isinstance(payload.get("content"), list) else []:
                if isinstance(part, dict) and part.get("type") == "text" and isinstance(part.get("text"), str):
                    add("assistant", part["text"])     # reasoning and tool parts are the tool's own
    return {"messages": out[-SAID_MESSAGES:], "more": False, "updated": updated}


def said_cline(session):
    """What was last said in a cline conversation, the same shape as said(). The stores are
    SQLite files cline's own hub writes; reading them never waits for that hub."""
    con = sqlite3.connect("file:%s?mode=ro" % cline_db("session-search.db"), uri=True)
    try:
        con.execute("pragma busy_timeout=1000")
        rows = con.execute("select role, content from session_search where session_id = ? "
                           "order by ordinal", (session,)).fetchall()
    finally:
        con.close()
    out, updated = [], 0

    def add(role, text):
        text = text.strip()
        if text:
            out.append({"role": role, "text": text if len(text) <= SAID_CHARS else text[:SAID_CHARS - 1] + "…"})

    for role, content in rows:
        if role not in ("user", "assistant") or not isinstance(content, str):
            continue
        lines = content.split("\n")
        stamp = iso_ms(lines[1][:23]) if len(lines) > 1 else 0
        if stamp:
            updated = max(updated, stamp)
        if role == "user":
            if lines[0] == "tool_result":
                continue                # what a tool answered, not what the person said
            text = lines[1] if len(lines) > 1 else ""            # under the <user_input> line
            if text.startswith("<user_input"):
                text = text.split(">", 1)[-1].rsplit("</user_input>", 1)[0]
            add("user", text)
        else:
            said = []
            for line in lines[1:]:
                if line != "text" and (line in ("tool_use", "tool_result", "thinking") or line.startswith("call_")):
                    break
                if line != "text":
                    said.append(line)
            add("assistant", "\n".join(said))
    return {"messages": out[-SAID_MESSAGES:], "more": False, "updated": updated}


def said_pi(path):
    """What was last said in a pi conversation, the same shape as said(). pi keeps one message
    to a line: what the person said and what the tool answered are its text, and its work is
    the calls and results written under it."""
    limit = 4 * 1024 * 1024
    out, steps, asked = [], {}, [0]

    def add(role, text):
        text = " ".join(text.split()) if role == "tool" else text.strip()
        room = SAID_NOTED if role == "thought" else SAID_CHARS
        if text:
            out.append({"role": role, "text": text if len(text) <= room else text[:room - 1] + "…"})

    def step(text, detail=""):
        before = len(out)
        add("tool", text)
        if len(out) == before:
            return None
        detail = (detail or "").strip()
        if detail and detail[:40] not in text:
            out[-1]["detail"] = detail if len(detail) <= 400 else detail[:400] + "…"
        return out[-1]

    for row in rows(read_part(path, True, limit)):
        if row.get("type") != "message" or not isinstance(row.get("message"), dict):
            continue
        message = row["message"]
        role = message.get("role")
        parts = message.get("content") if isinstance(message.get("content"), list) else []
        if role == "user":
            stamp = iso_ms(row.get("timestamp"))
            if stamp:
                asked[0] = stamp
            for part in parts:
                if isinstance(part, dict) and part.get("type") == "text" and isinstance(part.get("text"), str) \
                        and not part["text"].lstrip().startswith("<"):
                    add("user", part["text"])
        elif role == "assistant":
            for part in parts:
                if not isinstance(part, dict):
                    continue
                if part.get("type") == "text" and isinstance(part.get("text"), str):
                    add("assistant", part["text"])
                elif part.get("type") == "thinking" and isinstance(part.get("thinking"), str):
                    add("thought", part["thinking"])     # what it says of its work between the steps
                elif part.get("type") == "toolCall":
                    given = part.get("arguments") if isinstance(part.get("arguments"), dict) else {}
                    about = next((given[k] for k in ("description", "command", "file_path", "path", "pattern", "query", "url")
                                  if isinstance(given.get(k), str) and given[k]), "")
                    name = str(part.get("name") or "").strip()
                    made = step((name + ("：" + about[:160] if about else "")).strip(),
                                given.get("command") if isinstance(given.get("command"), str) else "")
                    if made is not None and isinstance(part.get("id"), str):
                        steps[part["id"]] = made
        elif role == "toolResult":
            made = steps.get(message.get("toolCallId"))
            if made is not None:
                made["result"] = first("\n".join(one.get("text") or "" for one in parts if isinstance(one, dict)))
    kept = talk(out, SAID_MESSAGES, SAID_RUN)
    with_result = 0
    for entry in reversed(kept):
        if "result" in entry:
            with_result += 1
            if with_result > SAID_RESULTS:
                del entry["result"]
    more = len(kept) < len(out) or os.path.getsize(path) > limit
    return {"messages": kept, "more": more, "updated": int(os.path.getmtime(path) * 1000), "asked": asked[0]}


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
    elif tool == "opencode":
        # --session takes an id that does not exist yet and makes it; --continue is the last one
        args = ["--session", session] if session else ["--continue"] if history else []
        # no read-only mode of its own: the default asks, --auto answers for the safe ones
        args += ["--auto"] if mode == "edit" else []
    elif tool == "qoder":
        # --session-id names a conversation that does not exist yet, --resume continues one
        args = ["--session-id" if fresh else "--resume", session] if session else ["--continue"] if history else []
        args += ["--permission-mode", "plan"] if mode == "read" else \
            ["--permission-mode", "accept_edits"] if mode == "edit" else []
    elif tool == "pi":
        # --session-id names a conversation that does not exist yet, --session takes the one it says
        args = ["--session-id" if fresh else "--session", session] if session else ["--continue"] if history else []
        # no read-only mode of its own, and every step runs as asked
    elif tool == "cline":
        # --id only continues; a new conversation is named by cline itself, like codex's
        args = ["--id", session] if session else []
        args += ["--plan"] if mode == "read" else []   # the default is act, already approving
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


FIND_MOST, FIND_ENTRIES, FIND_SECONDS = 20, 200000, 3.0
FIND_SKIPPED = {"node_modules", "bin", "obj", "dist", "build", "target", "__pycache__", "venv"}


def find_file(root, given):
    """The project's paths that end with the name an answer gives a file ('Peers.cs', 'chat/chat.js'), nearest
    first: walked breadth first past the folders of tools and builds, without following links, within a bound of
    entries and time. At most FIND_MOST, and `more` where there were others or the walk stopped at its bound."""
    wanted = str(given or "").strip().replace("\\", "/").strip("/")
    parts = wanted.split("/")
    if not wanted or len(wanted) > 300 or any(part in ("", ".", "..") for part in parts):
        raise OpError("文件名无效")
    if not os.path.isdir(root):
        raise OpError("文件夹不存在")
    hidden_wanted = any(part.startswith(".") for part in parts)
    tail, low = "/" + wanted.lower(), wanted.lower()
    found, seen, started, queue = [], 0, time.monotonic(), [""]
    while queue:
        relative = queue.pop(0)
        try:
            with os.scandir(os.path.join(root, relative) if relative else root) as listing:
                entries = sorted(listing, key=lambda e: e.name.lower())
        except OSError:
            continue
        for entry in entries:
            seen += 1
            if seen > FIND_ENTRIES or time.monotonic() - started > FIND_SECONDS:
                return {"found": found, "more": True}
            path = relative + "/" + entry.name if relative else entry.name
            if entry.is_dir(follow_symlinks=False):
                if entry.name in FIND_SKIPPED or (entry.name.startswith(".") and not hidden_wanted):
                    continue
                queue.append(path)
            elif entry.is_file() and (path.lower() == low or ("/" + path.lower()).endswith(tail)):
                if len(found) == FIND_MOST:
                    return {"found": found, "more": True}
                found.append(path)
    return {"found": found, "more": False}


def publish_upload(tmp, name, fd):
    """Publishes a finished upload without ever replacing a file that is already there. A
    hard link is the atomic 'create only if absent'; where the filesystem has none, an
    exclusive placeholder is created first, and only that placeholder is then replaced."""
    try:
        os.stat(name, dir_fd=fd, follow_symlinks=False)
        raise OpError("已有同名文件或文件夹，请重命名后再上传")
    except FileNotFoundError:
        pass
    try:
        os.link(tmp, name, src_dir_fd=fd, dst_dir_fd=fd, follow_symlinks=False)
        return
    except OSError as error:
        if error.errno not in (errno.EXDEV, errno.EPERM, errno.EMLINK, errno.ENOSYS):
            raise
    try:
        handle = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600, dir_fd=fd)
        os.close(handle)
    except FileExistsError:
        raise OpError("已有同名文件或文件夹，请重命名后再上传")
    try:
        os.replace(tmp, name, src_dir_fd=fd, dst_dir_fd=fd)
    except OSError:
        try:
            os.unlink(name, dir_fd=fd)
        except OSError:
            pass
        raise


def attachment_media(path):
    return {
        ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg",
        ".webp": "image/webp", ".gif": "image/gif"
    }.get(os.path.splitext(path)[1].lower(), "")


def read_attachments(root, value):
    if value is None:
        return []
    if not isinstance(value, list):
        raise OpError("\u9644\u56fe\u4e0d\u5b58\u5728\u6216\u5df2\u88ab\u5220\u9664")
    if len(value) > 4:
        raise OpError("\u9644\u56fe\u6700\u591a 4 \u5f20")
    result = []
    root = os.path.realpath(root)
    for relative in value:
        if not isinstance(relative, str) or not ATTACHMENT_PATH.fullmatch(relative):
            raise OpError("\u9644\u56fe\u4e0d\u5b58\u5728\u6216\u5df2\u88ab\u5220\u9664")
        absolute = os.path.normpath(os.path.join(root, relative))
        real = os.path.realpath(absolute)
        if absolute != os.path.join(root, relative) or not absolute.startswith(root.rstrip("/") + "/") \
                or real != absolute or not real.startswith(root.rstrip("/") + "/"):
            raise OpError("\u9644\u56fe\u4e0d\u5b58\u5728\u6216\u5df2\u88ab\u5220\u9664")
        try:
            info = os.stat(absolute, follow_symlinks=False)
            if not stat.S_ISREG(info.st_mode) or info.st_size > ATTACHMENT_LIMIT:
                raise OpError("\u9644\u56fe\u4e0d\u5b58\u5728\u6216\u5df2\u88ab\u5220\u9664")
            with open(absolute, "rb") as stream:
                data = stream.read()
        except (FileNotFoundError, PermissionError, OSError):
            raise OpError("\u9644\u56fe\u4e0d\u5b58\u5728\u6216\u5df2\u88ab\u5220\u9664")
        result.append({"relative": relative, "absolute": absolute, "media": attachment_media(absolute), "data": data})
    return result


def attachment_text(text, attachments):
    return text if not attachments else text + "\n\n" + "\n".join("\u9644\u56fe\uff1a" + one["absolute"] for one in attachments)


def claude_content(text, attachments):
    return [{"type": "text", "text": text}] + [
        {"type": "image", "source": {"type": "base64", "media_type": one["media"],
                                     "data": base64.b64encode(one["data"]).decode("ascii")}}
        for one in attachments
    ]


def codex_content(text, attachments):
    return [{"type": "text", "text": text, "text_elements": []}] + [
        {"type": "localImage", "path": one["absolute"]} for one in attachments
    ]


class Uploads:
    """Bounded uploads owned by this agent (docs/PROTOCOL.md, "Uploads"). A destination is
    published only after all bytes arrive, and an existing file or folder is never replaced.
    The destination directory is held open as a file descriptor for the upload's lifetime:
    every write and the final publish happen through that descriptor, so a folder swapped
    for a link mid-upload changes nothing — the bytes land in the directory that was
    checked at the start. The temporary file is published with a hard link, which fails
    rather than replaces on POSIX, where a rename would silently overwrite."""

    LIMIT = 300 * 1024 * 1024
    MAX_PENDING = 4
    IDLE_SECONDS = 600.0   # an upload nothing has arrived for is removed with its temp file

    def __init__(self):
        self.lock = threading.Lock()
        self.pending = {}   # upload id -> {"fd", "tmp", "stream", "root", "path", "size", "written", "touched"}

    @staticmethod
    def _key(op):
        key = str(op.get("upload") or "")
        if not re.fullmatch(r"[a-f0-9]{32}", key):
            raise OpError("上传编号无效")
        return key

    @staticmethod
    def _parts(relative):
        parts = [p for p in str(relative or "").replace("\\", "/").strip("/").split("/") if p != ""]
        if not parts or any(p in (".", "..") or any(ord(c) < 32 for c in p) for p in parts):
            raise OpError("文件名或路径无效，请重命名后再上传")
        return parts

    def _drop(self, key):
        current = self.pending.pop(key, None)
        if current is None:
            return
        try:
            current["stream"].close()
        except OSError:
            pass
        try:
            os.unlink(current["tmp"], dir_fd=current["fd"])
        except OSError:
            pass
        try:
            os.close(current["fd"])
        except OSError:
            pass

    def expire(self):
        """Removes uploads nothing has arrived for in ten minutes, with their temp files."""
        with self.lock:
            self._expire_locked()

    def _expire_locked(self):
        cutoff = time.time() - self.IDLE_SECONDS
        for key in [k for k, u in self.pending.items() if u["touched"] < cutoff]:
            self._drop(key)

    def dispose(self):
        with self.lock:
            for key in list(self.pending):
                self._drop(key)

    def run(self, root, op):
        action = str(op.get("action") or "")
        with self.lock:
            self._expire_locked()
            key = self._key(op)
            if action == "file_upload_start":
                return self._start(key, root, op)
            current = self.pending.get(key)
            if current is None:
                if action == "file_upload_cancel":
                    return {"offset": 0}    # cancelling an unknown id is harmless
                raise OpError("上传已过期，请重新选择文件")
            if current["root"] != root or current["path"] != str(op.get("path") or ""):
                raise OpError("上传目标不一致")
            current["touched"] = time.time()
            if action == "file_upload_cancel":
                self._drop(key)
                return {"offset": 0}
            if action == "file_upload_chunk":
                return self._chunk(key, current, op)
            if action == "file_upload_finish":
                return self._finish(key, current)
            raise OpError("不支持此操作")

    def _start(self, key, root, op):
        if key in self.pending:
            raise OpError("上传编号已使用")
        if len(self.pending) >= self.MAX_PENDING:
            raise OpError("同时上传的文件过多，请稍后重试")
        size = op.get("size")
        if isinstance(size, bool) or not isinstance(size, int) or not 0 <= size <= self.LIMIT:
            raise OpError("单个文件上传上限为 300 MB")
        relative = str(op.get("path") or "")
        if ATTACHMENT_PATH.fullmatch(relative):
            if size > ATTACHMENT_LIMIT:
                raise OpError("\u9644\u56fe\u4e0d\u5b58\u5728\u6216\u5df2\u88ab\u5220\u9664")
            remote = os.path.join(os.path.normpath(root), ".remote-ai")
            attachments = os.path.join(remote, "attachments")
            for directory in (remote, attachments):
                if os.path.lexists(directory) and (os.path.islink(directory) or not os.path.isdir(directory)):
                    raise OpError("\u9644\u56fe\u4e0d\u5b58\u5728\u6216\u5df2\u88ab\u5220\u9664")
                os.makedirs(directory, mode=0o700, exist_ok=True)
            ignore = os.path.join(remote, ".gitignore")
            if not os.path.exists(ignore):
                try:
                    fd_ignore = os.open(ignore, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
                    with os.fdopen(fd_ignore, "w", encoding="utf-8", newline="") as stream:
                        stream.write("*")
                except FileExistsError:
                    pass
        parts = self._parts(relative)
        root = os.path.normpath(root)
        full = os.path.normpath(os.path.join(root, *parts))
        inside = root.rstrip("/") + "/"
        if full != root and not full.startswith(inside):
            raise OpError("路径不在项目文件夹内")
        real_root = os.path.realpath(root)
        real_dir = os.path.realpath(os.path.dirname(full) or root)
        if real_dir != real_root and not real_dir.startswith(real_root.rstrip("/") + "/"):
            raise OpError("这个位置链接到项目文件夹之外，不能打开")
        try:
            fd = os.open(real_dir, os.O_RDONLY | os.O_DIRECTORY)
        except OSError:
            raise OpError("目标文件夹不存在或无法写入")
        stream, tmp = None, None
        try:
            target = parts[-1]
            try:
                os.stat(target, dir_fd=fd, follow_symlinks=False)
                raise OpError("已有同名文件或文件夹，请重命名后再上传")
            except FileNotFoundError:
                pass
            tmp = ".remoteai-upload-" + secrets.token_hex(16) + ".tmp"
            stream = os.fdopen(os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600, dir_fd=fd), "wb")
        except OpError:
            os.close(fd)
            raise
        except OSError:
            os.close(fd)
            raise OpError("目标文件夹不存在或无法写入")
        self.pending[key] = {"fd": fd, "tmp": tmp, "stream": stream, "root": root, "name": parts[-1],
                             "path": relative, "size": size, "written": 0, "touched": time.time()}
        return {"offset": 0}

    def _chunk(self, key, current, op):
        encoded = op.get("data")
        if not isinstance(encoded, str) or not encoded or len(encoded) > FILE_PIECE * 4 // 3:
            raise OpError("上传分块过大")
        try:
            data = base64.b64decode(encoded, validate=True)
        except (ValueError, binascii.Error):
            raise OpError("上传内容无效")
        offset = op.get("offset")
        if isinstance(offset, bool) or not isinstance(offset, int) \
                or offset != current["written"] or not data \
                or current["written"] + len(data) > current["size"]:
            raise OpError("上传位置或长度不一致")
        try:
            current["stream"].write(data)
        except OSError:
            self._drop(key)
            raise OpError("写入临时文件失败")
        current["written"] += len(data)
        return {"offset": current["written"]}

    def _finish(self, key, current):
        if current["written"] != current["size"]:
            raise OpError("文件尚未完整上传")
        fd, tmp, name = current["fd"], current["tmp"], current["name"]
        try:
            current["stream"].flush()
            os.fsync(current["stream"].fileno())
            current["stream"].close()
        except OSError:
            self._drop(key)
            raise OpError("写入临时文件失败")
        try:
            publish_upload(tmp, name, fd)
        except OpError:
            self.pending.pop(key, None)
            try:
                os.unlink(tmp, dir_fd=fd)
            except OSError:
                pass
            os.close(fd)
            raise
        except OSError:
            self.pending.pop(key, None)
            try:
                os.unlink(tmp, dir_fd=fd)
            except OSError:
                pass
            os.close(fd)
            raise OpError("发布文件失败")
        self.pending.pop(key, None)
        try:
            os.unlink(tmp, dir_fd=fd)
        except OSError:
            pass
        os.close(fd)
        return {"offset": current["size"]}


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
    limit = 4 * 1024 * 1024
    out = []
    # What a tool gave back fills most of a long conversation: of such a row only the beginning is taken, unread.
    given_back = '"type":"tool_result"' if tool == "claude" else '"type":"function_call_output"'
    steps, kept_lines = {}, []
    for line in read_part(path, True, limit).split("\n"):
        if len(line) > 4096 and given_back in line[:1200]:
            whose = re.search(r'"tool_use_id":"([^"]+)"', line[:1200])
            if tool == "claude" and whose:
                kept_lines.append(json.dumps({"type": "came-back", "whose": whose.group(1), "text": begins(line, line.find(given_back))}))
            continue
        kept_lines.append(line)
    text = "\n".join(kept_lines)

    def add(role, text):
        text = " ".join(text.split()) if role == "tool" else text.strip()
        room = SAID_NOTED if role == "thought" else SAID_CHARS
        if text:
            out.append({"role": role, "text": text if len(text) <= room else text[:room - 1] + "…"})

    def step(text, detail=""):
        """A step as a terminal would show it: besides what it is for, the command itself (where the step is told
        by something else) and, later, the first lines of what came back."""
        before = len(out)
        add("tool", text)
        if len(out) == before:
            return None
        detail = (detail or "").strip()
        if detail and detail[:40] not in text:
            out[-1]["detail"] = detail if len(detail) <= 400 else detail[:400] + "…"
        return out[-1]

    def came(made, back):
        if made is not None and first(back):
            made["result"] = first(back)

    def brief(name, given):
        given = given if isinstance(given, dict) else {}
        # what the step is for, where the tool says it (Bash's description), before the command itself
        about = next((given[k] for k in ("description", "command", "file_path", "path", "pattern", "query", "url")
                      if isinstance(given.get(k), str) and given[k]), "")
        return (str(name or "") + ("：" + about[:160] if about else "")).strip()

    # Codex notes each step for its own window as it ends (the command that ran, the files changed). Where it does,
    # the steps are told by those, and the calls that only wrap them ("exec", "shell") are passed over.
    itemized = tool != "claude" and '"item_completed"' in text
    asked = [0]       # when its user said the last thing: the work it is at began then

    def stamped(row):
        try:
            asked[0] = int(datetime.datetime.fromisoformat(str(row.get("timestamp")).replace("Z", "+00:00")).timestamp() * 1000)
        except (TypeError, ValueError):
            pass

    for row in rows(text):
        if tool == "claude":
            message = row.get("message")
            if row.get("type") == "came-back":
                came(steps.get(row.get("whose")), row.get("text"))
                continue
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
                        if row["type"] == "user":
                            stamped(row)
                elif part.get("type") == "tool_use" and row["type"] == "assistant":
                    given = part.get("input") if isinstance(part.get("input"), dict) else {}
                    made = step(brief(part.get("name"), given), given.get("command") if isinstance(given.get("command"), str) else "")
                    if made is not None and isinstance(part.get("id"), str):
                        steps[part["id"]] = made
                elif part.get("type") == "tool_result" and row["type"] == "user":
                    back = part.get("content")
                    came(steps.get(part.get("tool_use_id")), back if isinstance(back, str) else "\n".join(
                        one.get("text") or "" for one in back if isinstance(one, dict)) if isinstance(back, list) else "")
                # what it says of its work between the steps, where Claude Code keeps a summary of it
                elif part.get("type") == "thinking" and row["type"] == "assistant" and isinstance(part.get("thinking"), str):
                    add("thought", part["thinking"])
        else:
            payload = row.get("payload")
            if not isinstance(payload, dict):
                continue
            if row.get("type") == "event_msg" and payload.get("type") == "item_completed":
                item = payload.get("item") if isinstance(payload.get("item"), dict) else {}
                if item.get("type") == "CommandExecution":
                    parsed = [one for one in item.get("parsed_cmd") or [] if isinstance(one, dict)] if isinstance(item.get("parsed_cmd"), list) else []
                    reads = [one.get("name") or one.get("path") for one in parsed if one.get("type") == "read" and isinstance(one.get("name") or one.get("path"), str)]
                    ran = next((one["cmd"] for one in parsed if isinstance(one.get("cmd"), str) and one["cmd"]), None)
                    if ran is None:
                        ran = " ".join(str(part) for part in item.get("command") or []) if isinstance(item.get("command"), list) else ""
                    if parsed and len(reads) == len(parsed):
                        add("tool", "读取：" + "、".join(reads[:3]) + (" 等 %d 个文件" % len(reads) if len(reads) > 3 else ""))
                    elif ran:
                        code = item.get("exit_code")
                        came(step("运行：" + ran[:160], ran if len(ran) > 160 else ""),
                             ("（退出码 %s）\n" % code if code not in (None, 0, "0") else "") + (item.get("aggregated_output") if isinstance(item.get("aggregated_output"), str) else ""))
                elif item.get("type") == "FileChange" and isinstance(item.get("changes"), dict):
                    names = [name for name in (re.split(r"[/\\]", key.rstrip("/\\"))[-1] for key in item["changes"]) if name]
                    if names:
                        add("tool", "修改：" + "、".join(names[:3]) + (" 等 %d 个文件" % len(names) if len(names) > 3 else ""))
                continue
            if row.get("type") != "response_item":
                continue
            if payload.get("type") == "message" and payload.get("role") in ("user", "assistant"):
                for part in payload.get("content") if isinstance(payload.get("content"), list) else []:
                    if isinstance(part, dict) and isinstance(part.get("text"), str) \
                            and (payload["role"] == "assistant" or not part["text"].lstrip().startswith("<")):
                        add(payload["role"], part["text"])
                        if payload["role"] == "user":
                            stamped(row)
            elif payload.get("type") in ("function_call", "custom_tool_call", "local_shell_call"):
                called = payload.get("name") or "shell"
                if not (itemized and (payload["type"] == "local_shell_call" or called in ("exec", "shell", "local_shell"))):
                    add("tool", brief(called, None))
            elif payload.get("type") == "reasoning" and isinstance(payload.get("summary"), list):
                # the summary Codex keeps of its reasoning: what it says of its work between the steps
                add("thought", "\n\n".join(one["text"] for one in payload["summary"]
                                           if isinstance(one, dict) and isinstance(one.get("text"), str) and one["text"].strip()))
    kept = talk(out, SAID_MESSAGES, SAID_RUN)
    # What came back is given with the latest steps only: a window that follows a conversation at work is sent it
    # anew whenever it changes, and the output of every step of a long one is too much for that.
    with_result = 0
    for entry in reversed(kept):
        if "result" in entry:
            with_result += 1
            if with_result > SAID_RESULTS:
                del entry["result"]
    more = len(kept) < len(out) or os.path.getsize(path) > limit
    return {"messages": kept, "more": more, "updated": int(os.path.getmtime(path) * 1000), "asked": asked[0]}


def first(back):
    """The first lines of what came back from a step, as a terminal shows them: six lines at most, none longer
    than two hundred letters, and a mark where there is more."""
    back = (back or "").replace("\r\n", "\n").lstrip("\r\n").rstrip()
    if not back:
        return ""
    lines, kept, letters = back.split("\n"), [], 0
    for one in lines:
        if len(kept) == 6 or letters > 500:
            break
        cut = one.rstrip()
        kept.append(cut if len(cut) <= 200 else cut[:200] + "…")
        letters += len(kept[-1])
    return "\n".join(kept) + ("\n…" if len(kept) < len(lines) else "")


def begins(row, start):
    """The beginning of what a tool gave back, taken from a row without reading the row: the text that follows
    `"content":` after `start` (a string, or the first text of a list), as far as a few hundred letters go."""
    at = row.find('"content":', start) if start >= 0 else -1
    if at < 0:
        return ""
    at += 10
    if row[at:at + 1] == "[":
        at = row.find('"text":"', at)
        if at < 0 or at > start + 400:
            return ""
        at += 7
    if row[at:at + 1] != '"':
        return ""
    text, i = [], at + 1
    while i < len(row) and len(text) < 1600:
        c = row[i]
        if c == '"':
            break
        if c != "\\":
            text.append(c)
        else:
            i += 1
            if i >= len(row):
                break
            e = row[i]
            if e == "n":
                text.append("\n")
            elif e == "t":
                text.append("\t")
            elif e == "u":
                try:
                    text.append(chr(int(row[i + 1:i + 5], 16)))
                    i += 4
                except ValueError:
                    pass
            elif e not in "rbf":
                text.append(e)
        i += 1
    return "".join(text)


def talk(said, most, runs):
    """The last `most` things said by the user and the tool, oldest first, with what the tool
    did between them. A conversation at work is mostly things done: counted with the rest they
    would leave no room for what was said. Of one unbroken run of them the last `runs` are
    given, and a line says how many came before."""
    kept, spoken, run, passed = [], 0, 0, 0
    for entry in reversed(said):
        if entry["role"] == "tool":
            run += 1
            if run > runs:
                passed += 1
                continue
        else:
            if passed:
                kept.append({"role": "tool", "text": "更早的 %d 个操作" % passed})
            passed, run, spoken = 0, 0, spoken + 1
            if spoken > most:
                break
        kept.append(entry)
    if passed:
        kept.append({"role": "tool", "text": "更早的 %d 个操作" % passed})
    kept.reverse()
    return kept


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


HANDOVER_ASK = "【现在的要求】"
MAX_HANDOVERS = 500
MAX_HANDOVER_TURNS = 50


def is_handover_message(text):
    """True when `text` is an in-place hand-over opening message (`【对话交接】...【现在的要求】\\n...`)."""
    return (isinstance(text, str) and text.startswith("【对话交接】")
            and ("\n\n" + HANDOVER_ASK + "\n") in text)


def strip_handover_ask(text):
    """Returns the owner's own words from an in-place hand-over opening message, or `text` unchanged."""
    if not isinstance(text, str) or not text.startswith("【对话交接】"):
        return text if isinstance(text, str) else ""
    sep = "\n\n" + HANDOVER_ASK + "\n"
    at = text.find(sep)
    return text[at + len(sep):] if at >= 0 else text


def split_visits(messages, expected_visits, starts_with_handover, truncated=False):
    """Split a tool session transcript that was resumed across non-adjacent turns using handover
    opening messages (`【对话交接】...【现在的要求】\\n...`) as visit boundaries."""
    if expected_visits <= 0:
        return []
    if expected_visits == 1:
        return [list(messages)]
    chunks, cur = [], []
    for msg in messages:
        if msg.get("role") == "user" and is_handover_message(msg.get("text")) and cur:
            chunks.append(cur)
            cur = []
        cur.append(msg)
    if cur:
        chunks.append(cur)
    if len(chunks) == expected_visits:
        return chunks
    if len(chunks) > expected_visits:
        tail = [m for part in chunks[expected_visits - 1:] for m in part]
        return chunks[:expected_visits - 1] + [tail]
    head_trimmed = bool(chunks) and (truncated or (
        not starts_with_handover and chunks[0][0].get("role") == "user"
        and is_handover_message(chunks[0][0].get("text"))))
    pad = [[] for _ in range(expected_visits - len(chunks))]
    return (pad + chunks) if head_trimmed else (chunks + pad)


class Handovers:
    """Which conversations belong to the same in-place handover thread (`handovers.json`, ordered list of
    tool+session turns): listed once under the first conversation's title, recalled chronologically, and
    resuming an earlier tool's prior session when switched back to."""

    def __init__(self, data):
        self.file = os.path.join(data, "handovers.json")
        self.lock = threading.Lock()
        self.threads = []
        try:
            with open(self.file, encoding="utf-8") as stream:
                read = json.load(stream)
            if isinstance(read, dict) and isinstance(read.get("threads"), list):
                for item in read["threads"]:
                    if not isinstance(item, dict):
                        continue
                    raw_turns = item.get("turns") if isinstance(item.get("turns"), list) else []
                    turns = []
                    for one in raw_turns:
                        if not isinstance(one, dict):
                            continue
                        t, s = str(one.get("tool") or ""), str(one.get("session") or "")
                        if self._valid_turn(t, s) and (not turns or not self._same(turns[-1], t, s)):
                            turns.append({"tool": t, "session": s})
                    if len(turns) >= 2:
                        self.threads.append({"title": str(item.get("title") or "").strip(), "turns": turns[-MAX_HANDOVER_TURNS:]})
                del self.threads[:-MAX_HANDOVERS]
            elif isinstance(read, dict) and isinstance(read.get("links"), list):
                # Migrate v1 sequential `links` entries into ordered threads.
                pending = []
                for one in read["links"]:
                    if isinstance(one, dict):
                        pending.append({k: str(one.get(k) or "") for k in ("tool", "session", "from_tool", "from", "title")})
                while pending:
                    progressed = False
                    rest = []
                    for link in pending:
                        if self._valid_turn(link["tool"], link["session"]) and self._valid_turn(link["from_tool"], link["from"]):
                            if self._attach(link["tool"], link["session"], link["from_tool"], link["from"], link["title"]):
                                progressed = True
                                continue
                        rest.append(link)
                    if not progressed:
                        break
                    pending = rest
                del self.threads[:-MAX_HANDOVERS]
        except (OSError, ValueError):
            pass

    @staticmethod
    def _valid_turn(tool, session):
        return 0 < len(tool) <= 20 and 0 < len(session) <= 128

    @staticmethod
    def _same(turn, tool, session):
        return turn["tool"] == tool and turn["session"].lower() == session.lower()

    def _find_thread_idx(self, tool, session):
        for i in range(len(self.threads) - 1, -1, -1):
            if any(self._same(t, tool, session) for t in self.threads[i]["turns"]):
                return i
        return -1

    def _attach(self, tool, session, from_tool, from_id, title):
        title = str(title or "").strip()
        if not (self._valid_turn(tool, session) and self._valid_turn(from_tool, from_id)):
            return False
        if tool == from_tool and session.lower() == from_id.lower():
            return False
        from_idx = self._find_thread_idx(from_tool, from_id)
        to_idx = self._find_thread_idx(tool, session)
        if from_idx >= 0:
            thread = self.threads[from_idx]
            if not self._same(thread["turns"][-1], from_tool, from_id):
                return False
            if to_idx >= 0 and to_idx != from_idx:
                return False
            thread["turns"].append({"tool": tool, "session": session})
            if len(thread["turns"]) > MAX_HANDOVER_TURNS:
                del thread["turns"][:-MAX_HANDOVER_TURNS]
            if not thread["title"] and title:
                thread["title"] = title
            self.threads.append(self.threads.pop(from_idx))
        else:
            if to_idx >= 0:
                return False
            self.threads.append({
                "title": title,
                "turns": [{"tool": from_tool, "session": from_id}, {"tool": tool, "session": session}],
            })
        if len(self.threads) > MAX_HANDOVERS:
            del self.threads[:-MAX_HANDOVERS]
        return True

    def add(self, tool, session, from_tool, from_id, title):
        tool, session = str(tool or ""), str(session or "")
        from_tool, from_id = str(from_tool or ""), str(from_id or "")
        with self.lock:
            if not self._attach(tool, session, from_tool, from_id, title):
                return False
            write_private(self.file + ".tmp", json.dumps({"threads": self.threads}, ensure_ascii=False))
            os.replace(self.file + ".tmp", self.file)
            return True

    def turns_of(self, tool, session):
        tool, session = str(tool or ""), str(session or "")
        with self.lock:
            idx = self._find_thread_idx(tool, session)
            if idx < 0:
                return [{"tool": tool, "session": session}] if tool and session else []
            return [dict(t) for t in self.threads[idx]["turns"]]

    def previous_session(self, from_tool, from_id, target_tool):
        from_tool, from_id, target_tool = str(from_tool or ""), str(from_id or ""), str(target_tool or "")
        if not (from_tool and from_id and target_tool):
            return ""
        with self.lock:
            idx = self._find_thread_idx(from_tool, from_id)
            if idx < 0:
                return ""
            turns = self.threads[idx]["turns"]
            if not self._same(turns[-1], from_tool, from_id):
                return ""
            for t in reversed(turns[:-1]):
                if t["tool"] == target_tool and not self._same(t, from_tool, from_id):
                    return t["session"]
            return ""

    def gone_on(self, tool, session):
        tool, session = str(tool or ""), str(session or "")
        if not (tool and session):
            return False
        with self.lock:
            idx = self._find_thread_idx(tool, session)
            if idx < 0:
                return False
            return not self._same(self.threads[idx]["turns"][-1], tool, session)

    def title(self, tool, session):
        tool, session = str(tool or ""), str(session or "")
        with self.lock:
            idx = self._find_thread_idx(tool, session)
            return self.threads[idx]["title"] if idx >= 0 else ""


def trash(path):
    """Moves a file or a folder into the user's trash, the way the desktop's file managers do
    (the XDG trash: the thing itself, and a note of where it lay). Nothing is destroyed."""
    base = os.environ.get("XDG_DATA_HOME") or os.path.join(os.path.expanduser("~"), ".local", "share")
    files, notes = os.path.join(base, "Trash", "files"), os.path.join(base, "Trash", "info")
    os.makedirs(files, mode=0o700, exist_ok=True)
    os.makedirs(notes, mode=0o700, exist_ok=True)
    name, at = os.path.basename(path), 1
    while os.path.lexists(os.path.join(files, name)) or os.path.lexists(os.path.join(notes, name + ".trashinfo")):
        at += 1
        name = "%s.%d" % (os.path.basename(path), at)
    with open(os.path.join(notes, name + ".trashinfo"), "w", encoding="utf-8") as stream:
        stream.write("[Trash Info]\nPath=%s\nDeletionDate=%s\n" % (quote(path), time.strftime("%Y-%m-%dT%H:%M:%S")))
    shutil.move(path, os.path.join(files, name))


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

    def __init__(self, marks=None, handovers=None):
        self.marks = marks
        self.handovers = handovers
        self.home = os.path.expanduser("~")
        self.titles = {}                # path -> (mtime, size, title)
        self.folders = {}               # codex rollout path -> the folder it worked in
        self.used = {}                  # the same path, or a folder of Claude Code's -> by what it is used
        self.spoken = {}                # a folder of Claude Code's conversations -> the folder they were held in
        self.offers = {}                # every folder with conversations, as found by this look: by what it is used
        self.candidates = []            # ... the ones that are no projects yet, as told to the phone
        self.exists = os.path.isdir     # (a folder that is gone is not offered)
        self.names = (None, {})         # codex's own index of names, by its mtime
        self.found = []
        self.version = 0                # the list changed: the phone is told it anew

    def claude_home(self):
        """Where Claude Code keeps its conversations: its folder in the person's home, or the
        one CLAUDE_CONFIG_DIR names, as Claude Code itself reads it."""
        return os.environ.get("CLAUDE_CONFIG_DIR") or os.path.join(self.home, ".claude")

    def qoder_home(self):
        """Where qoder keeps its conversations: its folder in the person's home, or the one
        QODER_HOME names."""
        return os.environ.get("QODER_HOME") or os.path.join(self.home, ".qoder")

    def pi_home(self):
        """Where pi keeps its conversations: its agent folder in the person's home, or the
        one PI_HOME names."""
        return os.environ.get("PI_HOME") or os.path.join(self.home, ".pi", "agent")

    def known(self, session, tool, name):
        return next((s for s in self.found if s["id"] == session and s["tool"] == tool and s["dir"] == name), None)

    def read_folder(self, session, tool, folder, since=""):
        """Reads a conversation directly from its working folder (for both project dirs and
        temporary compare worktrees). Returns (answer, stamp)."""
        if not session_id_ok(tool, session) or not folder:
            raise OpError("电脑上没有找到这个对话，请刷新后重试")
        try:
            if tool == "opencode":
                answer = said_opencode(session)
                stamp = "u%d" % answer["updated"]
                return ({"same": True} if since and since == stamp else answer), stamp
            if tool == "cline":
                answer = said_cline(session)
                stamp = "u%d" % answer["updated"]
                return ({"same": True} if since and since == stamp else answer), stamp
            if tool == "pi":
                found = glob.glob(os.path.join(self.pi_home(), "sessions",
                                               pi_slug(folder), "*_%s.jsonl" % session))
                info = os.stat(found[0])
                stamp = "%d.%d" % (info.st_size, info.st_mtime_ns)
                return ({"same": True} if since and since == stamp else said_pi(found[0])), stamp
            if tool == "claude":
                found = [os.path.join(self.claude_home(), "projects", re.sub(r"[^a-zA-Z0-9]", "-", folder), session + ".jsonl")]
                read_tool = "claude"
            elif tool == "qoder":
                found = glob.glob(os.path.join(self.qoder_home(), "projects",
                                               re.sub(r"[^a-zA-Z0-9]", "-", folder), session + ".jsonl"))
                read_tool = "claude"
            else:
                home = os.environ.get("CODEX_HOME") or os.path.join(self.home, ".codex")
                found = glob.glob(os.path.join(home, "sessions", "**", "rollout-*%s.jsonl" % session), recursive=True)
                read_tool = "codex"
            info = os.stat(found[0])
            stamp = "%d.%d" % (info.st_size, info.st_mtime_ns)
            return ({"same": True} if since and since == stamp else said(found[0], read_tool)), stamp
        except (OSError, IndexError, sqlite3.Error):
            raise OpError("读不到这个对话的内容")

    def updated_in_folder(self, session, tool, folder):
        """Latest activity stamp (epoch ms) of a conversation in `folder`, or 0 if not yet written."""
        if not session_id_ok(tool, session) or not folder:
            return 0
        try:
            if tool == "opencode":
                db = opencode_db()
                if not os.path.exists(db):
                    return 0
                con = sqlite3.connect("file:%s?mode=ro" % db, uri=True)
                try:
                    con.execute("pragma busy_timeout=1000")
                    row = con.execute("select time_updated from session_v2 where id = ?", (session,)).fetchone()
                finally:
                    con.close()
                return int(row[0] or 0) if row else 0
            if tool == "cline":
                return int(said_cline(session).get("updated") or 0)
            if tool == "pi":
                found = glob.glob(os.path.join(self.pi_home(), "sessions",
                                               pi_slug(folder), "*_%s.jsonl" % session))
                return int(os.stat(found[0]).st_mtime * 1000) if found else 0
            if tool == "qoder":
                saved = os.path.join(self.qoder_home(), "projects", re.sub(r"[^a-zA-Z0-9]", "-", folder))
                path = os.path.join(saved, session + ".jsonl")
                if os.path.isfile(path):
                    return int(os.stat(path).st_mtime * 1000)
                state_path = os.path.join(saved, session, "state.json")
                if os.path.isfile(state_path):
                    with open(state_path, encoding="utf-8") as stream:
                        state = json.load(stream)
                    return iso_ms(state.get("updatedAt")) or int(os.stat(state_path).st_mtime * 1000)
                return 0
        except (OSError, ValueError, sqlite3.Error):
            return 0
        return 0

    def slice_turns(self, turns, name, dirs):
        """Split every turn of a handover thread into its own message list, reading each unique file once."""
        folder = dirs.get(name) if name else None
        counts, first_at = {}, {}
        for idx, turn in enumerate(turns):
            t, s = turn.get("tool") or "", turn.get("session") or ""
            if not t or not s:
                continue
            key = (t, s.lower())
            counts[key] = counts.get(key, 0) + 1
            if key not in first_at:
                first_at[key] = (idx, s)
        visits_by_key, more_by_key = {}, {}
        for key, count in counts.items():
            t = key[0]
            first_idx, s = first_at[key]
            if not folder:
                visits_by_key[key], more_by_key[key] = [[] for _ in range(count)], False
                continue
            try:
                answer, _ = self.read_folder(s, t, folder)
                raw = list(answer.get("messages") or [])
                raw_more = bool(answer.get("more"))
                visits_by_key[key] = split_visits(raw, count, first_idx > 0, raw_more)
                more_by_key[key] = raw_more
            except (OpError, OSError):
                visits_by_key[key], more_by_key[key] = [[] for _ in range(count)], False
        next_idx, slices, first_more = {}, [], False
        for idx, turn in enumerate(turns):
            t, s = turn.get("tool") or "", turn.get("session") or ""
            if not t or not s:
                slices.append([])
                continue
            key = (t, s.lower())
            pos = next_idx.get(key, 0)
            next_idx[key] = pos + 1
            parts = visits_by_key.get(key) or []
            slices.append(list(parts[pos]) if pos < len(parts) else [])
            if idx == 0:
                first_more = bool(more_by_key.get(key))
        return slices, first_more

    def line_messages(self, session, tool, name, dirs, next_tool=""):
        """What was said across a whole handover line, oldest first, with `switch` rows between parts."""
        turns = self.handovers.turns_of(tool, session) if self.handovers else ([{"tool": tool, "session": session}] if tool and session else [])
        slices, more = self.slice_turns(turns, name, dirs)
        messages = []
        for idx, turn in enumerate(turns):
            if idx > 0:
                messages.append({"role": "switch", "tool": turn["tool"], "text": TOOL_LABELS.get(turn["tool"], turn["tool"])})
            messages.extend(slices[idx])
        if next_tool:
            messages.append({"role": "switch", "tool": next_tool, "text": TOOL_LABELS.get(next_tool, next_tool)})
        return messages, more

    def delta_messages(self, from_session, from_tool, target_tool, target_session, name, dirs):
        """What happened since `target_tool:target_session` paused in this handover thread."""
        if not self.handovers:
            return [], []
        turns = self.handovers.turns_of(from_tool, from_session)
        last_own = -1
        for i in range(len(turns) - 1, -1, -1):
            if turns[i]["tool"] == target_tool and turns[i]["session"].lower() == target_session.lower():
                last_own = i
                break
        if last_own < 0 or last_own >= len(turns) - 1:
            return [], []
        slices, _ = self.slice_turns(turns, name, dirs)
        delta, between_tools = [], []
        for i in range(last_own + 1, len(turns)):
            t = turns[i]["tool"]
            if t not in between_tools:
                between_tools.append(t)
            delta.extend(slices[i])
        return delta, between_tools

    def open_outside(self, session, tool, terminals):
        """Whether a saved `tool`/`session` is currently held open by an external process."""
        session = str(session or "")
        if not session:
            return False
        if tool == "claude":
            return any(s.lower() == session.lower() for s in self._claude_open(terminals))
        if tool == "codex":
            root = os.path.join(os.environ.get("CODEX_HOME") or os.path.join(self.home, ".codex"), "sessions")
            return any(os.path.basename(p)[:-6][-36:].lower() == session.lower() and owner_of(pid, terminals) is None
                       for p, pid in self._codex_open(root).items())
        return False

    def read(self, session, tool, name, dirs, since=""):
        """What was last said in a conversation of a project, for the phone to look at. A page
        that follows it says what it has (`since`): it is given the conversation anew only
        when its file has changed."""
        saved = self.known(session, tool, name) if session_id_ok(tool, session) else None
        # one that is open in a folder that is no project is read where it is
        folder = dirs.get(name) if name else saved and saved["live"] and saved["folder"]
        if saved is None or not folder:
            raise OpError("电脑上没有找到这个对话，请刷新后重试")
        turns = self.handovers.turns_of(tool, session) if self.handovers else []
        if len(turns) > 1 and name:
            stamp_parts, seen = [], set()
            for turn in turns:
                key = (turn["tool"], turn["session"].lower())
                if not turn["session"] or key in seen:
                    continue
                seen.add(key)
                try:
                    _, part_stamp = self.read_folder(turn["session"], turn["tool"], folder, "")
                    stamp_parts.append(part_stamp)
                except (OpError, OSError):
                    pass
            if not stamp_parts:
                _, base_stamp = self.read_folder(session, tool, folder, "")
                stamp_parts.append(base_stamp)
            stamp = "%s.h%d" % (",".join(stamp_parts), len(turns))
            if since and since == stamp:
                return {"same": True, "stamp": stamp, "title": saved["title"], "status": saved.get("status", "")}
            messages, more = self.line_messages(session, tool, name, dirs)
            return {"messages": messages, "more": more, "stamp": stamp, "title": saved["title"], "status": saved.get("status", "")}
        answer, stamp = self.read_folder(session, tool, folder, since)
        return dict(answer, stamp=stamp, title=saved["title"], status=saved.get("status", ""))

    @staticmethod
    def used_by(*said):
        """By what a conversation is used, from what its file says started it (Claude Code's
        `entrypoint`; Codex's `source` and `originator`): an editor, or a terminal. (The tools
        have no desktop app on Linux.)"""
        said = " ".join(said).lower()
        return "编辑器" if "vscode" in said or "jetbrains" in said else "终端"

    def _offer(self, offers, dirs, folder, tool, at, live, kind):
        """Notes a folder a conversation was found in, a project or not: with which tool and by
        what it is used."""
        folder = os.path.normpath(folder) if folder else ""
        if not folder.startswith("/"):
            return
        item = offers.setdefault(folder, {"path": folder, "name": tidy(os.path.basename(folder)), "updated": 0,
                                          "live": False, "tools": [], "kinds": []})
        item["updated"] = max(item["updated"], int(at * 1000))
        item["live"] = item["live"] or live
        for key, value in (("tools", tool), ("kinds", kind)):
            if value not in item[key]:
                item[key].append(value)

    def _claude_folders(self, dirs, offers):
        """The folders Claude Code has saved conversations for, read from the newest of each."""
        for saved in glob.glob(os.path.join(self.claude_home(), "projects", "*", "")):
            name = os.path.basename(os.path.dirname(saved))
            newest = None
            for path in glob.glob(os.path.join(saved, "*.jsonl")):
                try:
                    info = os.stat(path)
                except OSError:
                    continue
                if newest is None or info.st_mtime > newest[0].st_mtime:
                    newest = (info, path)
            if newest is None:
                continue
            if name not in self.spoken:
                try:
                    head = read_part(newest[1], False, 1 << 20)
                except OSError:
                    continue
                row = next((r for r in rows(head) if isinstance(r.get("cwd"), str) and r["cwd"]), {})
                self.spoken[name] = row.get("cwd", "")
                self.used["claude\n" + name] = self.used_by(str(row.get("entrypoint") or ""))
            folder = self.spoken.get(name, "")
            if folder and re.sub(r"[^a-zA-Z0-9]", "-", folder) == name:
                self._offer(offers, dirs, folder, "claude", newest[0].st_mtime, False, self.used.get("claude\n" + name, "终端"))

    def delete(self, session, tool, dirs, terminals, archive=None):
        """Puts a saved conversation of a project away: one of Codex's by Codex itself (`codex
        archive`; `codex unarchive` brings it back), one of Claude Code's into the trash, with the
        folder of what belongs to it. Not one that a program has open."""
        saved = next((s for s in self.found if s["id"] == session and s["tool"] == tool), None) if UUID_RE.match(session) else None
        folder = dirs.get(saved["dir"]) if saved else None
        if not folder:
            raise OpError("电脑上没有找到这个对话，请刷新后重试")
        if saved["live"] or any(live.session == session and not live.closed for live in terminals):
            raise OpError("这段对话正开着，请先结束它再删除")
        if tool == "claude":
            path = os.path.join(self.claude_home(), "projects", re.sub(r"[^a-zA-Z0-9]", "-", folder), session + ".jsonl")
            if not os.path.isfile(path):
                raise OpError("电脑上没有找到这个对话，请刷新后重试")
            try:
                trash(path)
                if os.path.isdir(path[:-6]):
                    trash(path[:-6])
            except OSError:
                raise OpError("没有权限移走这段对话的文件")
        else:
            program = find_tool("codex")
            if not program:
                raise OpError("电脑上没有找到 Codex，对话没有删除")
            # This runs while the agent's state lock is held: a tool that never answers must
            # not hold every report and every other phone operation for half a minute.
            archive = archive or (lambda: subprocess.run([program, "archive", session], stdin=subprocess.DEVNULL, capture_output=True, timeout=10))
            try:
                done = archive()
            except (OSError, subprocess.TimeoutExpired):
                raise OpError("Codex 没有及时归档这段对话，请稍后重试")
            if done.returncode != 0:
                said = (done.stdout + done.stderr).decode("utf-8", "replace").strip()[-160:]
                raise OpError("Codex 没有归档这段对话" + ("：" + said if said else "，请确认 Codex 是带 archive 命令的版本"))
        if self.marks:
            for kind in MARKS:
                self.marks.set(kind, tool, session, False)
        self.found = [s for s in self.found if not (s["id"] == session and s["tool"] == tool)]

    def scan(self, dirs, terminals):
        """Every conversation the tools on this computer have saved, looked up from their own
        stores. It runs on its own thread: reading them takes a while, and no report, key press
        or file read waits for it. What it finds is published in one piece at the end, so a
        reader sees a whole list, or the one before it — never one half read."""
        found = []
        offers = {}
        if len(self.folders) > 3000 or len(self.spoken) > 500:
            # (what the folders are used by is read with the folders, and forgotten with them)
            self.folders.clear(), self.spoken.clear(), self.used.clear()
        # Each store is a tool's own: a broken one must not hide what the others found.
        # Anything can be wrong in them (a list where an object is expected, a number where
        # a string is), so each is read in its own try and the rest still gets published.
        broken = []
        for look in (lambda: self._claude(dirs, terminals, offers),
                     lambda: self._claude_folders(dirs, offers),
                     lambda: self._codex(dirs, terminals, offers),
                     lambda: self._opencode(dirs, terminals),
                     lambda: self._qoder(dirs, terminals),
                     lambda: self._pi(dirs, terminals),
                     lambda: self._cline(dirs, terminals)):
            try:
                found += look() or []
            except Exception as exc:        # never fatal: one tool's store is unreadable
                broken.append(type(exc).__name__)
        if broken:
            say("对话列表读取失败：%s" % ", ".join(broken))
        # the ones a project can still be made of: no project yet, no folder of the system's, and there
        candidates = [dict(c, tools=" ".join(c["tools"])) for c in sorted(
            (c for c in offers.values() if elsewhere(c["path"]) and c["path"] not in dirs.values() and self.exists(c["path"])),
            key=lambda c: (not c["live"], -c["updated"]))[:40]]
        if self.handovers:
            for entry in found:
                first_title = self.handovers.title(entry["tool"], entry["id"])
                if first_title:
                    entry["title"] = first_title
            found = [entry for entry in found if not self.handovers.gone_on(entry["tool"], entry["id"])]
        for entry in found:
            for kind in MARKS:
                entry[kind] = bool(self.marks and self.marks.has(kind, entry["tool"], entry["id"]))
        # the ones a program has open and the ones their owner pinned are listed whatever their age
        found.sort(key=lambda entry: (not (entry["live"] or entry["pinned"]), -entry["updated"]))
        if found != self.found:
            self.found = found
            self.version += 1
        self.offers, self.candidates = offers, candidates
        return found

    def told(self, projects):
        """The projects, each with the tools and by what its folder is used, as its conversations say."""
        for project in projects:
            use = self.offers.get(os.path.normpath(project["path"]), {})
            project["tools"], project["kinds"] = " ".join(use.get("tools", [])), list(use.get("kinds", []))
        return projects

    def _claude_kind(self, path):
        """By what a conversation of Claude Code's was started: every row of its file says so."""
        if path not in self.used:
            try:
                found = re.search(r'"entrypoint"\s*:\s*"([^"]*)"', read_part(path, False, 64 * 1024))
            except OSError:
                return "终端"
            self.used[path] = self.used_by(found.group(1) if found else "")
        return self.used[path]

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

    def _claude(self, dirs, terminals, offers):
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
                    found.append(self._entry(session, "claude", name, title, info, session in taken, kind=self._claude_kind(path)))
                    self._offer(offers, dirs, folder, "claude", info.st_mtime, session in taken, self._claude_kind(path))
        listed = {entry["id"] for entry in found}
        for session, folder in taken.items():
            folder = os.path.normpath(folder) if folder else ""
            if session in listed or not elsewhere(folder) or folder in dirs.values():
                continue
            self._offer(offers, dirs, folder, "claude", time.time(), True, "终端")
            path = os.path.join(self.claude_home(), "projects", re.sub(r"[^a-zA-Z0-9]", "-", folder), session + ".jsonl")
            try:
                info = os.stat(path)
            except OSError:
                continue                # nothing has been said in it yet
            title = self._title(path, info)
            if title:
                found.append(self._entry(session, "claude", "", title, info, True, folder, self._claude_kind(path)))
        return found

    def _codex(self, dirs, terminals, offers):
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
                # the folder is named in the first row, which may be long, or cut off by the part
                # that is read: it is looked for as text. (An earlier Codex wrote its instructions
                # before the folder: where the first part does not name it, more is read.)
                field = lambda name, text: (re.search(r'"%s"\s*:\s*("(?:[^"\\]|\\.)*")' % name, text) or [None, '""'])[1]
                try:
                    head = read_part(path, False, 16 * 1024)
                    if field("cwd", head) == '""':
                        head = read_part(path, False, 160 * 1024)
                    told = {name: json.loads(field(name, head)) for name in ("cwd", "source", "originator")}
                except (OSError, ValueError):
                    continue
                # a helper that a conversation started for itself is part of that conversation
                self.folders[path] = "" if '"parent_thread_id"' in head else str(told["cwd"] or "")
                self.used[path] = self.used_by(told["source"], told["originator"])
            folder = self.folders[path]
            name = next((n for n, p in dirs.items() if folder and os.path.normpath(folder) == p), None)
            pid = open_now.get(path)
            owner = owner_of(pid, terminals) if pid else None
            live = pid is not None and owner is None
            if folder:
                self._offer(offers, dirs, folder, "codex", info.st_mtime, live, self.used.get(path, "终端"))
            if name is None:
                # one that a program has open in a folder that is no project is listed with its folder
                if live and elsewhere(os.path.normpath(folder) if folder else ""):
                    title = tidy(str(self.names[1].get(session) or "")) \
                        or "Codex 对话 " + time.strftime("%m-%d %H:%M", time.localtime(info.st_mtime))
                    found.append(self._entry(session, "codex", "", title, info, True, os.path.normpath(folder), self.used.get(path, "终端")))
                continue
            if owner is not None and owner.tool == "codex" and not owner.session:
                owner.session = session
            if not live and counts.get(name, 0) >= 15:
                continue
            if not live:
                counts[name] = counts.get(name, 0) + 1
            title = tidy(str(self.names[1].get(session) or "")) \
                or "Codex 对话 " + time.strftime("%m-%d %H:%M", time.localtime(info.st_mtime))
            found.append(self._entry(session, "codex", name, title, info, live, kind=self.used.get(path, "终端")))
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

    def _opencode(self, dirs, terminals):
        """Conversations opencode kept in its SQLite store, and the ones a TUI of this user
        holds open (--session names the conversation it works in, wherever it was started)."""
        held = {}
        for cmd in glob.glob("/proc/[0-9]*/cmdline"):
            try:
                with open(cmd, "rb") as stream:
                    parts = stream.read().split(b"\0")
            except OSError:
                continue
            if b"opencode" not in parts[0]:
                continue
            args = [p.decode("utf-8", "replace") for p in parts if p]
            for at, arg in enumerate(args):
                if arg in ("-s", "--session") and at + 1 < len(args):
                    held[args[at + 1]] = int(os.path.basename(os.path.dirname(cmd)))
        db = opencode_db()
        if not os.path.exists(db):
            return []
        con = sqlite3.connect("file:%s?mode=ro" % db, uri=True)
        try:
            con.execute("pragma busy_timeout=1000")
            rows = con.execute(
                "select s.id, s.title, s.directory, s.time_updated, p.worktree "
                "from session_v2 s left join project p on p.id = s.project_id "
                "where s.time_archived is null "
                "order by s.time_updated desc limit 300").fetchall()
        finally:
            con.close()
        found, counts = [], {}
        for session, title, directory, updated, worktree in rows:
            if not session_id_ok("opencode", session):
                continue
            folder = directory or worktree or ""
            name = next((n for n, p in dirs.items() if folder and os.path.normpath(folder) == p), None)
            if name is None:
                continue
            pid = held.get(session)
            owner = owner_of(pid, terminals) if pid else None
            if owner is not None and owner.tool == "opencode" and not owner.session:
                owner.session = session
            live = pid is not None and owner is None
            if not live and counts.get(name, 0) >= 15:
                continue
            if not live:
                counts[name] = counts.get(name, 0) + 1
            title = tidy(str(title or "")) \
                or "Opencode 对话 " + time.strftime("%m-%d %H:%M", time.localtime((updated or 0) / 1000))
            found.append(self._entry(session, "opencode", name, title,
                                     SimpleNamespace(st_mtime=(updated or 0) / 1000.0), live, folder))
        return found

    def _qoder(self, dirs, terminals):
        """Conversations qoder kept under its projects folder, named the way claude names
        its own, and the ones a qoder of this user holds open (--resume or --session-id
        names the conversation it works in, wherever it was started)."""
        held = {}
        for cmd in glob.glob("/proc/[0-9]*/cmdline"):
            try:
                with open(cmd, "rb") as stream:
                    parts = stream.read().split(b"\0")
            except OSError:
                continue
            if b"qoder" not in parts[0]:
                continue
            args = [p.decode("utf-8", "replace") for p in parts if p]
            for at, arg in enumerate(args):
                if arg in ("--resume", "-r", "--session-id") and at + 1 < len(args):
                    held[args[at + 1]] = int(os.path.basename(os.path.dirname(cmd)))
        root = os.path.join(self.qoder_home(), "projects")
        found, counts = [], {}
        for name, folder in dirs.items():
            saved = os.path.join(root, re.sub(r"[^a-zA-Z0-9]", "-", folder))
            recent = []
            for path in glob.glob(os.path.join(saved, "*.jsonl")):
                try:
                    recent.append((os.stat(path), path, "jsonl"))
                except OSError:
                    pass
            for path in glob.glob(os.path.join(saved, "*", "state.json")):
                try:
                    with open(path, encoding="utf-8") as stream:
                        state = json.load(stream)
                    stamp = iso_ms(state.get("updatedAt")) or int(os.stat(path).st_mtime * 1000)
                    recent.append((SimpleNamespace(st_mtime=stamp / 1000.0), path, "state"))
                except (OSError, ValueError):
                    continue
            recent.sort(key=lambda item: item[0].st_mtime, reverse=True)
            for info, path, kind in recent[:60]:
                if kind == "jsonl":
                    session = os.path.basename(path)[:-6]
                    title = self._qoder_title(path) \
                        or "Qoder 对话 " + time.strftime("%m-%d %H:%M", time.localtime(info.st_mtime))
                else:
                    session = os.path.basename(os.path.dirname(path))
                    title = "Qoder 对话 " + time.strftime("%m-%d %H:%M", time.localtime(info.st_mtime))
                if not UUID_RE.match(session):
                    continue
                pid = held.get(session)
                owner = owner_of(pid, terminals) if pid else None
                if owner is not None and owner.tool == "qoder" and not owner.session:
                    owner.session = session
                live = pid is not None and owner is None
                if not live and counts.get(name, 0) >= 15:
                    continue
                if not live:
                    counts[name] = counts.get(name, 0) + 1
                found.append(self._entry(session, "qoder", name, title, info, live, folder))
        return found

    def _qoder_title(self, path):
        """The first thing the person said in a qoder conversation, like claude's own title."""
        try:
            head = read_part(path, False, 64 * 1024)
        except OSError:
            return ""
        for row in rows(head):
            if row.get("type") != "user":
                continue
            message = row.get("message") if isinstance(row.get("message"), dict) else {}
            content = message.get("content")
            parts = content if isinstance(content, list) else []
            for part in parts:
                if isinstance(part, dict) and part.get("type") == "text" and isinstance(part.get("text"), str):
                    return tidy(part["text"].split("\n")[0])[:60]
        return ""

    def _pi(self, dirs, terminals):
        """Conversations pi kept under its sessions, in a folder named for the one they were
        worked in, and the ones a pi of this user holds open: a pi that was told which
        conversation to take names it in its own command line, and only a bare one stands
        for the newest conversation of its folder -- one it began or resumed after starting."""
        held_ids, held_dir = {}, {}
        for cmd in glob.glob("/proc/[0-9]*/cmdline"):
            try:
                with open(cmd, "rb") as stream:
                    parts = stream.read().split(b"\0")
            except OSError:
                continue
            # a pi calls itself pi; one started as `node .../pi-coding-agent/...` says so by its script
            if not (parts[0] == b"pi" or parts[0].endswith(b"/pi")
                    or any(b"pi-coding-agent" in p for p in parts[:2])):
                continue
            try:
                pid = int(os.path.basename(os.path.dirname(cmd)))
                cwd = os.readlink(os.path.join(os.path.dirname(cmd), "cwd"))
            except (OSError, ValueError):
                continue
            args = [p.decode("utf-8", "replace") for p in parts if p]
            named = [args[at + 1] for at, arg in enumerate(args[:-1])
                     if arg in ("--session", "--session-id", "--resume", "-r")
                     and UUID_RE.match(args[at + 1])]
            if named:
                for session in named:
                    held_ids[session] = pid
            else:
                # where it names no conversation, its own start tells which one it took up:
                # the bare pi that opened a phone conversation later than it started is not it
                try:
                    born = os.stat(os.path.dirname(cmd)).st_ctime
                except OSError:
                    born = 0
                held_dir[pi_slug(cwd)] = (pid, born)
        root = os.path.join(self.pi_home(), "sessions")
        found, counts = [], {}
        for name, folder in dirs.items():
            saved = os.path.join(root, pi_slug(folder))
            recent = []
            for path in glob.glob(os.path.join(saved, "*.jsonl")):
                try:
                    recent.append((os.stat(path), path))
                except OSError:
                    pass
            recent.sort(key=lambda item: item[0].st_mtime, reverse=True)
            for rank, (info, path) in enumerate(recent[:60]):
                session = os.path.basename(path)[:-6].rsplit("_", 1)[-1]
                if not UUID_RE.match(session):
                    continue
                pid = held_ids.get(session)
                if pid is None and rank == 0:
                    bare = held_dir.get(os.path.basename(saved))
                    if bare and info.st_mtime >= bare[1] - 2.0:
                        pid = bare[0]
                owner = owner_of(pid, terminals) if pid else None
                if owner is not None and owner.tool == "pi" and not owner.session:
                    owner.session = session
                live = pid is not None and owner is None
                if not live and counts.get(name, 0) >= 15:
                    continue
                if not live:
                    counts[name] = counts.get(name, 0) + 1
                title = self._pi_title(path) \
                    or "Pi 对话 " + time.strftime("%m-%d %H:%M", time.localtime(info.st_mtime))
                found.append(self._entry(session, "pi", name, title, info, live, folder))
        return found

    def _pi_title(self, path):
        """The first thing the person said in a pi conversation, like claude's own title."""
        try:
            head = read_part(path, False, 64 * 1024)
        except OSError:
            return ""
        for row in rows(head):
            if row.get("type") != "message" or not isinstance(row.get("message"), dict) \
                    or row["message"].get("role") != "user":
                continue
            parts = row["message"].get("content") if isinstance(row["message"].get("content"), list) else []
            for part in parts:
                if isinstance(part, dict) and part.get("type") == "text" and isinstance(part.get("text"), str):
                    return tidy(part["text"].split("\n")[0])[:60]
        return ""

    def _cline(self, dirs, terminals):
        """Conversations cline kept in its SQLite stores, and the ones a cline of this user
        holds open (its hub records the process, wherever it was started)."""
        held = {}
        for cmd in glob.glob("/proc/[0-9]*/cmdline"):
            try:
                with open(cmd, "rb") as stream:
                    parts = stream.read().split(b"\0")
            except OSError:
                continue
            if b"cline" not in parts[0]:
                continue
            args = [p.decode("utf-8", "replace") for p in parts if p]
            for at, arg in enumerate(args):
                if arg == "--id" and at + 1 < len(args):
                    held[args[at + 1]] = int(os.path.basename(os.path.dirname(cmd)))
        search = cline_db("session-search.db")
        if not os.path.exists(search):
            return []
        con = sqlite3.connect("file:%s?mode=ro" % search, uri=True)
        try:
            con.execute("pragma busy_timeout=1000")
            rows = con.execute(
                "select session_id, max(workspace_root), "
                "max(case when role = 'session' then title else '' end), max(started_at) "
                "from session_search group by session_id order by 4 desc limit 300").fetchall()
        finally:
            con.close()
        runs = cline_db("sessions.db")
        pids = {}
        if os.path.exists(runs):
            con = sqlite3.connect("file:%s?mode=ro" % runs, uri=True)
            try:
                con.execute("pragma busy_timeout=1000")
                for session, pid in con.execute("select session_id, pid from sessions "
                                                "where ended_at is null"):
                    if isinstance(pid, int) and cline_alive(pid):
                        pids[session] = pid
            except sqlite3.Error:
                pass
            finally:
                con.close()
        found, counts = [], {}
        for session, folder, title, started in rows:
            if not session_id_ok("cline", session):
                continue
            pid = held.get(session, pids.get(session))
            owner = owner_of(pid, terminals) if pid else None
            if owner is not None and owner.tool == "cline" and not owner.session:
                owner.session = session
            name = next((n for n, p in dirs.items() if folder and os.path.normpath(folder) == p), None)
            if name is None:
                continue
            live = pid is not None and owner is None
            if not live and counts.get(name, 0) >= 15:
                continue
            if not live:
                counts[name] = counts.get(name, 0) + 1
            title = tidy(str(title or "")) \
                or "Cline 对话 " + time.strftime("%m-%d %H:%M", time.localtime(iso_ms(started) / 1000))
            found.append(self._entry(session, "cline", name, title,
                                     SimpleNamespace(st_mtime=iso_ms(started) / 1000.0), live, folder))
        return found

    @staticmethod
    def _entry(session, tool, name, title, info, live, folder="", kind=""):
        return {"id": session, "tool": tool, "dir": name, "folder": folder, "kind": kind, "title": title, "updated": int(info.st_mtime * 1000),
                "live": live, "now": live,      # what a program has open here is a command line: open is in use
                "host": "cli" if live else "", "status": "", "can_takeover": False,
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
    if isinstance(at, bool) or not isinstance(at, (int, float)) or not math.isfinite(at) \
            or abs(time.time() - at) > OP_TTL:
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
        self.created = int(time.time() * 1000)
        self.brackets = False           # whether the program enabled bracketed paste (\x1b[?2004h)
        self.last_out_at = 0.0
        self.on_close = None
        self.wake = wake
        self.lock = threading.Lock()
        self.pending = []               # read from the program, not yet numbered
        self.pending_bytes = 0          # the reader's own buffer is bounded, like `output`
        self.output = []                # numbered, not yet acknowledged by the relay
        self.out_bytes = 0              # bytes in `output`, maintained by seal/trim
        self.seq = 0
        self.closing_at = None          # TERM sent; KILL follows if this lapses three seconds
        self.swept = False              # the tasks that hold the screen have been killed once
        self.needs_sweep = False        # asked for outside the state lock, taken once by report()
        self.closed = False             # the process has been reaped
        self.drained = False            # the reader delivered everything
        self.exit_code = None
        self.closed_at = None
        self.told_closed = False        # the relay has accepted a report that said it ended
        self.decoder = codecs.getincrementaldecoder("utf-8")(errors="replace")
        self.stopping = False            # disposed: the reader leaves the file description alone
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
            except BaseException as error:
                # why it did not start, on the terminal's own screen, before the exit code
                try:
                    os.write(2, ("无法启动：%s: %s\n" % (type(error).__name__, error)).encode("utf-8", "replace")[:400])
                except OSError:
                    pass
                os._exit(127)
        self.pid = pid
        self.fd = fd
        # the screen's own device (TIOCGPTN names the slave; the master's name is the shared
        # /dev/ptmx): who has it open is who would keep this terminal from ever ending
        self.tty = pts_device(fd)       # None where the pts cannot be named: the terminal still works
        os.set_blocking(fd, False)      # input never waits on a program that is not reading
        self.resize(cols, rows)         # the kernel sends SIGWINCH to the foreground group
        threading.Thread(target=self._read, daemon=True).start()
        threading.Thread(target=self._wait, daemon=True).start()

    def add_pending(self, text):
        """Keeps the newest text the reader has: a report that is stalled (the relay is away,
        an operation is slow) must not let a noisy program grow this buffer without bound."""
        on, off = text.rfind("\x1b[?2004h"), text.rfind("\x1b[?2004l")
        if on >= 0 or off >= 0:
            self.brackets = on > off
        self.last_out_at = time.monotonic()
        with self.lock:
            self.pending.append(text)
            self.pending_bytes += len(text)
            while self.pending_bytes > PENDING_BYTES and len(self.pending) > 1:
                self.pending_bytes -= len(self.pending[0])
                del self.pending[0]

    def take_sweep(self):
        """The report loop takes the request once, to do the /proc work outside its lock."""
        wanted, self.needs_sweep = self.needs_sweep, False
        return wanted

    def _read(self):
        try:
            while True:
                fd = self.fd
                if fd < 0 or self.stopping:
                    return                  # disposed: the number may already be someone else's
                if not select.select([fd], [], [], 0.5)[0]:
                    continue
                try:
                    with self.lock:
                        if self.stopping or self.fd != fd:
                            return          # dispose and read cannot reuse the same descriptor
                        data = os.read(fd, 8192)
                except BlockingIOError:
                    continue
                if not data:
                    break
                text = self.decoder.decode(data)
                if text:
                    self.add_pending(text)
                    self.wake()
        except (OSError, ValueError):
            pass                        # the terminal was closed or the program left
        finally:
            tail = self.decoder.decode(b"", final=True)
            if tail:
                self.add_pending(tail)
            with self.lock:
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
        on_close = self.on_close
        if on_close:
            threading.Thread(target=on_close, daemon=True).start()
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

    def kill_leftovers(self):
        """Every process that still holds this terminal's screen open, wherever it put itself:
        a background task that made a group or a session of its own (nohup, setsid) is past a
        kill of the process group, and would keep the terminal from ever ending. It is looked
        for by the terminal's own device (who has it open) and by its parents in /proc (what
        the program started), because the program itself is usually gone by now and its tasks
        have been re-parented. (The Windows program kills the tree with taskkill /T; a task
        that let go of the terminal altogether is nobody's to kill any more.)"""
        born = {}
        for path in glob.glob("/proc/[0-9]*/stat"):
            try:
                with open(path, encoding="utf-8", errors="replace") as stream:
                    fields = stream.read().rsplit(")", 1)[-1].split()
            except (OSError, ValueError):
                continue
            if len(fields) > 19:
                try:
                    born[int(path.split("/")[2])] = (int(fields[1]), fields[19])   # pid -> (ppid, began at)
                except ValueError:
                    continue
        walked, mine = [self.pid], {}
        while walked:
            parent = walked.pop()
            for pid, (ppid, at) in born.items():
                if ppid == parent and pid not in mine:
                    mine[pid] = at
                    walked.append(pid)
        if self.tty is not None:
            for entry in os.listdir("/proc"):
                if not entry.isdigit() or int(entry) == self.pid:
                    continue
                try:
                    ones = os.listdir("/proc/%s/fd" % entry)
                except OSError:
                    continue                    # not ours to look into
                for one in ones:
                    try:
                        if os.stat(os.path.join("/proc", entry, "fd", one)).st_rdev == self.tty:
                            pid = int(entry)
                            mine[pid] = started_at(pid)
                            break
                    except OSError:
                        continue                # it closed that one while we looked
        for pid, at in mine.items():
            kill_if_started(pid, at)        # not a pid that someone else has since taken over

    def escalate(self):
        """Three seconds after TERM: KILL, and the program's own background tasks with it. It
        runs even once the program is gone: they hold the terminal's screen open, so the
        terminal would never be forgotten."""
        if self.swept:
            return
        if self.closing_at is None or time.time() - self.closing_at <= 3:
            return
        if not self.closed and self.fd >= 0:
            try:
                os.killpg(self.pid, signal.SIGKILL)
            except (ProcessLookupError, PermissionError, OSError):
                pass
        self.swept = True
        self.needs_sweep = True          # report() does the /proc work outside the state lock

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
        self.kill_leftovers()

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
        with self.lock:
            self.stopping = True
            fd, self.fd = self.fd, -1
        if fd >= 0:
            try:
                os.close(fd)
            except OSError:
                pass


def seal(live):
    """Turns pending text into numbered pieces, so a busy screen is not held back by the
    per-request piece limit. Only the report thread calls this. Pieces the relay has not
    confirmed are dropped once they outgrow OUTPUT_FLOOR_BYTES: the relay deduplicates by
    seq, so a reader that asks past dropped output gets its reset flag."""
    with live.lock:
        text = "".join(live.pending)
        live.pending = []
        live.pending_bytes = 0
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


# ---- conversations run through their tool's structured mode (docs/PROTOCOL.md, "Chats")
CHAT_TOOLS = ("claude", "codex")
CHAT_DECISIONS = ("allow", "always", "deny")
CHAT_OUTPUT_MOST = 60000        # of one step's output, what is passed on


def chat_brief(name, given):
    """A step named as the tools' windows name it: what it is, and what for, where the tool says it."""
    about = ""
    if isinstance(given, dict):
        for key in ("description", "command", "file_path", "path", "pattern", "query", "url"):
            if isinstance(given.get(key), str) and given[key]:
                about = given[key]
                break
    return (str(name) + ("：" + about[:160] if about else "")).strip()


def chat_shown(back):
    """What a step gave back, where its output was not passed on as it came: its last lines."""
    back = (back or "").replace("\r\n", "\n").strip("\r\n")
    if len(back) > 6000:
        back = "…\n" + back[-6000:]
    lines = back.split("\n")
    return "…\n" + "\n".join(lines[-40:]) if len(lines) > 40 else back


class Chat(Live):
    """A conversation of Claude Code or Codex that this computer runs for the phone through the tool's own
    structured mode, as the Windows program does (agent-windows/Chats.cs). To the relay it is a terminal whose output
    is the conversation's events, one JSON object a line; what is said to it, a stop and an answer go to the tool.
    It keeps the shape of a terminal (Live) for the report, the relay's acknowledgements and ending it."""

    form = "chat"
    mcp = None
    auto_ask = False

    def __init__(self, terminal_id, project, cwd, tool, launcher, wake, session, resume, mode, mcp=None):
        # (a terminal's fields, without a screen: Live.__init__ is not run, it would start a pty)
        self.id, self.project, self.dir, self.tool, self.session, self.status = terminal_id, project, cwd, tool, session, "starting"
        self.created = int(time.time() * 1000)
        self.wake, self.lock = wake, threading.Lock()
        self.pending, self.output, self.out_bytes, self.seq = [], [], 0, 0
        self.pending_bytes, self.needs_sweep = 0, False
        self.closing_at, self.swept, self.closed, self.drained, self.exit_code, self.closed_at, self.told_closed = None, False, False, False, None, None, False
        self.stopping, self.fd, self.tty, self.cols, self.rows = False, -1, None, 80, 24
        self.said_last, self.gate = "", threading.RLock()
        self.asks, self.printed, self.gather, self.halting, self.errors = {}, {}, ["", "", ""], False, []
        # what has been said in the turn at hand, who hears when the turn ends (teams / peers),
        # who keeps what the tool says of its usage limits, and the MCP server offered to the chat
        self.turn_words, self.turn_words_id, self.turned = "", "", None
        self.quotas_note = None
        self.on_session = None
        self.on_close = None
        self.on_ask = None          # who hears what it asks: (ask, kind, title, text, choices)
        self.mcp, self.auto_ask = mcp, False
        self.start(launcher, session, resume, mode)

    def run(self, argv):
        env = dict(os.environ)
        for key in STRIP_ENV:
            env.pop(key, None)
        if not any("utf-8" in env.get(k, "").lower() for k in ("LANG", "LC_ALL")):
            env["LANG"] = "C.UTF-8"
        beside = os.path.dirname(argv[0])       # a tool finds its own runtime (node) next to itself
        if beside not in env.get("PATH", "").split(os.pathsep):
            env["PATH"] = beside + os.pathsep + env.get("PATH", "/usr/local/bin:/usr/bin:/bin")
        self.proc = subprocess.Popen(argv, cwd=self.dir, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
        self.pid = self.proc.pid
        threading.Thread(target=self._read, daemon=True).start()
        threading.Thread(target=self._errors, daemon=True).start()
        threading.Thread(target=self._flush_now_and_then, daemon=True).start()

    # ---- the tool's program
    def _read(self):
        try:
            for raw in self.proc.stdout:
                line = raw.decode("utf-8", "replace").strip()
                if not line.startswith("{"):
                    continue
                try:
                    message = json.loads(line)
                except ValueError:
                    continue
                if isinstance(message, dict):
                    with self.gate:
                        try:
                            self.received(message)
                        except (KeyError, TypeError, ValueError, AttributeError, IndexError):
                            pass            # a message of a shape not known here is passed over
        except (OSError, ValueError):
            pass
        code = self.proc.wait()
        with self.gate:
            was_working = self.status in ("busy", "confirm")
            for ask in list(self.asks):
                self.event(t="answered", id=ask, decision="gone")
            self.asks.clear()
            exit_note = "工具已退出（代码 %d）%s" % (code, "：" + "\n".join(self.errors[-6:]) if self.errors else "")
            if code and not self.stopping and not self.closing_at:
                self.note(exit_note)
            if was_working:
                how = "stopped" if (self.stopping or self.closing_at) else "failed"
                self.turn_ended(how, "" if how == "stopped" else (exit_note if code else "对话已意外退出"))
            self.state("ended")
            self.flush()
        with self.lock:
            self.exit_code = code
            self.closed = self.drained = True
            self.closed_at = time.time()
        on_close = self.on_close
        if on_close:
            threading.Thread(target=on_close, daemon=True).start()
        self.wake()

    def _errors(self):
        try:
            for raw in self.proc.stderr:
                self.errors = (self.errors + [raw.decode("utf-8", "replace").rstrip()])[-6:]
        except (OSError, ValueError):
            pass

    def _flush_now_and_then(self):
        while not self.closed:
            time.sleep(0.12)
            with self.gate:
                self.flush()

    def send(self, message):
        try:
            self.proc.stdin.write((json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8"))
            self.proc.stdin.flush()
        except (OSError, ValueError):
            raise OpError("对话已结束")

    # ---- what goes to the phone
    def emit(self, line):
        with self.lock:
            self.pending.append(line)
        self.wake()

    def event(self, **what):
        self.flush()
        self.emit(json.dumps(what, ensure_ascii=False) + "\n")

    def flush(self):
        kind, item, text = self.gather
        if text:
            self.gather = ["", "", ""]
            self.emit(json.dumps({"t": kind, "id": item, "text": text}, ensure_ascii=False) + "\n")

    def append(self, kind, item, text):
        if not text:
            return
        if kind == "out":
            had = self.printed.get(item, 0)
            if had >= CHAT_OUTPUT_MOST:
                return
            if had + len(text) > CHAT_OUTPUT_MOST:
                text = text[:CHAT_OUTPUT_MOST - had] + "\n…"
            self.printed[item] = had + len(text)
        if kind == "more" and item == self.turn_words_id:
            self.turn_words = (self.turn_words + text)[-120000:]
        if self.gather[2] and (self.gather[0], self.gather[1]) != (kind, item):
            self.flush()
        self.gather = [kind, item, self.gather[2] + text]
        if len(self.gather[2]) > 8000:
            self.flush()
        if kind == "more":
            self.spoken(self.said_last + text)

    def spoken(self, text):
        lines = [one.strip() for one in (text or "").split("\n") if one.strip()]
        self.said_last = lines[-1][:200] if lines else self.said_last

    def state(self, now):
        if self.status != now:
            self.status = now
            self.event(t="state", state=now, at=int(time.time() * 1000))

    def known(self, session):
        if session and session != self.session:
            self.session = session
            self.event(t="session", session=session)
            if self.on_session:
                try:
                    self.on_session(session)
                except Exception:
                    pass

    def item(self, item, kind, text, **more):
        if kind == "text":
            self.turn_words_id = item
            self.turn_words = (text or "")[-120000:]
        elif kind == "user":
            self.turn_words_id, self.turn_words = "", ""
        self.event(t="item", id=item, kind=kind, text=text or "", at=int(time.time() * 1000), **more)
        if kind == "text":
            self.said_last = ""
            self.spoken(text)

    def done(self, item, ok, result):
        more = {"result": chat_shown(result)} if result and item not in self.printed else {}
        self.event(t="done", id=item, ok=ok, **more)

    def note(self, text):
        self.event(t="note", text=text, at=int(time.time() * 1000))

    def note_quota(self, tool, windows):
        if self.quotas_note and windows:
            self.quotas_note(tool, windows)

    def turn_ended(self, how, error=""):
        more = {"error": error} if error else {}
        self.event(t="turn", end=how, at=int(time.time() * 1000), **more)
        self.halting = False
        said = self.turn_words
        self.turn_words, self.turn_words_id = "", ""
        if how == "failed" and error:
            if not said.strip():
                said = error
            elif error.lower() not in said.lower():
                said = said + "\n" + error
        heard = self.turned
        if heard:
            threading.Thread(target=heard, args=(how, said), daemon=True).start()

    def ask(self, request, kind, title, text, always, choices=None, read_only=False):
        if self.auto_ask:
            decision = "allow" if (read_only or kind in ("read", "search")) else "deny"
            asked = {"request": request, "text": (title + ("\n" + text if text else "")).strip(),
                     "always": always, "choices": list(choices or [])}
            self.reply(asked, decision)
            return
        ask = secrets.token_hex(8)
        self.asks[ask] = {"request": request, "text": (title + ("\n" + text if text else "")).strip(), "always": always, "choices": list(choices or [])}
        options = [{"id": "choice:%d" % n, "label": label} for n, label in enumerate(choices)] if choices else \
            [{"id": "allow", "label": "允许"}] + ([{"id": "always", "label": "总是允许"}] if always else [])
        options.append({"id": "deny", "label": "不回答" if choices else "拒绝"})
        self.event(t="ask", id=ask, kind=kind, title=title, text=text[:4000], options=options)
        self.state("confirm")
        heard = self.on_ask
        if heard:
            threading.Thread(target=heard, args=(ask, kind, title, text, list(choices or [])), daemon=True).start()

    def before(self, messages, more):
        """What was said before, where a saved conversation is gone on with."""
        with self.gate:
            self.event(t="history", messages=messages, more=more)

    @property
    def asking(self):
        with self.gate:
            return next(iter(self.asks.values()))["text"] if self.asks else ""

    def answer(self, ask, decision):
        with self.gate:
            asked = self.asks.get(ask)
            if asked is None:
                raise OpError("这个确认请求已经处理过了")
            chosen = decision.startswith("choice:") and decision[7:].isdigit() and int(decision[7:]) < len(asked["choices"])
            if not chosen and decision not in CHAT_DECISIONS:
                raise OpError("回答无效")
            if decision == "always" and not asked["always"]:
                raise OpError("这个请求不能设为总是允许")
            del self.asks[ask]
            self.event(t="answered", id=ask, decision=decision)
            if not self.asks and self.status == "confirm":
                self.state("busy")
        self.reply(asked, decision)

    # ---- a terminal's ways, as they apply to a conversation
    def write(self, text):
        raise OpError("对话不接受终端输入")

    def resize(self, cols, rows):
        self.cols, self.rows = cols, rows

    def escalate(self):
        if self.swept or self.closing_at is None or time.time() - self.closing_at <= 3:
            return
        self.swept = True
        self.needs_sweep = True

    def kill_leftovers(self):
        try:
            os.killpg(self.pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError, OSError):
            pass

    def dispose(self):
        with self.lock:
            self.stopping = True
        if self.mcp:
            server, self.mcp = self.mcp, None
            try:
                server.dispose()
            except Exception:
                pass
        for pipe in (self.proc.stdin, self.proc.stdout, self.proc.stderr):
            try:
                pipe.close()
            except (OSError, ValueError):
                pass


class ClaudeChat(Chat):
    """Claude Code in --print mode with JSON lines both ways, as its SDK drives it: told what to say on its standard
    input, stopped by a control request, asked by it whether a step may run (--permission-prompt-tool stdio)."""

    background_work = False

    def start(self, launcher, session, resume, mode):
        if not session:
            if resume:
                raise OpError("对话编号无效")
            session = str(uuid.uuid4())
        if not session_id_ok("claude", session):
            raise OpError("对话编号无效")
        self.message, self.streamed, self.blocks, self.steps, self.said_here, self.requests = "", set(), {}, {}, [], 0
        with self.gate:
            self.known(session)
        argv = [launcher, "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                "--replay-user-messages", "--permission-prompt-tool", "stdio", "--resume" if resume else "--session-id", session]
        argv += ["--permission-mode", "plan"] if mode == "read" else ["--permission-mode", "acceptEdits"] if mode == "edit" else []
        if self.mcp and self.mcp.config_file:
            argv += ["--mcp-config", self.mcp.config_file, "--strict-mcp-config"]
        self.run(argv)
        self.requests += 1
        self.send({"type": "control_request", "request_id": "init-%d" % self.requests, "request": {"subtype": "initialize"}})
        with self.gate:
            self.state("idle")

    def say(self, text, op_id="", attachments=None):
        attachments = attachments or []
        shown = attachment_text(text, attachments)
        with self.gate:
            self.turn_words, self.turn_words_id = "", ""
            self.said_here.append((shown.strip(), op_id or ""))
            if self.status != "confirm":
                self.state("busy")
        content = claude_content(shown, attachments) if attachments else shown
        self.send({"type": "user", "message": {"role": "user", "content": content}})

    def stop_turn(self):
        with self.gate:
            if self.status not in ("busy", "confirm"):
                raise OpError("它现在没有在执行")
            self.halting = True
            self.requests += 1
        self.send({"type": "control_request", "request_id": "stop-%d" % self.requests, "request": {"subtype": "interrupt"}})

    def received(self, m):
        kind = m.get("type")
        if kind == "system":
            if m.get("subtype") == "init":
                self.known(m.get("session_id") or "")
            elif m.get("subtype") == "status" and m.get("status") == "requesting" and self.status == "idle":
                self.state("busy")
            elif m.get("subtype") == "background_tasks_changed" and isinstance(m.get("tasks"), list):
                self.background_work = any(isinstance(task, dict) and not task.get("ambient") and not task.get("awaited")
                                           for task in m["tasks"])
        elif kind == "stream_event":
            self.streamed_event(m.get("event") or {})
        elif kind == "assistant":
            self.whole(m.get("message") or {})
        elif kind == "user":
            self.came(m)
        elif kind == "rate_limit_event":
            info = m.get("rate_limit_info") if isinstance(m.get("rate_limit_info"), dict) else {}
            self.note_quota("claude", quotas_store.from_claude_event(info))
        elif kind == "control_request":
            self.permission(m)
        elif kind == "control_cancel_request":
            for key, asked in list(self.asks.items()):
                if asked["request"].get("request_id") == m.get("request_id"):
                    del self.asks[key]
                    self.event(t="answered", id=key, decision="gone")
            if not self.asks and self.status == "confirm":
                self.state("busy")
        elif kind == "result":
            ok = m.get("subtype") == "success" and not m.get("is_error")
            self.blocks = {}
            if ok and self.background_work and not self.halting:
                self.event(t="turn", end="done", at=int(time.time() * 1000))
                self.state("confirm" if self.asks else "busy")
                return
            how = "stopped" if self.halting and not ok else "done" if ok else "failed"
            self.turn_ended(how, "" if ok or how == "stopped" else str(m.get("result") or m.get("subtype") or "")[:600])
            self.state("confirm" if self.asks else "idle")

    def streamed_event(self, e):
        kind = e.get("type")
        if kind == "message_start":
            self.message, self.blocks = (e.get("message") or {}).get("id", ""), {}
            if self.status == "idle":
                self.state("busy")
            return
        index = e.get("index", -1)
        if kind == "content_block_start":
            block = e.get("content_block") or {}
            if block.get("type") in ("text", "thinking"):
                item = "%s:%s" % (self.message, index)
                self.blocks[index] = item
                self.item(item, "text" if block["type"] == "text" else "thought", block.get(block["type"]) or "")
                self.streamed.add(self.message)
        elif kind == "content_block_delta" and index in self.blocks:
            delta = e.get("delta") or {}
            self.append("more", self.blocks[index], (delta.get("text") or "") + (delta.get("thinking") or ""))

    def whole(self, message):
        for part in message.get("content") or []:
            if not isinstance(part, dict):
                continue
            if part.get("type") == "tool_use" and part.get("id") not in self.steps:
                item = "step-" + part["id"]
                self.steps[part["id"]] = item
                given, name = part.get("input") or {}, part.get("name") or ""
                more = {"name": name, "state": "run"}
                own = peers_mod.own_tool(name) if name.startswith("mcp__" + peers_mod.SERVER_NAME + "__") else ""
                if own:
                    title, detail = peers_mod.step_of(own, given)
                    if detail:
                        more["detail"] = detail[:2000]
                else:
                    title = chat_brief(name, given)
                    if isinstance(given, dict) and given.get("command"):
                        more["detail"] = str(given["command"])[:2000]
                    elif isinstance(given, dict) and name in ("Edit", "Write", "MultiEdit"):
                        more["detail"] = str(given.get("file_path") or "")
                self.item(item, "step", title, **more)
            elif part.get("type") == "text" and message.get("id") not in self.streamed and (part.get("text") or "").strip():
                self.item("%s:t%s" % (message.get("id"), secrets.token_hex(3)), "text", part["text"])

    def came(self, m):
        content = (m.get("message") or {}).get("content")
        if isinstance(content, str):
            return self.spoke(content, m)
        for part in content or []:
            if not isinstance(part, dict):
                continue
            if part.get("type") == "text" and not str(part.get("text") or "").startswith("[Request interrupted"):
                self.spoke(part.get("text") or "", m)
            elif part.get("type") == "tool_result" and part.get("tool_use_id") in self.steps:
                back = part.get("content")
                text = back if isinstance(back, str) else "\n".join(one.get("text", "") for one in back or [] if isinstance(one, dict))
                self.done(self.steps[part["tool_use_id"]], not part.get("is_error"), text)

    def spoke(self, text, m):
        if not text.strip() or m.get("isSynthetic"):
            return
        mine = ""
        if self.said_here and self.said_here[0][0] == text.strip():
            mine = self.said_here.pop(0)[1]
        self.item("user-" + (m.get("uuid") or secrets.token_hex(16)), "user", text, **({"op": mine} if mine else {}))

    def permission(self, m):
        request = m.get("request") or {}
        if request.get("subtype") != "can_use_tool":
            return self.respond(m.get("request_id"), None, "不支持的请求")
        name, given = request.get("tool_name") or "", request.get("input") or {}
        # Remote AI's own tools run through its own controls (a task given out never works more widely than this conversation)
        if self.mcp and name.startswith("mcp__" + self.mcp.name + "__") and peers_mod.own_tool(name):
            return self.respond(m.get("request_id"), {"behavior": "allow", "updatedInput": given}, None)
        if name == "AskUserQuestion":
            questions = given.get("questions") or []
            if len(questions) == 1 and not questions[0].get("multiSelect"):
                labels = [one.get("label") for one in questions[0].get("options") or [] if one.get("label")]
                if labels:
                    return self.ask(m, "question", questions[0].get("question") or "", "", False, labels)
        plan = name == "ExitPlanMode"
        text = given.get("plan") if plan else given.get("command") or given.get("file_path") or json.dumps(given, ensure_ascii=False)
        about = request.get("description") or ""
        if about and about != text:
            text = about + "\n" + str(text)
        suggestion = claude_permissions.select(request.get("permission_suggestions"), allow_session=True)
        if suggestion is not None:
            text = claude_permissions.detail(suggestion) + "\n\n" + str(text or "")
        self.ask(m, "plan" if plan else "tool", "开始按计划执行？" if plan else request.get("display_name") or name,
                 str(text or ""), suggestion is not None, read_only=(name in CLAUDE_READ_ONLY_TOOLS))

    def reply(self, asked, decision):
        m = asked["request"]
        request = m.get("request") or {}
        given = request.get("input") or {}
        if decision.startswith("choice:"):
            question = (given.get("questions") or [{}])[0]
            answer = {"behavior": "allow", "updatedInput": dict(given, answers={question.get("question", ""): asked["choices"][int(decision[7:])]})}
        elif decision == "deny":
            answer = {"behavior": "deny", "message": "用户在手机上拒绝了这一步"}
        else:
            answer = {"behavior": "allow", "updatedInput": given}
            if decision == "always":
                suggestion = claude_permissions.select(request.get("permission_suggestions"), allow_session=True)
                if suggestion is None:
                    raise OpError("这个请求不能设为总是允许")
                answer["updatedPermissions"] = [suggestion]
        self.respond(m.get("request_id"), answer, None)

    def respond(self, request, answer, error):
        response = {"request_id": request, "subtype": "success", "response": answer} if error is None else {"request_id": request, "subtype": "error", "error": error}
        self.send({"type": "control_response", "response": response})


class CodexChat(Chat):
    """Codex through `codex app-server`: JSON-RPC on its standard input and output. What is said starts a turn or
    joins the one at work; a stop interrupts it; the steps and file changes it wants are asked of this program."""

    def start(self, launcher, session, resume, mode):
        if resume and not session_id_ok("codex", session):
            raise OpError("对话编号无效")
        self.next, self.waiting, self.turn, self.early, self.thoughts = 0, {}, "", [], set()
        argv = [launcher, "app-server"]
        if self.mcp and self.mcp.command:
            prefix = "mcp_servers." + self.mcp.name + "."
            quoted = lambda text: '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'
            argv += ["-c", prefix + "command=" + quoted(self.mcp.command),
                     "-c", prefix + "args=[" + ",".join(quoted(one) for one in self.mcp.args) + "]"]
        self.run(argv)
        self.call("initialize", {"clientInfo": {"name": "remote-ai", "title": "Remote AI", "version": "1"}}, lambda _: None)
        self.send({"method": "initialized", "params": {}})
        given = {"cwd": self.dir}
        if mode in ("read", "edit"):
            given["sandbox"] = "read-only" if mode == "read" else "workspace-write"
        if resume:
            given["threadId"] = session

        def opened(answer):
            thread = ((answer.get("result") or {}).get("thread") or {}).get("id") or ""
            if not thread:
                self.note("Codex 没有打开这段对话：" + str((answer.get("error") or {}).get("message") or ""))
                return self.state("ended")
            self.known(thread)
            self.state("busy" if self.status == "busy" else "idle")
            for text, op_id, attachments in self.early:
                self.begin_turn(text, op_id, attachments)
            self.early = []
        self.call("thread/resume" if resume else "thread/start", given, opened)

    def call(self, method, given, answered):
        with self.gate:
            self.next += 1
            number = self.next
            self.waiting[number] = answered
        self.send({"id": number, "method": method, "params": given})

    @staticmethod
    def words(text, attachments=None):
        return [{"type": "text", "text": text, "text_elements": []}]

    def say(self, text, op_id="", attachments=None):
        attachments = attachments or []
        shown = attachment_text(text, attachments)
        with self.gate:
            if not self.session:
                self.early.append((text, op_id, attachments))
                return self.state("busy")
            if self.turn:
                # at work: the words join the turn, as its own window's box does
                def steered(answer):
                    if answer.get("error") is not None:
                        self.begin_turn(text, op_id, attachments)      # the turn had just ended: a new one
                return self.call("turn/steer", {"threadId": self.session, "expectedTurnId": self.turn, "input": self.words(shown), "clientUserMessageId": op_id or secrets.token_hex(16)}, steered)
            self.begin_turn(text, op_id, attachments)

    def ask_rate_limits(self):
        self.call("account/rateLimits/read", {}, lambda answer: self.note_quota(
            "codex", quotas_store.from_codex((answer.get("result") or {}).get("rateLimits") or
                                             (answer.get("result") or {}))))

    def begin_turn(self, text, op_id, attachments=None):
        with self.gate:
            self.turn_words, self.turn_words_id = "", ""
        self.state("busy")
        shown = attachment_text(text, attachments or [])

        def started(answer):
            if answer.get("error") is not None:
                msg = "Codex 没有收下这句话：" + str(answer["error"].get("message") or "")
                self.note(msg)
                if not self.turn:
                    self.turn_ended("failed", msg)
                return self.state("busy" if self.turn else "idle")
            begun = ((answer.get("result") or {}).get("turn") or {}).get("id") or ""
            if begun and not self.turn:
                self.turn = begun
        self.call("turn/start", {"threadId": self.session, "input": self.words(shown), "clientUserMessageId": op_id or secrets.token_hex(16)}, started)

    def stop_turn(self):
        with self.gate:
            if not self.turn:
                raise OpError("它现在没有在执行")
            self.halting = True
            self.call("turn/interrupt", {"threadId": self.session, "turnId": self.turn}, lambda _: None)

    def received(self, m):
        method, p = m.get("method") or "", m.get("params") or {}
        if "id" in m and not method:
            answered = self.waiting.pop(m["id"], None) if isinstance(m["id"], int) else None
            if answered:
                answered(m)
            return
        if "id" in m:
            return self.requested(m["id"], method, p)
        if method == "turn/started":
            self.turn = (p.get("turn") or {}).get("id") or ""
            self.state("confirm" if self.asks else "busy")
        elif method == "turn/completed":
            done = p.get("turn") or {}
            if done.get("id") == self.turn or not self.turn:
                self.turn = ""
            status = done.get("status")
            self.turn_ended("done" if status == "completed" else "stopped" if status == "interrupted" else "failed",
                            str((done.get("error") or {}).get("message") or "") if status == "failed" else "")
            self.state("confirm" if self.asks else "busy" if self.turn else "idle")
        elif method == "item/started":
            self.started(p.get("item") or {})
        elif method == "item/completed":
            self.completed(p.get("item") or {})
        elif method == "item/agentMessage/delta":
            self.append("more", p.get("itemId") or "", p.get("delta") or "")
        elif method == "item/reasoning/summaryTextDelta":
            item = p.get("itemId") or ""
            if item not in self.thoughts:
                self.thoughts.add(item)
                self.item(item, "thought", "")
            self.append("more", item, p.get("delta") or "")
        elif method in ("item/commandExecution/outputDelta", "item/fileChange/outputDelta"):
            self.append("out", p.get("itemId") or "", p.get("delta") or "")
        elif method == "serverRequest/resolved":
            for key, asked in list(self.asks.items()):
                if str(asked["request"].get("id")) == str(p.get("requestId")):
                    del self.asks[key]
                    self.event(t="answered", id=key, decision="gone")
            if not self.asks and self.status == "confirm":
                self.state("busy" if self.turn else "idle")
        elif method == "account/rateLimits/updated":
            self.note_quota("codex", quotas_store.from_codex(p.get("rateLimits") or p))
        elif method == "error":
            self.note(str((p.get("error") or {}).get("message") or "Codex 出错"))

    def started(self, item):
        kind, key = item.get("type"), item.get("id") or ""
        if kind == "userMessage":
            text = "\n".join(one.get("text", "") for one in item.get("content") or [] if isinstance(one, dict) and one.get("type") == "text")
            if text:
                self.item(key, "user", text, **({"op": item["clientId"]} if item.get("clientId") else {}))
        elif kind == "agentMessage":
            self.item(key, "text", item.get("text") or "")
        elif kind == "commandExecution":
            actions = [one for one in item.get("commandActions") or [] if isinstance(one, dict)]
            short = next((one.get("command") for one in actions if one.get("command")), None) or item.get("command") or ""
            reads = [one.get("name") or one.get("path") for one in actions if one.get("type") == "read" and (one.get("name") or one.get("path"))]
            title = "读取：" + "、".join(reads[:3]) + (" 等 %d 个文件" % len(reads) if len(reads) > 3 else "") if actions and len(reads) == len(actions) else "运行：" + short[:160]
            self.item(key, "step", title, name="shell", detail=short[:2000], state="run")
        elif kind == "fileChange":
            names = [os.path.basename(one.get("path", "")) for one in item.get("changes") or [] if isinstance(one, dict) and one.get("path")]
            self.item(key, "step", "修改：" + ("、".join(names[:3]) + (" 等 %d 个文件" % len(names) if len(names) > 3 else "") if names else "文件"), name="edit", state="run")
        elif kind == "mcpToolCall":
            tool_name = str(item.get("tool") or "")
            more = {"name": "mcp", "state": "run"}
            if item.get("server") == peers_mod.SERVER_NAME and tool_name in peers_mod.TOOL_NAMES:
                title, detail = peers_mod.step_of(tool_name, item.get("arguments"))
                if detail:
                    more["detail"] = detail[:2000]
            else:
                title = "%s：%s" % (item.get("server") or "", tool_name)
            self.item(key, "step", title, **more)
        elif kind == "webSearch":
            self.item(key, "step", "搜索：" + str(item.get("query") or ""), name="search", state="run")
        elif kind in ("dynamicToolCall", "collabAgentToolCall"):
            self.item(key, "step", str(item.get("tool") or ""), name="tool", state="run")

    def completed(self, item):
        kind, key = item.get("type"), item.get("id") or ""
        if kind == "commandExecution":
            code, status = item.get("exitCode"), item.get("status")
            ok = status == "completed" and code in (None, 0)
            lead = "（退出码 %s）\n" % code if code not in (None, 0) else "（已拒绝）\n" if status == "declined" else ""
            self.done(key, ok, lead + (item.get("aggregatedOutput") or ""))
        elif kind == "fileChange":
            self.done(key, item.get("status") == "completed", "\n".join("%s\n%s" % (one.get("path", ""), one.get("diff", "")) for one in item.get("changes") or [] if isinstance(one, dict)))
        elif kind == "mcpToolCall":
            result_obj = item.get("result")
            if item.get("error"):
                back = str((item.get("error") or {}).get("message") or "")
            elif isinstance(result_obj, dict) and isinstance(result_obj.get("content"), list):
                parts = [str(one.get("text") or "") for one in result_obj["content"] if isinstance(one, dict) and one.get("text")]
                back = "\n".join(parts) if parts else json.dumps(result_obj, ensure_ascii=False)
            elif isinstance(result_obj, str):
                back = result_obj
            else:
                back = json.dumps(result_obj or "", ensure_ascii=False)
            ok = item.get("status") == "completed" and not item.get("error") and not (isinstance(result_obj, dict) and result_obj.get("isError") is True)
            self.done(key, ok, back)
        elif kind in ("webSearch", "dynamicToolCall", "collabAgentToolCall"):
            self.done(key, item.get("status") != "failed", "")
        elif kind == "agentMessage":
            text = item.get("text") or ""
            if key == self.turn_words_id or text:
                self.turn_words_id = key
                self.turn_words = text[-120000:]
            self.event(t="set", id=key, text=text)
            self.said_last = ""
            self.spoken(text)
        elif kind == "reasoning" and key not in self.thoughts:
            summary = "\n\n".join(one if isinstance(one, str) else one.get("text", "") for one in item.get("summary") or [] if one)
            if summary.strip():
                self.thoughts.add(key)
                self.item(key, "thought", summary)

    def requested(self, number, method, p):
        request = {"id": number, "method": method, "params": p}
        if method in ("item/commandExecution/requestApproval", "execCommandApproval"):
            raw_actions = [one for one in p.get("commandActions") or [] if isinstance(one, dict)]
            actions = [one.get("command") for one in raw_actions if one.get("command")]
            read_only = bool(raw_actions) and all(one.get("type") in ("read", "listFiles", "search") for one in raw_actions)
            reason = p.get("reason") or ""
            self.ask(request, "command", "运行命令", (reason + "\n" if reason else "") + ("\n".join(actions) if actions else str(p.get("command") or "")), True, read_only=read_only)
        elif method in ("item/fileChange/requestApproval", "applyPatchApproval"):
            self.ask(request, "edit", "修改文件", p.get("reason") or p.get("grantRoot") or "", True)
        elif method == "item/tool/requestUserInput":
            questions = p.get("questions") or []
            labels = [one.get("label") for one in (questions[0].get("options") or []) if one.get("label")] if len(questions) == 1 else []
            if labels:
                self.ask(request, "question", questions[0].get("question") or "", "", False, labels)
            else:
                self.send({"id": number, "error": {"code": -32601, "message": "手机上不能回答这个问题"}})
        elif self.mcp and "mcp" in method.lower() and p.get("server") == self.mcp.name:
            self.send({"id": number, "result": {"decision": "accept"}})
        else:
            self.send({"id": number, "error": {"code": -32601, "message": "Remote AI 不支持这个请求"}})

    def reply(self, asked, decision):
        request, method = asked["request"], asked["request"]["method"]
        if decision.startswith("choice:"):
            question = (request["params"].get("questions") or [{}])[0]
            result = {"answers": {question.get("id", ""): {"answers": [asked["choices"][int(decision[7:])]]}}}
        elif method == "item/tool/requestUserInput":
            result = {"answers": {}}
        elif method in ("execCommandApproval", "applyPatchApproval"):
            result = {"decision": "denied" if decision == "deny" else "approved_for_session" if decision == "always" else "approved"}
        else:
            result = {"decision": "decline" if decision == "deny" else "acceptForSession" if decision == "always" else "accept"}
        self.send({"id": request["id"], "result": result})


def open_chat(terminal_id, project, cwd, tool, launcher, wake, session, resume, mode, mcp=None):
    """A conversation of `tool`, new or (`resume`) the saved one `session` gone on with."""
    kind = ClaudeChat if tool == "claude" else CodexChat if tool == "codex" else None
    if kind is None:
        raise OpError("这个工具不支持对话")
    return kind(terminal_id, project, cwd, tool, launcher, wake, session, resume, mode, mcp)


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


STAMP_RE = re.compile(r'"timestamp":"(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(?:\.(\d+))?(Z|[+-]\d\d:?\d\d)?"')
CWD_RE = re.compile(r'"cwd":"((?:[^"\\]|\\.)*)"')
ANSWER_RE = re.compile(r'"id":"(msg_[A-Za-z0-9_-]+)"')


def _stamp(line, last=False):
    """When a row of a record was written, in seconds since 1970: its first time, or its last."""
    found = None
    for found in STAMP_RE.finditer(line):
        if not last:
            break
    if found is None:
        return None
    year, month, day, hour, minute, second, part, zone = found.groups()
    at = calendar.timegm((int(year), int(month), int(day), int(hour), int(minute), int(second))) + (float("0." + part) if part else 0.0)
    if zone and zone != "Z":
        away = int(zone[1:3]) * 3600 + int(zone[-2:]) * 60
        at -= away if zone[0] == "+" else -away
    return at


def _number(text, name, start=0):
    found = re.compile('"%s":(\\d+)' % name).search(text, start)
    return int(found.group(1)) if found else 0


def _day(at):
    return time.strftime("%Y-%m-%d", time.localtime(at))


class Stats:
    """How the owner's work with the tools went, in numbers, read from the tools' own records on this computer: what
    was asked and when, how long the tools worked on it, how soon they first answered, and the tokens they note for
    each answer. By day, by tool, by project and by hour of the day. Nothing that was said is kept or passed on:
    numbers, dates, and the names of the folders the conversations are in. (As Stats.cs of the Windows program.)"""

    def __init__(self, claude_home, codex_home):
        self.claude_home, self.codex_home = claude_home, codex_home
        self.records = {}               # path -> (mtime, size, record): a record is read anew only when it changed
        self.kept = 0                   # about how much room the records take, as _size counts it
        self.lock = threading.Lock()

    @staticmethod
    def _size(record):
        """About how much room a record takes: its days, each with the waits and the sessions in it."""
        return 320 + sum(96 + 8 * len(day["waits"]) + 24 * len(day["sessions"]) for day in record["days"].values())

    @staticmethod
    def _of(record, at):
        return record["days"].setdefault(_day(at), {"prompts": 0, "active": 0.0, "in": 0, "out": 0, "cached": 0, "waits": [], "sessions": set()})

    @classmethod
    def _claude(cls, lines, record):
        """Claude Code: its prompts, the turns that followed, and the usage of each answer (noted in several rows,
        counted once)."""
        usage, began, ended, open_, waiting = {}, 0.0, 0.0, False, False

        def close():
            if open_:
                cls._of(record, began)["active"] += max(0.0, min(LONGEST_TURN, ended - began))

        for line in lines:
            if len(line) < 40:
                continue
            answer = '"type":"assistant"' in line
            asked = not answer and '"type":"user"' in line
            if not (answer or asked) or '"isSidechain":true' in line:
                continue
            at = _stamp(line, last=True)
            if at is None:
                continue
            if not record["folder"]:
                where = CWD_RE.search(line)
                if where:
                    record["folder"] = re.sub(r"\\(.)", lambda m: m.group(1), where.group(1))
            if asked:
                # what a tool gave back belongs to the turn that runs; what Claude Code notes for itself is nobody's prompt
                if '"tool_use_id"' in line:
                    if open_:
                        ended = at
                    continue
                if '"isMeta":true' in line:
                    continue
                close()
                began = ended = at
                open_ = waiting = True
                day = cls._of(record, at)
                day["prompts"] += 1
                day["sessions"].add(record["session"])
                record["hours"][time.localtime(at).tm_hour] += 1
                continue
            if open_:
                if waiting:
                    if 0 <= at - began <= LONGEST_WAIT:
                        cls._of(record, began)["waits"].append(at - began)
                    waiting = False
                ended = at
            start, name = line.find('"usage":{'), ANSWER_RE.search(line)
            if start < 0 or not name:
                continue
            usage[name.group(1)] = (_day(at), _number(line, "input_tokens", start), _number(line, "output_tokens", start),
                                    _number(line, "cache_read_input_tokens", start) + _number(line, "cache_creation_input_tokens", start))
        close()
        for key, given, written, cached in usage.values():
            day = record["days"].setdefault(key, {"prompts": 0, "active": 0.0, "in": 0, "out": 0, "cached": 0, "waits": [], "sessions": set()})
            day["in"] += given
            day["out"] += written
            day["cached"] += cached

    @classmethod
    def _codex(cls, lines, record):
        """Codex: its turns with how long each took, and the tokens of each request."""
        began, open_, waiting = 0.0, False, False
        for line in lines:
            if len(line) < 40:
                continue
            if not record["folder"] and '"type":"session_meta"' in line:
                where = CWD_RE.search(line)
                if where:
                    record["folder"] = re.sub(r"\\(.)", lambda m: m.group(1), where.group(1))
                continue
            if '"type":"event_msg"' not in line:
                # the first thing that comes back after a turn began: how soon it answered
                if waiting and '"type":"response_item"' in line:
                    first = _stamp(line)
                    if first is not None and 0 <= first - began <= LONGEST_WAIT:
                        cls._of(record, began)["waits"].append(first - began)
                    waiting = False
                continue
            at = _stamp(line)
            if at is None:
                continue
            if '"type":"task_started"' in line:
                began, open_, waiting = at, True, True
                day = cls._of(record, at)
                day["prompts"] += 1
                day["sessions"].add(record["session"])
                record["hours"][time.localtime(at).tm_hour] += 1
            elif '"type":"task_complete"' in line or '"type":"turn_aborted"' in line:
                if open_:
                    cls._of(record, began)["active"] += max(0.0, min(LONGEST_TURN, _number(line, "duration_ms") / 1000.0))
                open_ = waiting = False
            elif '"type":"token_count"' in line:
                start = line.find('"last_token_usage":{')
                if start < 0:
                    continue
                end = line.find("}", start)
                last = line[start:end] if end > start else line[start:]
                given, cached = _number(last, "input_tokens"), _number(last, "cached_input_tokens")
                day = cls._of(record, at)
                day["in"] += max(0, given - cached)
                day["cached"] += cached
                day["out"] += _number(last, "output_tokens")

    def _summed(self, path, tool, session):
        try:
            info = os.stat(path)
        except OSError:
            return None
        mark = (info.st_mtime_ns, info.st_size)
        with self.lock:
            known = self.records.get(path)
            if known and known[0] == mark:
                return known[1]
        record = {"tool": tool, "session": session, "folder": "", "days": {}, "hours": [0] * 24}
        try:
            with open(path, encoding="utf-8", errors="replace") as lines:
                (self._claude if tool == "claude" else self._codex)(lines, record)
        except OSError:
            pass
        with self.lock:
            gone = self.records.pop(path, None)
            if gone:
                self.kept -= self._size(gone[1])
            size = self._size(record)
            # the records of the days nobody asks about any more go before new ones are kept
            while self.records and (self.kept + size > RECORDS_BYTES or len(self.records) >= 4000):
                self.kept -= self._size(self.records.pop(next(iter(self.records)))[1])
            self.records[path] = (mark, record)
            self.kept += size
        return record

    def _recent(self, since):
        found = []
        for path in glob.glob(os.path.join(self.claude_home(), "projects", "*", "*.jsonl")):
            found.append((path, "claude"))
        for path in glob.glob(os.path.join(self.codex_home(), "sessions", "**", "rollout-*.jsonl"), recursive=True):
            found.append((path, "codex"))
        out = []
        for path, tool in found:
            try:
                if os.path.getmtime(path) < since:
                    continue
            except OSError:
                continue
            record = self._summed(path, tool, os.path.splitext(os.path.basename(path))[0])
            if record:
                out.append(record)
        return out

    @staticmethod
    def _middle(values):
        if not values:
            return None
        ordered, half = sorted(values), len(values) // 2
        return round(ordered[half] if len(ordered) % 2 else (ordered[half - 1] + ordered[half]) / 2, 1)

    def read(self, days, named, now=None):
        """The last `days` days (today the last of them), as the phone shows them. `named`: the name a folder goes
        by (its project's, where it is one)."""
        days = max(1, min(90, days))
        today = datetime.date.fromtimestamp(time.time() if now is None else now)
        first = today - datetime.timedelta(days=days - 1)
        read = self._recent(time.mktime(first.timetuple()) - 86400)
        by_day, tools, projects, hours, waits = [], {}, {}, [0] * 24, []
        for back in range(days):
            key = (first + datetime.timedelta(days=back)).isoformat()
            total = {"prompts": 0, "active": 0.0, "in": 0, "out": 0, "cached": 0}
            sessions, waited = set(), []
            for record in read:
                day = record["days"].get(key)
                if not day:
                    continue
                for name in total:
                    total[name] += day[name]
                sessions.update(record["tool"] + " " + session for session in day["sessions"])
                waited.extend(day["waits"])
                for sums, name in ((tools, record["tool"]), (projects, named(record["folder"]))):
                    if name:
                        kept = sums.setdefault(name, [0, 0, 0])
                        kept[0] += day["prompts"]
                        kept[1] += int(day["active"])
                        kept[2] += day["in"] + day["out"]
            waits.extend(waited)
            by_day.append({"date": key, "prompts": total["prompts"], "sessions": len(sessions), "active": int(total["active"] + 0.5),
                           "in": total["in"], "out": total["out"], "cached": total["cached"], "wait": self._middle(waited)})
        # (the hours of the day are of the whole span that was read, by the records that have something in it)
        for record in read:
            if any(key >= first.isoformat() for key in record["days"]):
                hours = [a + b for a, b in zip(hours, record["hours"])]
        listed = lambda sums, most: [{"name": name, "prompts": kept[0], "active": kept[1], "tokens": kept[2]}
                                     for name, kept in sorted(sums.items(), key=lambda one: (-one[1][1], -one[1][0])) if kept[0] > 0 or kept[2] > 0][:most]
        return {"ready": True, "days": by_day, "tools": listed(tools, 4), "projects": listed(projects, 6), "hours": hours, "wait": self._middle(waits),
                "updated": int(time.time() * 1000)}


class Schedules:
    """Questions the owner set to be asked at a time: once, every day at an hour, or every so many minutes. Each
    names the conversation it is for and the words. This keeps them (in one file of the data folder, readable by
    its owner only), says which are due, and notes how each asking went; the agent does the asking. (As
    Schedules.cs of the Windows program.)"""

    def __init__(self, path):
        self.path, self.items = path, []
        try:
            with open(path, encoding="utf-8") as stream:
                kept = json.load(stream)
            self.items = kept_rules(kept, SCHEDULE_KEYS, SCHEDULE_NUMBERS, SCHEDULE_FLAGS)
            for item in self.items:             # (kept as "1,2,3"; a rule written before there were days has none)
                item["days"] = ",".join(str(day) for day in week_days(item.get("days") if isinstance(item.get("days"), str) else "") or [])
        except (OSError, ValueError, TypeError):
            pass

    def _save(self):
        try:
            write_private(self.path, json.dumps(self.items, ensure_ascii=False))
        except OSError:
            pass

    @staticmethod
    def next_of(item, now):
        """When an item is due next after `now` (seconds), in milliseconds: 0 for a single one whose time has
        passed, or one that is off."""
        if not item["on"]:
            return 0
        if item["kind"] == "once":
            return item["at"] if item["at"] > now * 1000 else 0
        if item["kind"] == "every":
            return int((now + item["minutes"] * 60) * 1000)
        try:
            hour, minute = (int(part) for part in item["time"].split(":"))
        except (ValueError, AttributeError, TypeError):
            return 0                        # a hand-edited time must not take the report down
        at = datetime.datetime.fromtimestamp(now).replace(hour=hour, minute=minute, second=0, microsecond=0)
        if at.timestamp() <= now:
            at += datetime.timedelta(days=1)
        days = week_days(item.get("days", "")) or []
        while days and at.isoweekday() not in days:      # on the first of its days of the week from then on
            at += datetime.timedelta(days=1)
        return int(at.timestamp() * 1000)

    @staticmethod
    def shown(item):
        """As the phone is told of one."""
        out = {key: item[key] for key in ("id", "tool", "session", "title", "text", "kind", "time", "at", "minutes", "on", "next", "runs")}
        out["days"] = week_days(item.get("days", "")) or []
        out["last"] = {"at": item["last_at"], "ok": item["last_ok"], "note": item["last_note"]} if item["last_at"] else None
        return out

    def all(self):
        return [self.shown(item) for item in self.items]

    def set(self, given, now):
        """Sets one: a new one under a new number, or the one of that number anew. Says in words what is wrong with it."""
        word = lambda name: given.get(name) if isinstance(given.get(name), str) else ""
        whole = lambda name: given.get(name) if type(given.get(name)) in (int, float) else 0
        item = {"id": word("id"), "tool": word("tool"), "session": word("session"), "title": word("title")[:80], "text": word("text").strip(), "kind": word("kind"),
                "time": word("time"), "at": int(whole("at")), "minutes": int(whole("minutes")), "on": given.get("on") is not False,
                "next": 0, "runs": 0, "last_at": 0, "last_ok": False, "last_note": ""}
        days = week_days(given.get("days"))
        if days is None:
            raise OpError("星期需在周一到周日之间")
        item["days"] = ",".join(str(day) for day in days) if item["kind"] == "daily" else ""
        if not ID_RE.match(item["id"]):
            raise OpError("编号无效")
        if item["tool"] not in ("claude", "codex", "opencode", "qoder", "cline", "pi"):
            raise OpError("工具无效")
        if not session_id_ok(item["tool"], item["session"]):
            raise OpError("请选择要提问的对话")
        if not 0 < len(item["text"]) <= 2000:
            raise OpError("要问的话需为 1 至 2000 字")
        if item["kind"] == "once":
            if item["on"] and not now * 1000 < item["at"] <= (now + 366 * 86400) * 1000:
                raise OpError("时间需在现在之后、一年之内")
        elif item["kind"] == "daily":
            if not re.match(r"\A([01]\d|2[0-3]):[0-5]\d\Z", item["time"]):
                raise OpError("每天的时间需写成 09:30 这样")
        elif item["kind"] == "every":
            if not 5 <= item["minutes"] <= 10080:
                raise OpError("间隔需在 5 分钟到 7 天之间")
        else:
            raise OpError("重复方式无效")
        item["next"] = self.next_of(item, now)
        known = next((one for one in self.items if one["id"] == item["id"]), None)
        if known is None and len(self.items) >= MAX_SCHEDULES:
            raise OpError("最多保存 %d 个定时提问" % MAX_SCHEDULES)
        if known is not None:
            item.update({key: known[key] for key in ("runs", "last_at", "last_ok", "last_note")})
            self.items[self.items.index(known)] = item
        else:
            self.items.append(item)
        self._save()
        return item

    def remove(self, key):
        left = [item for item in self.items if item["id"] != key]
        gone = len(left) != len(self.items)
        if gone:
            self.items = left
            self._save()
        return gone

    def due(self, now):
        """What is to be asked now. Each is moved on to its next time at once (a single one is switched off), so
        that it is asked once however long the asking takes."""
        due = []
        for item in self.items:
            if item["on"] and 0 < item["next"] <= now * 1000:
                due.append(dict(item))
                if item["kind"] == "once":
                    item["on"], item["next"] = False, 0
                else:
                    item["next"] = self.next_of(item, now)
        if due:
            self._save()
        return due

    def done(self, key, ok, note, now):
        """How an asking went, for its owner to read."""
        for item in self.items:
            if item["id"] == key:
                item["runs"] += 1
                item.update(last_at=int(now * 1000), last_ok=ok, last_note=(note or "")[:200])
                self._save()


class Chains:
    """Work passed on from one conversation to another when the first has done its part: "when this one stops, give
    what it said last to that one, with these words of mine". The Windows program hears that a conversation has
    stopped from the tools' own hooks; on Linux there are none, so a finished turn is seen by watching the source
    conversation's record: it grows while the tool works, and once it has been quiet for a while after growing,
    the turn is over. (As Chains.cs of the Windows program, where the words are also kept encrypted for the
    Windows account; here the file is only readable by its owner.)"""

    def __init__(self, path):
        self.path, self.items = path, []
        self.seen = {}       # chain id -> {"updated": the record's stamp last seen, "armed": stamp or None}
        try:
            with open(path, encoding="utf-8") as stream:
                kept = json.load(stream)
            self.items = kept_rules(kept, CHAIN_KEYS, CHAIN_NUMBERS, CHAIN_FLAGS)
        except (OSError, ValueError, TypeError):
            pass

    def _save(self):
        try:
            write_private(self.path, json.dumps(self.items, ensure_ascii=False))
        except OSError:
            pass

    @staticmethod
    def shown(item):
        """As the phone is told of one."""
        out = {key: item[key] for key in ("id", "from", "from_tool", "from_title", "to", "to_tool", "to_title",
                                          "text", "left", "rounds", "runs")}
        out["last"] = {"at": item["last_at"], "ok": item["last_ok"], "note": item["last_note"]} if item["last_at"] else None
        return out

    def all(self):
        return [self.shown(item) for item in self.items]

    def set(self, given, found):
        """Sets one: a new one under a new number, or the one of that number anew (its rounds begin again). Both
        conversations must be among those this computer lists."""
        word = lambda name: given.get(name) if isinstance(given.get(name), str) else ""
        listed = lambda session: next((s for s in found if s["id"] == session), None)
        item = {"id": word("id"), "from": word("from"), "from_tool": word("from_tool"), "from_title": "",
                "to": word("to"), "to_tool": word("to_tool"), "to_title": "",
                "text": word("text").strip(), "left": 0, "rounds": 0, "runs": 0, "last_at": 0, "last_ok": False, "last_note": ""}
        source, taker = listed(item["from"]), listed(item["to"])
        if not ID_RE.match(item["id"]):
            raise OpError("编号无效")
        for tool in (item["from_tool"], item["to_tool"]):
            if tool not in CHAIN_TOOLS:
                raise OpError("工具无效")
        for which, pick in (("from", source), ("to", taker)):
            if pick is None or pick["tool"] != item[which + "_tool"]:
                raise OpError("电脑上没有找到这个对话，请刷新后重试")
            item[which + "_title"] = pick["title"][:80]
        if (item["from_tool"], item["from"]) == (item["to_tool"], item["to"]):
            raise OpError("不能接力给同一段对话")
        if not 0 < len(item["text"]) <= 1000:
            raise OpError("要说的话需为 1 至 1000 字")
        rounds = given.get("rounds")
        if isinstance(rounds, bool) or not isinstance(rounds, int) or not 1 <= rounds <= 10:
            raise OpError("次数需在 1 到 10 之间")
        item["rounds"] = item["left"] = rounds
        known = next((one for one in self.items if one["id"] == item["id"]), None)
        if known is None and len(self.items) >= MAX_CHAINS:
            raise OpError("最多保存 %d 条接力" % MAX_CHAINS)
        if known is not None:
            self.items[self.items.index(known)] = item
        else:
            self.items.append(item)
        self.seen.pop(item["id"], None)
        self._save()
        return item

    def remove(self, key):
        left = [item for item in self.items if item["id"] != key]
        gone = len(left) != len(self.items)
        if gone:
            self.items = left
            self.seen.pop(key, None)
            self._save()
        return gone

    def step(self, found, now):
        """Yields (item, source) for each chain whose source conversation has finished a turn. A finished turn is
        one whose record grew since the last look and has now been quiet past CHAIN_QUIET; the stamp it was fired
        for is remembered, so the same turn does not fire twice and a record that was quiet all along (also after
        a restart) never does. (A copy is walked: passing one on can drop a rule that has no rounds left.)"""
        for item in list(self.items):
            if item["left"] <= 0:
                continue
            source = next((s for s in found if s["tool"] == item["from_tool"] and s["id"] == item["from"]), None)
            if source is None:
                self.seen.pop(item["id"], None)
                continue
            updated = int(source.get("updated") or 0)
            seen = self.seen.setdefault(item["id"], {})
            if "updated" not in seen:
                seen.update(updated=updated, armed=None)        # first sight: a baseline, nothing to pass on
            elif seen["updated"] != updated:
                seen.update(updated=updated, armed=updated)     # the record grew: a turn is under way
            elif seen["armed"] is not None and updated and now - updated / 1000.0 >= CHAIN_QUIET:
                seen["armed"] = None                            # quiet after growing: the turn is over
                yield item, source

    def done(self, key, ok, note, now):
        """Notes how a passing-on went. Only one that arrived takes a round: a transient
        'this conversation is not open in a terminal' must not spend the owner's rounds."""
        for item in self.items:
            if item["id"] != key:
                continue
            item["runs"] += 1
            item["last_at"], item["last_ok"], item["last_note"] = int(now * 1000), ok, note[:300]
            if ok:
                item["left"] = max(0, item["left"] - 1)
            if item["left"] <= 0:
                self.items.remove(item)
                self.seen.pop(key, None)
            self._save()
            return


def handover(source, messages, note):
    """What a conversation is handed over with: what its owner asked first and last, what the tool answered last
    and did last, taken from its record. Written as its owner's own words to the conversation that takes over.
    (The same words as the Windows program's.)"""
    def cut(text, most):
        text = text.strip()
        # The phone and Windows count the 8,000-character input limit in UTF-16 units.
        encoded = text.encode("utf-16-le", errors="replace")
        return text if len(encoded) <= most * 2 else encoded[:most * 2].decode("utf-16-le", errors="ignore") + "…"
    asked = [strip_handover_ask(m["text"]) for m in messages if m.get("role") == "user"]
    answered = next((m["text"] for m in reversed(messages) if m.get("role") == "assistant"), "")
    steps = [m for m in messages if m.get("role") == "tool"]
    label = TOOL_LABELS.get(source["tool"], source["tool"])
    brief = "【对话交接】下面是我在另一段对话里的进展（%s，“%s”%s），请接着处理。\n" % (label, cut(source["title"], 100), "，项目 " + cut(source["dir"], 100) if source["dir"] else "")
    brief += "原对话 ID：" + cut(source["id"], 128) + "\n"
    if asked:
        brief += "\n我最初的要求：\n" + cut(asked[0], 800) + "\n"
    if len(asked) > 1:
        brief += "\n我最近的要求：\n" + cut(asked[-1], 800) + "\n"
    if answered:
        brief += "\n它最后的回答：\n" + cut(answered, 2000) + "\n"
    if steps:
        brief += "\n它最近做的操作：\n"
        for step in steps[-8:]:
            brief += "- " + cut(step["text"], 160) + "\n"
            result = cut(step.get("result") or "", 160)
            if result:
                brief += "  返回摘录：" + result.replace("\n", "\n  ") + "\n"
            else:
                brief += "  记录中没有返回内容\n"
    if note.strip():
        brief += "\n我的补充：\n" + cut(note, 1000) + "\n"
    return brief.rstrip()


def resume_handover(source, between_tools, delta_messages):
    """Short handover brief when switching back to an AI that paused earlier in the same handover thread."""
    def cut(text, most):
        text = text.strip()
        encoded = text.encode("utf-16-le", errors="replace")
        return text if len(encoded) <= most * 2 else encoded[:most * 2].decode("utf-16-le", errors="ignore") + "…"
    asked = [strip_handover_ask(m["text"]) for m in delta_messages if m.get("role") == "user"]
    asked = [q for q in asked if q.strip()]
    answered = next((m["text"] for m in reversed(delta_messages) if m.get("role") == "assistant"), "")
    steps = [m for m in delta_messages if m.get("role") == "tool"]
    between = "、".join(TOOL_LABELS.get(t, t) for t in between_tools)
    brief = "【对话交接】上面你暂停之后，我切到 %s 继续处理了一段%s，现在切回你这里接着做。\n" % (
        between, "（项目 " + cut(source["dir"], 100) + "）" if source.get("dir") else "")
    if len(asked) == 1:
        brief += "\n这段期间我的要求：\n" + cut(asked[0], 800) + "\n"
    elif len(asked) > 1:
        brief += "\n这段期间我最初的要求：\n" + cut(asked[0], 800) + "\n"
        brief += "\n这段期间我最近的要求：\n" + cut(asked[-1], 800) + "\n"
    if answered:
        brief += "\n对方最后的回答：\n" + cut(answered, 2000) + "\n"
    if steps:
        brief += "\n对方最近做的操作：\n"
        for step in steps[-8:]:
            brief += "- " + cut(step["text"], 160) + "\n"
            result = cut(step.get("result") or "", 160)
            if result:
                brief += "  返回摘录：" + result.replace("\n", "\n  ") + "\n"
            else:
                brief += "  记录中没有返回内容\n"
    return brief.rstrip()


def chain_brief(source, messages, note):
    """What a chain hands over when its source has finished a turn: what the tool answered last, and the words
    the owner set with the rule. (Lighter than a hand-over: the conversation that takes it keeps its own thread.)"""
    cut = lambda text, most: text.strip() if len(text.strip()) <= most else text.strip()[:most] + "…"
    answered = next((m["text"] for m in reversed(messages) if m.get("role") == "assistant"), "")
    brief = "【接力】%s“%s”完成了一轮，把它的进展转给你。\n" % (
        TOOL_LABELS.get(source["tool"], source["tool"]),
        source["title"])
    if answered:
        brief += "\n它最后的回答：\n" + cut(answered, 2000) + "\n"
    if note.strip():
        brief += "\n我的要求：\n" + cut(note, 1000) + "\n"
    return brief.rstrip()


class Agent:
    def __init__(self, data, password):
        self.data = data
        self.password = password
        self.instance = secrets.token_hex(16)
        self.projects = Projects(data)
        self.marks = Marks(data)
        self.handovers = Handovers(data)
        self.sessions = Sessions(self.marks, self.handovers)
        self.uploads = Uploads()
        # the owner's work: in numbers, and the questions asked at a set time
        self.stats = Stats(self.sessions.claude_home, lambda: os.environ.get("CODEX_HOME") or os.path.join(self.sessions.home, ".codex"))
        self.schedules = Schedules(os.path.join(data, "schedules.json"))
        self.chains = Chains(os.path.join(data, "chains.json"))
        self.quotas = quotas_store.Quotas(data)
        self.next_codex_ask = self.next_agy_ask = 0.0
        # what a conversation the phone runs hands to another AI by itself (delegations.py)
        self.delegations = delegations_mod.Delegations(delegations_mod.Hands(
            begin=self.begin_task, state_of=self.state_of, say=self.say_to_member, stop=self.stop_member, close=self.close_member,
            answer=self.answer_in, card=self.card_in, label=peers_mod.peers_label), changed=lambda: self.wake())
        self.peers = peers_mod.Peers(peers_mod.socket_path(data), find_tool, quota_of=self.quotas.of,
                                     config_dir=data, chat_tools=CHAT_TOOLS,
                                     open_read_only=self.open_peer_chat,
                                     bridge_script=os.path.abspath(__file__), delegations=self.delegations)
        self.worktrees = worktrees.Worktrees(os.path.join(data, "worktrees"))
        self.teams = teams.Teams(os.path.join(data, "teams.json"), teams.Hands(
            begin_in=self.begin_member_in, say=self.say_to_member,
            say_with_attachments=self.say_to_member_with_attachments,
            stop=self.stop_member, close=self.close_member,
            changes=self.changes_of, available=self.chat_tools_available, limits=self.quotas.of,
            make_worktree=self.make_team_worktree, worktree_diff=self.worktrees.diff,
            worktree_stat=self.worktrees.stat, apply_worktree=self.apply_team_worktree,
            clean_worktrees=self.clean_team_worktrees),
            chat_tools=CHAT_TOOLS,
            error_types=(OpError, teams.OpError, worktrees.OpError, ValueError))
        self.teams_version = 0
        self.teams.changed = self._on_teams_changed
        self.team_seen = {}
        self.team_seen_lock = threading.Lock()
        self.next_asking = 0.0
        self.sessions_sent = ((0, 0), 0.0)   # ((sessions.version, teams_version), when)
        self.tools = (["shell"], 0.0)
        self.terminals = {}
        self.completed = {}
        self.reported = set()
        self.work = threading.RLock()
        self.wakeup = threading.Event()
        self.stop = threading.Event()
        self.scan_want = ({}, [])       # what the conversation lookup last looked at (projects, terminals)
        self.scan_wake = threading.Event()
        self.scanned = threading.Condition()
        self.scan_done = 0              # a look is over, whether or not it changed anything
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
        self.warned_plain = False       # said once: an http relay that is not on a private net

    # ---- wiring
    def wake(self):
        self.wakeup.set()

    def _on_teams_changed(self):
        self.teams_version += 1
        self.wake()

    def stop_run(self, *_):
        self.stop.set()
        self.wakeup.set()

    def shell(self, cfg):
        shell = str(cfg.get("Shell") or "").strip() or os.environ.get("SHELL") or "/bin/bash"
        return shell if os.path.isfile(shell) else "/bin/bash"

    # ---- protocol
    def lost_token(self):
        """The relay forgot this sign-in: the next report must be allowed to sign in at once."""
        self.token = ""
        self.next_login = 0.0

    def login(self):
        if not SERVER_RE.match(self.server):
            return False
        # A refused password is tried again once a minute: the relay locks sign-in after a
        # few wrong ones. A relay that cannot be reached yet (both just started, the network
        # is not up) is tried again soon.
        self.next_login = time.time() + 60
        try:
            status, body = self.http.post(self.server + "/api/login", {"password": self.password})
            token = reply_object(body).get("token") if status == 200 else None
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
        if not self.warned_plain and self.server.startswith("http://") and not private_address(self.server):
            self.warned_plain = True
            say("中转地址是 http 且不在内网：密码与对话内容会明文经过网络，请改用 https")
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
                self.tools = ([t for t in ("claude", "codex", "opencode", "qoder", "cline", "pi") if find_tool(t)] + ["shell"], now)
            self.uploads.expire()               # uploads nothing has arrived for lose their temp files
            # what the conversation lookup works on: its own thread takes it from here
            self.scan_want = (dirs, list(self.terminals.values()))
            if enabled and now >= self.next_asking:
                self.next_asking = now + 15
                self.ask(now)
            if enabled:
                for due, source in self.chains.step(self.sessions.found, now):
                    try:
                        read = self.sessions.read(source["id"], source["tool"], source["dir"], dirs)
                        self.chains.done(due["id"], True, self.tell(due["to_tool"], due["to"],
                                                                    chain_brief(source, read.get("messages", []), due["text"])), now)
                    except (OpError, OSError, ValueError) as failed:
                        self.chains.done(due["id"], False, str(failed), now)
                codex_chats = [one for one in self.terminals.values() if isinstance(one, CodexChat) and not one.closed]
                if codex_chats and now >= self.next_codex_ask:
                    self.next_codex_ask = now + 600
                    for one in codex_chats:
                        one.ask_rate_limits()
                if now >= self.next_agy_ask:
                    self.next_agy_ask = now + 600
                    agy = find_tool("agy")
                    if agy:
                        threading.Thread(target=self.quotas.ask_agy, args=(agy,), daemon=True).start()
            for live in self.terminals.values():
                if live.session:
                    self.teams.learn(live.id, live.tool, live.session)
                    hf = getattr(live, "handed_from", None)
                    if hf is not None:
                        src, first_title = hf
                        self.handovers.add(live.tool, live.session, src["tool"], src["id"], first_title)
                        live.handed_from = None
                        self.scan_wake.set()
            team_features = [one for one in TEAM_FEATURES if one[:-5] in self.tools[0]]
            payload = {
                "info": {"instance": self.instance, "enabled": enabled, "tools": self.tools[0],
                         "features": FEATURES + team_features,
                         "quotas": self.quotas.report() if enabled else [],
                         "team_cards": self.teams.cards(self.terminal_is_asking) if enabled else [],
                         "version": VERSION, "newer": "",
                         "shell": os.path.basename(self.shell(cfg)),
                         "workspaces": list(dirs.keys()),
                         "projects": self.sessions.told(self.projects.entries(cfg, dirs)),
                         "candidates": self.sessions.candidates},
                "terminals": [dict({"id": live.id, "tool": live.tool, "dir": live.project, "created": live.created,
                               "state": "closed" if live.closed else "running",
                               "cols": live.cols, "rows": live.rows, "session": live.session,
                               "team": self.teams.team_of(live.id, live.tool, live.session),
                               "parent": self.task_of(live.id, "parent"), "task": self.task_of(live.id, "id"),
                               "status": "" if live.closed or live.status == "ended" else live.status, "exit_code": live.exit_code,
                               "error": "程序退出（代码 %d），请检查终端画面中的原因" % live.exit_code
                                        if live.exit_code else ""},
                              # a conversation run through its tool's structured mode: what it said last and what it asks
                              **({"form": "chat", "said": live.said_last, "ask": live.asking} if isinstance(live, Chat) else {}))
                               for live in self.terminals.values()],
                "output": output, "acks": acks}
            # A program may end while this report travels: it is forgotten only after a
            # report that said so, or the phone would show it running for ever.
            ended = [item["id"] for item in payload["terminals"] if item["state"] == "closed"]
            # The list of conversations is long: it goes out when it changed, and now and then
            # in case the relay restarted.
            listed = (self.sessions.version, self.teams_version)
            listing = listed != self.sessions_sent[0] or now - self.sessions_sent[1] > 10
            if listing:
                payload["sessions"] = [dict(s, team=self.teams.team_of_session(s["tool"], s["id"])) for s in self.sessions.found]
            step_dirs = dict(dirs) if enabled else None
        if step_dirs is not None:
            try:
                self.teams_step(step_dirs, now)
            except Exception as failed:
                say("协作任务推进失败：%s" % type(failed).__name__)
        try:
            status, body = self.http.post(self.server + "/api/terminal/agent", payload, self.token)
        except (OSError, http.client.HTTPException):
            return
        if status == 401:
            self.lost_token()
            return
        if status != 200:
            return
        try:
            result = json.loads(body.decode("utf-8") or "{}")
        except ValueError:
            return
        if not isinstance(result, dict):
            return
        sweep, forgotten = [], []
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
            for live in self.terminals.values():
                if live.take_sweep() and live not in sweep:
                    sweep.append(live)
            for key in [k for k, live in self.terminals.items() if live.can_forget()]:
                live = self.terminals.pop(key)
                if live not in sweep:
                    sweep.append(live)
                forgotten.append(live)
        # the /proc walks, the kills and the closes belong to no state lock
        for live in sweep:
            live.kill_leftovers()
        for live in forgotten:
            live.dispose()

    def execute(self, op, cfg, dirs):
        """Carries out one operation. Called with self.work held, from the report loop or
        the held pull; the same id is never carried out twice."""
        try:
            op_id, terminal = check_op(op, need_terminal=op.get("action") not in
                                       PROJECT_ACTIONS + FILE_ACTIONS + WORK_ACTIONS + ("session_read", "session_mark", "session_delete"))
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
                if action in UPLOAD_ACTIONS:
                    result = self.uploads.run(root, op)
                elif action == "file_find":
                    result = find_file(root, op.get("path", ""))
                else:
                    result = files(root, op.get("path", ""), action, op.get("offset", 0))
            elif action == "session_read":
                result = self.sessions.read(str(op.get("session") or ""), str(op.get("tool") or ""),
                                            str(op.get("dir") or ""), dirs, str(op.get("since") or ""))
            elif action == "session_mark":
                # pinned or hidden, or no longer: of a conversation this computer listed
                session, tool = str(op.get("session") or ""), str(op.get("tool") or "")
                if not any(s["id"] == session and s["tool"] == tool for s in self.sessions.found):
                    raise OpError("电脑上没有找到这个对话，请刷新后重试")
                for kind in MARKS:
                    if isinstance(op.get(kind), bool):
                        self.marks.set(kind, tool, session, op[kind])
                self.scan_wake.set()                  # the next report lists the conversations as they now stand
                result = {}
            elif action == "session_delete":
                self.sessions.delete(str(op.get("session") or ""), str(op.get("tool") or ""), dirs, list(self.terminals.values()))
                self.scan_wake.set()
                result = {}
            elif action == "work_stats":
                # read beside the terminals' work: the first reading of many records takes a while
                days = op.get("days") if type(op.get("days")) is int else 14
                threading.Thread(target=self.count, args=(op_id, days, dict(dirs)), daemon=True).start()
                return
            elif action in WORK_ACTIONS:
                result = self.owner_work(action, op, dirs)
            elif action == "start":
                self.start_terminal(op, dirs, terminal, op_id)
            else:
                live = self.terminals.get(terminal)
                if live is None or live.closed:
                    raise OpError("终端已结束")
                # a terminal can always be ended; typing into it needs its folder (or compare worktree) still allowed
                if action != "close" and live.dir not in dirs.values() \
                        and not (live.project in dirs and self.worktrees.contains(live.dir)):
                    raise OpError("目录不再获允许")
                if isinstance(live, Chat):
                    # a conversation: told something, stopped, answered or ended; it has no screen to type into
                    self.last_input = time.time()
                    if action == "say":
                        text = str(op.get("text") or "").strip()
                        attachments = read_attachments(live.dir, op.get("attachments"))
                        if (not text and not attachments) or len(text) > 8000:
                            raise OpError("请输入文字或添加图片，文字最多 8000 字")
                        live.say(text, op_id, attachments)
                    elif action == "stop":
                        live.stop_turn()
                    elif action == "answer":
                        live.answer(str(op.get("ask") or ""), str(op.get("decision") or ""))
                    elif action == "close":
                        live.begin_close()
                        self.teams.closed(live.id)
                    else:
                        raise OpError("对话不接受这个操作")
                elif action == "input":
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
                    self.teams.closed(live.id)
                else:
                    raise OpError("操作无效")
        except (OpError, teams.OpError, worktrees.OpError) as exc:
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

    # ---- the owner's work: in numbers, asked at a set time, handed from one conversation to another
    def count(self, op_id, days, dirs):
        """The numbers of the owner's work, read on a thread of its own and answered when they are there."""
        def named(folder):
            if not folder:
                return ""
            plain = folder.rstrip("/")
            within = [name for name, path in dirs.items() if plain == path.rstrip("/") or plain.startswith(path.rstrip("/") + "/")]
            return max(within, key=lambda name: len(dirs[name])) if within else os.path.basename(plain)
        error, result = "", None
        try:
            result = self.stats.read(days, named)
        except Exception:                       # never leak a path or a stack to the phone
            error = "统计没有读出来，请稍后再试"
        with self.work:
            self.completed[op_id] = {"id": op_id, "error": error}
            if result is not None:
                self.completed[op_id]["result"] = result
            self.reported.discard(op_id)
            self.soon = True
        self.wake()

    def tell(self, tool, session, text):
        """Says something to a conversation: typed into the terminal of the phone's that runs it, the way its page
        sends a message (the words as pasted text, then Enter). This computer has no other way to a conversation."""
        live = next((one for one in self.terminals.values() if not one.closed and one.tool == tool and one.session.lower() == session.lower()), None)
        if live is None:
            raise OpError("这段对话没有在手机打开的终端里运行，这台电脑只能把消息发到那里")
        if isinstance(live, Chat):
            live.say(text.strip())
            self.last_input = time.time()
            return "已发到手机上的对话"
        live.write("\x1b[200~" + text.replace("\r\n", "\n").replace("\r", "\n").strip() + "\x1b[201~\r")
        self.last_input = time.time()
        return "已发到手机终端"

    def ask(self, now):
        """Asks what is due (called with self.work held)."""
        for due in self.schedules.due(now):
            try:
                ok, note = True, self.tell(due["tool"], due["session"], due["text"])
            except OpError as refused:
                ok, note = False, str(refused)
            self.schedules.done(due["id"], ok, note, now)

    def owner_work(self, action, op, dirs):
        listed = lambda tool, session: next((s for s in self.sessions.found if s["tool"] == tool and s["id"] == session), None)
        if action == "work_schedules":
            return {"schedules": self.schedules.all()}
        if action == "work_schedule_set":
            # for a conversation this computer lists: its title is kept with it, to name it by later
            item = op.get("item")
            of = listed(item.get("tool"), item.get("session")) if isinstance(item, dict) else None
            if of is None:
                raise OpError("电脑上没有找到这个对话，请刷新后重试")
            return {"schedule": Schedules.shown(self.schedules.set(dict(item, title=of["title"]), time.time()))}
        if action == "work_schedule_remove":
            if not self.schedules.remove(str(op.get("schedule") or "")):
                raise OpError("没有找到这个定时提问")
            return {}
        if action == "work_teams":
            return {"teams": [teams.Teams.shown(one) for one in self.teams.all()]}
        if action == "work_team_start":
            item = op.get("item")
            if not isinstance(item, dict):
                raise OpError("协作任务无效")
            return {"team": teams.Teams.shown(self.teams.start(item))}
        if action == "work_team_stop":
            self.teams.stop(str(op.get("team") or ""))
            return {}
        if action == "work_team_resume":
            self.teams.resume(str(op.get("team") or ""))
            return {}
        if action == "work_team_remove":
            if not self.teams.remove(str(op.get("team") or "")):
                raise OpError("没有找到这个协作任务")
            return {}
        if action == "work_team_say":
            team_id = str(op.get("team") or "")
            raw_attachments = op.get("attachments")
            attachments = []
            if raw_attachments is not None:
                found = self.teams.of(team_id)
                if found is None:
                    raise OpError("没有找到这个协作任务")
                folder = dirs.get(found.dir)
                if not folder:
                    raise OpError("目录未获允许")
                attachments = read_attachments(folder, raw_attachments)
            return {"team": teams.Teams.shown(self.teams.say(team_id, str(op.get("text") or ""),
                                                             str(op.get("target") or ""), attachments))}
        if action == "work_team_pick":
            return {"team": teams.Teams.shown(self.teams.pick(str(op.get("team") or ""), str(op.get("tool") or "")))}
        if action == "work_team_swap":
            return {"team": teams.Teams.shown(self.teams.swap(str(op.get("team") or ""), str(op.get("role") or ""),
                                                              str(op.get("tool") or ""), str(op.get("note") or "")))}
        if action == "work_chains":
            return {"chains": self.chains.all()}
        if action == "work_chain_set":
            item = op.get("item")
            if not isinstance(item, dict):
                raise OpError("接力无效")
            return {"chain": Chains.shown(self.chains.set(item, self.sessions.found))}
        if action == "work_chain_remove":
            if not self.chains.remove(str(op.get("chain") or "")):
                raise OpError("没有找到这条接力")
            return {}
        source, taker = listed(op.get("from_tool"), op.get("from")), listed(op.get("to_tool"), op.get("to"))
        if action == "work_brief":
            # what a hand-over would say, for the phone to show, or to begin a new conversation with
            if source is None:
                raise OpError("电脑上没有找到要交接的对话，请刷新后重试")
            read = self.sessions.read(source["id"], source["tool"], source["dir"], dirs)
            return {"text": handover(source, read.get("messages", []), str(op.get("note") or "")), "tool": source["tool"], "dir": source["dir"], "title": source["title"]}
        if source is None:
            raise OpError("电脑上没有找到要交接的对话，请刷新后重试")
        if taker is None:
            raise OpError("接手的对话没有在电脑上开着")
        if (source["tool"], source["id"]) == (taker["tool"], taker["id"]):
            raise OpError("不能交接给同一段对话")
        read = self.sessions.read(source["id"], source["tool"], source["dir"], dirs)
        brief = handover(source, read.get("messages", []), str(op.get("note") or ""))
        return {"status": "accepted", "note": self.tell(taker["tool"], taker["id"], brief), "length": len(brief)}

    # ---- read-only peer consultations (peers.py `ask_ai`)
    def open_peer_chat(self, peer_tool, launcher, folder):
        """Opens a strictly read-only structured peer chat with no MCP server of its own (so a
        consultation never recurses); its permission asks are answered automatically — reads and
        searches allowed, everything else denied."""
        if peer_tool not in CHAT_TOOLS:
            raise OpError("这个工具不支持只读请教")
        cfg = read_config(self.data)
        dirs = self.projects.all(cfg)
        dir_name = next((name for name, path in dirs.items() if os.path.normpath(path) == os.path.normpath(folder)),
                        os.path.basename(folder.rstrip("/")))
        session = str(uuid.uuid4()) if peer_tool == "claude" else ""
        terminal = uuid.uuid4().hex
        chat = open_chat(terminal, dir_name, folder, peer_tool, launcher, self.wake, session, False, "read")
        chat.auto_ask = True
        chat.quotas_note = self.quotas.note
        return chat

    # ---- the tasks a conversation gives out (delegations.py Hands): conversations the phone runs, begun here
    def hear(self, chat):
        """A conversation the phone runs tells the tasks what its turns and questions are."""
        one = chat.id
        chat.turned = lambda how, said: self.delegations.turned(one, how, said)
        chat.on_ask = lambda ask, kind, title, text, choices: self.delegations.asked(one, ask, kind, title, text, choices)

    def begin_task(self, one):
        """Begins a task's conversation beside the one that gives it out: in its folder, never working more widely
        than the computer allows, or in a new worktree of the project where that is asked."""
        tool = one["tool"]
        if tool not in CHAT_TOOLS:
            raise delegations_mod.Refused("不能派给 %s：它不在可派的 AI 里" % tool)
        launcher = find_tool(tool)
        if launcher is None:
            raise delegations_mod.Refused("电脑上没有安装 " + peers_mod.peers_label(tool))
        cfg = read_config(self.data)
        with self.work:
            parent = self.terminals.get(one["parent"])
            if not isinstance(parent, Chat) or parent.closed:
                raise delegations_mod.Refused("原对话已结束")
            if sum(1 for live in self.terminals.values() if not live.closed) >= MAX_TERMINALS:
                raise delegations_mod.Refused("最多同时运行 %d 个终端，请先结束一个" % MAX_TERMINALS)
            cwd, worktree, branch = parent.dir, "", ""
            if one["worktree"]:
                try:
                    made = self.worktrees.create(parent.dir, uuid.uuid4().hex[:16], tool)
                except (worktrees.OpError, OSError) as error:
                    raise delegations_mod.Refused("没能建立独立工作树：" + str(error))
                cwd, worktree, branch = made["path"], made["path"], made.get("branch", "")
            terminal = uuid.uuid4().hex
            session = str(uuid.uuid4()) if tool == "claude" else ""
            chat = open_chat(terminal, parent.project, cwd, tool, launcher, self.wake, session, False, str(cfg.get("RemoteMaxMode") or ""),
                             mcp=self.peers.offer(tool, cwd, terminal, child=True))
            chat.quotas_note = self.quotas.note
            self.hear(chat)
            self.terminals[terminal] = chat
            chat.say(one["words"])
        self.wake()
        return {"terminal": terminal, "worktree": worktree, "branch": branch}

    def state_of(self, terminal):
        with self.work:
            live = self.terminals.get(terminal)
            if not isinstance(live, Chat) or live.closed or live.status == "ended":
                return ""
            return live.status

    def answer_in(self, terminal, ask, decision):
        with self.work:
            live = self.terminals.get(terminal)
        if not isinstance(live, Chat) or live.closed:
            return False
        try:
            live.answer(ask, decision)
            return True
        except OpError:
            return False

    def card_in(self, terminal, card):
        with self.work:
            live = self.terminals.get(terminal)
        if isinstance(live, Chat) and not live.closed:
            with live.gate:
                live.event(**card)

    def task_of(self, terminal, which):
        task = self.delegations.of(terminal)
        return "" if task is None else task["parent"] if which == "parent" else task["id"]

    # ---- the members of a team: conversations this program begins and tells (teams.py Hands)
    def terminal_is_asking(self, terminal):
        with self.work:
            live = self.terminals.get(terminal)
            return live is not None and not live.closed and live.status == "confirm"

    @staticmethod
    def _pty_message(live, words):
        text = words.replace("\r\n", "\n").replace("\r", "\n").strip()
        if getattr(live, "brackets", False):
            return "\x1b[200~" + text + "\x1b[201~\r"
        return " ".join(line.strip() for line in text.split("\n") if line.strip()) + "\r"

    def _send_pty_initial(self, live, words):
        """Waits briefly for a newly forked TUI to emit its initial prompt (and bracketed-paste
        switch) before typing the first task, avoiding dropped or garbled early stdin."""
        text = str(words or "").strip()
        if not text:
            return

        def deliver():
            deadline = time.monotonic() + 2.5
            while not live.closed and time.monotonic() < deadline:
                if live.last_out_at and time.monotonic() - live.last_out_at >= 0.35:
                    break
                time.sleep(0.05)
            if live.closed:
                return
            try:
                live.write(self._pty_message(live, text))
            except OpError:
                pass

        threading.Thread(target=deliver, daemon=True).start()

    def _pty_updated_now(self, live):
        if not live.session:
            return 0
        updated = self.sessions.updated_in_folder(live.session, live.tool, live.dir)
        if updated:
            return updated
        entry = next((one for one in self.sessions.found
                      if one["tool"] == live.tool and one["id"] == live.session), None)
        return int(entry.get("updated") or 0) if entry else 0

    def begin_member_in(self, tool, dir_name, words, read, folder_override=None):
        cfg = read_config(self.data)
        dirs = self.projects.all(cfg)
        if dir_name not in dirs:
            raise OpError("目录未获允许")
        if tool not in CHAIN_TOOLS:
            raise OpError("电脑没有这个工具")
        launcher = find_tool(tool)
        if launcher is None:
            raise OpError("电脑没有这个工具")
        if folder_override:
            if not os.path.isdir(folder_override) or not self.worktrees.contains(folder_override):
                raise OpError("独立工作区无效")
            cwd = folder_override
        else:
            cwd = dirs[dir_name]
        with self.work:
            if sum(1 for one in self.terminals.values() if not one.closed) >= MAX_TERMINALS:
                raise OpError("最多同时运行 %d 个终端，请先结束一个" % MAX_TERMINALS)
            fresh = tool in ("claude", "opencode", "qoder", "pi")
            session = ""
            if fresh:
                # named by us, so the terminal knows its conversation at once (as start_terminal does)
                session = "ses_" + secrets.token_hex(13) if tool == "opencode" else str(uuid.uuid4())
            terminal = uuid.uuid4().hex
            mode = "read" if read else str(cfg.get("RemoteMaxMode") or "")
            if tool in CHAT_TOOLS:
                chat = open_chat(terminal, dir_name, cwd, tool, launcher, self.wake, session, False, mode,
                                 mcp=None if read else self.peers.offer(tool, cwd))
                chat.quotas_note = self.quotas.note
                chat.turned = lambda how, said, one=terminal: self.teams.turned(one, how, said)
                chat.on_session = lambda sid, one=terminal, t=tool: self.teams.learn(one, t, sid)
                chat.on_close = lambda one=terminal: self.teams.closed(one)
                self.terminals[terminal] = chat
                if session:
                    self.teams.learn(terminal, tool, session)
                if str(words).strip():
                    chat.say(str(words).strip())
                return terminal
            argv = tool_argv(tool, launcher, session, False, mode, fresh)
            live = Live(terminal, dir_name, cwd, 80, 24, argv, self.wake, tool, session)
            live.on_close = lambda one=terminal: self.teams.closed(one)
            self.terminals[terminal] = live
            if session:
                self.teams.learn(terminal, tool, session)
            with self.team_seen_lock:
                self.team_seen[terminal] = {"updated": 0, "armed": 0}
            self._send_pty_initial(live, words)
            return terminal

    def member(self, terminal):
        with self.work:
            live = self.terminals.get(terminal)
            if live is None or live.closed:
                raise OpError("这个成员的对话已经结束")
            return live

    def say_to_member(self, terminal, words):
        self.say_to_member_with_attachments(terminal, words, None)

    def say_to_member_with_attachments(self, terminal, words, attachments=None):
        live = self.member(terminal)
        attachments = list(attachments or [])
        if isinstance(live, Chat):
            live.say(words.strip(), attachments=attachments)
            return
        baseline = self._pty_updated_now(live)
        with self.team_seen_lock:
            self.team_seen[terminal] = {"updated": baseline, "armed": 0}
        live.write(self._pty_message(live, attachment_text(words.strip(), attachments)))

    def stop_member(self, terminal):
        live = self.member(terminal)
        if isinstance(live, Chat):
            try:
                live.stop_turn()
            except OpError:
                pass                    # its turn was already over: nothing to interrupt
        else:
            live.write("\x03")         # the pty way of interrupting a turn

    def close_member(self, terminal):
        with self.work:
            live = self.terminals.get(terminal)
            if live is not None and not live.closed:
                live.begin_close()
        self.wake()

    def changes_of(self, dir_name):
        cfg = read_config(self.data)
        folder = self.projects.all(cfg).get(dir_name)
        return worktrees.changes_in(folder) if folder else ""

    def chat_tools_available(self):
        return [one for one in CHAIN_TOOLS if find_tool(one)]

    def make_team_worktree(self, dir_name, team_id, tool):
        cfg = read_config(self.data)
        folder = self.projects.all(cfg).get(dir_name)
        if not folder:
            raise OpError("目录未获允许")
        return self.worktrees.create(folder, team_id, tool)

    def apply_team_worktree(self, dir_name, wt_path, base_commit):
        cfg = read_config(self.data)
        folder = self.projects.all(cfg).get(dir_name)
        if not folder:
            raise OpError("目录未获允许")
        return self.worktrees.apply(folder, wt_path, base_commit)

    def clean_team_worktrees(self, dir_name, team_id):
        cfg = read_config(self.data)
        folder = self.projects.all(cfg).get(dir_name)
        self.worktrees.remove_all(folder, team_id)

    def teams_step(self, dirs, now):
        """Moves the pty members of the teams on: a structured chat reports its own turn ending;
        a terminal's member is done when its conversation record has gone quiet after growing
        (the way chains knows a turn has ended). Runs outside `self.work`."""
        checks = []
        for team in self.teams.all():
            if team.state != "running":
                continue
            for role in list(team.awaited):
                terminal = team.members.get(role)
                if not terminal:
                    continue
                with self.work:
                    live = self.terminals.get(terminal)
                if live is None or isinstance(live, Chat):
                    continue                # its own turn_ended callback drives it
                checks.append((team, role, terminal, live))
        for team, role, terminal, live in checks:
            if live.closed:
                with self.team_seen_lock:
                    self.team_seen.pop(terminal, None)
                self.teams.turned(terminal, "stopped", "")
                continue
            if not live.session:
                continue                    # the scan has not named its conversation yet
            self.teams.learn(terminal, live.tool, live.session)
            updated = 0
            if hasattr(self.sessions, "updated_in_folder") and getattr(live, "dir", ""):
                updated = self.sessions.updated_in_folder(live.session, live.tool, live.dir)
            if not updated:
                entry = next((one for one in self.sessions.found
                              if one["tool"] == live.tool and one["id"] == live.session), None)
                updated = int(entry.get("updated") or 0) if entry else 0
            if not updated:
                continue
            with self.team_seen_lock:
                seen = self.team_seen.setdefault(terminal, {"updated": updated, "armed": 0})
                if seen["updated"] != updated:
                    seen["updated"] = updated
                    seen["armed"] = updated
                    continue
                ready = bool(seen["armed"] and now - updated / 1000.0 >= self.teams.quiet)
                if ready:
                    self.team_seen.pop(terminal, None)
            if ready:
                said = ""
                try:
                    if hasattr(self.sessions, "read_folder") and getattr(live, "dir", ""):
                        try:
                            read, _ = self.sessions.read_folder(live.session, live.tool, live.dir)
                        except OpError:
                            read = self.sessions.read(live.session, live.tool, team.dir, dirs)
                    else:
                        read = self.sessions.read(live.session, live.tool, team.dir, dirs)
                    said = next((m["text"] for m in reversed(read.get("messages", []))
                                 if m.get("role") == "assistant"), "")
                except (OpError, OSError, ValueError):
                    pass
                self.teams.turned(terminal, "done", said)

    def known_now(self, session, tool, name):
        """A conversation of this computer's lists; one it has only just had is not listed yet: a look is asked for
        now and waited for, rather than the phone told to refresh."""
        if not session_id_ok(tool, session):
            return None
        saved = self.sessions.known(session, tool, name)
        if saved is None:
            self.scan_wake.set()
            seen, deadline = self.scan_done, time.time() + 3.5
            with self.scanned:
                while self.scan_done == seen and time.time() < deadline:
                    self.scanned.wait(deadline - time.time())
            saved = self.sessions.known(session, tool, name)
        return saved

    def reopening(self, op, tool, name):
        """The terminal a `reopen` start leaves: a Claude Code or Codex conversation that runs as a chat and is to be
        shown on its tool's own screen, or the other way round. One at work, asking or unnamed stays."""
        source = self.terminals.get(str(op.get("reopen") or ""))
        if source is None or source.closed or source.closing_at is not None:
            raise OpError("原对话已结束，请刷新后重试")
        if source.tool != tool or source.project != name or tool not in ("claude", "codex"):
            raise OpError("切换视图的选项无效")
        if isinstance(source, Chat) == (op.get("form") == "chat"):
            raise OpError("这段对话已经是这种视图")
        if not source.session:
            raise OpError("%s 还没有给这段对话编号，发一条消息后再切换" % TOOL_LABELS.get(tool, tool))
        if source.status in ("busy", "confirm"):
            raise OpError("对话正在执行或等待确认，请等这一轮结束或先停止")
        if self.teams.team_of(source.id, source.tool, source.session):
            raise OpError("协作任务的成员对话暂不支持切换视图")
        return source

    def handed(self, op, tool, name):
        """Locate the source conversation for an in-place AI switch (`from` + `from_tool`) on a project chat."""
        if op.get("form") != "chat" or op.get("session") or "reopen" in op or not name:
            raise OpError("换 AI 的选项无效")
        if not str(op.get("text") or "").strip():
            raise OpError("请写下要交给它的话")
        from_tool, from_id = str(op.get("from_tool") or ""), str(op.get("from") or "")
        saved = next((s for s in self.sessions.found
                      if s["tool"] == from_tool and s["id"].lower() == from_id.lower()), None)
        if saved is None:
            raise OpError("电脑上没有找到原来的对话，请刷新后重试")
        if saved["dir"] != name:
            raise OpError("只能在同一个项目里换 AI")
        if self.teams.team_of_session(saved["tool"], saved["id"]):
            raise OpError("协作任务中的对话请在协作里换 AI")
        running = next((t for t in self.terminals.values()
                        if not t.closed and t.closing_at is None and t.tool == saved["tool"]
                        and (t.session or "").lower() == saved["id"].lower()), None)
        if running is not None and running.status in ("busy", "confirm"):
            raise OpError("对话正在执行或等待确认，请等这一轮结束或先停止")
        return saved, running

    def start_terminal(self, op, dirs, terminal, op_id=""):
        if terminal in self.terminals:
            return                              # already started; the operation is a repeat
        tool = str(op.get("tool") or "")
        if tool not in ("claude", "codex", "opencode", "qoder", "cline", "pi", "shell"):
            raise OpError("电脑没有这个工具")
        name = str(op.get("dir") or "")
        if name not in dirs:
            raise OpError("目录未获允许")
        cwd = dirs[name]
        if not os.path.isdir(cwd):
            raise OpError("电脑上没有这个文件夹")
        # the same conversation shown the other way: the terminal it leaves is ended below, once all else is certain
        source = self.reopening(op, tool, name) if "reopen" in op else None
        handed, handed_runs = self.handed(op, tool, name) if ("from" in op or "from_tool" in op) else (None, None)
        if sum(1 for live in self.terminals.values() if not live.closed) - (1 if (source or handed_runs) else 0) >= MAX_TERMINALS:
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
            session, history = (source.session, False) if source else (str(op.get("session") or ""), op.get("history") is True)
            if source:
                if self.known_now(session, tool, name) is None:
                    raise OpError("%s 还没有保存这段对话，发一条消息后再切换" % TOOL_LABELS.get(tool, tool))
                source.kill()               # (waits for it: a conversation has one writer at a time)
                if not source.closed:
                    raise OpError("原对话还没有结束，请稍后重试")
            elif session:
                saved = self.known_now(session, tool, name)
                if saved is None:
                    raise OpError("电脑上没有找到这个对话，请刷新后重试")
                if saved["live"] or any(t.session == session and not t.closed for t in self.terminals.values()):
                    raise OpError("这个对话正在被使用，请先结束使用它的程序")
            elif handed is not None and op.get("form") == "chat":
                candidate = self.handovers.previous_session(handed["tool"], handed["id"], tool)
                if (candidate and session_id_ok(tool, candidate)
                        and not any(t.tool == tool and (t.session or "").lower() == candidate.lower() and not t.closed for t in self.terminals.values())
                        and not self.sessions.open_outside(candidate, tool, self.terminals.values())):
                    try:
                        self.sessions.read_folder(candidate, tool, cwd)
                        session = candidate
                    except (OpError, OSError):
                        pass
            fresh = tool in ("claude", "opencode", "qoder", "pi") and not session and not history
            if fresh:
                # named by us, so the terminal knows its conversation at once — in the
                # shape the tool itself names them, or it refuses the id and exits
                session = "ses_" + secrets.token_hex(13) if tool == "opencode" else str(uuid.uuid4())
            if op.get("form") == "chat":
                # a conversation run through its tool's structured mode; the first thing to say is said at once
                if tool not in CHAT_TOOLS or history:
                    raise OpError("对话选项无效")
                resume = not fresh and bool(session)     # (a new one of Claude Code's is named above, as for a terminal)
                first_text = str(op.get("text") or "").strip()
                handed_title, handed_msgs, handed_more = "", [], False
                if handed is not None:
                    handed_title = self.handovers.title(handed["tool"], handed["id"]) or handed["title"]
                    handed_msgs, handed_more = self.sessions.line_messages(handed["id"], handed["tool"], name, dirs, next_tool=tool)
                    delta, between = self.sessions.delta_messages(handed["id"], handed["tool"], tool, session, name, dirs) if resume else ([], [])
                    if delta:
                        first_text = resume_handover(handed, between, delta) + "\n\n" + HANDOVER_ASK + "\n" + first_text
                    else:
                        read = self.sessions.read(handed["id"], handed["tool"], handed["dir"], dirs)
                        first_text = handover(handed, read.get("messages", []), "") + "\n\n" + HANDOVER_ASK + "\n" + first_text
                chat = open_chat(terminal, name, cwd, tool, launcher, self.wake, session, resume,
                                 str(cfg.get("RemoteMaxMode") or ""),
                                 mcp=self.peers.offer(tool, cwd, terminal))
                chat.quotas_note = self.quotas.note
                self.hear(chat)
                if handed_runs is not None:
                    handed_runs.kill()
                    self.terminals.pop(handed_runs.id, None)
                if handed is not None:
                    for stale in [k for k, v in self.terminals.items()
                                  if k != terminal and v.closed and v.tool == handed["tool"]
                                  and (v.session or "").lower() == handed["id"].lower()]:
                        self.terminals.pop(stale, None)
                if resume:
                    for stale in [k for k, v in self.terminals.items()
                                  if k != terminal and v.closed and v.tool == tool
                                  and (v.session or "").lower() == session.lower()]:
                        self.terminals.pop(stale, None)
                self.terminals[terminal] = chat
                if handed is not None:
                    if any(m.get("role") != "switch" for m in handed_msgs):
                        chat.before(handed_msgs, handed_more)
                    if chat.session:
                        self.handovers.add(chat.tool, chat.session, handed["tool"], handed["id"], handed_title)
                        self.scan_wake.set()
                    else:
                        chat.handed_from = (handed, handed_title)
                elif resume:
                    earlier_msgs, earlier_more = self.sessions.line_messages(session, tool, name, dirs)
                    if any(m.get("role") != "switch" for m in earlier_msgs):
                        chat.before(earlier_msgs, earlier_more)
                if first_text:
                    chat.say(first_text, op_id)
                return
            argv = tool_argv(tool, launcher, session, history, str(cfg.get("RemoteMaxMode") or ""), fresh)
        self.terminals[terminal] = Live(terminal, name, cwd, 80, 24, argv, self.wake, tool, session)

    # ---- loops
    def scan_loop(self):
        """Looks up the conversations on its own thread, every few seconds and at once when
        asked: the report loop keeps no lock while the tools' stores are read, so a key press,
        a file read or another operation never waits behind it."""
        while not self.stop.is_set():
            if self.scan_wake.wait(3):
                self.scan_wake.clear()
            if not self.stop.is_set():
                self.scan_once()

    def scan_once(self):
        try:
            dirs, terminals = self.scan_want
            if dirs:
                self.sessions.scan(dirs, terminals)
        except Exception as exc:            # a store that cannot be read is never fatal
            say("对话列表读取失败：%s" % type(exc).__name__)
        finally:
            # waiters ask whether a look has FINISHED, not whether the list changed: a look
            # that found nothing new is still the answer, and one with no projects still is one
            with self.scanned:
                self.scan_done += 1
                self.scanned.notify_all()

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
                    self.lost_token()
                elif status == 200:
                    waiting = True
                    ops = operations(body)
                    if ops:
                        brought = True
                        cfg = read_config(self.data)
                        with self.work:
                            dirs = self.projects.all(cfg)
                            for op in ops:
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
        threading.Thread(target=self.scan_loop, daemon=True).start()
        while not self.stop.is_set():
            try:
                self.report()
                self.delegations.sweep()
            except Exception as exc:            # network and login failures are retried
                say("报告失败：%s" % type(exc).__name__)
            pause = 0.02 if self.soon else 0.25 if time.time() - self.last_activity < 20 else 0.7
            self.soon = False
            if self.wakeup.wait(pause):
                self.wakeup.clear()
                if time.time() - self.last_input < 0.15:
                    time.sleep(0.012)
        with self.work:                     # a held pull may still be carrying out an operation
            closing = list(self.terminals.values())
            self.terminals.clear()
        for live in closing:
            if not live.closed:
                live.kill()
            live.dispose()
        self.uploads.dispose()
        self.peers.dispose_all()
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
    if argv and argv[0] == "--mcp":
        if len(argv) < 3:
            return 2
        return peers_mod.bridge(argv[1], argv[2])
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
