"""How much of each tool's usage limits the owner's account has used, as the tools themselves
tell it (agent-windows/Quotas.cs). Only the share used of each window and when it starts anew
leaves the machine, never anything of the account.

The three tellers, and where they are heard here:
- Claude Code puts a `rate_limit_event` into its stream-json (`rate_limit_info` with
  `unifiedWindows`, each with `utilization` 0..1 and `resetsAt`): every conversation this
  agent runs in the structured chat mode hands it over (ClaudeChat.received).
- Codex answers `account/rateLimits/read` of its app-server (`primary` and `secondary`, each
  with `usedPercent`, `windowDurationMins`, `resetsAt`), and pushes `account/rateLimits/updated`
  on its own; the chat asks for it at most once in ten minutes.
- Antigravity CLI answers `agy -p /usage --output-format json` with model groups and quota
  buckets; asked at most once in ten minutes where the CLI is installed.

What a tool said is written to `quotas/<tool>.json` (0600) in the data folder; a report reads
it from there. A window whose time is over is left out — it has started anew, and how much of
it is used is not known.
"""
import json
import math
import os
import secrets
import subprocess
import threading
import time


def _at(given, name):
    return given.get(name) if isinstance(given, dict) else None


def _number(value):
    if isinstance(value, bool):
        return None
    try:
        number = float(value)
        return number if math.isfinite(number) else None
    except (TypeError, ValueError):
        return None


def _seconds(value):
    number = _number(value)
    if number is None or number <= 0:
        return 0
    return int(number if number < 10_000_000_000 else number / 1000.0)   # seconds, or milliseconds


def _clamp(used):
    return round(max(0.0, min(100.0, used)), 1)


def from_claude_event(info):
    """Claude Code's `rate_limit_info` of a `rate_limit_event` in its stream-json."""
    windows = []
    unified = _at(info or {}, "unifiedWindows")
    if isinstance(unified, dict):
        for name, one in unified.items():
            used = _number(_at(one, "utilization"))
            if used is None:
                continue
            windows.append({"name": str(name)[:32], "used": used * 100.0,
                            "resets": _seconds(_at(one, "resetsAt")), "group": ""})
    return windows


def from_claude_status(given):
    """What Claude Code gives its status line: `rate_limits`, each window with
    `used_percentage` and `resets_at`."""
    windows = []
    limits = _at(given or {}, "rate_limits")
    if isinstance(limits, dict):
        for name, one in limits.items():
            used = _number(_at(one, "used_percentage"))
            if used is None:
                continue
            windows.append({"name": str(name)[:32], "used": used,
                            "resets": _seconds(_at(one, "resets_at")), "group": ""})
    return windows


def from_codex(limits):
    """Codex's `rateLimits` (app-server): `primary` and `secondary`, named by their length."""
    windows = []
    for key in ("primary", "secondary"):
        one = _at(limits or {}, key)
        used = _number(_at(one, "usedPercent"))
        if used is None:
            continue
        minutes = _number(_at(one, "windowDurationMins")) or 0
        name = "five_hour" if minutes == 300 else "seven_day" if minutes == 10080 else \
            (str(int(minutes)) + "m" if minutes > 0 else key)
        windows.append({"name": name, "used": used, "resets": _seconds(_at(one, "resetsAt")), "group": ""})
    return windows


def _agy_window_kind(window, id_text):
    low = (window or "").lower()
    if low == "5h" or id_text.lower().endswith(("-5h", "_5h")):
        return "5h"
    if low == "weekly" or id_text.lower().endswith(("-weekly", "_weekly")):
        return "weekly"
    return ""


def _agy_group_label(name):
    if (name or "").strip().lower() == "claude and gpt models":
        return "Claude / GPT"
    clean = (name or "").strip()
    return "其他池子" if not clean else clean[:24].rstrip()


