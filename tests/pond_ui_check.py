"""Pond theme against real pages and relay, with a simulated computer and existing Chromium.

Usage: python tests/pond_ui_check.py <chrome.exe> <output directory>
"""
import json
import sys
import tempfile
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import readme_pictures as pictures
import server


class Browser(pictures.Browser):
    def __init__(self, *args):
        self.problems = []
        super().__init__(*args)
        self.ask("Network.enable")

    def until(self, code, what):
        try:
            super().until(code, what)
        except AssertionError:
            raise AssertionError((what, self.value("({url:location.href,text:document.body.innerText.slice(0,600)})"), self.problems))

    def ask(self, method, **params):
        self.sequence += 1
        self.pipe.send({"id": self.sequence, "method": method, "params": params})
        while True:
            message = self.pipe.receive(30)
            event, data = message.get("method"), message.get("params", {})
            if event == "Runtime.exceptionThrown":
                self.problems.append(data)
            if event == "Runtime.consoleAPICalled" and data.get("type") == "error":
                self.problems.append(data)
            if event == "Network.loadingFailed" and not data.get("canceled"):
                self.problems.append(data)
            if event == "Network.responseReceived" and data["response"]["status"] >= 400:
                self.problems.append(data["response"])
            if message.get("id") == self.sequence:
                assert "error" not in message, message
                return message.get("result", {})


