"""Relay for persistent terminals on the owner's computer. The HTTP layer in server.py does the authentication.

The computer makes outbound HTTPS requests. Operation IDs and output sequence numbers
make retrying a dropped response safe; input is never silently executed twice.
"""
import collections
import copy
import json
import os
import re
import threading
import time
import uuid

import screen as _screen


class RemoteError(ValueError):
    """A request the relay refuses; its text is shown to the person at the phone."""


def _folder(path):
    return os.path.splitext(path)[0] + "-output"


def _write(path, text, mode="w"):
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | (os.O_APPEND if mode == "a" else os.O_TRUNC), 0o600), mode, encoding="utf-8", newline="\n") as stream:
        stream.write(text)


def _replace(tmp, path):
    for attempt in range(6):
        try:
            return os.replace(tmp, path)
        except PermissionError:     # Windows: a virus scanner or the indexer is reading the file at this moment
            if attempt == 5:
                raise
            time.sleep(0.01)


def _lines(chunks):
    return "".join(json.dumps(c, ensure_ascii=False) + "\n" for c in chunks)


SAVE_SECONDS = 2


class _Operations(collections.OrderedDict):
    """Operations by their id, oldest first, kept a while so that a repeated request gets the same answer. The few
    the computer has not carried out yet are kept beside them: a phone that scrolls sends dozens a second, and
    going through all of them at every request made the relay slower the longer it was used."""

    def __init__(self):
        super().__init__()
        self.queued = {}

    def clear(self):
        super().clear()
        self.queued.clear()


_id = re.compile(r"^[a-f0-9]{16,32}$")
_terminal = re.compile(r"^[a-f0-9]{32}$")
_session = re.compile(r"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$")
_session_re = {"opencode": re.compile(r"^ses_[A-Za-z0-9]{10,40}$"),
               "cline": re.compile(r"^[0-9]{10,16}_[A-Za-z0-9]{4,10}$")}


def session_id_ok(tool, session):
    """A conversation id in this tool's own shape; claude, codex and qoder name a UUID."""
    return bool(_session_re.get(tool, _session).fullmatch(session))
_word = re.compile(r"^[a-z0-9_]{1,40}$")
PROJECT_ACTIONS = ("project_add", "project_remove", "project_rename")
DESKTOP_ACTIONS = ("app_open",)
# the tools whose desktop application a computer can offer to open: it says so for each by "<tool>-desktop-open"
DESKTOP_TOOLS = ("codex", "claude")
UPDATE_ACTION = "update"      # the program on the computer looks for a newer version of itself and installs it
MAX_TERMINALS, MAX_HISTORY, OUTPUT_LIMIT, CLOSED_LIMIT = 8, 12, 2_000_000, 300_000
LABELS = {"claude": "Claude Code", "codex": "Codex", "opencode": "OpenCode", "qoder": "Qoder", "cline": "Cline", "shell": "PowerShell"}
OP_SECONDS = 120


def _name(value, limit=40):
    return isinstance(value, str) and 0 < len(value.strip()) <= limit and not any(ord(c) < 32 for c in value)


_stamp = re.compile(r"[0-9.]{1,48}")         # how the file of a conversation was when it was read, in the words of the computer

def _words(text, most=8000):
    """What a person wrote, as it may be handed on: without carriage returns, not empty, not too long, and with no
    control characters but line breaks and tabs. None where it is not that."""
    text = text.replace("\r\n", "\n").replace("\r", "\n").strip() if isinstance(text, str) else ""
    if not text or len(text) > most or any(ord(c) < 32 and c not in "\n\t" for c in text):
        return None
    return text


def _folders(items, limit):
    """Folders as the computer reports them; the relay only shows them and hands the chosen one back."""
    out = []
    for item in items[:limit] if isinstance(items, list) else []:
        if isinstance(item, dict) and _name(item.get("name"), 60) and _name(item.get("path"), 260):
            out.append({"name": item["name"], "path": item["path"], "fixed": item.get("fixed") is True, "exists": item.get("exists") is not False,
                        "updated": item["updated"] if type(item.get("updated")) is int else 0, "live": item.get("live") is True,
                        "tools": [t for t in str(item.get("tools", "")).split() if t in ("claude", "codex", "opencode", "qoder", "cline")],
                        # by what the folder is used on the computer, in the computer's words: a desktop app, an editor, a terminal
                        "kinds": [k[:20] for k in item.get("kinds", []) if isinstance(k, str) and k.strip()][:4] if isinstance(item.get("kinds"), list) else []})
    return out


# A program says once that it draws on the alternate screen, reads the mouse or takes pasted text in brackets, and
# the output that said so is dropped when the history is full. These switches are therefore kept beside the output,
# in the order they were last changed, and are written again in front of a history that no longer begins at the
# terminal's start: a phone that opens the page later has to scroll and paste the way the program expects.
_SWITCHES = frozenset((1, 7, 9, 25, 47, 1000, 1002, 1003, 1004, 1005, 1006, 1007, 1015, 1016, 1047, 1049, 2004))
_ALTERNATE = ("47", "1047", "1049")
_SWITCH = re.compile(r"\x1b\[\?([0-9;]{1,40})([hl])|\x1bc")
_SWITCH_CUT = re.compile(r"\x1b(?:\[(?:\?[0-9;]{0,40})?)?\Z")
# A program that keeps the whole screen to itself has no history behind it: the last of its output is all that a
# reader who starts from nothing needs, and megabytes of earlier pictures only keep the page loading.
TAIL = 120_000


def _switch(modes, data, rest=""):
    """Follows the switches in a piece of output. Returns the beginning of a sequence the piece ends in the middle of."""
    data = rest + data
    cut = _SWITCH_CUT.search(data)
    for found in _SWITCH.finditer(data, 0, cut.start() if cut else len(data)):
        if not found.group(2):              # a full reset of the terminal
            modes.clear()
            continue
        for number in found.group(1).split(";"):
            if number.isdigit() and int(number) in _SWITCHES:
                modes.pop(str(int(number)), None)
                modes[str(int(number))] = found.group(2) == "h"
    return data[cut.start():] if cut else ""


def _preamble(modes):
    return "".join("\x1b[?%s%s" % (number, "h" if on else "l") for number, on in modes.items())


def _brackets(term):
    """Whether the program takes pasted text in brackets now, by what it last printed about it."""
    modes, rest = dict(term.get("modes") or {}), term.get("modes_rest", "")
    for chunk in term.get("output", []):
        rest = _switch(modes, chunk["data"], rest)
    return modes.get("2004") is True


def _resume(term, chunks):
    """Where a reader that has nothing yet begins, and what has to be said before that piece."""
    modes = dict(term.get("modes") or {})
    whole = 0, _preamble(modes)
    if term.get("state") != "running" or term.get("tool") == "shell" or term.get("form") == "chat":
        return whole            # a shell's history is read back, and an ended terminal is short already
    at, size = len(chunks), 0
    while at > 0 and size + len(chunks[at - 1]["data"]) <= TAIL:
        at -= 1
        size += len(chunks[at]["data"])
    for _ in range(20):         # not from the middle of a sequence
        if at <= 0 or not _screen._PARTIAL.search(chunks[at - 1]["data"]):
            break
        at -= 1
    if at <= 0:
        return whole
    rest = ""
    for chunk in chunks[:at]:
        rest = _switch(modes, chunk["data"], rest)
    then = dict(modes)
    for chunk in chunks[at:]:
        rest = _switch(modes, chunk["data"], rest)
    if any(then.get(key) for key in _ALTERNATE) and any(modes.get(key) for key in _ALTERNATE):
        return at, _preamble(then)
    return whole


def _trim(term):
    """A running screen keeps its recent history; an ended one keeps only its final screens."""
    limit = OUTPUT_LIMIT if term.get("state") in ("starting", "running") else CLOSED_LIMIT
    output = term.get("output", [])
    if "size" not in term:
        term["size"] = sum(len(c["data"]) for c in output)
    # The size is kept as pieces come and go: counting every piece again for each new one took longer the more there were.
    size, drop = term["size"], 0
    while size > limit and drop < len(output) - 1:
        size -= len(output[drop]["data"])
        term["modes_rest"] = _switch(term.setdefault("modes", {}), output[drop]["data"], term.get("modes_rest", ""))
        drop += 1
    del output[:drop]
    term["size"] = size
    return drop > 0


def _single(state):
    """A conversation is listed once among the terminals. A terminal that runs it replaces the ended ones that
    showed it before; of several ended ones the last that printed anything stays. Returns whether any went."""
    best = {}
    for term in state["threads"].values():
        if term.get("session") and term["state"] != "starting":
            rank = (term["state"] == "running", term.get("seq", 0) > 0, term["created"])
            key = (term["tool"], term["session"])
            if key not in best or rank > best[key][0]:
                best[key] = (rank, term["id"])
    gone = [term["id"] for term in state["threads"].values()
            if term.get("session") and term["state"] == "closed" and best[(term["tool"], term["session"])][1] != term["id"]]
    for key in gone:
        del state["threads"][key]
    return bool(gone)


