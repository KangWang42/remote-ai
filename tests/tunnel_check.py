"""Measure key-to-command-result latency with an isolated relay and Windows agent.

    python tests/tunnel_check.py --local
    python tests/tunnel_check.py <cloudflared.exe> --protocol http2

The held reader waits before input is sent, as in the app. SSE is measured
separately because quick tunnels can buffer it. Uses temporary state and never
prints credentials or terminal content. Build the Windows agent first.
"""
import argparse
import http.client
import json
import os
import re
import secrets
import socket
import statistics
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path
from urllib.parse import urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parent))  # the packaged Python uses an isolated search path
from ws_client import Socket

ROOT = Path(__file__).resolve().parents[1]


class Client:
    def __init__(self, origin, timeout=10):
        self.url = urlsplit(origin)
        self.timeout = timeout
        self.cookie = {}
        self.connection = None

    def connect(self):
        if self.connection is None:
            cls = http.client.HTTPSConnection if self.url.scheme == "https" else http.client.HTTPConnection
            self.connection = cls(self.url.hostname, self.url.port, timeout=self.timeout)
        return self.connection

    def call(self, path, payload=None):
        body = None if payload is None else json.dumps(payload).encode("utf-8")
        headers = dict(self.cookie)
        if body is not None:
            headers["Content-Type"] = "application/json"
        for attempt in range(2):
            try:
                self.connect().request("GET" if body is None else "POST", path, body, headers)
                reply = self.connection.getresponse()
                data = reply.read()
                break
            except (http.client.HTTPException, OSError):
                self.close()
                if attempt:
                    raise
        if reply.getheader("Set-Cookie"):
            self.cookie["Cookie"] = reply.getheader("Set-Cookie").split(";", 1)[0]
        if reply.status != 200:
            raise RuntimeError("%s returned HTTP %d" % (path.split("?", 1)[0], reply.status))
        return json.loads(data)

    def close(self):
        if self.connection is not None:
            self.connection.close()
            self.connection = None


def operation(terminal, action, **fields):
    return dict(fields, id=secrets.token_hex(16), terminal=terminal, action=action)


def await_ready(check, description, seconds=45, interval=0.2):
    deadline = time.monotonic() + seconds
    last = None
    while time.monotonic() < deadline:
        try:
            result = check()
            if result:
                return result
        except (OSError, http.client.HTTPException, RuntimeError) as error:
            last = type(error).__name__
        time.sleep(interval)
    raise TimeoutError("%s not ready%s" % (description, " (%s)" % last if last else ""))


def summary(values):
    good = sorted(value for value in values if value is not None)
    return {"samples_ms": values, "successful": len(good), "attempted": len(values),
            "median_ms": round(statistics.median(good), 2) if good else None,
            "p95_ms": good[max(0, int(len(good) * 0.95 + 0.999) - 1)] if good else None}


class HeldReader:
    def __init__(self, client, terminal, after):
        self.client, self.terminal, self.after = client, terminal, after
        self.condition = threading.Condition()
        self.waiting = threading.Event()
        self.events = []
        self.error = None
        self.stopped = threading.Event()
        self.thread = threading.Thread(target=self.read, daemon=True)

    def read(self):
        path = "/api/terminal?terminal=%s&after=%d&wait=20"
        try:
            while not self.stopped.is_set():
                self.client.connect().request("GET", path % (self.terminal, self.after), headers=self.client.cookie)
                self.waiting.set()
                reply = self.client.connection.getresponse()
                data = reply.read()
                if reply.status != 200:
                    raise RuntimeError("held reader returned HTTP %d" % reply.status)
                item = json.loads(data)
                self.after = item["after"]
                with self.condition:
                    self.events.append(("".join(c["data"] for c in item["chunks"]), time.perf_counter()))
                    self.condition.notify_all()
        except Exception as error:
            with self.condition:
                self.error = type(error).__name__
                self.condition.notify_all()

    def wait_for(self, mark, began, timeout):
        def arrival():
            text = ""
            for data, at in self.events:
                text += data
                if mark in text:
                    return at
            return None

        with self.condition:
            if not self.condition.wait_for(lambda: arrival() is not None or self.error is not None, timeout):
                return None
            at = arrival()
            if at is None:
                raise RuntimeError("held reader failed (%s)" % self.error)
            return round((at - began) * 1000, 2)

    def close(self):
        self.stopped.set()
        connection = self.client.connection
        if connection and connection.sock:
            try:
                connection.sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
        self.thread.join(self.client.timeout + 1)
        self.client.close()
        if self.thread.is_alive():
            raise RuntimeError("held reader did not stop")


