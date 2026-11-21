import base64
import io
import json
import socket
import struct
import sys
import tempfile
import threading
import unittest
from pathlib import Path

import relay as tr
import server
import websocket as framing

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tests"))
from tunnel_check import Client, operation
from ws_client import Socket


def frame(data, opcode=1, final=True):
    mask = b"abcd"
    length = len(data)
    head = bytes(((128 if final else 0) | opcode, 128 | length)) if length < 126 else bytes(((128 if final else 0) | opcode, 254)) + struct.pack("!H", length)
    return head + mask + bytes(b ^ mask[n % 4] for n, b in enumerate(data))


class Sink:
    def __init__(self):
        self.frames = []

    def sendall(self, data):
        self.frames.append(data)


class FramingTests(unittest.TestCase):
    def test_fragmented_unicode_and_interleaved_ping(self):
        data = json.dumps({"data": "中文"}, ensure_ascii=False).encode("utf-8")
        sink = Sink()
        connection = framing.Connection(io.BytesIO(frame(data[:11], final=False) + frame(b"alive", opcode=9) + frame(data[11:], opcode=0)), sink)
        self.assertEqual(json.loads(connection.receive()), {"data": "中文"})
        self.assertEqual(sink.frames, [b"\x8a\x05alive"])

    def test_rejects_unmasked_oversize_invalid_utf8_and_control_frames(self):
        cases = [(b"\x81\x01x", 1002), (b"\x81\xff" + struct.pack("!Q", framing.MAX_MESSAGE + 1), 1009),
                 (frame(b"\xff"), 1007), (frame(b"x", opcode=9, final=False), 1002),
                 (frame(b"x", opcode=0), 1002), (frame(b"x", opcode=2), 1003),
                 (frame(struct.pack("!H", 1006), opcode=8), 1002)]
        for data, code in cases:
            with self.subTest(code=code, data=data[:2]):
                with self.assertRaises(framing.ProtocolError) as found:
                    framing.Connection(io.BytesIO(data), Sink()).receive()
                self.assertEqual(found.exception.code, code)

    def test_compression_is_answered_only_as_offered_and_marked_only_where_agreed(self):
        import zlib
        offer = lambda text: framing.deflate({"Sec-WebSocket-Extensions": text})
        self.assertIsNone(framing.deflate({}))
        self.assertIsNone(offer("x-webkit-deflate-frame"))
        self.assertEqual(offer("permessage-deflate; client_max_window_bits"), ("permessage-deflate; client_no_context_takeover", 15, False))
        self.assertEqual(offer("permessage-deflate; server_no_context_takeover; server_max_window_bits=10")[1:], (10, True))
        self.assertIsNone(offer("permessage-deflate; server_max_window_bits=8"))            # a window this cannot keep
        self.assertEqual(offer("permessage-deflate; made_up, permessage-deflate")[0], "permessage-deflate; client_no_context_takeover")
        # what is sent: long text is compressed and builds on what went before; short text and pings are not
        sink, drawn = Sink(), {"data": "\x1b[5;1H\x1b[2Kline of a screen that is drawn again and again " * 40}
        channel = framing.Connection(io.BytesIO(), sink, ("permessage-deflate", 15, False))
        channel.send(drawn); channel.send(drawn); channel.send({"t": "ack"}); channel.send(b"", opcode=9)
        self.assertEqual([sent[0] for sent in sink.frames], [0xc1, 0xc1, 0x81, 0x89])
        self.assertLess(len(sink.frames[0]), 300)                           # 2400 characters that repeat
        self.assertLess(len(sink.frames[1]), len(sink.frames[0]) // 2)      # the same again: next to nothing
        unpacker = zlib.decompressobj(-15)
        body = lambda sent: sent[4:] if sent[1] == 126 else sent[2:]
        for sent in sink.frames[:2]:
            self.assertEqual(json.loads(unpacker.decompress(body(sent) + framing.TAIL)), drawn)
        # what is read: a compressed message, whole or in two frames; never one that was not agreed or is too long
        packer = zlib.compressobj(6, zlib.DEFLATED, -15)
        text = json.dumps({"data": "中文 " * 200}, ensure_ascii=False).encode("utf-8")
        packed = (packer.compress(text) + packer.flush(zlib.Z_SYNC_FLUSH))[:-4]
        marked = lambda data, opcode=1, final=True: bytes((frame(data, opcode, final)[0] | 0x40,)) + frame(data, opcode, final)[1:]
        agreed = lambda data: framing.Connection(io.BytesIO(data), Sink(), ("permessage-deflate", 15, False))
        self.assertEqual(agreed(marked(packed)).receive().encode("utf-8"), text)
        self.assertEqual(agreed(marked(packed[:9], final=False) + frame(packed[9:], opcode=0)).receive().encode("utf-8"), text)
        self.assertEqual(json.loads(agreed(frame(b'{"plain":1}')).receive()), {"plain": 1})
        long = zlib.compressobj(6, zlib.DEFLATED, -15)
        bomb = (long.compress(b"x" * (framing.MAX_MESSAGE + 10)) + long.flush(zlib.Z_SYNC_FLUSH))[:-4]
        for data, code, connection in ((marked(packed), 1002, lambda d: framing.Connection(io.BytesIO(d), Sink())), (marked(b"x", opcode=9), 1002, agreed),
                                       (marked(packed[:9], final=False) + marked(packed[9:], opcode=0), 1002, agreed), (marked(b"\xff\xff\xff"), 1007, agreed), (marked(bomb), 1009, agreed)):
            with self.subTest(code=code, length=len(data)):
                with self.assertRaises(framing.ProtocolError) as found:
                    connection(data).receive()
                self.assertEqual(found.exception.code, code)

    def test_extended_output_length_and_standard_accept_key(self):
        headers = {"Upgrade": "websocket", "Connection": "keep-alive, Upgrade", "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": "dGhlIHNhbXBsZSBub25jZQ=="}
        self.assertEqual(framing.accept_key(headers), "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=")
        for key in ("bad", base64.b64encode(b"short").decode()):
            with self.assertRaises(framing.ProtocolError):
                framing.accept_key(dict(headers, **{"Sec-WebSocket-Key": key}))
        sink = Sink()
        framing.Connection(io.BytesIO(), sink).send({"data": "x" * 180000})
        self.assertEqual(sink.frames[0][:2], b"\x81\x7f")
        self.assertEqual(struct.unpack("!Q", sink.frames[0][2:10])[0], len(sink.frames[0]) - 10)


class WebSocketIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.server = server.make_server("127.0.0.1", 0, self.temp.name, str(Path(__file__).resolve().parents[1] / "web"), password="test-password-1234")
        self.relay = self.server.space.relay
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.origin = "http://127.0.0.1:%d" % self.server.server_port
        self.client = Client(self.origin, timeout=3)
        self.client.call("/api/login", {"password": "test-password-1234"})
        self.info = {"instance": "b" * 32, "enabled": True, "tools": ["shell"], "workspaces": ["demo"]}
        self.relay.agent({"info": self.info})
        start = operation("", "start", tool="shell", dir="demo")
        self.terminal = self.relay.command(start)["terminal"]
        self.live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]}
        self.relay.agent(dict(self.live, acks=[{"id": start["id"]}]))
        self.sockets = []

    def test_a_ticket_signs_in_once_and_only_for_a_minute(self):
        ticket = self.client.call("/api/ticket", {})["ticket"]
        fresh = Client(self.origin, timeout=3)
        try:
            self.assertRaisesRegex(RuntimeError, "HTTP 401", fresh.call, "/api/terminal")
            self.assertTrue(fresh.call("/api/login", {"ticket": ticket})["ok"])
            self.assertIn("device", fresh.call("/api/terminal", None))
        finally:
            fresh.close()
        again = Client(self.origin, timeout=3)
        try:
            self.assertRaisesRegex(RuntimeError, "HTTP 401", again.call, "/api/login", {"ticket": ticket})       # used up
            # a request that names a ticket is judged by the ticket alone
            self.assertRaisesRegex(RuntimeError, "HTTP 401", again.call, "/api/login", {"ticket": "", "password": "wrong"})
            self.assertRaisesRegex(RuntimeError, "HTTP 401", again.call, "/api/ticket", {})       # only for someone signed in
        finally:
            again.close()
        tickets = self.server.tickets
        late = tickets.make()
        tickets.until = {key: 0 for key in tickets.until}
        self.assertFalse(tickets.take(late))

    def tearDown(self):
        for ws in self.sockets:
            ws.close()
        self.client.close()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(3)
        self.temp.cleanup()

    def connect(self, after=0, cookie=None):
        ws = Socket(self.origin, "/api/terminal/ws?terminal=%s&after=%d" % (self.terminal, after), self.client.cookie["Cookie"] if cookie is None else cookie, timeout=3)
        self.sockets.append(ws)
        return ws

    def next_type(self, ws, kind):
        for _ in range(10):
            item = ws.receive(3)
            if item.get("t") == kind:
                return item
        self.fail("message type did not arrive")

    def test_push_input_pipeline_and_retry_keep_order_without_duplicates(self):
        ws = self.connect()
        self.assertEqual(ws.status, 101)
        self.assertEqual(ws.receive()["t"], "out")
        ops = [operation(self.terminal, "input", data=text) for text in ("a", "中", "\r")]
        for op in ops:
            ws.send(op)
        acks = [self.next_type(ws, "ack") for _ in ops]
        self.assertEqual([a["id"] for a in acks], [o["id"] for o in ops])
        ws.send(ops[0])
        self.assertEqual(self.next_type(ws, "ack")["id"], ops[0]["id"])
        queued = self.relay.pull({"instance": self.info["instance"], "wait": 0})["operations"]
        self.assertEqual([o["data"] for o in queued], ["a", "中", "\r"])
        self.relay.agent(dict(self.live, output=[{"terminal": self.terminal, "seq": 1, "data": "实时输出"}]))
        self.assertEqual(self.next_type(ws, "out")["chunks"], [{"seq": 1, "data": "实时输出"}])
        resumed = self.connect(after=1)
        self.assertEqual(resumed.receive()["chunks"], [])
        self.client.call("/api/terminal", ops[0])
        self.assertEqual(len(self.relay._pending), 4)  # one start and three inputs, across both transports

    def test_no_auth_or_cross_origin_or_missing_origin_cannot_upgrade(self):
        self.assertEqual(self.connect(cookie="").status, 401)
        headers = {"Cookie": self.client.cookie["Cookie"], "Upgrade": "websocket", "Connection": "Upgrade",
                   "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": base64.b64encode(b"a" * 16).decode()}
        for origin in (None, "https://other.example"):
            connection = self.client.connect()
            extra = dict(headers)
            if origin:
                extra["Origin"] = origin
            connection.request("GET", "/api/terminal/ws?terminal=" + self.terminal, headers=extra)
            reply = connection.getresponse()
            self.assertEqual(reply.status, 403)
            reply.read()

    def test_revoked_cookie_cannot_send_operations(self):
        ws = self.connect()
        ws.receive()
        self.client.call("/api/logout", {})
        ws.send(operation(self.terminal, "input", data="should not execute"))
        with self.assertRaises(ConnectionError):
            ws.receive(3)
        self.assertEqual(len(self.relay._pending), 1)

    def test_connection_cannot_target_a_different_terminal(self):
        ws = self.connect()
        ws.receive()
        ws.send(operation("c" * 32, "input", data="should not execute"))
        with self.assertRaises(ConnectionError):
            ws.receive(3)
        self.assertEqual(len(self.relay._pending), 1)

    def test_pages_are_kept_by_the_reader_and_text_is_sent_compressed(self):
        import gzip
        import http.client
        port = self.server.server_port

        def get(path, **headers):
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
            try:
                connection.request("GET", path, headers=dict(headers, **self.client.cookie))
                reply = connection.getresponse()
                return reply.status, {k.lower(): v for k, v in reply.getheaders()}, reply.read()
            finally:
                connection.close()

        source = (Path(__file__).resolve().parents[1] / "web" / "terminal" / "vendor" / "xterm.js").read_bytes()
        status, plain, body = get("/terminal/vendor/xterm.js")
        self.assertEqual((status, body, plain["cache-control"], "content-encoding" in plain), (200, source, "no-cache", False))
        status, packed, body = get("/terminal/vendor/xterm.js", **{"Accept-Encoding": "gzip, br"})
        self.assertEqual((status, packed["content-encoding"], gzip.decompress(body)), (200, "gzip", source))
        self.assertLess(len(body), len(source) // 3)
        self.assertNotEqual(packed["etag"], plain["etag"])
        # a copy the reader holds is confirmed without being sent again; another copy is not
        for headers, tag in (({}, plain["etag"]), ({"Accept-Encoding": "gzip"}, packed["etag"])):
            status, kept, body = get("/terminal/vendor/xterm.js", **dict(headers, **{"If-None-Match": tag}))
            self.assertEqual((status, body, kept["etag"], kept["x-content-type-options"]), (304, b"", tag, "nosniff"))
        self.assertEqual(get("/terminal/vendor/xterm.js", **{"If-None-Match": '"older"'})[0], 200)
        # a page names its files by what they hold; asked for that way they are kept for good, and only that way
        import re
        page = get("/terminal/")[2].decode("utf-8")
        named = re.search(r'src="(vendor/xterm\.js\?v=[0-9a-f]{12})"', page).group(1)
        self.assertRegex(page, r'url\("vendor/jetbrains-mono-400\.woff2\?v=[0-9a-f]{12}"\)')
        favicon = re.search(r'href="\.\./(favicon\.svg\?v=[0-9a-f]{12})"', page)
        self.assertIsNotNone(favicon, page)
        fav_status, fav_headers, fav_body = get("/" + favicon.group(1))
        self.assertEqual((fav_status, fav_headers["content-type"], fav_headers["cache-control"]), (200, "image/svg+xml", "public, max-age=31536000, immutable"))
        self.assertIn(b"<svg", fav_body)
        status, kept, body = get("/terminal/" + named)
        self.assertEqual((status, body, kept["cache-control"]), (200, source, "public, max-age=31536000, immutable"))
        self.assertEqual(get("/terminal/vendor/xterm.js?v=000000000000")[1]["cache-control"], "no-cache")
        self.assertEqual(get("/terminal/")[1]["cache-control"], "no-cache")
        self.assertIn('src="app.js?v=', get("/")[2].decode("utf-8"))
        self.assertIn('href="../app.css?v=', get("/files/")[2].decode("utf-8"))
        # a font is compressed already
        self.assertNotIn("content-encoding", get("/terminal/vendor/jetbrains-mono-400.woff2", **{"Accept-Encoding": "gzip"})[1])
        # what a terminal printed is never kept by the reader, and a long answer is compressed for one that asks
        self.relay.agent(dict(self.live, output=[{"terminal": self.terminal, "seq": 1, "data": "\x1b[2K\x1b[1Gline of output\r\n" * 400}]))
        status, headers, body = get("/api/terminal?terminal=%s&after=0" % self.terminal, **{"Accept-Encoding": "gzip"})
        self.assertEqual((status, headers["cache-control"], headers["content-encoding"]), (200, "no-store", "gzip"))
        self.assertEqual(len(json.loads(gzip.decompress(body))["chunks"][0]["data"]), 400 * 24)
        status, headers, body = get("/api/terminal?terminal=%s&after=0" % self.terminal)
        self.assertEqual((headers["cache-control"], "content-encoding" in headers, len(json.loads(body)["chunks"][0]["data"])), ("no-store", False, 400 * 24))

    def test_a_reader_that_offers_compression_gets_the_same_output_in_far_fewer_bytes(self):
        plain = self.connect()
        packed = Socket(self.origin, "/api/terminal/ws?terminal=%s&after=0" % self.terminal, self.client.cookie["Cookie"], timeout=3, deflate=True)
        self.sockets.append(packed)
        self.assertEqual((plain.packed, packed.packed), (False, True))
        plain.receive(); packed.receive()
        # a screen drawn again and again, as a fullscreen program does while it is scrolled
        screens = ["".join("\x1b[%d;1H\x1b[2Kline %03d of the picture  中文" % (row + 1, top + row) for row in range(30)) for top in range(40)]
        for seq, screen in enumerate(screens, 1):
            self.relay.agent(dict(self.live, output=[{"terminal": self.terminal, "seq": seq, "data": screen}]))
        got = {}
        for name, ws in (("plain", plain), ("packed", packed)):
            text, last = "", 0
            while last < 40:
                item = ws.receive(3)
                if item.get("t") == "out":
                    text += "".join(chunk["data"] for chunk in item["chunks"])
                    last = item["after"]
            got[name] = text
        self.assertEqual(got["packed"], got["plain"])
        self.assertEqual(got["plain"], "".join(screens))
        self.assertLess(packed.wire, plain.wire // 8)
        # what the reader sends compressed is carried out like any other
        for ws in (plain, packed):
            ws.send(operation(self.terminal, "input", data="echo " + "long input " * 20))
            self.assertEqual(self.next_type(ws, "ack")["status"], 200)
        self.assertEqual(len(self.relay._pending), 3)           # the start of this terminal and the two inputs

    def test_invalid_input_returns_ack_and_keeps_connection_open(self):
        ws = self.connect()
        ws.receive()
        ws.send(operation(self.terminal, "input", data=""))
        self.assertEqual(self.next_type(ws, "ack")["status"], 400)
        ws.send(operation(self.terminal, "input", data="ok"))
        self.assertEqual(self.next_type(ws, "ack")["status"], 200)

    def test_gzip_post_agent_upload_and_bomb_guard(self):
        import gzip, http.client
        from urllib.parse import urlsplit
        parsed = urlsplit(self.origin)

        def post_raw(path, raw, encoding=None):
            conn = http.client.HTTPConnection(parsed.hostname, parsed.port, timeout=3)
            headers = {"Content-Type": "application/json", "Origin": self.origin, **self.client.cookie}
            if encoding:
                headers["Content-Encoding"] = encoding
            conn.request("POST", path, body=raw, headers=headers)
            reply = conn.getresponse()
            body = reply.read()
            hdrs = {k.lower(): v for k, v in reply.getheaders()}
            conn.close()
            return reply.status, hdrs, body

        # Plain agent report advertises gzip capability for updated agents.
        plain_body = json.dumps(self.live).encode("utf-8")
        status, hdrs, _ = post_raw("/api/terminal/agent", plain_body)
        self.assertEqual(status, 200)
        self.assertEqual(hdrs.get("x-remotecli-accept-encoding"), "gzip")

        # Gzipped agent report delivers large ANSI output cleanly.
        big_text = ("\x1b[32mhello world\x1b[0m\r\n" * 400)
        gz_payload = gzip.compress(json.dumps(dict(self.live, output=[{"terminal": self.terminal, "seq": 1, "data": big_text}])).encode("utf-8"))
        status, _, body = post_raw("/api/terminal/agent", gz_payload, encoding="gzip")
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(body)["output_ack"][self.terminal], 1)
        self.assertEqual(self.relay.overview(self.terminal, 0)["chunks"][0]["data"], big_text)

        # Corrupt gzip and gzip bomb (> BODY_LIMIT) are rejected safely.
        self.assertEqual(post_raw("/api/terminal/agent", b"not-gzip", encoding="gzip")[0], 400)
        bomb = gzip.compress(b" " * (server.BODY_LIMIT + 1024))
        self.assertEqual(post_raw("/api/terminal/agent", bomb, encoding="gzip")[0], 413)
        self.assertEqual(post_raw("/api/terminal/agent", plain_body, encoding="br")[0], 400)

    def test_large_unaligned_websocket_unmask_and_split_wakeups(self):
        # Fast big-int unmasking must match byte-by-byte XOR for every alignment (1..15 and >10KB).
        ws = self.connect()
        ws.receive()
        for sample in ("a", "ab", "abc", "abcd", "中文测试123", "x" * 12003):
            op = operation(self.terminal, "input", data=sample)
            ws.send(op)
            self.assertEqual(self.next_type(ws, "ack")["status"], 200)
        # Keystrokes wake pull() promptly via _ops_changed, and output wakes stream() via _changed.
        pulled = self.relay.pull({"instance": self.info["instance"], "wait": 0.2})
        self.assertEqual([op["data"] for op in pulled["operations"][-6:]], ["a", "ab", "abc", "abcd", "中文测试123", "x" * 12003])
        # _asks cache invalidates when new output arrives.
        term = self.relay._state()["threads"][self.terminal]
        self.assertFalse(tr._asks(term))
        self.relay.agent(dict(self.live, output=[{"terminal": self.terminal, "seq": 1, "data": "Allow command? [y/n]"}]))
        self.assertTrue(tr._asks(term))
        self.relay.agent(dict(self.live, output=[{"terminal": self.terminal, "seq": 2, "data": "x" * 7000}]))
        self.assertFalse(tr._asks(term))


if __name__ == "__main__":
    unittest.main()
