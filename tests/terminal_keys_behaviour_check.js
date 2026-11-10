// Runs the real renderKeys() in a tiny fake DOM: a tap must send the key,
// a long press must open its editor, and the synthetic click after the press
// must be swallowed. Called by tests/terminal_keys_check.py when node exists.
const fs = require('fs');
const vm = require('vm');
const path = require('path');

const root = path.resolve(__dirname, '..');
const source = fs.readFileSync(path.join(root, 'web/terminal/terminal.js'), 'utf8');
const start = source.indexOf('function renderKeys() {');
if (start < 0) throw new Error('renderKeys() not found');
let depth = 0, end = start;
for (; end < source.length; end++) {
  if (source[end] === '{') depth++;
  else if (source[end] === '}' && --depth === 0) { end++; break; }
}
const code = source.slice(start, end);

function button() {
  const listeners = {};
  return {
    listeners, attrs: {}, textContent: '', title: '', type: '', className: '',
    addEventListener(name, fn) { (listeners[name] = listeners[name] || []).push(fn); },
    setAttribute(name, value) { this.attrs[name] = value; },
    fire(name, event) { (listeners[name] || []).forEach(fn => fn(event || { preventDefault() {} })); }
  };
}

function render(item, keyPanel) {
  const host = { children: [], replaceChildren() { this.children = []; }, append(el) { this.children.push(el); } };
  const made = [];
  const calls = { sent: [], edited: [] };
  const context = {
    document: {
      querySelector: sel => sel === '#custom-keys' ? host : null,
      createElement: () => { const el = button(); made.push(el); return el; }
    },
    customKeys: [item], keyPanel,
    describe: () => 'Shift+Tab', bytesOf: () => '\x1b[Z',
    send: data => calls.sent.push(data), keepFocus: () => {}, openEditor: one => calls.edited.push(one),
    setTimeout, clearTimeout, console
  };
  vm.createContext(context);
  vm.runInContext(code + '\nrenderKeys();', context);
  return { button: made[0], host, calls };
}

const yolo = { id: 'x', kind: 'combo', label: 'yolo', mods: { shift: true }, base: 'tab', bytes: '\x1b[Z' };
const text = { id: 't', kind: 'text', label: 'hi', text: 'echo hi', bytes: 'echo hi' };

async function main() {
  const tap = render(yolo, true);                 // even with the panel "open" a tap sends
  if (tap.button.textContent !== 'yolo*') throw new Error('a combination key needs the * suffix');
  if (!/长按编辑/.test(tap.button.title)) throw new Error('the title must mention the long press');
  tap.button.fire('click');
  if (tap.calls.sent.length !== 1 || tap.calls.sent[0] !== '\x1b[Z') throw new Error('a tap did not send the key');
  if (tap.calls.edited.length) throw new Error('a tap opened the editor');

  const plain = render(text, false);
  if (plain.button.textContent !== 'hi') throw new Error('a text snippet must not carry *');

  const press = render(yolo, false);
  press.button.fire('pointerdown');
  await new Promise(resolve => setTimeout(resolve, 620));
  press.button.fire('click');                    // the synthetic click after the long press
  if (press.calls.edited.length !== 1 || press.calls.edited[0] !== yolo) throw new Error('a long press did not edit the key');
  if (press.calls.sent.length) throw new Error('a long press also sent the key');
  console.log('Custom key behaviour OK: tap sends, long press edits, combos marked');
}

main().catch(error => { console.error(error.message); process.exit(1); });
