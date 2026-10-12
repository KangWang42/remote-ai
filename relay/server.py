"""remote-cli relay: one small HTTP server between the phone and the computer.

    python server.py --host 127.0.0.1 --port 8722 --data ./data

It serves the web client, checks the password, and passes terminal input and output along. It never runs
anything itself: the program on the computer does. Standard library only.

Put it behind HTTPS (a reverse proxy or a tunnel) whenever it is reachable from outside your own network.
"""
import argparse
import gzip
import hashlib
import hmac
import json
import os
import re
import secrets
import shutil
import socket
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))   # an embedded Python does not add the script's folder
import relay  # noqa: E402
import websocket  # noqa: E402

VERSION = "1.5.7"
SESSION_DAYS = 90
LOGIN_TRIES, LOGIN_LOCK, LOGIN_TRIES_ALL = 6, 900, 40
BODY_LIMIT = 4 * 1024 * 1024
# A relay for everyone ("--public") keeps a space for each computer under /c/<id>/.
SPACE = re.compile(r"^/c/([a-f0-9]{20})(/.*)?$")
SPACES, SPACE_DAYS, SPACES_A_DAY = 100, 30, 6      # computers at most; days a space is kept unused; new spaces a day from one address
TYPES = {".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8",
         ".woff2": "font/woff2", ".bcmap": "application/octet-stream", ".json": "application/json; charset=utf-8", ".svg": "image/svg+xml", ".png": "image/png",
         ".ico": "image/x-icon", ".webmanifest": "application/manifest+json"}
SECURITY = {"X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer", "X-Frame-Options": "DENY",
            "Content-Security-Policy": "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; "
                                       "media-src 'self' blob:; worker-src 'self' blob:; "
                                       "font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"}


# The pages are the same files until the program on the computer is updated. A phone keeps them and asks only
# whether they have changed, and text is sent compressed: opening a terminal no longer fetches the terminal's
# program again, which over a tunnel took longer than everything else on the page.
PACKED = (".html", ".js", ".css", ".json", ".svg", ".webmanifest")
_pages = {}         # file -> ((time changed, size), tag, content, compressed content or None)
_pages_lock = threading.Lock()


def _page(full):
    stat = os.stat(full)
    mark = (stat.st_mtime_ns, stat.st_size)
    with _pages_lock:
        kept = _pages.get(full)
    if not kept or kept[0] != mark:
        with open(full, "rb") as stream:
            data = stream.read()
        packed = gzip.compress(data, 6, mtime=0) if full.lower().endswith(PACKED) and len(data) > 1024 else None
        kept = (mark, hashlib.sha256(data).hexdigest()[:24], data, packed)
        with _pages_lock:
            _pages[full] = kept
    return kept


# A page names its own files with a mark of what they hold ("app.js?v=3f9a..."). A file asked for by its mark never
# changes, so the phone keeps it for good and opens the page again without asking about each of its files; a file
# that did change has a new mark in the next page.
_LINK = re.compile(r'((?:src|href)="|url\(")([^"?#:]+\.(?:js|css|woff2|png|svg|ico|webmanifest))(")')


def _marked(full, root):
    """An HTML page with the marks of its files: its tag, its content, and its content compressed."""
    folder = os.path.dirname(full)

    def mark(found):
        target = os.path.normpath(os.path.join(folder, *found.group(2).split("/")))
        if not target.startswith(root + os.sep) or not os.path.isfile(target):
            return found.group(0)
        return "%s%s?v=%s%s" % (found.group(1), found.group(2), _page(target)[1][:12], found.group(3))

    body = _LINK.sub(mark, _page(full)[2].decode("utf-8")).encode("utf-8")
    return hashlib.sha256(body).hexdigest()[:24], body, gzip.compress(body, 6, mtime=0)


