/* The switcher behind the tool button at the end of a conversation's box: a row of available AIs across the top,
   and ONLY the selected AI's models listed below. Picking a model of the current AI switches in place (`reopen` /
   `team_swap`); picking another AI's model stages an in-place handover for the next message (or falls back to a new
   conversation on older computers without `handover`).

   RemoteSwitch.page(work, conv, { reopen, stage, pending, busy })
     work     a RemoteWork (ui/work.js)
     conv     { id (the session), terminal, tool, title, dir, model, effort, mode }
     reopen   async ({ model, effort, mode }) => …: ends this chat and goes on with the same conversation
     stage    (choice) => void: stages { tool, model, effort } for the next message, or null to cancel
     pending  current staged switch { tool, model, effort } or null
     busy     () => '' or why the model/AI cannot be changed now (a turn at work) */
(() => {
  'use strict';
  const { ui } = window.RemoteSheet, { el } = ui;
  const TOOLS = { claude: 'Claude Code', codex: 'Codex', agy: 'Antigravity CLI' };
  const SHORT = { claude: 'Claude', codex: 'Codex', agy: 'Agy' };
  const tint = tool => ['claude', 'agy'].includes(tool) ? tool : 'codex';

  function page(work, conv, { reopen, stage, pending, busy }) {
    const state = {
      asked: false,
      member: undefined,
      going: '',
      catalog: null,
      viewTool: (pending && pending.tool) || conv.tool
    };
    const d = window.RemoteTeamDefaults || null;
    return {
      title: '切换 AI 与模型',
      build(p) {
        if (!state.asked) {
          state.asked = true;
          work.ensure().then(() => work.member(conv.terminal)).then(member => { state.member = member; }, () => { state.member = null; }).then(p.refresh);
          window.RemoteRestart.models(work).then(found => { state.catalog = found; p.refresh(); });
        }
        if (state.member === undefined) { p.body.append(ui.loading(), ui.loading()); return; }
        const catalog = state.catalog || null;
        const fresh = work.freshTools(conv);
        const tools = fresh.includes(conv.tool) ? fresh : [conv.tool].concat(fresh);
        if (!tools.includes(state.viewTool)) state.viewTool = conv.tool;
        const activeTool = state.viewTool;
        const isCurrent = activeTool === conv.tool;
        const list = (catalog && catalog[activeTool]) || [];
        const left = tool => { const n = work.quotaLeft(tool); return n === null ? '' : '剩 ' + n + '%'; };
        const whyBusy = busy();
        const whyModel = whyBusy || (work.has('teams-models') || work.has('chat-params') ? '' : '电脑端更新后才能在这里选择模型');
        const go = async (key, run) => {
          if (state.going) return;
          state.going = key; p.refresh();
          try { await run(); } catch (error) { state.going = ''; p.refresh(); work.toast(error.message || '没有完成'); }
        };

        // Top row: horizontal AI tabs so switching agents needs zero scrolling past long model lists.
        if (tools.length > 1) {
          const bar = el('div', { className: 'rs-agent-bar', role: 'tablist', ariaLabel: '选择 AI' },
            ...tools.map(tool => {
              const q = left(tool);
              const selected = tool === activeTool;
              return el('button', {
                type: 'button',
                role: 'tab',
                className: 'rs-agent-tab rs-tint-' + tint(tool) + (tool === conv.tool ? ' is-current' : ''),
                ariaSelected: String(selected),
                dataTool: tool,
                disabled: !!state.going,
                onclick: () => { state.viewTool = tool; p.refresh(); }
              },
              el('span', { className: 'rs-agent-ico', html: ui.icon(tool, 18) }),
              el('span', { className: 'rs-agent-name', textContent: SHORT[tool] || tool }),
              q ? el('small', { className: 'rs-agent-quota', textContent: q }) : '');
            }));
          p.body.append(bar);
        }

        const going = key => state.going === key ? '正在切换…' : '';
        const models = [{ id: '', label: '工具默认' }].concat(list.filter(item => item.id));

        if (isCurrent) {
          const currentModel = conv.model || '';
          if (currentModel && !list.some(item => item.id === currentModel)) {
            models.push({ id: currentModel, label: d ? d.modelLabel(conv.tool, currentModel, catalog) : currentModel });
          }
          const pick = model => {
            if (model === currentModel) {
              if (pending && stage) stage(null);
              return p.sheet.close(null);
            }
            if (whyModel) return work.toast(whyModel);
            const entry = list.find(item => item.id === model);
            const effort = conv.effort && entry && (entry.efforts || []).includes(conv.effort) ? conv.effort : '';
            go('model:' + model, async () => {
              if (pending && stage) stage(null);
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
          const activeCheck = id => (!pending || pending.tool === conv.tool) && id === currentModel;
          p.body.append(ui.section(TOOLS[conv.tool] || conv.tool, ui.group(
            ...models.map(item => ui.row({
              icon: conv.tool, tint: tint(conv.tool), label: item.label,
              value: going('model:' + item.id) || (item.isDefault ? '默认' : ''),
              checked: activeCheck(item.id), disabled: !!state.going, act: () => pick(item.id)
            })),
            ui.row({ icon: 'sliders', tint: 'muted', label: '思考深度与权限', go: () => {
              if (whyModel) return work.toast(whyModel);
              p.sheet.push(window.RemoteRestart.paramsPage(work, conv, reopen));
            } })), whyModel && !state.going ? whyModel : ''));
          return;
        }

        // Another AI selected at the top bar
        if (state.member) {
          p.body.append(ui.section(TOOLS[activeTool] || activeTool, ui.group(
            ui.row({
              icon: activeTool, tint: tint(activeTool), label: '在协作中交给 ' + (TOOLS[activeTool] || activeTool),
              detail: '由它接手当前协作成员的任务', go: () => work.handoff(p.sheet, conv)
            }))));
          return;
        }

        const noSession = conv.id ? '' : '发出第一条消息后才能换 AI';
        const inPlace = work.has('handover') && typeof stage === 'function';
        const note = noSession || whyBusy || (inPlace
          ? '选择模型后，下一条消息将由 ' + (TOOLS[activeTool] || activeTool) + ' 在当前对话里继续'
          : '将在新对话中交给 ' + (TOOLS[activeTool] || activeTool));
        const pickOther = model => {
          if (noSession) return work.toast(noSession);
          if (whyBusy) return work.toast(whyBusy);
          if (inPlace) {
            stage({ tool: activeTool, model, effort: '' });
            p.sheet.close({ tool: activeTool, model });
            return;
          }
          go('to:' + activeTool, async () => {
            const started = await work.handToNew(conv, activeTool, '');
            p.sheet.close({ started, tool: activeTool }, true);
            work.leaveTo(started);
          });
        };
        const stagedHere = id => !!(pending && pending.tool === activeTool && (pending.model || '') === id);
        p.body.append(ui.section((TOOLS[activeTool] || activeTool) + (left(activeTool) ? ' · ' + left(activeTool) : ''), ui.group(
          ...models.map(item => ui.row({
            icon: activeTool, tint: tint(activeTool), label: item.label,
            value: going('to:' + activeTool) || (item.isDefault ? '默认' : ''),
            checked: stagedHere(item.id), disabled: !!state.going, act: () => pickOther(item.id)
          }))), note));
      }
    };
  }

  window.RemoteSwitch = { page };
})();
