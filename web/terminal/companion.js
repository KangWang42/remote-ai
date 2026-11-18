/* Keeps an eye on the other terminals while one is open: tap the title to switch, and a line appears when
   another task finishes or asks for a decision. Reads the same overview as the list page; sends nothing. */
(() => {
  'use strict';
  const id = new URLSearchParams(location.search).get('id') || '';
  const TOOLS = { claude: 'Claude Code', codex: 'Codex', agy: 'Antigravity CLI', shell: 'PowerShell' };
  const PHASE = { confirm: ['等你确认', 0], done: ['已完成', 1], busy: ['正在执行', 2], starting: ['正在启动', 3], idle: ['等待输入', 4] };
  const load = () => { try { return JSON.parse(localStorage.getItem('rcli-seen') || '{}') || {}; } catch (error) { return {}; } };
  const save = map => { try { localStorage.setItem('rcli-seen', JSON.stringify(map)); } catch (error) { /* private window */ } };
  const native = window.RemoteCliNative || null;
  let data = null, timer = 0, dismissed = '';

  function phaseOf(t, seen) {
    const phase = t.phase || (t.state === 'starting' ? 'starting' : t.status === 'busy' ? 'busy' : 'idle');
    return phase === 'idle' && t.done && seen[t.id] !== t.phase_at ? 'done' : phase;
  }
  function others() {
    const seen = load();
    return (data && data.terminals || []).filter(t => t.id !== id && (t.state === 'running' || t.state === 'starting'))
      .map(t => Object.assign({ shows: phaseOf(t, seen) }, t)).sort((a, b) => PHASE[a.shows][1] - PHASE[b.shows][1] || (b.phase_at || 0) - (a.phase_at || 0));
  }
  function go(terminal) { location.replace('?id=' + encodeURIComponent(terminal)); }

  // ---- the line under the header
  const bar = document.createElement('button');
  bar.type = 'button'; bar.id = 'attention'; bar.hidden = true;
  function drawBar() {
    const urgent = others().filter(t => t.shows === 'confirm' || t.shows === 'done')[0];
    const key = urgent ? urgent.id + urgent.phase_at : '';
    if (!urgent || key === dismissed) { bar.hidden = true; return; }
    bar.className = urgent.shows;
    bar.replaceChildren(Object.assign(document.createElement('span'), { className: 'pill ' + urgent.shows, textContent: PHASE[urgent.shows][0] }),
      Object.assign(document.createElement('b'), { textContent: urgent.dir + ' · ' + urgent.title }), Object.assign(document.createElement('i'), { textContent: '切换' }));
    bar.onclick = () => go(urgent.id);
    bar.hidden = false;
  }

  // ---- the sheet for switching
  function switcher() {
    let dialog = document.getElementById('rcli-switch');
    if (!dialog) { dialog = document.createElement('dialog'); dialog.id = 'rcli-switch'; dialog.className = 'sheet'; document.body.append(dialog); dialog.onclick = event => { if (event.target === dialog) dialog.close(); }; }
    dialog.textContent = '';
    dialog.append(Object.assign(document.createElement('h2'), { textContent: '切换终端' }));
    const list = others();
    if (!list.length) dialog.append(Object.assign(document.createElement('p'), { textContent: '没有别的终端在运行。回到列表可以新建或继续一段对话。' }));
    list.forEach(t => {
      const row = document.createElement('button');
      row.type = 'button'; row.className = 'row';
      const text = document.createElement('span');
      text.append(Object.assign(document.createElement('b'), { textContent: t.title }), Object.assign(document.createElement('small'), { textContent: t.dir + ' · ' + TOOLS[t.tool] }));
      row.append(text, Object.assign(document.createElement('span'), { className: 'pill ' + t.shows, textContent: PHASE[t.shows][0] }));
      row.onclick = () => go(t.id);
      dialog.append(row);
    });
    const all = Object.assign(document.createElement('button'), { type: 'button', className: 'cancel', textContent: '全部项目' });
    all.onclick = () => location.replace('../');
    dialog.append(all);
    dialog.showModal();
  }

  async function poll() {
    clearTimeout(timer);
    if (!document.hidden) {
      try {
        const reply = await fetch('../api/terminal', { credentials: 'same-origin' });
        if (reply.ok) {
          data = await reply.json();
          // What is on screen now counts as seen: this terminal is no longer "done" in the lists.
          const mine = (data.terminals || []).find(t => t.id === id), seen = load();
          if (mine && mine.phase_at && seen[id] !== mine.phase_at) {
            seen[id] = mine.phase_at;
            if (native && native.seen) native.seen(id, String(mine.phase_at));      // the app's workbench keeps the same record
            const alive = new Set((data.terminals || []).map(t => t.id));
            Object.keys(seen).forEach(key => { if (!alive.has(key)) delete seen[key]; });
            save(seen);
          }
          drawBar();
          const count = others().length, mark = document.getElementById('others');
          if (mark) { mark.textContent = count ? String(count) : ''; mark.hidden = !count; }
        }
      } catch (error) { /* the terminal itself reports connection trouble */ }
    }
    timer = setTimeout(poll, 4000);
  }

  document.addEventListener('DOMContentLoaded', () => {
    const who = document.querySelector('header .who'), screen = document.getElementById('screen');
    if (!who || !screen) return;
    who.setAttribute('role', 'button'); who.setAttribute('tabindex', '0'); who.setAttribute('aria-label', '切换终端');
    const mark = Object.assign(document.createElement('span'), { id: 'others', hidden: true });
    who.querySelector('h1').after(mark);
    who.addEventListener('click', switcher);
    who.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); switcher(); } });
    screen.before(bar);
    bar.addEventListener('contextmenu', event => { event.preventDefault(); const urgent = others()[0]; dismissed = urgent ? urgent.id + urgent.phase_at : ''; bar.hidden = true; });
    poll();
  });
  document.addEventListener('visibilitychange', () => { if (!document.hidden) poll(); });
})();
