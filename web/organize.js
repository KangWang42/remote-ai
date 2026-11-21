/* One organizer for the computer's task list and the Android workbench's computer picker. */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { ui } = window.RemoteSheet, { el } = ui;
  const labels = { pending: '未整理', queued: '排队中', running: '整理中', done: '已整理', failed: '整理失败' };
  const request = (verb, fields) => page.api('api/organize', { id: page.newId(), verb, ...fields });
  const targetOf = item => ({ session: item.id, tool: item.tool });
  const keyOf = item => item.tool + ':' + (item.session || item.id);

  function open(options = {}) {
    const selected = options.session ? { session: options.session, tool: options.tool } : null;
    const computer = page.computerName || page.data?.device?.name || '当前电脑';
    const size = 30;
    let status, items = [], total = 0, more = false, limit = size, busy = false, error = '', message = '', timer;
    const filtered = options.filtered ? [...new Map(options.targets.map(item => [keyOf(item), item])).values()] : [];
    let view, progress;
    const sheet = window.RemoteSheet.open({ title: selected ? '整理这段对话' : '整理对话', build(p) {
      view = p;
      const content = el('div', { className: 'organize-content' }, ui.note(computer));
      progress = el('p', { className: 'organize-progress', role: 'status' });
      p.body.append(content, progress);
      const controls = el('div', { className: 'organize-content' });
      p.body.append(controls);
      view.controls = controls;
      if (status) paint();
      else load();
    } });
    sheet.result.then(() => clearTimeout(timer));
    return sheet;

    async function load(poll = false) {
      clearTimeout(timer);
      if (!poll) { busy = true; paint(); }
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
          paint();
          page.refresh();
        }
      } catch (problem) {
        error = problem.message;
        if (poll) paint();
      } finally {
        if (!poll) { busy = false; paint(); }
      }
      if (!sheet.gone && status && (status.queued || status.running || status.auto)) timer = setTimeout(() => load(true), status.queued || status.running ? 2000 : 5000);
    }

    async function change(verb, fields) {
      clearTimeout(timer);
      busy = true; error = ''; message = ''; paint();
      try {
        const answer = await request(verb, fields);
        if (verb === 'start' || verb === 'retry') message = `已排队 ${answer.queued || 0} 段 · 跳过 ${answer.skipped || 0} 段`;
        if (verb === 'settings') message = '已保存设置';
        if (verb === 'edit') message = '已保存名称';
        await load();
        page.refresh();
        return true;
      } catch (problem) {
        error = problem.message; busy = false; paint();
        return false;
      }
    }

    function button(label, onclick, disabled = false) {
      return el('button', { type: 'button', className: 'quiet', textContent: label, onclick, disabled: disabled || busy });
    }

    function edit(item) {
      clearTimeout(timer);
      const editor = view.sheet.push({ title: '编辑对话名称', build(p) {
        const input = ui.input({ label: '对话名称', value: item.title || item.original_title, max: 80 });
        p.body.append(el('label', { className: 'organize-field' }, '对话名称', input));
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
      return editor;
    }

    function row(item) {
      const title = item.title || item.original_title;
      const original = item.original_title && title !== item.original_title ? '原标题 · ' + item.original_title : '';
      const actions = el('div', { className: 'organize-actions' });
      if (item.state === 'pending') actions.append(button('整理这段', () => change('start', targetOf(item)), !status.model_available));
      if (item.state === 'failed') actions.append(button('重试这段', () => change('retry', targetOf(item)), !status.model_available));
      actions.append(button('编辑名称', () => edit(item)));
      return el('li', { className: 'organize-row' }, el('b', { textContent: title }),
        ui.note([page.TOOLS[item.tool], item.dir, labels[item.state] || item.state, item.follow_up && '待跟进'].filter(Boolean).join(' · ')),
        original ? ui.note(original) : '', item.summary ? ui.note(item.summary) : '',
        item.tags?.length ? ui.note(item.tags.join(' · ')) : '', item.error ? ui.note(item.error, 'bad') : '', actions);
    }

    function confirmScope(verb, fields, count) {
      const description = count === undefined ? '重试这台电脑上整理失败的对话。' : '检查这台电脑的 ' + count + ' 段可读对话，已整理的会跳过。';
      view.sheet.push({ title: verb === 'retry' ? '重试失败记录' : '整理这台电脑的对话', build(p) {
        p.body.append(ui.note(computer), ui.note(description));
        p.foot.append(...ui.go(verb === 'retry' ? '重试失败记录' : '开始整理', async () => {
          if (await change(verb, fields)) p.sheet.goBack();
          else throw new Error(error);
        }, { busy: '正在提交…' }).nodes);
      } });
    }

    function paint() {
      if (!view?.controls) return;
      progress.textContent = status ? `排队 ${status.queued} · 整理中 ${status.running} · 已整理 ${status.done} · 失败 ${status.failed}` : '正在读取整理状态…';
      const controls = view.controls;
      if (!status) {
        controls.replaceChildren(error ? ui.note(error, 'bad') : '', error ? button('重试读取', () => load()) : '');
        return;
      }
      const focus = document.activeElement?.id;
      const model = el('select', { id: 'organize-model', className: 'rs-input', ariaLabel: '整理模型', disabled: busy });
      if (!status.model_available) model.append(el('option', { value: status.model, textContent: status.model + ' · 当前不可用' }));
      for (const choice of status.models) model.append(el('option', { value: choice.id, textContent: choice.label || choice.id }));
      model.value = status.model;
      model.onchange = () => change('settings', { model: model.value });
      const toggle = ui.toggle(status.auto, auto => change('settings', { auto }), '自动整理');
      toggle.id = 'organize-auto';
      toggle.disabled = busy || !status.auto && !status.model_available;
      const options = el('div', { className: 'organize-actions' });
      if (!selected) {
        options.append(button('整理全部可读对话', () => confirmScope('start', { scope: 'all' }, total), !total || !status.model_available));
        if (status.failed > 0) options.append(button('重试失败记录', () => confirmScope('retry', { scope: 'all' }), !status.model_available));
      }
      if (filtered.length) options.prepend(button(`整理当前筛选的 ${filtered.length} 段`, async () => {
        busy = true; error = ''; paint();
        let queued = 0, skipped = 0;
        const failures = [];
        for (const item of filtered) {
          try { const answer = await request('start', item); queued += answer.queued || 0; skipped += answer.skipped || 0; }
          catch (problem) { failures.push(problem.message); }
        }
        message = `已排队 ${queued} 段 · 跳过 ${skipped} 段` + (failures.length ? ` · ${failures.length} 段未提交` : '');
        await load();
        if (failures.length) { error = failures[0]; paint(); }
        page.refresh();
      }, !status.model_available));
      const list = el('ul', { className: 'organize-rows' }, ...items.map(row));
      controls.replaceChildren(
        el('label', { className: 'organize-field' }, '整理模型', model),
        ui.row({ label: '自动整理', detail: '开启后整理此后新增的对话', end: toggle }),
        status.model_available ? '' : ui.note('这台电脑暂时无法使用所选模型。请选择一个可用模型。'),
        message ? el('p', { className: 'rs-note', role: 'status', textContent: message }) : '',
        error ? ui.note(error, 'bad') : '', error ? button('刷新整理状态', () => load()) : '', options,
        items.length ? list : ui.empty(selected ? '这段对话暂无可整理记录' : '这台电脑暂无可整理的对话'),
        more ? button('加载更多', () => { limit += size; load(); }) : ''
      );
      if (focus) [...controls.querySelectorAll('[id]')].find(node => node.id === focus)?.focus({ preventScroll: true });
    }
  }

  window.RemoteOrganize = { open };
})();
