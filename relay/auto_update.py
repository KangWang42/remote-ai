"""Safe automatic updater for self-hosted and public Remote AI relays.

Checks GitHub's Latest formal release (`vX.Y.Z`), downloads the matching tag archive only when a
strictly newer release is published, compiles and verifies the staged release before atomically
switching `/opt/remote-cli/current`, restarts active relay systemd units, and rolls back
automatically if any health check fails. Standard library only (Python 3.9+).
"""
import argparse
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

DEFAULT_ROOT = Path("/opt/remote-cli")
DEFAULT_API = "https://api.github.com/repos/KangWang42/remote-ai/releases/latest"
DEFAULT_TARBALL = "https://codeload.github.com/KangWang42/remote-ai/tar.gz/refs/tags/v{version}"
DEFAULT_SERVICES = ("remote-cli-relay.service", "remote-cli-public.service")
VERSION_RE = re.compile(r'^VERSION\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"\s*$', re.MULTILINE)
SEMVER_RE = re.compile(r"^v?([0-9]+)\.([0-9]+)\.([0-9]+)$")


def parse_version(text):
    """Parses `X.Y.Z` or `vX.Y.Z` into a 3-int tuple; returns None for invalid or prerelease tags."""
    if not isinstance(text, str):
        return None
    m = SEMVER_RE.match(text.strip())
    if not m:
        return None
    return (int(m.group(1)), int(m.group(2)), int(m.group(3)))


def read_installed_version(root):
    server_py = Path(root) / "current" / "relay" / "server.py"
    try:
        m = VERSION_RE.search(server_py.read_text(encoding="utf-8"))
        return m.group(1) if m else ""
    except OSError:
        return ""


