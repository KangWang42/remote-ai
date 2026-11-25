/* The terminal screen. bridge.js connects it to the relay. */
(async () => {
  'use strict';
  const bridge = window.ProjectTerminal;
  if (new URLSearchParams(location.search).get('embed') === '1') document.documentElement.dataset.embed = '1';
  const $ = id => document.getElementById(id);
  const input = $('input'), status = $('status-text'), connection = $('connection'), connectionText = $('connection-text'), connectionDetail = $('connection-detail'), notice = $('notice'), palette = $('palette'), sendButton = $('send');
  const screen = $('screen'), holder = $('terminal'), dot = $('dot'), slash = $('slash'), latest = $('latest');
  const scrollTrack = $('scroll-track'), scrollThumb = $('scroll-thumb'), zoomHud = $('zoom-hud');
  const haptic = kind => { try { if (window.RemoteCliNative && window.RemoteCliNative.haptic) window.RemoteCliNative.haptic(kind || 'tick'); } catch (error) { /* ignore */ } };

  const SKINS = window.RemoteCliSkins;      // skins.js
  const resolveSkin = name => window.RemoteCliResolveSkin ? window.RemoteCliResolveSkin(name) : (SKINS[name] ? name : 'paper');
  let skin = bridge && (bridge.skin() === 'auto' || SKINS[bridge.skin()]) ? bridge.skin() : 'paper';
  let resolvedSkin = resolveSkin(skin);
  const FAMILY = '"Terminal Mono", "Noto Sans Mono CJK SC", "Droid Sans Mono", monospace';
  // The typeface must be ready before the terminal measures a character, or every cell gets the wrong width.
  try { await Promise.race([Promise.all([document.fonts.load('13px "Terminal Mono"'), document.fonts.load('bold 13px "Terminal Mono"')]), new Promise(done => setTimeout(done, 900))]); } catch (error) { /* system monospace */ }

  const sizes = [10, 11, 12, 13, 14, 15, 16, 17, 18];
  let noticeTimer = 0;
  // Room between the lines: closer shows more of the screen, wider is easier on the eyes.
  const SPACING = { tight: ['紧凑', 1.04], cozy: ['适中', 1.18], roomy: ['宽松', 1.32] };
  let spacing = bridge && bridge.spacing && SPACING[bridge.spacing()] ? bridge.spacing() : 'cozy';
  const CURSORS = { block: '方块', bar: '竖线', underline: '下划线' };
  let cursorStyle = bridge && bridge.cursor && CURSORS[bridge.cursor()] ? bridge.cursor() : 'block';
  let fontSize = Number(bridge && bridge.fontSize()) || 13;
  if (!sizes.includes(fontSize)) fontSize = 13;
  const term = new Terminal({ fontSize, fontFamily: FAMILY, cursorBlink: false, cursorStyle, cursorInactiveStyle: 'outline', lineHeight: SPACING[spacing][1],
    customGlyphs: true, rescaleOverlappingGlyphs: true,
    // No per-row accessibility tree: on a phone it is rebuilt on every refresh and makes typing stutter.
    scrollback: 5000, screenReaderMode: false, allowProposedApi: true, allowTransparency: resolvedSkin === 'pond', theme: SKINS[resolvedSkin].t, ...window.RemoteCliLook });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  if (window.Unicode11Addon) {
    try { term.loadAddon(new Unicode11Addon.Unicode11Addon()); term.unicode.activeVersion = '11'; } catch (error) { /* default unicode */ }
  }
  if (window.WebLinksAddon) {
    try {
      term.loadAddon(new WebLinksAddon.WebLinksAddon((event, uri) => {
        if (!/^https?:\/\//i.test(uri)) return;
        const app = window.RemoteCliNative;
        if (app && app.openUrl) app.openUrl(uri);
        else window.open(uri, '_blank', 'noopener,noreferrer');
      }));
    } catch (error) { /* plain text links */ }
  }
  term.open(holder);
  // Drawing on the graphics card keeps a busy screen smooth; a phone that cannot do it falls back to plain elements.
  // Android frequently evicts background WebGL contexts, so re-attach on return to keep GPU rendering & smooth scroll.
  let drawing = 'dom', webglAddon = null, webglLost = false, webglRetries = 0;
  let resetThemeScroll = null;
  function attachWebgl() {
    if (webglAddon || !window.WebglAddon || (bridge && bridge.renderer() === 'dom')) return false;
    try {
      const gl = new WebglAddon.WebglAddon();
      webglAddon = gl;
      gl.onContextLoss(() => {
        if (webglAddon === gl) webglAddon = null;
        try { gl.dispose(); } catch (error) { /* already lost */ }
        drawing = 'dom'; webglLost = true;
      });
      term.loadAddon(gl);
      drawing = 'webgl'; webglLost = false;
      return true;
    } catch (error) { webglAddon = null; drawing = 'dom'; return false; }
  }
  attachWebgl();
  function applySkin(name) {
    if (name !== 'auto' && !SKINS[name]) return;
    skin = name;
    resolvedSkin = resolveSkin(name);
    if (resetThemeScroll) resetThemeScroll();
    const s = window.RemoteCliPaint(name);
    term.options.allowTransparency = resolvedSkin === 'pond';
    // xterm halves this threshold for SGR dim text; keep that text readable on light surfaces too.
    term.options.minimumContrastRatio = s.light ? 9 : 4.5;
    term.options.theme = resolvedSkin === 'pond' ? { ...s.t, background: '#10191b66' } : s.t;
    if (bridge) bridge.chrome(s.t.background);
  }
  applySkin(skin);
  if (window.RemoteCliOnSystemTheme) window.RemoteCliOnSystemTheme(() => { if (skin === 'auto') applySkin('auto'); });

  // What the tools themselves offer after "/". Anything else can still be typed.
  const COMMANDS = {
    claude: [
      ['/resume', '打开历史对话列表，用 ↑ ↓ 选择后回车'], ['/model', '切换模型'], ['/compact', '压缩上下文，保留摘要继续'],
      ['/clear', '清空当前对话，重新开始'], ['/context', '查看上下文占用'], ['/usage', '查看套餐用量与限额'],
      ['/cost', '查看本次对话的用量'], ['/status', '查看版本、账号与连接状态'], ['/rewind', '回退到之前的某一步'],
      ['/permissions', '查看和调整工具权限'], ['/memory', '编辑记忆文件'], ['/review', '审查当前改动'],
      ['/agents', '管理子代理'], ['/mcp', '查看 MCP 服务'], ['/init', '为项目生成 CLAUDE.md'], ['/help', '全部命令与说明'],
      ['/exit', '退出 Claude Code（终端随之结束）']],
    codex: [
      ['/resume', '打开历史对话列表，用 ↑ ↓ 选择后回车'], ['/model', '切换模型与推理强度'], ['/new', '开始新对话'],
      ['/compact', '压缩上下文，保留摘要继续'], ['/status', '查看当前配置与用量'], ['/diff', '查看当前改动'],
      ['/review', '审查当前改动'], ['/mcp', '查看 MCP 服务'], ['/init', '为项目生成 AGENTS.md'],
      ['/quit', '退出 Codex（终端随之结束）']],
    shell: []
  };
  const KEYS = { escape: '\x1b', tab: '\t', backtab: '\x1b[Z', up: '\x1b[A', down: '\x1b[B', left: '\x1b[D', right: '\x1b[C', enter: '\r', interrupt: '\x03', ctrlp: '\x10',
    pageup: '\x1b[5~', pagedown: '\x1b[6~' };

  let running = false, restoring = true, initialized = false, lastSize = '', tool = 'claude', ended = false;
  let wide = 0, tall = 0, start = 0, paletteOpen = false, sizeTimer = 0;
  // A terminal can be open on the phone and in a window on the computer at once, and the two are seldom the same
  // size. The program is drawn for one size only: that of whoever typed last. `theirs` is the size the computer
  // reports; `sized` is when this page last asked for its own, so that a report still on its way is not taken for
  // someone else's wish.
  let theirs = '', sized = 0, elsewhere = false;
  // A fullscreen program is read from its last output only (`cut`), and draws no more than what changed: it is asked
  // once to draw the whole picture again, which it does when its screen changes size. `nudge` is the size asked for
  // in between, until the computer reports it.
  let cut = false, nudge = '', nudgeTimer = 0;

  function send(data) {
    if (!bridge) return false;
    if (!running) { notice.textContent = restoring && !ended ? '正在载入终端输出，稍后再发送' : '终端尚未连接或已经结束'; return false; }
    const mine = `${term.cols}x${term.rows}`;
    if (theirs && theirs !== mine && Date.now() - sized > 2000) {
      lastSize = mine; sized = Date.now();
      bridge.resize(JSON.stringify({ cols: term.cols, rows: term.rows }));
    }
    bridge.input(data);
    return true;
  }
  function resize() {
    const proposed = fit.proposeDimensions();
    if (!proposed || !proposed.cols || !proposed.rows) return;
    const cols = Math.max(20, Math.min(240, proposed.cols)), rows = Math.max(6, Math.min(100, proposed.rows));
    if (cols !== term.cols || rows !== term.rows) term.resize(cols, rows);
    const size = `${cols}x${rows}`;
    // The program on the computer is told once the phone's size has settled, and never while old output is replayed.
    clearTimeout(sizeTimer);
    if (running && size !== lastSize && bridge) sizeTimer = setTimeout(() => {
      if (!running || size === lastSize) return;
      lastSize = size; sized = Date.now();
      bridge.resize(JSON.stringify({ cols, rows }));
    }, 180);
  }
  function redraw() {
    cut = false;
    const cols = term.cols, rows = term.rows;
    // A size that differs from the computer's is sent anyway, and the program draws everything for it.
    if (!bridge || !running || term.buffer.active.type !== 'alternate' || rows <= 6 || theirs !== `${cols}x${rows}`) return;
    clearTimeout(sizeTimer);
    nudge = lastSize = `${cols}x${rows - 1}`; sized = Date.now();
    bridge.resize(JSON.stringify({ cols, rows: rows - 1 }));
    clearTimeout(nudgeTimer);
    nudgeTimer = setTimeout(settle, 3000);       // the size is put back even if the report never comes
  }
  function settle() {
    clearTimeout(nudgeTimer);
    if (!nudge) return;
    nudge = '';
    resize();
  }
  let scrollTrackHeight = 0, lastThumbH = -1, lastThumbY = -1, lastScrollShown = false;
  function layout(forceResize) {
    const w = screen.clientWidth, h = screen.clientHeight;
    if (!w || !h) return;
    scrollTrackHeight = Math.max(0, h - 16);
    const prevWide = wide, prevTall = tall;
    if (w !== wide) { wide = w; tall = h; } else if (h > tall) tall = h;
    if (tall !== prevTall) holder.style.height = tall + 'px';
    // Skip expensive fit.proposeDimensions() reflows on intermediate soft-keyboard animation frames.
    if ((!restoring || !initialized) && (forceResize || wide !== prevWide || tall !== prevTall || !lastSize)) resize();
  }
  new ResizeObserver(() => layout(false)).observe(screen);

  // The way back to the end is the button in the corner. A program that offers one of its own on its screen (Claude
  // Code writes "Jump to bottom (ctrl+End)" over the last row of its conversation) is not shown doing so: on a phone
  // it is words in another language cut into a line of text, for something the button already does.
  const offer = document.createElement('div');
  offer.id = 'offer'; offer.hidden = true;
  holder.querySelector('.xterm-screen').appendChild(offer);
  // That row is no row of the conversation any more: the rows above it are shown a row lower, over it (TerminalSlide,
  // `hole`), so that nothing stands between the conversation and the program's message box. Where there is no strip
  // to show them from, the row is covered instead.
  let offered = '', offeredKey = '', offeredRow = -1, hole = -1, pinAfter = 0, followFrame = 0, scrollFadeTimer = 0, lastScrollRatio = -1;
  function updateScrollBar(buffer, reveal) {
    if (!scrollTrack || !scrollThumb) return;
    if (buffer.type === 'alternate' || buffer.baseY <= 0) {
      if (lastScrollShown) { scrollTrack.classList.remove('shown'); lastScrollShown = false; }
      return;
    }
    const trackH = scrollTrackHeight || scrollTrack.clientHeight;
    if (trackH <= 0) return;
    const total = buffer.baseY + term.rows;
    const thumbH = Math.max(24, Math.round(trackH * term.rows / Math.max(1, total)));
    const ratio = Math.max(0, Math.min(1, buffer.viewportY / buffer.baseY));
    const y = Math.round((trackH - thumbH) * ratio);
    if (thumbH !== lastThumbH) { lastThumbH = thumbH; scrollThumb.style.height = thumbH + 'px'; }
    if (y !== lastThumbY) { lastThumbY = y; scrollThumb.style.transform = `translate3d(0, ${y}px, 0)`; }
    if (reveal || (lastScrollRatio >= 0 && Math.abs(ratio - lastScrollRatio) > 0.002)) {
      if (!lastScrollShown) { scrollTrack.classList.add('shown'); lastScrollShown = true; }
      clearTimeout(scrollFadeTimer);
      scrollFadeTimer = setTimeout(() => { scrollTrack.classList.remove('shown'); lastScrollShown = false; }, 750);
    }
    lastScrollRatio = ratio;
  }
  function follow() {
    if (!followFrame) followFrame = requestAnimationFrame(updateFollow);
  }
  function updateFollow() {
    followFrame = 0;
    const buffer = term.buffer.active;
    const scanKey = `${pictureRevision}:${buffer.type}:${buffer.viewportY}:${term.rows}:${term.modes.mouseTrackingMode}`;
    if (scanKey !== offeredKey) { offeredKey = scanKey; offeredRow = scrollRouter.offered(); }
    const row = offeredRow, now = row + ' ' + term.rows;
    hole = row;
    if (now !== offered) {
      offered = now;
      if (row >= 0) { offer.style.top = 100 * row / term.rows + '%'; offer.style.height = 100 / term.rows + '%'; }      // the row, in the terminal's own measure
    }
    // with no finger on the screen the picture is shown for the hole's sake; a strip that cannot be had is not asked for at every picture
    if (row >= 0 && !slide.active && performance.now() >= pinAfter) { pinAfter = performance.now() + 3000; slide.pin(performance.now()); }
    covered();
    updateScrollBar(buffer, scroller.dragging);
    // away from the end: the program says so itself, or the rows it was scrolled by say so. Either is enough: the
    // button is rather there once too often than missing when the program words its offer in a way not known here.
    const away = row >= 0 || !(buffer.viewportY >= buffer.baseY && scrollRouter.remoteUp <= 0);
    if (latest.hidden === away) latest.hidden = !away;
  }
  function covered() { const hidden = hole < 0 || strip.shown; if (offer.hidden !== hidden) offer.hidden = hidden; }
  term.onScroll(follow);
  const scrollRouter = new TerminalScrollRouter(term, send, () => tool);
  latest.addEventListener('click', () => {
    haptic('tick');
    scroller.stop(); unslide();
    scrollRouter.latest(); follow();
  });
  term.onData(data => { if (!restoring) send(data); });

  // While a finger scrolls, what is seen is shown from a strip of the rows the terminal has drawn (TerminalSlide,
  // TerminalStrip): so it moves with the finger by the pixel, and rows come into sight at one edge as they leave at
  // the other. The strip lies over the terminal's picture, in the same element, and covers the rows that move: those
  // a fullscreen program holds still, its heading and its message box, are the terminal's own.
  const over = document.createElement('div');
  over.id = 'slid'; over.hidden = true;
  holder.querySelector('.xterm-screen').appendChild(over);
  // An opaque surface under the moving rows hides the original text even when the live terminal is translucent.
  const strip = new TerminalStrip(over, TerminalSlide.KEPT, () => SKINS[resolvedSkin].t.background);
  // A paint notification is not necessarily new content. Reuse the last copied pixels until data,
  // selection or viewport changes; ANSI-only writes still invalidate through the parser event.
  let pictureRevision = 0;
  term.onWriteParsed(() => { pictureRevision++; });
  term.onSelectionChange(() => { pictureRevision++; });
  window.addEventListener('focus', () => { pictureRevision++; });
  window.addEventListener('blur', () => { pictureRevision++; });
  // the picture the terminal draws on the graphics card; drawn as plain elements, it has none to copy from
  const picture = () => drawing === 'webgl' ? holder.querySelector('.xterm-screen > canvas:not(.xterm-link-layer)') : null;
  let slideFrame = 0;
  const slide = new TerminalSlide({
    cell: () => tall / Math.max(1, term.rows),
    count: () => term.rows,
    viewport: () => term.buffer.active.viewportY,
    read() {
      const buffer = term.buffer.active, rows = [];
      for (let i = 0; i < term.rows; i++) { const line = buffer.getLine(buffer.viewportY + i); rows.push(line ? line.translateToString(true) : ''); }
      return rows;
    },
    draw() { if (!picture()) return false; term.refresh(0, term.rows - 1); return true; },
    keep(row, place, rows) {
      const source = picture(), buffer = term.buffer.active;
      return !!source && strip.keep(source, term.rows, row, place, rows, `${pictureRevision}:${buffer.type}:${buffer.viewportY}`);
    },
    show(place, top, bottom, first, last) { if (place === null) strip.hide(); else strip.show(place, top, bottom, first, last); covered(); },
    hole: () => hole,
    nudge: rows => scroller.nudge(rows)
  });
  const resting = () => !scroller.dragging && !scroller.velocity && !scroller.frame;
  function sliding(at) { slideFrame = slide.frame(at, resting()) ? requestAnimationFrame(sliding) : 0; }
  // (a hole that is still there has the picture shown over it again at the next frame)
  function unslide() { if (slideFrame) cancelAnimationFrame(slideFrame); slideFrame = 0; slide.cancel(); pinAfter = 0; requestAnimationFrame(follow); }
  term.onRender(() => {
    // The strip must see the current offer row before copying the newly drawn text.
    if (followFrame) cancelAnimationFrame(followFrame);
    updateFollow();
    slide.drawn();
  });
  // Touch movement scrolls local history or the program's fullscreen view, according to the active terminal mode.
  const scroller = new TerminalScroller({
    request: callback => requestAnimationFrame(callback), cancel: id => cancelAnimationFrame(id),
    lineHeight: () => tall / Math.max(1, term.rows),
    mode: () => scrollRouter.mode(),
    visible: () => !document.hidden,
    reduced: () => window.matchMedia('(prefers-reduced-motion: reduce)').matches,
    owing: () => scrollRouter.owing(),
    slide: pixels => slide.moved(pixels),
    scroll(lines, mode) {
      if (resolvedSkin === 'pond') document.dispatchEvent(new Event('rcli-terminal-motion'));
      const moved = scrollRouter.scroll(lines, mode);
      slide.ask(scrollRouter.asked, performance.now());
      follow(); return moved;
    }
  });
  // Two-finger pinch-to-zoom smoothly steps font size with a floating HUD pill.
  let pinch = null, zoomHudTimer = 0;
  const touchSpan = touches => Math.hypot(touches[0].clientX - touches[1].clientX, touches[0].clientY - touches[1].clientY);
  function showZoomHud(size) {
    if (!zoomHud) return;
    zoomHud.textContent = size + ' px';
    zoomHud.hidden = false;
    clearTimeout(zoomHudTimer);
    zoomHudTimer = setTimeout(() => { zoomHud.hidden = true; }, 750);
  }
  let touch = null;
  function sendTerminalTap(clientX, clientY) {
    const surface = holder.querySelector('.xterm-screen');
    if (!surface || !term.cols || !term.rows) return;
    const rect = surface.getBoundingClientRect();
    if (!rect.width || !rect.height) return;
    const col = Math.max(1, Math.min(term.cols, Math.floor((clientX - rect.left) / rect.width * term.cols) + 1));
    const row = Math.max(1, Math.min(term.rows, Math.floor((clientY - rect.top) / rect.height * term.rows) + 1));
    if (scrollRouter.sgr || tool === 'codex') {
      send(`\x1b[<0;${col};${row}M\x1b[<0;${col};${row}m`);
    } else if (col <= 223 && row <= 223) {
      const cx = String.fromCharCode(col + 32), cy = String.fromCharCode(row + 32);
      send('\x1b[M ' + cx + cy + '\x1b[M#' + cx + cy);
    }
  }
  screen.addEventListener('touchstart', event => {
    scrollRouter.start();
    if (event.touches.length === 2) {
      touch = null; scroller.stop(); unslide();
      pinch = { start: touchSpan(event.touches), size: fontSize };
    } else if (event.touches.length === 1 && !latest.contains(event.target)) {
      pinch = null;
      const point = event.touches[0];
      const caught = !!scroller.velocity;
      // Putting a finger down stops any active momentum glide immediately, while deferring WebGL strip capture until drag.
      scroller.stop();
      touch = { x: point.clientX, y: point.clientY, at: performance.now(), wall: Date.now(), moved: false, caught, target: event.target };
    } else { touch = null; pinch = null; scroller.stop(); unslide(); }
  }, { capture: true, passive: true });
  screen.addEventListener('touchmove', event => {
    if (event.touches.length === 2 && pinch) {
      const delta = touchSpan(event.touches) - pinch.start;
      const steps = Math.trunc(delta / 22);
      const next = Math.max(sizes[0], Math.min(sizes[sizes.length - 1], pinch.size + steps));
      if (next !== fontSize && sizes.includes(next)) {
        fontSize = next;
        term.options.fontSize = fontSize;
        if (bridge) bridge.saveFontSize(fontSize);
        showZoomHud(fontSize);
        haptic('tick');
        resize();
      }
      event.preventDefault(); event.stopPropagation();
      return;
    }
    if (event.touches.length !== 1) { touch = null; pinch = null; scroller.stop(); unslide(); return; }
    const point = event.touches[0], now = performance.now();
    if (touch && !touch.moved) {
      if (Math.hypot(point.clientX - touch.x, point.clientY - touch.y) < 6) return;
      touch.moved = true;
      scroller.start(touch.y, touch.at);
      scroller.move(touch.y, touch.at);
      slide.begin(scrollRouter.mode() === 'local' ? 'local' : 'remote', now);
      if (!slideFrame) slideFrame = requestAnimationFrame(sliding);
    }
    if (!scroller.dragging) return;
    scroller.move(point.clientY, now);
    event.preventDefault(); event.stopPropagation();
  }, { capture: true, passive: false });
  screen.addEventListener('touchend', event => {
    if (event.touches.length < 2) pinch = null;
    const endedTouch = touch;
    if (event.touches.length === 0) touch = null;
    if (endedTouch && !endedTouch.moved && !endedTouch.caught && event.touches.length === 0 && Date.now() - endedTouch.wall < 360) {
      const onLink = endedTouch.target && endedTouch.target.closest && endedTouch.target.closest('a, .xterm-link-layer');
      if (document.activeElement === input) {
        input.blur();
      } else if (!onLink && running && term.modes.mouseTrackingMode !== 'none') {
        sendTerminalTap(endedTouch.x, endedTouch.y);
      }
    }
    if (scroller.dragging) scroller.end(performance.now());
  }, { capture: true, passive: true });
  screen.addEventListener('touchcancel', () => { touch = null; pinch = null; scroller.stop(); unslide(); }, { capture: true, passive: true });
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
      touch = null; pinch = null; scroller.stop(); unslide();
    } else if (webglLost && webglRetries < 3) {
      webglRetries++;
      if (attachWebgl()) { strip.forget(); term.refresh(0, term.rows - 1); }
    }
  });
  term.buffer.onBufferChange(() => { scroller.stop(); unslide(); slide.forget(); scrollRouter.reset(); scrollRouter.offers = false; offeredKey = ''; follow(); });
  term.onResize(() => { unslide(); slide.forget(); strip.forget(); offeredKey = ''; });

  // Buttons act on the terminal without taking the keyboard away from the message box.
  function keepFocus(element) { element.addEventListener('mousedown', event => event.preventDefault()); }
  document.querySelectorAll('.keys button, #slash, #send, #latest, #voice').forEach(keepFocus);
  const REPEATABLE = new Set(['up', 'down', 'left', 'right', 'pageup', 'pagedown']);
  document.querySelectorAll('[data-key]').forEach(button => {
    const key = button.dataset.key;
    if (!REPEATABLE.has(key)) {
      button.addEventListener('click', () => { haptic('key'); send(KEYS[key]); });
      return;
    }
    let delay = 0, repeat = 0, repeated = false, startX = 0, startY = 0;
    const stop = () => {
      if (delay) { clearTimeout(delay); delay = 0; }
      if (repeat) { clearInterval(repeat); repeat = 0; }
    };
    button.addEventListener('pointerdown', event => {
      stop(); repeated = false; startX = event.clientX; startY = event.clientY;
      delay = setTimeout(() => {
        delay = 0; repeated = true;
        haptic('key'); send(KEYS[key]);
        repeat = setInterval(() => { haptic('key'); send(KEYS[key]); }, 70);
      }, 380);
    });
    button.addEventListener('pointermove', event => {
      if ((delay || repeat) && Math.hypot(event.clientX - startX, event.clientY - startY) > 10) stop();
    });
    ['pointerup', 'pointercancel', 'pointerleave'].forEach(name => button.addEventListener(name, stop));
    button.addEventListener('click', event => {
      stop();
      if (repeated) { repeated = false; event.preventDefault(); return; }
      haptic('key'); send(KEYS[key]);
    });
  });

  // ---- 自定义按键：本机存 localStorage（每台电脑各自一套），编辑器在按键条下展开。
  // 组合键编译成标准控制字节/转义序列；转义序列支持 \xNN \e \r \n \t；命令片段按原文发送。
  const KEYS_STORE = 'rcli-term-keys';
  const BASE_BYTES = { enter: '\r', tab: '\t', esc: '\x1b', space: ' ', backspace: '\x7f' };
  const CSI_LETTER = { up: 'A', down: 'B', right: 'C', left: 'D', home: 'H', end: 'F' };
  const CSI_TILDE = { pgup: 5, pagedown: 6, delete: 3, insert: 2 };
  const FKEYS = { f1: 'P', f2: 'Q', f3: 'R', f4: 'S', f5: '15~', f6: '17~', f7: '18~', f8: '19~', f9: '20~', f10: '21~', f11: '23~', f12: '24~' };
  const BASE_LABELS = { enter: '⏎', tab: 'Tab', esc: 'Esc', space: '空格', backspace: '⌫' };
  let customKeys = loadKeys(), editingKey = null, keyPanel = false;

  function nativeKeys(raw) {
    // App 的桥(RemoteCliNative,android6 分支起提供)把键存进应用偏好:升级与换线路都不丢
    try { return window.RemoteCliNative && typeof window.RemoteCliNative.termKeys === 'function'; } catch (error) { return false; }
  }
  function loadKeys() {
    try {
      if (nativeKeys()) {
        const list = JSON.parse(window.RemoteCliNative.termKeys() || '[]');
        return Array.isArray(list) ? list.filter(item => item && typeof item.label === 'string' && typeof item.bytes === 'string').slice(0, 12) : [];
      }
      const list = JSON.parse(localStorage.getItem(KEYS_STORE) || '[]');
      return Array.isArray(list) ? list.filter(item => item && typeof item.label === 'string' && typeof item.bytes === 'string').slice(0, 12) : [];
    } catch (error) { return []; }
  }
  function saveKeys() {
    const json = JSON.stringify(customKeys);
    try { if (nativeKeys()) { window.RemoteCliNative.termKeys(json); return; } } catch (error) { }
    try { localStorage.setItem(KEYS_STORE, json); } catch (error) { }
  }

  function comboBytes(mods, base) {
    const m = (mods.shift ? 1 : 0) + (mods.alt ? 2 : 0) + (mods.ctrl ? 4 : 0);   // xterm 修饰位:shift=1, alt=2, ctrl=4
    if (FKEYS[base]) {
      const code = FKEYS[base];
      return m ? '\x1b[1;' + (m + 1) + code : (code.length === 1 ? '\x1bO' + code : '\x1b[' + code);
    }
    if (CSI_LETTER[base]) return m ? '\x1b[1;' + (m + 1) + CSI_LETTER[base] : '\x1b[' + CSI_LETTER[base];
    if (CSI_TILDE[base]) return m ? '\x1b[' + CSI_TILDE[base] + ';' + (m + 1) + '~' : '\x1b[' + CSI_TILDE[base] + '~';
    if (BASE_BYTES[base]) {
      // Shift+Tab 的回退序列 ESC[Z 是这一组里唯一有独立历史的组合；其余按历史行为原样发送
      if (base === 'tab' && mods.shift && !mods.ctrl && !mods.alt) return '\x1b[Z';
      if (base === 'enter' && mods.ctrl && !mods.alt) return '\n';
      const plain = BASE_BYTES[base];
      return mods.alt ? '\x1b' + plain : plain;
    }
    const ch = base === 'space' ? ' ' : base;
    if (ch.length !== 1) return null;
    let byte = mods.shift && /[a-z]/.test(ch) ? ch.toUpperCase() : ch;
    if (mods.ctrl) {
      if (/[2-8]/.test(byte)) byte = String.fromCharCode({ 2: 0, 3: 27, 4: 28, 5: 29, 6: 30, 7: 31, 8: 127 }[byte]);
      else if (/[a-zA-Z@\[\\\]\^_ ]/.test(byte)) byte = String.fromCharCode(byte === ' ' ? 0 : byte.toUpperCase().charCodeAt(0) & 0x1f);
      else return null;                                            // 其余字符没有控制码
    }
    return (mods.alt ? '\x1b' : '') + byte;
  }
  function parseSeq(text) {
    if (typeof text !== 'string' || !text || text.length > 128) return null;
    let out = '', rest = text;
    while (rest.length) {
      const m = rest.match(/^\\x[0-9a-fA-F]{2}|^\\e|^\\r|^\\n|^\\t|^[\s\S]/);
      if (!m) return null;
      const token = m[0];
      rest = rest.slice(token.length);
      if (token === '\\e' || token === '\\E') out += '\x1b';
      else if (token === '\\r') out += '\r';
      else if (token === '\\n') out += '\n';
      else if (token === '\\t') out += '\t';
      else if (/^\\x/i.test(token)) out += String.fromCharCode(parseInt(token.slice(2), 16));
      else out += token;
    }
    return out;
  }
  function bytesOf(item) {
    if (item.kind === 'combo') return comboBytes(item.mods || {}, item.base);
    if (item.kind === 'seq') return item.bytes;
    return item.text + (item.enter ? '\r' : '');
  }
  function describe(item) {
    if (item.kind === 'combo') {
      const mods = ['ctrl', 'alt', 'shift'].filter(k => item.mods[k]).map(k => k[0].toUpperCase() + k.slice(1));
      const base = BASE_LABELS[item.base] || item.base.toUpperCase();
      return (mods.length ? mods.join('+') + '+' : '') + base;
    }
    if (item.kind === 'seq') return '转义序列 ' + item.source;
    return '发送 ' + item.text;
  }

  function renderKeys() {
    const host = document.querySelector('#custom-keys');
    if (!host) return;
    host.replaceChildren();
    for (const item of customKeys) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'custom';
      // A star marks a key that sends a control sequence, not plain text.
      const combo = item.kind !== 'text';
      button.textContent = item.label + (combo ? '*' : '');
      button.title = describe(item) + ' · 点击发送，长按编辑';
      button.setAttribute('aria-label', (combo ? '组合键 ' : '命令片段 ') + item.label + '，点击发送，长按编辑');
      // A tap always sends; only a long press opens the editor. The synthetic
      // click that follows a long press must not send the key a second time.
      let pressTimer = 0, longPressed = false;
      const cancelPress = () => { if (pressTimer) { clearTimeout(pressTimer); pressTimer = 0; } };
      button.addEventListener('pointerdown', () => {
        longPressed = false;
        cancelPress();
        pressTimer = setTimeout(() => { longPressed = true; pressTimer = 0; openEditor(item); }, 500);
      });
      ['pointerup', 'pointercancel', 'pointerleave'].forEach(name => button.addEventListener(name, cancelPress));
      button.addEventListener('contextmenu', event => {
        event.preventDefault();
        if (longPressed) return;
        cancelPress(); longPressed = true; openEditor(item);
      });
      button.addEventListener('click', event => {
        if (longPressed) { longPressed = false; event.preventDefault(); return; }
        send(bytesOf(item));
      });
      keepFocus(button);
      host.append(button);
      if (keyPanel) {
        const cross = document.createElement('button');
        cross.type = 'button';
        cross.className = 'custom-delete';
        cross.textContent = '✕';
        cross.setAttribute('aria-label', '删除 ' + item.label);
        cross.addEventListener('click', () => { customKeys = customKeys.filter(one => one.id !== item.id); saveKeys(); renderKeys(); });
        host.append(cross);
      }
    }
  }

  const BASE_CHOICES = [];
  for (let i = 0; i < 26; i++) BASE_CHOICES.push(['abcdefghijklmnopqrstuvwxyz'[i], String.fromCharCode(65 + i)]);
  for (let d = 0; d < 10; d++) BASE_CHOICES.push([String(d), String(d)]);
  [['enter', '⏎ 回车'], ['tab', 'Tab'], ['esc', 'Esc'], ['space', '空格'], ['backspace', '⌫ 退格'],
   ['up', '↑'], ['down', '↓'], ['left', '←'], ['right', '→'], ['home', 'Home'], ['end', 'End'],
   ['pgup', 'PgUp'], ['pgdn', 'PgDn'], ['delete', 'Del'], ['insert', 'Ins']].forEach(pair => BASE_CHOICES.push(pair));
  for (let f = 1; f <= 12; f++) BASE_CHOICES.push(['f' + f, 'F' + f]);

  function openEditor(item) {
    editingKey = item ? item.id : null;
    const form = document.querySelector('#key-editor');
    form.querySelector('#key-delete').hidden = !item;
    const kind = item ? item.kind : 'combo';
    form.querySelector('#key-kind').value = kind;
    form.querySelector('#key-label').value = item ? item.label : '';
    const mods = item && item.mods || { ctrl: false, alt: false, shift: false };
    form.querySelector('#key-ctrl').setAttribute('aria-pressed', String(!!mods.ctrl));
    form.querySelector('#key-alt').setAttribute('aria-pressed', String(!!mods.alt));
    form.querySelector('#key-shift').setAttribute('aria-pressed', String(!!mods.shift));
    if (item && item.kind === 'seq') form.querySelector('#key-seq').value = item.source;
    if (item && item.kind === 'combo') form.querySelector('#key-base').value = item.base;
    if (item && item.kind === 'text') {
      form.querySelector('#key-text').value = item.text;
      form.querySelector('#key-enter').checked = !!item.enter;
    }
    form.querySelector('#key-error').textContent = '';
    rowVisibility(kind);
    form.hidden = false;
  }
  function rowVisibility(kind) {
    const form = document.querySelector('#key-editor');
    form.querySelector('#key-combo-row').hidden = kind !== 'combo';
    form.querySelector('#key-seq-row').hidden = kind !== 'seq';
    form.querySelector('#key-text-row').hidden = kind !== 'text';
  }
  function closeEditor() {
    document.querySelector('#key-editor').hidden = true;
    editingKey = null;
    // Saving or cancelling leaves the strip usable: a tap must send, not edit.
    keyPanel = false;
    const add = document.querySelector('#key-add');
    if (add) add.setAttribute('aria-expanded', 'false');
  }

  function wireKeyEditor() {
    const form = document.querySelector('#key-editor');
    if (!form) return;
    const base = form.querySelector('#key-base');
    for (const [value, label] of BASE_CHOICES) base.append(new Option(label, value));
    form.querySelector('#key-kind').addEventListener('change', () => rowVisibility(form.querySelector('#key-kind').value));
    for (const id of ['key-ctrl', 'key-alt', 'key-shift']) {
      const button = form.querySelector('#' + id);
      button.addEventListener('click', () => button.setAttribute('aria-pressed', String(button.getAttribute('aria-pressed') !== 'true')));
    }
    form.addEventListener('submit', event => {
      event.preventDefault();
      const kind = form.querySelector('#key-kind').value;
      const label = form.querySelector('#key-label').value.trim();
      const error = form.querySelector('#key-error');
      let item = { id: editingKey || Array.from(crypto.getRandomValues(new Uint8Array(12)), b => b.toString(16).padStart(2, '0')).join(''), kind, label };
      if (!label) { error.textContent = '请填写标签'; return; }
      if (kind === 'combo') {
        item.mods = { ctrl: form.querySelector('#key-ctrl').getAttribute('aria-pressed') === 'true',
                      alt: form.querySelector('#key-alt').getAttribute('aria-pressed') === 'true',
                      shift: form.querySelector('#key-shift').getAttribute('aria-pressed') === 'true' };
        item.base = form.querySelector('#key-base').value;
        item.bytes = comboBytes(item.mods, item.base);
        if (item.bytes === null) { error.textContent = '这个组合键没有对应的控制码，请换一个基础键'; return; }
      } else if (kind === 'seq') {
        item.source = form.querySelector('#key-seq').value;
        item.bytes = parseSeq(item.source);
        if (item.bytes === null) { error.textContent = '转义序列无效（支持 \\xNN \\e \\r \\n \\t）'; return; }
      } else {
        item.text = form.querySelector('#key-text').value;
        item.enter = form.querySelector('#key-enter').checked;
        item.bytes = item.text + (item.enter ? '\r' : '');
        if (!item.text) { error.textContent = '请填写要发送的内容'; return; }
      }
      if (bytesOf(item).length > 32 && kind !== 'text') { error.textContent = '序列过长'; return; }
      const known = customKeys.find(one => one.id === item.id);
      if (known) customKeys[customKeys.indexOf(known)] = item; else customKeys.push(item);
      saveKeys();
      closeEditor();
      renderKeys();
    });
    form.querySelector('#key-delete').addEventListener('click', () => {
      customKeys = customKeys.filter(one => one.id !== editingKey);
      saveKeys(); closeEditor(); renderKeys();
    });
    form.querySelector('#key-cancel').addEventListener('click', closeEditor);
  }
  const keyAdd = document.querySelector('#key-add');
  if (keyAdd) keyAdd.addEventListener('click', () => {
    if (keyPanel) { closeEditor(); renderKeys(); return; }
    keyPanel = true;
    keyAdd.setAttribute('aria-expanded', 'true');
    openEditor(null);
  });
  wireKeyEditor();
  renderKeys();

  // The box is measured only when its text may have wrapped differently, once per frame: measuring on every key
  // press made the whole page lay itself out again while typing. Preserve scrollTop so editing the middle of
  // multi-line text never jumps back to the first line.
  const BOX_MIN = 38, BOX_MAX = 144;
  const terminalId = new URLSearchParams(location.search).get('id') || '';
  const draftKey = terminalId ? 'rcli-term-draft:' + terminalId : '';
  function saveDraft() {
    if (!draftKey) return;
    try { if (input.value) sessionStorage.setItem(draftKey, input.value); else sessionStorage.removeItem(draftKey); } catch (error) { /* private window */ }
  }
  try { const saved = draftKey ? sessionStorage.getItem(draftKey) : ''; if (saved && !input.value) input.value = saved.slice(0, 8000); } catch (error) { /* ignore */ }
  let boxHeight = BOX_MIN, boxLength = input.value.length, boxFrame = 0, boxFilled = null;
  function measure() {
    boxFrame = 0;
    const length = input.value.length;
    const prevScroll = input.scrollTop;
    if (!length) {
      if (boxHeight !== BOX_MIN) { boxHeight = BOX_MIN; input.style.height = BOX_MIN + 'px'; }
      boxLength = 0;
      return;
    }
    if (length < boxLength && boxHeight > BOX_MIN) { input.style.height = BOX_MIN + 'px'; boxHeight = BOX_MIN; }
    boxLength = length;
    const wanted = Math.min(BOX_MAX, Math.max(BOX_MIN, input.scrollHeight));
    if (wanted !== boxHeight) { boxHeight = wanted; input.style.height = wanted + 'px'; }
    if (prevScroll && input.scrollTop !== prevScroll) input.scrollTop = prevScroll;
  }
  function grow() {
    const filled = input.value.length > 0;
    if (filled !== boxFilled) {
      boxFilled = filled;
      sendButton.textContent = filled ? '发送' : '回车';
      sendButton.classList.toggle('text', filled);
    }
    if (!boxFrame) boxFrame = requestAnimationFrame(measure);
  }
  function insertAtCursor(text) {
    const val = input.value;
    const hasPos = typeof input.selectionStart === 'number' && typeof input.selectionEnd === 'number';
    const start = hasPos ? input.selectionStart : val.length;
    const end = hasPos ? input.selectionEnd : val.length;
    const before = val.slice(0, start), after = val.slice(end);
    const gap = /[A-Za-z0-9]$/.test(before) && /^[A-Za-z0-9]/.test(text) ? ' ' : '';   // Chinese runs on without a space
    const inserted = gap + text;
    const next = (before + inserted + after).slice(0, 8000);
    const caret = Math.min(next.length, before.length + inserted.length);
    input.value = next;
    if (hasPos) try { input.setSelectionRange(caret, caret); } catch (error) { /* unfocused */ }
    saveDraft(); grow(); drawPalette();
  }
  function drawPalette() {
    const typed = input.value;
    const typing = /^\/\S*$/.test(typed);
    const list = COMMANDS[tool] || [];
    if (!list.length || ended || !(typing || paletteOpen)) { palette.hidden = true; slash.setAttribute('aria-expanded', 'false'); return; }
    const wanted = typing ? typed.toLowerCase() : '/';
    const matches = list.filter(c => c[0].startsWith(wanted)).concat(list.filter(c => !c[0].startsWith(wanted) && c[0].includes(wanted.slice(1)) && wanted.length > 1));
    palette.textContent = '';
    matches.forEach(([command, about]) => {
      const row = document.createElement('button'), name = document.createElement('b'), text = document.createElement('span');
      row.type = 'button'; row.setAttribute('role', 'option'); name.textContent = command; text.textContent = about;
      row.append(name, text); keepFocus(row);
      row.addEventListener('click', () => { haptic('confirm'); if (send(paste(command) + '\r')) { input.value = ''; saveDraft(); paletteOpen = false; grow(); drawPalette(); } });
      palette.append(row);
    });
    if (!matches.length) { const none = document.createElement('p'); none.textContent = '常用命令里没有匹配项；仍可直接发送，由工具自己处理。'; palette.append(none); }
    palette.hidden = false; slash.setAttribute('aria-expanded', 'true');
  }
  // Bracketed paste keeps Chinese and multi-line text intact in the tool's own editor.
  function paste(text) { return term.modes.bracketedPasteMode ? `\x1b[200~${text}\x1b[201~` : text; }
  // Android WebViews and some OEM IMEs snap a mid-text tap inside a flex-resized textarea back to the end of the text.
  // Let native hit-testing run first; only if a short single tap left the caret stuck at the end do we map the tap
  // coordinates through an exact-typography mirror and place the caret where the finger actually landed.
  function caretOffsetAtPoint(el, relX, relY, scrollTop) {
    const val = el.value;
    if (!val) return null;
    const rect = el.getBoundingClientRect();
    if (!rect.width || !rect.height) return null;
    const cs = getComputedStyle(el);
    const padL = parseFloat(cs.paddingLeft) || 0, padR = parseFloat(cs.paddingRight) || 0;
    const padT = parseFloat(cs.paddingTop) || 0, padB = parseFloat(cs.paddingBottom) || 0;
    const hitX = rect.left + Math.max(padL + 1, Math.min(rect.width - padR - 1, relX));
    const hitY = rect.top + Math.max(padT + 1, Math.min(rect.height - padB - 1, relY));
    const mirror = document.createElement('div');
    const style = mirror.style;
    style.position = 'fixed';
    style.left = rect.left + 'px';
    style.top = rect.top + 'px';
    style.width = rect.width + 'px';
    style.height = rect.height + 'px';
    style.boxSizing = cs.boxSizing;
    style.padding = cs.padding;
    style.border = cs.border;
    style.font = cs.font;
    style.fontFamily = cs.fontFamily;
    style.fontSize = cs.fontSize;
    style.fontWeight = cs.fontWeight;
    style.lineHeight = cs.lineHeight;
    style.letterSpacing = cs.letterSpacing;
    style.whiteSpace = 'pre-wrap';
    style.wordBreak = cs.wordBreak;
    style.overflowWrap = cs.overflowWrap || 'break-word';
    style.overflow = 'hidden';
    style.opacity = '0';
    style.pointerEvents = 'auto';
    style.zIndex = '2147483647';
    const inner = document.createElement('div');
    if (scrollTop) inner.style.transform = `translateY(${-scrollTop}px)`;
    const textNode = document.createTextNode(val);
    inner.appendChild(textNode);
    mirror.appendChild(inner);
    document.body.appendChild(mirror);
    try {
      if (document.caretPositionFromPoint) {
        const pos = document.caretPositionFromPoint(hitX, hitY);
        if (pos && pos.offsetNode === textNode) return Math.max(0, Math.min(val.length, pos.offset));
      } else if (document.caretRangeFromPoint) {
        const range = document.caretRangeFromPoint(hitX, hitY);
        if (range && range.startContainer === textNode) return Math.max(0, Math.min(val.length, range.startOffset));
      }
      return null;
    } finally {
      mirror.remove();
    }
  }
  let inputComposing = false, inputTap = null, inputTapTimer = 0;
  input.addEventListener('compositionstart', () => { inputComposing = true; });
  input.addEventListener('compositionend', () => { inputComposing = false; });
  input.addEventListener('touchstart', event => {
    clearTimeout(inputTapTimer);
    if (event.touches.length !== 1 || inputComposing || !input.value || input.disabled) { inputTap = null; return; }
    const p = event.touches[0], rect = input.getBoundingClientRect();
    inputTap = { x: p.clientX, y: p.clientY, relX: p.clientX - rect.left, relY: p.clientY - rect.top, scrollTop: input.scrollTop, at: Date.now(), moved: false };
  }, { passive: true });
  input.addEventListener('touchmove', event => {
    if (!inputTap || event.touches.length !== 1) { inputTap = null; return; }
    const p = event.touches[0];
    if (Math.hypot(p.clientX - inputTap.x, p.clientY - inputTap.y) > 8) inputTap.moved = true;
  }, { passive: true });
  input.addEventListener('touchcancel', () => { inputTap = null; clearTimeout(inputTapTimer); }, { passive: true });
  input.addEventListener('touchend', event => {
    const tap = inputTap;
    inputTap = null;
    if (!tap || tap.moved || inputComposing || event.touches.length !== 0 || Date.now() - tap.at > 380) return;
    const len = input.value.length;
    if (!len) return;
    clearTimeout(inputTapTimer);
    inputTapTimer = setTimeout(() => {
      if (inputComposing || document.activeElement !== input || input.value.length !== len) return;
      if (input.selectionStart !== len || input.selectionEnd !== len) return;
      const offset = caretOffsetAtPoint(input, tap.relX, tap.relY, tap.scrollTop);
      if (offset !== null && offset >= 0 && offset < len) {
        try { input.setSelectionRange(offset, offset); } catch (error) { /* ignore */ }
      }
    }, 40);
  }, { passive: true });
  input.addEventListener('input', () => { saveDraft(); grow(); if (!palette.hidden || input.value.charCodeAt(0) === 47) drawPalette(); });
  slash.addEventListener('click', () => {
    haptic('tick');
    paletteOpen = palette.hidden;
    if (paletteOpen && !input.value) { input.value = '/'; saveDraft(); grow(); } else if (!paletteOpen && /^\/\S*$/.test(input.value)) { input.value = ''; saveDraft(); grow(); }
    drawPalette();
  });
  $('composer').addEventListener('submit', event => {
    event.preventDefault();
    const text = input.value;
    if (!text) { haptic('key'); send('\r'); return; }
    if (!text.trim()) return;
    haptic('confirm');
    if (send(paste(text) + '\r')) { input.value = ''; saveDraft(); paletteOpen = false; grow(); drawPalette(); scrollRouter.reset(); term.scrollToBottom(); follow(); }
  });
  $('menu').addEventListener('click', () => { haptic('tick'); if (bridge) bridge.menu(); });
  $('files').addEventListener('click', () => { haptic('tick'); if (bridge) bridge.files(); });
  $('again').addEventListener('click', () => { haptic('confirm'); if (bridge) bridge.again(); });
  $('back').addEventListener('click', () => { haptic('tick'); if (bridge) bridge.close(); });
  $('voice').addEventListener('click', () => { haptic('tick'); if (bridge) bridge.voice(); });
  // A microphone that does nothing is worse than none: many phones have no recognizer of their own, and there the
  // one on the keyboard is the way to speak. In a browser the button stays only where the browser can listen.
  const app = window.RemoteCliNative;
  if (app ? (app.canDictate && !app.canDictate()) : !(window.SpeechRecognition || window.webkitSpeechRecognition)) $('voice').hidden = true;

  const agyIds = (() => { try { const list = JSON.parse(sessionStorage.getItem('agy-shell-ids') || '[]'); return new Set(Array.isArray(list) ? list : []); } catch (error) { return new Set(); } })();
  let writing = false, writeEpoch = 0, queuedText = '', queuedMeta = null;
  function flushWrite(text, meta) {
    const myEpoch = writeEpoch;
    const finish = () => {
      if (myEpoch !== writeEpoch) { writing = false; return; }
      initialized = true;
      if (!meta.more) {
        const caughtUp = restoring, connected = meta.live && !running;
        restoring = false; running = meta.live;
        if (connected && tool === 'shell' && meta.plain === 'PowerShell') {
          try {
            if (sessionStorage.getItem('agy-shell-run') === meta.id) {
              sessionStorage.removeItem('agy-shell-run');
              send('agy\r');
            }
          } catch (error) { /* the ordinary terminal input remains available */ }
        }
        if (caughtUp) term.scrollToBottom();
        if (caughtUp || connected) layout(true);      // the size is settled once, not after every piece of output
        if (cut && running) redraw();
      } else if (!restoring) running = meta.live;
      follow();
      if (bridge) bridge.rendered(String(meta.after), meta.more ? 'more' : meta.hasText || meta.status === 'busy' ? 'active' : 'quiet');
      if (queuedMeta) {
        const nextText = queuedText, nextMeta = queuedMeta;
        queuedText = ''; queuedMeta = null;
        flushWrite(nextText, nextMeta);
      } else {
        writing = false;
      }
    };
    if (text) { writing = true; term.write(text, finish); }
    else finish();
  }
  let wasEnded = null, connectRevealTimer = 0;
  function show(element, text) { if (element.textContent !== text) element.textContent = text; }
  const transportNames = { websocket: 'WebSocket', sse: 'SSE', http: 'HTTP 长轮询' };
  const connectionStates = { connecting: '连接中', connected: '已连接', reconnecting: '重连中', offline: '已断开', paused: '后台暂停' };
  window.TerminalUI = {
    connection(info) {
      if (!info || !connectionText) return;
      const transport = transportNames[info.transport] || info.transport || '未知传输';
      const state = connectionStates[info.state] || info.state || '未知状态';
      const latency = Number.isFinite(info.lastLatency) ? `中转延迟 ${Math.round(Math.max(0, info.lastLatency))} ms` : '中转延迟待测';
      const reconnects = Number.isFinite(info.reconnects) ? Math.max(0, info.reconnects) : 0;
      show(connectionText, `${transport} · ${state}`);
      show(connectionDetail, `${latency} · 重连 ${reconnects} 次`);
      connection.className = info.state || '';
      // Keep the terminal canvas calm when healthy; reveal the banner only during slow connect or outages.
      clearTimeout(connectRevealTimer);
      if (info.state === 'connected' || info.state === 'paused') {
        connection.hidden = true;
      } else if (info.state === 'connecting') {
        connectRevealTimer = setTimeout(() => { if (connection.className === 'connecting') connection.hidden = false; }, 1200);
      } else {
        connection.hidden = false;
      }
    },
    receive(payload) {
      const t = payload.terminal, device = payload.device;
      const live = t.state === 'running' && device.online && device.enabled;
      tool = t.tool; ended = t.state === 'closed';
      // The size changing to one this page did not ask for means the terminal is open in another place too. A size
      // that merely has not caught up with this page's own request says nothing.
      const reported = t.cols && t.rows ? `${t.cols}x${t.rows}` : '';
      if (reported && theirs && reported !== theirs && reported !== lastSize && Date.now() - sized > 2500) elsewhere = true;
      if (reported === `${term.cols}x${term.rows}`) elsewhere = false;
      theirs = reported;
      if (nudge && reported === nudge) settle();
      slash.hidden = !(COMMANDS[tool] || []).length;
      if ($('files').hidden === !!t.dir) $('files').hidden = !t.dir;        // the files of its project; a terminal outside every project has none to show
      if (!initialized || payload.reset) {
        writeEpoch++; writing = false; queuedText = ''; queuedMeta = null;
        restoring = true; running = false;
        scroller.stop(); unslide(); slide.forget();
        scrollRouter.reset(); scrollRouter.sgr = false; offeredKey = '';
        term.reset(); term.resize(t.cols || 80, t.rows || 24);
        start = payload.chunks.length ? payload.chunks[0].seq - 1 : payload.after;
        cut = start > 0; nudge = ''; clearTimeout(nudgeTimer);
      }
      const more = payload.after < t.seq;
      // Output arrives many times a second: the page around the terminal is touched only where something changed.
      const plain = device.shell || 'PowerShell';      // what the computer calls its plain terminal
      // its project; one that took on a conversation outside every project runs in that conversation's folder
      const agyShell = tool === 'shell' && (/^Antigravity CLI(?:\s|$|·)/.test(t.title || '') || agyIds.has(t.id));
      const shownTool = agyShell ? 'agy' : tool;
      const toolName = { claude: 'Claude Code', codex: 'Codex', agy: 'Antigravity CLI', opencode: 'OpenCode', qoder: 'Qoder', cline: 'Cline', pi: 'Pi', shell: plain }[shownTool] || shownTool;
      const where = toolName + ' · ' + (t.dir || String(t.folder || '').replace(/[\\/]+$/, '').split(/[\\/]/).pop());
      // Someone else's size on the screen means the terminal is open in another place too.
      const hideShared = !(running && elsewhere);
      if ($('shared').hidden !== hideShared) $('shared').hidden = hideShared;
      const loading = restoring && more ? `载入输出 ${Math.round(100 * (payload.after - start) / Math.max(1, t.seq - start))}%` : '';
      show($('title'), t.title);
      show(status, where + ' · ' + (loading || (!device.online ? '电脑离线，等待重连' : !device.enabled ? '电脑远控已关闭' : t.state === 'starting' ? '正在启动'
        : ended ? '已结束' : t.status === 'busy' ? '正在执行' : t.status === 'idle' ? '等待输入' : '已连接')));
      const mark = live ? (t.status === 'busy' ? 'busy' : 'ready') : '';
      if (dot.className !== mark) dot.className = mark;
      show(notice, t.error || '');
      if ($('ended').hidden !== (!ended || more)) $('ended').hidden = !ended || more;
      if (ended !== wasEnded) {
        wasEnded = ended;
        const canResume = shownTool !== 'shell' && !(shownTool === 'agy' && !t.session);
        show($('ended-text'), canResume ? '终端已结束，对话保存在电脑上' : toolName + ' 已结束，画面保留到这里');
        $('again').hidden = !canResume;
        input.disabled = ended; sendButton.disabled = ended; $('voice').disabled = ended;
        drawPalette();
      }
      const text = payload.chunks.map(c => c.data).join('');
      const meta = { id: t.id, after: payload.after, more, live, plain, status: t.status, hasText: !!text };
      // Write immediately when xterm is idle (0 ms added latency); coalesce bursts arriving while a write is in flight.
      if (writing) {
        queuedText += text;
        queuedMeta = queuedMeta ? Object.assign(meta, { hasText: queuedMeta.hasText || meta.hasText }) : meta;
      } else {
        flushWrite(text, meta);
      }
    },
    error(message) { notice.textContent = message; status.textContent = '连接中断，正在重连'; dot.className = ''; running = false; },
    inputError(message, draft) {
      notice.textContent = message;
      // A typed message comes back for another try; a single key press does not.
      const typed = (draft || '').replace(/\x1b\[20[01]~/g, '').replace(/\r$/, '');
      if (typed && !input.value && !/[\x00-\x08\x0b-\x1f]/.test(typed)) { input.value = typed; saveDraft(); grow(); }
    },
    // Spoken words are inserted at the current cursor position so mid-sentence edits work naturally.
    dictated(text) {
      if (!text || ended) return;
      insertAtCursor(text);
    },
    // a word for a moment, under the screen (what was copied, why a choice cannot be made)
    notice(text) { notice.textContent = text; clearTimeout(noticeTimer); noticeTimer = setTimeout(() => { if (notice.textContent === text) notice.textContent = ''; }, 3500); },
    fontSizes() { return sizes.slice(); },
    fontSize() { return fontSize; },
    setFontSize(size) { if (!sizes.includes(size)) return; fontSize = size; term.options.fontSize = size; if (bridge) bridge.saveFontSize(size); resize(); },
    skins() { return Object.keys(SKINS).map(key => ({ key, name: SKINS[key].name + (SKINS[key].light ? '（明亮）' : ''), current: key === skin })); },
    spacings() { return Object.keys(SPACING).map(key => ({ key, name: SPACING[key][0], current: key === spacing })); },
    setSpacing(name) { if (!SPACING[name]) return; spacing = name; term.options.lineHeight = SPACING[name][1]; resize(); },
    cursors() { return Object.keys(CURSORS).map(key => ({ key, name: CURSORS[key], current: key === cursorStyle })); },
    setCursorStyle(style) { if (!CURSORS[style]) return; cursorStyle = style; term.options.cursorStyle = style; if (bridge && bridge.saveCursor) bridge.saveCursor(style); },
    setSkin(name) { applySkin(name); },
    drawing() { return drawing; },
    screenText() {
      let text = term.getSelection();
      if (!text) {
        const buffer = term.buffer.active, lines = [];
        for (let i = 0; i < buffer.length; i++) lines.push(buffer.getLine(i).translateToString(true));
        text = lines.join('\n').trim();
      }
      return text;
    },
    terminal: term,
    slide, strip          // for the checks
  };
  resetThemeScroll = () => { scroller.stop(); unslide(); slide.forget(); strip.forget(); };
  grow();
  layout();
  if (bridge) bridge.ready();
})();
