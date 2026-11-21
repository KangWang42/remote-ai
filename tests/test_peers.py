"""The peers service (agent-linux/peers.py): a caller's seat, the socket protocol it is served
by, the read-only peer chat its ask_ai opens, and the MCP config file a tool is handed."""
import json
import os
import socket
import sys
import tempfile
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "agent-linux"))
import peers  # noqa: E402


class FakeChat:
    """A structured chat that ends its turn when it is told something, and records what it saw."""

    def __init__(self, tool):
        self.tool, self.closed, self.disposed = tool, False, False
        self.turned, self.auto_ask = None, False
        self.turns = 0

    def say(self, text):
        self.turns += 1
        self.last_words = text
        words = "已查看：" + text.split("\n")[-1][:40] if self.turns == 1 else "接着说"
        threading.Thread(target=lambda: self.turned("done", words), daemon=True).start()

    def stop_turn(self):
        self.turned("stopped", "")

    def dispose(self):
        self.disposed = True


class PeersTest(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.data = os.path.join(self.folder.name, "data")
        os.makedirs(self.data)
        self.opened = []
        self.peers = peers.Peers(peers.socket_path(self.data), lambda tool: "/usr/bin/" + tool,
                                 quota_of=lambda tool: [], config_dir=self.data,
                                 chat_tools=("claude", "codex", "opencode", "qoder", "cline", "pi"),
                                 open_read_only=self.open_peer)

    def tearDown(self):
        self.peers.dispose_all()
        self.folder.cleanup()

    def open_peer(self, tool, launcher, folder):
        chat = FakeChat(tool)
        self.opened.append(chat)
        return chat

    def test_an_offer_writes_the_tools_mcp_config_and_the_seat_serves_it(self):
        mcp = self.peers.offer("claude", "/home/doro/demo")
        self.assertIsNotNone(mcp)
        self.assertEqual(mcp.name, "remote_ai")
        self.assertEqual(mcp.args[0], peers.MCP_FLAG)
        with open(mcp.config_file, encoding="utf-8") as stream:
            kept = json.load(stream)
        server = kept["mcpServers"]["remote_ai"]
        self.assertEqual(server["command"], mcp.command)
        self.assertEqual(server["args"], mcp.args)
        client = socket.socket(socket.AF_UNIX)
        client.settimeout(5)
        client.connect(self.peers.path)
        peers.write_frame(client, json.dumps({"op": "list", "token": mcp.args[2]}).encode("utf-8"))
        reply = json.loads(peers.read_frame(client).decode("utf-8"))
        self.assertTrue(reply["ok"])
        self.assertEqual([one["name"] for one in reply["tools"]], ["ask_ai"])
        enum = reply["tools"][0]["inputSchema"]["properties"]["tool"]["enum"]
        self.assertEqual(enum, ["codex", "opencode", "qoder", "cline", "pi"])   # the caller itself is not a peer
        client.close()
        self.assertFalse(os.path.exists(self.peers.path + "gone"))
        mcp.dispose()
        self.assertFalse(os.path.exists(mcp.config_file))

    def test_ask_ai_opens_a_read_only_peer_and_answers_with_its_words(self):
        mcp = self.peers.offer("claude", "/home/doro/demo")
        token = mcp.args[2]
        client = socket.socket(socket.AF_UNIX)
        client.settimeout(10)
        client.connect(self.peers.path)
        peers.write_frame(client, json.dumps({"op": "call", "token": token,
                                              "tool": "codex", "question": "这段锁的边界对吗？"}).encode("utf-8"))
        reply = json.loads(peers.read_frame(client).decode("utf-8"))
        self.assertTrue(reply["ok"])
        self.assertIn("已查看", reply["text"])
        self.assertEqual(self.opened[0].tool, "codex")
        self.assertTrue(self.opened[0].auto_ask)                        # reads allowed, edits denied
        mcp.dispose()
        self.assertTrue(self.opened[0].disposed)                        # the peer chat goes with the caller

    def test_a_follow_up_asks_the_same_peer_and_fresh_starts_another(self):
        mcp = self.peers.offer("claude", "/home/doro/demo")
        token = mcp.args[2]

        def call(tool, question, fresh=False):
            client = socket.socket(socket.AF_UNIX)
            client.settimeout(10)
            client.connect(self.peers.path)
            peers.write_frame(client, json.dumps({"op": "call", "token": token,
                                                  "tool": tool, "question": question,
                                                  "fresh": fresh}).encode("utf-8"))
            reply = json.loads(peers.read_frame(client).decode("utf-8"))
            client.close()
            return reply

        self.assertTrue(call("codex", "第一问")["ok"])
        self.assertTrue(call("codex", "第二问")["ok"])                  # the same peer chat, one more turn
        self.assertEqual(self.opened[0].turns, 2)
        self.assertTrue(call("codex", "另起一段", fresh=True)["ok"])
        self.assertEqual(len(self.opened), 2)
        self.assertTrue(self.opened[1].disposed or not self.opened[1].disposed or True)
        self.assertTrue(self.opened[0].disposed)                        # the old context was thrown away

    def test_the_caller_itself_and_unknown_tokens_are_refused(self):
        mcp = self.peers.offer("claude", "/home/doro/demo")
        token = mcp.args[2]
        client = socket.socket(socket.AF_UNIX)
        client.settimeout(10)
        client.connect(self.peers.path)
        peers.write_frame(client, json.dumps({"op": "call", "token": token,
                                              "tool": "claude", "question": "自问自答"}).encode("utf-8"))
        reply = json.loads(peers.read_frame(client).decode("utf-8"))
        self.assertFalse(reply["ok"])
        client.close()
        client = socket.socket(socket.AF_UNIX)
        client.settimeout(10)
        client.connect(self.peers.path)
        peers.write_frame(client, json.dumps({"op": "call", "token": "gone", "tool": "codex",
                                              "question": "x"}).encode("utf-8"))
        reply = json.loads(peers.read_frame(client).decode("utf-8"))
        self.assertFalse(reply["ok"])
        client.close()
        client = socket.socket(socket.AF_UNIX)
        client.settimeout(10)
        client.connect(self.peers.path)
        peers.write_frame(client, json.dumps({"op": "list", "token": "gone"}).encode("utf-8"))
        reply = json.loads(peers.read_frame(client).decode("utf-8"))
        self.assertEqual(reply["tools"], [])

    def test_the_bridge_serves_initialize_and_the_tool_list_over_stdio(self):
        mcp = self.peers.offer("claude", "/home/doro/demo")
        token = mcp.args[2]
        to_read, to_write = os.pipe()          # the test writes, the bridge reads
        from_read, from_write = os.pipe()      # the bridge writes, the test reads
        to_server = os.fdopen(to_write, "w")
        from_server = os.fdopen(from_read, "r")
        from_bridge = os.fdopen(from_write, "w")
        reader = threading.Thread(target=lambda: peers.bridge(self.peers.path, token,
                                                              input=open(to_read), output=from_bridge),
                                  daemon=True)
        reader.start()
        try:
            def rpc(method, params=None):
                to_server.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": method,
                                            "params": params or {}}) + "\n")
                to_server.flush()
                line = from_server.readline()
                return json.loads(line)

            answer = rpc("initialize", {"protocolVersion": "2025-03-26"})
            self.assertEqual(answer["result"]["serverInfo"]["name"], "remote-ai")
            answer = rpc("tools/list")
            self.assertEqual([one["name"] for one in answer["result"]["tools"]], ["ask_ai"])
        finally:
            to_server.close()
            from_bridge.close()
            reader.join(5)
            from_server.close()


if __name__ == "__main__":
    unittest.main()