def from_agy(given):
    """Antigravity CLI's `agy -p /usage --output-format json`: model groups (`groups`) and their
    quota buckets. The primary Gemini/Neon pool is reported without a group label so the phone
    shows it directly; secondary model pools keep a short group label so the phone can fold
    them behind a toggle."""
    windows = []
    data = _at(_at(given or {}, "command"), "data") or given
    raw_groups = [one for one in (_at(data, "groups") or []) if isinstance(one, dict)]
    parsed = []
    for group in raw_groups:
        kept_buckets = []
        for bucket in (_at(group, "buckets") or []):
            if not isinstance(bucket, dict):
                continue
            remaining = _number(_at(bucket, "remaining_fraction"))
            if remaining is not None and 0 <= remaining <= 1:
                kept_buckets.append(bucket)
        if kept_buckets:
            parsed.append((str(_at(group, "name") or "").strip(), str(_at(group, "description") or ""), kept_buckets))
    if not parsed:
        return windows
    primary = next((index for index, one in enumerate(parsed) if "neon" in one[1].lower()), -1)
    if primary < 0:
        primary = next((index for index, one in enumerate(parsed) if one[0].lower() == "gemini models"), -1)
    if primary < 0:
        primary = next((index for index, one in enumerate(parsed) if any(
            str(_at(bucket, "id") or "").lower() in ("gemini-5h", "gemini-weekly")
            for bucket in one[2])), -1)
    if primary < 0:
        primary = 0

    order = [primary] + [index for index in range(len(parsed)) if index != primary]
    taken = set()
    for index in order:
        is_primary = index == primary
        group_label = "" if is_primary else _agy_group_label(parsed[index][0])
        buckets = sorted(enumerate(parsed[index][2]),
                         key=lambda pair: {"5h": 0, "weekly": 1}.get(
                             _agy_window_kind(str(_at(pair[1], "window") or ""),
                                              str(_at(pair[1], "id") or "")), 2))
        for _, bucket in buckets:
            id_text = str(_at(bucket, "id") or "")
            remaining = _number(_at(bucket, "remaining_fraction"))
            if remaining is None:
                continue
            kind = _agy_window_kind(str(_at(bucket, "window") or ""), id_text)
            if is_primary:
                name = "five_hour" if kind == "5h" else "seven_day" if kind == "weekly" else \
                    (id_text or "primary")[:32]
            else:
                name = (kind or id_text or "pool")[:32]
                while name in taken:
                    name += "_"
            taken.add(name)
            windows.append({"name": name, "group": group_label,
                            "used": (1.0 - max(0.0, min(1.0, remaining))) * 100.0,
                            "resets": _seconds(_at(bucket, "reset_time"))})
    return windows


class Quotas:
    """What each tool last said of its limits, kept per tool and read for the report."""

    def __init__(self, data_folder):
        self.folder = os.path.join(data_folder, "quotas")
        self.lock = threading.Lock()

    def _file_of(self, tool):
        return os.path.join(self.folder, tool + ".json")

    def note(self, tool, windows):
        """What a tool told of its limits now. Empty readings (such as Codex API-key updates
        with null limits) are ignored so they never overwrite existing readings."""
        if windows:
            self._write(tool, windows, "")

    def unknown(self, tool, why):
        """That a tool's limits cannot be known here, in words."""
        self._write(tool, [], why)

    def _write(self, tool, windows, why):
        kept = {"tool": tool, "at": int(time.time() * 1000), "why": why or "",
                "windows": [{"name": str(one["name"])[:32], "used": _clamp(one["used"]),
                             "resets": int(one["resets"]),
                             **({"group": one["group"]} if one.get("group") else {})}
                            for one in windows]}
        with self.lock:
            try:
                os.makedirs(self.folder, mode=0o700, exist_ok=True)
                fresh = self._file_of(tool) + "." + secrets.token_hex(4)
                fd = os.open(fresh, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
                with os.fdopen(fd, "w", encoding="utf-8") as stream:
                    json.dump(kept, stream, ensure_ascii=False)
                os.replace(fresh, self._file_of(tool))
            except OSError:
                pass

    def _kept(self, tool):
        try:
            with open(self._file_of(tool), encoding="utf-8") as stream:
                kept = json.load(stream)
            seconds = int(time.time())
            windows = [one for one in kept.get("windows", []) if isinstance(one, dict)
                       and isinstance(one.get("name"), str)
                       and (one.get("resets", 0) == 0 or one.get("resets", 0) > seconds)]
            return windows, kept.get("at"), kept.get("why", "")
        except (OSError, ValueError, TypeError):
            return None, None, None

    def report(self):
        """What is known, for the computer's report: one entry for each tool, as it was last told."""
        all_known = []
        for tool in ("claude", "codex", "agy"):
            windows, at, why = self._kept(tool)
            if windows is not None:
                all_known.append({"tool": tool, "at": at, "why": why, "windows": windows})
        return all_known

    def of(self, tool):
        """A tool's main windows that are still running (a secondary pool left out), for what the
        program itself weighs; none where nothing is known."""
        windows, _, _ = self._kept(tool)
        if windows is None:
            return []
        return [{"name": one["name"], "used": float(one.get("used", 0)),
                 "resets": int(one.get("resets", 0))} for one in windows if not one.get("group")]

    def ask_agy(self, launcher):
        """Runs the Antigravity CLI's usage command and keeps what it answers (at most once in
        ten minutes is the caller's job)."""
        try:
            env = dict(os.environ)
            env["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true"
            done = subprocess.run([launcher, "-p", "/usage", "--output-format", "json"],
                                  capture_output=True, timeout=30, env=env)
            if done.returncode == 0:
                windows = from_agy(json.loads(done.stdout.decode("utf-8", "replace")))
                if windows:
                    self.note("agy", windows)
                    return True
        except (OSError, ValueError, subprocess.TimeoutExpired):
            pass
        return False
