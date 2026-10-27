# Third-party components

| Component | Version | License | Where |
| --- | --- | --- | --- |
| [ascii-pond](https://github.com/stqfdyr/ascii-pond) | `822a3c95167f79f0905720a2e6793cb4a64eda55` | MIT, Copyright (c) 2026 stqfdyr | `web/themes/vendor/ascii-pond/` (unmodified simulation and renderer; license included) |
| xterm.js (`@xterm/xterm`) | 5.5.0 | MIT | `web/terminal/vendor/xterm.js`, `xterm.css` |
| `@xterm/addon-fit` | 0.10.0 | MIT | `web/terminal/vendor/addon-fit.js` |
| `@xterm/addon-webgl` | 0.18.0 | MIT | `web/terminal/vendor/addon-webgl.js` |
| JetBrains Mono (`@fontsource/jetbrains-mono`) | 5.2.5 | OFL-1.1 | `web/terminal/vendor/jetbrains-mono-*.woff2` |
| highlight.js (`@highlightjs/cdn-assets`) | 11.10.0 | BSD-3-Clause | `web/files/vendor/highlight.min.js` (common languages plus PowerShell, Dockerfile, LaTeX, Julia, Stata, SAS, MATLAB) |
| marked | 12.0.2 | MIT | `web/files/vendor/marked.min.js` |
| DOMPurify | 3.1.6 | MPL-2.0 OR Apache-2.0 | `web/files/vendor/purify.min.js` |
| docx-preview | 0.4.1 | Apache-2.0 | `web/files/vendor/docx-preview.min.js` |
| JSZip | 3.10.2 | MIT OR GPL-3.0-or-later（按 MIT 使用） | `web/files/vendor/jszip.min.js` |
| pptx-preview | 1.0.7 | ISC（其 package.json 的声明；内含 echarts、lodash、jszip、uuid、tslib） | `web/files/vendor/pptx-preview.umd.js` |
| SheetJS Community Edition (`xlsx`) | 0.20.3 | Apache-2.0 | `web/files/vendor/xlsx.full.min.js` |
| PDF.js (`pdfjs-dist`) | 3.11.174 | Apache-2.0 | `web/files/vendor/pdf.min.js`, `pdf.worker.min.js`, `cmaps/` |
| ZXing core | 3.5.3 | Apache-2.0 | inside the Android app only; reads the QR code |
| Python embeddable package | 3.12.10 | PSF-2.0 | inside the Windows installer only (`python/`) |

The license texts of the terminal's four and of the six file viewers are next to the files;
`web/terminal/vendor/sources.json` and `web/files/vendor/sources.json` record where each file came from and its
integrity hash. The Windows installer includes an unmodified subset of the official
embeddable Python from python.org (files the relay never loads are left out); its license is at
https://docs.python.org/3.12/license.html.

`cloudflared` (Apache-2.0) is not included. The Windows program downloads it from Cloudflare's GitHub releases
only when you choose the public-tunnel mode and confirm.

Claude Code and Codex are separate products of Anthropic and OpenAI. This project starts whichever of them is
installed on your computer; it does not include or modify them.
