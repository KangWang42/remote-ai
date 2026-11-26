"""The peers service (agent-linux/peers.py): a caller's seat, the socket protocol it is served
by, the read-only peer chat its ask_ai opens, and the MCP config file a tool is handed."""
import io
import json
import os
import socket
import sys
import tempfile
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
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
                                 chat_tools=("claude", "codex"),
                                 open_read_only=self.open_peer,
                                 bridge_script="/opt/remote-ai/agent.py")

    def tearDown(self):
        self.peers.dispose_all()
        self.folder.cleanup()

    def open_peer(self, tool, launcher, folder):
        chat = FakeChat(tool)
        self.opened.append(chat)
        return chat

    def _call(self, payload):
        if hasattr(socket, "AF_UNIX") and self.peers.listener is not None:
            with socket.socket(socket.AF_UNIX) as client:
                client.settimeout(10)
                client.connect(self.peers.path)
                peers.write_frame(client, json.dumps(payload).encode("utf-8"))
                return json.loads(peers.read_frame(client).decode("utf-8"))
        return self.peers.handle_request(payload)

    def test_an_offer_writes_the_tools_mcp_config_and_the_seat_serves_it(self):
        mcp = self.peers.offer("claude", "/home/doro/demo")
        self.assertIsNotNone(mcp)
        self.assertEqual(mcp.name, "remote_ai")
        self.assertEqual(mcp.command, sys.executable)
        self.assertEqual(mcp.args[1], peers.MCP_FLAG)
        token = mcp.args[3]
        with open(mcp.config_file, encoding="utf-8") as stream:
            kept = json.load(stream)
        server = kept["mcpServers"]["remote_ai"]
        self.assertEqual(server["command"], mcp.command)
        self.assertEqual(server["args"], mcp.args)
        reply = self._call({"op": "list", "token": token})
        self.assertTrue(reply["ok"])
        self.assertEqual([one["name"] for one in reply["tools"]], ["ask_ai"])
        enum = reply["tools"][0]["inputSchema"]["properties"]["tool"]["enum"]
        self.assertEqual(enum, ["codex"])   # caller itself is excluded; only read-only chat peers are listed
        mcp.dispose()
        self.assertFalse(os.path.exists(mcp.config_file))

    def test_ask_ai_opens_a_read_only_peer_and_answers_with_its_words(self):
        mcp = self.peers.offer("claude", "/home/doro/demo")
        token = mcp.args[3]
        reply = self._call({"op": "call", "token": token,
                            "tool": "codex", "question": "这段锁的边界对吗？"})
        self.assertTrue(reply["ok"])
        self.assertIn("已查看", reply["text"])
        self.assertEqual(self.opened[0].tool, "codex")
        self.assertTrue(self.opened[0].auto_ask)
        mcp.dispose()
        self.assertTrue(self.opened[0].disposed)

    def test_a_follow_up_asks_the_same_peer_and_fresh_starts_another(self):
        mcp = self.peers.offer("claude", "/home/doro/demo")
        token = mcp.args[3]
        self.assertTrue(self._call({"op": "call", "token": token, "tool": "codex", "question": "第一问"})["ok"])
        self.assertTrue(self._call({"op": "call", "token": token, "tool": "codex", "question": "第二问"})["ok"])
        self.assertEqual(self.opened[0].turns, 2)
        self.assertTrue(self._call({"op": "call", "token": token, "tool": "codex", "question": "另起一段", "fresh": True})["ok"])
        self.assertEqual(len(self.opened), 2)
        self.assertTrue(self.opened[0].disposed)
        self.assertFalse(self.opened[1].disposed)

    def test_the_caller_itself_and_unknown_tokens_are_refused(self):
        mcp = self.peers.offer("claude", "/home/doro/demo")
        token = mcp.args[3]
        reply = self._call({"op": "call", "token": token, "tool": "claude", "question": "自问自答"})
        self.assertFalse(reply["ok"])
        reply = self._call({"op": "call", "token": "gone", "tool": "codex", "question": "x"})
        self.assertFalse(reply["ok"])
        reply = self._call({"op": "list", "token": "gone"})
        self.assertEqual(reply["tools"], [])

    def test_bridge_handles_initialize_list_and_call_with_full_timeout(self):
        mcp = self.peers.offer("claude", "/home/doro/demo")
        token = mcp.args[3]
        seen_timeouts = []
        done_call = threading.Event()
        lines = []

        class CaptureWriter(io.StringIO):
            def write(self, text):
                super().write(text)
                if text.strip():
                    lines.append(json.loads(text))
                    if lines[-1].get("id") == 3:
                        done_call.set()
                return len(text)

        def handler(payload, timeout_ms):
            seen_timeouts.append((payload["op"], timeout_ms))
            return self.peers.handle_request(payload)

        requests = "\n".join([
            json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-03-26"}}),
            json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}),
            json.dumps({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {
                "name": "ask_ai", "arguments": {"tool": "codex", "question": "复核这段实现"}}}),
        ]) + "\n"
        peers.bridge(self.peers.path, token, input=io.StringIO(requests), output=CaptureWriter(), call_handler=handler)
        self.assertTrue(done_call.wait(5))
        by_id = {one["id"]: one for one in lines}
        self.assertEqual(by_id[1]["result"]["serverInfo"]["name"], "remote-ai")
        self.assertEqual([t["name"] for t in by_id[2]["result"]["tools"]], ["ask_ai"])
        self.assertFalse(by_id[3]["result"]["isError"])
        self.assertIn("已查看", by_id[3]["result"]["content"][0]["text"])
        call_timeout = next(ms for op, ms in seen_timeouts if op == "call")
        self.assertGreaterEqual(call_timeout, peers.DEFAULT_TIMEOUT_SECONDS * 1000)

    @unittest.skipUnless(hasattr(socket, "AF_UNIX"), "AF_UNIX socket bridge runs on Unix")
    def test_the_bridge_serves_over_unix_socket(self):
        mcp = self.peers.offer("claude", "/home/doro/demo")
        token = mcp.args[3]
        to_read, to_write = os.pipe()
        from_read, from_write = os.pipe()
        to_bridge = os.fdopen(to_read, "r")
        to_server = os.fdopen(to_write, "w")
        from_server = os.fdopen(from_read, "r")
        from_bridge = os.fdopen(from_write, "w")
        reader = threading.Thread(target=lambda: peers.bridge(self.peers.path, token,
                                                              input=to_bridge, output=from_bridge),
                                  daemon=True)
        reader.start()
        try:
            def rpc(method, params=None):
                to_server.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": method,
                                            "params": params or {}}) + "\n")
                to_server.flush()
                return json.loads(from_server.readline())

            answer = rpc("initialize", {"protocolVersion": "2025-03-26"})
            self.assertEqual(answer["result"]["serverInfo"]["name"], "remote-ai")
            answer = rpc("tools/list")
            self.assertEqual([one["name"] for one in answer["result"]["tools"]], ["ask_ai"])
        finally:
            to_server.close()
            from_bridge.close()
            reader.join(5)
            to_bridge.close()
            from_server.close()


if __name__ == "__main__":
    unittest.main()
