/* The switcher behind the model button at the end of a conversation's box: the models of the conversation's tool,
   one tap to go on with another, and the other AIs on the computer, one tap to hand the conversation to a new
   conversation of that AI (the computer's summary, `brief`, as its first message). Effort and permission mode stay
   on the fuller page (ui/restart.js, paramsPage).

   RemoteSwitch.page(work, conv, { reopen, busy })
     work     a RemoteWork (ui/work.js)
     conv     { id (the session), terminal, tool, title, dir, model, effort, mode }
     reopen   async ({ model, effort, mode }) => …: ends this chat and goes on with the same conversation
     busy     () => '' or why the model cannot be changed now (a turn at work) */
(() => {
  'use strict';
  const { ui } = window.RemoteSheet;
  const TOOLS = { claude: 'Claude Code', codex: 'Codex', agy: 'Antigravity CLI' };
  const tint = tool => ['claude', 'agy'].includes(tool) ? tool : 'codex';

  function page(work, conv, { reopen, busy }) {
    const state = { asked: false, member: undefined, going: '', catalog: null };
    const d = window.RemoteTeamDefaults || null;
    return {
      title: '切换模型',
      build(p) {
        if (!state.asked) {
          state.asked = true;
          work.ensure().then(() => work.member(conv.terminal)).then(member => { state.member = member; }, () => { state.member = null; }).then(p.refresh);
          window.RemoteRestart.models(work).then(found => { state.catalog = found; p.refresh(); });
        }
        if (state.member === undefined) { p.body.append(ui.loading(), ui.loading()); return; }
        const catalog = state.catalog || null;
        const list = (catalog && catalog[conv.tool]) || [];
        const why = busy() || (work.has('teams-models') || work.has('chat-params') ? '' : '电脑端更新后才能在这里选择模型');
        const go = async (key, run) => {
          if (state.going) return;
          state.going = key; p.refresh();
          try { await run(); } catch (error) { state.going = ''; p.refresh(); work.toast(error.message || '没有完成'); }
        };

        // the models of this tool: the one in use ticked, another taken at a tap
        const pick = model => {
          if (model === (conv.model || '')) return p.sheet.close(null);
          if (why) return work.toast(why);
          const entry = list.find(item => item.id === model);
          const effort = conv.effort && entry && (entry.efforts || []).includes(conv.effort) ? conv.effort : '';
          go('model:' + model, async () => {
            if (state.member) {
              await work.work('team_swap', { team: state.member.team.id, role: state.member.role, tool: conv.tool, model, effort });
              p.sheet.close(null);
              work.toast('已切换模型');
              return;
            }
            const choice = Object.assign({ model, effort }, conv.mode && conv.tool !== 'agy' && work.has('chat-params') ? { mode: conv.mode } : {});
            await reopen(choice);
            p.sheet.close(null, true);
          });
        };
        const going = key => state.going === key ? '正在切换…' : '';
        const models = [{ id: '', label: '工具默认' }].concat(list.filter(item => item.id));
        if (conv.model && !list.some(item => item.id === conv.model)) models.push({ id: conv.model, label: d ? d.modelLabel(conv.tool, conv.model, catalog) : conv.model });
        p.body.append(ui.section(TOOLS[conv.tool] || conv.tool, ui.group(
          ...models.map(item => ui.row({ icon: conv.tool, tint: tint(conv.tool), label: item.label, value: going('model:' + item.id) || (item.isDefault ? '默认' : ''),
            checked: item.id === (conv.model || ''), disabled: !!state.going, act: () => pick(item.id) })),
          ui.row({ icon: 'sliders', tint: 'muted', label: '思考深度与权限', go: () => {
            if (why) return work.toast(why);
            p.sheet.push(window.RemoteRestart.paramsPage(work, conv, reopen));
          } })), why && !state.going ? why : ''));

        // the other AIs: a new conversation of one, in the same project, that goes on from this one
        const others = work.freshTools(conv).filter(tool => tool !== conv.tool);
        if (!others.length) return;
        const left = tool => { const n = work.quotaLeft(tool); return n === null ? '' : '剩余 ' + n + '%'; };
        const noSession = conv.id ? '' : '发出第一条消息后才能转交';
        p.body.append(ui.section('转交给', ui.group(...others.map(tool => ui.row({
          icon: tool, tint: tint(tool), label: TOOLS[tool], value: going('to:' + tool) || left(tool), disabled: !!state.going,
          act: () => {
            if (noSession) return work.toast(noSession);
            // a member of a collaboration task is handed over within its task, on the hand-over page
            if (state.member) return work.handoff(p.sheet, conv);
            go('to:' + tool, async () => {
              const started = await work.handToNew(conv, tool, '');
              p.sheet.close({ started, tool }, true);
              work.leaveTo(started);
            });
          } })))));
      }
    };
  }

  window.RemoteSwitch = { page };
})();
