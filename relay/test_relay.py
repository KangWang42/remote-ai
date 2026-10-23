import tempfile
import unittest

import relay as tr
from relay import RemoteError


class TerminalRelayTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = self.temp.name + "/terminal.json"
        self.relay = tr.Relay(self.path)
        self.info = {"instance": "b" * 32, "enabled": True, "tools": ["claude", "codex", "shell"], "workspaces": ["demo"]}
        self.relay.agent({"info": self.info}, now=1000)

    def start(self):
        payload = {"action": "start", "id": "a" * 32, "tool": "codex", "dir": "demo", "history": False}
        result = self.relay.command(payload, now=1001)
        self.assertEqual(result, self.relay.command(payload, now=1002))
        self.terminal = result["terminal"]
        self.relay.agent({"info": self.info, "acks": [{"id": payload["id"]}], "terminals": [{"id": self.terminal, "state": "running"}]}, now=1003)
        return payload

    def test_phone_computer_update_receipt_and_version_are_distinct(self):
        request = {"action": "update", "id": "a" * 32}
        with self.assertRaisesRegex(RemoteError, "不支持"):
            self.relay.command(request, now=1001)
        self.info.update(features=["update"], version="1.6.3", newer="1.6.4")
        self.relay.agent({"info": self.info}, now=1002)
        device = self.relay.overview(now=1002)["device"]
        self.assertEqual((device["version"], device["newer"]), ("1.6.3", "1.6.4"))
        queued = self.relay.command(request, now=1003)
        self.assertEqual(queued["state"], "queued")
        self.assertEqual(self.relay.command(request, now=1004), queued)
        self.assertEqual(len(self.relay._pending.queued), 1)
        self.relay.agent({"info": self.info, "acks": [{"id": request["id"]}]}, now=1005)
        self.assertEqual(self.relay.command(request, now=1006)["state"], "done")
        self.assertEqual(self.relay.overview(now=1006)["device"]["version"], "1.6.3")
        self.assertEqual(len(self.relay._pending.queued), 0)
        self.info.update(version="1.6.4", newer="")
        self.relay.agent({"info": self.info}, now=1007)
        self.assertEqual(self.relay.overview(now=1007)["device"]["version"], "1.6.4")
        self.info["enabled"] = False
        self.relay.agent({"info": self.info}, now=1008)
        with self.assertRaisesRegex(RemoteError, "远控已关闭"):
            self.relay.command(dict(request, id="c" * 32), now=1009)

    def test_a_chat_is_a_terminal_whose_output_is_its_events(self):
        start = {"action": "start", "id": "a" * 32, "tool": "claude", "dir": "demo", "history": False, "form": "chat", "text": "结算页的金额\n有时多一位小数"}
        with self.assertRaisesRegex(RemoteError, "不支持这种对话"):
            self.relay.command(start, now=1001)
        self.info["features"] = ["chat"]
        self.relay.agent({"info": self.info}, now=1001)
        for change in ({"tool": "shell"}, {"history": True}, {"form": "screen"}, {"text": "\0"}, {"text": "x" * 8001}):
            with self.assertRaises(RemoteError):
                self.relay.command(dict(start, id="c" * 32, **change), now=1002)
        with self.assertRaisesRegex(RemoteError, "终端选项无效"):
            self.relay.command({"action": "start", "id": "c" * 32, "tool": "claude", "dir": "demo", "history": False, "text": "hi"}, now=1002)
        terminal = self.relay.command(start, now=1002)["terminal"]
        op = self.relay.pull({"instance": self.info["instance"], "wait": 0})["operations"][0]
        self.assertEqual((op["form"], op["text"]), ("chat", start["text"]))
        listed = self.relay.overview(now=1003)["terminals"][0]
        self.assertEqual((listed["form"], listed["title"]), ("chat", "结算页的金额 有时多一位小数"))
        # the computer says how it stands, what it said last and what it asks: the list shows that, not a screen
        self.relay.agent({"info": self.info, "acks": [{"id": start["id"]}], "terminals": [{"id": terminal, "state": "running", "form": "chat", "status": "confirm", "said": "我先看看。", "ask": "Bash\nnpm test"}],
                          "output": [{"terminal": terminal, "seq": 1, "data": '{"t":"ask","id":"0123456789abcdef"}\n'}]}, now=1004)
        listed = self.relay.overview(now=1006)["terminals"][0]
        self.assertEqual((listed["phase"], listed["said"], listed["asks"]), ("confirm", "我先看看。", ["Bash", "npm test"]))
        self.assertNotIn("asking", listed)
        self.assertEqual(self.relay.overview(terminal, now=1006)["chunks"][0]["data"], '{"t":"ask","id":"0123456789abcdef"}\n')
        # what is said, a stop and an answer go to the computer as they are; a screen's input and size do not apply
        said = {"action": "say", "id": "d" * 32, "terminal": terminal, "text": "  先跑测试\r\n再改  "}
        self.relay.command(said, now=1007)
        op = self.relay.pull({"instance": self.info["instance"], "wait": 0})["operations"][0]
        self.assertEqual((op["action"], op["text"]), ("say", "先跑测试\n再改"))
        self.relay.command({"action": "answer", "id": "e" * 32, "terminal": terminal, "ask": "0123456789abcdef", "decision": "choice:2"}, now=1007)
        self.assertEqual(self.relay.pull({"instance": self.info["instance"], "wait": 0})["operations"][0]["decision"], "choice:2")
        for bad in ({"action": "answer", "ask": "0123456789abcdef", "decision": "yes"}, {"action": "answer", "ask": "x", "decision": "allow"},
                    {"action": "input", "data": "y\r"}, {"action": "resize", "cols": 80, "rows": 24}):
            with self.assertRaises(RemoteError):
                self.relay.command(dict(bad, id="f" * 32, terminal=terminal), now=1008)
        self.relay.command({"action": "stop", "id": "1" * 32, "terminal": terminal}, now=1008)
        self.relay.agent({"info": self.info, "terminals": [{"id": terminal, "state": "running", "form": "chat", "status": "idle", "said": "好了", "ask": ""}]}, now=1009)
        with self.assertRaisesRegex(RemoteError, "没有在执行"):
            self.relay.command({"action": "stop", "id": "2" * 32, "terminal": terminal}, now=1010)
        # a screen's terminal is not told to stop or answered this way
        screen = self.relay.command({"action": "start", "id": "3" * 32, "tool": "codex", "dir": "demo", "history": False}, now=1011)["terminal"]
        self.relay.agent({"info": self.info, "terminals": [{"id": terminal, "state": "running", "form": "chat", "status": "idle"}, {"id": screen, "state": "running"}]}, now=1012)
        with self.assertRaisesRegex(RemoteError, "不支持此操作"):
            self.relay.command({"action": "stop", "id": "4" * 32, "terminal": screen}, now=1013)

    def test_fresh_start_never_becomes_anonymous_resume(self):
        payload = {"action": "start", "id": "a" * 32, "tool": "claude", "dir": "demo", "history": True}
        with self.assertRaisesRegex(RemoteError, "resume"):
            self.relay.command(payload, now=1001)
        result = self.relay.command(dict(payload, history=False), now=1002)
        ops = self.relay.pull({"instance": self.info["instance"], "wait": 0})["operations"]
        self.assertFalse(ops[0]["history"])
        self.assertEqual(self.relay.overview(result["terminal"], now=1003)["terminal"]["session"], "")

    def test_desktop_handoff_is_capability_gated_and_replays_its_receipt(self):
        op = {"id": "a" * 32, "action": "app_open", "tool": "codex", "dir": "demo", "draft": "草稿"}
        with self.assertRaises(RemoteError):
            self.relay.command(op, now=1001)
        self.info["features"] = ["codex-desktop-open"]
        self.relay.agent({"info": self.info}, now=1002)
        for change in ({"tool": "claude"}, {"dir": "missing"}, {"path": "C:/private"}, {"draft": "x" * 1001}, {"draft": "\0"}, {"draft": "😀" * 501}, {"draft": []}):
            with self.assertRaises(RemoteError):
                self.relay.command(dict(op, **change), now=1003)
        queued = self.relay.command(op, now=1003)
        self.assertEqual(queued["terminal"], "")
        self.assertEqual(self.relay.overview(now=1003)["terminals"], [])
        receipt = {"status": "handed_off", "sent": False, "message": "尚未发送"}
        self.relay.agent({"info": self.info, "acks": [{"id": op["id"], "result": receipt}]}, now=1004)
        for _ in range(3):
            answer = self.relay.command(op, now=1005)
            self.assertEqual(answer["state"], "done")
            self.assertEqual(answer["result"], receipt)
        self.assertEqual(len(self.relay._pending.queued), 0)
        with self.assertRaises(RemoteError):
            self.relay.command(dict(op, draft="changed"), now=1005)

    def test_a_desktop_app_is_opened_for_each_tool_the_computer_offers_it_for(self):
        op = {"id": "b" * 32, "action": "app_open", "tool": "claude", "dir": "demo", "draft": "草稿"}
        self.info["features"] = ["codex-desktop-open"]
        self.relay.agent({"info": self.info}, now=1001)
        with self.assertRaisesRegex(RemoteError, "暂不支持"):
            self.relay.command(op, now=1002)
        self.info["features"] = ["codex-desktop-open", "claude-desktop-open", "gemini-desktop-open"]
        self.relay.agent({"info": self.info}, now=1003)
        self.assertEqual(self.relay.overview(now=1003)["device"]["features"], ["codex-desktop-open", "claude-desktop-open"])
        with self.assertRaisesRegex(RemoteError, "暂不支持"):
            self.relay.command(dict(op, tool="gemini"), now=1003)
        self.assertEqual(self.relay.command(op, now=1003)["terminal"], "")
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 0})["operations"]
        self.assertEqual([(o["action"], o["tool"], o["dir"], o["draft"]) for o in asked], [("app_open", "claude", "demo", "草稿")])

    def test_the_way_of_each_thing_and_the_reasons_are_passed_on(self):
        sid, other, quiet = "11111111-2222-3333-4444-555555555555", "66666666-7777-8888-9999-000000000000", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
        info = dict(self.info, features=["peek", "reach"])
        at_work = {"id": sid, "tool": "codex", "dir": "demo", "title": "桌面版里的", "updated": 1, "live": True, "status": "busy", "reach": ["say", "stop"], "transport": "codex-desktop", "turn": "turn-1",
                   "ways": {"say": "hook", "stop": "codex-desktop", "answer": "", "end": "", "other": "x"}, "why": {"end": "这段对话由电脑上的应用管理，请在那里关闭它", "other": "x", "say": 5}}
        # one whose stop names no turn is not let through, and no way is said for it; one whose tool tells nothing keeps its reasons
        no_turn = dict(at_work, id=other, turn="", ways={"say": "hook", "stop": "codex-desktop", "answer": "bogus", "end": "process"}, why={"stop": "长" * 300})
        untold = {"id": quiet, "tool": "codex", "dir": "demo", "title": "不通报的", "updated": 1, "live": True, "ways": {"say": "", "stop": "", "answer": "", "end": "process"}, "why": {"say": "现在收不到手机的消息"}}
        saved = {"id": "12121212-3434-5656-7878-909090909090", "tool": "claude", "dir": "demo", "title": "存着的", "updated": 1, "live": False, "ways": {"say": "wake"}}
        self.relay.agent({"info": info, "sessions": [at_work, no_turn, untold, saved]}, now=1001)
        shown = {s["id"]: s for s in self.relay.overview(now=1001)["sessions"]}
        self.assertEqual((shown[sid]["ways"], shown[sid]["why"]), ({"say": "hook", "stop": "codex-desktop", "answer": "", "end": ""}, {"end": "这段对话由电脑上的应用管理，请在那里关闭它"}))
        self.assertEqual((shown[other]["reach"], shown[other]["ways"], len(shown[other]["why"]["stop"])), (["say"], {"say": "hook", "stop": "", "answer": "", "end": "process"}, 200))
        self.assertEqual((shown[quiet]["reach"], shown[quiet]["ways"], shown[quiet]["why"]), (None, {"say": "", "stop": "", "answer": "", "end": "process"}, {"say": "现在收不到手机的消息"}))
        self.assertEqual((shown[saved["id"]]["ways"], shown[saved["id"]]["why"]), (None, None))
        # a computer of an earlier version says neither: nothing is made up for it
        self.relay.agent({"info": info, "sessions": [{k: v for k, v in at_work.items() if k not in ("ways", "why")}]}, now=1002)
        earlier = self.relay.overview(now=1002)["sessions"][0]
        self.assertEqual((earlier["ways"], earlier["why"], earlier["transport"], earlier["reach"]), (None, None, "codex-desktop", ["say", "stop"]))

    def test_what_waits_for_a_conversation_is_listed_taken_back_and_released(self):
        import threading
        sid = "11111111-2222-3333-4444-555555555555"
        info = dict(self.info, features=["peek", "reach", "outbox"])
        waiting = [{"id": "a" * 32, "text": "第一条" + "长" * 300, "state": "queued", "at": 5}, {"id": "b" * 32, "text": "第二条", "state": "paused", "at": 6},
                   {"id": "bad id", "text": "x", "state": "queued"}, {"id": "c" * 32, "text": "已经交给它的", "state": "handed"}]
        session = {"id": sid, "tool": "claude", "dir": "demo", "title": "正在做的", "updated": 1, "live": True, "status": "busy", "reach": ["say"], "queued": waiting}
        self.relay.agent({"info": info, "sessions": [session]}, now=1001)
        shown = self.relay.overview(now=1001)["sessions"][0]["queued"]
        self.assertEqual([(m["id"][0], len(m["text"]), m["state"], m["at"]) for m in shown], [("a", 200, "queued", 5), ("b", 3, "paused", 6)])
        with self.assertRaisesRegex(RemoteError, "消息编号无效"):
            self.relay.conversation({"id": "1" * 32, "session": sid, "action": "withdraw", "message": "nope"}, now=1001, wait=0)
        with self.assertRaisesRegex(RemoteError, "已经交给对话"):
            self.relay.conversation({"id": "2" * 32, "session": sid, "action": "withdraw", "message": "c" * 32}, now=1001, wait=0)

        def carried(payload, answer):
            got = {}
            asking = threading.Thread(target=lambda: got.update(value=self.relay.conversation(payload, now=1001, wait=5)))
            asking.start()
            asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
            self.relay.agent({"info": info, "acks": [{"id": payload["id"], "error": "", "result": answer}]}, now=1002)
            asking.join(5)
            return [{k: v for k, v in o.items() if k not in ("at", "terminal")} for o in asked], got.get("value")

        asked, got = carried({"id": "3" * 32, "session": sid, "action": "withdraw", "message": "a" * 32}, {"state": "withdrawn", "message": "a" * 32})
        self.assertEqual(asked, [{"id": "3" * 32, "action": "session_withdraw", "session": sid, "tool": "claude", "dir": "demo", "message": "a" * 32}])
        self.assertEqual((got, [m["id"][0] for m in self.relay.overview(now=1002)["sessions"][0]["queued"]]), ({"state": "withdrawn", "message": "a" * 32}, ["b"]))
        asked, got = carried({"id": "4" * 32, "session": sid, "action": "release"}, {"released": 1, "way": "wake"})
        self.assertEqual((asked[0]["action"], got, self.relay.overview(now=1002)["sessions"][0]["queued"][0]["state"]), ("session_release", {"released": 1, "way": "wake"}, "queued"))
        with self.assertRaisesRegex(RemoteError, "没有暂停的消息"):
            self.relay.conversation({"id": "5" * 32, "session": sid, "action": "release"}, now=1003, wait=0)
        # a computer that keeps no such list is not asked
        self.relay.agent({"info": dict(info, features=["peek", "reach"]), "sessions": [session]}, now=1004)
        with self.assertRaisesRegex(RemoteError, "不支持撤回"):
            self.relay.conversation({"id": "6" * 32, "session": sid, "action": "withdraw", "message": "b" * 32}, now=1004, wait=0)

    def test_the_owners_work_is_asked_of_the_computer(self):
        import threading
        a, b = "11111111-2222-3333-4444-555555555555", "66666666-7777-8888-9999-000000000000"
        sessions = [{"id": a, "tool": "claude", "dir": "demo", "title": "甲", "updated": 1, "live": True}, {"id": b, "tool": "codex", "dir": "demo", "title": "乙", "updated": 1, "live": False}]
        self.relay.agent({"info": dict(self.info, features=["peek"]), "sessions": sessions}, now=1001)
        with self.assertRaisesRegex(RemoteError, "不支持这项功能"):
            self.relay.work({"id": "1" * 32, "action": "stats"}, now=1001, wait=0)
        info = dict(self.info, features=["peek", "work"])
        self.relay.agent({"info": info, "sessions": sessions}, now=1002)
        item = {"id": "c" * 32, "session": a, "tool": "codex", "text": "进展如何？", "kind": "daily", "time": "09:30"}
        for wrong, said in (({"action": "nope"}, "不支持"), ({"action": "stats", "days": 0}, "天数"), ({"action": "schedule_set", "item": dict(item, session="x")}, "没有找到"),
                            ({"action": "schedule_set", "item": dict(item, text=" ")}, "1 至 2000"), ({"action": "schedule_set", "item": dict(item, time="9:30")}, "时间无效"),
                            ({"action": "schedule_set", "item": dict(item, kind="every")}, "时间无效"), ({"action": "schedule_remove", "schedule": "x"}, "无效"),
                            ({"action": "handoff", "from": a, "to": a}, "同一段"), ({"action": "handoff", "from": a, "to": b}, "没有在电脑上开着"), ({"action": "handoff", "from": a, "to": "zz"}, "没有找到")):
            with self.assertRaisesRegex(RemoteError, said):
                self.relay.work(dict({"id": "2" * 32}, **wrong), now=1002, wait=0)

        def carried(payload, answer):
            got = {}
            asking = threading.Thread(target=lambda: got.update(value=self.relay.work(payload, now=1002, wait=5)))
            asking.start()
            asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
            self.relay.agent({"info": info, "acks": [{"id": payload["id"], "error": "", "result": answer}]}, now=1003)
            asking.join(5)
            return {k: v for k, v in asked[0].items() if k not in ("at", "terminal")}, got.get("value")

        asked, got = carried({"id": "3" * 32, "action": "stats", "days": 7}, {"ready": True, "days": []})
        self.assertEqual((asked, got), ({"id": "3" * 32, "action": "work_stats", "days": 7}, {"ready": True, "days": []}))
        asked, got = carried({"id": "4" * 32, "action": "schedule_set", "item": item}, {"schedule": {"id": "c" * 32}})
        # the tool is the computer's word for that conversation, not the phone's
        self.assertEqual(asked["item"], {"id": "c" * 32, "session": a, "tool": "claude", "text": "进展如何？", "kind": "daily", "on": True, "at": 0, "minutes": 0, "time": "09:30"})
        asked, got = carried({"id": "5" * 32, "action": "handoff", "from": b, "to": a, "note": "接着做"}, {"status": "accepted"})
        self.assertEqual(asked, {"id": "5" * 32, "action": "work_handoff", "from": b, "from_tool": "codex", "to": a, "to_tool": "claude", "note": "接着做"})

        # passing on when a conversation stops is a feature of its own: the tools' hooks tell of the stop
        rule = {"id": "d" * 32, "from": a, "to": b, "text": "审查上面的改动", "rounds": 3, "from_tool": "codex"}
        with self.assertRaisesRegex(RemoteError, "不支持接力"):
            self.relay.work({"id": "6" * 32, "action": "chain_set", "item": rule}, now=1002, wait=0)
        info = dict(self.info, features=["peek", "work", "chains"])
        self.relay.agent({"info": info, "sessions": sessions}, now=1002)
        for wrong, said in (({"action": "chain_set", "item": dict(rule, to=a)}, "同一段"), ({"action": "chain_set", "item": dict(rule, to="zz")}, "没有找到"), ({"action": "chain_set", "item": dict(rule, text="")}, "1 至 1000"),
                            ({"action": "chain_set", "item": dict(rule, rounds=11)}, "1 到 10"), ({"action": "chain_set", "item": dict(rule, rounds=True)}, "1 到 10"), ({"action": "chain_remove", "chain": "x"}, "无效")):
            with self.assertRaisesRegex(RemoteError, said):
                self.relay.work(dict({"id": "7" * 32}, **wrong), now=1002, wait=0)
        asked, got = carried({"id": "8" * 32, "action": "chain_set", "item": rule}, {"chain": {"id": "d" * 32}})
        self.assertEqual(asked["item"], {"id": "d" * 32, "from": a, "from_tool": "claude", "to": b, "to_tool": "codex", "text": "审查上面的改动", "rounds": 3})
        asked, got = carried({"id": "9" * 32, "action": "chain_remove", "chain": "d" * 32}, {})
        self.assertEqual((asked["action"], asked["chain"]), ("work_chain_remove", "d" * 32))
        asked, got = carried({"id": "a" * 32, "action": "chains"}, {"chains": []})
        self.assertEqual((asked, got), ({"id": "a" * 32, "action": "work_chains"}, {"chains": []}))
        # a conversation that runs in one of the phone's own terminals takes over too, though no program on the computer has it
        started = self.relay.command({"id": "b" * 32, "action": "start", "tool": "codex", "dir": "demo", "session": b}, now=1002)
        with self.assertRaisesRegex(RemoteError, "没有在电脑上开着"):       # (not yet: the terminal is still being started)
            self.relay.work({"id": "e" * 32, "action": "handoff", "from": a, "to": b}, now=1002, wait=0)
        self.relay.agent({"info": info, "acks": [{"id": "b" * 32}], "terminals": [{"id": started["terminal"], "state": "running", "session": b}]}, now=1003)
        asked, got = carried({"id": "c" * 32, "action": "handoff", "from": a, "to": b}, {"status": "accepted"})
        self.assertEqual((asked["to"], asked["to_tool"]), (b, "codex"))

    def test_desktop_timeout_never_claims_not_executed(self):
        self.info["features"] = ["codex-desktop-open"]
        self.relay.agent({"info": self.info}, now=1001)
        op = {"id": "a" * 32, "action": "app_open", "tool": "codex", "dir": "demo"}
        self.relay.command(op, now=1002)
        reply = self.relay.command(op, now=1002 + tr.OP_SECONDS + 1)
        self.assertIn("结果未知", reply["error"])

    def test_live_original_requires_explicit_takeover(self):
        sid = "12345678-1234-1234-1234-123456789abc"
        for tool in ("claude", "codex"):
            self.relay.agent({"info": self.info, "sessions": [{"id": sid, "tool": tool, "dir": "demo", "title": "原对话", "updated": 1, "live": True, "origin": "Positron"}]}, now=1001)
            with self.assertRaisesRegex(RemoteError, "接管"):
                self.relay.command({"action": "start", "id": "d" * 32, "tool": tool, "dir": "demo", "session": sid}, now=1002)
            self.assertEqual(self.relay.overview(now=1002)["sessions"][0]["origin"], "Positron")

    def test_codex_fork_preserves_source_and_requires_updated_agent(self):
        sid = "12345678-1234-1234-1234-123456789abc"
        session = {"id": sid, "tool": "codex", "dir": "demo", "title": "电脑会话", "updated": 5000,
                   "live": True, "can_takeover": False, "ownership_known": True, "takeover_reason": "共享后台"}
        self.relay.agent({"info": self.info, "sessions": [session]}, now=1001)
        op = {"action": "start", "id": "f" * 32, "tool": "codex", "dir": "demo", "session": sid, "fork": True}
        with self.assertRaises(RemoteError):
            self.relay.command(op, now=1002)
        self.info["features"] = ["codex-fork", "terminal-exit"]
        self.relay.agent({"info": self.info}, now=1003)
        terminal = self.relay.command(op, now=1004)["terminal"]
        current = self.relay.overview(terminal, now=1004)["terminal"]
        self.assertEqual(current["session"], "")
        self.assertIn("副本", current["title"])
        self.assertEqual(self.relay.overview(now=1004)["sessions"][0]["terminal"], "")
        with self.assertRaises(RemoteError):
            self.relay.command(dict(op, id="e" * 32, takeover=True), now=1004)
        with self.assertRaises(RemoteError):
            self.relay.command(dict(op, id="e" * 32, fork=1), now=1004)

    def test_session_host_is_forwarded_without_claiming_a_visible_window(self):
        for host, expected in [("cli", "cli"), ("shared", "shared"), ("remote", "remote"), ("unknown", "unknown"), (None, ""), (["cli"], "")]:
            session = {"id": "12345678-1234-1234-1234-123456789abc", "tool": "codex", "dir": "demo",
                       "title": "后台保留的旧对话", "updated": 5000, "live": True, "host": host, "can_takeover": False}
            self.relay.agent({"info": self.info, "sessions": [session]}, now=1001)
            actual = self.relay.overview(now=1002)["sessions"][0]
            self.assertEqual(actual["host"], expected)
            self.assertTrue(actual["live"])
            self.assertFalse(actual["can_takeover"])

    def test_writer_lock_activity_does_not_claim_a_computer_window(self):
        base = {"tool": "codex", "dir": "demo", "title": "会话", "updated": 5000,
                "live": True, "can_takeover": False}
        sessions = [
            dict(base, id="12345678-1234-1234-1234-123456789abc", host="cli"),
            dict(base, id="22345678-1234-1234-1234-123456789abc", host="shared"),
            dict(base, id="32345678-1234-1234-1234-123456789abc", host="remote"),
            dict(base, id="42345678-1234-1234-1234-123456789abc", host="unknown"),
            dict(base, id="52345678-1234-1234-1234-123456789abc", live=False, host=""),
        ]
        self.relay.agent({"info": self.info, "sessions": sessions}, now=1001)
        activity = {s["host"] or "history": s["activity"] for s in self.relay.overview(now=1002)["sessions"]}
        self.assertEqual(activity, {"cli": "active", "shared": "locked", "remote": "locked", "unknown": "locked", "history": "history"})

    def test_nonzero_terminal_exit_is_persisted_with_output(self):
        self.start()
        self.relay.agent({"info": self.info,
                           "terminals": [{"id": self.terminal, "state": "closed", "exit_code": 7}],
                           "output": [{"terminal": self.terminal, "seq": 1, "data": "startup failed"}]}, now=1004)
        self.relay._kept = None
        record = self.relay.overview(self.terminal, now=1005)
        self.assertEqual(record["terminal"]["exit_code"], 7)
        self.assertIn("代码 7", record["terminal"]["error"])
        self.assertEqual(record["chunks"][0]["data"], "startup failed")

    def test_start_retry_and_input_delivery_are_idempotent_and_ordered(self):
        payload = self.start()
        self.assertEqual(len(self.relay.overview(now=1004)["terminals"]), 1)
        self.assertEqual(self.relay.command(payload, now=1004)["state"], "done")
        for n, data in enumerate(("/model", "\r", "\x1b[A", "中文\r"), 1):
            op = {"action": "input", "id": "%032x" % n, "terminal": self.terminal, "data": data}
            self.relay.command(op, now=1005)
            self.relay.command(op, now=1006)
        operations = self.relay.agent({"info": self.info}, now=1007)["operations"]
        self.assertEqual([o["data"] for o in operations], ["/model", "\r", "\x1b[A", "中文\r"])
        self.assertEqual(self.relay.agent({"info": self.info}, now=1008)["operations"], operations)
        self.relay.agent({"info": self.info, "acks": [{"id": o["id"]} for o in operations]}, now=1009)
        self.assertEqual(self.relay.agent({"info": self.info}, now=1010)["operations"], [])

    def test_output_retry_split_utf8_terminal_controls_and_reconnect(self):
        self.start()
        output = [{"terminal": self.terminal, "seq": i, "data": text} for i, text in enumerate(("\x1b[2J\x1b[H", "中文 /model", "\r\n完成"), 1)]
        report = {"info": self.info, "output": output}
        for at in (1004, 1007):  # the repeated report also writes the output that was held back for a moment
            self.assertEqual(self.relay.agent(report, now=at)["output_ack"][self.terminal], 3)
        replay = self.relay.overview(self.terminal, now=1005)
        self.assertEqual([c["data"] for c in replay["chunks"]], [c["data"] for c in output])
        self.assertEqual(self.relay.overview(self.terminal, after=2, now=1005)["chunks"], [replay["chunks"][2]])
        self.assertEqual(self.relay.overview(self.terminal, after=3, now=1005)["chunks"], [])
        self.relay._kept = None
        self.assertEqual(self.relay.overview(self.terminal, now=1005)["chunks"], replay["chunks"])

    def test_restart_closes_old_terminal_and_expired_input_is_never_replayed(self):
        self.start()
        self.relay.command({"action": "input", "id": "c" * 32, "terminal": self.terminal, "data": "danger\r"}, now=1004)
        self.assertEqual(self.relay.agent({"info": self.info}, now=1300)["operations"], [])
        new_info = dict(self.info, instance="d" * 32)
        self.relay.agent({"info": new_info}, now=1301)
        self.assertEqual(self.relay.overview(self.terminal, now=1302)["terminal"]["state"], "closed")

    def test_unreceived_start_expiry_and_server_restart_release_capacity(self):
        payload = {"action": "start", "id": "a" * 32, "tool": "codex", "dir": "demo"}
        terminal = self.relay.command(payload, now=1001)["terminal"]
        self.relay.overview(now=1300)
        self.relay._kept = None
        self.assertEqual(self.relay.overview(terminal, now=1301)["terminal"]["state"], "closed")
        self.relay.agent({"info": self.info}, now=1302)
        second = self.relay.command(dict(payload, id="c" * 32), now=1303)["terminal"]
        self.relay._kept = None
        self.relay._pending.clear()
        self.assertEqual(self.relay.overview(second, now=1304)["terminal"]["state"], "closed")
        self.assertEqual(self.relay.agent({"info": self.info}, now=1305)["operations"], [])

    def test_invalid_tool_workspace_input_size_and_operation_id_are_refused(self):
        for payload in ([], {"action": "start", "id": "bad"}, {"action": "start", "id": "f" * 32, "tool": "bash", "dir": "demo"},
                        {"action": "start", "id": "f" * 32, "tool": "codex", "dir": "elsewhere"}):
            with self.assertRaises(RemoteError):
                self.relay.command(payload, now=1001)
        payload = self.start()
        with self.assertRaises(RemoteError):
            self.relay.command(dict(payload, tool="claude"), now=1004)
        for changes in ({"action": "input", "data": ""}, {"action": "input", "data": "x" * 16001},
                        {"action": "resize", "cols": 1, "rows": 20}, {"action": "resize", "cols": "80", "rows": 24}):
            with self.assertRaises(RemoteError):
                self.relay.command({"id": "e" * 32, "terminal": self.terminal, **changes}, now=1005)
        with self.assertRaises(RemoteError):
            self.relay.command({"id": "e" * 32, "terminal": self.terminal, "action": "close"}, now=2000)

    def test_close_error_and_cap_do_not_drop_existing_terminals(self):
        self.start()
        op = {"id": "c" * 32, "terminal": self.terminal, "action": "close"}
        self.relay.command(op, now=1004)
        self.relay.agent({"info": self.info, "acks": [{"id": op["id"]}], "terminals": [{"id": self.terminal, "state": "closed"}]}, now=1005)
        self.assertEqual(self.relay.overview(self.terminal, now=1006)["terminal"]["state"], "closed")
        for n in range(8):
            self.relay.command({"id": "%032x" % n, "action": "start", "tool": "claude", "dir": "demo"}, now=1007)
        with self.assertRaises(RemoteError):
            self.relay.command({"id": "d" * 32, "action": "start", "tool": "claude", "dir": "demo"}, now=1008)

    def test_new_terminal_sorts_above_second_precision_history(self):
        old = {"action": "start", "id": "1" * 32, "tool": "shell", "dir": "demo"}
        self.relay.command(old, now=1000)
        self.relay.agent({"info": self.info, "acks": [{"id": old["id"]}]}, now=1001)
        new = {"action": "start", "id": "2" * 32, "tool": "shell", "dir": "demo"}
        new_terminal = self.relay.command(new, now=1001.2)["terminal"]
        self.assertEqual(self.relay.overview(now=1002)["terminals"][0]["id"], new_terminal)

    def test_saved_conversations_are_listed_named_and_opened_once(self):
        sid, other = "12345678-1234-1234-1234-123456789abc", "22345678-1234-1234-1234-123456789abc"
        sessions = [{"id": sid, "tool": "claude", "dir": "demo", "title": " 修复面板 ", "updated": 5000, "live": True, "status": "busy"},
                    {"id": other, "tool": "codex", "dir": "demo", "title": "旧对话", "updated": 4000},
                    {"id": "bad", "tool": "claude", "dir": "demo", "title": "x", "updated": 1}, {"id": other, "tool": "bash", "dir": "demo", "title": "x", "updated": 1}]
        self.relay.agent({"info": self.info, "sessions": sessions}, now=1001)
        listed = self.relay.overview(now=1002)["sessions"]
        self.assertEqual([(s["id"], s["title"], s["live"], s["status"], s["terminal"]) for s in listed], [(sid, "修复面板", True, "busy", ""), (other, "旧对话", False, "", "")])
        for wrong in ({"session": sid, "tool": "claude", "takeover": "yes"}, {"session": sid, "tool": "codex"}, {"session": "12345678-1234-1234-1234-123456789abd", "tool": "claude"}, {"session": 5, "tool": "claude"}):
            with self.assertRaises(RemoteError):
                self.relay.command({"action": "start", "id": "1" * 32, "dir": "demo", **wrong}, now=1003)
        start = {"action": "start", "id": "2" * 32, "tool": "claude", "dir": "demo", "session": sid, "takeover": True}
        terminal = self.relay.command(start, now=1004)["terminal"]
        self.assertEqual(self.relay.agent({"info": self.info, "sessions": sessions}, now=1005)["operations"][0]["takeover"], True)
        with self.assertRaises(RemoteError):
            self.relay.command(dict(start, id="3" * 32), now=1006)
        self.relay.agent({"info": self.info, "sessions": sessions, "acks": [{"id": start["id"]}],
                             "terminals": [{"id": terminal, "state": "running", "session": other, "status": "idle"}]}, now=1007)
        view = self.relay.overview(now=1008)
        self.assertEqual((view["terminals"][0]["title"], view["terminals"][0]["status"]), ("旧对话", "idle"))
        self.assertEqual([s["terminal"] for s in view["sessions"]], ["", terminal])
        self.relay.command({"action": "rename", "id": "4" * 32, "terminal": terminal, "title": "我的名字"}, now=1009)
        self.assertEqual(self.relay.overview(terminal, now=1010)["terminal"]["title"], "我的名字")

    def test_ended_terminal_keeps_only_its_final_output_and_long_output_arrives_whole(self):
        self.start()
        pieces = [{"terminal": self.terminal, "seq": i, "data": chr(64 + i % 26) * 12000} for i in range(1, 61)]
        for at in range(0, 60, 30):
            self.relay.agent({"info": self.info, "output": pieces[at:at + 30]}, now=1004)
        after, received, replies = 0, [], 0
        while True:
            reply = self.relay.overview(self.terminal, after=after, now=1005)
            if not reply["chunks"]:
                break
            self.assertFalse(reply["reset"])
            received += [c["data"] for c in reply["chunks"]]
            after, replies = reply["after"], replies + 1
        self.assertEqual((received, after, reply["terminal"]["seq"]), ([p["data"] for p in pieces], 60, 60))
        self.assertGreater(replies, 3)
        self.relay.agent({"info": self.info, "terminals": [{"id": self.terminal, "state": "closed"}],
                             "output": [{"terminal": self.terminal, "seq": 61, "data": "最后一行"}]}, now=1006)
        self.relay._kept = None
        kept = self.relay.overview(self.terminal, after=60, now=1007)
        self.assertEqual(([c["data"] for c in kept["chunks"]], kept["terminal"]["state"]), (["最后一行"], "closed"))
        stored = self.relay._state()["threads"][self.terminal]
        self.assertLessEqual(stored["size"], tr.CLOSED_LIMIT)
        self.assertEqual(stored["output"][-1]["data"], "最后一行")
        self.assertTrue(self.relay.overview(self.terminal, after=3, now=1008)["reset"])
    def test_projects_are_listed_and_changed_only_through_the_computer(self):
        info = dict(self.info, workspaces=["demo", "组会"], projects=[
            {"name": "demo", "path": "E:\\01\\demo", "fixed": True, "exists": True}, {"name": "组会", "path": "G:\\06 数据分析", "fixed": False, "exists": True},
            {"name": "", "path": "x"}, "bad"], candidates=[{"name": "EpiAgentKit", "path": "E:\\05\\EpiAgentKit", "updated": 7, "live": True, "tools": "claude codex shell", "kinds": ["Codex 桌面版", "终端", 5, ""]}]
            + [{"name": "more%d" % at, "path": "E:\\more\\%d" % at, "kinds": "终端"} for at in range(60)])
        self.relay.agent({"info": info}, now=1001)
        device = self.relay.overview(now=1002)["device"]
        self.assertEqual([(x["name"], x["fixed"]) for x in device["projects"]], [("demo", True), ("组会", False)])
        self.assertEqual((device["candidates"][0]["tools"], device["candidates"][0]["live"]), (["claude", "codex"], True))
        # by what a folder is used, in the computer's words; forty of them are passed on, for the phone to search in
        self.assertEqual((device["candidates"][0]["kinds"], device["candidates"][1]["kinds"], len(device["candidates"])), (["Codex 桌面版", "终端"], [], 40))
        for bad in ({"action": "project_add", "path": ""}, {"action": "project_add", "path": "E:\\a", "create": "yes"}, {"action": "project_remove", "name": "没有"},
                    {"action": "project_remove", "name": "demo"}, {"action": "project_rename", "name": "组会", "to": " "}, {"action": "project_rename", "name": "组会", "to": "x" * 41}):
            with self.assertRaises(RemoteError):
                self.relay.command(dict(bad, id="1" * 32), now=1003)
        started = self.relay.command({"action": "start", "id": "2" * 32, "tool": "shell", "dir": "组会"}, now=1004)["terminal"]
        rename = {"action": "project_rename", "id": "3" * 32, "name": "组会", "to": "周会"}
        self.assertEqual(self.relay.command(rename, now=1005)["state"], "queued")
        sent = [o for o in self.relay.agent({"info": info}, now=1006)["operations"] if o["action"] == "project_rename"]
        self.assertEqual((sent[0]["name"], sent[0]["to"], sent[0]["terminal"]), ("组会", "周会", ""))
        self.relay.agent({"info": info, "acks": [{"id": rename["id"]}]}, now=1007)
        self.assertEqual(self.relay.command(rename, now=1008)["state"], "done")
        self.assertEqual(self.relay.overview(started, now=1009)["terminal"]["dir"], "周会")
        # one that stands in the computer's own settings is changed from the phone where the computer says it does that
        self.relay.agent({"info": dict(info, features=["projects", "files"])}, now=1009)
        remove = {"action": "project_remove", "id": "5" * 32, "name": "demo"}
        self.assertEqual(self.relay.command(remove, now=1009)["state"], "queued")
        self.relay.agent({"info": info, "acks": [{"id": remove["id"]}]}, now=1009)
        with self.assertRaisesRegex(RemoteError, "请在电脑上修改"):
            self.relay.command(dict(remove, id="6" * 32), now=1009)
        add = {"action": "project_add", "id": "4" * 32, "path": "E:\\没有"}
        self.relay.command(add, now=1010)
        self.relay.agent({"info": info, "acks": [{"id": add["id"], "error": "电脑上没有这个文件夹"}]}, now=1011)
        self.assertEqual(self.relay.command(add, now=1012)["error"], "电脑上没有这个文件夹")
    def test_waiting_requests_answer_when_something_arrives(self):
        import threading
        import time
        self.start()
        instance = self.info["instance"]
        self.relay.agent({"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]})  # reported just now
        self.assertEqual(self.relay.pull({"instance": instance, "wait": 0}), {"operations": []})
        with self.assertRaises(RemoteError):
            self.relay.pull({"instance": "x"})
        answers = {}
        waiting = threading.Thread(target=lambda: answers.update(pull=self.relay.pull({"instance": instance, "wait": 10})))
        began = time.monotonic()
        waiting.start()
        time.sleep(0.2)
        self.relay.command({"action": "input", "id": "1" * 32, "terminal": self.terminal, "data": "x"})
        waiting.join(5)
        self.assertEqual([(o["action"], o["data"], o["terminal"]) for o in answers["pull"]["operations"]], [("input", "x", self.terminal)])
        self.assertLess(time.monotonic() - began, 3)
        self.assertEqual(len(self.relay.agent({"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]})["operations"]), 1)
        self.assertEqual(self.relay.pull({"instance": instance, "wait": 0}), {"operations": []})
        self.assertEqual(self.relay.pull({"instance": "c" * 32, "wait": 0}), {"operations": []})

        reading = threading.Thread(target=lambda: answers.update(read=self.relay.overview(self.terminal, after=0, wait=10)))
        began = time.monotonic()
        reading.start()
        time.sleep(0.2)
        self.relay.agent({"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}], "output": [{"terminal": self.terminal, "seq": 1, "data": "回显"}]})
        reading.join(5)
        self.assertEqual([c["data"] for c in answers["read"]["chunks"]], ["回显"])
        self.assertLess(time.monotonic() - began, 3)
        began = time.monotonic()
        self.assertEqual(self.relay.overview(self.terminal, after=1, wait=0.4)["chunks"], [])
        self.assertGreaterEqual(time.monotonic() - began, 0.35)
        changing = threading.Thread(target=lambda: answers.update(state=self.relay.overview(self.terminal, after=1, wait=10)))
        changing.start()
        time.sleep(0.2)
        self.relay.agent({"info": self.info, "terminals": [{"id": self.terminal, "state": "running", "status": "busy"}]})
        changing.join(5)
        self.assertEqual(answers["state"]["terminal"]["status"], "busy")

    def test_output_is_saved_in_intervals_and_a_gap_after_a_restart_does_not_block(self):
        self.start()
        report = lambda seq, now: self.relay.agent({"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}],
                                                      "output": [{"terminal": self.terminal, "seq": seq, "data": "片%d" % seq}]}, now=now)["output_ack"][self.terminal]
        self.assertEqual(report(1, 2000.0), 1)
        self.assertEqual(report(2, 2000.5), 2)
        self.assertEqual(report(3, 2002.5), 3)
        self.assertEqual(report(4, 2002.6), 4)
        self.relay._kept = None
        self.relay._unsaved = None
        self.assertEqual(self.relay.overview(self.terminal, now=2003)["terminal"]["seq"], 3)
        self.assertEqual(report(4, 2003.1), 4)
        self.assertEqual(report(9, 2003.2), 9)
        self.assertEqual(report(9, 2003.3), 9)
        self.assertEqual([c["data"] for c in self.relay.overview(self.terminal, after=3, now=2004)["chunks"]], ["片4", "片9"])
    def test_saving_adds_only_new_output_and_survives_a_restart_an_old_list_and_a_cut_line(self):
        import json
        import os
        self.start()
        live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]}
        report = lambda seq, now: self.relay.agent(dict(live, output=[{"terminal": self.terminal, "seq": seq, "data": "片%d\n" % seq}]), now=now)
        file = os.path.join(tr._folder(self.path), self.terminal + ".jsonl")
        def read(name):
            with open(name, encoding="utf-8") as stream:
                return stream.read()
        report(1, 3000.0)
        report(2, 3002.5)
        before = read(file)
        self.assertNotIn("output", json.loads(read(self.path))["threads"][self.terminal])
        saved = []
        write = tr._write
        tr._write = lambda path, text, mode="w": (saved.append((os.path.basename(path), mode, text)), write(path, text, mode))[1]
        self.addCleanup(setattr, tr, "_write", write)
        report(3, 3005.0)
        self.relay._save(self.relay._state())
        self.assertEqual([s for s in saved if s[0].startswith(self.terminal)], [(self.terminal + ".jsonl", "a", '{"seq": 3, "data": "片3\\n"}\n')])
        self.assertEqual(read(file), before + '{"seq": 3, "data": "片3\\n"}\n')
        restarted = lambda: (setattr(self.relay, "_kept", None), setattr(self.relay, "_unsaved", None), [c["data"] for c in self.relay.overview(self.terminal, now=3006)["chunks"]])[2]
        self.assertEqual(restarted(), ["片1\n", "片2\n", "片3\n"])
        with open(file, "a", encoding="utf-8") as stream:
            stream.write('{"seq": 4, "da')                       # the relay was ended in the middle of a line
        self.assertEqual(restarted(), ["片1\n", "片2\n", "片3\n"])
        report(4, 3010.0)
        self.relay._save(self.relay._state())
        self.assertEqual(restarted(), ["片1\n", "片2\n", "片3\n", "片4\n"])
        # A list written by an earlier version holds the output itself; it is read and then kept the new way.
        state = json.loads(read(self.path))
        state["threads"][self.terminal]["output"] = [{"seq": 1, "data": "旧"}, {"seq": 4, "data": "的"}]
        os.remove(file)
        with open(self.path, "w", encoding="utf-8") as stream:
            json.dump(state, stream, ensure_ascii=False)
        self.assertEqual(restarted(), ["旧", "的"])
        report(5, 3020.0)
        self.relay._save(self.relay._state())
        self.assertEqual(restarted(), ["旧", "的", "片5\n"])
        del self.relay._state()["threads"][self.terminal]
        self.relay._save(self.relay._state())
        self.assertFalse(os.path.exists(file))

    def test_a_long_history_does_not_slow_new_output(self):
        import time
        self.start()
        live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]}
        piece = "x" * 60 + "\r\n"
        for n in range(0, 30000, 200):
            self.relay.agent(dict(live, output=[{"terminal": self.terminal, "seq": n + i + 1, "data": piece} for i in range(200)]), now=4000 + n / 1000)
        began = time.perf_counter()
        for n in range(30000, 30100):
            self.relay.agent(dict(live, output=[{"terminal": self.terminal, "seq": n + 1, "data": piece}]), now=4100 + (n - 30000) * 0.05)
            self.assertEqual(len(self.relay.overview(self.terminal, after=n, now=4100 + (n - 30000) * 0.05)["chunks"]), 1)
        self.assertLess((time.perf_counter() - began) / 100, 0.005)      # it was a tenth of a second and more for each piece

    def test_finished_operations_do_not_slow_later_requests(self):
        import time
        self.start()
        live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]}
        for n in range(6000):       # a few minutes of scrolling a full-screen program
            key = "%032x" % (n + 1)
            self.relay.command({"action": "input", "id": key, "terminal": self.terminal, "data": "x"}, now=1004)
            self.relay.agent(dict(live, acks=[{"id": key}]), now=1004)
        self.assertEqual((len(self.relay._pending), len(self.relay._pending.queued)), (6001, 0))
        began = time.perf_counter()
        for n in range(200):
            self.relay.overview(self.terminal, after=0, now=1005)
        self.assertLess((time.perf_counter() - began) / 200, 0.0005)
        self.assertEqual(self.relay.command({"action": "input", "id": "%032x" % 1, "terminal": self.terminal, "data": "x"}, now=1006)["state"], "done")
        self.relay.overview(now=1004 + 601)
        self.assertEqual(len(self.relay._pending), 0)

    def test_a_question_is_shown_with_the_terminal_so_it_can_be_answered_from_the_list(self):
        self.start()
        live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]}
        asked = "\x1b[2J\x1b[H Bash command\r\n\r\n   rm -rf build\r\n\r\n Do you want to proceed?\r\n > 1. Yes\r\n   2. No\r\n"
        self.relay.agent(dict(live, output=[{"terminal": self.terminal, "seq": 1, "data": asked}]), now=1004)
        self.relay.agent(live, now=1007)         # quiet for a moment with the question on the screen
        shown = self.relay.overview(now=1007)["terminals"][0]
        self.assertEqual(shown["phase"], "confirm")
        self.assertEqual([line.strip() for line in shown["asks"]], ["Bash command", "", "rm -rf build", "", "Do you want to proceed?", "> 1. Yes", "2. No"])
        self.relay.agent(dict(live, output=[{"terminal": self.terminal, "seq": 2, "data": "building " * 150}]), now=1008)      # it went on
        self.relay.agent(live, now=1020)
        self.assertEqual(self.relay.overview(now=1020)["terminals"][0]["asks"], [])      # no question, nothing to answer

    def test_the_plain_terminal_is_called_what_the_computer_calls_it(self):
        self.assertEqual(self.relay.overview(now=1001)["device"].get("shell", ""), "")
        self.start()
        self.assertEqual(self.relay.overview(now=1004)["terminals"][0]["title"], "Codex")
        self.relay.agent({"info": dict(self.info, shell="bash\x07" + "x" * 40)}, now=1005)
        self.assertEqual(self.relay.overview(now=1005)["device"]["shell"], ("bash" + "x" * 40)[:24])
        self.relay.agent({"info": dict(self.info, shell="zsh")}, now=1006)
        shell = self.relay.command({"id": "d" * 32, "action": "start", "tool": "shell", "dir": "demo"}, now=1006)["terminal"]
        self.assertEqual(next(t for t in self.relay.overview(now=1006)["terminals"] if t["id"] == shell)["title"], "zsh")

    def test_a_conversation_is_read_by_the_computer_before_anything_is_done_to_it(self):
        import threading
        sid = "11111111-2222-3333-4444-555555555555"
        ask = {"id": "c" * 32, "session": sid}
        with self.assertRaisesRegex(RemoteError, "更新电脑端"):
            self.relay.conversation(ask, now=1001, wait=0)
        info = dict(self.info, features=["peek"])
        self.relay.agent({"info": info}, now=1001)
        with self.assertRaisesRegex(RemoteError, "没有找到这个对话"):
            self.relay.conversation(ask, now=1001, wait=0)
        self.relay.agent({"info": info, "sessions": [{"id": sid, "tool": "claude", "dir": "demo", "title": "原对话", "updated": 1, "live": True, "host": "cli"}]}, now=1001)
        for bad in (dict(ask, session="x"), dict(ask, id="x"), dict(ask, session=None)):
            with self.assertRaises(RemoteError):
                self.relay.conversation(bad, now=1001, wait=0)
        answers = {}
        waiting = threading.Thread(target=lambda: answers.update(got=self.relay.conversation(ask, now=1001, wait=5)))
        waiting.start()
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
        # the tool and the folder are what the computer reported, not what the viewer says
        self.assertEqual([(o["action"], o["session"], o["tool"], o["dir"]) for o in asked], [("session_read", sid, "claude", "demo")])
        said = [{"role": "user", "text": "修一下登录"}, {"role": "assistant", "text": "好的"}]
        self.relay.agent({"info": info, "acks": [{"id": ask["id"], "error": "", "result": {"messages": said, "more": False}}]}, now=1002)
        waiting.join(5)
        self.assertEqual(answers["got"]["messages"], said)
        self.assertNotIn(ask["id"], self.relay._pending)        # nothing of it is kept
        self.assertNotIn("since", asked[0])
        # a page that follows it says what it has, in the computer's own word; the computer answers that it is the same
        again = dict(ask, id="d" * 32, since="2048.638000000000000000")
        waiting = threading.Thread(target=lambda: answers.update(same=self.relay.conversation(again, now=1003, wait=5)))
        waiting.start()
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
        self.assertEqual((asked[0]["action"], asked[0]["since"]), ("session_read", "2048.638000000000000000"))
        self.relay.agent({"info": info, "acks": [{"id": again["id"], "error": "", "result": {"same": True, "stamp": again["since"], "status": "busy"}}]}, now=1004)
        waiting.join(5)
        self.assertEqual(answers["same"], {"same": True, "stamp": again["since"], "status": "busy"})
        # anything else in its place is not passed on
        waiting = threading.Thread(target=lambda: answers.update(odd=self.relay.conversation(dict(ask, id="e" * 32, since="x; rm"), now=1005, wait=5)))
        waiting.start()
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
        self.assertNotIn("since", asked[0])
        self.relay.agent({"info": info, "acks": [{"id": "e" * 32, "error": "", "result": {"messages": said}}]}, now=1006)
        waiting.join(5)
        # by what a conversation is used is passed on as the computer says it
        self.relay.agent({"info": info, "sessions": [{"id": sid, "tool": "claude", "dir": "demo", "title": "原对话", "updated": 1, "live": True, "host": "cli", "kind": "Claude 桌面版"}]}, now=1007)
        self.assertEqual([s["kind"] for s in self.relay.overview(now=1007)["sessions"]], ["Claude 桌面版"])

    def test_new_tool_conversations_can_be_read_and_marked(self):
        import threading
        info = dict(self.info, features=["peek", "marks"])
        for tool, sid in (("opencode", "ses_" + "a" * 26), ("cline", "1792116013202_ffehs")):
            self.relay.agent({"info": info, "sessions": [{"id": sid, "tool": tool, "dir": "demo", "title": tool, "updated": 1}]}, now=1001)
            for action in ("read", "mark"):
                with self.subTest(tool=tool, action=action):
                    ask = {"id": __import__("secrets").token_hex(16), "session": sid, "action": action}
                    if action == "mark":
                        ask["pinned"] = True
                    answers = {}
                    def request():
                        try:
                            answers["got"] = self.relay.conversation(ask, now=1001, wait=3)
                        except Exception as error:
                            answers["error"] = error
                    waiting = threading.Thread(target=request)
                    waiting.start()
                    operations = self.relay.pull({"instance": info["instance"], "wait": 1})["operations"]
                    if operations:
                        self.relay.agent({"info": info, "acks": [{"id": ask["id"], "result": {"ok": True}}]}, now=1002)
                    waiting.join(4)
                    self.assertFalse(waiting.is_alive())
                    self.assertNotIn("error", answers)
                    self.assertEqual([(op["action"], op["tool"], op["session"]) for op in operations], [("session_" + action, tool, sid)])
                    self.assertEqual(answers["got"], {"ok": True})
        with self.assertRaises(RemoteError):
            self.relay.conversation({"id": "f" * 32, "session": "ses_../invalid"}, now=1003, wait=0)

    def test_a_conversation_is_pinned_or_hidden_from_the_phone_and_the_computer_keeps_it(self):
        import threading
        sid = "11111111-2222-3333-4444-555555555555"
        saved = {"id": sid, "tool": "claude", "dir": "demo", "title": "常用的对话", "updated": 1, "pinned": "yes"}
        self.relay.agent({"info": dict(self.info, features=["peek"]), "sessions": [saved]}, now=1001)
        self.assertEqual([(s["pinned"], s["hidden"]) for s in self.relay.overview(now=1001)["sessions"]], [(False, False)])       # only a plain yes counts
        ask = lambda **more: dict({"id": "c" * 32, "session": sid, "action": "mark"}, **more)
        with self.assertRaisesRegex(RemoteError, "不支持置顶"):
            self.relay.conversation(ask(pinned=True), now=1001, wait=0)
        info = dict(self.info, features=["peek", "marks", "something-else"])
        self.relay.agent({"info": info}, now=1001)
        self.assertEqual(self.relay.overview(now=1001)["device"]["features"], ["peek", "marks"])
        for bad in (ask(), ask(pinned="yes"), ask(pinned=1), ask(hidden=None)):
            with self.assertRaisesRegex(RemoteError, "标记无效"):
                self.relay.conversation(bad, now=1001, wait=0)
        with self.assertRaisesRegex(RemoteError, "没有找到这个对话"):
            self.relay.conversation(ask(pinned=True, session="66666666-7777-8888-9999-000000000000"), now=1001, wait=0)
        answers = {}
        waiting = threading.Thread(target=lambda: answers.update(got=self.relay.conversation(ask(pinned=True, other="x"), now=1001, wait=5)))
        waiting.start()
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
        self.assertEqual([{k: v for k, v in o.items() if k not in ("at", "terminal")} for o in asked], [{"id": "c" * 32, "action": "session_mark", "session": sid, "tool": "claude", "dir": "demo", "pinned": True}])
        self.relay.agent({"info": info, "acks": [{"id": "c" * 32, "error": "", "result": {}}]}, now=1002)
        waiting.join(5)
        self.assertEqual(answers["got"], {})
        # it is said at once, before the computer has listed its conversations again; and as the computer says from then on
        self.assertEqual([(s["pinned"], s["hidden"]) for s in self.relay.overview(now=1002)["sessions"]], [(True, False)])
        self.relay.agent({"info": info, "sessions": [dict(saved, pinned=False, hidden=True)]}, now=1003)
        self.assertEqual([(s["pinned"], s["hidden"]) for s in self.relay.overview(now=1003)["sessions"]], [(False, True)])

    def test_a_saved_conversation_is_put_away_by_the_computer_and_leaves_the_list(self):
        import threading
        kept, open_, apart = "11111111-2222-3333-4444-555555555555", "22222222-2222-3333-4444-555555555555", "33333333-2222-3333-4444-555555555555"
        listed = [{"id": kept, "tool": "codex", "dir": "demo", "title": "旧的讨论", "updated": 1},
                  {"id": open_, "tool": "claude", "dir": "demo", "title": "开着的", "updated": 2, "live": True, "host": "cli"},
                  {"id": apart, "tool": "codex", "dir": "", "folder": "D:\\notes", "title": "项目之外的", "updated": 3, "live": True, "host": "cli"}]
        ask = lambda session, n: {"id": n * 32, "session": session, "action": "delete"}
        self.relay.agent({"info": dict(self.info, features=["peek", "marks"]), "sessions": listed}, now=1001)
        with self.assertRaisesRegex(RemoteError, "不支持删除对话"):
            self.relay.conversation(ask(kept, "a"), now=1001, wait=0)
        info = dict(self.info, features=["peek", "marks", "delete"])
        self.relay.agent({"info": info}, now=1001)
        # not one a program has open, not one outside the projects, not one a terminal here runs
        for refused in (open_, apart):
            with self.assertRaisesRegex(RemoteError, "正开着"):
                self.relay.conversation(ask(refused, "b"), now=1001, wait=0)
        with self.assertRaisesRegex(RemoteError, "没有找到这个对话"):
            self.relay.conversation(ask("66666666-7777-8888-9999-000000000000", "b"), now=1001, wait=0)
        answers = {}
        waiting = threading.Thread(target=lambda: answers.update(got=self.relay.conversation(ask(kept, "c"), now=1001, wait=5)))
        waiting.start()
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
        self.assertEqual([{k: v for k, v in o.items() if k not in ("at", "terminal")} for o in asked], [{"id": "c" * 32, "action": "session_delete", "session": kept, "tool": "codex", "dir": "demo"}])
        self.relay.agent({"info": info, "acks": [{"id": "c" * 32, "error": "", "result": {}}]}, now=1002)
        waiting.join(5)
        self.assertEqual(answers["got"], {})
        # it is gone from the list at once, before the computer has listed its conversations again
        self.assertEqual([s["id"] for s in self.relay.overview(now=1002)["sessions"]], [open_, apart])
        # the computer's lists are what counts from then on: one it still lists is listed
        self.relay.agent({"info": info, "sessions": listed}, now=1003)
        self.assertIn(kept, [s["id"] for s in self.relay.overview(now=1003)["sessions"]])

    def test_a_conversation_open_on_the_computer_is_ended_there_by_the_computer(self):
        import threading
        kept, open_ = "11111111-2222-3333-4444-555555555555", "22222222-2222-3333-4444-555555555555"
        listed = [{"id": kept, "tool": "codex", "dir": "demo", "title": "旧的讨论", "updated": 1},
                  {"id": open_, "tool": "claude", "dir": "", "folder": "D:\\notes", "title": "开着的", "updated": 2, "live": True, "host": "cli", "can_takeover": True}]
        ask = lambda session, n: {"id": n * 32, "session": session, "action": "end"}
        self.relay.agent({"info": dict(self.info, features=["peek", "reach"]), "sessions": listed}, now=1001)
        with self.assertRaisesRegex(RemoteError, "不支持在手机上结束"):
            self.relay.conversation(ask(open_, "a"), now=1001, wait=0)
        info = dict(self.info, features=["peek", "reach", "end"])
        self.relay.agent({"info": info}, now=1001)
        self.assertIn("end", self.relay.overview(now=1001)["device"]["features"])
        # only one that a program has open
        with self.assertRaisesRegex(RemoteError, "没有开着"):
            self.relay.conversation(ask(kept, "b"), now=1001, wait=0)
        answers = {}
        waiting = threading.Thread(target=lambda: answers.update(got=self.relay.conversation(ask(open_, "c"), now=1001, wait=5)))
        waiting.start()
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
        self.assertEqual([{k: v for k, v in o.items() if k not in ("at", "terminal")} for o in asked], [{"id": "c" * 32, "action": "session_end", "session": open_, "tool": "claude", "dir": ""}])
        self.relay.agent({"info": info, "acks": [{"id": "c" * 32, "error": "", "result": {}}]}, now=1002)
        waiting.join(5)
        self.assertEqual(answers["got"], {})

    def test_native_desktop_control_preserves_target_and_queries_while_busy(self):
        import threading
        sid = "11111111-2222-3333-4444-555555555555"
        info = dict(self.info, features=["peek", "reach", "codex-desktop-control"])
        session = dict(id=sid, tool="codex", dir="demo", title="原应用对话", updated=1, live=True,
                       status="waiting", reach=["say"], transport="codex-desktop", turn="")
        self.relay.agent({"info": info, "sessions": [session]}, now=1001)
        payload = dict(id="c" * 32, session=sid, action="say", text="中文", transport="codex-desktop")

        def roundtrip(request, result):
            answers = {}
            thread = threading.Thread(target=lambda: answers.update(got=self.relay.conversation(request, now=1002, wait=5)))
            thread.start()
            ops = self.relay.pull({"instance": info["instance"], "wait": 3})["operations"]
            self.assertEqual(len(ops), 1)
            self.relay.agent({"info": info, "acks": [{"id": request["id"], "result": result}]}, now=1003)
            thread.join(5)
            self.assertEqual(answers["got"], result)
            return ops[0]

        op = roundtrip(payload, {"status": "accepted", "turn": "turn-1", "sent": True})
        self.assertEqual((op["action"], op["transport"], op["text"], op["receipt"]), ("session_say", "codex-desktop", "中文", False))
        session.update(status="busy", reach=["stop"], turn="turn-1")
        self.relay.agent({"info": info, "sessions": [session]}, now=1003)
        with self.assertRaises(RemoteError):
            self.relay.conversation(dict(payload, id="d" * 32), now=1003, wait=0)
        op = roundtrip(dict(payload, receipt=True), {"status": "accepted", "turn": "turn-1", "sent": True})
        self.assertTrue(op["receipt"])
        stop = dict(id="e" * 32, session=sid, action="stop", transport="codex-desktop", turn="turn-1")
        op = roundtrip(stop, {"status": "confirmed", "turn": "turn-1"})
        self.assertEqual((op["action"], op["turn"]), ("session_stop", "turn-1"))
        for invalid in [dict(stop, transport=""), dict(stop, turn=""), dict(stop, receipt="true")]:
            with self.assertRaises(RemoteError):
                self.relay.conversation(invalid, now=1004, wait=0)
        session.update(transport="", reach=[], turn="")
        self.relay.agent({"info": info, "sessions": [session]}, now=1004)
        op = roundtrip(dict(stop, receipt=True), {"status": "unknown", "sent": None})
        self.assertTrue(op["receipt"])
        info["features"].remove("codex-desktop-control")
        self.relay.agent({"info": info}, now=1004)
        with self.assertRaises(RemoteError):
            self.relay.conversation(dict(stop, receipt=True), now=1004, wait=0)

    def test_native_stop_capability_requires_matching_application(self):
        from relay import _reached
        base = dict(live=True, tool="codex", reach=["stop"], transport="codex-desktop", turn="turn-1")
        self.assertEqual(_reached(base)["reach"], ["stop"])
        for change in [dict(live=False), dict(tool="claude"), dict(transport="other"), dict(turn="../bad")]:
            self.assertEqual(_reached(dict(base, **change))["reach"], [])

    def test_a_conversation_at_work_is_told_to_stop_through_its_tools_hooks(self):
        import threading
        sid, other = "11111111-2222-3333-4444-555555555555", "66666666-7777-8888-9999-000000000000"
        info = dict(self.info, features=["peek", "reach"])
        busy = {"id": sid, "tool": "claude", "dir": "demo", "title": "正在做的", "updated": 1, "live": True, "host": "cli", "status": "busy", "reach": ["say", "stop"], "transport": "hook"}
        # "stop" is passed on where the computer says how: by its tool's hooks here; not for one that names no way
        self.relay.agent({"info": info, "sessions": [busy, dict(busy, id=other, transport="")]}, now=1001)
        shown = {s["id"]: s for s in self.relay.overview(now=1001)["sessions"]}
        self.assertEqual([(shown[sid]["reach"], shown[sid]["transport"]), (shown[other]["reach"], shown[other]["transport"])], [(["say", "stop"], "hook"), (["say"], "")])
        with self.assertRaisesRegex(RemoteError, "没有可用的停止接口"):
            self.relay.conversation({"id": "a" * 32, "session": sid, "action": "stop"}, now=1001, wait=0)
        with self.assertRaisesRegex(RemoteError, "没有在执行"):
            self.relay.conversation({"id": "b" * 32, "session": other, "action": "stop", "transport": "hook"}, now=1001, wait=0)
        answers = {}
        waiting = threading.Thread(target=lambda: answers.update(got=self.relay.conversation({"id": "c" * 32, "session": sid, "action": "stop", "transport": "hook"}, now=1001, wait=5)))
        waiting.start()
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
        self.assertEqual([{k: v for k, v in o.items() if k not in ("at", "terminal")} for o in asked], [{"id": "c" * 32, "action": "session_stop", "session": sid, "tool": "claude", "dir": "demo"}])
        self.relay.agent({"info": info, "acks": [{"id": "c" * 32, "error": "", "result": {"status": "asked", "transport": "hook"}}]}, now=1002)
        waiting.join(5)
        self.assertEqual(answers["got"], {"status": "asked", "transport": "hook"})

    def test_a_conversation_that_sits_idle_is_told_where_it_is_and_the_answer_is_waited_for(self):
        import threading
        sid, other = "11111111-2222-3333-4444-555555555555", "66666666-7777-8888-9999-000000000000"
        info = dict(self.info, features=["peek", "reach"])
        idle = {"id": sid, "tool": "claude", "dir": "demo", "title": "桌面版里闲置的对话", "updated": 1, "live": True, "now": False, "host": "cli", "status": "idle", "reach": ["say"], "wake": True}
        self.relay.agent({"info": info, "sessions": [idle, dict(idle, id=other, reach=[], wake=True), dict(idle, id=sid[:-1] + "6", reach=None, wake=True)]}, now=1001)
        shown = {s["id"]: s for s in self.relay.overview(now=1001)["sessions"]}
        # it can be woken only where it can be told at all; it is still no conversation that is going on
        self.assertEqual([(s["reach"], s["wake"], s["activity"]) for s in shown.values()], [(["say"], True, "locked"), ([], False, "locked"), (None, False, "locked")])
        answers = {}
        waiting = threading.Thread(target=lambda: answers.update(got=self.relay.conversation({"id": "c" * 32, "session": sid, "action": "say", "text": "把结论再写短一点"}, now=1001, wait=5)))
        waiting.start()
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
        self.assertEqual([(o["action"], o["session"], o["tool"], o["text"]) for o in asked], [("session_say", sid, "claude", "把结论再写短一点")])
        self.relay.agent({"info": info, "acks": [{"id": "c" * 32, "error": "", "result": {"now": True, "woke": True}}]}, now=1002)
        waiting.join(5)
        self.assertEqual(answers["got"], {"now": True, "woke": True})
        self.assertGreater(tr.WAKE_SECONDS, tr.FILE_SECONDS)

    def test_a_conversation_a_program_holds_without_using_it_is_not_one_that_is_going_on(self):
        ids = ["11111111-2222-3333-4444-55555555555%d" % n for n in range(5)]
        held = {"tool": "claude", "dir": "demo", "title": "桌面版加载过的对话", "updated": 1, "live": True, "host": "cli", "origin": "Claude 桌面版"}
        self.relay.agent({"info": dict(self.info, features=["peek", "reach"]), "sessions": [
            dict(held, id=ids[0], now=False), dict(held, id=ids[1], now=True), dict(held, id=ids[2]),          # a computer that does not say: in use
            dict(held, id=ids[3], now=False, reach=["say"], status="idle"), dict(held, id=ids[4], live=False, now=True)]}, now=1001)
        shown = {s["id"]: (s["activity"], s["now"], s["live"]) for s in self.relay.overview(now=1001)["sessions"]}
        self.assertEqual([shown[i] for i in ids], [("locked", False, True), ("active", True, True), ("active", True, True), ("locked", False, True), ("history", False, False)])
        # it is still held: going on with it elsewhere needs the program to let go, as before
        with self.assertRaisesRegex(RemoteError, "接管"):
            self.relay.command({"action": "start", "id": "d" * 32, "tool": "claude", "dir": "demo", "session": ids[0]}, now=1002)

    def test_a_conversation_open_in_a_folder_that_is_no_project_is_listed_with_its_folder(self):
        sid, other, third = "11111111-2222-3333-4444-555555555555", "66666666-7777-8888-9999-000000000000", "aaaaaaaa-7777-8888-9999-000000000000"
        info = dict(self.info, features=["peek", "reach"])
        outside = {"id": sid, "tool": "claude", "dir": "", "folder": "D:\\notes\\reading", "title": "读书笔记", "updated": 1, "live": True, "host": "cli", "reach": ["say"], "status": "waiting"}
        self.relay.agent({"info": info, "sessions": [outside, dict(outside, id=other, live=False), dict(outside, id=third, folder=""), {"id": third, "tool": "codex", "dir": "demo", "folder": "D:\\x", "title": "项目里的", "updated": 1}]}, now=1001)
        shown = self.relay.overview(now=1001)["sessions"]
        # one without a project is listed only with its folder; a project's own says no folder
        self.assertEqual([(s["id"], s["dir"], s["folder"], s["live"]) for s in shown], [(sid, "", "D:\\notes\\reading", True), (other, "", "D:\\notes\\reading", False), (third, "demo", "")][:2] + [(third, "demo", "", False)])
        self.assertEqual(shown[0]["activity"], "active")
        # It is taken on from the phone like any other: the terminal runs in the conversation's folder and has no
        # project. Which folder is what the computer said, whatever the viewer says.
        start = {"action": "start", "id": "d" * 32, "tool": "claude", "dir": "", "session": sid, "takeover": True, "folder": "C:\\Windows"}
        with self.assertRaisesRegex(RemoteError, "接管"):
            self.relay.command(dict(start, takeover=False), now=1002)
        started = self.relay.command(start, now=1002)
        self.assertEqual(self.relay.command(start, now=1003), started)           # the same request again is the same one
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 0})["operations"]
        self.assertEqual([(o["action"], o["dir"], o["folder"], o["session"], o["takeover"]) for o in asked], [("start", "", "D:\\notes\\reading", sid, True)])
        terminal = self.relay.overview(started["terminal"], now=1003)["terminal"]
        self.assertEqual((terminal["dir"], terminal["folder"], terminal["session"], terminal["title"]), ("", "D:\\notes\\reading", sid, "读书笔记"))
        # the computer lists the conversation as that terminal's from then on, and it is no longer one that a program has open
        self.relay.agent({"info": info, "acks": [{"id": start["id"]}], "terminals": [{"id": started["terminal"], "state": "running"}], "sessions": [dict(outside, live=False, reach=None, status="")]}, now=1004)
        listed = self.relay.overview(now=1004)
        self.assertEqual([(s["id"], s["terminal"], s["live"]) for s in listed["sessions"]], [(sid, started["terminal"], False)])
        self.assertEqual([(t["dir"], t["folder"], t["title"]) for t in listed["terminals"]], [("", "D:\\notes\\reading", "读书笔记")])
        # going on from that terminal once it has ended: in the same folder
        self.relay.agent({"info": info, "terminals": [{"id": started["terminal"], "state": "closed"}], "sessions": []}, now=1005)
        self.assertEqual(self.relay.overview(now=1005)["terminals"][0]["title"], "读书笔记")          # it keeps the conversation's name
        again = {"action": "start", "id": "e" * 32, "tool": "claude", "dir": "", "previous": started["terminal"], "history": True}
        self.assertEqual(self.relay.overview(self.relay.command(again, now=1006)["terminal"], now=1006)["terminal"]["title"], "读书笔记")
        asked = [o for o in self.relay.pull({"instance": self.info["instance"], "wait": 0})["operations"] if o["id"] == again["id"]]
        self.assertEqual([(o["dir"], o["folder"], o["previous"]) for o in asked], [("", "D:\\notes\\reading", started["terminal"])])
        # nothing else is started where there is no project: not a new terminal, not a conversation the computer did not list there
        for bad in ({"tool": "shell"}, {"tool": "claude"}, {"tool": "claude", "session": third}, {"tool": "claude", "previous": "f" * 32, "history": True}, {"tool": "codex", "previous": started["terminal"], "history": True}):
            with self.assertRaises(RemoteError):
                self.relay.command(dict({"action": "start", "id": "9" * 32, "dir": ""}, **bad), now=1007)

    def test_a_terminal_outside_every_project_is_taken_as_the_computer_tells_of_it(self):
        info = dict(self.info, features=["peek"])
        told = {"id": "c" * 32, "state": "running", "tool": "codex", "dir": "", "folder": "D:\\notes\\reading", "created": 5000, "title": "", "session": "11111111-2222-3333-4444-555555555555"}
        self.relay.agent({"info": info, "terminals": [told, dict(told, id="d" * 32, folder=""), dict(told, id="e" * 32, dir="demo", folder="D:\\x")]}, now=1001)
        shown = {t["id"]: t for t in self.relay.overview(now=1001)["terminals"]}
        # one without a project and without a folder is nothing; a project's own says no folder
        self.assertEqual({key: (t["dir"], t["folder"]) for key, t in shown.items()}, {"c" * 32: ("", "D:\\notes\\reading"), "e" * 32: ("demo", "")})

    def test_a_conversation_open_on_the_computer_is_answered_and_told_from_the_phone(self):
        import threading
        sid, other = "11111111-2222-3333-4444-555555555555", "66666666-7777-8888-9999-000000000000"
        info = dict(self.info, features=["peek", "reach"])
        told = {"id": sid, "tool": "codex", "dir": "demo", "title": "桌面版里的对话", "updated": 1, "live": True, "host": "shared", "status": "confirm",
                "reach": ["say", "answer", "delete"], "ask": {"id": "ab12cd34ef56", "text": "Build it\nBash：cargo build" + "x" * 900}, "last": "y" * 500}
        plain = {"id": other, "tool": "claude", "dir": "demo", "title": "没有钩子的对话", "updated": 1, "live": True, "host": "shared", "status": "busy", "ask": {"id": "ab12cd34ef56", "text": "?"}}
        self.relay.agent({"info": info, "sessions": [told, plain]}, now=1001)
        shown = {s["id"]: s for s in self.relay.overview(now=1001)["sessions"]}
        # what its tool tells of it is passed on, cut to size; a conversation whose tool tells nothing has none of it
        self.assertEqual((shown[sid]["status"], shown[sid]["reach"], shown[sid]["ask"]["id"], len(shown[sid]["ask"]["text"]), len(shown[sid]["last"])), ("confirm", ["say", "answer"], "ab12cd34ef56", 600, 300))
        self.assertEqual(shown[sid]["activity"], "active")          # it is seen at work, whatever program runs it
        self.assertEqual((shown[other]["reach"], shown[other]["ask"], shown[other]["last"], shown[other]["activity"]), (None, None, "", "locked"))
        ask = lambda **more: dict({"id": "c" * 32, "session": sid}, **more)
        for scope in ("session", "*", [], None):
            with self.assertRaisesRegex(RemoteError, "授权范围无效"):
                self.relay.conversation(ask(action="answer", ask="ab12cd34ef56", allow=True, scope=scope), now=1001, wait=0)
        with self.assertRaisesRegex(RemoteError, "授权范围无效"):
            self.relay.conversation(ask(action="answer", ask="ab12cd34ef56", allow=False, scope="always"), now=1001, wait=0)
        for bad, why in ((ask(action="answer", ask="ab12cd34ef56"), "回答无效"), (ask(action="answer", ask="ab12cd34ef56", allow="yes"), "回答无效"), (ask(action="answer", ask="x", allow=True), "回答无效"),
                         (ask(action="answer", ask="ffffffffffff", allow=True), "已经处理过"), (ask(action="say", text="  "), "1 至 8000"), (ask(action="say", text="x" * 8001), "1 至 8000"),
                         (ask(action="end"), "不支持"), (ask(action="say", text="hi", session=other), "不能从手机发消息"), (ask(action="answer", ask="ab12cd34ef56", allow=True, session=other), "已经处理过")):
            with self.assertRaisesRegex(RemoteError, why):
                self.relay.conversation(bad, now=1001, wait=0)
        answers = {}
        waiting = threading.Thread(target=lambda: answers.update(got=self.relay.conversation(ask(action="answer", ask="ab12cd34ef56", allow=False), now=1001, wait=5)))
        waiting.start()
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
        self.assertEqual([(o["action"], o["session"], o["tool"], o["dir"], o["ask"], o["allow"]) for o in asked], [("session_answer", sid, "codex", "demo", "ab12cd34ef56", False)])
        self.relay.agent({"info": info, "acks": [{"id": "c" * 32, "error": "", "result": {}}]}, now=1002)
        waiting.join(5)
        self.assertEqual(answers["got"], {})
        waiting = threading.Thread(target=lambda: answers.update(said=self.relay.conversation(dict(ask(action="say", text="然后更新文档"), id="d" * 32), now=1002, wait=5)))
        waiting.start()
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
        self.assertEqual([(o["action"], o["session"], o["tool"], o["text"]) for o in asked], [("session_say", sid, "codex", "然后更新文档")])
        self.relay.agent({"info": info, "acks": [{"id": "d" * 32, "error": "", "result": {"now": False}}]}, now=1003)
        waiting.join(5)
        self.assertEqual(answers["said"], {"now": False})            # it takes the message when it has done what it is doing
        # what the computer refuses is said in its words
        waiting = threading.Thread(target=lambda: answers.update(error=self._refused(dict(ask(action="say", text="再来"), id="e" * 32), 1003)))
        waiting.start()
        self.relay.pull({"instance": self.info["instance"], "wait": 3})
        self.relay.agent({"info": info, "acks": [{"id": "e" * 32, "error": "这段对话已经在电脑上停下"}]}, now=1004)
        waiting.join(5)
        self.assertIn("已经在电脑上停下", answers["error"])
        # a computer whose program is older has no such thing
        self.relay.agent({"info": dict(self.info, features=["peek"]), "sessions": [told]}, now=1005)
        with self.assertRaisesRegex(RemoteError, "更新电脑端"):
            self.relay.conversation(dict(ask(action="say", text="hi"), id="f" * 32), now=1005, wait=0)

    def _refused(self, payload, now):
        try:
            self.relay.conversation(payload, now=now, wait=5)
            return ""
        except RemoteError as error:
            return str(error)

    def test_a_message_is_said_to_a_terminal_from_outside_its_page(self):
        self.start()
        pulled = lambda: self.relay.pull({"instance": self.info["instance"], "wait": 0})["operations"]
        pulled()
        say = {"action": "say", "id": "1" * 32, "terminal": self.terminal, "text": "  跑一下测试\r\n然后提交  "}
        # a program that has not said how it takes pasted text is given one line, and Enter
        self.assertEqual(self.relay.command(say, now=1004)["state"], "queued")
        self.assertEqual(self.relay.command(say, now=1005)["state"], "queued")       # the same request again is the same one
        self.assertEqual([(o["action"], o["data"]) for o in pulled()], [("input", "跑一下测试 然后提交\r")])
        # one that takes pasted text in brackets is given the text as it is, in brackets
        self.relay.agent({"info": self.info, "acks": [{"id": say["id"]}], "terminals": [{"id": self.terminal, "state": "running"}],
                          "output": [{"terminal": self.terminal, "seq": 1, "data": "\x1b[?2004h> "}]}, now=1006)
        self.relay.command(dict(say, id="2" * 32), now=1007)
        self.assertEqual([o["data"] for o in pulled()], ["\x1b[200~跑一下测试\n然后提交\x1b[201~\r"])
        for text in ("", "   ", "a\x1bb", "x" * 8001, 7):
            with self.assertRaisesRegex(RemoteError, "内容为空"):
                self.relay.command(dict(say, id="3" * 32, text=text), now=1008)
        with self.assertRaisesRegex(RemoteError, "其它操作"):
            self.relay.command(dict(say, text="别的话"), now=1008)
        # Enter would answer the question a program waits on: the message is not passed on
        self.relay._state()["threads"][self.terminal]["phase"] = "confirm"
        with self.assertRaisesRegex(RemoteError, "等待确认"):
            self.relay.command(dict(say, id="4" * 32), now=1009)
        self.assertEqual(pulled(), [])

    def test_the_program_on_the_computer_is_asked_about_itself(self):
        import threading
        ask = {"id": "f" * 32, "action": "settings_read"}
        with self.assertRaisesRegex(RemoteError, "更新电脑端"):
            self.relay.computer(ask, now=1001, wait=0)
        info = dict(self.info, features=["settings"])
        self.relay.agent({"info": info}, now=1001)
        self.assertIn("settings", self.relay.overview(now=1001)["device"]["features"])
        for bad in (dict(ask, action="settings_wipe"), dict(ask, id="x"), {"id": "e" * 32, "action": "settings_change", "name": "Rights", "value": "full"},
                    {"id": "e" * 32, "action": "settings_change", "name": "rights", "value": 3}, {"id": "e" * 32, "action": "line_switch", "to": "https://relay.example"},
                    {"id": "e" * 32, "action": "line_keep", "key": "no"}):
            with self.assertRaises(RemoteError):
                self.relay.computer(bad, now=1001, wait=0)
        # only what belongs to the question is passed on, whatever else the viewer sent with it
        for n, (sent, passed) in enumerate((({"action": "settings_change", "name": "autostart", "value": True, "to": "lan"}, {"action": "settings_change", "name": "autostart", "value": True}),
                                            ({"action": "line_switch", "to": "r0123456789ab", "key": "c" * 32}, {"action": "line_switch", "to": "r0123456789ab"}),
                                            ({"action": "line_take"}, {"action": "line_take"}), ({"action": "line_keep", "key": "c" * 32}, {"action": "line_keep", "key": "c" * 32}))):
            sent["id"] = "%032x" % (n + 1)
            answers = {}
            waiting = threading.Thread(target=lambda: answers.update(got=self.relay.computer(sent, now=1002, wait=5)))
            waiting.start()
            asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
            self.assertEqual([{k: v for k, v in o.items() if k not in ("id", "terminal", "at")} for o in asked], [passed])
            self.relay.agent({"info": info, "acks": [{"id": sent["id"], "error": "", "result": {"lines": [{"id": "lan"}]}}]}, now=1003)
            waiting.join(5)
            self.assertEqual(answers["got"], {"lines": [{"id": "lan"}]})
            self.assertNotIn(sent["id"], self.relay._pending)        # nothing of it is kept
        # what the program refuses is said to the viewer in the program's words
        refused, said = {"id": "d" * 32, "action": "line_take"}, {}

        def take():
            try:
                self.relay.computer(refused, now=1004, wait=5)
            except RemoteError as error:
                said["error"] = str(error)
        failing = threading.Thread(target=take)
        failing.start()
        self.relay.pull({"instance": self.info["instance"], "wait": 3})
        self.relay.agent({"info": info, "acks": [{"id": refused["id"], "error": "新线路还没有准备好"}]}, now=1005)
        failing.join(5)
        self.assertEqual(said, {"error": "新线路还没有准备好"})

    def test_file_upload_validation_and_forwarding(self):
        import threading
        ask = {"id": "f" * 32, "action": "file_upload_start", "dir": "demo", "path": "手机.txt", "upload": "a" * 32, "size": 1}
        self.relay.agent({"info": dict(self.info, features=["files"])}, now=1001)
        with self.assertRaisesRegex(RemoteError, "不支持上传"):
            self.relay.files(ask, now=1001, wait=0)
        info = dict(self.info, features=["files", "file-upload"])
        self.relay.agent({"info": info}, now=1001)
        for bad in (dict(ask, size=True), dict(ask, size=-1), dict(ask, size=314572801), dict(ask, upload="bad"),
                    dict(ask, action="file_upload_chunk", data="bad!"), dict(ask, action="file_upload_chunk", data="a" * 983044),
                    dict(ask, action="file_upload_chunk", data=""), dict(ask, offset=True)):
            with self.assertRaises(RemoteError):
                self.relay.files(bad, now=1001, wait=0)
        for index, (action, extra) in enumerate((("start", {"size": 1}), ("chunk", {"data": "YQ=="}), ("finish", {}), ("cancel", {}))):
            sent = dict(ask, id="%032x" % (index + 1), action="file_upload_" + action, unexpected="drop", **extra)
            answer = {}
            waiting = threading.Thread(target=lambda: answer.update(got=self.relay.files(sent, now=1002, wait=5)))
            waiting.start()
            operations = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
            self.assertEqual(len(operations), 1)
            operation = operations[0]
            self.assertEqual(operation["upload"], ask["upload"])
            self.assertNotIn("unexpected", operation)
            if action != "start":
                self.assertNotIn("size", operation)
            for key, value in extra.items():
                self.assertEqual(operation[key], value)
            self.relay.agent({"info": info, "acks": [{"id": sent["id"], "error": "", "result": {"offset": 1}}]}, now=1003)
            waiting.join(5)
            self.assertEqual(answer["got"], {"offset": 1})
            self.assertNotIn(sent["id"], self.relay._pending)

    def test_files_are_asked_of_the_computer_and_answered_once(self):
        import threading
        ask = {"id": "f" * 32, "action": "file_list", "dir": "demo", "path": "src"}
        with self.assertRaisesRegex(RemoteError, "更新电脑端"):
            self.relay.files(ask, now=1001, wait=0)
        info = dict(self.info, features=["files", "update"], version="0.7.0", newer="0.7.1")
        self.relay.agent({"info": info}, now=1001)
        for bad in (dict(ask, dir="other"), dict(ask, path="a\x00b"), dict(ask, action="file_delete"), dict(ask, offset=-1), dict(ask, id="x")):
            with self.assertRaises(RemoteError):
                self.relay.files(bad, now=1001, wait=0)
        answers = {}
        waiting = threading.Thread(target=lambda: answers.update(got=self.relay.files(ask, now=1001, wait=5)))
        waiting.start()
        asked = self.relay.pull({"instance": self.info["instance"], "wait": 3})["operations"]
        self.assertEqual([(o["action"], o["dir"], o["path"], o["offset"]) for o in asked], [("file_list", "demo", "src", 0)])
        self.relay.agent({"info": info, "acks": [{"id": ask["id"], "error": "", "result": {"path": "src", "entries": [{"name": "a.py"}]}}]}, now=1002)
        waiting.join(5)
        self.assertEqual(answers["got"]["entries"], [{"name": "a.py"}])
        self.assertNotIn(ask["id"], self.relay._pending)        # nothing of it is kept
        refused = dict(ask, id="e" * 32)
        failing = threading.Thread(target=lambda: answers.update(error=self.assertRaisesRegex(RemoteError, "不在项目文件夹内", self.relay.files, refused, 1003, 5)))
        failing.start()
        self.relay.pull({"instance": self.info["instance"], "wait": 3})
        self.relay.agent({"info": info, "acks": [{"id": refused["id"], "error": "路径不在项目文件夹内"}]}, now=1004)
        failing.join(5)
        with self.assertRaisesRegex(RemoteError, "没有及时回应"):
            self.relay.files(dict(ask, id="d" * 32), now=1005, wait=0.2)
        # the versions are shown, and the phone may ask for the update
        device = self.relay.overview(now=1006)["device"]
        self.assertEqual((device["version"], device["newer"]), ("0.7.0", "0.7.1"))
        self.assertEqual(self.relay.command({"action": "update", "id": "c" * 32}, now=1006)["state"], "queued")
        self.relay.agent({"info": self.info}, now=1007)
        with self.assertRaisesRegex(RemoteError, "先在电脑上更新一次"):
            self.relay.command({"action": "update", "id": "b" * 32}, now=1007)

    def test_output_is_streamed_as_it_arrives_and_a_wake_up_between_look_and_wait_is_not_lost(self):
        import threading
        import time
        self.start()
        live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]}
        self.relay.agent(live)
        got, times = [], []
        began = time.monotonic()

        def read():
            for item in self.relay.stream(self.terminal, after=0, seconds=2.5, beat=1):
                self.assertNotIn("tick", item)
                got.append(item)
                times.append(time.monotonic() - began)
        reader = threading.Thread(target=read)
        reader.start()
        time.sleep(0.3)
        self.relay.agent(dict(live, output=[{"terminal": self.terminal, "seq": 1, "data": "一"}]))
        time.sleep(0.3)
        self.relay.agent(dict(live, output=[{"terminal": self.terminal, "seq": 2, "data": "二"}, {"terminal": self.terminal, "seq": 3, "data": "三"}]))
        time.sleep(0.3)
        self.relay.agent({"info": self.info, "terminals": [{"id": self.terminal, "state": "running", "status": "busy"}]})
        reader.join(6)
        self.assertFalse(reader.is_alive())
        self.assertEqual("".join(c["data"] for item in got for c in item["chunks"]), "一二三")
        self.assertEqual([c["seq"] for item in got for c in item["chunks"]], [1, 2, 3])
        self.assertEqual(got[0]["chunks"], [], "the first line tells the state at once")
        self.assertLess(times[0], 0.2)
        arrived = [t for item, t in zip(got, times) if item["chunks"]]
        self.assertTrue(0.25 < arrived[0] < 0.6 and 0.55 < arrived[1] < 0.9, arrived)
        self.assertTrue(any(item["terminal"].get("status") == "busy" for item in got))
        self.assertGreaterEqual(len([item for item in got if not item["chunks"]]), 3, "state line, status change and heartbeats")
        self.assertLess(times[-1], 3.6)
        self.assertNotIn("tick", self.relay.overview(self.terminal, after=3))
        self.assertNotIn("tick", self.relay.overview(self.terminal, after=3, wait=0.2))
        # A wake-up after the look but before the wait must end the wait at once.
        seen = self.relay._overview(self.terminal, 3, time.time())["tick"]
        self.relay.agent(dict(live, output=[{"terminal": self.terminal, "seq": 4, "data": "四"}]))
        began = time.monotonic()
        self.relay._wait(seen, 3)
        self.assertLess(time.monotonic() - began, 0.5)
        with self.assertRaises(RemoteError):
            next(self.relay.stream("0" * 32))
    def test_phase_tells_working_asking_finished_and_ended_apart(self):
        self.start()
        live = lambda status="", **more: dict({"info": self.info, "terminals": [dict({"id": self.terminal, "state": "running", "status": status}, **more)]})
        phase = lambda now: self.relay.overview(now=now)["terminals"][0]
        self.assertEqual(phase(1004)["phase"], "idle")
        self.assertFalse(phase(1004).get("done"))                      # it has not worked yet
        key = lambda n, data, now: self.relay.command({"action": "input", "id": "%032x" % n, "terminal": self.terminal, "data": data}, now=now)
        # the banner a program prints when it opens is not work, and neither is the echo of a key
        self.relay.agent(dict(live(), output=[{"terminal": self.terminal, "seq": 1, "data": "Welcome\r\n"}]), now=1005)
        self.assertEqual(phase(1005)["phase"], "idle")
        key(1, "n", 1006)
        self.relay.agent(dict(live(), output=[{"terminal": self.terminal, "seq": 2, "data": "n"}]), now=1006.5)
        self.assertEqual(phase(1006.5)["phase"], "idle")
        self.assertFalse(phase(1006.5).get("done"))
        key(2, "pm run build\r", 1008)
        self.relay.agent(dict(live(), output=[{"terminal": self.terminal, "seq": 3, "data": "building\r\n"}]), now=1010)
        self.assertEqual(phase(1010)["phase"], "busy")                 # without a status of its own, fresh output means work
        self.relay.agent(live(), now=1016)
        done = phase(1016)
        self.assertEqual((done["phase"], done["done"]), ("idle", True))
        self.assertNotIn("out_at", done)
        self.assertNotIn("touched", done)
        # opening the terminal on the phone resizes it; the screen drawn again must not look like new work
        self.relay.command({"action": "resize", "id": "%032x" % 3, "terminal": self.terminal, "cols": 50, "rows": 30}, now=1017)
        self.relay.agent(dict(live(), output=[{"terminal": self.terminal, "seq": 4, "data": "\x1b[2Jbuilding\r\n"}]), now=1018)
        self.assertEqual(phase(1018)["phase"], "idle")
        self.assertEqual(phase(1018)["phase_at"], done["phase_at"])
        # a question on the screen that stays there
        self.relay.agent(dict(live(), output=[{"terminal": self.terminal, "seq": 5, "data": "\x1b[1mDo you want to proceed?\x1b[0m\r\n\x1b[36m> 1. Yes\x1b[0m\r\n  2. No"}]), now=1020)
        self.assertEqual(phase(1020)["phase"], "busy")                 # just written: still drawing
        self.relay.agent(live(), now=1023)
        self.assertEqual(phase(1023)["phase"], "confirm")
        # Claude Code reports its own status; a question only counts while it says it is working
        self.relay.agent(live("idle"), now=1030)
        self.assertEqual(phase(1030)["phase"], "idle")
        self.relay.agent(live("busy"), now=1031)
        self.assertEqual(phase(1031)["phase"], "confirm")
        self.relay.agent(dict(live("busy"), output=[{"terminal": self.terminal, "seq": 6, "data": "x" * 2000}]), now=1040)
        self.relay.agent(live("busy"), now=1045)
        self.assertEqual(phase(1045)["phase"], "busy")                 # answered: the question has scrolled out of the last screen
        self.relay.agent({"info": self.info, "terminals": [{"id": self.terminal, "state": "closed", "exit_code": 3}]}, now=1050)
        self.assertEqual(phase(1050)["phase"], "failed")

    def test_overview_tells_what_a_running_terminal_last_said(self):
        self.start()
        live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running", "cols": 40, "rows": 8}]}
        said = lambda now: self.relay.overview(now=now)["terminals"][0]["said"]
        self.assertEqual(said(1004), "")
        # a message that wraps, the frame of the prompt under it, and a status line that is drawn over and over
        self.relay.agent(dict(live, output=[{"terminal": self.terminal, "seq": 1, "data":
            "\x1b[2J\x1b[1;1H\u25cf \x1b[1mold answer\x1b[0m\r\n\r\n\u25cf Updated the README and\r\n  ran the tests\r\n\r\n" + "\u2500" * 40 + "\r\n> \r\n  ? for shortcuts"}]), now=1005)
        self.assertEqual(said(1006), "Updated the README and ran the tests")
        self.relay.agent(dict(live, output=[{"terminal": self.terminal, "seq": 2, "data": "\x1b[3;1H\x1b[2K\u25cf \u4e2d\u6587\u6d88\u606f\x1b[4;1H\x1b[2K"}]), now=1007)
        self.assertEqual(said(1008), "\u4e2d\u6587\u6d88\u606f")
        # after a restart of the relay the screen is drawn again from the output that was kept
        self.relay._screens.clear()
        self.assertEqual(said(1009), "\u4e2d\u6587\u6d88\u606f")
        self.relay.agent({"info": self.info, "terminals": [{"id": self.terminal, "state": "closed", "exit_code": 0}]}, now=1010)
        self.assertEqual(said(1011), "")

    def test_a_fullscreen_program_is_read_from_its_last_output_with_the_switches_it_set(self):
        self.start()
        feed = lambda pieces: [self.relay.agent({"info": self.info, "output": pieces[at:at + 100]}, now=1004) for at in range(0, len(pieces), 100)]
        piece = lambda seq, data: {"terminal": self.terminal, "seq": seq, "data": data}
        draw = "\x1b[5;1H" + "x" * 994
        # the switches arrive once, the last of them cut in two by the end of a piece
        feed([piece(1, "\x1b[?1049h\x1b[?1003;1006h\x1b[?25l\x1b[?20"), piece(2, "04h" + draw)] + [piece(seq, draw) for seq in range(3, 301)])
        lead = "\x1b[?1049h\x1b[?1003h\x1b[?1006h\x1b[?25l\x1b[?2004h"
        fresh = self.relay.overview(self.terminal, after=0, now=1005)
        self.assertTrue(fresh["reset"])
        self.assertGreater(fresh["chunks"][0]["seq"], 150)
        self.assertEqual(fresh["chunks"][0]["data"], lead + draw)
        self.assertEqual((fresh["after"], sum(len(c["data"]) for c in fresh["chunks"]) <= tr.TAIL + len(lead)), (300, True))
        self.assertNotIn("modes", fresh["terminal"])
        # a reader that follows along is given what is new and nothing else
        self.assertEqual((self.relay.overview(self.terminal, after=299, now=1005)["reset"], [c["data"] for c in self.relay.overview(self.terminal, after=299, now=1005)["chunks"]]), (False, [draw]))
        # when the history is full the pieces that set the switches are dropped; the switches are not, also over a restart
        limit, tr.OUTPUT_LIMIT = tr.OUTPUT_LIMIT, 200_000
        self.addCleanup(setattr, tr, "OUTPUT_LIMIT", limit)
        feed([piece(301, "\x1b[?1003l\x1b[?1000h" + draw), piece(302, draw)])
        self.relay.agent({"info": self.info}, now=1010)
        self.relay._kept = None
        stored = self.relay._state()["threads"][self.terminal]
        self.assertGreater(stored["output"][0]["seq"], 2)
        self.assertEqual(stored["modes"], {"1049": True, "1003": True, "1006": True, "25": False, "2004": True})
        late = self.relay.overview(self.terminal, after=0, now=1011)
        self.assertEqual((late["reset"], late["chunks"][0]["data"][:len(lead)]), (True, lead))
        self.assertIn("\x1b[?1003l\x1b[?1000h", "".join(c["data"] for c in late["chunks"]))
        # once the program has left the alternate screen its output is history again, and all that is kept is read back
        feed([piece(303, "\x1b[?1049l\x1b[?25hbye\r\n")])
        whole = self.relay.overview(self.terminal, after=0, now=1012)
        self.assertEqual((whole["reset"], whole["chunks"][0]["seq"], whole["chunks"][0]["data"][:len(lead)]), (True, stored["output"][0]["seq"], lead))

    def test_screen_follows_cursor_erasing_scrolling_and_wide_characters(self):
        from screen import Screen
        view = Screen(10, 3)
        view.feed("one\r\ntwo\r\nthree\r\nfour")
        self.assertEqual(view.lines(), ["two", "three", "four"])
        view.feed("\x1b[1;1H\x1b[Kab\x1b[3G\u4e2d\u6587\x1b[2;3H\x1b[1K")
        self.assertEqual(view.lines(), ["ab\u4e2d\u6587", "   ee", "four"])
        view.feed("\x1b[?1049h\x1b[Hmenu\x1b[?1049l")
        self.assertEqual(view.lines()[2], "four")
        view.feed("\x1b[3;1H\x1b")            # a sequence cut in two by the end of a piece
        view.feed("[2Kdone")
        self.assertEqual(view.lines()[2], "done")
        view.resize(4, 2)
        self.assertEqual(view.lines(), ["   e", "done"])

    def test_terminal_started_through_another_relay_is_listed_with_its_history(self):
        sid, terminal = "12345678-1234-1234-1234-123456789abc", "7" * 32
        sessions = [{"id": sid, "tool": "claude", "dir": "demo", "title": "修复面板", "updated": 5000}]
        mine = {"id": terminal, "state": "running", "tool": "claude", "dir": "demo", "created": 900_000, "session": sid, "title": "我的名字", "cols": 100, "rows": 30}
        reply = self.relay.agent({"info": self.info, "sessions": sessions, "terminals": [mine]}, now=1001)
        self.assertEqual((reply["output_ack"], reply["titles"]), ({terminal: 0}, {terminal: "我的名字"}))
        view = self.relay.overview(now=1002)
        shown = view["terminals"][0]
        self.assertEqual((shown["id"], shown["state"], shown["title"], shown["created"], shown["cols"], shown["phase"]), (terminal, "running", "我的名字", 900_000, 100, "idle"))
        self.assertEqual(view["sessions"][0]["terminal"], terminal)        # the conversation is the one that terminal shows, not one to open again
        with self.assertRaisesRegex(RemoteError, "已经在手机终端里打开"):
            self.relay.command({"action": "start", "id": "1" * 32, "tool": "claude", "dir": "demo", "session": sid}, now=1003)
        # the computer gives what it still has: it begins in the middle, says so, and is not counted as work
        again = [{"terminal": terminal, "seq": 41, "data": "\x1b[?2004hold screen", "old": True, "restart": True}, {"terminal": terminal, "seq": 42, "data": " more", "old": True}]
        self.assertEqual(self.relay.agent({"info": self.info, "terminals": [mine], "output": again}, now=1004)["output_ack"], {terminal: 42})
        read = self.relay.overview(terminal, after=0, now=1005)
        self.assertEqual(("".join(c["data"] for c in read["chunks"]), read["reset"], read["after"], read["terminal"]["phase"]), ("\x1b[?2004hold screen more", True, 42, "idle"))
        self.relay.command({"action": "input", "id": "2" * 32, "terminal": terminal, "data": "ls\r"}, now=1006)     # and it is used like any other
        self.assertEqual(self.relay.agent({"info": self.info, "terminals": [mine]}, now=1007)["operations"][0]["data"], "ls\r")
        # what does not say what it is (an older program on the computer) is not listed
        self.relay.agent({"info": self.info, "terminals": [mine, {"id": "8" * 32, "state": "running"}, {"id": "9" * 32, "state": "running", "tool": "bash", "dir": "demo", "created": 1}]}, now=1008)
        self.assertEqual([t["id"] for t in self.relay.overview(now=1009)["terminals"]], [terminal])

    def test_history_given_again_replaces_what_no_longer_continues(self):
        self.start()
        live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]}
        self.relay.agent(dict(live, output=[{"terminal": self.terminal, "seq": n, "data": "early %d " % n} for n in (1, 2)]), now=1004)
        # the computer went on through another relay; back here it gives what it kept, which begins later
        self.relay.agent(dict(live, output=[{"terminal": self.terminal, "seq": 9, "data": "late 9 ", "old": True, "restart": True}, {"terminal": self.terminal, "seq": 10, "data": "late 10"}]), now=1005)
        read = self.relay.overview(self.terminal, after=2, now=1006)
        self.assertEqual(("".join(c["data"] for c in read["chunks"]), read["reset"], read["after"]), ("late 9 late 10", True, 10))

    def test_running_terminal_the_computer_no_longer_has_is_ended(self):
        self.start()
        other = self.relay.command({"action": "start", "id": "c" * 32, "tool": "shell", "dir": "demo"}, now=1004)["terminal"]
        self.relay.agent({"info": self.info}, now=1005)                 # a report without the list says nothing about it
        self.relay.agent({"info": self.info, "terminals": []}, now=1006)
        states = {t["id"]: (t["state"], t["phase"]) for t in self.relay.overview(now=1007)["terminals"]}
        self.assertEqual(states, {self.terminal: ("closed", "ended"), other: ("starting", "starting")})      # one that has not started yet is still to come

    def test_a_conversation_has_one_terminal_in_the_list(self):
        sid = "12345678-1234-1234-1234-123456789abc"
        sessions = [{"id": sid, "tool": "claude", "dir": "demo", "title": "修复面板", "updated": 5000}]
        self.relay.agent({"info": self.info, "sessions": sessions}, now=1001)
        ids = lambda: [t["id"] for t in self.relay.overview(now=2000)["terminals"]]
        def run(n, close=True, fail=False):
            op = {"action": "start", "id": "%032x" % n, "tool": "claude", "dir": "demo", "session": sid}
            terminal = self.relay.command(op, now=1010 + n)["terminal"]
            if fail:
                self.relay.agent({"info": self.info, "acks": [{"id": op["id"], "error": "电脑未安装这个工具"}], "terminals": []}, now=1011 + n)
                return terminal
            self.relay.agent({"info": self.info, "acks": [{"id": op["id"]}], "terminals": [{"id": terminal, "state": "running"}],
                                 "output": [{"terminal": terminal, "seq": 1, "data": "screen %d" % n}]}, now=1011 + n)
            if close:
                self.relay.agent({"info": self.info, "terminals": [{"id": terminal, "state": "closed"}]}, now=1012 + n)
            return terminal
        first = run(1)
        self.assertEqual(ids(), [first])
        failed = run(10, fail=True)                 # a start that failed does not take the place of the screen that was kept
        self.assertEqual(ids(), [first])
        second = run(20, close=False)               # running: the ended one of the same conversation goes
        self.assertEqual(ids(), [second])
        self.relay.agent({"info": self.info, "terminals": [{"id": second, "state": "closed"}]}, now=1040)
        third = run(30)
        self.assertEqual(ids(), [third])
        shell = [self.relay.command({"action": "start", "id": "%032x" % (100 + n), "tool": "shell", "dir": "demo"}, now=1050 + n)["terminal"] for n in range(2)]
        self.relay.agent({"info": self.info, "terminals": [{"id": key, "state": "closed"} for key in shell]}, now=1055)
        self.assertEqual(len(ids()), 3)             # terminals without a conversation are each their own
        # lists written by an older relay are tidied when they are read
        state = self.relay._state()
        for n in range(3):
            state["threads"]["%032x" % (200 + n)] = dict(state["threads"][third], id="%032x" % (200 + n), created=n, output=[])
        self.relay._save(state)
        self.relay._kept = None
        self.assertEqual(sorted(ids()), sorted([third] + shell))
        self.assertNotIn(failed, ids())

    def test_ended_terminal_of_another_relay_is_listed_only_where_it_is_kept(self):
        sid = "12345678-1234-1234-1234-123456789abc"
        ended = lambda n, **more: dict({"id": "%032x" % n, "state": "closed", "tool": "codex", "dir": "demo", "created": 1000 + n, "exit_code": 0}, **more)
        ids = lambda: {t["id"] for t in self.relay.overview(now=2000)["terminals"]}
        reply = self.relay.agent({"info": self.info, "terminals": [ended(5, session=sid)], "output": []}, now=1001)
        self.assertEqual(reply["output_ack"], {"%032x" % 5: 0})
        self.relay.agent({"info": self.info, "terminals": [ended(5, session=sid)], "output": [{"terminal": "%032x" % 5, "seq": 3, "data": "last screen", "old": True, "restart": True}]}, now=1002)
        shown = self.relay.overview("%032x" % 5, now=1003)
        self.assertEqual((shown["terminal"]["state"], shown["terminal"]["phase"], shown["chunks"][0]["data"]), ("closed", "ended", "last screen"))
        self.relay.agent({"info": self.info, "terminals": [ended(5, session=sid), ended(6, session=sid)]}, now=1004)       # the same conversation: once
        self.assertEqual(ids(), {"%032x" % 5})
        many = [ended(5, session=sid)] + [ended(n) for n in range(10, 10 + tr.MAX_HISTORY)]
        for _ in range(2):          # said again and again, the list stays the same: the newest that fit
            self.relay.agent({"info": self.info, "terminals": many}, now=1005)
            self.assertEqual(ids(), {"%032x" % n for n in range(10, 10 + tr.MAX_HISTORY)})


if __name__ == "__main__":
    unittest.main()
