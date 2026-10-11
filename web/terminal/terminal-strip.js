/* The rows a terminal has shown, painted on tiles that the browser moves without painting them again. */
(function (root) {
  'use strict';
  // While a finger scrolls, what is seen moves in every frame, and a frame must cost next to nothing: a screenful
  // painted in every frame is what makes scrolling stutter on a phone. So the picture is not painted to be moved.
  // The rows lie on TILES canvases that follow one another on a track, each row under the place it has in what is
  // scrolled through, and the track is moved by a transform: the browser carries that out where it puts the page
  // together, without painting and without laying anything out. Rows are painted only when the terminal has drawn
  // (they are copied from its picture, once), and a tile none of whose rows is wanted any more is used again
  // further on. It is how a browser scrolls a page, and a map its tiles.
  // Two tiles hold every row that is kept; a third is there because the rows seldom begin where a tile does. No
  // tile is larger than a graphics card takes in one piece, however many rows are kept.
  // Everything is counted in the pixels of the terminal's picture, which are the screen's: a row is a whole number
  // of them high, the tiles lie on whole ones and are moved by whole ones, and are shown pixel for pixel. The text
  // is as sharp on the move as the terminal draws it.
  const TILES = 3;
  class TerminalStrip {
    // box: the element laid over the rows that move, cutting off what lies outside them. It stands where the
    // terminal's picture does (in the same element), so nothing is measured on the page. kept: how many rows are
    // held beyond those the terminal shows, either way. background(): the colour of a row with nothing on it.
    constructor(box, kept, background) {
      this.box = box; this.kept = kept; this.background = background;
      this.track = document.createElement('div');
      box.appendChild(this.track);
      this.tiles = [];
      this.shown = false; this.place = null;
      this.forget();
    }
    // Another size of terminal, or of its picture: the tiles are cut again when rows are next kept.
    forget() { this.cut = ''; this.top = -1; this.bottom = -1; this.moved = NaN; this.free(); }
    free() { for (const tile of this.tiles) tile.index = null; this.first = 0; this.last = -1; }
    fit(source, total) {
      const cut = [source.width, source.height, source.style.height, total, devicePixelRatio].join(' ');
      if (cut === this.cut) return;
      this.forget();
      this.cut = cut; this.total = total;
      // A row of the picture is a whole number of pixels high. The picture itself may be a pixel higher or lower
      // than its rows, where the page's measure does not come to whole pixels of the screen: the rows are drawn
      // from its lower edge, so the pixel too many, or too few, is at the top.
      this.high = Math.max(1, Math.round(source.height / total));
      this.above = source.height - total * this.high;
      // one of the picture's pixels, in the page's measure: one of the screen's, unless the picture is shown stretched
      const shown = parseFloat(source.style.height) || source.height / devicePixelRatio;
      this.unit = Math.abs(shown * devicePixelRatio - source.height) < 2 ? 1 / devicePixelRatio : shown / source.height;
      this.per = Math.ceil((total + 2 * this.kept) / (TILES - 1));                // the rows of a tile
      this.box.style.width = source.width * this.unit + 'px';
      while (this.tiles.length < TILES) {
        const canvas = document.createElement('canvas');
        this.track.appendChild(canvas);
        // nothing shows through a tile: it is put on the page without being mixed with what lies under it
        this.tiles.push({ canvas, draw: canvas.getContext('2d', { alpha: false }), index: null, from: 0, to: -1 });
      }
      for (const tile of this.tiles) {
        tile.canvas.width = source.width; tile.canvas.height = this.per * this.high;
        tile.canvas.style.width = source.width * this.unit + 'px'; tile.canvas.style.height = this.per * this.high * this.unit + 'px';
      }
    }
    // The tile for the places index * per and on: the one that holds them, or one that holds nothing, wiped.
    tile(index) {
      let tile = this.tiles.find(one => one.index === index);
      if (tile) return tile;
      tile = this.tiles.find(one => one.index === null);
      if (!tile) return null;
      tile.index = index; tile.from = 0; tile.to = -1;
      tile.draw.fillStyle = this.background();
      tile.draw.fillRect(0, 0, tile.canvas.width, tile.canvas.height);
      tile.canvas.style.transform = `translate3d(0,${index * this.per * this.high * this.unit}px,0)`;
      return tile;
    }
    wipe(tile, from, to) {
      tile.draw.fillStyle = this.background();
      tile.draw.fillRect(0, (from - tile.index * this.per) * this.high, tile.canvas.width, (to - from + 1) * this.high);
    }
    // `rows` rows of the terminal's picture `source`, from its row `row` on, are painted under the places `place`
    // and on. `total`: the rows of the whole picture. To be called as the terminal has drawn: a picture drawn on
    // the graphics card is there to be copied only until it has been shown.
    keep(source, total, row, place, rows) {
      this.fit(source, total);
      // rows further than `kept` from the terminal's are not held: a tile of nothing else is free
      const lowest = place - this.kept, highest = place + rows - 1 + this.kept;
      for (const tile of this.tiles) if (tile.index !== null && ((tile.index + 1) * this.per <= lowest || tile.index * this.per > highest)) tile.index = null;
      for (let at = place; at < place + rows;) {
        const index = Math.floor(at / this.per), run = Math.min(place + rows, (index + 1) * this.per) - at, tile = this.tile(index);
        if (tile) {
          tile.draw.drawImage(source, 0, this.above + (row + at - place) * this.high, source.width, run * this.high,
            0, (at - index * this.per) * this.high, source.width, run * this.high);
          if (tile.to < tile.from) { tile.from = at; tile.to = at + run - 1; }
          else { tile.from = Math.min(tile.from, at); tile.to = Math.max(tile.to, at + run - 1); }
        }
        at += run;
      }
      return true;
    }
    // Only the places `first` to `last` are held: a row painted outside them is no longer true, and is wiped.
    hold(first, last) {
      this.first = first; this.last = last;
      for (const tile of this.tiles) {
        if (tile.index === null) continue;
        if (tile.from <= tile.to && tile.from < first) { const to = Math.min(tile.to, first - 1); this.wipe(tile, tile.from, to); tile.from = to + 1; }
        if (tile.from <= tile.to && tile.to > last) { const from = Math.max(tile.from, last + 1); this.wipe(tile, from, tile.to); tile.to = from - 1; }
      }
    }
    // The rows between `top` still ones above and `bottom` below are shown from the tiles, with the terminal's
    // first row standing for `place` (which may lie between two rows). Nothing is painted here.
    show(place, top, bottom, first, last) {
      if (!this.cut) { this.hide(); return; }
      if (first !== this.first || last !== this.last) this.hold(first, last);
      if (top !== this.top || bottom !== this.bottom) {
        this.top = top; this.bottom = bottom;
        this.box.style.top = (this.above + top * this.high) * this.unit + 'px';
        this.box.style.height = Math.max(0, this.total - top - bottom) * this.high * this.unit + 'px';
        this.box.style.background = this.background();
      }
      const moved = Math.round((place + top) * this.high);
      if (moved !== this.moved) { this.moved = moved; this.track.style.transform = `translate3d(0,${-moved * this.unit}px,0)`; }
      this.place = place;
      if (!this.shown) { this.shown = true; this.box.hidden = false; }
    }
    // The terminal is seen as it draws itself, and no row is held.
    hide() {
      if (this.shown) { this.shown = false; this.box.hidden = true; }
      this.place = null; this.free();
    }
    // For the checks: how far between two rows the picture stands, in its pixels, and how many of the rows that are
    // shown have nothing on them.
    between() { return this.shown ? ((this.moved % this.high) + this.high) % this.high : 0; }
    blank() {
      if (!this.shown) return 0;
      const from = this.place + this.top, to = this.place + this.total - this.bottom;
      return Math.max(0, Math.max(this.first, Math.floor(from)) - from) + Math.max(0, to - 1 - Math.min(this.last, Math.ceil(to) - 1));
    }
  }
  root.TerminalStrip = TerminalStrip;
})(window);
