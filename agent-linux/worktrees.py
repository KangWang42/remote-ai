"""Isolated Git worktrees for the parallel comparison way of working (agent-windows/Worktrees.cs).

Each candidate AI of a `compare` team works in `<rootDir>/<teamId>/<tool>`, a detached-HEAD
worktree taken from the repository's HEAD when the team started. When the owner compares the
diffs and picks a winner, that worktree's changes — committed, unstaged and untracked — are
checked and applied to the main project folder as one binary-safe patch, and every temporary
worktree of the team is removed. The main folder is left untouched when a conflict exists.
"""
import os
import re
import shutil
import subprocess
import tempfile

SAFE_ID = re.compile(r"\A[a-f0-9]{16,32}\Z")
SAFE_TOOL = re.compile(r"\A[a-z0-9_-]{1,24}\Z")


class OpError(Exception):
    """What the phone is told, in its own words."""


def _run_git_bytes(folder, args, timeout=20):
    """Runs one git command in `folder`, returning raw stdout bytes so patches never lose data."""
    try:
        done = subprocess.run(["git", "-C", folder] + args, capture_output=True, timeout=timeout)
        return done.returncode, done.stdout, done.stderr.decode("utf-8", "replace").strip()
    except FileNotFoundError:
        return -1, b"", "电脑上未找到 git 命令"
    except subprocess.TimeoutExpired:
        return -1, b"", "git 命令超时"


def _run_git(folder, args, timeout=20):
    """Runs one git command in `folder`; a list of words, so no quoting decides what a path means."""
    code, out, err = _run_git_bytes(folder, args, timeout=timeout)
    return code, out.decode("utf-8", "replace"), err


def _try_delete_dir(path):
    if not os.path.isdir(path):
        return
    shutil.rmtree(path, ignore_errors=True)


