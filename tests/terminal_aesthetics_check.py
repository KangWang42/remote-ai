"""Regression checks for terminal rendering options, quiet connection state,
auto system theme resolution, and native Apple-style polish helpers.
"""
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
skins_js = (ROOT / "web/terminal/skins.js").read_text(encoding="utf-8")
term_html = (ROOT / "web/terminal/index.html").read_text(encoding="utf-8")
term_css = (ROOT / "web/terminal/terminal.css").read_text(encoding="utf-8")
term_js = (ROOT / "web/terminal/terminal.js").read_text(encoding="utf-8")
bridge_js = (ROOT / "web/terminal/bridge.js").read_text(encoding="utf-8")
kit_java = (ROOT / "android/src/io/github/kangwang42/remotecli/Kit.java").read_text(encoding="utf-8")
main_java = (ROOT / "android/src/io/github/kangwang42/remotecli/MainActivity.java").read_text(encoding="utf-8")

# Surface hierarchy: light paper theme uses soft canvas + elevated white cards while preserving calm terminal screen.
assert "bg: '#f2f3f7', panel: '#ffffff'" in skins_js
assert "background: '#fbfbfc'" in skins_js
assert "window.RemoteCliResolveSkin" in skins_js and "window.RemoteCliOnSystemTheme" in skins_js

# Terminal rendering craft: Unicode 11, WebLinks, customGlyphs, rescaleOverlappingGlyphs, cursor styles & pinch zoom.
assert "vendor/addon-unicode11.js" in term_html and "vendor/addon-web-links.js" in term_html
assert "customGlyphs: true" in term_js and "rescaleOverlappingGlyphs: true" in term_js
assert "cursorInactiveStyle: 'outline'" in term_js
assert "Unicode11Addon" in term_js and "WebLinksAddon" in term_js
assert "zoom-hud" in term_html and "scroll-track" in term_html
assert 'id="connection" hidden' in term_html
assert "connection.hidden = true" in term_js
assert "composer-shell" in term_html and ".composer-shell" in term_css
assert "cursor: 'cursor'" in bridge_js and "saveCursor" in bridge_js

# Native Android polish: hairline dividers, soft elevation, tabular sans figures, auto skin, haptics & URL bridge.
assert "int hairline()" in kit_java and "void elevate(" in kit_java and "TextView figure(" in kit_java
assert "resolveSkin(" in main_java and "public void haptic(" in main_java and "public void openUrl(" in main_java

# Verify auto skin resolution in Node VM for both light and dark system preferences.
if shutil.which("node"):
    node_check = r"""
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const src = fs.readFileSync('web/terminal/skins.js', 'utf8');
function run(dark) {
  const vars = {};
  const ctx = {
    URL,
    document: { currentScript: { src: 'https://local.invalid/terminal/skins.js' }, documentElement: { style: { setProperty(k, v) { vars[k] = v; } }, dataset: {} } },
    matchMedia: () => ({ matches: dark, addEventListener() {} }),
  };
  ctx.window = ctx;
  vm.runInNewContext(src, ctx);
  const s = ctx.window.RemoteCliPaint('auto');
  return { resolved: ctx.document.documentElement.dataset.skin, theme: ctx.document.documentElement.dataset.theme, vars, s };
}
const light = run(false);
assert.equal(light.resolved, 'paper');
assert.equal(light.theme, 'light');
assert.equal(light.vars['--bg'], '#f2f3f7');
assert.equal(light.vars['--panel'], '#ffffff');
assert.equal(light.vars['--term-bg'], '#fbfbfc');
const dark = run(true);
assert.equal(dark.resolved, 'night');
assert.equal(dark.theme, 'dark');
assert.equal(dark.vars['--bg'], '#1a1b26');
"""
    subprocess.run(["node", "-e", node_check], cwd=str(ROOT), check=True)

print("Terminal aesthetics and native polish checks passed")
