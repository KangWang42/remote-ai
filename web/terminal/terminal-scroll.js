/* Touch scrolling is paced by display frames; network and terminal rendering stay independent. */
(function (root) {
  'use strict';
  // One glide for every kind of terminal. The speed at lift-off fades with this time constant (ms): a flick of two
  // pixels a millisecond carries on for about forty rows and has come to rest within a second and a half.
  const GLIDE = 325;
  // Claude Code moves three lines for a wheel step on its own, four for two steps written together and six for four.
  // Steps that follow one another within about 80 ms move further each time (3, 7, 10, 13 ...), so they are sent
  // SLOT ms apart, where every step moves what it says here. Measured on Claude Code 2.1.
  const SLOT = 110, MOVES = [[6, 4], [4, 2], [3, 1]], OWED = 45;
  // Scrolled away from the end of its conversation, Claude Code writes these words over the middle of the last row
  // it scrolls: its own way back to the end, which is Ctrl+End (END, as a terminal sends it).
  const OFFER = 'Jump to bottom (ctrl+End)', END = '\x1b[1;5F';
  // A terminal moves by whole rows, and a program that scrolls its own view moves it a few rows at a time, a moment
  // after it was asked. What is seen is to move with the finger by the pixel all the same, and whole: with rows
  // coming into sight at one edge as they leave at the other. So while a finger scrolls, what is seen is shown from a
  // strip of the rows the terminal has shown (TerminalSlide says where it stands; TerminalStrip, in
  // terminal-strip.js, holds the pictures of the rows and moves them):
  // - the rows are kept on the strip as the terminal draws them, each under the place it has in what is scrolled
  //   through, and the strip is shown at the place the finger is at;
  // - the terminal is kept ahead of the finger the way it moves, so that the rows about to come into sight are on
  //   the strip before they are needed: by the rows the finger covers in the time the program takes to move (both
  //   are measured as it goes) and in SPARE ms more, for a network that stops for a moment, within LEAD [fewest,
  //   most]; while the finger goes on one way it only grows. It is, once the finger has gone SURE of
  //   a row that way: a finger that only trembles does not send a program back and forth. Those leaving at the
  //   other edge are on the strip because they were seen: KEPT rows are held beyond what the terminal shows;
  // - where the strip has no row (the finger outran the lead, or the program is at the end of its list) there is
  //   nothing to draw. BLANK is how many such rows may show, as [rows that follow the finger, rows never passed];
  //   between the two the picture gives way more and more, as a band does. `going` is for the way the program last
  //   moved, `stuck` for one it did not move when last asked, `unsure` for the rest.
  // WAIT (ms): how long the program may be silent before a move that was asked for is given up, or four times what
  // its moves take where that is longer; while moves arrive, the later ones are only on their way. SETTLE (ms): how
  // fast the picture comes to rest.
  // Moves that arrive after the picture had to stop are not jumped to: it comes on as fast as the finger moves, and
  // by itself with EASE (ms), for at most LATE rows.
  const LEAD = { local: [1, 1], remote: [4, 12] }, SPARE = 250, SURE = { local: .1, remote: .3 }, KEPT = 16, BLANK = { local: [1, 1], going: [1, 4], unsure: [1, 3], stuck: [.5, 1.5] };
  const WAIT = 700, SETTLE = 90, EASE = 70, LATE = 8;
  class TerminalSlide {
    // options: cell() the height of a row; count() the rows of the terminal; viewport() where the terminal's own
    // history is scrolled to; read() the text of every row on the screen; draw() asks the terminal to draw, and
    // says false when it has no picture that can be copied; keep(row, place, rows) copies rows of the picture onto
    // the strip, and is only called as the terminal has drawn; show(place, top, bottom, first, last) shows the rows
    // between the still ones from the strip, with the terminal's first row standing for `place` (which may lie
    // between two rows), the strip holding the places `first` to `last`; show(null) leaves the terminal as it
    // draws itself; nudge(rows) asks the terminal for rows the finger did not move.
    constructor(options) {
      this.options = options;
      this.top = 0; this.bottom = 0; this.went = 0; this.stuck = 0;
      this.active = false; this.kind = 'local'; this.rows = null; this.seen = false; this.at = 0; this.madeAt = 0;
      this.clear();
    }
    clear() {
      this.offset = 0; this.drain = 0; this.ease = 0; this.asked = [];
      this.base = 0; this.from = 0; this.to = -1;         // the place the terminal's first row has, and the places on the strip
      this.led = 0; this.nudged = 0; this.nudgedAt = 0; this.run = 0;
      this.gone = 0; this.speed = 0; this.lag = 150;      // the finger's way in this frame, its speed (rows a ms), and the ms a move takes
      this.back = false;                                  // the terminal was asked back the rows it was ahead by
    }
    begin(kind, at) {
      const fresh = !this.active || kind !== this.kind;
      if (fresh) this.clear();
      this.kind = kind; this.active = true; this.seen = false; this.at = at; this.drain = 0;
      this.viewport = this.options.viewport();
      this.rows = kind === 'local' ? null : this.options.read();
      // The terminal's picture can be copied only as it is drawn: it is asked to draw, and its rows go onto the
      // strip then (drawn). Without a picture there is nothing to show: it scrolls by the row, as it does by itself.
      if (fresh && this.options.draw() === false) this.active = false;
    }
    cancel() {
      const shown = this.active;
      this.active = false; this.clear();
      if (shown) this.options.show(null);
    }
    forget() { this.top = 0; this.bottom = 0; this.went = 0; this.stuck = 0; }          // another size or another screen: what stood still is not known
    // The rows that stand still at the top and at the bottom; the terminal's own history has none.
    edges() { return this.kind === 'local' ? [0, 0] : [this.top, this.bottom]; }
    // The rows the terminal shows now go onto the strip, each under its place.
    keep() {
      const [top, bottom] = this.edges(), from = this.base + top, to = this.base + this.options.count() - bottom - 1;
      if (to < from) return true;
      if (this.options.keep(top, from, to - from + 1) === false) return false;
      if (this.to < this.from || from > this.to + 1 || to < this.from - 1) { this.from = from; this.to = to; }        // not beside what was held
      else { this.from = Math.max(Math.min(this.from, from), from - KEPT); this.to = Math.min(Math.max(this.to, to), to + KEPT); }
      return true;
    }
    nudge(rows, at) { this.nudged += rows; this.led += rows; this.nudgedAt = at; this.options.nudge(rows); }
    moved(pixels) {
      if (!this.active || !pixels) return;
      const before = this.within();
      this.offset += pixels;
      // What the band did not let through of the finger's move brings on a picture that is behind.
      const kept = pixels - (this.within() - before);
      if (this.ease * kept < 0) this.ease += Math.sign(kept) * Math.min(Math.abs(kept), Math.abs(this.ease));
      // The terminal is kept ahead of the finger the way it has surely gone; not where the program will not go.
      const way = Math.sign(pixels), kind = this.kind === 'local' ? 'local' : 'remote', [fewest, most] = LEAD[kind];
      this.back = false;
      this.gone += Math.abs(pixels);
      this.run = way === Math.sign(this.run) ? this.run + pixels : pixels;
      if (Math.abs(this.run) < SURE[kind] * this.options.cell()) return;
      // While the finger goes on one way the lead only grows, by two rows or more at a time: rows asked back and
      // forth would be told apart badly from the finger's own. It is taken back when the finger turns, or rests.
      const rows = way === this.stuck ? 0 : Math.max(fewest, Math.min(most, Math.ceil(this.speed * (this.lag + SPARE))));
      const ahead = Math.sign(this.led) === way ? Math.abs(this.led) : -1;
      if (ahead < 0 ? rows * way !== this.led : rows >= ahead + 2) this.nudge(rows * way - this.led, this.at);
    }
    // The terminal was asked for rows. `free` of them are not the finger's: they keep the terminal ahead.
    ask(rows, at) {
      if (!this.active || !rows) return;
      const free = Math.sign(rows) === Math.sign(this.nudged) ? Math.sign(rows) * Math.min(Math.abs(rows), Math.abs(this.nudged)) : 0;
      this.nudged -= free;
      this.asked.push({ rows, at, free });
    }
    // By how many rows what is on the screen has moved between two pictures of it (down is positive), and how many
    // rows at its top and bottom did not move: a program keeps its heading and its message box where they are.
    shift(before, after) {
      const n = Math.min(before.length, after.length);
      let changed = 0;
      for (let i = 0; i < n; i++) if (before[i] !== after[i]) changed++;
      if (changed < 2) return 0;
      const count = k => { let same = 0; for (let i = Math.max(0, -k); i < n - Math.max(0, k); i++) if (before[i] === after[i + k] && before[i].trim()) same++; return same; };
      // seldom over twelve rows at once; after a wait, as many as were asked for
      const far = Math.min(n - 3, Math.max(12, this.asked.reduce((sum, ask) => sum + Math.abs(ask.rows), 0)));
      let best = 0, most = 0;
      for (let k = 1; k <= far; k++) for (const way of [k, -k]) { const same = count(way); if (same > most) { best = way; most = same; } }
      if (most < 2 || most < (changed - Math.abs(best)) / 2) return 0;
      // A row at the edge is one of those that move when it reads differently and is seen to have come from, or to
      // have gone to, the row that many further on. One that changed where it stands (a line that says how long the
      // program has been working) stands still, like those that did not change.
      const there = i => i >= 0 && i < n;
      const moved = i => before[i] !== after[i] && ((there(i - best) && after[i] === before[i - best]) || (there(i + best) && before[i] === after[i + best]));
      let top = 0, bottom = 0;
      while (top < n && !moved(top)) top++;
      while (bottom < n - top && !moved(n - 1 - bottom)) bottom++;
      // Rows that happen to read the same may be taken for still ones; over a drag the fewest seen are the true ones.
      const was = this.edges();
      this.top = this.seen ? Math.min(this.top, top) : top; this.bottom = this.seen ? Math.min(this.bottom, bottom) : bottom;
      this.seen = true;
      // More rows stand still than was thought: they were kept as if they moved, at the places next to the moving
      // ones. Those places are not held any more; the rows that do move stay on the strip.
      if (this.top > was[0] || this.bottom > was[1]) { this.from = Math.max(this.from, this.base + this.top); this.to = Math.min(this.to, this.base + n - this.bottom - 1); }
      return best;
    }
    // The terminal has drawn. It stands at another place by the rows it moved; those of them that were asked for no
    // longer have to be made up for; and what it shows now goes onto the strip.
    drawn() {
      if (!this.active) return;
      let shift = 0;
      if (this.kind === 'local') { const now = this.options.viewport(); shift = this.viewport - now; this.viewport = now; }
      else { const now = this.options.read(); if (this.asked.length) shift = this.shift(this.rows, now); this.rows = now; }
      const way = Math.sign(shift), cell = this.options.cell(), before = this.within();
      let left = Math.abs(shift), made = 0;
      while (left > 0 && this.asked.length && Math.sign(this.asked[0].rows) === way) {
        const first = this.asked[0], taken = Math.min(left, Math.abs(first.rows));
        this.lag += (Math.max(20, Math.min(1500, this.at - first.at)) - this.lag) * .3;
        first.rows -= way * taken; first.free -= way * Math.min(taken, Math.abs(first.free));
        left -= taken; made += taken; this.offset -= way * taken * cell;
        if (!first.rows) this.asked.shift();
      }
      this.base -= shift;
      // No picture to copy any more (the strip was lost): the terminal scrolls by the row from here on, and is asked
      // back the rows it was kept ahead of the finger by.
      if (!this.keep()) { const ahead = this.led; this.cancel(); if (ahead) this.options.nudge(-ahead); return; }
      if (made) { this.went = way; this.stuck = 0; this.madeAt = this.at; }
      // What is seen stays where it was: a move that changes where the picture may be is come on to, not jumped to.
      if (shift) { const most = LATE * cell; this.ease = Math.max(-most, Math.min(most, this.ease + before - shift * cell - this.within())); }
      this.show();
    }
    // Once a frame. `resting`: no finger on the screen and no glide. Returns whether another frame is needed.
    frame(at, resting) {
      if (!this.active) return false;
      const dt = Math.max(0, Math.min(50, at - this.at)), cell = this.options.cell();
      this.at = at;
      if (dt) { this.speed += (this.gone / cell / dt - this.speed) * .2; this.gone = 0; }
      // What was asked for long ago, with nothing having moved since, is not coming (the program is at the end of
      // its list). The terminal is not ahead by the rows that were to keep it ahead; and the finger's own are first
      // made up for by those it still is ahead by: the picture gives up only what is left of them.
      while (this.asked.length && at - Math.max(this.asked[0].at, this.madeAt) > Math.max(WAIT, 4 * this.lag)) {
        const lost = this.asked.shift(), own = lost.rows - lost.free;
        this.led -= lost.free;
        const spare = Math.sign(this.led) === Math.sign(own) ? Math.sign(own) * Math.min(Math.abs(own), Math.abs(this.led)) : 0;
        this.led -= spare; this.drain += (own - spare) * cell;
        // the finger's own rows say that the program will not go that way; rows asked to get ahead, or back, do not
        if (own) { this.stuck = Math.sign(own); if (this.went === this.stuck) this.went = 0; }
      }
      // Rows that were to be asked for and never were: the request was dropped, or the terminal cannot go further.
      if (this.nudged && at - this.nudgedAt > WAIT) { this.led -= this.nudged; this.nudged = 0; }
      // At rest the terminal is to show what is seen: it is asked back the rows it was kept ahead by, once, when
      // what it was asked before is settled. Rows it will not come back are made up for by the picture, below.
      if (resting && this.led && !this.nudged && !this.asked.length) {
        if (this.back || Math.sign(-this.led) === this.stuck) this.led = 0;
        else { this.back = true; this.nudge(-this.led, at); this.show(); return true; }
      }
      const settled = resting && !this.asked.length && !this.nudged;
      if (settled) this.drain = this.offset;
      if (this.ease) { this.ease *= Math.exp(-dt / EASE); if (Math.abs(this.ease) < .5) this.ease = 0; }
      if (this.drain) {
        const part = this.drain * (1 - Math.exp(-dt / SETTLE)), gone = Math.abs(this.drain - part) < .5 ? this.drain : part;
        this.offset -= gone; this.drain -= gone;
      }
      if (settled && Math.abs(this.offset) < .5 && !this.ease) { this.cancel(); return false; }
      this.show();
      return true;
    }
    // What the finger is ahead of the terminal by, held to what there is to show that way: the rows the strip has
    // beyond the terminal's, and no more rows of nothing than may show.
    within() {
      const way = Math.sign(this.offset);
      if (!way) return 0;
      const cell = this.options.cell(), [top, bottom] = this.edges();
      const held = this.to < this.from ? 0 : way > 0 ? this.base + top - this.from : this.to - (this.base + this.options.count() - bottom - 1);
      const known = Math.max(0, held), beyond = Math.abs(this.offset) / cell - known;
      if (beyond <= 0) return this.offset;
      const [free, most] = BLANK[this.kind === 'local' ? 'local' : way === this.stuck ? 'stuck' : way === this.went ? 'going' : 'unsure'], give = most - free;
      return way * cell * (known + (beyond <= free || !give ? Math.min(beyond, free) : free + give * (1 - Math.exp((free - beyond) / give))));
    }
    show() {
      if (this.to < this.from) { this.options.show(null); return; }      // no row is held yet: the terminal has not drawn
      const [top, bottom] = this.edges();
      this.options.show(this.base - (this.within() + this.ease) / this.options.cell(), top, bottom, this.from, this.to);
    }
  }
  TerminalSlide.KEPT = KEPT;
  class TerminalScroller {
    constructor(options) {
      this.options = options;
      this.frame = 0; this.dragging = false; this.velocity = 0; this.pending = 0; this.more = 0;
      this.tick = this.tick.bind(this);
    }
    stop() {
      if (this.frame) this.options.cancel(this.frame);
      this.frame = 0; this.dragging = false; this.velocity = 0; this.pending = 0; this.more = 0;
    }
    start(y, at) {
      this.stop(); this.dragging = true; this.y = y; this.lastMove = at; this.began = false;
      this.lastFrame = at; this.distance = 0; this.kind = this.options.mode();
    }
    move(y, at) {
      if (!this.dragging) return;
      // A browser tells of a finger's move only once it has left the spot it touched, and then of all the way it
      // has gone at once (some ten pixels). That is where scrolling begins, as it does in a list of the phone's
      // own: nothing has to be caught up with in one frame.
      if (!this.began) { this.began = true; this.y = y; this.lastMove = at; return; }
      const delta = y - this.y, dt = Math.max(8, Math.min(64, at - this.lastMove));
      if (delta && this.velocity && Math.sign(delta) !== Math.sign(this.velocity)) {
        this.pending = 0; this.velocity = 0;
      }
      this.velocity = this.velocity * .3 + delta / dt * .7;
      this.pending += delta; this.distance += Math.abs(delta);
      if (this.options.slide) this.options.slide(delta);
      this.y = y; this.lastMove = at;
      this.schedule();
    }
    end(at) {
      if (!this.dragging) return;
      this.dragging = false; this.released = at; this.lastFrame = at;
      const recent = at - this.lastMove < 80;
      if (!recent || this.distance < 12 || this.options.reduced()) this.velocity = 0;
      this.velocity = Math.max(-2.5, Math.min(2.5, this.velocity));
      this.schedule();
    }
    // Whole rows asked of the terminal that the finger did not move. They are sent with the finger's, and are
    // counted apart from them: the part of a row the finger has gone is not rounded with them.
    nudge(rows) { this.more += rows; this.schedule(); }
    schedule() { if (!this.frame) this.frame = this.options.request(this.tick); }
    tick(at) {
      this.frame = 0;
      if (this.options.mode() !== this.kind || !this.options.visible()) { this.stop(); return; }
      const remote = this.kind !== 'local', dt = Math.max(0, Math.min(32, at - this.lastFrame));
      this.lastFrame = at;
      if (!this.dragging && this.velocity) {
        const decay = Math.exp(-dt / GLIDE), glide = this.velocity * GLIDE * (1 - decay);
        this.pending += glide;
        if (this.options.slide) this.options.slide(glide);
        this.velocity *= decay;
        if (at - this.released > 2500 || Math.abs(this.velocity) < .03) this.velocity = 0;
      }
      const cell = Math.max(8, this.options.lineHeight());
      let lines = Math.trunc(this.pending / cell), more = this.more;
      if (remote) { lines = Math.max(-12, Math.min(12, lines)); more = Math.max(-12 - lines, Math.min(12 - lines, more)); }
      // A program that is sent fewer wheel steps than rows may still be owed one when the finger has stopped.
      const owing = () => !!this.options.owing && this.options.owing();
      if (lines || more || owing()) {
        this.pending -= lines * cell; this.more -= more;
        if (this.options.scroll(lines + more, this.kind) === false) { this.stop(); return; }
      }
      if ((!this.dragging && this.velocity) || Math.abs(this.pending) >= cell || this.more || owing()) this.schedule();
    }
  }
  class TerminalScrollRouter {
    constructor(term, send, tool, clock) {
      this.term = term; this.send = send; this.tool = tool;
      this.clock = clock || (() => performance.now());
      this.remoteUp = 0; this.pageLines = 0; this.sgr = false;
      this.owed = 0; this.way = 0; this.wheelAt = -1e9;
      this.offers = false;    // the program has offered its own way back to the end: it says itself when it is away from it
      this.asked = 0;         // the rows the last call of scroll() asked the terminal or the program to move by
      // Mouse tracking and mouse encoding are independent terminal modes.
      for (const [final, enabled] of [['h', true], ['l', false]]) {
        term.parser.registerCsiHandler({ prefix: '?', final }, params => {
          if (params.includes(1006)) this.sgr = enabled;
          return false;
        });
      }
    }
    mode() {
      const alternate = this.term.buffer.active.type === 'alternate';
      // Codex's inline view writes into the terminal's own history, which is scrolled here whatever it reports.
      if (this.tool() === 'codex' && !alternate) return 'local';
      // A fullscreen program that listens to the mouse scrolls by the line for each wheel step. Page keys move a
      // screenful at a time, and are only for a program whose mouse mode never reached this terminal.
      return this.term.modes.mouseTrackingMode !== 'none' ? 'mouse' : alternate ? 'page' : 'local';
    }
    reset() { this.remoteUp = 0; this.pageLines = 0; this.owed = 0; }
    start() { this.pageLines = 0; this.owed = 0; }
    // The wheel steps that make Claude Code follow the finger by `lines` more rows: the largest move that does not
    // run more than a row and a half ahead of the finger. What the finger is ahead by is kept, up to OWED rows,
    // and paid off in the slots that follow.
    paced(lines) {
      const now = this.clock(), way = Math.sign(lines) || this.way || 0;
      if (way !== this.way) { this.owed = 0; this.way = way; }       // nothing is owed in the direction that was left
      this.owed = Math.max(-OWED, Math.min(OWED, this.owed + lines));
      const move = now - this.wheelAt >= SLOT && MOVES.find(([rows]) => this.owed * way >= rows - 1.5);
      if (!way || !move) return { steps: 0, moved: 0 };
      this.owed -= way * move[0]; this.wheelAt = now;
      return { steps: way * move[1], moved: way * move[0] };
    }
    owing() { return this.tool() === 'claude' && this.owed * (this.way || 0) >= 1.5 && this.mode() === 'mouse'; }
    // The row on which the program itself offers the way back to the end, or -1: the offer's words, in the middle
    // of a row of the lower part of the screen. The same words anywhere else are somebody's text.
    offered() {
      if (this.mode() !== 'mouse') return -1;
      const buffer = this.term.buffer.active, rows = this.term.rows;
      for (let y = rows - 1; y >= Math.floor(rows / 3); y--) {
        const line = buffer.getLine(buffer.viewportY + y), at = line ? line.translateToString(true).indexOf(OFFER) : -1;
        if (at >= 0 && Math.abs(at + OFFER.length / 2 - this.term.cols / 2) <= 3) { this.offers = true; return y; }
      }
      return -1;
    }
    wheel(lines) {
      const column = Math.max(1, Math.floor(this.term.cols / 2)), row = Math.max(1, Math.floor(this.term.rows / 2));
      const button = lines > 0 ? 64 : 65;
      // Codex reads SGR reports only: the older encoding would arrive in its message box as typed characters.
      const key = this.sgr || this.tool() === 'codex' ? `\x1b[<${button};${column};${row}M`
        : '\x1b[M' + String.fromCharCode(button + 32, Math.min(223, column) + 32, Math.min(223, row) + 32);
      return key.repeat(Math.abs(lines));
    }
    scroll(lines, mode = this.mode()) {
      this.asked = 0;
      if (mode === 'local') {
        const before = this.term.buffer.active.viewportY;
        this.term.scrollLines(-lines);
        this.asked = before - this.term.buffer.active.viewportY;
        return this.asked !== 0;
      }
      if (mode === 'page') {
        this.pageLines += lines;
        const pages = Math.trunc(this.pageLines / 8);
        if (!pages) return true;
        this.pageLines -= pages * 8;
        if (!this.send((pages > 0 ? '\x1b[5~' : '\x1b[6~').repeat(Math.abs(pages)))) return false;
        this.remoteUp = Math.max(0, this.remoteUp + pages * 8);
        this.asked = pages * 8;
        return true;
      }
      const sent = this.tool() === 'claude' ? this.paced(lines) : { steps: lines, moved: lines };
      if (!sent.steps) return true;
      if (!this.send(this.wheel(sent.steps))) return false;
      this.remoteUp = Math.max(0, this.remoteUp + sent.moved);
      this.asked = sent.moved;
      return true;
    }
    latest() {
      const mode = this.mode();
      // a program that offers the way back itself is taken up on it: it leads to the end from anywhere. Another is
      // scrolled back as far as it was scrolled.
      if (this.offered() >= 0) { if (!this.send(END)) return false; }
      else if (this.remoteUp > 0 && mode !== 'local') {
        const keys = mode === 'page' ? '\x1b[6~'.repeat(Math.min(80, Math.ceil(this.remoteUp / 8) + 2))
          : this.wheel(-Math.min(600, this.remoteUp + 12));
        if (!this.send(keys)) return false;
      }
      this.reset(); this.term.scrollToBottom();
      return true;
    }
  }
  if (typeof module === 'object' && module.exports) {
    module.exports = TerminalScroller;
    module.exports.TerminalScrollRouter = TerminalScrollRouter;
    module.exports.TerminalSlide = TerminalSlide;
  } else { root.TerminalScroller = TerminalScroller; root.TerminalScrollRouter = TerminalScrollRouter; root.TerminalSlide = TerminalSlide; }
})(typeof window === 'undefined' ? globalThis : window);
