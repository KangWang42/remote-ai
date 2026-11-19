"""Windows agy session/arguments check and relay agy session check. Pass --live to open a new, isolated real CLI terminal."""
from pathlib import Path
import subprocess
import sys
import tempfile
import uuid

from agent_build import ROOT, compile_check

sys.path.insert(0, str(ROOT / "relay"))
import relay  # noqa: E402

# The CLI can keep its workspace open briefly after its terminal exits.
workspace = ROOT / ".build/agy-check/live-workspace"
workspace.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="remote-cli-agy-") as folder:
    exe = Path(folder) / "check.exe"
    compile_check("AntigravityCheck", exe, [Path(__file__).resolve().with_name("AntigravityCheck.cs")])
    subprocess.run([str(exe)] + ([str(workspace)] if "--live" in sys.argv[1:] else []), check=True, timeout=45)

    # Relay-level regression: Positron/external agy sessions are accepted, listed, readable, resumable and endable.
    r = relay.Relay(str(Path(folder) / "relay-state"))
    sid = str(uuid.uuid4())
    r.agent({
        "info": {
            "instance": "1" * 32,
            "enabled": True,
            "workspaces": ["demo"],
            "tools": ["claude", "codex", "agy", "shell"],
            "features": ["peek", "end"],
            "projects": [{"name": "demo", "path": r"E:\demo", "tools": "claude agy", "kinds": ["终端"]}],
        },
        "sessions": [{
            "id": sid,
            "tool": "agy",
            "dir": "demo",
            "title": "Positron 里的 agy 对话",
            "updated": 1700000000000,
            "live": True,
            "now": True,
            "host": "cli",
            "origin": "Positron",
            "kind": "终端",
            "status": "idle",
            "can_takeover": True,
            "ownership_known": True,
            "ways": {"say": "", "stop": "", "answer": "", "end": "process"},
        }],
    })
    overview = r.overview()
    assert len(overview["sessions"]) == 1, overview["sessions"]
    sess = overview["sessions"][0]
    assert sess["tool"] == "agy" and sess["origin"] == "Positron" and sess["live"] is True and sess["can_takeover"] is True, sess
    assert "agy" in overview["device"]["projects"][0]["tools"], overview["device"]["projects"]

    # Takeover start with session + takeover=True succeeds; history=True and fork=True are rejected.
    started = r.command({"action": "start", "id": "a" * 32, "tool": "agy", "dir": "demo", "session": sid, "takeover": True})
    assert started.get("terminal"), started
    for bad in (
        {"action": "start", "id": "b" * 32, "tool": "agy", "dir": "demo", "history": True},
        {"action": "start", "id": "c" * 32, "tool": "agy", "dir": "demo", "session": sid, "fork": True},
    ):
        try:
            r.command(bad)
            raise AssertionError("expected rejection for " + str(bad))
        except relay.RemoteError:
            pass
print("Antigravity Windows & relay session checks passed")
