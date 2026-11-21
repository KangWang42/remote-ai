"""Xiaohongshu progress posts: the owner's past notes read back, a post checked, its cards drawn, and the post
published through xiaohongshu-mcp (github.com/xpzouying/xiaohongshu-mcp), whose service runs on this computer.

    python xiaohongshu/xhs.py serve                      run the service (log in first with bin/xiaohongshu-login-*.exe)
    python xiaohongshu/xhs.py past                       the owner's notes, with text, figures and comments, into past/
    python xiaohongshu/xhs.py check  <post folder>       title, text, tags and pictures against the platform's limits
    python xiaohongshu/xhs.py render <post folder> <chrome.exe>    cards/*.html into out/*.png, 1080 x 1440
    python xiaohongshu/xhs.py publish <post folder> [--visibility 公开可见|仅自己可见|仅互关好友可见] [--at 2026-11-22T20:00:00+08:00]

A post folder holds post.md (front matter `title` and `tags`, then the text) and cards/NN-*.html.
"""
import argparse
import json
import re
import subprocess
import sys
import time
import urllib.request
from datetime import datetime
from pathlib import Path

import yaml

HERE = Path(__file__).resolve().parent
BIN = HERE / "bin"
SERVICE = "http://127.0.0.1:18060"
CARD = dict(width=540, height=720, deviceScaleFactor=2, mobile=False)    # drawn at 1080 x 1440, the 3:4 of a note
TITLE_MAX, TEXT_MAX, TAGS_MAX, PICTURES_MAX = 20, 1000, 10, 18


def call(path, payload=None, timeout=300):
    data = None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(SERVICE + path, data=data, headers={"Content-Type": "application/json"} if data else {})
    with urllib.request.urlopen(request, timeout=timeout) as reply:
        answer = json.loads(reply.read().decode("utf-8"))
    if not answer.get("success"):
        raise SystemExit("%s: %s" % (path, answer.get("message") or answer))
    return answer["data"]


def title_length(text):
    """As Xiaohongshu counts a title: a character outside ASCII is one, two ASCII characters are one."""
    units = sum(2 if ord(c) > 127 else 1 for c in text.encode("utf-16-le").decode("utf-16-le", "surrogatepass"))
    return (units + 1) // 2


def read_post(folder):
    raw = (folder / "post.md").read_text(encoding="utf-8")
    match = re.match(r"---\n(.*?)\n---\n(.*)", raw, re.S)
    if not match:
        raise SystemExit("%s: post.md starts with front matter between two --- lines" % folder)
    head = yaml.safe_load(match.group(1)) or {}
    return dict(title=str(head.get("title", "")).strip(), tags=[str(t) for t in head.get("tags") or []], text=match.group(2).strip())


def check(folder):
    post, faults = read_post(folder), []
    pictures = sorted((folder / "out").glob("*.png"))
    if not post["title"] or title_length(post["title"]) > TITLE_MAX:
        faults.append("标题为空或超过 %d 字（现为 %d）" % (TITLE_MAX, title_length(post["title"])))
    if not post["text"] or len(post["text"]) > TEXT_MAX:
        faults.append("正文为空或超过 %d 字（现为 %d）" % (TEXT_MAX, len(post["text"])))
    if len(post["tags"]) > TAGS_MAX:
        faults.append("话题超过 %d 个" % TAGS_MAX)
    if not 1 <= len(pictures) <= PICTURES_MAX:
        faults.append("图片应为 1 到 %d 张（out/ 里有 %d 张，先 render）" % (PICTURES_MAX, len(pictures)))
    drawn = {p.stem for p in pictures}
    missing = [c.stem for c in sorted((folder / "cards").glob("*.html")) if c.stem not in drawn]
    if missing:
        faults.append("这些卡片还没有画出：" + "、".join(missing))
    print("标题 %d/%d · 正文 %d/%d · 话题 %d · 图片 %d" % (title_length(post["title"]), TITLE_MAX, len(post["text"]), TEXT_MAX, len(post["tags"]), len(pictures)))
    for fault in faults:
        print("  未通过：" + fault)
    return post, pictures, not faults


