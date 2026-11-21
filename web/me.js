/* 我的: how much of each tool's usage limits is used, as the computer reads it from the tools (device.quotas,
   agent-windows/Quotas.cs); the owner's work with the tools in numbers, as the computer reads it from Claude Code's
   and Codex's own records (docs/PROTOCOL.md, `/api/work`, "stats"): today, and the last days by day, by tool, by
   project and by hour; and what program the computer runs. Numbers and names of folders only: nothing that was said. */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { $, el, TOOLS, api, newId } = page;
  const box = $('me'), kept = {};       // by how many days were asked for: when, and what the computer answered
  const METRIC = { prompts: '提问', active: '时长', tokens: 'token' }, WEEK = '日一二三四五六';
  let span = 7, metric = 'prompts', picked = -1, asking = 0, tried = 0, wanted = false, failed = '', drawn = '', expanded = new URLSearchParams(location.search).has('stats');
  const expandedPools = {};

  // ---- numbers as they are read
  const trim = value => value >= 100 ? String(Math.round(value)) : value.toFixed(1).replace(/\.0$/, '');
  const count = n => n >= 1e8 ? trim(n / 1e8) + ' 亿' : n >= 1e4 ? trim(n / 1e4) + ' 万' : String(Math.round(n)).replace(/\B(?=(\d{3})+$)/g, ',');
  const lasted = s => { const minutes = Math.round(s / 60); return s <= 0 ? '0 分钟' : minutes < 1 ? '不到 1 分钟' : minutes < 60 ? minutes + ' 分钟' : Math.floor(minutes / 60) + ' 小时' + (minutes % 60 ? ' ' + minutes % 60 + ' 分' : ''); };
  const waited = s => s === null || s === undefined ? '暂无' : s < 60 ? trim(s) + ' 秒' : Math.floor(s / 60) + ' 分 ' + Math.round(s % 60) + ' 秒';
  const tokens = day => day.in + day.out;
  const of = (day, what) => what === 'tokens' ? tokens(day) : day[what];
  const said = (value, what) => what === 'prompts' ? value + ' 次提问' : what === 'active' ? lasted(value) : count(value) + ' token';
  const dated = key => { const [, month, day] = key.split('-').map(Number); return month + '月' + day + '日'; };
  const weekday = key => { const [year, month, day] = key.split('-').map(Number); return WEEK[new Date(year, month - 1, day).getDay()]; };
  // a figure with its units set smaller: "1 小时 20 分"
  const figure = text => /\d/.test(text) ? el('b', null, ...String(text).split(/(\d[\d.,]*)/).filter(Boolean).map(part => /^\d/.test(part) ? part : el('small', { textContent: part }))) : el('b', { className: 'none', textContent: text });

  async function load(days) {
    if (asking) return;
    asking = days; tried = Date.now(); wanted = false;
    draw();
    try { kept[days] = { at: Date.now(), stats: await api('api/work', { id: newId(), action: 'stats', days }) }; failed = ''; }
    catch (error) { failed = error.message; }
    asking = 0;
    draw();
  }

  // ---- the parts of the page
  const head = (title, right) => el('div', { className: 'section-head' }, el('h2', { textContent: title }), typeof right === 'string' ? el('p', { textContent: right }) : right || '');
  function seg(label, choices, chosen, changed) {
    const group = el('span', { className: 'seg', role: 'group', ariaLabel: label });
    choices.forEach(([value, text]) => group.append(el('button', { type: 'button', textContent: text, ariaPressed: String(value === chosen), onclick: () => changed(value) })));
    return group;
  }
  const tile = (name, value, fact) => el('div', { className: 'tile' }, el('span', { textContent: name }), figure(value), el('small', { textContent: fact }));
  function today(stats) {
    const days = stats.days, now = days[days.length - 1], before = days.length > 1 ? days[days.length - 2] : null;
    return el('section', { id: 'today' }, head('今天', dated(now.date) + ' 周' + weekday(now.date)), el('div', { className: 'tiles' },
      tile('提问', now.prompts + ' 次', before ? '昨天 ' + before.prompts + ' 次' : ''), tile('对话', now.sessions + ' 段', before ? '昨天 ' + before.sessions + ' 段' : ''),
      tile('工作时长', lasted(now.active), before ? '昨天 ' + lasted(before.active) : ''), tile('首次响应', waited(now.wait), '近 ' + days.length + ' 天 ' + waited(stats.wait))));
  }
  // The days as bars, for one of three measures. A bar that is pressed says its day above them; today's is said at first.
  function trend(stats) {
    const days = stats.days, most = Math.max(1, ...days.map(day => of(day, metric))), at = picked >= 0 && picked < days.length ? picked : days.length - 1;
    const bars = el('div', { className: 'bars' + (days.length > 10 ? ' many' : '') }, ...days.map((day, index) => el('button', { type: 'button', className: 'day', ariaPressed: String(index === at),
      ariaLabel: dated(day.date) + '：' + said(of(day, metric), metric), onclick: () => { picked = index; draw(); } }, el('i', { style: 'height:' + (of(day, metric) / most * 100).toFixed(1) + '%' }))));
    // under seven bars each has its day of the week; under thirty, a date now and then
    const axis = el('div', { className: 'axis' + (days.length > 10 ? ' few' : '') }, ...(days.length > 10
      ? [0, Math.floor((days.length - 1) / 2), days.length - 1].map(index => el('span', { textContent: index === days.length - 1 ? '今天' : dated(days[index].date) }))
      : days.map((day, index) => el('span', { textContent: index === days.length - 1 ? '今天' : weekday(day.date) }))));
    return el('div', { className: 'panel chart' }, el('div', { className: 'chart-head' }, seg('指标', Object.keys(METRIC).map(key => [key, METRIC[key]]), metric, value => { metric = value; draw(); }),
      el('p', { className: 'at', textContent: dated(days[at].date) + ' · ' + said(of(days[at], metric), metric) })), bars, axis);
  }
  // Who or what took how much of the time: a line each, with its share drawn under it.
  function shares(list, name) {
    const by = list.some(one => one.active > 0) ? 'active' : 'prompts', most = Math.max(1, ...list.map(one => one[by]));
    return el('ul', { className: 'panel shares' }, ...list.map(one => el('li', null,
      el('div', { className: 'line' }, el('b', { textContent: name(one.name) }), el('span', { textContent: lasted(one.active) + ' · ' + one.prompts + ' 次提问' })),
      el('div', { className: 'track' }, el('i', { style: 'width:' + Math.max(2, one[by] / most * 100).toFixed(1) + '%' })))));
  }
  function hours(stats) {
    const most = Math.max(1, ...stats.hours);
    return el('div', { className: 'panel chart' }, el('div', { className: 'bars hours', role: 'img', ariaLabel: '一天里各小时的提问次数' }, ...stats.hours.map(n => el('i', { style: 'height:' + (n / most * 100).toFixed(1) + '%' }))),
      el('div', { className: 'axis few' }, ...[0, 6, 12, 18, 24].map(hour => el('span', { textContent: String(hour) }))));
  }
  function period(stats) {
    const days = stats.days, sum = what => days.reduce((total, day) => total + of(day, what), 0), worked = days.filter(day => day.prompts > 0).length;
    const cell = (name, value) => el('div', null, el('span', { textContent: name }), figure(value));
    const parts = [head('近 ' + span + ' 天', seg('时间范围', [[7, '7 天'], [30, '30 天']], span, value => { span = value; picked = -1; draw(); }))];
    if (!sum('prompts') && !sum('tokens')) return el('section', { id: 'period' }, ...parts, el('p', { className: 'panel none', textContent: '这段时间没有记录' }));
    parts.push(el('div', { className: 'panel sums' }, cell('提问', sum('prompts') + ' 次'), cell('工作时长', lasted(sum('active'))), cell('有记录的天数', worked + ' 天'),
      cell('输入', count(sum('in'))), cell('输出', count(sum('out'))), cell('缓存', count(sum('cached')))), trend(stats));
    if (stats.tools.length) parts.push(head('工具'), shares(stats.tools, name => TOOLS[name] || name));
    if (stats.projects.length) parts.push(head('项目'), shares(stats.projects, name => name));
    const peak = stats.hours.indexOf(Math.max(...stats.hours));
    if (stats.hours.some(n => n > 0)) parts.push(head('时段', peak + ' 点最多'), hours(stats));
    return el('section', { id: 'period' }, ...parts);
  }
  // ---- the limits: primary pool shown directly per tool; secondary pools expand on tap
  const WINDOWS = { five_hour: '5 小时', seven_day: '本周' };
  const windowName = name => WINDOWS[name] || (/_five_hour$/.test(name) ? '5 小时' : /_seven_day$/.test(name) ? '本周' : /^\d+m$/.test(name) ? (parseInt(name, 10) >= 1440 ? Math.round(parseInt(name, 10) / 1440) + ' 天' : Math.round(parseInt(name, 10) / 60) + ' 小时') : name);
  function renews(seconds) {
    if (!seconds) return '';
    const left = seconds * 1000 - Date.now();
    if (left <= 0) return '已重置';
    const minutes = Math.round(left / 60000);
    return (minutes < 60 ? minutes + ' 分钟' : minutes < 2880 ? Math.round(minutes / 60) + ' 小时' : Math.round(minutes / 1440) + ' 天') + '后重置';
  }
  const quotaLevel = used => used >= 80 ? 'high' : used > 50 ? 'elevated' : '';
  function quotaRow(title, w) {
    return el('li', { className: quotaLevel(w.used) },
      el('div', { className: 'line' }, el('b', { textContent: title }),
        el('span', { textContent: Math.round(w.used) + '%' + (w.resets ? ' · ' + renews(w.resets) : '') })),
      el('div', { className: 'track', role: 'img', ariaLabel: '已用 ' + Math.round(w.used) + '%' }, el('i', { style: 'width:' + Math.max(2, Math.min(100, w.used)).toFixed(1) + '%' })));
  }
  function quotas() {
    const known = (page.data.device.quotas || []).filter(q => q.windows.length || q.why || q.billing);
    if (!known.length) return '';
    const lines = [];
    known.forEach(q => {
      const line = (title, detail) => el('li', null, el('div', { className: 'line' }, el('b', { textContent: title }), el('span', { textContent: detail })));
      if (q.billing?.type === 'api') lines.push(line('Codex · API 余额',
        q.billing.balance && q.billing.unit ? q.billing.balance + ' ' + q.billing.unit : '暂未读到余额'));
      if (q.billing?.type === 'credits') lines.push(line('Codex · 额外额度', q.billing.unlimited ? '不限量' : q.billing.balance + ' 点'));
      if (q.why && !q.windows.length) lines.push(line(TOOLS[q.tool] || q.tool, q.why));
      const main = q.windows.filter(w => !w.group), extra = main.length ? q.windows.filter(w => w.group) : [];
      (main.length ? main : q.windows).forEach(w => lines.push(quotaRow((w.group || TOOLS[q.tool] || q.tool) + ' · ' + windowName(w.name), w)));
      if (extra.length) {
        const open = !!expandedPools[q.tool], pools = new Set(extra.map(w => w.group)).size, warn = !open && extra.some(w => quotaLevel(w.used) === 'high');
        const toggle = el('button', { type: 'button', className: 'quota-more' + (warn ? ' warn' : ''), ariaExpanded: String(open),
          onclick: () => { expandedPools[q.tool] = !open; draw(); } },
          el('span', { textContent: '其他 ' + pools + ' 个池子' }),
          el('b', { textContent: (warn ? '接近上限 · ' : '') + (open ? '收起 ▴' : '展开 ▾') }));
        const wrap = el('li', { className: 'quota-extra-wrap' }, toggle);
        if (open) wrap.append(el('ul', { className: 'quota-extra' }, ...extra.map(w => quotaRow(w.group + ' · ' + windowName(w.name), w))));
        lines.push(wrap);
      }
    });
    const newest = Math.max(...known.map(q => q.at));
    return el('section', { id: 'quotas' }, head('额度', page.ago(newest) + '更新'), el('ul', { className: 'panel shares' }, ...lines));
  }
  function computer() {
    const device = page.data.device, tools = (device.tools || []).map(tool => TOOLS[tool]).join(' · ');
    const row = (name, value, more) => el('li', { className: 'row' }, el('span', { className: 'text' }, el('b', { textContent: name })), el('span', { className: 'value', textContent: value }), more || '');
    return el('section', { id: 'computer' }, head('电脑端'), el('ul', { className: 'group' },
      row('状态', page.usable() ? '在线' : device.online ? '已暂停手机访问' : '离线'),
      device.version ? row('版本', device.version, device.newer ? el('button', { type: 'button', className: 'act', textContent: '可更新到 ' + device.newer, onclick: () => { location.href = 'computer/'; } }) : '') : '',
      tools ? row('工具', tools) : ''));
  }

  const ICONS = {
    bell: page.svg('<path d="M6.5 16.5h11l-1.3-2.2V10a4.2 4.2 0 1 0-8.4 0v4.3z"/><path d="M10 19a2 2 0 0 0 4 0"/>', 18),
    look: page.svg('<circle cx="12" cy="12" r="8.5"/><path d="M12 3.5a8.5 8.5 0 0 0 0 17c1.4 0 1.8-1 1.3-2-.6-1.2.2-2.5 1.6-2.5h2.1A3.5 3.5 0 0 0 20.5 12 8.5 8.5 0 0 0 12 3.5z"/><circle cx="8" cy="11" r="1.1"/><circle cx="11.5" cy="7.8" r="1.1"/><circle cx="15.5" cy="9" r="1.1"/>', 18),
    computer: page.svg('<rect x="3.5" y="5" width="17" height="11.5" rx="2.2"/><path d="M8.5 19.5h7M12 16.5v3"/>', 18),
    chart: page.svg('<path d="M5 19V11M10.5 19V6M16 19v-5M19.5 19H3.5"/>', 18),
    update: page.svg('<path d="M12 4.5v10M8 10.5l4 4 4-4"/><path d="M5 18.5h14"/>', 18)
  };
  function settings() {
    const native = window.RemoteCliNative, group = (title, ...rows) => el('section', { className: 'settings-section' }, head(title), el('div', { className: 'settings-group' }, ...rows));
    const row = (icon, tint, label, description, value, action, tone) => el('button', { type: 'button', className: 'setting-row', onclick: action },
      el('span', { className: 'setting-glyph ' + tint, html: ICONS[icon] }),
      el('span', { className: 'setting-copy' }, el('b', { textContent: label }), description ? el('small', { textContent: description }) : ''),
      value ? el('span', { className: 'setting-value' + (tone ? ' ' + tone : ''), textContent: value }) : '',
      el('span', { className: 'setting-chevron', ariaHidden: 'true', html: page.ICON.chev }));
    const deviceRows = [];
    if (native && native.reminders) deviceRows.push(row('bell', 't-warn', '提醒与权限', '通知、电池与后台联网', native.reminderStatus ? native.reminderStatus() : '', () => native.reminders()));
    deviceRows.push(row('look', 't-look', '外观与文字', '主题、字号与终端阅读方式', '', () => { location.href = 'computer/#part-look'; }));
    if (page.has('teams-models') || page.has('teams')) {
      const defSummary = window.RemoteTeamDefaults ? window.RemoteTeamDefaults.summary((page.data && page.data.device && page.data.device.tools) || []) : '';
      deviceRows.push(row('computer', 't-agy', '协作默认模型', defSummary || 'Opus 5.5 · Astra 6.2 · Neon · 思考高', '', () => { location.href = 'computer/#part-team-defaults'; }));
    }
    deviceRows.push(row('computer', 't-accent', '电脑设置', '连接线路、远程访问与工具', page.usable() ? '在线' : '离线', () => { location.href = 'computer/'; }, page.usable() ? 'on' : ''));
    const dataRows = [row('chart', 't-agy', '工作统计', '提问、对话与工作时长', expanded ? '收起' : '查看', () => { expanded = !expanded; draw(); })];
    if (native && native.update) dataRows.push(row('update', 't-muted', '检查更新', 'Remote AI 手机端', native.version ? 'v' + native.version() : '', () => native.update()));
    return el('div', { className: 'settings-grid' }, group('偏好与设备', ...deviceRows), group('数据与版本', ...dataRows));
  }
  function draw() {
    const data = page.data, supported = page.has('work'), online = page.usable(), have = kept[span];
    // asked anew when the tab is opened, and once a minute while it stays open
    const old = have ? Date.now() - have.at : Infinity;
    if (expanded && supported && online && !asking && (old > 60000 || (wanted && old > 15000)) && Date.now() - tried > 8000) { load(span); return; }
    const signature = JSON.stringify([expanded, Object.keys(expandedPools).filter(k => expandedPools[k]).sort(), span, metric, picked, have && have.at, asking, failed, supported, online, data.device.version, data.device.newer, data.device.tools, data.device.quotas, Math.floor(Date.now() / 60000)]);
    if (signature === drawn) return;
    drawn = signature;
    const parts = [quotas(), el('p', { className: 'settings-intro', textContent: '设备连接与个人偏好' }), settings()];
    if (!expanded) { box.replaceChildren(...parts); return; }
    if (!supported) parts.push(el('section', { className: 'notice plain' }, el('b', { textContent: '需要更新电脑端' }), el('span', { textContent: '工作统计由电脑端读取，这台电脑的版本还没有这项功能。' })));
    else if (have) parts.push(today(have.stats), period(have.stats),
      el('p', { className: 'hint', textContent: '按这台电脑上 Claude Code 与 Codex 的记录统计 · ' + page.ago(have.at) + '更新' }));
    else if (asking) parts.push(el('div', { className: 'skeleton', ariaHidden: 'true' }, el('i'), el('i'), el('i')));
    else parts.push(el('section', { className: 'notice plain' }, el('b', { textContent: online ? '没有读到统计' : '电脑离线' }), el('span', { textContent: online ? failed || '请稍后再试。' : '电脑上线后显示工作统计。' })));
    parts.push(computer());
    box.replaceChildren(...parts);
  }

  page.view('me', { open: () => { wanted = true; tried = 0; }, draw });
})();
