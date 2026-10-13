/* 我的: the owner's work with the tools in numbers, as the computer reads it from Claude Code's and Codex's own
   records (docs/PROTOCOL.md, `/api/work`, "stats"): today, and the last days by day, by tool, by project and by
   hour; and what program the computer runs. Numbers and names of folders only: nothing that was said. */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { $, el, TOOLS, api, newId } = page;
  const box = $('me'), kept = {};       // by how many days were asked for: when, and what the computer answered
  const METRIC = { prompts: '提问', active: '时长', tokens: 'token' }, WEEK = '日一二三四五六';
  let span = 7, metric = 'prompts', picked = -1, asking = 0, tried = 0, wanted = false, failed = '', drawn = '';

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
  function computer() {
    const device = page.data.device, tools = (device.tools || []).map(tool => TOOLS[tool]).join(' · ');
    const row = (name, value, more) => el('li', { className: 'row' }, el('span', { className: 'text' }, el('b', { textContent: name })), el('span', { className: 'value', textContent: value }), more || '');
    return el('section', { id: 'computer' }, head('电脑端'), el('ul', { className: 'group' },
      row('状态', page.usable() ? '在线' : device.online ? '已暂停手机访问' : '离线'),
      device.version ? row('版本', device.version, device.newer ? el('button', { type: 'button', className: 'act', textContent: '可更新到 ' + device.newer, onclick: () => { location.href = 'computer/'; } }) : '') : '',
      tools ? row('工具', tools) : ''));
  }

  function draw() {
    const data = page.data, supported = page.has('work'), online = page.usable(), have = kept[span];
    // asked anew when the tab is opened, and once a minute while it stays open
    const old = have ? Date.now() - have.at : Infinity;
    if (supported && online && !asking && (old > 60000 || (wanted && old > 15000)) && Date.now() - tried > 8000) { load(span); return; }
    const signature = JSON.stringify([span, metric, picked, have && have.at, asking, failed, supported, online, data.device.version, data.device.newer, data.device.tools]);
    if (signature === drawn) return;
    drawn = signature;
    const parts = [];
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
