/* Keeps an eye on the other terminals while one is open: tap the title to switch, and a line appears when
   another task finishes or asks for a decision. Reads the same overview as the list page; sends nothing. */
(() => {
  'use strict';
  const id = new URLSearchParams(location.search).get('id') || '';
  const TOOLS = { claude: 'Claude Code', codex: 'Codex', agy: 'Antigravity CLI', opencode: 'OpenCode', qoder: 'Qoder', cline: 'Cline', pi: 'Pi', shell: '终端' };
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

  const agyShell = t => {
    if (!t || t.tool !== 'shell') return false;
    if (/^Antigravity CLI(?:\s|$|·)/.test(t.title || '')) return true;
    try { const list = JSON.parse(sessionStorage.getItem('agy-shell-ids') || '[]'); return Array.isArray(list) && list.includes(t.id); } catch (error) { return false; }
  };
  const displayTool = t => agyShell(t) ? 'agy' : t.tool;
  // ---- the sheet for switching (../ui/sheet.js)
  function switcher() {
    if (!window.RemoteSheet) return;
    const kit = window.RemoteSheet.ui;
    window.RemoteSheet.open({ title: '切换终端', build(p) {
      const list = others();
      if (!list.length) p.body.append(kit.empty('没有别的终端在运行'));
      else p.body.append(kit.group(...list.map(t => {
        const shown = displayTool(t);
        const icon = shown === 'claude' || shown === 'codex' || shown === 'agy' ? shown : t.form === 'chat' ? 'chat' : 'term';
        const tint = shown === 'claude' ? 'claude' : shown === 'agy' ? 'agy' : shown === 'shell' ? 'good' : 'codex';
        return kit.row({ icon, tint,
          label: t.title, detail: (t.dir ? t.dir + ' · ' : '') + (TOOLS[shown] || shown), end: Object.assign(document.createElement('span'), { className: 'pill ' + t.shows, textContent: PHASE[t.shows][0] }),
          act: () => { p.sheet.close(null, true); go(t.id); } });
      })));
      p.foot.append(kit.go('全部项目', async () => { p.sheet.close(null, true); location.replace('../'); }, { kind: 'quiet', id: 'switch-all' }).button);
    } }, '切换终端');
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
