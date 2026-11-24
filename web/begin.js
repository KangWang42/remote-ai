/* 新建对话: where a new conversation begins, and with what. As a conversation (应用: Claude Code or Codex, run by the
   computer through the tool's structured mode and shown on the phone as a conversation, chat/; on a computer that
   cannot, the desktop app of Codex or of Claude, which opens with the first words in its box), or in a terminal
   (Claude Code, Codex, the shell, as a screen). These two are offered wherever a conversation is begun, and the kind
   that stands open is the one its owner chose last: kept, in the app for every computer. A held press on a tool of
   应用 on a project's page opens its desktop app on the computer instead, where the computer offers that.
   A project's page offers them for that project (`draw`); 任务, and the app's own lists, for every project
   (`anywhere`). */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { $, el, ICON, TOOLS, api, newId, toast, openSheet } = page;
  const KINDS = { app: '应用', term: '终端' };
  const APPS = { codex: 'Codex', claude: 'Claude', agy: 'Antigravity' };
  // what the computer offers of each kind: conversations (应用) and terminals (终端). Where the computer runs no
  // conversation of a tool, 应用 opens that tool's desktop app on the computer instead.
  const desktop = tool => page.has(tool + '-desktop-open');
  const viaShell = 'agy-shell';
  function offered() {
    const device = page.data.device;
    const term = Object.keys(TOOLS).filter(tool => (device.tools || []).includes(tool));
    // Before 1.7.14 the relay discarded agy even when the Windows agent advertised it.
    const version = /^1\.(\d+)\.(\d+)$/.exec(page.relayVersion);
    const oldRelay = version && (+version[1] < 7 || (+version[1] === 7 && +version[2] < 14));
    if (oldRelay && device.shell === 'PowerShell' && term.includes('shell') && !term.includes('agy')) {
      term.splice(term.indexOf('shell'), 0, viaShell);
    }
    const chatApps = ['codex', 'claude', 'agy'].filter(tool => page.chatOk(tool));
    return { app: chatApps.length ? chatApps : Object.keys(APPS).filter(tool => desktop(tool)), term };
  }
  function named(kind, tool) {
    if (tool === 'agy' || tool === viaShell) return kind === 'app' && !page.chatOk('agy') && desktop('agy') ? APPS.agy : 'agy';
    return kind === 'app' ? APPS[tool] : TOOLS[tool];
  }
  // The kind that stands open: the one chosen last. Before any choice: a terminal for a project whose folder has
  // only been used in terminals, an application for any other.
  function kindOf(project, has) {
    const available = Object.keys(KINDS).filter(k => has[k] && has[k].length);
    const kept = page.saved('newkind', '');
    if (kept && has[kept] && has[kept].length) return kept;
    if (available.length <= 1) return available[0] || 'term';
    const folder = (page.data.device.projects || []).find(p => p.name === project), kinds = folder && folder.kinds || [];
    if (has.term.length && kinds.length && kinds.every(kind => kind === '终端')) return 'term';
    return has.app.length ? 'app' : available[0];
  }
  // the offered kinds side by side, where the computer offers more than one
  function switcher(kind, has, changed) {
    const keys = Object.keys(KINDS).filter(k => has[k] && has[k].length);
    if (keys.length < 2) return '';
    const group = el('span', { className: 'seg', role: 'group', ariaLabel: '对话开在哪里' });
    keys.forEach(key => group.append(el('button', { type: 'button', textContent: KINDS[key], ariaPressed: String(key === kind), onclick: () => {
      page.keep('newkind', key);
      group.querySelectorAll('button').forEach(button => button.setAttribute('aria-pressed', String(button.textContent === KINDS[key])));
      changed(key);
    } })));
    return group;
  }
  // (the tile of a tool of 应用 still opens its desktop app on a held press, where both are offered)
  const tile = (kind, tool, props, project) => {
    const icon = tool === viaShell ? 'agy' : tool;
    const made = el('button', Object.assign({ type: 'button' }, props), el('span', { className: 'tool ' + icon, html: ICON[icon] }), el('span', { textContent: named(kind, tool) }));
    return kind === 'app' && page.chatOk(tool) && desktop(tool) && project ? page.held(made, () => inApp(tool, project)) : made;
  };
  // Resolves with whether the page goes on with what began (a conversation's or a terminal's page), as everything
  // here does: where it does not, a page that the app opened for this is left for the app's list again.
  async function begin(kind, tool, project, params) {
    if (tool === viaShell) {
      return page.startAgyShell(project);
    }
    return kind !== 'app' ? page.start(tool, project, undefined, false, false, undefined, params) : page.chatOk(tool) ? page.chat(tool, project, undefined, false, undefined, params) : inApp(tool, project);
  }

  // Keep the terminal entry visible on the default 应用 tab as well.
  function choices(kind, has) {
    if (kind === 'app') return has.app.concat(has.term.filter(tool => (tool === 'agy' || tool === viaShell) && !has.app.includes(tool)));
    return has.term;
  }
  // agy under 应用 is a conversation or desktop app where offered, else its terminal
  const kindFor = (kind, tool) => {
    if (tool === viaShell) return 'term';
    if (tool === 'agy' && kind === 'app' && !page.chatOk('agy') && !desktop('agy')) return 'term';
    return kind;
  };

  // ---- on a project's page: the kinds, and a button for each tool of the one that stands open
  function draw(project) {
    const box = $('begin'), has = offered(), ready = page.usable(), kind = kindOf(project, has);
    const signature = JSON.stringify([project, has, ready, kind, TOOLS.shell]);
    if (box.dataset.signature === signature) return;
    box.dataset.signature = signature;
    box.replaceChildren(el('div', { className: 'section-head' }, el('h2', { textContent: '新建对话' }), switcher(kind, has, () => draw(project))),
      has[kind].length ? el('div', { id: 'launch' }, ...choices(kind, has).map(tool => tile(kindFor(kind, tool), tool, { disabled: !ready, onclick: () => begin(kindFor(kind, tool), tool, project) }, project)))
        : el('p', { className: 'none', textContent: '电脑上没有找到 Claude Code、Codex 或命令行终端' }));
  }

  // ---- model catalog cache for the visual parameter bar inside 新建对话
  let modelCatalog = null, modelAsking = null;
  function loadModels() {
    const d = window.RemoteTeamDefaults;
    if (!d || !page.has('teams-models') || !page.usable()) return Promise.resolve(modelCatalog);
    if (modelCatalog) return Promise.resolve(modelCatalog);
    if (!modelAsking) {
      modelAsking = api('api/work', { id: newId(), action: 'team_models' })
        .then(res => { modelCatalog = d.normalizeModels(res && res.models); return modelCatalog; })
        .catch(() => null)
        .finally(() => { modelAsking = null; });
    }
    return modelAsking;
  }

  // ---- for every project: the kinds, the tools of the one that stands open, optional visual parameters, and the projects.
  // The tool used last stays chosen, so that a press on a project begins.
  async function anywhere() {
    if (!page.usable()) { toast('电脑离线，暂时不能新建'); return false; }
    const data = page.data, has = offered(), d = window.RemoteTeamDefaults;
    const latest = name => Math.max(0, ...(data.terminals || []).filter(t => t.dir === name).map(t => t.created || 0), ...(data.sessions || []).filter(s => s.dir === name).map(s => s.updated || 0));
    const names = (data.device.workspaces || []).slice().sort((a, b) => latest(b) - latest(a));
    if (!has.app.length && !has.term.length) { toast('电脑上没有可用的工具'); return false; }
    // a conversation begins in a project: without one, the page of projects is shown, where one is added
    if (!names.length) { toast('请先添加一个项目'); page.go('projects', true); return true; }
    const picked = await openSheet((form, finish) => {
      const tools = el('div', { className: 'tools', role: 'group', ariaLabel: '工具' });
      const paramBox = el('div', { className: 'begin-params', hidden: true });
      let kind = kindOf('', has), tool = '', paramsOpen = false;
      const overrides = {};
      const maxMode = (data.device && data.device.max_mode) || '';
      const configurable = t => ['claude', 'codex', 'agy'].includes(t) && (page.has('teams-models') || page.has('chat-params'));
      const stateFor = t => {
        if (!overrides[t]) {
          const base = d ? d.savedParams(t, maxMode) : {};
          overrides[t] = { model: base.model || '', effort: base.effort || '', mode: base.mode || '' };
        }
        return overrides[t];
      };
      const drawParams = () => {
        if (!d || !configurable(tool) || (kind === 'app' && !page.chatOk(tool))) { paramBox.hidden = true; return; }
        paramBox.hidden = false;
        const st = stateFor(tool);
        const list = (modelCatalog && modelCatalog[tool]) || [];
        const entry = d.modelEntry(tool, st.model, modelCatalog);
        const efforts = entry && Array.isArray(entry.efforts) ? entry.efforts : [];
        if (st.effort && efforts.length && !efforts.includes(st.effort)) st.effort = '';
        const summary = [
          st.model ? d.modelLabel(tool, st.model, modelCatalog) : '默认模型',
          st.effort ? '思考 ' + d.shortEffortLabel(st.effort) : '',
          st.mode && tool !== 'agy' ? d.modeLabel(st.mode, true) : ''
        ].filter(Boolean).join(' · ');
        const hasCustom = !!(st.model || st.effort || (st.mode && tool !== 'agy'));
        const headBtn = el('button', {
          type: 'button', className: 'begin-params-toggle' + (hasCustom ? ' active' : ''),
          'aria-expanded': String(paramsOpen),
          onclick: () => { paramsOpen = !paramsOpen; if (paramsOpen && !modelCatalog) loadModels().then(drawParams); drawParams(); }
        }, el('span', { className: 'begin-params-label', textContent: '运行参数' }),
          el('span', { className: 'begin-params-val', textContent: summary }),
          el('span', { className: 'begin-params-chev', textContent: paramsOpen ? '收起 ▴' : '调整 ▾' }));
        const parts = [headBtn];
        if (paramsOpen) {
          const panel = el('div', { className: 'begin-params-panel' });
          if (list.length) {
            const mChips = el('div', { className: 'begin-chips', role: 'group', ariaLabel: '模型' },
              ...[['', '工具默认']].concat(list.map(m => [m.id, m.label])).map(([id, label]) =>
                el('button', {
                  type: 'button', className: 'begin-chip', 'aria-pressed': String(st.model === id),
                  textContent: label,
                  onclick: () => { st.model = id; st.effort = ''; drawParams(); }
                })));
            panel.append(el('div', { className: 'begin-param-row' }, el('span', { className: 'begin-param-k', textContent: '模型' }), mChips));
          } else if (modelAsking) {
            panel.append(el('p', { className: 'begin-param-hint', textContent: '正在读取电脑端可用模型…' }));
          }
          if (efforts.length && page.has('teams-effort')) {
            const eChips = el('div', { className: 'begin-chips', role: 'group', ariaLabel: '思考深度' },
              ...[['', '默认']].concat(efforts.map(e => [e, d.shortEffortLabel(e)])).map(([val, label]) =>
                el('button', {
                  type: 'button', className: 'begin-chip', 'aria-pressed': String(st.effort === val),
                  textContent: label,
                  onclick: () => { st.effort = val; drawParams(); }
                })));
            panel.append(el('div', { className: 'begin-param-row' }, el('span', { className: 'begin-param-k', textContent: '思考深度' }), eChips));
          }
          if (tool !== 'agy' && page.has('chat-params')) {
            const allowed = d.MODE_ORDER.filter(m => d.modeAllowed(m, maxMode));
            const mdChips = el('div', { className: 'begin-chips', role: 'group', ariaLabel: '权限模式' },
              ...[['', '跟随电脑']].concat(allowed.map(m => [m, d.MODE_LABELS[m]])).map(([val, label]) =>
                el('button', {
                  type: 'button', className: 'begin-chip', 'aria-pressed': String(st.mode === val),
                  textContent: label,
                  onclick: () => { st.mode = val; drawParams(); }
                })));
            panel.append(el('div', { className: 'begin-param-row' }, el('span', { className: 'begin-param-k', textContent: '权限模式' }), mdChips));
          }
          parts.push(panel);
        }
        paramBox.replaceChildren(...parts);
      };
      const show = now => {
        kind = now;
        const options = choices(kind, has);
        tool = options.includes(page.saved('new' + kind, '')) ? page.saved('new' + kind, '') : options[0];
        tools.replaceChildren(...options.map(one => tile(kindFor(kind, one), one, { ariaPressed: String(one === tool), onclick: event => {
          tool = one; page.keep('new' + kind, one);
          tools.querySelectorAll('button').forEach(button => button.setAttribute('aria-pressed', String(button === event.currentTarget)));
          drawParams();
        } })));
        drawParams();
      };
      show(kind);
      if (page.has('teams-models') && !modelCatalog) loadModels().then(drawParams);
      form.append(el('div', { className: 'head' }, el('h3', { textContent: '新建对话' }), switcher(kind, has, show)), tools, paramBox,
        el('p', { className: 'quiet', textContent: '选择项目后创建' }),
        el('div', { className: 'anywhere folders' }, ...names.slice(0, 12).map(name => el('button', { type: 'button', className: 'choice', onclick: () => {
          const st = configurable(tool) ? stateFor(tool) : null;
          const params = st ? Object.assign({}, st.model ? { model: st.model } : {}, st.effort ? { effort: st.effort } : {}, st.mode && tool !== 'agy' ? { mode: st.mode } : {}) : undefined;
          finish({ kind: kindFor(kind, tool), tool, name, params });
        } },
          el('span', { className: 'tool folder', html: ICON.folder }), el('span', { textContent: name }), el('span', { className: 'chev', html: ICON.chev })))));
    });
    return picked ? begin(picked.kind, picked.tool, picked.name, picked.params) : false;
  }

  // ---- in an application
  // Direct desktop apps (Antigravity) create a live conversation and optionally send the first words right away;
  // draft-handoff apps (Codex, Claude) open the project with the draft in their box and enter once sent on the computer.
  // An answer that was lost is asked for again under the same operation id so the application is not opened twice.
  async function inApp(tool, project) {
    const app = APPS[tool] || tool;
    const direct = tool === 'agy';
    const entered = await openSheet((form, finish) => {
      const draft = el('textarea', {
        id: 'desktop-draft', rows: 4, maxLength: direct ? 8000 : 1000,
        placeholder: direct ? '输入第一句话（可留空仅新建）' : '第一句话',
        ariaLabel: '第一句话', autocapitalize: 'off', spellcheck: false
      });
      const status = el('p', { role: 'status', 'aria-live': 'polite', className: 'quiet' });
      let operation = null;
      const labelOf = () => direct ? (draft.value.trim() ? '在 ' + app + ' 中新建并发送' : '在 ' + app + ' 中新建') : '在 ' + app + ' 中打开';
      const button = el('button', { type: 'button', className: 'choice solid', textContent: labelOf(), onclick: async () => {
        if (button.disabled) return;
        if (!operation) operation = { id: newId(), action: 'app_open', tool, dir: project, draft: draft.value };
        button.disabled = true; draft.readOnly = true;
        status.textContent = direct ? '正在 ' + app + ' 中创建对话…' : '正在交给电脑上的 ' + app + '…';
        try {
          let answer = await api('api/terminal', operation);
          for (let n = 0; n < 60 && answer.state === 'queued' && status.isConnected && $('sheet').open; n++) {
            await new Promise(resolve => setTimeout(resolve, 400));
            answer = await api('api/terminal', operation);
          }
          if (answer.state === 'queued') throw new Error('暂未收到结果，请查询同一次操作，或先检查电脑上的应用。');
          if (answer.error) { status.textContent = answer.error; button.textContent = '操作未确认'; return; }
          const result = answer.result || {};
          if (direct && result.status === 'created' && result.session) {
            status.textContent = result.message || (result.sent ? '已在 ' + app + ' 中新建并发送，正在进入对话…' : '已在 ' + app + ' 中新建，正在进入对话…');
            button.textContent = '正在进入对话…';
            try { await page.refresh(); } catch (error) { /* synthetic session still opens */ }
            if (!status.isConnected || !$('sheet').open) return;
            const found = (page.data && page.data.sessions || []).find(s => s.id === result.session && s.tool === tool);
            const sent = result.sent === true, text = draft.value.trim();
            const synthetic = {
              id: result.session, tool, dir: project, folder: '',
              title: text ? text.slice(0, 60) : app + ' · ' + project,
              updated: Date.now(), live: true, now: true, host: 'shared',
              origin: app + ' 桌面版', kind: app + ' 桌面版',
              status: sent ? 'busy' : 'waiting',
              reach: sent ? ['say', 'stop', 'answer'] : ['say'],
              transport: tool + '-desktop',
              ways: { say: tool + '-desktop', stop: sent ? tool + '-desktop' : '', answer: tool + '-desktop', end: '' },
              why: {}, can_takeover: false
            };
            finish({ session: found || synthetic });
            return;
          }
          const validUnknown = result.status === 'unknown' && (result.sent === false || (direct && result.sent === null));
          if (!(result.status === 'handed_off' && result.sent === false) && !validUnknown) throw new Error('未收到可确认的桌面操作结果，请查询同一次操作。');
          status.textContent = result.message || '请在电脑上检查 ' + app + '，内容尚未发送。';
          button.textContent = result.status === 'handed_off' ? '在电脑上发送后开始' : '结果未知，请检查电脑';
          if (result.status !== 'handed_off') return;
          const watch = setInterval(() => {
            if (!status.isConnected || !$('sheet').open) return clearInterval(watch);
            const came = (page.data.sessions || []).find(s => s.opened === operation.id);
            if (!came) return;
            clearInterval(watch);
            finish({ session: came });
          }, 1000);
        } catch (error) {
          status.textContent = error.message;
          button.textContent = '查询本次操作结果'; button.disabled = false;
        }
      } });
      draft.addEventListener('input', () => { if (!operation) button.textContent = labelOf(); });
      draft.addEventListener('keydown', event => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.isComposing) { event.preventDefault(); button.click(); } });
      const fallback = !direct && page.chatOk(tool)
        ? el('button', { type: 'button', className: 'choice desktop-fallback', textContent: '改为在手机直接新建（同步历史）', onclick: () => finish({ fallbackChat: true }) })
        : '';
      form.append(el('h3', { textContent: app + ' · ' + project }), draft, button, status, fallback);
      // Leaving the sheet does not take back what was handed to the computer.
      setTimeout(() => { const cancel = form.querySelector('.cancel'); if (cancel) cancel.textContent = '返回'; }, 0);
    });
    if (entered && entered.fallbackChat) return page.chat(tool, project);
    return entered && entered.session ? page.takeOver(entered.session) : false;
  }

  page.begin = { draw, anywhere };
})();