def measure(client, terminal, count, timeout, interval):
    after = client.call("/api/terminal?terminal=%s&after=0" % terminal)["after"]
    output = Client(client.url.geturl(), timeout=max(25, timeout))
    output.cookie = dict(client.cookie)
    reader = HeldReader(output, terminal, after)
    reader.thread.start()
    values = []
    try:
        if not reader.waiting.wait(timeout):
            raise TimeoutError("held reader did not start")
        prefix = secrets.token_hex(6)
        # Warm up once; a split expression excludes the echoed command itself.
        for n in range(count + 1):
            mark = "HELD_%s_%d_OK" % (prefix, n)
            began = time.perf_counter()
            client.call("/api/terminal", operation(terminal, "input", data="'HELD_%s_%d'+'_OK'\r" % (prefix, n)))
            elapsed = reader.wait_for(mark, began, timeout)
            if n:
                values.append(elapsed)
            elif elapsed is None:
                raise TimeoutError("PowerShell warm-up did not complete")
            if interval:
                time.sleep(interval)
        return summary(values)
    finally:
        reader.close()


def probe_stream(client, terminal, timeout):
    stream = Client(client.url.geturl(), timeout=timeout)
    stream.cookie = dict(client.cookie)
    began = time.perf_counter()
    try:
        after = client.call("/api/terminal?terminal=%s&after=0" % terminal)["after"]
        began = time.perf_counter()
        stream.connect().request("GET", "/api/terminal/stream?terminal=%s&after=%d" % (terminal, after), headers=stream.cookie)
        reply = stream.connection.getresponse()
        if reply.status != 200:
            return {"status": reply.status}
        line = reply.readline()
        if not line.startswith(b"data:"):
            raise RuntimeError("stream did not return an event")
        first = round((time.perf_counter() - began) * 1000, 2)
        prefix = "STREAM_%s" % secrets.token_hex(6)
        began = time.perf_counter()
        client.call("/api/terminal", operation(terminal, "input", data="'%s'+'_OK'\r" % prefix))
        text = ""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            line = reply.readline()
            if not line:
                break
            if line.startswith(b"data:"):
                text += "".join(c["data"] for c in json.loads(line[5:])["chunks"])
                if prefix + "_OK" in text:
                    return {"first_event_ms": first, "key_to_echo_ms": round((time.perf_counter() - began) * 1000, 2)}
        return {"first_event_ms": first, "error": "echo timeout"}
    except (OSError, RuntimeError, ValueError, http.client.HTTPException) as error:
        return {"error": type(error).__name__, "elapsed_ms": round((time.perf_counter() - began) * 1000, 2)}
    finally:
        stream.close()


