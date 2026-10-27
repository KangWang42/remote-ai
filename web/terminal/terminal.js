/* The terminal screen. bridge.js connects it to the relay. */
(async () => {
  'use strict';
  const bridge = window.ProjectTerminal;
  const $ = id => document.getElementById(id);
  const input = $('input'), status = $('status-text'), connection = $('connection'), connectionText = $('connection-text'), connectionDetail = $('connection-detail'), notice = $('notice'), palette = $('palette'), sendButton = $('send');
  const screen = $('screen'), holder = $('terminal'), dot = $('dot'), slash = $('slash'), latest = $('latest');

  const SKINS = window.RemoteCliSkins;      // skins.js
  let skin = bridge && SKINS[bridge.skin()] ? bridge.skin() : 'paper';
  const FAMILY = '"Terminal Mono", "Noto Sans Mono CJK SC", "Droid Sans Mono", monospace';
  // The typeface must be ready before the terminal measures a character, or every cell gets the wrong width.
  try { await Promise.race([Promise.all([document.fonts.load('13px "Terminal Mono"'), document.fonts.load('bold 13px "Terminal Mono"')]), new Promise(done => setTimeout(done, 900))]); } catch (error) { /* system monospace */ }

  const sizes = [11, 13, 15, 17];
  // Room between the lines: closer shows more of the screen, wider is easier on the eyes.
  const SPACING = { tight: ['紧凑', 1.0], cozy: ['适中', 1.12], roomy: ['宽松', 1.3] };
  let spacing = bridge && bridge.spacing && SPACING[bridge.spacing()] ? bridge.spacing() : 'cozy';
  let fontSize = Number(bridge && bridge.fontSize()) || 13;
  if (!sizes.includes(fontSize)) fontSize = 13;
  const term = new Terminal({ fontSize, fontFamily: FAMILY, cursorBlink: false, lineHeight: SPACING[spacing][1],
    // No per-row accessibility tree: on a phone it is rebuilt on every refresh and makes typing stutter.
    scrollback: 5000, screenReaderMode: false, allowProposedApi: false, allowTransparency: true, theme: SKINS[skin].t, ...window.RemoteCliLook });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(holder);
  // Drawing on the graphics card keeps a busy screen smooth; a phone that cannot do it falls back to plain elements.
  let drawing = 'dom';
  let resetThemeScroll = null;
  if (window.WebglAddon && !(bridge && bridge.renderer() === 'dom')) {
    try {
      const gl = new WebglAddon.WebglAddon();
      gl.onContextLoss(() => { gl.dispose(); drawing = 'dom'; });
      term.loadAddon(gl);
      drawing = 'webgl';
    } catch (error) { drawing = 'dom'; }
  }
  function applySkin(name) {
    if (!SKINS[name]) return;
    skin = name;
    if (resetThemeScroll) resetThemeScroll();
    const s = window.RemoteCliPaint(name);
    term.options.theme = name === 'pond' ? { ...s.t, background: '#10191b66' } : s.t;
    if (bridge) bridge.chrome(s.t.background);
  }
  applySkin(skin);

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
  function layout() {
    const w = screen.clientWidth, h = screen.clientHeight;
    if (!w || !h) return;
    if (w !== wide) { wide = w; tall = h; } else if (h > tall) tall = h;
    holder.style.height = tall + 'px';
    if (!restoring || !initialized) resize();
  }
  new ResizeObserver(layout).observe(screen);

  // The way back to the end is the button in the corner. A program that offers one of its own on its screen (Claude
  // Code writes "Jump to bottom (ctrl+End)" over the last row of its conversation) is not shown doing so: on a phone
  // it is words in another language cut into a line of text, for something the button already does.
  const offer = document.createElement('div');
  offer.id = 'offer'; offer.hidden = true;
  holder.querySelector('.xterm-screen').appendChild(offer);
  // That row is no row of the conversation any more: the rows above it are shown a row lower, over it (TerminalSlide,
  // `hole`), so that nothing stands between the conversation and the program's message box. Where there is no strip
  // to show them from, the row is covered instead.
  let offered = '', hole = -1, pinAfter = 0, followFrame = 0;
  function follow() {
    if (!followFrame) followFrame = requestAnimationFrame(updateFollow);
  }
  function updateFollow() {
    followFrame = 0;
    const buffer = term.buffer.active, row = scrollRouter.offered(), now = row + ' ' + term.rows;
    hole = row;
    if (now !== offered) {
      offered = now;
      if (row >= 0) { offer.style.top = 100 * row / term.rows + '%'; offer.style.height = 100 / term.rows + '%'; }      // the row, in the terminal's own measure
    }
    // with no finger on the screen the picture is shown for the hole's sake; a strip that cannot be had is not asked for at every picture
    if (row >= 0 && !slide.active && performance.now() >= pinAfter) { pinAfter = performance.now() + 3000; slide.pin(performance.now()); }
    covered();
    // away from the end: the program says so itself, or the rows it was scrolled by say so. Either is enough: the
    // button is rather there once too often than missing when the program words its offer in a way not known here.
    const away = row >= 0 || !(buffer.viewportY >= buffer.baseY && scrollRouter.remoteUp <= 0);
    if (latest.hidden === away) latest.hidden = !away;
  }
  function covered() { const hidden = hole < 0 || strip.shown; if (offer.hidden !== hidden) offer.hidden = hidden; }
  term.onScroll(follow);
  const scrollRouter = new TerminalScrollRouter(term, send, () => tool);
  latest.addEventListener('click', () => {
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
  const strip = new TerminalStrip(over, TerminalSlide.KEPT, () => SKINS[skin].t.background);
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
    keep(row, place, rows) { const source = picture(); return !!source && strip.keep(source, term.rows, row, place, rows); },
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
      if (skin === 'pond') document.dispatchEvent(new Event('rcli-terminal-motion'));
      const moved = scrollRouter.scroll(lines, mode);
      slide.ask(scrollRouter.asked, performance.now());
      follow(); return moved;
    }
  });
  screen.addEventListener('touchstart', event => {
    scrollRouter.start();
    if (event.touches.length === 1 && !latest.contains(event.target)) {
      const now = performance.now();
      scroller.start(event.touches[0].clientY, now);
      slide.begin(scrollRouter.mode() === 'local' ? 'local' : 'remote', now);
      if (!slideFrame) slideFrame = requestAnimationFrame(sliding);
    } else { scroller.stop(); unslide(); }
  }, { capture: true, passive: true });
  screen.addEventListener('touchmove', event => {
    if (event.touches.length !== 1) { scroller.stop(); unslide(); return; }
    if (!scroller.dragging) return;
    scroller.move(event.touches[0].clientY, performance.now());
    event.preventDefault(); event.stopPropagation();
  }, { capture: true, passive: false });
  screen.addEventListener('touchend', () => scroller.end(performance.now()), { capture: true, passive: true });
  screen.addEventListener('touchcancel', () => { scroller.stop(); unslide(); }, { capture: true, passive: true });
  document.addEventListener('visibilitychange', () => { if (document.hidden) { scroller.stop(); unslide(); } });
  term.buffer.onBufferChange(() => { scroller.stop(); unslide(); slide.forget(); scrollRouter.reset(); scrollRouter.offers = false; follow(); });
  term.onResize(() => { unslide(); slide.forget(); strip.forget(); });

  // Buttons act on the terminal without taking the keyboard away from the message box.
  function keepFocus(element) { element.addEventListener('mousedown', event => event.preventDefault()); }
  document.querySelectorAll('.keys button, #slash, #send, #latest, #voice').forEach(keepFocus);
  document.querySelectorAll('[data-key]').forEach(button => button.addEventListener('click', () => send(KEYS[button.dataset.key])));

  // The box is measured only when its text may have wrapped differently, once per frame: measuring on every key
  // press made the whole page lay itself out again while typing.
  let boxHeight = 44, boxLength = 0, boxFrame = 0, boxFilled = null;
  function measure() {
    boxFrame = 0;
    const length = input.value.length;
    if (length < boxLength || !length) { input.style.height = '44px'; boxHeight = 44; }
    boxLength = length;
    const wanted = Math.min(120, Math.max(44, input.scrollHeight + 2));
    if (wanted !== boxHeight) { boxHeight = wanted; input.style.height = wanted + 'px'; }
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
      row.addEventListener('click', () => { if (send(paste(command) + '\r')) { input.value = ''; paletteOpen = false; grow(); drawPalette(); } });
      palette.append(row);
    });
    if (!matches.length) { const none = document.createElement('p'); none.textContent = '常用命令里没有匹配项；仍可直接发送，由工具自己处理。'; palette.append(none); }
    palette.hidden = false; slash.setAttribute('aria-expanded', 'true');
  }
  // Bracketed paste keeps Chinese and multi-line text intact in the tool's own editor.
  function paste(text) { return term.modes.bracketedPasteMode ? `\x1b[200~${text}\x1b[201~` : text; }
  input.addEventListener('input', () => { grow(); if (!palette.hidden || input.value.charCodeAt(0) === 47) drawPalette(); });
  slash.addEventListener('click', () => {
    paletteOpen = palette.hidden;
    if (paletteOpen && !input.value) { input.value = '/'; grow(); } else if (!paletteOpen && /^\/\S*$/.test(input.value)) { input.value = ''; grow(); }
    drawPalette();
  });
  $('composer').addEventListener('submit', event => {
    event.preventDefault();
    const text = input.value;
    if (!text) { send('\r'); return; }
    if (!text.trim()) return;
    if (send(paste(text) + '\r')) { input.value = ''; paletteOpen = false; grow(); drawPalette(); scrollRouter.reset(); term.scrollToBottom(); follow(); }
  });
  $('font').addEventListener('click', () => {
    fontSize = sizes[(sizes.indexOf(fontSize) + 1) % sizes.length];
    term.options.fontSize = fontSize;
    if (bridge) bridge.saveFontSize(fontSize);
    resize();
  });
  $('menu').addEventListener('click', () => { if (bridge) bridge.menu(); });
  $('files').addEventListener('click', () => { if (bridge) bridge.files(); });
  $('again').addEventListener('click', () => { if (bridge) bridge.again(); });
  $('back').addEventListener('click', () => { if (bridge) bridge.close(); });
  $('voice').addEventListener('click', () => { if (bridge) bridge.voice(); });
  // A microphone that does nothing is worse than none: many phones have no recognizer of their own, and there the
  // one on the keyboard is the way to speak. In a browser the button stays only where the browser can listen.
  const app = window.RemoteCliNative;
  if (app ? (app.canDictate && !app.canDictate()) : !(window.SpeechRecognition || window.webkitSpeechRecognition)) $('voice').hidden = true;

  let wasEnded = null;
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
        restoring = true; running = false;
        scroller.stop(); unslide(); slide.forget();
        scrollRouter.reset(); scrollRouter.sgr = false;
        term.reset(); term.resize(t.cols || 80, t.rows || 24);
        start = payload.chunks.length ? payload.chunks[0].seq - 1 : payload.after;
        cut = start > 0; nudge = ''; clearTimeout(nudgeTimer);
      }
      const more = payload.after < t.seq;
      // Output arrives many times a second: the page around the terminal is touched only where something changed.
      const plain = device.shell || 'PowerShell';      // what the computer calls its plain terminal
      // its project; one that took on a conversation outside every project runs in that conversation's folder
      const toolName = { claude: 'Claude Code', codex: 'Codex', opencode: 'OpenCode', qoder: 'Qoder', cline: 'Cline', pi: 'Pi', shell: plain }[tool] || tool;
      const where = toolName + ' · ' + (t.dir || String(t.folder || '').replace(/[\\/]+$/, '').split(/[\\/]/).pop());
      // Someone else's size on the screen means the terminal is open in another place too.
      $('shared').hidden = !(running && elsewhere);
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
        show($('ended-text'), tool === 'shell' ? plain + ' 已结束，画面保留到这里' : '终端已结束，对话保存在电脑上');
        $('again').hidden = tool === 'shell';
        input.disabled = ended; sendButton.disabled = ended; $('voice').disabled = ended;
        drawPalette();
      }
      const text = payload.chunks.map(c => c.data).join('');
      const written = () => {
        initialized = true;
        if (!more) {
          const caughtUp = restoring, connected = live && !running;
          restoring = false; running = live;
          if (caughtUp) term.scrollToBottom();
          if (caughtUp || connected) layout();      // the size is settled once, not after every piece of output
          if (cut && running) redraw();
        } else if (!restoring) running = live;
        follow();
        if (bridge) bridge.rendered(String(payload.after), more ? 'more' : text || t.status === 'busy' ? 'active' : 'quiet');
      };
      if (text) term.write(text, written); else written();
    },
    error(message) { notice.textContent = message; status.textContent = '连接中断，正在重连'; dot.className = ''; running = false; },
    inputError(message, draft) {
      notice.textContent = message;
      // A typed message comes back for another try; a single key press does not.
      const typed = (draft || '').replace(/\x1b\[20[01]~/g, '').replace(/\r$/, '');
      if (typed && !input.value && !/[\x00-\x08\x0b-\x1f]/.test(typed)) { input.value = typed; grow(); }
    },
    // Spoken words are put into the message box to be checked before they are sent.
    dictated(text) {
      if (!text || ended) return;
      const gap = /[A-Za-z0-9]$/.test(input.value) && /^[A-Za-z0-9]/.test(text) ? ' ' : '';   // Chinese runs on without a space
      input.value = (input.value + gap + text).slice(0, 8000);
      grow(); drawPalette();
    },
    skins() { return Object.keys(SKINS).map(key => ({ key, name: SKINS[key].name + (SKINS[key].light ? '（明亮）' : ''), current: key === skin })); },
    spacings() { return Object.keys(SPACING).map(key => ({ key, name: SPACING[key][0], current: key === spacing })); },
    setSpacing(name) { if (!SPACING[name]) return; spacing = name; term.options.lineHeight = SPACING[name][1]; resize(); },
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