def fetch_latest_release(api_url=DEFAULT_API, timeout=15):
    """Returns the latest formal release version string (`X.Y.Z`) from GitHub Releases API."""
    req = urllib.request.Request(
        api_url,
        headers={
            "Accept": "application/vnd.github+json",
            "User-Agent": "RemoteAI-Relay-Updater",
        },
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        data = json.loads(resp.read().decode("utf-8"))
    if data.get("draft") or data.get("prerelease"):
        return ""
    tag = str(data.get("tag_name") or "").strip()
    parsed = parse_version(tag)
    return "%d.%d.%d" % parsed if parsed else ""


def _safe_extract_release(archive_path, dest_dir):
    """Safely extracts `relay/*.py` (excluding tests) and `web/**` from a GitHub tag tarball."""
    dest = Path(dest_dir)
    relay_dir = dest / "relay"
    web_dir = dest / "web"
    relay_dir.mkdir(parents=True, exist_ok=True)
    web_dir.mkdir(parents=True, exist_ok=True)

    with tarfile.open(archive_path, "r:gz") as tf:
        for member in tf.getmembers():
            if member.issym() or member.islnk():
                continue
            parts = Path(member.name).parts
            if len(parts) < 2 or ".." in parts:
                continue
            sub = parts[1:]
            top = sub[0]
            if top == "relay":
                if len(sub) != 2 or not member.isfile():
                    continue
                fname = sub[1]
                if not fname.endswith(".py") or fname.startswith("test_"):
                    continue
                target = relay_dir / fname
                with tf.extractfile(member) as src, open(target, "wb") as dst:
                    shutil.copyfileobj(src, dst)
                os.chmod(target, 0o644)
            elif top == "web":
                rel = Path(*sub[1:]) if len(sub) > 1 else Path(".")
                target = web_dir / rel
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                    os.chmod(target, 0o755)
                elif member.isfile():
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with tf.extractfile(member) as src, open(target, "wb") as dst:
                        shutil.copyfileobj(src, dst)
                    os.chmod(target, 0o644)

    for root_path, dirs, files in os.walk(dest):
        os.chmod(root_path, 0o755)
        for d in dirs:
            os.chmod(os.path.join(root_path, d), 0o755)
        for f in files:
            os.chmod(os.path.join(root_path, f), 0o644)


def verify_staged_release(stage_dir, expected_version, python_bin):
    stage = Path(stage_dir)
    server_py = stage / "relay" / "server.py"
    console_index = stage / "web" / "console" / "index.html"
    if not server_py.is_file() or not (stage / "web" / "index.html").is_file() or not console_index.is_file():
        raise RuntimeError("staged release missing required relay/ or web/ files")
    m = VERSION_RE.search(server_py.read_text(encoding="utf-8"))
    found = m.group(1) if m else ""
    if found != expected_version:
        raise RuntimeError("staged version mismatch: expected %s, got %s" % (expected_version, found or "none"))
    py_files = sorted(str(p) for p in (stage / "relay").glob("*.py"))
    subprocess.run([python_bin, "-m", "py_compile", *py_files], check=True)


def detect_managed_services(candidates=DEFAULT_SERVICES):
    """Returns `(service_name, python_bin, port)` for each active/enabled managed relay systemd unit."""
    found = []
    for svc in candidates:
        res = subprocess.run(["systemctl", "cat", svc], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
        if res.returncode != 0 or not res.stdout.strip():
            continue
        active = subprocess.run(["systemctl", "is-active", svc], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
        enabled = subprocess.run(["systemctl", "is-enabled", svc], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
        if active.stdout.strip() != "active" and enabled.stdout.strip() != "enabled":
            continue
        m = re.search(r"^ExecStart=(\S+)\s+.*?--port\s+(\d+)", res.stdout, re.MULTILINE)
        py_bin = m.group(1) if m else sys.executable
        port = int(m.group(2)) if m else 8722
        found.append((svc, py_bin, port))
    return found


def _flush_nscd():
    if shutil.which("nscd"):
        subprocess.run(["nscd", "-i", "passwd"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(["nscd", "-i", "group"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def restart_and_verify(services, expected_version, wait_seconds=12):
    """Restarts all detected relay units and confirms each listens and reports healthy HTTP responses."""
    names = [s[0] for s in services]
    if not names:
        return True
    subprocess.run(["systemctl", "stop", *names], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    _flush_nscd()
    res = subprocess.run(["systemctl", "start", *names], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if res.returncode != 0:
        return False

    deadline = time.monotonic() + wait_seconds
    while time.monotonic() < deadline:
        all_ok = True
        for svc, _, port in services:
            st = subprocess.run(["systemctl", "is-active", svc], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
            if st.stdout.strip() != "active":
                all_ok = False
                break
            try:
                with urllib.request.urlopen("http://127.0.0.1:%d/api/session" % port, timeout=2) as resp:
                    if resp.status != 200:
                        all_ok = False
                        break
                    body = json.loads(resp.read().decode("utf-8"))
                    if not body.get("public") and body.get("version") != expected_version:
                        all_ok = False
                        break
            except Exception:
                all_ok = False
                break
        if all_ok:
            return True
        time.sleep(0.5)
    return False


def prune_old_releases(releases_dir, keep=3):
    releases = Path(releases_dir)
    if not releases.is_dir():
        return
    entries = []
    for child in releases.iterdir():
        if child.is_dir() and parse_version(child.name):
            entries.append((parse_version(child.name), child))
    entries.sort(key=lambda item: item[0], reverse=True)
    for _, old in entries[keep:]:
        shutil.rmtree(old, ignore_errors=True)


def run_update(root=DEFAULT_ROOT, api_url=DEFAULT_API, tarball_template=DEFAULT_TARBALL, force=False, dry_run=False):
    root = Path(root)
    current_ver = read_installed_version(root)
    current_tuple = parse_version(current_ver) or (0, 0, 0)

    latest_ver = fetch_latest_release(api_url)
    latest_tuple = parse_version(latest_ver)
    if not latest_tuple:
        print("skip: could not determine latest formal release")
        return 0

    if not force and latest_tuple <= current_tuple:
        print("up-to-date: %s (latest %s)" % (current_ver or "unknown", latest_ver))
        return 0

    print("update available: %s -> %s" % (current_ver or "none", latest_ver))
    if dry_run:
        return 0

    services = detect_managed_services()
    py_bin = services[0][1] if services else sys.executable
    current_link = root / "current"
    prev_target = os.path.realpath(str(current_link)) if current_link.exists() else ""

    releases_dir = root / "releases"
    releases_dir.mkdir(parents=True, exist_ok=True)
    stage_dir = releases_dir / (latest_ver + ".new")
    final_dir = releases_dir / latest_ver
    shutil.rmtree(stage_dir, ignore_errors=True)

    tar_url = tarball_template.format(version=latest_ver)
    with tempfile.TemporaryDirectory(prefix="rcli-update-", dir=str(root)) as tmp:
        archive = Path(tmp) / ("v%s.tar.gz" % latest_ver)
        req = urllib.request.Request(tar_url, headers={"User-Agent": "RemoteAI-Relay-Updater"})
        with urllib.request.urlopen(req, timeout=60) as resp, open(archive, "wb") as out:
            shutil.copyfileobj(resp, out)
        _safe_extract_release(archive, stage_dir)

    verify_staged_release(stage_dir, latest_ver, py_bin)

    if final_dir.exists():
        shutil.rmtree(final_dir)
    stage_dir.rename(final_dir)

    tmp_link = root / ".current.new"
    if tmp_link.is_symlink() or tmp_link.exists():
        tmp_link.unlink()
    os.symlink(str(final_dir), str(tmp_link))
    os.replace(str(tmp_link), str(current_link))

    if not restart_and_verify(services, latest_ver):
        print("ERROR: health check failed after switching to %s; rolling back" % latest_ver, file=sys.stderr)
        if prev_target and os.path.isdir(prev_target):
            if tmp_link.is_symlink() or tmp_link.exists():
                tmp_link.unlink()
            os.symlink(prev_target, str(tmp_link))
            os.replace(str(tmp_link), str(current_link))
            prev_ver = read_installed_version(root)
            restart_and_verify(services, prev_ver)
        return 1

    prune_old_releases(releases_dir, keep=3)
    print("updated: %s -> %s (%d services verified)" % (current_ver or "none", latest_ver, len(services)))
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description="Auto-update Remote AI relay to the latest formal GitHub release.")
    parser.add_argument("--root", default=str(DEFAULT_ROOT))
    parser.add_argument("--api", default=DEFAULT_API)
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    lock_fd = None
    try:
        if os.name == "posix":
            import fcntl
            lock_path = "/run/remote-cli-auto-update.lock" if os.geteuid() == 0 else os.path.join(tempfile.gettempdir(), "remote-cli-auto-update.lock")
            lock_fd = open(lock_path, "w")
            fcntl.flock(lock_fd.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        print("skip: another update is already running")
        return 0
    except OSError:
        lock_fd = None

    try:
        return run_update(root=args.root, api_url=args.api, force=args.force, dry_run=args.dry_run)
    finally:
        if lock_fd is not None:
            try:
                lock_fd.close()
            except OSError:
                pass


if __name__ == "__main__":
    sys.exit(main())
