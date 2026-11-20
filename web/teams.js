/* 协作任务: several AIs at one piece of work on this computer (agent-windows/Teams.cs, docs/PROTOCOL.md `/api/work`
   "teams"). 写审: one writes, another reviews read-only (optionally discussing read-only first). 领队: one splits
   the work by strengths and remaining quotas (optionally after a read-only discussion). 比稿: 2–3 AIs work in
   parallel inside isolated Git worktrees so the owner can compare diffs side by side and merge one. Each task opens
   as a chronological timeline with a persistent bottom composer to interject mid-run or continue after completion. */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { el, svg, TOOLS, api, newId, toast } = page;
  const kit = window.RemoteSheet.ui;
  const TEAM = svg('<circle cx="8" cy="8.500" r="2.800"/><circle cx="16.500" cy="8.500" r="2.800"/><path d="M3.500 18.500c.6-3 2.400-4.500 4.500-4.500s3.900 1.500 4.500 4.500M12 18.500c.6-3 2.400-4.500 4.500-4.500s3.900 1.500 4.500 4.500"/>');
  const MODES = { review: '写审', lead: '领队', compare: '比稿' }, ROUNDS = [1, 2, 3, 5, 10];
  const STATES = { running: ['进行中', 'starting'], choose: ['待选方案', 'confirm'], paused: ['已暂停', 'confirm'], done: ['已完成', 'ended'], stopped: ['已停止', 'ended'], failed: ['没有开始', 'ended'] };
  const ROLES = { writer: '写手', reviewer: '审查', leader: '领队', user: '你' };
  const SHORT = { claude: 'CC', codex: 'CX', agy: 'AG', user: '你' };
  let teams = null, failure = '', loading = false;
  let modelCatalog = null, modelError = '', modelLoading = false, modelPromise = null;

  const work = (action, more) => api('api/work', Object.assign({ id: newId(), action }, more));
  const hasV2 = () => page.has('teams-v2');
  const hasModels = () => page.has('teams-models');
  const hasRoster = () => page.has('teams-roster');
  const able = () => ['claude', 'codex', 'agy'].filter(tool => page.chatOk(tool));
  const phaseLabel = phase => phase === 'verify' ? '领队验收' : phase === 'discuss' ? '讨论方案' : '';
  const isUnverifiedStop = team => !!team && team.state === 'stopped' && team.mode === 'lead' && (team.phase === 'verify' || !!team.last);
  const stateInfo = team => {
    if (!team) return ['已结束', 'ended'];
    if (team.confirm) return ['等你确认', 'confirm'];
    if (team.state === 'running' && team.phase === 'verify') return ['领队验收', 'busy'];
    if (isUnverifiedStop(team)) return ['未完成', 'ended'];
    return STATES[team.state] || [team.state || '已结束', 'ended'];
  };
  const rosterOf = team => Array.isArray(team && team.roster)
    ? team.roster.filter(r => r && typeof r.role === 'string' && r.role && typeof r.tool === 'string' && r.tool)
    : [];
  const rosterEntry = (team, role) => {
    if (!role) return null;
    const clean = role.startsWith('discuss:') ? role.slice(8) : role;
    return rosterOf(team).find(r => r.role === role || r.role === clean) || null;
  };
  const roleTitle = role => {
    if (!role) return '';
    if (ROLES[role]) return ROLES[role];
    const m = /^member-(\d+)$/.exec(role);
    return m ? '成员 ' + m[1] : '';
  };
  const summarizeTools = list => {
    const counts = new Map();
    list.forEach(t => counts.set(t, (counts.get(t) || 0) + 1));
    return [...counts.entries()].map(([t, n]) => (TOOLS[t] || t) + (n > 1 ? ' ×' + n : '')).join('、');
  };
  const toolOf = (team, role) => {
    if (!role) return '';
    const entry = rosterEntry(team, role);
    if (entry) return entry.tool;
    const ms = team && team.member_sessions && team.member_sessions[role];
    if (ms && ms.tool) return ms.tool;
    if (role.startsWith('discuss:')) return role.slice(8);
    if (role === 'writer' || role === 'leader') return (team && team.tools && team.tools[0]) || '';
    if (role === 'reviewer') return (team && team.tools && team.tools[1]) || '';
    return role;
  };
  const modelOf = (team, role) => {
    const entry = rosterEntry(team, role);
    if (entry) return String(entry.model || '').trim();
    const tool = toolOf(team, role);
    return String((team && team.models && team.models[tool]) || '').trim();
  };
  const roleName = (team, role) => {
    if (role === 'user') return '你';
    if (role === '目标' || role === '结果' || role === '切换') return role;
    if (role.startsWith('discuss:')) {
      const inner = role.slice(8), tool = toolOf(team, role), title = roleTitle(inner);
      return '讨论 · ' + (title ? title + ' · ' : '') + (TOOLS[tool] || tool);
    }
    const tool = toolOf(team, role);
    if (team && team.mode === 'compare') return '候选 · ' + (TOOLS[tool] || tool);
    const title = roleTitle(role);
    return (title ? title + ' · ' : '成员 · ') + (TOOLS[tool] || tool);
  };
  const who = team => {
    const roster = rosterOf(team);
    if (team && team.mode === 'lead' && roster.length) {
      const leader = roster.find(r => r.role === 'leader') || roster[0];
      const rest = roster.filter(r => r !== leader).map(r => r.tool);
      const leadName = TOOLS[leader.tool] || leader.tool || '';
      return rest.length ? leadName + ' 带 ' + summarizeTools(rest) : leadName + (team.auto_roster ? ' 自行组队' : '');
    }
    const list = (team && team.tools) || [];
    if (team && team.mode === 'review') return (TOOLS[list[0]] || list[0] || '') + ' 写 · ' + (TOOLS[list[1]] || list[1] || '') + ' 审';
    if (team && team.mode === 'compare') return list.map(t => TOOLS[t] || t).join(' vs ');
    return (TOOLS[list[0]] || list[0] || '') + (list.length > 1 ? ' 带 ' + summarizeTools(list.slice(1)) : team && team.auto_roster ? ' 自行组队' : '');
  };

  function normalizeModels(raw) {
    const out = {};
    if (!raw || typeof raw !== 'object') return out;
    Object.keys(raw).forEach(tool => {
      if (!Array.isArray(raw[tool])) return;
      out[tool] = raw[tool]
        .filter(item => item && typeof item.id === 'string' && item.id.trim())
        .map(item => ({ id: item.id.trim(), label: String(item.label || item.id).trim() }));
    });
    return out;
  }
  async function loadModels(force) {
    if (!hasModels() || !page.usable()) return modelCatalog;
    if (!force && modelCatalog && !modelError) return modelCatalog;
    if (modelPromise) return modelPromise;
    modelLoading = true;
    modelError = '';
    modelPromise = (async () => {
      try {
        const res = await work('team_models');
        modelCatalog = normalizeModels(res && res.models);
        modelError = '';
      } catch (error) {
        modelError = (error && error.message) || '无法读取模型列表';
      } finally {
        modelLoading = false;
        modelPromise = null;
      }
      return modelCatalog;
    })();
    return modelPromise;
  }
  function modelLabel(tool, id) {
    const clean = String(id || '').trim();
    if (!clean) return '默认';
    const list = (modelCatalog && modelCatalog[tool]) || [];
    const found = list.find(item => item.id === clean);
    return found ? found.label : clean;
  }
  function modelOptions(tool, selected) {
    const list = (modelCatalog && modelCatalog[tool]) || [];
    const options = [['', '工具默认']].concat(list.map(item => [item.id, item.label]));
    const clean = String(selected || '').trim();
    if (clean && !list.some(item => item.id === clean)) options.push([clean, clean]);
    return options;
  }
  function makeModelSelect({ id, tool, value, ariaLabel, className, onchange }) {
    const options = modelOptions(tool, value);
    const select = el('select', { id, className: className || 'team-model-select', 'aria-label': ariaLabel },
      ...options.map(([val, label]) => el('option', { value: val, textContent: label, selected: val === value })));
    select.addEventListener('change', () => onchange(select.value));
    return select;
  }
  function plannedRoles(team) {
    if (!team) return [];
    const roster = rosterOf(team);
    if (roster.length) return roster.map(r => r.role);
    const list = team.tools || [];
    if (team.mode === 'review') return list.length >= 2 ? ['writer', 'reviewer'] : ['writer'];
    if (team.mode === 'lead') return ['leader'].concat(list.slice(1));
    return list.slice();
  }
  function memberRolesOf(team) {
    const seen = new Set(), out = [];
    const add = role => { if (role && !seen.has(role)) { seen.add(role); out.push(role); } };
    plannedRoles(team).forEach(add);
    Object.keys((team && team.members) || {}).forEach(add);
    Object.keys((team && team.member_sessions) || {}).forEach(add);
    return out;
  }

  async function load() {
    if (loading || !page.has('teams') || !page.usable()) return;
    loading = true;
    try { teams = (await work('teams')).teams || []; failure = ''; }
    catch (error) { failure = error.message; }
    loading = false;
  }

  const used = tool => {
    const q = ((page.data.device || {}).quotas || []).find(one => one.tool === tool);
    const all = (q && q.windows) || [], main = all.filter(w => !w.group), pool = main.length ? main : all;
    const most = pool.length ? Math.max(...pool.map(w => w.used)) : null;
    return most === null ? '' : ' · ' + Math.round(most) + '%';
  };
  const namedTools = tools => tools.map(tool => [tool, TOOLS[tool] + used(tool)]);

  const RULES_KEY = 'remotecli.team.rules';
  const savedRules = () => { try { return localStorage.getItem(RULES_KEY) || ''; } catch { return ''; } };
  const keepRules = text => { try { if (text) localStorage.setItem(RULES_KEY, text); else localStorage.removeItem(RULES_KEY); } catch { } };

  // ---- a new one
  function begin() {
    if (!page.usable()) return toast('电脑离线，暂时不能开始');
    if (!page.has('teams')) return toast('这台电脑的电脑端不支持协作任务，请更新电脑端');
    const tools = able(), projects = (page.data.device.workspaces || []);
    const rosterSupported = hasRoster();
    if (rosterSupported ? tools.length < 1 : tools.length < 2) {
      return toast(rosterSupported
        ? '需要这台电脑能在手机上运行至少一个 AI（Claude Code、Codex、Antigravity CLI）'
        : '需要这台电脑能在手机上运行至少两个 AI（Claude Code、Codex、Antigravity CLI）');
    }
    if (!projects.length) return toast('请先添加一个项目');
    const defaultWriter = tools.includes('claude') ? 'claude' : tools[0];
    const defaultReviewer = (tools.includes('agy') && defaultWriter !== 'agy') ? 'agy' : (tools.find(t => t !== defaultWriter) || tools[0]);
    const defaultLeader = tools.includes('codex') ? 'codex' : tools[0];
    const defaultMember1 = tools.find(t => t !== defaultLeader) || tools[0];
    const defaultMember2 = tools.find(t => t !== defaultLeader && t !== defaultMember1) || '';
    const plan = {
      id: newId(), mode: tools.length < 2 && rosterSupported ? 'lead' : 'review', goal: '', rules: savedRules(), discuss: false,
      dir: projects.length === 1 ? projects[0] : '',
      writer: defaultWriter, reviewer: defaultReviewer, leader: defaultLeader,
      members: tools.filter(t => t !== defaultLeader),
      autoRoster: false,
      leaderModel: '',
      rosterMembers: [{ tool: defaultMember1, model: '' }].concat(defaultMember2 ? [{ tool: defaultMember2, model: '' }] : []),
      compareTools: tools.slice(0, 3),
      models: {},
      rounds: 3
    };
    let beginSheet = null, modelBox = null, rosterBox = null;
    function activeRoleRows() {
      if (plan.mode === 'review') {
        if (plan.writer === plan.reviewer) return [{ role: '写手 / 审查', tool: plan.writer }];
        return [{ role: '写手', tool: plan.writer }, { role: '审查', tool: plan.reviewer }];
      }
      if (plan.mode === 'lead') {
        return [{ role: '领队', tool: plan.leader }].concat(plan.members.map(t => ({ role: '成员', tool: t })));
      }
      return plan.compareTools.slice(0, 3).map(t => ({ role: '候选', tool: t }));
    }
    function modelNoticeNodes(onRetry) {
      const nodes = [];
      if (modelLoading && !modelCatalog) {
        nodes.push(kit.note('正在读取可用模型，也可直接使用工具默认模型开始'));
      }
      if (modelError) {
        nodes.push(el('div', { className: 'team-models-error', role: 'alert' },
          el('span', { textContent: '模型列表加载失败：' + modelError + '（仍可使用工具默认模型）' }),
          el('button', {
            type: 'button', id: 'team-models-retry', className: 'team-models-retry',
            textContent: modelLoading ? '读取中…' : '重试', disabled: modelLoading,
            onclick: () => { loadModels(true).then(onRetry); onRetry(); }
          })));
      }
      return nodes;
    }
    function renderModels() {
      if (!modelBox) return;
      if (!hasModels() || (plan.mode === 'lead' && hasRoster())) { modelBox.replaceChildren(); return; }
      const roles = activeRoleRows();
      const nodes = modelNoticeNodes(renderModels);
      if (roles.length) {
        const rows = roles.map(({ role, tool }) => {
          const chosen = plan.models[tool] || '';
          const catalog = (modelCatalog && modelCatalog[tool]) || [];
          const sel = makeModelSelect({
            id: 'team-model-' + tool,
            tool,
            value: chosen,
            ariaLabel: role + '（' + (TOOLS[tool] || tool) + '）模型',
            onchange: val => { plan.models[tool] = val; }
          });
          const detail = (TOOLS[tool] || tool) + (!modelLoading && !modelError && modelCatalog && !catalog.length && !chosen ? ' · 仅支持默认模型' : '');
          return kit.row({ icon: tool, tint: tool, label: role, detail, end: sel });
        });
        nodes.push(kit.group(...rows));
      }
      modelBox.replaceChildren(kit.section('模型', nodes, '不选则使用各工具当前默认模型'));
    }
    function makeToolSelect({ id, value, ariaLabel, onchange }) {
      const options = namedTools(tools);
      const select = el('select', { id, className: 'team-slot-select', 'aria-label': ariaLabel },
        ...options.map(([val, label]) => el('option', { value: val, textContent: label, selected: val === value })));
      select.addEventListener('change', () => onchange(select.value));
      return select;
    }
    function renderRoster() {
      if (!rosterBox) return;
      if (plan.mode !== 'lead' || !hasRoster()) { rosterBox.replaceChildren(); return; }
      const modelsOn = hasModels();
      const nodes = modelsOn ? modelNoticeNodes(renderRoster) : [];
      const makeSlotRow = ({ role, label, tool, model, onTool, onModel, onRemove }) => {
        const toolSel = makeToolSelect({ id: 'team-roster-tool-' + role, value: tool, ariaLabel: label + '工具', onchange: onTool });
        const controls = [toolSel];
        if (modelsOn) {
          controls.push(makeModelSelect({
            id: 'team-roster-model-' + role,
            tool,
            value: model,
            className: 'team-slot-select',
            ariaLabel: label + '模型',
            onchange: onModel
          }));
        }
        return el('div', { className: 'team-slot' },
          el('div', { className: 'team-slot-head' },
            el('span', { className: 'dot ' + (tool === 'claude' ? 'n-cc' : tool === 'agy' ? 'n-ag' : 'n-cx'), textContent: SHORT[tool] || 'AI' }),
            el('b', { textContent: label }),
            onRemove ? el('button', { type: 'button', id: 'team-roster-remove-' + role, className: 'team-slot-remove', 'aria-label': '移除' + label, textContent: '移除', onclick: onRemove }) : ''),
          el('div', { className: 'team-slot-controls' + (modelsOn ? '' : ' single') }, ...controls));
      };
      const rows = [
        makeSlotRow({
          role: 'leader', label: '领队', tool: plan.leader, model: plan.leaderModel,
          onTool: val => { plan.leader = val; plan.leaderModel = ''; renderRoster(); },
          onModel: val => { plan.leaderModel = val; }
        })
      ];
      if (!plan.autoRoster) {
        plan.rosterMembers.forEach((slot, idx) => {
          const role = 'member-' + (idx + 1), label = '成员 ' + (idx + 1);
          rows.push(makeSlotRow({
            role, label, tool: slot.tool, model: slot.model,
            onTool: val => { slot.tool = val; slot.model = ''; renderRoster(); },
            onModel: val => { slot.model = val; },
            onRemove: plan.rosterMembers.length > 1 ? () => { plan.rosterMembers.splice(idx, 1); renderRoster(); } : null
          }));
        });
      }
      nodes.push(kit.group(...rows));
      if (!plan.autoRoster && plan.rosterMembers.length < 3) {
        nodes.push(el('button', {
          type: 'button', id: 'team-roster-add', className: 'team-roster-add',
          textContent: '＋ 添加成员（' + plan.rosterMembers.length + ' / 3）',
          onclick: () => {
            if (plan.rosterMembers.length >= 3) return;
            const usedTools = new Set([plan.leader].concat(plan.rosterMembers.map(s => s.tool)));
            const nextTool = tools.find(t => !usedTools.has(t)) || tools[0];
            plan.rosterMembers.push({ tool: nextTool, model: '' });
            renderRoster();
          }
        }));
      }
      const note = plan.autoRoster
        ? '成员由领队在开始后自行创建和分派（最多 3 个成员）；同账号共用额度'
        : '每个槽位可独立选择工具和模型（可重复使用同一工具，不选模型则使用工具默认，同账号共用额度）';
      rosterBox.replaceChildren(kit.section(plan.autoRoster ? '领队' : '团队成员', nodes, note));
    }
    if (hasModels() && (!modelCatalog || modelError)) {
      loadModels().then(() => { if (beginSheet && !beginSheet.gone) { renderModels(); renderRoster(); } });
    }
    beginSheet = window.RemoteSheet.open({ title: '协作任务', build(p) {
      const modeHint = plan.mode === 'review' ? '一个写，另一个只读审查；审查意见交回去改，直到通过'
        : plan.mode === 'lead' ? '一轮包含领队派工、成员执行并汇报、领队汇总验收；发现问题再进入下一轮'
        : '2–3 个 AI 在独立 Git worktree 副本中并行完成，互不干扰，做完后并排对比改动、任选一个合并';
      const modeKeys = hasV2() && tools.length >= 2 ? Object.keys(MODES) : tools.length >= 2 ? ['review', 'lead'] : ['lead'];
      p.body.append(kit.section('方式', kit.seg(modeKeys.map(key => [key, MODES[key]]), plan.mode, value => { plan.mode = value; p.refresh(); }, { label: '方式', full: true }), modeHint));
      const goal = kit.area({ id: 'team-goal', value: plan.goal, placeholder: '要完成什么', max: 4000, rows: 4, label: '目标' }, value => { plan.goal = value; });
      p.body.append(kit.section('目标', goal));
      p.body.append(kit.section('项目', kit.chips(projects.map(name => [name, name]), { chosen: plan.dir, label: '项目' }, value => { plan.dir = value; })));
      const named = namedTools(tools);
      if (plan.mode === 'review') {
        rosterBox = null;
        p.body.append(kit.section('写', kit.chips(named, { chosen: plan.writer, label: '写的 AI' }, value => {
          plan.writer = value;
          if (plan.reviewer === value && tools.length > 1) { plan.reviewer = tools.find(t => t !== value) || value; p.refresh(); return; }
          renderModels();
        })));
        p.body.append(kit.section('审查', kit.chips(named, { chosen: plan.reviewer, label: '审查的 AI' }, value => { plan.reviewer = value; renderModels(); }), '审查的 AI 以只读方式运行'));
      } else if (plan.mode === 'lead') {
        if (hasRoster()) {
          p.body.append(kit.section('组队方式', kit.seg([['manual', '预设成员'], ['auto', '领队自行组队']], plan.autoRoster ? 'auto' : 'manual', value => {
            plan.autoRoster = value === 'auto';
            p.refresh();
          }, { label: '组队方式', full: true }), plan.autoRoster ? '只需选定领队，成员槽位留给领队按需创建' : '提前定好领队和 1–3 个成员槽位'));
          rosterBox = el('div', { className: 'team-roster-host' });
          p.body.append(rosterBox);
          renderRoster();
        } else {
          rosterBox = null;
          p.body.append(kit.section('领队', kit.chips(named, { chosen: plan.leader, label: '领队' }, value => { plan.leader = value; plan.members = plan.members.filter(t => t !== value); p.refresh(); })));
          p.body.append(kit.section('成员', kit.chips(named.filter(([tool]) => tool !== plan.leader), { chosen: plan.members, multi: true, label: '成员' }, value => { plan.members = value; renderModels(); }), '领队按任务和剩余额度按需派工，避免多人改同一批文件'));
        }
      } else {
        rosterBox = null;
        p.body.append(kit.section('参与比稿的 AI', kit.chips(named, { chosen: plan.compareTools, multi: true, label: '比稿 AI' }, value => { plan.compareTools = value; renderModels(); }), '至少选 2 个不同 AI；要求项目目录是已有提交的 Git 仓库'));
      }
      if (hasModels() && !(plan.mode === 'lead' && hasRoster())) {
        modelBox = el('div', { className: 'team-models-host' });
        p.body.append(modelBox);
        renderModels();
      } else {
        modelBox = null;
      }
      if (plan.mode !== 'compare') {
        if (hasV2() && !(plan.mode === 'lead' && hasRoster() && plan.autoRoster)) {
          const discussToggle = kit.toggle(plan.discuss, on => { plan.discuss = on; }, '先议后写');
          discussToggle.id = 'team-discuss';
          p.body.append(kit.group(kit.row({ icon: 'chat', tint: 'agy', label: '先讨论再动手', detail: '先由各 AI 只读分析方案并汇总共识，再开始改代码', end: discussToggle })));
        }
        p.body.append(kit.section('最多几轮', kit.seg(ROUNDS.map(n => [n, n + ' 轮']), plan.rounds, value => { plan.rounds = value; }, { label: '轮数', full: true })));
      }
      const rules = kit.area({ id: 'team-rules', value: plan.rules, placeholder: '选填：默认 Codex 决策、Claude 写作、Gemini 做 UI，交叉检查优先 Claude/Gemini（自动记住）', max: 1000, rows: 2, label: '分工偏好' }, value => { plan.rules = value; });
      p.body.append(kit.section('分工偏好（选填）', rules));
      const go = kit.go('开始', async () => {
        if (!plan.goal.trim()) { goal.box.focus(); throw new Error('请写下要完成什么'); }
        if (!plan.dir) throw new Error('请选择项目');
        const trimmedRules = plan.rules.trim();
        keepRules(trimmedRules);
        let item;
        if (plan.mode === 'lead' && hasRoster()) {
          const roster = [{ role: 'leader', tool: plan.leader, model: hasModels() ? String(plan.leaderModel || '').trim() : '' }];
          if (!plan.autoRoster) {
            if (!plan.rosterMembers.length) throw new Error('请至少保留一个成员，或改用领队自行组队');
            plan.rosterMembers.slice(0, 3).forEach((slot, idx) => {
              roster.push({ role: 'member-' + (idx + 1), tool: slot.tool, model: hasModels() ? String(slot.model || '').trim() : '' });
            });
          }
          item = { id: plan.id, mode: 'lead', dir: plan.dir, goal: plan.goal.trim(), tools: roster.map(r => r.tool), roster, rounds: plan.rounds };
          if (plan.autoRoster) item.auto_roster = true;
          if (plan.discuss && !plan.autoRoster) item.discuss = true;
        } else {
          const chosen = plan.mode === 'review' ? [plan.writer, plan.reviewer]
            : plan.mode === 'lead' ? [plan.leader].concat(plan.members)
            : plan.compareTools.slice(0, 3);
          if (plan.mode === 'lead' && chosen.length < 2) throw new Error('请至少选一个成员');
          if (plan.mode === 'compare' && chosen.length < 2) throw new Error('并行比稿请至少选 2 个 AI');
          item = { id: plan.id, mode: plan.mode, dir: plan.dir, goal: plan.goal.trim(), tools: chosen, rounds: plan.mode === 'compare' ? 1 : plan.rounds };
          if (hasModels()) {
            const chosenModels = {};
            [...new Set(chosen)].forEach(tool => {
              const val = String(plan.models[tool] || '').trim();
              if (val) chosenModels[tool] = val;
            });
            if (Object.keys(chosenModels).length) item.models = chosenModels;
          }
          if (plan.discuss && plan.mode !== 'compare') item.discuss = true;
        }
        if (trimmedRules) item.rules = trimmedRules;
        const made = await work('team_start', { item });
        p.sheet.close('started'); toast('已开始');
        await load(); page.refresh();
        if (made.team) show(made.team);
      }, { busy: '正在开始…', id: 'team-start' });
      p.foot.append(...go.nodes);
    } }, '协作任务');
  }

  // ---- one: chronological unified stream + inline compare picker + persistent bottom composer
  function show(known) {
    let confirmRemove = false, confirmDiscard = false, sending = false, busyAct = false;
    let currentTeam = (teams || []).find(t => t.id === known.id) || known;
    let draftText = '', targetRole = '', activeCandidate = (currentTeam.tools && currentTeam.tools[0]) || '';
    const expanded = new Set();
    let streamBox = null, footBox = null, bodyScroll = null, activeSheet = null, lastKey = '';

    const keyOf = t => JSON.stringify([t.state, t.phase, !!t.last, t.round, t.rounds, t.note, t.picked, t.tools, t.models || null, t.roster || null, !!t.auto_roster, t.awaited, t.members, t.member_sessions || null, t.failed, t.confirm || null, t._loading || false, t._error || '', (t.log || []).length, (t.candidates || []).map(c => [c.tool, c.stat, c.ready, (c.diff || '').length])]);

    function updateTeamLocal(updated) {
      if (!updated) return;
      currentTeam = updated;
      if (Array.isArray(teams)) {
        const idx = teams.findIndex(t => t.id === updated.id);
        if (idx >= 0) teams[idx] = updated; else teams.unshift(updated);
      }
    }

    async function hydrate() {
      if (!page.usable() || !page.has('teams')) return;
      await load();
      if (!activeSheet || activeSheet.gone) return;
      const latest = (teams || []).find(t => t.id === known.id);
      if (latest) {
        currentTeam = latest;
        lastKey = keyOf(latest);
        renderStream(true);
        renderFoot();
      } else if (currentTeam._loading) {
        currentTeam = Object.assign({}, currentTeam, { _loading: false, _error: failure || '这个协作任务已不存在，请刷新列表' });
        lastKey = keyOf(currentTeam);
        renderStream(false);
        renderFoot();
      }
    }

    function openMember(role, tid, t, ms, tool) {
      if (t) { page.open(tid, t.form || 'chat', { team: currentTeam.id }); return; }
      if (ms && ms.id) {
        if (!page.usable()) { toast('电脑离线，暂时无法查看历史对话'); return; }
        const found = (page.data.sessions || []).find(s => s.id === ms.id && (!ms.tool || s.tool === ms.tool));
        const target = found || { id: ms.id, tool: ms.tool || tool, dir: currentTeam.dir || '', title: roleName(currentTeam, role), live: false, updated: Date.now() };
        page.takeOver(target);
      }
    }

    function renderStream(keepBottom) {
      if (!streamBox) return;
      const team = currentTeam, state = STATES[team.state] || [team.state || '已结束', 'ended'];
      const terminals = (page.data && page.data.terminals) || [];
      const wasAtEnd = keepBottom || (bodyScroll && bodyScroll.scrollHeight - bodyScroll.scrollTop - bodyScroll.clientHeight < 64);

      const parts = [];
      const phaseText = phaseLabel(team.phase);
      const headState = stateInfo(team);
      const showPhaseInSub = phaseText && phaseText !== headState[0];
      const subLine = headState[0]
        + (showPhaseInSub ? ' · ' + phaseText : '')
        + ' · ' + who(team)
        + (team.mode === 'compare' ? '' : ' · 第 ' + (team.round || 1) + ' / ' + (team.rounds || 3) + ' 轮');
      parts.push(kit.who({ icon: 'spark', tint: 'agy', title: team.goal, line: subLine, state: team.confirm ? 'var(--bad)' : team.state === 'running' ? 'var(--good)' : (team.state === 'paused' || team.state === 'choose' || isUnverifiedStop(team)) ? 'var(--busy)' : '' }));
      if (team.mode === 'lead' && team.phase !== 'discuss') {
        const round = team.round || 1, rounds = team.rounds || 3;
        const finalRound = !!team.last || round >= rounds;
        const awaited = team.awaited || [];
        let assignState = '', workState = '', verifyState = '';
        if (team.state === 'done') {
          assignState = workState = verifyState = 'done';
        } else if (isUnverifiedStop(team)) {
          assignState = workState = 'done'; verifyState = 'fail';
        } else if (team.phase === 'verify') {
          assignState = workState = 'done'; verifyState = team.state === 'running' ? 'active' : '';
        } else if (team.state === 'running') {
          if (awaited.some(r => r !== 'leader')) { assignState = 'done'; workState = 'active'; }
          else { assignState = 'active'; }
        } else if (awaited.some(r => r !== 'leader')) {
          assignState = 'done';
        }
        const stepNode = (text, cls) => el('span', { className: 'team-step' + (cls ? ' ' + cls : ''), textContent: text });
        parts.push(el('div', { className: 'team-round-steps', id: 'team-round-steps', ariaLabel: '本轮进度' },
          el('span', { className: 'team-round-tag', textContent: '第 ' + round + ' / ' + rounds + ' 轮' + (finalRound ? ' · 末轮' : '') }),
          stepNode('派工', assignState),
          el('span', { className: 'team-step-sep', ariaHidden: 'true', textContent: '→' }),
          stepNode('成员执行', workState),
          el('span', { className: 'team-step-sep', ariaHidden: 'true', textContent: '→' }),
          stepNode(verifyState === 'fail' ? '验收未通过' : '领队验收', verifyState)));
      }
      if (team.state === 'running' && team.phase === 'verify') {
        const round = team.round || 1, rounds = team.rounds || 3;
        const finalTag = (team.last || round >= rounds) ? '（末轮验收）' : '';
        const prefix = '第 ' + round + ' / ' + rounds + ' 轮 · 领队验收' + finalTag;
        const verifyText = team.note ? (team.note.includes('领队验收') ? team.note : prefix + '：' + team.note) : prefix + '：正在核对成员改动与结果';
        const banner = kit.summary(verifyText, '', 'spark');
        banner.classList.add('team-verify');
        parts.push(banner);
      } else if (team.note) {
        parts.push(kit.summary(team.note, team.state === 'failed' || team.state === 'paused' || isUnverifiedStop(team) ? 'bad' : '', team.state === 'done' ? 'tick' : 'clock'));
      }

      // Compact member chips (clear role + tool + selected/default model) + inline confirmation alerts
      const roster = rosterOf(team);
      if (team.auto_roster) {
        const assigned = roster.filter(r => r.role !== 'leader').length;
        parts.push(el('p', {
          className: 'team-roster-meta', id: 'team-auto-roster-status',
          textContent: assigned ? '领队自行组队 · 已创建 ' + assigned + ' 个成员（同账号共用额度）'
            : team.state === 'running' ? '领队自行组队 · 领队正在按任务创建成员…' : '领队自行组队 · 尚未创建成员'
        }));
      }
      const memberKeys = memberRolesOf(team);
      const showModels = hasModels() || !!(team.models && Object.keys(team.models).length) || roster.some(r => r.model);
      const pendingAsks = [];
      if (memberKeys.length) {
        const chips = memberKeys.map(role => {
          const tid = (team.members || {})[role] || '', t = tid ? terminals.find(one => one.id === tid) : null;
          const ms = (team.member_sessions || {})[role] || null;
          const active = (team.awaited || []).includes(role);
          const asking = t && (t.ask || (page.phaseOf(t) === 'confirm' && (t.asks || []).join('\n')));
          if (asking) pendingAsks.push({ role, tid, ask: asking });
          const tool = toolOf(team, role);
          const failed = team.failed && (team.failed[role] || team.failed[tool]);
          const modelId = modelOf(team, role);
          const canOpen = !!t || !!(ms && ms.id);
          const statusText = asking ? '等你确认' : failed ? '已中断' : active ? (team.phase === 'verify' && role === 'leader' ? '验收中' : '进行中') : t && page.running(t) ? '可查看' : t ? '已结束' : (ms && ms.id) ? '历史记录' : '待开始';
          return el('button', {
            type: 'button', id: 'member-' + role.replace(/[^a-z0-9_-]/gi, '_'),
            className: 'team-chip' + (active ? ' busy' : '') + (asking ? ' ask' : '') + (failed ? ' fail' : ''),
            disabled: !canOpen,
            onclick: () => openMember(role, tid, t, ms, tool)
          },
            el('span', { className: 'dot ' + (tool === 'claude' ? 'n-cc' : tool === 'agy' ? 'n-ag' : 'n-cx'), textContent: SHORT[tool] || 'AI' }),
            el('span', { className: 'name', textContent: roleName(team, role) }),
            showModels ? el('span', { className: 'model', textContent: modelLabel(tool, modelId) }) : '',
            el('small', { textContent: statusText }));
        });
        parts.push(el('div', { className: 'team-members', ariaLabel: '成员对话' }, ...chips));
      }

      if (pendingAsks.length) {
        pendingAsks.forEach(item => {
          parts.push(el('div', { className: 'team-ask-banner' },
            el('div', { className: 'copy' }, el('b', { textContent: roleName(team, item.role) + ' 正在等你确认' }), el('small', { textContent: item.ask })),
            el('button', { type: 'button', className: 'go', onclick: () => page.open(item.tid, 'chat', { team: team.id }) }, '去处理')));
        });
      }

      if (team._loading) {
        parts.push(kit.note('正在读取协作详情…'));
      } else if (team._error) {
        parts.push(el('div', { className: 'team-models-error', role: 'alert' },
          el('span', { textContent: team._error }),
          el('button', { type: 'button', className: 'team-models-retry', textContent: '重试', onclick: () => { currentTeam = Object.assign({}, currentTeam, { _loading: true, _error: '' }); renderStream(false); hydrate(); } })));
      }

      // Parallel compare candidates viewer & merge picker
      if (team.mode === 'compare' && Array.isArray(team.candidates) && team.candidates.length) {
        if (!team.candidates.some(c => c.tool === activeCandidate)) activeCandidate = team.candidates[0].tool;
        const chosen = team.candidates.find(c => c.tool === activeCandidate) || team.candidates[0];
        const tabs = team.candidates.map(c => el('button', {
          type: 'button', id: 'compare-tab-' + c.tool,
          className: 'team-tab' + (c.tool === chosen.tool ? ' on' : '') + (team.picked === c.tool ? ' picked' : ''),
          onclick: () => { activeCandidate = c.tool; renderStream(false); }
        }, (TOOLS[c.tool] || c.tool) + (team.picked === c.tool ? ' · 已采纳' : '')));

        const cardBody = [
          el('div', { className: 'team-compare-head' },
            el('b', { textContent: TOOLS[chosen.tool] || chosen.tool }),
            el('span', { className: 'stat', textContent: chosen.stat || ((team.awaited || []).includes(chosen.tool) ? '正在完成…' : '暂无改动') })),
          chosen.summary ? el('p', { className: 'team-compare-summary', textContent: chosen.summary }) : '',
          chosen.diff ? el('pre', { className: 'team-diff', textContent: chosen.diff }) : el('p', { className: 'team-diff-empty', textContent: (team.awaited || []).includes(chosen.tool) ? '正在独立 worktree 中生成方案…' : '没有文件改动' })
        ];

        const canPick = team.state !== 'running' && chosen.ready;
        if (canPick) {
          const pickRow = el('div', { className: 'team-pick-row' },
            el('button', {
              type: 'button', id: 'team-pick-winner', className: 'rs-go',
              onclick: async () => {
                if (busyAct) return;
                busyAct = true;
                try {
                  const res = await work('team_pick', { team: team.id, tool: chosen.tool });
                  updateTeamLocal(res.team);
                  toast('已合并 ' + (TOOLS[chosen.tool] || chosen.tool) + ' 的方案');
                  await load(); currentTeam = (teams || []).find(t => t.id === known.id) || currentTeam;
                  renderStream(true); renderFoot(); page.refresh();
                } catch (err) { toast(err.message); }
                finally { busyAct = false; }
              }
            }, '合并 ' + (TOOLS[chosen.tool] || chosen.tool) + ' 方案到主目录'),
            el('button', {
              type: 'button', id: 'team-pick-discard', className: 'rs-go quiet',
              onclick: async () => {
                if (busyAct) return;
                if (!confirmDiscard) { confirmDiscard = true; toast('再点一次放弃全部方案并清理工作区'); setTimeout(() => { confirmDiscard = false; }, 4000); return; }
                busyAct = true; confirmDiscard = false;
                try {
                  const res = await work('team_pick', { team: team.id, tool: '' });
                  updateTeamLocal(res.team);
                  toast('已放弃全部候选方案');
                  await load(); currentTeam = (teams || []).find(t => t.id === known.id) || currentTeam;
                  renderStream(true); renderFoot(); page.refresh();
                } catch (err) { toast(err.message); }
                finally { busyAct = false; }
              }
            }, '放弃全部'));
          cardBody.push(pickRow);
        }
        parts.push(el('section', { className: 'team-compare', id: 'team-compare' }, el('div', { className: 'team-tabs', role: 'tablist' }, ...tabs), el('div', { className: 'team-compare-card' }, ...cardBody)));
      }

      // Unified chronological timeline (oldest -> newest)
      const log = team.log || [];
      if (log.length) {
        const list = log.map((step, idx) => {
          const isUser = step.who === 'user', isMeta = step.who === '目标' || step.who === '结果' || step.who === '切换';
          const tool = isUser ? 'user' : isMeta ? '' : toolOf(team, step.who);
          const key = idx + ':' + step.at;
          const long = (step.text || '').length > 420 || (step.text || '').split('\n').length > 10;
          const open = !long || expanded.has(key);
          const bodyNode = el('p', { className: 'body' + (open ? '' : ' clamp'), textContent: step.text });
          const toggleBtn = long ? el('button', {
            type: 'button', className: 'more-btn', textContent: open ? '收起' : '展开全文',
            onclick: () => { if (expanded.has(key)) expanded.delete(key); else expanded.add(key); renderStream(false); }
          }) : '';
          const stepModel = (!isUser && !isMeta && showModels) ? modelOf(team, step.who) : '';
          return el('li', { className: 'team-msg' + (isUser ? ' mine' : isMeta ? ' meta' : '') },
            el('div', { className: 'head' },
              !isMeta ? el('span', { className: 'badge ' + (isUser ? 'n-me' : tool === 'claude' ? 'n-cc' : tool === 'agy' ? 'n-ag' : 'n-cx'), textContent: SHORT[tool] || 'AI' }) : '',
              el('b', { textContent: roleName(team, step.who) }),
              stepModel ? el('span', { className: 'model', textContent: modelLabel(tool, stepModel) }) : '',
              el('span', { className: 'at', textContent: page.ago(step.at) })),
            bodyNode,
            toggleBtn);
        });
        parts.push(el('ol', { className: 'team-stream', id: 'team-stream' }, ...list));
      }

      streamBox.replaceChildren(...parts);
      if (wasAtEnd && bodyScroll) requestAnimationFrame(() => { bodyScroll.scrollTop = bodyScroll.scrollHeight; });
    }

    function openSwapSheet() {
      if (!activeSheet) return;
      const team = currentTeam, tools = able(), roster = rosterOf(team);
      const isRosterLead = team.mode === 'lead' && (roster.length > 0 || hasRoster());
      if (isRosterLead ? tools.length < 1 : tools.length < 2) return toast(isRosterLead ? '需要电脑上至少安装一个可用 AI' : '需要电脑上至少安装两个可用 AI');
      const roleChoices = team.mode === 'lead' && roster.length
        ? roster.map(r => [r.role, roleName(team, r.role)])
        : team.mode === 'review'
        ? [['writer', '写手 · ' + (TOOLS[team.tools[0]] || team.tools[0])], ['reviewer', '审查 · ' + (TOOLS[team.tools[1]] || team.tools[1])]]
        : [['leader', '领队 · ' + (TOOLS[team.tools[0]] || team.tools[0])]].concat(team.tools.slice(1).map(t => [t, '成员 · ' + (TOOLS[t] || t)]));
      const currentOf = r => toolOf(team, r);
      const failedAwaited = (team.awaited || []).find(r => team.failed && (team.failed[r] || team.failed[currentOf(r)]));
      const initialRole = failedAwaited || ((team.awaited || []).find(r => roleChoices.some(([k]) => k === r))) || roleChoices[0][0];
      const initialTool = tools.find(t => t !== currentOf(initialRole)) || tools[0];
      const keptModels = Object.assign({}, team.models || {});
      const modelForChoice = (role, tool) => tool === currentOf(role) ? modelOf(team, role) : (keptModels[tool] || '');
      const state = { role: initialRole, tool: initialTool, model: modelForChoice(initialRole, initialTool), models: keptModels, note: '' };
      let swapModelBox = null;
      function renderSwapModel() {
        if (!swapModelBox) return;
        if (!hasModels()) { swapModelBox.replaceChildren(); return; }
        const nodes = [];
        if (modelLoading && !modelCatalog) nodes.push(kit.note('正在读取可用模型…'));
        if (modelError) {
          nodes.push(el('div', { className: 'team-models-error', role: 'alert' },
            el('span', { textContent: '模型列表加载失败：' + modelError + '（仍可使用工具默认模型）' }),
            el('button', {
              type: 'button', id: 'team-swap-models-retry', className: 'team-models-retry',
              textContent: modelLoading ? '读取中…' : '重试', disabled: modelLoading,
              onclick: () => { loadModels(true).then(renderSwapModel); renderSwapModel(); }
            })));
        }
        const sel = makeModelSelect({
          id: 'team-swap-model',
          tool: state.tool,
          value: state.model,
          ariaLabel: '接手 AI 的模型',
          onchange: val => { state.model = val; state.models[state.tool] = val; }
        });
        nodes.push(kit.group(kit.row({ icon: state.tool, tint: state.tool, label: '模型', detail: TOOLS[state.tool] || state.tool, end: sel })));
        swapModelBox.replaceChildren(kit.section('接手模型', nodes, '不选则使用该工具默认模型'));
      }
      if (hasModels() && (!modelCatalog || modelError)) {
        loadModels().then(() => { if (activeSheet && !activeSheet.gone) renderSwapModel(); });
      }
      activeSheet.push({ title: '换 AI 接手', build(p) {
        p.body.append(kit.section('要替换或转交的角色', kit.chips(roleChoices, { chosen: state.role, label: '替换角色' }, val => {
          state.role = val;
          if (!isRosterLead && state.tool === currentOf(val)) {
            state.tool = tools.find(t => t !== currentOf(val)) || tools[0];
          }
          state.model = modelForChoice(state.role, state.tool);
          p.refresh();
        })));
        const choices = namedTools(isRosterLead ? tools : tools.filter(t => t !== currentOf(state.role)));
        p.body.append(kit.section('交给哪个 AI 继续', kit.chips(choices, { chosen: state.tool, label: '接手 AI' }, val => {
          state.tool = val;
          state.model = modelForChoice(state.role, val);
          renderSwapModel();
        }), '会带上当前工作区改动和原任务上下文继续执行'));
        if (hasModels()) {
          swapModelBox = el('div', { className: 'team-models-host' });
          p.body.append(swapModelBox);
          renderSwapModel();
        } else {
          swapModelBox = null;
        }
        const noteBox = kit.area({ id: 'team-swap-note', value: state.note, placeholder: '选填：给接手 AI 的补充说明', max: 1000, rows: 2, label: '补充说明' }, val => { state.note = val; });
        p.body.append(kit.section('补充说明（选填）', noteBox));
        const go = kit.go('交给它继续', async () => {
          const payload = { team: currentTeam.id, role: state.role, tool: state.tool };
          if (hasModels() && state.model) payload.model = state.model;
          if (state.note.trim()) payload.note = state.note.trim();
          const res = await work('team_swap', payload);
          updateTeamLocal(res.team);
          lastKey = keyOf(currentTeam);
          toast('已交给 ' + (TOOLS[state.tool] || state.tool) + ' 接手');
          p.sheet.goBack();
          page.refresh();
        }, { busy: '正在切换…', id: 'team-swap-go' });
        p.foot.append(...go.nodes);
      } });
    }

    function targetOptions(team) {
      const first = ['', team.mode === 'compare' ? '全部候选' : '自动分配'];
      const roster = rosterOf(team);
      if (team.mode === 'lead' && roster.length) {
        return [first].concat(roster.map(r => [r.role, roleName(team, r.role)]));
      }
      return [first].concat(
        (team.mode === 'review' ? [['writer', '写手 · ' + (TOOLS[team.tools[0]] || team.tools[0])], ['reviewer', '审查 · ' + (TOOLS[team.tools[1]] || team.tools[1])]]
          : team.mode === 'lead' ? [['leader', '领队 · ' + (TOOLS[team.tools[0]] || team.tools[0])]].concat((team.tools || []).slice(1).map(t => [t, TOOLS[t] || t]))
          : (team.tools || []).map(t => [t, TOOLS[t] || t]))
      );
    }

    function renderFoot() {
      if (!footBox) return;
      const team = currentTeam, v2 = hasV2();
      const canSay = v2 && team.state !== 'failed' && !(team.mode === 'compare' && team.state === 'done');
      const existingBox = footBox.querySelector('#team-say-input');
      const hadFocus = existingBox && document.activeElement === existingBox;
      const isRosterLead = team.mode === 'lead' && (rosterOf(team).length > 0 || hasRoster());

      const actions = [];
      if (v2 && team.mode !== 'compare' && (team.state === 'running' || team.state === 'paused') && (isRosterLead ? able().length >= 1 : able().length >= 2)) {
        actions.push(el('button', { type: 'button', id: 'team-swap', className: 'team-act', onclick: openSwapSheet }, '换 AI 接手'));
      }
      if (team.state === 'running') {
        actions.push(el('button', {
          type: 'button', id: 'team-stop', className: 'team-act danger',
          onclick: async () => {
            if (busyAct) return; busyAct = true;
            try { await work('team_stop', { team: team.id }); await load(); currentTeam = (teams || []).find(t => t.id === known.id) || Object.assign({}, team, { state: 'stopped' }); renderStream(false); renderFoot(); page.refresh(); toast('已停止'); }
            catch (err) { toast(err.message); } finally { busyAct = false; }
          }
        }, '停止'));
      }
      if (team.state === 'paused') {
        actions.push(el('button', {
          type: 'button', id: 'team-resume', className: 'team-act primary',
          onclick: async () => {
            if (busyAct) return; busyAct = true;
            try { await work('team_resume', { team: team.id }); await load(); currentTeam = (teams || []).find(t => t.id === known.id) || Object.assign({}, team, { state: 'running' }); renderStream(false); renderFoot(); page.refresh(); toast('已继续'); }
            catch (err) { toast(err.message); } finally { busyAct = false; }
          }
        }, '继续'));
      }
      if (team.state !== 'running') {
        const delBtn = el('button', {
          type: 'button', id: 'team-remove', className: 'team-act danger', textContent: confirmRemove ? '再按一次删除' : '删除',
          onclick: async () => {
            if (busyAct) return;
            if (!confirmRemove) { confirmRemove = true; delBtn.textContent = '再按一次删除'; setTimeout(() => { confirmRemove = false; if (delBtn.isConnected) delBtn.textContent = '删除'; }, 4000); return; }
            busyAct = true;
            try { await work('team_remove', { team: team.id }); await load(); if (activeSheet) activeSheet.close('removed'); page.refresh(); toast('已删除'); }
            catch (err) { toast(err.message); } finally { busyAct = false; }
          }
        });
        actions.push(delBtn);
      }

      // Keep composer DOM stable when already present & canSay unchanged so typing never loses focus on poll
      let bar = footBox.querySelector('.team-act-bar');
      if (!bar) { bar = el('div', { className: 'team-act-bar' }); footBox.prepend(bar); }
      bar.replaceChildren(...actions);
      bar.hidden = !actions.length;

      let hint = footBox.querySelector('.team-upgrade-hint');
      if (!v2 && team.state !== 'failed') {
        if (!hint) { hint = el('p', { className: 'team-upgrade-hint', textContent: '当前电脑端版本较旧，更新电脑端后支持中途补充说明、换 AI 接手与继续下一轮' }); footBox.append(hint); }
      } else if (hint) { hint.remove(); }

      let form = footBox.querySelector('#team-say-form');
      if (!canSay) { if (form) form.remove(); return; }

      const placeholder = team.state === 'running' ? '随时插话补充方向或纠正…'
        : team.state === 'paused' ? '补充说明并继续…'
        : team.state === 'choose' ? '对候选方案提修改意见（默认发给全部 AI）…'
        : '发一条新要求，接着做下一轮…';

      const targets = targetOptions(team);
      if (form) {
        const input = form.querySelector('#team-say-input');
        if (input && input.placeholder !== placeholder) input.placeholder = placeholder;
        const sel = form.querySelector('#team-say-target');
        const sig = JSON.stringify(targets);
        if (sel && sel.dataset.sig !== sig) {
          sel.dataset.sig = sig;
          if (!targets.some(([v]) => v === targetRole)) targetRole = '';
          sel.replaceChildren(...targets.map(([val, label]) => el('option', { value: val, textContent: label, selected: val === targetRole })));
        }
        if (hadFocus && input) input.focus();
        return;
      }

      const input = el('textarea', {
        id: 'team-say-input', rows: 1, maxLength: 4000, value: draftText, placeholder, ariaLabel: '向协作任务发消息'
      });
      const grow = () => { input.style.height = 'auto'; input.style.height = Math.min(120, Math.max(38, input.scrollHeight)) + 'px'; };
      input.addEventListener('input', () => { draftText = input.value; grow(); });
      input.addEventListener('keydown', event => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) { event.preventDefault(); form.requestSubmit(); } });

      // Optional target selector for interjecting to a specific member
      const targetSel = el('select', { id: 'team-say-target', className: 'team-target', ariaLabel: '发送对象' },
        ...targets.map(([val, label]) => el('option', { value: val, textContent: label, selected: val === targetRole })));
      targetSel.dataset.sig = JSON.stringify(targets);
      targetSel.addEventListener('change', () => { targetRole = targetSel.value; });

      const sendBtn = el('button', { type: 'submit', id: 'team-say-send', className: 'team-send', textContent: '发送' });
      form = el('form', { id: 'team-say-form', className: 'team-composer' },
        el('div', { className: 'team-composer-top' }, targetSel),
        el('div', { className: 'team-composer-row' }, input, sendBtn));
      form.addEventListener('submit', async event => {
        event.preventDefault();
        const text = input.value.trim();
        if (!text || sending) return;
        sending = true; sendBtn.disabled = true; sendBtn.textContent = '发送中';
        try {
          const payload = { team: currentTeam.id, text };
          if (targetRole) payload.target = targetRole;
          const res = await work('team_say', payload);
          draftText = ''; input.value = ''; grow();
          updateTeamLocal(res.team);
          lastKey = keyOf(currentTeam);
          renderStream(true); renderFoot(); page.refresh();
          toast(currentTeam.state === 'running' ? '已发给协作任务' : '已发送');
        } catch (err) { toast(err.message); }
        finally { sending = false; sendBtn.disabled = false; sendBtn.textContent = '发送'; }
      });
      footBox.append(form);
      grow();
    }

    const sheet = window.RemoteSheet.open({ title: (MODES[known.mode] || '协作') + ' · ' + known.dir, build(p) {
      activeSheet = p.sheet;
      currentTeam = (teams || []).find(t => t.id === known.id) || currentTeam;
      lastKey = keyOf(currentTeam);
      bodyScroll = p.sheet.body;
      streamBox = el('div', { className: 'team-view' });
      p.body.append(streamBox);
      footBox = p.foot;
      renderStream(true);
      renderFoot();
    } }, '协作任务');

    if (currentTeam._loading || !teams || page.tab !== 'together') hydrate();
    if (hasModels() && !modelCatalog && !modelLoading) {
      loadModels().then(() => { if (!sheet.gone) renderStream(false); });
    }

    const timer = setInterval(async () => {
      if (sheet.gone) { clearInterval(timer); return; }
      if (currentTeam.state !== 'running' && currentTeam.state !== 'choose') return;
      await load();
      if (sheet.gone) return;
      const latest = (teams || []).find(t => t.id === known.id);
      if (!latest) return;
      const key = keyOf(latest);
      if (key !== lastKey) {
        currentTeam = latest;
        lastKey = key;
        renderStream(false);
        renderFoot();
        page.refresh();
      }
    }, 3500);
    if (sheet && sheet.result && sheet.result.finally) sheet.result.finally(() => clearInterval(timer));
  }

  function open(id, fallback) {
    if (!id) return;
    const found = (teams || []).find(t => t.id === id);
    if (found) { show(found); return; }
    const stub = Object.assign({
      id, dir: '', goal: '协作任务', mode: 'review', state: 'running', phase: '', note: '',
      tools: [], models: {}, awaited: [], members: {}, log: [], round: 1, rounds: 3
    }, fallback || {}, { _loading: true, _error: '' });
    show(stub);
  }

  // ---- on 协作: its card for each, and one to begin another
  function card(team) {
    const state = stateInfo(team);
    const meta = el('span', { className: 'meta' },
      el('span', { className: 'pill ' + state[1], textContent: state[0] }),
      el('span', { textContent: (MODES[team.mode] || team.mode) + (team.auto_roster ? ' · 自组' : '') + (team.discuss ? ' · 先议' : '') + ' · ' + who(team) }),
      team.mode === 'compare' ? '' : el('span', { textContent: '第 ' + team.round + ' / ' + team.rounds + ' 轮' }));
    return el('li', { className: 'card rule' + (team.state === 'running' || team.state === 'paused' || team.state === 'choose' ? '' : ' off') }, el('button', { type: 'button', className: 'open', onclick: () => show(team) },
      el('span', { className: 'tool plain', html: TEAM }),
      el('span', { className: 'text' }, el('b', { textContent: team.goal }), meta, team.note ? el('span', { className: 'peek', textContent: team.note }) : ''),
      el('span', { className: 'chev', html: page.ICON.chev })));
  }
  function cards() {
    return (teams || []).map(card).concat(el('li', { className: 'card add' }, el('button', { type: 'button', className: 'open', id: 'team-new', onclick: begin, html: page.ICON.plus + '<span>新的协作任务</span>' })));
  }
  const status = () => failure ? '读取失败，请重试；显示上次读取的任务。' : teams === null ? '正在读取协作任务…' : teams.length ? '' : '还没有协作任务。支持写审循环、领队分工、先议后写，或在独立 worktree 中并行比稿。';
  const count = () => teams && teams.length ? teams.filter(t => t.state === 'running' || t.state === 'paused' || t.state === 'choose').length + ' 个进行中 / ' + teams.length + ' 个' : '';

  window.RemoteTeams = { TEAM, load, loadModels, begin, show, open, cards, status, count, get teams() { return teams; }, get failure() { return failure; } };
})();
