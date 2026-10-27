"""Pond theme against real pages and relay, with a simulated computer and existing Chromium.

Usage: python tests/pond_ui_check.py <chrome.exe> <output directory> [APK for public-relay paths]
"""
import json
import base64
import http.client
import subprocess
import sys
import tempfile
import threading
import time
import zipfile
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import readme_pictures as pictures
import server


class Browser(pictures.Browser):
    def __init__(self, *args):
        self.problems = []
        self.assets = None
        self.origin = ''
        self.asset_types = {}
        self.intercepted = set()
        self.replies = {}
        super().__init__(*args)
        self.ask("Network.enable")

    def until(self, code, what):
        try:
            super().until(code, what)
        except AssertionError:
            raise AssertionError((what, self.value("({url:location.href,text:document.body.innerText.slice(0,600)})"), self.problems))

    def ask(self, method, **params):
        self.sequence += 1
        wanted = self.sequence
        self.pipe.send({"id": wanted, "method": method, "params": params})
        while True:
            message = self.replies.pop(wanted, None)
            if message is None:
                message = self.pipe.receive(30)
            if 'id' in message and message['id'] != wanted:
                self.replies[message['id']] = message
                continue
            event, data = message.get("method"), message.get("params", {})
            if event == 'Fetch.requestPaused':
                request = data['request']
                name = urlsplit(request['url']).path[len(urlsplit(self.origin).path) + 1:] if request['url'].startswith(self.origin + '/') else ''
                if name == '' or name.endswith('/'):
                    name += 'index.html'
                if request['method'] == 'GET' and request['url'].startswith(self.origin + '/') and name in self.asset_types:
                    self.intercepted.add(name)
                    self.ask('Fetch.fulfillRequest', requestId=data['requestId'], responseCode=200,
                             responseHeaders=[{'name': 'Content-Type', 'value': self.asset_types[name]},
                                              {'name': 'Cache-Control', 'value': 'no-store'},
                                              {'name': 'Content-Security-Policy', 'value': server.SECURITY['Content-Security-Policy']}],
                             body=base64.b64encode((self.assets / name).read_bytes()).decode())
                else:
                    self.ask('Fetch.continueRequest', requestId=data['requestId'])
            if event == "Runtime.exceptionThrown":
                self.problems.append(data)
            if event == "Runtime.consoleAPICalled" and data.get("type") == "error":
                self.problems.append(data)
            if event == "Network.loadingFailed" and not data.get("canceled"):
                self.problems.append(data)
            if event == "Network.responseReceived" and data["response"]["status"] >= 400:
                self.problems.append(data["response"])
            if message.get("id") == wanted:
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
        web = ROOT / 'web'
        bundled = len(sys.argv) > 3
        if bundled:
            web = Path(folder) / 'apk-web'
            with zipfile.ZipFile(sys.argv[3]) as apk:
                asset_types = dict(line.split('=', 1) for line in apk.read('assets/web-assets.properties').decode().splitlines() if line)
                for entry in apk.namelist():
                    if entry.startswith('assets/web/') and not entry.endswith('/'):
                        path = web / entry[len('assets/web/'):]
                        assert path.resolve().is_relative_to(web.resolve())
                        path.parent.mkdir(parents=True, exist_ok=True)
                        path.write_bytes(apk.read(entry))
        relay_web = web
        if bundled:
            # A stale relay really serves the pre-pond theme list. Only intercepted APK files can supply the new UI.
            relay_web = Path(folder) / 'old-relay-web'
            (relay_web / 'terminal').mkdir(parents=True)
            old_skins = subprocess.check_output(['git', 'show', 'v1.7.3:web/terminal/skins.js'], cwd=ROOT)
            assert b'pond:' not in old_skins
            (relay_web / 'terminal/skins.js').write_bytes(old_skins)
        played.server = server.make_server("127.0.0.1", 0, str(Path(folder) / "actual-relay"), str(relay_web), password="pond-test-only", public=bundled)
        space = played.server.space
        prefix = ''
        if bundled:
            key, _ = played.server.spaces.create('127.0.0.1')
            space = played.server.spaces.get(key)
            prefix = '/c/' + key
        space.relay = played.relay
        played.origin = "http://127.0.0.1:%d" % played.server.server_port + prefix
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
            browser.ask("Network.setCookie", name="rcli", value=space.sessions.create(), url=played.origin + '/', path=prefix + '/', httpOnly=True, sameSite="Strict")
            browser.ask("Page.addScriptToEvaluateOnNewDocument", source="localStorage.setItem('rcli-skin','pond');localStorage.setItem('rcli-renderer','dom');")
            if bundled:
                connection = http.client.HTTPConnection('127.0.0.1', played.server.server_port)
                connection.request('GET', prefix + '/terminal/skins.js')
                assert b'pond:' not in connection.getresponse().read()
                connection.close()
                browser.assets, browser.origin, browser.asset_types = web, played.origin, asset_types
                browser.ask('Fetch.enable', patterns=[{'urlPattern': '*', 'requestStage': 'Request'}])
                browser.ask('Page.addScriptToEvaluateOnNewDocument', source="window.RemoteCliNative={pref:k=>k==='skin'?(localStorage.getItem('native-skin')||'pond'):k==='termskin'?(localStorage.getItem('native-termskin')||''):localStorage.getItem('rcli-'+k)||'',setPref:(k,v)=>v==='app'?localStorage.removeItem('native-'+k):localStorage.setItem('native-'+k,v),chrome:()=>{}};")
            report = []
            for width in (390, 1440, 320, 430):
                for page, ready in (("terminal/?id=" + terminal, "window.TerminalUI?.screenText().includes('formatPrice')"),
                                    ("chat/?id=" + chat, "document.querySelector('#said .text')?.textContent.includes('formatPrice')"),
                                    ("computer/", "!!document.querySelector('#skins button[data-key=pond]')")):
                    browser.open(played.origin + "/" + page, ready, page, width=width, height=844, deviceScaleFactor=1, mobile=width < 500)
                    until("document.querySelector('.pond-fish')?.naturalWidth > 0", "decoded pond textures")
                    time.sleep(.3)
                    geometry = value("(() => {const m=document.querySelector('main').getBoundingClientRect();return {inner:innerWidth,client:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth,body:document.body.scrollWidth,left:m.left,right:innerWidth-m.right,h1:document.querySelectorAll('h1').length,duplicates:[...document.querySelectorAll('[id]')].map(n=>n.id).filter((id,i,a)=>a.indexOf(id)!==i),broken:[...document.images].filter(i=>!i.complete||!i.naturalWidth).length}})()")
                    assert geometry["inner"] == width and max(geometry["scroll"], geometry["body"]) <= width, geometry
                    assert abs(geometry["left"] - geometry["right"]) <= 1 and not geometry["duplicates"] and not geometry["broken"], geometry
                    before = value("getComputedStyle(document.querySelector('.pond-fish')).transform")
                    time.sleep(.25)
                    assert value("getComputedStyle(document.querySelector('.pond-fish')).transform") != before, "pond is not animated"
                    value("document.querySelector('.pond-toggle').click()")
                    value('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))')
                    before = value("getComputedStyle(document.querySelector('.pond-fish')).transform")
                    time.sleep(.2)
                    assert value("getComputedStyle(document.querySelector('.pond-fish')).transform") == before, "pause"
                    browser.picture(out / (page.split('/')[0] + "-%d.png" % width))
                    value("document.querySelector('.pond-toggle').click()")
                    report.append({"page": page.split('/')[0], "width": width, **geometry})
                    if page.startswith("terminal"):
                        value("document.querySelector('[data-key=tab]').click()")
                        for _ in range(40):
                            value('true')  # Pump intercepted requests while waiting for relay acknowledgments.
                            if any(o["action"] == "input" and o.get("data") == "\t" for o in played.asked):
                                break
                            time.sleep(.1)
                        else:
                            raise AssertionError("terminal input did not reach relay")

            # Select via the actual settings control, and confirm no duplicate instances after rapid changes.
            value("document.querySelector('#skins button[data-key=paper]').click()")
            assert value("document.querySelector('.pond-background') === null")
            value("document.querySelector('#skins button[data-key=pond]').click()")
            until("document.querySelector('.pond-fish')?.naturalWidth > 0", "restart")
            assert value("document.querySelectorAll('.pond-background').length") == 1
            browser.ask("Emulation.setEmulatedMedia", features=[{"name": "prefers-reduced-motion", "value": "reduce"}])
            browser.ask("Page.captureScreenshot", format="png")
            until("document.querySelector('.pond-toggle').disabled", "reduced motion state")
            time.sleep(.2)
            before = value("getComputedStyle(document.querySelector('.pond-fish')).transform")
            time.sleep(.25)
            assert value("getComputedStyle(document.querySelector('.pond-fish')).transform") == before
            assert value("document.querySelector('.pond-toggle').disabled"), value("({reduce:matchMedia('(prefers-reduced-motion: reduce)').matches,buttons:[...document.querySelectorAll('.pond-toggle')].map(b=>({label:b.title,disabled:b.disabled})),canvas:document.querySelectorAll('.pond-background').length})")
            browser.ask("Emulation.setEmulatedMedia", features=[{"name": "forced-colors", "value": "active"}])
            until("document.querySelector('.pond-background').hidden", "forced colors")
            browser.ask("Emulation.setEmulatedMedia", features=[])
            # Lifecycle is exercised through the class directly with observable animation state.
            value("import('../themes/pond.js').then(({PondTheme})=>{window.fixturePond=new PondTheme();fixturePond.setEnabled(true)})")
            value("dispatchEvent(new Event('pagehide'))")
            assert value("fixturePond.background.dataset.running") == 'false'
            value("dispatchEvent(new Event('pageshow'))")
            until("fixturePond.background.dataset.running === 'true'", "resume")
            value("fixturePond.setEnabled(false)")
            browser.ask("Page.addScriptToEvaluateOnNewDocument", source="localStorage.setItem('rcli-renderer','webgl')")
            # Match headless Chrome's physical scale; CDP-only DPR emulation reports inconsistent device pixel boxes to WebGL.
            browser.open(played.origin + '/terminal/?id=' + terminal, "window.TerminalUI?.screenText().includes('formatPrice')", "WebGL terminal", width=390, height=844, deviceScaleFactor=1, mobile=True)
            until("document.querySelector('.pond-fish')?.naturalWidth > 0", "WebGL pond")
            assert value("TerminalUI.drawing()") == 'webgl'
            time.sleep(.4)
            browser.picture(out / 'terminal-webgl-before.png')
            for skin in ('paper', 'pond', 'night', 'pond'):
                value("TerminalUI.setSkin('%s')" % skin)
                assert value("TerminalUI.terminal.options.theme.background") == ('#10191b66' if skin == 'pond' else value("RemoteCliSkins.%s.t.background" % skin))
            time.sleep(.4)
            browser.picture(out / 'terminal-webgl.png')
            # The user's exact entry: terminal top-right menu, with an independent terminal preference.
            value("document.getElementById('menu').click()")
            value("[...document.querySelectorAll('#rcli-menu button')].find(b=>b.textContent==='更换外观').click()")
            assert value("[...document.querySelectorAll('#rcli-menu button')].some(b=>b.textContent==='锦鲤池塘')")
            value("[...document.querySelectorAll('#rcli-menu button')].find(b=>b.textContent==='锦鲤池塘').click()")
            until("document.querySelector('.pond-fish')?.naturalWidth > 0", 'menu selection')
            if bundled:
                assert value("RemoteCliNative.pref('termskin')") == 'pond'
                value("RemoteCliNative.setPref('termskin','night')")
                browser.ask('Page.reload')
                until("document.documentElement.dataset.skin==='night' && window.TerminalUI?.screenText().includes('formatPrice')", 'independent terminal preference')
                value("document.getElementById('menu').click()")
                value("[...document.querySelectorAll('#rcli-menu button')].find(b=>b.textContent==='更换外观').click()")
                value("[...document.querySelectorAll('#rcli-menu button')].find(b=>b.textContent==='跟随 App 外观').click()")
                until("document.querySelector('.pond-fish')?.naturalWidth > 0", 'follow app pond')
                assert value("RemoteCliNative.pref('termskin')") == ''
            # Palette contrast, including the maximum possible white pond pixel at .42 opacity behind terminal .4 tint.
            contrast = value("(() => {const p=RemoteCliSkins.pond;const rgb=h=>h.slice(1).match(/../g).map(v=>parseInt(v,16));const lum=c=>c.map(v=>{v/=255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4}).reduce((a,v,i)=>a+v*[.2126,.7152,.0722][i],0);const ratio=(a,b)=>(Math.max(lum(a),lum(b))+.05)/(Math.min(lum(a),lum(b))+.05);const bg=rgb(p.t.background);return {text:ratio(rgb(p.t.foreground),bg.map(v=>v+(255-v)*.42*.6)),muted:ratio(rgb(p.muted),rgb(p.raised)),button:ratio(rgb(p.onAccent),rgb(p.accent))}})()")
            assert min(contrast.values()) >= 4.5, contrast
            assert not browser.problems, browser.problems
            if bundled:
                assert {'terminal/skins.js', 'terminal/index.html', 'themes/pond.js', 'themes/assets/koi-amber.png', 'themes/assets/koi-pearl.png', 'chat/index.html'} <= browser.intercepted, browser.intercepted
            (out / "report.json").write_text(json.dumps({"layouts": report, "contrast": contrast, "errors": browser.problems}, ensure_ascii=False, indent=2), encoding="utf-8")
            print("Pond checks passed: 12 layouts; real terminal text/input; animation/pause; switching; reduced motion; forced colors; lifecycle; contrast; no script/HTTP errors.")
        finally:
            browser.close()
            played.close()


if __name__ == "__main__":
    main()
