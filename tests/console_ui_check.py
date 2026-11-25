"""The desktop console (web-app/ → web/console/) on the real relay and the Windows agent just built, with made-up
homes of Claude Code and Codex and stand-ins for both (tests/made_up.py, tests/chat_standin.py): nothing real is
listed or started. A conversation is listed once however it runs; a saved one is gone on with by a message, as a
chat; a chat is shown on its tool's own screen and back with the same conversation (`reopen`); menus, the
hand-over, the new conversation's project picker and the branch menu are on top of what lies under them; a file's
changes open in the middle; a narrow window shows the list or what was opened from it.

    python tests/console_ui_check.py <chrome.exe> <folder for the pictures> [<folder of RemoteCliAgent.exe>]
"""
import json
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))
sys.path.insert(0, str(ROOT / "tools"))
from made_up import Homes          # noqa: E402
import readme_pictures as pictures  # noqa: E402

PASSWORD = "console-1234"
WIDE = dict(width=1440, height=900, deviceScaleFactor=1, mobile=False)
SAVED = "11111111-2222-3333-4444-555555555555"


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def main():
    chrome, out = sys.argv[1], Path(sys.argv[2])
    program = Path(sys.argv[3] if len(sys.argv) > 3 else ROOT / "agent-windows" / "bin") / "RemoteCliAgent.exe"
    out.mkdir(parents=True, exist_ok=True)
    # (a short folder: the made-up homes name projects by their whole path)
    with tempfile.TemporaryDirectory(prefix="rc-", ignore_cleanup_errors=True) as temporary:
        base = Path(temporary)
        data, shop, notes = base / "agent", base / "web-shop", base / "notes"
        for folder in (data, shop, notes):
            folder.mkdir()
        (shop / "app.js").write_text("export const total = items => items.reduce((a, b) => a + b.price, 0);\n", encoding="utf-8")
        for step in (["init", "-q"], ["add", "."], ["commit", "-qm", "init"]):
            subprocess.run(["git", "-c", "user.email=check@example.com", "-c", "user.name=check"] + step, cwd=shop, check=True)
        (shop / "app.js").write_text("export const total = items => items.reduce((a, b) => a + b.price * b.count, 0);\n", encoding="utf-8")
        homes = Homes(temporary)
        homes.claude_saved(shop, SAVED, "结算页金额多一位小数", 3, said=("结算页的金额有时多一位小数", "已经改成按分计算。"))
        homes.codex_saved(shop, "019a0000-0000-7000-8000-000000000001", "补充单元测试", 6)
        homes.codex_saved(notes, "019a0000-0000-7000-8000-000000000002", "整理周报", 50)
        environment = homes.environment(RCLI_PASSWORD=PASSWORD)
        tools = homes.stand_ins(temporary)
        for name in ("claude", "codex"):
            (tools / (name + ".cmd")).write_text('@if "%%1"=="--help" (echo stand-in) else ("%s" "%s" %s %%*)\r\n'
                                                 % (sys.executable, ROOT / "tests" / "chat_standin.py", name), encoding="mbcs")
        environment["PATH"] = str(tools) + ";" + environment["PATH"]
        port = free_port()
        (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % port, "RemoteEnabled": True, "RemoteMaxMode": "full",
                                                      "RemoteDirs": ["web-shop=" + str(shop), "notes=" + str(notes)]}), encoding="utf-8")
        relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--host", "127.0.0.1", "--port", str(port), "--data", str(base / "relay"),
                                  "--web", str(ROOT / "web")], env=environment, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run([str(program), "--set-password", str(data)], input=PASSWORD.encode(), check=True)
        agent = subprocess.Popen([str(program), "--data", str(data)], env=environment)
        browser = None
        try:
            browser = pictures.Browser(chrome, temporary)
            value, until = browser.value, browser.until
            browser.ask("Page.addScriptToEvaluateOnNewDocument", source="window.__errors = []; addEventListener('error', e => __errors.push(String(e.message)));"
                        " addEventListener('unhandledrejection', e => __errors.push('rejected: ' + (e.reason && e.reason.message || e.reason)));")
            origin = "http://127.0.0.1:%d" % port
            q = json.dumps
            titles = "Array.from(document.querySelectorAll('.proj .th-row .th-title')).map(n => n.textContent)"
            press = lambda scope, text: until("(() => { const b = Array.from(document.querySelectorAll(%s)).find(b => b.textContent.trim() === %s && !b.disabled); if (!b) return false; b.click(); return true; })()"
                                              % (q(scope), q(text)), "press " + text)
            # the element at the middle of what is found is that element or within it: nothing lies over it
            on_top = lambda selector: value("(n => { if (!n) return false; const r = n.getBoundingClientRect(); const at = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2); return !!at && (n === at || n.contains(at)); })(document.querySelector(%s))" % q(selector))
            listed = lambda: value("fetch('../api/terminal').then(r => r.json()).then(j => j.terminals.filter(t => t.state === 'running').map(t => [t.id, t.form || '', t.session || '']))")
            errors = lambda: value("JSON.stringify(window.__errors)")

            # ---- the list: conversations by project, each once
            browser.open(origin + "/console/#p=" + PASSWORD, "document.querySelectorAll('.proj .th-row').length === 3", "the console's list", **WIDE)
            assert sorted(value(titles)) == sorted(["整理周报", "补充单元测试", "结算页金额多一位小数"]), value(titles)
            assert value("document.querySelectorAll('.proj-name').length") == 2
            browser.picture(out / "list.png")

            # ---- a right click on a conversation: its menu, with the reasons of what cannot be done now
            value("(n => n.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 120, clientY: 300 })))(Array.from(document.querySelectorAll('.th-row')).find(n => n.textContent.includes('结算页')))")
            until("!!document.querySelector('.ctx-menu')", "the conversation's menu")
            items = value("Array.from(document.querySelectorAll('.ctx-item .ctx-label')).map(n => n.textContent)")
            assert "交给另一个 AI…" in items and "在终端界面中继续" in items and "停止这一轮" in items, items
            assert value("Array.from(document.querySelectorAll('.ctx-item')).find(n => n.textContent.startsWith('停止这一轮')).disabled"), "a stop of what does not run is greyed"
            assert on_top(".ctx-menu .ctx-item"), "the menu is on top"
            value("window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))")
            until("!document.querySelector('.ctx-menu')", "the menu closed")

            # ---- a saved conversation is gone on with by a message: a chat that runs it, listed once
            value("Array.from(document.querySelectorAll('.proj .th-row')).find(n => n.textContent.includes('结算页')).click()")
            until("document.querySelectorAll('.tl-item').length === 2", "its saved messages")
            until("!!document.querySelector('.thread-head .branch .chip')", "the project's branch")
            assert on_top(".thread-head .branch .chip"), "the branch is reachable"
            value("document.querySelector('.thread-head .branch .chip').click()")
            until("!!document.querySelector('.branch-pop')", "the branch menu")
            assert on_top(".branch-pop .pop-item"), "the branch menu is on top of the conversation"
            value("document.querySelector('.thread-head .branch .chip').click()")
            value("(b => { b.focus(); b.value = '再检查一下优惠券'; b.dispatchEvent(new Event('input', { bubbles: true })); })(document.querySelector('.composer textarea'))")
            value("document.querySelector('.composer textarea').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))")
            until("!!document.querySelector('.ask-card')", "the chat that goes on with it, asking first")
            texts = value("Array.from(document.querySelectorAll('.tl-item')).map(n => n.innerText.trim().split('\\n')[0])")
            assert texts[:4] == ["结算页的金额有时多一位小数", "已经改成按分计算。", "再检查一下优惠券", "我先看看。"], texts
            assert value(titles).count("结算页金额多一位小数") == 1, value(titles)
            chats = [t for t in listed() if t[2] == SAVED]
            assert len(chats) == 1 and chats[0][1] == "chat", chats
            browser.picture(out / "chat.png")
            press(".ask-card button", "允许")
            until("Array.from(document.querySelectorAll('.thread-head .seg button')).some(b => b.textContent === '终端' && !b.disabled)", "the turn ended: it can be shown the other way")

            # ---- the same conversation on its tool's own screen, and back: one terminal at a time, the same conversation
            press(".thread-head .seg button", "终端")
            until("!!document.querySelector('.screen-frame')", "its own screen")
            until("fetch('../api/terminal').then(r => r.json()).then(j => j.terminals.filter(t => t.state === 'running' && t.session === %s).map(t => t.form || 'screen').join() === 'screen')" % q(SAVED),
                  "one terminal runs it, on its screen")
            running = [t for t in listed() if t[2] == SAVED]
            assert len(running) == 1 and running[0][0] != chats[0][0], running
            assert value(titles).count("结算页金额多一位小数") == 1
            browser.picture(out / "screen.png")
            until("Array.from(document.querySelectorAll('.thread-head .seg button')).some(b => b.textContent === '对话' && !b.disabled)", "it can be a chat again")
            press(".thread-head .seg button", "对话")
            until("!document.querySelector('.screen-frame') && document.querySelectorAll('.tl-item').length >= 2", "a chat again")
            until("fetch('../api/terminal').then(r => r.json()).then(j => j.terminals.filter(t => t.state === 'running' && t.session === %s).map(t => t.form || 'screen').join() === 'chat')" % q(SAVED),
                  "one terminal runs it, as a chat")

            # ---- the hand-over: a new conversation of another AI, or one that is open
            press(".thread-head button", "交接")
            until("!!document.querySelector('.dlg .pick')", "the hand-over")
            offers = value("Array.from(document.querySelectorAll('.dlg .pick strong')).map(n => n.textContent)")
            assert offers[0] == "新对话 · Codex", offers
            assert on_top(".dlg .pick"), "the dialog is on top"
            browser.picture(out / "handoff.png")
            press(".dlg-foot button", "取消")

            # ---- a new conversation: the project picker opens over the page and chooses
            value("document.querySelector('.new-btn').click()")
            until("!!document.querySelector('.new-page')", "the new conversation")
            value("document.querySelector('.new-field .cselect-trigger').click()")
            until("!!document.querySelector('.cselect-menu')", "the projects")
            assert on_top(".cselect-menu .cselect-item:last-child"), "the project picker is not covered"
            press(".cselect-menu .cselect-item-label", "notes")
            assert value("document.querySelector('.new-field .cselect-val').textContent") == "notes"
            browser.picture(out / "new.png")

            # ---- a file's changes, in the middle
            value("document.querySelector('.brand').click()")
            until("!!document.querySelector('.fc-main')", "the project's changes")
            value("document.querySelector('.fc-main').click()")
            until("document.querySelectorAll('.d-line.k-add').length === 1 && document.querySelectorAll('.d-line.k-del').length === 1", "the file's changes")
            browser.picture(out / "diff.png")
            assert errors() == "[]", errors()

            # ---- a narrow window: the list, or what was opened from it
            browser.open(origin + "/console/", "document.querySelectorAll('.proj .th-row').length >= 3", "the list, narrow", width=390, height=844, deviceScaleFactor=1, mobile=True)
            assert value("getComputedStyle(document.querySelector('.col-main')).display") == "none"
            value("Array.from(document.querySelectorAll('.proj .th-row')).find(n => n.textContent.includes('补充单元测试')).click()")
            until("getComputedStyle(document.querySelector('.col-side')).display === 'none' && !!document.querySelector('.thread-head')", "the conversation, narrow")
            assert value("document.documentElement.scrollWidth <= innerWidth"), "nothing wider than the window"
            browser.picture(out / "narrow.png")
            assert errors() == "[]", errors()
        finally:
            if browser:
                browser.close()
            for process in (agent, relay):
                process.kill()
            homes.close()
    print("console: conversations listed once by project; menus with reasons; a saved one gone on with as a chat; shown on its own screen and back with the same conversation; hand-over; project picker and branch menu on top; a file's changes; narrow window")


if __name__ == "__main__":
    main()
