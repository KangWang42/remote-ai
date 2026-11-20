/* 协作: conversations working together. One conversation is handed over to another AI, a new conversation or one
   that is open, with what was asked and done in it; one question is put to several at once; when a conversation
   stops, what it said last goes on to another (接力); a question is asked of a conversation at a set time; and
   several AIs do one piece of work together (协作任务, teams.js). The
   computer keeps the rules and does the passing on (docs/PROTOCOL.md, `/api/work`); this draws them, and sets them
   in sheets (ui/sheet.js; the hand-over and the questions at a time are ui/work.js, shared with each conversation's
   own menu). */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { $, el, svg, ICON, TOOLS, api, newId, toast } = page;
  const kit = window.RemoteSheet.ui;
  const box = $('together'), APP = 'codex-desktop', MOST_ROUNDS = [1, 2, 3, 5, 10];
  const CLOCK = svg('<circle cx="12" cy="12" r="8.6"/><path d="M12 7.300V12l3.1 2"/>');
  const HAND = svg('<path d="M4 8.500h11.500M12 5l3.500 3.500L12 12M20 15.500H8.500M12 12l-3.500 3.500L12 19"/>'), MANY = svg('<path d="M4.500 6.500h15M4.500 12h15M4.500 17.500h9"/><circle cx="18" cy="17.500" r="1.6"/>');
  const TEMPLATES = [['审查改动', '审查上述改动，指出影响正确性和使用体验的问题，并给出具体修改建议。'], ['补充测试', '根据上述实现补充有意义的测试，验证关键行为和失败路径。'], ['整理文档', '根据上述工作更新使用说明，写清功能、操作步骤和必要限制。']];
  let chains = null, schedules = null, asked = 0, asking = false, reload = false;
  const failures = { chains: '', schedules: '' }, changing = new Set();

  const work = (action, more) => api('api/work', Object.assign({ id: newId(), action }, more));

  // ---- the conversations there are to choose from
  // Every conversation the computer lists: under the name its terminal has where one of the phone's runs it, by what
  // it runs and where, and whether it is open (a program on the computer has it, or a terminal of the phone's).
  function conversations() {
    const data = page.data || {}, terminals = {};
    (data.terminals || []).forEach(t => { if (page.running(t)) terminals[t.id] = t; });
    return (data.sessions || []).map(s => {
      const t = terminals[s.terminal] || null;
      return { id: s.id, tool: s.tool, s, t, open: !!t || !!s.live, title: t ? t.title : s.title, place: s.dir || page.base(s.folder),
        where: t ? TOOLS[s.tool] + (t.form === 'chat' ? ' · 手机对话' : ' · 手机终端') : page.whereOf({ s, tool: s.tool, live: !!s.live }), at: t ? t.phase_at || t.created : s.updated };
    }).sort((a, b) => (b.open - a.open) || b.at - a.at);
  }
  // whether something can be said to it now: a terminal that asks nothing, a conversation its tool lets be told
  const sayable = c => c.t ? page.phaseOf(c.t) !== 'confirm' : !!page.wayOf(c.s, 'say') && !c.s.ask;
  const titled = (id, kept) => { const now = page.data ? conversations().find(c => c.id === id) : null; return now ? now.title : kept || '已不在列表里的对话'; };
  const actions = new window.RemoteWork({ base: '', toast, data: () => page.data, reload: () => page.refresh(), list: conversations, sayable });
  const ready = what => {
    if (!page.usable()) { toast('电脑离线，暂时不能' + what); return false; }
    if (!page.has('work')) { toast('电脑端版本不支持' + what + '，请更新电脑端'); return false; }
    return true;
  };
  const glyph = c => c && (c.tool === 'claude' || c.tool === 'codex') ? c.tool : 'chat';
  const tint = c => c && c.tool === 'claude' ? 'claude' : 'codex';
  const fromOf = c => ({ id: c.id, tool: c.tool, title: c.title, dir: (c.s && c.s.dir) || '' });

  // ---- one conversation handed over to another AI (ui/work.js): from the one given, or one chosen first
  async function handoff(from) {
    if (!ready('交接')) return;
    if (from) {
      const c = conversations().find(one => one.id === from.id);
      return actions.handoff(null, c ? fromOf(c) : { id: from.id, tool: from.tool, title: from.title, dir: from.dir || '' });
    }
    const all = conversations();
    if (!all.length) return toast('电脑上还没有对话');
    window.RemoteSheet.open(actions.pickPage('交接哪段对话', all, [], (ids, sheet) => { const c = all.find(one => one.id === ids[0]); actions.handoff(sheet, fromOf(c)); }));
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
  function askMany() {
    if (!page.usable()) return toast('电脑离线，暂时不能发送');
    const list = conversations().filter(c => c.open && sayable(c));
    if (!list.length) return toast('没有可以发消息的对话');
    const state = { chosen: new Set(), text: '', results: new Map(), done: false };
    window.RemoteSheet.open({ title: '同时提问', build(p) {
      const rows = list.map(c => {
        const result = state.results.get(c.id);
        const row = kit.row({ icon: glyph(c), tint: tint(c), label: c.title, wrap: !!result, detail: result ? result.text : c.where + (c.place ? ' · ' + c.place : ''), checked: state.chosen.has(c.id), id: 'many-' + c.id,
          act: () => { if (state.done) return; if (state.chosen.has(c.id)) state.chosen.delete(c.id); else state.chosen.add(c.id); p.refresh(); } });
        row.setAttribute('role', 'checkbox');
        if (result) row.querySelector('small').className = result.ok ? 'ok' : 'failed';
        return row;
      });
      const sent = [...state.results.values()].filter(r => r.ok).length;
      p.body.append(kit.section('发给', kit.group(...rows), state.done ? '已发送 ' + sent + ' / ' + state.results.size + ' 段。没有发出的，请查看后再单独发送，避免重复。' : '已选 ' + state.chosen.size + ' 段对话'));
      const text = kit.area({ id: 'many-text', value: state.text, placeholder: '要问的话', max: 8000, rows: 3, label: '要问的话' }, value => { state.text = value; });
      if (state.done) text.box.readOnly = true;
      p.body.append(kit.section('问题', text));
      const go = kit.go(state.done ? '完成' : state.chosen.size ? '发给 ' + state.chosen.size + ' 段对话' : '发送', async () => {
        if (state.done) return p.sheet.close(sent);
        const chosen = list.filter(c => state.chosen.has(c.id)), words = state.text.trim();
        if (!chosen.length) throw new Error('请选择要问的对话');
        if (!words) { text.box.focus(); throw new Error('请写下要问的话'); }
        await Promise.all(chosen.map(async c => {
          try { await sayTo(c, words); state.results.set(c.id, { ok: true, text: '已发送' }); }
          catch (error) { state.results.set(c.id, { ok: false, text: error.message }); }
        }));
        const fine = chosen.filter(c => state.results.get(c.id).ok).length;
        if (fine === chosen.length) { p.sheet.close(fine); toast('已发给 ' + fine + ' 段对话'); page.refresh(); return; }
        // where one could not be told, the sheet stays to say which and why
        state.done = true; p.refresh();
      }, { busy: '正在发送…', id: 'many-send' });
      p.foot.append(...go.nodes);
    } }, '同时提问');
  }

  // ---- 接力: when one conversation stops, what it said last goes to another, with these words
  function editChain(known) {
    if (!ready('设置接力')) return;
    if (!page.has('chains')) return toast('这台电脑的电脑端不支持接力');
    const open = conversations().filter(c => c.open);
    if (!known && open.length < 2) {
      const tools = (((page.data || {}).device || {}).tools || []).filter(t => t !== 'shell').map(t => TOOLS[t] || t);
      return toast('先在这台电脑上打开两段对话' + (tools.length ? '（' + tools.join('、') + '）' : '') + '，再回来添加接力');
    }
    // the same number when a save is tried again, so that a lost answer does not make two rules
    const rule = known ? { id: known.id, from: known.from, to: known.to, text: known.text, rounds: known.rounds }
      : { id: newId(), from: open.length === 2 ? open[0].id : '', to: open.length === 2 ? open[1].id : '', text: '', rounds: 1 };
    let confirmRemove = false;
    // one that is set stays in the choice, also when its conversation is not open now
    const offered = side => {
      const id = rule[side];
      return !id || open.some(c => c.id === id) ? open : open.concat(conversations().find(c => c.id === id) || { id, title: (known && known[side + '_title']) || '原对话', where: '当前未打开', unavailable: true });
    };
    const named = (id, side) => { const c = conversations().find(one => one.id === id); return c ? c : id ? { id, title: (known && known[side + '_title']) || '原对话', where: '当前未打开' } : null; };
    window.RemoteSheet.open({ title: known ? '编辑接力' : '添加接力', build(p) {
      const side = (key, label) => {
        const c = named(rule[key], key);
        return kit.row({ icon: c ? glyph(c) : 'chat', tint: c ? tint(c) : 'muted', id: 'chain-' + key, label: c ? c.title : '选择' + label, detail: c ? label + ' · ' + c.where : '', wrap: true,
          go: () => p.sheet.push(actions.pickPage(label, offered(key), [rule[key]], ids => { rule[key] = ids[0]; p.sheet.goBack(); })) });
      };
      p.body.append(kit.section('对话', kit.group(side('from', '来源'), side('to', '接手')), '来源完成一轮后，把它最后的回答和下面的要求发给接手的对话'));
      const text = kit.area({ id: 'chain-text', value: rule.text, placeholder: '要接手的对话做什么', max: 1000, rows: 3, label: '要接手的对话做什么' }, value => { rule.text = value; });
      p.body.append(kit.section('让接手的对话做什么', [text, kit.chips(TEMPLATES.map(([label, words]) => [words, label]), {}, words => {
        const next = text.box.value.trim() ? text.box.value.trim() + '\n' + words : words;
        if (next.length > 1000) return toast('要求最多 1000 字，请先精简已有内容');
        text.box.value = rule.text = next; text.grow(); text.box.focus();
      })]));
      p.body.append(kit.section('最多自动转交', kit.seg(MOST_ROUNDS.map(n => [n, n + ' 次']), rule.rounds, value => { rule.rounds = value; }, { label: '次数', full: true }), '每次转交用掉一次；改动后保存会重新计数'));
      if (known && known.last) p.body.append(kit.note('上次 ' + page.ago(known.last.at) + ' · ' + known.last.note, known.last.ok ? '' : 'bad'));
      const save = kit.go('保存', async () => {
        if (!rule.from || !rule.to) throw new Error('请选择两段对话');
        if (rule.from === rule.to) throw new Error('不能接力给同一段对话');
        if (!rule.text.trim()) { text.box.focus(); throw new Error('请写下要接手的对话做什么'); }
        await work('chain_set', { item: { id: rule.id, from: rule.from, to: rule.to, text: rule.text.trim(), rounds: rule.rounds } });
        p.sheet.close('saved'); toast('已保存'); load(true);
      }, { busy: '正在保存…', id: 'chain-save' });
      p.foot.append(...save.nodes);
      if (known) {
        const remove = kit.go('删除', async () => {
          if (!confirmRemove) { confirmRemove = true; remove.button.relabel('再按一次删除'); setTimeout(() => { confirmRemove = false; if (remove.button.isConnected) remove.button.relabel('删除'); }, 4000); return; }
          await work('chain_remove', { chain: known.id });
          p.sheet.close('removed'); toast('已删除'); load(true);
        }, { busy: '正在删除…', kind: 'danger', id: 'chain-remove' });
        p.foot.append(...remove.nodes);
      }
    } }, known ? '编辑接力' : '添加接力');
  }

  // ---- a question asked at a time (ui/work.js)
  function editSchedule(known) {
    if (!ready('设置定时提问')) return;
    const open = conversations().filter(c => c.open);
    if (!known && !open.length) return toast('需要一段开着的对话');
    const only = !known && open.length === 1 ? open[0] : null;
    window.RemoteSheet.open(actions.scheduleEditor(known || null, known ? null : { session: only ? only.id : '', title: only ? only.title : '', tool: only ? only.tool : '', choose: true }, () => load(true)));
  }
  // switched off or on from its card; a single one whose time has passed is given a new time in its sheet
  async function flipSchedule(s, button) {
    if (!ready('更改定时提问')) { button.setAttribute('aria-checked', String(s.on)); return; }
    if (changing.has(s.id)) return;
    if (!s.on && s.kind === 'once' && s.at <= Date.now()) { button.setAttribute('aria-checked', 'false'); return editSchedule(s); }
    changing.add(s.id); button.disabled = true;
    try { await work('schedule_set', { item: actions.itemOf(Object.assign({}, s, { on: !s.on })) }); }
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
      const [first, second] = await Promise.allSettled([page.has('chains') ? work('chains') : { chains: [] }, work('schedules'), window.RemoteTeams.load()]);
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
    return el('li', { className: 'card rule' }, el('button', { type: 'button', className: 'open', onclick: () => editChain(c) },
      el('span', { className: 'tool plain', html: ICON.together }),
      el('span', { className: 'text' }, el('b', { textContent: titled(c.from, c.from_title) + ' → ' + titled(c.to, c.to_title) }), meta, el('span', { className: 'peek', textContent: c.text })),
      el('span', { className: 'chev', html: ICON.chev })));
  }
  function scheduleCard(s) {
    const due = s.on && s.next > 0;
    const meta = el('span', { className: 'meta' }, el('span', { className: 'pill ' + (due ? 'starting' : 'ended'), textContent: due ? '下次 ' + window.RemoteWork.when(s.next) : s.kind === 'once' && s.last ? '已问过' : '已停用' }),
      s.kind === 'once' ? '' : el('span', { textContent: window.RemoteWork.howOften(s) }));
    const flip = kit.toggle(s.on, (on, button) => flipSchedule(s, button), s.on ? '停用' : '启用');
    flip.disabled = changing.has(s.id);
    return el('li', { className: 'card rule' + (due ? '' : ' off') }, el('button', { type: 'button', className: 'open', onclick: () => editSchedule(s) },
      el('span', { className: 'tool plain', html: CLOCK }),
      el('span', { className: 'text' }, el('b', { textContent: s.text }), meta, el('span', { className: 'peek', textContent: TOOLS[s.tool] + ' · ' + titled(s.session, s.title) + (s.last ? ' · 上次 ' + page.ago(s.last.at) + ' ' + s.last.note : '') }))),
      el('span', { className: 'switch' }, flip));
  }
  const adding = (label, act, id) => el('li', { className: 'card add' }, el('button', { type: 'button', className: 'open', id, onclick: act, html: ICON.plus + '<span>' + label + '</span>' }));

  const parts = {};
  function build() {
    const act = (id, label, hint, icon, tone, run) => el('button', { type: 'button', id, className: 'act ' + tone, onclick: run }, el('span', { className: 'tool plain', html: icon }), el('span', { className: 'collab-action' }, el('b', { textContent: label }), el('small', { textContent: hint })));
    parts.notice = el('section', { className: 'notice plain', hidden: true });
    parts.chains = el('ul', { className: 'cards' }); parts.schedules = el('ul', { className: 'cards' });
    parts.chainCount = el('p'); parts.scheduleCount = el('p');
    parts.chainStatus = el('p', { className: 'hint', role: 'status' }); parts.scheduleStatus = el('p', { className: 'hint', role: 'status' });
    parts.passing = el('section', { id: 'chains' }, el('div', { className: 'section-head' }, el('h2', { textContent: '自动接力' }), parts.chainCount), parts.chainStatus, parts.chains);
    parts.teams = el('ul', { className: 'cards' }); parts.teamCount = el('p'); parts.teamStatus = el('p', { className: 'hint', role: 'status' });
    parts.working = el('section', { id: 'teams' }, el('div', { className: 'section-head' }, el('h2', { textContent: '协作任务' }), parts.teamCount), parts.teamStatus, parts.teams);
    parts.lists = el('div', null, parts.working,
      el('section', { id: 'schedules' }, el('div', { className: 'section-head' }, el('h2', { textContent: '定时提问' }), parts.scheduleCount), parts.scheduleStatus, parts.schedules), parts.passing);
    box.append(el('div', { className: 'acts collaboration-actions' },
      act('handoff', '交给另一个 AI', '新对话或开着的对话接着做', HAND, 'hand', () => handoff()),
      act('ask-many', '同时提问', '一个问题发给几段对话', MANY, 'many', () => askMany()),
      act('add-schedule', '定时提问', '到时间由电脑替你问', CLOCK, 'time', () => editSchedule()),
      act('add-chain', '自动接力', '做完一轮自动转交', ICON.together, 'relay', () => editChain())),
      parts.notice, parts.lists);
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
    parts.working.hidden = !page.has('teams');
    const teams = window.RemoteTeams;
    fill(parts.teams, teams.cards(), JSON.stringify([teams.teams, Math.floor(Date.now() / 60000), page.data.device.tools, page.data.device.features]));
    parts.teamCount.textContent = teams.count(); parts.teamStatus.textContent = teams.status(); parts.teamStatus.hidden = !parts.teamStatus.textContent;
    // (the minute is part of what a card is known by: "3 分钟前" and "下次 今天 14:30" move on)
    const tick = Math.floor(Date.now() / 60000), names = (page.data.sessions || []).map(s => s.id + s.title).join();
    fill(parts.chains, (chains || []).map(chainCard).concat(adding('添加接力', () => editChain(), 'chain-new')), JSON.stringify([chains, tick, names]));
    fill(parts.schedules, (schedules || []).map(scheduleCard).concat(adding('添加定时提问', () => editSchedule(), 'schedule-new')), JSON.stringify([schedules, tick, names, [...changing]]));
    parts.chainCount.textContent = chains && chains.length ? chains.filter(c => c.left > 0).length + ' 条待触发 / ' + chains.length + ' 条' : '';
    parts.scheduleCount.textContent = schedules && schedules.length ? schedules.filter(s => s.on && s.next > 0).length + ' 个待执行 / ' + schedules.length + ' 个' : '';
    parts.chainStatus.textContent = failures.chains ? '读取失败，请重试；显示上次读取的规则。' : chains === null ? '正在读取接力…' : chains.length ? '' : '还没有接力。可以让 Claude Code 做完实现后，交给 Codex 审查。';
    parts.scheduleStatus.textContent = failures.schedules ? '读取失败，请重试；显示上次读取的安排。' : schedules === null ? '正在读取定时提问…' : schedules.length ? '' : '还没有安排。到时间由电脑替你发出，手机可以离线。';
    parts.chainStatus.hidden = !parts.chainStatus.textContent; parts.scheduleStatus.hidden = !parts.scheduleStatus.textContent;
    launchWanted();
  }

  // ?do=<name> comes from the app's own 协作, which lists every computer: what was pressed there is begun here at
  // once, on this computer. (?newchain=1 is how the app of an earlier version asks for a new rule.)
  const DO = { handoff: () => handoff(), many: askMany, chain: () => editChain(), schedule: () => editSchedule(), team: () => window.RemoteTeams.begin() };
  const asks = new URLSearchParams(location.search);
  let wanted = asks.get('do') || (asks.get('newchain') === '1' ? 'chain' : '');
  const wantedItem = asks.get('item');
  function launchWanted() {
    if (!wanted || page.tab !== 'together' || !page.usable()) return;
    const action = wanted;
    if (!DO[action]) { wanted = ''; return; }
    if (wantedItem && (action === 'chain' || action === 'schedule' || action === 'team')) {
      const list = action === 'chain' ? chains : action === 'schedule' ? schedules : window.RemoteTeams.teams;
      const failed = action === 'chain' ? failures.chains : action === 'schedule' ? failures.schedules : window.RemoteTeams.failure;
      if (list === null || asking || failed) return;
      wanted = '';
      const known = list.find(item => item.id === wantedItem);
      if (!known) return toast(action === 'team' ? '这个协作任务已不存在，请刷新协作列表' : '这条安排已不存在，请刷新协作列表');
      if (action === 'chain') editChain(known); else if (action === 'schedule') editSchedule(known); else window.RemoteTeams.show(known);
    } else { wanted = ''; DO[action](); }
  }
  page.view('together', { open: () => {
    load();
    launchWanted();
  }, draw, handoff });
})();
