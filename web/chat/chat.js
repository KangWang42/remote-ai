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
  const lasted = ms => { const s = Math.max(0, Math.floor(ms / 1000)), two = n => String(n).padStart(2, '0'); return s >= 3600 ? Math.floor(s / 3600) + ':' + two(Math.floor(s / 60) % 60) + ':' + two(s % 60) : Math.floor(s / 60) + ':' + two(s % 60); };

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
      { FORBID_TAGS: ['style', 'form', 'button', 'textarea', 'select', 'input', 'img', 'a', 'iframe', 'video', 'audio'], FORBID_ATTR: ['style', 'href', 'src'] });
    node.querySelectorAll('table').forEach(table => { const wrap = el('div', { className: 'wide' }); table.replaceWith(wrap); wrap.append(table); });
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
  const shotsOf = names => RemoteAttach.shots(names, { base: '../', dir: terminal ? terminal.dir : '', from: 'terminal' });
  function userNode(text) {
    const { text: words, names } = RemoteAttach.split(text);
    if (!names.length) return el('div', { className: 'user', textContent: text });
    return el('div', { className: 'user with-shots' }, words ? el('span', { className: 'words', textContent: words }) : '', shotsOf(names));
  }
  // Successful steps collapse by default; explicit reading choices survive subsequent output.
  function stepNode(item) {
    const what = el('span', { className: 'what', textContent: item.text }), more = el('span', { className: 'more' });
    const head = el('button', { type: 'button', ariaExpanded: 'true' }, el('span', { className: 'mark', ariaHidden: 'true' }), what, more);
    // (the command is shown where the step is named by what it is for; a path it is named by is not said twice)
    const code = el('code', { textContent: item.detail || '', hidden: !item.detail || item.text.includes(item.detail) });
    const out = el('pre', { hidden: true });
    const node = el('div', { className: 'step run' }, head, code, out);
    head.onclick = () => { item.open = !stepOpen(item); reader.change(() => drawStep(item)); };
    Object.assign(item, { node, what, more, code, outNode: out, out: item.out || '' });
    return node;
  }
  const stepOpen = item => item.open === undefined ? item.state !== 'ok' : item.open;
  function drawStep(item) {
    const lines = item.out ? item.out.replace(/\s+$/, '').split('\n') : [];
    const open = stepOpen(item), end = item.outNode.scrollHeight - item.outNode.scrollTop - item.outNode.clientHeight < 40;
    item.node.className = 'step ' + item.state + (open ? '' : ' shut');
    item.node.firstChild.setAttribute('aria-expanded', String(open));
    item.outNode.hidden = !open || !lines.length;
    const output = item.out.replace(/\s+$/, '');
    reader.paint(item.outNode, () => { if (item.outNode.textContent !== output) item.outNode.textContent = output; });
    item.more.textContent = (item.state === 'run' ? '执行中' : item.state === 'fail' ? '未完成' : '已完成') + (lines.length ? ' · ' + lines.length + ' 行' : '') + (open ? ' · 收起' : ' · 展开');
    if (end && !reader.selected(item.outNode) && item.state === 'run' && !item.outNode.hidden) item.outNode.scrollTop = item.outNode.scrollHeight;
  }
  function add(e) {
    let node;
    const item = { id: e.id, kind: e.kind, text: e.text || '', detail: e.detail || '', state: 'run', out: '' };
    if (e.kind === 'user') {
      // what this page sent comes back from the tool: the bubble(s) that waited for it are that message now
      const ackOps = Array.isArray(e.ops) && e.ops.length ? e.ops : (e.op ? [e.op] : []);
      let changed = false;
      ackOps.forEach(op => {
        const waiting = op && mine.get(op);
        if (waiting) { acknowledge(waiting); mine.delete(op); changed = true; if (waiting.node) waiting.node.remove(); }
      });
      if (changed) saveMine();
      node = userNode(item.text);
    } else if (e.kind === 'text') {
      node = el('div', { className: 'text md' });
      setText(node, item.text, true);
    } else if (e.kind === 'thought') {
      node = el('div', { className: 'thought', textContent: item.text });
    } else if (e.kind === 'step') {
      node = stepNode(item);
      drawStep(item);
    } else {
      node = el('div', { className: 'note', textContent: item.text });
    }
    item.node = item.node || node;
    items.set(e.id, item);
    said.append(node);
    if (e.kind === 'text' || e.kind === 'thought') write(e.id);
    return item;
  }
  // the item that is being written: marked so, until another one is, or the turn ends
  function write(id) {
    const before = items.get(writing);
    if (before && before.node) { before.node.classList.remove('writing'); if (before.kind === 'text') setText(before.node, before.text, false); }
    writing = id;
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
      else item.node.textContent = item.text;
    }); });
    dirty.clear();
  }
  function earlier(messages, more) {
    // what was said before it was gone on with here: above, as the conversation's window shows it
    const box = el('div', { className: 'before' });
    box.style.display = 'contents';
    if (more) box.append(el('p', { className: 'earlier', textContent: '更早的内容在电脑上' }));
    (messages || []).forEach(m => {
      if (m.role === 'user') box.append(userNode(m.text));
      else if (m.role === 'assistant') { const node = el('div', { className: 'text md' }); setText(node, m.text, false); box.append(node); }
      else if (m.role === 'thought') box.append(el('div', { className: 'thought', textContent: m.text }));
      else if (m.role === 'tool') {
        const item = { kind: 'step', text: m.text, detail: m.detail || '', state: 'ok', out: m.result || '' };
        box.append(stepNode(item)); drawStep(item);
      }
    });
    if (messages && messages.length) box.append(el('p', { className: 'divide', textContent: '在手机上继续' }));
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
    $('standing').textContent = { ended: '对话已结束', off: '电脑离线', starting: '正在启动', busy: '正在执行', confirm: '等你确认', idle: lastEnd === 'stopped' ? '已停止' : '等你的消息' }[kind] || '';
    $('clock').textContent = kind === 'busy' && since ? lasted(Date.now() - since) : '';
    $('stop').hidden = !(kind === 'busy' || kind === 'confirm');
    $('again').hidden = !(ended && terminal.session);
    const paramsBtn = $('params-btn');
    if (paramsBtn) {
      const canTune = !!terminal && !ended && kind !== 'busy' && kind !== 'confirm' && device.online && (work.has('teams-models') || work.has('chat-params'));
      paramsBtn.hidden = !canTune;
      if (canTune) {
        const label = paramsSummary();
        $('params-text').textContent = label || '模型与参数';
        paramsBtn.classList.toggle('custom', !!label);
      }
    }
    $('write').hidden = !!ended;
    $('go').hidden = !(kind === 'idle' && (lastEnd === 'stopped' || lastEnd === 'failed') && !$('box').value.trim() && !(picker && picker.count));
    $('send').disabled = kind === 'off';
    $('send').classList.toggle('going', sendingPictures);
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
    $('where-text').textContent = (TOOLS[terminal.tool] || terminal.tool) + ' · ' + (terminal.dir || base(terminal.folder));
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
        return window.RemoteRestart.models(work).then(cat => { modelCatalog = cat; stands(); });
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
  const picker = new RemoteAttach.Picker({ form: $('write'), before: $('params-btn'), base: '../', dir: () => terminal ? terminal.dir : '', toast, changed: () => stands() });
  function canPicture() {
    if (pictureAsked || !terminal) return;
    pictureAsked = true;
    work.ensure().then(data => { picker.enabled = !!(data && terminal.dir && (data.device.features || []).includes('attachments')); }, () => { pictureAsked = false; });
  }
  $('write').addEventListener('submit', async event => {
    event.preventDefault();
    if (sendingPictures) return;
    if (!box.value.trim() && !picker.count) return box.focus();
    if (!picker.count) { say(box.value); grow(); return; }
    if (!terminal || terminal.state === 'closed') return;
    if (Array.from(mine.values()).some(p => sameMessage(p, box.value.trim(), picker.names))) return toast('这条消息正在确认送达，请先查看它的状态');
    sendingPictures = true; stands();
    const send = $('send');
    try {
      const paths = await picker.upload(done => { send.textContent = done + '%'; });
      if (say(box.value, paths)) picker.clear();
      grow();
    } catch (error) { toast(error.message); }
    finally { sendingPictures = false; send.textContent = '发送'; stands(); }
  });
  $('go').onclick = () => say('继续');
  $('stop').onclick = () => { $('stop').disabled = true; bridge().send({ action: 'stop' }); setTimeout(() => { $('stop').disabled = false; }, 2500); };
  $('again').onclick = () => bridge().again();
  $('back').onclick = () => bridge().close();
  $('files').onclick = () => { if (terminal && terminal.dir) location.href = '../files/?dir=' + encodeURIComponent(terminal.dir) + '&from=terminal'; };
  if ($('params-btn')) $('params-btn').onclick = () => {
    if (!terminal) return;
    work.ensure();
    RemoteSheet.open(RemoteRestart.paramsPage(work, of(), choice => reopen('chat', choice)), '模型与参数');
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
  setInterval(() => { if (state === 'busy') $('clock').textContent = since ? lasted(Date.now() - since) : ''; }, 1000);
  paint();
  grow();
  document.addEventListener('DOMContentLoaded', async () => {
    canRich = await richText();
    if (bridge()) bridge().ready();
  });
})();
