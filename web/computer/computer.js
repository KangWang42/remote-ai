/* The settings of one computer, on the phone: the line it is reached by, a few settings of the program on it, the
   look of these pages, the app, and signing out.

   Changing the line is done by the computer, so that a phone far from it is not shut out: it makes the new line
   ready beside the one in use and hands this page its address with a sign-in that works once; the page goes there
   and says that it arrived. A page that does not arrive is waited for, and then the computer goes back to the line
   it had (docs/PROTOCOL.md, "The program itself"). */
(() => {
  'use strict';
  const $ = id => document.getElementById(id), native = window.RemoteCliNative || null;
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  const wait = ms => new Promise(done => setTimeout(done, ms));
  // The look belongs to the app, not to one computer (see ../app.js).
  const saved = (key, fallback) => {
    if (native && native.pref) { const chosen = native.pref(key); if (chosen) return chosen; }
    try { return localStorage.getItem('rcli-' + key) || fallback; } catch (error) { return fallback; }
  };
  const keep = (key, value) => {
    if (native && native.setPref) native.setPref(key, value);
    try { localStorage.setItem('rcli-' + key, value); } catch (error) { /* private window */ }
  };
  const svg = path => `<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${path}</svg>`;
  const relay = svg('<rect x="4" y="4.5" width="16" height="6" rx="2"/><rect x="4" y="13.5" width="16" height="6" rx="2"/><path d="M7.5 7.5h.01M7.5 16.5h.01"/>');
  const ICON = { back: svg('<path d="M15 5l-7 7 7 7"/>'), chev: svg('<path d="M9 6l6 6-6 6"/>'), own: relay, public: relay,
    lan: svg('<path d="M4.5 10a11 11 0 0 1 15 0M7.5 13.2a6.6 6.6 0 0 1 9 0M10.4 16.3a2.4 2.4 0 0 1 3.2 0"/><path d="M12 19.5h.01"/>'),
    cloud: svg('<path d="M7.5 18.5a4 4 0 0 1-.6-7.950 5.500 5.500 0 0 1 10.600 1.200 3.400 3.400 0 0 1-.5 6.750z"/>') };
  function el(tag, props, ...children) {
    const node = document.createElement(tag);
    Object.keys(props || {}).forEach(key => { if (key === 'html') node.innerHTML = props[key]; else if (key in node) node[key] = props[key]; else node.setAttribute(key, props[key]); });
    node.append(...children.filter(Boolean));
    return node;
  }
  // One row of a group: something to press when it has an action, else only shown.
  const row = (action, ...children) => el('li', null, action
    ? el('button', { type: 'button', className: 'row' + (action.kind ? ' ' + action.kind : ''), disabled: !!action.disabled, onclick: action.act }, ...children)
    : el('div', { className: 'row' }, ...children));
  const text = (title, about) => el('span', { className: 'text' }, el('b', { textContent: title }), about ? el('small', { textContent: about }) : '');
  let toastTimer = 0;
  function toast(words) { const box = $('toast'); box.textContent = words; box.hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => { box.hidden = true; }, 3000); }
  function confirm(title, words, yes, danger) {
    return new Promise(done => {
      const form = $('sheet-form'), sheet = $('sheet');
      let agreed = false;
      form.replaceChildren(el('h3', { textContent: title }), words ? el('p', { textContent: words }) : '',
        el('button', { type: 'button', className: 'choice ' + (danger ? 'danger' : 'solid'), onclick: () => { agreed = true; sheet.close(); } }, el('span', { textContent: yes })),
        el('button', { type: 'button', className: 'cancel', textContent: '取消', onclick: () => sheet.close() }));
      sheet.onclose = () => done(agreed);
      sheet.onclick = event => { if (event.target === sheet) sheet.close(); };
      sheet.showModal();
    });
  }

  // ---- look
  const TEXT = { small: ['紧凑', '14px'], normal: ['标准', '15.5px'], large: ['大', '17px'], larger: ['特大', '19px'] };
  function paint() {
    const skin = window.RemoteCliPaint(saved('skin', 'paper'));
    document.documentElement.style.fontSize = (TEXT[saved('text', 'normal')] || TEXT.normal)[1];
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = skin.t.background;
    if (native && native.chrome) native.chrome(skin.t.background);
  }
  function looks() {
    const skins = window.RemoteCliSkins;
    const mark = () => {
      $('skins').querySelectorAll('button').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.key === saved('skin', 'paper'))));
      $('sizes').querySelectorAll('button').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.key === saved('text', 'normal'))));
    };
    Object.keys(skins).forEach(key => {
      const s = skins[key], preview = el('i');
      preview.style.background = s.t.background;
      [[s.t.foreground, 22], [s.accent, 30], [s.t.green, 16], [s.t.yellow, 24], [s.t.red, 12]].forEach(([colour, height]) => { const bar = el('span'); bar.style.background = colour; bar.style.height = height + 'px'; preview.append(bar); });
      const button = el('button', { type: 'button', onclick: () => { keep('skin', key); paint(); mark(); } }, preview, s.name + (s.light ? ' · 明亮' : ''));
      button.dataset.key = key; $('skins').append(button);
    });
    Object.keys(TEXT).forEach(key => { const button = el('button', { type: 'button', textContent: TEXT[key][0], onclick: () => { keep('text', key); paint(); mark(); } }); button.dataset.key = key; $('sizes').append(button); });
    mark();
  }
  paint();
  $('back').innerHTML = ICON.back;
  // A page the phone was sent to from another line has nothing behind it.
  $('back').addEventListener('click', () => { if (history.length > 1 && document.referrer.startsWith(location.origin)) history.back(); else location.replace('../'); });

  // ---- asking the relay, and through it the program on the computer
  async function post(path, payload) {
    const reply = await fetch('../' + path, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
    let body = {};
    try { body = await reply.json(); } catch (error) { /* not JSON */ }
    if (reply.status === 401 && path !== 'api/login') { location.replace('../'); throw new Error('请重新登录'); }
    if (!reply.ok) throw new Error(body.error || '请求失败（' + reply.status + '）');
    return body;
  }
  const ask = (action, fields) => post('api/computer', Object.assign({ id: newId(), action }, fields));
  // Asked again for a while: the program's relay may be started again in the middle of a change, and a computer that
  // has just turned to this line is not there in the first moment.
  async function read(tries) {
    for (let n = 1; ; n++) {
      try { return await ask('settings_read'); }
      catch (error) { if (n >= tries) throw error; await wait(1000); }
    }
  }

  // ---- what is shown
  const KIND = { lan: ['局域网', '手机和电脑连同一个 Wi-Fi 时使用，速度最快'], cloud: ['公网隧道', '任何网络都能连；每次建立，地址都会变'],
    own: ['自己的中转', '部署在自己服务器上的中转，地址固定'], public: ['公共中转', '无需配置，地址固定；终端内容经由其服务器'] };
  // Of relays there may be many of a kind: each kind is one row, and pressing it lists its relays.
  const NONE = { own: '尚未添加，可在电脑端添加', public: '暂无可用线路' };
  const took = line => line.ms >= 0 ? line.ms + ' ms' : line.ms === -1 ? '连不上' : '';
  const SETTINGS = [
    { name: 'rights', title: '新终端的权限', about: 'Claude Code 和 Codex 从手机启动时的权限模式', choices: [['full', '默认'], ['edit', '自动改文件'], ['read', '只读']] },
    { name: 'tunnel', title: '公网隧道的协议', about: '隧道经常断开时改用 HTTP/2，下次建立时生效', choices: [['auto', '自动'], ['http2', 'HTTP/2']] },
    { name: 'autostart', title: '登录 Windows 后自动启动' },
    { name: 'reach', title: '手机处理电脑上的对话', about: '电脑上开着的 Claude Code 和 Codex 对话，可在手机上查看、确认和发消息，电脑上照常使用' },
    { name: 'update', title: '自动检查更新', about: '有新版本时提示' }];
  let device = null, told = null, busy = false, problem = '';
  const name = line => line.name || KIND[line.kind][0];
  const has = feature => !!device && (device.features || []).includes(feature);
  function state(kind, title, words) {
    const box = $('state');
    box.hidden = !title; box.className = 'notice ' + kind;
    $('state-title').textContent = title || ''; $('state-text').textContent = words || '';
  }
  function draw() {
    const there = !!device && device.online && device.enabled;
    $('dot').className = there ? 'on' : 'off';
    $('device-text').textContent = !device ? '连不上中转' : !device.online ? '电脑离线' : !device.enabled ? '电脑端已暂停手机访问' : (told ? told.name + ' · ' : '') + '电脑在线';
    // What only the program on the computer can tell: said in place of the rows when it cannot be asked.
    const why = !device ? '连不上中转，请检查网络后再试。' : !there ? '电脑离线，连上后可在此查看和更改。'
      : !has('settings') ? '这台电脑的电脑端不支持在手机上更改，请在电脑上操作。' : problem || '正在询问电脑…';
    $('lines').hidden = !told; $('lines-note').hidden = !!told; $('lines-note').textContent = why;
    $('lines').classList.toggle('held', busy);
    const lines = told ? told.lines : [], of = kind => lines.filter(line => line.kind === kind);
    const one = line => {
      const item = row({ act: () => turn(line), disabled: line.current || busy }, el('span', { className: 'tool ' + line.kind, html: ICON[line.kind] }),
        text(name(line), KIND[line.kind][1] + (line.kind === 'cloud' && !line.ready ? ' · 需先下载隧道程序' : '')), line.current ? el('span', { className: 'pill done', textContent: '使用中' }) : '');
      if (line.current) item.className = 'current';
      return item;
    };
    const several = kind => {
      const these = of(kind), current = these.find(line => line.current);
      const item = row({ act: () => choose(kind, these), disabled: busy || !these.length }, el('span', { className: 'tool ' + kind, html: ICON[kind] }),
        text(KIND[kind][0], current ? '正在使用：' + name(current) : these.length ? these.length + ' 条线路可选' : NONE[kind]),
        current ? el('span', { className: 'pill done', textContent: '使用中' }) : '', these.length ? el('span', { className: 'chev', html: ICON.chev }) : '');
      if (current) item.className = 'current';
      return item;
    };
    // those for everyone are offered when there are any; one's own always, to say where they are added
    $('lines').replaceChildren(...of('lan').map(one), ...of('cloud').map(one), ...(of('public').length ? [several('public')] : []), ...(told ? [several('own')] : []));
    // a setting the computer's program does not tell of is one it does not have
    // How the tools stand while the computer's program has its hooks in their settings, in place of what the switch is for.
    const stands = { good: ' 状态良好', ready: ' 已就绪', failed: ' 的配置写不进去' };
    const reached = told && told.settings.reach === true && (told.reach || []).length
      ? told.reach.map(one => one.name + stands[one.state] + (one.state === 'ready' && one.tool === 'codex' ? '，首次需在 Codex 里确认信任' : '')).join(' · ') : '';
    const well = !!reached && told.reach.every(one => one.state === 'good');
    const rows = (told ? SETTINGS.filter(setting => setting.name in told.settings) : []).map(setting => {
      const value = told.settings[setting.name];
      if (setting.name === 'reach' && reached) {
        const about = text(setting.title, reached);
        about.classList.toggle('well', well);
        return row(null, about, el('button', { type: 'button', className: 'flip', role: 'switch', 'aria-checked': 'true', 'aria-label': setting.title, onclick: () => change(setting.name, false) }));
      }
      const control = setting.choices
        ? el('span', { className: 'pick', role: 'group', 'aria-label': setting.title }, ...setting.choices.map(([key, label]) =>
            el('button', { type: 'button', textContent: label, 'aria-pressed': String(key === value), onclick: () => { if (key !== value) change(setting.name, key); } })))
        : el('button', { type: 'button', className: 'flip', role: 'switch', 'aria-checked': String(value === true), 'aria-label': setting.title, onclick: () => change(setting.name, value !== true) });
      return row(null, text(setting.title, setting.about), control);
    });
    if (device && device.version) {
      const can = there && has('update');
      rows.push(row(can ? { act: update } : null, text('电脑端版本', 'v' + device.version + (device.newer ? ' · 有新版本 v' + device.newer : '')),
        can ? el('span', { className: 'act', textContent: device.newer ? '更新' : '检查更新' }) : ''));
    }
    $('settings').hidden = !rows.length; $('settings-note').hidden = !!rows.length; $('settings-note').textContent = why;
    $('settings').replaceChildren(...rows);
  }
  async function change(setting, value) {
    try { told = await ask('settings_change', { name: setting, value }); toast('已保存'); }
    catch (error) { toast(error.message); }
    draw();
  }
  async function update() {
    if (device.newer && !await confirm('更新电脑端到 v' + device.newer + '？', '更新时手机会断开几秒，终端随后接回原对话。', '更新')) return;
    try { await post('api/terminal', { action: 'update', id: newId() }); toast(device.newer ? '电脑端开始更新，几秒后自动恢复' : '已让电脑端检查更新'); }
    catch (error) { toast(error.message); }
  }
  // What does not depend on the computer: the app itself, and signing out.
  function fixed() {
    if (native && native.update) {
      $('part-app').hidden = false;
      const rows = [];
      // looking by itself is off until it is switched on (an app that cannot say so looks by itself as before)
      if (native.autoUpdate && native.setAutoUpdate) {
        const on = native.autoUpdate();
        rows.push(row(null, text('自动检查更新', '有新版本时提示'), el('button', { type: 'button', className: 'flip', role: 'switch', 'aria-checked': String(on === true), 'aria-label': '自动检查更新',
          onclick: () => { native.setAutoUpdate(!on); toast('已保存'); fixed(); } })));
      }
      rows.push(row({ act: () => native.update() }, text('App 版本', native.version ? 'v' + native.version() : ''), el('span', { className: 'act', textContent: '检查更新' })));
      $('app').replaceChildren(...rows);
    }
    $('account').replaceChildren(row({ kind: 'danger', act: async () => {
      if (!await confirm('退出登录？', '下次需要重新扫码或输入密码。', '退出登录', true)) return;
      try { await post('api/logout', {}); } catch (error) { /* signed out anyway */ }
      if (native && native.disconnect) native.disconnect(); else location.replace('../');
    } }, text('退出登录')));
  }

  // ---- another line
  // The relays of one kind, to choose one from: how fast each answered the computer, and which is in use.
  function choose(kind, lines) {
    const form = $('sheet-form'), sheet = $('sheet');
    let picked = null;
    form.replaceChildren(el('h3', { textContent: KIND[kind][0] }), el('p', { textContent: KIND[kind][1] }),
      ...lines.map(line => el('button', { type: 'button', className: 'choice', onclick: () => { picked = line; sheet.close(); } },
        el('span', null, name(line), took(line) ? el('small', { textContent: took(line) }) : ''), line.current ? el('i', { className: 'tick', textContent: '使用中' }) : '')),
      el('button', { type: 'button', className: 'cancel', textContent: '取消', onclick: () => sheet.close() }));
    // the next sheet, which asks whether to change, is opened when this one has closed
    sheet.onclose = () => { sheet.onclose = null; if (picked && !picked.current) turn(picked); };
    sheet.onclick = event => { if (event.target === sheet) sheet.close(); };
    sheet.showModal();
  }
  async function turn(line) {
    if (busy) return;
    // An app that cannot follow to another address would be left behind at this one.
    if (native && !native.moved) return toast('请先把 App 更新到 1.2.0 或更高版本，再从手机切换线路');
    const more = line.kind === 'lan' ? '局域网只在手机和电脑连同一个 Wi-Fi 时能连上。' : line.kind === 'public' ? '终端内容会经过这个公共中转的服务器。'
      : line.kind === 'cloud' && !line.ready ? '电脑上还没有隧道程序，会先下载（约 60 MB），需要等几分钟。' : '';
    if (!await confirm('切换到“' + name(line) + '”？', '新线路连不上时，电脑会在 2 分钟内自动回到现在的线路。' + more, '切换')) return;
    busy = true; draw();
    try {
      told = await ask('line_switch', { to: line.id });
      // The computer makes the line ready; the page asks how far it is, takes it when it is ready, and goes there.
      for (;;) {
        const now = told.change;
        if (now.state === 'ready') {
          const handed = await ask('line_take');
          state('busy', '正在转到“' + name(line) + '”', '');
          if (native) native.moved(handed.address, handed.ticket, handed.key);
          else location.href = handed.address + '/computer/#t=' + encodeURIComponent(handed.ticket) + '&keep=' + handed.key;
          return;
        }
        if (now.state !== 'preparing') { state('', now.state === 'failed' ? '没有切换到“' + name(line) + '”' : '', now.note); break; }
        state('busy', '正在准备“' + name(line) + '”', now.note);
        await wait(1200);
        told = await read(15);
      }
    } catch (error) { state('', '没有切换到“' + name(line) + '”', error.message); }
    busy = false; draw();
  }
  // This page was opened at the new line with what the old one was handed: it signs in, and tells the computer that
  // the phone has arrived, with the word the computer gave for it.
  async function arrive(ticket, key) {
    history.replaceState(null, '', location.pathname);
    state('busy', '正在新线路上确认', '');
    try { await post('api/login', { ticket }); }
    catch (error) { return state('', '没有登录到新线路', '这次切换的登录已失效。电脑会自动回到原来的线路，之后请从工作台重新打开这台电脑。'); }
    told = await read(60);
    if (told.change.state !== 'trial' && told.change.state !== 'moving') return state('', '这次切换已经结束', told.change.note || '');
    told = await ask('line_keep', { key });
    const current = told.lines.find(line => line.current);
    state('good', '已切换到“' + (current ? name(current) : '新线路') + '”', '之后都从这条线路连接这台电脑。');
    if (native && native.kept) native.kept();
  }
  // What the relay knows of the computer, and what the program on it tells when it can be asked.
  async function load() {
    try {
      const reply = await fetch('../api/terminal', { credentials: 'same-origin' });
      if (reply.status === 401) return location.replace('../');
      device = (await reply.json()).device;
      problem = '';
      if (device.online && device.enabled && has('settings')) {
        told = await read(told ? 3 : 1);
        if (!$('state-title').textContent) {
          if (told.change.state === 'failed') state('', '上一次切换线路没有完成', told.change.note);
          else if (told.change.state) state('plain', '电脑正在切换线路', told.change.note);
        }
      } else told = null;
    } catch (error) { problem = error.message; if (!device) told = null; }
    draw();
  }

  looks(); fixed();
  (async () => {
    const handed = new URLSearchParams(location.hash.slice(1));
    if (handed.get('t') && handed.get('keep')) {
      try { await arrive(handed.get('t'), handed.get('keep')); }
      catch (error) { state('', '没有在新线路上确认成功', error.message + '。电脑会自动回到原来的线路。'); }
    }
    await load();
  })();
  document.addEventListener('visibilitychange', () => { if (!document.hidden && !busy) load(); });
})();