def render(folder, chrome):
    import tempfile
    sys.path.insert(0, str(HERE.parent / "tools"))
    from readme_pictures import Browser
    out = folder / "out"
    out.mkdir(exist_ok=True)
    for old in out.glob("*.png"):
        old.unlink()
    with tempfile.TemporaryDirectory(prefix="remote-ai-cards-", ignore_cleanup_errors=True) as temporary:
        browser = Browser(chrome, temporary)
        try:
            for card in sorted((folder / "cards").glob("*.html")):
                browser.open(card.as_uri(), "document.fonts.status === 'loaded' && Array.from(document.images).every(i => i.complete && i.naturalWidth > 0)", card.name, **CARD)
                overflow = browser.value("document.documentElement.scrollHeight > innerHeight + 1 || document.documentElement.scrollWidth > innerWidth + 1")
                browser.picture(out / (card.stem + ".png"))
                print(card.stem + ".png", "（内容超出画面）" if overflow else "")
        finally:
            browser.close()


def past():
    """Each of the owner's notes as note.md with its figures; index.md lists them with what they drew."""
    me = call("/api/v1/user/me")["data"]
    folder = HERE / "past"
    folder.mkdir(exist_ok=True)
    rows = []
    for feed in me["feeds"]:
        note = call("/api/v1/feeds/detail", {"feed_id": feed["id"], "xsec_token": feed["xsecToken"]})["data"]
        body, comments = note["note"], note.get("comments", {}).get("list", [])
        day = datetime.fromtimestamp(body["time"] / 1000).strftime("%Y-%m-%d")
        place = folder / ("%s-%s" % (day, feed["id"]))
        place.mkdir(exist_ok=True)
        for n, image in enumerate(body.get("imageList", []), 1):
            target = place / ("%02d.webp" % n)
            if not target.exists():
                urllib.request.urlretrieve(image["urlDefault"], target)
        counts = body["interactInfo"]
        said = "\n".join("- %s（%s 赞）" % (c["content"], c.get("likeCount", "0")) for c in comments) or "（无）"
        (place / "note.md").write_text("# %s\n\n%s · 赞 %s · 收藏 %s · 评论 %s · 图 %d 张\n\n%s\n\n## 评论\n\n%s\n" % (
            body["title"], day, counts["likedCount"], counts["collectedCount"], counts["commentCount"], len(body.get("imageList", [])), body["desc"], said), encoding="utf-8")
        rows.append((day, body["title"], counts["likedCount"], counts["collectedCount"], counts["commentCount"], place.name))
        time.sleep(2)
    lines = ["| 日期 | 标题 | 赞 | 收藏 | 评论 |", "| --- | --- | --- | --- | --- |"]
    lines += ["| %s | [%s](%s/note.md) | %s | %s | %s |" % (d, t, p, l, c, m) for d, t, l, c, m, p in sorted(rows, reverse=True)]
    (folder / "index.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))


def publish(folder, visibility, at):
    post, pictures, fine = check(folder)
    if not fine:
        raise SystemExit("检查没有通过，未发布")
    payload = dict(title=post["title"], content=post["text"], images=[str(p) for p in pictures], tags=post["tags"], visibility=visibility)
    if at:
        payload["schedule_at"] = at
    print(json.dumps(call("/api/v1/publish", payload, timeout=600), ensure_ascii=False))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    steps = parser.add_subparsers(dest="step", required=True)
    steps.add_parser("serve")
    steps.add_parser("past")
    for name in ("check", "render", "publish"):
        one = steps.add_parser(name)
        one.add_argument("folder", type=Path)
        if name == "render":
            one.add_argument("chrome")
        if name == "publish":
            one.add_argument("--visibility", default="公开可见", choices=["公开可见", "仅自己可见", "仅互关好友可见"])
            one.add_argument("--at", default="", help="定时发布，ISO 8601，1 小时到 14 天内")
    asked = parser.parse_args()
    if asked.step == "serve":
        # the build with xiaohongshu-mcp-v2.7.4-creator-session.patch (README); with a window, so that a scan asked
        # for on publishing can be seen. cwd: cookies.json is kept beside it
        subprocess.run([str(BIN / "xiaohongshu-mcp-windows-amd64-fixed.exe"), "-headless=false", "-port", "127.0.0.1:18060"], cwd=BIN, check=True)
    elif asked.step == "past":
        past()
    elif asked.step == "check":
        sys.exit(0 if check(asked.folder.resolve())[2] else 1)
    elif asked.step == "render":
        render(asked.folder.resolve(), asked.chrome)
    else:
        publish(asked.folder.resolve(), asked.visibility, asked.at)


if __name__ == "__main__":
    main()
