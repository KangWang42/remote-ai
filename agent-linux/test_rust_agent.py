"""The Rust agent, held to the same end-to-end contract as the Python one: the real relay
runs as a subprocess, this test speaks the viewer protocol, and the binary under test is
agent-rust/target/release/remote-cli-agent. Build it first:

    cargo build --release --manifest-path ../agent-rust/Cargo.toml
"""
import base64
import os
import secrets
import subprocess
import tempfile
import time
import unittest

import test_agent
from test_agent import plain, prompted, request

HERE = os.path.dirname(os.path.abspath(__file__))
BINARY = os.path.join(HERE, "..", "agent-rust", "target", "release", "remote-cli-agent")


@unittest.skipUnless(os.path.isfile(BINARY), "build the Rust agent first: cargo build --release (agent-rust/)")
class RustAgentContract(test_agent.EndToEnd):
    """Held to the terminal-and-files contract. The Python agent's evolved flow on the
    inherited class additionally covers peek and saved-session resume, which the Rust
    agent does not implement yet."""

    @classmethod
    def agent_command(cls, agent_data):
        return [BINARY, "--data", agent_data]

    @unittest.skip("the Rust agent runs shell only; tools beyond it are the Python agent's")
    def test_a_tool_beyond_the_first_three_is_started_and_closed(self):
        pass

    def test_end_to_end(self):
        # 0. the binary under test is the Rust agent
        started = self.op({"action": "start", "tool": "shell", "dir": "项目"})
        self.assertEqual(started["state"], "queued", started)
        terminal = started["terminal"]
        after, text = 0, ""
        deadline = time.time() + 40
        while time.time() < deadline:
            status, view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                                   % (self.base, terminal, after), token=self.token)
            self.assertEqual(status, 200, view)
            for chunk in view["chunks"]:
                text += chunk["data"]
                after = chunk["seq"]
            if prompted(text):
                break
        self.assertTrue(prompted(text), "no shell prompt arrived")
        self.assertEqual(self.overview()["terminals"][0]["state"], "running")

        # 2. text round trip: the phone types, the shell answers
        self.op({"action": "input", "terminal": terminal, "data": "echo E2EOK_$((21*2))\n"})
        deadline = time.time() + 30
        while time.time() < deadline and "E2EOK_42" not in plain(text):
            status, view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                                   % (self.base, terminal, after), token=self.token)
            for chunk in view.get("chunks", []):
                text += chunk["data"]
                after = chunk["seq"]
            time.sleep(0.2)
        self.assertIn("E2EOK_42", plain(text), "the shell never answered")

        # 3. resize reaches the program; the probe is typed once
        self.op({"action": "resize", "terminal": terminal, "cols": 100, "rows": 30})
        self.op({"action": "input", "terminal": terminal,
                 "data": "python3 -c \"import shutil;print('COLS_'+str(shutil.get_terminal_size().columns))\"\n"})
        deadline = time.time() + 40
        while time.time() < deadline and "COLS_100" not in plain(text):
            status, view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                                   % (self.base, terminal, after), token=self.token)
            for chunk in view.get("chunks", []):
                text += chunk["data"]
                after = chunk["seq"]
            time.sleep(0.3)
        self.assertIn("COLS_100", plain(text), "resize did not reach the terminal")

        # 4. a graceful exit is reported with its code
        self.op({"action": "input", "terminal": terminal, "data": "exit\n"})
        view = self.poll(lambda: self.overview(),
                         lambda v: v["terminals"] and v["terminals"][0]["state"] == "closed")
        self.assertEqual(view["terminals"][0].get("exit_code"), 0)

        # 5. projects added from the phone appear as workspaces, rename and remove follow
        fresh = os.path.join(self.tmp, "新建项目")
        self.op({"action": "project_add", "path": fresh, "name": "新增", "create": True})
        self.poll(lambda: self.overview(), lambda v: "新增" in v["device"]["workspaces"])
        self.assertTrue(os.path.isdir(fresh), "the agent did not create the folder")
        self.op({"action": "project_rename", "name": "新增", "to": "改名"})
        self.poll(lambda: self.overview(),
                  lambda v: "改名" in v["device"]["workspaces"] and "新增" not in v["device"]["workspaces"])
        self.op({"action": "project_remove", "name": "改名"})
        self.poll(lambda: self.overview(), lambda v: "改名" not in v["device"]["workspaces"])

        # 6. a folder that is not a project never becomes a terminal: the relay refuses
        # it at the door (the agent checks again itself, but this cannot even reach it)
        status, refused = request("POST", self.base + "/api/terminal",
                                  {"id": secrets.token_hex(16), "action": "start",
                                   "tool": "shell", "dir": "其他"}, token=self.token)
        self.assertEqual(status, 400)
        self.assertIn("工具或目录", refused["error"])

    def test_exit_code_is_preserved(self):
        # a non-zero exit keeps its code: exercises the other half of the waitpid decode
        started = self.op({"action": "start", "tool": "shell", "dir": "项目"})
        terminal = started["terminal"]
        self.poll(
            lambda: self.overview(),
            lambda v: any(t["id"] == terminal and t["state"] == "running"
                          for t in v["terminals"]))
        self.op({"action": "input", "terminal": terminal, "data": "exit 7\n"})
        view = self.poll(
            lambda: self.overview(),
            lambda v: any(t["id"] == terminal and t["state"] == "closed"
                          for t in v["terminals"]))
        code = next(t["exit_code"] for t in view["terminals"] if t["id"] == terminal)
        self.assertEqual(code, 7)

    def test_device_info_contract(self):
        # the phone builds its device card from these; drift must not stay green
        device = self.overview()["device"]
        self.assertTrue(device.get("version"), "the agent reports no version")
        self.assertTrue(device.get("shell"), "the agent reports no shell")

    def test_file_browsing(self):
        # the files feature flag, a folder listing, one file piece, and a path that
        # leaves the project refused — the phone's whole file flow, asked of /api/files
        # the way the app asks
        device = self.overview()["device"]
        self.assertIn("files", device.get("features", []))
        status, listed = request("POST", self.base + "/api/files",
                                 {"id": secrets.token_hex(16), "action": "file_list",
                                  "dir": "项目", "path": ""}, token=self.token)
        self.assertEqual(status, 200, listed)
        self.assertIn("说明.txt", [e["name"] for e in listed["entries"]])
        self.assertTrue(listed["entries"][0]["size"] > 0)
        self.assertFalse(listed["more"])
        status, piece = request("POST", self.base + "/api/files",
                                {"id": secrets.token_hex(16), "action": "file_read",
                                 "dir": "项目", "path": "说明.txt", "offset": 0}, token=self.token)
        self.assertEqual(status, 200, piece)
        self.assertEqual(base64.b64decode(piece["data"]).decode("utf-8"), "文件内容 e2e\n")
        self.assertTrue(piece["end"])
        status, refused = request("POST", self.base + "/api/files",
                                  {"id": secrets.token_hex(16), "action": "file_read",
                                   "dir": "项目", "path": "../../etc/passwd"}, token=self.token)
        self.assertEqual(status, 400, refused)
        self.assertIn("项目文件夹", refused["error"])


@unittest.skipUnless(os.path.isfile(BINARY), "build the Rust agent first: cargo build --release (agent-rust/)")
class RustSingletonLock(unittest.TestCase):
    """The Rust binary holds the same agent.lock as the Python agent."""

    def test_second_instance_exits_3(self):
        import fcntl
        data = tempfile.mkdtemp()
        lock = open(os.path.join(data, "agent.lock"), "w")
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            proc = subprocess.run([BINARY, "--data", data], capture_output=True, timeout=30)
            self.assertEqual(proc.returncode, 3)
            self.assertIn("agent.lock", proc.stderr.decode("utf-8"))
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)
            lock.close()


if __name__ == "__main__":
    unittest.main()
