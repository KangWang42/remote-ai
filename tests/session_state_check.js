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
assert.match(app, /free\.filter\(x => x\.state === 'active'\)/,
  'only explicitly active sessions may enter the activity list');
assert.match(app, /free\.filter\(x => x\.state === 'locked'\)/,
  'retained writer locks must remain available as locked history');
assert.match(app, /here\.filter\(x => x\.state === 'active'\)/, 'and so within a project');
// A conversation an app holds is talked to in its window. Why it cannot be taken into a terminal of the phone's is
// said by the computer when that is asked for; the page does not explain it beforehand.
assert.match(fs.readFileSync(path.join(root, 'agent-windows/CodexSessions.cs'), 'utf8'), /请先在电脑原应用中结束并关闭此对话/,
  'a conversation held by an app needs to say what to do on the computer');
assert.doesNotMatch(app, /手机无法关闭它/, 'the lists and the window carry the main words only');
assert.match(app, /api\('api\/conversation'/, 'a conversation open on the computer is looked at before anything is done to it');
assert.doesNotMatch(app, /写入锁|app-server|归属待确认/, 'the page speaks of what happens on the computer, not of locks');
assert.match(page, /<details id="background" class="held-sessions"/);
assert.match(page, /电脑上闲置或被占用的对话/);
assert.match(app, /DOMPurify\.sanitize\(marked\.parse\(/, 'what a tool said is set as text from Markdown, and only after it has been made harmless');
console.log('session state checks passed');
// Into a terminal of the phone's, from the conversation's own window: offered for one that runs in a terminal on the
// computer, which the computer closes for it, at work or not; one that an app has is not closed under the app.
assert.match(app, /const inTerminal = session => sourceOfKind\(session\.kind \|\| ''\) === 'term';/);
assert.match(app, /className: 'icon take', ariaLabel: '接到手机终端', html: ICON\.shell, hidden: session\.can_takeover !== true \|\| !inTerminal\(session\)/, 'the window offers it for a terminal\'s conversation only');
assert.match(app, /session\.can_takeover === true && \(!locked \|\| inTerminal\(session\)\) \? \{ label: '接到手机终端（关闭电脑上的）', value: 'close' \}/, 'and a held press says that the computer closes it');
assert.match(app, /const current = now\(\); finish\(null\); into\(current, 'close'\);/, 'the window is left, and the terminal started with the conversation');
// The steps of a conversation stand open, each with its command and the first lines of what came back.
assert.match(app, /el\('details', \{ className: 'did', open: true \}/, 'what the tool did is shown, not folded away');
assert.match(app, /m\.detail \? el\('code', \{ className: 'ran', textContent: m\.detail \}\) : '', m\.result \? el\('pre', \{ className: 'got', textContent: m\.result \}\) : ''/);