class Sessions:
    """Sign-ins that survive a restart. Only a hash of each token is kept."""

    def __init__(self, path):
        self.path, self.lock = path, threading.Lock()
        try:
            with open(path, encoding="utf-8") as stream:
                self.items = {k: float(v) for k, v in json.load(stream).items()}
        except (OSError, ValueError, AttributeError):
            self.items = {}

    def _save(self):
        tmp = self.path + ".tmp"
        with os.fdopen(os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w", encoding="utf-8") as stream:
            json.dump(self.items, stream)
        os.replace(tmp, self.path)

    def create(self):
        token = secrets.token_hex(32)
        with self.lock:
            now = time.time()
            self.items = {k: v for k, v in self.items.items() if v > now}
            self.items[hashlib.sha256(token.encode()).hexdigest()] = now + SESSION_DAYS * 86400
            self._save()
        return token

    def valid(self, token):
        if not token or len(token) != 64:
            return False
        key = hashlib.sha256(token.encode()).hexdigest()
        with self.lock:
            expires = self.items.get(key, 0)
            now = time.time()
            if expires <= now:
                return False
            if expires - now < (SESSION_DAYS - 1) * 86400:      # used: good for another full period
                self.items[key] = now + SESSION_DAYS * 86400
                self._save()
            return True

    def remove(self, token):
        with self.lock:
            if self.items.pop(hashlib.sha256((token or "").encode()).hexdigest(), None) is not None:
                self._save()


class Attempts:
    """Wrong passwords: a limit per address and one for all addresses together."""

    def __init__(self):
        self.lock, self.failed = threading.Lock(), {}

    def _recent(self, key, now):
        self.failed[key] = [t for t in self.failed.get(key, []) if now - t < LOGIN_LOCK]
        return self.failed[key]

    def blocked(self, address):
        with self.lock:
            now = time.time()
            return len(self._recent(address, now)) >= LOGIN_TRIES or len(self._recent("*", now)) >= LOGIN_TRIES_ALL

    def fail(self, address):
        with self.lock:
            now = time.time()
            self._recent(address, now).append(now)
            self._recent("*", now).append(now)
            for key in [k for k, v in self.failed.items() if not v]:
                del self.failed[key]

    def clear(self, address):
        with self.lock:
            self.failed.pop(address, None)


def read_password(data):
    """The password from RCLI_PASSWORD, else from <data>/password.txt; one is made on first start."""
    given = os.environ.get("RCLI_PASSWORD", "").strip()
    path = os.path.join(data, "password.txt")
    if given:
        return given, False
    try:
        with open(path, encoding="utf-8") as stream:
            saved = stream.read().strip()
        if saved:
            return saved, False
    except OSError:
        pass
    made = "-".join(secrets.token_hex(3) for _ in range(4))
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w", encoding="utf-8") as stream:
        stream.write(made + "\n")
    return made, True


class Tickets:
    """Sign-ins handed out to someone who is signed in already, each good once and for a minute. The program on
    the computer uses one to open its own pages in a browser window without the password ever being in an address."""

    def __init__(self):
        self.lock = threading.Lock()
        self.until = {}

    def make(self):
        ticket = secrets.token_urlsafe(24)
        with self.lock:
            now = time.time()
            for old in [key for key, end in self.until.items() if end < now]:
                del self.until[old]
            if len(self.until) > 200:
                self.until.clear()
            self.until[hashlib.sha256(ticket.encode("utf-8")).hexdigest()] = now + 60
        return ticket

    def take(self, ticket):
        if not isinstance(ticket, str) or not ticket:
            return False
        with self.lock:
            end = self.until.pop(hashlib.sha256(ticket.encode("utf-8")).hexdigest(), 0)
        return end >= time.time()


class Space:
    """What one computer and its phones share: a password, the sign-ins, and the terminals (a relay.Relay)."""

    def __init__(self, folder, password="", digest="", prefix=""):
        os.makedirs(folder, exist_ok=True)
        self.folder, self.prefix = folder, prefix
        self.password, self.digest = password, digest
        self.sessions = Sessions(os.path.join(folder, "sessions.json"))
        self.attempts, self.tickets = Attempts(), Tickets()
        self.store = os.path.join(folder, "terminals.json")
        self.relay = relay.Relay(self.store)

    def accepts(self, given):
        if not isinstance(given, str):
            return False
        if self.digest:         # a space keeps a hash only: its password is known to the computer that asked for it
            return hmac.compare_digest(hashlib.sha256(given.encode("utf-8")).hexdigest(), self.digest)
        return hmac.compare_digest(given.encode("utf-8"), self.password.encode("utf-8"))


class Refused(Exception):
    """A space that is not made: the status to answer with, and the reason for the person."""

    def __init__(self, status, reason):
        super().__init__(reason)
        self.status = status


class Spaces:
    """A relay for everyone: every computer has a space of its own under /c/<id>/, made when its program asks, with
    a password that only it and its phones know. A space has its own sign-ins and its own terminals, so nothing of
    one can be read or reached from another, and one busy space does not hold up another; the relay has no list of
    them to show. A space nobody has used for a month is removed."""

    def __init__(self, data, key="", most=SPACES, days=SPACE_DAYS):
        self.folder, self.key, self.most, self.days = os.path.join(data, "spaces"), key, most, days
        os.makedirs(self.folder, exist_ok=True)
        self.lock, self.open, self.made, self.swept = threading.Lock(), {}, {}, 0

    def get(self, key):
        with self.lock:
            space = self.open.get(key)
            if space is None:
                folder = os.path.join(self.folder, key)
                try:
                    with open(os.path.join(folder, "space.json"), encoding="utf-8") as stream:
                        digest = json.load(stream)["password"]
                    if not isinstance(digest, str) or len(digest) != 64:
                        return None
                except (OSError, ValueError, KeyError, TypeError):
                    return None
                space = self.open[key] = Space(folder, digest=digest, prefix="/c/" + key)
            return space

    def _used(self, key):
        folder = os.path.join(self.folder, key)
        try:        # signing in, a day of use and every change of a terminal write a file here
            return max(os.stat(os.path.join(folder, name)).st_mtime for name in ("space.json", "sessions.json", "terminals.json") if os.path.exists(os.path.join(folder, name)))
        except (OSError, ValueError):
            return 0

    def sweep(self, now=None):
        """Removes the spaces nobody has used for `days`. Called with the lock held, at most once an hour."""
        now = time.time() if now is None else now
        self.swept = now
        for key in os.listdir(self.folder):
            if SPACE.match("/c/" + key) and now - self._used(key) > self.days * 86400:
                self.open.pop(key, None)
                shutil.rmtree(os.path.join(self.folder, key), ignore_errors=True)

    def create(self, address, key=""):
        """A new space: its id and its password, which is told once and kept here as a hash."""
        with self.lock:
            now = time.time()
            if self.key and not (isinstance(key, str) and hmac.compare_digest(key.encode("utf-8"), self.key.encode("utf-8"))):
                raise Refused(403, "这个公共中转需要口令，电脑端提供的口令不对")
            if now - self.swept > 3600:
                self.sweep(now)
            recent = self.made[address] = [t for t in self.made.get(address, []) if now - t < 86400]
            if len(recent) >= SPACES_A_DAY:
                raise Refused(429, "这个地址今天申请得太多了，请明天再试")
            if sum(1 for name in os.listdir(self.folder) if SPACE.match("/c/" + name)) >= self.most:
                raise Refused(503, "这个公共中转的名额已满，请换一个中转或稍后再试")
            recent.append(now)
            name, password = secrets.token_hex(10), "-".join(secrets.token_hex(3) for _ in range(4))
            folder = os.path.join(self.folder, name)
            os.makedirs(folder)
            with os.fdopen(os.open(os.path.join(folder, "space.json"), os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w", encoding="utf-8") as stream:
                json.dump({"password": hashlib.sha256(password.encode("utf-8")).hexdigest(), "created": int(now)}, stream)
            return name, password


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "remote-cli"       # the version is told to who asks a relay or a space for it, not in every answer
    sys_version = ""

    def setup(self):
        super().setup()
        # Headers and small terminal updates must not wait for a delayed TCP acknowledgment.
        self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

    def handle(self):
        try:
            super().handle()
        except (ConnectionError, TimeoutError):
            pass  # readers can cancel a held request when the app goes into the background

    def log_message(self, *args):       # terminal input and output must never reach a log
        pass

    # ---- helpers
    def _address(self):
        peer = self.client_address[0]
        if peer in ("127.0.0.1", "::1"):                     # a proxy or tunnel on this machine
            forwarded = self.headers.get("CF-Connecting-IP") or (self.headers.get("X-Forwarded-For") or "").split(",")[0]
            return forwarded.strip()[:64] or peer
        return peer

    def _secure(self):
        return self.headers.get("X-Forwarded-Proto", "").lower() == "https" or self.headers.get("CF-Visitor", "").find("https") >= 0

    def _token(self):
        header = self.headers.get("Authorization", "")
        if header.startswith("Bearer "):
            return header[7:].strip()
        for part in self.headers.get("Cookie", "").split(";"):
            name, _, value = part.strip().partition("=")
            if name == "rcli":
                return value
        return ""

    def _authed(self):
        return self.space.sessions.valid(self._token())

    def _enter(self, path, payload=None):
        """Finds the space a request is for. Returns the path inside it, or None when the request is answered
        already: a relay for everyone has nothing at its top but its version and the making of a space."""
        spaces = self.server.spaces
        if spaces is None:
            self.space = self.server.space
            return path
        found = SPACE.match(path)
        if found:
            self.space = spaces.get(found.group(1))
            if self.space is None:
                self._json(404, {"error": "space"})
            elif found.group(2) is None:        # the pages name their files relative to the folder
                self.send_response(308)
                self.send_header("Location", path + "/")
                self.send_header("Content-Length", "0")
                self.end_headers()
            else:
                return found.group(2)
        elif path == "/api/session" and payload is None:
            # no version here: whoever is not in a space learns that this is a relay, and nothing about it
            self._json(200, {"signed_in": False, "public": True, "key": bool(spaces.key)})
        elif path == "/api/space" and payload is not None:
            try:
                name, password = spaces.create(self._address(), payload.get("key", ""))
                self._json(200, {"space": "/c/" + name, "password": password})
            except Refused as refused:
                self._json(refused.status, {"error": str(refused)})
            except OSError:
                self._json(500, {"error": "中转没能保存新的空间"})
        elif path in ("", "/") and payload is None:
            self._send(200, "Remote AI relay\n", "text/plain; charset=utf-8")
        else:
            self._json(404, {"error": "not found"})
        return None

    def _send(self, code, body, kind="application/json; charset=utf-8", extra=None):
        data = body if isinstance(body, bytes) else body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(data)))
        for key, value in dict(SECURITY, **{"Cache-Control": "no-store", **(extra or {})}).items():
            self.send_header(key, value)
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(data)

    def _gzip(self):
        return "gzip" in self.headers.get("Accept-Encoding", "").lower()

    def _json(self, code, value, extra=None):
        data = json.dumps(value, ensure_ascii=False).encode("utf-8")
        # What a reader asks for (the lists, a terminal's output) is long and repeats itself; what it sends is short.
        if self.command == "GET" and len(data) > 2048 and self._gzip():
            data, extra = gzip.compress(data, 3, mtime=0), dict(extra or {}, **{"Content-Encoding": "gzip", "Vary": "Accept-Encoding"})
        self._send(code, data, extra=extra)

    def _error(self, exc, fallback):
        self._json(400, {"error": str(exc) if isinstance(exc, relay.RemoteError) else fallback})

    def _static(self, path, mark=""):
        root = self.server.web
        name = "index.html" if path in ("", "/") else path.lstrip("/")
        if name.endswith("/"):
            name += "index.html"
        full = os.path.normpath(os.path.join(root, *name.split("/")))
        kind = TYPES.get(os.path.splitext(full)[1].lower())
        if not full.startswith(root + os.sep) or kind is None or not os.path.isfile(full):
            return self._json(404, {"error": "not found"})
        _, tag, data, packed = _page(full)
        if full.lower().endswith(".html"):
            tag, data, packed = _marked(full, root)
        # Asked for by the mark of what it holds now, a file is kept for good; otherwise the reader asks each time.
        kept = "public, max-age=31536000, immutable" if mark and mark == tag[:12] else "no-cache"
        headers = {"Cache-Control": kept, "Vary": "Accept-Encoding"}
        if packed and self._gzip():
            data, tag = packed, tag + "-gz"
            headers["Content-Encoding"] = "gzip"
        headers["ETag"] = '"%s"' % tag
        if headers["ETag"] in self.headers.get("If-None-Match", ""):
            self.send_response(304)
            for key, value in dict(SECURITY, **headers).items():
                self.send_header(key, value)
            return self.end_headers()
        self._send(200, data, kind, headers)

    # ---- routes
    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        url = urlsplit(self.path)
        path, query = url.path, parse_qs(url.query)
        first = lambda key, default="": (query.get(key) or [default])[0]
        path = self._enter(path)
        if path is None:
            return
        terminals = self.space.relay
        if not path.startswith("/api/"):
            return self._static(path, first("v"))
        if path == "/api/session":
            return self._json(200, {"signed_in": self._authed(), "version": VERSION})
        if not self._authed():
            return self._json(401, {"error": "auth"})
        if path == "/api/terminal/ws":
            return self._websocket(first("terminal"), first("after", "0"))
        try:
            if path == "/api/terminal":
                wait = max(0.0, min(25.0, float(first("wait", "0"))))
                return self._json(200, terminals.overview(first("terminal"), int(first("after", "0")), wait=wait))
            if path == "/api/terminal/stream":
                lines = terminals.stream(first("terminal"), int(first("after", "0")))
                item = next(lines)
            else:
                return self._json(404, {"error": "not found"})
        except StopIteration:
            return self._json(400, {"error": "终端参数无效"})
        except (ValueError, TypeError) as exc:
            return self._error(exc, "终端参数无效")
        # One response kept open: a line is written and flushed whenever the terminal produces output.
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-store, no-transform")
        self.send_header("X-Accel-Buffering", "no")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True
        try:
            while True:
                self.wfile.write(b"data: " + json.dumps(item, ensure_ascii=False).encode("utf-8") + b"\n\n")
                self.wfile.flush()
                item = next(lines)
        except (StopIteration, OSError, ValueError):
            pass        # finished, the reader went away, or the terminal was removed meanwhile

    def _websocket(self, terminal, after):
        origin = urlsplit(self.headers.get("Origin", ""))
        if origin.scheme not in ("http", "https") or origin.netloc.lower() != self.headers.get("Host", "").lower():
            return self._json(403, {"error": "origin"})
        try:
            if self.command != "GET":
                raise ValueError
            key = websocket.accept_key(self.headers)
            after = int(after)
            terminals = self.space.relay
            lines = terminals.stream(terminal, after, seconds=3600)
            first = next(lines)
        except (ValueError, StopIteration):
            return self._json(400, {"error": "WebSocket 参数无效"})
        self.send_response(101)
        self.send_header("Upgrade", "websocket")
        self.send_header("Connection", "Upgrade")
        self.send_header("Sec-WebSocket-Accept", key)
        compression = websocket.deflate(self.headers)
        if compression:
            self.send_header("Sec-WebSocket-Extensions", compression[0])
        self.end_headers()
        self.close_connection = True
        self.connection.settimeout(35)
        channel = websocket.Connection(self.rfile, self.connection, compression)
        channel.send(dict(first, t="out"))

        def output():
            last_ping = time.monotonic()
            try:
                for item in lines:
                    if channel.closed.is_set():
                        return
                    if not self._authed():
                        channel.close(1008)
                        return
                    channel.send(dict(item, t="out"))
                    if time.monotonic() - last_ping >= 15:
                        channel.send(b"", opcode=9)  # browsers answer pong even when the user is idle
                        last_ping = time.monotonic()
                channel.close(1001)
            except (OSError, ValueError):
                channel.close(1011)
            finally:
                channel.closed.set()
                try:
                    self.connection.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass

        threading.Thread(target=output, daemon=True).start()
        try:
            while not channel.closed.is_set():
                raw = channel.receive()
                if not self._authed():
                    channel.close(1008)
                    break
                payload = json.loads(raw)
                if not isinstance(payload, dict) or payload.get("terminal") != terminal or payload.get("action") not in ("input", "resize", "close", "rename"):
                    raise relay.RemoteError("终端操作无效")
                try:
                    result = terminals.command(payload)
                    channel.send(dict(result, t="ack", status=200))
                except relay.RemoteError as error:
                    channel.send({"t": "ack", "id": payload.get("id"), "status": 400, "state": "error", "error": str(error)})
                except OSError:
                    channel.send({"t": "ack", "id": payload.get("id"), "status": 500, "error": "终端记录保存失败"})
        except websocket.ProtocolError as error:
            channel.close(error.code)
        except (ValueError, TypeError):
            channel.close(1007)
        except OSError:
            pass
        finally:
            channel.closed.set()

    def do_POST(self):
        path = urlsplit(self.path).path
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = -1
        if length < 0 or length > BODY_LIMIT:
            self.close_connection = True
            return self._json(413, {"error": "请求过大"})
        raw = self.rfile.read(length)
        # A page on another site cannot send JSON with our cookie: the content type forces a preflight that is
        # never answered, and a browser's Origin must be this server.
        origin = self.headers.get("Origin")
        if not self.headers.get("Content-Type", "").lower().startswith("application/json") or (
                origin and urlsplit(origin).netloc.lower() != (self.headers.get("Host") or "").lower()):
            return self._json(403, {"error": "origin"})
        try:
            payload = json.loads(raw.decode("utf-8") or "{}")
            if not isinstance(payload, dict):
                raise ValueError
        except (ValueError, UnicodeDecodeError):
            return self._json(400, {"error": "请求格式无效"})
        path = self._enter(path, payload)
        if path is None:
            return
        space, terminals = self.space, self.space.relay
        if path == "/api/login":
            address, attempts = self._address(), space.attempts
            if attempts.blocked(address):
                return self._json(429, {"error": "密码错误次数过多，请 15 分钟后再试"})
            given = payload.get("password")
            if "ticket" in payload:
                accepted = space.tickets.take(payload.get("ticket"))
            else:
                accepted = space.accepts(given)
            if not accepted:
                attempts.fail(address)
                time.sleep(0.4)
                return self._json(401, {"error": "密码不正确"})
            attempts.clear(address)
            token = space.sessions.create()
            # In a space the cookie is for that space's pages only: a phone may know several computers at one relay.
            cookie = "rcli=%s; Path=%s; Max-Age=%d; HttpOnly; SameSite=Strict%s" % (token, space.prefix or "/", SESSION_DAYS * 86400, "; Secure" if self._secure() else "")
            return self._json(200, {"ok": True, "token": token}, {"Set-Cookie": cookie})
        if path == "/api/logout":
            space.sessions.remove(self._token())
            return self._json(200, {"ok": True}, {"Set-Cookie": "rcli=; Path=%s; Max-Age=0; HttpOnly; SameSite=Strict" % (space.prefix or "/")})
        if not self._authed():
            return self._json(401, {"error": "auth"})
        if path == "/api/ticket":
            return self._json(200, {"ticket": space.tickets.make()})
        call = {"/api/terminal": terminals.command, "/api/terminal/agent": terminals.agent, "/api/terminal/agent/pull": terminals.pull, "/api/files": terminals.files,
                "/api/conversation": terminals.conversation, "/api/computer": terminals.computer}.get(path)
        if call is None:
            return self._json(404, {"error": "not found"})
        try:
            return self._json(200, call(payload))
        except (ValueError, TypeError) as exc:
            return self._error(exc, "请求格式无效")
        except OSError:
            return self._json(500, {"error": "终端记录保存失败"})


class Server(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 64


def make_server(host, port, data, web, password=None, public=False, key="", most=SPACES):
    """One computer's relay with one password, or with `public` a relay for everyone, which has no password of its
    own: every computer is given a space (see Spaces). `key`, when set, must be given to have a space made."""
    os.makedirs(data, exist_ok=True)
    server = Server((host, port), Handler)
    server.web = os.path.abspath(web)
    server.spaces, server.space, server.password, server.password_made = None, None, "", False
    if public:
        server.spaces = Spaces(data, key, most)
        return server
    made = False
    if password is None:
        password, made = read_password(data)
    server.password, server.password_made = password, made
    space = server.space = Space(data, password=password)
    server.sessions, server.attempts, server.tickets, server.store = space.sessions, space.attempts, space.tickets, space.store
    return server


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    parser = argparse.ArgumentParser(description="remote-cli relay")
    parser.add_argument("--host", default="127.0.0.1", help="address to listen on; 0.0.0.0 for the local network")
    parser.add_argument("--port", type=int, default=8722)
    parser.add_argument("--data", default=os.path.join(here, "data"), help="folder for the password, sign-ins and terminal state")
    parser.add_argument("--web", default=os.path.join(os.path.dirname(here), "web"), help="folder of the web client")
    parser.add_argument("--public", action="store_true", default=os.environ.get("RCLI_PUBLIC", "") == "1",
                        help="a relay for everyone: no password of its own, every computer is given a space (also RCLI_PUBLIC=1)")
    parser.add_argument("--spaces", type=int, default=int(os.environ.get("RCLI_SPACES", "") or SPACES), help="with --public: how many computers at most")
    args = parser.parse_args()
    server = make_server(args.host, args.port, args.data, args.web, public=args.public, key=os.environ.get("RCLI_JOIN_KEY", "").strip(), most=args.spaces)
    if not args.public and len(server.password) < 12:
        print("warning: the password is shorter than 12 characters", file=sys.stderr)
    print("remote-cli relay %s on http://%s:%d%s" % (VERSION, args.host, args.port, " for everyone, at most %d computers" % args.spaces if args.public else ""), flush=True)
    if server.password_made:
        print("password (also saved in %s): %s" % (os.path.join(args.data, "password.txt"), server.password), flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
