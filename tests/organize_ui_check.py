"""Exercise the organizer in a phone-sized browser through the real relay."""
import json
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT / "tools"), str(ROOT / "relay")]
import readme_pictures as pictures


class OrganizerComputer(pictures.Played):
    def __init__(self, temporary):
        super().__init__(temporary)
        self.info["features"].append("organize")
        self.info["workspaces"].append("网络工具")
        self.model = "gpt-6-luna"
        self.auto = False
        self.turn = 0
        self.items = []
        for index in range(37):
            sid = f"00000000-0000-4000-8000-{index + 20:012d}"
            item = {
                "id": sid, "tool": "codex", "dir": "thesis" if index % 2 else "网络工具",
                "title": f"对话记录 {index:02d}", "original_title": f"请接着核对记录 {index:02d}",
                "summary": "验证连接恢复后的输入和阅读位置", "tags": ["断线重连"],
                "follow_up": index in (0, 1), "updated": self.now - index * 60000,
                "state": "pending" if index in (0, 36) else "done", "error": "",
            }
            self.items.append(item)
            self.sessions.append(dict(item, live=False, organize=item))
            self.said[sid] = [{"role": "assistant", "text": f"原始记录 {index:02d}"}]
        self.relay.agent({"info": self.info, "sessions": self.sessions})
        handler = self.server.RequestHandlerClass
        old_post = handler.do_POST

        def post(request):
            if request.path != "/api/organize":
                return old_post(request)
            payload = json.loads(request.rfile.read(int(request.headers["Content-Length"])))
            try:
                request.reply(self.relay.organize(payload, wait=8))
            except pictures.RemoteError as error:
                request.reply({"error": str(error)}, 400)

        handler.do_POST = post

    def answer(self, op):
        if op["action"] != "session_organize":
            return super().answer(op)
        verb = op["verb"]
        items = [row for row in self.items if not op.get("session") or row["id"] == op["session"]]
        if verb == "settings":
            self.model = op.get("model", self.model)
            self.auto = op.get("auto", self.auto)
        if verb in ("settings", "status"):
            counts = {state: sum(row["state"] == state for row in items) for state in ("queued", "running", "done", "failed")}
            if verb == "status" and any(row["state"] in ("queued", "running") for row in items):
                self.turn += 1
                for row in items:
                    if row["state"] not in ("queued", "running"):
                        continue
                    row["state"] = "running" if self.turn < 2 else self.outcome
                    row["error"] = "样例模型暂时不可用" if row["state"] == "failed" else ""
            return dict(counts, auto=self.auto, model=self.model, default_model="gpt-6-luna", model_available=True,
                        models=[{"id": "gpt-6-luna", "label": "GPT-6 Luna"}, {"id": "alternate", "label": "备用模型"}])
        if verb == "list":
            first, limit = op["offset"], op["limit"]
            return {"items": items[first:first + limit], "more": first + limit < len(items), "total": len(items)}
        if verb == "edit":
            if "title" in op:
                items[0]["title"] = op["title"]
                items[0]["manual_title"] = op["title"]
            if "follow_up" in op:
                items[0]["follow_up"] = bool(op["follow_up"])
            return {"item": items[0]}
        eligible = [row for row in items if row["state"] == ("pending" if verb == "start" else "failed")]
        for row in eligible:
            row["state"], row["error"] = "queued", ""
        self.turn, self.outcome = 0, "failed" if verb == "start" else "done"
        return {"queued": len(eligible), "skipped": len(items) - len(eligible)}


def main():
    chrome = sys.argv[1]
    evidence = Path(sys.argv[2]) if len(sys.argv) > 2 else None
    if evidence:
        evidence.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="remote-ai-organize-", ignore_cleanup_errors=True) as temporary:
        computer = OrganizerComputer(temporary)
        browser = pictures.Browser(chrome, temporary)
        try:
            run(browser, computer, evidence)
        finally:
            browser.close()
            computer.close()


