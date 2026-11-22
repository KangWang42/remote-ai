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
  window.RemoteRestart = { page };
})();
