import io
import json
import os
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import auto_update as au


def _make_release_tarball(path, version, extra_files=None):
    with tarfile.open(path, "w:gz") as tf:
        files = {
            f"remote-ai-{version}/relay/server.py": f'VERSION = "{version}"\n',
            f"remote-ai-{version}/relay/relay.py": "def ok(): return True\n",
            f"remote-ai-{version}/relay/test_ignored.py": "assert False\n",
            f"remote-ai-{version}/web/index.html": "<html>ok</html>\n",
            f"remote-ai-{version}/web/console/index.html": "<html>console</html>\n",
        }
        if extra_files:
            files.update(extra_files)
        for name, body in files.items():
            raw = body.encode("utf-8")
            info = tarfile.TarInfo(name=name)
            info.size = len(raw)
            tf.addfile(info, io.BytesIO(raw))


class AutoUpdateTests(unittest.TestCase):
    def test_parse_version_only_accepts_formal_semver(self):
        self.assertEqual(au.parse_version("v1.27.0"), (1, 27, 0))
        self.assertEqual(au.parse_version("1.28.2"), (1, 28, 2))
        self.assertIsNone(au.parse_version("v1.28.0-rc1"))
        self.assertIsNone(au.parse_version("main"))
        self.assertIsNone(au.parse_version(""))

    def test_extract_and_verify_skips_tests_and_traversal(self):
        with tempfile.TemporaryDirectory() as tmp:
            tar_path = Path(tmp) / "rel.tar.gz"
            stage = Path(tmp) / "stage"
            _make_release_tarball(
                tar_path,
                "1.28.0",
                extra_files={"remote-ai-1.28.0/relay/../evil.py": "boom"},
            )
            au._safe_extract_release(tar_path, stage)
            self.assertTrue((stage / "relay" / "server.py").is_file())
            self.assertTrue((stage / "web" / "console" / "index.html").is_file())
            self.assertFalse((stage / "relay" / "test_ignored.py").exists())
            self.assertFalse((stage / "evil.py").exists())
            au.verify_staged_release(stage, "1.28.0", sys.executable)
            with self.assertRaises(RuntimeError):
                au.verify_staged_release(stage, "1.29.0", sys.executable)

    def test_prune_old_releases_keeps_newest_three(self):
        with tempfile.TemporaryDirectory() as tmp:
            rel = Path(tmp)
            for ver in ("1.14.1", "1.16.0", "1.26.0", "1.27.0", "1.28.0"):
                (rel / ver).mkdir()
            (rel / "notes").mkdir()
            au.prune_old_releases(rel, keep=3)
            kept = sorted(p.name for p in rel.iterdir())
            self.assertEqual(kept, ["1.26.0", "1.27.0", "1.28.0", "notes"])

    def test_run_update_skips_when_already_latest(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            cur = root / "current" / "relay"
            cur.mkdir(parents=True)
            (cur / "server.py").write_text('VERSION = "1.27.0"\n', encoding="utf-8")
            with mock.patch.object(au, "fetch_latest_release", return_value="1.27.0"):
                rc = au.run_update(root=root)
            self.assertEqual(rc, 0)


if __name__ == "__main__":
    unittest.main()