def run(browser, computer, evidence):
    value, until = browser.value, browser.until
    browser.ask("Page.addScriptToEvaluateOnNewDocument", source="""
        window.__errors = []; addEventListener('error', e => __errors.push(e.message));
        addEventListener('unhandledrejection', e => __errors.push(String(e.reason)));
        if (location.search.includes('from=bench')) window.RemoteCliNative = {
          computer: () => '隔离样例电脑', home: () => { window.__nativeReturned = true; },
          appTabs: () => 'tasks,projects,together,me', appPage: () => {}, chrome: () => {}
        };
    """)

    def screen(width):
        return dict(width=width, height=840, deviceScaleFactor=1, mobile=True)

    def click(selector):
        value(f"document.querySelector({json.dumps(selector)}).click()")

    def type_in(selector, text, event="input"):
        value(f"((n) => {{n.value={json.dumps(text)}; n.dispatchEvent(new Event('{event}', {{bubbles:true}}));}})(document.querySelector({json.dumps(selector)}))")

    def button(text):
        until(f"""(b => b && !b.disabled && (b.click(), true))
            ([...document.querySelectorAll('dialog[open] button')].find(b => b.textContent.trim() === {json.dumps(text)}))""",
              f"有可点按的按钮 {text}")

    def shown(text, selector="dialog[open]"):
        until(f"document.querySelector({json.dumps(selector)})?.innerText.includes({json.dumps(text)})", text)

    def check_width(width, check_pane=True):
        browser.ask("Emulation.setDeviceMetricsOverride", **screen(width))
        geometry = value(f"""(() => {{
            const root = document.documentElement, pane = document.querySelector('dialog[open] .rs-body');
            return [innerWidth, root.clientWidth, root.scrollWidth, document.body.scrollWidth,
                    pane ? pane.clientWidth : 0, pane ? pane.scrollWidth : 0];
        }})()""")
        assert geometry[0] == geometry[1] == width and geometry[2] <= width and geometry[3] <= width, (width, geometry)
        if check_pane:
            assert geometry[4] > 0 and geometry[5] <= geometry[4] + 1, (width, geometry)
        return geometry

    browser.open(computer.origin + "/?tab=tasks", "!document.querySelector('#organize').hidden && !!document.querySelector('#filters #follow-filter')", "整理按钮与待跟进筛选", **screen(390))
    assert value("document.querySelector('#follow-filter .chip-count')?.textContent") == "2"
    if evidence:
        browser.picture(evidence / "tasks-390.png")
    for width in (320, 390, 430):
        check_width(width, check_pane=False)
    browser.ask("Emulation.setDeviceMetricsOverride", **screen(390))

    click("#follow-filter")
    shown("2 个", "#results")
    shown("thesis", "#results")
    shown("网络工具", "#results")
    if evidence:
        browser.picture(evidence / "follow-390.png")
    click("#follow-filter")
    type_in("#query", "断线重连")
    shown("37 个", "#results")
    shown("主题 · 断线重连", "#results")
    click("#more-results")
    value("document.querySelectorAll('#results-list .card')[18].scrollIntoView()")
    top = value("document.querySelectorAll('#results-list .card')[18].getBoundingClientRect().top")
    click("#results-list .card:nth-child(19) .open")
    shown("原始记录 18")
    assert not any(op["action"] == "start" for op in computer.asked)
    value("history.back()")
    until("!document.querySelector('dialog[open]')", "阅读返回")
    assert value("document.querySelector('#query').value") == "断线重连"
    assert value("document.querySelectorAll('#results-list .card').length") == 37
    assert abs(value("document.querySelectorAll('#results-list .card')[18].getBoundingClientRect().top") - top) <= 2

    # Manual follow-up toggle from conversation menu via edit { follow_up: bool }
    type_in("#query", "请接着核对记录 00")
    shown("1 个", "#results")
    click("#results-list .organize-more")
    button("移出待跟进")
    until("!document.querySelector('dialog[open]')", "移出待跟进后关闭菜单")
    until("document.querySelector('#follow-filter .chip-count')?.textContent === '1'", "待跟进计数更新为 1")
    follow_ops = [op for op in computer.asked if op.get("action") == "session_organize" and op.get("verb") == "edit" and "follow_up" in op]
    assert follow_ops[-1]["follow_up"] is False and "title" not in follow_ops[-1], follow_ops[-1]
    click("#results-list .organize-more")
    button("加入待跟进")
    until("!document.querySelector('dialog[open]')", "加入待跟进后关闭菜单")
    until("document.querySelector('#follow-filter .chip-count')?.textContent === '2'", "待跟进计数恢复为 2")
    follow_ops = [op for op in computer.asked if op.get("action") == "session_organize" and op.get("verb") == "edit" and "follow_up" in op]
    assert follow_ops[-1]["follow_up"] is True and "title" not in follow_ops[-1], follow_ops[-1]

    # Single-session organize, fail -> retry, rename
    click("#results-list .organize-more")
    button("整理这段对话")
    shown("未整理")
    button("整理这段")
    shown("样例模型暂时不可用")
    button("重试这段")
    shown("已整理")
    button("编辑名称")
    type_in("dialog[open] input", "手动命名的对话")
    button("保存名称")
    shown("手动命名的对话")
    click("dialog[open] .rs-close")
    shown("手动命名的对话", "#results")

    # Full organizer: conversations to continue appear first; model/auto are tucked in settings
    click("#organize")
    until("!!document.querySelector('dialog[open] #organize-settings')", "设置入口可见")
    assert not value("!!document.querySelector('dialog[open] #organize-model')"), "模型选择器已收入设置子页"
    assert not value("!!document.querySelector('dialog[open] #organize-auto')"), "自动开关已收入设置子页"
    shown("待跟进 · 2")
    shown("1 段待整理")
    until("(document.querySelector('dialog[open] .organize-row')?.getBoundingClientRect().top || 9999) < 420", "要继续的对话在首屏上半部分可见")
    first_row_top = value("document.querySelector('dialog[open] .organize-row')?.getBoundingClientRect().top || 9999")
    assert first_row_top < 420, f"要继续的对话应在首屏上半部分可见: top={first_row_top}"
    if evidence:
        browser.picture(evidence / "organize-390.png")

    # Open settings subpage: default GPT-6 Luna, auto off -> switch model & turn auto on
    click("dialog[open] #organize-settings")
    until("document.querySelector('dialog[open] #organize-model')?.value === 'gpt-6-luna'", "默认 Luna")
    assert value("document.querySelector('dialog[open] #organize-auto').getAttribute('aria-checked')") == "false"
    until("Math.abs((document.querySelector('dialog[open] .rs-page')?.getBoundingClientRect().left || 99) - 16) <= 4", "设置页滑入完成")
    if evidence:
        browser.picture(evidence / "organize-settings-390.png")
    for width in (320, 390, 430):
        check_width(width, check_pane=True)
    browser.ask("Emulation.setDeviceMetricsOverride", **screen(390))
    type_in("dialog[open] #organize-model", "alternate", "change")
    shown("已保存设置")
    click("dialog[open] #organize-auto")
    until("document.querySelector('dialog[open] #organize-auto')?.getAttribute('aria-checked') === 'true'", "开启自动")
    click("dialog[open] .rs-back")
    until("!!document.querySelector('dialog[open] #organize-settings')", "返回整理主视图")
    shown("备用模型 · 自动整理已开")

    # Batch organize, retry failure, and batch skip when all are already done
    button("整理全部可读对话")
    shown("37 段可读对话")
    button("开始整理")
    shown("已排队 1 段 · 跳过 36 段")
    button("重试失败记录")
    button("重试失败记录")
    shown("已整理 37 段")
    shown("全部可读对话已处理")
    button("整理全部可读对话")
    button("开始整理")
    shown("已排队 0 段 · 跳过 37 段")
    for width in (320, 390, 430):
        check_width(width, check_pane=True)
        if evidence:
            browser.picture(evidence / f"organize-done-{width}.png")
    assert value("window.__errors") == []

    # Project history default of 16: first open, load more to reveal 17th, re-enter project resets to 16
    project_name = "历史验证"
    computer.info["workspaces"].append(project_name)
    for idx in range(17):
        sid = f"00000000-0000-4000-8000-{idx + 200:012d}"
        computer.sessions.append({
            "id": sid, "tool": "codex", "dir": project_name, "title": f"项目历史 {idx + 1:02d}",
            "updated": computer.now - idx * 60000, "live": False,
        })
    computer.relay.agent({"info": computer.info, "sessions": computer.sessions})
    browser.open(computer.origin + "/?project=" + project_name, "!document.querySelector('#project').hidden && !document.querySelector('#p-history').hidden", "首次打开项目详情", **screen(390))
    click("#p-history .section-head")
    until("document.querySelectorAll('#p-history .card').length === 16", "首次打开默认展开前 16 条历史")
    assert not value("document.querySelector('#more-history').hidden"), "第 17 条历史应可展开"
    assert not value("document.querySelector('#p-history').innerText.includes('项目历史 17')")
    click("#more-history")
    until("document.querySelectorAll('#p-history .card').length === 17", "加载更多后显示全部 17 条历史")
    assert value("document.querySelector('#p-history').innerText.includes('项目历史 17')")
    assert value("document.querySelector('#more-history').hidden")
    # Re-enter the project from projects list: history count resets to 16 and 17th can be expanded again
    click("#back")
    until("!document.querySelector('#overview').hidden", "返回项目列表")
    value(f"Array.from(document.querySelectorAll('#project-list .card b')).find(b => b.textContent === {json.dumps(project_name)}).closest('button').click()")
    until("!document.querySelector('#project').hidden && document.querySelector('#heading').textContent === " + json.dumps(project_name), "重新进入项目详情")
    click("#p-history .section-head")
    until("document.querySelectorAll('#p-history .card').length === 16", "重新进入项目后默认显示前 16 条历史")
    assert not value("document.querySelector('#more-history').hidden")
    click("#more-history")
    until("document.querySelectorAll('#p-history .card').length === 17", "重新进入后可展开第 17 条历史")

    # Scoped native workbench entry
    browser.open(computer.origin + "/?tab=tasks&organize=1&from=bench", "!!document.querySelector('#organize-settings')", "原生面板入口", **screen(390))
    shown("隔离样例电脑")
    click("dialog[open] .rs-close")
    until("window.__nativeReturned", "关闭后回原生工作台")
    assert value("window.__errors") == []
    asked = [op for op in computer.asked if op["action"] == "session_organize"]
    assert {op["verb"] for op in asked} == {"status", "settings", "list", "start", "retry", "edit"}
    assert any(op.get("scope") == "all" for op in asked)
    assert any(op["verb"] == "edit" and op.get("follow_up") is False for op in asked)
    assert any(op["verb"] == "edit" and op.get("follow_up") is True for op in asked)
    print("Organizer UI passed: first-screen follow-up, settings subpage, state progress, manual follow_up edit, reading return, batch skip, 16/17 project history, 320/390/430px.")


if __name__ == "__main__":
    main()
