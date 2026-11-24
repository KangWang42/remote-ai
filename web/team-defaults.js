/* Shared collaboration model & reasoning-effort defaults (used by teams.js, computer/computer.js, and me.js).
   Recommended defaults prefer Opus 5.5 (Claude), Astra 6.2 (Codex), Neon (Antigravity CLI), and `high` effort
   whenever the computer's real CLI model catalog offers them; custom choices saved in Settings override them. */
(() => {
  'use strict';
  const STORAGE_KEY = 'rcli-team-defaults';
  const TOOLS_ORDER = ['claude', 'codex', 'agy'];
  const TOOL_NAMES = { claude: 'Claude Code', codex: 'Codex', agy: 'Antigravity CLI' };
  const EFFORT_LABELS = {
    none: '无（none）',
    minimal: '极低（minimal）',
    low: '低（low）',
    medium: '中（medium）',
    high: '高（high）',
    xhigh: '极高（xhigh）',
    max: '最高（max）',
    ultra: '极致（ultra）'
  };
  const MODE_ORDER = ['full', 'edit', 'read'];
  const MODE_LABELS = { '': '跟随电脑设置', full: '默认', edit: '自动改文件', read: '只读' };
  const MODE_SHORT = { '': '默认权限', full: '默认', edit: '自动改文件', read: '只读' };
  const MODE_HINTS = {
    '': '沿用电脑端设置中的默认权限模式',
    full: '按工具原本方式运行，高风险操作前会向你确认',
    edit: '自动接受文件修改，执行命令等仍按工具规则确认',
    read: '仅允许读取与分析，不修改文件也不执行改动操作'
  };
  const MODE_RANK = { read: 0, edit: 1, full: 2 };
  const RECOMMENDED_HINTS = {
    claude: '推荐 Opus 5.5 · 思考高',
    codex: '推荐 Astra 6.2 · 思考高',
    agy: '推荐 Neon · 思考高'
  };

  function normalizeModels(raw) {
    const out = {};
    if (!raw || typeof raw !== 'object') return out;
    Object.keys(raw).forEach(tool => {
      if (!Array.isArray(raw[tool])) return;
      out[tool] = raw[tool]
        .filter(item => item && typeof item.id === 'string' && item.id.trim())
        .map(item => ({
          id: item.id.trim(),
          label: String(item.label || item.id).trim(),
          efforts: Array.isArray(item.efforts)
            ? [...new Set(item.efforts.map(e => String(e || '').trim().toLowerCase()).filter(Boolean))]
            : [],
          defaultEffort: String(item.default_effort || '').trim().toLowerCase(),
          isDefault: item.is_default === true
        }));
    });
    return out;
  }

  function readPrefs() {
    try {
      const raw = JSON.parse(localStorage.getItem(STORAGE_KEY) || 'null');
      if (!raw || typeof raw !== 'object') return { models: {}, efforts: {}, modes: {} };
      const models = raw.models && typeof raw.models === 'object' ? Object.assign({}, raw.models) : {};
      const efforts = raw.efforts && typeof raw.efforts === 'object' ? Object.assign({}, raw.efforts) : {};
      const modes = raw.modes && typeof raw.modes === 'object' ? Object.assign({}, raw.modes) : {};
      return { models, efforts, modes };
    } catch {
      return { models: {}, efforts: {}, modes: {} };
    }
  }

  function writePrefs(prefs) {
    try {
      const clean = { models: {}, efforts: {}, modes: {} };
      if (prefs && prefs.models && typeof prefs.models === 'object') {
        Object.keys(prefs.models).forEach(k => {
          if (typeof prefs.models[k] === 'string') clean.models[k] = prefs.models[k].trim();
        });
      }
      if (prefs && prefs.efforts && typeof prefs.efforts === 'object') {
        Object.keys(prefs.efforts).forEach(k => {
          if (typeof prefs.efforts[k] === 'string') clean.efforts[k] = prefs.efforts[k].trim().toLowerCase();
        });
      }
      if (prefs && prefs.modes && typeof prefs.modes === 'object') {
        Object.keys(prefs.modes).forEach(k => {
          const val = typeof prefs.modes[k] === 'string' ? prefs.modes[k].trim().toLowerCase() : '';
          if (val === '' || MODE_ORDER.includes(val)) clean.modes[k] = val;
        });
      }
      if (!Object.keys(clean.models).length && !Object.keys(clean.efforts).length && !Object.keys(clean.modes).length) {
        localStorage.removeItem(STORAGE_KEY);
      } else {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(clean));
      }
    } catch { /* private window or full quota */ }
  }

  function setModelPref(tool, modelId) {
    const prefs = readPrefs();
    prefs.models[tool] = String(modelId || '').trim();
    writePrefs(prefs);
    return prefs;
  }

  function setEffortPref(tool, effort) {
    const prefs = readPrefs();
    prefs.efforts[tool] = String(effort || '').trim().toLowerCase();
    writePrefs(prefs);
    return prefs;
  }

  function setModePref(tool, mode) {
    const prefs = readPrefs();
    const clean = String(mode || '').trim().toLowerCase();
    prefs.modes[tool] = MODE_ORDER.includes(clean) ? clean : '';
    writePrefs(prefs);
    return prefs;
  }

  function resetPrefs() {
    try { localStorage.removeItem(STORAGE_KEY); } catch { }
    return { models: {}, efforts: {}, modes: {} };
  }

  function hasCustomPrefs(prefs) {
    const p = prefs || readPrefs();
    return Object.keys(p.models || {}).length > 0 || Object.keys(p.efforts || {}).length > 0 || Object.keys(p.modes || {}).length > 0;
  }

  function clampMode(requested, maxMode) {
    const clean = String(requested || '').trim().toLowerCase();
    if (!MODE_ORDER.includes(clean)) return '';
    const cap = String(maxMode || '').trim().toLowerCase();
    if (!MODE_ORDER.includes(cap)) return clean;
    return MODE_RANK[clean] <= MODE_RANK[cap] ? clean : cap;
  }

  function modeAllowed(mode, maxMode) {
    const clean = String(mode || '').trim().toLowerCase();
    const cap = String(maxMode || '').trim().toLowerCase();
    if (!clean || !MODE_ORDER.includes(cap)) return true;
    return MODE_RANK[clean] <= MODE_RANK[cap];
  }

  function savedParams(tool, maxMode, prefs) {
    const p = prefs || readPrefs();
    const out = {};
    const m = p.models && typeof p.models[tool] === 'string' ? p.models[tool].trim() : '';
    const e = p.efforts && typeof p.efforts[tool] === 'string' ? p.efforts[tool].trim().toLowerCase() : '';
    const md = p.modes && typeof p.modes[tool] === 'string' ? clampMode(p.modes[tool], maxMode) : '';
    if (m) out.model = m;
    if (e) out.effort = e;
    if (md && tool !== 'agy') out.mode = md;
    return out;
  }

  function modeLabel(mode, shortForm) {
    const clean = String(mode || '').trim().toLowerCase();
    return (shortForm ? MODE_SHORT : MODE_LABELS)[clean] || clean || (shortForm ? '默认权限' : '跟随电脑设置');
  }

  function matchItem(list, patterns) {
    for (const re of patterns) {
      const found = list.find(item => re.test(item.id) || re.test(item.label));
      if (found) return found.id;
    }
    return '';
  }

  function recommendedModel(tool, catalog) {
    const list = (catalog && catalog[tool]) || [];
    if (!list.length) return '';
    if (tool === 'claude') {
      return matchItem(list, [
        /opus.*5[._ -]?5.*\bhigh\b/i,
        /opus.*5[._ -]?5|5[._ -]?5.*opus/i,
        /\bopus\b/i
      ]);
    }
    if (tool === 'codex') {
      return matchItem(list, [
        /astra.*6[._ -]?2|6[._ -]?2.*astra/i,
        /\bastra\b/i
      ]);
    }
    if (tool === 'agy') {
      return matchItem(list, [
        /^gemini(?:(?!orchestrator).)*\bneon\b.*\bhigh\b/i,
        /^gemini(?:(?!orchestrator).)*\bneon\b/i,
        /\bneon\b.*\bhigh\b/i,
        /\bneon\b/i
      ]);
    }
    return '';
  }

  function modelEntry(tool, modelId, catalog) {
    const list = (catalog && catalog[tool]) || [];
    const clean = String(modelId || '').trim();
    if (clean) return list.find(item => item.id === clean) || null;
    return list.find(item => item.isDefault) || null;
  }

  function effortListFor(tool, modelId, catalog) {
    const entry = modelEntry(tool, modelId, catalog);
    return entry && Array.isArray(entry.efforts) ? entry.efforts : [];
  }

  function recommendedEffort(tool, modelId, catalog) {
    const efforts = effortListFor(tool, modelId, catalog);
    return efforts.includes('high') ? 'high' : '';
  }

  function resolveModel(tool, catalog, prefs) {
    const p = prefs || readPrefs();
    const list = catalog ? (catalog[tool] || []) : null;
    if (p.models && Object.prototype.hasOwnProperty.call(p.models, tool)) {
      const saved = String(p.models[tool] || '').trim();
      if (!saved) return '';
      if (!list) return saved;
      if (list.some(item => item.id === saved)) return saved;
    }
    return list ? recommendedModel(tool, catalog) : '';
  }

  function resolveEffort(tool, modelId, catalog, prefs) {
    const p = prefs || readPrefs();
    const hasCat = !!catalog;
    const efforts = hasCat ? effortListFor(tool, modelId, catalog) : null;
    if (p.efforts && Object.prototype.hasOwnProperty.call(p.efforts, tool)) {
      const saved = String(p.efforts[tool] || '').trim().toLowerCase();
      if (!saved) return '';
      if (!hasCat) return saved;
      if (efforts.includes(saved)) return saved;
    }
    return hasCat ? recommendedEffort(tool, modelId, catalog) : '';
  }

  function modelLabel(tool, id, catalog) {
    const clean = String(id || '').trim();
    if (!clean) return '工具默认';
    const list = (catalog && catalog[tool]) || [];
    const found = list.find(item => item.id === clean);
    return found ? found.label : clean;
  }

  function effortLabel(level) {
    const clean = String(level || '').trim().toLowerCase();
    if (!clean) return '工具默认';
    return EFFORT_LABELS[clean] || clean;
  }

  function shortEffortLabel(level) {
    const clean = String(level || '').trim().toLowerCase();
    if (!clean) return '';
    return (EFFORT_LABELS[clean] || clean).replace(/（.*）/, '');
  }

  // The tag a member carries for its reasoning effort, in the short form: "思考 · 高".
  function effortTag(level) {
    return '思考 · ' + (shortEffortLabel(level) || '默认');
  }

  function summary(tools, catalog, prefs) {
    const p = prefs || readPrefs();
    const list = (Array.isArray(tools) && tools.length ? tools : TOOLS_ORDER).filter(t => TOOLS_ORDER.includes(t));
    if (!list.length) return 'Opus 5.5 · Astra 6.2 · Neon · 思考高';
    if (!catalog) {
      if (!hasCustomPrefs(p)) return 'Opus 5.5 · Astra 6.2 · Neon · 思考高';
      return list.map(tool => {
        const m = p.models && Object.prototype.hasOwnProperty.call(p.models, tool) ? (p.models[tool] || '默认') : '推荐';
        const e = p.efforts && Object.prototype.hasOwnProperty.call(p.efforts, tool) ? shortEffortLabel(p.efforts[tool]) : '高';
        return (TOOL_NAMES[tool] || tool).split(' ')[0] + ' ' + m + (e ? '·' + e : '');
      }).join(' / ');
    }
    return list.map(tool => {
      const m = resolveModel(tool, catalog, p);
      const e = resolveEffort(tool, m, catalog, p);
      const mName = m ? modelLabel(tool, m, catalog) : '默认';
      const eName = shortEffortLabel(e);
      return mName + (eName ? ' · ' + eName : '');
    }).join(' / ');
  }

  window.RemoteTeamDefaults = {
    STORAGE_KEY,
    TOOLS_ORDER,
    TOOL_NAMES,
    EFFORT_LABELS,
    MODE_ORDER,
    MODE_LABELS,
    MODE_SHORT,
    MODE_HINTS,
    RECOMMENDED_HINTS,
    normalizeModels,
    readPrefs,
    writePrefs,
    setModelPref,
    setEffortPref,
    setModePref,
    resetPrefs,
    hasCustomPrefs,
    clampMode,
    modeAllowed,
    savedParams,
    modeLabel,
    recommendedModel,
    recommendedEffort,
    modelEntry,
    effortListFor,
    resolveModel,
    resolveEffort,
    modelLabel,
    effortLabel,
    shortEffortLabel,
    effortTag,
    summary
  };
})();
