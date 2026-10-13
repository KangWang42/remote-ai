/* Regression checks for the visible-session/retained-lock split. */
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..');
const app = fs.readFileSync(path.join(root, 'web/app.js'), 'utf8');
const page = fs.readFileSync(path.join(root, 'web/index.html'), 'utf8');

assert.match(app, /function sessionActivity\(s\)/);
assert.match(app, /state: sessionActivity\(s\)/, 'how a conversation stands in the lists is what sessionActivity says of it');
assert.match(app, /free\.filter\(working\)/,
  'only executing sessions enter the running list');
assert.match(app, /free\.filter\(x => x\.live && !working\(x\)\)/,
  'open but waiting sessions remain in the opened list');
assert.match(app, /const here = inside\.filter\(x => !x\.t\), open2 = inside\.filter\(x => x\.live && !working\(x\) && !needsYou\(x\)/, 'and so within a project');
// A conversation an app holds is talked to in its window. Why it cannot be taken into a terminal of the phone's is
// said by the computer when that is asked for; the page does not explain it beforehand.
assert.match(fs.readFileSync(path.join(root, 'agent-windows/CodexSessions.cs'), 'utf8'), /请先在电脑原应用中结束并关闭此对话/,
  'a conversation held by an app needs to say what to do on the computer');
assert.doesNotMatch(app, /手机无法关闭它/, 'the lists and the window carry the main words only');
assert.match(app, /api\('api\/conversation'/, 'a conversation open on the computer is looked at before anything is done to it');
assert.doesNotMatch(app, /写入锁|app-server|归属待确认/, 'the page speaks of what happens on the computer, not of locks');
assert.match(page, /<section id="opened" hidden>/);
assert.match(page, /<h2>已打开<\/h2>/);
assert.match(app, /DOMPurify\.sanitize\(marked\.parse\(/, 'what a tool said is set as text from Markdown, and only after it has been made harmless');
console.log('session state checks passed');
// Into a terminal of the phone's, from the conversation's own window: offered for one that runs in a terminal on the
// computer, which the computer closes for it, at work or not; a desktop/shared app remains in collaboration mode.
assert.match(app, /const inTerminal = session => sourceOfKind\(session\.kind \|\| ''\) === 'term';/);
assert.match(app, /className: 'icon take', ariaLabel: '接到手机终端', html: ICON\.shell, hidden: session\.can_takeover !== true \|\| !inTerminal\(session\)/, 'the window offers it for a terminal\'s conversation only');
assert.match(app, /if \(session\.can_takeover === true && \(!locked \|\| inTerminal\(session\)\)\) \{/, 'a hand-off is only offered after ownership is verified');
assert.doesNotMatch(app, /电脑上已关闭时.*value: 'close'/, 'unverified app sessions must not submit a takeover request');
assert.match(app, /const controlMode = session =>/, 'conversation window defines an explicit control mode');
assert.match(app, /共享协作：消息会交给电脑上的原对话，电脑窗口继续保持打开/, 'shared mode explains that the computer app remains usable');
assert.match(app, /手机消息 · 正在送达/, 'phone-originated messages expose delivery state');
assert.match(app, /const current = now\(\); finish\(null\); into\(current, 'close'\);/, 'the window is left, and the terminal started with the conversation');
// The steps of a conversation stand open, each with its command and the first lines of what came back.
assert.match(app, /el\('details', \{ className: 'did', open: saved\('steps', 'open'\) !== 'shut' \}/, 'what the tool did is shown, not folded away, unless its reader chose that');
assert.match(app, /m\.detail \? el\('code', \{ className: 'ran', textContent: m\.detail \}\) : '', m\.result \? el\('pre', \{ className: 'got', textContent: m\.result \}\) : ''/);

// The window says in one line how the conversation stands and for how long, offers to end it on the computer where
// the computer can, and has a way back to what was said last.
assert.match(app, /const state = el\('div', \{ className: 'state' \}, el\('span', \{ className: 'dot' \}\), standing, clock, stopping, ending\);/);
assert.match(app, /const canEnd = s => wayOf\(s, 'end'\) === 'process' && \(data\.device\.features \|\| \[\]\)\.includes\('end'\);/, 'ending is offered where the computer says it can end it');
assert.match(app, /action: 'end' \}\); toast\('已结束电脑上的这段对话'\); finish\(null\); refresh\(\);/);
assert.match(app, /className: 'latest', hidden: true, ariaLabel: '回到最新'/);
assert.match(app, /clock\.textContent = !from \? '' : kind === 'busy' \? lasted\(Date\.now\(\) - from\) : ago\(from\);/, 'at work, for how long; stopped, since when');
// What is done to a conversation goes by the way the computer names for each thing (`ways`), the same for every
// tool; how the window behaves by them is checked in a browser (tests/ways_ui_check.py). Here only that nothing in
// the page decides a way by the tool's name.
assert.match(app, /const wayOf = \(s, thing\) =>/, 'one place says by which way a thing is done');
assert.doesNotMatch(app, /tool === 'codex' && [^\n]*(say|stop)|tool === 'claude' && [^\n]*(say|stop)/, 'no way of saying or stopping is chosen by the tool\'s name');
assert.doesNotMatch(app, /'codex-desktop-open'|Claude 桌面应用暂不支持/, 'a desktop application is offered for whichever tool the computer names');