def measure_socket(client, terminal, count, timeout, interval):
    after = client.call("/api/terminal?terminal=%s&after=0" % terminal)["after"]
    ws = Socket(client.url.geturl(), "/api/terminal/ws?terminal=%s&after=%d" % (terminal, after), client.cookie["Cookie"], timeout)
    try:
        if ws.status != 101:
            return {"status": ws.status}
        ws.receive(timeout)
        values, prefix = [], secrets.token_hex(6)
        for n in range(count + 1):
            mark = "SOCK_%s_%d_OK" % (prefix, n)
            began, deadline, text = time.perf_counter(), time.monotonic() + timeout, ""
            ws.send(operation(terminal, "input", data="'SOCK_%s_%d'+'_OK'\r" % (prefix, n)))
            elapsed = None
            while time.monotonic() < deadline:
                item = ws.receive(max(0.01, deadline - time.monotonic()))
                if item.get("t") == "out":
                    text += "".join(c["data"] for c in item["chunks"])
                    if mark in text:
                        elapsed = round((time.perf_counter() - began) * 1000, 2)
                        break
                elif item.get("t") == "ack" and item.get("status") != 200:
                    raise RuntimeError("WebSocket operation rejected")
            if n:
                values.append(elapsed)
            elif elapsed is None:
                raise TimeoutError("WebSocket warm-up failed")
            if interval:
                time.sleep(interval)
        return dict(summary(values), status=101)
    finally:
        ws.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("cloudflared", nargs="?")
    parser.add_argument("--local", action="store_true")
    parser.add_argument("--samples", type=int, default=8)
    parser.add_argument("--timeout", type=float, default=12)
    parser.add_argument("--interval", type=float, default=0.2)
    parser.add_argument("--agent", type=Path, default=ROOT / "agent-windows/bin/RemoteCliAgent.exe")
    parser.add_argument("--relay", type=Path, default=ROOT / "relay/server.py")
    args, extra = parser.parse_known_args()
    if not args.local and not args.cloudflared:
        parser.error("provide cloudflared.exe or use --local")
    if args.samples < 1 or args.timeout <= 0 or args.interval < 0:
        parser.error("samples and timeout must be positive; interval cannot be negative")
    if args.local and extra:
        parser.error("extra tunnel arguments are not used in local mode")
    if not args.agent.is_file() or not args.relay.is_file():
        parser.error("build the agent first and check --agent/--relay paths")
    processes, client, local, terminal = [], None, None, None
    hidden = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}
    with tempfile.TemporaryDirectory(prefix="rcli-latency-") as folder:
        try:
            root = Path(folder)
            data, work = root / "agent", root / "work"
            data.mkdir()
            work.mkdir()
            with socket.socket() as reserved:
                reserved.bind(("127.0.0.1", 0))
                port = reserved.getsockname()[1]
            origin = "http://127.0.0.1:%d" % port
            password = "test-" + secrets.token_hex(16)
            (data / "config.json").write_text(json.dumps({"Server": origin, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["demo=" + str(work)]}), encoding="utf-8")
            processes.append(subprocess.Popen([sys.executable, str(args.relay), "--port", str(port), "--data", str(root / "relay")],
                                             env=dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1"), stdout=subprocess.DEVNULL, **hidden))
            local = Client(origin, timeout=2)
            await_ready(lambda: local.call("/api/login", {"password": password}), "relay")
            subprocess.run([str(args.agent), "--set-password", str(data)], input=password.encode(), check=True, timeout=10, **hidden)
            processes.append(subprocess.Popen([str(args.agent), "--data", str(data)], **hidden))
            await_ready(lambda: local.call("/api/terminal")["device"]["workspaces"], "agent")
            found = {"edge": "local"}
            if not args.local:
                tunnel = subprocess.Popen([args.cloudflared, "tunnel", "--no-autoupdate"] + extra + ["--url", origin],
                                          stderr=subprocess.PIPE, stdout=subprocess.DEVNULL, text=True, encoding="utf-8", errors="replace", **hidden)
                processes.append(tunnel)
                got_url = threading.Event()
                registered = threading.Event()

                def watch():
                    for line in tunnel.stderr:
                        match = re.search(r"https://(?!api\.)[a-z0-9-]+\.trycloudflare\.com\b", line)
                        if match:
                            found.setdefault("url", match.group(0))
                            got_url.set()
                        match = re.search(r"location=(\w+).*protocol=(\w+)", line)
                        if match:
                            found["edge"] = " ".join(match.groups())
                        if "Registered tunnel connection" in line:
                            found["registered"] = True
                            registered.set()
                    got_url.set()
                    registered.set()

                watcher = threading.Thread(target=watch, daemon=True)
                watcher.start()
                if not got_url.wait(60) or "url" not in found:
                    raise TimeoutError("no quick tunnel address (cloudflared exit code: %s)" % tunnel.poll())
                if not registered.wait(60) or not found.get("registered") or tunnel.poll() is not None:
                    raise TimeoutError("quick tunnel did not register an edge connection (cloudflared exit code: %s)" % tunnel.poll())
                # Give the newly announced hostname a moment before the first external lookup.
                time.sleep(5)
                origin = found["url"]
            client = Client(origin, timeout=args.timeout)
            await_ready(lambda: client.call("/api/login", {"password": password}), "viewer", seconds=60, interval=1.0 if not args.local else 0.2)
            terminal = client.call("/api/terminal", {"action": "start", "id": secrets.token_hex(16), "tool": "shell", "dir": "demo"})["terminal"]
            await_ready(lambda: client.call("/api/terminal?terminal=%s&after=0" % terminal)["terminal"]["state"] == "running", "terminal")
            began = time.perf_counter()
            client.call("/api/session")
            report = {"mode": "local" if args.local else "quick_tunnel", "edge": found["edge"],
                      "cloudflared_arguments": extra, "one_small_request_ms": round((time.perf_counter() - began) * 1000, 2)}
            report["held_requests"] = measure(client, terminal, args.samples, args.timeout, args.interval)
            report["websocket"] = measure_socket(client, terminal, args.samples, args.timeout, args.interval)
            report["stream"] = probe_stream(client, terminal, min(args.timeout, 5))
            print(json.dumps(report, ensure_ascii=False))
            good_socket = report["websocket"].get("status") == 101 and report["websocket"]["successful"] == args.samples
            return 0 if report["held_requests"]["successful"] == args.samples and good_socket else 1
        finally:
            if terminal and local:
                try:
                    local.call("/api/terminal", operation(terminal, "close"))
                    await_ready(lambda: local.call("/api/terminal?terminal=%s&after=0" % terminal)["terminal"]["state"] == "closed", "terminal cleanup", seconds=5)
                except (OSError, RuntimeError, http.client.HTTPException):
                    pass
            if client:
                client.close()
            if local:
                local.close()
            for process in reversed(processes):
                if process.poll() is None:
                    process.terminate()
                process.wait(timeout=10)
                if process.stderr:
                    process.stderr.close()


if __name__ == "__main__":
    sys.exit(main())
