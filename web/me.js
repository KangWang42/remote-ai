/* 我的: remaining usage limits per tool (device.quotas, agent-windows/Quotas.cs), continuous daily work statistics
   across Claude Code, Codex, and Antigravity CLI (docs/PROTOCOL.md, `/api/work`, "stats"), and computer status.
   Numbers and folder names only: nothing that was said leaves the computer. */
(() => {
  'use strict';
  const page = window.RemoteCliPage, { $, el, TOOLS, api, newId } = page;
  const box = $('me'), kept = {};       // by span days: { at, stats, cached }
  const METRIC = { prompts: '提问', active: '时长', tokens: 'Token' }, WEEK = '日一二三四五六';
  const TOOL_ORDER = ['claude', 'codex', 'agy'];
  const TOOL_MONO = { claude: 'CC', codex: 'CX', agy: 'AG' };
  const TOOL_SHORT = { claude: 'Claude', codex: 'Codex', agy: 'Antigravity' };
  let span = 7, metric = 'prompts', picked = -1, pickedHour = -1, asking = 0, tried = 0, wanted = false, failed = '', drawn = '';
  let expanded = new URLSearchParams(location.search).has('stats'), quotaAnimated = false, statsAnimated = false;
  const expandedPools = {};

  // ---- persistent local continuity cache so updates/restarts never flash empty
  function cacheKey(days) {
    const id = (page.data && page.data.device && page.data.device.instance) || 'default';
    return 'rcli:stats:v2:' + id + ':' + days;
  }
  function readCache(days) {
    try {
      const raw = localStorage.getItem(cacheKey(days));
      if (!raw) return null;
      const parsed = JSON.parse(raw);
      if (!parsed || !parsed.stats || !Array.isArray(parsed.stats.days)) return null;
      return { at: Number(parsed.at) || 0, stats: parsed.stats, cached: true };
    } catch { return null; }
  }
  function writeCache(days, stats, at) {
    try { localStorage.setItem(cacheKey(days), JSON.stringify({ at, stats })); } catch { }
  }
  function ensureCached(days) {
    if (!kept[days]) {
      const hit = readCache(days);
      if (hit) kept[days] = hit;
    }
    return kept[days] || null;
  }

  // ---- numbers & readable formatting
  const trim = value => value >= 100 ? String(Math.round(value)) : value.toFixed(1).replace(/\.0$/, '');
  const count = n => {
    const v = Math.max(0, Number(n) || 0);
    return v >= 1e8 ? trim(v / 1e8) + ' 亿' : v >= 1e4 ? trim(v / 1e4) + ' 万' : String(Math.round(v)).replace(/\B(?=(\d{3})+$)/g, ',');
  };
  const lasted = s => {
    const sec = Math.max(0, Number(s) || 0);
    const minutes = Math.round(sec / 60);
    return sec <= 0 ? '0 分钟' : minutes < 1 ? '不到 1 分钟' : minutes < 60 ? minutes + ' 分钟' : Math.floor(minutes / 60) + ' 小时' + (minutes % 60 ? ' ' + minutes % 60 + ' 分' : '');
  };
  const waited = s => s === null || s === undefined ? '暂无' : s < 60 ? trim(s) + ' 秒' : Math.floor(s / 60) + ' 分 ' + Math.round(s % 60) + ' 秒';
  const totalTokensOf = day => (Number(day.in) || 0) + (Number(day.out) || 0) + (Number(day.cached) || 0);
  const toolTotalTokens = t => (Number(t.tokens) || 0) + (Number(t.cached) || 0);
  const of = (day, what) => what === 'tokens' ? totalTokensOf(day) : (Number(day[what]) || 0);
  const said = (value, what) => what === 'prompts' ? value + ' 次提问' : what === 'active' ? lasted(value) : count(value) + ' token';
  const dated = key => { const [, month, day] = key.split('-').map(Number); return month + '月' + day + '日'; };
  const weekday = key => { const [year, month, day] = key.split('-').map(Number); return WEEK[new Date(year, month - 1, day).getDay()]; };
  // a figure with its units set smaller: "1 小时 20 分"
  const figure = text => /\d/.test(text) ? el('b', null, ...String(text).split(/(\d[\d.,]*)/).filter(Boolean).map(part => /^\d/.test(part) ? part : el('small', { textContent: part }))) : el('b', { className: 'none', textContent: text });

  function deltaBadge(todayVal, beforeVal, kind) {
    if (beforeVal === null || beforeVal === undefined) return { text: '', tone: '' };
    if (kind === 'active') {
      const tMin = Math.round((todayVal || 0) / 60), bMin = Math.round((beforeVal || 0) / 60), diff = tMin - bMin;
      if (diff === 0) return { text: bMin === 0 ? '昨天 0 分钟' : '与昨持平', tone: '' };
      return { text: diff > 0 ? '较昨 +' + diff + ' 分' : '较昨 ' + diff + ' 分', tone: diff > 0 ? 'up' : 'down' };
    }
    const diff = (todayVal || 0) - (beforeVal || 0);
    if (diff === 0) return { text: beforeVal === 0 ? '昨天 0' : '与昨持平', tone: '' };
    return { text: diff > 0 ? '较昨 +' + diff : '较昨 ' + diff, tone: diff > 0 ? 'up' : 'down' };
  }

  function computeStreak(stats) {
    if (typeof stats.streak === 'number' && stats.streak >= 0) return stats.streak;
    const days = stats.days || [];
    let i = days.length - 1;
    if (i > 0 && !(days[i].prompts > 0)) i--;
    let run = 0;
    while (i >= 0 && days[i].prompts > 0) { run++; i--; }
    return run;
  }

  function hourSlotLabel(hours) {
    const blocks = [
      ['凌晨', hours.slice(0, 6).reduce((a, b) => a + b, 0)],
      ['上午', hours.slice(6, 12).reduce((a, b) => a + b, 0)],
      ['下午', hours.slice(12, 18).reduce((a, b) => a + b, 0)],
      ['夜间', hours.slice(18, 24).reduce((a, b) => a + b, 0)]
    ].sort((a, b) => b[1] - a[1]);
    return blocks[0][1] > 0 ? blocks[0][0] + '为主' : '';
  }

  async function load(days) {
    if (asking) return;
    asking = days; tried = Date.now(); wanted = false;
    draw();
    try {
      const fresh = await api('api/work', { id: newId(), action: 'stats', days });
      const now = Date.now();
      kept[days] = { at: now, stats: fresh, cached: false };
      writeCache(days, fresh, now);
      failed = '';
    } catch (error) {
      failed = error.message;
    }
    asking = 0;
    draw();
  }

  // ---- reusable UI helpers
  const head = (title, right) => el('div', { className: 'section-head' }, el('h2', { textContent: title }), typeof right === 'string' ? el('p', { textContent: right }) : right || '');
  function seg(label, choices, chosen, changed) {
    const group = el('span', { className: 'seg', role: 'group', ariaLabel: label });
    choices.forEach(([value, text]) => group.append(el('button', { type: 'button', textContent: text, ariaPressed: String(value === chosen), onclick: () => changed(value) })));
    return group;
  }
  const tile = (name, value, fact, tone) => el('div', { className: 'tile' },
    el('span', { textContent: name }),
    figure(value),
    el('small', { className: tone ? 'delta ' + tone : '', textContent: fact }));

  function continuityStrip(have, online) {
    const stats = have.stats, streak = computeStreak(stats);
    const ledgerDays = Number(stats.ledger_days) || (stats.days || []).filter(d => d.prompts > 0).length;
    const sources = Array.isArray(stats.sources) && stats.sources.length ? stats.sources : ['claude', 'codex'];
    const left = el('div', { className: 'stats-badges' });
    if (streak > 0) left.append(el('span', { className: 'stats-pill streak', textContent: '连续 ' + streak + ' 天' }));
    if (ledgerDays > 0) left.append(el('span', { className: 'stats-pill', textContent: '累计活跃 ' + ledgerDays + ' 天' }));
    const rightText = have.cached
      ? (online ? '显示缓存 · 正在同步…' : '电脑离线 · 显示缓存')
      : sources.map(s => TOOL_SHORT[s] || s).join(' · ') + ' · ' + page.ago(have.at) + '更新';
    return el('div', { className: 'stats-meta' + (have.cached ? ' cached' : '') }, left, el('span', { className: 'stats-sync', textContent: rightText }));
  }

  function today(stats) {
    const days = stats.days, now = days[days.length - 1], before = days.length > 1 ? days[days.length - 2] : null;
    const dPrompts = deltaBadge(now.prompts, before ? before.prompts : null);
    const dSessions = deltaBadge(now.sessions, before ? before.sessions : null);
    const dActive = deltaBadge(now.active, before ? before.active : null, 'active');
    return el('section', { id: 'today' },
      head('今天', dated(now.date) + ' 周' + weekday(now.date)),
      el('div', { className: 'tiles' },
        tile('提问', now.prompts + ' 次', dPrompts.text, dPrompts.tone),
        tile('对话', now.sessions + ' 段', dSessions.text, dSessions.tone),
        tile('工作时长', lasted(now.active), dActive.text, dActive.tone),
        tile('首次响应', waited(now.wait), '近 ' + days.length + ' 天 ' + waited(stats.wait))));
  }

  function tokenCard(days) {
    const freshIn = days.reduce((s, d) => s + (Number(d.in) || 0), 0);
    const outTok = days.reduce((s, d) => s + (Number(d.out) || 0), 0);
    const cachedTok = days.reduce((s, d) => s + (Number(d.cached) || 0), 0);
    const total = freshIn + outTok + cachedTok;
    if (total <= 0) return '';
    const cacheDenom = freshIn + cachedTok;
    const hitRate = cacheDenom > 0 ? Math.round(cachedTok / cacheDenom * 100) : 0;
    const pct = v => (v / total * 100).toFixed(2) + '%';
    const bar = el('div', { className: 'token-bar', role: 'img', ariaLabel: 'Token 构成：新输入、输出与缓存复用' },
      freshIn > 0 ? el('i', { className: 'seg-in', style: 'width:' + pct(freshIn) }) : '',
      outTok > 0 ? el('i', { className: 'seg-out', style: 'width:' + pct(outTok) }) : '',
      cachedTok > 0 ? el('i', { className: 'seg-cache', style: 'width:' + pct(cachedTok) }) : '');
    const legendItem = (cls, label, val) => el('div', { className: 'token-cell' },
      el('span', null, el('i', { className: 't-dot ' + cls }), label),
      figure(count(val)));
    return el('div', { className: 'panel token-card' },
      el('div', { className: 'token-head' },
        el('div', null, el('b', { textContent: 'Token 吞吐' }), el('small', { textContent: count(total) + ' token' })),
        cacheDenom > 0 ? el('span', { className: 'stats-pill cache', textContent: '缓存复用 ' + hitRate + '%' }) : ''),
      bar,
      el('div', { className: 'token-grid' },
        legendItem('seg-in', '新输入', freshIn),
        legendItem('seg-out', '输出', outTok),
        legendItem('seg-cache', '缓存复用', cachedTok)));
  }

  function dayToolValue(toolObj, what) {
    if (!toolObj) return 0;
    return what === 'tokens' ? toolTotalTokens(toolObj) : (Number(toolObj[what]) || 0);
  }

  // Stacked multi-AI daily trend bars with average line and interactive selected-day inspector.
  function trend(stats) {
    const days = stats.days;
    const values = days.map(day => of(day, metric));
    const most = Math.max(1, ...values);
    const avg = values.reduce((a, b) => a + b, 0) / Math.max(1, days.length);
    const at = picked >= 0 && picked < days.length ? picked : days.length - 1;
    const activeDay = days[at];
    const hasTools = days.some(d => d.tools && Object.keys(d.tools).length > 0);
    const seenTools = new Set();

    const bars = el('div', { className: 'bars' + (days.length > 10 ? ' many' : '') }, ...days.map((day, index) => {
      const total = of(day, metric);
      const heightPct = total > 0 ? Math.max(3, total / most * 100) : 0;
      const col = el('div', { className: 'bar-stack', style: 'height:' + heightPct.toFixed(1) + '%' });
      if (total > 0 && hasTools && day.tools) {
        let used = 0;
        const present = [];
        for (const key of TOOL_ORDER) {
          const v = dayToolValue(day.tools[key], metric);
          if (v > 0) { present.push([key, v]); used += v; seenTools.add(key); }
        }
        const rem = Math.max(0, total - used);
        if (rem > 0) present.push(['other', rem]);
        const sumParts = present.reduce((s, p) => s + p[1], 0) || total;
        present.forEach(([key, v]) => col.append(el('span', { className: 'bar-seg t-' + key, style: 'flex:' + (v / sumParts).toFixed(4) })));
      } else {
        col.append(el('span', { className: 'bar-seg t-other', style: 'flex:1' }));
      }
      return el('button', {
        type: 'button',
        className: 'day' + (total <= 0 ? ' empty' : ''),
        ariaPressed: String(index === at),
        ariaLabel: dated(day.date) + '：' + said(total, metric),
        onclick: () => { picked = index; draw(); }
      }, col);
    }));

    const barsWrap = el('div', { className: 'bars-wrap' }, bars);
    if (avg > 0) {
      const avgPct = Math.min(96, Math.max(6, avg / most * 100));
      const avgText = metric === 'prompts' ? trim(avg) + ' 次' : metric === 'active' ? lasted(avg) : count(avg);
      barsWrap.append(el('div', { className: 'avg-line', style: 'bottom:' + avgPct.toFixed(1) + '%' },
        el('span', { textContent: '日均 ' + avgText })));
    }

    const axis = el('div', { className: 'axis' + (days.length > 10 ? ' few' : '') }, ...(days.length > 10
      ? [0, Math.floor((days.length - 1) / 2), days.length - 1].map(index => el('span', { textContent: index === days.length - 1 ? '今天' : dated(days[index].date) }))
      : days.map((day, index) => el('span', { textContent: index === days.length - 1 ? '今天' : weekday(day.date) }))));

    const chips = el('div', { className: 'day-chips' });
    if (activeDay.sessions > 0) chips.append(el('span', { className: 'day-chip', textContent: activeDay.sessions + ' 段对话' }));
    if (activeDay.active > 0 && metric !== 'active') chips.append(el('span', { className: 'day-chip', textContent: lasted(activeDay.active) }));
    if (activeDay.wait !== null && activeDay.wait !== undefined) chips.append(el('span', { className: 'day-chip', textContent: '首响 ' + waited(activeDay.wait) }));
    if (activeDay.tools) {
      for (const key of TOOL_ORDER) {
        const v = dayToolValue(activeDay.tools[key], metric);
        if (v > 0) {
          const valText = metric === 'prompts' ? v + ' 次' : metric === 'active' ? lasted(v) : count(v);
          chips.append(el('span', { className: 'day-chip tool-' + key }, el('i', { className: 't-dot t-' + key }), (TOOL_SHORT[key] || key) + ' ' + valText));
        }
      }
    }

    const legend = seenTools.size > 0
      ? el('div', { className: 'chart-legend' }, ...TOOL_ORDER.filter(k => seenTools.has(k)).map(k =>
        el('span', null, el('i', { className: 't-dot t-' + k }), TOOLS[k] || TOOL_SHORT[k] || k)))
      : '';

    return el('div', { className: 'panel chart' },
      el('div', { className: 'chart-head' },
        seg('指标', Object.keys(METRIC).map(key => [key, METRIC[key]]), metric, value => { metric = value; draw(); }),
        el('div', { className: 'day-inspect' },
          el('p', { className: 'at', textContent: (at === days.length - 1 ? '今天 · ' : dated(activeDay.date) + ' 周' + weekday(activeDay.date) + ' · ') + said(of(activeDay, metric), metric) }),
          chips.childElementCount ? chips : '')),
      barsWrap, axis, legend);
  }

  function shares(list, name, isTools) {
    const by = list.some(one => one.active > 0) ? 'active' : 'prompts';
    const sumBy = list.reduce((s, one) => s + (Number(one[by]) || 0), 0) || 1;
    const most = Math.max(1, ...list.map(one => Number(one[by]) || 0));
    return el('ul', { className: 'panel shares' }, ...list.map(one => {
      const sharePct = Math.round((Number(one[by]) || 0) / sumBy * 100);
      const tokTotal = toolTotalTokens(one);
      const detail = lasted(one.active) + ' · ' + one.prompts + ' 次提问' + (tokTotal > 0 ? ' · ' + count(tokTotal) + ' token' : '');
      const titleNode = isTools && TOOL_MONO[one.name]
        ? el('span', { className: 'share-name' }, el('i', { className: 'share-badge t-' + one.name, textContent: TOOL_MONO[one.name] }), el('b', { textContent: name(one.name) }))
        : el('span', { className: 'share-name' }, el('b', { textContent: name(one.name) }));
      return el('li', null,
        el('div', { className: 'line' }, titleNode, el('em', { className: 'share-pct', textContent: sharePct + '%' })),
        el('div', { className: 'track' }, el('i', { className: isTools ? 't-' + one.name : '', style: 'width:' + Math.max(2, (Number(one[by]) || 0) / most * 100).toFixed(1) + '%' })),
        el('div', { className: 'share-detail', textContent: detail }));
    }));
  }

  function hours(stats) {
    const list = stats.hours || [];
    const most = Math.max(1, ...list);
    const peak = list.indexOf(Math.max(...list));
    const shownHour = pickedHour >= 0 && pickedHour < 24 ? pickedHour : peak;
    const hourNote = shownHour >= 0 ? String(shownHour).padStart(2, '0') + ':00–' + String((shownHour + 1) % 24).padStart(2, '0') + ':00 · ' + (list[shownHour] || 0) + ' 次提问' : '';
    return el('div', { className: 'panel chart hours-panel' },
      el('div', { className: 'hours-head' },
        el('span', { className: 'hours-note', textContent: hourNote }),
        el('span', { className: 'hours-slot', textContent: hourSlotLabel(list) })),
      el('div', { className: 'bars hours', role: 'group', ariaLabel: '一天里各小时的提问次数' }, ...list.map((n, h) =>
        el('button', {
          type: 'button',
          className: 'hour-bar' + (h === peak && n > 0 ? ' peak' : ''),
          ariaPressed: String(h === shownHour),
          ariaLabel: h + ' 点：' + n + ' 次提问',
          onclick: () => { pickedHour = h; draw(); }
        }, el('i', { style: 'height:' + (n > 0 ? Math.max(6, n / most * 100) : 0).toFixed(1) + '%' })))),
      el('div', { className: 'axis few' }, ...[0, 6, 12, 18, 24].map(hour => el('span', { textContent: String(hour).padStart(2, '0') }))));
  }

  function period(stats) {
    const days = stats.days;
    const sum = what => days.reduce((total, day) => total + of(day, what), 0);
    const worked = days.filter(day => day.prompts > 0).length;
    const avgPrompts = trim(sum('prompts') / Math.max(1, days.length));
    const streak = computeStreak(stats);
    const bestStreak = Math.max(streak, Number(stats.best_streak) || 0);
    const cell = (name, value, sub) => el('div', null, el('span', { textContent: name }), figure(value), sub ? el('small', { className: 'sum-sub', textContent: sub }) : '');
    const parts = [head('近 ' + span + ' 天', seg('时间范围', [[7, '7 天'], [14, '14 天'], [30, '30 天']], span, value => { span = value; picked = -1; pickedHour = -1; draw(); }))];
    if (!sum('prompts') && !sum('tokens')) return el('section', { id: 'period' }, ...parts, el('p', { className: 'panel none', textContent: '这段时间没有记录' }));
    parts.push(
      el('div', { className: 'panel sums' },
        cell('提问', sum('prompts') + ' 次', '日均 ' + avgPrompts + ' 次'),
        cell('工作时长', lasted(sum('active')), '活跃 ' + worked + '/' + days.length + ' 天'),
        cell('连续活跃', streak + ' 天', bestStreak > streak ? '最长 ' + bestStreak + ' 天' : '保持连续')),
      tokenCard(days),
      trend(stats));
    if (stats.tools && stats.tools.length) parts.push(head('工具分布'), shares(stats.tools, name => TOOLS[name] || name, true));
    if (stats.projects && stats.projects.length) parts.push(head('项目分布'), shares(stats.projects, name => name, false));
    const peak = (stats.hours || []).indexOf(Math.max(...(stats.hours || [0])));
    if ((stats.hours || []).some(n => n > 0)) parts.push(head('活跃时段', peak + ' 点高峰'), hours(stats));
    const firstStatsDraw = !statsAnimated;
    statsAnimated = true;
    return el('section', { id: 'period', className: firstStatsDraw ? 'stats-reveal' : '' }, ...parts);
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
  const quotaLeft = used => Math.max(0, Math.min(100, 100 - (Number(used) || 0)));
  const quotaBar = used => { const left = quotaLeft(used); return left <= 0 ? 0 : Math.max(2, Math.min(100, left)); };
  const quotaLevel = used => used >= 80 ? 'high' : used > 50 ? 'elevated' : '';
  function quotaRow(title, w, index) {
    const left = quotaLeft(w.used), shown = Math.round(left);
    return el('li', { className: quotaLevel(w.used), style: '--stagger:' + Math.min(index || 0, 8) },
      el('div', { className: 'line' }, el('b', { textContent: title }),
        el('span', { textContent: '剩余 ' + shown + '%' + (w.resets ? ' · ' + renews(w.resets) : '') })),
      el('div', { className: 'track', role: 'img', ariaLabel: '剩余 ' + shown + '%' }, el('i', { style: 'width:' + quotaBar(w.used).toFixed(1) + '%' })));
  }
  function quotas() {
    const known = (page.data.device.quotas || []).filter(q => q.windows.length || q.why || q.billing);
    if (!known.length) return '';
    const lines = [];
    let rowIdx = 0;
    known.forEach(q => {
      const line = (title, detail) => el('li', null, el('div', { className: 'line' }, el('b', { textContent: title }), el('span', { textContent: detail })));
      if (q.billing?.type === 'api') lines.push(line('Codex · API 余额',
        q.billing.balance && q.billing.unit ? q.billing.balance + ' ' + q.billing.unit : '暂未读到余额'));
      if (q.billing?.type === 'credits') lines.push(line('Codex · 额外额度', q.billing.unlimited ? '不限量' : q.billing.balance + ' 点'));
      if (q.why && !q.windows.length) lines.push(line(TOOLS[q.tool] || q.tool, q.why));
      const main = q.windows.filter(w => !w.group), extra = main.length ? q.windows.filter(w => w.group) : [];
      (main.length ? main : q.windows).forEach(w => lines.push(quotaRow((w.group || TOOLS[q.tool] || q.tool) + ' · ' + windowName(w.name), w, rowIdx++)));
      if (extra.length) {
        const open = !!expandedPools[q.tool], pools = new Set(extra.map(w => w.group)).size, warn = !open && extra.some(w => quotaLevel(w.used) === 'high');
        const toggle = el('button', { type: 'button', className: 'quota-more' + (warn ? ' warn' : ''), ariaExpanded: String(open),
          onclick: () => { expandedPools[q.tool] = !open; draw(); } },
          el('span', { textContent: '其他 ' + pools + ' 个池子' }),
          el('b', { textContent: (warn ? '余量不足 · ' : '') + (open ? '收起 ▴' : '展开 ▾') }));
        const wrap = el('li', { className: 'quota-extra-wrap' }, toggle);
        if (open) wrap.append(el('ul', { className: 'quota-extra' }, ...extra.map((w, idx) => quotaRow(w.group + ' · ' + windowName(w.name), w, idx))));
        lines.push(wrap);
      }
    });
    const newest = Math.max(...known.map(q => q.at));
    const firstDraw = !quotaAnimated;
    quotaAnimated = true;
    return el('section', { id: 'quotas', className: firstDraw ? 'quota-reveal' : '' }, head('剩余额度', page.ago(newest) + '更新'), el('ul', { className: 'panel shares' }, ...lines));
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
      deviceRows.push(row('computer', 't-agy', '模型与常用参数', defSummary || '可视化设置模型、思考深度与权限', '', () => { location.href = 'computer/#part-team-defaults'; }));
    }
    deviceRows.push(row('computer', 't-accent', '电脑设置', '连接线路、远程访问与工具', page.usable() ? '在线' : '离线', () => { location.href = 'computer/'; }, page.usable() ? 'on' : ''));
    const dataRows = [row('chart', 't-agy', '工作统计', '每日趋势、工具占比与 Token 吞吐', expanded ? '收起' : '查看', () => { expanded = !expanded; draw(); })];
    if (native && native.update) dataRows.push(row('update', 't-muted', '检查更新', 'Remote AI 手机端', native.version ? 'v' + native.version() : '', () => native.update()));
    return el('div', { className: 'settings-grid' }, group('偏好与设备', ...deviceRows), group('数据与版本', ...dataRows));
  }
  function draw() {
    const data = page.data, supported = page.has('work'), online = page.usable(), have = ensureCached(span);
    // asked anew when the tab is opened, and once a minute while it stays open; cached stats stay visible while refreshing
    const old = have ? (have.cached ? Infinity : Date.now() - have.at) : Infinity;
    if (expanded && supported && online && !asking && (old > 60000 || (wanted && old > 15000)) && Date.now() - tried > 8000) { load(span); return; }
    const signature = JSON.stringify([expanded, Object.keys(expandedPools).filter(k => expandedPools[k]).sort(), span, metric, picked, pickedHour, have && have.at, have && have.cached, asking, failed, supported, online, data.device.version, data.device.newer, data.device.tools, data.device.quotas, Math.floor(Date.now() / 60000)]);
    if (signature === drawn) return;
    drawn = signature;
    const parts = [quotas(), el('p', { className: 'settings-intro', textContent: '设备连接与个人偏好' }), settings()];
    if (!expanded) { box.replaceChildren(...parts); return; }
    if (!supported) parts.push(el('section', { className: 'notice plain' }, el('b', { textContent: '需要更新电脑端' }), el('span', { textContent: '工作统计由电脑端读取，这台电脑的版本还没有这项功能。' })));
    else if (have) parts.push(continuityStrip(have, online), today(have.stats), period(have.stats),
      el('p', { className: 'hint', textContent: '数据已本地持久化账本保存，更新或重启不会清零 · 仅统计数字与项目名' }));
    else if (asking) parts.push(el('div', { className: 'skeleton', ariaHidden: 'true' }, el('i'), el('i'), el('i')));
    else parts.push(el('section', { className: 'notice plain' }, el('b', { textContent: online ? '没有读到统计' : '电脑离线' }), el('span', { textContent: online ? failed || '请稍后再试。' : '电脑上线后显示工作统计。' })));
    parts.push(computer());
    box.replaceChildren(...parts);
  }

  page.view('me', { open: () => { wanted = true; tried = 0; }, draw });
})();
