/* Skins: the sixteen terminal colours and the colours of the pages around them come from one palette.
   Used by the list page and the terminal page. */
(() => {
  'use strict';
  const pondModule = new URL('../themes/pond.js', document.currentScript.src).href;
  let pondLoading, pond;
  const SKINS = {
    night: { name: '夜航', panel: '#222436', raised: '#2c2f47', line: '#2f334d', muted: '#8189ad', accent: '#7aa2f7', onAccent: '#10121c',
      t: { background: '#1a1b26', foreground: '#c0caf5', cursor: '#c0caf5', selectionBackground: '#33467c', black: '#15161e', red: '#f7768e', green: '#9ece6a', yellow: '#e0af68', blue: '#7aa2f7', magenta: '#bb9af7', cyan: '#7dcfff', white: '#a9b1d6',
        brightBlack: '#414868', brightRed: '#ff899d', brightGreen: '#9fe044', brightYellow: '#faba4a', brightBlue: '#8db0ff', brightMagenta: '#c7a9ff', brightCyan: '#a4daff', brightWhite: '#c0caf5' } },
    slate: { name: '墨岩', panel: '#1e2127', raised: '#2a2e37', line: '#2c313a', muted: '#7f8795', accent: '#61afef', onAccent: '#0f1114',
      t: { background: '#16181d', foreground: '#d7dae0', cursor: '#d7dae0', selectionBackground: '#3e4451', black: '#1e2127', red: '#e06c75', green: '#98c379', yellow: '#e5c07b', blue: '#61afef', magenta: '#c678dd', cyan: '#56b6c2', white: '#abb2bf',
        brightBlack: '#5c6370', brightRed: '#ef7b85', brightGreen: '#a9d48a', brightYellow: '#f0cd8b', brightBlue: '#74bdf7', brightMagenta: '#d48be8', brightCyan: '#66c6d2', brightWhite: '#e6e9ef' } },
    pine: { name: '松林', panel: '#272e33', raised: '#323c41', line: '#343f44', muted: '#859289', accent: '#a7c080', onAccent: '#1a2023',
      t: { background: '#1e2326', foreground: '#d3c6aa', cursor: '#d3c6aa', selectionBackground: '#3d484d', black: '#272e33', red: '#e67e80', green: '#a7c080', yellow: '#dbbc7f', blue: '#7fbbb3', magenta: '#d699b6', cyan: '#83c092', white: '#d3c6aa',
        brightBlack: '#7a8478', brightRed: '#f0898b', brightGreen: '#b5cf8d', brightYellow: '#e6c88c', brightBlue: '#8dc9c1', brightMagenta: '#e2a6c3', brightCyan: '#90cf9f', brightWhite: '#e6dcc4' } },
    dusk: { name: '暮紫', panel: '#262637', raised: '#313244', line: '#313244', muted: '#8c8fa8', accent: '#cba6f7', onAccent: '#181825',
      t: { background: '#1e1e2e', foreground: '#cdd6f4', cursor: '#f5e0dc', selectionBackground: '#45475a', black: '#181825', red: '#f38ba8', green: '#a6e3a1', yellow: '#f9e2af', blue: '#89b4fa', magenta: '#f5c2e7', cyan: '#94e2d5', white: '#bac2de',
        brightBlack: '#585b70', brightRed: '#f5a0b8', brightGreen: '#b5eab1', brightYellow: '#fbe9c0', brightBlue: '#9cc1fb', brightMagenta: '#f8d0ed', brightCyan: '#a6e9de', brightWhite: '#cdd6f4' } },
    pond: { name: '锦鲤池塘', panel: '#1b292b', raised: '#27383a', line: '#3b5052', muted: '#a4b7b6', accent: '#edbb83', onAccent: '#10191b',
      t: { background: '#10191b', foreground: '#e2ece9', cursor: '#edbb83', selectionBackground: '#415957', black: '#172426', red: '#f39188', green: '#a8c99a', yellow: '#e7c58b', blue: '#9cbfd9', magenta: '#ceafd1', cyan: '#97ceca', white: '#c2d3cf',
        brightBlack: '#94aaa7', brightRed: '#ffada4', brightGreen: '#bfdfb2', brightYellow: '#f4dba8', brightBlue: '#b7d7ed', brightMagenta: '#e0c7e3', brightCyan: '#b3e4df', brightWhite: '#eff7f4' } },
    // Light skins. The text colours are dark enough to read on the pale background in daylight.
    paper: { name: '纸白', light: true, panel: '#f0f1f4', raised: '#e4e6eb', line: '#d5d8df', muted: '#666b78', accent: '#2f6fe4', onAccent: '#ffffff',
      t: { background: '#fbfbfc', foreground: '#2b2f3a', cursor: '#2f6fe4', cursorAccent: '#fbfbfc', selectionBackground: '#cfdcf7', black: '#2b2f3a', red: '#c93c37', green: '#3d8a3a', yellow: '#a36a00', blue: '#2f6fe4', magenta: '#9a2fa0', cyan: '#0b7a96', white: '#8b909c',
        brightBlack: '#5c6170', brightRed: '#d9534e', brightGreen: '#4c9a48', brightYellow: '#b87a05', brightBlue: '#4a84ee', brightMagenta: '#ab45b0', brightCyan: '#168aa6', brightWhite: '#2b2f3a' } },
    dawn: { name: '晨光', light: true, panel: '#f4ede4', raised: '#ebe1d5', line: '#ddd3c6', muted: '#7a7089', accent: '#286983', onAccent: '#ffffff',
      t: { background: '#faf4ed', foreground: '#4a4566', cursor: '#286983', cursorAccent: '#faf4ed', selectionBackground: '#dfd6e6', black: '#4a4566', red: '#b4506a', green: '#3b7d5c', yellow: '#b9781a', blue: '#286983', magenta: '#80689c', cyan: '#3f8590', white: '#9893a5',
        brightBlack: '#6e6a86', brightRed: '#c2607a', brightGreen: '#4a8d6b', brightYellow: '#c98820', brightBlue: '#357793', brightMagenta: '#907aa9', brightCyan: '#56949f', brightWhite: '#4a4566' } }
  };
  window.RemoteCliSkins = SKINS;
  /* Claude Code colours its code with fixed colours made for a dark screen: white for the code itself, pale yellow
     for strings. On a light skin they vanished. The terminal darkens or lightens any letter that would not stand
     out from what is behind it, whatever colour the program asked for. */
  window.RemoteCliLook = { minimumContrastRatio: 4.5 };
  /* Sets the page colours of a skin and returns it; an unknown name gives the default, the light paper skin. */
  window.RemoteCliPaint = name => {
    const s = SKINS[name] || SKINS.paper, style = document.documentElement.style;
    const vars = { bg: s.t.background, panel: s.panel, raised: s.raised, line: s.line, ink: s.t.foreground, muted: s.muted, accent: s.accent, 'on-accent': s.onAccent,
      good: s.t.green, busy: s.t.yellow, bad: s.t.red, 'warn-ink': s.t.yellow, 'warn-bg': s.raised };
    Object.keys(vars).forEach(key => style.setProperty('--' + key, vars[key]));
    style.colorScheme = s.light ? 'light' : 'dark';
    document.documentElement.dataset.skin = SKINS[name] ? name : 'paper';
    if (pond) pond.setEnabled(name === 'pond');
    else if (name === 'pond' && !pondLoading) {
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
