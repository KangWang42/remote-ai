/* The list pages, under four tabs at the foot. 任务: everything on the computer at a glance, by what needs you (what
   waits for an answer, what is pinned, what is at work, what was used lately), found by one search and told apart by
   tool and by where it runs. 项目: the projects, and one project in detail. 协作 and 我的 are views of their own
   (together.js, me.js), and a new conversation is begun by a module of its own (begin.js): this page gives them
   what it has (window.RemoteCliPage). Inside the app the tabs are the app's: see `go`. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  // The plain terminal is called what the computer calls it: PowerShell on Windows, the person's shell elsewhere.
  const TOOLS = { claude: 'Claude Code', codex: 'Codex', agy: 'Antigravity CLI', opencode: 'OpenCode', qoder: 'Qoder', cline: 'Cline', pi: 'Pi', shell: '终端' };
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  const native = window.RemoteCliNative || null;
  // The look belongs to the app, not to one computer: inside the app it is kept there, so every computer's pages
  // open in the same skin and text size. A browser keeps it per address, as it keeps everything else. So is the
  // kind of conversation its owner begins (begin.js): in an application, or in a terminal.
  const LOOK = ['skin', 'text', 'readfont', 'readsize', 'readline', 'newtool'];
  const saved = (key, fallback) => {
    if (native && native.pref && LOOK.includes(key)) { const chosen = native.pref(key); if (chosen) return chosen; }
    try { return localStorage.getItem('rcli-' + key) || fallback; } catch (error) { return fallback; }
  };
  const keep = (key, value) => {
    if (native && native.setPref && LOOK.includes(key)) native.setPref(key, value);
    try { localStorage.setItem('rcli-' + key, value); } catch (error) { /* private window */ }
  };
  let data = null, project = '', timer = 0, shown = 16, endedShown = 8, toastTimer = 0;
  let relayVersion = '';
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
    agy: svg('<path d="M3 17c5 0 6-11 9-11s4 11 9 11M8 18l4-4 4 4"/>'),
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
    pi: svg('<circle cx="12" cy="12" r="8.5"/><path d="M9 9.5c0 3 6 0 6 3s-6 0-6 3"/>'),
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
  if (asked && (asked === 'auto' || window.RemoteCliSkins[asked])) keep('skin', asked);
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
    const shade = skin.bg || skin.t.background;
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = shade;
    if (native) native.chrome(shade);
  }
  paint();
  if (window.RemoteCliOnSystemTheme) window.RemoteCliOnSystemTheme(paint);
  $('back').innerHTML = ICON.back; $('settings').innerHTML = ICON.gear; $('new-task').innerHTML = svg('<path d="M12 5v14M5 12h14"/>', 24);
  $('organize').innerHTML = svg('<path d="M12 3.5l1.9 5.6 5.6 1.9-5.6 1.9L12 18.5l-1.9-5.6-5.6-1.9 5.6-1.9z"/>', 16) + '<span>整理</span>';
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
  // `fullWindow`: it fills the screen and is left by its own way back (a conversation), instead of rising from the bottom.
  // A sheet is told that it was closed a moment after it is closed. One that is opened in that moment waits for it:
  // else it would take the word meant for the one before, and answer "nothing chosen" while it is still on the screen.
  let sheetGone = Promise.resolve(), backFromReader = null;
  window.addEventListener('popstate', event => {
    if (!backFromReader) return;
    event.stopImmediatePropagation();
    backFromReader();
  });
  function openSheet(build, fullWindow) {
    return sheetGone.then(() => new Promise(done => {
      const form = $('sheet-form'), sheet = $('sheet');
      const nestedReader = fullWindow && window.RemoteSheet.current;
      form.textContent = '';
      sheet.classList.toggle('window', !!fullWindow);
      let answer = null;
      build(form, value => { answer = value; sheet.close(); });
      if (!fullWindow) form.append(el('button', { type: 'button', className: 'cancel', textContent: '取消', onclick: () => sheet.close() }));
      sheetGone = new Promise(gone => {
        let finished = false, backTimer = 0;
        const finish = () => {
          if (finished) return;
          finished = true;
          clearTimeout(backTimer);
          backFromReader = null;
          gone();
          done(answer);
        };
        if (nestedReader) {
          history.pushState({ ...history.state, conversationReader: true }, '');
          // The full-screen reader is on top of a collaboration sheet whose Back must wait.
          backFromReader = () => { if (sheet.open) sheet.close(); else finish(); };
        }
        sheet.onclose = () => {
          if (nestedReader && history.state?.conversationReader) {
            backTimer = setTimeout(finish, 400);
            history.back();
          } else finish();
        };
      });
      sheet.onclick = event => { if (event.target === sheet) sheet.close(); };       // a tap beside the sheet
      sheet.showModal();
    }));
  }
  function closeReader() {
    const reader = $('sheet');
    if (!reader || !reader.open) return false;
    reader.close();
    return true;
  }
  // A question with a few answers, optionally with fields to fill in; resolves with the answer, or null when dismissed.
  // It rises as the sheet of ui/sheet.js: a single answer that ends or saves something is its one button at the foot;
  // several are rows with a mark for what each does, those that end or delete something apart at the end.
  const MARK = { pin: 'pin', hide: 'hide', finish: 'stop', end: 'stop', handoff: 'hand', add: 'folder', enter: 'enter', delete: 'trash', remove: 'trash', rename: 'pen',
    resume: 'term', chat: 'chat', 'chat-close': 'chat', close: 'term', copy: 'copy', organize: 'spark', follow_up: 'clock', select: 'tick' };
  function ask(title, text, choices, fields) {
    const kit = window.RemoteSheet.ui;
    return window.RemoteSheet.open({ title, build(p) {
      const inputs = {};
      const answer = c => p.sheet.close(fields ? Object.assign({ choice: c.value }, ...Object.keys(inputs).map(k => ({ [k]: inputs[k].value.trim() }))) : c.value);
      const single = choices.length === 1 && (fields || choices[0].kind === 'danger' || choices[0].kind === 'solid');
      if (text || (single && !fields)) p.body.append(kit.summary(text || title, choices.some(c => c.kind === 'danger') ? 'bad' : '', choices.some(c => c.kind === 'danger') ? 'stop' : 'spark'));
      (fields || []).forEach(f => {
        inputs[f.key] = kit.input({ id: 'field-' + f.key, value: f.value || '', placeholder: f.placeholder || '', max: f.max || 240, label: f.label });
        inputs[f.key].addEventListener('keydown', event => { if (event.key === 'Enter' && single) { event.preventDefault(); answer(choices[0]); } });
        p.body.append(kit.section(f.label, inputs[f.key]));
        setTimeout(() => { inputs[f.key].focus(); inputs[f.key].select(); }, 280);
      });
      if (single) { p.foot.append(kit.go(choices[0].label, async () => answer(choices[0]), { kind: choices[0].kind === 'danger' ? 'danger' : '', id: 'ask-go' }).button); return; }
      const row = c => kit.row({ icon: MARK[c.value] || 'chev', tint: c.kind === 'danger' ? '' : MARK[c.value] === 'hand' ? 'claude' : 'muted', label: c.label, detail: c.sub || '', danger: false, act: () => answer(c) });
      const plain = choices.filter(c => c.kind !== 'danger'), danger = choices.filter(c => c.kind === 'danger');
      if (plain.length) p.body.append(kit.group(...plain.map(row)));
      if (danger.length) p.body.append(kit.group(...danger.map(c => { const node = row(c); node.classList.add('warn'); return node; })));
    } }, title).result;
  }
  function showLogin() {
    clearTimeout(timer);
    $('home').hidden = true; $('tabs').hidden = true; $('login').hidden = false;
    const native = window.RemoteCliNative;
    const box = $('login-native');
    if (box) {
      box.hidden = !native;
      if ($('login-forget')) $('login-forget').hidden = !(native && native.forgetCurrent);
    }
    $('password').focus();
  }
  if ($('login-back')) $('login-back').addEventListener('click', () => {
    const native = window.RemoteCliNative;
    if (native && native.home) native.home();
  });
  if ($('login-forget')) $('login-forget').addEventListener('click', () => {
    const native = window.RemoteCliNative;
    if (native && native.forgetCurrent) native.forgetCurrent();
  });
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
  const NATIVE_APPS = ['codex-desktop', 'agy-desktop'];
  const isNativeApp = way => NATIVE_APPS.includes(way);
  const needsTurnForStop = way => way === 'codex-desktop';
  const wayOf = (s, thing) => !s || !s.live ? '' : s.ways ? s.ways[thing] || ''
    : thing === 'end' ? (s.can_takeover === true ? 'process' : '') : !can(s, thing) ? ''
    : isNativeApp(s.transport) && (thing !== 'answer' || s.transport === 'agy-desktop') ? s.transport : 'hook';
  const whyNot = (s, thing, usual) => (s && s.why && s.why[thing]) || usual;
  const running = t => t.state === 'running' || t.state === 'starting';
  const usable = () => data && data.device.online && data.device.enabled;
  const canEnd = s => wayOf(s, 'end') === 'process' && (data.device.features || []).includes('end');

  // Reading a transcript alone does not imply that the original application accepts remote input.
  const controlMode = session => {
    if (!session || !session.live) return 'saved';
    return isNativeApp(session.transport) || can(session, 'say') || can(session, 'answer') || can(session, 'stop') ? 'shared' : 'read';
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
        p.payload.action === 'say' && isNativeApp(p.payload.transport) && p.payload.text === p.text &&
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
      const nativePending = pending.filter(p => isNativeApp(p.payload.transport));
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
  const startParams = new URLSearchParams(location.search);
  let take = startParams.get('take') || '', anew = startParams.get('new') === '1', fromBench = !!take || anew || startParams.get('from') === 'bench';
  let readFromBench = startParams.get('read') || '';
  let organizeFromBench = startParams.get('organize') === '1';
  let wantedTeam = startParams.get('do') === 'team' && /^[a-f0-9]{16,32}$/.test(startParams.get('item') || '') ? startParams.get('item') : '';
  // A terminal's page; for one that runs a conversation through its tool's structured mode, the conversation's (chat/).
  function open(terminal, form, extra) {
    let url = (form === 'chat' ? 'chat/' : 'terminal/') + '?id=' + encodeURIComponent(terminal) + (fromBench ? '&from=bench' : '');
    if (extra && extra.team) {
      url += '&team=' + encodeURIComponent(extra.team);
      if (project) url += '&returnProject=' + encodeURIComponent(project);
      else if (tab && tab !== 'together') url += '&returnTab=' + encodeURIComponent(tab);
    } else if (!fromBench) url += '&returnList=' + (extra?.returnList || saveListPosition());
    location.href = url;
  }
  // What the app's own lists opened this page for (a new conversation, one conversation's window) leads back to
  // them when it is left without going on to a terminal's page.
  const leave = left => { if (!left && fromBench && native && native.home) native.home(); };
  // Resolves with whether the page is left for the terminal that was started.
  function applyParams(payload, tool, params) {
    const d = window.RemoteTeamDefaults;
    const saved = d ? d.savedParams(tool, data && data.device && data.device.max_mode) : {};
    const chosen = Object.assign({}, saved, params || {});
    if (chosen.model) payload.model = chosen.model;
    if (chosen.effort) payload.effort = chosen.effort;
    if (chosen.mode && tool !== 'agy' && pageHost.has('chat-params')) payload.mode = chosen.mode;
    return payload;
  }
  async function start(tool, dir, session, takeover, fork, extra, params) {
    const payload = { action: 'start', tool, dir, history: false };
    if (session) Object.assign(payload, { session, takeover: !!takeover });
    if (fork) payload.fork = true;
    if (!session) applyParams(payload, tool, params);
    const result = await run(payload, '正在电脑上启动 ' + TOOLS[tool] + '…');
    if (result) open(result.terminal, 'terminal', extra);
    return !!result;
  }
  const agyShellTitle = dir => 'Antigravity CLI' + (dir ? ' · ' + dir : '');
  const isAgyShell = t => !!t && t.tool === 'shell' && (/^Antigravity CLI(?:\s|$|·)/.test(t.title || '') || agyShellIds().includes(t.id));
  function agyShellIds() {
    try { const list = JSON.parse(sessionStorage.getItem('agy-shell-ids') || '[]'); return Array.isArray(list) ? list : []; } catch (error) { return []; }
  }
  function rememberAgyShell(id) {
    try {
      const ids = agyShellIds().filter(one => one !== id);
      ids.push(id);
      sessionStorage.setItem('agy-shell-ids', JSON.stringify(ids.slice(-16)));
    } catch (error) { /* title rename still identifies it */ }
  }
  async function startAgyShell(dir) {
    const result = await run({ action: 'start', tool: 'shell', dir, history: false }, '正在电脑上启动 agy…');
    if (!result) return false;
    rememberAgyShell(result.terminal);
    try { sessionStorage.setItem('agy-shell-run', result.terminal); } catch (error) { /* command can be entered manually */ }
    try { await api('api/terminal', { id: newId(), action: 'rename', terminal: result.terminal, title: agyShellTitle(dir) }); } catch (error) { /* best-effort title update */ }
    open(result.terminal);
    return true;
  }
  // A conversation that the computer runs through its tool's structured mode, shown on the phone as a conversation
  // (web/chat/): a new one, or a saved one gone on with (`session`). The computer says whether it can (`chat`).
  // the tools the computer runs as a conversation, and what it says of itself where it does (Antigravity CLI: by ACP)
  const CHAT_TOOLS = { claude: 'chat', codex: 'chat', agy: 'chat-agy' };
  const chatOk = tool => !!data && (data.device.features || []).includes('chat') && !!CHAT_TOOLS[tool] && (data.device.features || []).includes(CHAT_TOOLS[tool]) && (data.device.tools || []).includes(tool);
  async function chat(tool, dir, session, takeover, extra, params) {
    const payload = { action: 'start', tool, dir, history: false, form: 'chat' };
    if (session) Object.assign(payload, { session, takeover: !!takeover });
    if (!session) applyParams(payload, tool, params);
    const result = await run(payload, '正在电脑上启动 ' + TOOLS[tool] + '…');
    if (result) open(result.terminal, 'chat', extra);
    return !!result;
  }
  // A saved conversation is gone on with as a conversation where the computer runs its tool so (it can be shown on the
  // tool's own screen from there), else in a terminal. The other way is behind a held press.
  const asChat = s => chatOk(s.tool);
  const goOn = (s, extra) => asChat(s) ? chat(s.tool, s.dir, s.id, false, extra) : start(s.tool, s.dir, s.id, false, false, extra);

  const speakerOf = s => s && (/Antigravity 桌面版/.test(s.origin || s.kind || '') || s.transport === 'agy-desktop') ? 'Antigravity' : TOOLS[s.tool];
  async function readHistory(session) {
    if (!usable()) { toast('电脑离线，暂时无法打开'); return false; }
    if (!pageHost.has('peek')) return goOn(session);
    const key = fromBench ? '' : saveListPosition();
    const ui = window.RemoteSheet.ui;
    const title = session.organize?.title || session.title || speakerOf(session);
    const sheet = window.RemoteSheet.open({ full: true, title, build(p) {
      const record = el('div', { className: 'said saved-reader', ariaLabel: '对话记录' });
      const load = async () => {
        record.replaceChildren(ui.note('正在读取对话…'));
        try {
          const reply = await api('api/conversation', { id: newId(), session: session.id });
          const content = el('div');
          if (reply.more) content.append(ui.note('更早的内容在电脑上'));
          if (!reply.messages?.length) content.append(ui.empty('这段对话暂无可读内容'));
          await writeSaid(content, reply.messages || [], speakerOf(session), new Set(), new Map());
          record.replaceChildren(content);
        } catch (error) {
          record.replaceChildren(ui.note(error.message, 'bad'), ui.row({ label: '重试读取', act: load }));
        }
      };
      p.body.append(record);
      p.foot.append(...ui.go('继续对话', () => p.sheet.close(true)).nodes);
      load();
    } });
    const next = await sheet.result;
    if (key) restoreListPosition(key, false);
    if (next && key) return goOn(session, { returnList: key });
    return next ? goOn(session) : false;
  }
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
    if (isNativeApp(s.transport)) return takeOver(s);
    if (!usable()) return toast('电脑离线，暂时不能发送');
    const text = await openSheet((form, finish) => {
      const box = el('textarea', { rows: 3, maxLength: 8000, placeholder: '发送消息', autocapitalize: 'off', spellcheck: false, ariaLabel: '消息' });
      form.append(el('h3', { textContent: s.title }), el('p', { textContent: s.last || speakerOf(s) + ' · ' + (s.dir || base(s.folder)) }),
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
  async function answerAsk(s, allow, scope = 'once', choice = '') {
    const id = s.ask && s.ask.id;
    if (!id || answering.has(id)) return false;
    answering.add(id);
    document.querySelectorAll('[data-approval="' + id + '"] button').forEach(b => b.disabled = true);
    let ok = false;
    try {
      const payload = { id: newId(), session: s.id, action: 'answer', ask: id, allow, scope };
      if (choice) payload.choice = choice;
      const answerWay = wayOf(s, 'answer') || (isNativeApp(s.transport) ? s.transport : '');
      if (isNativeApp(answerWay)) payload.transport = answerWay;
      await api('api/conversation', payload);
      toast(choice ? '已提交选择' : allow ? '已向电脑提交授权' : '已向电脑提交拒绝');
      ok = true;
    } catch (error) { toast(error.message); }
    finally { answering.delete(id); }
    await refresh();
    document.querySelectorAll('[data-approval="' + id + '"] button').forEach(b => b.disabled = false);
    return ok;
  }
  function approvalActions(s, after = () => {}) {
    const options = s.ask.options && s.ask.options.length ? s.ask.options : [{ id: 'once', label: '允许一次', detail: '只允许这一次操作' }];
    const hasChoice = options.some(o => /^choice:\d+$/.test(o.id || ''));
    const hasDeny = options.some(o => o.id === 'deny');
    const answers = el('div', { className: 'answers' + (hasChoice ? ' choices' : '') });
    answers.dataset.approval = s.ask.id;
    if (can(s, 'answer') || !!wayOf(s, 'answer')) {
      options.forEach(o => {
        if (o.id === 'deny') {
          answers.append(el('button', { type: 'button', className: 'no', textContent: o.label || '拒绝', title: o.detail || '', disabled: answering.has(s.ask.id), onclick: () => answerAsk(s, false, 'once').then(after) }));
          return;
        }
        if (/^choice:\d+$/.test(o.id || '')) {
          const detail = o.detail && o.detail !== o.label ? el('small', { textContent: o.detail }) : '';
          answers.append(el('button', { type: 'button', className: 'yes choice-opt', title: o.detail || o.label || '', disabled: answering.has(s.ask.id), onclick: () => answerAsk(s, true, 'once', o.id).then(after) },
            el('span', { textContent: o.label || o.id }), detail));
          return;
        }
        answers.append(el('button', { type: 'button', className: o.id === 'once' ? 'yes' : 'see', textContent: o.label, title: o.detail || '', disabled: answering.has(s.ask.id), onclick: () => {
          if (o.id === 'once') { answerAsk(s, true, o.id).then(after); return; }
          if (answers.querySelector('.approval-rule')) return;
          const rule = el('div', { className: 'approval-rule' }, el('p', { textContent: o.detail || '将在本机保存此允许规则' }),
            el('button', { type: 'button', className: 'yes', textContent: '确认保存并允许', onclick: () => answerAsk(s, true, o.id).then(after) }),
            el('button', { type: 'button', className: 'see', textContent: '取消', onclick: () => rule.remove() }));
          answers.append(rule);
        } }));
      });
      if (!hasDeny) {
        answers.append(el('button', { type: 'button', className: 'no', textContent: hasChoice ? '取消' : '拒绝', disabled: answering.has(s.ask.id), onclick: () => answerAsk(s, false, 'once').then(after) }));
      }
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
  let markdownReady = null;
  function loadMarkdown() {
    if (window.marked && window.DOMPurify) return Promise.resolve(true);
    if (!markdownReady) {
      markdownReady = Promise.all([need('marked.min.js'), need('purify.min.js')])
        .then(() => !!(window.marked && window.DOMPurify), () => false);
    }
    return markdownReady;
  }
  function markdownNode(text, cache) {
    const raw = String(text || '');
    const rich = !!(window.marked && window.DOMPurify);
    if (rich && cache && cache.has(raw)) return cache.get(raw).cloneNode(true);
    if (!rich) return el('div', { className: 'plain', textContent: raw });
    // nothing that could act or load: no links to follow, no pictures, no forms, no styles of its own
    const html = DOMPurify.sanitize(marked.parse(raw, { gfm: true, breaks: true }),
      { FORBID_TAGS: ['style', 'form', 'button', 'textarea', 'select', 'input', 'img', 'a', 'iframe', 'video', 'audio'], FORBID_ATTR: ['style', 'href', 'src'] });
    const body = el('div', { className: 'md', html });
    body.querySelectorAll('table').forEach(table => { const wrap = el('div', { className: 'wide' }); table.replaceWith(wrap); wrap.append(table); });
    if (cache) {
      cache.set(raw, body.cloneNode(true));
      if (cache.size > 160) cache.delete(cache.keys().next().value);
    }
    return body;
  }
  async function writeSaid(box, messages, toolName, phoneTexts, cache) {
    await loadMarkdown();
    const set = text => markdownNode(text, cache);
    let run = null, last = '';
    const keyed = (node, value) => { node.readKey = JSON.stringify(value); return node; };
    messages.forEach(m => {
      if (m.role === 'tool') {
        // The steps stand open, each as a terminal would show it: what it is, the command itself where it is told
        // by what it is for, and the first lines of what came back. The line above them folds a run away.
        if (!run) { run = { list: el('ul'), count: 0 }; run.box = el('details', { className: 'did', open: saved('steps', 'open') !== 'shut' }, el('summary'), run.list); box.append(run.box); }
        run.list.append(el('li', null, el('span', { className: 'what', textContent: m.text }),
          m.detail ? el('code', { className: 'ran', textContent: m.detail }) : '', m.result ? el('pre', { className: 'got', textContent: m.result }) : ''));
        run.count++;
        run.box.firstChild.textContent = '做了 ' + run.count + ' 个操作';
        run.rows = (run.rows || []).concat(m);
        keyed(run.box, run.rows);
        return;
      }
      run = null;
      // what the tool said of its work between its steps: a quieter line of its own, under the tool's name like its answers
      if (m.role === 'thought') {
        box.append(keyed(el('div', { className: 'turn assistant thought' }, last !== 'assistant' ? el('b', { textContent: toolName }) : '', el('div', { className: 'plain', textContent: m.text }), m.truncated ? el('small', { className: 'quiet', textContent: '这段内容超长，剩余部分请在电脑上查看' }) : ''), [m, last]));
        last = 'assistant';
        return;
      }
      const mine = m.role === 'user';
      const fromPhone = mine && phoneTexts && phoneTexts.has(String(m.text || '').trim());
      box.append(keyed(el('div', { className: 'turn ' + (mine ? 'user' : 'assistant') },
        !mine && last !== 'assistant' ? el('b', { textContent: toolName }) : '', set(m.text),
        m.truncated ? el('small', { className: 'quiet', textContent: '这条消息超长，剩余部分请在电脑上查看' }) : '',
        fromPhone ? el('small', { className: 'phone-delivery', textContent: '手机消息' }) : ''), [m, last, !!fromPhone]));
      last = m.role;
    });
    const texts = new Set(messages.map(m => m.text));
    for (const key of cache.keys()) if (!texts.has(key)) cache.delete(key);
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
  async function takeOver(session, extra) {
    if (!usable()) return false;
    const canPeek = (data.device.features || []).includes('peek');
    let watch = 0, readingView;
    const choice = await openSheet((form, finish) => {
      const now = () => (data.sessions || []).find(s => s.id === session.id && s.tool === session.tool) || session;
      const headDot = el('span', { className: 'head-dot', ariaHidden: 'true' }), headText = el('span', { className: 'stands-text' });
      const head = el('p', { className: 'stands' }, headDot, headText), said = el('div', { className: 'said' }), asks = el('div', { className: 'asks-host', hidden: true }), write = el('div', { className: 'write' }), below = el('div', { className: 'below' });
      const reader = readingView = new RemoteConversation.Reader(said), textCache = new Map();
      const draft = new RemoteConversation.Draft(pendingKey(session));
      said.style.overflowAnchor = 'none';
      said.setAttribute('aria-label', '对话内容'); said.tabIndex = 0;
      const control = el('div', { className: 'control-mode', role: 'status' });
      const phoneTexts = new Set();
      // Stopping what it is at, the same for every tool: by its application's channel (the one turn that is named,
      // with a receipt that can be asked for again), or through its tool's hooks (before its next step). Where it
      // has neither, it can still be ended, if the computer can close its program: asked first, with the reason.
      let stopAttempt = null;
      const stopNote = el('p', { className: 'stop-note', role: 'status', hidden: true });
      const stopSays = text => { stopNote.textContent = text; stopNote.hidden = !text; };
      const stopping = el('button', { type: 'button', className: 'end halt', textContent: '停止回答', hidden: true, onclick: async () => {
        if (stopping.disabled) return;
        if (!usable()) { stopSays('电脑离线，停止请求尚未发送'); return; }
        stopping.disabled = true;
        stopping.textContent = stopAttempt ? '正在查询…' : '正在停止…';
        stopSays(stopAttempt ? '正在查询上次停止请求的结果…' : '正在请求停止回答…');
        try {
          let current = now(), way = stopAttempt ? stopAttempt.transport : wayOf(current, 'stop');
          // A just-started turn may not yet be present in the overview. Refresh before deciding it cannot stop.
          if (!stopAttempt && (!way || (needsTurnForStop(way) && !current.turn))) {
            await refresh();
            current = now(); way = wayOf(current, 'stop');
          }
          if (!way) {
            const why = whyNot(current, 'stop', '这段对话现在不能从手机停止，请在电脑上停止');
            stopSays(why);
            return;
          }
          if (needsTurnForStop(way) && !stopAttempt && !current.turn) {
            stopSays('尚未取得当前执行轮次，停止请求未发送。请稍后重试，或在电脑上停止');
            return;
          }
          const query = !!stopAttempt, nativeStop = isNativeApp(way);
          if (nativeStop && !stopAttempt) {
            stopAttempt = { id: newId(), session: current.id, action: 'stop', transport: way };
            if (needsTurnForStop(way)) stopAttempt.turn = current.turn;
          }
          const result = await api('api/conversation', nativeStop ? { ...stopAttempt, receipt: query } : { id: newId(), session: current.id, action: 'stop', transport: way });
          if (result.status === 'confirmed') { stopSays(result.goal_pause_error ? '本次回答已停止，但目标未暂停，请在电脑上检查' : '已停止本次回答，可以继续对话'); stopAttempt = null; }
          else if (result.status === 'asked') stopSays('已请求停止，将在下一次工具操作前停下；当前输出可能仍会继续');
          else stopSays('停止结果尚未确认，请查询停止结果或检查电脑上的原对话');
        } catch (error) { stopSays('停止结果未确认：' + error.message); }
        finally {
          stopping.disabled = false; stopping.textContent = stopAttempt ? '查询停止结果' : '停止回答'; refresh(); show();
        }
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
      state.after(stopNote);
      const stateKey = current => JSON.stringify([current.updated, current.status, current.last, current.turn]);
      let stamp = '', seen = '', read = false, reading = false, sending = false, stands = null, standsAt = '', liveAsk = null, liveAskAt = '', readError = '', quick = 0, asked = 0, changed = 0, sawBusy = 0, lastKind = '';
      const pendingScope = pendingKey(session);
      const pending = desktopPending.get(pendingScope) || loadDesktopPending(pendingScope, session);
      desktopPending.set(pendingScope, pending);
      const recordedIds = new Set();
      const savePending = () => saveDesktopPending(pendingScope, pending);
      const same = (messages, text) => messages.filter(m => m.role === 'user' && String(m.text || '').trim() === text).length;
      let record = [];
      function mergeCurrent(listed) {
        const key = stateKey(listed), freshRead = standsAt === key, freshAsk = liveAskAt === key;
        let out = listed;
        if (freshRead && stands !== null && !listed.ask && listed.transport !== 'codex-desktop') {
          if (stands !== 'confirm' || (freshAsk && liveAsk)) out = Object.assign({}, out, { status: stands });
        }
        if (freshAsk && liveAsk && !out.ask) {
          const reach = Array.isArray(out.reach) ? (out.reach.includes('answer') ? out.reach : out.reach.concat('answer')) : ['answer'];
          const answerWay = wayOf(out, 'answer') || (isNativeApp(out.transport) ? out.transport : '');
          const ways = out.ways ? Object.assign({}, out.ways, { answer: out.ways.answer || answerWay }) : out.ways;
          out = Object.assign({}, out, { status: 'confirm', ask: liveAsk, reach, ways });
        }
        return out;
      }
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
        // Reconcile on every delivery update, including when the transcript stamp stays unchanged.
        // Occurrence counts keep repeated, identical prompts separate from earlier turns.
        let removed = false;
        for (let i = pending.length - 1; i >= 0; i--) {
          if (same(record, pending[i].text) > pending[i].before) {
            recordedIds.add(pending[i].payload.id);
            pending.splice(i, 1); removed = true;
          }
        }
        if (removed) savePending();
        // The overview's queue can lag behind the transcript; do not resurrect an echoed message.
        const kept = (now().queued || []).filter(item => !recordedIds.has(item.id)), shownIds = new Set();
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
          if (isNativeApp(p.payload.transport) && p.uncertain) row.append(el('button', { type: 'button', textContent: '查询发送结果', onclick: async event => {
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
        const receipt = isNativeApp(mine.payload.transport);
        try {
          const result = await api('api/conversation', receipt ? { ...mine.payload, receipt: query } : mine.payload);
          mine.uncertain = result.status !== 'accepted' || result.sent !== true; mine.sent = !mine.uncertain;
          mine.delivery = deliveryText(result); savePending();
          return true;
        } catch (error) {
          toast(error.message);
          if (receipt) { mine.uncertain = true; mine.delivery = '发送结果未确认，请查询本次结果'; savePending(); }
          return receipt;
        }
      }
      async function follow(current) {
        const listed = [current.updated, current.status, current.last || ''].join('\n');
        if (!canPeek) {
          if (!read) { read = true; said.replaceChildren(el('p', { className: 'quiet', textContent: '当前电脑端版本不支持直接查看历史对话' })); }
          return;
        }
        if (reading || (read && stamp === null && listed === seen)) return;
        reading = true;
        const startedAt = stateKey(current);
        let stateChanged = false;
        try {
          const got = await api('api/conversation', Object.assign({ id: newId(), session: session.id }, stamp ? { since: stamp } : {}));
          readError = '';
          if (typeof got.status === 'string') {
            if (stands !== got.status || standsAt !== startedAt) stateChanged = true;
            stands = got.status; standsAt = startedAt;
          }
          if (Object.prototype.hasOwnProperty.call(got, 'ask')) {
            const nextAsk = got.ask && typeof got.ask === 'object' && got.ask.id ? got.ask : null;
            if (JSON.stringify(liveAsk) !== JSON.stringify(nextAsk) || liveAskAt !== startedAt) stateChanged = true;
            if (nextAsk && (!current.ask || current.ask.id !== nextAsk.id)) refresh();
            liveAsk = nextAsk; liveAskAt = startedAt;
          }
          if (!got.same) {
            record = got.messages || [];
            if (typeof got.asked === 'number') asked = got.asked;
            if (typeof got.updated === 'number') changed = got.updated;
            const fresh = el('div');
            if (got.more) fresh.append(el('p', { className: 'earlier', textContent: '更早的内容在电脑上' }));
            await writeSaid(fresh, record, speakerOf(current), phoneTexts, textCache);
            if (!said.isConnected) return;
            const initial = !read;
            reader.paint(said, () => reader.change(() => { reader.replace(fresh); waiting(); latest.hidden = atEnd(); }, initial));
          }
          stamp = typeof got.stamp === 'string' ? got.stamp : null; seen = listed; read = true;
        } catch (error) { stands = null; liveAsk = null; readError = '对话同步中断，正在重试：' + error.message; if (!read) said.replaceChildren(el('p', { className: 'quiet', textContent: error.message })); }
        reading = false;
        if (stateChanged && said.isConnected && $('sheet').open) show();
      }
      // ---- how it stands, what it asks, and the box
      const box = el('textarea', { rows: 1, maxLength: 8000, placeholder: '发消息', autocapitalize: 'off', spellcheck: false, ariaLabel: '消息' });
      box.value = draft.get();
      box.addEventListener('input', () => {
        draft.set(box.value);
        const prevScroll = box.scrollTop;
        box.style.height = 'auto';
        box.style.height = Math.min(144, box.scrollHeight + 2) + 'px';
        if (prevScroll && box.scrollTop !== prevScroll) box.scrollTop = prevScroll;
      });
      const send = el('button', { type: 'button', className: 'solid', textContent: '发送', onclick: async () => {
        const original = box.value, text = original.trim();
        const active = mergeCurrent(now());
        if (send.disabled || !usable() || !can(active, 'say') || active.ask) return;
        if (!text) return box.focus();
        stopSays('');
        // what is written stands in the window at once, as what the user said, while it is on its way
        const mine = { text, before: same(record, text) + pending.filter(p => p.text === text).length, sent: false, createdAt: Date.now(), payload: { id: newId(), session: session.id, action: 'say', text } };
        const sayWay = wayOf(active, 'say');
        if (isNativeApp(sayWay)) mine.payload.transport = sayWay;
        pending.push(mine); savePending(); draft.set(original);
        phoneTexts.add(text);
        waiting(); said.scrollTop = said.scrollHeight;
        sending = true; send.disabled = true; quick = Date.now() + 45000;
        // what was not sent goes back into the box, to be sent again or changed
        if (!await deliver(mine)) { const index = pending.indexOf(mine); if (index >= 0) pending.splice(index, 1); savePending(); phoneTexts.delete(text); }
        else { draft.clear(original); if (box.value === original) { box.value = ''; box.style.height = 'auto'; } }
        sending = false; send.disabled = false; waiting(); refresh(); seen = ''; shown = ''; show();
      } });
      box.addEventListener('keydown', event => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.isComposing) { event.preventDefault(); send.click(); } });
      // a conversation that has stopped is most often told to go on: one press, beside what is typed
      const goOn = el('button', { type: 'button', className: 'go', textContent: '继续', hidden: true, onclick: () => { if (!box.value.trim()) { box.value = '继续'; send.click(); } else box.focus(); } });
      write.append(goOn, box, send);
      let shown = '';
      function show() {
        // how it stands is what the computer said of it last: with the conversation itself, or in the lists
        const listed = now(), current = mergeCurrent(listed), phase = current.live ? reachPhase(current) : '';
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
        lastKind = kind;
        // at work since its user spoke, as its record says; where the record does not say, since this window saw it at work
        sawBusy = kind !== 'busy' ? 0 : sawBusy || Date.now();
        const from = kind === 'busy' ? (asked && asked <= Date.now() ? asked : sawBusy) : kind === 'stopped' || kind === 'waiting' ? changed || current.updated : 0;
        state.className = 'state ' + kind;
        headDot.className = 'head-dot ' + (!current.live ? 'off' : kind === 'busy' ? 'busy' : kind === 'confirm' ? 'confirm' : 'on');
        standing.textContent = !current.live ? '已在电脑上关闭' : PHASE[kind][0];
        clock.textContent = !from ? '' : kind === 'busy' ? lasted(Date.now() - from) : ago(from);
        ending.hidden = !canEnd(current);
        // A running CLI is not necessarily interruptible. Never present ending its process as stopping one turn.
        const stopWay = wayOf(current, 'stop');
        const stoppable = !!stopWay && (!needsTurnForStop(stopWay) || !!current.turn);
        stopping.hidden = !stopAttempt && !stoppable;
        if (kind === 'busy' && !stoppable && !stopAttempt && !stopping.disabled) {
          stopSays(whyNot(current, 'stop', '当前对话不支持从手机单独中断本轮回答，请在电脑上停止'));
        }
        goOn.hidden = !(can(current, 'say') && !current.ask && (kind === 'stopped' || kind === 'waiting') && record.length > 0);
        if (signature === shown) return;
        shown = signature;
        waiting();
        headText.textContent = whereOf({ s: current, tool: current.tool, live: current.live }) + ' · ' + (current.dir || base(current.folder));      // (how it stands is said in the line above the box)
        // what it asks is answered here, under what was said
        asks.textContent = '';
        asks.hidden = !current.ask;
        if (current.ask) {
          const askId = current.ask.id;
          const answers = approvalActions(current, ok => { if (ok && liveAsk && liveAsk.id === askId) liveAsk = null; shown = ''; show(); });
          asks.append(el('div', { className: 'card asking' }, el('div', { className: 'ask' }, el('pre', { textContent: current.ask.text }), answers)));
        }
        // The box stands wherever something can be said, and also where it will be once the conversation has done
        // what it is at (its application takes nothing meanwhile): the next thing can be written there already.
        const sayable = can(current, 'say') && !current.ask, later = !sayable && !current.ask && current.live && isNativeApp(current.transport);
        write.hidden = !sayable && !later;
        send.disabled = sending || !sayable; box.placeholder = sayable ? '发消息' : '先写草稿，稍后发送';
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
      // ~600ms while busy/confirm or in the quick window after sending; ~1200ms while idle
      let ticks = 0;
      watch = setInterval(() => {
        if (!$('sheet').open) return;
        ticks++;
        const fast = lastKind === 'busy' || lastKind === 'confirm' || Date.now() < quick;
        if (fast ? ticks % 2 === 0 : ticks % 4 === 0) show();
      }, 300);
    }, true);
    clearInterval(watch);
    if (readingView) readingView.dispose();
    if (choice && choice.handoff) { await views.together.handoff(choice.handoff); return false; }
    return choice ? into(choice.session, choice.value, extra) : false;
  }
  // Into a terminal of the phone's: by taking the conversation from the program that has it, by a copy, or, for one
  // that nothing has open, simply by going on with it.
  function into(session, how, extra) {
    if (how === 'chat' || how === 'chat-close') return chat(session.tool, session.dir, session.id, how === 'chat-close', extra);
    return start(session.tool, session.dir, session.id, how === 'close', how === 'copy', extra);
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
  const sourceOf = s => !s.live || s.kind ? sourceOfKind(s.kind) : /桌面版|后台服务/.test(s.origin || '') || isNativeApp(s.transport) || sessionHost(s) === 'shared' || (s.origin && s.origin !== '命令行' && s.origin !== '电脑独立终端') ? 'app' : 'term';
  const TEAM_MODES = { review: '写审', lead: '分工协作', compare: '比稿' };
  const teamUpdatedMs = value => { const n = Number(value) || 0; return n > 0 && n < 10000000000 ? n * 1000 : n; };
  const summarizeTeamTools = list => {
    const counts = new Map();
    list.forEach(t => counts.set(t, (counts.get(t) || 0) + 1));
    return [...counts.entries()].map(([t, n]) => (TOOLS[t] || t) + (n > 1 ? ' ×' + n : '')).join('、');
  };
  const teamWho = card => {
    const roster = Array.isArray(card.roster) ? card.roster.filter(r => r && r.tool) : [];
    if (card.mode === 'lead' && roster.length) {
      const leader = roster.find(r => r.role === 'leader') || roster[0];
      const rest = roster.filter(r => r !== leader).map(r => r.tool);
      const leadName = TOOLS[leader.tool] || leader.tool || '';
      return rest.length ? leadName + ' 带 ' + summarizeTeamTools(rest) : leadName + (card.auto_roster ? ' 自动组队' : '');
    }
    const list = Array.isArray(card.tools) ? card.tools : [];
    if (card.mode === 'review') return (TOOLS[list[0]] || list[0] || '') + ' 写 · ' + (TOOLS[list[1]] || list[1] || '') + ' 审';
    if (card.mode === 'compare') return list.map(t => TOOLS[t] || t).join(' vs ');
    return (TOOLS[list[0]] || list[0] || '') + (list.length > 1 ? ' 带 ' + summarizeTeamTools(list.slice(1)) : card.auto_roster ? ' 自动组队' : '');
  };
  const teamStatusOf = x => {
    const c = x.team;
    if (x.teamConfirm) return ['confirm', '等你确认'];
    if (c.state === 'choose') return ['confirm', '待选方案'];
    if (c.state === 'running') return ['busy', window.RemoteTeams?.phaseLabel?.(c.phase) || '进行中'];
    if (c.state === 'paused') return ['waiting', '已暂停'];
    if (c.state === 'done') return ['ended', '已完成'];
    if (c.state === 'stopped') return ['ended', c.mode === 'lead' && (c.phase === 'verify' || c.last) ? '未完成' : '已停止'];
    if (c.state === 'failed') return ['failed', '没有开始'];
    return ['ended', c.state || '已结束'];
  };
  function things() {
    const sessions = data.sessions || [], saved = {};
    sessions.forEach(s => { saved[s.tool + ':' + s.id] = s; });
    const rawCards = Array.isArray(data.device && data.device.team_cards) ? data.device.team_cards : [];
    const teamCards = [], teamIds = new Set();
    rawCards.forEach(c => { if (c && c.id && !teamIds.has(c.id)) { teamIds.add(c.id); teamCards.push(c); } });
    const sessionTeam = new Map();
    sessions.forEach(s => { if (s.team && teamIds.has(s.team)) sessionTeam.set(s.tool + ':' + s.id, s.team); });
    const teamChildConfirm = new Map();
    (data.terminals || []).forEach(t => {
      const tid = (t.team && teamIds.has(t.team)) ? t.team : (t.session ? sessionTeam.get(t.tool + ':' + t.session) : '');
      if (tid && running(t) && phaseOf(t) === 'confirm') {
        teamChildConfirm.set(tid, (t.asks && t.asks.join('\n')) || t.ask || '成员正在等你确认');
      }
    });
    sessions.forEach(s => {
      if (s.team && teamIds.has(s.team) && s.live && reachPhase(s) === 'confirm') {
        teamChildConfirm.set(s.team, (s.ask && s.ask.text) || '成员正在等你确认');
      }
    });
    const out = [];
    (data.terminals || []).forEach(t => {
      const s = t.session ? saved[t.tool + ':' + t.session] || null : null;
      const belongs = (t.team && teamIds.has(t.team)) || (s && s.team && teamIds.has(s.team));
      if (belongs) return;
      const phase = phaseOf(t), live = running(t), tool = isAgyShell(t) ? 'agy' : t.tool;
      // an ended terminal whose conversation is listed is found there, to be gone on with: it is not listed twice
      out.push({ t, s, tool, title: t.title, place: t.dir || base(t.folder), project: t.dir, apart: !t.dir, live, phase, rank: PHASE[phase][1], at: t.phase_at || t.created,
        source: t.form === 'chat' ? 'app' : 'term', pinned: !!(s && s.pinned), hidden: false, state: live ? 'active' : 'ended', twice: !live && !!s && !s.terminal });
    });
    sessions.filter(s => !s.terminal).forEach(s => {
      if (s.team && teamIds.has(s.team)) return;
      const phase = !s.live || s.now === false ? '' : reachPhase(s) || (sessionHost(s) === 'cli' ? (s.status === 'busy' ? 'busy' : s.status === 'confirm' ? 'confirm' : s.status === 'waiting' ? 'waiting' : 'idle') : '');
      out.push({ s, tool: s.tool, title: s.title, place: s.dir || base(s.folder), project: s.dir, apart: !s.dir, live: !!s.live, phase, rank: phase ? PHASE[phase][1] : 6, at: s.updated,
        source: sourceOf(s), pinned: !!s.pinned, hidden: !!s.hidden && !s.live, state: sessionActivity(s) });
    });
    teamCards.forEach(c => {
      const rosterTools = Array.isArray(c.roster) ? c.roster.filter(r => r && r.tool).map(r => r.tool) : [];
      const tools = [...new Set(rosterTools.length ? rosterTools : (Array.isArray(c.tools) ? c.tools : []))];
      const confirmRaw = c.confirm;
      const teamConfirm = !!confirmRaw || teamChildConfirm.has(c.id);
      const confirmText = typeof confirmRaw === 'string' ? confirmRaw : (confirmRaw && typeof confirmRaw === 'object' ? (confirmRaw.text || confirmRaw.ask || '') : '') || teamChildConfirm.get(c.id) || '';
      const isChoose = c.state === 'choose', isRunning = c.state === 'running', isPaused = c.state === 'paused';
      const live = teamConfirm || isChoose || isRunning || isPaused;
      const phase = teamConfirm || isChoose ? 'confirm' : isRunning ? 'busy' : isPaused ? 'waiting' : (c.state === 'failed' ? 'failed' : 'ended');
      out.push({
        team: c, teamTools: tools, teamConfirm, teamConfirmText: confirmText,
        tool: tools[0] || '', title: c.goal || '协作任务', place: c.dir || '', project: c.dir || '', apart: !c.dir,
        live, phase, rank: PHASE[phase] ? PHASE[phase][1] : 6, at: teamUpdatedMs(c.updated),
        source: 'app', pinned: false, hidden: false, state: live ? 'active' : 'history'
      });
    });
    for (const x of out) {
      if (!x.s) continue;
      x.organize = x.s.organize;
      x.originalTitle = x.organize?.original_title || x.s.title || x.title;
      if (x.organize?.title) x.title = x.organize.title;
    }
    return out;
  }
  const needsYou = x => x.live && x.phase === 'confirm';
  const working = x => x.live && (x.phase === 'busy' || x.phase === 'starting');
  const byRank = (a, b) => a.rank - b.rank || b.at - a.at, byTime = (a, b) => b.at - a.at;
  const byOpen = (a, b) => (b.pinned - a.pinned) || byRank(a, b);
  const byOpened = (a, b) => (b.pinned - a.pinned) || (Number(!!a.t) - Number(!!b.t)) || byTime(a, b);
  // by what it runs and where, in the words of the cards
  // Every card says it the same way: the tool, then where it runs. A terminal of the phone's; for a conversation
  // that a program has open, that program (a desktop app by its own name, which names the tool too; a command
  // line; an editor, and whether it is the editor's terminal); for one that is only saved, by what it was started.
  const whereOf = x => {
    if (x.team) return '协作 · ' + (TEAM_MODES[x.team.mode] || x.team.mode || '协作') + ' · ' + teamWho(x.team);
    const tool = TOOLS[x.tool];
    if (x.t) return tool + (x.live ? ' · ' + (x.t.form === 'chat' ? PHONE_CHAT : PHONE) : '');
    const kind = x.s.kind || '';
    if (!x.live) return !kind ? tool : /桌面版/.test(kind) ? kind : tool + ' · ' + kind;
    const origin = x.s.origin && x.s.origin !== '电脑独立终端' ? x.s.origin : (x.s.transport === 'agy-desktop' ? 'Antigravity 桌面版' : x.s.transport === 'codex-desktop' ? 'Codex 桌面版' : '');
    return !origin ? tool : /桌面版|后台服务/.test(origin) ? origin : tool + ' · ' + origin + (kind === '终端' && origin !== '命令行' ? ' 的终端' : '');
  };
  // what a search looks through
  const wordsOf = x => {
    if (x.team) {
      const c = x.team, [, label] = teamStatusOf(x), models = c.models && typeof c.models === 'object' ? Object.values(c.models).join('\n') : '';
      const rosterModels = Array.isArray(c.roster) ? c.roster.map(r => [r && r.model, r && r.effort].filter(Boolean).join('\n')).filter(Boolean).join('\n') : '';
      return [x.title, x.place, '协作任务', TEAM_MODES[c.mode] || c.mode || '', c.auto_roster ? '自动组队' : '', label, c.note || '', x.teamConfirmText || '', (x.teamTools || []).map(t => TOOLS[t] || t).join('\n'), models, rosterModels, SOURCE.app].join('\n').toLowerCase();
    }
    return [x.title, x.originalTitle, x.organize?.summary, ...(x.organize?.tags || []), x.place, TOOLS[x.tool], SOURCE[x.source] || '', x.t && x.live ? (x.t.form === 'chat' ? PHONE_CHAT : PHONE) : '', x.s && x.s.origin || '', x.s && x.s.kind || '', x.apart ? '未加入项目' : '', x.pinned ? '置顶' : '', x.hidden ? '已隐藏' : ''].join('\n').toLowerCase();
  };
  // what a card shows: it is built again when any of this changes
  const signed = x => x.team ? ['team', x.team.id, x.team.state, x.team.phase, !!x.team.last, x.team.goal, x.team.dir, x.team.note, x.team.updated, x.teamConfirm, x.teamConfirmText, x.teamTools, x.team.models || null, x.team.roster || null, !!x.team.auto_roster, x.team.awaited || null]
    : x.t ? [x.t.id, x.phase, x.title, x.t.phase_at, x.pinned, x.place, x.apart, x.organize]
    : [x.s.id, x.phase, x.s.now, x.s.status, x.title, x.s.host, x.s.origin, (x.s.reach || ['-']).join(), canEnd(x.s), endingSessions.has(x.s.id), x.s.ask || null, x.s.last || '', x.pinned, x.hidden, x.place, !!x.apart, x.state, x.source, x.organize];

  // ---- the filters: by tool, and by where a thing runs. They last as long as the page is kept open.
  let filter = { tool: '', source: '', follow: false }, resultsShown = 30, recentShown = 2, hiddenShown = false;
  try { filter = Object.assign(filter, JSON.parse(sessionStorage.getItem('rcli-filter') || '{}')); } catch (error) { /* no storage */ }
  if (!SOURCE[filter.source]) filter.source = '';
  const hasTool = (x, tool) => x.team ? (x.teamTools || []).includes(tool) : x.tool === tool;
  const pass = x => (!filter.tool || hasTool(x, filter.tool)) && (!filter.source || x.source === filter.source);
  const passTasks = x => pass(x) && (!filter.follow || !!x.organize?.follow_up);
  const taskWords = () => $('query').value.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const matchesTask = (x, asked) => asked.every(word => wordsOf(x).includes(word));
  const listKey = x => x.team ? 'team:' + x.team.id : (x.s ? 'session:' + x.s.tool + ':' + x.s.id : 'terminal:' + x.t.id);
  const storedPosition = key => 'rcli-list:' + location.pathname + ':' + key;
  function saveListPosition() {
    const key = history.state?.listKey || newId();
    const rows = [...document.querySelectorAll('[data-list-key]')].filter(row => row.getClientRects().length);
    const index = rows.findIndex(row => row.getBoundingClientRect().bottom > 0);
    const anchors = index < 0 ? [] : [rows[index], rows[index + 1], rows[index - 1]].filter(Boolean)
      .map(row => [row.dataset.listKey, row.getBoundingClientRect().top]);
    try {
      sessionStorage.setItem(storedPosition(key), JSON.stringify({ tab, project, cameFrom, filter, resultsShown, recentShown, shown, endedShown,
        hiddenShown, projectFolds, query: $('query').value, search: $('search').value, y: scrollY, anchors }));
      history.replaceState({ ...history.state, listKey: key }, '');
    } catch (error) { /* Browsing still works without storage. */ }
    return key;
  }
  function restoreListPosition(key, restoreFields = true) {
    let state;
    try { state = JSON.parse(sessionStorage.getItem(storedPosition(key))); } catch (error) { return false; }
    if (!state) return false;
    if (restoreFields) {
      ({ tab, project, cameFrom, filter, resultsShown, recentShown, shown, endedShown, hiddenShown } = state);
      Object.assign(projectFolds, state.projectFolds);
      $('query').value = state.query;
      $('search').value = state.search;
      draw();
    }
    requestAnimationFrame(() => {
      const visible = [...document.querySelectorAll('[data-list-key]')].filter(row => row.getClientRects().length);
      for (const [id, top] of state.anchors) {
        const row = visible.find(one => one.dataset.listKey === id);
        if (row) { scrollBy(0, row.getBoundingClientRect().top - top); return; }
      }
      scrollTo(0, state.y);
    });
    return true;
  }
  function organizeCandidates() {
    const asked = taskWords();
    return things().filter(x => supportsOrganize(x.s) && !x.twice && !x.hidden && passTasks(x) && matchesTask(x, asked))
      .map(x => ({ session: x.s.id, tool: x.s.tool }));
  }
  const supportsOrganize = s => !!s && ['codex', 'claude', 'agy'].includes(s.tool);
  // a project is one of a tool or of a kind by what its folder is used for, or by what is in it now
  const passProject = (folder, inside) => (!filter.tool || (folder.tools || []).includes(filter.tool) || inside.some(x => hasTool(x, filter.tool)))
    && (!filter.source || (folder.kinds || []).some(kind => sourceOfKind(kind) === filter.source) || inside.some(x => x.source === filter.source));
  function choose(change) {
    filter = Object.assign({}, filter, change);
    try { sessionStorage.setItem('rcli-filter', JSON.stringify(filter)); } catch (error) { /* no storage */ }
    resultsShown = 30; recentShown = 2; draw();
  }
  function drawFilters(all) {
    // a row is offered only where it tells something apart, or to take back what was chosen
    const folders = data.device.projects || [];
    const tools = Object.keys(TOOLS).filter(tool => tool === filter.tool || all.some(x => hasTool(x, tool)) || folders.some(f => (f.tools || []).includes(tool)));
    const sources = Object.keys(SOURCE).filter(source => source === filter.source || all.some(x => x.source === source) || folders.some(f => (f.kinds || []).some(kind => sourceOfKind(kind) === source)));
    const offered = (tools.length > 1 ? tools : filter.tool ? [filter.tool] : []), places = (sources.length > 1 ? sources : filter.source ? [filter.source] : []);
    const canOrganize = tab === 'tasks' && (pageHost.has('organize') || filter.follow);
    const followCount = canOrganize ? all.filter(x => !x.twice && !x.hidden && pass(x) && !!x.organize?.follow_up).length : 0;
    const showFollow = canOrganize && (followCount > 0 || filter.follow);
    const box = $('filters'), signature = JSON.stringify([offered, places, filter, TOOLS.shell, showFollow, followCount]);
    box.hidden = !offered.length && !places.length && !showFollow;
    if (box.dataset.signature === signature) return;
    box.dataset.signature = signature;
    const MONO = { claude: 'CC', codex: 'CX', agy: 'AG', opencode: 'OC', qoder: 'QD', cline: 'CL', pi: 'PI' };
    const chip = (text, on, act, toolKey) => {
      const btn = el('button', { type: 'button', className: on ? 'on' : '', ariaPressed: String(on), textContent: text, onclick: act });
      if (toolKey && MONO[toolKey]) { btn.dataset.tool = toolKey; btn.dataset.mono = MONO[toolKey]; }
      return btn;
    };
    const row = [chip('全部', !filter.tool && !filter.source && (!showFollow || !filter.follow), () => choose({ tool: '', source: '', follow: false }))];
    if (showFollow) {
      const followBtn = el('button', {
        type: 'button', id: 'follow-filter', className: filter.follow ? 'on' : '',
        ariaPressed: String(!!filter.follow), onclick: () => choose({ follow: !filter.follow })
      }, el('span', { textContent: '待跟进' }), followCount > 0 ? el('span', { className: 'chip-count', textContent: String(followCount) }) : '');
      row.push(followBtn);
    }
    offered.forEach(tool => row.push(chip(TOOLS[tool], filter.tool === tool, () => choose({ tool: filter.tool === tool ? '' : tool }), tool)));
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
  async function setFollowUp(s, followUp) {
    if (!usable()) return toast('电脑离线，暂时无法修改');
    try {
      const answer = await api('api/organize', { id: newId(), verb: 'edit', tool: s.tool, session: s.id, follow_up: !!followUp });
      s.organize = Object.assign({}, s.organize, answer && answer.item ? answer.item : { follow_up: !!followUp });
      draw();
      toast(followUp ? '已加入待跟进' : '已移出待跟进');
    } catch (error) { toast(error.message); }
    refresh();
  }
  async function addFolder(path) {
    if (await run({ action: 'project_add', path, name: '', create: false }, '正在添加…')) { toast('已加为项目'); refresh(); }
  }
  async function manage(x) {
    if (window.RemoteSelect && window.RemoteSelect.active) return;
    const s = x.s, choices = [{ label: '多选', value: 'select' }];
    if (supportsOrganize(s) && pageHost.has('organize')) {
      choices.push({ label: s.organize?.follow_up ? '移出待跟进' : '加入待跟进', value: 'follow_up' });
      choices.push({ label: '整理这段对话', value: 'organize' });
    }
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
    const choice = await ask(x.title, '', choices);
    if (choice === 'select') window.RemoteSelect.start(x);
    else if (choice === 'follow_up') setFollowUp(s, !s.organize?.follow_up);
    else if (choice === 'organize') window.RemoteOrganize.open({ session: s.id, tool: s.tool });
    else if (choice === 'pin') mark(s, { pinned: !s.pinned });
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
    if (x.organize?.follow_up) meta.append(pill('waiting', '待跟进'));
    if (!x.place) return;
    meta.append(el('span', { className: 'chip' + (x.apart ? ' apart' : ''), textContent: x.place, title: x.place, ariaLabel: (x.apart ? '文件夹：' : '项目：') + x.place }));
    if (x.apart) meta.append(el('span', { textContent: '未加入项目' }));
  }
  const card = x => {
    const made = x.team ? teamCard(x) : x.t ? terminalCard(x) : sessionCard(x);
    made.dataset.listKey = listKey(x);
    if (x.team) return made;
    const summary = x.organize?.summary;
    const query = taskWords();
    const matched = x.organize?.tags?.filter(tag => query.some(word => tag.toLowerCase().includes(word))) || [];
    if (summary || matched.length) made.querySelector('.open .text')?.append(el('span', {
      className: 'organized-summary',
      textContent: matched.length ? '主题 · ' + matched.join('、') + (summary ? ' · ' + summary : '') : summary
    }));
    if (supportsOrganize(x.s) && pageHost.has('organize')) made.insertBefore(el('button', {
      type: 'button', className: 'side organize-more', ariaLabel: '对话操作', title: '对话操作', textContent: '⋯',
      onclick: () => manage(x)
    }), made.querySelector('.ask'));
    if (!x.t && canEnd(x.s)) made.insertBefore(el('button', { type: 'button', className: 'side end-session', ariaLabel: '结束电脑上的这段对话', title: '结束对话', html: ICON.stop, disabled: !usable() || endingSessions.has(x.s.id), onclick: () => endConversation(x.s) }), made.querySelector('.ask'));
    return held(made, () => manage(x));
  };
  function teamCard(x) {
    const c = x.team, [kind, label] = teamStatusOf(x);
    const meta = el('span', { className: 'meta' }, pill(kind, label));
    placed(meta, x);
    meta.append(el('span', { textContent: whereOf(x) + (x.at ? ' · ' + ago(x.at) : '') }));
    const peekText = x.teamConfirmText || c.note || (c.state === 'running' && c.phase === 'verify' ? '领队正在验收执行成员改动与结果' : '');
    const iconHtml = (window.RemoteTeams && window.RemoteTeams.TEAM) || ICON.together;
    const openTeam = () => {
      if (!usable()) return toast('电脑离线，暂时无法打开');
      if (window.RemoteTeams && window.RemoteTeams.open) window.RemoteTeams.open(c.id, c);
      else go('together', true);
    };
    return el('li', { className: 'card team-card ' + (x.live ? (kind === 'waiting' ? 'done' : kind) : 'ended'), 'data-team': c.id },
      el('button', { type: 'button', className: 'open', onclick: openTeam },
        el('span', { className: 'tool team', html: iconHtml }),
        el('span', { className: 'text' }, named(x), meta, peekText ? el('span', { className: 'peek', textContent: peekText }) : ''),
        el('span', { className: 'chev', html: ICON.chev })));
  }
  // A terminal opened from the phone.
  function terminalCard(x) {
    const t = x.t, phase = x.phase, meta = el('span', { className: 'meta' }, pill(phase, PHASE[phase][0]));
    placed(meta, x);
    meta.append(el('span', { textContent: whereOf(x) + (t.phase_at ? ' · ' + ago(t.phase_at) : '') }));
    // What the program last said. It changes far more often than the card, so peeks() writes it without a rebuild.
    const peek = el('span', { className: 'peek', hidden: true });
    peek.dataset.peek = t.id;
    const made = el('li', { className: 'card ' + phase }, el('button', { type: 'button', className: 'open', onclick: () => open(t.id, t.form) },
      el('span', { className: 'tool ' + x.tool, html: ICON[x.tool] || ICON.shell }), el('span', { className: 'text' }, named(x), meta, peek)));
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
    return el('li', { className: 'card' }, el('button', { type: 'button', className: 'open', onclick: () => { if (!usable()) return toast('电脑离线，暂时无法打开'); if (s.live) takeOver(s); else readHistory(s); } },
      el('span', { className: 'tool ' + s.tool, html: ICON[s.tool] }), el('span', { className: 'text' }, named(x), meta), el('span', { className: 'chev', html: ICON.chev })));
  }
  // Lists are rebuilt only when what they show has changed, so a press or a scroll is not interrupted every few seconds.
  function fill(list, cards, signature) {
    if (list.dataset.signature === signature) return;
    list.dataset.signature = signature;
    list.replaceChildren(...cards);
    if (window.RemoteSelect) window.RemoteSelect.paint(list);      // (cards made anew are marked again while choosing)
  }
  function applyProjectFold(key) {
    const head = document.querySelector('#project .section-head[data-fold="' + key + '"]');
    if (!head) return;
    const open = projectFolds[key];
    // A folded head hides the rest of its section by style (app.css), so what draw() shows or hides there, the
    // search box and the "more" buttons, stays its own; the way back to hidden conversations stays in reach.
    head.classList.toggle('folded', !open);
    head.setAttribute('aria-expanded', String(open));
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
    $('organize').hidden = tab !== 'tasks' || !pageHost.has('organize');
    $('organize').disabled = !ready;
    document.querySelectorAll('#tabs button').forEach(button => { if (button.dataset.tab === tab) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current'); });
    // conversations open on the computer are counted with the terminals, by how they stand
    const stands = (list, ...phases) => list.filter(x => x.live && phases.includes(x.phase)).length;
    const waiting = stands(everything, 'confirm'), busy = everything.filter(working).length, opened = everything.filter(x => x.live && !needsYou(x) && !working(x)).length;
    // (inside the app, which lists several computers, the line says first which one this is)
    $('device-text').textContent = (computerName ? computerName + ' · ' : '') + (!ready ? (device.online ? '已暂停访问' : '电脑离线')
      : '电脑在线');
    $('monitor').hidden = !!project || tab !== 'tasks';
    $('metric-needs').textContent = ready ? waiting : '—';
    $('metric-active').textContent = ready ? busy : '—';
    $('metric-open').textContent = ready ? opened : '—';
    $('metric-needs').toggleAttribute('data-active', ready && waiting > 0);
    $('metric-active').toggleAttribute('data-active', ready && busy > 0);
    const external = $('external-agy'), starts = ready ? device.agy_external || [] : [];
    external.hidden = !starts.length;
    if (external.dataset.starts !== starts.join(',')) {
      external.dataset.starts = starts.join(',');
      external.querySelector('summary').textContent = '电脑上的 Antigravity CLI · ' + starts.length + ' 个进程';
      external.querySelector('ul').replaceChildren(...starts.map(at => el('li', { textContent: new Date(at).toLocaleString() + ' 启动' })));
    }
    // what waits for an answer is counted on its tab, whichever tab is open
    $('tab-count').hidden = !waiting; $('tab-count').textContent = waiting > 9 ? '9+' : waiting;
    if (!lists) { if (views[tab]) views[tab].draw(); return; }

    if (!project) {
      const tasks = tab === 'tasks', all = everything.filter(tasks ? passTasks : pass), asked = taskWords();
      const searching = asked.length > 0 || tasks && filter.follow, tick = [stamp(1), ready];
      $('find').hidden = tasks ? !everything.length : names.length < 2;
      $('query').placeholder = tasks && pageHost.has('organize') ? '搜索标题、主题或项目' : tasks ? '搜索对话、终端和项目' : '搜索项目';
      drawFilters(everything);
      $('results').hidden = !searching || !tasks; $('sections').hidden = searching || !tasks;
      $('tasks-empty').hidden = everything.length > 0; $('first-task').hidden = !ready;
      if (!tasks) { /* the projects only */ } else if (searching) {
        // everything is looked through, what is hidden too; what is open comes first
        const titleMatch = x => asked.every(word => [x.title, x.originalTitle].join('\n').toLowerCase().includes(word));
        const found = all.filter(x => !x.twice && matchesTask(x, asked)).sort((a, b) => (titleMatch(b) - titleMatch(a)) || (b.live - a.live) || (b.pinned - a.pinned) || (a.live ? byRank(a, b) : byTime(a, b)));
        $('results').querySelector('h2').textContent = !asked.length && filter.follow ? '待跟进' : '搜索结果';
        show($('results-list'), found.slice(0, resultsShown), tick.concat(asked));
        $('results-summary').textContent = found.length ? found.length + ' 个' : '没有找到';
        $('more-results').hidden = found.length <= resultsShown;
      } else {
        const listed = all.filter(x => !x.twice), needs = listed.filter(needsYou).sort(byOpen);
        const replies = listed.filter(x => x.live && x.phase === 'waiting' && !x.team).sort(byOpen);
        const unseen = listed.filter(x => x.live && x.phase === 'done' && !x.team).sort(byOpen);
        const rest = listed.filter(x => !needsYou(x) && !replies.includes(x) && !unseen.includes(x));
        const pinned = rest.filter(x => x.pinned && !x.live).sort(byTime), free = rest.filter(x => !x.pinned || x.live);
        const active = free.filter(working).sort(byOpen);
        const opened = free.filter(x => x.live && !working(x)).sort(byOpened);
        const recent = free.filter(x => !x.live && ((x.state === 'history' && !x.hidden) || x.state === 'ended'))
          .sort((a, b) => (Number(!!b.organize?.follow_up) - Number(!!a.organize?.follow_up)) || byTime(a, b));
        $('needs').hidden = !needs.length;
        show($('needs-list'), needs, tick);
        $('needs-summary').textContent = needs.length + ' 个等你确认';
        $('replies').hidden = !replies.length;
        show($('replies').querySelector('ul'), replies, tick);
        $('unseen').hidden = !unseen.length;
        show($('unseen').querySelector('ul'), unseen, tick);
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
        $('nothing').hidden = !(filter.tool || filter.source) || listed.length > 0;
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
        const folderTone = need ? ' confirm' : work ? ' busy' : opened ? ' active' : '';
        const made = held(el('li', { className: 'card' }, el('button', { type: 'button', className: 'open', onclick: () => enter(name) },
          el('span', { className: 'tool folder' + folderTone, html: ICON.folder }), el('span', { className: 'text' }, el('b', { textContent: name }), meta), el('span', { className: 'chev', html: ICON.chev }))),
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
    const here = inside.filter(x => !x.t), open2 = inside.filter(x => x.live && !working(x) && !needsYou(x)).sort(byOpened);
    $('p-live').hidden = !open2.length;
    $('p-live').querySelector('p').textContent = open2.length + ' 个';
    applyProjectFold('live');
    show($('p-live').querySelector('ul'), open2, tick);
    // what was pinned comes first; what was hidden is left out until it is asked for
    const saved = here.filter(x => !x.live && x.state === 'history').sort((a, b) => (b.pinned - a.pinned) || byTime(a, b)), put = saved.filter(x => x.hidden).length;
    const all = saved.filter(x => hiddenShown || !x.hidden), wanted = $('search').value.trim().toLowerCase();
    const found = wanted ? all.filter(x => matchesTask(x, wanted.split(/\s+/))) : all;
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
    if (window.RemoteSelect) window.RemoteSelect.stop();
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
  function enter(name) { cameFrom = tab; tab = 'projects'; project = name; shown = 16; endedShown = 8; hiddenShown = false; Object.keys(projectFolds).forEach(key => { projectFolds[key] = false; }); $('search').value = ''; history.pushState({ project: name }, ''); scrollTo(0, 0); draw(); }
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
  $('organize').addEventListener('click', () => window.RemoteOrganize.open({
    targets: organizeCandidates(), filtered: !!($('query').value.trim() || filter.tool || filter.source || filter.follow)
  }));
  // While something is typed the tabs make room: the keyboard takes the foot of the screen.
  document.addEventListener('focusin', event => { if (event.target.matches && event.target.matches('#home input, #home textarea')) document.body.classList.add('typing'); });
  document.addEventListener('focusout', () => document.body.classList.remove('typing'));
  $('search').addEventListener('input', () => { shown = 16; draw(); });
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
      const returnList = first ? startParams.get('backList') || history.state?.listKey : '';
      if (!returnList || !restoreListPosition(returnList)) draw();
      peeks();
      if (window.RemoteTeams && window.RemoteTeams.sync) window.RemoteTeams.sync();
      if (wantedTeam && tab !== 'together') {
        const id = wantedTeam;
        wantedTeam = '';
        if (usable() && window.RemoteTeams && window.RemoteTeams.open) {
          const card = ((data.device && data.device.team_cards) || []).find(c => c && c.id === id);
          window.RemoteTeams.open(id, card || undefined);
        }
      }
      if (anew) {
        anew = false; history.replaceState(history.state, '', location.pathname + (project ? '?project=' + encodeURIComponent(project) : ''));
        beginAnywhere().then(leave);
      }
      if (take) {
        const wanted = (data.sessions || []).find(s => s.id === take && !s.terminal);
        take = ''; history.replaceState(history.state, '', location.pathname + (project ? '?project=' + encodeURIComponent(project) : ''));
        if (wanted && usable()) (wanted.live ? takeOver(wanted) : start(wanted.tool, wanted.dir, wanted.id, false)).then(leave);
      }
      if (readFromBench) {
        const wanted = (data.sessions || []).find(s => s.id === readFromBench && s.tool === startParams.get('tool'));
        readFromBench = '';
        if (wanted) (wanted.live ? takeOver(wanted) : readHistory(wanted)).then(leave);
        else toast('这段对话已不在当前列表');
      }
      if (organizeFromBench && window.RemoteOrganize) {
        organizeFromBench = false;
        if (!pageHost.has('organize') || !usable()) return toast('当前电脑暂时无法整理对话');
        const sheet = window.RemoteOrganize.open({ session: startParams.get('session'), tool: startParams.get('tool') });
        sheet.result.then(() => leave(false));
      }
    }
    catch (error) { if (!$('home').hidden) { $('device-text').textContent = error.message; $('dot').className = 'off'; } }
    if (!$('home').hidden) timer = setTimeout(refresh, document.hidden ? 15000 : 2500);
  }
  document.addEventListener('visibilitychange', () => { if (!document.hidden && !$('home').hidden) refresh(); });
  window.addEventListener('pageshow', event => {
    if (!event.persisted || $('home').hidden) return;
    const key = history.state?.listKey;
    if (key) restoreListPosition(key, false);
    refresh();
  });

  // What the views of their own work with: the page's helpers, what the computer last said, and the ways into a
  // conversation. A view names its tab and says how it is opened (asked for what it shows) and drawn.
  const pageHost = window.RemoteCliPage = {
    $, el, svg, ICON, TOOLS, api, newId, toast, openSheet, closeReader, ask, ago, base, things, whereOf, wayOf, usable, running, phaseOf, go, takeOver, open, start, chat, chatOk, canEnd, held, saved, keep, loadMarkdown, markdownNode,
    openSession: s => { if (!usable()) return toast('电脑离线，暂时无法打开'); return s.live ? takeOver(s) : readHistory(s); },
    get data() { return data; },
    get relayVersion() { return relayVersion; }, startAgyShell,
    get tab() { return tab; },
    get project() { return project; },
    get computerName() { return computerName; },
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
      relayVersion = session.version || '';
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
