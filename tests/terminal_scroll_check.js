'use strict';
const assert = require('node:assert/strict');
const TerminalScroller = require('../web/terminal/terminal-scroll.js');
const { TerminalScrollRouter } = TerminalScroller;
function fixture(kind = 'local', reduced = false) {
  let id = 0, now = 0, visible = true;
  const frames = new Map(), moves = [];
  const scroller = new TerminalScroller({request: f => {frames.set(++id, f); return id;},
    cancel: n => frames.delete(n), mode: () => kind, lineHeight: () => 10,
    visible: () => visible, reduced: () => reduced, scroll: (lines, mode) => {moves.push({lines, mode, at: now}); return true;}});
  return {scroller, moves, frames, step(t) {now = t; const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach(f => f(t));},
    hide() {visible = false;}, mode(m) {kind = m;}};
}
{
  const f = fixture('mouse'); f.scroller.start(0, 0); f.scroller.move(0, 0); f.scroller.move(15, 8); f.scroller.move(35, 12);
  assert.equal(f.frames.size, 1); f.step(16);
  assert.deepEqual(f.moves, [{lines: 3, mode: 'mouse', at: 16}]);
  f.scroller.move(30, 18); f.scroller.move(10, 22); f.step(32);
  assert.equal(f.moves[1].lines, -2, 'reversing a drag must not replay queued movement in the previous direction');
}
{
  const f = fixture(); f.scroller.start(0, 0); f.scroller.move(0, 0);
  for (let i = 1; i <= 5; i++) {f.scroller.move(i * 2, i * 16); f.step(i * 16);}
  assert.equal(f.moves.reduce((n, m) => n + m.lines, 0), 1, 'small movements must accumulate');
  f.scroller.move(70, 96); f.step(96); f.scroller.end(97); const before = f.moves.length;
  for (let t = 112; t < 3000; t += 16) f.step(t);
  assert.ok(f.moves.length > before, 'a flick should continue after the finger lifts');
  assert.equal(f.frames.size, 0, 'inertia must settle');
}
{
  const f = fixture('mouse'); f.scroller.start(0, 0); f.scroller.move(0, 0); f.scroller.move(1000, 16); f.scroller.end(17);
  for (let t = 32; t < 3000; t += 16) f.step(t);
  assert.ok(f.moves.every(m => Math.abs(m.lines) <= 12), 'remote scrolling must send bounded batches');
  assert.equal(f.frames.size, 0, 'remote inertia must not keep queuing input');
}
{
  const f = fixture(); f.scroller.start(0, 0); f.scroller.move(0, 0); f.scroller.move(100, 16); f.step(16); f.scroller.end(17);
  f.step(32); const count = f.moves.length; f.scroller.start(0, 33); f.scroller.move(0, 33); f.step(48);
  assert.equal(f.moves.length, count, 'a new touch cancels an old fling');
  f.scroller.move(20, 50); f.scroller.stop(); f.step(64); assert.equal(f.moves.length, count);
}
{
  // A browser tells of a finger's move once it has left the spot it touched, and then of all the way it has gone:
  // that is where the scrolling begins, and the way gone before is not caught up with.
  const f = fixture(); f.scroller.start(100, 0); f.scroller.move(115, 30);
  assert.equal(f.frames.size, 0); f.step(32);
  assert.equal(f.moves.length, 0, 'the first move only says where the finger is');
  f.scroller.move(127, 46); f.step(48);
  assert.deepEqual(f.moves.map(m => m.lines), [1], 'and what follows is scrolled from there');
}
{
  for (const action of ['hide', 'mode']) {
    const f = fixture(); f.scroller.start(0, 0); f.scroller.move(0, 0); f.scroller.move(40, 16);
    if (action === 'hide') f.hide(); else f.mode('mouse');
    f.step(16); assert.equal(f.moves.length, 0, 'hidden or changed terminal must cancel pending scrolling');
  }
  const f = fixture('local', true); f.scroller.start(0, 0); f.scroller.move(0, 0); f.scroller.move(40, 16); f.step(16); f.scroller.end(17);
  f.step(32); assert.equal(f.moves.length, 1, 'reduced-motion mode must avoid inertia');
}
{
  let tool = 'codex';
  const sent = [], handlers = {};
  const term = { cols: 80, rows: 24, modes: { mouseTrackingMode: 'any' },
    buffer: { active: { type: 'normal', viewportY: 100, baseY: 100, getLine: () => null } },
    parser: { registerCsiHandler(id, handler) { handlers[id.final] = handler; } },
    scrollLines(lines) { this.buffer.active.viewportY = Math.max(0, Math.min(100, this.buffer.active.viewportY + lines)); },
    scrollToBottom() { this.buffer.active.viewportY = this.buffer.active.baseY; }
  };
  const router = new TerminalScrollRouter(term, text => { sent.push(text); return true; }, () => tool);
  assert.equal(router.mode(), 'local', 'Codex inline history must scroll locally even if pointer reporting is enabled');
  router.scroll(8);
  assert.equal(term.buffer.active.viewportY, 92, 'pulling the content down must reveal older output');
  assert.equal(sent.length, 0, 'local history must not be turned into program input');
  term.buffer.active.type = 'alternate';
  assert.equal(router.mode(), 'mouse', 'Codex fullscreen that reports the mouse must scroll by the line');
  router.scroll(2);
  assert.equal(sent.at(-1), '\x1b[<64;40;12M'.repeat(2), 'Codex takes SGR wheel reports even before the encoding is seen');
  assert.equal(router.remoteUp, 2);
  router.latest();
  assert.equal(sent.at(-1), '\x1b[<65;40;12M'.repeat(14), 'return to latest must scroll Codex back down');
  assert.equal(router.remoteUp, 0);
  term.modes.mouseTrackingMode = 'none';
  assert.equal(router.mode(), 'page', 'Codex fullscreen navigation must work without relying on native mouse mode propagation');
  router.scroll(8);
  assert.equal(sent.at(-1), '\x1b[5~', 'older fullscreen content is PageUp');
  assert.equal(router.remoteUp, 8);
  router.latest();
  assert.equal(sent.at(-1), '\x1b[6~'.repeat(3), 'return to latest must work after page scrolling');
  router.scroll(-8);
  assert.equal(sent.at(-1), '\x1b[6~', 'the opposite gesture is PageDown');
  assert.equal(router.remoteUp, 0);
  router.reset(); router.start(); router.scroll(3); router.scroll(5);
  assert.equal(sent.at(-1), '\x1b[5~', 'short movements in one gesture accumulate to a page');
  tool = 'shell'; term.modes.mouseTrackingMode = 'any';       // any other program that reads the mouse, such as an editor
  assert.equal(router.mode(), 'mouse');
  router.scroll(1);
  assert.equal(sent.at(-1), '\x1b[M' + String.fromCharCode(96, 72, 44), 'default mouse reports must use legacy encoding');
  assert.equal(handlers.h([1006]), false, 'mode tracking must let xterm parse the sequence too');
  router.scroll(1);
  assert.equal(sent.at(-1), '\x1b[<64;40;12M');
  router.scroll(-1);
  assert.equal(sent.at(-1), '\x1b[<65;40;12M');
  handlers.l([1006]); router.scroll(1);
  assert.ok(sent.at(-1).startsWith('\x1b[M'), 'disabling SGR must restore the requested mouse encoding');
  term.modes.mouseTrackingMode = 'none';
  assert.equal(router.mode(), 'page');
  term.buffer.active.type = 'normal';
  assert.equal(router.mode(), 'local');
  router.reset(); term.buffer.active.viewportY = 100;
  assert.equal(router.scroll(-1), false, 'a local scroll at the boundary must stop inertia');
}
{
  // Claude Code, scrolled away from the end, writes its own way back over the middle of the last row it scrolls.
  // That row is found, and "回到最新" takes the program up on it: Ctrl+End, as a terminal sends it. The same words
  // elsewhere on a row, or high up on the screen, are somebody's text.
  const sent = [], rows = new Array(24).fill('');
  const term = { cols: 52, rows: 24, modes: { mouseTrackingMode: 'any' }, parser: { registerCsiHandler() {} }, scrollLines() {}, scrollToBottom() {},
    buffer: { active: { type: 'alternate', viewportY: 0, baseY: 0, getLine: y => ({ translateToString: () => rows[y] }) } } };
  const router = new TerminalScrollRouter(term, text => { sent.push(text); return true; }, () => 'claude');
  router.sgr = true;
  assert.equal(router.offered(), -1); assert.equal(router.offers, false);
  rows[18] = '\u25cf agents-md: Jump to bottom (ctrl+End) \u2193 d';
  assert.equal(router.offered(), 18, 'the offer, in the middle of a row');
  assert.equal(router.offers, true, 'and from then on the program is known to say itself when it is away from the end');
  router.remoteUp = 40;
  assert.equal(router.latest(), true);
  assert.deepEqual(sent, ['\x1b[1;5F'], 'the way back is the program\'s own, not forty rows of the wheel');
  assert.equal(router.remoteUp, 0);
  rows[18] = '  Jump to bottom (ctrl+End) is what it says when';
  assert.equal(router.offered(), -1, 'the words at the beginning of a row are text');
  rows[18] = '  it says Jump to bottom (ctrl+End) there, I think';
  assert.equal(router.offered(), -1, 'and so are they in the middle of one, without the arrow');
  // with new messages below, the same key stands behind other words
  rows[18] = '\u25cf agents-md:  3 new messages (ctrl+End) \u2193  .md';
  assert.equal(router.offered(), 18, 'the offer is known by its key and arrow, whatever it says in front');
  rows[18] = ''; rows[3] = ' '.repeat(13) + 'Jump to bottom (ctrl+End) \u2193';
  assert.equal(router.offered(), -1, 'and so are they high up on the screen');
  rows[3] = ''; router.remoteUp = 5; router.latest();
  assert.equal(sent.at(-1), '\x1b[<65;26;12M'.repeat(17), 'with no offer on the screen the program is scrolled back by the wheel');
  term.modes.mouseTrackingMode = 'none'; rows[18] = ' '.repeat(13) + 'Jump to bottom (ctrl+End) \u2193';
  assert.equal(router.offered(), -1, 'a program that does not read the mouse does not scroll itself');
}
{
  // Claude Code moves 3 lines for one wheel step, 4 for two written together and 6 for four, and further still when
  // steps follow within 80 ms: the page sends at most one group every 110 ms and counts what each group moves.
  let now = 1000;
  const sent = [];
  const term = { cols: 80, rows: 24, modes: { mouseTrackingMode: 'any' }, buffer: { active: { type: 'alternate', viewportY: 0, baseY: 0, getLine: () => null } },
    parser: { registerCsiHandler() {} }, scrollLines() {}, scrollToBottom() {} };
  const router = new TerminalScrollRouter(term, text => { sent.push({ at: now, steps: text.split('\x1b[').length - 1, up: text.includes(String.fromCharCode(96)) || text.includes('<64;') }); return true; }, () => 'claude', () => now);
  router.sgr = true;
  const frame = lines => { now += 1000 / 60; if (lines || router.owing()) router.scroll(lines, 'mouse'); };
  const moved = () => sent.reduce((rows, s) => rows + (s.up ? 1 : -1) * { 1: 3, 2: 4, 4: 6 }[s.steps], 0);
  router.start(); frame(1);
  assert.equal(sent.length, 0, 'one row is less than half of the smallest move');
  frame(1);
  assert.deepEqual(sent.map(s => s.steps), [1], 'the smallest move is sent when the finger is halfway through it');
  // a steady drag of 30 rows a second for a second, then the finger rests
  for (let i = 0; i < 60; i++) frame(i % 2);
  for (let i = 0; i < 30; i++) frame(0);
  assert.ok(Math.abs(moved() - 32) <= 1.5, 'what Claude Code is moved by must be what the finger moved: ' + moved());
  assert.ok(sent.every((s, i) => !i || s.at - sent[i - 1].at >= 110), 'groups must be far enough apart for every step to move what it says');
  assert.ok(sent.every(s => [1, 2, 4].includes(s.steps)), 'only groups whose movement is known');
  assert.equal(router.owing(), false, 'nothing is owed once the picture has caught up');
  assert.ok(Math.abs(router.remoteUp - 32) <= 1.5);
  // a fast flick is paid off afterwards, six rows a group
  const before = moved();
  for (let i = 0; i < 6; i++) frame(5);
  for (let i = 0; i < 60; i++) frame(0);
  assert.ok(Math.abs(moved() - before - 30) <= 1.5, 'a flick must arrive in full: ' + (moved() - before));
  // turning round owes nothing in the old direction
  const turned = sent.length;
  frame(4); frame(-2);
  for (let i = 0; i < 20; i++) frame(0);
  assert.ok(sent.slice(turned).filter(s => !s.up).length === 1 && sent.at(-1).up === false, 'the reverse move follows the finger back');
}
{
  // the glide after lift-off is the same wherever the scrolling is done
  const rows = kind => { const f = fixture(kind); f.scroller.start(0, 0); f.scroller.move(0, 0);
    for (let i = 1; i <= 6; i++) { f.scroller.move(i * 20, i * 16); f.step(i * 16); }
    f.scroller.end(100); for (let t = 116; t < 4000; t += 16) f.step(t);
    assert.equal(f.frames.size, 0, 'the glide must come to rest');
    return f.moves.reduce((n, m) => n + m.lines, 0); };
  assert.equal(rows('local'), rows('mouse'));
  assert.ok(rows('mouse') >= 12 + 30, 'a flick must carry on well past where the finger lifted: ' + rows('mouse'));
}
{
  // While a finger scrolls, what is seen is drawn from a strip of the rows the terminal has shown, and the program
  // is kept some rows ahead of the finger. A program like Claude Code is asked by the wheel, moves its list three
  // rows or more at a time a moment later, and keeps a heading and a message box where they are, with a line above
  // the box that changes every time it draws (how long it has been working); the finger moves
  // evenly. What is seen must stay with the finger at every frame, with rows at both edges and not a band of nothing
  // where rows are yet to come; every row must stand where it belongs; and at rest the terminal itself must show
  // what was seen, the still rows untouched.
  const { TerminalSlide } = TerminalScroller;
  const cell = 20, rows = 24, head = 1, foot = 4, latency = 150;
  let now = 0, top = 500, id = 0, shown = null, redraw = false, copies = 0, draws = 0;
  const frames = new Map(), due = [], strip = new Map();
  const screen = () => { const text = ['heading'];
    for (let i = 0; i < rows - head - foot; i++) text.push('line ' + (top + i));
    return text.concat(['', '* working (' + (500 - top) + ')', '> message', 'box bottom']); };
  const term = { cols: 80, rows, modes: { mouseTrackingMode: 'any' }, buffer: { active: { type: 'alternate', viewportY: 0, baseY: 0, getLine: () => null } },
    parser: { registerCsiHandler() {} }, scrollLines() {}, scrollToBottom() {} };
  const router = new TerminalScrollRouter(term, text => { const steps = text.split('\x1b[').length - 1, up = text.includes('<64;');
    due.push({ at: now + latency, rows: (up ? 1 : -1) * { 1: 3, 2: 4, 4: 6 }[steps] }); return true; }, () => 'claude', () => now);
  router.sgr = true;
  const slide = new TerminalSlide({ cell: () => cell, count: () => rows, viewport: () => 0, read: screen,
    draw() { redraw = true; },                  // the terminal draws in its next frame
    keep(row, place, count) { copies++; const text = screen(); for (let i = 0; i < count; i++) strip.set(place + i, text[row + i]); return true; },
    show(place, t, b, first, last) { shown = place === null ? null : { place, top: t, bottom: b, first, last }; },
    nudge: count => scroller.nudge(count) });
  const scroller = new TerminalScroller({ request: f => { frames.set(++id, f); return id; }, cancel: n => frames.delete(n), mode: () => 'mouse', lineHeight: () => cell,
    visible: () => true, reduced: () => false, owing: () => router.owing(), slide: pixels => slide.moved(pixels),
    scroll(lines, mode) { const moved = router.scroll(lines, mode); slide.ask(router.asked, now); return moved; } });
  // What the picture shows: by how many pixels the lines stand below where they stood when `from` was the list's
  // first line; how many rows of the list have nothing to show; and whether every line stands where it belongs.
  const seen = from => {
    if (!shown) return { moved: (from - top) * cell, blank: 0, whole: true, still: [0, 0] };
    const first = Math.ceil(shown.place + shown.top - 1e-9), last = Math.floor(shown.place + rows - shown.bottom - 1 + 1e-9);
    let moved = null, whole = true, blank = 0;
    for (let i = first; i <= last; i++) {
      const text = i >= shown.first && i <= shown.last ? strip.get(i) : undefined, line = /^line (\d+)$/.exec(text || '');
      if (!line) { blank++; continue; }
      const here = (i - shown.place - (Number(line[1]) - from + head)) * cell;
      if (moved === null) moved = here; else if (Math.abs(here - moved) > .01) whole = false;
    }
    return { moved, blank, whole, still: [shown.top, shown.bottom] };
  };
  // One frame: the finger's callbacks, what the program has drawn by now, and the slide's own.
  let moving = true;
  const step = (arrives = true, drop = false) => {
    const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach(f => f(now));
    if (drop) due.length = 0;
    let drew = false;
    while (arrives && due.length && due[0].at <= now) { top -= due.shift().rows; drew = true; }
    if (drew || redraw) { redraw = false; draws++; slide.drawn(); }         // what arrived within a frame is drawn at once
    moving = slide.frame(now, !scroller.dragging && !scroller.velocity && !scroller.frame);
  };
  const start = top;
  let finger = 0, worst = 0, jumps = 0, last = 0, nothing = 0;
  scroller.start(0, 0); scroller.move(0, 0); slide.begin('remote', 0); router.start();
  for (let n = 1; n <= 300; n++) {
    now = n * 1000 / 60;
    if (n <= 90) { finger = n * 4; scroller.move(finger, now); }        // 12 rows a second for a second and a half
    if (n === 91) { scroller.velocity = 0; scroller.end(now); }           // the finger is lifted without a flick
    step();
    const picture = seen(start);
    assert.ok(picture.whole, 'every line stands where it belongs, frame ' + n);
    if (n <= 90) {
      worst = Math.max(worst, Math.abs(picture.moved - finger));
      if (Math.abs(picture.moved - last) > cell * 1.2) jumps++;
      if (n > 14) nothing = Math.max(nothing, picture.blank);           // once the program has answered for the first time
    }
    last = picture.moved;
  }
  assert.ok(worst <= cell, 'the picture must stay with the finger, within a row: ' + worst / cell);
  assert.equal(jumps, 0, 'and must not jump by rows, as the program by itself does');
  assert.equal(nothing, 0, 'rows come into sight as the finger moves, not after a band of nothing: ' + nothing);
  assert.equal(shown, null, 'at rest the terminal is left as it draws itself'); assert.equal(moving, false);
  // a frame in which the terminal has not drawn costs no copy of its picture: rows are copied as it draws, and only then
  assert.ok(copies === draws && draws < 60, 'the picture is copied once for each time the terminal draws: ' + copies + ' copies, ' + draws + ' draws, 300 frames');
  assert.deepEqual([slide.top, slide.bottom], [head, foot], 'the heading and the message box are found to stand still, the line that changes with them');
  assert.ok(Math.abs((start - top) - 18) <= 1.5, 'and the program is where the finger left it, not ahead of it: ' + (start - top));
  // at the end of its list the program does not move: there is a little of nothing to see, never more, and it goes again
  const pull = () => {
    let most = 0;
    const from = top;
    scroller.start(0, now); scroller.move(0, now); slide.begin('remote', now); router.start(); due.length = 0;
    for (let n = 1; n <= 360; n++) {
      now += 1000 / 60;
      if (n <= 60) scroller.move(n * 6, now);
      if (n === 61) { scroller.velocity = 0; scroller.end(now); }
      step(true, true);                                           // asked, and not done
      most = Math.max(most, Math.abs(seen(from).moved || 0) / cell);
    }
    assert.equal(shown, null); assert.equal(moving, false); assert.equal(top, from);
    return most;
  };
  let most = pull();
  assert.ok(most > .5 && most <= 4, 'the program was moving this way: at most four rows of nothing: ' + most);
  most = pull();
  assert.ok(most <= 1.5 + .001, 'having found the end, a second pull the same way gives only a little: ' + most);
  slide.forget();
  most = pull();
  assert.ok(most > .5 && most <= 3, 'a way the program has not been seen to move in: at most three rows: ' + most);
  slide.forget();
  // The program reaches the end of its list while it is ahead of the finger: the rows it does not make are taken
  // from those it is ahead by, and the picture stays under a finger that holds it, and comes to rest where it was left.
  {
    const end = top - 20, from = top;
    scroller.start(0, now); scroller.move(0, now); slide.begin('remote', now); router.start(); due.length = 0;
    let held = null;
    for (let n = 1; n <= 420; n++) {
      now += 1000 / 60;
      if (n <= 54) scroller.move(n * 6, now);                      // 16 rows, at 18 rows a second: the program is asked for more than 20
      if (n === 300) { scroller.velocity = 0; scroller.end(now); }
      const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach(f => f(now));
      let drew = false;
      while (due.length && due[0].at <= now) { const rows = due.shift().rows, to = Math.max(end, top - rows); if (to !== top) { top = to; drew = true; } }
      if (drew || redraw) { redraw = false; slide.drawn(); }
      moving = slide.frame(now, !scroller.dragging && !scroller.velocity && !scroller.frame);
      const picture = seen(from);
      if (n === 90) held = picture.moved;                          // the finger has stopped, and holds the picture
      if (n > 90 && n < 300) assert.ok(Math.abs(picture.moved - held) < .5, 'the picture stays under the finger that holds it: ' + held + ', ' + picture.moved + ' at frame ' + n);
    }
    assert.ok(Math.abs(held - 54 * 6) <= cell, 'which is where the finger is: ' + held);
    assert.equal(shown, null); assert.equal(moving, false);
    assert.ok(Math.abs((from - top) - 16) <= 1, 'the program has come back to where the finger left it: ' + (from - top));
    top = from;
  }
  slide.forget();
  // A network that holds messages back for a while and then delivers them at once. The program was ahead of the
  // finger, so for a while there are rows to go on with; what comes after a longer wait is come on to, not jumped to.
  // (the program here answers after 150 ms and moves three rows or more at a time)
  for (const [held, pace, within, leap] of [[200, 5, 1.5, 1], [450, 5, 2, 1], [450, 9, 5, 1.5]]) {
    let finger = 0, away = 0, jump = 0, last = null;
    scroller.start(0, now); scroller.move(0, now); slide.begin('remote', now); router.start(); due.length = 0;
    const began = now, from = top;
    for (let n = 1; n <= 240; n++) {
      now += 1000 / 60;
      if (n <= 110) { finger = n * pace; scroller.move(finger, now); }      // fifteen rows a second, or twenty-seven
      if (n === 111) { scroller.velocity = 0; scroller.end(now); }
      step(!(now - began > 600 && now - began < 600 + held));               // nothing arrives in this while
      const picture = seen(from);
      assert.ok(picture.whole, 'every line stands where it belongs');
      if (n <= 110 && picture.moved !== null) { away = Math.max(away, Math.abs(picture.moved - finger)); if (last !== null) jump = Math.max(jump, Math.abs((picture.moved - last) - pace)); last = picture.moved; }
    }
    if (process.env.SLIDE_SAYS) console.log(held, pace, (away / cell).toFixed(2), (jump / cell).toFixed(2));
    assert.ok(away <= cell * within, held + ' ms held back: the picture stays near the finger: ' + away / cell);
    assert.ok(jump <= cell * leap, held + ' ms held back: and does not jump when the moves arrive: ' + jump / cell);
    assert.equal(shown, null); assert.equal(moving, false);
  }
  // the terminal's own history moves a row at a time, at once: it is kept one row ahead, and the picture is under
  // the finger with rows at both edges
  let viewport = 100, place = null;
  const kept = new Map();
  const local = new TerminalSlide({ cell: () => cell, count: () => rows, viewport: () => viewport, read: () => [], draw() {},
    keep(row, at, count) { for (let i = 0; i < count; i++) kept.set(at + i, viewport + row + i); return true; },
    show(at, t, b, first, last) { place = at === null ? null : { at, first, last }; },
    nudge(count) { viewport -= count; local.ask(count, 0); } });
  local.begin('local', 0);
  local.moved(1); local.frame(1, false);
  assert.equal(place, null, 'until the terminal has drawn there is no picture of it to show');
  local.drawn();                                // it draws, as it was asked to, before it is scrolled
  let moved = 1, off = 0, bare = 0;
  for (let n = 1; n <= 50; n++) {
    local.moved(3); moved += 3;
    const lines = Math.trunc(moved / cell) - (100 - viewport) + local.led;
    if (lines) { viewport -= lines; local.ask(lines, n * 16); }
    local.drawn(); local.frame(n * 16, false);
    // the line that stood in the terminal's first row is now `moved` pixels further down, and every row on the
    // strip is the one that belongs at its place
    off = Math.max(off, Math.abs(place.at * cell + moved));
    for (let i = place.first; i <= place.last; i++) assert.equal(kept.get(i), 100 + i);
    if (n > 1 && (Math.floor(place.at) < place.first || Math.ceil(place.at + rows) - 1 > place.last)) bare++;
  }
  assert.ok(off < 1, 'in the history the picture is under the finger, within a pixel: ' + off);
  assert.equal(bare, 0, 'with a row to show at either edge');
  for (let n = 51; n < 120; n++) { local.drawn(); local.frame(n * 16, true); }
  assert.equal(place, null, 'and settles on a whole row, which the terminal then shows itself');
  const plain = new TerminalSlide({ cell: () => cell, count: () => rows, viewport: () => 0, read: () => [], draw: () => false,
    keep() { assert.fail('there is no picture to copy'); }, show(at) { assert.equal(at, null); }, nudge() { assert.fail('nothing is asked of a terminal that is not slid'); } });
  plain.begin('local', 0); plain.moved(30);
  assert.equal(plain.active, false, 'a terminal that has no picture to copy is not slid: it scrolls by the row');
  assert.equal(plain.frame(16, false), false);
  // A program that writes its offer of the way back over a row (a hole): that row and what is under it stand still,
  // and the picture is shown a row lower, over it, with a finger on the screen and without.
  {
    let at = 20, seen = null, draws = 0, kept = [];
    const holed = new TerminalSlide({ cell: () => cell, count: () => rows, viewport: () => 0, read: () => [], draw() { draws++; },
      keep(row, place, count) { kept = [row, place, count]; return true; }, show(place, t, b, first, last) { seen = place === null ? null : { place, top: t, bottom: b, first, last }; },
      nudge() {}, hole: () => at });
    holed.pin(0);
    assert.ok(holed.active && holed.idle && draws === 1, 'with a hole and no finger the terminal is asked to draw');
    holed.drawn();
    assert.deepEqual(kept, [0, 0, 20], 'the rows above the hole are kept, the hole and what is under it are not');
    assert.deepEqual(seen, { place: -1, top: 0, bottom: rows - 20 - 1, first: 0, last: 19 }, 'and are shown a row lower, over the hole: ' + JSON.stringify(seen));
    holed.pin(5); assert.equal(draws, 1, 'once');
    // a finger scrolls: nothing changes in where the picture stands; when it has come to rest the picture stays
    holed.begin('remote', 10); assert.equal(holed.idle, false);
    assert.equal(holed.frame(26, false), true);
    assert.equal(seen.place, -1); assert.equal(seen.bottom, rows - 21);
    assert.equal(holed.frame(42, true), false, 'at rest no frame is needed');
    assert.ok(holed.active && holed.idle && seen && seen.place === -1, 'and the picture is still shown over the hole');
    // the hole goes (the program is back at the end): the terminal is left as it draws itself
    at = -1; holed.drawn();
    assert.ok(!holed.active && seen === null, 'without a hole and without a finger there is nothing to show the picture for');
    // a hole that comes under a finger: the picture goes down the row as it moves, not at a jump
    holed.begin('remote', 100); holed.drawn(); holed.frame(116, false);
    assert.deepEqual([seen.place, seen.bottom], [0, 0]);
    at = 20; holed.drawn();
    assert.ok(Math.abs(seen.place) < .01 && seen.bottom === rows - 21, 'the box reaches over the hole at once, the picture is where it was: ' + seen.place);
    for (let n = 1; n <= 40; n++) holed.frame(116 + n * 16, false);
    assert.equal(seen.place, -1, 'and comes to stand a row lower');
  }
  // The picture can be lost while a finger scrolls (a phone's graphics card takes back what was on it): the
  // terminal scrolls by the row from there, and the rows it was kept ahead by are asked back.
  let ahead = 0, there = true;
  const lost = new TerminalSlide({ cell: () => cell, count: () => rows, viewport: () => viewport, read: () => [], draw() {},
    keep: () => there, show() {}, nudge(count) { ahead += count; } });
  lost.begin('local', 0); lost.drawn(); lost.moved(30);
  assert.equal(ahead, 1, 'the terminal is kept a row ahead');
  there = false; lost.drawn();
  assert.equal(lost.active, false); assert.equal(ahead, 0, 'and is asked back that row when there is no picture any more');
  assert.equal(100 - viewport, Math.trunc(moved / cell), 'the terminal is at the row the finger left it in, not ahead of it: ' + (100 - viewport));
}
console.log('Terminal scroll behavior checks passed');
