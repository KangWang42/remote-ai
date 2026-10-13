/* 协作: conversations working together. One conversation is handed over to another with what was asked and done in
   it; one question is put to several at once; when a conversation stops, what it said last goes on to another (接力);
   and a question is asked of a conversation at a set time. The computer keeps the rules and does the passing on
   (docs/PROTOCOL.md, `/api/work`); this draws them and asks for them to be set. */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { $, el, svg, ICON, TOOLS, api, newId, toast, openSheet } = page;
  const box = $('together'), APP = 'codex-desktop', MOST_ROUNDS = [1, 2, 3, 5, 10];
  const EVERY = [[15, '15 分钟'], [30, '30 分钟'], [60, '1 小时'], [120, '2 小时'], [240, '4 小时'], [480, '8 小时'], [720, '12 小时']];
  const CLOCK = svg('<circle cx="12" cy="12" r="8.6"/><path d="M12 7.300V12l3.1 2"/>'), PAUSE = svg('<path d="M9 7v10M15 7v10"/>', 18), PLAY = svg('<path d="M9 6.500l8.5 5.500L9 17.500z"/>', 18);
  const HAND = svg('<path d="M4 8.500h11.500M12 5l3.500 3.500L12 12M20 15.500H8.500M12 12l-3.500 3.500L12 19"/>'), MANY = svg('<path d="M4.500 6.500h15M4.500 12h15M4.500 17.500h9"/><circle cx="18" cy="17.500" r="1.6"/>');
  let chains = null, schedules = null, asked = 0, asking = false, failed = '';

  const work = (action, more) => api('api/work', Object.assign({ id: newId(), action }, more));
  const two = n => String(n).padStart(2, '0');
  // A moment, as one says it on the day: 今天 14:30, 明天 09:00, 10月15日 09:00.
  function when(ms) {
    const at = new Date(ms), now = new Date(), day = date => new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
    const apart = Math.round((day(at) - day(now)) / 86400000), time = two(at.getHours()) + ':' + two(at.getMinutes());
    return (apart === 0 ? '今天 ' : apart === 1 ? '明天 ' : apart === -1 ? '昨天 ' : (at.getMonth() + 1) + '月' + at.getDate() + '日 ') + time;
  }
  const every = minutes => (EVERY.find(one => one[0] === minutes) || [0, minutes % 60 ? minutes + ' 分钟' : minutes / 60 + ' 小时'])[1];
  const stamped = ms => { const at = new Date(ms); return at.getFullYear() + '-' + two(at.getMonth() + 1) + '-' + two(at.getDate()) + 'T' + two(at.getHours()) + ':' + two(at.getMinutes()); };

  // ---- the conversations there are to choose from
  // Every conversation the computer lists: under the name its terminal has where one of the phone's runs it, by what
  // it runs and where, and whether it is open (a program on the computer has it, or a terminal of the phone's).
  function conversations() {
    const data = page.data, terminals = {};
    (data.terminals || []).forEach(t => { if (page.running(t)) terminals[t.id] = t; });
    return (data.sessions || []).map(s => {
      const t = terminals[s.terminal] || null;
      return { id: s.id, tool: s.tool, s, t, open: !!t || !!s.live, title: t ? t.title : s.title, place: s.dir || page.base(s.folder),
        where: t ? TOOLS[s.tool] + ' · 手机终端' : page.whereOf({ s, tool: s.tool, live: !!s.live }), at: t ? t.phase_at || t.created : s.updated };
    }).sort((a, b) => (b.open - a.open) || b.at - a.at);
  }
  // whether something can be said to it now: a terminal that asks nothing, a conversation its tool lets be told
  const sayable = c => c.t ? page.phaseOf(c.t) !== 'confirm' : !!page.wayOf(c.s, 'say') && !c.s.ask;
  const titled = (id, kept) => { const now = page.data ? conversations().find(c => c.id === id) : null; return now ? now.title : kept || '已不在列表里的对话'; };
  function chooser(id, list, chosen) {
    const select = el('select', { id });
    if (!list.some(c => c.id === chosen)) select.append(el('option', { value: '', textContent: '选择对话' }));
    list.forEach(c => select.append(el('option', { value: c.id, textContent: c.title + ' · ' + c.where + (c.place ? ' · ' + c.place : '') })));
    select.value = list.some(c => c.id === chosen) ? chosen : '';
    return select;
  }
  const field = (label, control) => el('label', { className: 'field', htmlFor: control.id }, el('span', { textContent: label }), control);
  const fields = (...rows) => el('div', { className: 'fields' }, ...rows);
  // one of a few, as a row of buttons
  function seg(label, choices, chosen, changed) {
    const group = el('span', { className: 'seg', role: 'group', ariaLabel: label });
    choices.forEach(([value, text]) => group.append(el('button', { type: 'button', textContent: text, ariaPressed: String(value === chosen), onclick: event => {
      group.querySelectorAll('button').forEach(button => button.setAttribute('aria-pressed', String(button === event.currentTarget)));
      changed(value);
    } })));
    return group;
  }
  // The button of a sheet that asks the computer for something: while it asks it says so; what the computer
  // refuses is said above it, and the sheet stays as it was filled in.
  function asker(label, busy, act) {
    const said = el('p', { className: 'problem', role: 'alert' }), text = el('span', { textContent: label });
    const button = el('button', { type: 'button', className: 'choice solid', onclick: async () => {
      if (button.disabled) return;
      said.textContent = '';
      button.disabled = true; text.textContent = busy;
      try { await act(problem => { throw new Error(problem); }); }
      catch (error) { said.textContent = error.message; }
      button.disabled = false; text.textContent = label;
    } }, text);
    return [said, button];
  }
  const ready = what => {
    if (!page.usable()) { toast('电脑离线，暂时不能' + what); return false; }
    if (!page.has('work')) { toast('电脑端版本不支持' + what + '，请更新电脑端'); return false; }
    return true;
  };

  // ---- one conversation handed over to another
  // The computer reads the one that is given and writes the other what was asked first and last, what was answered
  // last and done last, with what is added here. `from`: the conversation it starts from, where it is asked for there.
  async function handoff(from) {
    if (!ready('交接')) return;
    const all = conversations(), takers = all.filter(c => c.open);
    if (all.length < 2 || !takers.length) return toast('需要另一段开着的对话来接手');
    const given = from ? from.id : '';
    const taken = await openSheet((form, finish) => {
      const source = chooser('handoff-from', all, given), taker = chooser('handoff-to', takers, (takers.find(c => c.id !== given && given) || {}).id || '');
      const note = el('textarea', { id: 'handoff-note', rows: 3, maxLength: 1000, placeholder: '补充说明（可不填）', ariaLabel: '补充说明', autocapitalize: 'off', spellcheck: false });
      form.append(el('h3', { textContent: '交接对话' }), fields(field('从', source), field('交给', taker)), note, ...asker('交接', '正在交接…', async refuse => {
        if (!source.value || !taker.value) refuse('请选择两段对话');
        if (source.value === taker.value) refuse('不能交接给同一段对话');
        await work('handoff', { from: source.value, to: taker.value, note: note.value.trim() });
        finish(taker.value);
      }));
    });
    if (!taken) return;
    toast('已交给“' + titled(taken) + '”');
    page.refresh();
  }

  // ---- one question put to several conversations at once
  async function sayTo(c, text) {
    if (c.t) {
      const op = { id: newId(), action: 'say', terminal: c.t.id, text };
      let result = await api('api/terminal', op);
      for (let n = 0; n < 40 && result.state === 'queued'; n++) { await new Promise(done => setTimeout(done, n < 3 ? 150 : 400)); result = await api('api/terminal', op); }
      if (result.error) throw new Error(result.error);
      if (result.state === 'queued') throw new Error('电脑没有响应');
      return;
    }
    const payload = { id: newId(), session: c.id, action: 'say', text };
    if (page.wayOf(c.s, 'say') === APP) payload.transport = APP;
    const result = await api('api/conversation', payload);
    if (result.status === 'unknown') throw new Error('结果未知，请查看这段对话');
  }
  async function askMany() {
    if (!page.usable()) return toast('电脑离线，暂时不能发送');
    const list = conversations().filter(c => c.open && sayable(c));
    if (!list.length) return toast('没有可以发消息的对话');
    const sent = await openSheet((form, finish) => {
      const rows = list.map(c => {
        const check = el('input', { type: 'checkbox', id: 'many-' + c.id }), state = el('em');
        return { c, check, state, row: el('label', { className: 'check', htmlFor: check.id }, check,
          el('span', { className: 'text' }, el('b', { textContent: c.title }), el('small', { textContent: c.where + (c.place ? ' · ' + c.place : '') })), state) };
      });
      const text = el('textarea', { id: 'many-text', rows: 3, maxLength: 8000, placeholder: '要问的话', ariaLabel: '要问的话', autocapitalize: 'off', spellcheck: false });
      let done = -1;
      const [said, button] = asker('发送', '正在发送…', async refuse => {
        if (done >= 0) return finish(done);
        const chosen = rows.filter(row => row.check.checked), words = text.value.trim();
        if (!chosen.length) refuse('请选择要问的对话');
        if (!words) { text.focus(); return; }
        rows.forEach(row => { row.check.disabled = true; });
        text.readOnly = true;
        let fine = 0;
        await Promise.all(chosen.map(async row => {
          row.state.className = ''; row.state.textContent = '发送中';
          try { await sayTo(row.c, words); row.state.textContent = '已发送'; row.state.className = 'ok'; fine++; }
          catch (error) { row.state.textContent = error.message; row.state.className = 'failed'; }
        }));
        if (fine === chosen.length) return finish(fine);
        // where one could not be told, the sheet stays to say which and why
        done = fine;
        setTimeout(() => { button.firstChild.textContent = '完成'; }, 0);
      });
      form.append(el('h3', { textContent: '同时提问' }), el('div', { className: 'checks' }, ...rows.map(row => row.row)), text, said, button);
    });
    if (sent) { toast('已发给 ' + sent + ' 段对话'); page.refresh(); }
  }

  // ---- 接力: when one conversation stops, what it said last goes to another, with these words
  async function editChain(known) {
    if (!ready('设置接力')) return;
    if (!page.has('chains')) return toast('这台电脑的电脑端不支持接力');
    const open = conversations().filter(c => c.open);
    // one that is set stays in the choice, also when its conversation is not open now
    const offered = side => { const id = known ? known[side] : ''; return !id || open.some(c => c.id === id) ? open : open.concat(conversations().filter(c => c.id === id)); };
    if (!known && open.length < 2) return toast('先在这台电脑上打开两段 Claude Code 或 Codex 对话，再回来添加接力');
    let rounds = known ? known.rounds : 1;
    const result = await openSheet((form, finish) => {
      const first = chooser('chain-from', offered('from'), known ? known.from : ''), then = chooser('chain-to', offered('to'), known ? known.to : '');
      const text = el('textarea', { id: 'chain-text', rows: 3, maxLength: 1000, placeholder: '要接手的对话做什么', ariaLabel: '要接手的对话做什么', autocapitalize: 'off', spellcheck: false, value: known ? known.text : '' });
      const instructions = field('3. 让接手对话做什么', text); instructions.classList.add('chain-instructions');
      form.append(el('h3', { textContent: known ? '接力' : '添加接力' }), el('p', { className: 'hint', textContent: '来源对话下一次完成一轮后，将最后的回答和下面的要求发给接手对话。保存不会立即发送；电脑端须保持运行并开启状态同步。' }), fields(field('1. 来源对话', first), field('2. 接手对话', then)), instructions,
        fields(el('div', { className: 'field chain-rounds' }, el('span', { textContent: '最多自动转交几次' }), seg('次数', MOST_ROUNDS.map(n => [n, String(n)]), rounds, value => { rounds = value; }))),
        el('p', { className: 'hint', textContent: '默认 1 次：来源完成一轮后转交一次。每次触发都会消耗一次额度，发送失败也会记录并消耗；这不是让 AI 立即连续执行多轮。修改保存会重新开始计数。' }),
        ...asker('保存', '正在保存…', async refuse => {
          if (!first.value || !then.value) refuse('请选择两段对话');
          if (first.value === then.value) refuse('不能接力给同一段对话');
          if (!text.value.trim()) { text.focus(); return; }
          await work('chain_set', { item: { id: known ? known.id : newId(), from: first.value, to: then.value, text: text.value.trim(), rounds } });
          finish('saved');
        }));
      if (known) form.append(el('button', { type: 'button', className: 'choice danger', onclick: async () => {
        try { await work('chain_remove', { chain: known.id }); finish('removed'); } catch (error) { toast(error.message); }
      } }, el('span', { textContent: '删除' })));
    });
    if (result) { toast(result === 'removed' ? '已删除' : '已保存'); load(true); }
  }

  // ---- a question asked at a set time
  async function editSchedule(known) {
    if (!ready('设置定时提问')) return;
    const open = conversations().filter(c => c.open);
    const offered = known && !open.some(c => c.id === known.session) ? open.concat(conversations().filter(c => c.id === known.session)) : open;
    if (!offered.length) return toast('需要一段开着的对话');
    let kind = known ? known.kind : 'daily', on = known ? known.on : true;
    const result = await openSheet((form, finish) => {
      const target = chooser('schedule-session', offered, known ? known.session : offered.length === 1 ? offered[0].id : '');
      const text = el('textarea', { id: 'schedule-text', rows: 3, maxLength: 2000, placeholder: '要问的话', ariaLabel: '要问的话', autocapitalize: 'off', spellcheck: false, value: known ? known.text : '' });
      const once = el('input', { id: 'schedule-at', type: 'datetime-local', value: stamped(known && known.kind === 'once' && known.at > Date.now() ? known.at : Date.now() + 3600000) });
      const daily = el('input', { id: 'schedule-time', type: 'time', value: known && known.time || '09:00' });
      const gap = el('select', { id: 'schedule-minutes' }, ...EVERY.map(([minutes, label]) => el('option', { value: String(minutes), textContent: label })));
      gap.value = String(known && EVERY.some(one => one[0] === known.minutes) ? known.minutes : 60);
      const times = { once: field('时间', once), daily: field('时间', daily), every: field('间隔', gap) };
      const show = () => Object.keys(times).forEach(key => { times[key].hidden = key !== kind; });
      const flip = el('button', { type: 'button', className: 'flip', role: 'switch', ariaChecked: String(on), ariaLabel: '启用', onclick: () => { on = !on; flip.setAttribute('aria-checked', String(on)); } });
      show();
      form.append(el('h3', { textContent: known ? '定时提问' : '添加定时提问' }), fields(field('对话', target)), text,
        fields(el('div', { className: 'field' }, el('span', { textContent: '重复' }), seg('重复', [['once', '一次'], ['daily', '每天'], ['every', '每隔']], kind, value => { kind = value; show(); })),
          times.once, times.daily, times.every, known ? el('div', { className: 'field' }, el('span', { textContent: '启用' }), flip) : ''),
        ...asker('保存', '正在保存…', async refuse => {
          if (!target.value) refuse('请选择对话');
          if (!text.value.trim()) { text.focus(); return; }
          const item = { id: known ? known.id : newId(), session: target.value, text: text.value.trim(), kind, on };
          if (kind === 'once') { item.at = new Date(once.value).getTime(); if (!Number.isFinite(item.at)) refuse('请填写时间'); }
          else if (kind === 'daily') { item.time = daily.value; if (!/^\d\d:\d\d$/.test(item.time)) refuse('请填写时间'); }
          else item.minutes = Number(gap.value);
          await work('schedule_set', { item });
          finish('saved');
        }));
      if (known) form.append(el('button', { type: 'button', className: 'choice danger', onclick: async () => {
        try { await work('schedule_remove', { schedule: known.id }); finish('removed'); } catch (error) { toast(error.message); }
      } }, el('span', { textContent: '删除' })));
    });
    if (result) { toast(result === 'removed' ? '已删除' : '已保存'); load(true); }
  }
  // switched off or on from its card; a single one whose time has passed is given a new time in its sheet
  async function flipSchedule(s) {
    if (!ready('更改定时提问')) return;
    if (!s.on && s.kind === 'once' && s.at <= Date.now()) return editSchedule(s);
    try { await work('schedule_set', { item: { id: s.id, session: s.session, text: s.text, kind: s.kind, time: s.time, at: s.at, minutes: s.minutes, on: !s.on } }); }
    catch (error) { toast(error.message); }
    load(true);
  }

  // ---- what is kept on the computer, read and drawn
  async function load(now) {
    if (asking || !page.data || !page.usable() || !page.has('work')) return;
    if (!now && Date.now() - asked < 3000) return;
    asking = true;
    try {
      // (接力 needs the tools' own word that a conversation has stopped: a computer says whether it has that)
      const [first, second] = await Promise.all([page.has('chains') ? work('chains') : { chains: [] }, work('schedules')]);
      chains = first.chains || []; schedules = second.schedules || []; failed = '';
    } catch (error) { failed = error.message; }
    asking = false; asked = Date.now();
    draw();
  }
  const how = last => el('span', { className: last.ok ? '' : 'failed', textContent: page.ago(last.at) + ' · ' + last.note });
  function chainCard(c) {
    const meta = el('span', { className: 'meta' }, el('span', { className: 'pill ' + (c.left > 0 ? 'starting' : 'ended'), textContent: c.left > 0 ? '还剩 ' + c.left + ' 次' : '已用完' }),
      el('span', { textContent: TOOLS[c.from_tool] + ' → ' + TOOLS[c.to_tool] }), c.last ? how(c.last) : '');
    return el('li', { className: 'card' }, el('button', { type: 'button', className: 'open', onclick: () => editChain(c) },
      el('span', { className: 'tool plain', html: ICON.together }),
      el('span', { className: 'text' }, el('b', { textContent: titled(c.from, c.from_title) + ' → ' + titled(c.to, c.to_title) }), meta, el('span', { className: 'peek', textContent: c.text })),
      el('span', { className: 'chev', html: ICON.chev })));
  }
  function scheduleCard(s) {
    const due = s.on && s.next > 0;
    const meta = el('span', { className: 'meta' }, el('span', { className: 'pill ' + (due ? 'starting' : 'ended'), textContent: due ? '下次 ' + when(s.next) : s.kind === 'once' && s.last ? '已问过' : '已停用' }),
      s.kind === 'once' ? '' : el('span', { textContent: s.kind === 'daily' ? '每天 ' + s.time : '每隔 ' + every(s.minutes) }),
      el('span', { textContent: TOOLS[s.tool] + ' · ' + titled(s.session, s.title) }), s.last ? how(s.last) : '');
    return el('li', { className: 'card' }, el('button', { type: 'button', className: 'open', onclick: () => editSchedule(s) },
      el('span', { className: 'tool plain', html: CLOCK }), el('span', { className: 'text' }, el('b', { textContent: s.text }), meta)),
      el('button', { type: 'button', className: 'side', ariaLabel: s.on ? '停用' : '启用', html: s.on ? PAUSE : PLAY, onclick: () => flipSchedule(s) }));
  }
  const adding = (label, act) => el('li', { className: 'card add' }, el('button', { type: 'button', className: 'open', onclick: act, html: ICON.plus + '<span>' + label + '</span>' }));

  const parts = {};
  function build() {
    const act = (id, label, icon, run) => el('button', { type: 'button', id, onclick: run }, el('span', { className: 'tool plain', html: icon }), el('span', { textContent: label }));
    parts.notice = el('section', { className: 'notice plain', hidden: true });
    parts.chains = el('ul', { className: 'cards' }); parts.schedules = el('ul', { className: 'cards' });
    parts.chainCount = el('p'); parts.scheduleCount = el('p');
    parts.passing = el('section', { id: 'chains' }, el('div', { className: 'section-head' }, el('h2', { textContent: '接力' }), parts.chainCount), el('p', { className: 'hint', textContent: '让一段对话完成后自动交给另一段。例如 Claude Code 写实现，Codex 审查。先打开两段对话，再点“添加接力”选择来源、接手对话和要求。仅限同一台电脑。' }), parts.chains);
    parts.lists = el('div', null, parts.passing,
      el('section', { id: 'schedules' }, el('div', { className: 'section-head' }, el('h2', { textContent: '定时提问' }), parts.scheduleCount), parts.schedules));
    box.append(el('div', { className: 'acts' }, act('handoff', '交接对话', HAND, () => handoff()), act('ask-many', '同时提问', MANY, () => askMany())), parts.notice, parts.lists);
  }
  function fill(list, cards, signature) {
    if (list.dataset.signature === signature) return;
    list.dataset.signature = signature;
    list.replaceChildren(...cards);
  }
  function draw() {
    if (!parts.lists) build();
    const supported = page.has('work'), online = page.usable();
    if (supported && online && Date.now() - asked > 15000) load();
    const notice = !supported ? ['需要更新电脑端', '接力、定时提问和交接由电脑端执行，这台电脑的版本还没有这些功能。'] : failed && chains === null ? ['没有读到', failed] : null;
    parts.notice.hidden = !notice;
    if (notice) parts.notice.replaceChildren(el('b', { textContent: notice[0] }), el('span', { textContent: notice[1] }));
    parts.lists.hidden = !supported || chains === null;
    parts.passing.hidden = !page.has('chains');
    if (chains === null) return;
    // (the minute is part of what a card is known by: "3 分钟前" and "下次 今天 14:30" move on)
    const tick = Math.floor(Date.now() / 60000), names = (page.data.sessions || []).map(s => s.id + s.title).join();
    fill(parts.chains, chains.map(chainCard).concat(adding('添加接力', () => editChain())), JSON.stringify([chains, tick, names]));
    fill(parts.schedules, schedules.map(scheduleCard).concat(adding('添加定时提问', () => editSchedule())), JSON.stringify([schedules, tick, names]));
    parts.chainCount.textContent = chains.length ? chains.length + ' 条' : '';
    parts.scheduleCount.textContent = schedules.length ? schedules.length + ' 个' : '';
  }

  let createOnOpen = new URLSearchParams(location.search).get('newchain') === '1';
  page.view('together', { open: () => {
    load();
    if (createOnOpen) { createOnOpen = false; editChain(); }
  }, draw, handoff });
})();
