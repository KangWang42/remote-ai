"""Compile and run isolated Antigravity Desktop ConnectRPC checks. Pass --live-ro for a strict read-only smoke check against the running language_server.exe."""
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_build  # noqa: E402


def make_summary_db(db_path, rows, include_app_data_dir=True, include_source=True):
    db_path.parent.mkdir(parents=True, exist_ok=True)
    con = sqlite3.connect(str(db_path))
    cols = [
        "conversation_id TEXT PRIMARY KEY",
        "title TEXT NOT NULL DEFAULT ''",
        "preview TEXT NOT NULL DEFAULT ''",
        "last_modified_time TEXT NOT NULL",
        "workspace_uris TEXT NOT NULL DEFAULT '[]'",
        "status TEXT NOT NULL DEFAULT ''",
        "parent_conversation_id TEXT NOT NULL DEFAULT ''",
        "not_fully_idle INTEGER NOT NULL DEFAULT 0",
        "killed INTEGER NOT NULL DEFAULT 0",
    ]
    if include_source:
        cols.append("source TEXT NOT NULL DEFAULT ''")
    if include_app_data_dir:
        cols.append("app_data_dir TEXT NOT NULL DEFAULT ''")
    con.execute("CREATE TABLE conversation_summaries (" + ", ".join(cols) + ")")
    for row in rows:
        keys = list(row.keys())
        placeholders = ", ".join("?" for _ in keys)
        con.execute(
            "INSERT INTO conversation_summaries (" + ", ".join(keys) + ") VALUES (" + placeholders + ")",
            [row[k] for k in keys],
        )
    con.commit()
    con.close()


with tempfile.TemporaryDirectory(prefix="remote-ai-agy-desktop-") as folder:
    root = Path(folder)
    ws_uri = '["file:///E:/demo"]'
    conv_offline_db = "11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
    conv_cli_only = "22222222-bbbb-4bbb-8bbb-bbbbbbbbbbbb"

    # Desktop DB contains both a desktop conversation and a mirrored CLI conversation.
    make_summary_db(
        root / "desktop-home" / "conversation_summaries.db",
        [
            {
                "conversation_id": conv_offline_db,
                "title": "离线桌面历史",
                "preview": "离线桌面历史",
                "last_modified_time": "2026-11-20 08:00:00+00:00",
                "workspace_uris": ws_uri,
                "status": "CASCADE_RUN_STATUS_IDLE",
                "source": "",
                "app_data_dir": "antigravity",
            },
            {
                "conversation_id": conv_cli_only,
                "title": "镜像的 CLI 对话",
                "preview": "镜像的 CLI 对话",
                "last_modified_time": "2026-11-21 09:00:00+00:00",
                "workspace_uris": ws_uri,
                "status": "CASCADE_RUN_STATUS_IDLE",
                "source": "CORTEX_TRAJECTORY_SOURCE_CLI",
                "app_data_dir": "antigravity",
            },
        ],
    )

    # CLI DB contains both a CLI conversation (antigravity-cli) and a mirrored desktop conversation (antigravity).
    make_summary_db(
        root / "cli-home" / "conversation_summaries.db",
        [
            {
                "conversation_id": conv_cli_only,
                "title": "真正的 CLI 对话",
                "preview": "真正的 CLI 对话",
                "last_modified_time": "2026-11-21 09:00:00+00:00",
                "workspace_uris": ws_uri,
                "status": "CASCADE_RUN_STATUS_IDLE",
                "source": "",
                "app_data_dir": "antigravity-cli",
            },
            {
                "conversation_id": conv_offline_db,
                "title": "镜像的桌面话",
                "preview": "镜像的桌面话",
                "last_modified_time": "2026-11-21 08:00:00+00:00",
                "workspace_uris": ws_uri,
                "status": "",
                "source": "",
                "app_data_dir": "antigravity",
            },
        ],
    )

    # Legacy CLI DB without app_data_dir/source columns must still work.
    make_summary_db(
        root / "legacy-cli-home" / "conversation_summaries.db",
        [
            {
                "conversation_id": conv_cli_only,
                "title": "旧版 CLI 对话",
                "preview": "旧版 CLI 对话",
                "last_modified_time": "2026-11-21 09:00:00+00:00",
                "workspace_uris": ws_uri,
                "status": "CASCADE_RUN_STATUS_IDLE",
            },
        ],
        include_app_data_dir=False,
        include_source=False,
    )

    exe = root / "check.exe"
    agent_build.compile_check(
        "AntigravityDesktopCheck",
        exe,
        extra=("AntigravityDesktop", agent_build.ROOT / "tests" / "AntigravityDesktopCheck.cs"),
    )
    cmd = [str(exe), str(root)]
    if "--live-ro" in sys.argv[1:]:
        cmd.append("--live-ro")
    subprocess.run(cmd, check=True, timeout=45)
