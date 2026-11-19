"""Bounded RFC 6455 text messages over an already authenticated HTTP connection, compressed when the reader offers it."""
import base64
import hashlib
import json
import struct
import threading
import zlib

MAX_MESSAGE = 128_000
# What a terminal draws repeats itself from one piece to the next: with the compressor kept for the whole
# connection (RFC 7692) a screen drawn again costs a fraction of its length on the way to the phone.
TAIL = bytes((0, 0, 255, 255))


class ProtocolError(ValueError):
    def __init__(self, code=1002):
        self.code = code


def accept_key(headers):
    if (headers.get("Upgrade", "").lower() != "websocket"
            or "upgrade" not in [part.strip().lower() for part in headers.get("Connection", "").split(",")]
            or headers.get("Sec-WebSocket-Version") != "13"):
        raise ProtocolError()
    key = headers.get("Sec-WebSocket-Key", "")
    try:
        if len(base64.b64decode(key, validate=True)) != 16:
            raise ProtocolError()
    except (ValueError, UnicodeError):
        raise ProtocolError() from None
    return base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode("ascii")).digest()).decode("ascii")


def deflate(headers):
    """The answer to an offer of message compression: the header to send back and the window to compress with, or
    nothing when the reader did not offer it. The reader is asked to compress each of its messages on its own."""
    for offer in headers.get("Sec-WebSocket-Extensions", "").split(","):
        name, *given = [part.strip().lower() for part in offer.split(";")]
        if name != "permessage-deflate":
            continue
        answer, bits, fresh = ["permessage-deflate", "client_no_context_takeover"], 15, False
        for item in given:
            key, _, value = item.partition("=")
            value = value.strip('"')
            if key == "server_no_context_takeover" and not value:
                fresh = True
                answer.append(key)
            elif key == "server_max_window_bits" and value.isdigit() and 9 <= int(value) <= 15:
                bits = int(value)
                answer.append("%s=%d" % (key, bits))
            elif key not in ("client_no_context_takeover", "client_max_window_bits"):
                break           # an offer this cannot keep: the next one, or none
        else:
            return "; ".join(answer), bits, fresh
    return None


class Connection:
    def __init__(self, reader, socket, compression=None):
        self.reader, self.socket = reader, socket
        self.lock = threading.Lock()
        self.closed = threading.Event()
        self.compression, self.packer = compression, None

    def send(self, payload, opcode=1):
        if opcode == 1:
            payload = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        with self.lock:
            if self.closed.is_set():
                raise ConnectionError("WebSocket closed")
            first = 0x80 | opcode
            # Compressed in the order they are sent: each message builds on the ones before it.
            if self.compression and opcode == 1 and len(payload) >= 96:
                if self.packer is None or self.compression[2]:
                    self.packer = zlib.compressobj(2, zlib.DEFLATED, -self.compression[1])
                payload = (self.packer.compress(payload) + self.packer.flush(zlib.Z_SYNC_FLUSH))[:-4]
                first |= 0x40
            length = len(payload)
            head = bytes((first, length)) if length < 126 else (
                bytes((first, 126)) + struct.pack("!H", length) if length <= 65535
                else bytes((first, 127)) + struct.pack("!Q", length))
            self.socket.sendall(head + payload)

    def close(self, code=1000):
        try:
            self.send(struct.pack("!H", code), opcode=8)
        except OSError:
            pass
        self.closed.set()

    def read(self, size):
        data = self.reader.read(size)
        if len(data) != size:
            raise ConnectionError("WebSocket closed")
        return data

    def receive(self):
        message, active, packed = bytearray(), False, False
        while True:
            first, second = self.read(2)
            final, opcode, length = bool(first & 128), first & 15, second & 127
            # Only the first frame of a message says that it is compressed, and only when that was agreed.
            if first & 48 or not second & 128 or (first & 64 and not (self.compression and opcode == 1)):
                raise ProtocolError()
            if opcode >= 8 and (not final or length > 125):
                raise ProtocolError()
            if length == 126:
                length = struct.unpack("!H", self.read(2))[0]
                if length < 126:
                    raise ProtocolError()
            elif length == 127:
                length = struct.unpack("!Q", self.read(8))[0]
                if length < 65536 or length >> 63:
                    raise ProtocolError()
            if length > MAX_MESSAGE or (opcode < 8 and len(message) + length > MAX_MESSAGE):
                raise ProtocolError(1009)
            mask = self.read(4)
            raw = self.read(length)
            if length:
                full_mask = (mask * ((length + 3) // 4))[:length]
                data = (int.from_bytes(raw, "big") ^ int.from_bytes(full_mask, "big")).to_bytes(length, "big")
            else:
                data = b""
            if opcode == 8:
                if len(data) == 1:
                    raise ProtocolError()
                if len(data) >= 2:
                    code = struct.unpack("!H", data[:2])[0]
                    if code not in (1000, 1001, 1002, 1003, 1007, 1008, 1009, 1010, 1011, 1012, 1013, 1014) and not 3000 <= code < 5000:
                        raise ProtocolError()
                    try:
                        data[2:].decode("utf-8")
                    except UnicodeError:
                        raise ProtocolError(1007) from None
                self.close()
                raise ConnectionError("WebSocket closed")
            if opcode == 9:
                self.send(data, opcode=10)
                continue
            if opcode == 10:
                continue
            if opcode not in (0, 1):
                raise ProtocolError(1003)
            if (opcode == 1 and active) or (opcode == 0 and not active):
                raise ProtocolError()
            active, packed = True, packed or bool(first & 64)
            message.extend(data)
            if final:
                if packed:
                    try:
                        unpacker = zlib.decompressobj(-15)
                        message = unpacker.decompress(bytes(message) + TAIL, MAX_MESSAGE + 1)
                    except zlib.error:
                        raise ProtocolError(1007) from None
                    if len(message) > MAX_MESSAGE or unpacker.unconsumed_tail:
                        raise ProtocolError(1009)
                try:
                    return message.decode("utf-8")
                except UnicodeError:
                    raise ProtocolError(1007) from None
