"""Runs a program that speaks JSON lines on its standard streams (an ACP server) and writes every line that passes,
either way, to a file: what a live check reads afterwards to see what was asked and in what order.

    python acp_record_proxy.py <record file> <program> [arguments...]
"""
import json
import subprocess
import sys
import threading

record, program = sys.argv[1], sys.argv[2:]
child = subprocess.Popen(program, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
lock = threading.Lock()


def note(way, raw):
    with lock, open(record, "a", encoding="utf-8") as out:
        out.write(json.dumps({"way": way, "line": raw.decode("utf-8", "replace").strip()}, ensure_ascii=False) + "\n")


def back():
    for raw in child.stdout:
        note("out", raw)
        sys.stdout.buffer.write(raw)
        sys.stdout.buffer.flush()


threading.Thread(target=back, daemon=True).start()
for raw in sys.stdin.buffer:
    note("in", raw)
    child.stdin.write(raw)
    child.stdin.flush()
child.stdin.close()
child.wait()
