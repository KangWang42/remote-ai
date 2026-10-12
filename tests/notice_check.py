"""While a hook keeps a tool for the phone, the computer's screen says so: the program itself, off the screen, with
made-up folders of the tools, and the hook started as Codex and Claude Code start it.

    python tests/notice_check.py [folder for the picture]
"""
import hashlib
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT / "agent-windows" / "bin"


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else None
    temp = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
    data, noted = Path(temp.name) / "data", Path(temp.name) / "noticed.txt"
    data.mkdir()
    with socket.socket() as reserved:
        reserved.bind(("127.0.0.1", 0))
        port = reserved.getsockname()[1]
    settings = {"Mode": "lan", "Port": port, "Reach": True, "UpdateCheckUtc": "2099-01-01T00:00:00.0000000Z"}
    (data / "config.json").write_text(json.dumps(settings), encoding="utf-8")
    pipe = "RemoteCli.reach." + hashlib.sha256(os.path.abspath(str(data)).lower().encode("utf-8")).hexdigest()[:12]
    # nobody has touched the computer for ten minutes
    environment = dict(os.environ, REMOTECLI_DATA=str(data), REMOTECLI_LAN="127.0.0.1", REMOTECLI_UNATTENDED="600", REMOTECLI_NOTICED=str(noted),
                       CLAUDE_CONFIG_DIR=str(Path(temp.name) / "claude-home"), CODEX_HOME=str(Path(temp.name) / "codex-home"), PYTHONUTF8="1")
    program = subprocess.Popen([str(BIN / "RemoteCli.exe"), "--offstage"], env=environment, cwd=str(BIN))
    hooks = []

    def hook(tool, name, session, **more):
        event = dict({"hook_event_name": name, "session_id": session, "transcript_path": "", "cwd": temp.name}, **more)
        running = subprocess.Popen([str(BIN / "RemoteCliAgent.exe"), "--hook", tool, pipe], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        running.stdin.write(json.dumps(event).encode("utf-8")); running.stdin.close()
        hooks.append(running)
        return running

    def said():
        try:
            return noted.read_text(encoding="utf-8").split("\n")
        except OSError:
            return None

    def until(test, what, seconds=20):
        deadline = time.time() + seconds
        while time.time() < deadline:
            if test():
                return
            time.sleep(0.2)
        raise AssertionError("never: %s; the notice: %r" % (what, said()))

    def change(**values):
        (data / "config.json").write_text(json.dumps(dict(json.loads((data / "config.json").read_text(encoding="utf-8")), **values)), encoding="utf-8")

    try:
        # the program listens for the hooks: one that is answered at once tells so
        for _ in range(60):
            if hook("codex", "SessionStart", str(uuid.uuid4())).wait(10) == 0 and said() is not None:
                break
            time.sleep(0.5)
        else:
            raise AssertionError("the program does not hear the hooks")
        assert said() == ["", ""], said()
        # a request of Codex's is kept for the phone: Codex shows a running hook, and the screen says what waits
        codex = str(uuid.uuid4())
        asking = hook("codex", "PermissionRequest", codex, tool_name="Bash", tool_input={"command": "git push"})
        until(lambda: said() == ["Codex 的确认请求已发到手机", "动一下鼠标或键盘，改在这台电脑上回答"], "the notice of a request that is kept")
        drawn = Path(str(noted) + ".png")       # written after the words
        until(lambda: drawn.exists() and drawn.stat().st_size > 400, "the notice drawn", 5)
        assert asking.poll() is None
        if out:
            out.mkdir(parents=True, exist_ok=True)
            (out / "notice.png").write_bytes(Path(str(noted) + ".png").read_bytes())
        # Codex lets the hook go (its own time for it is over, or it was closed): nothing is kept, nothing is said
        asking.kill()
        until(lambda: said() == ["", ""], "the notice to go with what it was about")
        # Claude Code's own question is on the screen while its hook runs: there is nothing to explain
        claude = str(uuid.uuid4())
        beside = hook("claude", "PermissionRequest", claude, tool_name="Bash", tool_input={"command": "npm test"})
        time.sleep(2.5)
        assert beside.poll() is None and said() == ["", ""], said()
        beside.kill()
        # its owner has it not kept: the hook ends at once, Codex shows its own question, and nothing is said
        change(ReachAsk=False)
        assert hook("codex", "PermissionRequest", codex, tool_name="Bash", tool_input={"command": "git push"}).wait(10) == 0
        time.sleep(1.5)
        assert said() == ["", ""], said()
        # a tool that stops and is kept waiting for the phone's message, where its owner chose a time
        change(ReachAsk=True, ReachWait=60)
        stopped = hook("claude", "Stop", claude, last_assistant_message="Done.")
        until(lambda: said() == ["Claude Code 做完了，在等手机的消息", "动一下鼠标或键盘即结束等待"], "the notice of a stop that waits")
        stopped.kill()
        until(lambda: said() == ["", ""], "the notice to go again")
        # and one that is not kept waiting says nothing
        change(ReachWait=0)
        assert hook("claude", "Stop", claude, last_assistant_message="Done again.").wait(10) == 0
        time.sleep(1.5)
        assert said() == ["", ""], said()
        print("the computer's screen says what a hook keeps a tool for: a request of Codex's, a stop that waits; nothing where nothing is kept")
    finally:
        for running in hooks:
            if running.poll() is None:
                running.kill()
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(program.pid)], capture_output=True)
        time.sleep(1)
        temp.cleanup()


if __name__ == "__main__":
    main()
