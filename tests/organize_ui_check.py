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
            items[0]["title"] = op["title"]
            items[0]["manual_title"] = op["title"]
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

    browser.open(computer.origin + "/?tab=tasks", "!document.querySelector('#organize').hidden", "整理按钮", **screen(390))
    click("#follow-filter")
    shown("2 个", "#results")
    shown("thesis", "#results")
    shown("网络工具", "#results")
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

    type_in("#query", "请接着核对记录 00")
    shown("1 个", "#results")
    click("#results-list .organize-more")
    button("整理这段对话")
    shown("未整理")
    button("整理这段")
    shown("样例模型暂时不可用")
    button("重试这段")
    shown("已整理 36")
    button("编辑名称")
    type_in("dialog[open] input", "手动命名的对话")
    button("保存名称")
    shown("手动命名的对话")
    click("dialog[open] .rs-close")
    shown("手动命名的对话", "#results")

    click("#organize")
    until("document.querySelector('dialog[open] #organize-model')?.value === 'gpt-6-luna'", "默认 Luna")
    assert value("document.querySelector('dialog[open] #organize-auto').getAttribute('aria-checked')") == "false"
    type_in("dialog[open] #organize-model", "alternate", "change")
    shown("已保存设置")
    click("dialog[open] #organize-auto")
    until("document.querySelector('dialog[open] #organize-auto')?.getAttribute('aria-checked') === 'true'", "开启自动")
    button("整理全部可读对话")
    shown("37 段可读对话")
    button("开始整理")
    shown("已排队 1 段 · 跳过 36 段")
    button("重试失败记录")
    button("重试失败记录")
    shown("已整理 37")
    button("整理全部可读对话")
    button("开始整理")
    shown("已排队 0 段 · 跳过 37 段")
    for width in (320, 390, 430):
        browser.ask("Emulation.setDeviceMetricsOverride", **screen(width))
        geometry = value("""(() => {
            const root = document.documentElement, pane = document.querySelector('dialog[open] .rs-body');
            return [innerWidth, root.clientWidth, root.scrollWidth, document.body.scrollWidth, pane.clientWidth, pane.scrollWidth];
        })()""")
        assert geometry[0] == geometry[1] == width and geometry[2] <= width and geometry[3] <= width, geometry
        assert geometry[5] <= geometry[4] + 1, geometry
        if evidence:
            browser.picture(evidence / f"organize-{width}.png")
    assert value("window.__errors") == []
    browser.open(computer.origin + "/?tab=tasks&organize=1&from=bench", "document.querySelector('#organize-model')", "原生面板入口", **screen(390))
    shown("隔离样例电脑")
    click("dialog[open] .rs-close")
    until("window.__nativeReturned", "关闭后回原生工作台")
    assert value("window.__errors") == []
    asked = [op for op in computer.asked if op["action"] == "session_organize"]
    assert {op["verb"] for op in asked} == {"status", "settings", "list", "start", "retry", "edit"}
    assert any(op.get("scope") == "all" for op in asked)
    print("Organizer UI passed: six operations, scoped native entry, cross-project search, reading return, 320/390/430px.")


if __name__ == "__main__":
    main()
