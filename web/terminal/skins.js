/* Skins: one cohesive palette per theme unifying the page UI, conversation reader, and 16-colour terminal screen.
   Single source of truth for embedded web pages and Android (tools/sync_palettes.js -> Palettes.java). */
(() => {
  'use strict';
  const pondModule = new URL('../themes/pond.js', document.currentScript.src).href;
  let pondLoading, pond;
  const SKINS = {
    night: {
      name: '夜航',
      bg: '#1a1b26', panel: '#222436', raised: '#2c3048', sub: '#151621',
      border: '#2e334e', divider: '#282c44', line: '#323756', muted: '#8892b8',
      accent: '#7aa2f7', onAccent: '#10121c',
      accentSoft: '#232c4a', accentInk: '#9ab8ff',
      busySoft: '#352b1f', busyInk: '#f0c47a', busyDot: '#e0af68',
      goodSoft: '#1f3327', goodInk: '#a8da76', goodDot: '#9ece6a',
      badSoft: '#3b212c',
      chat: {
        bg: '#171823', userBg: '#232d4b', userBorder: '#354574', userInk: '#e0e7ff',
        codeBg: '#12131c', codeBorder: '#2a2e46', stepBg: '#1e2030', stepBorder: '#2d324c',
        composerBg: '#212334', composerBorder: '#343a59', thoughtBorder: '#434d75'
      },
      term: { dock: '#1a1b26', keyBg: '#26293d', keyBorder: '#363b57', composerBg: '#222436' },
      t: {
        background: '#161722', foreground: '#c0caf5', cursor: '#7aa2f7', cursorAccent: '#161722', selectionBackground: '#33467c',
        black: '#15161e', red: '#f7768e', green: '#9ece6a', yellow: '#e0af68', blue: '#7aa2f7', magenta: '#bb9af7', cyan: '#7dcfff', white: '#a9b1d6',
        brightBlack: '#414868', brightRed: '#ff899d', brightGreen: '#9fe044', brightYellow: '#faba4a', brightBlue: '#8db0ff', brightMagenta: '#c7a9ff', brightCyan: '#a4daff', brightWhite: '#c0caf5'
      }
    },
    slate: {
      name: '墨岩',
      bg: '#17191e', panel: '#1f232a', raised: '#292e38', sub: '#13151a',
      border: '#2d333e', divider: '#262b35', line: '#333a47', muted: '#8891a0',
      accent: '#61afef', onAccent: '#0f1216',
      accentSoft: '#1d2d3e', accentInk: '#7ec0f7',
      busySoft: '#332b1e', busyInk: '#ebd093', busyDot: '#e5c07b',
      goodSoft: '#1e3024', goodInk: '#a6d087', goodDot: '#98c379',
      badSoft: '#382125',
      chat: {
        bg: '#15171c', userBg: '#1e2c3b', userBorder: '#2f455e', userInk: '#e3ecf7',
        codeBg: '#101216', codeBorder: '#292e38', stepBg: '#1d2027', stepBorder: '#2d333e',
        composerBg: '#1f232a', composerBorder: '#343b48', thoughtBorder: '#444d5e'
      },
      term: { dock: '#181a20', keyBg: '#262b34', keyBorder: '#353c48', composerBg: '#1f232a' },
      t: {
        background: '#13151a', foreground: '#d7dae0', cursor: '#61afef', cursorAccent: '#13151a', selectionBackground: '#3e4451',
        black: '#1e2127', red: '#e06c75', green: '#98c379', yellow: '#e5c07b', blue: '#61afef', magenta: '#c678dd', cyan: '#56b6c2', white: '#abb2bf',
        brightBlack: '#5c6370', brightRed: '#ef7b85', brightGreen: '#a9d48a', brightYellow: '#f0cd8b', brightBlue: '#74bdf7', brightMagenta: '#d48be8', brightCyan: '#66c6d2', brightWhite: '#e6e9ef'
      }
    },
    pine: {
      name: '松林',
      bg: '#1e2427', panel: '#262e32', raised: '#313b40', sub: '#191f22',
      border: '#354045', divider: '#2d373c', line: '#3b474d', muted: '#8e9d92',
      accent: '#a7c080', onAccent: '#182019',
      accentSoft: '#293529', accentInk: '#bdd497',
      busySoft: '#363022', busyInk: '#e4c88d', busyDot: '#dbbc7f',
      goodSoft: '#273528', goodInk: '#b7cf91', goodDot: '#a7c080',
      badSoft: '#3b2628',
      chat: {
        bg: '#1b2023', userBg: '#29342a', userBorder: '#3e513e', userInk: '#e6ebd8',
        codeBg: '#161b1d', codeBorder: '#313c41', stepBg: '#232b2f', stepBorder: '#344045',
        composerBg: '#262e32', composerBorder: '#3a464c', thoughtBorder: '#4f5e56'
      },
      term: { dock: '#1f2528', keyBg: '#2d363b', keyBorder: '#3c484e', composerBg: '#262e32' },
      t: {
        background: '#191e21', foreground: '#d3c6aa', cursor: '#a7c080', cursorAccent: '#191e21', selectionBackground: '#3d484d',
        black: '#272e33', red: '#e67e80', green: '#a7c080', yellow: '#dbbc7f', blue: '#7fbbb3', magenta: '#d699b6', cyan: '#83c092', white: '#d3c6aa',
        brightBlack: '#7a8478', brightRed: '#f0898b', brightGreen: '#b5cf8d', brightYellow: '#e6c88c', brightBlue: '#8dc9c1', brightMagenta: '#e2a6c3', brightCyan: '#90cf9f', brightWhite: '#e6dcc4'
      }
    },
    dusk: {
      name: '暮紫',
      bg: '#1e1e2e', panel: '#262638', raised: '#313247', sub: '#181825',
      border: '#34354c', divider: '#2c2d42', line: '#3a3b54', muted: '#969ab5',
      accent: '#cba6f7', onAccent: '#181524',
      accentSoft: '#2f2744', accentInk: '#d9bdf9',
      busySoft: '#362f23', busyInk: '#fbe6b7', busyDot: '#f9e2af',
      goodSoft: '#21332a', goodInk: '#b6eab1', goodDot: '#a6e3a1',
      badSoft: '#3b222e',
      chat: {
        bg: '#1a1a28', userBg: '#2e2645', userBorder: '#47396b', userInk: '#ece4ff',
        codeBg: '#14141f', codeBorder: '#313248', stepBg: '#232334', stepBorder: '#34354c',
        composerBg: '#262638', composerBorder: '#3d3e59', thoughtBorder: '#534c70'
      },
      term: { dock: '#1e1e2e', keyBg: '#2d2e42', keyBorder: '#3d3f58', composerBg: '#262638' },
      t: {
        background: '#181825', foreground: '#cdd6f4', cursor: '#f5e0dc', cursorAccent: '#181825', selectionBackground: '#45475a',
        black: '#181825', red: '#f38ba8', green: '#a6e3a1', yellow: '#f9e2af', blue: '#89b4fa', magenta: '#f5c2e7', cyan: '#94e2d5', white: '#bac2de',
        brightBlack: '#585b70', brightRed: '#f5a0b8', brightGreen: '#b5eab1', brightYellow: '#fbe9c0', brightBlue: '#9cc1fb', brightMagenta: '#f8d0ed', brightCyan: '#a6e9de', brightWhite: '#cdd6f4'
      }
    },
    pond: {
      name: '锦鲤池塘',
      bg: '#10191b', panel: '#192628', raised: '#243537', sub: '#0d1517',
      border: '#2e4244', divider: '#253739', line: '#374d4f', muted: '#a4b7b6',
      accent: '#edbb83', onAccent: '#10191b',
      accentSoft: '#2e2b22', accentInk: '#f4cc9d',
      busySoft: '#312c1f', busyInk: '#eed099', busyDot: '#e7c58b',
      goodSoft: '#1d3028', goodInk: '#b6d5a9', goodDot: '#a8c99a',
      badSoft: '#382324',
      chat: {
        bg: '#10191b', userBg: '#243334', userBorder: '#3e5352', userInk: '#eef6f3',
        codeBg: '#0c1416', codeBorder: '#2a3d3f', stepBg: '#182527', stepBorder: '#2d4143',
        composerBg: '#192628', composerBorder: '#364c4e', thoughtBorder: '#4c6665'
      },
      term: { dock: '#121d1f', keyBg: '#213133', keyBorder: '#34494b', composerBg: '#192628' },
      t: {
        background: '#10191b', foreground: '#e2ece9', cursor: '#edbb83', cursorAccent: '#10191b', selectionBackground: '#415957',
        black: '#172426', red: '#f39188', green: '#a8c99a', yellow: '#e7c58b', blue: '#9cbfd9', magenta: '#ceafd1', cyan: '#97ceca', white: '#c2d3cf',
        brightBlack: '#94aaa7', brightRed: '#ffada4', brightGreen: '#bfdfb2', brightYellow: '#f4dba8', brightBlue: '#b7d7ed', brightMagenta: '#e0c7e3', brightCyan: '#b3e4df', brightWhite: '#eff7f4'
      }
    },
    // Light skins: each carries its own canvas, paper, chat surface and semantic tints without cross-bleeding.
    paper: {
      name: '纸白', light: true,
      bg: '#f2f3f7', panel: '#ffffff', raised: '#eef0f5', sub: '#f6f7fa',
      border: '#e5e8ef', divider: '#eceef3', line: '#dfe3eb', muted: '#5b6270',
      accent: '#2f54eb', onAccent: '#ffffff',
      accentSoft: '#edf1ff', accentInk: '#2445c8',
      busySoft: '#fff6e8', busyInk: '#8a4b00', busyDot: '#ea8c00',
      goodSoft: '#e4f7eb', goodInk: '#15763a', goodDot: '#16a34a',
      badSoft: '#fdedec',
      chat: {
        bg: '#ffffff', userBg: '#eef2ff', userBorder: '#d7e0ff', userInk: '#141e3c',
        codeBg: '#f6f7fa', codeBorder: '#e5e8ef', stepBg: '#fafbfe', stepBorder: '#e6e9f2',
        composerBg: '#f4f6fa', composerBorder: '#dfe3ee', thoughtBorder: '#cfd6e6'
      },
      term: { dock: '#f2f3f7', keyBg: '#ffffff', keyBorder: '#dfe3eb', composerBg: '#ffffff' },
      t: {
        background: '#fbfbfc', foreground: '#0e1116', cursor: '#2f54eb', cursorAccent: '#fbfbfc', selectionBackground: '#d9e1ff',
        black: '#0e1116', red: '#c0261a', green: '#15803d', yellow: '#9a5b00', blue: '#2445c8', magenta: '#7c3aed', cyan: '#0b7a96', white: '#8b909c',
        brightBlack: '#5b6270', brightRed: '#dc2626', brightGreen: '#16a34a', brightYellow: '#d97706', brightBlue: '#2f54eb', brightMagenta: '#9333ea', brightCyan: '#0891b2', brightWhite: '#0e1116'
      }
    },
    dawn: {
      name: '晨光', light: true,
      bg: '#f3ede3', panel: '#fcf8f2', raised: '#eae1d3', sub: '#f6f0e7',
      border: '#e2d7c7', divider: '#ebe2d4', line: '#d9ccba', muted: '#6f667a',
      accent: '#286983', onAccent: '#ffffff',
      accentSoft: '#e4f0f2', accentInk: '#1d566d',
      busySoft: '#fbf0dc', busyInk: '#8f590e', busyDot: '#c98218',
      goodSoft: '#e4f1e8', goodInk: '#2b6b4d', goodDot: '#3b7d5c',
      badSoft: '#fae8ec',
      chat: {
        bg: '#fbf7f0', userBg: '#e5f0f1', userBorder: '#c6dde1', userInk: '#1f3946',
        codeBg: '#f3ece1', codeBorder: '#dfd3c1', stepBg: '#fefbf6', stepBorder: '#e4d9c9',
        composerBg: '#f3ece1', composerBorder: '#dacdb9', thoughtBorder: '#cfc2b0'
      },
      term: { dock: '#f1e9dd', keyBg: '#fcf8f2', keyBorder: '#ddd0bd', composerBg: '#fcf8f2' },
      t: {
        background: '#faf4eb', foreground: '#3e3956', cursor: '#286983', cursorAccent: '#faf4eb', selectionBackground: '#d7e4e8',
        black: '#3e3956', red: '#b44864', green: '#357455', yellow: '#a86914', blue: '#286983', magenta: '#7d6299', cyan: '#367d88', white: '#9893a5',
        brightBlack: '#6c657c', brightRed: '#c25773', brightGreen: '#438463', brightYellow: '#be7b1b', brightBlue: '#357793', brightMagenta: '#8d73a8', brightCyan: '#498f9a', brightWhite: '#3e3956'
      }
    }
  };
  window.RemoteCliSkins = SKINS;
  /* Claude Code colours its code with fixed colours made for a dark screen: white for the code itself, pale yellow
     for strings. On a light skin they vanished. The terminal darkens or lightens any letter that would not stand
     out from what is behind it, whatever colour the program asked for. */
  window.RemoteCliLook = { minimumContrastRatio: 4.5 };
  const prefersDark = () => !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
  window.RemoteCliResolveSkin = name => {
    if (name === 'auto') return prefersDark() ? 'night' : 'paper';
    return SKINS[name] ? name : 'paper';
  };
  let requestedSkin = 'paper', systemWatcher = null;
  const systemListeners = new Set();
  window.RemoteCliOnSystemTheme = fn => {
    if (typeof fn !== 'function') return () => {};
    systemListeners.add(fn);
    if (!systemWatcher && window.matchMedia) {
      systemWatcher = window.matchMedia('(prefers-color-scheme: dark)');
      const notify = () => { if (requestedSkin === 'auto') systemListeners.forEach(cb => { try { cb(); } catch (error) { /* ignore */ } }); };
      if (systemWatcher.addEventListener) systemWatcher.addEventListener('change', notify);
      else if (systemWatcher.addListener) systemWatcher.addListener(notify);
    }
    return () => systemListeners.delete(fn);
  };
  /* Sets the page, conversation and terminal CSS tokens of a skin and returns it; 'auto' follows the system appearance. */
  window.RemoteCliPaint = name => {
    requestedSkin = name || 'paper';
    const resolved = window.RemoteCliResolveSkin(requestedSkin);
    const s = SKINS[resolved] || SKINS.paper, style = document.documentElement.style;
    const bg = s.bg || s.t.background;
    const chat = s.chat || {};
    const term = s.term || {};
    const vars = {
      bg, panel: s.panel, raised: s.raised, line: s.line, ink: s.t.foreground, muted: s.muted,
      accent: s.accent, 'on-accent': s.onAccent,
      good: s.t.green, busy: s.t.yellow, bad: s.t.red,
      'warn-ink': s.busyInk || s.t.yellow, 'warn-bg': s.busySoft || s.raised,
      'ink-solid': s.accent, 'on-ink-solid': s.onAccent,
      'hero-bg': s.light ? 'color-mix(in srgb, ' + s.accent + ' 6%, ' + s.panel + ')' : 'color-mix(in srgb, ' + s.accent + ' 7%, ' + s.panel + ')',
      'hero-border': s.light ? 'color-mix(in srgb, ' + s.accent + ' 18%, ' + s.border + ')' : 'color-mix(in srgb, ' + s.accent + ' 22%, ' + s.border + ')',
      'hero-fg': s.t.foreground, 'hero-muted': s.muted,
      'surface-sub': s.sub, 'card-border': s.border, divider: s.divider,
      'accent-soft': s.accentSoft, 'accent-ink': s.accentInk,
      'busy-soft': s.busySoft, 'busy-ink': s.busyInk, 'busy-dot': s.busyDot,
      'good-soft': s.goodSoft, 'good-ink': s.goodInk, 'good-dot': s.goodDot,
      'bad-soft': s.badSoft,
      // Conversation theme tokens
      'reader-bg': chat.bg || (s.light ? s.panel : bg),
      'chat-bg': chat.bg || (s.light ? s.panel : bg),
      'chat-user-bg': chat.userBg || s.accentSoft,
      'chat-user-border': chat.userBorder || s.border,
      'chat-user-ink': chat.userInk || s.t.foreground,
      'chat-code-bg': chat.codeBg || s.sub,
      'chat-code-border': chat.codeBorder || s.border,
      'chat-step-bg': chat.stepBg || s.panel,
      'chat-step-border': chat.stepBorder || s.border,
      'chat-composer-bg': chat.composerBg || s.sub,
      'chat-composer-border': chat.composerBorder || s.border,
      'chat-thought-border': chat.thoughtBorder || s.line,
      // Terminal theme tokens
      'term-bg': s.t.background,
      'term-dock': term.dock || bg,
      'term-key-bg': term.keyBg || (s.light ? s.panel : s.raised),
      'term-key-border': term.keyBorder || s.border,
      'term-composer-bg': term.composerBg || s.panel,
      elevate: s.light
        ? (resolved === 'dawn'
          ? '0 1px 2px rgba(62, 57, 86, .05), 0 8px 24px rgba(62, 57, 86, .06)'
          : '0 1px 2px rgba(14, 17, 22, .04), 0 8px 24px rgba(14, 17, 22, .05)')
        : '0 1px 2px rgb(0 0 0 / .26), 0 8px 24px rgb(0 0 0 / .16)',
      rim: s.light
        ? (resolved === 'dawn' ? 'rgba(62, 57, 86, .09)' : 'rgba(14, 17, 22, .07)')
        : 'rgba(255, 255, 255, .08)'
    };
    Object.keys(vars).forEach(key => style.setProperty('--' + key, vars[key]));
    style.colorScheme = s.light ? 'light' : 'dark';
    document.documentElement.dataset.skin = resolved;
    document.documentElement.dataset.theme = s.light ? 'light' : 'dark';
    if (pond) pond.setEnabled(resolved === 'pond');
    else if (resolved === 'pond' && !pondLoading) {
      pondLoading = import(pondModule).then(({ PondTheme }) => {
        pond = new PondTheme();
        pond.setEnabled(document.documentElement.dataset.skin === 'pond');
      }).catch(error => {
        // Decoration must never prevent a terminal or conversation from opening.
        pondLoading = null;
        console.warn('池塘背景未能加载，已保留静态配色。', error);
      });
    }
    return s;
  };
})();
