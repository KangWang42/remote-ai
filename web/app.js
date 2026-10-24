/* The list pages, under four tabs at the foot. 任务: everything on the computer at a glance, by what needs you (what
   waits for an answer, what is pinned, what is at work, what was used lately), found by one search and told apart by
   tool and by where it runs. 项目: the projects, and one project in detail. 协作 and 我的 are views of their own
   (together.js, me.js), and a new conversation is begun by a module of its own (begin.js): this page gives them
   what it has (window.RemoteCliPage). Inside the app the tabs are the app's: see `go`. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  // The plain terminal is called what the computer calls it: PowerShell on Windows, the person's shell elsewhere.
  const TOOLS = { claude: 'Claude Code', codex: 'Codex', opencode: 'OpenCode', qoder: 'Qoder', cline: 'Cline', shell: '终端' };
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  const native = window.RemoteCliNative || null;
  // The look belongs to the app, not to one computer: inside the app it is kept there, so every computer's pages
  // open in the same skin and text size. A browser keeps it per address, as it keeps everything else. So is the
  // kind of conversation its owner begins (begin.js): in an application, or in a terminal.
  const LOOK = ['skin', 'text', 'readfont', 'readsize', 'readline', 'newkind'];
  const saved = (key, fallback) => {
    if (native && native.pref && LOOK.includes(key)) { const chosen = native.pref(key); if (chosen) return chosen; }
    try { return localStorage.getItem('rcli-' + key) || fallback; } catch (error) { return fallback; }
  };
  const keep = (key, value) => {
    if (native && native.setPref && LOOK.includes(key)) native.setPref(key, value);
    try { localStorage.setItem('rcli-' + key, value); } catch (error) { /* private window */ }
  };
  let data = null, project = '', timer = 0, shown = 2, endedShown = 8, toastTimer = 0;
  // The tabs at the foot. The one last used is kept while the app is open (a terminal's page leads back to it);
  // ?tab=<name> opens one by a link. A project in detail belongs to 项目: `project` is set only there.
  const TABS = { tasks: '任务', projects: '项目', together: '协作', me: '我的' };
  let tab = 'tasks';
  try { tab = sessionStorage.getItem('rcli-tab') || tab; } catch (error) { /* no storage */ }
  tab = new URLSearchParams(location.search).get('tab') || tab;
  if (!TABS[tab]) tab = 'tasks';
  // The views of 协作 and 我的, by the name of their tab: each draws itself into its own part of the page.
  const views = {};
  // Project detail starts as a compact index. Each long section opens only when the user asks for it.
  const projectFolds = { active: false, live: false, history: false, ended: false };
  // The first list is asked for while the sign-in is still being checked: over a tunnel each answer takes a
  // quarter of a second, and waiting for one before asking for the other doubled the time to the first picture.
  let early = null;

  // ---- drawing helpers
  const svg = (path, size) => `<svg viewBox="0 0 24 24" width="${size || 22}" height="${size || 22}" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${path}</svg>`;
  const ICON = {
    back: svg('<path d="M15 5l-7 7 7 7"/>'),
    // a wheel with eight teeth: the ring of teeth is one circle drawn in dashes
    gear: svg('<circle cx="12" cy="12" r="2.8"/><circle cx="12" cy="12" r="6.3"/><circle cx="12" cy="12" r="8.4" stroke-width="2.4" stroke-linecap="butt" stroke-dasharray="2.64 3.957"/>'),
    // the tabs: what waits (a tray), the projects (a folder), conversations passing work on, the owner
    tasks: svg('<path d="M4 13.5l2.2-7.1A2 2 0 0 1 8.1 5h7.8a2 2 0 0 1 1.9 1.4l2.2 7.1v3.5a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z"/><path d="M4 13.5h4.3a1 1 0 0 1 .9.6 3.1 3.1 0 0 0 5.6 0 1 1 0 0 1 .9-.6H20"/>', 24),
    together: svg('<circle cx="6.5" cy="6.8" r="2.9"/><circle cx="17.5" cy="17.2" r="2.9"/><path d="M12 6.8h3a2.5 2.5 0 0 1 2.5 2.5v2.2M12 17.2H9a2.5 2.5 0 0 1-2.5-2.5v-2.2"/><path d="M15.4 9.6l2.1 2.1 2.1-2.1M8.600 14.4l-2.1-2.1-2.1 2.1"/>', 24),
    me: svg('<circle cx="12" cy="8.300" r="3.8"/><path d="M4.6 20a7.4 7.4 0 0 1 14.8 0"/>', 24),
    chev: svg('<path d="M9 6l6 6-6 6"/>', 18), stop: svg('<rect x="6" y="6" width="12" height="12" rx="3.2" fill="currentColor" stroke="none"/>', 18), plus: svg('<path d="M12 5v14M5 12h14"/>', 20),
    folder: svg('<path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2.2h7a2 2 0 0 1 2 2v7.8a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/>'),
    claude: svg('<path d="M12 3.5v17M3.5 12h17M6 6l12 12M18 6L6 18"/>'), codex: svg('<path d="M12 3l7.8 4.5v9L12 21l-7.8-4.5v-9z"/><path d="M9 10l-2 2 2 2M15 10l2 2-2 2"/>'),
    opencode: svg('<circle cx="12" cy="12" r="8.5"/><path d="M12 8.5v7M8.5 12h7"/>'),
    qoder: svg('<path d="M4 18L12 5l8 13M8.5 14h7"/>'), cline: svg('<path d="M5 5h14v14H5zM9 9.5h6M9 13h4"/>'),
    say: svg('<path d="M20.5 3.5L3.5 10.5l6.5 3 3 6.5zM10 13.5L20.5 3.500"/>', 18),
    pin: svg('<path d="M9 3.5h6l-1 6 3.5 3.5v1.5h-11V13L10 9.5zM12 14.5V21"/>', 13),
    shell: svg('<rect x="3" y="5" width="18" height="14" rx="3"/><path d="M7.5 10l3 2.2-3 2.2M12.5 15h4"/>'), mark: svg('<path d="M7 8l5 4-5 4M13.5 16.5h4.5"/>', 30)
  };
  function el(tag, props, ...children) {
    const node = document.createElement(tag);
    Object.keys(props || {}).forEach(key => {
      if (key === 'html') node.innerHTML = props[key];
      else if (key in node) node[key] = props[key]; else node.setAttribute(key, props[key]);
    });
    node.append(...children.filter(Boolean));
    return node;
  }
  function ago(ms) {
    const s = Math.max(0, (Date.now() - ms) / 1000);
    return s < 90 ? '刚刚' : s < 3600 ? Math.round(s / 60) + ' 分钟前' : s < 86400 ? Math.round(s / 3600) + ' 小时前' : Math.round(s / 86400) + ' 天前';
  }
  function toast(text) {
    const box = $('toast');
    if (!text) { box.hidden = true; return; }
    box.textContent = text; box.hidden = false;
    clearTimeout(toastTimer); toastTimer = setTimeout(() => { box.hidden = true; }, 3200);
  }

  // ---- look: chosen on the page of settings, and put on here
  const TEXT = { small: '14px', normal: '15.5px', large: '17px', larger: '19px' };
  // ?skin=<name> chooses a skin from a link, for example when showing the app to someone.
  const asked = new URLSearchParams(location.search).get('skin');
  if (asked && window.RemoteCliSkins[asked]) keep('skin', asked);
  // How a conversation is read in its window: the type, its size beside the page's own, and the spacing of the lines.
  const READ = {
    readfont: ['字体', { sans: ['默认', 'inherit'], serif: ['衬线', '"Noto Serif SC", "Source Han Serif SC", "Songti SC", SimSun, serif'], mono: ['等宽', 'ui-monospace, "Cascadia Mono", Consolas, monospace'] }, 'sans', '--read-font'],
    readsize: ['字号', { small: ['小', '.84rem'], normal: ['标准', '.93rem'], large: ['大', '1.04rem'], larger: ['特大', '1.18rem'] }, 'normal', '--read-size'],
    readline: ['行距', { tight: ['紧凑', '1.45'], normal: ['标准', '1.65'], loose: ['宽松', '1.9'] }, 'normal', '--read-line'] };
  function typeset() {
    Object.keys(READ).forEach(key => { const [, choices, usual, name] = READ[key]; document.documentElement.style.setProperty(name, (choices[saved(key, usual)] || choices[usual])[1]); });
  }
  function paint() {
    const skin = window.RemoteCliPaint(saved('skin', 'paper'));
    typeset();
    document.documentElement.style.fontSize = TEXT[saved('text', 'normal')] || TEXT.normal;
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = skin.t.background;
    if (native) native.chrome(skin.t.background);
  }
  paint();
  $('back').innerHTML = ICON.back; $('settings').innerHTML = ICON.gear; $('new-task').innerHTML = svg('<path d="M12 5v14M5 12h14"/>', 24);
  document.querySelectorAll('#tabs button').forEach(button => {
    button.querySelector('.glyph').innerHTML = button.dataset.tab === 'projects' ? svg(ICON.folder.replace(/^<svg[^>]*>|<\/svg>$/g, ''), 24) : ICON[button.dataset.tab];
    button.addEventListener('click', () => go(button.dataset.tab));
  });
  // Everything that is set, for this computer and for these pages, is on one page: the line, the program on the
  // computer, the look, the app, signing out.
  $('settings').addEventListener('click', () => { location.href = 'computer/'; });
  $('project-files-icon').innerHTML = ICON.folder; $('project-files-chev').innerHTML = ICON.chev;
  $('project-files').addEventListener('click', () => {
    if (!usable()) return toast('电脑离线，暂时无法查看文件');
    if (!(data.device.features || []).includes('files')) return toast('电脑端版本不支持查看文件，请更新电脑端');
    location.href = 'files/?dir=' + encodeURIComponent(project);
  });
  document.querySelector('.mark').innerHTML = ICON.mark;

  async function api(path, payload) {
    const reply = await fetch(path, payload === undefined ? { credentials: 'same-origin' }
      : { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
    let body = {};
    try { body = await reply.json(); } catch (error) { /* not JSON */ }
    if (reply.status === 401 && path !== 'api/login') { showLogin(); throw new Error('请重新输入访问密码'); }
    if (!reply.ok) throw new Error(body.error || '请求失败（' + reply.status + '）');
    return body;
  }

  // ---- sheets from the bottom
  // `window`: it fills the screen and is left by its own way back (a conversation), instead of rising from the bottom.
  // A sheet is told that it was closed a moment after it is closed. One that is opened in that moment waits for it:
  // else it would take the word meant for the one before, and answer "nothing chosen" while it is still on the screen.
  let sheetGone = Promise.resolve();
  function openSheet(build, window) {
    return sheetGone.then(() => new Promise(done => {
      const form = $('sheet-form'), sheet = $('sheet');
      form.textContent = '';
      sheet.classList.toggle('window', !!window);
      let answer = null;
      build(form, value => { answer = value; sheet.close(); });
      if (!window) form.append(el('button', { type: 'button', className: 'cancel', textContent: '取消', onclick: () => sheet.close() }));
      sheetGone = new Promise(gone => { sheet.onclose = () => { gone(); done(answer); }; });
      sheet.onclick = event => { if (event.target === sheet) sheet.close(); };       // a tap beside the sheet
      sheet.showModal();
    }));
  }
  // A question with a few answers, optionally with fields to fill in; resolves with the answer, or null when dismissed.
  function ask(title, text, choices, fields) {
    return openSheet((form, finish) => {
      form.append(el('h3', { textContent: title }), text ? el('p', { textContent: text }) : '');
      const inputs = {};
      (fields || []).forEach(f => {
        inputs[f.key] = el('input', { id: 'field-' + f.key, value: f.value || '', placeholder: f.placeholder || '', maxLength: f.max || 240, autocapitalize: 'off', spellcheck: false });
        form.append(el('label', { htmlFor: 'field-' + f.key, textContent: f.label }), inputs[f.key]);
      });
      choices.forEach(c => form.append(el('button', { type: 'button', className: 'choice ' + (c.kind || ''), onclick: () =>
        finish(fields ? Object.assign({ choice: c.value }, ...Object.keys(inputs).map(k => ({ [k]: inputs[k].value.trim() }))) : c.value) },
        el('span', null, c.label, c.sub ? el('small', { textContent: c.sub }) : ''))));
    });
  }
  function showLogin() {
    clearTimeout(timer);
    $('home').hidden = true; $('tabs').hidden = true; $('login').hidden = false;
    $('password').focus();
  }
  $('login-form').addEventListener('submit', async event => {
    event.preventDefault();
    $('login-error').textContent = '';
    try {
      await api('api/login', { password: $('password').value });
      $('password').value = '';
      $('login').hidden = true; $('home').hidden = false; $('tabs').hidden = false;
      refresh();
    } catch (error) { $('login-error').textContent = error.message; }
  });

  // ---- what a task is doing
  // A terminal you have looked at since it finished is "waiting"; one you have not is "done".
  function seen() { try { return JSON.parse(saved('seen', '{}')) || {}; } catch (error) { return {}; } }
  function phaseOf(t) {
    // A terminal that has ended is never shown as working, whatever phase it was last recorded in.
    if (t.state !== 'running' && t.state !== 'starting') return t.phase === 'failed' ? 'failed' : 'ended';
    const phase = t.phase || (t.state === 'starting' ? 'starting' : t.status === 'busy' ? 'busy' : 'idle');
    if (phase === 'idle' && t.done && seen()[t.id] !== t.phase_at) return 'done';
    return phase;
  }
  const PHASE = { confirm: ['等你确认', 0], done: ['已完成', 1], waiting: ['等你的消息', 1], busy: ['正在执行', 2], starting: ['正在启动', 3], idle: ['等待输入', 4], stopped: ['已停下', 5], ended: ['已结束', 8], failed: ['异常退出', 7] };
  // A conversation that a program on the computer has open, where its tool tells of it (docs/PROTOCOL.md, `reach`):
  // it stands in the lists like a terminal, by how it stands. '' for one whose tool tells nothing.
  const reachPhase = s => !Array.isArray(s.reach) ? '' : s.status === 'confirm' ? 'confirm' : s.status === 'waiting' ? 'waiting' : s.status === 'busy' ? 'busy' : 'stopped';
  const can = (s, way) => Array.isArray(s.reach) && s.reach.includes(way);
  // By which way the computer does a thing to a conversation ("say", "stop", "answer", "end"; docs/PROTOCOL.md,
  // `ways`): its application's own channel, its tool's hooks, its tool's own way to one that sits idle. '' where
  // there is none, and then the computer's words why. (A relay of an earlier version passes neither on: the one
  // way it names for the conversation is taken then.)
  const APP = 'codex-desktop';
  const wayOf = (s, thing) => !s || !s.live ? '' : s.ways ? s.ways[thing] || ''
    : thing === 'end' ? (s.can_takeover === true ? 'process' : '') : !can(s, thing) ? '' : thing !== 'answer' && s.transport === APP ? APP : 'hook';
  const whyNot = (s, thing, usual) => (s && s.why && s.why[thing]) || usual;
  const running = t => t.state === 'running' || t.state === 'starting';
  const usable = () => data && data.device.online && data.device.enabled;
  const canEnd = s => wayOf(s, 'end') === 'process' && (data.device.features || []).includes('end');

  // Reading a transcript alone does not imply that the original application accepts remote input.
  const controlMode = session => {
    if (!session || !session.live) return 'saved';
    return session.transport === APP || can(session, 'say') || can(session, 'answer') || can(session, 'stop') ? 'shared' : 'read';
  };
  const controlCopy = {
    shared: '连接电脑上的原对话 · 返回不结束任务',
    read: '当前只能查看记录，原应用尚未提供手机控制',
    saved: '已结束 · 已保存的对话记录保留'
  };
  const deliveryText = result => result.status === 'unknown' ? '发送结果未知，请检查原对话' : result.now === false ? '已排队，它做完手头这一步后收到' : result.now === true ? '工具已接收，等待对话更新' : '已提交，等待对话更新';
  const desktopPending = new Map();
  // A native request can be accepted by the computer and then lose its HTTP response. Keep the
  // optimistic row in session storage so a refresh can still query the same operation instead of
  // making the user guess whether the message was sent. It is scoped to this browser tab and expires.
  const pendingKey = session => 'rcli-desktop-pending-' + JSON.stringify([location.pathname, data.device.instance || '', session.tool, session.id]);
  function loadDesktopPending(key, session) {
    try {
      const raw = JSON.parse(sessionStorage.getItem(key) || '[]');
      if (!Array.isArray(raw)) return [];
      const now = Date.now();
      const restored = raw.filter(p => p && p.payload && /^[a-f0-9]{16,32}$/.test(p.payload.id) && p.payload.session === session.id &&
        p.payload.action === 'say' && p.payload.transport === APP && p.payload.text === p.text &&
        typeof p.text === 'string' && p.text.length > 0 && p.text.length <= 8000 && Number.isFinite(p.before) && p.before >= 0 &&
        Number.isFinite(p.createdAt) && now >= p.createdAt && now - p.createdAt < 24 * 60 * 60 * 1000).map(p => {
          // A tab may have disappeared between dispatch and its response. Treat such a row as
          // uncertain after reload; native rows offer the same operation receipt for querying.
          if (p.sent || p.uncertain) return p;
          return Object.assign({}, p, { uncertain: true, delivery: p.delivery || '发送结果未确认，请查询本次结果' });
        });
      saveDesktopPending(key, restored);
      return restored;
    } catch (error) { return []; }
  }
  function saveDesktopPending(key, pending) {
    try {
      const nativePending = pending.filter(p => p.payload.transport === APP);
      if (nativePending.length) sessionStorage.setItem(key, JSON.stringify(nativePending));
      else sessionStorage.removeItem(key);
    } catch (error) { /* private browsing or a full storage quota */ }
  }

  // Sends one operation and waits until the computer has carried it out. One at a time: what is asked for while
  // another is still being carried out waits for it, and is then done. (It was dropped without a word before, which
  // lost a press made in the moment after the one before had shown its result.)
  let turn = Promise.resolve();
  function run(payload, waiting) {
    const mine = turn.then(() => carry(payload, waiting));
    turn = mine.then(() => null, () => null);
    return mine;
  }
  async function carry(payload, waiting) {
    toast(waiting);
    try {
      const op = Object.assign({ id: newId() }, payload);
      let result = await api('api/terminal', op);
      // asked again soon at first: most things are done within a moment
      for (let n = 0; n < 62 && result.state === 'queued'; n++) {
        await new Promise(r => setTimeout(r, n < 3 ? 150 : 400));
        result = await api('api/terminal', op);
      }
      if (result.error) throw new Error(result.error);
      if (result.state === 'queued') throw new Error('电脑没有响应，请确认电脑端程序在运行');
      toast('');
      return result;
    } catch (error) { toast(error.message); return null; }
  }
  // ?take=<conversation> comes from the app's workbench: the question about taking over a conversation that runs on
  // the computer is asked at once, and the terminal that follows leads back to the workbench.
  let take = new URLSearchParams(location.search).get('take') || '', anew = new URLSearchParams(location.search).get('new') === '1', fromBench = !!take || anew;
  // A terminal's page; for one that runs a conversation through its tool's structured mode, the conversation's (chat/).
  function open(terminal, form) { location.href = (form === 'chat' ? 'chat/' : 'terminal/') + '?id=' + encodeURIComponent(terminal) + (fromBench ? '&from=bench' : ''); }
  // What the app's own lists opened this page for (a new conversation, one conversation's window) leads back to
  // them when it is left without going on to a terminal's page.
  const leave = left => { if (!left && fromBench && native && native.home) native.home(); };
  // Resolves with whether the page is left for the terminal that was started.
  async function start(tool, dir, session, takeover, fork) {
    const payload = { action: 'start', tool, dir, history: false };
    if (session) Object.assign(payload, { session, takeover: !!takeover });
    if (fork) payload.fork = true;
    const result = await run(payload, '正在电脑上启动 ' + TOOLS[tool] + '…');
    if (result) open(result.terminal);
    return !!result;
  }
  // A conversation that the computer runs through its tool's structured mode, shown on the phone as a conversation
  // (web/chat/): a new one, or a saved one gone on with (`session`). The computer says whether it can (`chat`).
  const CHAT_TOOLS = ['claude', 'codex'];
  const chatOk = tool => !!data && (data.device.features || []).includes('chat') && CHAT_TOOLS.includes(tool) && (data.device.tools || []).includes(tool);
  async function chat(tool, dir, session, takeover) {
    const payload = { action: 'start', tool, dir, history: false, form: 'chat' };
    if (session) Object.assign(payload, { session, takeover: !!takeover });
    const result = await run(payload, '正在电脑上启动 ' + TOOLS[tool] + '…');
    if (result) open(result.terminal, 'chat');
    return !!result;
  }
  // A saved conversation is gone on with the way its owner begins conversations (begin.js): as a conversation, or in
  // a terminal for one who chose terminals. The other way is behind a held press.
  const asChat = s => chatOk(s.tool) && saved('newkind', '') !== 'term';
  const goOn = s => asChat(s) ? chat(s.tool, s.dir, s.id) : start(s.tool, s.dir, s.id, false);
  // A message for a running terminal without going into it: what the box of its page would send. The relay puts it
  // the way the program takes pasted text and adds Enter (the operation "say").
  async function say(t) {
    const text = await openSheet((form, finish) => {
      const box = el('textarea', { rows: 3, maxLength: 8000, placeholder: '发送消息', autocapitalize: 'off', spellcheck: false, ariaLabel: '消息' });
      // what the program said last is shown, to tell which terminal this is and where it stands
      form.append(el('h3', { textContent: t.title }), el('p', { textContent: t.said || TOOLS[t.tool] + ' · ' + (t.dir || base(t.folder)) }), box,
        el('button', { type: 'button', className: 'choice solid', onclick: () => { if (box.value.trim()) finish(box.value); else box.focus(); } }, el('span', { textContent: '发送' })));
      setTimeout(() => box.focus(), 80);
    });
    if (text && await run({ action: 'say', terminal: t.id, text }, '')) { toast('已发送'); refresh(); }
  }
  // The same for a conversation that is open on the computer: this is shared collaboration. The message is delivered
  // to the program that owns the conversation and the window stays there; it is never presented as a hand-off.
  async function tell(s) {
    if (s.transport === APP) return takeOver(s);
    if (!usable()) return toast('电脑离线，暂时不能发送');
    const text = await openSheet((form, finish) => {
      const box = el('textarea', { rows: 3, maxLength: 8000, placeholder: '发送消息', autocapitalize: 'off', spellcheck: false, ariaLabel: '消息' });
      form.append(el('h3', { textContent: s.title }), el('p', { textContent: s.last || TOOLS[s.tool] + ' · ' + (s.dir || base(s.folder)) }),
        el('div', { className: 'control-mode shared', role: 'status', textContent: '共享协作：消息会交给电脑上的原对话，电脑窗口继续保持打开' }), box,
        el('button', { type: 'button', className: 'choice solid', onclick: () => { if (box.value.trim()) finish(box.value); else box.focus(); } }, el('span', { textContent: '发送' })));
      setTimeout(() => box.focus(), 80);
    });
    if (!text) return;
    toast('发送中…');
    try { const result = await api('api/conversation', { id: newId(), session: s.id, action: 'say', text }); toast(deliveryText(result)); }
    catch (error) { toast(error.message); }
    refresh();
  }
  const answering = new Set();
  async function answerAsk(s, allow, scope = 'once') {
    const id = s.ask.id;
    if (answering.has(id)) return;
    answering.add(id);
    document.querySelectorAll('[data-approval="' + id + '"] button').forEach(b => b.disabled = true);
    try { await api('api/conversation', { id: newId(), session: s.id, action: 'answer', ask: id, allow, scope }); toast(allow ? '已向电脑提交授权' : '已向电脑提交拒绝'); }
    catch (error) { toast(error.message); }
    finally { answering.delete(id); }
    await refresh();
    document.querySelectorAll('[data-approval="' + id + '"] button').forEach(b => b.disabled = false);
  }
  function approvalActions(s, after = () => {}) {
    const answers = el('div', { className: 'answers' });
    answers.dataset.approval = s.ask.id;
    if (can(s, 'answer')) {
      const options = s.ask.options && s.ask.options.length ? s.ask.options : [{ id: 'once', label: '允许一次', detail: '只允许这一次操作' }];
      options.forEach(o => answers.append(el('button', { type: 'button', className: o.id === 'once' ? 'yes' : 'see', textContent: o.label, title: o.detail, disabled: answering.has(s.ask.id), onclick: () => {
        if (o.id === 'once') { answerAsk(s, true, o.id).then(after); return; }
        if (answers.querySelector('.approval-rule')) return;
        const rule = el('div', { className: 'approval-rule' }, el('p', { textContent: o.detail }),
          el('button', { type: 'button', className: 'yes', textContent: '确认保存并允许', onclick: () => answerAsk(s, true, o.id).then(after) }),
          el('button', { type: 'button', className: 'see', textContent: '取消', onclick: () => rule.remove() }));
        answers.append(rule);
      } })));
      answers.append(el('button', { type: 'button', className: 'no', textContent: '拒绝', disabled: answering.has(s.ask.id), onclick: () => answerAsk(s, false).then(after) }));
    } else answers.append(el('span', { className: 'there', textContent: '请在电脑或原终端中选择授权范围' }));
    return answers;
  }
  async function endTerminal(t) {
    if (!await ask('结束“' + t.title + '”？', '', [{ label: '结束终端', value: true, kind: 'danger' }])) return;
    await run({ action: 'close', terminal: t.id }, '正在结束…');
    refresh();
  }
  // Going on with a conversation that a program on the computer has, in a terminal of the phone's: that takes it from
  // the program, or goes on with a copy. It is no part of the conversation's window, which works beside the program;
  // a held press on its card offers it. Each choice says in its own words what happens on the computer.
  const inTerminal = session => sourceOfKind(session.kind || '') === 'term';
  function ways(session) {
    const locked = sessionActivity(session) === 'locked' && session.now !== false, choices = [];
    if (!session.live) return choices;
    // A hand-off is only offered after the computer has verified that it owns a safe-to-stop CLI process. Desktop
    // apps and shared backends stay in shared collaboration mode; showing a disabled-looking action that still
    // submits takeover=true was the source of a guaranteed error from the phone.
    if (session.can_takeover === true && (!locked || inTerminal(session))) {
      if (chatOk(session.tool)) choices.push({ label: '接管到手机对话（结束电脑会话）', value: 'chat-close' });
      choices.push({ label: '接管到手机终端（结束电脑会话）', value: 'close' });
    }
    if (session.tool === 'codex' && (data.device.features || []).includes('codex-fork')) choices.push({ label: '另开一份到手机终端', value: 'copy' });
    return choices;
  }
  // What was said, laid out to be read: the tools answer in Markdown, which is set as text with headings, lists, code
  // and tables rather than shown as its source; what the person asked stands apart on the right; and a run of things
  // the tool did is one quiet block, folded when it is long, so that it does not push the talk off the screen.
  const loading = {};
  const need = name => loading[name] || (loading[name] = new Promise((done, failed) => {
    const script = el('script', { src: 'files/vendor/' + name, onload: done, onerror: failed });
    document.head.append(script);
  }));
  async function writeSaid(box, messages, toolName, phoneTexts) {
    let rich = false;
    try { await Promise.all([need('marked.min.js'), need('purify.min.js')]); rich = !!(window.marked && window.DOMPurify); } catch (error) { /* plain text then */ }
    const set = text => {
      if (!rich) return el('div', { className: 'plain', textContent: text });
      // nothing that could act or load: no links to follow, no pictures, no forms, no styles of its own
      const html = DOMPurify.sanitize(marked.parse(text, { gfm: true, breaks: true }),
        { FORBID_TAGS: ['style', 'form', 'button', 'textarea', 'select', 'input', 'img', 'a', 'iframe', 'video', 'audio'], FORBID_ATTR: ['style', 'href', 'src'] });
      const body = el('div', { className: 'md', html });
      body.querySelectorAll('table').forEach(table => { const wrap = el('div', { className: 'wide' }); table.replaceWith(wrap); wrap.append(table); });
      return body;
    };
    let run = null, last = '';
    messages.forEach(m => {
      if (m.role === 'tool') {
        // The steps stand open, each as a terminal would show it: what it is, the command itself where it is told
        // by what it is for, and the first lines of what came back. The line above them folds a run away.
        if (!run) { run = { list: el('ul'), count: 0 }; run.box = el('details', { className: 'did', open: saved('steps', 'open') !== 'shut' }, el('summary'), run.list); box.append(run.box); }
        run.list.append(el('li', null, el('span', { className: 'what', textContent: m.text }),
          m.detail ? el('code', { className: 'ran', textContent: m.detail }) : '', m.result ? el('pre', { className: 'got', textContent: m.result }) : ''));
        run.count++;
        run.box.firstChild.textContent = '做了 ' + run.count + ' 个操作';
        return;
      }
      run = null;
      // what the tool said of its work between its steps: a quieter line of its own, under the tool's name like its answers
      if (m.role === 'thought') {
        box.append(el('div', { className: 'turn assistant thought' }, last !== 'assistant' ? el('b', { textContent: toolName }) : '', el('div', { className: 'plain', textContent: m.text }), m.truncated ? el('small', { className: 'quiet', textContent: '这段内容超长，剩余部分请在电脑上查看' }) : ''));
        last = 'assistant';
        return;
      }
      const mine = m.role === 'user';
      const fromPhone = mine && phoneTexts && phoneTexts.has(String(m.text || '').trim());
      box.append(el('div', { className: 'turn ' + (mine ? 'user' : 'assistant') },
        !mine && last !== 'assistant' ? el('b', { textContent: toolName }) : '', set(m.text),
        m.truncated ? el('small', { className: 'quiet', textContent: '这条消息超长，剩余部分请在电脑上查看' }) : '',
        fromPhone ? el('small', { className: 'phone-delivery', textContent: '手机消息' }) : ''));
      last = m.role;
    });
  }
  // For how long: 0:07, 3:25, 1:02:10.
  const lasted = ms => { const s = Math.max(0, Math.floor(ms / 1000)), two = n => String(n).padStart(2, '0'); return s >= 3600 ? Math.floor(s / 3600) + ':' + two(Math.floor(s / 60) % 60) + ':' + two(s % 60) : Math.floor(s / 60) + ':' + two(s % 60); };
  // Ending a conversation on the computer closes the program that has it there: asked once, in the words of what happens.
  const confirmEnd = (s, busy, first = '') => new Promise(done => {
    const box = el('div', { className: 'confirm' }, el('p', { textContent: first + (busy ? '结束电脑上的这段对话？它正在执行，会被中断。对话记录保留，之后可以继续。' : '结束电脑上的这段对话？对话记录保留，之后可以继续。') }),
      el('div', { className: 'answers' }, el('button', { type: 'button', className: 'no', textContent: '结束', onclick: () => { box.remove(); done(true); } }), el('button', { type: 'button', className: 'see', textContent: '取消', onclick: () => { box.remove(); done(false); } })));
    const old = document.querySelector('#sheet-form .confirm'); if (old) old.remove();
    const anchor = document.querySelector('#sheet-form .state');
    if (anchor) anchor.before(box); else done(window.confirm('结束电脑上的这段对话？'));
  });
  // The window of a conversation that is open on the computer: what was said, and a box to write in. The
  // conversation stays in the program that has it and is used there as before; this works beside it. It follows what
  // is said there while it is open, what the conversation asks is answered here, and what is written here is handed to
  // it where it is. (Taking it into a terminal is another thing: see ways.)
  async function takeOver(session) {
    if (!usable()) return false;
    const canPeek = (data.device.features || []).includes('peek');
    let watch = 0;
    const choice = await openSheet((form, finish) => {
      const now = () => (data.sessions || []).find(s => s.id === session.id && s.tool === session.tool) || session;
      const head = el('p', { className: 'stands' }), said = el('div', { className: 'said' }), asks = el('div'), write = el('div', { className: 'write' }), below = el('div', { className: 'below' });
      const control = el('div', { className: 'control-mode', role: 'status' });
      const phoneTexts = new Set();
      // Stopping what it is at, the same for every tool: by its application's channel (the one turn that is named,
      // with a receipt that can be asked for again), or through its tool's hooks (before its next step). Where it
      // has neither, it can still be ended, if the computer can close its program: asked first, with the reason.
      let stopAttempt = null;
      const stopping = el('button', { type: 'button', className: 'end halt', textContent: '停止回答', hidden: true, onclick: async () => {
        if (stopping.disabled || !usable()) return;
        const current = now(), way = stopAttempt ? APP : wayOf(current, 'stop');
        if (!way) {
          const why = whyNot(current, 'stop', '这段对话现在不能从手机停止');
          if (!canEnd(current)) return toast(why);
          if (await confirmEnd(current, true, why + '。')) endIt(current);
          return;
        }
        if (way === APP && !stopAttempt && !current.turn) return;
        const query = !!stopAttempt;
        if (way === APP && !stopAttempt) stopAttempt = { id: newId(), session: current.id, action: 'stop', turn: current.turn, transport: APP };
        stopping.disabled = true;
        try {
          const result = await api('api/conversation', way === APP ? { ...stopAttempt, receipt: query } : { id: newId(), session: current.id, action: 'stop', transport: way });
          if (result.status === 'confirmed') { toast(result.goal_pause_error ? '本次回答已停止，但目标未暂停，请在电脑上检查' : '已停止本次回答，可以继续对话'); stopAttempt = null; }
          else if (result.status === 'asked') toast('已告诉它停止：它会在下一步之前停下');
          else toast('停止结果未知，请检查电脑上的原对话');
        } catch (error) { toast(error.message); }
        stopping.disabled = false; stopping.textContent = stopAttempt ? '查询停止结果' : '停止回答'; refresh(); show();
      } });
      // How it stands, in one line above the box, as a terminal says it at its foot: at work and for how long, asking,
      // waiting for what its user says, or stopped and since when. Beside it, the way to end it on the computer.
      const standing = el('span', { className: 'standing' }), clock = el('span', { className: 'clock' });
      const endIt = async current => {
        try { await api('api/conversation', { id: newId(), session: current.id, action: 'end' }); toast('已结束电脑上的这段对话'); finish(null); refresh(); }
        catch (error) { toast(error.message); }
      };
      const ending = el('button', { type: 'button', className: 'end', textContent: '结束', hidden: true, onclick: async () => {
        const current = now();
        if (await confirmEnd(current, reachPhase(current) === 'busy')) endIt(current);
      } });
      const state = el('div', { className: 'state' }, el('span', { className: 'dot' }), standing, clock, stopping, ending);
      // back to what was said last, for someone who has gone up to read
      const latest = el('button', { type: 'button', className: 'latest', hidden: true, ariaLabel: '回到最新', textContent: '回到最新 ↓', onclick: () => { said.scrollTo({ top: said.scrollHeight, behavior: 'smooth' }); } });
      const atEnd = () => said.scrollHeight - said.scrollTop - said.clientHeight < 60;
      said.addEventListener('scroll', () => { latest.hidden = atEnd(); }, { passive: true });
      // the way back from the files leads to this window again
      const files = name => {
        history.replaceState(history.state, '', location.pathname + '?take=' + encodeURIComponent(session.id));
        location.href = 'files/?dir=' + encodeURIComponent(name);
      };
      const outside = el('div', { className: 'reading offer', hidden: true }, el('span', { textContent: base(session.folder) }),
        el('button', { type: 'button', className: 'solid', onclick: async () => {
          if (!await run({ action: 'project_add', path: session.folder, name: '', create: false }, '正在添加…')) return;
          try { data = await api('api/terminal'); } catch (error) { return toast(error.message); }
          const made = (data.device.projects || []).find(p => samePath(p.path, session.folder));
          if (made) files(made.name); else toast('已加为项目');
        } }, el('span', { textContent: '加为项目并查看文件' })));
      // into a terminal of the phone's: for one that runs in a terminal on the computer, which the computer closes
      // for it. There it is typed into and seen as a terminal shows it. (Any other way is behind a held press.)
      const taking = el('div', { className: 'reading take', hidden: true }, el('span', { textContent: '结束电脑上的原会话，在手机终端继续同一段历史' }),
        el('button', { type: 'button', className: 'solid', onclick: () => { const current = now(); finish(null); into(current, 'close'); } }, el('span', { textContent: '接管' })));
      // how it is read: chosen behind "Aa", kept with the look (in the app for every computer)
      const format = el('div', { className: 'reading format', hidden: true });
      Object.keys(READ).forEach(key => {
        const [label, choices, usual] = READ[key], group = el('span', { className: 'pick', role: 'group', ariaLabel: label });
        Object.keys(choices).forEach(value => group.append(el('button', { type: 'button', textContent: choices[value][0], ariaPressed: String(saved(key, usual) === value), onclick: () => {
          keep(key, value); typeset();
          group.querySelectorAll('button').forEach(b => b.setAttribute('aria-pressed', String(b.textContent === choices[value][0])));
        } })));
        format.append(el('span', { textContent: label }), group);
      });
      // and whether the steps stand open or folded to a line each run
      const steps = el('span', { className: 'pick', role: 'group', ariaLabel: '步骤' });
      [['open', '展开'], ['shut', '收起']].forEach(([value, label]) => steps.append(el('button', { type: 'button', textContent: label, ariaPressed: String((saved('steps', 'open') === 'shut' ? 'shut' : 'open') === value), onclick: () => {
        keep('steps', value);
        steps.querySelectorAll('button').forEach(b => b.setAttribute('aria-pressed', String(b.textContent === label)));
        said.querySelectorAll('details.did').forEach(d => { d.open = value === 'open'; });
      } })));
      format.append(el('span', { textContent: '步骤' }), steps);
      form.append(el('div', { className: 'above' }, el('div', { className: 'bar' }, el('button', { type: 'button', className: 'icon', ariaLabel: '返回', html: ICON.back, onclick: () => finish(null) }),
        el('div', { className: 'who' }, el('h3', { textContent: session.title }), head),
        // at the top right, as on a terminal's page: the files of its project, and how it is read. The phone is shown
        // the files of projects only: for a conversation outside every project the folder offers to make its folder
        // one, and opens the files then.
        el('button', { type: 'button', className: 'icon take', ariaLabel: '接到手机终端', html: ICON.shell, hidden: session.can_takeover !== true || !inTerminal(session), onclick: () => {
          if (!usable()) return toast('电脑离线，暂时不能接到手机');
          outside.hidden = format.hidden = true; taking.hidden = !taking.hidden;
        } }),
        // handing it over to another conversation: the window is left for the sheet that asks to which
        el('button', { type: 'button', className: 'icon hand', ariaLabel: '交接给另一段对话', html: ICON.together, hidden: !(views.together && views.together.handoff && (data.device.features || []).includes('work')), onclick: () => finish({ handoff: now() }) }),
        el('button', { type: 'button', className: 'icon files', ariaLabel: '查看项目文件', html: ICON.folder, hidden: !(session.dir || session.folder) || !(data.device.features || []).includes('files'), onclick: () => {
          if (!usable()) return toast('电脑离线，暂时无法查看文件');
          const current = now();
          if (current.dir) return files(current.dir);
          outside.hidden = !outside.hidden;
        } }),
        el('button', { type: 'button', className: 'icon type', ariaLabel: '字体和版式', textContent: 'Aa', onclick: () => { format.hidden = !format.hidden; } })), control, taking, outside, format), el('div', { className: 'reads' }, said, latest), asks, state, below, write);
      // ---- what is said there
      // The window asks the computer for the conversation itself, again and again while it is open, and says what
      // it has (`since`): the computer gives it anew only when it changed, with how it stands. So what is said on
      // the computer is here within a moment, whatever the lists know. (A computer of an earlier version gives no
      // such word; it is asked when the lists say that the conversation changed.)
      const syncNote = el('p', { className: 'quiet sync-note', role: 'status', hidden: true });
      state.before(syncNote);
      const stateKey = current => JSON.stringify([current.updated, current.status, current.last, current.turn]);
      let stamp = '', seen = '', read = false, reading = false, sending = false, stands = null, standsAt = '', readError = '', quick = 0, asked = 0, changed = 0, sawBusy = 0;
      const pendingScope = pendingKey(session);
      const pending = desktopPending.get(pendingScope) || loadDesktopPending(pendingScope, session);
      desktopPending.set(pendingScope, pending);
      const savePending = () => saveDesktopPending(pendingScope, pending);
      const same = (messages, text) => messages.filter(m => m.role === 'user' && String(m.text || '').trim() === text).length;
      let record = [];
      // What the phone said and the conversation has not taken yet stands under what was said, message by message,
      // as the computer keeps it (`queued`): waiting, or held since the conversation was told to stop. Each can be
      // taken back; what is held can be sent after all. What this page sent itself is shown with its whole words;
      // what it finds waiting after it was opened anew, with the beginning the computer gives.
      const withdraw = async id => {
        try {
          await api('api/conversation', { id: newId(), session: session.id, action: 'withdraw', message: id });
          const mine = pending.findIndex(p => p.payload.id === id);
          if (mine >= 0) { phoneTexts.delete(pending[mine].text); pending.splice(mine, 1); savePending(); }
          toast('已撤回');
        } catch (error) { toast(error.message); }
        await refresh(); shown = ''; show();
      };
      const release = async () => {
        try { await api('api/conversation', { id: newId(), session: session.id, action: 'release' }); toast('已继续发送'); }
        catch (error) { toast(error.message); }
        await refresh(); shown = ''; show();
      };
      const QUEUED = { queued: '排队中 · 它做完手头这一步后收到', sending: '正在送达 · 等待电脑确认', paused: '已暂停 · 请确认对话和发送情况后继续' };
      function waiting() {
        said.querySelectorAll('.turn.mine, .held').forEach(node => node.remove());
        const kept = now().queued || [], shownIds = new Set();
        const line = (text, id, label, going) => {
          const state = kept.find(item => item.id === id);
          const row = el('div', { className: 'turn user mine' + (going ? ' going' : '') }, el('div', { className: 'plain', textContent: text }),
            el('small', { className: 'phone-delivery', textContent: state ? QUEUED[state.state] : label }));
          if (state && state.state !== 'sending') row.append(el('button', { type: 'button', textContent: '撤回', onclick: event => { event.currentTarget.disabled = true; withdraw(id); } }));
          return row;
        };
        pending.forEach(p => {
          shownIds.add(p.payload.id);
          if (kept.some(item => item.id === p.payload.id)) p.waited = true;
          // (one that waited and waits no more has been handed to the conversation: its record says so soon)
          const row = line(p.text, p.payload.id, p.waited ? '已交给对话' : p.delivery || '手机消息 · 正在送达', !(p.sent || p.uncertain));
          if (p.payload.transport === APP && p.uncertain) row.append(el('button', { type: 'button', textContent: '查询发送结果', onclick: async event => {
            event.currentTarget.disabled = true;
            try { await deliver(p, true); } finally { waiting(); refresh(); }
          } }));
          said.append(row);
        });
        kept.filter(item => !shownIds.has(item.id)).forEach(item => said.append(line(item.text, item.id, '', false)));
        const held = kept.filter(item => item.state === 'paused').length;
        if (held) said.append(el('div', { className: 'held' }, el('button', { type: 'button', className: 'solid', textContent: held > 1 ? '继续发送这 ' + held + ' 条' : '继续发送', onclick: event => { event.currentTarget.disabled = true; release(); } })));
      }
      // Hands a message to the computer, which answers the same for every way: accepted, and whether the tool has
      // it now or takes it when it has done what it is at. The application's channel keeps a receipt that can be
      // asked for again when the answer was lost; by any other way a refusal is final. False: it was not sent.
      async function deliver(mine, query = false) {
        const receipt = mine.payload.transport === APP;
        try {
          const result = await api('api/conversation', receipt ? { ...mine.payload, receipt: query } : mine.payload);
          mine.uncertain = result.status !== 'accepted' || result.sent !== true; mine.sent = !mine.uncertain;
          mine.settledAt = Date.now(); mine.delivery = deliveryText(result); savePending();
          return true;
        } catch (error) {
          toast(error.message);
          if (receipt) { mine.uncertain = true; mine.delivery = '发送结果未确认，请查询本次结果'; savePending(); }
          return receipt;
        }
      }
      async function follow(current) {
        const listed = [current.updated, current.status, current.last || ''].join('\n');
        if (!canPeek || reading || (read && stamp === null && listed === seen)) return;
        reading = true;
        const startedAt = stateKey(current);
        try {
          const got = await api('api/conversation', Object.assign({ id: newId(), session: session.id }, stamp ? { since: stamp } : {}));
          readError = '';
          if (typeof got.status === 'string') { stands = got.status; standsAt = startedAt; }
          if (!got.same) {
            record = got.messages || [];
            if (typeof got.asked === 'number') asked = got.asked;
            if (typeof got.updated === 'number') changed = got.updated;
            // A different message typed at the computer is not an acknowledgement of a phone message.
            // Leave a confirmed phone turn visible briefly so its delivery state is readable before the transcript
            // replaces the optimistic row. A subsequent poll removes it once the persisted record is stable.
            let removed = false;
            for (let i = pending.length - 1; i >= 0; i--) if (!pending[i].uncertain && same(record, pending[i].text) > pending[i].before && (!pending[i].settledAt || Date.now() - pending[i].settledAt > 1200)) { pending.splice(i, 1); removed = true; }
            if (removed) savePending();
            const end = !read || atEnd(), was = said.scrollTop;
            const fresh = el('div');
            if (got.more) fresh.append(el('p', { className: 'earlier', textContent: '更早的内容在电脑上' }));
            await writeSaid(fresh, record, TOOLS[session.tool], phoneTexts);
            said.replaceChildren(...fresh.childNodes);
            waiting();
            said.scrollTop = end ? said.scrollHeight : was;      // what was said last is what one came to see; someone reading further up is left there
            latest.hidden = atEnd();
          }
          stamp = typeof got.stamp === 'string' ? got.stamp : null; seen = listed; read = true;
        } catch (error) { stands = null; readError = '对话同步中断，正在重试：' + error.message; if (!read) said.replaceChildren(el('p', { className: 'quiet', textContent: error.message })); }
        reading = false;
      }
      // ---- how it stands, what it asks, and the box
      const box = el('textarea', { rows: 1, maxLength: 8000, placeholder: '发消息', autocapitalize: 'off', spellcheck: false, ariaLabel: '消息' });
      box.addEventListener('input', () => { box.style.height = 'auto'; box.style.height = Math.min(140, box.scrollHeight + 2) + 'px'; });
      const send = el('button', { type: 'button', className: 'solid', textContent: '发送', onclick: async () => {
        const text = box.value.trim();
        if (send.disabled || !usable() || !can(now(), 'say') || now().ask) return;
        if (!text) return box.focus();
        // what is written stands in the window at once, as what the user said, while it is on its way
        const mine = { text, before: same(record, text) + pending.filter(p => p.text === text).length, sent: false, createdAt: Date.now(), payload: { id: newId(), session: session.id, action: 'say', text } };
        if (wayOf(now(), 'say') === APP) mine.payload.transport = APP;
        pending.push(mine); savePending(); box.value = ''; box.style.height = 'auto';
        phoneTexts.add(text);
        waiting(); said.scrollTop = said.scrollHeight;
        sending = true; send.disabled = true; quick = Date.now() + 45000;
        // what was not sent goes back into the box, to be sent again or changed
        if (!await deliver(mine)) { pending.splice(pending.indexOf(mine), 1); savePending(); phoneTexts.delete(text); if (!box.value) { box.value = text; box.dispatchEvent(new Event('input')); } }
        sending = false; send.disabled = false; waiting(); refresh(); seen = ''; shown = ''; show();
      } });
      // a conversation that has stopped is most often told to go on: one press, beside what is typed
      const goOn = el('button', { type: 'button', className: 'go', textContent: '继续', hidden: true, onclick: () => { if (!box.value.trim()) { box.value = '继续'; send.click(); } else box.focus(); } });
      write.append(goOn, box, send);
      let shown = '';
      function show() {
        // how it stands is what the computer said of it last: with the conversation itself, or in the lists
        const listed = now(), current = stands === null || standsAt !== stateKey(listed) || stands === 'confirm' || listed.ask || listed.transport === APP ? listed : Object.assign({}, listed, { status: stands }), phase = current.live ? reachPhase(current) : '';
        syncNote.textContent = readError || (String(current.last || '').startsWith('Claude Code 本次执行失败：') ? current.last : '');
        syncNote.hidden = !syncNote.textContent;
        const mode = controlMode(current);
        control.className = 'control-mode ' + mode;
        // (where it can only be read, the computer's own words why it cannot be told anything)
        control.textContent = mode === 'read' ? whyNot(current, 'say', controlCopy.read) : controlCopy[mode] + (mode === 'shared' && current.can_takeover === true ? ' 需要完整终端时，可选择“接管”。' : '');
        const signature = JSON.stringify([current.live, current.status, current.reach, current.ways, current.why, current.queued, current.wake, current.ask, current.host, current.now, current.dir, current.transport, current.turn]);
        follow(listed);
        // the line of how it stands: its clock goes on between the computer's words
        const kind = !current.live ? 'stopped' : phase || 'stopped';
        // at work since its user spoke, as its record says; where the record does not say, since this window saw it at work
        sawBusy = kind !== 'busy' ? 0 : sawBusy || Date.now();
        const from = kind === 'busy' ? (asked && asked <= Date.now() ? asked : sawBusy) : kind === 'stopped' || kind === 'waiting' ? changed || current.updated : 0;
        state.className = 'state ' + kind;
        standing.textContent = !current.live ? '已在电脑上关闭' : PHASE[kind][0];
        clock.textContent = !from ? '' : kind === 'busy' ? lasted(Date.now() - from) : ago(from);
        ending.hidden = !canEnd(current);
        // while it works it can be told to stop, whatever its tool: the press says what happens where it cannot
        stopping.hidden = !stopAttempt && kind !== 'busy' && !can(current, 'stop');
        goOn.hidden = !(can(current, 'say') && !current.ask && (kind === 'stopped' || kind === 'waiting') && record.length > 0);
        if (signature === shown) return;
        shown = signature;
        waiting();
        head.textContent = whereOf({ s: current, tool: current.tool, live: current.live }) + ' · ' + (current.dir || base(current.folder));      // (how it stands is said in the line above the box)
        // what it asks is answered here, under what was said
        asks.textContent = '';
        if (current.ask) {
          const answers = approvalActions(current, show);
          asks.append(el('div', { className: 'card asking' }, el('div', { className: 'ask' }, el('pre', { textContent: current.ask.text }), answers)));
        }
        // The box stands wherever something can be said, and also where it will be once the conversation has done
        // what it is at (its application takes nothing meanwhile): the next thing can be written there already.
        const sayable = can(current, 'say') && !current.ask, later = !sayable && !current.ask && current.live && current.transport === APP;
        write.hidden = !sayable && !later;
        send.disabled = sending || !sayable; box.placeholder = sayable ? '发消息' : '可以先写下一句，等待当前回答结束';
        below.textContent = '';
        // why nothing can be said to it now, where that is not said above already
        if (!sayable && !later && !current.ask && current.live && mode !== 'read' && whyNot(current, 'say', '')) below.append(el('p', { className: 'quiet', textContent: whyNot(current, 'say', '') }));
        // where it cannot be told anything from here, it can still be taken into a terminal: the one thing left to do
        if (!sayable && !current.ask) {
          const taken = current.live ? ways(current) : current.dir ? (chatOk(current.tool) ? [{ label: '在手机上继续', value: 'chat' }] : []).concat([{ label: '在手机终端继续', value: 'resume' }]) : [];
          taken.forEach(c => below.append(el('button', { type: 'button', className: 'choice', onclick: () => finish({ value: c.value, session: current }) }, el('span', { textContent: c.label }))));
        }
      }
      show();
      // every second; for a while after something was written here, twice as often
      let ticks = 0;
      watch = setInterval(() => { if ($('sheet').open && (++ticks % 2 === 0 || Date.now() < quick)) show(); }, 500);
    }, true);
    clearInterval(watch);
    if (choice && choice.handoff) { await views.together.handoff(choice.handoff); return false; }
    return choice ? into(choice.session, choice.value) : false;
  }
  // Into a terminal of the phone's: by taking the conversation from the program that has it, by a copy, or, for one
  // that nothing has open, simply by going on with it.
  function into(session, how) {
    if (how === 'chat' || how === 'chat-close') return chat(session.tool, session.dir, session.id, how === 'chat-close');
    return start(session.tool, session.dir, session.id, how === 'close', how === 'copy');
  }
  // A new conversation is begun by begin.js: for one project on its page, for any of them from 任务 and from the
  // app's own lists (?new=1).
  const beginAnywhere = () => window.RemoteCliPage.begin.anywhere();
  // A project is made of a folder on the computer: one of those that already have conversations (each says by what
  // it is used: a desktop app, an editor, a terminal), found by searching when they are many, or one that is typed.
  const FEW = 6;
  async function addProject() {
    const offered = data.device.candidates || [];
    const answer = await openSheet((form, finish) => {
      form.append(el('h3', { textContent: '添加项目' }));
      if (offered.length) {
        const list = el('div', { className: 'anywhere folders' });
        const show = query => {
          const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
          list.replaceChildren(...offered.filter(c => words.every(w => (c.name + '\n' + c.path + '\n' + (c.kinds || []).join(' ')).toLowerCase().includes(w)))
            .map(c => el('button', { type: 'button', className: 'choice', onclick: () => finish({ path: c.path, name: '' }) },
              el('span', null, c.name, el('small', { textContent: (c.kinds || []).concat(c.path).join(' · ') })))));
        };
        if (offered.length > FEW) form.append(el('input', { type: 'search', className: 'find', placeholder: '搜索文件夹', ariaLabel: '搜索电脑上有对话的文件夹', autocapitalize: 'off', spellcheck: false, oninput: event => show(event.target.value) }));
        form.append(list);
        show('');
      }
      const path = el('input', { id: 'field-path', placeholder: 'D:\\projects\\demo', maxLength: 240, autocapitalize: 'off', spellcheck: false });
      const name = el('input', { id: 'field-name', maxLength: 40, autocapitalize: 'off', spellcheck: false });
      form.append(el('label', { htmlFor: 'field-path', textContent: '文件夹路径' }), path, el('label', { htmlFor: 'field-name', textContent: '名称（可不填）' }), name,
        el('button', { type: 'button', className: 'choice solid', onclick: () => finish({ path: path.value.trim(), name: name.value.trim() }) }, el('span', null, '添加填写的路径')));
    });
    if (!answer) return;
    if (!answer.path) { toast('请填写文件夹路径'); return; }
    if (!await run({ action: 'project_add', path: answer.path, name: answer.name, create: false }, '正在添加…')) return;
    // the project that was added is entered: what it was added for (a new conversation, its files) is there
    await refresh();
    const made = (data.device.projects || []).find(p => samePath(p.path, answer.path));
    if (made && !project) enter(made.name);
  }

  // ---- everything there is to work with, said one way
  // A terminal of the phone's, a conversation that is open on the computer, a conversation that is only saved: each
  // is a thing with a name, a place (its project, or the folder it is open in), a tool, a source (where it runs), a
  // phase, and the time it was last used. The lists, the search and the filters all work on these.
  // Where a thing runs is told apart in two: in a terminal (one of the phone's, or a command line on the computer),
  // or in an app (a desktop app, an editor). The computer says by what a conversation or a folder is used (`kind`,
  // `kinds`: docs/PROTOCOL.md); every word of it but the terminal's is an app.
  const SOURCE = { term: '终端', app: '应用' }, PHONE = '手机终端', PHONE_CHAT = '手机对话';
  const sourceOfKind = kind => !kind ? '' : kind === '终端' ? 'term' : 'app';
  const marks = () => (data.device.features || []).includes('marks');
  const base = path => String(path || '').replace(/[\\/]+$/, '').split(/[\\/]/).pop() || String(path || '');
  const samePath = (a, b) => String(a || '').replace(/[\\/]+$/, '').toLowerCase() === String(b || '').replace(/[\\/]+$/, '').toLowerCase();
  // where a conversation that a program has open runs: the desktop app or an editor ('app'), or a command line
  // (one that no program has open is told by what started it)
  // and one that a program has open by what the computer says uses it (a command line inside an editor's terminal
  // is a terminal's); a computer of an earlier version says only where the program runs
  const sourceOf = s => !s.live || s.kind ? sourceOfKind(s.kind) : /桌面版|后台服务/.test(s.origin || '') || sessionHost(s) === 'shared' || (s.origin && s.origin !== '命令行' && s.origin !== '电脑独立终端') ? 'app' : 'term';
  function things() {
    const sessions = data.sessions || [], saved = {};
    sessions.forEach(s => { saved[s.tool + ':' + s.id] = s; });
    const out = (data.terminals || []).map(t => {
      const s = t.session ? saved[t.tool + ':' + t.session] || null : null, phase = phaseOf(t), live = running(t);
      // an ended terminal whose conversation is listed is found there, to be gone on with: it is not listed twice
      return { t, s, tool: t.tool, title: t.title, place: t.dir || base(t.folder), project: t.dir, apart: !t.dir, live, phase, rank: PHASE[phase][1], at: t.phase_at || t.created,
        source: t.form === 'chat' ? 'app' : 'term', pinned: !!(s && s.pinned), hidden: false, state: live ? 'active' : 'ended', twice: !live && !!s && !s.terminal };
    });
    sessions.filter(s => !s.terminal).forEach(s => {
      const phase = !s.live || s.now === false ? '' : reachPhase(s) || (sessionHost(s) === 'cli' ? (s.status === 'busy' ? 'busy' : s.status === 'confirm' ? 'confirm' : s.status === 'waiting' ? 'waiting' : 'idle') : '');
      out.push({ s, tool: s.tool, title: s.title, place: s.dir || base(s.folder), project: s.dir, apart: !s.dir, live: !!s.live, phase, rank: phase ? PHASE[phase][1] : 6, at: s.updated,
        source: sourceOf(s), pinned: !!s.pinned, hidden: !!s.hidden && !s.live, state: sessionActivity(s) });
    });
    return out;
  }
  const needsYou = x => x.live && x.phase === 'confirm';
  const working = x => x.live && (x.phase === 'busy' || x.phase === 'starting');
  const byRank = (a, b) => a.rank - b.rank || b.at - a.at, byTime = (a, b) => b.at - a.at;
  const byOpen = (a, b) => (b.pinned - a.pinned) || byRank(a, b);
  // by what it runs and where, in the words of the cards
  // Every card says it the same way: the tool, then where it runs. A terminal of the phone's; for a conversation
  // that a program has open, that program (a desktop app by its own name, which names the tool too; a command
  // line; an editor, and whether it is the editor's terminal); for one that is only saved, by what it was started.
  const whereOf = x => {
    const tool = TOOLS[x.tool];
    if (x.t) return tool + (x.live ? ' · ' + (x.t.form === 'chat' ? PHONE_CHAT : PHONE) : '');
    const kind = x.s.kind || '';
    if (!x.live) return !kind ? tool : /桌面版/.test(kind) ? kind : tool + ' · ' + kind;
    const origin = x.s.origin && x.s.origin !== '电脑独立终端' ? x.s.origin : '';
    return !origin ? tool : /桌面版|后台服务/.test(origin) ? origin : tool + ' · ' + origin + (kind === '终端' && origin !== '命令行' ? ' 的终端' : '');
  };
  // what a search looks through
  const wordsOf = x => [x.title, x.place, TOOLS[x.tool], SOURCE[x.source] || '', x.t && x.live ? (x.t.form === 'chat' ? PHONE_CHAT : PHONE) : '', x.s && x.s.origin || '', x.s && x.s.kind || '', x.apart ? '未加入项目' : '', x.pinned ? '置顶' : '', x.hidden ? '已隐藏' : ''].join('\n').toLowerCase();
  // what a card shows: it is built again when any of this changes
  const signed = x => x.t ? [x.t.id, x.phase, x.title, x.t.phase_at, x.pinned, x.place, x.apart]
    : [x.s.id, x.phase, x.s.now, x.s.status, x.title, x.s.host, x.s.origin, (x.s.reach || ['-']).join(), canEnd(x.s), endingSessions.has(x.s.id), x.s.ask || null, x.s.last || '', x.pinned, x.hidden, x.place, !!x.apart, x.state, x.source];

  // ---- the filters: by tool, and by where a thing runs. They last as long as the page is kept open.
  let filter = { tool: '', source: '' }, resultsShown = 30, recentShown = 2, hiddenShown = false;
  try { filter = Object.assign(filter, JSON.parse(sessionStorage.getItem('rcli-filter') || '{}')); } catch (error) { /* no storage */ }
  if (!SOURCE[filter.source]) filter.source = '';
  const pass = x => (!filter.tool || x.tool === filter.tool) && (!filter.source || x.source === filter.source);
  // a project is one of a tool or of a kind by what its folder is used for, or by what is in it now
  const passProject = (folder, inside) => (!filter.tool || (folder.tools || []).includes(filter.tool) || inside.some(x => x.tool === filter.tool))
    && (!filter.source || (folder.kinds || []).some(kind => sourceOfKind(kind) === filter.source) || inside.some(x => x.source === filter.source));
  function choose(change) {
    filter = Object.assign({}, filter, change);
    try { sessionStorage.setItem('rcli-filter', JSON.stringify(filter)); } catch (error) { /* no storage */ }
    resultsShown = 30; recentShown = 2; draw();
  }
  function drawFilters(all) {
    // a row is offered only where it tells something apart, or to take back what was chosen
    const folders = data.device.projects || [];
    const tools = Object.keys(TOOLS).filter(tool => tool === filter.tool || all.some(x => x.tool === tool) || folders.some(f => (f.tools || []).includes(tool)));
    const sources = Object.keys(SOURCE).filter(source => source === filter.source || all.some(x => x.source === source) || folders.some(f => (f.kinds || []).some(kind => sourceOfKind(kind) === source)));
    const offered = (tools.length > 1 ? tools : filter.tool ? [filter.tool] : []), places = (sources.length > 1 ? sources : filter.source ? [filter.source] : []);
    const box = $('filters'), signature = JSON.stringify([offered, places, filter, TOOLS.shell]);
    box.hidden = !offered.length && !places.length;
    if (box.dataset.signature === signature) return;
    box.dataset.signature = signature;
    const chip = (text, on, act) => el('button', { type: 'button', className: on ? 'on' : '', ariaPressed: String(on), textContent: text, onclick: act });
    const row = [chip('全部', !filter.tool && !filter.source, () => choose({ tool: '', source: '' }))];
    offered.forEach(tool => row.push(chip(TOOLS[tool], filter.tool === tool, () => choose({ tool: filter.tool === tool ? '' : tool }))));
    if (offered.length && places.length) row.push(el('i'));
    places.forEach(source => row.push(chip(SOURCE[source], filter.source === source, () => choose({ source: filter.source === source ? '' : source }))));
    box.replaceChildren(...row);
  }

  // ---- what is done with a thing besides opening it: asked for by a held press on its card
  async function mark(s, change) {
    try { await api('api/conversation', Object.assign({ id: newId(), session: s.id, action: 'mark' }, change)); Object.assign(s, change); draw(); }
    catch (error) { toast(error.message); }
    refresh();
  }
  async function addFolder(path) {
    if (await run({ action: 'project_add', path, name: '', create: false }, '正在添加…')) { toast('已加为项目'); refresh(); }
  }
  async function manage(x) {
    const s = x.s, choices = [];
    if (s && marks()) choices.push({ label: s.pinned ? '取消置顶' : '置顶', value: 'pin' });
    if (s && !x.t && marks() && (!s.live || s.hidden || s.now === false)) choices.push({ label: s.hidden ? '取消隐藏' : '从列表隐藏', value: 'hide' });
    if (s && !x.t && canEnd(s) && usable()) choices.push({ label: '结束电脑上的这段对话', value: 'finish', kind: 'danger' });
    if (s && !x.t && s.live) ways(s).forEach(way => choices.push(way));
    if (s && !x.t && !s.live && s.dir && chatOk(s.tool) && usable()) choices.push(asChat(s) ? { label: '在手机终端继续', value: 'resume' } : { label: '在手机上继续', value: 'chat' });
    const folder = x.apart ? (x.t ? x.t.folder : s.folder) || '' : '';
    if (folder && usable()) choices.push({ label: '把 ' + base(folder) + ' 加为项目', value: 'add' });
    if (s && views.together && views.together.handoff && usable() && (data.device.features || []).includes('work')) choices.push({ label: '交接给另一段对话', value: 'handoff' });
    if (!project && x.project) choices.push({ label: '进入项目 ' + x.project, value: 'enter' });
    if (x.t && x.live) choices.push({ label: '结束终端', value: 'end', kind: 'danger' });
    // a saved conversation of a project that nothing has open can be put away (the computer keeps it recoverable)
    if (s && !x.t && !s.live && s.dir && usable() && (data.device.features || []).includes('delete')) choices.push({ label: '删除对话', value: 'delete', kind: 'danger' });
    if (!choices.length) return;
    const choice = await ask(x.title, '', choices);
    if (choice === 'pin') mark(s, { pinned: !s.pinned });
    else if (choice === 'hide') mark(s, s.hidden ? { hidden: false } : { hidden: true, pinned: false });
    else if (choice === 'add') addFolder(folder);
    else if (choice === 'enter') enter(x.project);
    else if (choice === 'handoff') views.together.handoff(s);
    else if (choice === 'end') endTerminal(x.t);
    else if (choice === 'finish') endConversation(s);
    else if (choice === 'close' || choice === 'copy' || choice === 'chat' || choice === 'chat-close' || choice === 'resume') into(s, choice);
    else if (choice === 'delete') remove(s);
  }
  async function remove(s) {
    if (!await ask('删除“' + s.title + '”？', '', [{ label: '删除', value: true, kind: 'danger' }])) return;
    toast('正在删除…');
    try { await api('api/conversation', { id: newId(), session: s.id, action: 'delete' }); toast('已删除'); }
    catch (error) { toast(error.message); }
    refresh();
  }
  // ---- a project besides entering it: renamed, or taken from the list (its folder and what is in it stay)
  // One the computer keeps in its own settings is changed from here where the computer says it does that.
  async function manageProject(name, folder) {
    if (!usable() || (folder.fixed && !(data.device.features || []).includes('projects'))) return;
    const choice = await ask(name, '', [{ label: '重命名', value: 'rename' }, { label: '移除项目', value: 'remove', kind: 'danger' }]);
    if (choice === 'rename') {
      const answer = await ask('重命名“' + name + '”', '', [{ label: '保存', value: true, kind: 'solid' }], [{ key: 'to', label: '名称', value: name, max: 40 }]);
      if (answer && answer.to && answer.to !== name && await run({ action: 'project_rename', name, to: answer.to }, '正在保存…')) refresh();
    } else if (choice === 'remove') {
      if (await ask('移除“' + name + '”？', '', [{ label: '移除项目', value: true, kind: 'danger' }]) && await run({ action: 'project_remove', name }, '正在移除…')) { toast('已移除'); refresh(); }
    }
  }
  // A press that is held on a card asks what to do with it. A finger that moves is scrolling; the press that ends a
  // held one opens nothing. A mouse asks with its other button, and so does a phone that says a held press that way.
  function held(node, action) {
    let timer = 0, fired = false, x = 0, y = 0;
    const clear = () => { clearTimeout(timer); timer = 0; };
    const act = () => { if (!$('sheet').open) action(); };
    node.addEventListener('touchstart', event => {
      clear(); fired = false;
      if (event.touches.length !== 1) return;
      x = event.touches[0].clientX; y = event.touches[0].clientY;
      timer = setTimeout(() => { timer = 0; fired = true; act(); }, 520);
    }, { passive: true });
    node.addEventListener('touchmove', event => { const at = event.touches[0]; if (Math.abs(at.clientX - x) > 10 || Math.abs(at.clientY - y) > 10) clear(); }, { passive: true });
    node.addEventListener('touchend', event => { clear(); if (fired && event.cancelable) event.preventDefault(); });
    node.addEventListener('touchcancel', clear);
    node.addEventListener('contextmenu', event => { event.preventDefault(); clear(); if (!fired) act(); });
    return node;
  }

  // ---- cards
  const endingSessions = new Set();
  async function endConversation(s) {
    if (!usable() || !canEnd(s) || endingSessions.has(s.id)) return;
    if (!await ask('结束“' + s.title + '”？', '正在执行的任务会被中断；已保存的对话记录保留。', [{ label: '结束电脑上的这段对话', value: true, kind: 'danger' }])) return;
    const current = (data.sessions || []).find(one => one.id === s.id && one.tool === s.tool);
    if (!usable() || !current || !canEnd(current) || endingSessions.has(s.id)) return;
    endingSessions.add(s.id); draw();
    try { await api('api/conversation', { id: newId(), session: s.id, action: 'end' }); toast('已结束电脑上的这段对话'); }
    catch (error) { toast(error.message); }
    finally { endingSessions.delete(s.id); await refresh(); }
  }
  function pill(kind, text) { return el('span', { className: 'pill ' + kind, textContent: text }); }
  const named = x => el('b', null, x.pinned ? el('i', { className: 'pin', html: ICON.pin }) : '', x.title);
  // where it is: its project, or the folder a conversation is open in when that is no project
  function placed(meta, x) {
    if (!x.place) return;
    meta.append(el('span', { className: 'chip' + (x.apart ? ' apart' : ''), textContent: x.place, title: x.place, ariaLabel: (x.apart ? '文件夹：' : '项目：') + x.place }));
    if (x.apart) meta.append(el('span', { textContent: '未加入项目' }));
  }
  const card = x => {
    const made = x.t ? terminalCard(x) : sessionCard(x);
    if (!x.t && canEnd(x.s)) made.insertBefore(el('button', { type: 'button', className: 'side end-session', ariaLabel: '结束电脑上的这段对话', title: '结束对话', html: ICON.stop, disabled: !usable() || endingSessions.has(x.s.id), onclick: () => endConversation(x.s) }), made.querySelector('.ask'));
    return held(made, () => manage(x));
  };
  // A terminal opened from the phone.
  function terminalCard(x) {
    const t = x.t, phase = x.phase, meta = el('span', { className: 'meta' }, pill(phase, PHASE[phase][0]));
    placed(meta, x);
    meta.append(el('span', { textContent: whereOf(x) + (t.phase_at ? ' · ' + ago(t.phase_at) : '') }));
    // What the program last said. It changes far more often than the card, so peeks() writes it without a rebuild.
    const peek = el('span', { className: 'peek', hidden: true });
    peek.dataset.peek = t.id;
    const made = el('li', { className: 'card ' + phase }, el('button', { type: 'button', className: 'open', onclick: () => open(t.id, t.form) },
      el('span', { className: 'tool ' + t.tool, html: ICON[t.tool] }), el('span', { className: 'text' }, named(x), meta, peek)));
    // A terminal that waits on a question is answered below, where the question is read: Enter would say yes to it.
    if (t.state === 'running' && phase !== 'confirm') made.append(el('button', { type: 'button', className: 'side say', ariaLabel: '给这个终端发消息', html: ICON.say, onclick: () => say(t) }));
    if (running(t)) made.append(el('button', { type: 'button', className: 'side', ariaLabel: '结束这个终端', html: ICON.stop, onclick: () => endTerminal(t) }));
    // A question the program waits on is answered here, under what it asks: the question is on the card, so nothing
    // is allowed unseen. The two keys are the ones Claude Code and Codex take for "yes" and "no".
    if (phase === 'confirm' && (t.asks || []).length) {
      made.classList.add('asking');
      // (a conversation answers with its own buttons, on its page)
      made.append(el('div', { className: 'ask' }, el('pre', { textContent: t.asks.join('\n') }), el('div', { className: 'answers' },
        el('button', { type: 'button', className: 'yes', textContent: t.form === 'chat' ? '去确认' : '选择授权范围', onclick: () => open(t.id, t.form) }),
        t.form === 'chat' ? '' : el('span', { className: 'there', textContent: '在原终端选择一次、会话内或长期允许' }))));
    }
    return made;
  }
  // A writer lock means the conversation is held, not that a computer window is showing it.
  function sessionHost(s) {
    if (s.host) return s.host;
    // Older agents did not report host; never infer a visible window from a lock.
    return 'unknown';
  }
  function sessionActivity(s) {
    if (s.activity === 'active' || s.activity === 'locked' || s.activity === 'history') return s.activity;
    if (!s.live) return 'history';
    return sessionHost(s) === 'cli' ? 'active' : 'locked';
  }
  const hostLabel = { cli: '电脑上打开着', shared: '被电脑上的应用占用', remote: '被另一个远程终端占用', unknown: '被电脑上的程序占用' };
  function sessionCard(x) {
    const s = x.s, meta = el('span', { className: 'meta' }), phase = x.phase;
    if (phase) {
      // told of by its tool: a card like a terminal's, with where it runs in place of "from the phone"
      const see = () => { if (!usable()) return toast('电脑离线，暂时无法打开'); takeOver(s); };
      meta.append(pill(phase, PHASE[phase][0]));
      placed(meta, x);
      // it is open on the computer and stays there: said so, not as something a program holds
      meta.append(el('span', { textContent: whereOf(x) }));
      const made = el('li', { className: 'card ' + (phase === 'waiting' ? 'done' : phase) }, el('button', { type: 'button', className: 'open', onclick: see },
        el('span', { className: 'tool ' + s.tool, html: ICON[s.tool] }), el('span', { className: 'text' }, named(x), meta, el('span', { className: 'peek', hidden: !s.last || phase === 'confirm', textContent: s.last || '' }))));
      if (can(s, 'say') && phase !== 'confirm') made.append(el('button', { type: 'button', className: 'side say', ariaLabel: '给这段对话发消息', html: ICON.say, onclick: () => tell(s) }));
      if (phase === 'confirm' && s.ask) {
        made.classList.add('asking');
        const answers = approvalActions(s);
        answers.append(el('button', { type: 'button', className: 'see', textContent: '看对话', onclick: see }));
        made.append(el('div', { className: 'ask' }, el('pre', { textContent: s.ask.text }), answers));
      }
      return made;
    }
    // one that a program holds without using it (a desktop app keeps what it has loaded) is said to be idle there
    if (s.live) meta.append(pill(x.state === 'active' && s.status === 'busy' ? 'busy' : x.state === 'active' ? 'pc' : 'locked', s.now === false ? '电脑上闲置' : hostLabel[sessionHost(s)] || hostLabel.unknown));
    else if (x.hidden) meta.append(pill('locked', '已隐藏'));
    placed(meta, x);
    meta.append(el('span', { textContent: whereOf(x) + ' · ' + ago(s.updated) }));
    return el('li', { className: 'card' }, el('button', { type: 'button', className: 'open', onclick: () => { if (!usable()) return toast('电脑离线，暂时无法打开'); if (s.live) takeOver(s); else goOn(s); } },
      el('span', { className: 'tool ' + s.tool, html: ICON[s.tool] }), el('span', { className: 'text' }, named(x), meta), el('span', { className: 'chev', html: ICON.chev })));
  }
  // Lists are rebuilt only when what they show has changed, so a press or a scroll is not interrupted every few seconds.
  function fill(list, cards, signature) {
    if (list.dataset.signature === signature) return;
    list.dataset.signature = signature;
    list.replaceChildren(...cards);
  }
  function applyProjectFold(key) {
    const head = document.querySelector('#project .section-head[data-fold="' + key + '"]');
    if (!head) return;
    const section = head.parentElement, open = projectFolds[key];
    head.classList.toggle('folded', !open);
    head.setAttribute('aria-expanded', String(open));
    Array.from(section.children).slice(1).forEach(child => {
      // The recovery action for hidden conversations stays reachable even folded, and the
      // "more" buttons belong to draw(): their visibility says whether anything is left.
      if (child.id !== 'hidden-history' && !child.id.startsWith('more-')) child.hidden = !open;
    });
  }
  function toggleProjectFold(key) { projectFolds[key] = !projectFolds[key]; applyProjectFold(key); }
  // one list of things: its cards, and what they are known by
  const show = (list, shown, more) => fill(list, shown.map(card), JSON.stringify([shown.map(signed), more, data.device.features, usable()]));
  const stamp = minutes => Math.floor(Date.now() / (minutes * 60000));
  function peeks() {
    const said = {};
    (data.terminals || []).forEach(t => { if (running(t)) said[t.id] = t.said || ''; });
    document.querySelectorAll('[data-peek]').forEach(node => {
      const text = said[node.dataset.peek] || '';
      if (node.textContent !== text) node.textContent = text;
      if (node.hidden !== !text) node.hidden = !text;
    });
  }

  function draw() {
    const device = data.device, ready = usable(), names = device.workspaces || [];
    $('loading').hidden = true;
    $('dot').className = ready ? 'on' : 'off';
    const offline = $('offline');
    offline.hidden = ready;
    if (!ready) offline.replaceChildren(el('b', { textContent: device.online ? '电脑端暂停了手机访问' : '电脑离线' }),
      el('span', { textContent: device.online ? '在电脑上的 Remote AI“设置”里打开“允许手机访问”。' : '请确认电脑已开机且 Remote AI 正在运行。使用公网隧道时，退出程序或重启电脑后地址会变更，需要重新扫码。' }));
    if (project && !names.includes(project)) { project = ''; }
    const everything = things(), mine = everything.filter(x => x.live), lists = tab === 'tasks' || tab === 'projects';
    $('overview').hidden = !!project || !lists; $('project').hidden = !project;
    $('together').hidden = tab !== 'together'; $('me').hidden = tab !== 'me';
    $('home').dataset.tab = tab;
    // In the app the arrow on the first page leads to the workbench, where every computer is listed.
    $('back').hidden = !project && !(native && native.home);
    $('back').setAttribute('aria-label', project ? '返回全部项目' : '返回全部电脑');
    $('heading').textContent = project || TABS[tab];
    document.querySelector('#home .top').classList.toggle('large', !project);
    $('new-task').hidden = tab !== 'tasks'; $('settings').hidden = tab !== 'me';
    document.querySelectorAll('#tabs button').forEach(button => { if (button.dataset.tab === tab) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current'); });
    // conversations open on the computer are counted with the terminals, by how they stand
    const stands = (list, ...phases) => list.filter(x => x.live && phases.includes(x.phase)).length;
    const waiting = stands(everything, 'confirm'), busy = everything.filter(working).length, opened = everything.filter(x => x.live && !needsYou(x) && !working(x)).length;
    // (inside the app, which lists several computers, the line says first which one this is)
    $('device-text').textContent = (computerName ? computerName + ' · ' : '') + (!ready ? (device.online ? '已暂停访问' : '电脑离线')
      : project || !lists ? '电脑在线' : [waiting ? waiting + ' 个等你确认' : '', busy ? busy + ' 个正在运行' : '', opened ? opened + ' 个已打开' : ''].filter(Boolean).join(' · ') || '电脑在线');
    // what waits for an answer is counted on its tab, whichever tab is open
    $('tab-count').hidden = !waiting; $('tab-count').textContent = waiting > 9 ? '9+' : waiting;
    if (!lists) { if (views[tab]) views[tab].draw(); return; }

    if (!project) {
      const all = everything.filter(pass), asked = $('query').value.trim().toLowerCase().split(/\s+/).filter(Boolean), searching = asked.length > 0, tick = [stamp(1), ready], tasks = tab === 'tasks';
      $('find').hidden = tasks ? !everything.length : names.length < 2;
      $('query').placeholder = tasks ? '搜索对话、终端和项目' : '搜索项目';
      drawFilters(everything);
      $('results').hidden = !searching || !tasks; $('sections').hidden = searching || !tasks;
      $('tasks-empty').hidden = everything.length > 0; $('first-task').hidden = !ready;
      if (!tasks) { /* the projects only */ } else if (searching) {
        // everything is looked through, what is hidden too; what is open comes first
        const found = all.filter(x => !x.twice && asked.every(word => wordsOf(x).includes(word))).sort((a, b) => (b.live - a.live) || (b.pinned - a.pinned) || (a.live ? byRank(a, b) : byTime(a, b)));
        show($('results-list'), found.slice(0, resultsShown), tick);
        $('results-summary').textContent = found.length ? found.length + ' 个' : '没有找到';
        $('more-results').hidden = found.length <= resultsShown;
      } else {
        const listed = all.filter(x => !x.twice), needs = listed.filter(needsYou).sort(byOpen), rest = listed.filter(x => !needsYou(x));
        const pinned = rest.filter(x => x.pinned && !x.live).sort(byTime), free = rest.filter(x => !x.pinned || x.live);
        const active = free.filter(working).sort(byOpen);
        const opened = free.filter(x => x.live && !working(x)).sort(byOpen);
        const recent = free.filter(x => !x.live && ((x.state === 'history' && !x.hidden) || x.state === 'ended')).sort(byTime);
        $('needs').hidden = !needs.length;
        show($('needs-list'), needs, tick);
        $('needs-summary').textContent = needs.length + ' 个等你确认';
        $('pinned').hidden = !pinned.length;
        show($('pinned-list'), pinned, tick);
        $('pinned-summary').textContent = pinned.length + ' 个';
        $('active').hidden = !active.length;
        show($('active-list'), active, tick);
        $('active-summary').textContent = active.length + ' 个';
        $('opened').hidden = !opened.length;
        show($('opened-list'), opened, tick);
        $('opened-summary').textContent = opened.length + ' 个 · 等待输入或已闲置';
        $('recent').hidden = !recent.length;
        show($('recent-list'), recent.slice(0, recentShown), [stamp(5), ready]);
        $('recent-summary').textContent = recent.length + ' 个';
        $('more-recent').hidden = recent.length <= recentShown;
        $('nothing').hidden = !(filter.tool || filter.source) || needs.length + pinned.length + active.length + recent.length + opened.length > 0;
      }
      // the projects: all of them, or those the search names
      // (with a filter chosen, those of that tool or kind)
      const sessions = everything.filter(x => !x.t), folderOf = name => (device.projects || []).find(p => p.name === name) || {};
      const wanted = (searching ? names.filter(name => asked.every(word => name.toLowerCase().includes(word))) : names)
        .filter(name => passProject(folderOf(name), everything.filter(x => x.project === name)));
      const list = wanted.map(name => {
        const folder = folderOf(name);
        const own = mine.filter(x => x.project === name), count = sessions.filter(x => x.project === name).length;
        // a project says by what it is used, in the words of the filters: by what its folder is used for, and by what is in it now
        const uses = new Set((folder.kinds || []).map(sourceOfKind).concat(everything.filter(x => x.project === name).map(x => x.source)).filter(Boolean));
        const source = Object.keys(SOURCE).filter(key => uses.has(key)).map(key => SOURCE[key]).join(' · ');
        const need = own.filter(needsYou).length, work = own.filter(working).length, opened = own.length - need - work;
        const meta = el('span', { className: 'meta' });
        if (need) { const c = el('span', { className: 'count', textContent: need + ' 个等你确认' }); c.prepend(el('i')); c.style.setProperty('--c', 'var(--bad)'); c.style.color = 'var(--bad)'; meta.append(c); }
        if (work) { const c = el('span', { className: 'count', textContent: work + ' 个正在运行' }); c.prepend(el('i')); c.style.setProperty('--c', 'var(--busy)'); c.style.color = 'var(--busy)'; meta.append(c); }
        if (opened) meta.append(el('span', { textContent: opened + ' 个已打开' }));
        meta.append(el('span', { textContent: (source ? source + ' · ' : '') + (count ? count + ' 段对话' : '还没有对话') }));
        const made = held(el('li', { className: 'card' }, el('button', { type: 'button', className: 'open', onclick: () => enter(name) },
          el('span', { className: 'tool folder', html: ICON.folder }), el('span', { className: 'text' }, el('b', { textContent: name }), meta), el('span', { className: 'chev', html: ICON.chev }))),
          () => manageProject(name, folder));
        return { name, card: made, sign: [name, need, opened, work, own.length, count, folder.path, source] };
      });
      const cardsP = list.map(x => x.card);
      if (ready && !searching) cardsP.push(el('li', { className: 'card add' }, el('button', { type: 'button', className: 'open', onclick: addProject, html: ICON.plus + '<span>添加项目</span>' })));
      // 项目 lists them; 任务 shows the ones a search names, under what it found (app.css, by `data-finding`)
      $('projects').hidden = searching && !list.length;
      $('home').dataset.finding = searching ? 'yes' : 'no';
      fill($('project-list'), cardsP, JSON.stringify([list.map(x => x.sign), ready, searching]));
      $('project-summary').textContent = searching || filter.tool || filter.source ? list.length + ' 个' : names.length ? names.length + ' 个' : '还没有项目';
      return;
    }

    const folder = (device.projects || []).find(p => p.name === project);
    $('project-path').textContent = folder ? '\u200e' + folder.path : '';      // read from the left although it is cut at the left
    window.RemoteCliPage.begin.draw(project);
    const inside = everything.filter(x => x.project === project), tick = [stamp(1), ready];
    const own = inside.filter(working).sort(byOpen), confirms = inside.filter(needsYou).sort(byOpen);
    $('p-needs').hidden = !confirms.length;
    show($('p-needs').querySelector('ul'), confirms, tick);
    $('p-active').hidden = !own.length;
    $('p-active').querySelector('p').textContent = own.length + ' 个';
    applyProjectFold('active');
    show($('p-active').querySelector('ul'), own, tick);
    const here = inside.filter(x => !x.t), open2 = inside.filter(x => x.live && !working(x) && !needsYou(x)).sort(byOpen);
    $('p-live').hidden = !open2.length;
    $('p-live').querySelector('p').textContent = open2.length + ' 个';
    applyProjectFold('live');
    show($('p-live').querySelector('ul'), open2, tick);
    // what was pinned comes first; what was hidden is left out until it is asked for
    const saved = here.filter(x => !x.live && x.state === 'history').sort((a, b) => (b.pinned - a.pinned) || byTime(a, b)), put = saved.filter(x => x.hidden).length;
    const all = saved.filter(x => hiddenShown || !x.hidden), wanted = $('search').value.trim().toLowerCase();
    const found = wanted ? all.filter(x => x.title.toLowerCase().includes(wanted)) : all;
    $('p-history').hidden = !saved.length;
    $('search').hidden = all.length <= 8;
    $('history-count').textContent = wanted ? found.length + ' / ' + all.length + ' 段' : all.length + ' 段';
    show($('p-history').querySelector('ul'), found.slice(0, shown), [stamp(5), ready]);
    $('more-history').hidden = found.length <= shown;
    $('hidden-history').hidden = !put;
    $('hidden-history').textContent = hiddenShown ? '不显示已隐藏的 ' + put + ' 段' : '已隐藏 ' + put + ' 段 · 显示';
    applyProjectFold('history');
    // Kept terminal screens and saved conversations have different actions: view a screen or resume a conversation.
    const ended = inside.filter(x => x.t && !x.live).sort(byTime);
    $('p-ended').hidden = !ended.length;
    show($('p-ended').querySelector('ul'), ended.slice(0, endedShown), [stamp(5), ready]);
    $('more-ended').hidden = ended.length <= endedShown;
    applyProjectFold('ended');
    $('empty').hidden = inside.length > 0;
  }

  // ---- moving between the overview and one project; the phone's back key goes back to the overview
  // To another tab. A project in detail is left with its tab; pressing 项目 while in one leads back to the list.
  // Inside the app the tabs are the app's own, the same on every screen: each leads to its list over all the
  // computers, and this page is one computer's part of the tab it was opened for. (An app of an earlier version
  // has 协作 and 我的 only.)
  const computerName = native && native.computer ? String(native.computer() || '') : '';
  const appTabs = !native || !native.appPage ? [] : native.appTabs ? String(native.appTabs()).split(',') : ['together', 'me'];
  // (`own`: this page's part of the tab is meant, whoever has the tabs)
  function go(name, own) {
    if (!TABS[name]) return;
    if (!own && appTabs.includes(name)) { native.appPage(name); return; }
    const left = project;
    if (left) { project = ''; cameFrom = 'projects'; if (history.state && history.state.project) history.back(); }
    if (name !== tab || left) scrollTo(0, 0);
    tab = name;
    try { sessionStorage.setItem('rcli-tab', tab); } catch (error) { /* no storage */ }
    if ($('query').value && name !== 'tasks' && name !== 'projects') $('query').value = '';
    if (!data) return;
    if (views[tab] && views[tab].open) views[tab].open();
    draw();
  }
  // A project is entered from the list of projects, or from a card on 任务: the way back leads to where one came from.
  let cameFrom = 'projects';
  function enter(name) { cameFrom = tab; tab = 'projects'; project = name; shown = 2; endedShown = 8; hiddenShown = false; Object.keys(projectFolds).forEach(key => { projectFolds[key] = false; }); $('search').value = ''; history.pushState({ project: name }, ''); scrollTo(0, 0); draw(); }
  window.addEventListener('popstate', event => {
    const left = project;
    project = event.state && event.state.project || '';
    if (project) tab = 'projects'; else if (left && tab === 'projects') { tab = cameFrom; cameFrom = 'projects'; }
    if (data) draw();
  });
  // A project opened by a link (the workbench does that) has no overview behind it in the history.
  $('back').addEventListener('click', () => {
    if (project && history.state && history.state.project) return history.back();
    if (native && native.home) return native.home();
    project = ''; tab = cameFrom; cameFrom = 'projects'; history.replaceState(null, '', location.pathname); draw();
  });
  $('new-task').addEventListener('click', () => beginAnywhere());
  $('first-task').addEventListener('click', () => beginAnywhere());
  // While something is typed the tabs make room: the keyboard takes the foot of the screen.
  document.addEventListener('focusin', event => { if (event.target.matches && event.target.matches('#home input, #home textarea')) document.body.classList.add('typing'); });
  document.addEventListener('focusout', () => document.body.classList.remove('typing'));
  $('search').addEventListener('input', () => { shown = 2; draw(); });
  $('more-history').addEventListener('click', () => { shown += 30; draw(); });
  $('more-ended').addEventListener('click', () => { endedShown += 30; draw(); });
  $('hidden-history').addEventListener('click', () => { hiddenShown = !hiddenShown; if (hiddenShown) { shown = Math.max(shown, 30); projectFolds.history = true; } draw(); });
  $('query').addEventListener('input', () => { resultsShown = 30; draw(); });
  $('more-results').addEventListener('click', () => { resultsShown += 30; draw(); });
  $('more-recent').addEventListener('click', () => { recentShown += 20; draw(); });
  document.querySelectorAll('#project .section-head[data-fold]').forEach(head => {
    const key = head.dataset.fold;
    head.addEventListener('click', () => toggleProjectFold(key));
    head.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); toggleProjectFold(key); } });
  });

  async function refresh() {
    clearTimeout(timer);
    const first = !data;
    try {
      const asked = early; early = null;
      data = (asked && await asked) || await api('api/terminal');
      if (data.device.shell) TOOLS.shell = data.device.shell;
      if (first && views[tab] && views[tab].open) views[tab].open();
      draw(); peeks();
      if (anew) {
        anew = false; history.replaceState(history.state, '', location.pathname + (project ? '?project=' + encodeURIComponent(project) : ''));
        beginAnywhere().then(leave);
      }
      if (take) {
        const wanted = (data.sessions || []).find(s => s.id === take && !s.terminal);
        take = ''; history.replaceState(history.state, '', location.pathname + (project ? '?project=' + encodeURIComponent(project) : ''));
        if (wanted && usable()) (wanted.live ? takeOver(wanted) : start(wanted.tool, wanted.dir, wanted.id, false)).then(leave);
      }
    }
    catch (error) { if (!$('home').hidden) { $('device-text').textContent = error.message; $('dot').className = 'off'; } }
    if (!$('home').hidden) timer = setTimeout(refresh, document.hidden ? 15000 : 2500);
  }
  document.addEventListener('visibilitychange', () => { if (!document.hidden && !$('home').hidden) refresh(); });
  window.addEventListener('pageshow', event => { if (event.persisted && !$('home').hidden) refresh(); });      // back from a terminal

  // What the views of their own work with: the page's helpers, what the computer last said, and the ways into a
  // conversation. A view names its tab and says how it is opened (asked for what it shows) and drawn.
  window.RemoteCliPage = {
    $, el, svg, ICON, TOOLS, api, newId, toast, openSheet, ask, ago, base, things, whereOf, wayOf, usable, running, phaseOf, go, takeOver, open, start, chat, chatOk, held, saved, keep,
    get data() { return data; },
    get tab() { return tab; },
    has: feature => !!data && (data.device.features || []).includes(feature),
    refresh: () => refresh(),
    view(name, made) { views[name] = made; if (data) { if (tab === name && made.open) made.open(); draw(); } }
  };

  (async () => {
    try {
      // The app hands the password over once, after the "#"; it never travels in a request line. The program on the
      // computer opens its own window with a ticket instead: good once, for a minute, and of no use afterwards.
      const handed = new URLSearchParams(location.hash.slice(1)), given = handed.get('p'), ticket = handed.get('t');
      const wanted = new URLSearchParams(location.search).get('open') || '';
      if (given || ticket) {
        history.replaceState(null, '', location.pathname + location.search);
        try { await api('api/login', ticket ? { ticket } : { password: given }); }
        catch (error) { showLogin(); $('login-error').textContent = ticket ? '这个窗口的登录已过期，请从电脑端程序重新打开' : error.message; return; }
      }
      if (!/^[a-f0-9]{32}$/.test(wanted)) early = fetch('api/terminal', { credentials: 'same-origin' }).then(reply => reply.ok ? reply.json() : null).catch(() => null);
      const session = await (await fetch('api/session', { credentials: 'same-origin' })).json();
      if (!session.signed_in) return showLogin();
      // ?open=<terminal> goes straight to that terminal: the computer's own window opens the one that was picked.
      if (/^[a-f0-9]{32}$/.test(wanted)) return location.replace('terminal/?id=' + wanted);
      // ?project=<name> opens one project directly, for a link or a picture of that page.
      project = history.state && history.state.project || new URLSearchParams(location.search).get('project') || '';
      if (project) tab = 'projects';
      else { const requested = new URLSearchParams(location.search).get('tab'); if (['tasks', 'projects', 'together', 'me'].includes(requested)) tab = requested; }
      $('home').hidden = false; $('tabs').hidden = false;
      refresh();
    } catch (error) { showLogin(); $('login-error').textContent = '连不上服务，请检查地址'; }
  })();
})();
