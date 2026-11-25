import http.client
import json
import os
import tempfile
import threading
import time
import unittest
from pathlib import Path

import server


class Phone:
    """Requests as a phone or a computer makes them, with the cookie it was given."""

    def __init__(self, port, prefix=""):
        self.port, self.prefix, self.cookie = port, prefix, ""

    def ask(self, path, payload=None, headers=None, inside=True):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        sent = dict(headers or {})
        if self.cookie:
            sent["Cookie"] = self.cookie
        if payload is not None:
            sent["Content-Type"] = "application/json"
        connection.request("GET" if payload is None else "POST", (self.prefix if inside else "") + path, None if payload is None else json.dumps(payload).encode("utf-8"), sent)
        reply = connection.getresponse()
        body = reply.read()
        given = reply.getheader("Set-Cookie")
        if given and reply.status == 200:
            self.cookie = given.split(";", 1)[0]
        connection.close()
        kind = reply.getheader("Content-Type") or ""
        return reply.status, (json.loads(body) if kind.startswith("application/json") and body else body), reply


class SpacesTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.start()

    def start(self, **more):
        self.server = server.make_server("127.0.0.1", 0, self.temp.name, str(Path(__file__).resolve().parents[1] / "web"), public=True, **more)
        self.port = self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.addCleanup(self.stop, self.server)

    def stop(self, running):
        running.shutdown()
        running.server_close()

    def space(self, address="1.2.3.4", key=None, expect=200):
        status, made, _ = Phone(self.port).ask("/api/space", {} if key is None else {"key": key}, {"X-Forwarded-For": address})
        self.assertEqual(status, expect, made)
        if status != 200:
            return made
        self.assertRegex(made["space"], r"^/c/[a-f0-9]{20}$")
        return made

    def computer(self, made, instance="b" * 32):
        """Signs in to a space as its computer and reports once; returns the connection and what it reports."""
        agent = Phone(self.port, made["space"])
        self.assertEqual(agent.ask("/api/login", {"password": made["password"]})[0], 200)
        info = {"instance": instance, "enabled": True, "tools": ["shell"], "workspaces": ["demo"]}
        self.assertEqual(agent.ask("/api/terminal/agent", {"info": info, "terminals": []})[0], 200)
        return agent, info

    def test_the_top_of_a_relay_for_everyone_has_nothing_but_its_version(self):
        top = Phone(self.port)
        status, said, _ = top.ask("/api/session")
        self.assertEqual((status, said["public"], said["signed_in"], said["key"]), (200, True, False, False))
        self.assertNotIn("version", said)                              # nothing about the relay itself is told
        self.assertNotIn(server.VERSION, top.ask("/api/session")[2].getheader("Server"))
        for path in ("/api/terminal", "/app.js", "/terminal/", "/c/", "/c/zz", "/c/" + "a" * 20 + "/", "/c/" + "a" * 20 + "/api/session"):
            self.assertEqual(top.ask(path)[0], 404, path)
        for path in ("/api/login", "/api/terminal", "/api/terminal/agent"):
            self.assertEqual(top.ask(path, {"password": "x"})[0], 404, path)
        status, page, _ = top.ask("/")
        self.assertEqual((status, b"<" in page), (200, False))         # a line of text: no page, no list of who uses it
        self.assertEqual(top.ask("/api/space", {}, {"Origin": "https://elsewhere.example"})[0], 403)

    def test_each_computer_has_its_own_space_and_nothing_crosses(self):
        one, two = self.space(), self.space()
        self.assertNotEqual((one["space"], one["password"]), (two["space"], two["password"]))
        agent, info = self.computer(one)
        other_agent, other_info = self.computer(two, "c" * 32)
        phone = Phone(self.port, one["space"])
        self.assertEqual(phone.ask("/api/terminal")[0], 401)
        self.assertEqual(phone.ask("/api/login", {"password": two["password"]})[0], 401)       # the other space's password
        status, _, reply = phone.ask("/api/login", {"password": one["password"]})
        self.assertEqual(status, 200)
        self.assertIn("Path=%s;" % one["space"], reply.getheader("Set-Cookie"))                # sent to this space's pages only
        started = phone.ask("/api/terminal", {"action": "start", "id": "a" * 32, "tool": "shell", "dir": "demo"})[1]
        ops = agent.ask("/api/terminal/agent", {"info": info, "terminals": []})[1]["operations"]
        self.assertEqual([op["terminal"] for op in ops], [started["terminal"]])
        agent.ask("/api/terminal/agent", {"info": info, "acks": [{"id": "a" * 32}], "terminals": [{"id": started["terminal"], "state": "running"}],
                                          "output": [{"terminal": started["terminal"], "seq": 1, "data": "only in the first space"}]})
        seen = phone.ask("/api/terminal?terminal=" + started["terminal"])[1]
        self.assertEqual(seen["chunks"][0]["data"], "only in the first space")
        # the other computer and its phone see none of it, and the first space's sign-in is worth nothing there
        self.assertEqual(other_agent.ask("/api/terminal/agent", {"info": other_info, "terminals": []})[1]["operations"], [])
        stranger = Phone(self.port, two["space"])
        stranger.cookie = phone.cookie
        self.assertEqual(stranger.ask("/api/terminal")[0], 401)
        stranger.ask("/api/login", {"password": two["password"]})
        listed = stranger.ask("/api/terminal")[1]
        self.assertEqual((listed["terminals"], listed["device"]["instance"]), ([], "c" * 32))
        self.assertEqual(stranger.ask("/api/terminal?terminal=" + started["terminal"])[0], 400)
        self.assertEqual(phone.ask("/api/terminal")[1]["device"]["instance"], "b" * 32)
        # the pages of a space are served inside it, with their files named relative to the folder
        status, page, _ = phone.ask("/")
        self.assertEqual((status, b'src="app.js?v=' in page), (200, True))
        self.assertEqual(phone.ask("/terminal/")[0], 200)
        status, _, reply = phone.ask("")
        self.assertEqual((status, reply.getheader("Location")), (308, one["space"] + "/"))
        self.assertEqual(phone.ask("/api/session")[1]["signed_in"], True)

    def test_spaces_last_through_a_restart_and_keep_only_a_hash_of_the_password(self):
        made = self.space()
        agent, info = self.computer(made)
        kept = json.loads((Path(self.temp.name) / "spaces" / made["space"][3:] / "space.json").read_text(encoding="utf-8"))
        self.assertNotIn(made["password"], json.dumps(kept))
        self.stop(self.server)
        self.start()
        again = Phone(self.port, made["space"])
        again.cookie = agent.cookie
        self.assertEqual(again.ask("/api/session")[1]["signed_in"], True)
        self.assertEqual(again.ask("/api/login", {"password": made["password"]})[0], 200)

    def test_limits_a_key_and_spaces_nobody_uses(self):
        for _ in range(server.SPACES_A_DAY):
            self.space("9.9.9.9")
        self.assertIn("太多", self.space("9.9.9.9", expect=429)["error"])
        self.space("8.8.8.8")                       # another address is not held back by it
        self.stop(self.server)
        self.start(key="open-sesame", most=server.SPACES_A_DAY + 3)
        self.assertEqual(Phone(self.port).ask("/api/session")[1]["key"], True)
        self.assertIn("口令", self.space("7.7.7.7", expect=403)["error"])
        self.assertIn("口令", self.space("7.7.7.7", key="wrong", expect=403)["error"])
        made = [self.space("7.7.7.7", key="open-sesame"), self.space("7.7.7.7", key="open-sesame")]
        self.assertIn("已满", self.space("6.6.6.6", key="open-sesame", expect=503)["error"])
        # a space nobody has used for a month goes, and its place is free again
        old = Path(self.temp.name) / "spaces" / made[0]["space"][3:]
        long_ago = time.time() - (server.SPACE_DAYS + 1) * 86400
        for name in os.listdir(old):
            os.utime(old / name, (long_ago, long_ago))
        self.server.spaces.swept = 0
        self.space("6.6.6.6", key="open-sesame")
        self.assertFalse(old.exists())
        self.assertEqual(Phone(self.port, made[0]["space"]).ask("/api/session")[0], 404)
        self.assertEqual(Phone(self.port, made[1]["space"]).ask("/api/login", {"password": made[1]["password"]})[0], 200)

    def test_a_relay_of_one_computer_has_no_spaces(self):
        self.stop(self.server)
        self.server = server.make_server("127.0.0.1", 0, self.temp.name + "/own", str(Path(__file__).resolve().parents[1] / "web"), password="one-password-123")
        self.port = self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        phone = Phone(self.port)
        self.assertNotIn("public", phone.ask("/api/session")[1])
        self.assertEqual(phone.ask("/api/space", {})[0], 401)
        self.assertEqual(phone.ask("/c/" + "a" * 20 + "/api/session")[0], 404)
        status, _, reply = phone.ask("/api/login", {"password": "one-password-123"})
        self.assertEqual(status, 200)
        self.assertIn("Path=/;", reply.getheader("Set-Cookie"))

    def test_well_known_discovery_and_routes_work_for_single_and_public_spaces(self):
        env_id = "e" * 32
        made = self.space("9.9.9.9")
        space_phone = Phone(self.port, made["space"])
        status, before, _ = space_phone.ask("/.well-known/remote-ai")
        self.assertEqual(status, 200)
        self.assertEqual((before["service"], before["environment_id"], before["online"]), ("remote-ai", "", False))
        self.assertIn("routes", before["capabilities"])
        self.assertEqual(space_phone.ask("/api/login", {"password": made["password"]})[0], 200)
        agent_info = {
            "instance": "b" * 32, "environment_id": env_id, "enabled": True, "features": ["settings", "routes"],
            "routes": [
                {"kind": "tunnel", "url": "https://demo.trycloudflare.com", "priority": 30},
                {"kind": "lan", "url": "http://192.168.1.9:8722", "priority": 10, "local_auth": True},
                {"kind": "tailscale", "url": "http://100.80.1.2:8722", "priority": 20, "local_auth": True},
                {"kind": "lan", "url": "http://192.168.1.9:8722", "priority": 10},
                {"kind": "bad", "url": "http://192.168.1.9:8722", "priority": 5},
                {"kind": "lan", "url": "javascript:alert(1)", "priority": 5},
            ],
        }
        status, _, _ = space_phone.ask("/api/terminal/agent", {"info": agent_info})
        self.assertEqual(status, 200)
        status, after, _ = Phone(self.port, made["space"]).ask("/.well-known/remote-ai")
        self.assertEqual(status, 200)
        self.assertEqual((after["environment_id"], after["online"]), (env_id, True))
        _, overview, _ = space_phone.ask("/api/terminal")
        self.assertEqual(overview["device"]["environment_id"], env_id)
        self.assertEqual([(r["kind"], r["url"], r.get("local_auth", False)) for r in overview["device"]["routes"]], [
            ("lan", "http://192.168.1.9:8722", True),
            ("tailscale", "http://100.80.1.2:8722", True),
            ("tunnel", "https://demo.trycloudflare.com", False),
        ])
        # environment_id persists across a Relay reload
        reloaded = server.relay.Relay(self.server.spaces.get(made["space"][3:]).store)
        self.assertEqual(reloaded.discovery()["environment_id"], env_id)
        # routes_read is accepted by /api/computer
        answered = {}
        def ask_routes():
            answered["reply"] = space_phone.ask("/api/computer", {"id": "1" * 32, "action": "routes_read"})
        worker = threading.Thread(target=ask_routes)
        worker.start()
        _, pulled, _ = space_phone.ask("/api/terminal/agent/pull", {"instance": "b" * 32, "wait": 5})
        self.assertEqual(pulled["operations"][0]["action"], "routes_read")
        space_phone.ask("/api/terminal/agent", {"info": agent_info, "acks": [{"id": "1" * 32, "result": {"environment_id": env_id, "local_password": "secret"}}]})
        worker.join(timeout=5)
        self.assertEqual(answered["reply"][0], 200)
        self.assertEqual(answered["reply"][1]["environment_id"], env_id)


if __name__ == "__main__":
    unittest.main()