def _room(state):
    """Ended terminals beyond the number kept go, oldest first."""
    while len(state["threads"]) > MAX_HISTORY:
        closed = [t for t in state["threads"].values() if t["state"] not in ("starting", "running")]
        if not closed:
            break
        del state["threads"][min(closed, key=lambda t: t["created"])["id"]]


# What a terminal is doing, for the lists on the phone:
#   starting  not running yet            busy     the program is working
#   confirm   it asks a yes/no question   idle     it waits for the next message ("done": it worked before)
#   ended     it has ended                failed   it ended with an error code
_ESCAPES = re.compile(r"\x1b\[[0-9;?<=>]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]|[\x00-\x08\x0b-\x1f]")
# Questions the tools draw when they need a decision, compared without spaces and in lower case.
_ASKS = ("doyouwantto", "wouldyouliketo", "1.yes", "allowcommand", "yes,proceed", "approve?", "[y/n]", "(y/n)", "[y]yes", "[y]是", "presstoconfirm", "enter确认")
QUIET_SECONDS, ACTIVE_SECONDS = 1.5, 4
# Output that is only the screen being drawn again is not work: what a program prints before it has been given
# anything, the redraw after the phone changed the size of the screen, and the echo of keys without Enter.
REDRAW_SECONDS, ECHO_SECONDS = 3, 1.2


def _asks(term):
    text, size = [], 0
    for chunk in reversed(term.get("output", [])):
        text.append(chunk["data"])
        size += len(chunk["data"])
        if size >= 6000:
            break
    tail = "".join(_ESCAPES.sub("", "".join(reversed(text))).split()).lower()[-900:]
    return any(ask in tail for ask in _ASKS)


def _phase(term, now):
    if term["state"] == "starting":
        return "starting"
    if term["state"] != "running":
        return "failed" if term.get("exit_code") not in (None, 0) else "ended"
    if term.get("form") == "chat":
        # a conversation run through its tool's structured mode says itself how it stands
        return term.get("status") if term.get("status") in ("starting", "busy", "confirm") else "idle"
    quiet = now - term.get("out_at", 0)
    # Claude Code says itself whether it is working; for the others, fresh output means work.
    working = term.get("status") == "busy" if term.get("status") else quiet < ACTIVE_SECONDS
    if quiet >= QUIET_SECONDS and (working or not term.get("status")) and _asks(term):
        return "confirm"
    return "busy" if working else "idle"


def _track(term, now):
    """Keeps the phase of a terminal up to date; returns whether it changed."""
    phase = _phase(term, now)
    if phase == term.get("phase"):
        return False
    if phase == "idle":
        term["done"] = term.get("phase") in ("busy", "confirm")      # it finished something, as opposed to never having started
    term["phase"], term["phase_at"] = phase, int(now * 1000)
    return True


def _shell(value):
    """The name the computer gives its plain terminal ("PowerShell", "bash"), for titles and buttons."""
    value = "".join(c for c in value if c.isprintable()).strip() if isinstance(value, str) else ""
    return value[:24]


def _session_activity(session):
    """Classify a saved conversation without treating a writer lock as a window.

    The agent can prove an independent CLI owner (``host=cli``), but a shared
    app-server, another remote terminal, or an unknown lock has no visible
    computer window that this app can locate.  Keep those records available for
    history/fork actions while keeping them out of the active count.
    """
    if not session.get("live"):
        return "history"
    # A program holds it and it is not in use now (a desktop app keeps every conversation it has loaded): it is
    # kept apart from what is going on. A computer that does not say takes every held conversation for one in use.
    if session.get("now") is False:
        return "locked"
    # A conversation whose tool tells of it itself is seen at work, whatever program runs it.
    return "active" if session.get("host") == "cli" or session.get("reach") is not None else "locked"


def _public_session(session, terminal=""):
    shown = dict(session)
    shown["activity"] = _session_activity(session)
    shown["terminal"] = terminal
    return shown


FILE_ACTIONS = ("file_list", "file_read")
FILE_SECONDS = 25
WAKE_SECONDS = 85       # a message that is handed to a conversation where it sits idle takes a short run of its tool on the computer
# What the program on the computer is asked about itself: a few of its settings, and the line it is reached by.
COMPUTER_ACTIONS = ("settings_read", "settings_change", "line_switch", "line_take", "line_keep")
# What the phone asks about its owner's work on the computer (the computer needs the feature "work"): the numbers of
# it, the questions set to be asked at a time, and one conversation handed over to another.
WORK_ACTIONS = ("stats", "schedules", "schedule_set", "schedule_remove", "handoff", "chains", "chain_set", "chain_remove")
# What the phone can do to a conversation that a program on the computer has open, where its tool lets it be reached.
REACH = ("say", "answer", "stop")
# What is done to a terminal that runs a conversation through its tool's structured mode (form "chat"; docs/PROTOCOL.md,
# "Chats"): what is said, a stop, an answer to what it asks, ending it. It has no screen to type into or size.
CHAT_TOOLS = ("claude", "codex")
CHAT_ACTIONS = ("say", "stop", "answer", "close", "rename")
CHAT_DECISIONS = re.compile(r"^(allow|always|deny|choice:[0-9]{1,2})$")
# By which way the computer does each thing to a conversation that a program there has open (docs/PROTOCOL.md):
# its application's own channel, its tool's hooks, its tool's own way to one that sits idle; ending closes its program.
WAYS = {"say": ("codex-desktop", "hook", "wake"), "stop": ("codex-desktop", "hook"), "answer": ("hook",), "end": ("process",)}
# What the owner has said of a conversation, kept by the computer: it stands first in the lists, or is left out of them.
MARKS = ("pinned", "hidden")
ANSWERED = FILE_ACTIONS + ("session_read", "session_say", "session_withdraw", "session_release", "session_stop", "session_answer", "session_mark", "session_delete", "session_end") + COMPUTER_ACTIONS + DESKTOP_ACTIONS + tuple("work_" + name for name in WORK_ACTIONS)     # operations whose acknowledgment carries an answer
_ask = re.compile(r"^[a-f0-9]{8,32}$")


def _queued(session):
    """What the phone said to a conversation and it has not taken yet, as the computer keeps it: each message with
    its number, the beginning of its words, and whether it waits or is held since the conversation was told to stop."""
    listed = session.get("queued")
    return [{"id": item["id"], "text": item["text"][:200], "state": item["state"], "at": item["at"] if type(item.get("at")) in (int, float) else 0}
            for item in (listed if isinstance(listed, list) else [])[:20]
            if isinstance(item, dict) and isinstance(item.get("id"), str) and _id.fullmatch(item["id"]) and isinstance(item.get("text"), str) and item.get("state") in ("queued", "paused", "sending")]


def _reached(session):
    """What the computer says of a conversation its tool tells of: what the phone can do, the request it waits on,
    and what it said last. Nothing of it where the tool tells nothing."""
    # the way of each thing, and for what cannot be done the computer's words why: None from a computer that says neither
    ways, why = session.get("ways"), session.get("why")
    told = isinstance(ways, dict) and session.get("live") is True
    ways = {thing: ways[thing] if ways.get(thing) in allowed else "" for thing, allowed in WAYS.items()} if told else None
    why = {thing: text[:200] for thing, text in why.items() if thing in WAYS and isinstance(text, str) and text} if told and isinstance(why, dict) else {} if told else None
    if not isinstance(session.get("reach"), list):
        return {"reach": None, "ask": None, "last": "", "wake": False, "ways": ways and dict(ways, say="", stop="", answer=""), "why": why, "queued": _queued(session)}
    ask = session.get("ask")
    options = ask.get("options") if isinstance(ask, dict) else None
    ask = {"id": ask["id"], "text": ask["text"][:600]} if (session.get("status") == "confirm" and isinstance(ask, dict) and isinstance(ask.get("id"), str)
                                                              and _ask.fullmatch(ask["id"]) and isinstance(ask.get("text"), str)) else None
    if ask is not None and isinstance(options, list):
        ask["options"] = [{"id": o["id"], "label": o["label"][:40], "detail": o.get("detail", "")[:600]}
                          for o in options[:4] if isinstance(o, dict) and o.get("id") in ("once", "always")
                          and isinstance(o.get("label"), str) and isinstance(o.get("detail", ""), str)]
    # wake: it sits idle, and a message is handed to it where it is (that takes some seconds), instead of being left for its next stop
    native = session.get("tool") == "codex" and session.get("transport") == "codex-desktop" and session.get("live") is True
    # told to stop through its tool's hooks, before its next step (Claude Code): no turn is named for that
    hook = session.get("transport") == "hook" and session.get("live") is True
    turn = session.get("turn") if isinstance(session.get("turn"), str) and re.fullmatch(r"[a-zA-Z0-9_-]{1,128}", session["turn"]) else ""
    reach = [way for way in REACH if way in session["reach"] and (way != "answer" or ask) and (way != "stop" or (native and turn) or hook)]
    return {"reach": reach, "ask": ask, "last": str(session.get("last") or "")[:300],
            "transport": "codex-desktop" if native else "hook" if hook else "", "turn": turn if native else "",
            "wake": session.get("wake") is True and "say" in session["reach"],
            # (a way is said only for what the list above lets through)
            "ways": ways and {thing: way if thing not in REACH or thing in reach else "" for thing, way in ways.items()}, "why": why, "queued": _queued(session)}


