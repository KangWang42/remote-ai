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
  const TOOLS = { claude: 'Claude Code', codex: 'Codex' };
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');

  // ---- the look: the app's, as on the list pages (app.js)
  const LOOK = ['skin', 'text', 'readfont', 'readsize', 'readline'];
  const saved = (key, fallback) => {
    if (native && native.pref && LOOK.includes(key)) { const chosen = native.pref(key); if (chosen) return chosen; }
    try { return localStorage.getItem('rcli-' + key) || fallback; } catch (error) { return fallback; }
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
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = skin.t.background;
    if (native && native.chrome) native.chrome(skin.t.background);
  }

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
        mine.set(p.id, { id: p.id, text: p.text, draft: p.draft, state: ['accepted', 'rejected'].includes(p.state) ? p.state : 'unknown', error: p.error || '', node: null });
      }
    });
  } catch (error) { /* an unreadable receipt does not prevent the conversation from loading */ }
  function saveMine() {
    const rows = Array.from(mine.values(), ({ id, text, draft, state, error }) => ({ id, text, draft, state, error }));
    pendingStore.set(rows.length ? JSON.stringify(rows) : '');
  }
  const OUT_MOST = 200000;

  function clear() {
    items = new Map(); asks = new Map(); rest = ''; writing = ''; lastEnd = '';
    said.replaceChildren();
    mine.forEach(p => { p.node = null; });
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
      // what this page sent comes back from the tool: the bubble that waited for it is that message now
      const waiting = e.op && mine.get(e.op);
      if (waiting) { acknowledge(waiting); mine.delete(e.op); saveMine(); if (waiting.node) waiting.node.remove(); }
      node = el('div', { className: 'user', textContent: item.text });
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
      if (m.role === 'user') box.append(el('div', { className: 'user', textContent: m.text }));
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
    $('write').hidden = !!ended;
    $('go').hidden = !(kind === 'idle' && (lastEnd === 'stopped' || lastEnd === 'failed') && !$('box').value.trim());
    $('send').disabled = kind === 'off';
  }
  function header() {
    if (!terminal) return;
    $('title').textContent = terminal.title || TOOLS[terminal.tool] || '对话';
    document.title = terminal.title || 'Remote AI';
    $('where-text').textContent = (TOOLS[terminal.tool] || terminal.tool) + ' · ' + (terminal.dir || base(terminal.folder));
    $('dot').className = device.online ? 'on' : 'off';
    $('files').hidden = !terminal.dir;
    if (terminal.error && terminal.error !== lastError) { lastError = terminal.error; if (drawn) toast(terminal.error); }
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
    const node = el('div', { className: 'user going' + (p.state === 'rejected' || p.state === 'unknown' ? ' failed' : ''), textContent: p.text },
      el('small', { textContent: labels[p.state] || labels.queued }));
    if (p.state === 'rejected' || p.state === 'unknown') node.append(el('button', { type: 'button', textContent: p.state === 'unknown' ? '检查后重新编辑' : '重新编辑', onclick: () => {
      const restore = () => {
        if (box.value.trim() && box.value !== p.draft) return toast('输入框里还有未发送的文字，请先处理');
        box.value = p.draft; draft.set(box.value); grow(); box.focus(); p.node.remove(); mine.delete(p.id); saveMine(); stands();
      };
      if (p.state === 'unknown') sheet('重新编辑这条消息？', '电脑可能已经收到，请先检查对话。再次发送会作为一条新消息。', [['已检查，重新编辑', restore, 'solid']]);
      else restore();
    } }));
    return node;
  }
  function acknowledge(p) {
    draft.clear(p.draft);
    if (box.value === p.draft) { box.value = ''; grow(); }
  }
  function say(text) {
    const original = text; text = text.trim();
    if (!text || !terminal || terminal.state === 'closed') return;
    if (Array.from(mine.values()).some(p => p.text === text)) return toast('这条消息正在确认送达，请先查看它的状态');
    const p = { id: newId(), text, draft: original, state: 'queued' };
    draft.set(box.value);
    mine.set(p.id, p);
    saveMine();
    said.querySelectorAll('.empty').forEach(node => node.remove());
    p.node = bubble(p); said.append(p.node); toEnd();
    bridge().send({ id: p.id, action: 'say', text });
  }
  const grow = () => { box.style.height = 'auto'; box.style.height = Math.min(160, box.scrollHeight + 2) + 'px'; stands(); };
  box.addEventListener('input', () => { draft.set(box.value); grow(); });
  box.addEventListener('keydown', event => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) { event.preventDefault(); $('write').requestSubmit(); } });
  $('write').addEventListener('submit', event => {
    event.preventDefault();
    if (!box.value.trim()) return box.focus();
    say(box.value); grow();
  });
  $('go').onclick = () => say('继续');
  $('stop').onclick = () => { $('stop').disabled = true; bridge().send({ action: 'stop' }); setTimeout(() => { $('stop').disabled = false; }, 2500); };
  $('again').onclick = () => bridge().again();
  $('back').onclick = () => bridge().close();
  $('files').onclick = () => { if (terminal && terminal.dir) location.href = '../files/?dir=' + encodeURIComponent(terminal.dir) + '&from=terminal'; };

  // ---- the page's own sheet
  function sheet(title, text, choices, fill) {
    let dialog = $('chat-sheet');
    if (!dialog) { dialog = el('dialog', { id: 'chat-sheet' }); document.body.append(dialog); dialog.onclick = event => { if (event.target === dialog) dialog.close(); }; }
    const form = el('form', { method: 'dialog' }, el('h2', { textContent: title }), text ? el('p', { textContent: text }) : '');
    if (fill) fill(form);
    choices.forEach(([label, act, kind]) => form.append(el('button', { type: 'button', className: kind || '', textContent: label, onclick: () => { dialog.close(); act(); } })));
    form.append(el('button', { type: 'button', className: 'cancel', textContent: '取消', onclick: () => dialog.close() }));
    dialog.setAttribute('aria-label', title);
    dialog.replaceChildren(form);
    dialog.showModal();
  }
  // Into a terminal of the phone's: the conversation is closed here and gone on with there, once the computer has let it go.
  async function inTerminal() {
    const { tool, dir, session } = terminal;
    bridge().send({ action: 'close' });
    toast('正在转到终端…');
    for (let n = 0; n < 40; n++) {
      await new Promise(done => setTimeout(done, 500));
      try {
        const list = await (await fetch('../api/terminal', { credentials: 'same-origin' })).json();
        const listed = (list.sessions || []).find(s => s.id === session && s.tool === tool);
        if (listed && !listed.live) break;
      } catch (error) { /* asked again */ }
    }
    try {
      const { status, body } = await bridge().post({ action: 'start', id: newId(), tool, dir, session, history: false });
      if (status !== 200) throw new Error(body.error || '没有打开');
      location.replace('../terminal/?id=' + body.terminal + (new URLSearchParams(location.search).get('from') === 'bench' ? '&from=bench' : ''));
    } catch (error) { toast(error.message); }
  }
  $('menu').onclick = () => {
    if (!terminal) return;
    const open = terminal.state !== 'closed', choices = [];
    choices.push(['重命名', () => sheet('对话名称', '', [['保存', async () => {
      const title = $('chat-name').value.trim();
      if (!title) return;
      try { const { status, body } = await bridge().post({ action: 'rename', id: newId(), terminal: terminal.id, title }); if (status !== 200) toast(body.error || '没有保存'); }
      catch (error) { toast('没有保存，请重试'); }
    }, 'solid']], form => { form.append(el('input', { id: 'chat-name', value: terminal.title, maxLength: 60, spellcheck: false, ariaLabel: '对话名称' })); setTimeout(() => $('chat-name').select(), 50); })]);
    if (terminal.session && terminal.dir) choices.push(['在终端中打开', () => sheet('在终端中打开？', '这里的对话会关闭，在手机终端里继续同一段对话。', [['在终端中打开', inTerminal, 'solid']])]);
    if (open) choices.push(['结束对话', () => sheet('结束这段对话？', '正在执行的会被中断。对话保存在电脑上，之后可以继续。', [['结束对话', () => bridge().send({ action: 'close' }), 'danger']]), 'danger']);
    sheet('对话选项', '', choices);
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
    dictated: text => { box.value += text; draft.set(box.value); grow(); },
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
