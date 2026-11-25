/* Pictures sent with a message, on the conversation page (chat/) and in a collaboration task (teams.js); styles in
   ui/attach.css. The pictures go up first, each as its own file in the project's `.remote-ai/attachments/`
   (files/transfers.js, `file_upload_*` of `/api/files`); the message then names them in `attachments`, and the
   computer adds a line `附图：<path>` for each after the words (docs/PROTOCOL.md).
   Such lines are drawn back as the pictures themselves, read with `file_read`.

   new RemoteAttach.Picker({ form, before, base, dir, toast })   the picture button in a composer and its tray
   RemoteAttach.split(text)                                       -> { text, names }: the words and the pictures named
   RemoteAttach.shots(names, { base, dir, from })                 -> the pictures as thumbnails, each opening in files/ */
(() => {
  'use strict';
  const FOLDER = '.remote-ai/attachments', MOST = 4, LARGEST = 20 * 1024 * 1024;
  const NAME = /^\d{8}-\d{6}-[0-9a-f]{6}\.(?:png|jpg|jpeg|webp|gif)$/;
  const LINE = /^附图：.*[\\/]\.remote-ai[\\/]attachments[\\/](\d{8}-\d{6}-[0-9a-f]{6}\.(?:png|jpg|jpeg|webp|gif))$/;
  const TYPES = { png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', webp: 'image/webp', gif: 'image/gif' };
  const EXT = { 'image/png': 'png', 'image/jpeg': 'jpg', 'image/webp': 'webp', 'image/gif': 'gif' };
  const PICTURE = '<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3.500" y="4.500" width="17" height="15" rx="3"/><circle cx="9" cy="10" r="1.700"/><path d="M20.500 15.500l-4.600-4.600a1.500 1.500 0 0 0-2.100 0L5 19.500"/></svg>';
  const CROSS = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2.600" stroke-linecap="round" aria-hidden="true"><path d="M7 7l10 10M17 7L7 17"/></svg>';
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  const two = n => String(n).padStart(2, '0');

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

  // A picture's name on the computer, made here: when it was picked, and six hex digits so two at once differ.
  function nameOf(file, at) {
    const ext = EXT[file.type] || ((/\.([a-z0-9]+)$/i.exec(file.name || '') || [])[1] || '').toLowerCase();
    if (!TYPES[ext]) return '';
    const hex = Array.from(crypto.getRandomValues(new Uint8Array(3)), b => b.toString(16).padStart(2, '0')).join('');
    return at.getFullYear() + two(at.getMonth() + 1) + two(at.getDate()) + '-' + two(at.getHours()) + two(at.getMinutes()) + two(at.getSeconds()) + '-' + hex + '.' + ext;
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
      if (found) names.push(found[1]); else kept.push(line);
    });
    return { text: names.length ? kept.join('\n').replace(/\s+$/, '') : String(text || ''), names };
  }
  function shots(names, { base = '', dir = '', from = '' } = {}) {
    const box = el('div', { className: 'attach-shots' });
    names.forEach(name => {
      const img = el('img', { alt: '图片', decoding: 'async' });
      const open = el('button', { type: 'button', className: 'attach-shot loading', ariaLabel: '查看图片', onclick: event => {
        event.stopPropagation();
        location.href = base + 'files/?dir=' + encodeURIComponent(dir) + '&path=' + encodeURIComponent(FOLDER) + '&file=' + encodeURIComponent(name) + (from ? '&from=' + from : '');
      } }, img);
      open.dataset.name = name;
      picture(base, dir, name).then(url => { img.src = url; open.classList.remove('loading'); }, () => { open.classList.remove('loading'); open.classList.add('missing'); });
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
      this.tray = el('div', { className: 'attach-tray', ariaLabel: '要发送的图片', hidden: true });
      if (before) before.parentNode.insertBefore(this.button, before); else form.append(this.button);
      form.append(this.input);
      form.parentNode.insertBefore(this.tray, form);
      this.input.addEventListener('change', () => { this.add(Array.from(this.input.files || [])); this.input.value = ''; });
    }
    get count() { return this.items.length; }
    get names() { return this.items.map(item => item.name); }
    set enabled(on) { this.button.hidden = !on; }
    get enabled() { return !this.button.hidden; }
    pick() {
      if (this.busy) return;
      if (this.items.length >= MOST) return this.toast('最多 ' + MOST + ' 张图片');
      this.input.click();
    }
    add(files) {
      const at = new Date();
      let refused = '';
      for (const file of files) {
        if (this.items.length >= MOST) { refused = '最多 ' + MOST + ' 张图片'; break; }
        const name = nameOf(file, at);
        if (!name) { refused = '只能添加 PNG、JPG、WebP 或 GIF 图片'; continue; }
        if (file.size > LARGEST) { refused = '单张图片不能超过 20 MB'; continue; }
        if (!file.size) { refused = '无法读取这张图片，请重新选择'; continue; }
        this.items.push({ file, name, url: URL.createObjectURL(file), sent: false });
      }
      if (refused) this.toast(refused);
      this.draw();
    }
    remove(item) {
      if (this.busy) return;
      this.items = this.items.filter(one => one !== item);
      if (!item.sent) URL.revokeObjectURL(item.url);
      this.draw();
    }
    draw() {
      this.tray.replaceChildren(...this.items.map(item => el('div', { className: 'attach-chip' },
        el('img', { src: item.url || undefined, alt: '' }),
        el('button', { type: 'button', className: 'attach-remove', ariaLabel: '移除图片', html: CROSS, disabled: !!this.busy, onclick: () => this.remove(item) }))));
      this.tray.hidden = !this.items.length;
      this.form.classList.toggle('attach-busy', !!this.busy);
      this.changed();
    }
    /* Sends the pictures up, one after another, and answers the paths to name in the message. What went up before a
       failure is not sent again; on a failure the pictures stay here, and the error says why. */
    async upload(progress) {
      if (!this.items.length) return [];
      this.busy = true; this.draw();
      const total = this.items.reduce((sum, item) => sum + (item.sent ? 0 : item.file.size), 0) || 1;
      let done = 0;
      try {
        for (const item of this.items) {
          if (item.sent) continue;
          const upload = new window.RemoteCliUpload((action, path, offset, extra) => ask(this.base, this.dir(), action, path, offset, extra), newId,
            at => progress(Math.min(99, Math.floor(100 * (done + at) / total))));
          try { await upload.send(item.file, FOLDER + '/' + item.name); }
          catch (error) { throw new Error('图片没有发出：' + error.message); }
          item.sent = true;
          done += item.file.size;
          seen.set(this.dir() + '/' + item.name, Promise.resolve(item.url));
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
        picture(this.base, this.dir(), name).then(url => { item.url = url; this.draw(); }, () => {});
      });
      this.draw();
    }
  }

  window.RemoteAttach = { Picker, split, shots, NAME, FOLDER, MOST };
})();
