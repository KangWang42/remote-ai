/* 协作任务: several AIs at one piece of work on this computer (agent-windows/Teams.cs, docs/PROTOCOL.md `/api/work`
   "teams"). 写审: one writes, another reviews read-only, until the review passes. 领队: one splits the work by what
   each does best and gives it out, checks what comes back, and says when it is done. Every member is a conversation
   of the phone's: it is opened, watched, stopped or answered as any other. This draws the tasks on 协作 and starts,
   stops, goes on with and removes them in sheets (ui/sheet.js). */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { el, svg, TOOLS, api, newId, toast } = page;
  const kit = window.RemoteSheet.ui;
  const TEAM = svg('<circle cx="8" cy="8.500" r="2.800"/><circle cx="16.500" cy="8.500" r="2.800"/><path d="M3.500 18.500c.6-3 2.400-4.500 4.500-4.500s3.900 1.500 4.500 4.500M12 18.500c.6-3 2.400-4.500 4.500-4.500s3.900 1.500 4.500 4.500"/>');
  const MODES = { review: '写审', lead: '领队' }, ROUNDS = [1, 2, 3, 5, 10];
  const STATES = { running: ['进行中', 'starting'], paused: ['已暂停', 'confirm'], done: ['已完成', 'ended'], stopped: ['已停止', 'ended'], failed: ['没有开始', 'ended'] };
  const ROLES = { writer: '写', reviewer: '审查', leader: '领队' };
  let teams = null, failure = '', loading = false;

  const work = (action, more) => api('api/work', Object.assign({ id: newId(), action }, more));
  // the tools that can take part: those this computer runs as a conversation of the phone's
  const able = () => ['claude', 'codex', 'agy'].filter(tool => page.chatOk(tool));
  const roleName = (team, role) => (ROLES[role] ? ROLES[role] + ' · ' : '成员 · ') + TOOLS[role === 'writer' || role === 'leader' ? team.tools[0] : role === 'reviewer' ? team.tools[1] : role];
  const toolOf = (team, role) => role === 'writer' || role === 'leader' ? team.tools[0] : role === 'reviewer' ? team.tools[1] : role;
  const who = team => team.mode === 'review' ? TOOLS[team.tools[0]] + ' 写 · ' + TOOLS[team.tools[1]] + ' 审' : TOOLS[team.tools[0]] + ' 带 ' + team.tools.slice(1).map(t => TOOLS[t]).join('、');

  async function load() {
    if (loading || !page.has('teams') || !page.usable()) return;
    loading = true;
    try { teams = (await work('teams')).teams || []; failure = ''; }
    catch (error) { failure = error.message; }
    loading = false;
  }

  // ---- a new one
  function begin() {
    if (!page.usable()) return toast('电脑离线，暂时不能开始');
    if (!page.has('teams')) return toast('这台电脑的电脑端不支持协作任务，请更新电脑端');
    const tools = able(), projects = (page.data.device.workspaces || []);
    if (tools.length < 2) return toast('需要这台电脑能在手机上运行至少两个 AI（Claude Code、Codex、Antigravity CLI）');
    if (!projects.length) return toast('请先添加一个项目');
    // the same number when it is tried again, so that a lost answer does not start two
    const plan = { id: newId(), mode: 'review', goal: '', dir: projects.length === 1 ? projects[0] : '', writer: tools[0], reviewer: tools[1], leader: tools[0], members: tools.slice(1), rounds: 3 };
    window.RemoteSheet.open({ title: '协作任务', build(p) {
      p.body.append(kit.section('方式', kit.seg(Object.keys(MODES).map(key => [key, MODES[key]]), plan.mode, value => { plan.mode = value; p.refresh(); }, { label: '方式', full: true }),
        plan.mode === 'review' ? '一个写，另一个只读审查；审查意见交回去改，直到通过' : '领队按各自特点分派、检查结果，直到它确认完成'));
      const goal = kit.area({ id: 'team-goal', value: plan.goal, placeholder: '要完成什么', max: 4000, rows: 4, label: '目标' }, value => { plan.goal = value; });
      p.body.append(kit.section('目标', goal));
      p.body.append(kit.section('项目', kit.chips(projects.map(name => [name, name]), { chosen: plan.dir, label: '项目' }, value => { plan.dir = value; })));
      const named = tools.map(tool => [tool, TOOLS[tool]]);
      if (plan.mode === 'review') {
        p.body.append(kit.section('写', kit.chips(named, { chosen: plan.writer, label: '写的 AI' }, value => { plan.writer = value; })));
        p.body.append(kit.section('审查', kit.chips(named, { chosen: plan.reviewer, label: '审查的 AI' }, value => { plan.reviewer = value; }), '审查的 AI 以只读方式运行'));
      } else {
        p.body.append(kit.section('领队', kit.chips(named, { chosen: plan.leader, label: '领队' }, value => { plan.leader = value; plan.members = plan.members.filter(t => t !== value); p.refresh(); })));
        p.body.append(kit.section('成员', kit.chips(named.filter(([tool]) => tool !== plan.leader), { chosen: plan.members, multi: true, label: '成员' }, value => { plan.members = value; }), '成员在同一个项目文件夹里工作，领队会把不重叠的部分分给它们'));
      }
      p.body.append(kit.section('最多几轮', kit.seg(ROUNDS.map(n => [n, n + ' 轮']), plan.rounds, value => { plan.rounds = value; }, { label: '轮数', full: true })));
      const go = kit.go('开始', async () => {
        if (!plan.goal.trim()) { goal.box.focus(); throw new Error('请写下要完成什么'); }
        if (!plan.dir) throw new Error('请选择项目');
        const chosen = plan.mode === 'review' ? [plan.writer, plan.reviewer] : [plan.leader].concat(plan.members);
        if (plan.mode === 'lead' && chosen.length < 2) throw new Error('请至少选一个成员');
        const made = await work('team_start', { item: { id: plan.id, mode: plan.mode, dir: plan.dir, goal: plan.goal.trim(), tools: chosen, rounds: plan.rounds } });
        p.sheet.close('started'); toast('已开始');
        await load(); page.refresh();
        if (made.team) show(made.team);
      }, { busy: '正在开始…', id: 'team-start' });
      p.foot.append(...go.nodes);
    } }, '协作任务');
  }

  // ---- one: how it stands, what each member said, its members' conversations, and what can be done to it
  function show(known) {
    let confirmRemove = false;
    window.RemoteSheet.open({ title: MODES[known.mode] + ' · ' + known.dir, build(p) {
      const team = (teams || []).find(t => t.id === known.id) || known, state = STATES[team.state] || [team.state, 'ended'];
      p.body.append(kit.who({ icon: 'spark', tint: 'agy', title: team.goal, line: state[0] + ' · ' + who(team) + ' · 第 ' + team.round + ' / ' + team.rounds + ' 轮', state: team.state === 'running' ? 'var(--good)' : team.state === 'paused' ? 'var(--busy)' : '' }));
      if (team.note) p.body.append(kit.summary(team.note, team.state === 'failed' || team.state === 'paused' ? 'bad' : '', team.state === 'done' ? 'tick' : 'clock'));
      // each member's conversation, to be opened as any other
      const terminals = page.data.terminals || [];
      const members = Object.keys(team.members || {}).map(role => {
        const terminal = team.members[role], t = terminals.find(one => one.id === terminal), at = (team.awaited || []).includes(role);
        return kit.row({ icon: toolOf(team, role), tint: toolOf(team, role) === 'claude' || toolOf(team, role) === 'agy' ? toolOf(team, role) : 'codex', id: 'member-' + role,
          label: roleName(team, role), detail: at ? '正在做' : t && page.running(t) ? '对话开着' : '对话已结束', go: t ? () => page.open(terminal, 'chat') : null });
      });
      if (members.length) p.body.append(kit.section('成员', kit.group(...members), '点一个成员打开它的对话，可以查看、停止或回答它的确认'));
      const steps = (team.log || []).slice(-30).reverse().map(step => el('li', null, el('b', { textContent: ROLES[step.who] || TOOLS[step.who] || step.who }), el('span', { className: 'at', textContent: page.ago(step.at) }), el('p', { textContent: step.text })));
      if (steps.length) p.body.append(kit.section('经过', el('ul', { className: 'team-log' }, ...steps)));
      if (team.state === 'running') {
        const stop = kit.go('停止', async () => { await work('team_stop', { team: team.id }); await load(); p.refresh(); page.refresh(); toast('已停止'); }, { busy: '正在停止…', kind: 'danger', id: 'team-stop' });
        p.foot.append(...stop.nodes);
      }
      if (team.state === 'paused') {
        const resume = kit.go('继续', async () => { await work('team_resume', { team: team.id }); await load(); p.refresh(); page.refresh(); toast('已继续'); }, { busy: '正在继续…', id: 'team-resume' });
        p.foot.append(...resume.nodes);
      }
      if (team.state !== 'running') {
        const remove = kit.go('删除', async () => {
          if (!confirmRemove) { confirmRemove = true; remove.button.relabel('再按一次删除'); setTimeout(() => { confirmRemove = false; if (remove.button.isConnected) remove.button.relabel('删除'); }, 4000); return; }
          await work('team_remove', { team: team.id }); await load(); p.sheet.close('removed'); page.refresh(); toast('已删除');
        }, { busy: '正在删除…', kind: 'danger', id: 'team-remove' });
        p.foot.append(...remove.nodes);
      }
    } }, '协作任务');
  }

  // ---- on 协作: its card for each, and one to begin another
  function card(team) {
    const state = STATES[team.state] || [team.state, 'ended'];
    const meta = el('span', { className: 'meta' }, el('span', { className: 'pill ' + state[1], textContent: state[0] }), el('span', { textContent: MODES[team.mode] + ' · ' + who(team) }),
      el('span', { textContent: '第 ' + team.round + ' / ' + team.rounds + ' 轮' }));
    return el('li', { className: 'card rule' + (team.state === 'running' || team.state === 'paused' ? '' : ' off') }, el('button', { type: 'button', className: 'open', onclick: () => show(team) },
      el('span', { className: 'tool plain', html: TEAM }),
      el('span', { className: 'text' }, el('b', { textContent: team.goal }), meta, team.note ? el('span', { className: 'peek', textContent: team.note }) : ''),
      el('span', { className: 'chev', html: page.ICON.chev })));
  }
  function cards() {
    return (teams || []).map(card).concat(el('li', { className: 'card add' }, el('button', { type: 'button', className: 'open', id: 'team-new', onclick: begin, html: page.ICON.plus + '<span>新的协作任务</span>' })));
  }
  const status = () => failure ? '读取失败，请重试；显示上次读取的任务。' : teams === null ? '正在读取协作任务…' : teams.length ? '' : '还没有协作任务。可以让 Claude Code 写、Codex 审查，或让一个 AI 带着其它 AI 分工完成。';
  const count = () => teams && teams.length ? teams.filter(t => t.state === 'running' || t.state === 'paused').length + ' 个进行中 / ' + teams.length + ' 个' : '';

  window.RemoteTeams = { TEAM, load, begin, show, cards, status, count, get teams() { return teams; }, get failure() { return failure; } };
})();