def main():
    out = Path(sys.argv[2]).resolve()
    out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="pond-", dir=out, ignore_cleanup_errors=True) as folder:
        played = pictures.Played(folder)
        answer = played.answer
        played.answer = lambda op: {} if op["action"] in ("input", "resize", "say") else answer(op)
        played.server.shutdown(); played.server.server_close()
        played.server = server.make_server("127.0.0.1", 0, str(Path(folder) / "actual-relay"), str(ROOT / "web"), password="pond-test-only")
        played.server.space.relay = played.relay
        played.origin = "http://127.0.0.1:%d" % played.server.server_port
        played.info["version"] = "1.7.4"
        played.info["features"].append("chat")
        threading.Thread(target=played.server.serve_forever, daemon=True).start()
        terminal = played.relay.command({"action": "start", "id": "1" * 32, "tool": "shell", "dir": "web-shop"})["terminal"]
        chat = played.relay.command({"action": "start", "id": "2" * 32, "tool": "claude", "dir": "web-shop", "form": "chat"})["terminal"]
        terminals = [{"id": terminal, "state": "running", "cols": 60, "rows": 24}, {"id": chat, "state": "running", "form": "chat", "status": "idle"}]
        code = "\x1b[2J\x1b[H\x1b[38;5;215mPS web-shop>\x1b[0m npm test\r\n\r\n  formatPrice\r\n  \x1b[32mPASS\x1b[0m keeps two decimal places\r\n  \x1b[32mPASS\x1b[0m applies the coupon\r\n\r\n\x1b[36mconst\x1b[0m total = formatPrice(12.5);\r\n// 12.50\r\n\r\nPS web-shop> "
        events = [{"t": "item", "id": "u1", "kind": "user", "text": "检查金额格式，并补上测试。"},
                  {"t": "item", "id": "t1", "kind": "text", "text": "金额统一保留两位小数。\n\n```js\nconst total = formatPrice(12.5);\n// 12.50\n```\n\n接下来检查优惠券和退款时的显示。"},
                  {"t": "state", "state": "idle", "at": int(time.time() * 1000)}]
        played.relay.agent({"info": played.info, "terminals": terminals, "output": [
            {"terminal": terminal, "seq": 1, "data": code},
            {"terminal": chat, "seq": 1, "data": "".join(json.dumps(e, ensure_ascii=False) + "\n" for e in events)}]})

        def heartbeat():
            while not played.closed:
                played.relay.agent({"info": played.info, "terminals": terminals, "sessions": played.sessions})
                time.sleep(.3)

        threading.Thread(target=heartbeat, daemon=True).start()
        browser = Browser(sys.argv[1], folder)
        try:
            value, until = browser.value, browser.until
            browser.ask("Emulation.setFocusEmulationEnabled", enabled=True)
            browser.ask("Network.setCookie", name="rcli", value=played.server.space.sessions.create(), url=played.origin, httpOnly=True, sameSite="Strict")
            browser.ask("Page.addScriptToEvaluateOnNewDocument", source="localStorage.setItem('rcli-skin','pond');localStorage.setItem('rcli-renderer','dom');")
            report = []
            for width in (390, 1440, 320, 430):
                for page, ready in (("terminal/?id=" + terminal, "window.TerminalUI?.screenText().includes('formatPrice')"),
                                    ("chat/?id=" + chat, "document.querySelector('#said .text')?.textContent.includes('formatPrice')"),
                                    ("computer/", "!!document.querySelector('#skins button[data-key=pond]')")):
                    browser.open(played.origin + "/" + page, ready, page, width=width, height=844, deviceScaleFactor=1, mobile=width < 500)
                    until("document.querySelector('.pond-background')?.width > 0", "pond canvas")
                    time.sleep(.3)
                    geometry = value("(() => {const m=document.querySelector('main').getBoundingClientRect();return {inner:innerWidth,client:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth,body:document.body.scrollWidth,left:m.left,right:innerWidth-m.right,h1:document.querySelectorAll('h1').length,duplicates:[...document.querySelectorAll('[id]')].map(n=>n.id).filter((id,i,a)=>a.indexOf(id)!==i),broken:[...document.images].filter(i=>!i.complete||!i.naturalWidth).length}})()")
                    assert geometry["inner"] == width and max(geometry["scroll"], geometry["body"]) <= width, geometry
                    assert abs(geometry["left"] - geometry["right"]) <= 1 and not geometry["duplicates"] and not geometry["broken"], geometry
                    before = value("document.querySelector('.pond-background').toDataURL()")
                    time.sleep(.25)
                    assert value("document.querySelector('.pond-background').toDataURL()") != before, "pond is not animated"
                    value("document.querySelector('.pond-toggle').click()")
                    before = value("document.querySelector('.pond-background').toDataURL()")
                    time.sleep(.2)
                    assert value("document.querySelector('.pond-background').toDataURL()") == before, "pause"
                    browser.picture(out / (page.split('/')[0] + "-%d.png" % width))
                    value("document.querySelector('.pond-toggle').click()")
                    report.append({"page": page.split('/')[0], "width": width, **geometry})
                    if page.startswith("terminal"):
                        value("document.querySelector('[data-key=tab]').click()")
                        for _ in range(40):
                            if any(o["action"] == "input" and o.get("data") == "\t" for o in played.asked):
                                break
                            time.sleep(.1)
                        else:
                            raise AssertionError("terminal input did not reach relay")

            # Select via the actual settings control, and confirm no duplicate instances after rapid changes.
            value("document.querySelector('#skins button[data-key=paper]').click()")
            assert value("document.querySelector('.pond-background') === null")
            value("document.querySelector('#skins button[data-key=pond]').click()")
            until("document.querySelector('.pond-background')?.width > 0", "restart")
            assert value("document.querySelectorAll('.pond-background').length") == 1
            browser.ask("Emulation.setEmulatedMedia", features=[{"name": "prefers-reduced-motion", "value": "reduce"}])
            browser.ask("Page.captureScreenshot", format="png")
            until("document.querySelector('.pond-toggle').disabled", "reduced motion state")
            time.sleep(.2)
            before = value("document.querySelector('.pond-background').toDataURL()")
            time.sleep(.25)
            assert value("document.querySelector('.pond-background').toDataURL()") == before
            assert value("document.querySelector('.pond-toggle').disabled"), value("({reduce:matchMedia('(prefers-reduced-motion: reduce)').matches,buttons:[...document.querySelectorAll('.pond-toggle')].map(b=>({label:b.title,disabled:b.disabled})),canvas:document.querySelectorAll('.pond-background').length})")
            browser.ask("Emulation.setEmulatedMedia", features=[{"name": "forced-colors", "value": "active"}])
            until("document.querySelector('.pond-background').hidden", "forced colors")
            browser.ask("Emulation.setEmulatedMedia", features=[])
            # Lifecycle is exercised through the class directly with observable simulation time.
            value("import('/themes/pond.js').then(({PondTheme})=>{window.fixturePond=new PondTheme();fixturePond.setEnabled(true)})")
            value("dispatchEvent(new Event('pagehide'))")
            assert value("fixturePond.frame") == 0
            value("dispatchEvent(new Event('pageshow'))")
            until("fixturePond.frame > 0", "resume")
            value("fixturePond.setEnabled(false)")
            browser.ask("Page.addScriptToEvaluateOnNewDocument", source="localStorage.setItem('rcli-renderer','webgl')")
            browser.open(played.origin + '/terminal/?id=' + terminal, "window.TerminalUI?.screenText().includes('formatPrice')", "WebGL terminal", width=390, height=844, deviceScaleFactor=2, mobile=True)
            until("document.querySelector('.pond-background')?.width > 0", "WebGL pond")
            assert value("TerminalUI.drawing()") == 'webgl'
            time.sleep(.4)
            browser.picture(out / 'terminal-webgl-before.png')
            for skin in ('paper', 'pond', 'night', 'pond'):
                value("TerminalUI.setSkin('%s')" % skin)
                assert value("TerminalUI.terminal.options.theme.background") == ('#10191b66' if skin == 'pond' else value("RemoteCliSkins.%s.t.background" % skin))
            time.sleep(.4)
            browser.picture(out / 'terminal-webgl.png')
            # Palette contrast, including the maximum possible white pond pixel at .42 opacity behind terminal .4 tint.
            contrast = value("(() => {const p=RemoteCliSkins.pond;const rgb=h=>h.slice(1).match(/../g).map(v=>parseInt(v,16));const lum=c=>c.map(v=>{v/=255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4}).reduce((a,v,i)=>a+v*[.2126,.7152,.0722][i],0);const ratio=(a,b)=>(Math.max(lum(a),lum(b))+.05)/(Math.min(lum(a),lum(b))+.05);const bg=rgb(p.t.background);return {text:ratio(rgb(p.t.foreground),bg.map(v=>v+(255-v)*.42*.6)),muted:ratio(rgb(p.muted),rgb(p.raised)),button:ratio(rgb(p.onAccent),rgb(p.accent))}})()")
            assert min(contrast.values()) >= 4.5, contrast
            assert not browser.problems, browser.problems
            (out / "report.json").write_text(json.dumps({"layouts": report, "contrast": contrast, "errors": browser.problems}, ensure_ascii=False, indent=2), encoding="utf-8")
            print("Pond checks passed: 12 layouts; real terminal text/input; animation/pause; switching; reduced motion; forced colors; lifecycle; contrast; no script/HTTP errors.")
        finally:
            browser.close()
            played.close()


if __name__ == "__main__":
    main()
