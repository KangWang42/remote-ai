"""Bake the pinned ASCII koi renderer into small transparent PNG textures.

Usage: python tools/pond_sprites.py <chrome.exe> <workbench directory>
Only the generated textures are used by the App; simulation stays in this build step.
"""
import base64
import sys
import tempfile
from pathlib import Path
from readme_pictures import Browser, Played

ROOT = Path(__file__).resolve().parents[1]


def main():
    work = Path(sys.argv[2]).resolve()
    work.mkdir(parents=True, exist_ok=True)
    assets = ROOT / 'web/themes/assets'
    assets.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=work, ignore_cleanup_errors=True) as folder:
        played = Played(folder)
        browser = Browser(sys.argv[1], folder)
        try:
            browser.open(played.origin+'/', '!!document.body', 'page')
            sprites = browser.value("""(async () => {
                const {Pond,PondRenderer,DARK,LIGHT}=await import('./themes/vendor/ascii-pond/pond.js');
                const {koi}=await import('./themes/vendor/ascii-pond/koi.js');
                let seed=6819; Math.random=()=>((seed=(Math.imul(seed,1664525)+1013904223)>>>0)/4294967296);
                return ['kohaku','tancho'].map((variety,index)=>{
                    const species=koi({count:1,young:0}), pond=new Pond([species]);
                    pond.resize(192,320);
                    const fish=pond.creatures[0];
                    fish.variety=variety; fish.length=248; fish.depth=.95; fish.heading=-Math.PI/2;
                    fish.phase=index?1.8:.4; fish.beat=.5;
                    for(let n=0;n<fish.nodes.length/2;n++) {
                        fish.nodes[n*2]=96+Math.sin(n/19*Math.PI)*8*(index?1:-1);
                        fish.nodes[n*2+1]=34+n/19*248;
                    }
                    const canvas=document.createElement('canvas');
                    const dark={...DARK,...species.colors.dark,page:[16,25,27],bg:[16,25,27]};
                    const renderer=new PondRenderer(canvas,{dark,light:{...LIGHT,...species.colors.light}});
                    renderer.resize(192,320,1);renderer.render(pond);
                    return canvas.toDataURL('image/png').split(',')[1];
                });
            })()""")
            for name, data in zip(('koi-amber.png','koi-pearl.png'), sprites):
                path = assets / name
                path.write_bytes(base64.b64decode(data))
                print('%s: %d bytes' % (name,path.stat().st_size))
        finally:
            browser.close()
            played.close()


if __name__ == '__main__':
    main()
