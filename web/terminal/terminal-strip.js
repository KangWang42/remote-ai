/* The rows a terminal has shown, kept as a picture on the graphics card and moved without being painted again. */
(function (root) {
  'use strict';
  // While a finger scrolls, what is seen moves in every frame, and a frame must cost next to nothing: so the picture
  // is not painted to be moved. The rows lie on one canvas, as many above and below the terminal's own as are kept,
  // and the canvas is moved by a transform: the browser carries that out where it puts the page together, without
  // painting and without laying anything out. It is how a browser scrolls a page.
  // The rows are copied only when the terminal has drawn, and they never leave the graphics card: the terminal's
  // picture is drawn there (WebGL), and so is this one. Its picture is taken over as a texture, the rows that move
  // go from it onto a ring of rows (a second texture, where each row has a slot by the place it has in what is
  // scrolled through), and the canvas is drawn from the ring. A 2D canvas would not do: a phone may paint those in
  // memory, and then every copy fetches the terminal's whole picture back from the card and sends it there again.
  // Everything is counted in the pixels of the terminal's picture, which are the screen's: a row is a whole number
  // of them high, and the canvas is moved by whole ones and shown pixel for pixel. The text is as sharp on the move
  // as the terminal draws it.
  // Phones differ. Where the browser has no such canvas, the graphics card takes its memory back, or has no room
  // for the picture, there is no strip, and the terminal scrolls by the row as it does by itself. And the strip
  // times its own work: what copying the rows costs the page each time the terminal draws, averaged. On a phone
  // where that is over SLOW ms (a browser that fetches the picture back from the card after all), the strip would
  // make scrolling stutter instead of smooth, and gives up for as long as the page is open.
  const SLOW = 5;
  const NONE = 0x7fffffff;                // in a slot of the ring: no row
  const CORNERS = `#version 300 es
in vec2 corner;
uniform vec4 to, from;
out vec2 at;
void main() { at = mix(from.xy, from.zw, corner); gl_Position = vec4(mix(to.xy, to.zw, corner), 0.0, 1.0); }`;
  const COPY = `#version 300 es
precision highp float;
uniform sampler2D rows;
in vec2 at;
out vec4 colour;
void main() { colour = texture(rows, at); }`;
  const mod = (n, by) => ((n % by) + by) % by;
  class TerminalStrip {
    // box: the element laid over the rows that move, cutting off what lies outside them. It stands where the
    // terminal's picture does (in the same element), so nothing is measured on the page. kept: how many rows are
    // held beyond those the terminal shows, either way. background(): the colour of a row with nothing on it.
    constructor(box, kept, background) {
      this.box = box; this.kept = kept; this.background = background;
      this.canvas = document.createElement('canvas');
      box.appendChild(this.canvas);
      this.gl = null; this.held = null; this.shown = false; this.place = null; this.colour = ''; this.rgb = [0, 0, 0];
      this.kept = kept; this.copies = 0; this.cost = 0; this.slow = false;
      // A graphics card may take its memory back. What was on it is gone: all of it is made again when rows are next kept.
      this.canvas.addEventListener('webglcontextlost', event => { event.preventDefault(); this.gl = null; this.forget(); });
      this.forget();
    }
    // Another size of terminal, or of its picture: the strip is cut again when rows are next kept.
    forget() { this.cut = ''; this.top = -1; this.bottom = -1; this.moved = NaN; this.fresh = false; this.first = 0; this.last = -1; if (this.held) this.held.fill(NONE); }
    open() {
      if (this.gl) return true;
      // Where nothing has been drawn the canvas shows what lies under it, the terminal itself: should a phone ever
      // show the strip before it is drawn, or lose what was on it, the rows are seen to jump, not hidden by a patch.
      const gl = this.canvas.getContext('webgl2', { alpha: true, antialias: false, depth: false, stencil: false });
      if (!gl || gl.isContextLost()) return false;
      const program = gl.createProgram();
      for (const [kind, text] of [[gl.VERTEX_SHADER, CORNERS], [gl.FRAGMENT_SHADER, COPY]]) {
        const shader = gl.createShader(kind);
        gl.shaderSource(shader, text); gl.compileShader(shader); gl.attachShader(program, shader);
      }
      gl.linkProgram(program);
      if (!gl.getProgramParameter(program, gl.LINK_STATUS)) return false;
      gl.useProgram(program);
      gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([0, 0, 1, 0, 0, 1, 1, 1]), gl.STATIC_DRAW);
      const corner = gl.getAttribLocation(program, 'corner');
      gl.enableVertexAttribArray(corner); gl.vertexAttribPointer(corner, 2, gl.FLOAT, false, 0, 0);
      // the terminal's picture is taken as it is: it has no see-through parts to be worked out again
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
      gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, gl.NONE);
      this.to = gl.getUniformLocation(program, 'to'); this.from = gl.getUniformLocation(program, 'from');
      this.picture = null; this.ring = null; this.frame = null;
      this.gl = gl;
      return true;
    }
    // A texture of that size, read pixel for pixel.
    texture(wide, tall) {
      const gl = this.gl, made = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, made);
      gl.texStorage2D(gl.TEXTURE_2D, 1, gl.RGBA8, wide, tall);
      for (const [name, value] of [[gl.TEXTURE_MIN_FILTER, gl.NEAREST], [gl.TEXTURE_MAG_FILTER, gl.NEAREST], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) gl.texParameteri(gl.TEXTURE_2D, name, value);
      return made;
    }
    // Says false when there is no strip to be had: the graphics card is not to be used, or takes no picture this size.
    fit(source, total) {
      if (this.slow) return false;
      const cut = [source.width, source.height, source.style.height, total, devicePixelRatio].join(' ');
      if (cut === this.cut) return true;
      if (!this.open() || !source.width || !source.height) return false;
      const gl = this.gl;
      this.forget();
      this.total = total;
      // A row of the picture is a whole number of pixels high. The picture itself may be a pixel higher or lower
      // than its rows, where the page's measure does not come to whole pixels of the screen: the rows are drawn
      // from its lower edge, so the pixel too many, or too few, is at the top.
      this.high = Math.max(1, Math.round(source.height / total));
      this.above = source.height - total * this.high;
      // one of the picture's pixels, in the page's measure: one of the screen's, unless the picture is shown stretched
      const shown = parseFloat(source.style.height) || source.height / devicePixelRatio;
      this.unit = Math.abs(shown * devicePixelRatio - source.height) < 2 ? 1 / devicePixelRatio : shown / source.height;
      // as many rows beyond the terminal's as are kept, or as the graphics card has room for in one picture
      const most = gl.getParameter(gl.MAX_TEXTURE_SIZE);
      this.spare = Math.max(0, Math.min(this.kept, Math.floor((most / this.high - total) / 2)));
      this.places = total + 2 * this.spare;
      this.wide = source.width; this.tall = this.places * this.high; this.source = source.height;
      this.canvas.width = this.wide; this.canvas.height = this.tall;
      if (gl.drawingBufferWidth !== this.wide || gl.drawingBufferHeight !== this.tall || this.wide > most) return false;
      this.canvas.style.width = this.wide * this.unit + 'px'; this.canvas.style.height = this.tall * this.unit + 'px';
      this.box.style.width = this.wide * this.unit + 'px';
      for (const old of [this.picture, this.ring]) if (old) gl.deleteTexture(old);
      if (this.frame) gl.deleteFramebuffer(this.frame);
      this.picture = this.texture(this.wide, source.height);
      this.ring = this.texture(this.wide, this.tall);
      this.frame = gl.createFramebuffer();
      gl.bindFramebuffer(gl.FRAMEBUFFER, this.frame);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, this.ring, 0);
      if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) return false;
      this.held = new Int32Array(this.places).fill(NONE);       // for every slot of the ring: the place of the row on it
      this.cut = cut;
      return true;
    }
    // Pixels `from` to `until` of the bound texture, `size` high, are drawn at `to` and on, `tall` being the height
    // drawn into. Both are counted from the top; the canvas has its top where a texture drawn into has its bottom.
    quad(to, tall, onto, from, until, size) {
      const gl = this.gl, end = to + until - from;
      if (onto) gl.uniform4f(this.to, -1, 1 - 2 * to / tall, 1, 1 - 2 * end / tall); else gl.uniform4f(this.to, -1, 2 * to / tall - 1, 1, 2 * end / tall - 1);
      gl.uniform4f(this.from, 0, from / size, 1, until / size);
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    }
    // `rows` rows of the terminal's picture `source`, from its row `row` on, are kept under the places `place` and
    // on. `total`: the rows of the whole picture. To be called as the terminal has drawn: its picture is there to
    // be taken only until it has been shown. Says false when nothing could be kept.
    keep(source, total, row, place, rows) {
      if (!this.fit(source, total)) return false;
      const gl = this.gl, high = this.high, began = performance.now();
      gl.bindTexture(gl.TEXTURE_2D, this.picture);
      gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, gl.RGBA, gl.UNSIGNED_BYTE, source);
      gl.bindFramebuffer(gl.FRAMEBUFFER, this.frame);
      gl.viewport(0, 0, this.wide, this.tall);
      for (let done = 0; done < rows;) {
        const slot = mod(place + done, this.places), run = Math.min(rows - done, this.places - slot);
        let from = this.above + (row + done) * high, to = slot * high;
        const until = from + run * high;
        if (from < 0) { to -= from; from = 0; }                 // the picture's first row lacks its top pixel
        this.quad(to, this.tall, false, from, until, this.source);
        for (let i = 0; i < run; i++) this.held[slot + i] = place + done + i;
        done += run;
      }
      this.base = place - row; this.fresh = true;
      // The first copies make room on the card and are not counted. After that the average says what a copy costs
      // here; one that took long for another reason counts for no more than twice SLOW, so only copies that stay
      // slow bring the average over it.
      if (++this.copies > 2) this.cost += (Math.min(performance.now() - began, 2 * SLOW) - this.cost) / Math.min(this.copies - 2, 8);
      if (this.copies > 10 && this.cost > SLOW) this.slow = true;
      return true;
    }
    // The canvas is drawn from the ring: the places held, around the terminal's own, each row under the one before.
    paint() {
      const gl = this.gl, high = this.high, origin = this.base - this.spare;
      const colour = this.background();
      if (colour !== this.colour) {
        this.colour = colour; this.box.style.background = colour;
        this.rgb = (getComputedStyle(this.box).backgroundColor.match(/[\d.]+/g) || [0, 0, 0]).slice(0, 3).map(part => Number(part) / 255);
      }
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      gl.viewport(0, 0, this.wide, this.tall);
      gl.clearColor(this.rgb[0], this.rgb[1], this.rgb[2], 1);
      gl.clear(gl.COLOR_BUFFER_BIT);
      gl.bindTexture(gl.TEXTURE_2D, this.ring);
      const last = Math.min(origin + this.places - 1, this.last);
      for (let place = Math.max(origin, this.first); place <= last;) {
        const slot = mod(place, this.places);
        let run = 0;
        while (place + run <= last && slot + run < this.places && this.held[slot + run] === place + run) run++;
        if (run) this.quad((place - origin) * high, this.tall, true, slot * high, (slot + run) * high, this.tall);
        place += run || 1;
      }
      this.origin = origin; this.fresh = false;
    }
    // The rows between `top` still ones above and `bottom` below are shown from the strip, with the terminal's
    // first row standing for `place` (which may lie between two rows); of the rows kept, only the places `first`
    // to `last` are true ones. Unless the terminal has drawn since, or other places are held, only the canvas is
    // moved here: nothing is drawn.
    show(place, top, bottom, first, last) {
      if (!this.cut || !this.gl) { this.hide(); return; }
      if (this.fresh || first !== this.first || last !== this.last) { this.first = first; this.last = last; this.paint(); }
      if (top !== this.top || bottom !== this.bottom) {
        this.top = top; this.bottom = bottom;
        this.box.style.top = (this.above + top * this.high) * this.unit + 'px';
        this.box.style.height = Math.max(0, this.total - top - bottom) * this.high * this.unit + 'px';
      }
      const moved = Math.round((place + top - this.origin) * this.high);
      if (moved !== this.moved) { this.moved = moved; this.canvas.style.transform = `translate3d(0,${-moved * this.unit}px,0)`; }
      this.place = place;
      if (!this.shown) { this.shown = true; this.box.hidden = false; }
    }
    // The terminal is seen as it draws itself, and no row is held.
    hide() {
      if (this.shown) { this.shown = false; this.box.hidden = true; }
      this.place = null; this.first = 0; this.last = -1;
      if (this.held) this.held.fill(NONE);
    }
    // For the checks: how far between two rows the picture stands, in its pixels, and how many of the rows that are
    // shown have nothing on them.
    between() { return this.shown ? mod(this.moved, this.high) : 0; }
    blank() {
      if (!this.shown) return 0;
      const from = this.place + this.top, to = this.place + this.total - this.bottom;
      return Math.max(0, Math.max(this.first, Math.floor(from)) - from) + Math.max(0, to - 1 - Math.min(this.last, Math.ceil(to) - 1));
    }
  }
  root.TerminalStrip = TerminalStrip;
})(window);
