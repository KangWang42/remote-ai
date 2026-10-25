/* 协作: conversations working together. One conversation is handed over to another with what was asked and done in
   it; one question is put to several at once; when a conversation stops, what it said last goes on to another (接力);
   and a question is asked of a conversation at a set time. The computer keeps the rules and does the passing on
   (docs/PROTOCOL.md, `/api/work`); this draws them and asks for them to be set. */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { $, el, svg, ICON, TOOLS, api, newId, toast, openSheet } = page;
  const box = $('together'), APP = 'codex-desktop', MOST_ROUNDS = [1, 2, 3, 5, 10];
  const EVERY = [[5, '5 分钟'], [10, '10 分钟'], [15, '15 分钟'], [30, '30 分钟'], [60, '1 小时'], [120, '2 小时'], [240, '4 小时'], [480, '8 小时'], [720, '12 小时'], [1440, '1 天'], [10080, '7 天']];
  const CLOCK = svg('<circle cx="12" cy="12" r="8.6"/><path d="M12 7.300V12l3.1 2"/>'), PAUSE = svg('<path d="M9 7v10M15 7v10"/>', 18), PLAY = svg('<path d="M9 6.500l8.5 5.500L9 17.500z"/>', 18);
  const HAND = svg('<path d="M4 8.500h11.500M12 5l3.500 3.500L12 12M20 15.500H8.500M12 12l-3.500 3.500L12 19"/>'), MANY = svg('<path d="M4.500 6.500h15M4.500 12h15M4.500 17.500h9"/><circle cx="18" cy="17.500" r="1.6"/>');
  let chains = null, schedules = null, asked = 0, asking = false, reload = false;
  const failures = { chains: '', schedules: '' }, changing = new Set();

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
    const data = page.data || {}, terminals = {};
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
    list.forEach(c => select.append(el('option', { value: c.id, disabled: !!c.unavailable, textContent: c.title + ' · ' + c.where + (c.place ? ' · ' + c.place : '') })));
    select.value = list.some(c => c.id === chosen) ? chosen : '';
    return select;
  }
  const field = (label, control) => el('label', { className: 'field' + (control.tagName === 'TEXTAREA' ? ' text-field' : ''), htmlFor: control.id }, el('span', { textContent: label }), control);
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
      button.disabled = false; text.textContent = button.dataset.done ? '完成' : label;
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
    const all = conversations(), takers = all.filter(c => c.open && sayable(c));
    if (all.length < 2 || !takers.length) return toast('需要另一段开着的对话来接手');
    const given = from ? from.id : all.length === 2 ? all[0].id : '';
    const taken = await openSheet((form, finish) => {
      const source = chooser('handoff-from', all, given), taker = chooser('handoff-to', takers, (takers.find(c => c.id !== given && given) || {}).id || '');
      const note = el('textarea', { id: 'handoff-note', rows: 3, maxLength: 1000, placeholder: '补充说明（可不填）', ariaLabel: '补充说明', autocapitalize: 'off', spellcheck: false });
      form.append(el('h3', { textContent: '交接对话' }), el('p', { className: 'hint collab-intro', textContent: '立即把来源对话的上下文交给接手对话。原对话会保留；接手对话需能接收消息。' }), fields(field('从', source), field('交给', taker)), field('补充说明（可选）', note), ...asker('交接', '正在交接…', async refuse => {
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
      const count = el('p', { className: 'hint collab-intro', role: 'status', textContent: '选择要接收同一问题的对话' });
      rows.forEach(row => row.check.addEventListener('change', () => { count.textContent = '已选 ' + rows.filter(one => one.check.checked).length + ' 段对话'; }));
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
        done = fine; button.dataset.done = 'true';
        count.textContent = '已发送 ' + fine + ' / ' + chosen.length + ' 段。未确认成功的对话请查看下方结果，避免重复发送。';
      });
      form.append(el('h3', { textContent: '同时提问' }), count, el('div', { className: 'checks' }, ...rows.map(row => row.row)), field('同一个问题', text), said, button);
    });
    if (sent) { toast('已发给 ' + sent + ' 段对话'); page.refresh(); }
  }

  // ---- 接力: when one conversation stops, what it said last goes to another, with these words
  async function editChain(known) {
    if (!ready('设置接力')) return;
    if (!page.has('chains')) return toast('这台电脑的电脑端不支持接力');
    const open = conversations().filter(c => c.open);
    // one that is set stays in the choice, also when its conversation is not open now
    const offered = side => {
      const id = known ? known[side] : '';
      return !id || open.some(c => c.id === id) ? open : open.concat(conversations().find(c => c.id === id) || { id, title: known[side + '_title'] || '原对话', where: '当前未打开', unavailable: true });
    };
    if (!known && open.length < 2) {
      const with_tools = ((page.data || {}).device || {}).tools || [];
      return toast('先在这台电脑上打开两段对话' + (with_tools.length ? '（' + with_tools.filter(t => t !== 'shell').join('、') + '）' : '') + '，再回来添加接力');
    }
    const itemId = known ? known.id : newId();
    let rounds = known ? known.rounds : 1;
    const result = await openSheet((form, finish) => {
      const first = chooser('chain-from', offered('from'), known ? known.from : open.length === 2 ? open[0].id : ''), then = chooser('chain-to', offered('to'), known ? known.to : open.length === 2 ? open[1].id : '');
      const text = el('textarea', { id: 'chain-text', rows: 3, maxLength: 1000, placeholder: '要接手的对话做什么', ariaLabel: '要接手的对话做什么', autocapitalize: 'off', spellcheck: false, value: known ? known.text : '' });
      const instructions = field('3. 让接手对话做什么', text); instructions.classList.add('chain-instructions');
      const templates = el('div', { className: 'collab-presets', role: 'group', ariaLabel: '接力要求示例' });
      [['审查改动', '审查上述改动，指出影响正确性和使用体验的问题，并给出具体修改建议。'], ['补充测试', '根据上述实现补充有意义的测试，验证关键行为和失败路径。'], ['整理文档', '根据上述工作更新使用说明，写清功能、操作步骤和必要限制。']].forEach(([label, words]) => templates.append(el('button', { type: 'button', textContent: label, onclick: () => {
        const next = text.value.trim() ? text.value.trim() + '\n' + words : words;
        if (next.length > text.maxLength) return toast('要求最多 1000 字，请先精简已有内容');
        text.value = next; text.focus();
      } })));
      const details = el('details', { className: 'collab-help' }, el('summary', { textContent: '执行条件与次数说明' }), el('p', { className: 'hint', textContent: '先打开同一台电脑上的两段对话，并在电脑端开启“手机处理电脑上的对话”。每次触发消耗一次额度，失败也会记录并消耗。单条规则是单向转交；修改保存会重新开始计数。' }));
      form.append(el('h3', { textContent: known ? '接力' : '添加接力' }), el('p', { className: 'hint collab-intro', textContent: '来源完成一轮 → 把最后的回答和要求发给接手对话。保存不会立即发送。' }), fields(field('1. 来源对话', first), field('2. 接手对话', then)), instructions, templates,
        fields(el('div', { className: 'field chain-rounds' }, el('span', { textContent: '最多自动转交几次' }), seg('次数', MOST_ROUNDS.map(n => [n, String(n)]), rounds, value => { rounds = value; }))),
        details,
        ...asker('保存', '正在保存…', async refuse => {
          if (!first.value || !then.value) refuse('请选择两段对话');
          if (first.value === then.value) refuse('不能接力给同一段对话');
          if (!text.value.trim()) { text.focus(); return; }
          await work('chain_set', { item: { id: itemId, from: first.value, to: then.value, text: text.value.trim(), rounds } });
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
    const offered = known && !open.some(c => c.id === known.session) ? open.concat(conversations().find(c => c.id === known.session) || { id: known.session, title: known.title || '原对话', where: '当前未打开', unavailable: true }) : open;
    if (!offered.length) return toast('需要一段开着的对话');
    const itemId = known ? known.id : newId();
    let kind = known ? known.kind : 'daily', on = known ? known.on : true;
    const result = await openSheet((form, finish) => {
      const target = chooser('schedule-session', offered, known ? known.session : offered.length === 1 ? offered[0].id : '');
      const text = el('textarea', { id: 'schedule-text', rows: 3, maxLength: 2000, placeholder: '要问的话', ariaLabel: '要问的话', autocapitalize: 'off', spellcheck: false, value: known ? known.text : '' });
      const once = el('input', { id: 'schedule-at', type: 'datetime-local', value: stamped(known && known.kind === 'once' && known.at > Date.now() ? known.at : Date.now() + 3600000) });
      const daily = el('input', { id: 'schedule-time', type: 'time', value: known && known.time || '09:00' });
      const gap = el('select', { id: 'schedule-minutes' }, ...EVERY.map(([minutes, label]) => el('option', { value: String(minutes), textContent: label })));
      if (known && known.kind === 'every' && !EVERY.some(one => one[0] === known.minutes)) gap.append(el('option', { value: String(known.minutes), textContent: every(known.minutes) }));
      gap.value = String(known && known.kind === 'every' ? known.minutes : 60);
      const times = { once: field('时间', once), daily: field('时间', daily), every: field('间隔', gap) };
      const show = () => Object.keys(times).forEach(key => { times[key].hidden = key !== kind; });
      const flip = el('button', { type: 'button', className: 'flip', role: 'switch', ariaChecked: String(on), ariaLabel: '启用', onclick: () => { on = !on; flip.setAttribute('aria-checked', String(on)); } });
      show();
      form.append(el('h3', { textContent: known ? '定时提问' : '添加定时提问' }), el('p', { className: 'hint collab-intro', textContent: '到时间后由电脑发出提问，手机可以离线。电脑和目标对话须保持可用；每天的时间按电脑时区执行。' }), fields(field('对话', target)), field('要问的话', text),
        fields(el('div', { className: 'field' }, el('span', { textContent: '重复' }), seg('重复', [['once', '一次'], ['daily', '每天'], ['every', '每隔']], kind, value => { kind = value; show(); })),
          times.once, times.daily, times.every, known ? el('div', { className: 'field' }, el('span', { textContent: '启用' }), flip) : ''),
        ...asker('保存', '正在保存…', async refuse => {
          if (!target.value) refuse('请选择对话');
          if (!text.value.trim()) { text.focus(); return; }
          const item = { id: itemId, session: target.value, text: text.value.trim(), kind, on };
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
    if (changing.has(s.id)) return;
    if (!s.on && s.kind === 'once' && s.at <= Date.now()) return editSchedule(s);
    changing.add(s.id); draw();
    try { await work('schedule_set', { item: { id: s.id, session: s.session, text: s.text, kind: s.kind, time: s.time, at: s.at, minutes: s.minutes, on: !s.on } }); }
    catch (error) { toast(error.message); }
    finally { changing.delete(s.id); await load(true); draw(); }
  }

  // ---- what is kept on the computer, read and drawn
  async function load(now) {
    if (asking) { if (now) reload = true; return; }
    if (!page.data || !page.usable() || !page.has('work')) return;
    if (!now && Date.now() - asked < 3000) return;
    asking = true;
    try {
      // (接力 needs the tools' own word that a conversation has stopped: a computer says whether it has that)
      const [first, second] = await Promise.allSettled([page.has('chains') ? work('chains') : { chains: [] }, work('schedules')]);
      if (first.status === 'fulfilled') { chains = first.value.chains || []; failures.chains = ''; }
      else failures.chains = first.reason.message;
      if (second.status === 'fulfilled') { schedules = second.value.schedules || []; failures.schedules = ''; }
      else failures.schedules = second.reason.message;
    } catch (error) { failures.chains = failures.schedules = error.message; }
    asking = false; asked = Date.now();
    if (reload) { reload = false; return load(true); }
    draw(); launchWanted();
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
      el('button', { type: 'button', className: 'side', disabled: changing.has(s.id), ariaLabel: changing.has(s.id) ? '正在保存' : s.on ? '停用' : '启用', html: s.on ? PAUSE : PLAY, onclick: () => flipSchedule(s) }));
  }
  const adding = (label, act) => el('li', { className: 'card add' }, el('button', { type: 'button', className: 'open', onclick: act, html: ICON.plus + '<span>' + label + '</span>' }));

  const parts = {};
  function build() {
    const act = (id, label, hint, icon, run) => el('button', { type: 'button', id, onclick: run }, el('span', { className: 'tool plain', html: icon }), el('span', { className: 'collab-action' }, el('b', { textContent: label }), el('small', { textContent: hint })));
    parts.notice = el('section', { className: 'notice plain', hidden: true });
    parts.chains = el('ul', { className: 'cards' }); parts.schedules = el('ul', { className: 'cards' });
    parts.chainCount = el('p'); parts.scheduleCount = el('p');
    parts.chainStatus = el('p', { className: 'hint', role: 'status' }); parts.scheduleStatus = el('p', { className: 'hint', role: 'status' });
    parts.passing = el('section', { id: 'chains' }, el('div', { className: 'section-head' }, el('h2', { textContent: '自动接力' }), parts.chainCount), parts.chainStatus, parts.chains);
    parts.lists = el('div', null, parts.passing,
      el('section', { id: 'schedules' }, el('div', { className: 'section-head' }, el('h2', { textContent: '定时提问' }), parts.scheduleCount), parts.scheduleStatus, parts.schedules));
    box.append(el('div', { className: 'acts collaboration-actions' }, act('add-chain', '添加接力', '完成一轮后自动转交', ICON.together, () => editChain()), act('add-schedule', '定时提问', '在指定时间发起提问', CLOCK, () => editSchedule()), act('handoff', '交接对话', '立即交出上下文', HAND, () => handoff()), act('ask-many', '同时提问', '同一个问题发给多段对话', MANY, () => askMany())),
      el('p', { className: 'hint collab-context', textContent: '协作在这台电脑上执行。电脑保持运行，手机可以离线。' }), parts.notice, parts.lists);
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
    const errors = Object.entries(failures).filter(([, error]) => error).map(([key, error]) => (key === 'chains' ? '接力：' : '定时提问：') + error).join('；');
    const notice = !online ? ['电脑暂时离线', '恢复连接后会刷新规则。已读取的内容仅供查看。'] : !supported ? ['需要更新电脑端', '接力、定时提问和交接由电脑端执行，这台电脑的版本还没有这些功能。'] : errors ? ['部分内容未能刷新', errors + '。已有内容保留。'] : null;
    parts.notice.hidden = !notice;
    if (notice) parts.notice.replaceChildren(el('b', { textContent: notice[0] }), el('span', { textContent: notice[1] }), online && supported ? el('button', { type: 'button', textContent: asking ? '正在重试…' : '重新读取', disabled: asking, onclick: () => load(true) }) : '');
    parts.lists.hidden = !supported;
    parts.passing.hidden = !page.has('chains');
    // (the minute is part of what a card is known by: "3 分钟前" and "下次 今天 14:30" move on)
    const tick = Math.floor(Date.now() / 60000), names = (page.data.sessions || []).map(s => s.id + s.title).join();
    fill(parts.chains, (chains || []).map(chainCard).concat(adding('添加接力', () => editChain())), JSON.stringify([chains, tick, names]));
    fill(parts.schedules, (schedules || []).map(scheduleCard).concat(adding('添加定时提问', () => editSchedule())), JSON.stringify([schedules, tick, names, [...changing]]));
    parts.chainCount.textContent = chains && chains.length ? chains.filter(c => c.left > 0).length + ' 条待触发 / ' + chains.length + ' 条' : '';
    parts.scheduleCount.textContent = schedules && schedules.length ? schedules.filter(s => s.on && s.next > 0).length + ' 个待执行 / ' + schedules.length + ' 个' : '';
    parts.chainStatus.textContent = failures.chains ? '读取失败，请重试；显示上次读取的规则。' : chains === null ? '正在读取接力…' : chains.length ? '来源完成一轮后自动转交，点击规则可编辑。' : '还没有接力。可以让 Claude Code 完成实现后，交给 Codex 审查。';
    parts.scheduleStatus.textContent = failures.schedules ? '读取失败，请重试；显示上次读取的安排。' : schedules === null ? '正在读取定时提问…' : schedules.length ? '点击编辑，右侧可直接启用或停用。' : '还没有安排。添加一个问题，让电脑到时间再问。';
    launchWanted();
  }

  // ?do=<name> comes from the app's own 协作, which lists every computer: what was pressed there is begun here at
  // once, on this computer. (?newchain=1 is how the app of an earlier version asks for a new rule.)
  const DO = { handoff: () => handoff(), many: askMany, chain: () => editChain(), schedule: () => editSchedule() };
  const asks = new URLSearchParams(location.search);
  let wanted = asks.get('do') || (asks.get('newchain') === '1' ? 'chain' : '');
  const wantedItem = asks.get('item');
  function launchWanted() {
    if (!wanted || page.tab !== 'together' || !page.usable()) return;
    const action = wanted;
    if (!DO[action]) { wanted = ''; return; }
    if (wantedItem && (action === 'chain' || action === 'schedule')) {
      const list = action === 'chain' ? chains : schedules;
      if (list === null || asking || failures[action === 'chain' ? 'chains' : 'schedules']) return;
      wanted = '';
      const known = list.find(item => item.id === wantedItem);
      if (!known) return toast('这条安排已不存在，请刷新协作列表');
      if (action === 'chain') editChain(known); else editSchedule(known);
    } else { wanted = ''; DO[action](); }
  }
  page.view('together', { open: () => {
    load();
    launchWanted();
  }, draw, handoff });
})();
