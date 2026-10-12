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
        self.holders = []
        self.rollouts = {}

    def environment(self, **more):
        return dict(os.environ, PYTHONUTF8="1", CLAUDE_CONFIG_DIR=str(self.claude), CODEX_HOME=str(self.codex), **more)

    def claude_saved(self, folder, session, title, hours_ago, said=("帮我看看", "好的。"), entry=None):
        """A conversation Claude Code saved for a folder, named, and last used that many hours ago. `entry`: what
        started it, as Claude Code notes it with the folder in every row ("cli", "claude-desktop-3p")."""
        saved = self.claude / "projects" / re.sub("[^a-zA-Z0-9]", "-", str(folder))
        saved.mkdir(parents=True, exist_ok=True)
        noted = {"cwd": str(folder), "entrypoint": entry} if entry else {}
        rows = [dict({"type": "user", "message": {"role": "user", "content": said[0]}}, **noted), {"type": "assistant", "message": {"role": "assistant", "content": [{"type": "text", "text": said[1]}]}},
                {"type": "ai-title", "aiTitle": title, "sessionId": session}]
        path = saved / (session + ".jsonl")
        path.write_text("".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows), encoding="utf-8")
        at = time.time() - hours_ago * 3600
        os.utime(path, (at, at))
        return path

    def codex_saved(self, folder, session, title, hours_ago, said=("帮我看看", "好的。"), held=False, originator=None, source=None, instructions=None):
        """A conversation Codex saved for a folder; `held`: something has it open (its lock is held until close());
        `originator` and `source`: what started it, as Codex notes it ("codex-tui" and "cli"; "Codex Desktop" and
        "vscode"); `instructions`: written before the folder, as an earlier Codex wrote them."""
        path = self.codex / "sessions" / "2026" / "10" / "11" / ("rollout-2026-10-11T08-00-00-%s.jsonl" % session)
        meta = dict({"id": session}, **({"instructions": instructions} if instructions else {}))
        meta["cwd"] = str(folder)
        meta.update({key: value for key, value in (("originator", originator), ("source", source)) if value})
        rows = [{"type": "session_meta", "payload": meta}] + [
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

    def claude_open(self, folder, session, socket="\\\\.\\pipe\\LOCAL\\cc-msg-made-up", entry="claude-desktop-3p", status="idle", version=None):
        """A conversation of Claude Code's that a program has open, as Claude Code notes it: the note names a process
        that runs (one is started here and ended by close()), and where the conversation takes messages from other
        sessions. The desktop app keeps such a process for every conversation it has loaded."""
        import subprocess
        import shutil
        holder = subprocess.Popen([shutil.which("node"), "-e", "setInterval(() => {}, 1e6)"], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.holders.append(holder)
        time.sleep(0.5)             # the note is written after the process began, as Claude Code writes it
        (self.claude / "sessions" / ("%d.json" % holder.pid)).write_text(json.dumps(
            dict({"pid": holder.pid, "sessionId": session, "cwd": str(folder), "status": status, "kind": "interactive", "entrypoint": entry, "messagingSocketPath": socket},
                 **({"version": version} if version else {}))), encoding="utf-8")
        return holder

    QUEUE = '''using System; using System.IO; using System.Text;
static class StandIn {
    static int Main(string[] given) {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (given.Length >= 2 && given[0] == "queue" && given[1] == "--help") { Console.WriteLine("Usage: codex queue [OPTIONS] --thread <THREAD> --message <TEXT>"); return 0; }
        if (given.Length == 5 && given[0] == "queue" && given[1] == "--thread" && given[3] == "--message") {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "queued.txt"), given[2] + "\\n" + given[4], new UTF8Encoding(false));
            Console.WriteLine("Queued message 0 for thread " + given[2] + ".");
            return 0;
        }
        if (given.Length == 2 && given[0] == "archive") {
            string home = Environment.GetEnvironmentVariable("CODEX_HOME"), kept = Path.Combine(home, "archived_sessions");
            Directory.CreateDirectory(kept);
            foreach (string file in Directory.GetFiles(Path.Combine(home, "sessions"), "rollout-*" + given[1] + ".jsonl", SearchOption.AllDirectories)) File.Move(file, Path.Combine(kept, Path.GetFileName(file)));
            Console.WriteLine("Archived session " + given[1] + ".");
            return 0;
        }
        return 2;
    }
}'''

    def stand_ins(self, folder, queue=False):
        """Programs that take the place of the tools: what a check starts, on purpose or by mistake, is never a real
        tool. A stand-in says the folder it was started in and what it was started with, and stays; asked for its help
        (the agent does that to learn what a tool can do) it answers and leaves. Returns the folder to put first in PATH."""
        tools = Path(folder) / "tools"
        tools.mkdir(parents=True, exist_ok=True)
        (tools / "standin.py").write_text(
            "import json, os, sys, time\n"
            "if '--help' in sys.argv: print('stand-in'); sys.exit(0)\n"
            # run for one prompt (`-p`), as a session that hands a message over is: what it was given is written beside it, and it says the message was sent
            "if '-p' in sys.argv:\n"
            "    given = sys.stdin.buffer.read().decode('utf-8')\n"
            "    open('stand-in-run.json', 'w', encoding='utf-8').write(json.dumps({'arguments': sys.argv[2:], 'given': given, 'settings': open(sys.argv[sys.argv.index('--settings') + 1], encoding='utf-8').read() if '--settings' in sys.argv else ''}, ensure_ascii=False))\n"
            "    print(json.dumps({'type': 'assistant', 'message': {'content': [{'type': 'tool_use', 'name': 'SendMessage', 'input': {}}]}}))\n"
            "    print(json.dumps({'type': 'user', 'message': {'content': [{'type': 'tool_result', 'content': json.dumps({'success': True})}]}}))\n"
            "    print(json.dumps({'type': 'result', 'result': 'SENT'})); sys.exit(0)\n"
            "print('STAND-IN %s in [%s] with [%s]' % (sys.argv[1], os.getcwd(), ' '.join(sys.argv[2:])), flush=True)\n"
            "time.sleep(600)\n", encoding="utf-8")
        for name in ("claude", "codex"):
            (tools / (name + ".cmd")).write_text('@"%s" "%s" %s %%*\r\n' % (sys.executable, tools / "standin.py", name), encoding="mbcs")
        if queue:
            # Codex as npm lays it out: the script above, and the program itself beside it. This one only knows the
            # command that queues a message for a conversation, and writes down what it was given (queued.txt).
            import subprocess
            inside = tools / "node_modules" / "@openai" / "codex" / "vendor"
            inside.mkdir(parents=True)
            (inside / "codex.cs").write_text(self.QUEUE, encoding="utf-8")
            subprocess.run([str(Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"), "/nologo", "/out:" + str(inside / "codex.exe"), str(inside / "codex.cs")], check=True, capture_output=True)
            self.queued = inside / "queued.txt"
        return tools

    def close(self):
        for lock in self.locks:
            lock.close()
        self.locks = []
        for holder in self.holders:
            holder.kill()
        self.holders = []
