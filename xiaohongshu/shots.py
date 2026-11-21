"""Draws the phone's 协作 pages for the Xiaohongshu cards into xiaohongshu/shots/: the real embedded pages (web/) and
the real relay, with the played computer of tools/readme_pictures.py and collaboration tasks that are made up.

    python xiaohongshu/shots.py <chrome.exe>
"""
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "tools"))
from readme_pictures import PHONE, Browser, Played, uuid    # noqa: E402

OUT = HERE / "shots"
SPLIT, REVIEW, COMPARE = (uuid(n) for n in range(41, 44))
MINUTE = 60000


def teams(now):
    """Three tasks as the computer keeps them (Teams.cs): a split one in its second round, a finished review, and a
    comparison waiting for its owner's choice."""
    def log(start, *steps):
        return [{"at": start + i * 3 * MINUTE, "who": who, "text": text} for i, (who, text) in enumerate(steps)]

    roster = [{"role": "leader", "tool": "claude", "model": "", "effort": ""},
              {"role": "member-1", "tool": "codex", "model": "", "effort": ""},
              {"role": "member-2", "tool": "codex", "model": "", "effort": ""}]
    split = dict(id=SPLIT, mode="lead", dir="web-shop", goal="给结算页加上优惠券，并补齐测试", tools=[r["tool"] for r in roster], roster=roster,
                 rounds=0, round=2, state="running", phase="verify", note="", picked="", created=now - 31 * MINUTE, awaited=["leader"],
                 members={r["role"]: uuid(50 + i) for i, r in enumerate(roster)},
                 member_sessions={r["role"]: {"id": uuid(55 + i), "tool": r["tool"]} for i, r in enumerate(roster[1:], 1)},
                 log=log(now - 30 * MINUTE,
                         ("目标", "给结算页加上优惠券，并补齐测试"),
                         ("leader", "第 1 轮派工：\n- 执行成员 1：在 `cart/coupon.js` 实现一单一张券的规则\n- 执行成员 2：为金额计算和优惠券补测试"),
                         ("member-1", "已实现 `applyCoupon`，满减券和折扣券走同一个入口，金额统一保留两位小数。"),
                         ("member-2", "新增 9 条用例；“折扣后金额为负”这条没有通过，复现步骤写在 `coupon.test.js`。"),
                         ("leader", "第 1 轮验收未通过：折扣后金额可能小于 0。第 2 轮请执行成员 1 设下限，执行成员 2 重跑全部用例。"),
                         ("member-1", "金额下限改为 0，满减逻辑保持不变。"),
                         ("member-2", "`npm test -- cart`：23 条全部通过。")))
    review = dict(id=REVIEW, mode="review", dir="data-tools", goal="把导出脚本改成增量", tools=["codex", "claude"], rounds=3, round=2, state="done", phase="",
                  note="审查通过", picked="", created=now - 3 * 3600000, awaited=[], members={"writer": uuid(60), "reviewer": uuid(61)},
                  log=log(now - 3 * 3600000, ("目标", "把导出脚本改成增量"), ("writer", "按修改时间只导出新增和改动的记录。"),
                          ("reviewer", "删除的记录没有处理，下一次导出会留下旧数据。"), ("writer", "补上删除记录的同步，并加了对应用例。"),
                          ("reviewer", "审查通过。"), ("结果", "审查通过")))
    compare = dict(id=COMPARE, mode="compare", dir="web-shop", goal="重做结算页的价格样式", tools=["claude", "codex"], rounds=1, round=1, state="choose", phase="",
                   note="各候选方案已完成，对比改动后选一个合并到主目录", picked="", created=now - 52 * MINUTE, awaited=[], members={"claude": uuid(70), "codex": uuid(71)},
                   candidates=[{"tool": "claude", "summary": "原价划线，实付加粗放大", "stat": "2 个文件改动：cart.css、Price.vue", "diff": "", "ready": True},
                               {"tool": "codex", "summary": "优惠金额单独成行，用强调色", "stat": "1 个文件改动：cart.css", "diff": "", "ready": True}],
                   log=log(now - 52 * MINUTE, ("目标", "重做结算页的价格样式"), ("claude", "方案已完成"), ("codex", "方案已完成")))
    return [split, compare, review]


def main():
    chrome = sys.argv[1]
    OUT.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="remote-ai-xhs-", ignore_cleanup_errors=True) as temporary:
        played, browser = Played(temporary), Browser(chrome, temporary)
        try:
            played.info.update(version="1.12.1", features=played.info["features"] + ["chat", "teams","teams-v2", "teams-roster", "teams-flex-rounds"])
            played.teams[:] = teams(played.now)
            browser.open(played.origin + "/?tab=together", "document.body.innerText.includes('给结算页加上优惠券')", "the tab with the tasks")
            browser.picture(OUT / "together.png")
            browser.open(played.origin + "/?tab=together&do=team&item=" + SPLIT, "document.body.innerText.includes('23 条全部通过')", "a task in its second round",
                         **dict(PHONE, height=1000))
            browser.picture(OUT / "team.png")
            # the same task with its record scrolled to the latest steps: the leader turning a round back, and the fix
            browser.value("document.querySelectorAll('*').forEach(e => { if (e.scrollHeight > e.clientHeight + 40 && getComputedStyle(e).overflowY !== 'visible') e.scrollTop = e.scrollHeight; })")
            time.sleep(0.6)
            browser.picture(OUT / "team-log.png")
            # the sheet of a new task, set to a leader with members
            browser.open(played.origin + "/?tab=together&do=team", "!!document.getElementById('team-roster-tool-leader') || [...document.querySelectorAll('button, [role=tab], label')].some(b => b.textContent.trim() === '分工协作')",
                         "the sheet of a new task", **dict(PHONE, height=1400))
            browser.value("(() => { const b = [...document.querySelectorAll('button, [role=tab], label')].find(b => b.textContent.trim() === '分工协作'); if (b) b.click(); })()")
            browser.until("!!document.getElementById('team-roster-tool-leader')", "the roster of a split task")
            browser.value("""(() => {
              const goal = document.querySelector('#sheet textarea, dialog textarea');
              goal.value = '给结算页加上优惠券，并补齐测试'; goal.dispatchEvent(new Event('input', { bubbles: true }));
              [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'web-shop').click();
              const leader = document.getElementById('team-roster-tool-leader');
              leader.value = 'claude'; leader.dispatchEvent(new Event('change', { bubbles: true }));
            })()""")
            time.sleep(0.3)
            browser.value("""(() => {
              const pick = (id, tool) => { const s = document.getElementById(id); s.value = tool; s.dispatchEvent(new Event('change', { bubbles: true })); };
              pick('team-roster-tool-member-1', 'codex');
              document.getElementById('team-roster-add').click();
              pick('team-roster-tool-member-2', 'codex');
              [...document.querySelectorAll('button')].find(b => b.textContent.trim() === '自由轮数').click();
            })()""")
            time.sleep(0.6)
            browser.picture(OUT / "begin.png")
        finally:
            browser.close(); played.close()
    for one in sorted(OUT.glob("*.png")):
        print(one.name)


if __name__ == "__main__":
    main()
