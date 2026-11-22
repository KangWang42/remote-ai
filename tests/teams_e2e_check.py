"""End-to-end check for 分工协作 leader model & reasoning effort through the real relay and real Windows agent.

Verifies:
- `device.features` advertises `teams-models`, `teams-roster`, and `teams-effort`
- `team_models` returns Codex `model/list` (`efforts`, `default_effort`, `is_default`, hidden filtered) and Claude `supportedEffortLevels`
- `team_start` (manual preset members and `auto_roster: true`) passes leader `model` & `effort` into Codex `thread/start` (`config.model_reasoning_effort`) and every `turn/start` (initial dispatch, verification turn, follow-up round)
- Restarting the agent restores persisted `roster[0].model` and `roster[0].effort` from `teams.dat` and reuses them on reopened rounds
- `team_remove` closes every terminal the task began (leader and members), their conversations stay in the history
  listed by themselves, and a conversation of the phone's own goes on running

    python tests/teams_e2e_check.py [<folder with RemoteCliAgent.exe>]
"""
import base64
import http.client
import json
import secrets
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))
from made_up import Homes       # noqa: E402


def free_port():
    with socket.socket() as reserved:
        reserved.bind(("127.0.0.1", 0))
        return reserved.getsockname()[1]


def main():
    program = Path(sys.argv[1] if len(sys.argv) > 1 else ROOT / "agent-windows" / "bin") / "RemoteCliAgent.exe"
    port, password = free_port(), "test-" + secrets.token_hex(8)
    temp = tempfile.TemporaryDirectory(prefix="remote-ai-teams-e2e-", ignore_cleanup_errors=True)
    data, shop = Path(temp.name) / "agent", Path(temp.name) / "projects" / "web-shop"
    codex_log = Path(temp.name) / "codex.jsonl"
    claude_log = Path(temp.name) / "claude.jsonl"
    agy_log = Path(temp.name) / "agy.jsonl"
    data.mkdir()
    shop.mkdir(parents=True)
    image = ".remote-ai/attachments/20261121-123456-a1b2c3.png"
    (shop / image).parent.mkdir(parents=True)
    pixels = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="
    (shop / image).write_bytes(base64.b64decode(pixels))
    homes = Homes(temp.name)
    environment = homes.environment(
        RCLI_PASSWORD=password,
        STANDIN_CODEX_LOG=str(codex_log),
        STANDIN_CLAUDE_LOG=str(claude_log),
        STANDIN_AGY_RECORD=str(agy_log),
        STANDIN_AGY_IMAGE="1",
        STANDIN_TEAM_AUTO="1",
    )
    tools = homes.stand_ins(temp.name)
    for name in ("claude", "codex", "agy"):
        (tools / (name + ".cmd")).write_text(
            '@if "%%1"=="--help" (echo stand-in) else ("%s" "%s" %s %%*)\r\n'
            % (sys.executable, ROOT / "tests" / "chat_standin.py", name),
            encoding="mbcs",
        )
    environment["PATH"] = str(tools) + ";" + environment["PATH"]
    (data / "config.json").write_text(
        json.dumps({"Server": "http://127.0.0.1:%d" % port, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["web-shop=" + str(shop)]}),
        encoding="utf-8",
    )
    relay = subprocess.Popen(
        [sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(port), "--data", str(Path(temp.name) / "relay")],
        env=environment,
        stdout=subprocess.DEVNULL,
    )
    subprocess.run([str(program), "--set-password", str(data)], input=password.encode(), check=True)
    agent = subprocess.Popen([str(program), "--data", str(data)], env=environment)
    cookie = {}
    try:
        def call(path, payload=None, expect=200):
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=60)
            body = None if payload is None else json.dumps(payload).encode()
            connection.request(
                "POST" if body is not None else "GET",
                path,
                body=body,
                headers=dict(cookie, **({"Content-Type": "application/json"} if body else {})),
            )
            reply = connection.getresponse()
            text = reply.read()
            if reply.getheader("Set-Cookie"):
                cookie["Cookie"] = reply.getheader("Set-Cookie").split(";")[0]
            assert reply.status == expect, (path, reply.status, text[:300].decode("utf-8", "replace"))
            return json.loads(text)

        def work(action, expect=200, **more):
            return call("/api/work", dict({"id": secrets.token_hex(16), "action": action}, **more), expect)

        def op(**payload):
            payload.setdefault("id", secrets.token_hex(16))
            for _ in range(200):
                answer = call("/api/terminal", payload)
                if answer.get("state") != "queued":
                    break
                time.sleep(0.1)
            assert not answer.get("error"), (payload["action"], answer)
            return answer

        def wait_online():
            for _ in range(80):
                device = call("/api/terminal")["device"]
                if device["online"] and "teams-effort" in device.get("features", []):
                    return device
                time.sleep(0.25)
            raise AssertionError("agent never came online with teams-effort")

        def read_codex_log():
            if not codex_log.exists():
                return []
            return [json.loads(line) for line in codex_log.read_text(encoding="utf-8").splitlines() if line.strip()]

        for _ in range(60):
            try:
                call("/api/login", {"password": password})
                break
            except OSError:
                time.sleep(0.5)

        device = wait_online()
        for feat in ("teams", "teams-v2", "teams-models", "teams-roster", "teams-effort"):
            assert feat in device["features"], device["features"]

        # 1) Model catalog from CLI (`codex model/list` + `claude initialize`)
        catalog = work("team_models")["models"]
        codex_models = {m["id"]: m for m in catalog["codex"]}
        assert "hidden-model" not in codex_models, codex_models
        assert codex_models["gpt-6.2-sol"]["efforts"] == ["low", "medium", "high", "xhigh", "ultra"], codex_models["gpt-6.2-sol"]
        assert codex_models["gpt-6.2-sol"].get("default_effort") == "medium" and codex_models["gpt-6.2-sol"].get("is_default") is True, codex_models["gpt-6.2-sol"]
        assert codex_models["gpt-5.4"]["efforts"] == ["low", "medium", "high"], codex_models["gpt-5.4"]
        claude_models = {m["id"]: m for m in catalog["claude"]}
        assert claude_models["opus"]["efforts"] == ["low", "medium", "high", "xhigh", "max"] and claude_models["opus"].get("is_default") is True, claude_models["opus"]
        assert "efforts" not in claude_models["claude-haiku-4-5"], claude_models["claude-haiku-4-5"]

        # 2) Manual 分工协作 with Codex leader (`gpt-6.2-sol` + `high`): runs round 1 dispatch + verify to `done`
        manual_id = secrets.token_hex(16)
        started = work("team_start", item={
            "id": manual_id, "mode": "lead", "dir": "web-shop", "goal": "检查分工协作领队参数",
            "rounds": 2,
            "roster": [
                {"role": "leader", "tool": "codex", "model": "gpt-6.2-sol", "effort": "high"},
                {"role": "member-1", "tool": "claude", "model": "sonnet"},
            ],
        })["team"]
        assert started["roster"][0] == {"role": "leader", "tool": "codex", "model": "gpt-6.2-sol", "effort": "high"}, started["roster"]

        deadline = time.time() + 30
        while time.time() < deadline:
            team = next(t for t in work("teams")["teams"] if t["id"] == manual_id)
            if team["state"] == "done":
                break
            time.sleep(0.3)
        assert team["state"] == "done", team

        events = read_codex_log()
        starts = [e for e in events if e["event"] == "thread/start"]
        turns = [e for e in events if e["event"] == "turn/start"]
        assert len(starts) >= 1 and starts[-1]["model"] == "gpt-6.2-sol" and starts[-1]["config_effort"] == "high", starts
        assert len(turns) >= 2 and all(t["model"] == "gpt-6.2-sol" and t["effort"] == "high" for t in turns[-2:]), turns

        # 3) Follow-up round via `team_say` keeps model & effort on subsequent turns
        turns_before = len(turns)
        work("team_say", team=manual_id, text="再补做一轮核验")
        deadline = time.time() + 30
        while time.time() < deadline:
            team = next(t for t in work("teams")["teams"] if t["id"] == manual_id)
            if team["state"] == "done" and team["round"] >= 2:
                break
            time.sleep(0.3)
        assert team["state"] == "done" and team["round"] >= 2, team
        turns_after = [e for e in read_codex_log() if e["event"] == "turn/start"]
        assert len(turns_after) >= turns_before + 2 and all(t["model"] == "gpt-6.2-sol" and t["effort"] == "high" for t in turns_after[turns_before:]), turns_after

        # 4) Auto-roster (`auto_roster: true`) passes leader model & effort too
        auto_id = secrets.token_hex(16)
        auto_started = work("team_start", item={
            "id": auto_id, "mode": "lead", "dir": "web-shop", "goal": "自动组队检查领队参数",
            "rounds": 1, "auto_roster": True,
            "roster": [{"role": "leader", "tool": "codex", "model": "gpt-6.2-astra", "effort": "xhigh"}],
        })["team"]
        assert auto_started["roster"][0] == {"role": "leader", "tool": "codex", "model": "gpt-6.2-astra", "effort": "xhigh"}, auto_started["roster"]
        deadline = time.time() + 30
        while time.time() < deadline:
            auto_team = next(t for t in work("teams")["teams"] if t["id"] == auto_id)
            if auto_team["state"] == "done":
                break
            time.sleep(0.3)
        assert auto_team["state"] == "done" and len(auto_team["roster"]) == 2, auto_team
        auto_events = read_codex_log()
        assert any(e["event"] == "thread/start" and e["model"] == "gpt-6.2-astra" and e["config_effort"] == "xhigh" for e in auto_events), auto_events
        assert any(e["event"] == "turn/start" and e["model"] == "gpt-6.2-astra" and e["effort"] == "xhigh" for e in auto_events), auto_events

        # 5) Restart agent: persisted `teams.dat` restores leader model & effort and reuses them on a reopened round
        agent.kill()
        agent.wait(timeout=10)
        codex_log.write_text("", encoding="utf-8")
        agent = subprocess.Popen([str(program), "--data", str(data)], env=environment)
        wait_online()
        restored = next(t for t in work("teams")["teams"] if t["id"] == manual_id)
        assert restored["roster"][0] == {"role": "leader", "tool": "codex", "model": "gpt-6.2-sol", "effort": "high"}, restored["roster"]
        work("team_say", team=manual_id, text="重启后再验证一轮")
        deadline = time.time() + 30
        while time.time() < deadline:
            reopened_team = next(t for t in work("teams")["teams"] if t["id"] == manual_id)
            if reopened_team["state"] == "done" and reopened_team["round"] >= 3:
                break
            time.sleep(0.3)
        assert reopened_team["state"] == "done" and reopened_team["round"] >= 3, reopened_team
        after_restart = read_codex_log()
        assert any(e["event"] == "thread/resume" and e["model"] == "gpt-6.2-sol" and e["config_effort"] == "high" for e in after_restart), after_restart
        assert any(e["event"] == "turn/start" and e["model"] == "gpt-6.2-sol" and e["effort"] == "high" for e in after_restart), after_restart

        # A screenshot crosses the relay, Windows collaboration and the actual tool adapter.
        for writer, log in (("claude", claude_log), ("agy", agy_log)):
            attachment_team = secrets.token_hex(16)
            work("team_start", item={
                "id": attachment_team, "mode": "review", "dir": "web-shop",
                "goal": "协作原生图片测试", "rounds": 2, "tools": [writer, "codex"],
            })
            work("team_say", team=attachment_team, text="", attachments=[image])
            deadline = time.monotonic() + 15
            packets = []
            while time.monotonic() < deadline:
                if log.exists():
                    packets = [json.loads(line) for line in log.read_text(encoding="utf-8").splitlines()]
                    if any(packet.get("image_data") == pixels for packet in packets):
                        break
                time.sleep(0.15)
            else:
                raise AssertionError((writer, "no native image payload received", packets))
            work("team_stop", team=attachment_team)
            work("team_remove", team=attachment_team)
        print("team_say delivered matching native PNG bytes to Claude stream-json and image-capable ACP")

        # 6) Removing the task closes the conversations it began; the history keeps them, other conversations go on
        own = op(action="start", tool="claude", dir="web-shop", form="chat", text="不属于协作的对话")["terminal"]
        deadline = time.time() + 30
        while time.time() < deadline:
            listed = call("/api/terminal")
            members = [t for t in listed["terminals"] if t["team"] == manual_id and t["state"] == "running"]
            mine = next((t for t in listed["terminals"] if t["id"] == own), None)
            if {t["tool"] for t in members} >= {"codex", "claude"} and all(t["session"] for t in members) and mine and mine["session"]:
                break
            time.sleep(0.3)
        assert {t["tool"] for t in members} >= {"codex", "claude"} and all(t["session"] for t in members), members
        assert mine and mine["state"] == "running" and mine["team"] == "", mine
        # the stand-ins keep no records: each member's conversation is saved as its tool would save it
        for member in members:
            save = homes.claude_saved if member["tool"] == "claude" else homes.codex_saved
            save(shop, member["session"], "协作成员 " + member["tool"], 0)

        def history():
            return {(s["tool"], s["id"]): s for s in call("/api/terminal")["sessions"]}

        deadline = time.time() + 20
        while time.time() < deadline:
            kept = history()
            if all(kept.get((m["tool"], m["session"]), {}).get("team") == manual_id for m in members):
                break
            time.sleep(0.5)
        assert all(kept.get((m["tool"], m["session"]), {}).get("team") == manual_id for m in members), [kept.get((m["tool"], m["session"])) for m in members]
        work("team_remove", team=manual_id)
        assert all(t["id"] != manual_id for t in work("teams")["teams"])
        gone = {t["id"] for t in members}
        deadline = time.time() + 20
        while time.time() < deadline:
            listed = call("/api/terminal")
            still = [t for t in listed["terminals"] if t["id"] in gone and t["state"] == "running"]
            if not still:
                break
            time.sleep(0.3)
        assert not still, still
        mine = next(t for t in listed["terminals"] if t["id"] == own)
        assert mine["state"] == "running", mine
        deadline = time.time() + 20
        while time.time() < deadline:
            seats = [history().get((m["tool"], m["session"])) for m in members]
            if all(seat and not seat.get("team") and not seat.get("live") for seat in seats):
                break
            time.sleep(0.5)
        assert all(seat and not seat.get("team") and not seat.get("live") for seat in seats), seats
        print("teams e2e: CLI model/effort catalog, manual & auto-roster leader effort across rounds, restart recovery, and removal closing its conversations passed")
    finally:
        agent.kill()
        relay.kill()
        homes.close()
        time.sleep(0.5)
        try:
            temp.cleanup()
        except OSError:
            pass


if __name__ == "__main__":
    main()
