"""Measure the pond at 4x CPU slowdown and exercise real terminal touch scrolling.

Usage: python tests/pond_performance_check.py <chrome.exe> <output directory>
"""
import json
import sys
import tempfile
import threading
import time
from pathlib import Path

from pond_ui_check import Browser, pictures, server, ROOT


def main():
    out = Path(sys.argv[2]).resolve()
    out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='performance-', dir=out, ignore_cleanup_errors=True) as folder:
        played = pictures.Played(folder)
        played.server.shutdown()
        played.server.server_close()
        played.server = server.make_server('127.0.0.1', 0, str(Path(folder) / 'relay'), str(ROOT / 'web'), password='performance-test')
        played.server.space.relay = played.relay
        played.origin = 'http://127.0.0.1:%d' % played.server.server_port
        threading.Thread(target=played.server.serve_forever, daemon=True).start()
        browser = Browser(sys.argv[1], folder)
        try:
            value = browser.value
            browser.ask('Emulation.setFocusEmulationEnabled', enabled=True)
            browser.ask('Emulation.setCPUThrottlingRate', rate=4)
            browser.ask('Emulation.setTouchEmulationEnabled', enabled=True, maxTouchPoints=1)
            browser.ask('Network.setCookie', name='rcli', value=played.server.space.sessions.create(), url=played.origin+'/', httpOnly=True, sameSite='Strict')
            browser.ask('Page.addScriptToEvaluateOnNewDocument', source="localStorage.setItem('rcli-skin','pond');localStorage.setItem('rcli-renderer','dom');Math.random=()=>.42;")
            browser.open(played.origin + '/computer/', '!!window.RemoteCliSkins', 'page', width=390, height=844, deviceScaleFactor=1, mobile=True)
            value("RemoteCliPaint('night');import('../themes/pond.js').then(({PondTheme})=>{window.measuredPond=new PondTheme();measuredPond.setEnabled(true)})")
            browser.until('window.measuredPond?.ready', 'decoded pond textures')
            # Isolate one instance; the production page theme remains covered by pond_ui_check.
            assert value("document.querySelectorAll('canvas.pond-background').length") == 0
            assert not value("performance.getEntriesByType('resource').some(r=>r.name.includes('vendor/ascii-pond'))"), 'runtime simulation was loaded'
            value('window.keptFish=measuredPond.background.firstElementChild')
            browser.ask('Performance.enable')
            metrics = lambda: {m['name']:m['value'] for m in browser.ask('Performance.getMetrics')['metrics']}
            before = metrics()
            time.sleep(3)
            after = metrics()
            timings = {'idleSeconds':3,'scriptSeconds':after['ScriptDuration']-before['ScriptDuration'],'taskSeconds':after['TaskDuration']-before['TaskDuration']}
            value("measuredPond.paused=false;measuredPond.sync()")
            time.sleep(.3)
            value("document.dispatchEvent(new TouchEvent('touchstart',{touches:[new Touch({identifier:1,target:document.body,clientX:100,clientY:100})]}))")
            value('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))')
            frozen = value('getComputedStyle(keptFish).transform')
            time.sleep(1.5)
            assert value('getComputedStyle(keptFish).transform') == frozen, 'held touch must stop movement'
            value("document.dispatchEvent(new TouchEvent('touchend',{touches:[]}))")
            time.sleep(.5)
            assert value('getComputedStyle(keptFish).transform') == frozen, 'inertia grace period'
            browser.until("measuredPond.background.dataset.running === 'true'", 'resume after idle')
            value("document.dispatchEvent(new Event('input'));dispatchEvent(new Event('pagehide'))")
            time.sleep(1.4)
            assert value("measuredPond.background.dataset.running === 'false' && measuredPond.idleTimer === 0"), 'idle timer restarted a departed page'
            value("dispatchEvent(new Event('pageshow'))")
            value("document.dispatchEvent(new Event('input'));measuredPond.setEnabled(false)")
            time.sleep(1.4)
            assert value("measuredPond.background.dataset.running === 'false' && measuredPond.idleTimer === 0 && !measuredPond.background.isConnected"), 'disabled theme restarted'
            value('measuredPond.setEnabled(true)')
            browser.until("measuredPond.background.dataset.running === 'true'", 'theme enabled again after input')
            assert value('keptFish === measuredPond.background.firstElementChild'), 'decoded image was discarded'
            value('measuredPond.setEnabled(false)')
            # Use the real terminal page with a simulated shell and enough history to exercise scrolling.
            terminal = played.relay.command({'action':'start','id':'a'*32,'tool':'shell','dir':'web-shop'})['terminal']
            scrolls = []
            for renderer in ('dom', 'webgl'):
                browser.ask('Page.addScriptToEvaluateOnNewDocument', source="localStorage.setItem('rcli-renderer','%s')" % renderer)
                browser.open(played.origin + '/terminal/?id=' + terminal, '!!window.TerminalUI', 'terminal', width=390, height=844, deviceScaleFactor=1, mobile=True)
                value("new Promise(resolve=>TerminalUI.terminal.write(Array.from({length:250},(_,i)=>'History row '+i+'\\r\\n').join(''),resolve))")
                time.sleep(.3)
                before = value('TerminalUI.terminal.buffer.active.viewportY')
                browser.ask('Input.dispatchTouchEvent', type='touchStart', touchPoints=[{'x':180,'y':260}])
                value('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))')
                frozen = value("getComputedStyle(document.querySelector('.pond-fish')).transform")
                smooth = False
                for y in range(280, 621, 20):
                    browser.ask('Input.dispatchTouchEvent', type='touchMove', touchPoints=[{'x':180,'y':y}])
                    time.sleep(.016)
                    smooth |= bool(value('TerminalUI.strip.shown'))
                during = value('TerminalUI.terminal.buffer.active.viewportY')
                assert value("getComputedStyle(document.querySelector('.pond-fish')).transform") == frozen, 'pond moves during scroll'
                browser.picture(out / ('terminal-scrolled-' + renderer + '.png'))
                browser.ask('Input.dispatchTouchEvent', type='touchEnd', touchPoints=[])
                time.sleep(.2)
                assert before - during >= 15, (renderer, before, during)
                if renderer == 'webgl':
                    assert smooth, 'pond disabled the smooth scrolling layer'
                scrolls.append({'renderer':renderer,'before':before,'during':during,'smooth':smooth})
            assert not browser.problems, browser.problems
            result = {'cpuSlowdown':4,'idle':timings,'scroll':scrolls}
            (out / 'report.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
            print(json.dumps(result))
        finally:
            browser.close()
            played.close()


if __name__ == '__main__':
    main()
