/* Structure guards for the terminal's custom key strip: the keys are stored per computer,
   compiled to standard control bytes, and rendered without HTML injection. */
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..');
const app = fs.readFileSync(path.join(root, 'web/terminal/terminal.js'), 'utf8');
const page = fs.readFileSync(path.join(root, 'web/terminal/index.html'), 'utf8');
const css = fs.readFileSync(path.join(root, 'web/terminal/terminal.css'), 'utf8');

assert.match(page, /<span id="custom-keys"><\/span>/, 'custom keys render into their own slot of the strip');
assert.match(page, /id="key-add"/, 'the strip carries one entry point to the editor');
assert.match(page, /id="key-editor" hidden/, 'the editor ships hidden');
assert.match(page, /id="key-ctrl"|id="key-alt"|id="key-shift"/, 'modifiers are chosen in the editor');
assert.match(app, /rcli-term-keys/, 'custom keys are stored per computer under their own key');
assert.match(app, /comboBytes\(item\.mods, item\.base\)/, 'combo keys compile through comboBytes');
assert.match(app, /charCodeAt\(0\) & 0x1f/, 'ctrl over a letter is the standard control byte');
assert.match(app, /2: 0, 3: 27, 4: 28/, 'ctrl over 2-8 uses the historic control bytes');
assert.match(app, /mods\.shift \? 1 : 0\) \+ \(mods\.alt \? 2 : 0\) \+ \(mods\.ctrl \? 4 : 0\)/,
  'xterm modifier order is shift=1, alt=2, ctrl=4 (Ctrl+Right must be ESC[1;5C, not ESC[1;2C)');
assert.match(app, /\\x\[0-9a-fA-F\]\{2\}|\\\\e/, 'escape sequences accept \\xNN and \\e');
assert.doesNotMatch(app, /#custom-keys[\s\S]*innerHTML/, 'custom key labels are not written as HTML');
assert.match(app, /slice\(0, 12\)/, 'at most twelve custom keys are kept');
assert.match(app, /send\(bytesOf\(item\)\)/, 'a custom key sends its compiled bytes');
assert.match(app, /keepFocus\(button\)/, 'custom keys keep the message box keyboard, as built-in ones do');
assert.match(css, /#key-editor\[hidden\] \{ display: none; \}/, 'the hidden attribute works despite the grid display');
assert.doesNotMatch(css, /\.keys \{[^}]*overflow-x: (?!auto)/, 'the strip keeps scrolling however many keys');

console.log('Terminal key strip checks passed: per-computer custom keys, standard byte compilation, no HTML injection');
