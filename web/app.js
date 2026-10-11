/* The list pages: every project at a glance (what is running and what needs you), and one project in detail. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  // The plain terminal is called what the computer calls it: PowerShell on Windows, the person's shell elsewhere.
  const TOOLS = { claude: 'Claude Code', codex: 'Codex', shell: '终端' };
  const ABOUT = { claude: 'Anthropic', codex: 'OpenAI', shell: '命令行' };
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  const native = window.RemoteCliNative || null;
  // The look belongs to the app, not to one computer: inside the app it is kept there, so every computer's pages
  // open in the same skin and text size. A browser keeps it per address, as it keeps everything else.
  const LOOK = ['skin', 'text'];
  const saved = (key, fallback) => {
    if (native && native.pref && LOOK.includes(key)) { const chosen = native.pref(key); if (chosen) return chosen; }
    try { return localStorage.getItem('rcli-' + key) || fallback; } catch (error) { return fallback; }
  };
  const keep = (key, value) => {
    if (native && native.setPref && LOOK.includes(key)) native.setPref(key, value);
    try { localStorage.setItem('rcli-' + key, value); } catch (error) { /* private window */ }
  };
  let data = null, project = '', timer = 0, busy = false, shown = 12, endedShown = 8, toastTimer = 0;
  // The first list is asked for while the sign-in is still being checked: over a tunnel each answer takes a
  // quarter of a second, and waiting for one before asking for the other doubled the time to the first picture.
  let early = null;

  // ---- drawing helpers
  const svg = (path, size) => `<svg viewBox="0 0 24 24" width="${size || 22}" height="${size || 22}" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${path}</svg>`;
  const ICON = {
    back: svg('<path d="M15 5l-7 7 7 7"/>'), more: svg('<circle cx="5" cy="12" r="1.3" fill="currentColor"/><circle cx="12" cy="12" r="1.3" fill="currentColor"/><circle cx="19" cy="12" r="1.3" fill="currentColor"/>'),
    look: svg('<circle cx="12" cy="12" r="8.5"/><path d="M12 3.5v17M12 7.5a4.5 4.5 0 0 1 0 9" /><path d="M12 3.5a8.5 8.5 0 0 1 0 17z" fill="currentColor" stroke="none" opacity=".35"/>'),
    chev: svg('<path d="M9 6l6 6-6 6"/>', 18), stop: svg('<rect x="6" y="6" width="12" height="12" rx="3.2" fill="currentColor" stroke="none"/>', 18), plus: svg('<path d="M12 5v14M5 12h14"/>', 20),
    folder: svg('<path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2.2h7a2 2 0 0 1 2 2v7.8a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/>'),
    claude: svg('<path d="M12 3.5v17M3.5 12h17M6 6l12 12M18 6L6 18"/>'), codex: svg('<path d="M12 3l7.8 4.5v9L12 21l-7.8-4.5v-9z"/><path d="M9 10l-2 2 2 2M15 10l2 2-2 2"/>'),
    say: svg('<path d="M20.5 3.5L3.5 10.5l6.5 3 3 6.5zM10 13.5L20.5 3.500"/>', 18),
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

  // ---- look: the skin chosen here is the one the terminal page opens with
  const TEXT = { small: ['紧凑', '14px'], normal: ['标准', '15.5px'], large: ['大', '17px'], larger: ['特大', '19px'] };
  // ?skin=<name> chooses a skin from a link, for example when showing the app to someone.
  const asked = new URLSearchParams(location.search).get('skin');
  if (asked && window.RemoteCliSkins[asked]) keep('skin', asked);
  function paint() {
    const skin = window.RemoteCliPaint(saved('skin', 'paper'));
    document.documentElement.style.fontSize = (TEXT[saved('text', 'normal')] || TEXT.normal)[1];
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = skin.t.background;
    if (native) native.chrome(skin.t.background);
  }
  paint();
  $('back').innerHTML = ICON.back; $('look').innerHTML = ICON.look; $('more').innerHTML = ICON.more;
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
  function openSheet(build) {
    return new Promise(done => {
      const form = $('sheet-form'), sheet = $('sheet');
      form.textContent = '';
      let answer = null;
      build(form, value => { answer = value; sheet.close(); });
      form.append(el('button', { type: 'button', className: 'cancel', textContent: '取消', onclick: () => sheet.close() }));
      sheet.onclose = () => done(answer);
      sheet.onclick = event => { if (event.target === sheet) sheet.close(); };       // a tap beside the sheet
      sheet.showModal();
    });
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
  $('look').addEventListener('click', () => openSheet(form => {
    const skins = window.RemoteCliSkins;
    form.append(el('h3', { textContent: '外观' }), el('p', { textContent: '配色同时用于列表和终端。' }));
    const grid = el('div', { className: 'swatches' }), sizes = el('div', { className: 'sizes' });
    const mark = () => {
      grid.querySelectorAll('button').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.key === saved('skin', 'paper'))));
      sizes.querySelectorAll('button').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.key === saved('text', 'normal'))));
    };
    Object.keys(skins).forEach(key => {
      const s = skins[key];
      const preview = el('i', { style: 'background:' + s.t.background }); preview.style.background = s.t.background;
      [[s.t.foreground, 22], [s.accent, 30], [s.t.green, 16], [s.t.yellow, 24], [s.t.red, 12]].forEach(([colour, height]) => { const bar = el('span'); bar.style.background = colour; bar.style.height = height + 'px'; preview.append(bar); });
      const button = el('button', { type: 'button', onclick: () => { keep('skin', key); paint(); mark(); } }, preview, s.name + (s.light ? ' · 明亮' : ''));
      button.dataset.key = key; grid.append(button);
    });
    Object.keys(TEXT).forEach(key => { const button = el('button', { type: 'button', textContent: TEXT[key][0], onclick: () => { keep('text', key); paint(); mark(); } }); button.dataset.key = key; sizes.append(button); });
    form.append(grid, el('label', { textContent: '文字大小' }), sizes);
    mark();
  }));
  $('more').addEventListener('click', async () => {
    const ready = data && data.device.online && data.device.enabled;
    const choices = [];
    if (ready) choices.push({ label: '添加项目', sub: '添加电脑上的一个文件夹', value: 'add' });
    if (ready && (data.device.features || []).includes('settings')) choices.push({ label: '电脑设置', sub: '切换连接线路，更改电脑端的几项设置', value: 'settings' });
    if (native) choices.push({ label: '回到工作台', sub: '查看所有电脑和进行中的任务，或换一台电脑', value: 'switch' });
    if (native && native.update) choices.push({ label: '检查 App 更新', sub: '当前版本 ' + (native.version ? native.version() : ''), value: 'update' });
    const device = data ? data.device : {};
    if (ready && (device.features || []).includes('update')) choices.push(device.newer
      ? { label: '更新电脑端到 v' + device.newer, sub: '当前 v' + device.version + '。更新时会中断几秒，终端随后接回原对话', value: 'computer' }
      : { label: '检查电脑端更新', sub: '电脑端当前 v' + device.version + '；有新版本时自动安装', value: 'computer' });
    choices.push({ label: '退出登录', sub: '下次需要重新扫码或输入密码', value: 'out', kind: 'danger' });
    const choice = await ask('更多', '', choices);
    if (choice === 'add') addProject();
    else if (choice === 'settings') location.href = 'computer/';
    else if (choice === 'switch') native.disconnect();
    else if (choice === 'update') native.update();
    else if (choice === 'computer') {
      try { await api('api/terminal', { action: 'update', id: newId() }); toast(device.newer ? '电脑端开始更新，中断几秒后自动恢复' : '已让电脑端检查更新'); }
      catch (error) { toast(error.message); }
    }
    else if (choice === 'out') { try { await api('api/logout', {}); } catch (error) { /* signed out anyway */ } if (native) native.disconnect(); else showLogin(); }
  });

  function showLogin() {
    clearTimeout(timer);
    $('home').hidden = true; $('login').hidden = false;
    $('password').focus();
  }
  $('login-form').addEventListener('submit', async event => {
    event.preventDefault();
    $('login-error').textContent = '';
    try {
      await api('api/login', { password: $('password').value });
      $('password').value = '';
      $('login').hidden = true; $('home').hidden = false;
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
  const PHASE = { confirm: ['等你确认', 0], done: ['已完成', 1], busy: ['正在执行', 2], starting: ['正在启动', 3], idle: ['等待输入', 4], ended: ['已结束', 8], failed: ['异常退出', 7] };
  const running = t => t.state === 'running' || t.state === 'starting';
  const usable = () => data && data.device.online && data.device.enabled;

  // Sends one operation and waits until the computer has carried it out.
  async function run(payload, waiting) {
    if (busy) return null;
    busy = true; toast(waiting);
    try {
      const op = Object.assign({ id: newId() }, payload);
      let result = await api('api/terminal', op);
      for (let n = 0; n < 60 && result.state === 'queued'; n++) {
        await new Promise(r => setTimeout(r, 400));
        result = await api('api/terminal', op);
      }
      if (result.error) throw new Error(result.error);
      if (result.state === 'queued') throw new Error('电脑没有响应，请确认电脑端程序在运行');
      toast('');
      return result;
    } catch (error) { toast(error.message); return null; } finally { busy = false; }
  }
  // ?take=<conversation> comes from the app's workbench: the question about taking over a conversation that runs on
  // the computer is asked at once, and the terminal that follows leads back to the workbench.
  let take = new URLSearchParams(location.search).get('take') || '', anew = new URLSearchParams(location.search).get('new') === '1', fromBench = !!take || anew;
  function open(terminal) { location.href = 'terminal/?id=' + encodeURIComponent(terminal) + (fromBench ? '&from=bench' : ''); }
  async function start(tool, dir, session, takeover, fork) {
    const payload = { action: 'start', tool, dir, history: false };
    if (session) Object.assign(payload, { session, takeover: !!takeover });
    if (fork) payload.fork = true;
    const result = await run(payload, '正在电脑上启动 ' + TOOLS[tool] + '…');
    if (result) open(result.terminal);
  }
  // A message for a running terminal without going into it: what the box of its page would send. The relay puts it
  // the way the program takes pasted text and adds Enter (the operation "say").
  async function say(t) {
    const text = await openSheet((form, finish) => {
      const box = el('textarea', { rows: 3, maxLength: 8000, placeholder: '发送消息', autocapitalize: 'off', spellcheck: false, ariaLabel: '消息' });
      // what the program said last is shown, to tell which terminal this is and where it stands
      form.append(el('h3', { textContent: t.title }), el('p', { textContent: t.said || TOOLS[t.tool] + ' · ' + t.dir }), box,
        el('button', { type: 'button', className: 'choice solid', onclick: () => { if (box.value.trim()) finish(box.value); else box.focus(); } }, el('span', { textContent: '发送' })));
      setTimeout(() => box.focus(), 80);
    });
    if (text && await run({ action: 'say', terminal: t.id, text }, '')) { toast('已发送'); refresh(); }
  }
  async function endTerminal(t) {
    if (!await ask('结束“' + t.title + '”？', '正在运行的任务会被中断。对话保存在电脑上，可以从历史对话继续。', [{ label: '结束终端', value: true, kind: 'danger' }])) return;
    await run({ action: 'close', terminal: t.id }, '正在结束…');
    refresh();
  }
  // A conversation that a program on the computer has open. Looking at it does nothing to that program: the last
  // things said are read from the conversation's file. Carrying on from the phone is a separate, deliberate step,
  // offered below what was said, in the words of what will happen on the computer.
  function ways(session) {
    const host = sessionHost(session), locked = sessionActivity(session) === 'locked';
    const canClose = session.can_takeover === true && !locked;
    const choices = [canClose
      ? { label: '在手机上继续', sub: '电脑上的对应窗口将关闭，正在执行的任务会中断', value: 'close', kind: 'solid' }
      : { label: '已在电脑上关闭，在手机上继续', sub: '如仍在运行，不会关闭它，并说明原因', value: 'close', kind: 'solid' }];
    if (session.tool === 'codex' && (data.device.features || []).includes('codex-fork'))
      choices.push({ label: '另开一份继续', sub: '以当前内容新建一段对话；电脑上的原对话不受影响，两者此后相互独立', value: 'copy' });
    const why = canClose ? ''
      : host === 'shared' ? '这段对话正由电脑上的 Codex 应用或编辑器使用，手机无法关闭它。请先在该应用中结束这段对话。'
      : session.takeover_reason || '无法确定使用这段对话的程序，因此不会关闭它。请先在电脑上结束该程序。';
    return { choices, why };
  }
  // What was said, laid out to be read: the tools answer in Markdown, which is set as text with headings, lists, code
  // and tables rather than shown as its source; what the person asked stands apart on the right; and a run of things
  // the tool did is one quiet block, folded when it is long, so that it does not push the talk off the screen.
  const loading = {};
  const need = name => loading[name] || (loading[name] = new Promise((done, failed) => {
    const script = el('script', { src: 'files/vendor/' + name, onload: done, onerror: failed });
    document.head.append(script);
  }));
  async function writeSaid(box, messages, toolName) {
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
        if (!run) { run = { list: el('ul'), count: 0 }; run.box = el('details', { className: 'did' }, el('summary'), run.list); box.append(run.box); }
        run.list.append(el('li', { textContent: m.text })); run.count++;
        run.box.firstChild.textContent = '做了 ' + run.count + ' 个操作';
        run.box.open = run.count <= 3;          // a few are shown; a long run is opened by a tap
        return;
      }
      run = null;
      const mine = m.role === 'user';
      box.append(el('div', { className: 'turn ' + (mine ? 'user' : 'assistant') },
        !mine && last !== 'assistant' ? el('b', { textContent: toolName }) : '', set(m.text)));
      last = m.role;
    });
  }
  async function takeOver(session) {
    if (!usable()) return;
    const { choices, why } = ways(session), canPeek = (data.device.features || []).includes('peek');
    const choice = await openSheet((form, finish) => {
      form.append(el('h3', { textContent: session.title }),
        el('p', { textContent: (hostLabel[sessionHost(session)] || hostLabel.unknown) + ' · ' + TOOLS[session.tool] + ' · ' + session.dir }));
      if (canPeek) {
        const said = el('div', { className: 'said' }, el('p', { className: 'quiet', textContent: '正在读取对话…' }));
        form.append(said, el('p', { className: 'quiet', textContent: '只是查看，不影响电脑上的程序。' }));
        api('api/conversation', { id: newId(), session: session.id }).then(got => {
          said.textContent = '';
          const messages = got.messages || [];
          if (got.more) said.append(el('p', { className: 'quiet', textContent: '更早的内容没有显示' }));
          if (!messages.length) said.append(el('p', { className: 'quiet', textContent: '这段对话还没有内容' }));
          writeSaid(said, messages, TOOLS[session.tool]).then(() => { said.scrollTop = said.scrollHeight; });      // what was said last is what one came to see
        }).catch(error => { said.textContent = ''; said.append(el('p', { className: 'quiet', textContent: error.message })); });
      }
      if (why) form.append(el('p', { className: 'why', textContent: why }));
      choices.forEach(c => form.append(el('button', { type: 'button', className: 'choice ' + (c.kind || ''), onclick: () => finish(c.value) },
        el('span', null, c.label, el('small', { textContent: c.sub })))));
    });
    if (choice) start(session.tool, session.dir, session.id, choice === 'close', choice === 'copy');
  }
  // ?new=1 comes from the app's workbench: a terminal is started without walking to a project first. The projects
  // used last come first; each offers the tools the computer has.
  async function startAnywhere() {
    if (!usable()) return toast('电脑离线，暂时不能新建');
    const tools = Object.keys(TOOLS).filter(t => (data.device.tools || []).includes(t));
    const latest = name => Math.max(0, ...data.terminals.filter(t => t.dir === name).map(t => t.created || 0), ...(data.sessions || []).filter(s => s.dir === name).map(s => s.updated || 0));
    const names = (data.device.workspaces || []).slice().sort((a, b) => latest(b) - latest(a));
    if (!names.length || !tools.length) return toast(names.length ? '电脑上没有可用的工具' : '请先添加一个项目');
    const picked = await openSheet((form, finish) => {
      form.append(el('h3', { textContent: '新建终端' }), el('p', { textContent: '选一个项目和要用的工具。最近用过的项目在前。' }));
      const list = el('div', { className: 'anywhere' });
      names.slice(0, 12).forEach(name => list.append(el('div', { className: 'row' }, el('b', { textContent: name }),
        el('span', null, ...tools.map(tool => el('button', { type: 'button', className: 'tool ' + tool, ariaLabel: '在 ' + name + ' 新建 ' + TOOLS[tool], html: ICON[tool], onclick: () => finish({ tool, name }) }))))));
      form.append(list);
    });
    if (picked) start(picked.tool, picked.name);
  }
  async function addProject() {
    const candidates = (data.device.candidates || []).slice(0, 4).map(c => ({ label: '添加 ' + c.name, sub: c.path, value: c.path }));
    const answer = await ask('添加项目', '填写电脑上的完整文件夹路径；下面是电脑上有对话记录的文件夹。',
      [{ label: '添加填写的路径', value: '', kind: 'solid' }].concat(candidates),
      [{ key: 'path', label: '文件夹路径', placeholder: 'D:\\projects\\demo' }, { key: 'name', label: '名称（可不填）', max: 40 }]);
    if (!answer) return;
    const path = answer.choice || answer.path;
    if (!path) { toast('请填写文件夹路径'); return; }
    if (await run({ action: 'project_add', path, name: answer.choice ? '' : answer.name, create: false }, '正在添加…')) refresh();
  }

  // ---- cards
  function pill(kind, text) { return el('span', { className: 'pill ' + kind, textContent: text }); }
  // A terminal opened from the phone.
  function terminalCard(t, withProject) {
    const phase = phaseOf(t), meta = el('span', { className: 'meta' }, pill(phase, PHASE[phase][0]));
    if (withProject) meta.append(el('span', { className: 'chip', textContent: t.dir }));
    meta.append(el('span', { textContent: TOOLS[t.tool] + (t.phase_at ? ' · ' + ago(t.phase_at) : '') }));
    // What the program last said. It changes far more often than the card, so peeks() writes it without a rebuild.
    const peek = el('span', { className: 'peek', hidden: true });
    peek.dataset.peek = t.id;
    const card = el('li', { className: 'card ' + phase }, el('button', { type: 'button', className: 'open', onclick: () => open(t.id) },
      el('span', { className: 'tool ' + t.tool, html: ICON[t.tool] }), el('span', { className: 'text' }, el('b', { textContent: t.title }), meta, peek)));
    // A terminal that waits on a question is answered below, where the question is read: Enter would say yes to it.
    if (t.state === 'running' && phase !== 'confirm') card.append(el('button', { type: 'button', className: 'side say', ariaLabel: '给这个终端发消息', html: ICON.say, onclick: () => say(t) }));
    if (running(t)) card.append(el('button', { type: 'button', className: 'side', ariaLabel: '结束这个终端', html: ICON.stop, onclick: () => endTerminal(t) }));
    // A question the program waits on is answered here, under what it asks: the question is on the card, so nothing
    // is allowed unseen. The two keys are the ones Claude Code and Codex take for "yes" and "no".
    if (phase === 'confirm' && (t.asks || []).length) {
      const answer = (key, done) => async () => { if (await run({ action: 'input', terminal: t.id, data: key }, '')) { toast(done); refresh(); } };
      card.classList.add('asking');
      card.append(el('div', { className: 'ask' }, el('pre', { textContent: t.asks.join('\n') }), el('div', { className: 'answers' },
        el('button', { type: 'button', className: 'yes', textContent: '允许（回车）', onclick: answer('\r', '已允许') }),
        el('button', { type: 'button', className: 'no', textContent: '拒绝（Esc）', onclick: answer('\x1b', '已拒绝') }),
        el('button', { type: 'button', className: 'see', textContent: '进去看', onclick: () => open(t.id) }))));
    }
    return card;
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
  function sessionCard(s, withProject) {
    const meta = el('span', { className: 'meta' });
    if (s.live) {
      const state = sessionActivity(s);
      meta.append(pill(state === 'active' && s.status === 'busy' ? 'busy' : state === 'active' ? 'pc' : 'locked', (s.origin ? s.origin + ' · ' : '') + (hostLabel[sessionHost(s)] || hostLabel.unknown)));
    }
    if (withProject) meta.append(el('span', { className: 'chip', textContent: s.dir }));
    meta.append(el('span', { textContent: TOOLS[s.tool] + ' · ' + ago(s.updated) }));
    return el('li', { className: 'card' }, el('button', { type: 'button', className: 'open', onclick: () => { if (!usable()) return toast('电脑离线，暂时无法打开'); if (s.live) takeOver(s); else start(s.tool, s.dir, s.id, false); } },
      el('span', { className: 'tool ' + s.tool, html: ICON[s.tool] }), el('span', { className: 'text' }, el('b', { textContent: s.title }), meta), el('span', { className: 'chev', html: ICON.chev })));
  }
  // Lists are rebuilt only when what they show has changed, so a press or a scroll is not interrupted every few seconds.
  function fill(list, cards, signature) {
    if (list.dataset.signature === signature) return;
    list.dataset.signature = signature;
    list.replaceChildren(...cards);
  }
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
      el('span', { textContent: device.online ? '在电脑上的 Remote CLI“设置”里打开“允许手机访问”。' : '请确认电脑已开机且 Remote CLI 正在运行。使用公网隧道时，退出程序或重启电脑后地址会变更，需要重新扫码。' }));
    if (project && !names.includes(project)) { project = ''; }
    const terminals = data.terminals || [], sessions = (data.sessions || []).filter(s => !s.terminal);
    const mine = terminals.filter(running).map(t => Object.assign({ rank: PHASE[phaseOf(t)][1], at: t.phase_at || t.created }, t));
    const live = sessions.filter(s => sessionActivity(s) === 'active');
    const background = sessions.filter(s => sessionActivity(s) === 'locked');
    $('overview').hidden = !!project; $('project').hidden = !project;
    // In the app the arrow on the first page leads to the workbench, where every computer is listed.
    $('back').hidden = !project && !(native && native.home);
    $('back').setAttribute('aria-label', project ? '返回全部项目' : '返回工作台');
    $('heading').textContent = project || 'Remote CLI';
    const waiting = mine.filter(t => phaseOf(t) === 'confirm').length, done = mine.filter(t => phaseOf(t) === 'done').length, working = mine.filter(t => phaseOf(t) === 'busy').length;
    $('device-text').textContent = !ready ? (device.online ? '已暂停访问' : '电脑离线')
      : project ? '电脑在线' : waiting ? waiting + ' 个任务等你确认' : done ? done + ' 个任务已完成' : working ? working + ' 个任务在执行' : '电脑在线';

    if (!project) {
      const order = mine.slice().sort((a, b) => a.rank - b.rank || b.at - a.at);
      const cards = order.map(t => terminalCard(t, true)).concat(live.sort((a, b) => b.updated - a.updated).map(s => sessionCard(s, true)));
      $('active').hidden = !cards.length;
      fill($('active-list'), cards, JSON.stringify([order.map(t => [t.id, phaseOf(t), t.title, t.phase_at]), live.map(s => [s.id, s.status, s.title, s.host, s.origin]), stamp(1), ready]));
      $('background').hidden = !background.length;
      $('background-summary').textContent = background.length + ' 段 · 不计入运行中';
      fill($('background-list'), background.map(s => sessionCard(s, true)), JSON.stringify([background.map(s => [s.id, s.status, s.title, s.host, s.origin]), stamp(1), ready]));
      const parts = [];
      if (waiting) parts.push(waiting + ' 个等你确认'); if (done) parts.push(done + ' 个已完成'); if (working) parts.push(working + ' 个在执行');
      $('active-summary').textContent = parts.join(' · ') || (cards.length + ' 个终端在运行');
      const list = names.map(name => {
        const folder = (device.projects || []).find(p => p.name === name) || {};
        const own = mine.filter(t => t.dir === name), count = sessions.filter(s => s.dir === name).length;
        const need = own.filter(t => phaseOf(t) === 'confirm').length, done = own.filter(t => phaseOf(t) === 'done').length, work = own.filter(t => phaseOf(t) === 'busy').length;
        const meta = el('span', { className: 'meta' });
        if (need) { const c = el('span', { className: 'count', textContent: need + ' 个等你确认' }); c.prepend(el('i')); c.style.setProperty('--c', 'var(--bad)'); c.style.color = 'var(--bad)'; meta.append(c); }
        if (done) { const c = el('span', { className: 'count', textContent: done + ' 个已完成' }); c.prepend(el('i')); c.style.setProperty('--c', 'var(--good)'); c.style.color = 'var(--good)'; meta.append(c); }
        if (work) { const c = el('span', { className: 'count', textContent: work + ' 个在执行' }); c.prepend(el('i')); c.style.setProperty('--c', 'var(--busy)'); c.style.color = 'var(--busy)'; meta.append(c); }
        if (own.length && !need && !done && !work) meta.append(el('span', { textContent: own.length + ' 个终端开着' }));
        meta.append(el('span', { textContent: count ? count + ' 段对话' : '还没有对话' }));
        return { name, card: el('li', { className: 'card' }, el('button', { type: 'button', className: 'open', onclick: () => enter(name) },
          el('span', { className: 'tool folder', html: ICON.folder }), el('span', { className: 'text' }, el('b', { textContent: name }), meta), el('span', { className: 'chev', html: ICON.chev }))), sign: [name, need, done, work, own.length, count, folder.path] };
      });
      const cardsP = list.map(x => x.card);
      if (ready) cardsP.push(el('li', { className: 'card add' }, el('button', { type: 'button', className: 'open', onclick: addProject, html: ICON.plus + '<span>添加项目</span>' })));
      fill($('project-list'), cardsP, JSON.stringify([list.map(x => x.sign), ready]));
      $('project-summary').textContent = names.length ? names.length + ' 个' : '还没有项目';
      return;
    }

    const folder = (device.projects || []).find(p => p.name === project);
    $('project-path').textContent = folder ? '\u200e' + folder.path : '';      // read from the left although it is cut at the left
    const tools = Object.keys(TOOLS).filter(t => (device.tools || []).includes(t));
    const launch = $('launch');
    if (launch.dataset.signature !== tools.join() + ready) {
      launch.dataset.signature = tools.join() + ready;
      launch.replaceChildren(...tools.map(tool => el('button', { type: 'button', disabled: !ready, onclick: () => start(tool, project) },
        el('span', { className: 'tool ' + tool, html: ICON[tool] }), el('span', null, '新建 ' + TOOLS[tool], el('small', { textContent: ABOUT[tool] })))));
      if (ready && !tools.length) launch.append(el('p', { textContent: '电脑上没有找到 claude、codex 或命令行终端。' }));
    }
    const own = mine.filter(t => t.dir === project).sort((a, b) => a.rank - b.rank || b.at - a.at);
    $('p-active').hidden = !own.length;
    fill($('p-active').querySelector('ul'), own.map(t => terminalCard(t, false)), JSON.stringify([own.map(t => [t.id, phaseOf(t), t.title, t.phase_at]), stamp(1)]));
    const here = sessions.filter(s => s.dir === project), open2 = here.filter(s => sessionActivity(s) === 'active'), held = here.filter(s => sessionActivity(s) === 'locked');
    $('p-live').hidden = !open2.length;
    fill($('p-live').querySelector('ul'), open2.map(s => sessionCard(s, false)), JSON.stringify([open2.map(s => [s.id, s.status, s.title, s.host, s.origin]), stamp(1)]));
    $('p-background').hidden = !held.length;
    $('p-background-summary').textContent = held.length + ' 段 · 不计入运行中';
    fill($('p-background').querySelector('ul'), held.map(s => sessionCard(s, false)), JSON.stringify([held.map(s => [s.id, s.status, s.title, s.host, s.origin]), stamp(1)]));
    const all = here.filter(s => !s.live).sort((a, b) => b.updated - a.updated), wanted = $('search').value.trim().toLowerCase();
    const found = wanted ? all.filter(s => s.title.toLowerCase().includes(wanted)) : all;
    $('p-history').hidden = !all.length;
    $('search').hidden = all.length <= 8;
    $('history-count').textContent = wanted ? found.length + ' / ' + all.length + ' 段' : all.length + ' 段';
    fill($('p-history').querySelector('ul'), found.slice(0, shown).map(s => sessionCard(s, false)), JSON.stringify([found.slice(0, shown).map(s => [s.id, s.title]), stamp(5), ready]));
    $('more-history').hidden = found.length <= shown;
    // Kept terminal screens and saved conversations have different actions: view a screen or resume a conversation.
    const ended = terminals.filter(t => t.dir === project && !running(t)).sort((a, b) => (b.phase_at || b.created) - (a.phase_at || a.created));
    $('p-ended').hidden = !ended.length;
    fill($('p-ended').querySelector('ul'), ended.slice(0, endedShown).map(t => terminalCard(t, false)), JSON.stringify([ended.slice(0, endedShown).map(t => [t.id, t.phase, t.title]), stamp(5)]));
    $('more-ended').hidden = ended.length <= endedShown;
    $('empty').hidden = own.length + here.length + ended.length > 0;
  }

  // ---- moving between the overview and one project; the phone's back key goes back to the overview
  function enter(name) { project = name; shown = 12; endedShown = 8; $('search').value = ''; history.pushState({ project: name }, ''); scrollTo(0, 0); draw(); }
  window.addEventListener('popstate', event => { project = event.state && event.state.project || ''; if (data) draw(); });
  // A project opened by a link (the workbench does that) has no overview behind it in the history.
  $('back').addEventListener('click', () => {
    if (project && history.state && history.state.project) return history.back();
    if (native && native.home) return native.home();
    project = ''; history.replaceState(null, '', location.pathname); draw();
  });
  $('search').addEventListener('input', () => { shown = 12; draw(); });
  $('more-history').addEventListener('click', () => { shown += 30; draw(); });
  $('more-ended').addEventListener('click', () => { endedShown += 30; draw(); });

  async function refresh() {
    clearTimeout(timer);
    try {
      const asked = early; early = null;
      data = (asked && await asked) || await api('api/terminal');
      if (data.device.shell) TOOLS.shell = data.device.shell;
      draw(); peeks();
      if (anew) {
        anew = false; history.replaceState(history.state, '', location.pathname + (project ? '?project=' + encodeURIComponent(project) : ''));
        startAnywhere();
      }
      if (take) {
        const wanted = (data.sessions || []).find(s => s.id === take && !s.terminal);
        take = ''; history.replaceState(history.state, '', location.pathname + (project ? '?project=' + encodeURIComponent(project) : ''));
        if (wanted && usable()) { if (wanted.live) takeOver(wanted); else start(wanted.tool, wanted.dir, wanted.id, false); }
      }
    }
    catch (error) { if (!$('home').hidden) { $('device-text').textContent = error.message; $('dot').className = 'off'; } }
    if (!$('home').hidden) timer = setTimeout(refresh, document.hidden ? 15000 : 2500);
  }
  document.addEventListener('visibilitychange', () => { if (!document.hidden && !$('home').hidden) refresh(); });
  window.addEventListener('pageshow', event => { if (event.persisted && !$('home').hidden) refresh(); });      // back from a terminal

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
      $('home').hidden = false;
      refresh();
    } catch (error) { showLogin(); $('login-error').textContent = '连不上服务，请检查地址'; }
  })();
})();
