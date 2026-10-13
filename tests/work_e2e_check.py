"""The owner's work, through the real relay and the real Windows program (agent-windows/bin, build it first): the
numbers of made-up records; a conversation handed over to one that runs in a terminal of the phone's, where the
program types it; a question asked there when its time has come; a rule that passes work on, set and removed. The
tools are stand-ins and their conversations made up (tests/made_up.py): nothing real is read, started or told.

    python tests/work_e2e_check.py
"""
import http.client
import json
import os
import secrets
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))
from made_up import Homes       # noqa: E402

PORT = 8743
uuid = lambda n: "0199bbbb-cccc-7ddd-8eee-%012d" % n
GIVEN, TAKING = uuid(1), uuid(2)


def main():
    temp = tempfile.TemporaryDirectory(prefix="remote-ai-work-", ignore_cleanup_errors=True)
    password = "test-" + secrets.token_hex(8)
    data, shop = Path(temp.name) / "agent", Path(temp.name) / "projects" / "web-shop"
    data.mkdir(); shop.mkdir(parents=True)
    homes = Homes(temp.name)
    homes.claude_saved(shop, GIVEN, "修复结算页的金额显示", 1, said=("结算页的金额有时多出一位小数", "问题在 formatPrice，已经改好并补了用例。"),
                       worked=({"type": "tool_use", "id": "t1", "name": "Bash", "input": {"command": "npm test -- cart", "description": "运行测试"}},))
    homes.codex_saved(shop, TAKING, "审查改动", 2)
    environment = homes.environment(RCLI_PASSWORD=password, REMOTECLI_UNATTENDED="0")
    tools = homes.stand_ins(temp.name)
    # (the stand-in for Codex of this check reads what is typed to it and says it again, as a tool takes its user's words)
    (tools / "reads.py").write_text("import sys\nprint('STAND-IN codex reads', flush=True)\nfor line in sys.stdin:\n    print('GOT ' + line.strip(), flush=True)\n", encoding="utf-8")
    (tools / "codex.cmd").write_text('@if "%%1"=="--help" (echo stand-in) else ("%s" "%s")\r\n' % (sys.executable, tools / "reads.py"), encoding="mbcs")
    environment["PATH"] = str(tools) + ";" + environment["PATH"]
    (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % PORT, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["web-shop=" + str(shop)]}), encoding="utf-8")
    relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(PORT), "--data", str(Path(temp.name) / "relay")], env=environment, stdout=subprocess.DEVNULL)
    program = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
    subprocess.run([str(program), "--set-password", str(data)], input=password.encode(), check=True)
    agent = subprocess.Popen([str(program), "--data", str(data)], env=environment)
    try:
        cookie = {}

        def call(path, payload=None, expect=200):
            connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=100)
            body = None if payload is None else json.dumps(payload).encode()
            connection.request("POST" if body is not None else "GET", path, body=body, headers=dict(cookie, **({"Content-Type": "application/json"} if body is not None else {})))
            reply = connection.getresponse()
            text = reply.read()
            if reply.getheader("Set-Cookie"):
                cookie["Cookie"] = reply.getheader("Set-Cookie").split(";")[0]
            assert reply.status == expect, (path, reply.status, text[:300].decode("utf-8", "replace"))
            return json.loads(text)

        def until(test, seconds, what):
            deadline = time.time() + seconds
            while time.time() < deadline:
                found = test()
                if found:
                    return found
                time.sleep(0.3)
            raise AssertionError("waited in vain for " + what)

        work = lambda action, expect=200, **more: call("/api/work", dict({"id": secrets.token_hex(16), "action": action}, **more), expect)
        for _ in range(40):
            try:
                call("/api/login", {"password": password})
                break
            except OSError:
                time.sleep(0.5)
        device = until(lambda: (lambda d: d if len(call("/api/terminal").get("sessions", [])) == 2 else None)(call("/api/terminal")["device"]), 40, "the computer with its two conversations")
        assert "work" in device["features"] and "chains" in device["features"], device["features"]

        # ---- the numbers: the made-up records hold no times, so there is nothing to count; the days are there
        numbers = work("stats", days=7)
        assert numbers["ready"] is True and len(numbers["days"]) == 7 and numbers["days"][-1]["date"] == time.strftime("%Y-%m-%d") and len(numbers["hours"]) == 24, numbers
        assert "天数" in work("stats", 400, days=0)["error"]

        # ---- a conversation in a terminal of the phone's: the one that takes over
        assert "没有在电脑上开着" in work("handoff", 400, **{"from": GIVEN, "to": TAKING})["error"], "nothing has it open yet"
        terminal = call("/api/terminal", {"id": secrets.token_hex(16), "action": "start", "tool": "codex", "dir": "web-shop", "session": TAKING, "history": False})["terminal"]
        seen = {"after": 0, "text": ""}

        def screen():
            view = call("/api/terminal?terminal=%s&after=%d" % (terminal, seen["after"]))
            for chunk in view.get("chunks", []):
                seen["text"] += chunk["data"]
                seen["after"] = chunk["seq"]
            return seen["text"]

        until(lambda: "STAND-IN codex" in screen(), 40, "the stand-in for Codex in its terminal")
        handed = work("handoff", **{"from": GIVEN, "to": TAKING, "note": "重点看边界情况"})
        assert handed["status"] == "accepted" and handed["note"] == "已发到手机终端" and handed["length"] > 60, handed
        # what the program typed there, as the terminal shows it: the owner's words, from first to last
        for part in ("【对话交接】", "Claude Code", "修复结算页的金额显示", "结算页的金额有时多出一位小数", "问题在 formatPrice", "重点看边界情况"):
            until(lambda: part in screen(), 15, "%r in the terminal that takes over" % part)

        # ---- a question asked at its time: set for a moment from now, asked once, and how it went is kept
        item = {"id": secrets.token_hex(16), "session": TAKING, "text": "测试都通过了吗", "kind": "once", "at": int((time.time() + 4) * 1000)}
        set_ = work("schedule_set", item=item)["schedule"]
        assert set_["tool"] == "codex" and set_["title"] == "审查改动" and set_["on"] is True and set_["next"] == item["at"] and set_["last"] is None, set_
        assert "现在之后" in work("schedule_set", 400, item=dict(item, id=secrets.token_hex(16), at=int((time.time() - 60) * 1000)))["error"]
        until(lambda: "测试都通过了吗" in screen(), 40, "the question in the terminal when its time has come")
        asked = until(lambda: (lambda s: s if s["last"] else None)(work("schedules")["schedules"][0]), 10, "how the asking went")
        assert asked["on"] is False and asked["runs"] == 1 and asked["last"]["ok"] is True and asked["last"]["note"] == "已发到手机终端", asked
        work("schedule_remove", schedule=item["id"])
        assert work("schedules")["schedules"] == [] and "没有找到" in work("schedule_remove", 400, schedule=item["id"])["error"]
        # kept in the data folder, its words not in plain sight
        assert (data / "schedules.json").exists() and "测试都通过了吗" not in (data / "schedules.json").read_text(encoding="utf-8")

        # ---- what is passed on when a conversation stops: the rule is kept and told back, with the titles of both
        rule = {"id": secrets.token_hex(16), "from": TAKING, "to": GIVEN, "text": "按审查意见修改", "rounds": 2}
        kept = work("chain_set", item=rule)["chain"]
        assert (kept["from_tool"], kept["to_tool"], kept["from_title"], kept["to_title"], kept["left"], kept["rounds"], kept["last"]) == ("codex", "claude", "审查改动", "修复结算页的金额显示", 2, 2, None), kept
        assert [one["id"] for one in work("chains")["chains"]] == [rule["id"]]
        work("chain_remove", chain=rule["id"])
        assert work("chains")["chains"] == []
        print("work: the numbers, a handover typed into the phone's terminal, a question asked at its time, a rule set and removed, by the real program")
    finally:
        agent.kill(); relay.kill(); homes.close()
        time.sleep(1)
        try:
            temp.cleanup()
        except OSError:
            pass


if __name__ == "__main__":
    main()
