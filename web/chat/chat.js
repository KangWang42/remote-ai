/* A conversation that the computer runs through its tool's structured mode (Claude Code's stream-json, Codex's
   app-server; docs/PROTOCOL.md, "Chats"). To the relay it is a terminal whose output is not a screen but the
   conversation's events, one JSON object a line: what is said as it is written, each step as it starts with what it
   prints and how it ended, what it asks, how it stands. This page draws them as a conversation and sends what is
   said, a stop and an answer straight to the tool. ../terminal/bridge.js keeps the connection, as for a terminal. */
(() => {
  'use strict';
  window.ChatPage = true;
  const $ = id => document.getElementById(id);
  const native = window.RemoteCliNative || null;
  const bridge = () => window.ProjectTerminal;
  const TOOLS = { claude: 'Claude Code', codex: 'Codex', agy: 'Antigravity CLI' };
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');

  // ---- the look: the app's, as on the list pages (app.js)
  const LOOK = ['skin', 'text', 'readfont', 'readsize', 'readline'];
  const saved = (key, fallback) => {
    if (native && native.pref && LOOK.includes(key)) { const chosen = native.pref(key); if (chosen) return chosen; }
    try { return localStorage.getItem('rcli-' + key) || fallback; } catch (error) { return fallback; }
  };
  const keep = (key, value) => {
    if (native && native.setPref && LOOK.includes(key)) native.setPref(key, value);
    try { localStorage.setItem('rcli-' + key, value); } catch (error) { /* private window */ }
  };
  const TEXT = { small: '14px', normal: '15.5px', large: '17px', larger: '19px' };
  const READ = {
    readfont: [{ sans: 'inherit', serif: '"Noto Serif SC", "Source Han Serif SC", "Songti SC", SimSun, serif', mono: 'ui-monospace, "Cascadia Mono", Consolas, monospace' }, 'sans', '--read-font'],
    readsize: [{ small: '.84rem', normal: '.95rem', large: '1.04rem', larger: '1.18rem' }, 'normal', '--read-size'],
    readline: [{ tight: '1.45', normal: '1.62', loose: '1.9' }, 'normal', '--read-line'] };
  function paint() {
    const skin = window.RemoteCliPaint(saved('skin', 'paper'));
    Object.keys(READ).forEach(key => { const [choices, usual, name] = READ[key]; document.documentElement.style.setProperty(name, choices[saved(key, usual)] || choices[usual]); });
    document.documentElement.style.fontSize = TEXT[saved('text', 'normal')] || TEXT.normal;
    const shade = (skin.chat && skin.chat.bg) || skin.bg || skin.t.background;
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = shade;
    if (native && native.chrome) native.chrome(shade);
  }
  if (window.RemoteCliOnSystemTheme) window.RemoteCliOnSystemTheme(paint);

  function el(tag, props, ...children) {
    const node = document.createElement(tag);
    Object.keys(props || {}).forEach(key => {
      if (key === 'html') node.innerHTML = props.html;
      else if (key.startsWith('aria')) node.setAttribute('aria-' + key.slice(4).toLowerCase(), props[key]);
      else node[key] = props[key];
    });
    children.forEach(child => { if (child !== '' && child != null) node.append(child); });
    return node;
  }
  let toastTimer = 0;
  function toast(text) {
    const box = $('toast');
    clearTimeout(toastTimer);
    box.textContent = text; box.hidden = !text;
    if (text) toastTimer = setTimeout(() => { box.hidden = true; }, 3200);
  }
  const base = path => String(path || '').split(/[\\/]/).filter(Boolean).pop() || '';
  // how long it has been at work, in words: 35 秒, 2 分 05 秒, 1 小时 03 分
  const worked = ms => {
    const s = Math.max(0, Math.floor(ms / 1000)), two = n => String(n).padStart(2, '0');
    return s >= 3600 ? Math.floor(s / 3600) + ' 小时 ' + two(Math.floor(s / 60) % 60) + ' 分' : s >= 60 ? Math.floor(s / 60) + ' 分 ' + two(s % 60) + ' 秒' : s + ' 秒';
  };
  // when a message was said: the hour on the day itself, the date before it on another
  const clockOf = at => {
    if (!at) return '';
    const d = new Date(at), now = new Date(), two = n => String(n).padStart(2, '0');
    const hour = two(d.getHours()) + ':' + two(d.getMinutes());
    return d.toDateString() === now.toDateString() ? hour : (d.getMonth() + 1) + '/' + d.getDate() + ' ' + hour;
  };
  const icon = (name, size) => RemoteSheet.ui.icon(name, size);

  // ---- Markdown, as the conversation window sets it: no links, pictures, forms or styles of its own
  let rich = null;
  const need = name => new Promise((done, failed) => document.head.append(el('script', { src: '../files/vendor/' + name, onload: done, onerror: failed })));
  function richText() {
    if (!rich) rich = Promise.all([need('marked.min.js'), need('purify.min.js')]).then(() => !!(window.marked && window.DOMPurify), () => false);
    return rich;
  }
  let canRich = false;
  function setText(node, text, writing) {
    reader.paint(node, () => renderText(node, text, writing));
  }
  function renderText(node, text, writing) {
    const mode = canRich && !(writing && text.length > 24000);
    if (node.readText === text && node.readRich === mode) return;
    node.readText = text; node.readRich = mode;
    // a long answer is set as plain text while it is written, and in full once it is
    if (!canRich || (writing && text.length > 24000)) { node.className = node.className.replace(/\bmd\b/, '').trim() + ' plain'; node.style.whiteSpace = 'pre-wrap'; node.textContent = text; return; }
    node.style.whiteSpace = '';
    node.innerHTML = DOMPurify.sanitize(marked.parse(text, { gfm: true, breaks: true }),
      { FORBID_TAGS: ['style', 'form', 'button', 'textarea', 'select', 'input', 'img', 'iframe', 'video', 'audio'], FORBID_ATTR: ['style', 'src'] });
    node.querySelectorAll('table').forEach(table => { const wrap = el('div', { className: 'wide' }); table.replaceWith(wrap); wrap.append(table); });
    // links are not followed from here: one to a file of the project opens it in the project's files, any other is its words
    node.querySelectorAll('a').forEach(a => {
      const path = inProject(a.getAttribute('href') || '');
      a.replaceWith(path ? fileRef(a.textContent, path) : el('span', { textContent: a.textContent }));
    });
    node.querySelectorAll(':not(pre) > code').forEach(code => {
      const path = namesFile(code.textContent) && inProject(code.textContent);
      if (!path) return;
      // an absolute path is shown as the project names it, with the line it points to
      if (/^([A-Za-z]:)?[\\/]/.test(code.textContent)) code.textContent = path + (/(:\d+){1,2}$/.exec(code.textContent) || [''])[0];
      code.classList.add('file-ref'); code.dataset.path = path; code.title = path;
      code.prepend(el('span', { className: 'file-mark', html: icon('file', 14) }));
    });
  }
  // Inline code that is a file: a path with a folder in it ('src/a.js:12'), or a bare name of a common kind of file
  // ('README.md'), but not the name of a library ('Node.js').
  const KINDS = /\.(md|txt|js|mjs|cjs|ts|tsx|jsx|py|r|rmd|qmd|ipynb|cs|java|kt|swift|go|rs|c|h|cpp|json|ya?ml|toml|csv|tsv|xlsx?|docx?|pptx?|pdf|html?|css|sh|ps1|bat|sql|xml|log|tex|bib)(:\d+){0,2}$/i;
  const namesFile = words => /^[^\s`'"<>|*?]+$/.test(words) && !/^[a-z][\w+.-]*:\/\//i.test(words) &&
    (/[\\/][^\\/]/.test(words) || (KINDS.test(words) && !/^[A-Z][a-z]+\.js$/.test(words)));
  // A path the answer names, as the project's files page finds it ('src/a.js'), or '' where it is not in the project.
  function inProject(given) {
    if (!terminal || !terminal.dir) return '';
    let path = String(given).trim().replace(/^file:\/\/\/?/, '').replace(/#L?\d.*$/, '').replace(/:\d+(:\d+)?$/, '').replace(/\\/g, '/');
    try { path = decodeURIComponent(path); } catch (error) { /* as written */ }
    if (!path || /^[a-z][\w+.-]*:\/\//i.test(path) || /^(mailto|tel|data|javascript):/i.test(path) || path.startsWith('#')) return '';
    const folder = String(terminal.folder || projectRoot).replace(/\\/g, '/').replace(/\/+$/, '');
    if (/^([A-Za-z]:)?\//.test(path)) {
      if (!folder || path.toLowerCase().indexOf(folder.toLowerCase() + '/') !== 0) return '';
      path = path.slice(folder.length + 1);
    }
    path = path.replace(/^\.\//, '');
    return path && !path.split('/').includes('..') && !path.endsWith('/') ? path : '';
  }
  // Where the project lies on the computer, from its list of projects: absolute paths in answers are read against it.
  let projectRoot = '';
  function knowRoot(data) {
    const found = data && terminal && terminal.dir ? ((data.device && data.device.projects) || []).find(p => p.name === terminal.dir) : null;
    if (!found || !found.path || found.path === projectRoot) return;
    projectRoot = found.path;
    reader.change(() => said.querySelectorAll('.text.md').forEach(node => {
      const text = node.readText;
      if (typeof text !== 'string') return;
      node.readText = null;
      renderText(node, text, node.classList.contains('writing'));
    }));
  }
  function fileRef(text, path) {
    const ref = el('span', { className: 'file-ref', title: path }, el('span', { className: 'file-mark', html: icon('file', 14) }), text || base(path));
    ref.dataset.path = path;
    return ref;
  }
  function openFile(path) {
    const at = path.lastIndexOf('/');
    location.href = '../files/?dir=' + encodeURIComponent(terminal.dir) + '&from=terminal' + (at > 0 ? '&path=' + encodeURIComponent(path.slice(0, at)) : '') + '&file=' + encodeURIComponent(path.slice(at + 1));
  }

  // ---- what was said: the items by their id, in the order they came
  const said = $('said');
  const reader = new RemoteConversation.Reader(said);
  const draft = new RemoteConversation.Draft(location.pathname + ':' + new URLSearchParams(location.search).get('id'));
  const pendingStore = new RemoteConversation.Draft(draft.key + ':delivery');
  let terminal = null, device = { online: true }, rest = '', items = new Map(), asks = new Map();
  let state = 'starting', since = 0, lastEnd = '', writing = '', lastError = '', drawn = false;
  const mine = new Map();        // what was written here and not given back yet, by the number it was sent under
  try {
    const pending = JSON.parse(pendingStore.get() || '[]');
    if (Array.isArray(pending)) pending.forEach(p => {
      if (p && /^[a-f0-9]{32}$/.test(p.id) && typeof p.text === 'string' && typeof p.draft === 'string' && p.draft.length <= 8000) {
        const pictures = Array.isArray(p.pictures) ? p.pictures.filter(name => RemoteAttach.NAME.test(name)).slice(0, RemoteAttach.MOST) : [];
        mine.set(p.id, { id: p.id, text: p.text, draft: p.draft, pictures, state: ['accepted', 'rejected'].includes(p.state) ? p.state : 'unknown', error: p.error || '', node: null });
      }
    });
  } catch (error) { /* an unreadable receipt does not prevent the conversation from loading */ }
  function saveMine() {
    const rows = Array.from(mine.values(), ({ id, text, draft, pictures, state, error }) => ({ id, text, draft, pictures, state, error }));
    pendingStore.set(rows.length ? JSON.stringify(rows) : '');
  }
  const OUT_MOST = 200000;

  function clear() {
    items = new Map(); asks = new Map(); rest = ''; writing = ''; lastEnd = '';
    said.replaceChildren();
    mine.forEach(p => { p.node = null; });
  }
  // What the user said: the words, then the pictures the computer named in lines of their own (ui/attach.js).
  const HANDOVER_SEP = '\n\n【现在的要求】\n';
  function shownUserText(text) {
    const raw = String(text || '');
    if (!raw.startsWith('【对话交接】')) return raw;
    const at = raw.indexOf(HANDOVER_SEP);
    return at >= 0 ? raw.slice(at + HANDOVER_SEP.length) : raw;
  }
  const shotsOf = names => RemoteAttach.shots(names, { base: '../', dir: terminal ? terminal.dir : '', from: 'terminal' });
  function userNode(text) {
    const { text: words, names } = RemoteAttach.split(text);
    if (!names.length) return el('div', { className: 'user', textContent: text });
    return el('div', { className: 'user with-shots' }, words ? el('span', { className: 'words', textContent: words }) : '', shotsOf(names));
  }
  // Under a message: when it was said, and a button that copies its words.
  function metaNode(text, at, mineSide) {
    const copy = el('button', { type: 'button', className: 'copy', ariaLabel: '复制', html: icon('copy', 16), onclick: async () => {
      const done = await RemoteSheet.copy(text);
      toast(done ? '已复制' : '没能复制，请长按选择文字');
    } });
    const when = clockOf(at);
    return el('div', { className: 'meta' + (mineSide ? ' mine' : '') }, mineSide && when ? el('time', { textContent: when }) : '', copy, !mineSide && when ? el('time', { textContent: when }) : '');
  }
  // The answer of a turn ends with its meta line: once, after the last words the turn said.
  function endTurn(item) {
    if (!item || !item.node || item.metaNode || !item.text.trim()) return;
    item.metaNode = metaNode(item.text, item.at, false);
    item.node.after(item.metaNode);
  }

  // ---- the steps and thoughts between the words: one row each in a list, its icon saying what kind of step it is.
  // A row at work stands open with what it prints; a finished one is its name only, opened by a tap.
  const STEP_ICONS = [
    [/^(Read|NotebookRead|读取)$/, 'eye'], [/^(Edit|MultiEdit|Write|NotebookEdit|edit|修改)$/, 'pen'],
    [/^(Grep|Glob|LS|search|WebSearch|搜索)$/, 'search'], [/^WebFetch$/, 'globe'], [/^TodoWrite$/, 'many'],
    [/^(Bash|BashOutput|KillShell|KillBash|shell|运行)$/, 'term']];
  const STEP_WORDS = { Read: '读取', NotebookRead: '读取', Edit: '修改', MultiEdit: '修改', Write: '写入', NotebookEdit: '修改', Grep: '搜索', Glob: '查找文件', LS: '列出', WebSearch: '搜索网页', WebFetch: '打开网页', Bash: '运行', TodoWrite: '更新待办' };
  function stepLook(item) {
    const named = /^([A-Za-z_]+|读取|修改|搜索|运行)：/.exec(item.text);
    const prefix = named ? named[1] : '';
    const kind = [item.name, prefix].reduce((got, name) => got || (name && (STEP_ICONS.find(([test]) => test.test(name)) || [])[1]), '');
    const words = STEP_WORDS[prefix] ? STEP_WORDS[prefix] + ' ' + item.text.slice(prefix.length + 1) : item.text;
    return { icon: kind || (item.detail ? 'term' : 'spark'), words };
  }
  // A step or thought goes into the list just above it, or begins one.
  function intoSteps(parent, node) {
    let list = parent.lastElementChild;
    if (!list || !list.classList.contains('steps')) { list = el('div', { className: 'steps' }); parent.append(list); }
    list.append(node);
  }
  const chev = () => el('span', { className: 'chev', ariaHidden: 'true', html: icon('chev', 14) });
  // Successful steps collapse by default; explicit reading choices survive subsequent output.
  function stepNode(item) {
    const look = stepLook(item);
    const what = el('span', { className: 'what', textContent: look.words }), more = el('span', { className: 'more' });
    const head = el('button', { type: 'button', ariaExpanded: 'true' }, el('span', { className: 'mark', ariaHidden: 'true', html: icon(look.icon, 16) }), what, more, chev());
    // (the command is shown where the step is named by what it is for; a path it is named by is not said twice)
    const code = el('code', { textContent: item.detail || '', hidden: !item.detail || item.text.includes(item.detail) });
    const out = el('pre', { hidden: true });
    const node = el('div', { className: 'step run' }, head, code, out);
    node.dataset.kind = look.icon;
    head.onclick = () => { item.open = !stepOpen(item); reader.change(() => drawStep(item)); };
    Object.assign(item, { node, what, more, code, outNode: out, out: item.out || '' });
    return node;
  }
  const stepOpen = item => item.open === undefined ? item.state !== 'ok' : item.open;
  function drawStep(item) {
    const output = item.out.replace(/\s+$/, '');
    const open = stepOpen(item), end = item.outNode.scrollHeight - item.outNode.scrollTop - item.outNode.clientHeight < 40;
    item.node.className = 'step ' + item.state + (open ? '' : ' shut');
    item.node.firstChild.setAttribute('aria-expanded', String(open));
    item.outNode.hidden = !open || !output;
    reader.paint(item.outNode, () => { if (item.outNode.textContent !== output) item.outNode.textContent = output; });
    item.more.textContent = item.state === 'fail' ? '未完成' : '';
    if (end && !reader.selected(item.outNode) && item.state === 'run' && !item.outNode.hidden) item.outNode.scrollTop = item.outNode.scrollHeight;
  }
  // A thought: open while it is written, then its first line, opened by a tap.
  function thoughtNode(item) {
    const what = el('span', { className: 'what' });
    const head = el('button', { type: 'button', ariaExpanded: 'false' }, el('span', { className: 'mark', ariaHidden: 'true', html: icon('think', 16) }), what, chev());
    const node = el('div', { className: 'thought' }, head);
    head.onclick = () => { item.open = !thoughtOpen(item); reader.change(() => drawThought(item)); };
    Object.assign(item, { node, what });
    drawThought(item);
    return node;
  }
  const thoughtOpen = item => item.open === undefined ? !!item.id && item.id === writing : item.open;
  function drawThought(item) {
    const open = thoughtOpen(item), words = item.text.trim();
    item.node.hidden = !words;
    item.node.classList.toggle('shut', !open);
    item.node.firstChild.setAttribute('aria-expanded', String(open));
    reader.paint(item.what, () => { if (item.what.textContent !== words) item.what.textContent = words; });
  }
  let lastWords = null;      // the last words of the turn at work, which get its meta line when it ends
  function add(e) {
    let node;
    const item = { id: e.id, kind: e.kind, text: e.text || '', detail: e.detail || '', name: e.name || '', at: e.at || 0, state: 'run', out: '' };
    if (e.kind === 'user') {
      // what this page sent comes back from the tool: the bubble(s) that waited for it are that message now
      const ackOps = Array.isArray(e.ops) && e.ops.length ? e.ops : (e.op ? [e.op] : []);
      let changed = false;
      ackOps.forEach(op => {
        const waiting = op && mine.get(op);
        if (waiting) { acknowledge(waiting); mine.delete(op); changed = true; if (waiting.node) waiting.node.remove(); }
      });
      if (changed) saveMine();
      endTurn(lastWords); lastWords = null;
      item.shown = shownUserText(item.text);
      node = userNode(item.shown);
    } else if (e.kind === 'text') {
      node = el('div', { className: 'text md' });
      setText(node, item.text, true);
      lastWords = item;
    } else if (e.kind === 'thought') {
      node = thoughtNode(item);
    } else if (e.kind === 'step') {
      node = stepNode(item);
      drawStep(item);
    } else {
      node = el('div', { className: 'note', textContent: item.text });
    }
    item.node = item.node || node;
    items.set(e.id, item);
    if (e.kind === 'step' || e.kind === 'thought') intoSteps(said, node);
    else said.append(node);
    if (e.kind === 'user') said.append(metaNode(item.shown, item.at, true));
    if (e.kind === 'text' || e.kind === 'thought') write(e.id);
    return item;
  }
  // the item that is being written: marked so, until another one is, or the turn ends
  function write(id) {
    const before = items.get(writing);
    writing = id;
    if (before && before.node) {
      before.node.classList.remove('writing');
      if (before.kind === 'text') setText(before.node, before.text, false);
      else if (before.kind === 'thought') drawThought(before);
    }
    const now = items.get(id);
    if (now && now.node) now.node.classList.add('writing');
  }
  // what came for an item this page has not seen (its beginning was before what the relay still keeps)
  const found = (id, kind) => items.get(id) || add({ id, kind, text: '' });
  const dirty = new Set();
  let frame = 0;
  function later(item) { dirty.add(item); if (!frame) frame = requestAnimationFrame(redraw); }
  function redraw() {
    frame = 0;
    reader.change(() => { dirty.forEach(item => {
      if (item.kind === 'step') drawStep(item);
      else if (item.kind === 'text') setText(item.node, item.text, item.id === writing);
      else if (item.kind === 'thought') drawThought(item);
      else item.node.textContent = item.text;
    }); });
    dirty.clear();
  }
  function earlier(messages, more) {
    // what was said before it was gone on with here: above, as the conversation's window shows it
    const box = el('div', { className: 'before' });
    box.style.display = 'contents';
    if (more) box.append(el('p', { className: 'earlier', textContent: '更早的内容在电脑上' }));
    let words = null;      // the last answer before the next message, which gets the meta line
    const close = () => { if (words) words.node.after(metaNode(words.text, 0, false)); words = null; };
    (messages || []).forEach(m => {
      if (m.role === 'user') { const shown = shownUserText(m.text); close(); box.append(userNode(shown), metaNode(shown, 0, true)); }
      else if (m.role === 'switch') {
        close();
        const t = m.tool || '';
        const mark = ['claude', 'codex', 'agy'].includes(t) ? el('span', { className: 'switch-mark tool-' + t, html: icon(t, 14), ariaHidden: 'true' }) : '';
        box.append(el('p', { className: 'divide switch' }, mark, el('span', { textContent: '以下由 ' + (m.text || TOOLS[t] || t) + ' 继续' })));
      }
      else if (m.role === 'assistant') { const node = el('div', { className: 'text md' }); setText(node, m.text, false); box.append(node); if (m.text.trim()) words = { node, text: m.text }; }
      else if (m.role === 'thought') intoSteps(box, thoughtNode({ kind: 'thought', text: m.text || '' }));
      else if (m.role === 'tool') {
        const item = { kind: 'step', text: m.text, detail: m.detail || '', state: 'ok', out: m.result || '' };
        intoSteps(box, stepNode(item)); drawStep(item);
      }
    });
    close();
    if (messages && messages.length && messages[messages.length - 1].role !== 'switch') box.append(el('p', { className: 'divide', textContent: '在手机上继续' }));
    said.prepend(box);
  }
  function apply(e) {
    switch (e.t) {
      case 'state': state = e.state; since = e.at || Date.now(); if (state !== 'busy' && state !== 'confirm') write(''); break;
      case 'history': earlier(e.messages, e.more); break;
      case 'item': add(e); break;
      case 'more': { const item = found(e.id, 'text'); item.text += e.text; if (writing !== e.id) write(e.id); later(item); break; }
      case 'set': { const item = found(e.id, 'text'); item.text = e.text; later(item); break; }
      case 'out': {
        const item = found(e.id, 'step');
        item.out += e.text;
        if (item.out.length > OUT_MOST) item.out = '…\n' + item.out.slice(-OUT_MOST);
        later(item); break;
      }
      case 'done': {
        const item = found(e.id, 'step');
        item.state = e.ok ? 'ok' : 'fail';
        if (e.result && !item.out) item.out = e.result;
        later(item); break;
      }
      case 'ask': asks.set(e.id, e); drawAsks(); break;
      case 'answered': asks.delete(e.id); drawAsks(); break;
      case 'turn':
        write('');
        endTurn(lastWords); lastWords = null;
        lastEnd = e.end;
        if (e.end === 'stopped') said.append(el('div', { className: 'note', textContent: '已停止' }));
        else if (e.end === 'failed') said.append(el('div', { className: 'note bad', textContent: e.error ? '没有完成：' + e.error : '没有完成' }));
        break;
      case 'note': said.append(el('div', { className: 'note', textContent: e.text })); break;
    }
  }

  // ---- what it asks, above the box
  const answering = new Set();
  function drawAsks() {
    const box = $('asks');
    box.replaceChildren(...Array.from(asks.values()).map(ask => {
      const answers = el('div', { className: 'answers' });
      (ask.options || []).forEach(option => answers.append(el('button', {
        type: 'button', textContent: option.label, disabled: answering.has(ask.id),
        className: option.id === 'allow' || option.id.startsWith('choice:') ? 'yes' : option.id === 'deny' ? 'no' : '',
        onclick: () => {
          answering.add(ask.id);
          answers.querySelectorAll('button').forEach(b => { b.disabled = true; });
          bridge().send({ action: 'answer', ask: ask.id, decision: option.id });
        } })));
      return el('div', { className: 'ask' }, el('b', { textContent: ask.title || '请确认' }), ask.text ? el('pre', { textContent: ask.text }) : '', answers);
    }));
    box.hidden = !asks.size;
    stands();
  }

  // ---- how it stands, in one line above the box: at work and for how long, asking, waiting, ended
  function stands() {
    const ended = terminal && terminal.state === 'closed', line = $('stands');
    const kind = ended ? 'ended' : !device.online ? 'off' : terminal && terminal.state === 'starting' ? 'starting' : state;
    line.className = kind;
    $('standing').textContent = { ended: '对话已结束', off: '电脑离线', starting: '正在启动', busy: '已工作', confirm: '等你确认', idle: lastEnd === 'stopped' ? '已停止' : '等你的消息' }[kind] || '';
    $('clock').textContent = kind === 'busy' && since ? worked(Date.now() - since) : '';
    // the round button at the end: a stop while it works and nothing is written, the arrow otherwise
    const working = kind === 'busy' || kind === 'confirm', typed = !!$('box').value.trim() || !!(picker && picker.count);
    $('stop').hidden = !working;
    $('send').hidden = working && !typed && !sendingPictures && !handingOver;
    $('send').classList.toggle('ready', typed);
    $('again').hidden = !(ended && terminal.session);
    drawParams(ended);
    $('write').hidden = !!ended;
    $('go').hidden = !(kind === 'idle' && (lastEnd === 'stopped' || lastEnd === 'failed') && !typed && !pendingSwitch);
    $('send').disabled = kind === 'off' || handingOver;
    $('send').classList.toggle('going', sendingPictures || handingOver);
  }
  // The model button at the end of the box: the active (or staged) tool's icon, tinted when customized or switching AI.
  let paramsDrawn = '', pendingSwitch = null, handingOver = false;
  function setPendingSwitch(next) {
    pendingSwitch = next && terminal && next.tool && next.tool !== terminal.tool ? next : null;
    stands();
    if (pendingSwitch) setTimeout(() => box.focus(), 60);
  }
  function drawParams(ended) {
    const button = $('params-btn'), bar = $('switch-bar');
    const baseTool = terminal && ['claude', 'codex', 'agy'].includes(terminal.tool) ? terminal.tool : '';
    const tool = pendingSwitch && !ended ? pendingSwitch.tool : baseTool;
    button.hidden = !tool || ended;
    if (bar) {
      const showBar = !!(pendingSwitch && !ended);
      bar.hidden = !showBar;
      if (showBar) {
        const d = window.RemoteTeamDefaults;
        const mLabel = pendingSwitch.model ? (d ? d.modelLabel(pendingSwitch.tool, pendingSwitch.model, modelCatalog) : pendingSwitch.model) : '';
        bar.className = 'tool-' + pendingSwitch.tool;
        $('switch-bar-icon').innerHTML = icon(pendingSwitch.tool, 16);
        $('switch-bar-text').textContent = '下一条由 ' + (TOOLS[pendingSwitch.tool] || pendingSwitch.tool) + (mLabel ? ' · ' + mLabel : '') + ' 继续';
      }
      box.placeholder = showBar ? '发消息，由 ' + (TOOLS[pendingSwitch.tool] || pendingSwitch.tool) + ' 继续' : '发消息';
    }
    if (button.hidden) return;
    const label = pendingSwitch ? ('下一条由 ' + (TOOLS[pendingSwitch.tool] || pendingSwitch.tool) + ' 继续') : paramsSummary();
    button.className = 'tool-' + tool + (label || pendingSwitch ? ' custom' : '');
    button.setAttribute('aria-label', (TOOLS[tool] || tool) + ' · ' + (label || '默认模型'));
    button.title = label || TOOLS[tool];
    if (paramsDrawn !== tool) { $('params-icon').innerHTML = icon(tool, 18); paramsDrawn = tool; }
  }
  function teamUrl(teamId) {
    const fromBench = new URLSearchParams(location.search).get('from') === 'bench';
    return '../?tab=together&do=team&item=' + encodeURIComponent(teamId) + (fromBench ? '&from=bench' : '');
  }
  function openTeam(teamId) {
    if (!teamId) return;
    location.href = teamUrl(teamId);
  }
  function header() {
    if (!terminal) return;
    $('title').textContent = terminal.title || TOOLS[terminal.tool] || '对话';
    document.title = terminal.title || 'Remote AI';
    const d = window.RemoteTeamDefaults, model = terminal.model ? (d ? d.modelLabel(terminal.tool, terminal.model, modelCatalog) : terminal.model) : '';
    $('where-text').textContent = [TOOLS[terminal.tool] || terminal.tool, model, terminal.dir || base(terminal.folder)].filter(Boolean).join(' · ');
    $('dot').className = device.online ? 'on' : 'off';
    $('files').hidden = !terminal.dir;
    const teamBar = $('team-bar');
    if (teamBar) {
      teamBar.hidden = !terminal.team;
      if (terminal.team) $('team-bar-text').textContent = '协作任务进行中 · 当前对话已加入协作';
    }
    canPicture();
    ensureModelCatalog();
    if (terminal.error && terminal.error !== lastError) { lastError = terminal.error; if (drawn) toast(terminal.error); }
  }
  let modelCatalog = null, modelCatalogAsked = false;
  function ensureModelCatalog() {
    if (modelCatalogAsked || !terminal) return;
    modelCatalogAsked = true;
    work.ensure().then(() => {
      stands();
      if (window.RemoteRestart && window.RemoteRestart.models && work.has('teams-models')) {
        return window.RemoteRestart.models(work).then(cat => { modelCatalog = cat; header(); stands(); });
      }
    }, () => { modelCatalogAsked = false; });
  }
  function paramsSummary() {
    if (!terminal) return '';
    const d = window.RemoteTeamDefaults;
    const parts = [];
    if (terminal.model) parts.push(d ? d.modelLabel(terminal.tool, terminal.model, modelCatalog) : terminal.model);
    if (terminal.effort) parts.push('思考 ' + (d ? d.shortEffortLabel(terminal.effort) : terminal.effort));
    if (terminal.mode && terminal.mode !== 'full' && terminal.tool !== 'agy') parts.push(d ? d.modeLabel(terminal.mode, true) : terminal.mode);
    return parts.join(' · ');
  }

  // ---- reading: the stay at the end while one is there
  const atEnd = () => reader.atEnd();
  const toEnd = () => { said.scrollTop = said.scrollHeight; $('latest').hidden = true; };
  said.addEventListener('scroll', () => { $('latest').hidden = atEnd(); }, { passive: true });
  said.addEventListener('click', event => {
    const ref = event.target.closest('.file-ref');
    if (ref && !reader.selected(ref)) openFile(ref.dataset.path || ref.title);
  });
  $('latest').onclick = () => said.scrollTo({ top: said.scrollHeight, behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });

  // ---- what the bridge gives: the terminal, and the pieces of its output
  function receive(item) {
    reader.change(() => {
    if (item.device) device = item.device;
    if (item.reset) clear();
    terminal = item.terminal || terminal;
    (item.chunks || []).forEach(chunk => {
      rest += chunk.data;
      let at;
      while ((at = rest.indexOf('\n')) >= 0) {
        const line = rest.slice(0, at); rest = rest.slice(at + 1);
        if (!line.trim()) continue;
        let e;
        try { e = JSON.parse(line); } catch (error) { continue; }
        apply(e);
      }
    });
    redraw();
    mine.forEach(p => { if (!p.node) { p.node = bubble(p); said.append(p.node); } });
    header(); stands();
    if (!drawn && !said.children.length) said.append(el('p', { className: 'empty', textContent: '发一句话开始' }));
    else if (said.children.length > 1) said.querySelectorAll('.empty').forEach(node => node.remove());
    drawn = true;
    }, !drawn);
  }

  // ---- what is written here
  const box = $('box');
  box.value = draft.get();
  function bubble(p) {
    const labels = { queued: '正在送达', retrying: '连接中断，正在确认送达', accepted: '已提交，等待对话接收', unknown: '送达结果未知，请先检查对话', rejected: '没有送出：' + (p.error || '') };
    const pictures = p.pictures || [];
    const node = el('div', { className: 'user going' + (pictures.length ? ' with-shots' : '') + (p.state === 'rejected' || p.state === 'unknown' ? ' failed' : '') },
      pictures.length ? (p.text ? el('span', { className: 'words', textContent: p.text }) : '') : p.text,
      pictures.length ? shotsOf(pictures) : '',
      el('small', { textContent: labels[p.state] || labels.queued }));
    if (p.state === 'rejected' || p.state === 'unknown') node.append(el('button', { type: 'button', textContent: p.state === 'unknown' ? '检查后重新编辑' : '重新编辑', onclick: () => {
      const restore = () => {
        if ((box.value.trim() && box.value !== p.draft) || (p.pictures && p.pictures.length && picker.count)) return toast('输入框里还有未发送的内容，请先处理');
        box.value = p.draft; draft.set(box.value); grow(); box.focus(); p.node.remove(); mine.delete(p.id); saveMine();
        if (p.pictures && p.pictures.length) picker.restore(p.pictures);
        stands();
      };
      if (p.state === 'unknown') RemoteSheet.confirm({ title: '重新编辑这条消息', text: '电脑可能已经收到，请先检查对话。再次发送会作为一条新消息。', label: '已检查，重新编辑', act: sheet => { sheet.close(true); restore(); } });
      else restore();
    } }));
    return node;
  }
  function acknowledge(p) {
    draft.clear(p.draft);
    if (box.value === p.draft) { box.value = ''; grow(); }
  }
  const sameMessage = (p, text, pictures) => p.text === text && (p.pictures || []).join() === pictures.join();
  function say(text, paths) {
    const original = text, attachments = paths || [], pictures = attachments.map(path => path.split('/').pop());
    text = text.trim();
    if ((!text && !attachments.length) || !terminal || terminal.state === 'closed') return false;
    if (Array.from(mine.values()).some(p => sameMessage(p, text, pictures))) { toast('这条消息正在确认送达，请先查看它的状态'); return false; }
    const p = { id: newId(), text, draft: original, pictures, state: 'queued' };
    draft.set(box.value);
    mine.set(p.id, p);
    saveMine();
    said.querySelectorAll('.empty').forEach(node => node.remove());
    p.node = bubble(p); said.append(p.node); toEnd();
    const op = { id: p.id, action: 'say', text };
    if (attachments.length) op.attachments = attachments;
    bridge().send(op);
    return true;
  }
  const grow = () => {
    const prevScroll = box.scrollTop;
    box.style.height = 'auto';
    box.style.height = Math.min(160, box.scrollHeight + 2) + 'px';
    if (prevScroll && box.scrollTop !== prevScroll) box.scrollTop = prevScroll;
    stands();
  };
  box.addEventListener('input', () => { draft.set(box.value); grow(); });
  box.addEventListener('focus', () => {
    const keepEnd = atEnd();
    setTimeout(() => {
      if (document.activeElement === box && keepEnd) toEnd();
    }, 220);
  });
  if (window.visualViewport) {
    window.visualViewport.addEventListener('resize', () => {
      if (document.activeElement === box && atEnd()) toEnd();
    }, { passive: true });
  }
  box.addEventListener('keydown', event => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) { event.preventDefault(); $('write').requestSubmit(); } });
  // The picture button, shown where the computer takes pictures with a message and the conversation is in a project.
  let sendingPictures = false, pictureAsked = false;
  const picker = new RemoteAttach.Picker({ form: $('write'), before: $('box'), base: '../', dir: () => terminal ? terminal.dir : '', toast, changed: () => stands() });
  function canPicture() {
    if (pictureAsked || !terminal) return;
    pictureAsked = true;
    work.ensure().then(data => { knowRoot(data); picker.enabled = !!(data && terminal.dir && (data.device.features || []).includes('attachments')); }, () => { pictureAsked = false; });
  }
  async function handoverSend(words, target) {
    if (!words || !terminal || terminal.state === 'closed') return;
    handingOver = true; stands();
    try {
      const payload = { action: 'start', tool: target.tool, dir: terminal.dir, form: 'chat', from: terminal.session, from_tool: terminal.tool, text: words };
      if (target.model) payload.model = target.model;
      if (target.effort) payload.effort = target.effort;
      const started = await work.terminal(payload);
      draft.clear(box.value);
      pendingSwitch = null;
      const tail = bridge().teamQuery ? bridge().teamQuery() : (new URLSearchParams(location.search).get('from') === 'bench' ? '&from=bench' : '');
      location.replace('?id=' + encodeURIComponent(started.terminal) + tail);
    } catch (error) {
      toast(error.message || '切换失败');
    } finally {
      handingOver = false; stands();
    }
  }
  $('write').addEventListener('submit', async event => {
    event.preventDefault();
    if (sendingPictures || handingOver) return;
    if (!box.value.trim() && !picker.count) return box.focus();
    if (pendingSwitch && terminal && terminal.state !== 'closed') {
      if (picker.count) return toast('换 AI 的第一条消息暂不支持图片，请先发送文字');
      if (state === 'busy' || state === 'confirm') return toast('请等这一轮结束后再切换 AI');
      return handoverSend(box.value.trim(), pendingSwitch);
    }
    if (!picker.count) { say(box.value); grow(); return; }
    if (!terminal || terminal.state === 'closed') return;
    if (Array.from(mine.values()).some(p => sameMessage(p, box.value.trim(), picker.names))) return toast('这条消息正在确认送达，请先查看它的状态');
    sendingPictures = true; stands();
    try {
      const paths = await picker.upload(done => { $('send-done').hidden = false; $('send-done').textContent = done + '%'; });
      if (say(box.value, paths)) picker.clear();
      grow();
    } catch (error) { toast(error.message); }
    finally { sendingPictures = false; $('send-done').hidden = true; stands(); }
  });
  if ($('switch-cancel')) $('switch-cancel').onclick = () => setPendingSwitch(null);
  $('go').onclick = () => say('继续');
  $('stop').onclick = () => { $('stop').disabled = true; bridge().send({ action: 'stop' }); setTimeout(() => { $('stop').disabled = false; }, 2500); };
  $('again').onclick = () => bridge().again();
  $('back').onclick = () => bridge().close();
  $('files').onclick = () => { if (terminal && terminal.dir) location.href = '../files/?dir=' + encodeURIComponent(terminal.dir) + '&from=terminal'; };
  $('params-btn').onclick = () => {
    if (!terminal) return;
    if (!device.online) return toast('电脑离线');
    work.ensure();
    RemoteSheet.open(RemoteSwitch.page(work, of(), {
      reopen: choice => reopen('chat', choice),
      stage: setPendingSwitch,
      pending: pendingSwitch,
      busy: () => state === 'busy' || state === 'confirm' ? '这一轮结束后再切换' : ''
    }), '切换 AI 与模型');
  };
  if ($('team-bar')) $('team-bar').onclick = () => { if (terminal && terminal.team) openTeam(terminal.team); };

  // The same conversation in a new terminal of the phone's: closed here, and gone on with there once the computer has
  // let it go. `form`: 'chat' to go on with it as a conversation (restarted: its tool started anew, `more` the model,
  // effort and permission mode it goes on with), '' in a terminal's screen.
  async function reopen(form, more = {}) {
    const { tool, dir, session, id: was } = terminal;
    const tail = () => bridge().teamQuery ? bridge().teamQuery() : (new URLSearchParams(location.search).get('from') === 'bench' ? '&from=bench' : '');
    // shown the other way as it is: the computer ends this one and goes on with it in one step (`reopen`)
    if (!Object.keys(more).length && form !== 'chat' && session && terminal.state !== 'closed' && work.has('reopen') && (tool === 'claude' || tool === 'codex')) {
      const { status, body } = await bridge().post({ action: 'start', id: newId(), tool, dir, reopen: was });
      if (status !== 200) throw new Error(body.error || '没有打开');
      location.replace('../terminal/?id=' + body.terminal + tail());
      return;
    }
    if (terminal.state !== 'closed') bridge().send({ action: 'close' });
    for (let n = 0; n < 40; n++) {
      await new Promise(done => setTimeout(done, 500));
      try {
        const list = await (await fetch('../api/terminal', { credentials: 'same-origin', cache: 'no-store' })).json();
        const listed = session ? (list.sessions || []).find(s => s.id === session && s.tool === tool) : null;
        const open = (list.terminals || []).some(t => t.id === was && (t.state === 'running' || t.state === 'starting'));
        if (!open && (!session || !listed || !listed.live)) break;
      } catch (error) { /* asked again */ }
    }
    const start = Object.assign({ action: 'start', id: newId(), tool, dir, history: false }, session ? { session } : {}, form ? { form } : {}, more);
    const { status, body } = await bridge().post(start);
    if (status !== 200) throw new Error(body.error || '没有打开');
    location.replace((form === 'chat' ? '?id=' : '../terminal/?id=') + body.terminal + tail());
  }
  async function inTerminal() {
    toast('正在转到终端…');
    try { await reopen(''); } catch (error) { toast(error.message); }
  }
  // ---- the menu: what the conversation is, what is done with it (handed to another AI, asked at a time), its
  // name, and how it is read. Each opens as a page of the same sheet (../ui/sheet.js, ../ui/work.js).
  const work = RemoteWork.forPage('../', toast);
  const skinList = () => {
    const cur = saved('skin', 'paper');
    const items = Object.keys(window.RemoteCliSkins).map(key => { const k = window.RemoteCliSkins[key]; return { key, name: k.name, bg: (k.chat && k.chat.bg) || k.bg || k.t.background, bubble: (k.chat && k.chat.userBg) || k.accentSoft, termBg: k.t.background, ink: k.t.foreground, muted: k.muted, accent: k.accent, current: key === cur }; });
    items.unshift({ key: 'auto', name: '跟随系统', follow: true, current: cur === 'auto' });
    return items;
  };
  const READING = {
    text: ['字号', [['small', '小'], ['normal', '标准'], ['large', '大'], ['larger', '特大']], 'normal'],
    readline: ['行距', [['tight', '紧凑'], ['normal', '标准'], ['loose', '宽松']], 'normal'],
    readfont: ['字体', [['sans', '默认'], ['serif', '衬线'], ['mono', '等宽']], 'sans'] };
  const STATE = { busy: ['正在执行', 'var(--busy)'], confirm: ['等你确认', 'var(--bad)'], starting: ['正在启动', 'var(--accent)'], idle: ['等你的消息', 'var(--good)'], ended: ['已结束', 'var(--muted)'] };
  const of = () => ({ id: terminal.session || '', tool: terminal.tool, title: terminal.title || TOOLS[terminal.tool], dir: terminal.dir || '', terminal: terminal.id, model: terminal.model || '', effort: terminal.effort || '', mode: terminal.mode || '' });
  function menuPage() {
    const { ui } = RemoteSheet;
    return { title: '对话', build(p) {
      const ended = terminal.state === 'closed', kind = ended ? 'ended' : terminal.state === 'starting' ? 'starting' : state;
      const [standing, colour] = STATE[kind] || STATE.idle;
      p.body.append(ui.who({ icon: ['claude', 'agy'].includes(terminal.tool) ? terminal.tool : 'codex', tint: ['claude', 'agy'].includes(terminal.tool) ? terminal.tool : 'codex', title: terminal.title || TOOLS[terminal.tool] || '对话',
        line: (TOOLS[terminal.tool] || terminal.tool) + (terminal.dir ? ' · ' + terminal.dir : '') + ' · ' + (device.online ? standing : '电脑离线'), state: device.online ? colour : 'var(--bad)' }));
      const noSession = terminal.session ? '' : '发出第一条消息后才能使用';
      p.body.append(ui.tiles([
        { id: 'menu-handoff', icon: 'hand', tint: 'claude', label: '交给 AI', why: noSession, say: toast, act: () => work.handoff(p.sheet, of()) },
        { id: 'menu-schedule', icon: 'clock', tint: 'time', label: '定时提问', why: noSession || (ended ? '对话已结束，先继续这段对话' : ''), say: toast, act: () => work.schedules(p.sheet, of()) },
        { id: 'menu-files', icon: 'folder', tint: 'file', label: '项目文件', why: terminal.dir ? '' : '这段对话不在项目里', say: toast, act: () => { p.sheet.close(null, true); location.href = '../files/?dir=' + encodeURIComponent(terminal.dir) + '&from=terminal'; } },
        { id: 'menu-copy', icon: 'copy', tint: 'copy', label: '复制全文', say: toast, act: async () => { p.sheet.close(null); toast(await RemoteSheet.copy(said.innerText.trim()) ? '已复制整段对话' : '没能复制，请长按选择文字'); } }]));
      const rows = [];
      if (terminal.team) {
        rows.push(ui.row({ icon: 'relay', tint: 'agy', id: 'menu-team', label: '查看协作任务', value: '已加入', go: () => { p.sheet.close(null, true); openTeam(terminal.team); } }));
      } else {
        const promoteWhy = !terminal.dir ? '这段对话不在项目里'
          : ended ? '对话已结束，先继续这段对话'
          : (state === 'busy' || state === 'confirm') ? '请等本轮对话结束后再转为协作'
          : '';
        rows.push(ui.row({
          icon: 'spark', tint: 'agy', id: 'menu-promote', label: '转为协作',
          detail: promoteWhy || '保留上下文，原地升级为多 AI 协作',
          go: () => {
            if (promoteWhy) return toast(promoteWhy);
            p.sheet.push(work.promotePage(of(), team => { if (team && team.id) openTeam(team.id); }));
          }
        }));
      }
      if (!ended && (work.has('teams-models') || work.has('chat-params'))) {
        rows.push(ui.row({ icon: 'sliders', tint: 'codex', id: 'menu-params', label: '模型与参数', value: paramsSummary() || '默认', go: () => p.sheet.push(RemoteRestart.paramsPage(work, of(), choice => reopen('chat', choice))) }));
      }
      rows.push(ui.row({ icon: 'pen', tint: 'muted', label: '重命名', value: terminal.title || '', go: () => p.sheet.push(renamePage()) }));
      if (terminal.session) rows.push(ui.row({ icon: 'refresh', tint: 'muted', label: '重启对话', go: () => p.sheet.push(RemoteRestart.page(work, of(), choice => reopen('chat', choice))) }));
      if (terminal.session && terminal.dir && !ended) rows.push(ui.row({ icon: 'term', tint: 'muted', label: '在终端界面中继续', go: () => p.sheet.push(ui.confirm({ title: '在终端界面中继续', text: '同一段对话改在 ' + (TOOLS[terminal.tool] || terminal.tool) + ' 自己的终端界面里继续，之后可以再换回对话。', label: '在终端界面中继续', busy: '正在转到终端…', act: async sheet => { sheet.close(null, true); await inTerminal(); } })) }));
      if (ended && terminal.session) rows.push(ui.row({ icon: 'play', tint: 'good', label: '继续这段对话', act: () => { p.sheet.close(null, true); bridge().again(); } }));
      p.body.append(ui.section('对话', ui.group(...rows)));
      const chosenSkin = saved('skin', 'paper');
      const skin = window.RemoteCliSkins[chosenSkin] || window.RemoteCliSkins.paper;
      p.body.append(ui.section('阅读', ui.group(ui.row({ icon: 'look', tint: 'look', label: '外观', value: chosenSkin === 'auto' ? '跟随系统' : skin.name, go: () => p.sheet.push(lookPage()) }),
        ...Object.keys(READING).map(key => { const [name, choices, usual] = READING[key]; return ui.row({ label: name, end: ui.seg(choices, saved(key, usual), value => { keep(key, value); reader.change(paint); }, { label: name }) }); }))));
      if (!ended) p.body.append(ui.group(ui.row({ label: '结束对话', danger: true, act: () => p.sheet.push(ui.confirm({ title: '结束对话', text: '正在执行的会被中断。对话保存在电脑上，之后可以继续。', label: '结束对话', kind: 'danger', act: sheet => { sheet.close(null); bridge().send({ action: 'close' }); } })) })));
    } };
  }
  function renamePage() {
    const { ui } = RemoteSheet;
    return { title: '重命名', build(p) {
      const box = ui.input({ id: 'chat-name', value: terminal.title || '', max: 60, label: '对话名称' });
      setTimeout(() => { box.focus(); box.select(); }, 280);
      const save = ui.go('保存', async () => {
        const title = box.value.trim();
        if (!title) throw new Error('请写下名称');
        const { status, body } = await bridge().post({ action: 'rename', id: newId(), terminal: terminal.id, title });
        if (status !== 200) throw new Error(body.error || '没有保存');
        terminal.title = title; header();
        p.sheet.goBack();
      }, { busy: '正在保存…', id: 'rename-save' });
      box.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); save.button.click(); } });
      p.body.append(box);
      p.foot.append(...save.nodes);
    } };
  }
  function lookPage() {
    const { ui } = RemoteSheet;
    return { title: '外观', build(p) {
      p.body.append(ui.swatches(skinList(), key => { keep('skin', key); paint(); }), ui.note('对 App 的所有页面生效'));
    } };
  }
  $('menu').onclick = () => {
    if (!terminal) return;
    work.ensure();
    RemoteSheet.open(menuPage(), '对话选项');
  };

  // ---- what the bridge asks of this page
  window.TerminalUI = {
    receive,
    error: text => toast(text),
    connection(info) {
      const link = $('link');
      link.hidden = info.state === 'connected' || info.state === 'paused' || (info.state === 'connecting' && !info.reconnects);
      link.textContent = info.state === 'offline' ? '连接已断开，已显示的内容和草稿保留' : '正在重新连接，已显示的内容和草稿保留…';
    },
    inputState(state, op, message) {
      const p = op && mine.get(op.id);
      if (!p) { if (message) toast(message); return; }
      p.state = state; p.error = message || '';
      saveMine();
      if (state === 'accepted') acknowledge(p);
      if (p.node) reader.change(() => { const next = bubble(p); p.node.replaceWith(next); p.node = next; });
    },
    inputError(message, typed, op) {
      if (op && op.action === 'say' && mine.has(op.id)) {
        window.TerminalUI.inputState('rejected', op, message);
        return;
      }
      if (op && op.action === 'answer') { answering.delete(op.ask); drawAsks(); }
      toast(message);
    },
    dictated: text => {
      const val = box.value;
      const hasPos = typeof box.selectionStart === 'number' && typeof box.selectionEnd === 'number';
      const start = hasPos ? box.selectionStart : val.length, end = hasPos ? box.selectionEnd : val.length;
      const before = val.slice(0, start), after = val.slice(end);
      const gap = /[A-Za-z0-9]$/.test(before) && /^[A-Za-z0-9]/.test(text) ? ' ' : '';
      const inserted = gap + text;
      box.value = before + inserted + after;
      const caret = before.length + inserted.length;
      if (hasPos) try { box.setSelectionRange(caret, caret); } catch (error) { /* unfocused */ }
      draft.set(box.value); grow();
    },
    skins: () => [], spacings: () => [], screenText: () => said.innerText
  };
  setInterval(() => { if (state === 'busy') $('clock').textContent = since ? worked(Date.now() - since) : ''; }, 1000);
  paint();
  grow();
  document.addEventListener('DOMContentLoaded', async () => {
    canRich = await richText();
    if (bridge()) bridge().ready();
  });
})();
