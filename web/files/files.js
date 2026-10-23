/* The files of a project: its folders as lists, and a file shown the way its kind is read: code with its colours,
   Markdown and Word as a document, a workbook as tables, slides, a PDF, a picture. The computer reads the files;
   uploads add files to the selected folder without replacing existing files. */
(() => {
  'use strict';
  const K = window.RemoteCliKinds, $ = id => document.getElementById(id), native = window.RemoteCliNative || null;
  const asked = new URLSearchParams(location.search), dir = asked.get('dir') || '';
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  const saved = (key, fallback) => {
    if (native && native.pref && (key === 'skin' || key === 'text')) { const chosen = native.pref(key); if (chosen) return chosen; }
    try { return localStorage.getItem('rcli-' + key) || fallback; } catch (error) { return fallback; }
  };
  const keep = (key, value) => { try { localStorage.setItem('rcli-' + key, value); } catch (error) { /* private window */ } };

  // ---- drawing helpers
  const svg = (path, size) => `<svg viewBox="0 0 24 24" width="${size || 22}" height="${size || 22}" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${path}</svg>`;
  const sheetOfPaper = '<path d="M7 3.5h7l4.5 4.5v11a1.5 1.5 0 0 1-1.5 1.5H7A1.5 1.5 0 0 1 5.5 19V5A1.5 1.5 0 0 1 7 3.5z"/><path d="M14 3.5V8h4.5"/>';
  const ICON = {
    back: svg('<path d="M15 5l-7 7 7 7"/>'), more: svg('<circle cx="5" cy="12" r="1.3" fill="currentColor"/><circle cx="12" cy="12" r="1.3" fill="currentColor"/><circle cx="19" cy="12" r="1.3" fill="currentColor"/>'),
    chev: svg('<path d="M9 6l6 6-6 6"/>', 18),
    folder: svg('<path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2.2h7a2 2 0 0 1 2 2v7.8a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/>'),
    code: svg('<path d="M8.5 8l-4 4 4 4M15.5 8l4 4-4 4M13.2 5.5l-2.4 13"/>'),
    text: svg(sheetOfPaper + '<path d="M8.5 12.5h7M8.5 16h5"/>'),
    markdown: svg('<rect x="3" y="5.5" width="18" height="13" rx="2.5"/><path d="M6.5 15V9.5l2.5 3 2.5-3V15M15.5 9.5V15m-2-2l2 2 2-2"/>'),
    word: svg(sheetOfPaper + '<path d="M8.3 11.5l1.3 5 1.4-4 1.4 4 1.3-5"/>'),
    sheet: svg('<rect x="3.5" y="4.5" width="17" height="15" rx="2.5"/><path d="M3.5 9.5h17M3.5 14.5h17M9.5 4.5v15"/>'),
    slides: svg('<rect x="3.5" y="4.5" width="17" height="11.5" rx="2"/><path d="M12 16v3.5M8.5 19.5h7M8 12V9.5M12 12V8M16 12v-1.5"/>'),
    pdf: svg(sheetOfPaper + '<path d="M8.5 16.5c2-1 3.2-3.6 3.5-6 .3 2.4 1.8 4.3 4 5-2.4-.2-5.2.3-7.5 1z"/>'),
    image: svg('<rect x="3.5" y="4.5" width="17" height="15" rx="2.5"/><circle cx="9" cy="10" r="1.6"/><path d="M4 17l4.5-4.3 3.5 3.3 3-2.5 5 4.5"/>'),
    archive: svg('<rect x="4.5" y="3.5" width="15" height="17" rx="2.5"/><path d="M12 3.500v2m0 2v2m0 2v2M10.5 15.500h3v2.500h-3z"/>'),
    other: svg(sheetOfPaper)
  };
  function el(tag, props, ...children) {
    const node = document.createElement(tag);
    Object.keys(props || {}).forEach(key => {
      if (key === 'html') node.innerHTML = props[key];
      else if (key in node) node[key] = props[key]; else node.setAttribute(key, props[key]);
    });
    node.append(...children.filter(Boolean));
    return node;
  }
  function ago(ms) {
    const s = Math.max(0, (Date.now() - ms) / 1000);
    if (s > 30 * 86400) { const d = new Date(ms); return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0'); }
    return s < 90 ? '刚刚' : s < 3600 ? Math.round(s / 60) + ' 分钟前' : s < 86400 ? Math.round(s / 3600) + ' 小时前' : Math.round(s / 86400) + ' 天前';
  }
  let toastTimer = 0;
  function toast(text) { const box = $('toast'); box.textContent = text; box.hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => { box.hidden = true; }, 3000); }
  function choose(title, choices) {
    const form = $('sheet-form'), sheet = $('sheet');
    form.textContent = '';
    form.append(el('h3', { textContent: title }));
    choices.forEach(c => form.append(el('button', { type: 'button', className: 'choice', onclick: () => { sheet.close(); c.act(); } },
      el('span', null, c.label, c.sub ? el('small', { textContent: c.sub }) : ''), c.on ? el('i', { className: 'tick', textContent: '✓' }) : '')));
    form.append(el('button', { type: 'button', className: 'cancel', textContent: '取消', onclick: () => sheet.close() }));
    sheet.onclick = event => { if (event.target === sheet) sheet.close(); };
    sheet.showModal();
  }

  // ---- look
  const TEXT = { small: '14px', normal: '15.5px', large: '17px', larger: '19px' };
  const skin = window.RemoteCliPaint(saved('skin', 'paper'));
  const root = document.documentElement.style;
  root.fontSize = TEXT[saved('text', 'normal')] || TEXT.normal;
  // The colours of code are the skin's own terminal colours, so a file looks as it does in the terminal beside it.
  ['red', 'green', 'yellow', 'blue', 'magenta', 'cyan'].forEach(name => root.setProperty('--c-' + name, skin.t[name]));
  root.setProperty('--c-faint', skin.muted);
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.content = skin.t.background;
  if (native && native.chrome) native.chrome(skin.t.background);
  $('back').innerHTML = ICON.back; $('more').innerHTML = ICON.more;

  // ---- asking the computer
  let run = 0;        // what is shown now; an answer for something left behind is dropped
  async function ask(action, path, offset, extra) {
    const controller = new AbortController(), timer = setTimeout(() => controller.abort(), 35000);
    let reply, body = {};
    try {
      reply = await fetch('../api/files', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, signal: controller.signal,
        body: JSON.stringify(Object.assign({ id: newId(), action, dir, path, offset: offset || 0 }, extra)) });
      try { body = await reply.json(); } catch (error) { /* not JSON */ }
    } catch (error) { throw new Error(error.name === 'AbortError' ? '传输超时，请检查连接后重试' : '连接中断，请检查网络后重试'); }
    finally { clearTimeout(timer); }
    if (reply.status === 401) { location.replace('../'); throw new Error('请重新登录'); }
    if (!reply.ok) throw new Error(body.error || '请求失败（' + reply.status + '）');
    return body;
  }
  function bytesOf(base64) {
    const raw = atob(base64), bytes = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
    return bytes;
  }
  // A Word file with its formatting (headings, emphasis, colours, tables, pictures, lists), flowing to the width
  // of the screen like any page of text; a PowerPoint file as its slides, each the width of the screen.
  async function wordLooks(bytes) {
    await need('jszip.min.js');           // the second looks for the first when it is loaded
    await need('docx-preview.min.js');
    const box = el('div', { className: 'looks word-looks' });
    await docx.renderAsync(bytes, box, null, { inWrapper: false, ignoreWidth: true, ignoreHeight: true, breakPages: false, useBase64URL: true,
      renderHeaders: false, renderFooters: false, renderFootnotes: true });
    box.querySelectorAll('table').forEach(table => { const wrap = el('div', { className: 'wide' }); table.replaceWith(wrap); wrap.append(table); });
    return box;
  }
  async function slideLooks(bytes) {
    await need('pptx-preview.umd.js');
    const box = el('div', { className: 'looks slide-looks' });
    const width = Math.max(300, Math.min(document.documentElement.clientWidth - 20, 960));       // the margin beside the slides
    const viewer = pptxPreview.init(box, { width, height: Math.round(width * 9 / 16), mode: 'list' });
    await viewer.preview(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength));
    return box;
  }
  /** The whole file, piece by piece. Refuses one larger than `limit`; `first` is a piece that was fetched already. */
  async function read(path, limit, mine, first) {
    let piece = first || await ask('file_read', path, 0);
    if (piece.size > limit) { const error = new Error('large'); error.size = piece.size; throw error; }
    const whole = new Uint8Array(piece.size);
    let at = 0;
    for (;;) {
      const part = bytesOf(piece.data);
      whole.set(part.subarray(0, Math.min(part.length, whole.length - at)), at);
      at += part.length;
      if (piece.end || !part.length || at >= whole.length) break;
      if (mine !== run) throw new Error('left');
      $('progress').hidden = false;
      $('progress-text').textContent = '正在读取 ' + K.size(at) + ' / ' + K.size(piece.size);
      $('progress-bar').style.width = Math.round(100 * at / piece.size) + '%';
      piece = await ask('file_read', path, at);
    }
    $('progress').hidden = true;
    return { bytes: whole.subarray(0, Math.min(at, whole.length)), modified: piece.modified };
  }
  const scripts = {};
  function need(file) {
    if (!scripts[file]) scripts[file] = new Promise((done, failed) => {
      const tag = el('script', { src: 'vendor/' + file });
      tag.onload = done; tag.onerror = () => { delete scripts[file]; failed(new Error('页面组件没有加载成功，请重试')); };
      document.head.append(tag);
    });
    return scripts[file];
  }

  // ---- where we are: ?dir=<project>&path=<folder>[&file=<name>]
  let place = { path: asked.get('path') || '', file: asked.get('file') || '' };
  const from = asked.get('from') === 'terminal' ? 'terminal' : '';
  const address = p => '?dir=' + encodeURIComponent(dir) + (from ? '&from=' + from : '') + (p.path ? '&path=' + encodeURIComponent(p.path) : '') + (p.file ? '&file=' + encodeURIComponent(p.file) : '');
  const join = (folder, name) => folder ? folder + '/' + name : name;
  function go(next) { next.depth = ((history.state && history.state.depth) || 0) + 1; place = next; history.pushState(next, '', address(next)); scrollTo(0, 0); show(); }
  window.addEventListener('popstate', event => { place = event.state || { path: asked.get('path') || '', file: asked.get('file') || '' }; show(); });
  $('back').addEventListener('click', () => {
    if (history.state) return history.back();                                     // a step taken on this page
    if (place.file) return go({ path: place.path, file: '' });                   // a file opened by a link: its folder
    if (place.path) return go({ path: place.path.split('/').slice(0, -1).join('/'), file: '' });
    leave();
  });
  // Out of the file pages: to the terminal they were opened from, which is one step back, else to the project.
  function leave() { if (from && history.length > 1) history.back(); else location.href = '../?project=' + encodeURIComponent(dir); }
  function show() {
    run++;
    $('download').hidden = !place.file;
    $('upload').hidden = !!place.file;
    $('problem').hidden = true; $('progress').hidden = true;
    for (const url of handed.splice(0)) URL.revokeObjectURL(url);
    if (place.file) showFile(run); else showFolder(run);
  }
  function problem(title, text, again) {
    $('loading').hidden = true; $('progress').hidden = true;
    $('problem').hidden = false; $('problem-title').textContent = title; $('problem-text').textContent = text || '';
    $('retry').hidden = !again; $('retry').onclick = again || null;
  }
  const handed = [];      // pictures handed to the page as addresses; given back when another file is shown
  function hand(bytes, type) { const url = URL.createObjectURL(new Blob([bytes], { type })); handed.push(url); return url; }

  // ---- a folder
  const folders = new Map();      // what was listed before is shown at once and then brought up to date
  let sort = saved('files-sort', 'name'), hidden = saved('files-hidden', '') === '1';
  function drawFolder(listing) {
    const all = listing.entries.filter(e => hidden || !e.hidden), wanted = $('search').value.trim().toLowerCase();
    const found = K.order(wanted ? all.filter(e => e.name.toLowerCase().includes(wanted)) : all, sort);
    $('search').hidden = all.length <= 12 && !wanted;
    const skipped = listing.entries.length - all.length;
    $('where').textContent = all.length + ' 项' + (skipped ? ' · ' + skipped + ' 项隐藏' : '');
    $('entries').replaceChildren(...found.map(entry => {
      const picture = K.picture(entry.name, entry.dir);
      const about = entry.dir ? ago(entry.modified) : K.size(entry.size) + ' · ' + ago(entry.modified);
      return el('li', { className: 'card' + (entry.hidden ? ' dim' : '') }, el('button', { type: 'button', className: 'open',
        onclick: () => go(entry.dir ? { path: join(place.path, entry.name), file: '' } : { path: place.path, file: entry.name }) },
        el('span', { className: 'tool kind-' + picture, html: ICON[picture] }),
        el('span', { className: 'text' }, el('b', { textContent: entry.name }), el('span', { className: 'meta', textContent: about })),
        entry.dir ? el('span', { className: 'chev', html: ICON.chev }) : ''));
    }));
    const note = $('folder-note');
    note.hidden = found.length > 0 && !listing.more;
    note.textContent = listing.more ? '该文件夹内容过多，仅列出前 3000 项。' : wanted ? '没有名称里带“' + wanted + '”的。' : skipped ? '这里只有隐藏的文件，可在右上角打开“显示隐藏的文件”。' : '这个文件夹是空的。';
  }
  function crumbs() {
    const parts = place.path ? place.path.split('/') : [], nav = $('crumbs');
    nav.replaceChildren(el('button', { type: 'button', textContent: dir, disabled: !parts.length, onclick: () => go({ path: '', file: '' }) }));
    parts.forEach((name, at) => nav.append(el('span', { html: ICON.chev }), el('button', { type: 'button', textContent: name, disabled: at === parts.length - 1,
      onclick: () => go({ path: parts.slice(0, at + 1).join('/'), file: '' }) })));
    nav.scrollLeft = nav.scrollWidth;
  }
  async function showFolder(mine) {
    $('file').hidden = true; $('folder').hidden = false; $('tabs').hidden = true;
    $('heading').textContent = place.path ? place.path.split('/').pop() : dir;
    document.title = $('heading').textContent + ' · 文件';
    crumbs();
    const known = folders.get(place.path);
    if (known) drawFolder(known); else { $('entries').textContent = ''; $('folder-note').hidden = true; $('where').textContent = ''; $('search').hidden = true; $('loading').hidden = false; }
    try {
      const listing = await ask('file_list', place.path);
      if (mine !== run) return;
      folders.set(place.path, listing);
      $('loading').hidden = true;
      drawFolder(listing);
    } catch (error) { if (mine === run && !known) problem('打不开这个文件夹', error.message, show); else if (mine === run) toast(error.message); }
  }
  $('search').addEventListener('input', () => { const known = folders.get(place.path); if (known) drawFolder(known); });

  // ---- one file
  let wrap = saved('files-wrap', '') === '1', current = null;
  function code(text, language) {
    const lines = text.split('\n').length - (text.endsWith('\n') ? 1 : 0), body = el('code');
    const colours = window.hljs && language && language !== 'plaintext' && hljs.getLanguage(language) && text.length < 400000;
    if (colours) body.innerHTML = hljs.highlight(text, { language, ignoreIllegals: true }).value; else body.textContent = text;
    return el('div', { className: 'code' + (wrap ? ' wrap' : '') }, el('pre', { className: 'gutter', textContent: Array.from({ length: Math.max(1, lines) }, (_, n) => n + 1).join('\n') }), el('pre', { className: 'lines' }, body));
  }
  // A document: what Markdown or Word turned into, cleaned of anything that could run, with its code coloured and its
  // wide tables given room to be moved sideways.
  function documentOf(html, paper) {
    const doc = el('article', { className: 'doc' + (paper ? ' paper' : ''), html: DOMPurify.sanitize(html, { FORBID_TAGS: ['style', 'form', 'button', 'textarea', 'select'], FORBID_ATTR: ['style'] }) });
    doc.querySelectorAll('input').forEach(box => { if (box.type === 'checkbox') box.disabled = true; else box.remove(); });       // the ticks of a task list, nothing to type into
    doc.querySelectorAll('pre code').forEach(block => {
      const named = (block.className.match(/language-([\w+-]+)/) || [])[1];
      if (window.hljs && named && hljs.getLanguage(named)) block.innerHTML = hljs.highlight(block.textContent, { language: named, ignoreIllegals: true }).value;
    });
    doc.querySelectorAll('table').forEach(table => { const box = el('div', { className: 'wide' }); table.replaceWith(box); box.append(table); });
    doc.querySelectorAll('a[href]').forEach(link => { link.target = '_blank'; link.rel = 'noopener noreferrer'; });
    return doc;
  }
  // Pictures and links of a Markdown file that point to files beside it are fetched from the computer like the file itself.
  function relatives(doc, mine) {
    const local = address => address && !/^([a-z][a-z0-9+.-]*:|\/\/|#|\/)/i.test(address);
    const resolve = address => { const parts = (place.path ? place.path.split('/') : []); decodeURI(address.split(/[?#]/)[0]).split('/').forEach(p => { if (p === '..') parts.pop(); else if (p && p !== '.') parts.push(p); }); return parts.join('/'); };
    doc.querySelectorAll('a[href]').forEach(link => {
      const to = link.getAttribute('href');
      if (!local(to)) return;
      link.removeAttribute('target');
      link.addEventListener('click', event => { event.preventDefault(); const target = resolve(to), cut = target.lastIndexOf('/'); go({ path: cut < 0 ? '' : target.slice(0, cut), file: target.slice(cut + 1) }); });
    });
    Array.from(doc.querySelectorAll('img')).filter(image => local(image.getAttribute('src'))).slice(0, 24).forEach(async image => {
      const from = resolve(image.getAttribute('src'));
      image.removeAttribute('src');
      try { const got = await read(from, 8e6, mine); if (mine === run) image.src = hand(got.bytes, K.kind(from).type); } catch (error) { image.alt = (image.alt || from) + '（没有读到）'; }
    });
  }
  async function markdown(text, mine) {
    await Promise.all([need('marked.min.js'), need('purify.min.js'), need('highlight.min.js')]);
    let front = '';
    const fenced = /^---\r?\n([\s\S]*?)\r?\n---\r?\n/.exec(text);
    if (fenced) { front = fenced[1]; text = text.slice(fenced[0].length); }
    const doc = documentOf(marked.parse(text, { gfm: true, breaks: false }), false);
    if (front) doc.prepend(el('pre', { className: 'front' }, el('code', { html: hljs.highlight(front, { language: 'yaml', ignoreIllegals: true }).value })));
    relatives(doc, mine);
    return doc;
  }
  // A notebook: its cells in order, the text as a document, the code with what it printed and drew.
  async function notebook(text) {
    await Promise.all([need('marked.min.js'), need('purify.min.js'), need('highlight.min.js')]);
    const book = JSON.parse(text), language = ((book.metadata || {}).language_info || {}).name || ((book.metadata || {}).kernelspec || {}).language || 'python';
    const whole = value => Array.isArray(value) ? value.join('') : String(value == null ? '' : value);
    const page = el('div', { className: 'notebook' });
    (book.cells || []).forEach(cell => {
      if (cell.cell_type === 'markdown') return page.append(documentOf(marked.parse(whole(cell.source), { gfm: true }), false));
      if (cell.cell_type !== 'code') return page.append(el('pre', { className: 'printed', textContent: whole(cell.source) }));
      const box = el('section', { className: 'cell' }, el('span', { className: 'count', textContent: '[' + (cell.execution_count == null ? ' ' : cell.execution_count) + ']' }), code(whole(cell.source), language));
      (cell.outputs || []).forEach(out => {
        const data = out.data || {};
        if (data['image/png'] || data['image/jpeg']) box.append(el('img', { src: 'data:' + (data['image/png'] ? 'image/png' : 'image/jpeg') + ';base64,' + whole(data['image/png'] || data['image/jpeg']).replace(/\s/g, '') }));
        else if (data['text/html'] && /<table/i.test(whole(data['text/html']))) box.append(documentOf(whole(data['text/html']), false));
        else { const printed = out.text != null ? whole(out.text) : data['text/plain'] != null ? whole(data['text/plain']) : out.traceback ? out.traceback.join('\n') : ''; if (printed) box.append(el('pre', { className: 'printed' + (out.traceback ? ' failed' : ''), textContent: printed.replace(/\x1b\[[0-9;]*m/g, '') })); }
      });
      page.append(box);
    });
    return page;
  }
  function table(rows) {
    const shown = rows.slice(0, 2000), columns = Math.min(80, shown.reduce((most, row) => Math.max(most, row.length), 0));
    const letters = n => { let s = ''; for (n++; n > 0; n = Math.floor((n - 1) / 26)) s = String.fromCharCode(65 + (n - 1) % 26) + s; return s; };
    const grid = el('table', { className: 'grid' }, el('thead', null, el('tr', null, el('th'), ...Array.from({ length: columns }, (_, c) => el('th', { textContent: letters(c) })))));
    const body = el('tbody');
    shown.forEach((row, r) => body.append(el('tr', null, el('th', { textContent: r + 1 }), ...Array.from({ length: columns }, (_, c) => {
      const value = row[c] == null ? '' : String(row[c]);
      return el('td', { textContent: value, className: /^[-+(]?[\d,]*\.?\d+(e[-+]?\d+)?[%)]?$/i.test(value.trim()) ? 'number' : '' });
    }))));
    grid.append(body);
    const box = el('div', { className: 'wide sheet' }, grid);
    const cut = rows.length > shown.length || rows.some(row => row.length > columns);
    return cut ? el('div', null, box, el('p', { className: 'note', textContent: '表格很大，这里显示前 ' + shown.length + ' 行、' + columns + ' 列。' })) : box;
  }
  async function workbook(bytes, ext) {
    await need('xlsx.full.min.js');
    const book = ext === 'csv' || ext === 'tsv' ? XLSX.read(K.text(bytes), { type: 'string', raw: true, FS: ext === 'tsv' ? '\t' : undefined }) : XLSX.read(bytes, { type: 'array', cellDates: true });
    return book.SheetNames.map(name => ({ label: book.SheetNames.length > 1 || !/^Sheet1$/i.test(name) ? name : '', draw: () => {
      const rows = XLSX.utils.sheet_to_json(book.Sheets[name], { header: 1, raw: false, defval: '', blankrows: true });
      while (rows.length && rows[rows.length - 1].every(v => v === '')) rows.pop();
      return rows.length ? table(rows) : el('p', { className: 'note', textContent: '这张表是空的。' });
    } }));
  }
  async function pdf(bytes, mine) {
    await need('pdf.min.js');
    pdfjsLib.GlobalWorkerOptions.workerSrc = 'vendor/pdf.worker.min.js';
    const file = await pdfjsLib.getDocument({ data: bytes, cMapUrl: 'vendor/cmaps/', cMapPacked: true, isEvalSupported: false }).promise;
    const page = el('div', { className: 'pages' }), width = Math.min(document.documentElement.clientWidth, 720) - 24, sharp = Math.min(2, window.devicePixelRatio || 1);
    const first = (await file.getPage(1)).getViewport({ scale: 1 });
    // A page is drawn when it comes near the screen, and one at a time: a long document does not hold the phone up.
    let line = Promise.resolve();
    const near = new IntersectionObserver(seen => seen.forEach(entry => {
      if (!entry.isIntersecting) return;
      near.unobserve(entry.target);
      line = line.then(async () => {
        if (mine !== run) return;
        const sheet = await file.getPage(Number(entry.target.dataset.page)), view = sheet.getViewport({ scale: width / sheet.getViewport({ scale: 1 }).width * sharp });
        const canvas = el('canvas', { width: Math.floor(view.width), height: Math.floor(view.height) });
        await sheet.render({ canvasContext: canvas.getContext('2d'), viewport: view }).promise;
        entry.target.style.aspectRatio = view.width + ' / ' + view.height;
        entry.target.replaceChildren(canvas);
      }).catch(() => { entry.target.textContent = '这一页没有画出来'; });
    }), { rootMargin: '600px 0px' });
    for (let n = 1; n <= file.numPages; n++) {
      const holder = el('div', { className: 'sheet-of-paper', textContent: n + ' / ' + file.numPages });
      holder.dataset.page = n; holder.style.aspectRatio = first.width + ' / ' + first.height;
      page.append(holder); near.observe(holder);
    }
    return page;
  }
  function picture(bytes, type) {
    const image = el('img', { src: hand(bytes, type), alt: place.file }), box = el('div', { className: 'picture' }, image);
    image.addEventListener('load', () => { $('where').textContent = $('where').textContent.replace(/^/, image.naturalWidth + ' × ' + image.naturalHeight + ' · '); }, { once: true });
    image.addEventListener('click', () => box.classList.toggle('actual'));       // a tap changes between fitting the screen and its real size
    return box;
  }
  function tabs(list, start) {
    const bar = $('tabs');
    bar.hidden = list.filter(t => t.label).length < 2;
    const pick = async at => {
      Array.from(bar.children).forEach((b, n) => b.setAttribute('aria-selected', String(n === at)));
      const mine = run;
      try { const drawn = await list[at].draw(); if (mine === run) $('view').replaceChildren(drawn); } catch (error) { if (mine === run) problem('这个文件没有显示出来', error.message); }
    };
    bar.replaceChildren(...list.map((t, at) => el('button', { type: 'button', role: 'tab', textContent: t.label || '内容', onclick: () => pick(at) })));
    return pick(start || 0);
  }
  async function showFile(mine) {
    const path = join(place.path, place.file), kind = K.kind(place.file);
    $('folder').hidden = true; $('file').hidden = false; $('tabs').hidden = true;
    $('view').textContent = ''; $('loading').hidden = false;
    $('heading').textContent = place.file; $('where').textContent = '';
    document.title = place.file + ' · 文件';
    current = { path, kind, bytes: null, text: null };
    try {
      // A file of a kind this page does not know is looked at by its first piece: text is shown, anything else is named.
      const first = await ask('file_read', path, 0);
      if (mine !== run) return;
      $('where').textContent = K.size(first.size) + ' · ' + ago(first.modified);
      let view = kind.view, language = kind.language;
      if (!view && K.readable(bytesOf(first.data))) { view = 'code'; language = 'plaintext'; }
      if (!view) { $('loading').hidden = true; return $('view').replaceChildren(unknown(first.size, '这种文件不能在这里预览。')); }
      const limit = kind.limit || 3e6;
      if (first.size > limit) { $('loading').hidden = true; return $('view').replaceChildren(unknown(first.size, '文件有 ' + K.size(first.size) + '，超过了预览的上限 ' + K.size(limit) + '。')); }
      const got = await read(path, limit, mine, first);
      if (mine !== run) return;
      current.bytes = got.bytes;
      const text = () => current.text == null ? (current.text = K.text(got.bytes)) : current.text;
      const source = { label: '源码', draw: async () => { await need('highlight.min.js'); return code(text(), language || 'plaintext'); } };
      let list;
      if (view === 'markdown') list = [{ label: '预览', draw: () => markdown(text(), mine) }, source];
      else if (view === 'code' && kind.ext === 'ipynb') list = [{ label: '笔记本', draw: () => notebook(text()) }, source];
      else if (view === 'code') list = [Object.assign({}, source, { label: '' })];
      else if (view === 'image') list = kind.ext === 'svg' ? [{ label: '图片', draw: () => picture(got.bytes, kind.type) }, Object.assign({}, source, { draw: async () => { await need('highlight.min.js'); return code(text(), 'xml'); } })] : [{ label: '', draw: () => picture(got.bytes, kind.type) }];
      else if (view === 'word') list = [{ label: '', draw: () => wordLooks(got.bytes) }];
      else if (view === 'sheet') list = await workbook(got.bytes, kind.ext);
      else if (view === 'slides') list = [{ label: '', draw: () => slideLooks(got.bytes) }];
      else if (view === 'pdf') list = [{ label: '', draw: () => pdf(got.bytes, mine) }];
      if (mine !== run) return;
      await tabs(list, 0);
      $('loading').hidden = true;
    } catch (error) {
      if (mine !== run || error.message === 'left') return;
      problem('打不开这个文件', error.message, show);
    }
  }
  function unknown(size, why) {
    const picture = K.picture(place.file, false);
    return el('section', { className: 'unknown' }, el('span', { className: 'tool kind-' + picture, html: ICON[picture] }), el('b', { textContent: place.file }), el('p', { textContent: K.size(size) }), el('p', { textContent: why }),
      el('button', { type: 'button', className: 'solid', textContent: native ? '保存到手机' : '保存到这台设备', onclick: save }));
  }
  // The file is fetched from the computer again, piece by piece, and kept on this device: inside the app in the
  // phone's Download folder, each piece handed on as it arrives; in a browser as an ordinary download.
  const SAVE_LIMIT = 300 * 1024 * 1024;
  let saving = false, uploading = null, cancelled = false, pickedFolder = '';
  function transfer(text, at, size) {
    $('transfer').hidden = false;
    $('transfer-text').textContent = text;
    $('transfer-bar').hidden = false;
    $('transfer-bar').value = Math.round(100 * at / Math.max(1, size));
    $('transfer-cancel').hidden = false;
    $('upload').disabled = $('download').disabled = true;
  }
  function transferDone(text) {
    $('transfer').hidden = false;
    $('transfer-text').textContent = text;
    $('transfer-bar').hidden = $('transfer-cancel').hidden = true;
    $('upload').disabled = $('download').disabled = false;
  }
  $('transfer-cancel').onclick = () => {
    cancelled = true;
    if (uploading) uploading.cancel();
    $('transfer-cancel').disabled = true;
    $('transfer-text').textContent = '正在取消传输…';
  };
  $('upload').onclick = () => {
    if (native && !native.canUploadFiles) return toast('请先更新手机 App，再上传文件');
    pickedFolder = place.path; $('upload-input').value = ''; $('upload-input').click();
  };
  $('upload-input').onchange = async () => {
    const files = Array.from($('upload-input').files || []), folder = pickedFolder;
    if (!files.length || saving || uploading) return;
    cancelled = false; $('transfer-cancel').disabled = false;
    let done = 0, failure = '';
    uploading = new window.RemoteCliUpload(ask, newId, (at, size) => transfer('正在上传 ' + (done + 1) + '/' + files.length + '：' + files[done].name + ' · ' + K.size(at) + ' / ' + K.size(size), at, size));
    try {
      for (const file of files) {
        await uploading.send(file, join(folder, file.name));
        done++;
      }
    } catch (error) { failure = error.message; }
    finally {
      uploading = null;
      folders.delete(folder);
      if (!place.file && place.path === folder) show();
      transferDone('已上传 ' + done + '/' + files.length + ' 个文件到 ' + join(dir, folder) + (failure ? '。' + failure : '。'));
    }
  };
  $('download').onclick = save;
  window.addEventListener('beforeunload', event => {
    if (saving || uploading) { event.preventDefault(); event.returnValue = ''; }
  });
  async function save() {
    if (saving || uploading) return toast('请等待当前传输完成');
    if (native && !native.saveStart) return toast('请先更新手机 App，再保存文件');
    const path = join(place.path, place.file), name = place.file, parts = [];
    saving = true; cancelled = false; $('transfer-cancel').disabled = false;
    transfer('正在准备下载 ' + name, 0, 1);
    let outcome = '';
    try {
      let piece = await ask('file_read', path, 0), at = 0;
      const size = piece.size, modified = piece.modified;
      if (piece.size > SAVE_LIMIT) throw new Error('文件有 ' + K.size(piece.size) + '，超过了保存的上限 ' + K.size(SAVE_LIMIT));
      if (native) { const refused = native.saveStart(name); if (refused) throw new Error(refused); }
      for (;;) {
        if (cancelled) throw new Error('已取消下载');
        if (piece.offset !== at || piece.size !== size || piece.modified !== modified) throw new Error('电脑上的文件已变化，请重新下载');
        if (native) { const refused = native.savePiece(piece.data); if (refused) throw new Error(refused); } else parts.push(bytesOf(piece.data));
        at += Math.floor(piece.data.length * 3 / 4) - (piece.data.endsWith('==') ? 2 : piece.data.endsWith('=') ? 1 : 0);
        transfer('正在下载 ' + name + ' · ' + K.size(at) + ' / ' + K.size(size), at, size);
        if (piece.end) { if (at !== size) throw new Error('文件未完整下载，请重试'); break; }
        if (!piece.data.length || at >= size) throw new Error('下载进度不一致，请重试');
        piece = await ask('file_read', path, at);
      }
      if (native) {
        const where = native.saveEnd();
        if (!where) throw new Error('没有保存成功，请检查手机的存储空间');
        choose('已保存到手机', [{ label: '打开', sub: '用手机上的应用打开 ' + name, act: () => native.openSaved() }, { label: '知道了', sub: '文件在：' + where, act: () => {} }]);
      } else {
        const url = URL.createObjectURL(new Blob(parts, { type: 'application/octet-stream' }));
        el('a', { href: url, download: name }).click();
        setTimeout(() => URL.revokeObjectURL(url), 60000);
      }
      outcome = native ? '已保存到手机：' + name : '已交给浏览器下载：' + name;
    } catch (error) {
      if (native && native.saveCancel) native.saveCancel();
      outcome = error.message || '没有保存';
    } finally { saving = false; transferDone(outcome); }
  }

  // ---- the menu
  $('more').addEventListener('click', () => {
    const list = [];
    if (place.file) {
      if (current && (current.kind.view === 'code' || current.kind.view === 'markdown' || current.kind.ext === 'svg' || !current.kind.view))
        list.push({ label: '长行自动换行', sub: '关闭时可以左右滑动看完整的一行', on: wrap, act: () => { wrap = !wrap; keep('files-wrap', wrap ? '1' : ''); document.querySelectorAll('.code').forEach(c => c.classList.toggle('wrap', wrap)); } });
      list.push({ label: '复制文件路径', sub: join(dir, join(place.path, place.file)), act: async () => { try { await navigator.clipboard.writeText(join(place.path, place.file)); toast('已复制'); } catch (error) { toast('这里不能复制'); } } });
      list.push({ label: native ? '保存到手机' : '保存到这台设备', sub: native ? '放进手机的“下载 / RemoteAI”，之后可以用别的应用打开' : '作为浏览器的下载', act: save });
    } else {
      [['name', '按名称排列'], ['time', '最近修改的在前'], ['size', '最大的在前']].forEach(([key, label]) => list.push({ label, on: sort === key, act: () => { sort = key; keep('files-sort', key); show(); } }));
      list.push({ label: '显示隐藏的文件', sub: '以 . 开头的和系统隐藏的', on: hidden, act: () => { hidden = !hidden; keep('files-hidden', hidden ? '1' : ''); show(); } });
    }
    if (from) list.push({ label: '回到终端', sub: '终端一直在电脑上运行', act: () => { const steps = (history.state && history.state.depth) || 0; history.go(-steps - 1); } });
    list.push({ label: '回到项目', sub: dir, act: () => { location.href = '../?project=' + encodeURIComponent(dir); } });
    choose(place.file ? '文件' : '文件夹', list);
  });

  if (!dir) location.replace('../'); else show();
})();
