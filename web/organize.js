/* One organizer for the computer's task list and the Android workbench's computer picker. */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { ui } = window.RemoteSheet, { el } = ui;
  const labels = { pending: '未整理', queued: '排队中', running: '整理中', done: '已整理', failed: '整理失败' };
  const request = (verb, fields) => page.api('api/organize', { id: page.newId(), verb, ...fields });
  const targetOf = item => ({ session: item.id, tool: item.tool });
  const keyOf = item => item.tool + ':' + (item.session || item.id);
  const gearIcon = '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="2.8"/><circle cx="12" cy="12" r="6.3"/><circle cx="12" cy="12" r="8.4" stroke-width="2.4" stroke-linecap="butt" stroke-dasharray="2.64 3.957"/></svg>';

  function open(options = {}) {
    const selected = options.session ? { session: options.session, tool: options.tool } : null;
    const computer = page.computerName || page.data?.device?.name || '当前电脑';
    const size = 30;
    let status, items = [], total = 0, more = false, limit = size, busy = false, error = '', message = '', timer;
    let scopeFilter = 'all';
    const filtered = options.filtered ? [...new Map(options.targets.map(item => [keyOf(item), item])).values()] : [];
    let view, settingsView = null;

    const sheet = window.RemoteSheet.open({ title: selected ? '整理这段对话' : '整理对话', build(p) {
      view = p;
      settingsView = null;
      const root = el('div', { className: 'organize-content' });
      p.body.append(root);
      view.root = root;
      if (status) paint();
      else load();
    } });
    sheet.result.then(() => clearTimeout(timer));
    return sheet;

    async function load(poll = false) {
      clearTimeout(timer);
      if (!poll) { busy = true; paintAll(); }
      try {
        const [next, first] = await Promise.all([request('status'), request('list', selected || { offset: 0, limit: Math.min(120, limit) })]);
        const rows = [...first.items];
        let remaining = first.more;
        if (!selected && limit > 120) {
          while (remaining && rows.length < limit) {
            const nextPage = await request('list', { offset: rows.length, limit: Math.min(120, limit - rows.length) });
            rows.push(...nextPage.items);
            remaining = nextPage.more;
          }
        }
        const changed = JSON.stringify([next, rows, first.total, remaining]) !== JSON.stringify([status, items, total, more]);
        const recovered = !!error;
        status = next; items = rows; total = first.total; more = remaining; error = '';
        if (poll && (changed || recovered)) {
          paintAll();
          page.refresh();
        }
      } catch (problem) {
        error = problem.message;
        if (poll) paintAll();
      } finally {
        if (!poll) { busy = false; paintAll(); }
      }
      if (!sheet.gone && status && (status.queued || status.running || status.auto)) {
        timer = setTimeout(() => load(true), status.queued || status.running ? 2000 : 5000);
      }
    }

    async function change(verb, fields) {
      clearTimeout(timer);
      busy = true; error = ''; message = ''; paintAll();
      try {
        const answer = await request(verb, fields);
        if (verb === 'start' || verb === 'retry') message = `已排队 ${answer.queued || 0} 段 · 跳过 ${answer.skipped || 0} 段`;
        if (verb === 'settings') message = '已保存设置';
        if (verb === 'edit') {
          if ('follow_up' in fields && !('title' in fields)) message = fields.follow_up ? '已加入待跟进' : '已移出待跟进';
          else message = '已保存名称';
        }
        await load();
        page.refresh();
        return true;
      } catch (problem) {
        error = problem.message; busy = false; paintAll();
        return false;
      }
    }

    function paintAll() {
      paint();
      if (settingsView && settingsView.box?.isConnected) paintSettings();
    }

    function modelLabel() {
      if (!status) return '';
      const found = (status.models || []).find(choice => choice.id === status.model);
      return found ? (found.label || found.id) : (status.model || '');
    }

    function statusSummary() {
      if (!status) return { tone: 'idle', headline: '正在读取整理状态…', detail: computer, percent: null };
      if (selected) {
        const one = items[0];
        if (!one) return { tone: 'idle', headline: '暂无可整理记录', detail: computer, percent: null };
        const st = one.state || 'pending';
        const tone = st === 'running' || st === 'queued' ? 'busy' : st === 'failed' ? 'bad' : st === 'done' ? 'good' : 'idle';
        return {
          tone,
          headline: labels[st] || st,
          detail: one.follow_up ? '已标为待跟进' : (status.done ? `本机已整理 ${status.done} 段` : computer),
          percent: null
        };
      }
      const queued = status.queued || 0, running = status.running || 0, done = status.done || 0, failed = status.failed || 0;
      const pending = Math.max(0, total - queued - running - done - failed);
      if (running > 0 || queued > 0) {
        const headline = running > 0 ? `正在整理 ${running} 段` : `已排队 ${queued} 段`;
        const parts = [];
        if (running > 0 && queued > 0) parts.push(`排队 ${queued}`);
        parts.push(total > 0 ? `已整理 ${done} / ${total}` : `已整理 ${done}`);
        if (failed > 0) parts.push(`失败 ${failed}`);
        const pct = total > 0 ? Math.min(100, Math.round((done / total) * 100)) : null;
        return { tone: 'busy', headline, detail: parts.join(' · '), percent: pct };
      }
      if (failed > 0) {
        const parts = [`已整理 ${done} 段`];
        if (pending > 0) parts.push(`${pending} 段未整理`);
        return { tone: 'bad', headline: `${failed} 段整理失败`, detail: parts.join(' · '), percent: null };
      }
      if (total === 0) return { tone: 'idle', headline: '暂无可读对话', detail: '有保存的对话后可在这里整理', percent: null };
      if (pending > 0) return { tone: 'idle', headline: `${pending} 段待整理`, detail: `已整理 ${done} / ${total} 段`, percent: null };
      return { tone: 'good', headline: `已整理 ${done} 段`, detail: '全部可读对话已处理', percent: null };
    }

    function actionBtn(label, onclick, { primary = false, danger = false, disabled = false } = {}) {
      const cls = 'organize-pill-btn' + (primary ? ' primary' : '') + (danger ? ' danger' : '');
      return el('button', { type: 'button', className: cls, textContent: label, onclick, disabled: disabled || busy });
    }

    function openSettings() {
      message = '';
      error = '';
      view.sheet.push({ title: '整理设置', build(p) {
        const box = el('div', { className: 'organize-content' });
        p.body.append(box);
        settingsView = { page: p, box };
        paintSettings();
      } });
    }

    function paintSettings() {
      if (!settingsView?.box) return;
      const box = settingsView.box;
      if (!status) {
        box.replaceChildren(ui.note('正在读取整理设置…'));
        return;
      }
      const focus = document.activeElement?.id;
      const model = el('select', { id: 'organize-model', className: 'organize-select', ariaLabel: '整理模型', disabled: busy });
      if (!status.model_available) model.append(el('option', { value: status.model, textContent: status.model + ' · 当前不可用' }));
      for (const choice of status.models || []) model.append(el('option', { value: choice.id, textContent: choice.label || choice.id }));
      model.value = status.model;
      model.onchange = () => change('settings', { model: model.value });

      const toggle = ui.toggle(status.auto, auto => change('settings', { auto }), '自动整理');
      toggle.id = 'organize-auto';
      toggle.disabled = busy || (!status.auto && !status.model_available);

      const defaultChoice = (status.models || []).find(c => c.id === status.default_model);
      const defaultName = defaultChoice ? (defaultChoice.label || defaultChoice.id) : (status.default_model || 'GPT-6 Luna');

      box.replaceChildren(
        ui.note(computer),
        ui.section('模型', ui.group(
          ui.row({ label: '整理模型', detail: '用于生成标题、摘要与待跟进标记', wrap: true, end: model })
        ), `默认使用 ${defaultName}；更改后用于新的整理任务。`),
        !status.model_available ? ui.summary('这台电脑暂时无法使用所选模型，请切换为可用模型。', 'bad', 'stop') : '',
        ui.section('自动化', ui.group(
          ui.row({ label: '自动整理', detail: '开启后自动整理新增对话，已整理的不重复处理', wrap: true, end: toggle })
        )),
        message ? el('p', { className: 'organize-banner', role: 'status', textContent: message }) : '',
        error ? el('div', { className: 'organize-banner bad', role: 'alert' }, el('span', { textContent: error })) : ''
      );
      if (focus) [...box.querySelectorAll('[id]')].find(node => node.id === focus)?.focus({ preventScroll: true });
    }

    function edit(item) {
      clearTimeout(timer);
      return view.sheet.push({ title: '编辑对话名称', build(p) {
        const input = ui.input({ label: '对话名称', value: item.title || item.original_title, max: 80, autofocus: true });
        p.body.append(ui.section('对话名称', input, item.original_title ? '原标题 · ' + item.original_title : ''));
        p.foot.append(...ui.go('保存名称', async () => {
          const title = input.value.trim();
          if (!title) throw new Error('请输入对话名称');
          await request('edit', { ...targetOf(item), title });
          message = '已保存名称';
          await load();
          page.refresh();
          p.sheet.goBack();
        }, { busy: '正在保存…' }).nodes);
      } });
    }

    function confirmScope(verb, fields, count) {
      const description = count === undefined
        ? '重试这台电脑上整理失败的对话。'
        : `检查这台电脑的 ${count} 段可读对话，已整理过的会自动跳过。`;
      view.sheet.push({ title: verb === 'retry' ? '重试失败记录' : '整理这台电脑的对话', build(p) {
        p.body.append(ui.note(computer), ui.summary(description, '', 'spark'));
        p.foot.append(...ui.go(verb === 'retry' ? '重试失败记录' : '开始整理', async () => {
          if (await change(verb, fields)) p.sheet.goBack();
          else throw new Error(error);
        }, { busy: '正在提交…' }).nodes);
      } });
    }

    function findSession(item) {
      return (page.data?.sessions || []).find(s => s.id === item.id && s.tool === item.tool) || null;
    }

    function openItem(item) {
      const session = findSession(item);
      if (!session || !page.openSession) return;
      sheet.close(null);
      setTimeout(() => page.openSession(session), 20);
    }

    function row(item) {
      const title = item.title || item.original_title;
      const original = item.original_title && title !== item.original_title ? '原标题 · ' + item.original_title : '';
      const session = !selected ? findSession(item) : null;
      const badges = el('span', { className: 'organize-row-badges' });
      if (item.follow_up) badges.append(el('span', { className: 'organize-badge follow', textContent: '待跟进' }));
      if (item.state && item.state !== 'done') {
        badges.append(el('span', { className: 'organize-badge state-' + item.state, textContent: labels[item.state] || item.state }));
      }

      const actions = el('div', { className: 'organize-row-actions' });
      if (session) actions.append(actionBtn('查看对话', () => openItem(item), { primary: !!item.follow_up }));
      if (item.state === 'pending') actions.append(actionBtn('整理这段', () => change('start', targetOf(item)), { primary: !session, disabled: !status.model_available }));
      if (item.state === 'failed') actions.append(actionBtn('重试这段', () => change('retry', targetOf(item)), { danger: true, disabled: !status.model_available }));
      actions.append(actionBtn(item.follow_up ? '移出待跟进' : '加入待跟进', () => change('edit', { ...targetOf(item), follow_up: !item.follow_up })));
      actions.append(actionBtn('编辑名称', () => edit(item)));

      const tags = item.tags?.length
        ? el('div', { className: 'organize-tags' }, ...item.tags.map(tag => el('span', { className: 'organize-tag', textContent: tag })))
        : '';

      return el('li', { className: 'organize-row' + (item.follow_up ? ' is-follow' : '') },
        el('div', { className: 'organize-row-head' }, el('b', { textContent: title }), badges.childNodes.length ? badges : ''),
        el('p', { className: 'organize-row-meta', textContent: [page.TOOLS[item.tool] || item.tool, item.dir, item.state === 'done' ? '已整理' : ''].filter(Boolean).join(' · ') }),
        item.summary ? el('p', { className: 'organize-row-summary', textContent: item.summary }) : '',
        tags,
        original ? el('p', { className: 'organize-row-origin', textContent: original }) : '',
        item.error ? el('p', { className: 'rs-note bad', textContent: item.error }) : '',
        actions
      );
    }

    function paint() {
      if (!view?.root) return;
      const root = view.root;
      const info = statusSummary();
      const focus = document.activeElement?.id;

      const statusBlock = el('div', { className: 'organize-status', role: 'status' },
        el('div', { className: 'organize-status-line' },
          el('i', { className: 'organize-dot ' + info.tone, ariaHidden: 'true' }),
          el('b', { className: 'organize-progress', textContent: info.headline })
        ),
        info.detail ? el('small', { textContent: info.detail }) : ''
      );

      if (!status) {
        root.replaceChildren(
          el('div', { className: 'organize-head' }, el('div', { className: 'organize-head-top' }, statusBlock)),
          error ? el('div', { className: 'organize-banner bad', role: 'alert' }, el('span', { textContent: error }), actionBtn('重试读取', () => load())) : ''
        );
        return;
      }

      const settingsBtn = el('button', {
        type: 'button', id: 'organize-settings', className: 'organize-settings-btn',
        ariaLabel: '整理设置', disabled: busy, onclick: openSettings
      }, el('span', { html: gearIcon }), el('span', { textContent: '设置' }));

      const metaText = [computer, modelLabel(), status.auto ? '自动整理已开' : '手动整理'].filter(Boolean).join(' · ');
      const headChildren = [
        el('div', { className: 'organize-head-top' }, statusBlock, settingsBtn),
        info.percent !== null ? el('div', { className: 'organize-bar', ariaHidden: 'true' }, el('i', { style: 'width:' + Math.max(4, info.percent) + '%' })) : '',
        el('p', { className: 'organize-meta', textContent: metaText })
      ];
      if (!status.model_available) {
        headChildren.push(el('div', { className: 'organize-warn' },
          el('span', { textContent: '所选模型当前不可用' }),
          actionBtn('更换模型', openSettings)
        ));
      }
      const headCard = el('div', { className: 'organize-head' }, ...headChildren);

      const batchBar = el('div', { className: 'organize-actions' });
      if (!selected) {
        if (filtered.length) {
          batchBar.append(actionBtn(`整理当前筛选的 ${filtered.length} 段`, async () => {
            busy = true; error = ''; message = ''; paintAll();
            let queued = 0, skipped = 0;
            const failures = [];
            for (const item of filtered) {
              try { const answer = await request('start', item); queued += answer.queued || 0; skipped += answer.skipped || 0; }
              catch (problem) { failures.push(problem.message); }
            }
            message = `已排队 ${queued} 段 · 跳过 ${skipped} 段` + (failures.length ? ` · ${failures.length} 段未提交` : '');
            await load();
            if (failures.length) { error = failures[0]; paintAll(); }
            page.refresh();
          }, { primary: true, disabled: !status.model_available }));
        }
        const pendingCount = Math.max(0, total - (status.queued || 0) - (status.running || 0) - (status.done || 0) - (status.failed || 0));
        batchBar.append(actionBtn('整理全部可读对话', () => confirmScope('start', { scope: 'all' }, total), {
          primary: !filtered.length && pendingCount > 0,
          disabled: !total || !status.model_available
        }));
        if (status.failed > 0) {
          batchBar.append(actionBtn('重试失败记录', () => confirmScope('retry', { scope: 'all' }), {
            danger: true,
            disabled: !status.model_available
          }));
        }
      }

      const followItems = items.filter(item => item.follow_up);
      const pendingItems = items.filter(item => item.state === 'pending' || item.state === 'queued' || item.state === 'running');
      const failedItems = items.filter(item => item.state === 'failed');
      if (scopeFilter === 'follow' && !followItems.length) scopeFilter = 'all';
      if (scopeFilter === 'pending' && !pendingItems.length) scopeFilter = 'all';
      if (scopeFilter === 'failed' && !failedItems.length) scopeFilter = 'all';

      let filterBar = '';
      if (!selected && items.length && (followItems.length || pendingItems.length || failedItems.length)) {
        const tabs = [['all', `全部 ${total || items.length}`]];
        if (followItems.length) tabs.push(['follow', `待跟进 ${followItems.length}`]);
        if (pendingItems.length) tabs.push(['pending', `待整理 ${pendingItems.length}`]);
        if (failedItems.length) tabs.push(['failed', `失败 ${failedItems.length}`]);
        filterBar = el('div', { className: 'organize-tabs', role: 'group', ariaLabel: '筛选对话' },
          ...tabs.map(([key, text]) => el('button', {
            type: 'button', className: 'organize-tab' + (scopeFilter === key ? ' on' : ''),
            ariaPressed: String(scopeFilter === key), textContent: text,
            onclick: () => { scopeFilter = key; paint(); }
          }))
        );
      }

      const listNodes = [];
      if (!items.length) {
        listNodes.push(ui.empty(selected ? '这段对话暂无可整理记录' : '这台电脑暂无可整理的对话'));
      } else if (selected || scopeFilter !== 'all') {
        const shownItems = selected ? items : scopeFilter === 'follow' ? followItems : scopeFilter === 'pending' ? pendingItems : failedItems;
        listNodes.push(el('ul', { className: 'organize-rows rs-group' }, ...shownItems.map(row)));
      } else if (followItems.length) {
        const others = items.filter(item => !item.follow_up);
        listNodes.push(ui.section(`待跟进 · ${followItems.length}`, el('ul', { className: 'organize-rows rs-group' }, ...followItems.map(row))));
        if (others.length) listNodes.push(ui.section(more ? '其他对话' : `其他对话 · ${others.length}`, el('ul', { className: 'organize-rows rs-group' }, ...others.map(row))));
      } else {
        listNodes.push(ui.section(more ? '全部对话' : `全部对话 · ${items.length}`, el('ul', { className: 'organize-rows rs-group' }, ...items.map(row))));
      }

      root.replaceChildren(
        headCard,
        message ? el('p', { className: 'organize-banner', role: 'status', textContent: message }) : '',
        error ? el('div', { className: 'organize-banner bad', role: 'alert' }, el('span', { textContent: error }), actionBtn('刷新整理状态', () => load())) : '',
        batchBar.childNodes.length ? batchBar : '',
        filterBar,
        ...listNodes,
        more ? el('button', { type: 'button', className: 'quiet wide organize-more-btn', textContent: '加载更多', disabled: busy, onclick: () => { limit += size; load(); } }) : ''
      );
      if (focus) [...root.querySelectorAll('[id]')].find(node => node.id === focus)?.focus({ preventScroll: true });
    }
  }

  window.RemoteOrganize = { open };
})();
