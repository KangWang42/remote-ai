/* 多选: several conversations, terminals and collaboration tasks of the lists handled at once. Begun from a card's held
   press (多选); while it lasts a press on a card chooses it, and the bar at the foot does the same to all chosen:
   继续 (each is told 继续; a paused task goes on; a conversation that has ended is gone on with as a phone
   conversation) or 结束 (each is ended; a task is stopped). What cannot be done to one is left, and said.
   The cards are app.js's; this only marks them (`paint`, called when a list is filled) and takes the presses. */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { el, api, newId, toast } = page;
  const chosen = new Set();
  let active = false, bar = null, busy = false;
  const keyOf = x => x.team ? 'team:' + x.team.id : (x.s ? 'session:' + x.s.tool + ':' + x.s.id : 'terminal:' + x.t.id);
  const current = () => page.things().filter(x => chosen.has(keyOf(x)));
  const quiet = phase => ['idle', 'waiting', 'done', 'stopped', 'failed', ''].includes(phase);

  // an operation on terminals, asked again until the computer has done it (as app.js's)
  async function operate(payload) {
    const op = Object.assign({ id: newId() }, payload);
    let result = await api('api/terminal', op);
    for (let n = 0; n < 62 && result.state === 'queued'; n++) {
      await new Promise(done => setTimeout(done, n < 3 ? 150 : 400));
      result = await api('api/terminal', op);
    }
    if (result.error) throw new Error(result.error);
    if (result.state === 'queued') throw new Error('电脑没有响应');
    return result;
  }
  const work = (action, more) => api('api/work', Object.assign({ id: newId(), action }, more));

  // 继续: what it takes for each to go on; null where it cannot
  function goOn(x) {
    if (x.team) return x.team.state === 'paused' ? () => work('team_resume', { team: x.team.id }) : null;
    if (x.t && x.live) return x.phase !== 'confirm' && quiet(x.phase) ? () => operate({ action: 'say', terminal: x.t.id, text: '继续' }) : null;
    const s = x.s || null, t = x.t || null;
    if (s && s.live) return quiet(x.phase) && Array.isArray(s.reach) && s.reach.includes('say') && !s.ask
      ? () => api('api/conversation', { id: newId(), session: s.id, action: 'say', text: '继续' }) : null;
    // ended: gone on with as a conversation on the phone, told 继续
    const tool = s ? s.tool : t.tool, session = s ? s.id : t.session, dir = s ? s.dir : t.dir;
    if (!session || !dir || !page.chatOk(tool)) return null;
    return () => operate({ action: 'start', tool, dir, session, history: false, form: 'chat', text: '继续' });
  }
  // 结束: what it takes for each to end; null where there is nothing to end
  function end(x) {
    if (x.team) return ['running', 'paused'].includes(x.team.state) ? () => work('team_stop', { team: x.team.id }) : null;
    if (x.t) return x.live ? () => operate({ action: 'close', terminal: x.t.id }) : null;
    return x.s && x.s.live && page.canEnd(x.s) ? () => api('api/conversation', { id: newId(), session: x.s.id, action: 'end' }) : null;
  }

  async function act(how, done, left) {
    if (busy) return;
    const all = current(), doable = all.map(x => [x, how(x)]).filter(([, does]) => does);
    if (!doable.length) return toast(all.length ? '选中的' + left : '先选择对话');
    if (how === end && !await page.ask('结束选中的 ' + doable.length + ' 个？', '正在执行的会被中断；对话记录保留，之后可以继续。', [{ label: '结束', value: true, kind: 'danger' }])) return;
    busy = true; draw();
    const failed = [];
    // one at a time: the computer takes its operations in order, and a terminal slot is freed before the next begins
    for (const [x, does] of doable) {
      try { await does(); } catch (error) { failed.push(x.title + '：' + error.message); }
    }
    busy = false;
    const skipped = all.length - doable.length;
    toast(done + ' ' + (doable.length - failed.length) + ' 个' + (skipped ? '，' + skipped + ' 个' + left : '') + (failed.length ? '，' + failed.length + ' 个没有完成：' + failed[0] : ''));
    stop();
    page.refresh();
  }

  function draw() {
    if (!active) { if (bar) bar.hidden = true; return; }
    if (!bar) {
      bar = el('div', { id: 'select-bar', role: 'toolbar', ariaLabel: '多选' });
      document.body.append(bar);
    }
    bar.hidden = false;
    const count = chosen.size;
    bar.replaceChildren(
      el('button', { type: 'button', className: 'quit', textContent: '取消', onclick: stop }),
      el('b', { textContent: count ? '已选 ' + count + ' 个' : '选择对话' }),
      el('button', { type: 'button', id: 'select-go', textContent: '继续', disabled: !count || busy, onclick: () => act(goOn, '已继续', '不能继续') }),
      el('button', { type: 'button', id: 'select-end', className: 'danger', textContent: '结束', disabled: !count || busy, onclick: () => act(end, '已结束', '没有可结束的') }));
    document.body.classList.toggle('selecting', true);
  }
  // the cards of a list, marked as chosen or not
  function paint(root = document) {
    if (!active) return;
    root.querySelectorAll('[data-list-key]').forEach(card => {
      card.classList.add('choosable');
      card.classList.toggle('chosen', chosen.has(card.dataset.listKey));
      card.setAttribute('aria-selected', String(chosen.has(card.dataset.listKey)));
    });
  }
  function start(x) {
    active = true; chosen.clear();
    if (x) chosen.add(keyOf(x));
    draw(); paint();
  }
  function stop() {
    active = false; chosen.clear();
    document.body.classList.remove('selecting');
    document.querySelectorAll('[data-list-key].choosable').forEach(card => { card.classList.remove('choosable', 'chosen'); card.removeAttribute('aria-selected'); });
    draw();
  }
  // while choosing, a press on a card (anywhere on it, its side buttons too) chooses it and does nothing else
  document.addEventListener('click', event => {
    if (!active) return;
    const card = event.target.closest && event.target.closest('[data-list-key]');
    if (!card || event.target.closest('#select-bar')) return;
    event.preventDefault(); event.stopPropagation();
    const key = card.dataset.listKey;
    if (chosen.has(key)) chosen.delete(key); else chosen.add(key);
    paint(); draw();
  }, true);
  // the phone's back key ends choosing before it leaves the page
  window.addEventListener('keydown', event => { if (active && event.key === 'Escape') stop(); });

  window.RemoteSelect = { start, stop, paint, get active() { return active; } };
})();
