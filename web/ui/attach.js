/* Uploads chat attachments and renders their references in conversation history. */
(() => {
  'use strict';
  const FOLDER = '.remote-ai/attachments', MOST = 4, LARGEST = 20 * 1024 * 1024;
  const { name: NAME, storedName, label } = window.RemoteAttachmentNames;
  const LINE = /^(?:附图|附件)：.*[\\/]\.remote-ai[\\/]attachments[\\/]([^\\/]+)$/;
  const TYPES = { png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', webp: 'image/webp', gif: 'image/gif' };
  const PICTURE = '<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3.500" y="4.500" width="17" height="15" rx="3"/><circle cx="9" cy="10" r="1.700"/><path d="M20.500 15.500l-4.600-4.600a1.500 1.500 0 0 0-2.100 0L5 19.500"/></svg>';
  const CROSS = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2.600" stroke-linecap="round" aria-hidden="true"><path d="M7 7l10 10M17 7L7 17"/></svg>';
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  const CLIP = '<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true"><path d="m9 13 6-6a2.1 2.1 0 0 1 3 3l-8 8a4 4 0 0 1-5.7-5.7l9-9a5.6 5.6 0 0 1 8 8l-9.4 9.4"/></svg>';

  function el(tag, props, ...children) {
    const node = document.createElement(tag);
    Object.keys(props || {}).forEach(key => {
      const value = props[key];
      if (value === undefined || value === null) return;
      if (key === 'html') node.innerHTML = value;
      else if (key.startsWith('aria')) node.setAttribute('aria-' + key.slice(4).toLowerCase(), String(value));
      else node[key] = value;
    });
    children.forEach(child => { if (child !== '' && child != null) node.append(child); });
    return node;
  }

  // ---- the computer's files, as files/ asks for them
  async function ask(base, dir, action, path, offset, extra) {
    const controller = new AbortController(), timer = setTimeout(() => controller.abort(), 35000);
    let reply, body = {};
    try {
      reply = await fetch(base + 'api/files', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, signal: controller.signal,
        body: JSON.stringify(Object.assign({ id: newId(), action, dir, path, offset: offset || 0 }, extra)) });
      try { body = await reply.json(); } catch (error) { /* not JSON */ }
    } catch (error) { throw new Error(error.name === 'AbortError' ? '传输超时，请检查连接后重试' : '连接中断，请检查网络后重试'); }
    finally { clearTimeout(timer); }
    if (!reply.ok) throw new Error(body.error || '请求失败（' + reply.status + '）');
    return body;
  }
  // the pictures already here, by name: those sent from this page, and those read once
  const seen = new Map();
  function picture(base, dir, name) {
    const key = dir + '/' + name;
    if (!seen.has(key)) {
      seen.set(key, (async () => {
        const parts = [];
        let at = 0, piece;
        do {
          piece = await ask(base, dir, 'file_read', FOLDER + '/' + name, at);
          if (piece.size > LARGEST) throw new Error('图片过大');
          const raw = atob(piece.data || ''), bytes = new Uint8Array(raw.length);
          for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
          parts.push(bytes);
          at += bytes.length;
        } while (!piece.end && at < piece.size && parts[parts.length - 1].length);
        return URL.createObjectURL(new Blob(parts, { type: TYPES[name.split('.').pop()] }));
      })());
      seen.get(key).catch(() => seen.delete(key));
    }
    return seen.get(key);
  }

  // ---- what a message says, and the pictures it names
  function split(text) {
    const names = [], kept = [];
    String(text || '').split('\n').forEach(line => {
      const found = LINE.exec(line.replace(/\r$/, ''));
      if (found && NAME.test(found[1])) names.push(found[1]); else kept.push(line);
    });
    return { text: names.length ? kept.join('\n').replace(/\s+$/, '') : String(text || ''), names };
  }
  function shots(names, { base = '', dir = '', from = '' } = {}) {
    const box = el('div', { className: 'attach-shots' });
    names.forEach(name => {
      const filename = label(name);
      const img = filename ? null : el('img', { alt: '图片', decoding: 'async' });
      const open = el('button', { type: 'button', className: filename ? 'attach-file' : 'attach-shot loading', ariaLabel: filename ? '查看 ' + filename : '查看图片', onclick: event => {
        event.stopPropagation();
        location.href = base + 'files/?dir=' + encodeURIComponent(dir) + '&path=' + encodeURIComponent(FOLDER) + '&file=' + encodeURIComponent(name) + (from ? '&from=' + from : '');
      } }, filename || img);
      open.dataset.name = name;
      if (img) picture(base, dir, name).then(url => { img.src = url; open.classList.remove('loading'); }, () => { open.classList.remove('loading'); open.classList.add('missing'); });
      box.append(open);
    });
    return box;
  }

  // ---- the button in a composer, and the pictures picked to go with the next message
  class Picker {
    /* form: the composer; before: what the button stands before (in the form, or in a row of it); base: where api/ lies; dir(): the project */
    constructor({ form, before, base = '', dir, toast, changed }) {
      Object.assign(this, { form, base, dir, toast, changed: changed || (() => {}) });
      this.items = [];
      this.input = el('input', { type: 'file', accept: 'image/*', multiple: true, hidden: true, ariaLabel: '选择图片' });
      this.button = el('button', { type: 'button', className: 'attach-pick', ariaLabel: '添加图片', html: PICTURE, hidden: true, onclick: () => this.pick() });
      this.tray = el('div', { className: 'attach-tray', ariaLabel: '要发送的附件', hidden: true });
      if (before) before.parentNode.insertBefore(this.button, before); else form.append(this.button);
      form.append(this.input);
      form.parentNode.insertBefore(this.tray, form);
      this.input.addEventListener('change', () => { this.add(Array.from(this.input.files || [])); this.input.value = ''; });
    }
    set allowFiles(on) {
      if (this.documents === on) return;
      this.documents = on;
      this.input.accept = on ? '' : 'image/*';
      this.input.setAttribute('aria-label', on ? '选择文件' : '选择图片');
      this.button.setAttribute('aria-label', on ? '添加文件' : '添加图片');
      this.button.innerHTML = on ? CLIP : PICTURE;
    }
    get count() { return this.items.length; }
    get names() { return this.items.map(item => item.name); }
    set enabled(on) { this.button.hidden = !on; }
    get enabled() { return !this.button.hidden; }
    pick() {
      if (this.busy) return;
      if (this.items.length >= MOST) return this.toast('最多 ' + MOST + ' 个附件');
      this.input.click();
    }
    add(files) {
      if (this.busy) return;
      let refused = '';
      for (const file of files) {
        if (this.items.length >= MOST) { refused = '最多 ' + MOST + ' 个附件'; break; }
        const name = storedName(file, this.documents);
        if (!name) { refused = '只能添加 PNG、JPG、WebP 或 GIF 图片'; continue; }
        if (file.size > LARGEST) { refused = '单个附件不能超过 20 MB'; continue; }
        if (!file.size && !label(name)) { refused = '无法读取这张图片，请重新选择'; continue; }
        this.items.push({ file, name, url: label(name) ? null : URL.createObjectURL(file), sent: false });
      }
      if (refused) this.toast(refused);
      this.draw();
    }
    remove(item) {
      if (this.busy) return;
      this.items = this.items.filter(one => one !== item);
      if (!item.sent && item.url) URL.revokeObjectURL(item.url);
      this.draw();
    }
    draw() {
      this.tray.replaceChildren(...this.items.map(item => el('div', { className: label(item.name) ? 'attach-chip attach-file-chip' : 'attach-chip' },
        label(item.name) || el('img', { src: item.url || undefined, alt: '' }),
        el('button', { type: 'button', className: 'attach-remove', ariaLabel: '移除' + (label(item.name) || '图片'), html: CROSS, disabled: !!this.busy, onclick: () => this.remove(item) }))));
      this.tray.hidden = !this.items.length;
      this.form.classList.toggle('attach-busy', !!this.busy);
      this.changed();
    }
    /* Sends the pictures up, one after another, and answers the paths to name in the message. What went up before a
       failure is not sent again; on a failure the pictures stay here, and the error says why. */
    async upload(progress) {
      if (!this.items.length) return [];
      this.busy = true; this.draw();
      const dir = this.dir();
      const total = this.items.reduce((sum, item) => sum + (item.sent ? 0 : item.file.size), 0) || 1;
      let done = 0;
      try {
        for (const item of this.items) {
          if (item.sent) continue;
          const upload = new window.RemoteCliUpload((action, path, offset, extra) => ask(this.base, dir, action, path, offset, extra), newId,
            at => progress(Math.min(99, Math.floor(100 * (done + at) / total))));
          try { await upload.send(item.file, FOLDER + '/' + item.name); }
          catch (error) { throw new Error('附件没有发出：' + error.message); }
          item.sent = true;
          done += item.file.size;
          if (item.url) seen.set(dir + '/' + item.name, Promise.resolve(item.url));
        }
        return this.items.map(item => FOLDER + '/' + item.name);
      } finally { this.busy = false; this.draw(); }
    }
    /* After the message went: the pictures leave the tray (what was drawn of them stays, for the message itself). */
    clear() { this.items = []; this.draw(); }
    /* A message that did not go, put back to be edited: its pictures are on the computer already and go with it again. */
    restore(names) {
      names.slice(0, MOST - this.items.length).forEach(name => {
        const item = { file: null, name, url: '', sent: true };
        this.items.push(item);
        if (!label(name)) picture(this.base, this.dir(), name).then(url => { item.url = url; this.draw(); }, () => {});
      });
      this.draw();
    }
  }

  window.RemoteAttach = { Picker, split, shots, NAME, FOLDER, MOST };
})();
