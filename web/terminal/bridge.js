/* Connects the terminal page to the relay from a browser: reading output, sending input, and the page's menu.
   Inside the Android app, window.RemoteCliNative adds system speech recognition and status-bar colour. The page of a
   conversation run through its tool's structured mode (chat/) is connected by it too: it sets window.TerminalUI to
   itself, and says so by window.ChatPage; a terminal that turns out to be such a conversation is shown there. */
(() => {
  'use strict';
  const id = new URLSearchParams(location.search).get('id') || '';
  const native = window.RemoteCliNative || null;
  // Inside the app the look is the app's own, the same for every computer; a browser keeps it per address.
  // The terminal may have a skin of its own there ("termskin"); without one it follows the app's.
  const LOOK = { skin: 'termskin', font: 'font', spacing: 'spacing', renderer: 'renderer' };
  const store = {
    get(key, fallback) {
      if (native && native.pref && LOOK[key]) { const chosen = native.pref(LOOK[key]) || (key === 'skin' ? native.pref('skin') : ''); if (chosen) return chosen; }
      try { return localStorage.getItem('rcli-' + key) || fallback; } catch (error) { return fallback; }
    },
    set(key, value) {
      if (native && native.setPref && LOOK[key]) native.setPref(LOOK[key], value);
      try { localStorage.setItem('rcli-' + key, value); } catch (error) { /* private window */ }
    } };
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  const ui = () => window.TerminalUI;
  const home = () => { location.replace('../'); };      // the list is one step back, not one more page
  // Opened from the app's workbench, the back arrow leads there again rather than to this computer's list.
  const fromBench = new URLSearchParams(location.search).get('from') === 'bench';
  const leave = () => { if (fromBench && native && native.home) native.home(); else home(); };
  // Quick tunnels buffer SSE; a held JSON response is delivered without that delay.
  let after = 0, terminal = null, loaded = false, streamed = !location.hostname.endsWith('.trycloudflare.com'), failures = 0, reader = null, run = 0, fetching = false, gone = false;
  let socketMode = typeof WebSocket === 'function', socket = null, socketTimer = null;
  const link = { transport: socketMode ? 'websocket' : streamed ? 'sse' : 'http', state: 'connecting', reconnects: 0, lastLatency: null, everConnected: false };
  let lastLinkReport = '', latencyTimer = null, latencyReader = null, latencyRun = 0;

  function reportLink() {
    const target = ui();
    if (!target || typeof target.connection !== 'function') return;
    const info = { transport: link.transport, state: link.state, reconnects: link.reconnects, lastLatency: link.lastLatency };
    const key = JSON.stringify(info);
    if (key === lastLinkReport) return;
    lastLinkReport = key;
    target.connection(info);
  }
  function startLink(transport) {
    link.transport = transport;
    if (link.state !== 'connected' && link.state !== 'reconnecting') link.state = 'connecting';
    reportLink();
  }
  function connectedLink(transport) {
    link.transport = transport;
    link.state = 'connected';
    link.everConnected = true;
    reportLink();
    if (!latencyReader && latencyTimer === null) measureLatency();
  }
  function lostLink() {
    if (link.state === 'connected') link.reconnects++;
    link.state = link.everConnected ? 'reconnecting' : 'connecting';
    pauseLatency();
    reportLink();
  }
  function offlineLink() {
    link.state = 'offline';
    pauseLatency();
    reportLink();
  }
  function pauseLatency() {
    latencyRun++;
    clearTimeout(latencyTimer); latencyTimer = null;
    if (latencyReader) { latencyReader.abort(); latencyReader = null; }
  }
  // This endpoint responds immediately. Output long-polls deliberately wait and cannot measure a round trip.
  async function measureLatency() {
    if (gone || document.hidden || link.state !== 'connected') return;
    const mine = latencyRun, startedAt = Date.now(), control = new AbortController();
    latencyReader = control;
    const timer = setTimeout(() => control.abort(), 5000);
    try {
      const reply = await fetch('../api/session', { credentials: 'same-origin', cache: 'no-store', signal: control.signal });
      const result = await reply.json();
      if (mine !== latencyRun) return;
      if (reply.ok && result.signed_in) { link.lastLatency = Math.max(0, Date.now() - startedAt); reportLink(); }
      else if (reply.ok && result.signed_in === false) { offlineLink(); home(); }
    } catch (error) { /* the output connection, not a measurement, determines whether the terminal is connected */ }
    finally {
      clearTimeout(timer);
      if (mine === latencyRun) {
        latencyReader = null;
        latencyTimer = setTimeout(() => { latencyTimer = null; measureLatency(); }, 20000);
      }
    }
  }

  async function post(payload) {
    const control = new AbortController(), timer = setTimeout(() => control.abort(), 20000);
    try {
      const reply = await fetch('../api/terminal', { method: 'POST', credentials: 'same-origin', signal: control.signal, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
      let body = {};
      try { body = await reply.json(); } catch (error) { /* not JSON */ }
      if (reply.status === 401) { home(); throw new Error('请重新输入访问密码'); }
      return { status: reply.status, body };
    } finally { clearTimeout(timer); }
  }

  // ---- output: a WebSocket, then a kept-open response or held requests when needed
  function receive(item, pushed = false) {
    if (item.terminal && item.terminal.form === 'chat' && !window.ChatPage) { gone = true; hangUp(); location.replace('../chat/' + location.search); return; }
    terminal = item.terminal;
    if (streamed || pushed) after = item.after;
    ui().receive(item);
  }
  function resetPending() {
    for (const op of queue) { clearTimeout(op.timer); op.inFlight = false; }
  }
  function connectSocket() {
    if (gone || !loaded || document.hidden || socket) return;
    const mine = ++run;
    startLink('websocket');
    const startedAt = Date.now();
    let channel;
    try { channel = new WebSocket(new URL('../api/terminal/ws?terminal=' + id + '&after=' + after, location.href).href.replace(/^http/, 'ws')); }
    catch (error) { socketMode = false; return read(); }
    socket = channel;
    let got = false;
    function failed(event) {
      if (mine !== run) return;
      clearTimeout(socketTimer);
      // The relay renews an established socket after an hour with a normal "going away" close.
      if (!(got && event && event.code === 1001 && Date.now() - startedAt >= 3500000)) lostLink();
      run++; socket = null; resetPending();
      if (event && event.code === 1008) return home();
      // An older relay or a proxy without WebSocket support uses the HTTP interfaces.
      if (!got) socketMode = false;
      if (!document.hidden) setTimeout(read, got ? 300 : 0);
      flush();
    }
    channel.onclose = failed;
    socketTimer = setTimeout(() => { if (!got && mine === run) { failed(); channel.close(); } }, 4000);
    channel.onopen = () => { if (mine === run) flush(); };
    channel.onmessage = event => {
      if (mine !== run) return;
      let item;
      try { item = JSON.parse(event.data); } catch (error) { channel.close(); return; }
      if (item.t === 'out') { got = true; clearTimeout(socketTimer); connectedLink('websocket'); receive(item, true); }
      else if (item.t === 'ack') {
        const at = queue.findIndex(op => op.id === item.id);
        if (at < 0) return;
        const op = queue[at];
        clearTimeout(op.timer);
        if (item.status >= 500) { socketMode = false; channel.close(); return; }
        queue.splice(at, 1);
        if (item.status !== 200 || item.state === 'error') ui().inputError(item.error || '操作没有执行', op.data || '', op);
        flush();
      }
    };
  }
  function problem() { ui().error('读取失败，正在重连；电脑上的终端会继续运行'); }
  async function stream() {
    if (gone || !loaded || reader || document.hidden) return;
    const mine = ++run;
    startLink('sse');
    const startedAt = Date.now();
    let got = 0, status = 0, completed = false;
    const control = new AbortController();
    reader = control;
    let buffered = false;
    const firstLineTimer = setTimeout(() => { if (!got) { buffered = true; control.abort(); } }, 4000);
    try {
      const reply = await fetch('../api/terminal/stream?terminal=' + id + '&after=' + after, { credentials: 'same-origin', signal: control.signal, headers: { Accept: 'text/event-stream' } });
      status = reply.status;
      if (status === 200 && reply.body) {
        const body = reply.body.getReader(), text = new TextDecoder();
        let rest = '';
        for (;;) {
          const { value, done } = await body.read();
          if (done || mine !== run) { completed = done; break; }
          rest += text.decode(value, { stream: true });
          let at;
          while ((at = rest.indexOf('\n')) >= 0) {
            const line = rest.slice(0, at); rest = rest.slice(at + 1);
            if (line.startsWith('data:')) { got++; clearTimeout(firstLineTimer); if (got === 1) connectedLink('sse'); receive(JSON.parse(line.slice(5))); }
          }
        }
      } else if (status === 400) { let body = {}; try { body = await reply.json(); } catch (error) { /* not JSON */ } ui().error(body.error || '终端已不存在'); gone = true; }
    } catch (error) { /* reconnect below */ }
    clearTimeout(firstLineTimer);
    if (mine !== run) return;
    reader = null;
    if (gone) { offlineLink(); return; }
    if (status === 401) return home();
    failures = got ? 0 : buffered ? 2 : failures + 1;
    if (!(got && completed && Date.now() - startedAt >= 50000)) lostLink();
    if (failures >= 2) { streamed = false; return held(); }
    if (!got) problem();
    if (!document.hidden) setTimeout(read, got ? 30 : 1500);
  }
  async function held() {
    if (gone || !loaded || fetching || document.hidden) return;
    fetching = true;
    startLink('http');
    const mine = ++run, control = new AbortController();
    reader = control;
    const timer = setTimeout(() => control.abort(), 30000);
    try {
      const reply = await fetch('../api/terminal?terminal=' + id + '&after=' + after + '&wait=20', { credentials: 'same-origin', signal: control.signal });
      if (mine !== run) return;
      if (reply.status === 401) return home();
      const item = await reply.json();
      if (mine !== run) return;
      if (!reply.ok) throw new Error(item.error || '');
      fetching = false; reader = null;
      connectedLink('http');
      receive(item);
    } catch (error) { if (mine === run) { fetching = false; reader = null; lostLink(); problem(); setTimeout(read, 1500); } }
    finally { clearTimeout(timer); }
  }
  function read() { if (socketMode) connectSocket(); else if (streamed) stream(); else held(); }
  function hangUp() {
    run++; fetching = false; clearTimeout(socketTimer); pauseLatency();
    if (socket) { socket.close(); socket = null; resetPending(); }
    if (reader) { reader.abort(); reader = null; }
    if (document.hidden) { link.state = 'paused'; reportLink(); }
  }
  document.addEventListener('visibilitychange', () => { if (document.hidden) hangUp(); else read(); });

  // ---- input: ordered WebSocket messages; HTTP fallback sends one request at a time
  const queue = [];
  let sending = false;
  function enqueue(op) {
    Object.assign(op, { id: op.id || newId(), terminal: id });
    op.at = Date.now();
    const last = queue[queue.length - 1];
    if (last && !last.sent && op.action === 'input' && last.action === 'input' && last.data.length + op.data.length <= 8000) last.data += op.data;
    else queue.push(op);
    flush();
  }
  async function flush() {
    if (sending || !queue.length) return;
    if (socketMode) {
      if (!socket || socket.readyState !== WebSocket.OPEN) return;
      for (const op of [...queue]) {
        if (op.inFlight) continue;
        if (Date.now() - op.at > 110000) { queue.splice(queue.indexOf(op), 1); ui().inputError('发送超时，请确认终端画面后重试', op.data || '', op); continue; }
        op.sent = true; op.inFlight = true;
        const { at, sent, inFlight, timer, ...payload } = op;
        const channel = socket;
        try { channel.send(JSON.stringify(payload)); }
        catch (error) { channel.close(); return; }
        op.timer = setTimeout(() => { if (socket === channel) channel.close(); }, 20000);
      }
      return;
    }
    const op = queue[0];
    if (Date.now() - op.at > 110000) { queue.shift(); ui().inputError('发送超时，请确认终端画面后重试', op.data || '', op); return flush(); }
    sending = true; op.sent = true;
    let message = '', again = false;
    try {
      const { at, sent, inFlight, timer, ...payload } = op;
      const { status, body } = await post(payload);
      if (status !== 200 || body.state === 'error') { message = body.error || '操作没有执行'; again = status >= 500; }
    } catch (error) { again = true; message = '发送失败，正在重连'; }
    sending = false;
    if (!again) queue.shift();
    if (message) ui().inputError(message, again ? '' : op.data || '', op);
    if (again) setTimeout(flush, 1000); else flush();
  }

  // ---- menu
  // fill adds what belongs between the title and the choices: a line of text, or a box to type in.
  function sheet(title, choices, fill) {
    let dialog = document.getElementById('rcli-menu');
    if (!dialog) { dialog = document.createElement('dialog'); dialog.id = 'rcli-menu'; document.body.append(dialog); dialog.onclick = event => { if (event.target === dialog) dialog.close(); }; }
    dialog.textContent = '';
    const heading = document.createElement('h2'); heading.textContent = title; dialog.append(heading);
    if (fill) fill(dialog);
    choices.forEach(([label, act, current, kind]) => {
      const button = document.createElement('button');
      button.type = 'button'; button.textContent = label;
      if (kind) button.className = kind;
      if (current) button.setAttribute('aria-current', 'true');
      button.onclick = () => { dialog.close(); act(); };
      dialog.append(button);
    });
    const cancel = document.createElement('button');
    cancel.type = 'button'; cancel.className = 'cancel'; cancel.textContent = '取消'; cancel.onclick = () => dialog.close();
    dialog.append(cancel);
    dialog.showModal();
  }
  async function again() {
    if (!terminal || terminal.state !== 'closed' || terminal.tool === 'shell') return;
    const start = { action: 'start', id: newId(), tool: terminal.tool, dir: terminal.dir };
    if (terminal.session) start.session = terminal.session;
    start.history = false;
    if (terminal.form === 'chat') start.form = 'chat';
    try {
      const { status, body } = await post(start);
      if (status !== 200) throw new Error(body.error || '没有打开');
      location.replace('?id=' + body.terminal);
    } catch (error) { ui().error(error.message); }
  }
  // The files of the project this terminal works in. The terminal goes on running on the computer; the back arrow
  // of the file pages, or the phone's back key, leads here again.
  function files() { if (terminal) location.href = '../files/?dir=' + encodeURIComponent(terminal.dir) + '&from=terminal'; }
  function menu() {
    if (!terminal) return;
    const closed = terminal.state === 'closed', dom = store.get('renderer', 'webgl') === 'dom', choices = [];
    if (closed && terminal.tool !== 'shell') choices.push([terminal.session ? '继续这个对话' : '重新新建对话', again]);
    choices.push(['更换外观', () => {
      const follows = !!(native && native.pref && native.setPref) && !native.pref('termskin');
      const looks = ui().skins().map(s => [s.name, () => { store.set('skin', s.key); ui().setSkin(s.key); }, s.current && !follows]);
      if (native && native.pref && native.setPref) looks.unshift(['跟随 App 外观', () => { native.setPref('termskin', 'app'); ui().setSkin(store.get('skin', 'paper')); }, follows]);
      sheet('终端外观', looks);
    }]);
    choices.push(['行距', () => sheet('行距', ui().spacings().map(s => [s.name, () => { store.set('spacing', s.key); ui().setSpacing(s.key); }, s.current]))]);
    // Questions are asked on the same sheet: the system's own dialogs look foreign, and not every app shows them.
    choices.push(['重命名', () => {
      const box = document.createElement('input');
      box.value = terminal.title; box.maxLength = 60; box.spellcheck = false; box.setAttribute('aria-label', '终端名称');
      sheet('终端名称', [['保存', async () => {
        const title = box.value.trim();
        if (!title) return;
        try { const { status, body } = await post({ action: 'rename', id: newId(), terminal: id, title }); if (status !== 200) ui().error(body.error || '没有保存'); }
        catch (error) { ui().error('没有保存，请重试'); }
      }, false, 'solid']], dialog => dialog.append(box));
      box.focus(); box.select();
    }]);
    choices.push(['查看项目文件', files]);
    choices.push(['复制终端画面', async () => { try { await navigator.clipboard.writeText(ui().screenText()); } catch (error) { ui().error('浏览器不允许复制，请长按选择文字'); } }]);
    choices.push([dom ? '画面卡顿时：改用显卡绘制' : '画面空白或花屏时：改用兼容绘制', () => { store.set('renderer', dom ? 'webgl' : 'dom'); location.reload(); }]);
    if (!closed) choices.push(['结束终端', () => sheet('结束这个终端？', [['结束终端', () => enqueue({ action: 'close' }), false, 'danger']], dialog => {
      const text = document.createElement('p'); text.textContent = '正在运行的任务会被中断。对话保存在电脑上，可以从历史对话继续。'; dialog.append(text);
    }), false, 'danger']);
    sheet('终端选项', choices);
  }

  // ---- speech: the app's system recognizer, else the browser's where it has one
  window.RemoteCliDictated = text => ui().dictated(String(text || ''));
  function voice() {
    if (native && native.voice) return native.voice();
    const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!Recognition) return ui().error('这个浏览器没有语音识别，请用输入法的语音键');
    const listener = new Recognition();
    listener.lang = navigator.language || 'zh-CN';
    listener.onresult = event => ui().dictated(event.results[0][0].transcript);
    listener.onerror = () => ui().error('没有听清，请再试一次或用输入法的语音键');
    listener.start();
  }

  window.ProjectTerminal = {
    ready() { loaded = true; reportLink(); if (!/^[a-f0-9]{16,32}$/.test(id)) return home(); read(); },
    rendered(cursor) { if (socketMode || streamed) return; after = Number(cursor) || after; setTimeout(read, 0); },
    close: leave,
    voice,
    skin: () => store.get('skin', 'paper'),
    renderer: () => store.get('renderer', 'webgl'),
    spacing: () => store.get('spacing', 'cozy'),
    chrome(color) {
      if (!/^#[0-9a-fA-F]{6}$/.test(color)) return;
      let meta = document.querySelector('meta[name="theme-color"]');
      if (!meta) { meta = document.createElement('meta'); meta.name = 'theme-color'; document.head.append(meta); }
      meta.content = color;
      if (native && native.chrome) native.chrome(color);
    },
    input(data) { if (data && data.length <= 16000) enqueue({ action: 'input', data }); },
    // any other operation on this terminal (a conversation's say, stop, answer, close); its id is kept where it has one
    send(op) { enqueue(Object.assign({}, op)); },
    post,
    resize(size) { try { const s = JSON.parse(size); enqueue({ action: 'resize', cols: s.cols, rows: s.rows }); } catch (error) { /* ignore */ } },
    menu,
    files,
    again,
    fontSize: () => Number(store.get('font', '13')),
    saveFontSize(size) { if (size >= 9 && size <= 20) store.set('font', String(size)); }
  };
})();
