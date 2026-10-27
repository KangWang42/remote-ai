"""Browser rendering diagnostics for terminal contrast and scrolling, with an isolated relay."""
import json
import sys
import tempfile
import threading
import time
from pathlib import Path
from PIL import Image

from pond_ui_check import Browser, pictures, server, ROOT


def main():
    out = Path(sys.argv[2]).resolve()
    out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=out, ignore_cleanup_errors=True) as folder:
        played = pictures.Played(folder)
        answer = played.answer
        played.answer = lambda op: {} if op['action'] in ('resize','input') else answer(op)
        played.server.shutdown()
        played.server.server_close()
        played.server = server.make_server('127.0.0.1', 0, str(Path(folder)/'relay'), str(ROOT/'web'), password='render-test')
        played.server.space.relay = played.relay
        played.origin = 'http://127.0.0.1:%d' % played.server.server_port
        threading.Thread(target=played.server.serve_forever, daemon=True).start()
        browser = Browser(sys.argv[1], folder)
        try:
            value = browser.value
            browser.ask('Emulation.setFocusEmulationEnabled', enabled=True)
            browser.ask('Emulation.setCPUThrottlingRate', rate=4)
            browser.ask('Emulation.setTouchEmulationEnabled', enabled=True)
            browser.ask('Network.setCookie', name='rcli', value=played.server.space.sessions.create(), url=played.origin+'/', httpOnly=True)
            browser.ask('Performance.enable')
            terminal = played.relay.command({'action':'start','id':'a'*32,'tool':'shell','dir':'web-shop'})['terminal']
            terminals = [{'id':terminal,'state':'running','cols':50,'rows':35}]
            played.relay.agent({'info':played.info,'terminals':terminals,'output':[{'terminal':terminal,'seq':1,'data':'Fixture ready\r\n'}]})
            def heartbeat():
                while not played.closed:
                    played.relay.agent({'info':played.info,'terminals':terminals})
                    time.sleep(.3)
            threading.Thread(target=heartbeat,daemon=True).start()
            report = []
            for renderer in ('dom', 'webgl'):
                for skin in ('paper', 'night', 'pond'):
                    browser.ask('Page.addScriptToEvaluateOnNewDocument', source="localStorage.setItem('rcli-skin','%s');localStorage.setItem('rcli-renderer','%s');Math.random=()=>.42;" % (skin, renderer))
                    browser.open(played.origin+'/terminal/?id='+terminal, "window.TerminalUI?.screenText().includes('Fixture ready')", 'terminal', width=390, height=844, deviceScaleFactor=1, mobile=True)
                    # Include truecolour/indexed white, dim and explicit contrasting backgrounds.
                    lines = '\x1b[0mDefault text\r\n' + '\r\n'.join('\x1b['+code+'m'+label+' MMMMWWWW\x1b[0m' for code,label in (
                        ('37','ANSI white'),('97','Bright white'),('38;5;255','Indexed white'),('38;2;255;255;255','RGB white'),
                        ('2;38;2;255;255;255','Dim RGB white'),('38;2;215;215;215','RGB grey'),('30;107','Black on white'),('97;40','White on black')))
                    value('new Promise(resolve=>TerminalUI.terminal.write('+json.dumps('\x1b[2J\x1b[H'+lines)+',resolve))')
                    time.sleep(.3)
                    screenshot = out/(renderer+'-'+skin+'-contrast.png')
                    browser.picture(screenshot)
                    ink = []
                    if skin == 'paper':
                        box = value("(()=>{const r=document.querySelector('.xterm-screen').getBoundingClientRect();return {x:r.x,y:r.y,h:r.height/TerminalUI.terminal.rows}})()")
                        def lum(rgb):
                            parts = [v/255/12.92 if v/255 <= .04045 else ((v/255+.055)/1.055)**2.4 for v in rgb]
                            return sum(v*w for v,w in zip(parts,(.2126,.7152,.0722)))
                        with Image.open(screenshot).convert('RGB') as shot:
                            for row in range(7):
                                region = shot.crop((int(box['x']),int(box['y']+row*box['h']),int(box['x']+180),int(box['y']+(row+1)*box['h'])))
                                pixels = [region.getpixel((x,y)) for y in range(region.height) for x in range(region.width)]
                                contrasts = sorted(((lum((251,251,252))+.05)/(lum(p)+.05) for p in pixels), reverse=True)
                                ink.append(round(contrasts[20],2))
                        assert min(ink) >= 4.5, (renderer,'white/grey text lacks readable strokes',ink)
                    colors = value("({contrast:TerminalUI.terminal.options.minimumContrastRatio,transparent:TerminalUI.terminal.options.allowTransparency,spans:[...document.querySelectorAll('.xterm-rows>div')].slice(0,9).map(r=>[...r.children].map(e=>({text:e.textContent,color:getComputedStyle(e).color,background:getComputedStyle(e).backgroundColor,opacity:getComputedStyle(e).opacity})))})")
                    before = {m['name']:m['value'] for m in browser.ask('Performance.getMetrics')['metrics']}
                    time.sleep(3)
                    after = {m['name']:m['value'] for m in browser.ask('Performance.getMetrics')['metrics']}
                    value("new Promise(resolve=>TerminalUI.terminal.write('\\x1b[0m'+Array.from({length:300},(_,i)=>'History '+i+' text text text\\r\\n').join(''),resolve))")
                    time.sleep(.2)
                    start = value('TerminalUI.terminal.buffer.active.viewportY')
                    browser.ask('Input.dispatchTouchEvent', type='touchStart', touchPoints=[{'x':180,'y':240}])
                    for y in range(260,621,20):
                        browser.ask('Input.dispatchTouchEvent', type='touchMove', touchPoints=[{'x':180,'y':y}])
                        time.sleep(.016)
                    end = value('TerminalUI.terminal.buffer.active.viewportY')
                    info = value('({cost:TerminalUI.strip.cost,copies:TerminalUI.strip.copies,slow:TerminalUI.strip.slow,shown:TerminalUI.strip.shown})')
                    browser.ask('Input.dispatchTouchEvent', type='touchEnd', touchPoints=[])
                    assert start-end >= 15, (renderer,skin,start,end)
                    if renderer == 'webgl':
                        browser.ask('Input.dispatchTouchEvent', type='touchStart', touchPoints=[{'x':180,'y':300}])
                        value('TerminalUI.slide.begin("local",performance.now())')
                        time.sleep(.1)
                        copies = value('TerminalUI.strip.copies')
                        for _ in range(3):
                            value('TerminalUI.terminal.refresh(0,TerminalUI.terminal.rows-1)')
                            time.sleep(.04)
                        assert value('TerminalUI.strip.copies') == copies, 'identical repaint recopied pixels'
                        value("new Promise(resolve=>TerminalUI.terminal.write('\\x1b[1;1H\\x1b[31mChanged\\x1b[0m',resolve))")
                        time.sleep(.1)
                        assert value('TerminalUI.strip.copies') > copies, 'new output failed to invalidate cache'
                        copies = value('TerminalUI.strip.copies')
                        value("new Promise(resolve=>TerminalUI.terminal.write('\\x1b[1;1H\\x1b[32mChanged\\x1b[0m',resolve))")
                        time.sleep(.1)
                        assert value('TerminalUI.strip.copies') > copies, 'same text with new ANSI colour used stale pixels'
                        browser.ask('Input.dispatchTouchEvent', type='touchEnd', touchPoints=[])
                    browser.ask('Input.dispatchTouchEvent', type='touchStart', touchPoints=[{'x':180,'y':300}])
                    value('TerminalUI.slide.cancel();TerminalUI.terminal.scrollToLine(100)')
                    reading = value('TerminalUI.terminal.buffer.active.viewportY')
                    value("new Promise(resolve=>{for(let i=0;i<100;i++)TerminalUI.terminal.write('\\x1b[999;1H\\r\\nBurst '+i, i===99?resolve:undefined)})")
                    assert value('TerminalUI.terminal.buffer.active.viewportY') == reading, 'output pulled reader back to bottom'
                    browser.ask('Input.dispatchTouchEvent', type='touchEnd', touchPoints=[])
                    value("TerminalUI.setSkin('pond');TerminalUI.setSkin('paper')")
                    assert value('TerminalUI.terminal.options.allowTransparency') is False
                    assert value('TerminalUI.terminal.options.minimumContrastRatio') == 9
                    report.append({'renderer':renderer,'skin':skin,'colors':colors,'inkContrast':ink,'idleSeconds':3,'scriptSeconds':after['ScriptDuration']-before['ScriptDuration'],'taskSeconds':after['TaskDuration']-before['TaskDuration'],'scroll':{'before':start,'after':end,**info}})
            assert not browser.problems, browser.problems
            (out/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
            print(json.dumps([{k:v for k,v in item.items() if k!='colors'} for item in report]))
        finally:
            browser.close()
            played.close()


if __name__ == '__main__':
    main()