def _placed(session):
    """Where a conversation belongs: a project by its name, or, for one that is open in a folder that is no project
    (in a program on the computer, or in a terminal that took it on from there), no project and that folder. None
    for anything else."""
    if not isinstance(session.get("dir"), str):
        return None
    if session["dir"]:
        return {"dir": session["dir"], "folder": ""}
    return {"dir": "", "folder": session["folder"]} if _name(session.get("folder"), 260) else None


class Relay:
    """The terminals of one computer, as a relay knows them: what the computer last reported, the operations on their
    way to it, and each terminal's recent output. A relay for one computer has one of these; a relay for everyone has
    one for each computer's space, and nothing of one is known to another.

    The list of terminals is one small file, `path`. The output of each terminal is a file of its own in the folder
    beside it, one piece per line, and saving adds only the new pieces: with megabytes of history kept, writing all
    of it again every few seconds held up every key and every line for as long as the writing took."""

    def __init__(self, path):
        self.path = path
        self._lock = threading.Lock()
        self._changed = threading.Condition(self._lock)   # new output, a new state or a new operation: waiting requests look again
        self._tick = [0]            # counts every wake-up, so a waiter can tell that one happened between its look and its wait
        self._kept = None           # the list of terminals, once it has been read
        self._unsaved = None        # the time of the oldest output not yet written to disk
        self._logged = {}           # terminal -> [seq of the last piece in its file, characters in the file]
        self._pending = _Operations()
        self._device = {"seen": 0, "instance": "", "enabled": False, "workspaces": [], "tools": [], "projects": [], "candidates": []}
        self._sessions = []         # conversations saved on the computer, as its last report listed them
        self._screens = {}          # terminal -> (instance, Screen): rebuilt from the kept output when it is missing

    def _load(self):
        try:
            with open(self.path, encoding="utf-8") as stream:
                state = json.load(stream)
            if not isinstance(state, dict) or not isinstance(state.get("threads"), dict):
                raise ValueError
        except (OSError, ValueError):
            return {"threads": {}}
        for key, term in state["threads"].items():
            self._logged.pop(key, None)
            if "output" not in term:        # a list from before this arrangement carries the output itself
                chunks, whole = [], True
                try:
                    with open(os.path.join(_folder(self.path), key + ".jsonl"), encoding="utf-8", newline="\n") as stream:
                        for line in stream:
                            try:
                                chunk = json.loads(line)
                                if type(chunk["seq"]) is not int or not isinstance(chunk["data"], str) or (chunks and chunk["seq"] <= chunks[-1]["seq"]):
                                    raise ValueError
                            except (ValueError, KeyError, TypeError):
                                whole = False       # a line cut off by a crash: what came before it is kept
                                break
                            chunks.append({"seq": chunk["seq"], "data": chunk["data"]})
                except OSError:
                    whole = False
                term["output"] = chunks
                if chunks:
                    term["seq"] = max(term.get("seq", 0), chunks[-1]["seq"])
                if whole:
                    self._logged[key] = [chunks[-1]["seq"] if chunks else 0, sum(len(c["data"]) for c in chunks)]
            term["size"] = sum(len(c["data"]) for c in term["output"])
        return state

    def _save(self, state):
        folder = _folder(self.path)
        os.makedirs(folder, exist_ok=True)
        for key, term in state["threads"].items():
            output, kept, file = term.get("output", []), self._logged.get(key), os.path.join(folder, key + ".jsonl")
            last = output[-1]["seq"] if output else 0
            # Added to while the file continues where the kept output goes on; written again when it does not, and when
            # the file has grown to twice what is still kept.
            if kept and kept[0] <= last and (not output or output[0]["seq"] <= kept[0] + 1) and kept[1] <= 2 * term.get("size", 0) + 100_000:
                start = len(output)
                while start > 0 and output[start - 1]["seq"] > kept[0]:
                    start -= 1
                if start < len(output):
                    _write(file, _lines(output[start:]), "a")
                    kept[0], kept[1] = last, kept[1] + sum(len(c["data"]) for c in output[start:])
            elif not kept or kept != [last, term.get("size", 0)]:
                _write(file + ".tmp", _lines(output))
                _replace(file + ".tmp", file)
                self._logged[key] = [last, sum(len(c["data"]) for c in output)]
        _write(self.path + ".tmp", json.dumps({"threads": {key: {k: v for k, v in term.items() if k != "output"} for key, term in state["threads"].items()}}, ensure_ascii=False))
        _replace(self.path + ".tmp", self.path)
        for name in os.listdir(folder):
            if name.endswith(".jsonl") and name[:-6] not in state["threads"]:
                self._logged.pop(name[:-6], None)
                try:
                    os.remove(os.path.join(folder, name))
                except OSError:
                    pass

    def _state(self):
        if self._kept is None:
            state = self._load()
            changed = False
            # Starts awaiting delivery are in memory. Never replay them after a server restart.
            for term in state["threads"].values():
                if term.get("state") == "starting" and not any(
                        op["terminal"] == term["id"]
                        for op in self._pending.queued.values()):
                    term.update(state="closed", error="中转服务重启前的启动未完成，请重新打开终端")
                    changed = True
                changed = _trim(term) or changed
            changed = _single(state) or changed
            self._kept = state
            if changed:
                self._save(state)
        return self._kept

    def _view(self, now):
        return {**self._device, "online": now - self._device["seen"] < 15}

    def _adopt(self, state, item, instance):
        """A terminal the computer has and this relay does not know: the computer was using another relay when it was
        started, or this relay lost its list. The computer says what it is, and it is listed here as it was there. An
        ended one is taken only where it would be kept: not beside a terminal of the same conversation, and not when
        the list is full of newer ones."""
        key, tool, folder, created, session = item.get("id"), item.get("tool"), item.get("dir"), item.get("created"), item.get("session")
        # a terminal belongs to a project, or runs in a folder that is none and says which
        apart = item.get("folder") if folder == "" and _name(item.get("folder"), 260) else ""
        if not isinstance(key, str) or not _terminal.fullmatch(key) or tool not in LABELS or not (_name(folder, 60) or apart) \
                or type(created) is not int or created <= 0 or item.get("state") not in ("running", "closed"):
            return None
        session = session if isinstance(session, str) and _session.fullmatch(session) else ""
        threads = state["threads"]
        if item["state"] == "closed":
            if session and any(t["tool"] == tool and t.get("session") == session for t in threads.values()):
                return None
            closed = [t["created"] for t in threads.values() if t["state"] == "closed"]
            if len(threads) >= MAX_HISTORY and not any(at < created for at in closed):
                return None
        named = _name(item.get("title"), 60)
        if item.get("form") == "chat" and tool not in CHAT_TOOLS:
            return None
        term = {"id": key, "tool": tool, "dir": folder, "folder": apart, "title": item["title"].strip() if named else self._device.get("shell") or LABELS[tool] if tool == "shell" else LABELS[tool],
                "session": session, "status": "", "created": created, "state": item["state"], "error": "", "seq": 0, "output": [], "size": 0, "cols": 80, "rows": 24,
                "instance": instance, "previous": "", "history": False, "touched": True}
        if named:
            term["renamed"] = True
        if item.get("form") == "chat":
            term["form"] = "chat"
        threads[key] = term
        _room(state)
        return threads.get(key)

    def _said(self, term):
        """One line of what a terminal's program last said or did, for the lists. The screen is brought up to date only
        when someone looks, so a terminal nobody watches costs nothing."""
        if term.get("form") == "chat":
            return term.get("said", "")
        output = term.get("output") or []
        if not output:
            return ""
        kept = self._screens.get(term["id"])
        if not kept or kept[0] != term.get("instance") or kept[1].seq > term["seq"] or kept[1].seq < output[0]["seq"] - 1:
            kept = self._screens[term["id"]] = (term.get("instance"), _screen.Screen(term.get("cols", 80), term.get("rows", 24)))
            size, start = 0, len(output)
            while start > 0 and size < 200_000:         # the last screens are enough to draw the present one
                start -= 1
                size += len(output[start]["data"])
            kept[1].seq = output[start]["seq"] - 1
        view = kept[1]
        if view.seq < term["seq"]:
            start, size = len(output), 0
            while start > 0 and output[start - 1]["seq"] > view.seq and size < 200_000:
                start -= 1
                size += len(output[start]["data"])
            if start > 0 and output[start - 1]["seq"] > view.seq:      # nobody looked for a long time: again from the last screens
                view = _screen.Screen(term.get("cols", 80), term.get("rows", 24))
                self._screens[term["id"]] = (term.get("instance"), view)
        view.resize(term.get("cols", 80), term.get("rows", 24))
        if view.seq < term["seq"]:
            for chunk in output[start:]:
                view.feed(chunk["data"])
            view.seq = term["seq"]
            view.text = view.said()
        return view.text

    def _question(self, term):
        """The question a terminal's program is waiting on, as the last lines of its screen: enough to answer it from
        the list without opening the terminal, and never answered without having been shown."""
        if term.get("form") == "chat":
            return [line[:160] for line in term.get("asking", "").split("\n") if line.strip()][:14]
        self._said(term)                         # brings the kept screen up to date
        kept = self._screens.get(term["id"])
        if not kept:
            return []
        lines = [line[:160] for line in kept[1].lines()]
        while lines and not lines[-1].strip():
            lines.pop()
        lines = lines[-14:]
        while lines and not lines[0].strip():
            lines.pop(0)
        return lines

    def _public(self, term):
        shown = {k: v for k, v in term.items() if k not in ("output", "instance", "size", "out_at", "touched", "calm_until", "modes", "modes_rest", "asking")}
        # Until the owner names a terminal, it carries the name of the conversation it has open.
        if not term.get("renamed"):
            shown["title"] = next((s["title"] for s in self._sessions if s["id"] == term.get("session")), term["title"])
        return shown

    def _expire(self, now):
        for op in [o for o in self._pending.queued.values() if now - o["at"] > OP_SECONDS]:
            op.update(state="error", error="桌面操作结果未知，请先检查电脑上的应用" if op["payload"]["action"] in DESKTOP_ACTIONS else "电脑未及时接收，操作没有执行，请重试")
            del self._pending.queued[op["id"]]
            term = self._kept["threads"].get(op["terminal"]) if self._kept else None
            if term:
                term["error"] = op["error"]
                if op["payload"]["action"] == "start":
                    term["state"] = "closed"
                    _trim(term)
                self._save(self._kept)
        while self._pending and now - next(iter(self._pending.values()))["at"] > 600:
            self._pending.queued.pop(self._pending.popitem(last=False)[0], None)

    def overview(self, terminal="", after=0, now=None, wait=0):
        """The list, or one terminal's output after `after`. With `wait`, an answer that has nothing new is held back
        until there is output or the terminal changed, at most that many seconds."""
        deadline = time.monotonic() + min(max(wait, 0), 25)
        first = None
        while True:
            result = self._overview(terminal, after, time.time() if now is None else now)
            if not terminal:
                return result
            if deadline <= time.monotonic():
                result.pop("tick", None)
                return result
            mark = json.dumps([result["terminal"], result["device"]["online"], result["device"]["enabled"]], sort_keys=True)
            first = mark if first is None else first
            remaining = deadline - time.monotonic()
            tick = result.pop("tick")
            if result["chunks"] or result["reset"] or mark != first or remaining <= 0:
                return result
            self._wait(tick, remaining)

    def _wait(self, seen, seconds):
        """Sleeps until the next wake-up, unless one already happened after the caller looked."""
        with self._changed:
            if self._tick[0] == seen:
                self._changed.wait(min(seconds, 5))

    def _wake(self):
        self._tick[0] += 1
        self._changed.notify_all()

    def stream(self, terminal, after=0, seconds=55, beat=15):
        """Yields a terminal's output as it arrives, for about `seconds`; the reader then asks again from where it is.
        A line without output goes out when the terminal's state changes, and every `beat` seconds so that the reader
        and anything between it and the relay can tell the response is alive."""
        deadline = time.monotonic() + seconds
        sent, last = None, time.monotonic()
        while True:
            result = self._overview(terminal, after, time.time())
            tick = result.pop("tick")
            mark = json.dumps([result["terminal"], result["device"]["online"], result["device"]["enabled"]], sort_keys=True)
            now = time.monotonic()
            if result["chunks"] or result["reset"] or mark != sent or now - last >= beat:
                sent, last, after = mark, now, result["after"]
                yield result
                if result["chunks"] and after < result["terminal"]["seq"]:
                    continue        # more is waiting: no pause between the pieces
            if now >= deadline:
                return
            self._wait(tick, min(deadline - now, beat - (now - last)) + 0.01)

    def _overview(self, terminal, after, now):
        with self._lock:
            self._expire(now)
            state = self._state()
            if not terminal:
                attached = {t.get("session"): t["id"] for t in state["threads"].values() if t["state"] in ("starting", "running")}
                for gone in [key for key in self._screens if key not in state["threads"]]:
                    del self._screens[gone]
                return {"device": self._view(now), "terminals": sorted(
                    [dict(self._public(t), said=self._said(t) if t["state"] == "running" else "",
                          asks=self._question(t) if t["state"] == "running" and t.get("phase") == "confirm" else [])
                     for t in state["threads"].values()], key=lambda t: -t["created"]),
                    "sessions": [_public_session(s, attached.get(s["id"], "")) for s in self._sessions]}
            term = state["threads"].get(terminal)
            if not term:
                raise RemoteError("终端已不存在")
            chunks = term.get("output", [])
            reset = bool(chunks and (after < chunks[0]["seq"] - 1 or after > term.get("seq", 0)))
            start, lead = len(chunks), ""
            if chunks and (reset or after == 0):
                # A reader that has nothing: it may be given the last of the output only, and is told so.
                start, lead = _resume(term, chunks)
                reset = reset or start > 0
            else:
                # A reader is nearly always at the end: the new pieces are found from there, not by going through all of them.
                while start > 0 and chunks[start - 1]["seq"] > after:
                    start -= 1
            # Bound each mobile reply, without skipping the remaining output.
            out, length = [], 0
            for at in range(start, len(chunks)):
                out.append(dict(chunks[at]))
                length += len(chunks[at]["data"])
                if length >= 180_000:
                    break
            if lead and out:
                out[0]["data"] = lead + out[0]["data"]
            # A terminal's page needs to know only whether the computer is there; the lists of folders stay with the list.
            return {"device": {"online": now - self._device["seen"] < 15, "enabled": self._device["enabled"], "shell": self._device.get("shell", "")}, "terminal": self._public(term), "chunks": out, "reset": reset,
                    "after": out[-1]["seq"] if out else term.get("seq", 0), "tick": self._tick[0]}

    def command(self, payload, now=None):
        now = time.time() if now is None else now
        if not isinstance(payload, dict):
            raise RemoteError("请求格式无效")
        op_id, action = payload.get("id"), payload.get("action")
        if not isinstance(op_id, str) or not _id.fullmatch(op_id):
            raise RemoteError("操作编号无效")
        with self._lock:
            self._expire(now)
            old = self._pending.get(op_id)
            if old:
                if old.get("asked", old["payload"]) != payload:
                    raise RemoteError("操作编号已被其它操作使用")
                return {k: old[k] for k in ("id", "terminal", "state", "error", "result") if k in old}
            state = self._state()
            if not self._view(now)["online"] or not self._device["enabled"]:
                raise RemoteError("电脑未连接或远控已关闭，请等待电脑上线后重试")
            terminal, asked = payload.get("terminal", ""), None
            if action == UPDATE_ACTION:
                terminal = ""
                if "update" not in self._device.get("features", []):
                    raise RemoteError("电脑端版本不支持从手机更新，请先在电脑上更新一次")
            elif action in DESKTOP_ACTIONS:
                terminal = ""
                if payload.get("tool") not in DESKTOP_TOOLS or payload["tool"] + "-desktop-open" not in self._device.get("features", []):
                    raise RemoteError("这台电脑暂不支持此桌面应用入口")
                if payload.get("dir") not in self._device["workspaces"]:
                    raise RemoteError("电脑没有报告这个项目")
                draft = payload.get("draft", "")
                if not isinstance(draft, str) or len(draft.encode("utf-16-le", errors="surrogatepass")) > 2000 or any(ord(c) < 32 and c not in "\n\r\t" for c in draft):
                    raise RemoteError("草稿需在 1000 字以内，且不能含控制字符")
                if set(payload) - {"id", "action", "tool", "dir", "draft"}:
                    raise RemoteError("桌面操作参数无效")
            elif action in PROJECT_ACTIONS:
                # The computer keeps the list of project folders and checks the folder itself; this only refuses malformed requests.
                terminal = ""
                if action == "project_add":
                    if not _name(payload.get("path"), 240) or payload.get("create", False) not in (True, False) or not (payload.get("name", "") == "" or _name(payload.get("name"))):
                        raise RemoteError("文件夹路径或名称无效")
                else:
                    project = next((x for x in self._device["projects"] if x["name"] == payload.get("name")), None)
                    if not project:
                        raise RemoteError("没有这个项目")
                    # one that stands in the computer's own settings is changed there, unless the computer says that
                    # it changes those from the phone too (`projects`)
                    if project["fixed"] and "projects" not in self._device.get("features", []):
                        raise RemoteError("这个项目写在电脑的配置文件里，请在电脑上修改")
                    if action == "project_rename" and not _name(payload.get("to")):
                        raise RemoteError("名称需为 1 至 40 字")
            elif action == "start":
                tool, folder = payload.get("tool"), payload.get("dir")
                # A terminal is started in a project. Only one that goes on with a conversation that is open in a
                # folder that is no project runs there instead (`dir` is then empty): which folder is what the
                # computer said of that conversation, never what the viewer says.
                apart, where, called = folder == "", "", ""
                if tool not in self._device["tools"] or not (apart or folder in self._device["workspaces"]):
                    raise RemoteError("电脑没有报告这个工具或目录")
                previous = payload.get("previous", "")
                if previous and (not isinstance(previous, str) or not _id.fullmatch(previous)):
                    raise RemoteError("历史对话编号无效")
                if any(type(payload.get(k, False)) is not bool for k in ("history", "takeover", "fork")):
                    raise RemoteError("历史选项无效")
                chat = payload.get("form", "") == "chat"
                if payload.get("form", "") not in ("", "chat") or "text" in payload and not chat:
                    raise RemoteError("终端选项无效")
                if chat:
                    if tool not in CHAT_TOOLS or "chat" not in self._device.get("features", []):
                        raise RemoteError("电脑端版本不支持这种对话，请更新电脑端")
                    if payload.get("history") or payload.get("fork"):
                        raise RemoteError("对话选项无效")
                    if "text" in payload and _words(payload["text"]) is None:
                        raise RemoteError("内容为空、超过 8000 字或含有控制字符")
                session = payload.get("session", "")
                if payload.get("history") and not session and not previous:
                    raise RemoteError("请在项目中选择具体的历史对话；新建终端不会打开 resume 选择器")
                saved = None
                fork = payload.get("fork", False)
                if fork and (tool != "codex" or not session or payload.get("takeover") or "codex-fork" not in self._device.get("features", [])):
                    raise RemoteError("电脑端不支持这个副本选项，请更新电脑后台")
                if session:
                    saved = next((s for s in self._sessions if s["id"] == session and s["tool"] == tool and s["dir"] == folder), None)                     if isinstance(session, str) and session_id_ok(tool, session) else None
                    if not saved:
                        raise RemoteError("电脑上没有找到这个对话，请刷新后重试")
                    if saved.get("live") and not fork and not payload.get("takeover"):
                        raise RemoteError("原对话仍被占用，请选择接管原对话，先结束原终端")
                    if not fork and any(t.get("session") == session and t["state"] in ("starting", "running") for t in state["threads"].values()):
                        raise RemoteError("这个对话已经在手机终端里打开")
                    where, called = saved["folder"], saved["title"]
                elif apart:
                    # going on from a terminal that ran in such a folder: the folder is that terminal's
                    earlier = state["threads"].get(previous) if previous else None
                    where = earlier.get("folder", "") if earlier and earlier["tool"] == tool and earlier["dir"] == "" else ""
                    called = self._public(earlier)["title"] if where else ""
                if apart and not where:
                    raise RemoteError("电脑没有报告这个工具或目录")
                if apart:
                    asked, payload = payload, dict(payload, folder=where)       # what the viewer sent is kept, to know the same request again
                if sum(t["state"] in ("starting", "running") for t in state["threads"].values()) >= MAX_TERMINALS:
                    raise RemoteError("最多同时运行 8 个终端，请先结束一个")
                terminal = uuid.uuid4().hex
                if chat and not called and payload.get("text"):
                    # a new conversation is called by what it was first told, until its tool names it
                    called = " ".join(_words(payload["text"]).split())[:40]
                term = {"id": terminal, "tool": tool, "dir": folder, "folder": where,
                        # one outside every project is called what its conversation is: the computer lists that conversation only while it is open
                        "title": (called[:60] if (apart or chat) and called else self._device.get("shell") or LABELS[tool] if tool == "shell" else LABELS[tool]) + (" · 副本" if fork else ""), "session": "" if fork else session, "status": "",
                        "created": int(now * 1000), "state": "starting", "error": "", "seq": 0, "output": [], "size": 0, "cols": 80, "rows": 24,
                        "instance": self._device["instance"], "previous": previous, "history": bool(payload.get("history")), "touched": False}
                if chat:
                    term.update(form="chat", touched=True)
                state["threads"][terminal] = term
                _room(state)
            else:
                term = state["threads"].get(terminal) if isinstance(terminal, str) else None
                if not term:
                    raise RemoteError("终端已不存在")
                if action == "rename":
                    title = payload.get("title")
                    if not isinstance(title, str) or not title.strip() or len(title) > 60 or any(ord(c) < 32 for c in title):
                        raise RemoteError("名称需为 1 至 60 字")
                    term.update(title=title.strip(), renamed=True)
                    self._save(state)
                    return {"id": op_id, "terminal": terminal, "state": "done", "error": ""}
                if term["state"] != "running":
                    raise RemoteError("终端已结束或尚未启动")
                if term.get("form") == "chat":
                    if action not in CHAT_ACTIONS:
                        raise RemoteError("这段对话不支持此操作")
                    if action == "say":
                        text = _words(payload.get("text"))
                        if text is None:
                            raise RemoteError("内容为空、超过 8000 字或含有控制字符")
                        asked, payload = payload, {"id": op_id, "action": "say", "terminal": terminal, "text": text}
                    elif action == "stop" and term.get("phase") not in ("busy", "confirm"):
                        raise RemoteError("它现在没有在执行")
                    elif action == "answer":
                        if not isinstance(payload.get("ask"), str) or not _ask.fullmatch(payload["ask"]) or not isinstance(payload.get("decision"), str) or not CHAT_DECISIONS.fullmatch(payload["decision"]):
                            raise RemoteError("回答无效")
                        payload = {"id": op_id, "action": "answer", "terminal": terminal, "ask": payload["ask"], "decision": payload["decision"]}
                elif action == "say":
                    # A message, as typed into the box of the terminal's page and sent: the text, then Enter. It comes
                    # from where the terminal is not on the screen, so the relay puts it the way the program takes
                    # pasted text, which it knows from what the program printed; the computer is given plain input.
                    text = _words(payload.get("text"))
                    if text is None:
                        raise RemoteError("内容为空、超过 8000 字或含有控制字符")
                    # Enter answers "yes" to a question the program waits on: that is answered where the question is read.
                    if term.get("phase") == "confirm":
                        raise RemoteError("这个终端正在等待确认，请先回答它的问题")
                    data = ("\x1b[200~%s\x1b[201~" % text if _brackets(term) else " ".join(text.split("\n"))) + "\r"
                    asked, payload, action = payload, {"id": op_id, "action": "input", "terminal": terminal, "data": data}, "input"
                if action == "input":
                    data = payload.get("data")
                    if not isinstance(data, str) or not data or len(data) > 16000:
                        raise RemoteError("输入为空或超过 16000 字")
                    term["touched"] = True
                    if "\r" not in data and "\n" not in data:
                        term["calm_until"] = max(term.get("calm_until", 0), now + ECHO_SECONDS)
                elif action == "resize":
                    if any(type(payload.get(k)) is not int for k in ("cols", "rows")) or not (20 <= payload["cols"] <= 240 and 6 <= payload["rows"] <= 100):
                        raise RemoteError("终端尺寸无效")
                    term["calm_until"] = max(term.get("calm_until", 0), now + REDRAW_SECONDS)
                elif action not in ("close", "say", "stop", "answer") or term.get("form") != "chat" and action != "close":
                    raise RemoteError("不支持此操作")
            op = {"id": op_id, "terminal": terminal, "state": "queued", "error": "", "at": now, "payload": copy.deepcopy(payload)}
            if asked is not None:
                op["asked"] = asked         # what the viewer sent, to know the same request again
            self._pending[op_id] = self._pending.queued[op_id] = op
            self._wake()
            if action == "start":
                self._save(state)
            return {k: op[k] for k in ("id", "terminal", "state", "error")}

    def files(self, payload, now=None, wait=FILE_SECONDS):
        """What a project folder holds, or a piece of one of its files. The computer reads it; the relay passes the
        question on and holds the request until the answer is there. Nothing of it is written anywhere, and the answer
        is handed over once."""
        now = time.time() if now is None else now
        if not isinstance(payload, dict):
            raise RemoteError("请求格式无效")
        op_id, action, folder, where = payload.get("id"), payload.get("action"), payload.get("dir"), payload.get("path", "")
        if not isinstance(op_id, str) or not _id.fullmatch(op_id):
            raise RemoteError("操作编号无效")
        if action not in FILE_ACTIONS:
            raise RemoteError("不支持此操作")
        if not isinstance(where, str) or len(where) > 1000 or any(ord(c) < 32 for c in where):
            raise RemoteError("路径无效")
        if type(payload.get("offset", 0)) is not int or payload.get("offset", 0) < 0:
            raise RemoteError("位置无效")
        with self._changed:
            self._expire(now)
            if op_id in self._pending:
                raise RemoteError("操作编号已被其它操作使用")
            if not self._view(now)["online"] or not self._device["enabled"]:
                raise RemoteError("电脑未连接或远控已关闭，请等待电脑上线后重试")
            if "files" not in self._device.get("features", []):
                raise RemoteError("电脑端版本不支持查看文件，请更新电脑端")
            if folder not in self._device["workspaces"]:
                raise RemoteError("没有这个项目")
            return self._held({"id": op_id, "action": action, "dir": folder, "path": where, "offset": payload.get("offset", 0)}, now, wait)

    def _held(self, question, now, wait):
        """Passes a question on to the computer and waits for its answer. Called with self._changed held."""
        op_id = question["id"]
        op = {"id": op_id, "terminal": "", "state": "queued", "error": "", "at": now, "payload": question}
        self._pending[op_id] = self._pending.queued[op_id] = op
        self._wake()
        deadline = time.monotonic() + wait
        while op["state"] == "queued":
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            self._changed.wait(min(remaining, 5))
        self._pending.queued.pop(op_id, None)
        self._pending.pop(op_id, None)           # asked once, answered once: nothing of the answer stays here
        if op["state"] == "queued":
            op["state"] = "error"
            raise RemoteError("电脑没有及时回应，请重试")
        if op["error"]:
            raise RemoteError(op["error"])
        return op.get("result") or {}

    def conversation(self, payload, now=None, wait=FILE_SECONDS):
        """A conversation saved on the computer. Without an action: the last things said in it, so that it can be
        looked at before anything is done to the program that has it open; the computer reads it, and nothing of it
        is written here. With one ("say", "answer"): a message for it, or the answer to what it asks, where a program
        has it open and its tool lets it be reached. The program is not taken from the computer by either."""
        now = time.time() if now is None else now
        if not isinstance(payload, dict):
            raise RemoteError("请求格式无效")
        op_id, session, action = payload.get("id"), payload.get("session"), payload.get("action", "read")
        if not isinstance(op_id, str) or not _id.fullmatch(op_id):
            raise RemoteError("操作编号无效")
        if not isinstance(session, str) or not any(session_id_ok(tool, session) for tool in ("claude", "opencode", "cline")):
            raise RemoteError("对话编号无效")
        more = {}
        if action == "say":
            if not isinstance(payload.get("text"), str) or not 0 < len(payload["text"].strip()) <= 8000:
                raise RemoteError("消息需为 1 至 8000 字")
            more = {"text": payload["text"]}
        elif action == "stop":
            # the application's channel stops the one turn that is named; a tool's hooks stop what it is at
            if payload.get("transport") == "codex-desktop":
                if not isinstance(payload.get("turn"), str) or not re.fullmatch(r"[a-zA-Z0-9_-]{1,128}", payload["turn"]):
                    raise RemoteError("执行轮次无效，请刷新后重试")
                more = {"turn": payload["turn"]}
        elif action == "answer":
            if not isinstance(payload.get("ask"), str) or not _ask.fullmatch(payload["ask"]) or type(payload.get("allow")) is not bool:
                raise RemoteError("回答无效")
            more = {"ask": payload["ask"], "allow": payload["allow"]}
            if "scope" in payload:
                if payload["scope"] not in ("once", "always") or (payload["scope"] == "always" and not payload["allow"]):
                    raise RemoteError("授权范围无效")
                more["scope"] = payload["scope"]
        elif action == "mark":
            more = {mark: payload[mark] for mark in MARKS if mark in payload}
            if not more or any(type(value) is not bool for value in more.values()):
                raise RemoteError("标记无效")
        elif action == "withdraw":
            # one message that still waits for the conversation, by its number, is taken back
            if not isinstance(payload.get("message"), str) or not _id.fullmatch(payload["message"]):
                raise RemoteError("消息编号无效")
            more = {"message": payload["message"]}
        elif action in ("delete", "end", "release"):
            pass
        elif action == "read":
            # a page that follows a conversation says what it has: the computer answers {"same": true} to that
            if isinstance(payload.get("since"), str) and _stamp.fullmatch(payload["since"]):
                more = {"since": payload["since"]}
        else:
            raise RemoteError("不支持此操作")
        native = payload.get("transport") == "codex-desktop" and action in ("say", "stop")
        if "receipt" in payload and type(payload["receipt"]) is not bool:
            raise RemoteError("回执查询无效")
        if action == "stop" and not native and payload.get("transport") != "hook":
            raise RemoteError("此对话没有可用的停止接口")
        if native:
            more.update(transport="codex-desktop", receipt=payload.get("receipt") is True)
        with self._changed:
            self._expire(now)
            if op_id in self._pending:
                raise RemoteError("操作编号已被其它操作使用")
            if not self._view(now)["online"] or not self._device["enabled"]:
                raise RemoteError("电脑未连接或远控已关闭，请等待电脑上线后重试")
            needed, without = {"read": ("peek", "电脑端版本不支持查看对话内容，请更新电脑端"), "mark": ("marks", "电脑端版本不支持置顶和隐藏对话，请更新电脑端"),
                               "delete": ("delete", "电脑端版本不支持删除对话，请更新电脑端"), "end": ("end", "电脑端版本不支持在手机上结束电脑上的对话，请更新电脑端"),
                               "withdraw": ("outbox", "电脑端版本不支持撤回消息，请更新电脑端"), "release": ("outbox", "电脑端版本不支持继续发送暂停的消息，请更新电脑端")}.get(
                action, ("reach", "电脑端版本不支持在手机上处理电脑上的对话，请更新电脑端"))
            if needed not in self._device.get("features", []):
                raise RemoteError(without)
            saved = next((s for s in self._sessions if s["id"] == session), None)
            if not saved:
                raise RemoteError("电脑上没有找到这个对话，请刷新后重试")
            if native:
                if saved["tool"] != "codex" or "codex-desktop-control" not in self._device.get("features", []):
                    raise RemoteError("电脑端没有可用的 Codex 原应用控制接口")
                if not more["receipt"] and saved.get("transport") != "codex-desktop":
                    raise RemoteError("原应用连接已变化，请刷新后重试")
            if action == "delete":
                # A saved conversation is put away by the computer (nothing is destroyed: its tool's archive, or the
                # recycle bin). Not one a program has open, nor one a terminal here runs. It leaves the list at once.
                if saved.get("live") or not saved["dir"] or any(t.get("session") == session and t["state"] in ("starting", "running") for t in self._state()["threads"].values()):
                    raise RemoteError("这段对话正开着，请先结束它再删除")
                answer = self._held({"id": op_id, "action": "session_delete", "session": session, "tool": saved["tool"], "dir": saved["dir"]}, now, wait)
                self._sessions[:] = [s for s in self._sessions if s["id"] != session]
                return answer
            if action == "end":
                # The computer ends the program that has the conversation there: it stops at once and is open no more.
                # Only one that a program has open; the list says so with the computer's next report.
                if not saved.get("live"):
                    raise RemoteError("这段对话没有开着")
                return self._held({"id": op_id, "action": "session_end", "session": session, "tool": saved["tool"], "dir": saved["dir"]}, now, wait)
            if action in ("withdraw", "release"):
                # The computer keeps what waits, and decides: only what it listed for this conversation is asked for.
                waiting = saved.get("queued") or []
                if action == "withdraw" and not any(item["id"] == more["message"] for item in waiting):
                    raise RemoteError("这条消息已经交给对话，或已撤回")
                if action == "release" and not any(item["state"] == "paused" for item in waiting):
                    raise RemoteError("没有暂停的消息")
                answer = self._held({"id": op_id, "action": "session_" + action, "session": session, "tool": saved["tool"], "dir": saved["dir"], **more}, now, wait)
                # (the list here says so at once, and again with the computer's next report)
                saved["queued"] = [dict(item, state="queued") if action == "release" else item for item in waiting if action == "release" or item["id"] != more["message"]]
                return answer
            if action == "mark":
                # the computer keeps it; the list here says so at once, and again with the computer's next report
                answer = self._held({"id": op_id, "action": "session_mark", "session": session, "tool": saved["tool"], "dir": saved["dir"], **more}, now, wait)
                saved.update(more)
                return answer
            if action != "read":
                if not (native and more["receipt"]) and action not in (saved.get("reach") or []):
                    raise RemoteError("这个确认请求已经处理过了，或只能在电脑上回答" if action == "answer" else "这段对话现在没有在执行，请刷新后重试" if action == "stop" else "这段对话现在不能从手机发消息，请刷新后重试")
                if action == "stop" and not native and saved.get("transport") != "hook":
                    raise RemoteError("这段对话的停止方式已变化，请刷新后重试")
                if action == "answer" and saved["ask"]["id"] != more["ask"]:
                    raise RemoteError("这个确认请求已经处理过了")
                if action == "say" and saved.get("wake") and wait == FILE_SECONDS:
                    wait = WAKE_SECONDS
            return self._held({"id": op_id, "action": "session_" + action, "session": session, "tool": saved["tool"], "dir": saved["dir"], **more}, now, wait)

    def computer(self, payload, now=None, wait=FILE_SECONDS):
        """A question to the program on the computer about itself: its settings, and the line the phone reaches it
        by. The program answers and decides; the relay checks the shape of the question, passes it on, holds the
        request until the answer is there and keeps nothing of it."""
        now = time.time() if now is None else now
        if not isinstance(payload, dict):
            raise RemoteError("请求格式无效")
        op_id, action = payload.get("id"), payload.get("action")
        if not isinstance(op_id, str) or not _id.fullmatch(op_id):
            raise RemoteError("操作编号无效")
        if action not in COMPUTER_ACTIONS:
            raise RemoteError("不支持此操作")
        question = {"id": op_id, "action": action}
        if action == "settings_change":
            name, value = payload.get("name"), payload.get("value")
            if not isinstance(name, str) or not _word.fullmatch(name) or not (type(value) is bool or isinstance(value, str) and _word.fullmatch(value)):
                raise RemoteError("设置无效")
            question.update(name=name, value=value)
        elif action == "line_switch":
            if not isinstance(payload.get("to"), str) or not _word.fullmatch(payload["to"]):
                raise RemoteError("线路无效")
            question["to"] = payload["to"]
        elif action == "line_keep":
            if not isinstance(payload.get("key"), str) or not _id.fullmatch(payload["key"]):
                raise RemoteError("线路无效")
            question["key"] = payload["key"]
        with self._changed:
            self._expire(now)
            if op_id in self._pending:
                raise RemoteError("操作编号已被其它操作使用")
            if not self._view(now)["online"] or not self._device["enabled"]:
                raise RemoteError("电脑未连接或远控已关闭，请等待电脑上线后重试")
            if "settings" not in self._device.get("features", []):
                raise RemoteError("电脑端版本不支持在手机上设置，请更新电脑端")
            return self._held(question, now, wait)

    def work(self, payload, now=None, wait=FILE_SECONDS):
        """The owner's work on the computer: its numbers by day ("stats"), the questions set to be asked at a time
        ("schedules", "schedule_set", "schedule_remove"), and a conversation handed over to another ("handoff").
        The computer keeps and does all of it; the relay checks the question, passes it on and keeps nothing."""
        now = time.time() if now is None else now
        if not isinstance(payload, dict):
            raise RemoteError("请求格式无效")
        op_id, action = payload.get("id"), payload.get("action")
        if not isinstance(op_id, str) or not _id.fullmatch(op_id):
            raise RemoteError("操作编号无效")
        if action not in WORK_ACTIONS:
            raise RemoteError("不支持此操作")
        question = {"id": op_id, "action": "work_" + action}
        with self._changed:
            self._expire(now)
            if op_id in self._pending:
                raise RemoteError("操作编号已被其它操作使用")
            if not self._view(now)["online"] or not self._device["enabled"]:
                raise RemoteError("电脑未连接或远控已关闭，请等待电脑上线后重试")
            if "work" not in self._device.get("features", []):
                raise RemoteError("电脑端版本不支持这项功能，请更新电脑端")
            # (passing on when a conversation stops needs its tool's own word that it has: a computer says whether it has that)
            if action.startswith("chain") and "chains" not in self._device.get("features", []):
                raise RemoteError("这台电脑的电脑端不支持接力")
            listed = {s["id"]: s for s in self._sessions}
            # (a conversation in one of the phone's own terminals is open too: the computer types into that terminal)
            typed = {t.get("session") for t in self._state()["threads"].values() if t["state"] == "running" and t.get("session")}
            if action == "stats":
                days = payload.get("days", 14)
                if type(days) is not int or not 1 <= days <= 90:
                    raise RemoteError("天数无效")
                question["days"] = days
            elif action == "schedule_set":
                item = payload.get("item")
                if not isinstance(item, dict) or not isinstance(item.get("id"), str) or not _id.fullmatch(item["id"]):
                    raise RemoteError("定时提问无效")
                of = listed.get(item.get("session"))
                if not of:
                    raise RemoteError("电脑上没有找到这个对话，请刷新后重试")
                text, kind = item.get("text"), item.get("kind")
                if not isinstance(text, str) or not 0 < len(text.strip()) <= 2000:
                    raise RemoteError("要问的话需为 1 至 2000 字")
                if kind not in ("once", "daily", "every"):
                    raise RemoteError("重复方式无效")
                if kind == "once" and type(item.get("at")) not in (int, float) or kind == "every" and type(item.get("minutes")) is not int \
                        or kind == "daily" and not (isinstance(item.get("time"), str) and re.fullmatch(r"[0-2][0-9]:[0-5][0-9]", item["time"])):
                    raise RemoteError("时间无效")
                # (the tool is the one the computer listed the conversation with, whatever the phone says)
                question["item"] = {"id": item["id"], "session": of["id"], "tool": of["tool"], "text": text, "kind": kind, "on": item.get("on") is not False,
                                    "at": int(item["at"]) if kind == "once" else 0, "minutes": item["minutes"] if kind == "every" else 0, "time": item["time"] if kind == "daily" else ""}
            elif action == "schedule_remove":
                if not isinstance(payload.get("schedule"), str) or not _id.fullmatch(payload["schedule"]):
                    raise RemoteError("定时提问无效")
                question["schedule"] = payload["schedule"]
            elif action == "chain_set":
                # when the first has stopped, what it said last goes to the second, with these words
                item = payload.get("item")
                if not isinstance(item, dict) or not isinstance(item.get("id"), str) or not _id.fullmatch(item["id"]):
                    raise RemoteError("接力无效")
                first, then, text, rounds = listed.get(item.get("from")), listed.get(item.get("to")), item.get("text"), item.get("rounds")
                if not first or not then:
                    raise RemoteError("电脑上没有找到这个对话，请刷新后重试")
                if first["id"] == then["id"]:
                    raise RemoteError("不能接力给同一段对话")
                if not isinstance(text, str) or not 0 < len(text.strip()) <= 1000:
                    raise RemoteError("要说的话需为 1 至 1000 字")
                if type(rounds) is not int or not 1 <= rounds <= 10:
                    raise RemoteError("次数需在 1 到 10 之间")
                question["item"] = {"id": item["id"], "from": first["id"], "from_tool": first["tool"], "to": then["id"], "to_tool": then["tool"], "text": text, "rounds": rounds}
            elif action == "chain_remove":
                if not isinstance(payload.get("chain"), str) or not _id.fullmatch(payload["chain"]):
                    raise RemoteError("接力无效")
                question["chain"] = payload["chain"]
            elif action == "handoff":
                given, taking, note = listed.get(payload.get("from")), listed.get(payload.get("to")), payload.get("note", "")
                if not given or not taking:
                    raise RemoteError("电脑上没有找到这个对话，请刷新后重试")
                if given["id"] == taking["id"]:
                    raise RemoteError("不能交接给同一段对话")
                if not taking.get("live") and taking["id"] not in typed:
                    raise RemoteError("接手的对话没有在电脑上开着")
                if not isinstance(note, str) or len(note) > 1000:
                    raise RemoteError("补充说明需在 1000 字以内")
                question.update({"from": given["id"], "from_tool": given["tool"], "to": taking["id"], "to_tool": taking["tool"], "note": note})
                if wait == FILE_SECONDS:
                    wait = WAKE_SECONDS         # (the one that takes over may have to be woken first)
            return self._held(question, now, wait)

    def pull(self, payload, now=None):
        """Held open by the computer: answers with the operations it has not been given yet, as soon as there is one.
        They stay queued until the computer reports them done, so a lost answer is made up by its next report."""
        if not isinstance(payload, dict) or not isinstance(payload.get("instance"), str) or not _id.fullmatch(payload["instance"]):
            raise RemoteError("电脑实例编号无效")
        seconds = payload.get("wait", 12)
        deadline = time.monotonic() + (min(max(seconds, 0), 12) if type(seconds) in (int, float) else 12)
        with self._changed:
            while True:
                fresh = [op for op in self._pending.queued.values() if not op.get("sent")] if payload["instance"] == self._device["instance"] else []
                if fresh:
                    for op in fresh:
                        op["sent"] = True
                    return {"operations": [{**op["payload"], "terminal": op["terminal"], "at": op["at"]} for op in fresh]}
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return {"operations": []}
                self._changed.wait(min(remaining, 5))

    def agent(self, payload, now=None):
        now = time.time() if now is None else now
        if not isinstance(payload, dict) or not isinstance(payload.get("info"), dict):
            raise RemoteError("请求格式无效")
        info = payload["info"]
        instance = info.get("instance")
        if not isinstance(instance, str) or not _id.fullmatch(instance):
            raise RemoteError("电脑实例编号无效")
        with self._lock:
            self._expire(now)
            state = self._state()
            dirty = wrote = False
            if isinstance(payload.get("sessions"), list):
                self._sessions[:] = [
                    {"id": s["id"], "tool": s["tool"], **_placed(s), "title": s["title"].strip()[:80], "updated": s["updated"],
                     **{mark: s.get(mark) is True for mark in MARKS}, "now": s.get("live") is True and s.get("now") is not False,
                     "live": s.get("live") is True, "status": s.get("status") if s.get("status") in ("idle", "busy", "confirm", "waiting") else "", **_reached(s),
                     "host": s.get("host") if s.get("host") in ("cli", "shared", "remote", "unknown") else "",
                     "origin": str(s.get("origin") or "")[:60],
                     "completion": str(s.get("completion") or "")[:200],
                     # the operation ("app_open") it came of, where it began in a desktop application opened for it from the phone
                     "opened": s["opened"] if isinstance(s.get("opened"), str) and _id.fullmatch(s["opened"]) else "",
                     # by what it is used, in the computer's words: by the program that has it open, else by what started it
                     "kind": str(s.get("kind") or "")[:20],
                     "can_takeover": s.get("can_takeover") is True, "ownership_known": s.get("ownership_known") is True,
                     "takeover_reason": str(s.get("takeover_reason") or "")[:200]}
                    for s in payload["sessions"][:120]
                    if isinstance(s, dict) and isinstance(s.get("id"), str) and session_id_ok(s.get("tool"), s["id"]) and s.get("tool") in ("claude", "codex", "opencode", "qoder", "cline")
                    and _placed(s) and isinstance(s.get("title"), str) and s["title"].strip() and type(s.get("updated")) is int]
            self._device.update(seen=now, instance=instance, enabled=info.get("enabled") is True,
                           version=str(info.get("version") or "")[:20], newer=str(info.get("newer") or "")[:20], shell=_shell(info.get("shell")),
                           features=[x for x in info.get("features", []) if x in ("codex-fork", "codex-takeover", "terminal-exit", "files", "update", "peek", "settings", "reach", "marks", "delete", "projects", "end", "outbox", "work", "chains", "codex-desktop-control", "chat") + tuple(tool + "-desktop-open" for tool in DESKTOP_TOOLS)],
                           tools=[x for x in info.get("tools", []) if x in ("claude", "codex", "opencode", "qoder", "cline", "shell")],
                           workspaces=[x for x in info.get("workspaces", []) if isinstance(x, str) and 0 < len(x) <= 60],
                           projects=_folders(info.get("projects"), 60), candidates=_folders(info.get("candidates"), 40))
            for term in state["threads"].values():
                if term.get("instance") != instance and term["state"] in ("starting", "running"):
                    term.update(state="closed", error="电脑后台已重启，请从历史对话恢复")
                    _track(term, now)       # or it would keep the phase it had, and be listed as still working
                    _trim(term)
                    dirty = True
            for ack in payload.get("acks", [])[:200]:
                if not isinstance(ack, dict):
                    continue
                op = self._pending.get(ack.get("id"))
                if op and op["state"] == "queued":
                    if op["payload"]["action"] in ANSWERED:
                        op["result"] = ack.get("result") if isinstance(ack.get("result"), dict) else {}
                        wrote = True        # the request that waits for it looks again
                    op.update(state="error" if ack.get("error") else "done", error=str(ack.get("error", ""))[:300])
                    self._pending.queued.pop(op["id"], None)
                    term = state["threads"].get(op["terminal"])
                    if term and term.get("error") != op["error"]:       # nearly every key ends without one: nothing to write
                        term["error"] = op["error"]
                        dirty = True
                    if op["payload"]["action"] == "project_rename" and not op["error"]:
                        # Records made under the old name follow the project.
                        for record in state["threads"].values():
                            if record["dir"] == op["payload"]["name"]:
                                record["dir"] = op["payload"]["to"].strip()
                                dirty = True
                    if term and op["payload"]["action"] == "start" and op["error"]:
                        term.update(state="closed", error=op["error"])
                        _trim(term)
                        dirty = True
            reported = payload.get("terminals") if isinstance(payload.get("terminals"), list) else None
            if reported is not None:
                # The computer names every terminal it has. One that is listed here as running and is no longer among
                # them ended while the computer was using another relay, or without this relay being told.
                there = {item.get("id") for item in reported[:200] if isinstance(item, dict) and isinstance(item.get("id"), str)}
                for term in state["threads"].values():
                    if term.get("instance") == instance and term["state"] == "running" and term["id"] not in there:
                        term.update(state="closed", status="")
                        _trim(term)
                        dirty = True
            for item in (reported or [])[:MAX_TERMINALS + MAX_HISTORY]:
                if not isinstance(item, dict):
                    continue
                term = state["threads"].get(item.get("id")) if isinstance(item.get("id"), str) else None
                if term is None and isinstance(item.get("id"), str):
                    term = self._adopt(state, item, instance)
                    dirty = dirty or term is not None
                if term and term.get("instance") == instance:
                    status = item.get("state")
                    if status in ("running", "closed") and status != term["state"]:
                        term["state"] = status
                        _trim(term)
                        dirty = True
                    session = item.get("session")
                    if isinstance(session, str) and session_id_ok(term["tool"], session) and session != term.get("session"):
                        term["session"] = session
                        dirty = True
                    status = item.get("status") if item.get("status") in (("idle", "busy", "confirm", "starting") if term.get("form") == "chat" else ("idle", "busy")) and term["state"] == "running" else ""
                    wrote = wrote or status != term.get("status")
                    term["status"] = status
                    term["completion"] = str(item.get("completion") or "")[:200]
                    if term.get("form") == "chat":
                        # what it said last and what it asks, in the computer's words
                        said, asking = str(item.get("said") or "")[:200], str(item.get("ask") or "")[:2000]
                        wrote = wrote or said != term.get("said") or asking != term.get("asking")
                        term["said"], term["asking"] = said, asking
                    exit_code = item.get("exit_code")
                    if term["state"] == "closed" and type(exit_code) is int and term.get("exit_code") != exit_code:
                        term["exit_code"] = exit_code
                        if exit_code != 0:
                            term["error"] = "程序退出（代码 %d），请检查终端画面中的原因" % exit_code
                        dirty = True
                    for key in ("cols", "rows"):
                        if type(item.get(key)) is int:
                            term[key] = item[key]
            for chunk in payload.get("output", [])[:200]:
                if not isinstance(chunk, dict):
                    continue
                term = state["threads"].get(chunk.get("terminal"))
                seq, data = chunk.get("seq"), chunk.get("data")
                # A piece is taken once. A gap can only follow a relay restart that lost the last unsaved pieces; the
                # computer has discarded them by then, so later output is accepted rather than refused forever.
                if not term or term.get("instance") != instance or type(seq) is not int or seq <= term["seq"] or not isinstance(data, str) or len(data) > 30000:
                    continue
                if chunk.get("restart") is True:
                    # The computer gives its history again and it does not continue what is kept here: this piece begins
                    # with the terminal modes in force, and what was kept before it is dropped. Readers are told to start anew.
                    term["output"], term["size"] = [], 0
                    term["modes"], term["modes_rest"] = {}, ""
                term["output"].append({"seq": seq, "data": data})
                term["seq"] = seq
                # A terminal from before this rule has no "touched" and keeps the old behaviour. Output given again is not work.
                if term.get("touched", True) and now >= term.get("calm_until", 0) and chunk.get("old") is not True:
                    term["out_at"] = now
                term["size"] = term.get("size", 0) + len(data)
                _trim(term)
                wrote = True
                if self._unsaved is None:
                    self._unsaved = now
            for term in state["threads"].values():
                if term.get("instance") == instance and _track(term, now):
                    dirty = True
            if dirty:
                _single(state)
            # States are written at once. Output alone is written every few seconds: a busy screen reports many times a second.
            if dirty or (self._unsaved is not None and now - self._unsaved >= SAVE_SECONDS):
                self._save(state)
                self._unsaved = None
            if dirty or wrote:
                self._wake()
            for op in self._pending.queued.values():
                op["sent"] = True
            ops = [{**op["payload"], "terminal": op["terminal"], "at": op["at"]} for op in self._pending.queued.values()]
            mine = [t for t in state["threads"].values() if t.get("instance") == instance]
            # The names the owner gave: the computer keeps them, so that another relay it turns to can show them too.
            return {"operations": ops, "output_ack": {t["id"]: t["seq"] for t in mine}, "titles": {t["id"]: t["title"] for t in mine if t.get("renamed")}}