class Worktrees:
    def __init__(self, root_dir):
        self.root_dir = os.path.abspath(root_dir)

    def contains(self, folder):
        """Whether `folder` lies inside this worktree root (for checking terminal working dirs)."""
        if not folder:
            return False
        real_root = os.path.realpath(self.root_dir)
        real_path = os.path.realpath(folder)
        return real_path != real_root and real_path.startswith(real_root.rstrip(os.sep) + os.sep)

    def create(self, repo_dir, team_id, tool):
        """Creates an isolated detached-HEAD worktree for one candidate tool of a compare task."""
        if not repo_dir or not os.path.isdir(repo_dir):
            raise OpError("项目文件夹不存在")
        if not SAFE_ID.match(team_id or "") or not SAFE_TOOL.match(tool or ""):
            raise OpError("工作区参数无效")

        code, out, err = _run_git(repo_dir, ["rev-parse", "--show-toplevel"])
        if code != 0:
            raise OpError("该项目文件夹不是 Git 仓库，无法创建独立 worktree 进行并行比稿")
        code, out, _ = _run_git(repo_dir, ["rev-parse", "HEAD"])
        if code != 0 or not out.strip():
            raise OpError("该 Git 仓库尚无提交记录，无法创建独立 worktree 进行并行比稿")
        base_commit = out.strip()

        team_folder = os.path.join(self.root_dir, team_id)
        path = os.path.join(team_folder, tool)
        os.makedirs(team_folder, exist_ok=True)
        if os.path.isdir(path):
            _run_git(repo_dir, ["worktree", "remove", "--force", path])
            _try_delete_dir(path)
        code, _, err = _run_git(repo_dir, ["worktree", "add", "--detach", path, base_commit], timeout=30)
        if code != 0 or not os.path.isdir(path):
            raise OpError("未能创建独立工作区：" + (err or "git worktree add 失败"))
        return {"path": path, "base_commit": base_commit}

    def diff(self, worktree_path, base_commit):
        """Human-readable file status and diff of a worktree relative to its starting commit
        (committed, unstaged, and a preview of untracked text files)."""
        if not worktree_path or not os.path.isdir(worktree_path):
            return ""
        target = base_commit.strip() if (base_commit or "").strip() else "HEAD"
        parts = []
        code, status, _ = _run_git(worktree_path, ["-c", "core.quotepath=false", "status", "--porcelain", "--untracked-files=all"])
        if code == 0 and status.strip():
            parts.append("文件：\n" + status.rstrip())
        else:
            code, committed, _ = _run_git(worktree_path, ["-c", "core.quotepath=false", "diff", "--name-status", target])
            if code == 0 and committed.strip():
                parts.append("文件：\n" + committed.rstrip())
        code, diff, _ = _run_git(worktree_path, ["-c", "core.quotepath=false", "diff", target, "--no-color"])
        if code == 0 and diff.strip():
            parts.append(diff.rstrip())
        code, untracked, _ = _run_git(worktree_path, ["-c", "core.quotepath=false", "ls-files", "--others", "--exclude-standard"])
        for rel in (untracked.splitlines() if code == 0 else [])[:8]:
            full = os.path.join(worktree_path, rel.strip())
            if not os.path.isfile(full):
                continue
            try:
                with open(full, "rb") as stream:
                    raw = stream.read()
                if b"\x00" in raw[:4096]:
                    parts.append("+++ " + rel + "（二进制文件，" + str(len(raw)) + " 字节）")
                    continue
                text = raw.decode("utf-8", "replace")
                if len(text) > 4000:
                    text = text[:4000] + "\n…（后文省略）"
                parts.append("+++ 新建文件：" + rel + "\n" + text)
            except OSError:
                pass
        return "\n\n".join(parts)

    def stat(self, worktree_path, base_commit):
        """Short one-line summary of how many files changed in a worktree relative to base_commit."""
        if not worktree_path or not os.path.isdir(worktree_path):
            return "工作区不可用"
        target = base_commit.strip() if (base_commit or "").strip() else "HEAD"
        names = []
        code, diff_names, _ = _run_git(worktree_path, ["-c", "core.quotepath=false", "diff", "--name-only", target])
        if code == 0:
            names.extend(line.strip() for line in diff_names.splitlines() if line.strip())
        code, untracked, _ = _run_git(worktree_path, ["-c", "core.quotepath=false", "ls-files", "--others", "--exclude-standard"])
        if code == 0:
            for line in untracked.splitlines():
                if line.strip() and line.strip() not in names:
                    names.append(line.strip())
        if not names:
            return "没有文件改动"
        short = [name.rsplit("/", 1)[-1] for name in names[:3]]
        return str(len(names)) + " 个文件改动：" + "、".join(short) + (" 等" if len(names) > 3 else "")

    def apply(self, repo_dir, worktree_path, base_commit):
        """Checks and applies all changes from worktree_path (committed, unstaged, and untracked)
        onto repo_dir. Leaves repo_dir untouched when a conflict exists."""
        if not repo_dir or not os.path.isdir(repo_dir):
            raise OpError("主项目文件夹不存在")
        if not worktree_path or not os.path.isdir(worktree_path):
            raise OpError("候选方案的工作区已不存在")
        target = base_commit.strip() if (base_commit or "").strip() else "HEAD"
        stat = self.stat(worktree_path, target)

        code, _, err = _run_git(worktree_path, ["add", "-A"], timeout=30)
        if code != 0:
            raise OpError("未能整理候选工作区的改动：" + err)
        code, patch, err = _run_git_bytes(worktree_path, ["-c", "core.quotepath=false", "diff", "--cached", "--binary", target], timeout=30)
        if code != 0:
            raise OpError("未能生成候选改动补丁：" + err)
        if not patch:
            return "没有文件改动"

        os.makedirs(self.root_dir, exist_ok=True)
        handle, patch_file = tempfile.mkstemp(prefix="apply-", suffix=".patch", dir=self.root_dir)
        with os.fdopen(handle, "wb") as stream:
            stream.write(patch)
        try:
            code, _, err = _run_git(repo_dir, ["apply", "--check", patch_file], timeout=30)
            if code != 0:
                raise OpError("无法直接合并到主目录（存在冲突或主目录已有冲突改动）：" + (err or "git apply --check 失败"))
            code, _, err = _run_git(repo_dir, ["apply", patch_file], timeout=30)
            if code != 0:
                raise OpError("合并补丁失败：" + err)
            return stat
        finally:
            try:
                os.remove(patch_file)
            except OSError:
                pass

    def remove_all(self, repo_dir, team_id):
        """Removes every worktree created for team_id and prunes Git worktree metadata."""
        if not SAFE_ID.match(team_id or ""):
            return
        team_folder = os.path.join(self.root_dir, team_id)
        if os.path.isdir(team_folder):
            for sub in os.listdir(team_folder):
                sub_path = os.path.join(team_folder, sub)
                if repo_dir and os.path.isdir(repo_dir):
                    _run_git(repo_dir, ["worktree", "remove", "--force", sub_path], timeout=15)
                _try_delete_dir(sub_path)
            _try_delete_dir(team_folder)
        if repo_dir and os.path.isdir(repo_dir):
            _run_git(repo_dir, ["worktree", "prune"], timeout=10)


def changes_in(folder):
    """git status and diff in a working directory (empty string if not a repository or clean)."""
    if not folder or not os.path.isdir(folder):
        return ""
    code, files, _ = _run_git(folder, ["-c", "core.quotepath=false", "status", "--porcelain", "--untracked-files=all"], timeout=15)
    if code != 0 or not files.strip():
        return ""
    code, diff, _ = _run_git(folder, ["-c", "core.quotepath=false", "diff", "HEAD", "--no-color"], timeout=15)
    return "文件：\n" + files.rstrip() + "\n\n" + (diff.rstrip() if code == 0 else "")
