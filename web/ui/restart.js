/* 重启对话: the tool of a conversation started anew, going on with the same conversation. What the tool reads when it
   starts (the account signed in, a quota renewed, its settings) is read again, and a model or reasoning effort may be
   chosen to go on with. A member of a collaboration task is restarted by its task in its own terminal (`team_swap`
   with its own AI), and the task goes on with it; a conversation of its own is ended and gone on with in a new chat
   (`start` with its `session`, `model`, `effort`). docs/PROTOCOL.md, "Chats" and "The owner's work".

   RemoteRestart.page(work, conv, reopen)
     work     a RemoteWork (ui/work.js)
     conv     { terminal, tool, title, dir, session }
     reopen   async ({ model?, effort? }) => …: ends this chat and opens the same conversation again */
(() => {
  'use strict';
  const { ui } = window.RemoteSheet;
  const TOOLS = { claude: 'Claude Code', codex: 'Codex', agy: 'Antigravity CLI' };
  const tint = tool => ['claude', 'agy'].includes(tool) ? tool : 'codex';
  let catalog = null, asking = null;
  // the models each tool offers, as the computer lists them (`team_models`); asked once a page
  function models(work) {
    if (catalog || !work.has('teams-models')) return Promise.resolve(catalog);
    if (!asking) asking = work.work('team_models').then(answer => { catalog = window.RemoteTeamDefaults.normalizeModels(answer.models); return catalog; })
      .catch(() => null).finally(() => { asking = null; });
    return asking;
  }
  const effortName = effort => (window.RemoteTeamDefaults.EFFORT_LABELS || {})[effort] || effort;
  const defaults = () => window.RemoteTeamDefaults || null;

  function page(work, conv, reopen) {
    const state = { model: null, effort: null, member: undefined, asked: false };
    return {
      title: '重启对话',
      build(p) {
        if (!state.asked) {
          state.asked = true;
          work.ensure().then(() => work.member(conv.terminal)).then(member => { state.member = member; }, () => { state.member = null; }).then(p.refresh);
          models(work).then(p.refresh);
        }
        p.body.append(ui.who({ icon: conv.tool, tint: tint(conv.tool), title: conv.title || TOOLS[conv.tool] || '对话',
          line: (TOOLS[conv.tool] || conv.tool) + (conv.dir ? ' · ' + conv.dir : '') }));
        if (state.member === undefined) { p.body.append(ui.loading(), ui.loading()); return; }
        const list = (catalog && catalog[conv.tool]) || [];
        if (list.length) {
          p.body.append(ui.section('模型', ui.group(
            ui.row({ label: '沿用当前', checked: state.model === null, act: () => { state.model = null; state.effort = null; p.refresh(); } }),
            ...list.map(item => ui.row({ label: item.label, checked: state.model === item.id, act: () => {
              state.model = item.id;
              if (state.effort && !item.efforts.includes(state.effort)) state.effort = null;
              p.refresh();
            } })))));
          const chosen = list.find(item => item.id === state.model);
          if (chosen && chosen.efforts.length && work.has('teams-effort')) {
            p.body.append(ui.section('思考深度', ui.chips([['', '默认']].concat(chosen.efforts.map(effort => [effort, effortName(effort)])),
              { chosen: state.effort || '', label: '思考深度' }, value => { state.effort = value || null; })));
          }
        }
        const go = ui.go('重启对话', async () => {
          const choice = {};
          if (state.model !== null) choice.model = state.model;
          if (state.model !== null && state.effort) choice.effort = state.effort;
          if (state.member) {
            const payload = Object.assign({ team: state.member.team.id, role: state.member.role, tool: conv.tool }, choice);
            await work.work('team_swap', payload);
            p.sheet.close(null);
            work.toast('已重启');
            return;
          }
          await reopen(choice);
          p.sheet.close(null, true);
        }, { busy: '正在重启…', id: 'restart-go' });
        p.foot.append(...go.nodes);
      }
    };
  }

  /* Visual model, reasoning-effort, and permission-mode configuration sheet for an active chat conversation. */
  function paramsPage(work, conv, reopen) {
    const d = defaults();
    const state = {
      model: conv.model || '',
      effort: conv.effort || '',
      mode: conv.mode || '',
      remember: false,
      member: undefined,
      asked: false
    };
    return {
      title: '模型与参数',
      build(p) {
        if (!state.asked) {
          state.asked = true;
          work.ensure().then(() => work.member(conv.terminal)).then(member => { state.member = member; }, () => { state.member = null; }).then(p.refresh);
          models(work).then(p.refresh);
        }
        const list = (catalog && catalog[conv.tool]) || [];
        const dev = (work.data && work.data.device) || {};
        const maxMode = dev.max_mode || '';
        const chosen = d ? d.modelEntry(conv.tool, state.model, catalog) : list.find(item => item.id === state.model);
        const efforts = chosen && Array.isArray(chosen.efforts) ? chosen.efforts : [];
        if (state.effort && efforts.length && !efforts.includes(state.effort)) state.effort = '';

        const summaryParts = [
          d ? d.modelLabel(conv.tool, state.model, catalog) : (state.model || '工具默认'),
          state.effort ? ('思考 ' + (d ? d.shortEffortLabel(state.effort) : state.effort)) : '默认思考'
        ];
        if (conv.tool !== 'agy' && state.mode && d) summaryParts.push(d.modeLabel(state.mode, true));
        p.body.append(ui.who({
          icon: conv.tool, tint: tint(conv.tool),
          title: TOOLS[conv.tool] || conv.tool,
          line: summaryParts.join(' · ')
        }));
        if (state.member === undefined && !list.length && asking) {
          p.body.append(ui.loading(), ui.loading());
          return;
        }
        if (list.length) {
          const recId = d ? d.recommendedModel(conv.tool, catalog) : '';
          p.body.append(ui.section('模型', ui.group(
            ui.row({
              label: '工具默认',
              detail: '跟随电脑上 ' + (TOOLS[conv.tool] || conv.tool) + ' 自身的默认模型',
              checked: state.model === '',
              act: () => { state.model = ''; state.effort = ''; p.refresh(); }
            }),
            ...list.map(item => ui.row({
              label: item.label,
              value: item.id === recId ? '推荐' : item.isDefault ? '默认' : '',
              checked: state.model === item.id,
              act: () => {
                state.model = item.id;
                if (state.effort && !item.efforts.includes(state.effort)) state.effort = '';
                p.refresh();
              }
            }))
          )));
        } else if (work.has('teams-models')) {
          p.body.append(ui.note('正在读取可用模型列表，或电脑端暂未返回该工具的模型目录。'));
        }
        if (efforts.length && work.has('teams-effort')) {
          const effortChoices = [['', '默认']].concat(efforts.map(level => [level, d ? d.shortEffortLabel(level) : effortName(level)]));
          p.body.append(ui.section('思考深度', ui.chips(effortChoices, { chosen: state.effort || '', label: '思考深度' }, value => {
            state.effort = value || '';
            p.refresh();
          }), '控制模型推理与思考投入，越高越深入但耗时更长'));
        }
        if (!state.member && conv.tool !== 'agy' && work.has('chat-params') && d) {
          const allowedModes = d.MODE_ORDER.filter(m => d.modeAllowed(m, maxMode));
          const modeChoices = allowedModes.map(m => [m, d.MODE_LABELS[m]]);
          const currentMode = state.mode && allowedModes.includes(state.mode) ? state.mode : (allowedModes[0] || 'full');
          state.mode = currentMode;
          const capNote = maxMode && maxMode !== 'full' ? '（电脑端最高权限限制为「' + d.MODE_LABELS[maxMode] + '」）' : '';
          p.body.append(ui.section('权限模式', ui.seg(modeChoices, currentMode, value => {
            state.mode = value;
            p.refresh();
          }, { label: '权限模式', full: true }), (d.MODE_HINTS[currentMode] || '') + capNote));
        }
        if (d) {
          const sw = ui.el('button', {
            type: 'button', className: 'rs-switch', role: 'switch',
            'aria-checked': String(state.remember), 'aria-label': '记住为该工具默认参数',
            onclick: () => { state.remember = !state.remember; p.refresh(); }
          });
          p.body.append(ui.group(ui.row({
            label: '记住为该工具默认参数',
            detail: '之后新建 ' + (TOOLS[conv.tool] || conv.tool) + ' 对话与终端时自动带入',
            end: sw
          })));
        }
        const go = ui.go('应用并生效', async () => {
          if (state.remember && d) {
            d.setModelPref(conv.tool, state.model);
            d.setEffortPref(conv.tool, state.effort);
            if (!state.member && conv.tool !== 'agy' && state.mode) d.setModePref(conv.tool, state.mode);
          }
          const choice = { model: state.model, effort: state.effort };
          if (!state.member && conv.tool !== 'agy' && state.mode && work.has('chat-params')) choice.mode = state.mode;
          if (state.member) {
            const payload = Object.assign({ team: state.member.team.id, role: state.member.role, tool: conv.tool }, choice);
            await work.work('team_swap', payload);
            p.sheet.close(null);
            work.toast('已切换模型参数');
            return;
          }
          await reopen(choice);
          p.sheet.close(null, true);
        }, { busy: '正在应用参数…', id: 'params-apply' });
        p.foot.append(...go.nodes);
      }
    };
  }

  window.RemoteRestart = { page, paramsPage, models };
})();
