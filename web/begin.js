/* 新建对话: where a new conversation begins, and with what. In an application (the desktop app of Codex or of Claude
   on the computer: the conversation is the app's own and runs there), or in a terminal (Claude Code, Codex, the
   shell: it runs under this program and is worked wholly from the phone). Both are offered wherever a conversation
   is begun, and the kind that stands open is the one its owner chose last: kept, in the app for every computer.
   A project's page offers them for that project (`draw`); 任务, and the app's own lists, for every project
   (`anywhere`). */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { $, el, ICON, TOOLS, api, newId, toast, openSheet } = page;
  const KINDS = { app: '应用', term: '终端' }, APPS = { codex: 'Codex', claude: 'Claude' };
  // what the computer offers of each kind: the desktop apps it opens a conversation in, and the tools it has
  const offered = () => ({ app: Object.keys(APPS).filter(tool => page.has(tool + '-desktop-open')), term: Object.keys(TOOLS).filter(tool => (page.data.device.tools || []).includes(tool)) });
  const named = (kind, tool) => kind === 'app' ? APPS[tool] : TOOLS[tool];
  // The kind that stands open: the one chosen last. Before any choice: a terminal for a project whose folder has
  // only been used in terminals, an application for any other.
  function kindOf(project, has) {
    const kept = page.saved('newkind', '');
    if (has[kept] && has[kept].length) return kept;
    if (!has.app.length || !has.term.length) return has.app.length ? 'app' : 'term';
    const folder = (page.data.device.projects || []).find(p => p.name === project), kinds = folder && folder.kinds || [];
    return kinds.length && kinds.every(kind => kind === '终端') ? 'term' : 'app';
  }
  // the two kinds side by side, where the computer offers both
  function switcher(kind, has, changed) {
    if (!has.app.length || !has.term.length) return '';
    const group = el('span', { className: 'seg', role: 'group', ariaLabel: '对话开在哪里' });
    Object.keys(KINDS).forEach(key => group.append(el('button', { type: 'button', textContent: KINDS[key], ariaPressed: String(key === kind), onclick: () => {
      page.keep('newkind', key);
      group.querySelectorAll('button').forEach(button => button.setAttribute('aria-pressed', String(button.textContent === KINDS[key])));
      changed(key);
    } })));
    return group;
  }
  const tile = (kind, tool, props) => el('button', Object.assign({ type: 'button' }, props), el('span', { className: 'tool ' + tool, html: ICON[tool] }), el('span', { textContent: named(kind, tool) }));
  // Resolves with whether the page goes on with what began (a terminal's page), as everything here does: where it
  // does not, a page that the app opened for this is left for the app's list again.
  const begin = (kind, tool, project) => kind === 'app' ? inApp(tool, project) : page.start(tool, project);

  // ---- on a project's page: the kinds, and a button for each tool of the one that stands open
  function draw(project) {
    const box = $('begin'), has = offered(), ready = page.usable(), kind = kindOf(project, has);
    const signature = JSON.stringify([project, has, ready, kind, TOOLS.shell]);
    if (box.dataset.signature === signature) return;
    box.dataset.signature = signature;
    box.replaceChildren(el('div', { className: 'section-head' }, el('h2', { textContent: '新建对话' }), switcher(kind, has, () => draw(project))),
      has[kind].length ? el('div', { id: 'launch' }, ...has[kind].map(tool => tile(kind, tool, { disabled: !ready, onclick: () => begin(kind, tool, project) })))
        : el('p', { className: 'none', textContent: '电脑上没有找到 Claude Code、Codex 或命令行终端' }));
  }

  // ---- for every project: the kinds, the tools of the one that stands open, and the projects, the ones used last first.
  // The tool used last stays chosen, so that a press on a project begins.
  async function anywhere() {
    if (!page.usable()) { toast('电脑离线，暂时不能新建'); return false; }
    const data = page.data, has = offered();
    const latest = name => Math.max(0, ...(data.terminals || []).filter(t => t.dir === name).map(t => t.created || 0), ...(data.sessions || []).filter(s => s.dir === name).map(s => s.updated || 0));
    const names = (data.device.workspaces || []).slice().sort((a, b) => latest(b) - latest(a));
    if (!has.app.length && !has.term.length) { toast('电脑上没有可用的工具'); return false; }
    // a conversation begins in a project: without one, the page of projects is shown, where one is added
    if (!names.length) { toast('请先添加一个项目'); page.go('projects', true); return true; }
    const picked = await openSheet((form, finish) => {
      const tools = el('div', { className: 'tools', role: 'group', ariaLabel: '工具' });
      let kind = kindOf('', has), tool = '';
      const show = now => {
        kind = now;
        tool = has[kind].includes(page.saved('new' + kind, '')) ? page.saved('new' + kind, '') : has[kind][0];
        tools.replaceChildren(...has[kind].map(one => tile(kind, one, { ariaPressed: String(one === tool), onclick: event => {
          tool = one; page.keep('new' + kind, one);
          tools.querySelectorAll('button').forEach(button => button.setAttribute('aria-pressed', String(button === event.currentTarget)));
        } })));
      };
      show(kind);
      form.append(el('div', { className: 'head' }, el('h3', { textContent: '新建对话' }), switcher(kind, has, show)), tools,
        el('div', { className: 'anywhere folders' }, ...names.slice(0, 12).map(name => el('button', { type: 'button', className: 'choice', onclick: () => finish({ kind, tool, name }) },
          el('span', { className: 'tool folder', html: ICON.folder }), el('span', { textContent: name }), el('span', { className: 'chev', html: ICON.chev })))));
    });
    return picked ? begin(picked.kind, picked.tool, picked.name) : false;
  }

  // ---- in an application
  // Its desktop app opens the project with a new conversation, and what is written here stands in its box. Neither
  // application sends that by itself: the conversation begins when it is sent on the computer. The computer then
  // says which conversation came of this (`opened`), and the phone goes into it. An answer that was lost is asked
  // for again under the same number: the application is not opened twice.
  async function inApp(tool, project) {
    const app = APPS[tool];
    const entered = await openSheet((form, finish) => {
      const draft = el('textarea', { id: 'desktop-draft', rows: 4, maxLength: 1000, placeholder: '第一句话', ariaLabel: '第一句话', autocapitalize: 'off', spellcheck: false });
      const status = el('p', { role: 'status', 'aria-live': 'polite', className: 'quiet' });
      let operation = null;
      const button = el('button', { type: 'button', className: 'choice solid', textContent: '在 ' + app + ' 中打开', onclick: async () => {
        if (button.disabled) return;
        if (!operation) operation = { id: newId(), action: 'app_open', tool, dir: project, draft: draft.value };
        button.disabled = true; draft.readOnly = true;
        status.textContent = '正在交给电脑上的 ' + app + '…';
        try {
          let answer = await api('api/terminal', operation);
          for (let n = 0; n < 60 && answer.state === 'queued' && status.isConnected && $('sheet').open; n++) {
            await new Promise(resolve => setTimeout(resolve, 400));
            answer = await api('api/terminal', operation);
          }
          if (answer.state === 'queued') throw new Error('暂未收到结果，请查询同一次操作，或先检查电脑上的应用。');
          if (answer.error) { status.textContent = answer.error; button.textContent = '操作未确认'; return; }
          const result = answer.result || {};
          if (!['handed_off', 'unknown'].includes(result.status) || result.sent !== false) throw new Error('未收到可确认的桌面操作结果，请查询同一次操作。');
          status.textContent = result.message || '请在电脑上检查 ' + app + '，内容尚未发送。';
          button.textContent = result.status === 'handed_off' ? '在电脑上发送后开始' : '结果未知，请检查电脑';
          if (result.status !== 'handed_off') return;
          const watch = setInterval(() => {
            if (!status.isConnected || !$('sheet').open) return clearInterval(watch);
            const came = (page.data.sessions || []).find(s => s.opened === operation.id);
            if (!came) return;
            clearInterval(watch);
            finish(came);
          }, 1000);
        } catch (error) {
          status.textContent = error.message;
          button.textContent = '查询本次操作结果'; button.disabled = false;
        }
      } });
      form.append(el('h3', { textContent: app + ' · ' + project }), draft, button, status);
      // Leaving the sheet does not take back what was handed to the computer.
      setTimeout(() => { const cancel = form.querySelector('.cancel'); if (cancel) cancel.textContent = '返回'; }, 0);
    });
    return entered ? page.takeOver(entered) : false;
  }

  page.begin = { draw, anywhere };
})();
