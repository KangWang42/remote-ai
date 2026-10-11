"""Made-up homes of Claude Code and Codex for the checks and the pictures: conversations that are saved, and ones
that something holds open. An agent that is given these homes (CLAUDE_CONFIG_DIR, CODEX_HOME) reads nothing of the
tools on this computer, so no real conversation is ever listed by a check or drawn into a picture."""
import json
import msvcrt
import os
import re
import sys
import time
from pathlib import Path


class Homes:
    def __init__(self, folder):
        self.claude, self.codex = Path(folder) / "claude-home", Path(folder) / "codex-home"
        for made in (self.claude / "projects", self.claude / "sessions", self.codex / "sessions" / "2026" / "10" / "11", self.codex / "thread-writer-locks"):
            made.mkdir(parents=True, exist_ok=True)
        self.locks = []
        self.rollouts = {}

    def environment(self, **more):
        return dict(os.environ, PYTHONUTF8="1", CLAUDE_CONFIG_DIR=str(self.claude), CODEX_HOME=str(self.codex), **more)

    def claude_saved(self, folder, session, title, hours_ago, said=("帮我看看", "好的。")):
        """A conversation Claude Code saved for a folder, named, and last used that many hours ago."""
        saved = self.claude / "projects" / re.sub("[^a-zA-Z0-9]", "-", str(folder))
        saved.mkdir(parents=True, exist_ok=True)
        rows = [{"type": "user", "message": {"role": "user", "content": said[0]}}, {"type": "assistant", "message": {"role": "assistant", "content": [{"type": "text", "text": said[1]}]}},
                {"type": "ai-title", "aiTitle": title, "sessionId": session}]
        path = saved / (session + ".jsonl")
        path.write_text("".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows), encoding="utf-8")
        at = time.time() - hours_ago * 3600
        os.utime(path, (at, at))
        return path

    def codex_saved(self, folder, session, title, hours_ago, said=("帮我看看", "好的。"), held=False):
        """A conversation Codex saved for a folder; `held`: something has it open (its lock is held until close())."""
        path = self.codex / "sessions" / "2026" / "10" / "11" / ("rollout-2026-10-11T08-00-00-%s.jsonl" % session)
        rows = [{"type": "session_meta", "payload": {"id": session, "cwd": str(folder)}}] + [
            {"type": "response_item", "payload": {"type": "message", "role": role, "content": [{"type": "input_text" if role == "user" else "output_text", "text": text}]}}
            for role, text in zip(("user", "assistant"), said)]
        path.write_text("".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows), encoding="utf-8")
        with open(self.codex / "session_index.jsonl", "a", encoding="utf-8") as index:
            index.write(json.dumps({"id": session, "thread_name": title}, ensure_ascii=False) + "\n")
        at = time.time() - hours_ago * 3600
        os.utime(path, (at, at))
        if held:
            lock = open(self.codex / "thread-writer-locks" / (session + ".lock"), "w+b")
            lock.write(b"held"); lock.flush(); lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 4)
            self.locks.append(lock)
        self.rollouts[session] = path
        return path

    def stand_ins(self, folder):
        """Programs that take the place of the tools: what a check starts by mistake is never a real tool. Returns the
        folder to put first in PATH."""
        tools = Path(folder) / "tools"
        tools.mkdir(parents=True, exist_ok=True)
        for name in ("claude", "codex"):
            (tools / (name + ".cmd")).write_text('@"%s" -c "import time; print(\'STAND-IN %s\'); time.sleep(600)"\r\n' % (sys.executable, name), encoding="mbcs")
        return tools

    def close(self):
        for lock in self.locks:
            lock.close()
        self.locks = []
