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
  const MODES = { review: '写审', lead: '分工协作', compare: '比稿' }, ROUNDS = [1, 2, 3, 5, 10];
  const STATES = { running: ['进行中', 'starting'], choose: ['待选方案', 'confirm'], paused: ['已暂停', 'confirm'], done: ['已完成', 'ended'], stopped: ['已停止', 'ended'], failed: ['没有开始', 'ended'] };
  const ROLES = { writer: '写手', reviewer: '审查', leader: '领队', user: '你' };
  const SHORT = { claude: 'CC', codex: 'CX', agy: 'AG', user: '你' };
  const defaults = window.RemoteTeamDefaults;
  const EFFORT_LABELS = (defaults && defaults.EFFORT_LABELS) || { none: '无（none）', minimal: '极低（minimal）', low: '低（low）', medium: '中（medium）', high: '高（high）', xhigh: '极高（xhigh）', max: '最高（max）', ultra: '极致（ultra）' };
  const effortTag = level => defaults ? defaults.effortTag(level) : '思考 · ' + ((EFFORT_LABELS[String(level || '').trim().toLowerCase()] || '').replace(/（.*）/, '') || '默认');
  const MENU_ICON = '<svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true"><circle cx="5" cy="12" r="1.8" fill="currentColor"/><circle cx="12" cy="12" r="1.8" fill="currentColor"/><circle cx="19" cy="12" r="1.8" fill="currentColor"/></svg>';
  // the colour of the state dot beside the task, the same as the header's badge (badgeInfo)
  const TONE_DOT = { busy: 'var(--busy-dot, var(--busy))', confirm: 'var(--bad)', good: 'var(--good-dot, var(--good))', starting: 'var(--accent)', ended: 'var(--muted)' };
  let teams = null, failure = '', loading = null, activeSync = null, loadEpoch = 0;
  let modelCatalog = null, modelError = '', modelLoading = false, modelPromise = null;
  const mdCache = new Map();
  const isRichReady = () => !!(window.marked && window.DOMPurify);
  function renderRichBody(text, className) {
    if (page.markdownNode && isRichReady()) {
      const node = page.markdownNode(text, mdCache);
      node.className = className + ' ' + node.className;
      return node;
    }
    return el('p', { className, textContent: text || '' });
  }

  const work = (action, more) => api('api/work', Object.assign({ id: newId(), action }, more));
  const hasV2 = () => page.has('teams-v2');
  const hasModels = () => page.has('teams-models');
  const hasRoster = () => page.has('teams-roster');
  const hasEffort = () => page.has('teams-effort');
  const hasFlexRounds = () => page.has('teams-flex-rounds');
  const roundsOf = team => (team && team.rounds === 0) ? 0 : (team && team.rounds) || 3;
  const isFlexRounds = team => !!team && team.mode === 'lead' && team.rounds === 0;
  const roundBadgeText = team => {
    if (!team || (team._loading && team.rounds === undefined)) return '';
    const round = team.round || 1;
    return isFlexRounds(team) ? '第 ' + round + ' 轮 · 自由轮数' : '第 ' + round + ' / ' + roundsOf(team) + ' 轮';
  };
  const able = () => ['claude', 'codex', 'agy'].filter(tool => page.chatOk(tool));
  const PHASE_LABELS = new Map([['discuss', '讨论方案'], ['decision', '领队处理中'], ['verify', '领队验收']]);
  const phaseLabel = phase => PHASE_LABELS.get(phase) || '';
  const isUnverifiedStop = team => !!team && team.state === 'stopped' && team.mode === 'lead' && (team.phase === 'verify' || !!team.last);
  const stateInfo = team => {
    if (!team) return ['已结束', 'ended'];
    if (team.confirm) return ['等你确认', 'confirm'];
    const runningPhase = team.state === 'running' && phaseLabel(team.phase);
    if (runningPhase) return [runningPhase, 'busy'];
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
    return m ? '执行成员 ' + m[1] : '';
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
  const effortOf = (team, role) => {
    const entry = rosterEntry(team, role);
    if (entry) return String(entry.effort || '').trim().toLowerCase();
    const tool = toolOf(team, role);
    return String((team && team.efforts && team.efforts[tool]) || '').trim().toLowerCase();
  };
  const roleName = (team, role) => {
    if (role === 'user') return '你';
    if (role === '目标' || role === '结果' || role === '切换' || role === '验收') return role;
    if (role.startsWith('discuss:')) {
      const inner = role.slice(8), tool = toolOf(team, role), title = roleTitle(inner);
      return '讨论 · ' + (title ? title + ' · ' : '') + (TOOLS[tool] || tool);
    }
    const tool = toolOf(team, role);
    if (team && team.mode === 'compare') return '候选 · ' + (TOOLS[tool] || tool);
    const title = roleTitle(role);
    return (title ? title + ' · ' : '执行成员 · ') + (TOOLS[tool] || tool);
  };
  const who = team => {
    const roster = rosterOf(team);
    if (team && team.mode === 'lead' && roster.length) {
      const leader = roster.find(r => r.role === 'leader') || roster[0];
      const rest = roster.filter(r => r !== leader).map(r => r.tool);
      const leadName = TOOLS[leader.tool] || leader.tool || '';
      return rest.length ? leadName + ' 带 ' + summarizeTools(rest) : leadName + (team.auto_roster ? ' 自动组队' : '');
    }
    const list = (team && team.tools) || [];
    if (team && team.mode === 'review') return (TOOLS[list[0]] || list[0] || '') + ' 写 · ' + (TOOLS[list[1]] || list[1] || '') + ' 审';
    if (team && team.mode === 'compare') return list.map(t => TOOLS[t] || t).join(' vs ');
    return (TOOLS[list[0]] || list[0] || '') + (list.length > 1 ? ' 带 ' + summarizeTools(list.slice(1)) : team && team.auto_roster ? ' 自动组队' : '');
  };

  const normalizeModels = raw => defaults ? defaults.normalizeModels(raw) : {};
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
  function effortLabel(level) {
    const clean = String(level || '').trim().toLowerCase();
    if (!clean) return '工具默认';
    return EFFORT_LABELS[clean] || clean;
  }
  function modelEntry(tool, modelId) {
    const list = (modelCatalog && modelCatalog[tool]) || [];
    const clean = String(modelId || '').trim();
    if (clean) return list.find(item => item.id === clean) || null;
    return list.find(item => item.isDefault) || null;
  }
  function effortListFor(tool, modelId) {
    const item = modelEntry(tool, modelId);
    return item && Array.isArray(item.efforts) ? item.efforts : [];
  }
  function reconcileModel(tool, modelId) {
    const clean = String(modelId || '').trim();
    if (!clean || !hasModels()) return '';
    if (!modelCatalog) return clean;
    const list = modelCatalog[tool] || [];
    return list.some(item => item.id === clean) ? clean : '';
  }
  function reconcileEffort(tool, modelId, effortId) {
    const clean = String(effortId || '').trim().toLowerCase();
    if (!clean || !hasEffort()) return '';
    if (!modelCatalog) return clean;
    return effortListFor(tool, modelId).includes(clean) ? clean : '';
  }
  function modelOptions(tool, selected) {
    const list = (modelCatalog && modelCatalog[tool]) || [];
    const options = [['', '工具默认']].concat(list.map(item => [item.id, item.label]));
    const clean = String(selected || '').trim();
    if (clean && !list.some(item => item.id === clean)) options.push([clean, clean]);
    return options;
  }
  function effortOptions(tool, modelId, selected) {
    const list = effortListFor(tool, modelId);
    const options = [['', '工具默认']].concat(list.map(level => [level, effortLabel(level)]));
    const clean = String(selected || '').trim().toLowerCase();
    if (clean && !list.includes(clean)) options.push([clean, effortLabel(clean)]);
    return options;
  }
  function makeModelSelect({ id, tool, value, ariaLabel, className, disabled, onchange }) {
    const options = disabled ? [['', '工具默认']] : modelOptions(tool, value);
    const select = el('select', { id, className: className || 'team-model-select', 'aria-label': ariaLabel, disabled: !!disabled },
      ...options.map(([val, label]) => el('option', { value: val, textContent: label, selected: val === (disabled ? '' : value) })));
    if (!disabled) select.addEventListener('change', () => onchange(select.value));
    return select;
  }
  function makeEffortSelect({ id, tool, model, value, ariaLabel, className, disabled, onchange }) {
    const options = disabled ? [['', '工具默认']] : effortOptions(tool, model, value);
    const select = el('select', { id, className: className || 'team-slot-select', 'aria-label': ariaLabel, disabled: !!disabled },
      ...options.map(([val, label]) => el('option', { value: val, textContent: label, selected: val === (disabled ? '' : value) })));
    if (!disabled) select.addEventListener('change', () => onchange(select.value));
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

  // A removed task leaves the list at once; a reading begun before it was removed is not taken.
  function forgetTeam(id) {
    loadEpoch++;
    if (Array.isArray(teams)) teams = teams.filter(team => team && team.id !== id);
  }
  // Resolves true only when the computer's list was read and taken: a task missing from it is gone. A reading that
  // failed, was not possible, or was overtaken by a removal leaves the list as it was and says nothing about any task.
  function load() {
    if (!page.has('teams') || !page.usable()) return Promise.resolve(false);
    if (!loading) loading = read().finally(() => { loading = null; });
    return loading;
  }
  async function read() {
    const epoch = loadEpoch;
    try {
      const latest = (await work('teams')).teams || [];
      failure = '';
      if (epoch !== loadEpoch) return false;
      teams = latest;
      return true;
    }
    catch (error) { failure = error.message; return false; }
  }

  const used = tool => {
    const q = ((page.data.device || {}).quotas || []).find(one => one.tool === tool);
    const all = (q && q.windows) || [], main = all.filter(w => !w.group), pool = main.length ? main : all;
    const most = pool.length ? Math.max(...pool.map(w => w.used)) : null;
    return most === null ? '' : ' · ' + Math.round(most) + '%';
  };
  const namedTools = tools => tools.map(tool => [tool, TOOLS[tool] + used(tool)]);

  const RULES_KEY = 'remotecli.team.rules';
  const LEADER_KEY = 'remotecli.team.leader';
  const savedRules = () => { try { return localStorage.getItem(RULES_KEY) || ''; } catch { return ''; } };
  const keepRules = text => { try { if (text) localStorage.setItem(RULES_KEY, text); else localStorage.removeItem(RULES_KEY); } catch { } };
  const savedLeader = () => {
    try {
      const raw = JSON.parse(localStorage.getItem(LEADER_KEY) || 'null');
      return raw && typeof raw === 'object' ? raw : null;
    } catch { return null; }
  };
  const keepLeader = prefs => {
    try {
      if (prefs && prefs.tool) localStorage.setItem(LEADER_KEY, JSON.stringify({ tool: prefs.tool, model: prefs.model || '', effort: prefs.effort || '', autoRoster: !!prefs.autoRoster }));
      else localStorage.removeItem(LEADER_KEY);
    } catch { }
  };

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
    const savedPrefs = defaults ? defaults.readPrefs() : { models: {}, efforts: {} };
    const defaultModelFor = tool => (hasModels() && defaults) ? defaults.resolveModel(tool, modelCatalog, savedPrefs) : '';
    const defaultEffortFor = (tool, modelId) => (hasEffort() && defaults) ? defaults.resolveEffort(tool, modelId, modelCatalog, savedPrefs) : '';

    const defaultWriter = tools.includes('claude') ? 'claude' : tools[0];
    const defaultReviewer = (tools.includes('agy') && defaultWriter !== 'agy') ? 'agy' : (tools.find(t => t !== defaultWriter) || tools[0]);
    const restored = savedLeader();
    const restoredTool = restored && tools.includes(restored.tool) ? restored.tool : '';
    const defaultLeader = restoredTool || (tools.includes('codex') ? 'codex' : tools[0]);
    const defaultMember1 = tools.find(t => t !== defaultLeader) || tools[0];
    const defaultMember2 = tools.find(t => t !== defaultLeader && t !== defaultMember1) || '';
    const plan = {
      id: newId(), mode: tools.length < 2 && rosterSupported ? 'lead' : 'review', goal: '', rules: savedRules(), discuss: false,
      dir: projects.length === 1 ? projects[0] : '',
      writer: defaultWriter, reviewer: defaultReviewer, leader: defaultLeader,
      members: tools.filter(t => t !== defaultLeader),
      autoRoster: restoredTool && typeof restored.autoRoster === 'boolean' ? restored.autoRoster : false,
      leaderModel: '', leaderCustomModel: false,
      leaderEffort: '', leaderCustomEffort: false,
      rosterMembers: [{ tool: defaultMember1, model: '', effort: '', customModel: false, customEffort: false }]
        .concat(defaultMember2 ? [{ tool: defaultMember2, model: '', effort: '', customModel: false, customEffort: false }] : []),
      compareTools: tools.slice(0, 3),
      modelOverrides: {},
      effortOverrides: {},
      moreOpen: false,
      rounds: 3,
      lastFixedRounds: 3
    };

    const effectiveToolModel = tool => {
      if (!hasModels()) return '';
      if (Object.prototype.hasOwnProperty.call(plan.modelOverrides, tool)) return reconcileModel(tool, plan.modelOverrides[tool]);
      return defaultModelFor(tool);
    };
    const effectiveToolEffort = (tool, modelId) => {
      if (!hasEffort()) return '';
      const m = modelId === undefined ? effectiveToolModel(tool) : modelId;
      if (Object.prototype.hasOwnProperty.call(plan.effortOverrides, tool)) return reconcileEffort(tool, m, plan.effortOverrides[tool]);
      return defaultEffortFor(tool, m);
    };
    const effectiveLeaderModel = () => !hasModels() ? '' : plan.leaderCustomModel ? reconcileModel(plan.leader, plan.leaderModel) : defaultModelFor(plan.leader);
    const effectiveLeaderEffort = modelId => {
      if (!hasEffort()) return '';
      const m = modelId === undefined ? effectiveLeaderModel() : modelId;
      return plan.leaderCustomEffort ? reconcileEffort(plan.leader, m, plan.leaderEffort) : defaultEffortFor(plan.leader, m);
    };
    const effectiveSlotModel = slot => !hasModels() ? '' : slot.customModel ? reconcileModel(slot.tool, slot.model) : defaultModelFor(slot.tool);
    const effectiveSlotEffort = (slot, modelId) => {
      if (!hasEffort()) return '';
      const m = modelId === undefined ? effectiveSlotModel(slot) : modelId;
      return slot.customEffort ? reconcileEffort(slot.tool, m, slot.effort) : defaultEffortFor(slot.tool, m);
    };

    let beginSheet = null, rosterBox = null, moreHost = null;
    function activeRoleRows() {
      if (plan.mode === 'review') {
        if (plan.writer === plan.reviewer) return [{ role: '写手 / 审查', tool: plan.writer }];
        return [{ role: '写手', tool: plan.writer }, { role: '审查', tool: plan.reviewer }];
      }
      if (plan.mode === 'lead') {
        return [{ role: '领队', tool: plan.leader }].concat(plan.members.map(t => ({ role: '执行成员', tool: t })));
      }
      return plan.compareTools.slice(0, 3).map(t => ({ role: '候选', tool: t }));
    }
    function syncCatalogDefaults() {
      if (plan.leaderCustomModel) plan.leaderModel = reconcileModel(plan.leader, plan.leaderModel);
      if (plan.leaderCustomEffort) plan.leaderEffort = reconcileEffort(plan.leader, effectiveLeaderModel(), plan.leaderEffort);
      plan.rosterMembers.forEach(slot => {
        if (slot.customModel) slot.model = reconcileModel(slot.tool, slot.model);
        if (slot.customEffort) slot.effort = reconcileEffort(slot.tool, effectiveSlotModel(slot), slot.effort);
      });
      Object.keys(plan.modelOverrides).forEach(tool => { plan.modelOverrides[tool] = reconcileModel(tool, plan.modelOverrides[tool]); });
      Object.keys(plan.effortOverrides).forEach(tool => { plan.effortOverrides[tool] = reconcileEffort(tool, effectiveToolModel(tool), plan.effortOverrides[tool]); });
      if (modelError) plan.moreOpen = true;
    }
    function hasOneShotOverride() {
      if (plan.mode === 'lead' && hasRoster()) {
        if (plan.leaderCustomModel || plan.leaderCustomEffort) return true;
        return !plan.autoRoster && plan.rosterMembers.some(s => s.customModel || s.customEffort);
      }
      const activeTools = [...new Set(activeRoleRows().map(r => r.tool))];
      return activeTools.some(t => Object.prototype.hasOwnProperty.call(plan.modelOverrides, t) || Object.prototype.hasOwnProperty.call(plan.effortOverrides, t));
    }
    function moreSummaryLine() {
      if (modelError) return '模型列表读取失败，点此重试或使用工具默认';
      if (modelLoading && !modelCatalog) return '正在读取模型目录，默认按设置与推荐配置';
      if (!hasModels()) return '先讨论、分工偏好等高级选项';
      const shortE = level => (defaults && defaults.shortEffortLabel ? defaults.shortEffortLabel(level) : level);
      if (plan.mode === 'lead' && hasRoster()) {
        const lm = effectiveLeaderModel(), le = effectiveLeaderEffort(lm);
        const parts = ['领队 ' + modelLabel(plan.leader, lm) + (le ? '·' + shortE(le) : '')];
        if (!plan.autoRoster) {
          plan.rosterMembers.forEach((slot, idx) => {
            const sm = effectiveSlotModel(slot), se = effectiveSlotEffort(slot, sm);
            parts.push('成员' + (idx + 1) + ' ' + modelLabel(slot.tool, sm) + (se ? '·' + shortE(se) : ''));
          });
        }
        return parts.join(' / ');
      }
      const uniqueTools = [...new Set(activeRoleRows().map(r => r.tool))];
      return uniqueTools.map(tool => {
        const m = effectiveToolModel(tool), e = effectiveToolEffort(tool, m);
        return modelLabel(tool, m) + (e ? '·' + shortE(e) : '');
      }).join(' / ');
    }
    function modelNoticeNodes(onRetry) {
      const nodes = [];
      if (modelLoading && !modelCatalog) {
        nodes.push(kit.note('正在读取可用模型与思考深度，也可直接开始'));
      }
      if (modelError) {
        nodes.push(el('div', { className: 'team-models-error', role: 'alert' },
          el('span', { textContent: '模型列表加载失败：' + modelError + '（仍可使用工具默认）' }),
          el('button', {
            type: 'button', id: 'team-models-retry', className: 'team-models-retry',
            textContent: modelLoading ? '读取中…' : '重试', disabled: modelLoading,
            onclick: () => { loadModels(true).then(() => { syncCatalogDefaults(); onRetry(); }); onRetry(); }
          })));
      }
      return nodes;
    }
    function makeToolSelect({ id, value, ariaLabel, onchange }) {
      const options = namedTools(tools);
      const select = el('select', { id, className: 'team-slot-select', 'aria-label': ariaLabel },
        ...options.map(([val, label]) => el('option', { value: val, textContent: label, selected: val === value })));
      select.addEventListener('change', () => onchange(select.value));
      return select;
    }
    function makeField(labelText, control) {
      return el('div', { className: 'team-field' },
        el('label', { htmlFor: control.id, textContent: labelText }),
        control);
    }
    function slotBadgeSummary(tool, modelId, effortId) {
      if (!hasModels()) return '';
      const chips = [el('span', { textContent: modelLabel(tool, modelId) })];
      if (hasEffort() && effortId) chips.push(el('span', { className: 'effort', textContent: effortTag(effortId) }));
      return el('div', { className: 'team-slot-summary' }, ...chips);
    }
    function refreshAllSections() {
      renderRoster();
      renderMore();
    }
    function renderRoster() {
      if (!rosterBox) return;
      if (plan.mode !== 'lead' || !hasRoster()) { rosterBox.replaceChildren(); return; }
      const sections = [];
      const lm = effectiveLeaderModel(), le = effectiveLeaderEffort(lm);
      const leaderToolSel = makeToolSelect({
        id: 'team-roster-tool-leader',
        value: plan.leader,
        ariaLabel: '领队工具',
        onchange: val => {
          plan.leader = val;
          plan.leaderCustomModel = false; plan.leaderModel = '';
          plan.leaderCustomEffort = false; plan.leaderEffort = '';
          refreshAllSections();
        }
      });
      const leaderSlot = el('div', { className: 'team-slot team-leader-slot' },
        el('div', { className: 'team-slot-head' },
          el('span', { className: 'dot ' + (plan.leader === 'claude' ? 'n-cc' : plan.leader === 'agy' ? 'n-ag' : 'n-cx'), textContent: SHORT[plan.leader] || 'AI' }),
          el('b', { textContent: '领队' })),
        el('div', { className: 'team-slot-fields single' }, makeField('工具', leaderToolSel)),
        slotBadgeSummary(plan.leader, lm, le));
      const leaderNote = plan.autoRoster
        ? '领队统筹分工与验收，执行成员按任务动态创建（最多 3 个）；可在下方「更多」调整本次模型或思考深度'
        : '默认沿用设置中的模型与思考深度；可在下方「更多」单次调整';
      sections.push(kit.section('领队', kit.group(leaderSlot), leaderNote));

      if (!plan.autoRoster) {
        const memberRows = plan.rosterMembers.map((slot, idx) => {
          const role = 'member-' + (idx + 1), label = '执行成员 ' + (idx + 1);
          const sm = effectiveSlotModel(slot), se = effectiveSlotEffort(slot, sm);
          const toolSel = makeToolSelect({
            id: 'team-roster-tool-' + role,
            value: slot.tool,
            ariaLabel: label + '工具',
            onchange: val => {
              slot.tool = val;
              slot.customModel = false; slot.model = '';
              slot.customEffort = false; slot.effort = '';
              refreshAllSections();
            }
          });
          const onRemove = plan.rosterMembers.length > 1 ? () => { plan.rosterMembers.splice(idx, 1); refreshAllSections(); } : null;
          return el('div', { className: 'team-slot' },
            el('div', { className: 'team-slot-head' },
              el('span', { className: 'dot ' + (slot.tool === 'claude' ? 'n-cc' : slot.tool === 'agy' ? 'n-ag' : 'n-cx'), textContent: SHORT[slot.tool] || 'AI' }),
              el('b', { textContent: label }),
              onRemove ? el('button', { type: 'button', id: 'team-roster-remove-' + role, className: 'team-slot-remove', 'aria-label': '移除' + label, textContent: '移除', onclick: onRemove }) : ''),
            el('div', { className: 'team-slot-fields single' }, makeField('工具', toolSel)),
            slotBadgeSummary(slot.tool, sm, se));
        });
        const memberNodes = [kit.group(...memberRows)];
        if (plan.rosterMembers.length < 3) {
          memberNodes.push(el('button', {
            type: 'button', id: 'team-roster-add', className: 'team-roster-add',
            textContent: '＋ 添加执行成员（' + plan.rosterMembers.length + ' / 3）',
            onclick: () => {
              if (plan.rosterMembers.length >= 3) return;
              const usedTools = new Set([plan.leader].concat(plan.rosterMembers.map(s => s.tool)));
              const nextTool = tools.find(t => !usedTools.has(t)) || tools[0];
              plan.rosterMembers.push({ tool: nextTool, model: '', effort: '', customModel: false, customEffort: false });
              refreshAllSections();
            }
          }));
        }
        sections.push(kit.section('执行成员', memberNodes, '可重复使用同一工具，同账号共用额度'));
      }
      rosterBox.replaceChildren(...sections);
    }

    function buildMoreModelsSection() {
      const modelsOn = hasModels(), effortOn = hasEffort();
      if (!modelsOn && !(plan.mode === 'lead' && hasRoster())) return null;
      const nodes = modelsOn ? modelNoticeNodes(refreshAllSections) : [];
      if (plan.mode === 'lead' && hasRoster()) {
        const slots = [];
        const lm = effectiveLeaderModel(), le = effectiveLeaderEffort(lm);
        const leaderEfforts = effortListFor(plan.leader, lm);
        const leaderEffortSelectable = effortOn && (leaderEfforts.length > 0 || (!!le && !modelCatalog));
        const leaderModelSel = makeModelSelect({
          id: 'team-roster-model-leader', tool: plan.leader, value: modelsOn ? lm : '',
          className: 'team-slot-select', ariaLabel: '领队模型', disabled: !modelsOn,
          onchange: val => {
            plan.leaderCustomModel = true; plan.leaderModel = val;
            if (plan.leaderCustomEffort) plan.leaderEffort = reconcileEffort(plan.leader, val, plan.leaderEffort);
            refreshAllSections();
          }
        });
        const leaderEffortSel = makeEffortSelect({
          id: 'team-roster-effort-leader', tool: plan.leader, model: lm, value: leaderEffortSelectable ? le : '',
          className: 'team-slot-select', ariaLabel: '领队思考深度', disabled: !leaderEffortSelectable,
          onchange: val => { plan.leaderCustomEffort = true; plan.leaderEffort = val; refreshAllSections(); }
        });
        slots.push(el('div', { className: 'team-slot' },
          el('div', { className: 'team-slot-head' },
            el('span', { className: 'dot ' + (plan.leader === 'claude' ? 'n-cc' : plan.leader === 'agy' ? 'n-ag' : 'n-cx'), textContent: SHORT[plan.leader] || 'AI' }),
            el('b', { textContent: '领队 · ' + (TOOLS[plan.leader] || plan.leader) })),
          el('div', { className: 'team-slot-fields' },
            makeField('模型', leaderModelSel),
            makeField('思考深度', leaderEffortSel))));

        if (!plan.autoRoster) {
          plan.rosterMembers.forEach((slot, idx) => {
            const role = 'member-' + (idx + 1), label = '执行成员 ' + (idx + 1);
            const sm = effectiveSlotModel(slot), se = effectiveSlotEffort(slot, sm);
            const efforts = effortListFor(slot.tool, sm);
            const effortSelectable = effortOn && (efforts.length > 0 || (!!se && !modelCatalog));
            const modelSel = makeModelSelect({
              id: 'team-roster-model-' + role, tool: slot.tool, value: modelsOn ? sm : '',
              className: 'team-slot-select', ariaLabel: label + '模型', disabled: !modelsOn,
              onchange: val => {
                slot.customModel = true; slot.model = val;
                if (slot.customEffort) slot.effort = reconcileEffort(slot.tool, val, slot.effort);
                refreshAllSections();
              }
            });
            const effortSel = makeEffortSelect({
              id: 'team-roster-effort-' + role, tool: slot.tool, model: sm, value: effortSelectable ? se : '',
              className: 'team-slot-select', ariaLabel: label + '思考深度', disabled: !effortSelectable,
              onchange: val => { slot.customEffort = true; slot.effort = val; refreshAllSections(); }
            });
            slots.push(el('div', { className: 'team-slot' },
              el('div', { className: 'team-slot-head' },
                el('span', { className: 'dot ' + (slot.tool === 'claude' ? 'n-cc' : slot.tool === 'agy' ? 'n-ag' : 'n-cx'), textContent: SHORT[slot.tool] || 'AI' }),
                el('b', { textContent: label + ' · ' + (TOOLS[slot.tool] || slot.tool) })),
              el('div', { className: 'team-slot-fields' },
                makeField('模型', modelSel),
                makeField('思考深度', effortSel))));
          });
        }
        nodes.push(kit.group(...slots));
        const note = !modelsOn
          ? '当前电脑端或中转版本较旧，不支持设置模型和思考深度，将使用工具默认'
          : !effortOn
          ? '当前电脑端或中转版本较旧，不支持设置思考深度，将使用工具默认（更新后可调整）'
          : '仅覆盖本次协作的模型与思考深度；不改动设置中的长期默认值';
        return kit.section('本次模型与思考深度', nodes, note);
      }

      if (!modelsOn) return null;
      const roles = activeRoleRows();
      const seenTools = new Set();
      const rows = [];
      roles.forEach(({ role, tool }) => {
        if (seenTools.has(tool)) return;
        seenTools.add(tool);
        const chosen = effectiveToolModel(tool);
        const chosenEffort = effectiveToolEffort(tool, chosen);
        const efforts = effortListFor(tool, chosen);
        const effortSelectable = effortOn && (efforts.length > 0 || (!!chosenEffort && !modelCatalog));
        const modelSel = makeModelSelect({
          id: 'team-model-' + tool, tool, value: chosen,
          className: 'team-slot-select',
          ariaLabel: role + '（' + (TOOLS[tool] || tool) + '）模型',
          onchange: val => {
            plan.modelOverrides[tool] = val;
            if (Object.prototype.hasOwnProperty.call(plan.effortOverrides, tool)) {
              plan.effortOverrides[tool] = reconcileEffort(tool, val, plan.effortOverrides[tool]);
            }
            refreshAllSections();
          }
        });
        const fields = [makeField('模型', modelSel)];
        if (effortOn) {
          fields.push(makeField('思考深度', makeEffortSelect({
            id: 'team-effort-' + tool, tool, model: chosen, value: effortSelectable ? chosenEffort : '',
            className: 'team-slot-select',
            ariaLabel: role + '（' + (TOOLS[tool] || tool) + '）思考深度',
            disabled: !effortSelectable,
            onchange: val => { plan.effortOverrides[tool] = val; refreshAllSections(); }
          })));
        }
        rows.push(el('div', { className: 'team-slot' },
          el('div', { className: 'team-slot-head' },
            el('span', { className: 'dot ' + (tool === 'claude' ? 'n-cc' : tool === 'agy' ? 'n-ag' : 'n-cx'), textContent: SHORT[tool] || 'AI' }),
            el('b', { textContent: role + ' · ' + (TOOLS[tool] || tool) })),
          el('div', { className: 'team-model-row-controls' + (fields.length === 1 ? ' single' : '') }, ...fields)));
      });
      if (rows.length) nodes.push(kit.group(...rows));
      const note = !effortOn
        ? '仅覆盖本次协作的模型；更新电脑端与中转后支持调整思考深度'
        : '仅覆盖本次协作的模型与思考深度；不改动设置中的长期默认值';
      return kit.section('本次模型与思考深度', nodes, note);
    }

    function renderMore() {
      if (!moreHost) return;
      const overridden = hasOneShotOverride();
      const toggleBtn = el('button', {
        type: 'button', id: 'team-more-toggle', className: 'team-more-toggle',
        ariaExpanded: String(!!plan.moreOpen),
        onclick: () => { plan.moreOpen = !plan.moreOpen; renderMore(); }
      },
        el('span', { className: 'team-more-copy' },
          el('b', { textContent: '更多（更改本次模型与偏好）' }),
          el('small', { textContent: moreSummaryLine() })),
        overridden ? el('span', { className: 'team-more-pill', textContent: '本次已改' }) : '',
        el('span', { className: 'team-more-chev', ariaHidden: 'true', textContent: '▾' }));

      const panelChildren = [];
      const modelsSection = buildMoreModelsSection();
      if (modelsSection) panelChildren.push(modelsSection);
      if (hasModels()) {
        panelChildren.push(el('div', { className: 'team-more-links' },
          el('small', { textContent: '想长期修改默认模型或思考深度？' }),
          el('button', {
            type: 'button', id: 'team-defaults-link', className: 'team-more-link',
            textContent: '前往设置',
            onclick: () => { if (beginSheet) beginSheet.close(null, true); location.href = 'computer/#part-team-defaults'; }
          })));
      }
      if (plan.mode !== 'compare' && hasV2() && !(plan.mode === 'lead' && hasRoster() && plan.autoRoster)) {
        const discussToggle = kit.toggle(plan.discuss, on => { plan.discuss = on; }, '先议后写');
        discussToggle.id = 'team-discuss';
        panelChildren.push(kit.group(kit.row({ icon: 'chat', tint: 'agy', label: '先讨论再动手', detail: '先由各 AI 只读分析方案并汇总共识，再开始改代码', end: discussToggle })));
      }
      const rules = kit.area({ id: 'team-rules', value: plan.rules, placeholder: '选填：默认 Codex 决策、Claude 写作、Gemini 做 UI，交叉检查优先 Claude/Gemini（自动记住）', max: 1000, rows: 2, label: '分工偏好' }, value => { plan.rules = value; });
      panelChildren.push(kit.section('分工偏好（选填）', rules));

      const panel = el('div', { id: 'team-more-panel', className: 'team-more-panel', hidden: !plan.moreOpen }, ...panelChildren);
      moreHost.replaceChildren(el('div', { className: 'team-more-card' }, toggleBtn, panel));
    }

    if (hasModels() && (!modelCatalog || modelError)) {
      loadModels().then(() => { if (beginSheet && !beginSheet.gone) { syncCatalogDefaults(); refreshAllSections(); } });
    }
    beginSheet = window.RemoteSheet.open({ title: '协作任务', build(p) {
      const modeHint = plan.mode === 'review' ? '一个写，另一个只读审查；审查意见交回去改，直到通过'
        : plan.mode === 'lead' ? '一轮包含领队派工、执行成员完成并汇报、领队汇总验收；发现问题再进入下一轮'
        : '2–3 个 AI 在独立 Git worktree 副本中并行完成，互不干扰，做完后并排对比改动、任选一个合并';
      const modeKeys = hasV2() && tools.length >= 2 ? Object.keys(MODES) : tools.length >= 2 ? ['review', 'lead'] : ['lead'];
      p.body.append(kit.section('方式', kit.seg(modeKeys.map(key => [key, MODES[key]]), plan.mode, value => {
        plan.mode = value;
        if (plan.mode !== 'lead' && plan.rounds === 0) plan.rounds = plan.lastFixedRounds || 3;
        p.refresh();
      }, { label: '方式', full: true }), modeHint));
      const goal = kit.area({ id: 'team-goal', value: plan.goal, placeholder: '要完成什么', max: 4000, rows: 4, label: '目标' }, value => { plan.goal = value; });
      p.body.append(kit.section('目标', goal));
      p.body.append(kit.section('项目', kit.chips(projects.map(name => [name, name]), { chosen: plan.dir, label: '项目' }, value => { plan.dir = value; })));
      const named = namedTools(tools);
      if (plan.mode === 'review') {
        rosterBox = null;
        p.body.append(kit.section('写', kit.chips(named, { chosen: plan.writer, label: '写的 AI' }, value => {
          plan.writer = value;
          if (plan.reviewer === value && tools.length > 1) { plan.reviewer = tools.find(t => t !== value) || value; p.refresh(); return; }
          renderMore();
        })));
        p.body.append(kit.section('审查', kit.chips(named, { chosen: plan.reviewer, label: '审查的 AI' }, value => { plan.reviewer = value; renderMore(); }), '审查的 AI 以只读方式运行'));
      } else if (plan.mode === 'lead') {
        if (hasRoster()) {
          p.body.append(kit.section('组队方式', kit.seg([['manual', '手动选成员'], ['auto', '自动组队']], plan.autoRoster ? 'auto' : 'manual', value => {
            plan.autoRoster = value === 'auto';
            p.refresh();
          }, { label: '组队方式', full: true }), plan.autoRoster ? '只需配置领队，执行成员由领队按任务按需创建' : '提前配置领队和 1–3 个执行成员'));
          rosterBox = el('div', { className: 'team-roster-host' });
          p.body.append(rosterBox);
          renderRoster();
        } else {
          rosterBox = null;
          p.body.append(kit.section('领队', kit.chips(named, { chosen: plan.leader, label: '领队' }, value => { plan.leader = value; plan.members = plan.members.filter(t => t !== value); p.refresh(); })));
          p.body.append(kit.section('执行成员', kit.chips(named.filter(([tool]) => tool !== plan.leader), { chosen: plan.members, multi: true, label: '执行成员' }, value => { plan.members = value; renderMore(); }), '领队按任务和剩余额度按需派工，避免多人改同一批文件'));
        }
      } else {
        rosterBox = null;
        p.body.append(kit.section('参与比稿的 AI', kit.chips(named, { chosen: plan.compareTools, multi: true, label: '比稿 AI' }, value => { plan.compareTools = value; renderMore(); }), '至少选 2 个不同 AI；要求项目目录是已有提交的 Git 仓库'));
      }
      if (plan.mode !== 'compare') {
        const flexAllowed = plan.mode === 'lead' && hasFlexRounds();
        if (!flexAllowed && plan.rounds === 0) plan.rounds = plan.lastFixedRounds || 3;
        const roundChoices = (flexAllowed ? [[0, '自由轮数']] : []).concat(ROUNDS.map(n => [n, n + ' 轮']));
        const roundNoteText = () => {
          if (plan.mode !== 'lead') return '';
          if (!hasFlexRounds()) return '当前电脑端或中转版本较旧，不支持「自由轮数」，更新电脑端与中转后可用';
          return plan.rounds === 0
            ? '由领队按每轮进展与验收结果自主决定何时结束（最多 20 轮），达标即止'
            : '达到设定轮数仍未通过验收时会暂停，点「继续」可追加轮次；也可选择「自由轮数」';
        };
        const roundNoteEl = roundNoteText() ? el('p', { id: 'team-rounds-note', className: 'rs-note', textContent: roundNoteText() }) : null;
        const roundSeg = kit.seg(roundChoices, plan.rounds, value => {
          plan.rounds = value;
          if (value > 0) plan.lastFixedRounds = value;
          if (roundNoteEl) roundNoteEl.textContent = roundNoteText();
        }, { label: '轮数', full: !flexAllowed });
        if (flexAllowed) roundSeg.classList.add('team-rounds-grid');
        Array.from(roundSeg.querySelectorAll('button')).forEach((btn, idx) => {
          const val = roundChoices[idx][0];
          btn.dataset.rounds = String(val);
          if (val === 0) btn.id = 'team-round-flex';
        });
        p.body.append(kit.section('最多几轮', roundNoteEl ? [roundSeg, roundNoteEl] : roundSeg));
      }
      moreHost = el('div', { className: 'team-more-host' });
      p.body.append(moreHost);
      renderMore();

      const go = kit.go('开始', async () => {
        if (!plan.goal.trim()) { goal.box.focus(); throw new Error('请写下要完成什么'); }
        if (!plan.dir) throw new Error('请选择项目');
        const trimmedRules = plan.rules.trim();
        keepRules(trimmedRules);
        const submitRounds = plan.mode === 'compare' ? 1
          : (plan.mode === 'lead' && hasFlexRounds() && plan.rounds === 0) ? 0
          : (plan.rounds || plan.lastFixedRounds || 3);
        let item;
        if (plan.mode === 'lead' && hasRoster()) {
          const leaderModel = effectiveLeaderModel();
          const leaderEffort = effectiveLeaderEffort(leaderModel);
          const leaderEntry = { role: 'leader', tool: plan.leader, model: leaderModel };
          if (leaderEffort) leaderEntry.effort = leaderEffort;
          const roster = [leaderEntry];
          if (!plan.autoRoster) {
            if (!plan.rosterMembers.length) throw new Error('请至少保留一个执行成员，或改用自动组队');
            plan.rosterMembers.slice(0, 3).forEach((slot, idx) => {
              const sm = effectiveSlotModel(slot);
              const se = effectiveSlotEffort(slot, sm);
              const entry = { role: 'member-' + (idx + 1), tool: slot.tool, model: sm };
              if (se) entry.effort = se;
              roster.push(entry);
            });
          }
          keepLeader({ tool: plan.leader, autoRoster: plan.autoRoster });
          item = { id: plan.id, mode: 'lead', dir: plan.dir, goal: plan.goal.trim(), tools: roster.map(r => r.tool), roster, rounds: submitRounds };
          if (plan.autoRoster) item.auto_roster = true;
          if (plan.discuss && !plan.autoRoster) item.discuss = true;
        } else {
          const chosen = plan.mode === 'review' ? [plan.writer, plan.reviewer]
            : plan.mode === 'lead' ? [plan.leader].concat(plan.members)
            : plan.compareTools.slice(0, 3);
          if (plan.mode === 'lead' && chosen.length < 2) throw new Error('请至少选一个执行成员');
          if (plan.mode === 'compare' && chosen.length < 2) throw new Error('并行比稿请至少选 2 个 AI');
          item = { id: plan.id, mode: plan.mode, dir: plan.dir, goal: plan.goal.trim(), tools: chosen, rounds: submitRounds };
          if (hasModels()) {
            const chosenModels = {};
            const chosenEfforts = {};
            [...new Set(chosen)].forEach(tool => {
              const val = effectiveToolModel(tool);
              if (val) chosenModels[tool] = val;
              if (hasEffort()) {
                const eff = effectiveToolEffort(tool, val);
                if (eff) chosenEfforts[tool] = eff;
              }
            });
            if (Object.keys(chosenModels).length) item.models = chosenModels;
            if (Object.keys(chosenEfforts).length) item.efforts = chosenEfforts;
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

  // ---- one: full-screen collaboration detail (stage & member status first, scrollable activity log, preserved reading position)
  function show(known) {
    let confirmDiscard = false, sending = false, busyAct = false, menuButton = null;
    let currentTeam = (teams || []).find(t => t.id === known.id) || known;
    let draftText = '', targetRole = '', activeCandidate = (currentTeam.tools && currentTeam.tools[0]) || '';
    const expanded = new Set();
    let streamBox = null, overviewBox = null, compareBox = null, activityBox = null, activityHead = null, streamList = null, jumpBtn = null;
    let footBox = null, bodyScroll = null, activeSheet = null, activePage = null, lastKey = '', lastLogCount = (currentTeam.log || []).length, lastLogTail = '', unreadLogs = 0;
    let removed = false;

    function closeRemoved(notify) {
      if (removed) return;
      removed = true;
      forgetTeam(known.id);
      if (page.closeReader) page.closeReader();
      if (activeSheet && !activeSheet.gone) activeSheet.close('removed');
      if (notify) toast('协作任务已删除');
      page.refresh();
    }

    function inspectMember(team, role, terminals) {
      const tid = (team.members || {})[role] || '';
      const t = tid ? terminals.find(one => one.id === tid) : null;
      const liveTerm = t && page.running(t) ? t : null;
      const ms = (team.member_sessions || {})[role] || null;
      const tool = toolOf(team, role);
      const sessionRef = (ms && ms.id) ? { id: ms.id, tool: ms.tool || tool }
        : (t && t.session ? { id: t.session, tool: t.tool || tool } : null);
      const isRunning = team.state === 'running';
      const isPaused = team.state === 'paused';
      const isEnded = team.state === 'done' || team.state === 'stopped' || team.state === 'failed';
      const awaited = (team.awaited || []).includes(role);
      const active = isRunning && awaited;
      const waitingResume = isPaused && awaited;
      const asking = !!liveTerm && (page.phaseOf(liveTerm) === 'confirm' || !!liveTerm.ask);
      const askText = liveTerm ? (liveTerm.ask || (liveTerm.asks || []).join('\n') || (asking ? '需要你确认后才能继续' : '')) : '';
      const rawFailed = !!(team.failed && (team.failed[role] || team.failed[tool]));
      const failed = rawFailed && !active;
      const canOpen = !!liveTerm || !!sessionRef || !!t;
      const route = liveTerm ? 'live' : sessionRef ? 'history' : t ? 'terminal' : 'none';
      let statusText = '待开始', statusTone = 'idle';
      if (asking) { statusText = '等你确认'; statusTone = 'ask'; }
      else if (active) {
        statusText = team.phase === 'discuss' ? '讨论中' : '进行中';
        if (role === 'leader' && team.phase === 'verify') statusText = '验收中';
        if (role === 'leader' && team.phase === 'decision') statusText = phaseLabel(team.phase);
        statusTone = 'busy';
      }
      else if (failed) { statusText = '已中断'; statusTone = 'fail'; }
      else if (waitingResume) { statusText = '待继续'; statusTone = 'pause'; }
      else if (liveTerm) { statusText = '当前对话'; statusTone = 'live'; }
      else if (sessionRef) { statusText = '查看历史'; statusTone = 'hist'; }
      else if (t) { statusText = '已结束'; statusTone = 'hist'; }
      else if (isEnded) { statusText = '未参与'; statusTone = 'idle'; }
      return { role, tid, t, liveTerm, ms, sessionRef, tool, active, waitingResume, asking, askText, failed, canOpen, route, statusText, statusTone };
    }

    const terminalSig = t => {
      const terminals = (page.data && page.data.terminals) || [];
      return memberRolesOf(t).map(role => {
        const tid = (t.members || {})[role] || '';
        const term = tid ? terminals.find(one => one.id === tid) : null;
        return term ? [role, term.id, term.state, term.phase || '', term.status || '', term.form || '', term.session || '', term.ask || '', (term.asks || []).length] : [role, tid, ''];
      });
    };
    const logSigOf = t => {
      const log = (t && t.log) || [];
      const first = log[0], tail = log[log.length - 1];
      return [log.length, first ? first.at : 0, tail ? tail.at : 0, tail ? tail.who : '', tail ? (tail.text || '').length : 0];
    };
    const keyOf = t => JSON.stringify([
      t.state, t.phase, !!t.last, t.round, t.rounds, t.note, t.picked, t.tools,
      t.models || null, t.efforts || null, t.roster || null, !!t.auto_roster,
      t.awaited, t.members, t.member_sessions || null, t.failed, t.confirm || null,
      t._loading || false, t._error || '',
      logSigOf(t),
      terminalSig(t),
      page.usable(), !!modelCatalog, modelError,
      (t.candidates || []).map(c => [c.tool, c.stat, c.ready, (c.diff || '').length])
    ]);

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
      const fresh = await load();
      if (!activeSheet || activeSheet.gone) return;
      const latest = (teams || []).find(t => t.id === known.id);
      if (latest) {
        const wasLoading = !!currentTeam._loading;
        currentTeam = latest;
        lastKey = keyOf(latest);
        const atTop = !bodyScroll || bodyScroll.scrollTop <= 16;
        renderStream(wasLoading && atTop ? 'top' : 'preserve');
        renderFoot();
      } else if (currentTeam._loading) {
        currentTeam = Object.assign({}, currentTeam, { _loading: false, _error: fresh ? '这个协作任务已不存在' : failure || '没有读到协作任务，请重试' });
        lastKey = keyOf(currentTeam);
        renderStream('preserve');
        renderFoot();
      } else if (fresh) {
        closeRemoved(true);
      }
    }

    function openMember(info) {
      if (!info || !info.canOpen) return;
      if (info.liveTerm) {
        page.open(info.liveTerm.id, info.liveTerm.form || 'chat', { team: currentTeam.id });
        return;
      }
      if (info.sessionRef && info.sessionRef.id) {
        if (!page.usable()) { toast('电脑离线，暂时无法查看历史对话'); return; }
        if (page.has('peek')) {
          const found = ((page.data && page.data.sessions) || []).find(s => s.id === info.sessionRef.id && (!info.sessionRef.tool || s.tool === info.sessionRef.tool));
          const target = found || { id: info.sessionRef.id, tool: info.sessionRef.tool || info.tool, dir: currentTeam.dir || '', title: roleName(currentTeam, info.role), live: false, updated: Date.now() };
          page.takeOver(target, { team: currentTeam.id });
          return;
        }
        if (info.t) {
          page.open(info.t.id, info.t.form || 'chat', { team: currentTeam.id });
          return;
        }
        toast('当前电脑端版本不支持查看历史对话');
        return;
      }
      if (info.t) {
        page.open(info.t.id, info.t.form || 'chat', { team: currentTeam.id });
      }
    }

    function badgeInfo(team, pendingAsks) {
      if (pendingAsks.length) return stateInfo({ confirm: true });
      const [label, tone] = stateInfo(team);
      if (team.confirm) return [label, tone];
      if (team.state === 'running' || team.state === 'paused') return [label, 'busy'];
      if (team.state === 'done') return [label, 'good'];
      if (team.state === 'failed' || isUnverifiedStop(team)) return [label, 'confirm'];
      return [label, tone];
    }

    function buildStageSteps(team) {
      const renderTrack = (roundTag, stages, ariaLabel) => {
        const list = el('ol', { className: 'team-pipeline' }, ...stages.map(([label, state], idx) =>
          el('li', { className: 'team-stage' + (state ? ' ' + state : '') },
            el('span', { className: 'stage-node', ariaHidden: 'true', textContent: state === 'done' ? '✓' : state === 'fail' ? '!' : String(idx + 1) }),
            el('span', { className: 'stage-label team-step' + (state ? ' ' + state : ''), textContent: label }))
        ));
        return el('div', { className: 'team-round-steps', id: 'team-round-steps', ariaLabel },
          el('div', { className: 'team-round-meta' }, el('span', { className: 'team-round-tag', textContent: roundTag })),
          list);
      };
      const round = team.round || 1, flex = isFlexRounds(team), rounds = roundsOf(team);
      const finalRound = !!team.last || (!flex && round >= rounds);
      const awaited = team.awaited || [];
      if (team.mode === 'lead') {
        const hasDiscuss = !!team.discuss || team.phase === 'discuss';
        let discussState = '', assignState = '', workState = '', decisionState = '', verifyState = '';
        if (team.state === 'done') {
          discussState = assignState = workState = verifyState = 'done';
        } else if (team.phase === 'discuss') {
          discussState = team.state === 'running' ? 'active' : 'pause';
        } else if (team.phase === 'decision') {
          discussState = assignState = workState = 'done';
          decisionState = team.state === 'running' ? 'active' : team.state === 'paused' ? 'pause' : '';
        } else if (isUnverifiedStop(team)) {
          discussState = assignState = workState = 'done'; verifyState = 'fail';
        } else if (team.phase === 'verify') {
          discussState = assignState = workState = 'done'; verifyState = team.state === 'running' ? 'active' : team.state === 'paused' ? 'pause' : '';
        } else if (team.state === 'running') {
          discussState = 'done';
          if (awaited.some(r => r !== 'leader')) { assignState = 'done'; workState = 'active'; }
          else { assignState = 'active'; }
        } else if (team.state === 'paused') {
          discussState = 'done';
          if (awaited.some(r => r !== 'leader')) { assignState = 'done'; workState = 'pause'; }
          else { assignState = 'pause'; }
        } else if (awaited.some(r => r !== 'leader')) {
          discussState = assignState = 'done';
        }
        const roundTag = (flex ? '第 ' + round + ' 轮 · 自由轮数' : '第 ' + round + ' / ' + rounds + ' 轮') + (finalRound ? ' · 末轮' : '');
        const stages = [];
        if (hasDiscuss) stages.push([phaseLabel('discuss'), discussState]);
        stages.push(['领队派工', assignState], ['成员执行', workState]);
        if (decisionState) stages.push([phaseLabel('decision'), decisionState]);
        stages.push([verifyState === 'fail' ? '验收未通过' : phaseLabel('verify'), verifyState]);
        return renderTrack(roundTag, stages, '本轮进度');
      }
      if (team.mode === 'review') {
        const hasDiscuss = !!team.discuss || team.phase === 'discuss';
        let discussState = '', writeState = '', reviewState = '', passState = '';
        if (team.state === 'done') {
          discussState = writeState = reviewState = passState = 'done';
        } else if (team.phase === 'discuss') {
          discussState = team.state === 'running' ? 'active' : 'pause';
        } else if (awaited.includes('reviewer')) {
          discussState = writeState = 'done';
          reviewState = team.state === 'running' ? 'active' : team.state === 'paused' ? 'pause' : '';
        } else if (team.state === 'running') {
          discussState = 'done'; writeState = 'active';
        } else if (team.state === 'paused') {
          discussState = 'done'; writeState = 'pause';
        }
        const stages = [];
        if (hasDiscuss) stages.push([phaseLabel('discuss'), discussState]);
        stages.push(['写手实现', writeState], ['只读审查', reviewState], ['审查通过', passState]);
        return renderTrack('第 ' + round + ' / ' + rounds + ' 轮', stages, '本轮进度');
      }
      if (team.mode === 'compare') {
        const runState = team.state === 'running' ? 'active' : team.state === 'failed' ? 'fail' : 'done';
        const pickState = team.state === 'choose' ? 'active' : team.state === 'done' ? 'done' : '';
        const endState = team.state === 'done' ? 'done' : '';
        return renderTrack('并行比稿', [
          ['独立实现', runState],
          ['对比选稿', pickState],
          [team.picked ? '已采纳' : '已结束', endState]
        ], '比稿进度');
      }
      return null;
    }

    function updateJump() {
      if (!jumpBtn || !bodyScroll) return;
      const distBottom = bodyScroll.scrollHeight - bodyScroll.scrollTop - bodyScroll.clientHeight;
      if (distBottom < 48) unreadLogs = 0;
      const showNew = unreadLogs > 0 && distBottom >= 48;
      jumpBtn.hidden = !showNew;
      if (showNew) jumpBtn.textContent = unreadLogs > 1 ? unreadLogs + ' 条新记录' : '有新记录';
    }

    function scrollToLatest(smooth) {
      if (!bodyScroll) return;
      unreadLogs = 0;
      updateJump();
      const reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
      bodyScroll.scrollTo({ top: bodyScroll.scrollHeight, behavior: smooth && !reduce ? 'smooth' : 'auto' });
    }

    function renderStream(scrollMode) {
      if (!streamBox || !overviewBox || !compareBox || !activityBox || !activityHead || !streamList) return;
      const mode = scrollMode === true ? 'bottom' : scrollMode === false || !scrollMode ? 'preserve' : scrollMode;
      const team = currentTeam;
      const terminals = (page.data && page.data.terminals) || [];

      // Capture reading position anchor before any DOM mutation so polling never jolts the reader.
      const prevScrollTop = bodyScroll ? bodyScroll.scrollTop : 0;
      const scrolledDown = prevScrollTop > 16;
      const distFromBottom = bodyScroll ? bodyScroll.scrollHeight - prevScrollTop - bodyScroll.clientHeight : 0;
      const wasAtBottom = scrolledDown && distFromBottom < 52;
      let anchorKey = '', anchorEl = null, anchorOffset = 0;
      if (mode === 'preserve' && scrolledDown && !wasAtBottom && bodyScroll) {
        const viewTop = bodyScroll.getBoundingClientRect().top;
        for (const node of streamList.children) {
          const rect = node.getBoundingClientRect();
          if (rect.bottom > viewTop + 8) {
            anchorKey = node.dataset.msgKey || '';
            anchorOffset = rect.top - viewTop;
            break;
          }
        }
        if (!anchorKey && !activityBox.hidden) {
          anchorEl = activityBox;
          anchorOffset = activityBox.getBoundingClientRect().top - viewTop;
        }
      }

      const memberKeys = memberRolesOf(team);
      const memberInfos = memberKeys.map(role => inspectMember(team, role, terminals));
      const pendingAsks = memberInfos.filter(info => info.asking);
      // Each fact is said once: the state in the header's badge, the round and phase on the stage track, who works
      // on it beside the goal (with a dot in the badge's colour), each member with its model on its card.
      const badge = badgeInfo(team, pendingAsks);
      if (activePage && activePage.setHeader) {
        activePage.setHeader({ title: (MODES[team.mode] || '协作') + (team.dir ? ' · ' + team.dir : ''), subtitle: '', badge: badge[0], tone: badge[1] });
      }

      const overviewParts = [];
      const heroParts = [kit.who({ icon: 'spark', tint: 'agy', title: team.goal || '协作任务', line: who(team), state: TONE_DOT[badge[1]] || '' })];
      const stageBar = buildStageSteps(team);
      if (stageBar) heroParts.push(stageBar);
      if (team.state === 'running' && team.phase === 'verify') {
        const banner = kit.summary(team.note || '正在核对执行成员改动与结果', '', 'spark');
        banner.classList.add('team-verify');
        heroParts.push(banner);
      } else if (team.state !== 'paused' && team.note) {
        heroParts.push(kit.summary(team.note, team.state === 'failed' || isUnverifiedStop(team) ? 'bad' : '', team.state === 'done' ? 'tick' : 'clock'));
      }
      overviewParts.push(el('section', { className: 'team-hero', ariaLabel: '阶段与任务概览' }, ...heroParts));

      if (team.state === 'paused') {
        overviewParts.push(el('div', { id: 'team-pause-reason', className: 'team-pause-banner', role: 'status' },
          el('b', { textContent: '协作已暂停' }),
          el('p', { textContent: team.note || '本轮执行已暂停：点下方「继续」恢复当前步骤，或在输入框补充说明后继续。' })));
      }

      if (pendingAsks.length) {
        pendingAsks.forEach(info => {
          overviewParts.push(el('div', { className: 'team-ask-banner', role: 'alert' },
            el('div', { className: 'copy' },
              el('b', { textContent: roleName(team, info.role) + ' 正在等你确认' }),
              el('small', { textContent: info.askText })),
            el('button', { type: 'button', className: 'go', onclick: () => openMember(info) }, '去处理')));
        });
      }

      const roster = rosterOf(team);
      if (team.auto_roster) {
        const assigned = roster.filter(r => r.role !== 'leader').length;
        overviewParts.push(el('p', {
          className: 'team-roster-meta', id: 'team-auto-roster-status',
          textContent: assigned ? '自动组队 · 已创建 ' + assigned + ' 个执行成员（同账号共用额度）'
            : team.state === 'running' ? '自动组队 · 领队正在按任务创建执行成员…' : '自动组队 · 尚未创建执行成员'
        }));
      }

      const showModels = hasModels() || !!(team.models && Object.keys(team.models).length) || roster.some(r => r.model);
      const showEfforts = hasEffort() || !!(team.efforts && Object.keys(team.efforts).length) || roster.some(r => r.effort);
      if (memberInfos.length) {
        const liveCount = memberInfos.filter(m => m.liveTerm).length;
        const histCount = memberInfos.filter(m => !m.liveTerm && m.canOpen).length;
        const memberHint = liveCount && histCount ? liveCount + ' 个对话中 · ' + histCount + ' 个可查历史'
          : liveCount ? liveCount + ' 个进行中对话 · 点击查看'
          : histCount ? histCount + ' 个历史对话可查看'
          : '等待成员开始';
        const chips = memberInfos.map(info => {
          const modelId = modelOf(team, info.role);
          const effortId = effortOf(team, info.role);
          const showRoleEffort = !!effortId || (info.role === 'leader' && team.mode === 'lead' && showEfforts);
          const tags = [];
          if (showModels) tags.push(el('span', { className: 'model', textContent: modelLabel(info.tool, modelId) }));
          if (showRoleEffort) tags.push(el('span', { className: 'effort', textContent: effortTag(effortId) }));
          return el('button', {
            type: 'button',
            id: 'member-' + info.role.replace(/[^a-z0-9_-]/gi, '_'),
            className: 'team-chip' + (info.active ? ' busy' : '') + (info.asking ? ' ask' : '') + (info.failed ? ' fail' : '') + (info.canOpen ? ' openable' : ''),
            'data-route': info.route,
            disabled: !info.canOpen,
            onclick: () => openMember(info)
          },
            el('span', { className: 'dot ' + (info.tool === 'claude' ? 'n-cc' : info.tool === 'agy' ? 'n-ag' : 'n-cx'), textContent: SHORT[info.tool] || 'AI' }),
            el('span', { className: 'member-main' },
              el('span', { className: 'name', textContent: roleName(team, info.role) }),
              tags.length ? el('span', { className: 'member-tags' }, ...tags) : ''),
            el('span', { className: 'member-tail' },
              el('small', { className: 'status-tag ' + info.statusTone, textContent: info.statusText }),
              info.canOpen ? el('span', { className: 'member-chev', ariaHidden: 'true', html: page.ICON.chev }) : ''));
        });
        overviewParts.push(el('section', { className: 'team-members-wrap', ariaLabel: '成员状态' },
          el('div', { className: 'team-subhead' },
            el('h3', { textContent: '协作成员' }),
            el('span', { textContent: memberHint })),
          el('div', { className: 'team-members', ariaLabel: '协作成员对话' }, ...chips)));
      }

      if (team._loading) {
        overviewParts.push(kit.note('正在读取协作详情…'));
      } else if (team._error) {
        overviewParts.push(el('div', { className: 'team-models-error', role: 'alert' },
          el('span', { textContent: team._error }),
          el('button', { type: 'button', className: 'team-models-retry', textContent: '重试', onclick: () => { currentTeam = Object.assign({}, currentTeam, { _loading: true, _error: '' }); renderStream('preserve'); hydrate(); } })));
      }
      overviewBox.replaceChildren(...overviewParts);

      // Parallel compare candidates viewer & merge picker
      if (team.mode === 'compare' && Array.isArray(team.candidates) && team.candidates.length) {
        if (!team.candidates.some(c => c.tool === activeCandidate)) activeCandidate = team.candidates[0].tool;
        const chosen = team.candidates.find(c => c.tool === activeCandidate) || team.candidates[0];
        const tabs = team.candidates.map(c => el('button', {
          type: 'button', id: 'compare-tab-' + c.tool,
          className: 'team-tab' + (c.tool === chosen.tool ? ' on' : '') + (team.picked === c.tool ? ' picked' : ''),
          onclick: () => { activeCandidate = c.tool; renderStream('preserve'); }
        }, (TOOLS[c.tool] || c.tool) + (team.picked === c.tool ? ' · 已采纳' : '')));

        const cardBody = [
          el('div', { className: 'team-compare-head' },
            el('b', { textContent: TOOLS[chosen.tool] || chosen.tool }),
            el('span', { className: 'stat', textContent: chosen.stat || ((team.awaited || []).includes(chosen.tool) ? '正在完成…' : '暂无改动') })),
          chosen.summary ? renderRichBody(chosen.summary, 'team-compare-summary') : '',
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
                  renderStream('preserve'); renderFoot(); page.refresh();
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
                  renderStream('preserve'); renderFoot(); page.refresh();
                } catch (err) { toast(err.message); }
                finally { busyAct = false; }
              }
            }, '放弃全部'));
          cardBody.push(pickRow);
        }
        compareBox.hidden = false;
        compareBox.replaceChildren(el('section', { className: 'team-compare', id: 'team-compare' }, el('div', { className: 'team-tabs', role: 'tablist' }, ...tabs), el('div', { className: 'team-compare-card' }, ...cardBody)));
      } else {
        compareBox.hidden = true;
        compareBox.replaceChildren();
      }

      // Structured collaboration workflow timeline (oldest -> newest) with incremental DOM reuse
      const log = team.log || [];
      const tailSig = log.length ? (log[log.length - 1].at + ':' + log[log.length - 1].who + ':' + (log[log.length - 1].text || '').length) : '';
      let addedLogs = 0;
      if (mode === 'preserve' && log.length) {
        if (log.length > lastLogCount) addedLogs = log.length - lastLogCount;
        else if (lastLogTail && tailSig && tailSig !== lastLogTail) addedLogs = 1;
      }
      lastLogCount = log.length;
      lastLogTail = tailSig;

      const memberByRole = new Map(memberInfos.map(info => [info.role, info]));
      const parseAssignments = text => {
        const lines = String(text || '').replace(/\r\n/g, '\n').split('\n');
        const prose = [], assigns = [];
        const headRe = /^[\s>*\-_`]*ASSIGN[*_`]*[ \t]+[*_`]*(claude|codex|agy|member-[123])(?:[ \t]+(claude|codex|agy))?[*_`]*[ \t]*[:：][ \t]*(.+)$/i;
        let current = null;
        for (const line of lines) {
          const m = headRe.exec(line);
          if (m) {
            current = { role: m[1].toLowerCase(), tool: (m[2] || '').toLowerCase(), lines: [m[3].trim()] };
            assigns.push(current);
          } else if (current && /^[\s>*\-_`]*(DONE|VERIFIED)[*_`]*\s*[:：]/i.test(line)) {
            current = null;
            prose.push(line);
          } else if (current) {
            current.lines.push(line);
          } else {
            prose.push(line);
          }
        }
        return {
          prose: prose.join('\n').trim(),
          assigns: assigns.map(a => ({ role: a.role, tool: a.tool, body: a.lines.join('\n').trim() }))
        };
      };
      const isSystemPulse = step => {
        const t = String(step.text || '').trim();
        return (t.startsWith('开始：') || t.startsWith('收到：')) && step.who !== 'user' && step.who !== '目标' && step.who !== '结果' && step.who !== '切换' && step.who !== '验收';
      };
      const classifyStep = (step, parsed) => {
        if (step.who === '目标') return { kind: 'goal', tag: '协作目标', stage: '任务起点' };
        if (step.who === '切换') return { kind: 'switch', tag: '流程切换', stage: '' };
        if (step.who === '验收') return { kind: 'verify', tag: '验收依据', stage: '领队验收' };
        if (step.who === '结果') return { kind: 'outcome', tag: '最终结论', stage: '协作完成' };
        if (step.who === 'user') return { kind: 'mine', tag: '你的补充', stage: '' };
        if (isSystemPulse(step)) {
          const isRecv = String(step.text || '').trim().startsWith('收到：');
          return { kind: 'event', tag: isRecv ? '收到指令' : '已派发任务', stage: '' };
        }
        if (step.who.startsWith('discuss:')) return { kind: 'discuss', tag: '方案讨论', stage: '方案讨论' };
        if (step.who === 'leader') {
          if (/^[\s>*\-_`]*(VERIFIED|DONE)[*_`]*\s*[:：]/im.test(step.text || '')) return { kind: 'verify', tag: '验收结论', stage: '领队验收' };
          if (parsed.assigns.length) return { kind: 'dispatch', tag: '领队派工', stage: '分工派发' };
          return { kind: 'lead', tag: '领队回复', stage: '' };
        }
        if (step.who === 'writer') return { kind: 'work', tag: '写手交付', stage: '写手实现' };
        if (step.who === 'reviewer') return { kind: 'review', tag: '审阅意见', stage: '只读审查' };
        if (team.mode === 'compare') return { kind: 'work', tag: '候选方案', stage: '独立实现' };
        return { kind: 'work', tag: '成员交付', stage: '成员执行' };
      };

      const flowKeyCount = log.filter(s => !isSystemPulse(s)).length;
      if (!currentTeam._flowMode) currentTeam._flowMode = 'all';
      const flowMode = currentTeam._flowMode;

      activityBox.hidden = !log.length;
      if (log.length) {
        const showFilter = log.length > flowKeyCount && flowKeyCount > 0;
        const filterGroup = showFilter ? el('div', { className: 'team-flow-tabs', role: 'group', ariaLabel: '记录视图' },
          el('button', { type: 'button', className: 'team-flow-tab' + (flowMode === 'key' ? ' on' : ''), ariaPressed: String(flowMode === 'key'), textContent: '流程视图', onclick: () => { currentTeam._flowMode = 'key'; renderStream('preserve'); } }),
          el('button', { type: 'button', className: 'team-flow-tab' + (flowMode === 'all' ? ' on' : ''), ariaPressed: String(flowMode === 'all'), textContent: '全部 ' + log.length, onclick: () => { currentTeam._flowMode = 'all'; renderStream('preserve'); } })) : '';
        activityHead.replaceChildren(
          el('h3', { textContent: '协作流程' }),
          !showFilter ? el('span', { className: 'team-activity-count', textContent: log.length + ' 步' }) : '',
          filterGroup,
          log.length >= 3 ? el('button', { type: 'button', className: 'team-to-latest', textContent: '跳到最新', onclick: () => scrollToLatest(true) }) : ''
        );

        const existing = new Map();
        Array.from(streamList.children).forEach(node => {
          if (node.dataset && node.dataset.msgKey) existing.set(node.dataset.msgKey, node);
        });
        const occ = new Map();
        let roundSeen = 0, lastStage = '';
        const nextNodes = [];
        for (const step of log) {
          const isUser = step.who === 'user', isMeta = step.who === '目标' || step.who === '结果' || step.who === '切换' || step.who === '验收';
          const tool = isUser ? 'user' : isMeta ? '' : toolOf(team, step.who);
          const baseKey = step.at + ':' + step.who;
          const seq = occ.get(baseKey) || 0;
          occ.set(baseKey, seq + 1);
          const key = baseKey + ':' + seq;
          const parsed = step.who === 'leader' ? parseAssignments(step.text) : { prose: step.text || '', assigns: [] };
          const meta = classifyStep(step, parsed);
          if (flowMode === 'key' && meta.kind === 'event') continue;

          // Infer round heading when a new dispatch/write cycle begins
          let stageBanner = '';
          if (meta.kind === 'dispatch' || (meta.kind === 'work' && step.who === 'writer')) {
            roundSeen++;
            stageBanner = '第 ' + roundSeen + ' 轮 · ' + meta.stage;
          } else if (meta.stage && meta.stage !== lastStage && meta.kind !== 'goal') {
            stageBanner = meta.stage;
          }
          if (meta.stage) lastStage = meta.stage;

          const bodyText = meta.kind === 'dispatch' ? parsed.prose : (step.text || '');
          const compactEvent = meta.kind === 'event';
          const long = !compactEvent && (bodyText.length > 420 || bodyText.split('\n').length > 10);
          const open = compactEvent ? expanded.has(key) : (!long || expanded.has(key));
          const agoText = page.ago(step.at);
          const labelText = roleName(team, step.who);
          const memberLink = !isUser && !isMeta ? memberByRole.get(step.who) : null;
          const canJumpMember = !!(memberLink && memberLink.canOpen);
          const useMarkdown = !isUser && !compactEvent && step.who !== '目标' && step.who !== '切换';
          const richNow = useMarkdown && isRichReady();
          const sig = JSON.stringify([meta.kind, meta.tag, stageBanner, labelText, tool, agoText, step.text || '', open, long, richNow, canJumpMember]);
          const reused = existing.get(key);
          if (reused && reused.dataset.msgSig === sig) { nextNodes.push(reused); continue; }

          if (meta.kind === 'switch') {
            const li = el('li', { className: 'team-msg switch' },
              el('span', { className: 'switch-dot', ariaHidden: 'true' }),
              el('span', { className: 'switch-text', textContent: step.text || '' }),
              el('span', { className: 'at', textContent: agoText }));
            li.dataset.msgKey = key; li.dataset.msgSig = sig;
            nextNodes.push(li);
            continue;
          }

          if (compactEvent) {
            const summaryLine = String(step.text || '').replace(/^(开始|收到)：\s*/, '').replace(/\s+/g, ' ').trim();
            const li = el('li', { className: 'team-msg compact' + (open ? ' expanded' : '') },
              el('button', {
                type: 'button', className: 'compact-row',
                onclick: () => { if (expanded.has(key)) expanded.delete(key); else expanded.add(key); renderStream('preserve'); }
              },
                el('span', { className: 'badge ' + (tool === 'claude' ? 'n-cc' : tool === 'agy' ? 'n-ag' : 'n-cx'), textContent: SHORT[tool] || 'AI' }),
                el('span', { className: 'compact-who', textContent: labelText }),
                el('span', { className: 'action-tag subtle', textContent: meta.tag }),
                el('span', { className: 'compact-preview', textContent: summaryLine }),
                el('span', { className: 'at', textContent: agoText })),
              open ? el('p', { className: 'body compact-full', textContent: step.text }) : '');
            li.dataset.msgKey = key; li.dataset.msgSig = sig;
            nextNodes.push(li);
            continue;
          }

          const contentNodes = [];
          if (stageBanner) contentNodes.push(el('div', { className: 'team-stage-pill', textContent: stageBanner }));
          contentNodes.push(el('div', { className: 'head' },
            !isMeta ? el('span', { className: 'badge ' + (isUser ? 'n-me' : tool === 'claude' ? 'n-cc' : tool === 'agy' ? 'n-ag' : 'n-cx'), textContent: SHORT[tool] || 'AI' }) : '',
            el('b', { textContent: labelText }),
            el('span', { className: 'action-tag kind-' + meta.kind, textContent: meta.tag }),
            canJumpMember ? el('button', { type: 'button', className: 'team-msg-open', textContent: '查看对话', onclick: () => openMember(memberLink) }) : '',
            el('span', { className: 'at', textContent: agoText })));

          if (bodyText) {
            contentNodes.push(useMarkdown
              ? renderRichBody(bodyText, 'body' + (open ? '' : ' clamp'))
              : el('p', { className: 'body' + (open ? '' : ' clamp'), textContent: bodyText }));
          }
          if (parsed.assigns.length) {
            contentNodes.push(el('div', { className: 'team-assign-list' }, ...parsed.assigns.map(item => {
              const targetTool = item.tool || toolOf(team, item.role);
              const targetTitle = roleTitle(item.role) ? roleTitle(item.role) + (targetTool ? ' · ' + (TOOLS[targetTool] || targetTool) : '') : (TOOLS[targetTool] || item.role);
              return el('div', { className: 'team-assign-card' },
                el('div', { className: 'team-assign-head' },
                  el('span', { className: 'badge ' + (targetTool === 'claude' ? 'n-cc' : targetTool === 'agy' ? 'n-ag' : 'n-cx'), textContent: SHORT[targetTool] || 'AI' }),
                  el('b', { textContent: targetTitle }),
                  el('span', { className: 'action-tag kind-dispatch', textContent: '分派任务' })),
                useMarkdown ? renderRichBody(item.body, 'team-assign-body') : el('p', { className: 'team-assign-body', textContent: item.body }));
            })));
          }
          if (long) {
            contentNodes.push(el('button', {
              type: 'button', className: 'more-btn', textContent: open ? '收起' : '展开全文',
              onclick: () => { if (expanded.has(key)) expanded.delete(key); else expanded.add(key); renderStream('preserve'); }
            }));
          }
          const li = el('li', { className: 'team-msg kind-' + meta.kind + (isUser ? ' mine' : isMeta ? ' meta' : '') }, ...contentNodes);
          li.dataset.msgKey = key;
          li.dataset.msgSig = sig;
          nextNodes.push(li);
        }

        let cursor = streamList.firstChild;
        for (const node of nextNodes) {
          if (node === cursor) cursor = cursor.nextSibling;
          else streamList.insertBefore(node, cursor);
        }
        while (cursor) { const next = cursor.nextSibling; cursor.remove(); cursor = next; }
      } else {
        streamList.replaceChildren();
      }

      if (bodyScroll) {
        if (mode === 'top') {
          bodyScroll.scrollTop = 0;
          unreadLogs = 0;
          updateJump();
        } else if (mode === 'bottom') {
          unreadLogs = 0;
          updateJump();
          bodyScroll.scrollTop = bodyScroll.scrollHeight;
        } else {
          if (wasAtBottom && addedLogs > 0) {
            unreadLogs = 0;
            updateJump();
            bodyScroll.scrollTop = bodyScroll.scrollHeight;
          } else if (scrolledDown) {
            if (addedLogs > 0) unreadLogs += addedLogs;
            updateJump();
            if (anchorKey) {
              const target = Array.from(streamList.children).find(n => n.dataset.msgKey === anchorKey);
              if (target) {
                const delta = (target.getBoundingClientRect().top - bodyScroll.getBoundingClientRect().top) - anchorOffset;
                if (Math.abs(delta) > 0.5) bodyScroll.scrollTop += delta;
              }
            } else if (anchorEl && anchorEl.isConnected) {
              const delta = (anchorEl.getBoundingClientRect().top - bodyScroll.getBoundingClientRect().top) - anchorOffset;
              if (Math.abs(delta) > 0.5) bodyScroll.scrollTop += delta;
            }
          } else {
            // Stay at top viewing stage & member status; never yank the user down to the bottom.
            if (addedLogs > 0) unreadLogs += addedLogs;
            updateJump();
            bodyScroll.scrollTop = prevScrollTop;
          }
        }
      }
    }

    const isRosterLeadOf = team => team.mode === 'lead' && (rosterOf(team).length > 0 || hasRoster());
    const canSwap = team => hasV2() && team.mode !== 'compare' && (team.state === 'running' || team.state === 'paused') && able().length >= (isRosterLeadOf(team) ? 1 : 2);

    // 换 AI 接手: a page of the detail's sheet, reached from its menu (it takes the menu's place, so it goes back to the detail)
    function swapPage() {
      const team = currentTeam, tools = able(), roster = rosterOf(team);
      const isRosterLead = isRosterLeadOf(team);
      const roleChoices = team.mode === 'lead' && roster.length
        ? roster.map(r => [r.role, roleName(team, r.role)])
        : team.mode === 'review'
        ? [['writer', '写手 · ' + (TOOLS[team.tools[0]] || team.tools[0])], ['reviewer', '审查 · ' + (TOOLS[team.tools[1]] || team.tools[1])]]
        : [['leader', '领队 · ' + (TOOLS[team.tools[0]] || team.tools[0])]].concat(team.tools.slice(1).map(t => [t, '执行成员 · ' + (TOOLS[t] || t)]));
      const currentOf = r => toolOf(team, r);
      const failedAwaited = (team.awaited || []).find(r => team.failed && (team.failed[r] || team.failed[currentOf(r)]));
      const initialRole = failedAwaited || ((team.awaited || []).find(r => roleChoices.some(([k]) => k === r))) || roleChoices[0][0];
      const initialTool = tools.find(t => t !== currentOf(initialRole)) || tools[0];
      const keptModels = Object.assign({}, team.models || {});
      const modelForChoice = (role, tool) => tool === currentOf(role) ? modelOf(team, role) : (keptModels[tool] || '');
      const effortForChoice = (role, tool, model) => tool === currentOf(role) ? reconcileEffort(tool, model, effortOf(team, role)) : '';
      const initialModel = modelForChoice(initialRole, initialTool);
      const state = { role: initialRole, tool: initialTool, model: initialModel, effort: effortForChoice(initialRole, initialTool, initialModel), models: keptModels, note: '' };
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
        const rows = [];
        const sel = makeModelSelect({
          id: 'team-swap-model',
          tool: state.tool,
          value: state.model,
          ariaLabel: '接手 AI 的模型',
          onchange: val => {
            state.model = val;
            state.models[state.tool] = val;
            state.effort = reconcileEffort(state.tool, val, state.effort);
            renderSwapModel();
          }
        });
        rows.push(kit.row({ icon: state.tool, tint: state.tool, label: '模型', detail: TOOLS[state.tool] || state.tool, end: sel }));
        if (state.role === 'leader' && hasEffort()) {
          const efforts = effortListFor(state.tool, state.model);
          const selectable = efforts.length > 0 || (!!state.effort && !modelCatalog);
          const effortSel = makeEffortSelect({
            id: 'team-swap-effort',
            tool: state.tool,
            model: state.model,
            value: selectable ? state.effort : '',
            className: 'team-model-select',
            ariaLabel: '领队思考深度',
            disabled: !selectable,
            onchange: val => { state.effort = val; }
          });
          rows.push(kit.row({ icon: 'spark', tint: state.tool, label: '思考深度', detail: selectable ? '按 CLI 目录' : '工具默认', end: effortSel }));
        }
        nodes.push(kit.group(...rows));
        swapModelBox.replaceChildren(kit.section(state.role === 'leader' && hasEffort() ? '接手模型与思考深度' : '接手模型', nodes, '不选则使用该工具默认配置'));
      }
      if (hasModels() && (!modelCatalog || modelError)) {
        loadModels().then(() => { if (activeSheet && !activeSheet.gone) { state.effort = reconcileEffort(state.tool, state.model, state.effort); renderSwapModel(); } });
      }
      return { title: '换 AI 接手', build(p) {
        if (menuButton) menuButton.hidden = true;
        p.body.append(kit.section('要替换或转交的角色', kit.chips(roleChoices, { chosen: state.role, label: '替换角色' }, val => {
          state.role = val;
          if (!isRosterLead && state.tool === currentOf(val)) {
            state.tool = tools.find(t => t !== currentOf(val)) || tools[0];
          }
          state.model = modelForChoice(state.role, state.tool);
          state.effort = effortForChoice(state.role, state.tool, state.model);
          p.refresh();
        })));
        const choices = namedTools(isRosterLead ? tools : tools.filter(t => t !== currentOf(state.role)));
        p.body.append(kit.section('交给哪个 AI 继续', kit.chips(choices, { chosen: state.tool, label: '接手 AI' }, val => {
          state.tool = val;
          state.model = modelForChoice(state.role, val);
          state.effort = effortForChoice(state.role, val, state.model);
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
          if (hasModels() && (state.model || (isRosterLead && modelOf(team, state.role)))) payload.model = state.model;
          if (hasEffort() && state.role === 'leader') {
            const cleanEffort = reconcileEffort(state.tool, state.model, state.effort);
            if (cleanEffort || effortOf(team, state.role)) payload.effort = cleanEffort;
          }
          if (state.note.trim()) payload.note = state.note.trim();
          const res = await work('team_swap', payload);
          updateTeamLocal(res.team);
          lastKey = keyOf(currentTeam);
          toast('已交给 ' + (TOOLS[state.tool] || state.tool) + ' 接手');
          p.sheet.goBack();
          page.refresh();
        }, { busy: '正在切换…', id: 'team-swap-go' });
        p.foot.append(...go.nodes);
      } };
    }

    // whom a message goes to, named short for the composer's one line (the member cards name each in full)
    function targetOptions(team) {
      const first = ['', team.mode === 'compare' ? '全部候选' : '自动分配'];
      const roster = rosterOf(team);
      if (team.mode === 'lead' && roster.length) {
        return [first].concat(roster.map(r => [r.role, roleTitle(r.role).replace('执行成员', '成员') || TOOLS[r.tool] || r.tool]));
      }
      return [first].concat(
        (team.mode === 'review' ? [['writer', '写手'], ['reviewer', '审查']]
          : team.mode === 'lead' ? [['leader', '领队']].concat((team.tools || []).slice(1).map(t => [t, TOOLS[t] || t]))
          : (team.tools || []).map(t => [t, TOOLS[t] || t]))
      );
    }

    // What is done to the task as a whole, refreshed from the computer afterwards.
    async function act(action, after, said) {
      if (busyAct) return;
      busyAct = true;
      try {
        const res = await work(action, { team: currentTeam.id });
        if (res && res.team) updateTeamLocal(res.team);
        await load();
        currentTeam = (teams || []).find(t => t.id === known.id) || Object.assign({}, currentTeam, after);
        lastKey = keyOf(currentTeam);
        renderStream('preserve'); renderFoot(); page.refresh(); toast(said);
      }
      catch (err) { toast(err.message); } finally { busyAct = false; }
    }
    const resume = () => act('team_resume', { state: 'running', note: '' }, '已继续');

    // The menu at the top right: what is done seldom, or cannot be taken back (removing asks first).
    function menuPage() {
      return { title: '协作任务', build(p) {
        if (menuButton) menuButton.hidden = true;
        const team = currentTeam, terminals = (page.data && page.data.terminals) || [];
        const asks = memberRolesOf(team).map(role => inspectMember(team, role, terminals)).filter(info => info.asking);
        const badge = badgeInfo(team, asks);
        p.body.append(kit.who({ icon: 'spark', tint: 'agy', title: team.goal || '协作任务', line: (MODES[team.mode] || '协作') + (team.dir ? ' · ' + team.dir : '') + ' · ' + badge[0], state: TONE_DOT[badge[1]] || '' }));
        const rows = [];
        if (canSwap(team)) rows.push(kit.row({ id: 'team-swap', icon: 'hand', tint: 'muted', label: '换 AI 接手', go: () => p.sheet.replace(swapPage()) }));
        if (team.state === 'running') rows.push(kit.row({ id: 'team-stop', icon: 'stop', tint: 'muted', label: '停止', act: () => { p.sheet.goBack(); act('team_stop', { state: 'stopped' }, '已停止'); } }));
        if (rows.length) p.body.append(kit.group(...rows));
        if (team.state !== 'running') {
          p.body.append(kit.group(kit.row({ id: 'team-remove', label: '删除', danger: true, act: () => p.sheet.push(kit.confirm({
            title: '删除协作任务', text: '会关闭它打开的领队和成员对话，聊天记录仍在历史中。', label: '删除', kind: 'danger', busy: '正在删除…',
            act: async () => {
              await work('team_remove', { team: currentTeam.id });
              closeRemoved(false);
              await load();
              page.refresh();
              toast('已删除');
            }
          })) })));
        }
      } };
    }

    // The foot is one line to write in, as on the conversation page: whom it goes to, the words, and send. While the
    // task is paused and nothing is written, the button there is 继续.
    function renderFoot() {
      if (!footBox) return;
      const team = currentTeam, v2 = hasV2();
      const canSay = v2 && team.state !== 'failed' && !(team.mode === 'compare' && team.state === 'done');
      const paused = team.state === 'paused';

      let hint = footBox.querySelector('.team-upgrade-hint');
      if (!v2 && team.state !== 'failed') {
        if (!hint) { hint = el('p', { className: 'team-upgrade-hint', textContent: '当前电脑端版本较旧，更新电脑端后支持中途补充说明、换 AI 接手与继续下一轮' }); footBox.append(hint); }
      } else if (hint) { hint.remove(); }

      let form = footBox.querySelector('#team-say-form');
      const lone = footBox.querySelector('.team-resume-only');
      if (!canSay) {
        if (form) form.remove();
        if (paused && !lone) footBox.append(el('button', { type: 'button', id: 'team-resume', className: 'team-resume team-resume-only', textContent: '继续', onclick: resume }));
        if (!paused && lone) lone.remove();
        return;
      }
      if (lone) lone.remove();

      const placeholder = team.state === 'running' ? '插话补充或纠正'
        : paused ? '补充说明并继续'
        : team.state === 'choose' ? '对候选方案提意见'
        : '新要求，接着做';
      const targets = targetOptions(team);

      if (form) {
        const input = form.querySelector('#team-say-input');
        const hadFocus = document.activeElement === input;
        if (input.placeholder !== placeholder) input.placeholder = placeholder;
        const sel = form.querySelector('#team-say-target');
        const sig = JSON.stringify(targets);
        if (sel.dataset.sig !== sig) {
          sel.dataset.sig = sig;
          if (!targets.some(([v]) => v === targetRole)) targetRole = '';
          sel.replaceChildren(...targets.map(([val, label]) => el('option', { value: val, textContent: label, selected: val === targetRole })));
        }
        form.showAction();
        if (hadFocus) input.focus();
        return;
      }

      const input = el('textarea', {
        id: 'team-say-input', rows: 1, maxLength: 4000, value: draftText, placeholder, ariaLabel: '向协作任务发消息'
      });
      const grow = () => { input.style.height = 'auto'; input.style.height = Math.min(120, Math.max(38, input.scrollHeight)) + 'px'; };
      const targetSel = el('select', { id: 'team-say-target', className: 'team-target', ariaLabel: '发送对象' },
        ...targets.map(([val, label]) => el('option', { value: val, textContent: label, selected: val === targetRole })));
      targetSel.dataset.sig = JSON.stringify(targets);
      targetSel.addEventListener('change', () => { targetRole = targetSel.value; });
      const resumeBtn = el('button', { type: 'button', id: 'team-resume', className: 'team-resume', textContent: '继续', onclick: resume });
      const sendBtn = el('button', { type: 'submit', id: 'team-say-send', className: 'team-send', textContent: '发送' });
      form = el('form', { id: 'team-say-form', className: 'team-composer' }, targetSel, input, resumeBtn, sendBtn);
      form.showAction = () => {
        const resuming = currentTeam.state === 'paused' && !input.value.trim();
        resumeBtn.hidden = !resuming;
        sendBtn.hidden = resuming;
      };
      input.addEventListener('input', () => { draftText = input.value; grow(); form.showAction(); });
      input.addEventListener('keydown', event => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) { event.preventDefault(); form.requestSubmit(); } });
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
          renderStream('bottom'); renderFoot(); page.refresh();
          toast(currentTeam.state === 'running' ? '已发给协作任务' : '已发送');
        } catch (err) { toast(err.message); }
        finally { sending = false; sendBtn.disabled = false; sendBtn.textContent = '发送'; form.showAction(); }
      });
      form.showAction();
      footBox.append(form);
      grow();
    }

    let builtOnce = false;
    const sheet = window.RemoteSheet.open({ full: true, title: (MODES[known.mode] || '协作') + (known.dir ? ' · ' + known.dir : ''), build(p) {
      activeSheet = p.sheet;
      activePage = p;
      currentTeam = (teams || []).find(t => t.id === known.id) || currentTeam;
      lastKey = keyOf(currentTeam);
      bodyScroll = p.sheet.body;
      if (!menuButton) {
        menuButton = el('button', { type: 'button', id: 'team-menu', className: 'rs-icon team-menu', ariaLabel: '协作任务选项', html: MENU_ICON, onclick: () => { if (activeSheet.stack.length === 1) activeSheet.push(menuPage()); } });
        p.sheet.top.querySelector('.rs-bar').append(menuButton);
      }
      menuButton.hidden = false;
      overviewBox = el('div', { className: 'team-overview' });
      compareBox = el('div', { className: 'team-compare-slot', hidden: true });
      activityHead = el('div', { className: 'team-activity-head' });
      streamList = el('ol', { className: 'team-stream', id: 'team-stream' });
      activityBox = el('section', { className: 'team-activity', ariaLabel: '活动记录', hidden: true }, activityHead, streamList);
      jumpBtn = el('button', { type: 'button', id: 'team-jump-latest', className: 'team-jump-latest', hidden: true, onclick: () => scrollToLatest(true) });
      streamBox = el('div', { className: 'team-view' }, overviewBox, el('div', { className: 'team-main' }, compareBox, activityBox, jumpBtn));
      p.body.append(streamBox);
      footBox = p.foot;
      const initialMode = builtOnce ? 'preserve' : 'top';
      builtOnce = true;
      renderStream(initialMode);
      renderFoot();
    } }, '协作任务');

    const onBodyScroll = () => updateJump();
    if (bodyScroll) bodyScroll.addEventListener('scroll', onBodyScroll, { passive: true });

    const syncLocal = () => {
      if (sheet.gone || activeSheet && activeSheet.stack.length > 1) return;
      const latest = (teams || []).find(t => t.id === known.id);
      if (latest) currentTeam = latest;
      const key = keyOf(currentTeam);
      if (key !== lastKey) {
        lastKey = key;
        renderStream('preserve');
        renderFoot();
      }
    };
    activeSync = syncLocal;

    async function poll() {
      if (sheet.gone || document.hidden) return;
      if (currentTeam.state !== 'running' && currentTeam.state !== 'choose' && currentTeam.state !== 'paused') {
        syncLocal();
        return;
      }
      const fresh = await load();
      if (sheet.gone) return;
      const latest = (teams || []).find(t => t.id === known.id);
      if (!latest) {
        if (fresh && !currentTeam._loading) closeRemoved(true);
        return;
      }
      const key = keyOf(latest);
      if (key !== lastKey) {
        currentTeam = latest;
        lastKey = key;
        if (activeSheet && activeSheet.stack.length === 1) {
          renderStream('preserve');
          renderFoot();
        }
        page.refresh();
      }
    }

    if (currentTeam._loading || !teams || page.tab !== 'together') hydrate();
    if (page.loadMarkdown && !isRichReady()) {
      page.loadMarkdown().then(ok => { if (ok && !sheet.gone && activeSheet && activeSheet.stack.length === 1) renderStream('preserve'); });
    }
    if (hasModels() && !modelCatalog && !modelLoading) {
      loadModels().then(() => { if (!sheet.gone && activeSheet && activeSheet.stack.length === 1) renderStream('preserve'); });
    }

    const onVisible = () => { if (!document.hidden && !sheet.gone) poll(); };
    document.addEventListener('visibilitychange', onVisible);
    const timer = setInterval(poll, 3500);
    if (sheet && sheet.result && sheet.result.finally) {
      sheet.result.finally(() => {
        clearInterval(timer);
        document.removeEventListener('visibilitychange', onVisible);
        if (bodyScroll) bodyScroll.removeEventListener('scroll', onBodyScroll);
        if (activeSync === syncLocal) activeSync = null;
      });
    }
  }

  function open(id, fallback) {
    if (!id) return;
    const found = (teams || []).find(t => t.id === id);
    if (found) { show(found); return; }
    const stub = Object.assign({
      id, dir: '', goal: '协作任务', mode: 'review', state: 'running', phase: '', note: '',
      tools: [], models: {}, awaited: [], members: {}, log: [], round: 1, rounds: undefined
    }, fallback || {}, { _loading: true, _error: '' });
    show(stub);
  }

  // ---- on 协作: its card for each, and one to begin another
  function card(team) {
    const state = stateInfo(team);
    const meta = el('span', { className: 'meta' },
      el('span', { className: 'pill ' + state[1], textContent: state[0] }),
      el('span', { textContent: (MODES[team.mode] || team.mode) + (team.auto_roster ? ' · 自组' : '') + (team.discuss ? ' · 先议' : '') + ' · ' + who(team) }),
      team.mode === 'compare' ? '' : el('span', { textContent: roundBadgeText(team) }));
    return el('li', { className: 'card rule' + (team.state === 'running' || team.state === 'paused' || team.state === 'choose' ? '' : ' off') }, el('button', { type: 'button', className: 'open', onclick: () => show(team) },
      el('span', { className: 'tool plain', html: TEAM }),
      el('span', { className: 'text' }, el('b', { textContent: team.goal }), meta, team.note ? el('span', { className: 'peek', textContent: team.note }) : ''),
      el('span', { className: 'chev', html: page.ICON.chev })));
  }
  function cards() {
    return (teams || []).map(card).concat(el('li', { className: 'card add' }, el('button', { type: 'button', className: 'open', id: 'team-new', onclick: begin, html: page.ICON.plus + '<span>新的协作任务</span>' })));
  }
  const status = () => failure ? '读取失败，请重试；显示上次读取的任务。' : teams === null ? '正在读取协作任务…' : teams.length ? '' : '还没有协作任务。支持写审循环、分工协作、先议后写，或在独立 worktree 中并行比稿。';
  const count = () => teams && teams.length ? teams.filter(t => t.state === 'running' || t.state === 'paused' || t.state === 'choose').length + ' 个进行中 / ' + teams.length + ' 个' : '';

  window.RemoteTeams = { TEAM, phaseLabel, load, loadModels, begin, show, open, sync: () => { if (activeSync) activeSync(); }, cards, status, count, get teams() { return teams; }, get failure() { return failure; } };
})();
