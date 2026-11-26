/* What is done with a conversation beyond talking to it, from wherever it is looked at (its menu on the conversation
   and terminal pages, and 协作 on the list page): handing it over to another AI, a new conversation or one that is
   open, and questions asked of it at a time. The computer does them (docs/PROTOCOL.md, `/api/work`: brief, handoff,
   schedules); these are the pages of a sheet (ui/sheet.js) that set them.

   new RemoteWork({ base, toast, data, reload, list?, sayable?, leave? })
     base     where api/ and chat/ lie from this page ('' or '../')
     data()   what the computer listed last ({device, sessions, terminals}), or null; reload() asks anew
     list()   the conversations to choose from, as conversations() makes them (the list page has its own words) */
(() => {
  'use strict';
  const { ui } = window.RemoteSheet, { el } = ui;
  const TOOLS = { claude: 'Claude Code', codex: 'Codex', agy: 'Antigravity CLI', opencode: 'OpenCode', qoder: 'Qoder', cline: 'Cline', pi: 'Pi', shell: '终端' };
  const NEW_TOOLS = ['claude', 'codex', 'agy'];
  // a tool's mark and colour in the sheet: its own where it has one
  const mark = tool => ['claude', 'codex', 'agy'].includes(tool) ? tool : 'chat';
  const tint = tool => ['claude', 'agy'].includes(tool) ? tool : 'codex';
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  const two = n => String(n).padStart(2, '0');
  const WEEK = ['一', '二', '三', '四', '五', '六', '日'];       // 1 Monday … 7 Sunday, as the computer counts them
  const EVERY = [[15, '15 分钟'], [30, '30 分钟'], [60, '1 小时'], [120, '2 小时'], [240, '4 小时'], [480, '8 小时'], [1440, '1 天']];
  const ASKS = ['继续', '汇报一下当前进展和下一步', '运行测试，修复失败的用例', '总结今天完成的工作'];
  const NOTES = ['接着完成剩下的工作', '先检查已做的改动，再继续', '只总结进展和下一步，不改代码'];

  // ---- time, as one says it on the day
  function when(ms) {
    const at = new Date(ms), now = new Date(), day = date => new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
    const apart = Math.round((day(at) - day(now)) / 86400000), time = two(at.getHours()) + ':' + two(at.getMinutes());
    return (apart === 0 ? '今天 ' : apart === 1 ? '明天 ' : apart === 2 ? '后天 ' : apart === -1 ? '昨天 ' : (at.getMonth() + 1) + '月' + at.getDate() + '日 ') + time;
  }
  const ago = ms => { const s = Math.max(0, (Date.now() - ms) / 1000); return s < 90 ? '刚刚' : s < 3600 ? Math.round(s / 60) + ' 分钟前' : s < 86400 ? Math.round(s / 3600) + ' 小时前' : Math.round(s / 86400) + ' 天前'; };
  const every = minutes => (EVERY.find(one => one[0] === minutes) || [0, minutes % 1440 === 0 ? minutes / 1440 + ' 天' : minutes % 60 ? minutes + ' 分钟' : minutes / 60 + ' 小时'])[1];
  const stamped = ms => { const at = new Date(ms); return at.getFullYear() + '-' + two(at.getMonth() + 1) + '-' + two(at.getDate()) + 'T' + two(at.getHours()) + ':' + two(at.getMinutes()); };
  // the days of a daily one in words: 每天, 工作日, 周末, 周一、三、五
  function daysSaid(days) {
    const list = (days || []).slice().sort();
    if (!list.length || list.length === 7) return '每天';
    if (list.join() === '1,2,3,4,5') return '工作日';
    if (list.join() === '6,7') return '周末';
    return '周' + list.map(day => WEEK[day - 1]).join('、');
  }
  // how often, in words: 每天 09:00, 工作日 09:00, 每隔 1 小时, 一次
  const howOften = s => s.kind === 'daily' ? daysSaid(s.days) + ' ' + s.time : s.kind === 'every' ? '每隔 ' + every(s.minutes) : '一次';
  // when one would be asked next, by this phone's clock (the computer's may differ by its time zone)
  function nextOf(s, now = Date.now()) {
    if (s.kind === 'once') return s.at > now ? s.at : 0;
    if (s.kind === 'every') return now + s.minutes * 60000;
    if (!/^\d\d:\d\d$/.test(s.time || '')) return 0;
    const [hour, minute] = s.time.split(':').map(Number), at = new Date(now);
    at.setHours(hour, minute, 0, 0);
    if (at.getTime() <= now) at.setDate(at.getDate() + 1);
    const days = s.days && s.days.length ? s.days : null;
    for (let n = 0; days && n < 8 && !days.includes(at.getDay() || 7); n++) at.setDate(at.getDate() + 1);
    return at.getTime();
  }

  class RemoteWork {
    constructor(options) {
      this.o = options;
      this.base = options.base || '';
      this.toast = options.toast || (() => {});
    }
    get data() { return this.o.data(); }
    has(feature) { const data = this.data; return !!data && (data.device.features || []).includes(feature); }
    usable() { const data = this.data; return !!data && data.device.online !== false && data.device.enabled !== false; }
    async api(path, payload) {
      const reply = await fetch(this.base + path, payload === undefined ? { credentials: 'same-origin', cache: 'no-store' }
        : { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
      let body = {};
      try { body = await reply.json(); } catch (error) { /* not JSON */ }
      if (reply.status === 401) throw new Error('请重新输入访问密码');
      if (!reply.ok) throw new Error(body.error || '请求失败（' + reply.status + '）');
      return body;
    }
    work(action, more) { return this.api('api/work', Object.assign({ id: newId(), action }, more)); }
    // an operation on terminals, asked again until the computer has done it
    async terminal(payload) {
      const op = Object.assign({ id: newId() }, payload);
      let result = await this.api('api/terminal', op);
      for (let n = 0; n < 62 && result.state === 'queued'; n++) {
        await new Promise(done => setTimeout(done, n < 3 ? 150 : 400));
        result = await this.api('api/terminal', op);
      }
      if (result.error) throw new Error(result.error);
      if (result.state === 'queued') throw new Error('电脑没有响应，请确认电脑端程序在运行');
      return result;
    }
    // The conversations to choose from: each with what runs it and where, whether it is open (a program on the
    // computer has it, or one of the phone's terminals), and whether something can be said to it now.
    list() {
      if (this.o.list) return this.o.list();
      const data = this.data || {}, terminals = {};
      (data.terminals || []).forEach(t => { if (t.state === 'running' || t.state === 'starting') terminals[t.id] = t; });
      return (data.sessions || []).map(s => {
        const t = terminals[s.terminal] || null;
        return { id: s.id, tool: s.tool, s, t, open: !!t || !!s.live, title: t ? t.title : s.title, place: s.dir || '',
          where: (TOOLS[s.tool] || s.tool) + (t ? t.form === 'chat' ? ' · 手机对话' : ' · 手机终端' : s.live ? ' · 电脑上开着' : ''), at: t ? t.phase_at || t.created : s.updated };
      }).sort((a, b) => (b.open - a.open) || b.at - a.at);
    }
    sayable(c) {
      if (this.o.sayable) return this.o.sayable(c);
      if (c.t) return c.t.phase !== 'confirm' && c.t.status !== 'confirm';
      const s = c.s;
      return !!s.live && !s.ask && (s.ways ? !!s.ways.say : Array.isArray(s.reach) && s.reach.includes('say'));
    }
    find(id) { return this.list().find(c => c.id === id) || null; }
    ready(what) {
      if (!this.data) return '正在读取电脑上的对话，请稍候';
      if (!this.usable()) return '电脑离线，暂时不能' + what;
      if (!this.has('work')) return '电脑端版本不支持' + what + '，请更新电脑端';
      return '';
    }
    // Reads the computer's list once more, for a page that was opened before it changed.
    async fresh() { try { await this.o.reload(); } catch (error) { /* the last list stays */ } }
    // The collaboration task a phone's terminal is a member of, and its role there: { team, role, tool } or null.
    async member(terminal) {
      const listed = ((this.data || {}).terminals || []).find(t => t.id === terminal);
      if (!terminal || !listed || !listed.team || !this.has('teams-v2')) return null;
      const team = ((await this.work('teams')).teams || []).find(t => t.id === listed.team);
      const role = team && Object.keys(team.members || {}).find(r => team.members[r] === terminal);
      if (!role || !['running', 'paused', 'done', 'stopped'].includes(team.state)) return null;
      return { team, role, tool: listed.tool };
    }
    // The AIs a member's work can be handed to in its task: those advertised via `<tool>-team`, falling back to chat tools.
    teamTools() {
      const device = (this.data || {}).device || {};
      const feats = device.features || [];
      const flagged = (device.tools || []).filter(tool => feats.includes(tool + '-team'));
      return flagged.length ? flagged : NEW_TOOLS.filter(tool => (device.tools || []).includes(tool) && this.has('chat') && (tool !== 'agy' || this.has('chat-agy')));
    }

    // ================= handing a conversation over to another AI
    /* from: { id (the conversation), tool, title, dir, terminal (the phone's that runs it, if one does) } */
    handoff(sheetOrNull, from) {
      const work = this;
      const state = { to: '', note: '', loaded: false, member: undefined };
      const page = {
        title: '交给另一个 AI',
        build(p) {
          if (!state.loaded) {
            state.loaded = true;
            work.fresh().then(() => work.member(from.terminal)).then(member => { state.member = member; }, () => { state.member = null; }).then(p.refresh);
          }
          // a member of a collaboration task is handed over within it: the task goes on with the AI that takes over
          if (state.member) { work.teamHandoffPage(state.member, state).build(p); return; }
          if (from.terminal && state.member === undefined) { p.body.append(ui.loading(), ui.loading()); return; }
          const data = work.data || { device: {} }, device = data.device || {};
          const listed = work.find(from.id);
          const takers = work.list().filter(c => c.open && c.id !== from.id && work.sayable(c));
          // a new conversation of a tool the computer has, in the same project: begun with the hand-over as its first message
          const fresh = from.dir && work.has('chat') ? NEW_TOOLS.filter(tool => (device.tools || []).includes(tool) && (tool !== 'agy' || work.has('chat-agy'))) : [];
          // how much of its limits each tool has left, where the computer knows it (primary pool first)
          const remaining = tool => {
            const q = (device.quotas || []).find(one => one.tool === tool);
            const all = (q && q.windows) || [], main = all.filter(w => !w.group), pool = main.length ? main : all;
            const least = pool.length ? Math.min(...pool.map(w => Math.max(0, Math.min(100, 100 - (Number(w.used) || 0))))) : null;
            return least === null ? '' : ' · 剩余额度 ' + Math.round(least) + '%';
          };
          const offers = fresh.map(tool => ({ key: 'new:' + tool, tool, label: '新对话 · ' + TOOLS[tool], detail: '在 ' + from.dir + ' 中开始' + (tool === from.tool ? ' · 同一工具，重新开始' : '') + remaining(tool), tint: tool, icon: tool }))
            .concat(takers.map(c => ({ key: c.id, c, label: c.title || TOOLS[c.tool], detail: c.where + (c.place ? ' · ' + c.place : ''), tint: tint(c.tool), icon: ['claude', 'codex', 'agy'].includes(c.tool) ? c.tool : 'chat' })));
          // another tool before the one it was in: the usual reason is that one has run out
          offers.sort((a, b) => (a.tool === from.tool) - (b.tool === from.tool));
          if (!offers.some(o => o.key === state.to)) state.to = (offers.find(o => o.tool && o.tool !== from.tool) || offers[0] || {}).key || '';
          const chosen = offers.find(o => o.key === state.to);

          p.body.append(ui.who({ icon: mark(from.tool), tint: tint(from.tool), title: from.title || TOOLS[from.tool], line: '从 ' + (TOOLS[from.tool] || from.tool) + (from.dir ? ' · ' + from.dir : '') }));
          const why = work.ready('交接');
          if (why) { p.body.append(ui.summary(why, 'bad', 'hand')); return; }
          if (!listed) { p.body.append(ui.summary(work.data ? '这段对话还没有可以交接的内容，先发一条消息' : '正在读取…', work.data ? 'bad' : '', 'hand')); return; }
          if (!offers.length) {
            p.body.append(ui.summary(from.dir ? '电脑上没有可以接手的工具或开着的对话' : '这段对话不在项目里，只能交给开着的对话；现在没有', 'bad', 'hand'));
            return;
          }
          const rows = offers.map(o => ui.row({ icon: o.icon, tint: o.tint, label: o.label, detail: o.detail, checked: o.key === state.to, act: () => { state.to = o.key; p.refresh(); } }));
          p.body.append(ui.section('交给', ui.group(...rows), chosen && chosen.tool && !work.has('brief') ? '电脑端或中转服务版本较旧，暂不能开始新对话，请更新' : ''));
          const note = ui.area({ id: 'handoff-note', value: state.note, placeholder: '补充说明（可不填）', max: 1000, rows: 3, label: '补充说明' }, value => { state.note = value; });
          p.body.append(ui.section('补充说明', [note, ui.chips(NOTES.map(n => [n, n]), {}, words => {
            const box = note.box, next = box.value.trim() ? box.value.trim() + '\n' + words : words;
            if (next.length > 1000) return;
            box.value = state.note = next; note.grow(); box.focus();
          })]));
          if (work.has('brief')) p.body.append(ui.group(ui.row({ icon: 'chat', tint: 'muted', label: '预览交接内容', detail: '最初和最近的要求、最后的回答和最近的操作', go: () => p.sheet.push(work.briefPage(from, () => state.note)) })));
          const label = !chosen ? '交接' : chosen.tool ? '交给新的 ' + TOOLS[chosen.tool] : '交给“' + chosen.label + '”';
          const go = ui.go(label, async () => {
            const target = offers.find(o => o.key === state.to);
            if (!target) throw new Error('请选择接手的一方');
            if (target.tool) {
              if (!work.has('brief')) throw new Error('电脑端或中转服务版本较旧，暂不能开始新对话，请更新');
              const brief = await work.work('brief', { from: from.id, note: state.note.trim() });
              const started = await work.terminal({ action: 'start', tool: target.tool, dir: from.dir, history: false, form: 'chat', text: String(brief.text || '').slice(0, 8000) });
              p.sheet.close({ started: started.terminal, tool: target.tool }, true);
              work.leaveTo(started.terminal);
              return;
            }
            await work.work('handoff', { from: from.id, to: target.key, note: state.note.trim() });
            p.sheet.close({ to: target.key, title: target.label });
            work.toast('已交给“' + target.label + '”');
          }, { busy: chosen && chosen.tool ? '正在开始新对话…' : '正在交接…', id: 'handoff-go' });
          p.foot.append(...go.nodes);
        }
      };
      return sheetOrNull ? sheetOrNull.push(page) : window.RemoteSheet.open(page);
    }
    // A team member's work given to another AI in the same task (`team_swap`): it takes over with the task's record and
    // the folder's changes, and the conversation that takes over is shown in place of this one.
    teamHandoffPage(member, state) {
      const work = this, { team, role } = member;
      return { build(p) {
        const tools = work.teamTools().filter(tool => tool !== member.tool);
        const named = role === 'leader' ? '领队' : /^member-\d$/.test(role) ? '成员 ' + role.slice(7) : role === 'writer' ? '写手' : role === 'reviewer' ? '审查' : '成员';
        p.body.append(ui.who({ icon: mark(member.tool), tint: tint(member.tool), title: team.goal || '协作任务', line: '协作 · ' + named + ' · ' + (TOOLS[member.tool] || member.tool) }));
        if (!['running', 'paused'].includes(team.state)) { p.body.append(ui.summary('协作任务已结束，在协作页发新要求后再换 AI', 'bad', 'hand')); return; }
        if (!tools.length) { p.body.append(ui.summary('电脑上没有可以接手的其它 AI', 'bad', 'hand')); return; }
        if (!tools.includes(state.to)) state.to = tools[0];
        p.body.append(ui.section('在协作中交给', ui.group(...tools.map(tool => ui.row({ icon: tool, tint: tint(tool), label: TOOLS[tool], checked: tool === state.to, act: () => { state.to = tool; p.refresh(); } })))));
        const note = ui.area({ id: 'handoff-note', value: state.note, placeholder: '补充说明（可不填）', max: 1000, rows: 3, label: '补充说明' }, value => { state.note = value; });
        p.body.append(ui.section('补充说明', note));
        const go = ui.go('交给 ' + TOOLS[state.to] + ' 接手', async () => {
          const payload = { team: team.id, role, tool: state.to };
          if (state.note.trim()) payload.note = state.note.trim();
          const done = await work.work('team_swap', payload);
          const next = done.team && done.team.members ? done.team.members[role] || done.team.members[state.to] : '';
          p.sheet.close(null, true);
          work.toast('已在协作中交给 ' + TOOLS[state.to]);
          const isChat = NEW_TOOLS.includes(state.to) && work.has('chat') && (state.to !== 'agy' || work.has('chat-agy'));
          if (next) location.replace(work.base + (isChat ? 'chat/' : 'terminal/') + '?id=' + encodeURIComponent(next) + '&team=' + encodeURIComponent(team.id));
        }, { busy: '正在交接…', id: 'handoff-go' });
        p.foot.append(...go.nodes);
      } };
    }
    // what a hand-over says, read before it is given
    briefPage(from, note) {
      const work = this, state = { text: null, error: '', asked: '' };
      return {
        title: '交接内容',
        build(p) {
          const words = note();
          if (state.asked !== words) {
            state.asked = words; state.text = null; state.error = '';
            work.work('brief', { from: from.id, note: words.trim() }).then(answer => { state.text = String(answer.text || ''); }, error => { state.error = error.message; }).then(p.refresh);
          }
          if (state.error) p.body.append(ui.summary(state.error, 'bad', 'chat'));
          else if (state.text === null) p.body.append(ui.loading(), ui.loading());
          else p.body.append(el('pre', { className: 'rs-preview', textContent: state.text }), ui.note(state.text.length + ' 字 · 由电脑从对话记录整理'));
        }
      };
    }
    // a new conversation was begun: its page is opened in place of this one
    leaveTo(terminal) {
      const bench = new URLSearchParams(location.search).get('from') === 'bench';
      if (this.o.leave) return this.o.leave(terminal);
      location.replace(this.base + 'chat/?id=' + encodeURIComponent(terminal) + (bench ? '&from=bench' : ''));
    }

    // ================= questions asked at a time
    /* The questions set for one conversation, to switch on and off, to change, and to add to. */
    schedules(sheetOrNull, of) {
      const work = this, state = { items: null, error: '', loading: false, changing: new Set() };
      const load = async p => {
        if (state.loading) return;
        state.loading = true;
        try { state.items = ((await work.work('schedules')).schedules || []).filter(s => s.session === of.id); state.error = ''; }
        catch (error) { state.error = error.message; }
        state.loading = false;
        p.refresh();
      };
      const page = {
        title: '定时提问',
        build(p) {
          const why = work.ready('设置定时提问');
          if (why) { p.body.append(ui.summary(why, 'bad')); return; }
          if (!work.find(of.id)) { p.body.append(ui.summary('这段对话还没有内容，先发一条消息再设置', 'bad')); return; }
          if (state.items === null || page.stale) { page.stale = false; load(p); }
          if (state.error && state.items === null) {
            p.body.append(ui.summary(state.error, 'bad'), ui.group(ui.row({ icon: 'refresh', tint: 'muted', label: '重新读取', act: () => load(p) })));
            return;
          }
          if (state.items === null) { p.body.append(ui.loading(), ui.loading()); return; }
          const add = () => p.sheet.push(work.scheduleEditor(null, { session: of.id, title: of.title, tool: of.tool }, () => { page.stale = true; }));
          if (!state.items.length) {
            p.body.append(el('div', { className: 'rs-empty' }, el('p', { textContent: '到时间由电脑替你发出，手机可以离线' })));
          } else {
            p.body.append(ui.section('这段对话', ui.group(...state.items.map(s => {
              const due = s.on && s.next > 0;
              const flip = ui.toggle(s.on, async (on, button) => {
                if (s.kind === 'once' && on && s.at <= Date.now()) { button.setAttribute('aria-checked', 'false'); return p.sheet.push(work.scheduleEditor(s, { session: of.id }, () => { page.stale = true; })); }
                button.disabled = true;
                try { const saved = await work.work('schedule_set', { item: work.itemOf(Object.assign({}, s, { on })) }); Object.assign(s, saved.schedule || { on }); }
                catch (error) { button.setAttribute('aria-checked', String(!on)); work.toast(error.message); }
                p.refresh();
              }, s.on ? '停用' : '启用');
              return ui.row({ icon: 'clock', tint: due ? 'time' : 'muted', label: s.text, detail: howOften(s) + (due ? ' · 下次 ' + when(s.next) : s.kind === 'once' && s.last ? ' · 已问过' : ' · 已停用'), end: flip, wrap: true, go: () => p.sheet.push(work.scheduleEditor(s, { session: of.id }, () => { page.stale = true; })) });
            }))));
          }
          p.foot.append(ui.go('新建定时提问', async () => add(), { id: 'schedule-add' }).button);
        }
      };
      return sheetOrNull ? sheetOrNull.push(page) : window.RemoteSheet.open(page);
    }
    // what the computer is given of one (the fields it keeps)
    itemOf(s) {
      const item = { id: s.id, session: s.session, text: s.text, kind: s.kind, on: s.on !== false };
      if (s.kind === 'once') item.at = s.at;
      if (s.kind === 'daily') { item.time = s.time; if (s.days && s.days.length && s.days.length < 7) item.days = s.days.slice().sort(); }
      if (s.kind === 'every') item.minutes = s.minutes;
      return item;
    }
    /* known: one that is set (to change); fresh: { session, title, tool } for a new one (session '' lets it be
       chosen here). saved(): told when it was saved or removed. */
    scheduleEditor(known, fresh, saved) {
      const work = this;
      const hourOn = (days, hour) => { const at = new Date(); at.setDate(at.getDate() + days); at.setHours(hour, 0, 0, 0); return at.getTime(); };
      const s = known ? Object.assign({ days: [] }, known, { days: (known.days || []).slice() })
        : { id: newId(), session: fresh.session || '', title: fresh.title || '', tool: fresh.tool || '', text: '', kind: 'daily', time: '09:00', days: [], minutes: 60, at: Date.now() + 3600000, on: true };
      if (s.kind !== 'once' || !(s.at > Date.now())) s.at = s.at > Date.now() ? s.at : Date.now() + 3600000;
      if (!s.minutes) s.minutes = 60;
      if (!s.time) s.time = '09:00';
      let confirmRemove = false;
      return {
        title: known ? '编辑定时提问' : '新建定时提问',
        build(p) {
          const why = work.ready('设置定时提问');
          if (why) { p.body.append(ui.summary(why, 'bad')); return; }
          // the conversation it is for: chosen here where it was not given
          if (!fresh || !fresh.session || fresh.choose) {
            const c = work.find(s.session);
            p.body.append(ui.section('对话', ui.group(ui.row({ icon: c ? mark(c.tool) : 'chat', tint: c ? tint(c.tool) : 'muted', id: 'schedule-session',
              label: c ? c.title : s.session ? s.title || '已不在列表里的对话' : '选择对话', detail: c ? c.where : '', go: () => p.sheet.push(work.pickPage('选择对话', work.list().filter(one => one.open || one.id === s.session), [s.session], picked => { s.session = picked[0]; const one = work.find(s.session); if (one) { s.title = one.title; s.tool = one.tool; } p.sheet.goBack(); })) }))));
          }
          const text = ui.area({ id: 'schedule-text', value: s.text, placeholder: '到时间要问的话', max: 2000, rows: 3, label: '要问的话', autofocus: !known }, value => { s.text = value; });
          p.body.append(ui.section('要问的话', [text, ui.chips(ASKS.map(a => [a, a]), {}, words => {
            const box = text.box; box.value = s.text = box.value.trim() ? box.value.trim() + '\n' + words : words; text.grow(); box.focus();
          })]));

          const summary = el('div');
          const sayWhen = () => {
            const next = nextOf(s);
            summary.replaceChildren(s.kind === 'once' && !(s.at > Date.now()) ? ui.summary('这个时间已经过去', 'bad') : ui.summary(next ? '下次提问：' + when(next) + (s.kind === 'every' ? '（保存后开始计时）' : '') : '请选择时间', next ? '' : 'bad'));
          };
          const timing = [ui.seg([['once', '一次'], ['daily', '每天'], ['every', '间隔']], s.kind, kind => { s.kind = kind; p.refresh(); }, { label: '重复', full: true })];
          if (s.kind === 'once') {
            const at = el('input', { type: 'datetime-local', id: 'schedule-at', value: stamped(s.at), ariaLabel: '日期和时间' });
            at.addEventListener('change', () => { const ms = new Date(at.value).getTime(); if (Number.isFinite(ms)) { s.at = ms; quick.pick(''); sayWhen(); } });
            const tonight = new Date().getHours() < 21 ? hourOn(0, 21) : hourOn(1, 21);
            const quick = ui.chips([[30, '30 分钟后'], [60, '1 小时后'], [180, '3 小时后'], ['tonight', '今晚 21:00'], ['morning', '明早 09:00']], { chosen: '' }, value => {
              s.at = value === 'tonight' ? tonight : value === 'morning' ? hourOn(1, 9) : Math.ceil((Date.now() + value * 60000) / 60000) * 60000;
              at.value = stamped(s.at); sayWhen();
            });
            timing.push(quick, ui.group(ui.row({ label: '日期时间', end: at })));
          } else if (s.kind === 'daily') {
            const time = el('input', { type: 'time', id: 'schedule-time', value: s.time, ariaLabel: '时间' });
            time.addEventListener('change', () => { if (/^\d\d:\d\d$/.test(time.value)) { s.time = time.value; sayWhen(); } });
            timing.push(ui.group(ui.row({ label: '时间', end: time })));
            if (work.has('schedule-days')) {
              const all = [1, 2, 3, 4, 5, 6, 7];
              const presets = ui.chips([['all', '每天'], ['work', '工作日'], ['rest', '周末']], { chosen: daysSaid(s.days) === '每天' ? 'all' : daysSaid(s.days) === '工作日' ? 'work' : daysSaid(s.days) === '周末' ? 'rest' : '' }, value => {
                s.days = value === 'all' ? [] : value === 'work' ? [1, 2, 3, 4, 5] : [6, 7];
                days.pick(s.days.length ? s.days : all); sayWhen();
              });
              const days = ui.chips(all.map(day => [day, WEEK[day - 1]]), { chosen: s.days.length ? s.days : all, multi: true, days: true, label: '星期' }, picked => {
                if (!picked.length) { days.pick(s.days.length ? s.days : all); return work.toast('至少选一天'); }
                s.days = picked.length === 7 ? [] : picked.sort();
                const said = daysSaid(s.days);
                presets.pick(said === '每天' ? 'all' : said === '工作日' ? 'work' : said === '周末' ? 'rest' : '');
                sayWhen();
              });
              timing.push(presets, days);
            }
          } else {
            const options = EVERY.some(one => one[0] === s.minutes) ? EVERY : EVERY.concat([[s.minutes, every(s.minutes)]]);
            timing.push(ui.chips(options.map(([minutes, label]) => [minutes, label]), { chosen: s.minutes }, minutes => { s.minutes = minutes; sayWhen(); }));
          }
          sayWhen();
          p.body.append(ui.section('时间', timing), summary);
          if (known) {
            const last = known.last;
            p.body.append(ui.group(ui.row({ label: '启用', end: ui.toggle(s.on, on => { s.on = on; }, '启用') })),
              last ? ui.note('上次 ' + ago(last.at) + ' · ' + last.note + (known.runs ? ' · 共 ' + known.runs + ' 次' : ''), last.ok ? '' : 'bad') : '');
          }
          const save = ui.go('保存', async () => {
            if (!s.session) throw new Error('请选择对话');
            if (!s.text.trim()) { text.box.focus(); throw new Error('请写下要问的话'); }
            if (s.kind === 'once' && !(s.at > Date.now())) throw new Error('请选择现在之后的时间');
            if (s.kind === 'once' && known && known.kind === 'once' && !known.on) s.on = true;      // a new time is asked again
            await work.work('schedule_set', { item: work.itemOf(Object.assign({}, s, { text: s.text.trim() })) });
            if (saved) saved('saved');
            work.toast('已保存');
            if (p.sheet.stack.length > 1) p.sheet.goBack(); else p.sheet.close('saved');
          }, { busy: '正在保存…', id: 'schedule-save' });
          p.foot.append(...save.nodes);
          if (known) {
            const remove = ui.go('删除', async () => {
              if (!confirmRemove) { confirmRemove = true; remove.button.relabel('再按一次删除'); setTimeout(() => { confirmRemove = false; if (remove.button.isConnected) remove.button.relabel('删除'); }, 4000); return; }
              await work.work('schedule_remove', { schedule: known.id });
              if (saved) saved('removed');
              work.toast('已删除');
              if (p.sheet.stack.length > 1) p.sheet.goBack(); else p.sheet.close('removed');
            }, { busy: '正在删除…', kind: 'danger', id: 'schedule-remove' });
            p.foot.append(...remove.nodes);
          }
        }
      };
    }

    // ================= promoting a conversation into a collaboration task
    /* from: { id (session), tool, title, dir, terminal } */
    promotePage(from, onStarted) {
      const work = this;
      const defaults = window.RemoteTeamDefaults || null;
      const ROUNDS = [1, 2, 3, 5, 10];
      const RULES_KEY = 'remotecli.team.rules';
      const savedRules = () => { try { return localStorage.getItem(RULES_KEY) || ''; } catch { return ''; } };
      const keepRules = text => { try { if (text) localStorage.setItem(RULES_KEY, text); else localStorage.removeItem(RULES_KEY); } catch { } };
      const cleanTitle = (from.title || '').trim();
      const genericTitle = !cleanTitle || cleanTitle === '对话' || cleanTitle === '新的对话' || cleanTitle === TOOLS[from.tool];
      const state = {
        id: newId(),
        mode: 'lead',
        autoRoster: true,
        goal: genericTitle ? '' : cleanTitle,
        rules: savedRules(),
        rounds: 0,
        lastFixedRounds: 3,
        members: [],
        reviewer: '',
        loaded: false
      };
      return {
        title: '转为协作',
        build(p) {
          if (!state.loaded) {
            state.loaded = true;
            work.fresh().then(p.refresh);
          }
          const why = work.ready('转为协作');
          const tools = work.teamTools();
          const device = (work.data || {}).device || {};
          const remaining = tool => {
            const q = (device.quotas || []).find(one => one.tool === tool);
            const all = (q && q.windows) || [], main = all.filter(w => !w.group), pool = main.length ? main : all;
            const least = pool.length ? Math.min(...pool.map(w => Math.max(0, Math.min(100, 100 - (Number(w.used) || 0))))) : null;
            return least === null ? null : Math.round(least);
          };
          const named = tools.map(tool => { const left = remaining(tool); return [tool, (TOOLS[tool] || tool) + (left === null ? '' : ' · 剩 ' + left + '%')]; });

          p.body.append(ui.who({
            icon: mark(from.tool), tint: tint(from.tool),
            title: from.title || TOOLS[from.tool] || '对话',
            line: (state.mode === 'review' ? '当前对话将担任写手 · ' : '当前对话将担任领队 · ') + (TOOLS[from.tool] || from.tool) + (from.dir ? ' · ' + from.dir : '')
          }));
          if (why) { p.body.append(ui.summary(why, 'bad', 'spark')); return; }
          if (!work.has('teams-promote')) { p.body.append(ui.summary('电脑端或中转版本较旧，不支持将对话转为协作，请更新后再试', 'bad', 'spark')); return; }
          if (!from.dir) { p.body.append(ui.summary('这段对话不在项目里，无法转为协作', 'bad', 'spark')); return; }
          if (!tools.includes(from.tool)) { p.body.append(ui.summary((TOOLS[from.tool] || from.tool) + ' 暂不支持参与协作', 'bad', 'spark')); return; }

          if (!state.reviewer || !tools.includes(state.reviewer)) {
            state.reviewer = tools.find(t => t !== from.tool) || from.tool;
          }
          if (!state.members.length) {
            const first = tools.find(t => t !== from.tool) || from.tool;
            const second = tools.find(t => t !== from.tool && t !== first) || '';
            state.members = second ? [first, second] : [first];
          }
          const flexAllowed = state.mode === 'lead' && work.has('teams-flex-rounds');
          if (!flexAllowed && state.rounds === 0) state.rounds = state.lastFixedRounds || 3;

          const modeChoices = tools.length >= 2 ? [['lead', '分工协作'], ['review', '写审']] : [['lead', '分工协作']];
          if (!modeChoices.some(([k]) => k === state.mode)) state.mode = 'lead';
          const modeHint = state.mode === 'lead'
            ? '当前对话担任领队，保留已有上下文；新成员会自动收到前期背景和并行分工说明'
            : '当前对话担任写手继续完成，另一个 AI 只读审查并给出修改意见';
          p.body.append(ui.section('方式', ui.seg(modeChoices, state.mode, value => {
            state.mode = value;
            if (value !== 'lead' && state.rounds === 0) state.rounds = state.lastFixedRounds || 3;
            p.refresh();
          }, { label: '方式', full: true }), modeHint));

          const goal = ui.area({ id: 'promote-goal', value: state.goal, placeholder: '要协作完成什么（可补充要求或约束）', max: 4000, rows: 3, label: '目标' }, value => { state.goal = value; });
          p.body.append(ui.section('目标', goal));

          if (state.mode === 'lead') {
            p.body.append(ui.section('组队方式', ui.seg([['auto', '自动组队'], ['manual', '手动选成员']], state.autoRoster ? 'auto' : 'manual', value => {
              state.autoRoster = value === 'auto';
              p.refresh();
            }, { label: '组队方式', full: true }), state.autoRoster ? '领队按任务和额度按需创建 1–3 个执行成员' : '提前指定 1–3 个执行成员（默认按设置中的模型与思考深度）'));
            if (!state.autoRoster) {
              const memberRows = state.members.map((tool, idx) => { const left = remaining(tool); return ui.row({
                icon: mark(tool), tint: tint(tool),
                label: '执行成员 ' + (idx + 1) + ' · ' + (TOOLS[tool] || tool),
                detail: left !== null ? '剩余额度 ' + left + '%' : '点击切换工具',
                end: state.members.length > 1 ? el('button', {
                  type: 'button', className: 'rs-mini', textContent: '移除',
                  onclick: e => { e.stopPropagation(); state.members.splice(idx, 1); p.refresh(); }
                }) : '',
                act: () => {
                  const nextIdx = (tools.indexOf(tool) + 1) % tools.length;
                  state.members[idx] = tools[nextIdx];
                  p.refresh();
                }
              }); });
              const memberNodes = [ui.group(...memberRows)];
              if (state.members.length < 3) {
                memberNodes.push(ui.group(ui.row({
                  icon: 'plus', tint: 'muted', id: 'promote-member-add',
                  label: '添加执行成员（' + state.members.length + ' / 3）',
                  act: () => {
                    const nextTool = tools.find(t => t !== from.tool && !state.members.includes(t)) || tools[0];
                    state.members.push(nextTool);
                    p.refresh();
                  }
                })));
              }
              p.body.append(ui.section('执行成员', memberNodes, '点成员行可切换工具；同账号共用额度'));
            }
          } else {
            p.body.append(ui.section('审查', ui.chips(named, { chosen: state.reviewer, label: '审查的 AI' }, value => { state.reviewer = value; }), '审查的 AI 以只读方式运行'));
          }

          const roundChoices = (flexAllowed ? [[0, '自由轮数']] : []).concat(ROUNDS.map(n => [n, n + ' 轮']));
          p.body.append(ui.section('最多几轮', ui.seg(roundChoices, state.rounds, value => {
            state.rounds = value;
            if (value > 0) state.lastFixedRounds = value;
          }, { label: '轮数', full: !flexAllowed }), state.rounds === 0 ? '由领队按进展与验收结果决定何时结束（最多 20 轮）' : '达到轮数上限仍未通过验收时会暂停，点「继续」可追加轮次'));

          const rules = ui.area({ id: 'promote-rules', value: state.rules, placeholder: '选填：分工偏好或额外约束（自动记住）', max: 1000, rows: 2, label: '分工偏好' }, value => { state.rules = value; });
          p.body.append(ui.section('分工偏好（选填）', rules));

          const go = ui.go('转为协作', async () => {
            const text = state.goal.trim();
            if (!text) { goal.box.focus(); throw new Error('请写下要协作完成什么'); }
            const trimmedRules = state.rules.trim();
            keepRules(trimmedRules);
            const savedPrefs = defaults ? defaults.readPrefs() : { models: {}, efforts: {} };
            const defaultModel = tool => work.has('teams-models') && savedPrefs.models && savedPrefs.models[tool] ? savedPrefs.models[tool] : '';
            const defaultEffort = tool => work.has('teams-effort') && savedPrefs.efforts && savedPrefs.efforts[tool] ? savedPrefs.efforts[tool] : '';
            const submitRounds = state.mode === 'lead' && flexAllowed && state.rounds === 0 ? 0 : (state.rounds || state.lastFixedRounds || 3);
            let item;
            if (state.mode === 'lead') {
              const roster = [{ role: 'leader', tool: from.tool, model: '' }];
              if (!state.autoRoster) {
                state.members.slice(0, 3).forEach((tool, idx) => {
                  const entry = { role: 'member-' + (idx + 1), tool, model: defaultModel(tool) };
                  const eff = defaultEffort(tool);
                  if (eff) entry.effort = eff;
                  roster.push(entry);
                });
              }
              item = { id: state.id, mode: 'lead', dir: from.dir, goal: text, tools: roster.map(r => r.tool), roster, rounds: submitRounds, from_terminal: from.terminal };
              if (state.autoRoster) item.auto_roster = true;
            } else {
              item = { id: state.id, mode: 'review', dir: from.dir, goal: text, tools: [from.tool, state.reviewer], rounds: submitRounds, from_terminal: from.terminal };
              const rm = defaultModel(state.reviewer), re = defaultEffort(state.reviewer);
              if (rm) item.models = { [state.reviewer]: rm };
              if (re) item.efforts = { [state.reviewer]: re };
            }
            if (trimmedRules) item.rules = trimmedRules;
            const made = await work.work('team_start', { item });
            p.sheet.close(made.team || true, true);
            work.toast('已转为协作任务');
            if (onStarted) onStarted(made.team || null);
          }, { busy: '正在开启协作…', id: 'promote-go' });
          p.foot.append(...go.nodes);
        }
      };
    }

    // ================= choosing conversations
    /* A page of conversations to choose one (or, `multi`, several) of. picked(ids) is told the choice. */
    pickPage(title, list, chosen, picked, { multi, note } = {}) {
      const choice = new Set((chosen || []).filter(Boolean));
      return {
        title,
        build(p) {
          if (!list.length) { p.body.append(ui.empty('没有开着的对话')); return; }
          const groups = [['开着的', list.filter(c => c.open)], ['其它', list.filter(c => !c.open)]].filter(([, l]) => l.length);
          groups.forEach(([name, l]) => p.body.append(ui.section(groups.length > 1 ? name : '', ui.group(...l.map(c => ui.row({
            icon: c.tool === 'shell' ? 'term' : mark(c.tool), tint: tint(c.tool),
            label: c.title || TOOLS[c.tool], detail: c.where + (c.place ? ' · ' + c.place : ''), checked: choice.has(c.id), disabled: !!c.unavailable,
            act: () => {
              if (!multi) { choice.clear(); choice.add(c.id); return picked([c.id], p.sheet); }
              if (choice.has(c.id)) choice.delete(c.id); else choice.add(c.id);
              picked([...choice], p.sheet); p.refresh();
            } }))))));
          if (note) p.body.append(ui.note(note));
        }
      };
    }
  }
  /* For a page that does not list the computer's conversations itself (a conversation's, a terminal's): what the
     computer lists is asked for when a sheet needs it, and kept for a while. */
  RemoteWork.forPage = (base, toast) => {
    let data = null, asked = 0, asking = null;
    const reload = () => {
      if (!asking) asking = fetch(base + 'api/terminal', { credentials: 'same-origin', cache: 'no-store' }).then(reply => reply.ok ? reply.json() : null)
        .then(fresh => { if (fresh && fresh.device) { data = fresh; asked = Date.now(); } return data; }).catch(() => data).finally(() => { asking = null; });
      return asking;
    };
    const work = new RemoteWork({ base, toast, data: () => data, reload });
    work.ensure = () => data && Date.now() - asked < 15000 ? Promise.resolve(data) : reload();
    return work;
  };
  RemoteWork.howOften = howOften;
  RemoteWork.when = when;
  RemoteWork.nextOf = nextOf;
  RemoteWork.daysSaid = daysSaid;
  RemoteWork.TOOLS = TOOLS;
  window.RemoteWork = RemoteWork;
})();
