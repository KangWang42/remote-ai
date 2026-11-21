/* The sheet that rises from the foot of a page: one card that holds a stack of pages (a menu, and what is set from
   it, slide in from the side and back), with the parts such pages are made of. Used by the conversation and terminal
   pages for their menu, and by 协作 for what is set there (ui/work.js). Styles: ui/sheet.css.

   RemoteSheet.open(page) -> a Sheet; page = { title, build(p) }: build fills p.body (and p.foot, for what is done on
   the page) and is run again whenever the page is shown anew, so it draws from the state it is given. The sheet
   answers its `result` with what close(value) was given, or null. The back key of the phone (history) goes back a
   page, and from the first page closes the sheet; so does a pull down on its top bar. */
(() => {
  'use strict';
  const svg = (path, size = 20) => `<svg viewBox="0 0 24 24" width="${size}" height="${size}" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${path}</svg>`;
  const ICONS = {
    back: '<path d="M15 5l-7 7 7 7"/>', close: '<path d="M7 7l10 10M17 7L7 17"/>', chev: '<path d="M9 6l6 6-6 6"/>', tick: '<path d="M5 12.5l4.5 4.5L19 7.500"/>',
    hand: '<path d="M4 8.500h11.500M12 5l3.500 3.500L12 12M20 15.500H8.500M12 12l-3.500 3.500L12 19"/>',
    clock: '<circle cx="12" cy="12" r="8.600"/><path d="M12 7.300V12l3.100 2"/>',
    folder: '<path d="M3.500 7.500a2 2 0 0 1 2-2h4l2 2.200h7a2 2 0 0 1 2 2v7.800a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/>',
    copy: '<rect x="8.500" y="8.500" width="11" height="11" rx="2.500"/><path d="M15.500 8.500V6.500a2 2 0 0 0-2-2h-7a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h2"/>',
    pen: '<path d="M4.500 19.500l1-4.200L15.800 5a2 2 0 0 1 2.800 0l.400.400a2 2 0 0 1 0 2.800L8.700 18.500z"/><path d="M13.500 7.300l3.200 3.200"/>',
    look: '<circle cx="12" cy="12" r="8.500"/><path d="M12 3.500a8.500 8.500 0 0 0 0 17c1.400 0 1.800-1 1.300-2-.600-1.200.200-2.500 1.600-2.500h2.100A3.500 3.500 0 0 0 20.500 12 8.500 8.500 0 0 0 12 3.500z"/><circle cx="8" cy="11" r="1.100"/><circle cx="11.500" cy="7.800" r="1.100"/><circle cx="15.500" cy="9" r="1.100"/>',
    text: '<path d="M4 18l4.500-12h1L14 18M5.600 14h6.800M15 18l3-8h.600L21.500 18M15.800 15.600h4.700"/>',
    lines: '<path d="M8 6h12M8 12h12M8 18h12M4 4.500v15M2.700 6L4 4.500 5.300 6M2.700 18L4 19.500 5.300 18"/>',
    term: '<rect x="3" y="5" width="18" height="14" rx="3"/><path d="M7.500 10l3 2.200-3 2.200M12.500 15h4"/>',
    chat: '<path d="M5 18.500l-1 2.500 3.600-1.400A8.500 8.500 0 1 0 5 18.500z"/><path d="M8.500 11h7M8.500 14h4"/>',
    stop: '<rect x="6" y="6" width="12" height="12" rx="3"/>', play: '<path d="M8.500 6l9 6-9 6z"/>',
    plus: '<path d="M12 5v14M5 12h14"/>', trash: '<path d="M4.500 7h15M9.500 7V4.500h5V7M6.500 7l1 12.500h9l1-12.500"/>',
    many: '<path d="M4.500 6.500h15M4.500 12h15M4.500 17.500h9"/><circle cx="18" cy="17.500" r="1.600"/>',
    relay: '<circle cx="6.500" cy="6.800" r="2.900"/><circle cx="17.500" cy="17.200" r="2.900"/><path d="M12 6.800h3a2.500 2.500 0 0 1 2.500 2.500v2.200M12 17.200H9a2.500 2.500 0 0 1-2.500-2.500v-2.200"/>',
    spark: '<path d="M12 3.500l1.900 5.600 5.600 1.900-5.600 1.900L12 18.500l-1.900-5.600-5.600-1.900 5.600-1.900z"/>',
    claude: '<path d="M12 3.500v17M3.500 12h17M6 6l12 12M18 6L6 18"/>', codex: '<path d="M12 3l7.800 4.500v9L12 21l-7.800-4.500v-9z"/><path d="M9 10l-2 2 2 2M15 10l2 2-2 2"/>',
    agy: '<path d="M3 17c5 0 6-11 9-11s4 11 9 11M8 18l4-4 4 4"/>',
    gpu: '<rect x="4" y="6" width="16" height="12" rx="2.500"/><path d="M8 10h3v4H8zM14 10h2M14 14h2"/>',
    refresh: '<path d="M19.500 12a7.500 7.500 0 1 1-2.200-5.300M19.500 4.500v3.800h-3.800"/>',
    pin: '<path d="M9 3.500h6l-1 6 3.500 3.500v1.500h-11V13L10 9.500zM12 14.500V21"/>',
    hide: '<path d="M3.500 12s3-6 8.500-6c1.600 0 3 .5 4.200 1.200M20.500 12s-3 6-8.500 6c-1.600 0-3-.5-4.200-1.200M4 4l16 16"/><path d="M9.900 9.900a3 3 0 0 0 4.200 4.200"/>',
    enter: '<path d="M3.500 7.500a2 2 0 0 1 2-2h4l2 2.200h7a2 2 0 0 1 2 2v7.800a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/><path d="M10.500 11l3 2.500-3 2.500"/>'
  };
  const icon = (name, size) => svg(ICONS[name] || '', size);

  function el(tag, props, ...children) {
    const node = document.createElement(tag);
    Object.keys(props || {}).forEach(key => {
      const value = props[key];
      if (value === undefined || value === null) return;
      if (key === 'html') node.innerHTML = value;
      else if (key === 'className') node.className = value;
      else if (key === 'style' && typeof value === 'string') node.style.cssText = value;
      else if (key.startsWith('on') && typeof value === 'function') node.addEventListener(key.slice(2), value);
      else if (key.startsWith('aria') && key.length > 4) node.setAttribute('aria-' + key.slice(4).toLowerCase(), String(value));
      else if (key.startsWith('data') && key.length > 4) node.dataset[key[4].toLowerCase() + key.slice(5)] = value;
      else if (key in node) node[key] = value;
      else node.setAttribute(key, value);
    });
    children.flat(Infinity).forEach(child => { if (child !== '' && child !== null && child !== undefined && child !== false) node.append(child); });
    return node;
  }

  // ---- the sheet
  // The steps a closed sheet kept in the page's history are undone after it (history.go is not at once): the next
  // sheet waits for that, or it would take the going back for the phone's back key and close at once.
  let current = null, settled = Promise.resolve();
  function undo(steps) {
    settled = new Promise(done => {
      let left = steps;
      const finished = () => { clearTimeout(timer); window.removeEventListener('popstate', heard); done(); };
      const heard = () => { if (--left <= 0) finished(); };
      const timer = setTimeout(finished, 500);
      window.addEventListener('popstate', heard);
      try { history.go(-steps); } catch (error) { finished(); }
    });
  }
  class Sheet {
    constructor(label, options) {
      this.stack = [];
      this.full = !!(options && options.full);
      this.result = new Promise(done => { this.answer = done; });
      this.entries = 0; this.ignoring = 0; this.gone = false;
      this.dialog = el('dialog', { className: 'rs' + (this.full ? ' rs-full' : ''), ariaLabel: label || '' });
      this.titleNode = el('h2', { id: 'rs-title-' + Math.random().toString(36).slice(2) });
      this.subtitleNode = el('span', { className: 'rs-sub', hidden: true });
      this.badgeNode = el('span', { className: 'rs-badge', hidden: true });
      this.titleWrap = el('div', { className: 'rs-title-wrap' }, this.titleNode, this.subtitleNode);
      this.backButton = el('button', { type: 'button', className: 'rs-icon rs-back', ariaLabel: '返回', html: icon('back', 22), onclick: () => this.goBack() });
      this.closeButton = el('button', { type: 'button', className: 'rs-icon rs-close', ariaLabel: '关闭', html: icon('close', 18), onclick: () => this.close(null) });
      this.top = el('div', { className: 'rs-top' }, el('span', { className: 'rs-grab', ariaHidden: 'true' }),
        el('div', { className: 'rs-bar' }, this.backButton, this.full ? this.titleWrap : this.titleNode, this.full ? this.badgeNode : '', this.closeButton));
      this.body = el('div', { className: 'rs-body' });
      this.foot = el('div', { className: 'rs-foot' });
      this.card = el('div', { className: 'rs-card' }, this.top, this.body, this.foot);
      this.dialog.setAttribute('aria-labelledby', this.titleNode.id);
      this.dialog.append(this.card);
      // a tap beside the card, Esc, and the phone's back key
      this.dialog.addEventListener('click', event => { if (event.target === this.dialog) this.close(null); });
      this.dialog.addEventListener('cancel', event => { event.preventDefault(); this.goBack(); });
      this.popped = () => {
        clearTimeout(this.waiting); this.waiting = 0;
        if (this.ignoring > 0) { this.ignoring--; return; }       // a step back already taken without it (goBack)
        if (this.entries > 0) this.entries--;
        if (this.stack.length > 1) this.pop(); else this.finish(null, false);
      };
      if (!this.full) this.drag();
    }
    // a pull down on the top bar takes the sheet away, as on the phone's own sheets
    drag() {
      let start = null, moved = 0;
      this.top.addEventListener('pointerdown', event => {
        if (event.target.closest('button')) return;
        start = event.clientY; moved = 0;
        this.top.setPointerCapture(event.pointerId);
        this.dialog.classList.add('dragging');
      });
      this.top.addEventListener('pointermove', event => {
        if (start === null) return;
        moved = Math.max(0, event.clientY - start);
        this.card.style.transform = moved ? 'translateY(' + moved + 'px)' : '';
      });
      const end = () => {
        if (start === null) return;
        start = null;
        this.dialog.classList.remove('dragging');
        if (moved > Math.min(140, this.card.offsetHeight * .28)) this.close(null);
        else { this.card.style.transition = 'transform .2s ease-out'; this.card.style.transform = ''; setTimeout(() => { this.card.style.transition = ''; }, 220); }
      };
      this.top.addEventListener('pointerup', end);
      this.top.addEventListener('pointercancel', end);
    }
    get page() { return this.stack[this.stack.length - 1] || null; }
    setHeader({ title, subtitle, badge, tone } = {}) {
      if (title !== undefined) {
        if (this.page) this.page.title = title;
        this.titleNode.textContent = title || '';
      }
      if (this.full) {
        if (subtitle !== undefined) {
          this.subtitleNode.textContent = subtitle || '';
          this.subtitleNode.hidden = !subtitle;
        }
        if (badge !== undefined) {
          this.badgeNode.textContent = badge || '';
          this.badgeNode.className = 'rs-badge' + (tone ? ' ' + tone : '');
          this.badgeNode.hidden = !badge || this.stack.length > 1;
        }
      }
    }
    show(direction) {
      const page = this.page;
      if (!page) return;
      const keep = page.scroll || 0;
      this.body.replaceChildren(); this.foot.replaceChildren();
      const view = el('div', { className: 'rs-page' + (direction === 'back' ? ' back' : '') });
      const p = {
        sheet: this, body: view, foot: this.foot,
        set title(text) { page.title = text; },
        setHeader: info => { if (this.page === page) this.setHeader(info); },
        refresh: () => { if (this.page === page) { page.scroll = this.body.scrollTop; this.show('same'); } }
      };
      this.titleNode.textContent = page.title || '';
      if (this.full) {
        const rootPage = this.stack.length < 2;
        this.backButton.hidden = rootPage;
        this.closeButton.classList.toggle('rs-close-back', rootPage);
        this.closeButton.innerHTML = icon(rootPage ? 'back' : 'close', rootPage ? 22 : 18);
        this.closeButton.setAttribute('aria-label', rootPage ? '返回' : '关闭');
        if (!rootPage) {
          this.subtitleNode.textContent = '';
          this.subtitleNode.hidden = true;
          this.badgeNode.textContent = '';
          this.badgeNode.hidden = true;
        }
      } else {
        this.backButton.hidden = this.stack.length < 2;
      }
      try { page.build(p); }
      catch (error) { view.append(el('p', { className: 'rs-note bad', textContent: error.message || '没有显示出来' })); console.error(error); }
      if (direction === 'same') view.style.animation = 'none';
      this.titleNode.textContent = page.title || '';
      this.body.append(view);
      this.body.scrollTop = direction === 'same' || direction === 'back' ? keep : 0;
    }
    open(page) {
      if (current && current !== this) current.finish(null, true);
      current = this;
      this.opener = document.activeElement;             // given the focus back when the sheet is gone
      document.body.append(this.dialog);
      this.stack = [page];
      this.show('in');
      this.dialog.showModal();
      settled.then(() => { if (this.gone) return; window.addEventListener('popstate', this.popped); this.remember(); });
      // the first thing to type in is ready; otherwise the card itself, not its first button, has the focus
      const first = this.body.querySelector('[autofocus]');
      if (first) setTimeout(() => first.focus(), 260); else { this.card.tabIndex = -1; this.card.focus({ preventScroll: true }); }
      return this;
    }
    remember() { try { history.pushState({ rs: true }, ''); this.entries++; } catch (error) { /* a page without history: the buttons still lead back */ } }
    push(page) { if (this.page) this.page.scroll = this.body.scrollTop; this.stack.push(page); this.show('in'); settled.then(() => { if (!this.gone) this.remember(); }); return this; }
    replace(page) { this.stack[this.stack.length - 1] = page; this.show('in'); return this; }
    pop() { if (this.stack.length < 2) return; this.stack.pop(); this.show('back'); }
    goBack() {
      if (this.entries > 0) {
        // popped() does the rest, as for the phone's own key. Where the step back does not come (a page whose
        // history the browser keeps otherwise), the sheet goes back by itself and lets the late one pass.
        if (this.waiting) return;
        this.waiting = setTimeout(() => {
          this.waiting = 0; this.entries--; this.ignoring++;
          if (this.stack.length > 1) this.pop(); else this.finish(null, false);
        }, 400);
        history.back();
        return;
      }
      if (this.stack.length > 1) this.pop(); else this.finish(null, false);
    }
    /* `leave`: the page itself is left at once (location.replace), so the steps kept in its history are not undone */
    close(value, leave) { this.finish(value, leave); }
    finish(value, leave) {
      if (this.gone) return;
      this.gone = true;
      clearTimeout(this.waiting);
      window.removeEventListener('popstate', this.popped);
      if (!leave && this.entries > 0) { const steps = this.entries; this.entries = 0; undo(steps); }
      if (current === this) current = null;
      this.answer(value === undefined ? null : value);
      const dialog = this.dialog, opener = this.opener;
      const gone = () => {
        dialog.close(); dialog.remove();
        // back to what opened it, unless another sheet has the focus by now
        if (!current && opener && opener.isConnected && opener.focus && !document.querySelector('dialog[open]')) opener.focus({ preventScroll: true });
      };
      if (!dialog.open) { dialog.remove(); return; }
      if (matchMedia('(prefers-reduced-motion: reduce)').matches || leave) { gone(); return; }
      // It stops being modal at once (the page and the next sheet take presses and the focus again); only its card
      // is seen going down, taking no presses itself.
      dialog.classList.add('closing'); dialog.inert = true; dialog.close();
      if (!current && opener && opener.isConnected && opener.focus) opener.focus({ preventScroll: true });
      setTimeout(() => dialog.remove(), 190);
    }
  }

  const haptic = kind => { try { if (window.RemoteCliNative && window.RemoteCliNative.haptic) window.RemoteCliNative.haptic(kind || 'tick'); } catch (error) { /* ignore */ } };
  // ---- the parts of a page
  const ui = {
    el, icon, svg, ICONS,
    /* a group of rows, with a name above it and a few words under it */
    section(label, content, note) {
      return el('section', { className: 'rs-section' }, label ? el('h3', { className: 'rs-label', textContent: label }) : '', content, note ? el('p', { className: 'rs-note', textContent: note }) : '');
    },
    group(...rows) { return el('div', { className: 'rs-group' }, ...rows); },
    /* a row. `go`: a press leads on (a chevron); `act`: what a press does; `end`: a control at its end */
    row({ icon: name, tint, label, detail, value, go, act, end, danger, lead, wrap, checked, disabled, id, html }) {
      const control = !act && !go;
      const node = el(control ? 'div' : 'button', { className: 'rs-row' + (danger ? ' danger' : '') + (lead ? ' lead' : '') + (end && end.classList && end.classList.contains('rs-seg') ? ' control' : ''), id, type: control ? undefined : 'button', disabled: !!disabled });
      if (checked !== undefined) { node.setAttribute('role', 'radio'); node.setAttribute('aria-checked', String(!!checked)); }
      if (name) node.append(el('span', { className: 'rs-glyph' + (tint ? ' rs-tint-' + tint : ''), html: html || icon(name, lead ? 22 : 18) }));
      if (danger && !name) node.append(el('span', { textContent: label }));
      else node.append(el('span', { className: 'rs-text' + (wrap ? ' wrap' : '') }, el('b', { textContent: label }), detail ? el('small', { textContent: detail }) : ''));
      if (value !== undefined && value !== '') node.append(el('span', { className: 'rs-value', textContent: value }));
      // a control at the end of a row that is itself pressed stands beside it, not inside it (a button in a button)
      const apart = end && !control && (end.tagName === 'BUTTON' || (end.querySelector && end.querySelector('button, input, select')));
      if (end && !apart) node.append(end);
      if (checked !== undefined) node.append(el('span', { className: 'rs-tick', html: icon('tick', 20) }));
      if (go) node.append(el('span', { className: 'rs-chev', html: icon('chev', 18) }));
      if (act || go) node.addEventListener('click', () => { haptic('tick'); (act || go)(); });
      return apart ? el('div', { className: 'rs-split' }, node, el('span', { className: 'rs-end' }, end)) : node;
    },
    /* the things done most: a few tiles side by side. A tile that cannot be used says why when it is pressed */
    tiles(list) {
      const box = el('div', { className: 'rs-tiles', style: '--n:' + list.length });
      list.forEach(t => box.append(el('button', { type: 'button', className: 'rs-tile', id: t.id, ariaDisabled: t.why ? 'true' : undefined, ariaLabel: t.label,
        onclick: () => { haptic('tick'); t.why ? t.say(t.why) : t.act(); } }, el('span', { className: 'rs-glyph' + (t.tint ? ' rs-tint-' + t.tint : ''), html: icon(t.icon, 20) }), el('span', { textContent: t.label }))));
      return box;
    },
    /* one of a few, side by side */
    seg(choices, chosen, changed, { label, full } = {}) {
      const box = el('span', { className: 'rs-seg' + (full ? ' full' : ''), role: 'group', ariaLabel: label || '' });
      choices.forEach(([value, text]) => box.append(el('button', { type: 'button', textContent: text, ariaPressed: String(value === chosen), dataValue: String(value), onclick: event => {
        event.stopPropagation();
        haptic('tick');
        box.querySelectorAll('button').forEach(button => button.setAttribute('aria-pressed', String(button === event.currentTarget)));
        changed(value);
      } })));
      return box;
    },
    toggle(on, changed, label) {
      const button = el('button', { type: 'button', className: 'rs-switch', role: 'switch', ariaChecked: String(!!on), ariaLabel: label || '', onclick: event => {
        event.stopPropagation();
        haptic('tick');
        const next = button.getAttribute('aria-checked') !== 'true';
        button.setAttribute('aria-checked', String(next));
        changed(next, button);
      } });
      return button;
    },
    stepper(value, { min, max, step = 1, unit = '', label }, changed) {
      const out = el('output', { textContent: value + unit });
      let now = value;
      const minus = el('button', { type: 'button', ariaLabel: '减小' + (label || ''), textContent: '−' }), plus = el('button', { type: 'button', ariaLabel: '增大' + (label || ''), textContent: '+' });
      const set = next => { now = Math.min(max, Math.max(min, next)); out.textContent = now + unit; minus.disabled = now <= min; plus.disabled = now >= max; };
      minus.onclick = event => { event.stopPropagation(); haptic('tick'); set(now - step); changed(now); };
      plus.onclick = event => { event.stopPropagation(); haptic('tick'); set(now + step); changed(now); };
      set(now);
      return el('span', { className: 'rs-stepper', role: 'group', ariaLabel: label || '' }, minus, out, plus);
    },
    /* a few words to pick (`multi`: several) or to press */
    chips(choices, { chosen, multi, days, label } = {}, changed) {
      const box = el('div', { className: 'rs-chips' + (days ? ' days' : ''), role: 'group', ariaLabel: label || '' });
      const picked = new Set(multi ? chosen || [] : chosen === undefined ? [] : [chosen]);
      choices.forEach(([value, text]) => {
        const chip = el('button', { type: 'button', className: 'rs-chip', textContent: text, dataValue: String(value), ariaPressed: changed && (multi || chosen !== undefined) ? String(picked.has(value)) : undefined });
        chip.onclick = () => {
          if (multi) { if (picked.has(value)) picked.delete(value); else picked.add(value); chip.setAttribute('aria-pressed', String(picked.has(value))); changed([...picked]); return; }
          if (chosen !== undefined) box.querySelectorAll('.rs-chip').forEach(one => one.setAttribute('aria-pressed', String(one === chip)));
          changed(value);
        };
        box.append(chip);
      });
      box.pick = values => {
        picked.clear(); (multi ? values : [values]).forEach(v => picked.add(v));
        box.querySelectorAll('.rs-chip').forEach(one => one.setAttribute('aria-pressed', String([...picked].some(v => String(v) === one.dataset.value))));
      };
      return box;
    },
    /* skins, each drawn as a small picture of itself */
    swatches(list, changed) {
      const box = el('div', { className: 'rs-swatches', role: 'group', ariaLabel: '外观' });
      list.forEach(s => {
        const look = s.follow ? el('span', { className: 'look', textContent: s.key === 'auto' ? '系统' : 'App' })
          : el('span', { className: 'look', style: 'background:' + s.bg },
              el('i', { style: 'background:' + (s.bubble || s.accent) }),
              el('i', { style: 'background:' + s.ink + ';opacity:.78' }),
              el('i', { style: 'background:' + s.muted + ';opacity:.58' }),
              el('b', { style: 'background:' + s.accent }));
        box.append(el('button', { type: 'button', className: 'rs-swatch' + (s.follow ? ' follow' : ''), ariaPressed: String(!!s.current), dataValue: s.key, onclick: event => {
          box.querySelectorAll('.rs-swatch').forEach(one => one.setAttribute('aria-pressed', String(one === event.currentTarget)));
          changed(s.key);
        } }, look, el('span', { textContent: s.name })));
      });
      return box;
    },
    area({ id, value = '', placeholder = '', max = 2000, rows = 4, label, autofocus } = {}, changed) {
      const box = el('textarea', { className: 'rs-area', id, value, placeholder, maxLength: max, rows, ariaLabel: label || placeholder, spellcheck: false, autocapitalize: 'off', autofocus: !!autofocus });
      const count = el('span', { className: 'rs-count' });
      const counted = () => { count.textContent = box.value.length > max * .8 ? box.value.length + ' / ' + max : ''; };
      const grow = () => { box.style.height = 'auto'; box.style.height = Math.min(260, Math.max(96, box.scrollHeight + 2)) + 'px'; };
      box.addEventListener('input', () => { counted(); grow(); if (changed) changed(box.value); });
      counted();
      requestAnimationFrame(grow);
      const field = el('div', { className: 'rs-field' }, box, count);
      field.box = box;
      field.grow = grow;
      return field;
    },
    input({ id, value = '', placeholder = '', max = 80, label, autofocus } = {}) {
      return el('input', { className: 'rs-input', id, value, placeholder, maxLength: max, ariaLabel: label || placeholder, spellcheck: false, autocapitalize: 'off', autocomplete: 'off', autofocus: !!autofocus });
    },
    note(text, kind) { return el('p', { className: 'rs-note' + (kind ? ' ' + kind : ''), textContent: text }); },
    summary(text, kind, name) { return el('p', { className: 'rs-summary' + (kind ? ' ' + kind : ''), role: 'status' }, el('span', { html: icon(name || 'clock', 18) }), el('span', { textContent: text })); },
    loading() { return el('i', { className: 'rs-loading', ariaHidden: 'true' }); },
    empty(text) { return el('p', { className: 'rs-empty', textContent: text }); },
    /* the conversation a menu is about: what runs it, its name, and how it stands */
    who({ icon: name, tint, title, line, state }) {
      return el('div', { className: 'rs-who' }, el('span', { className: 'rs-glyph' + (tint ? ' rs-tint-' + tint : ''), html: icon(name, 24) }),
        el('span', { className: 'rs-text' }, el('b', { textContent: title }), el('small', null, state ? el('i', { style: '--c:' + state }) : '', el('span', { textContent: line }))));
    },
    /* the main thing done on a page. While it is done the button says so and cannot be pressed again; what went
       wrong (an Error thrown by `act`) is said above it, and the page stays as it was filled in. */
    go(label, act, { busy, kind, id } = {}) {
      const said = el('p', { className: 'rs-said', role: 'alert' }), text = el('span', { textContent: label });
      const button = el('button', { type: 'button', className: 'rs-go' + (kind ? ' ' + kind : ''), id }, text);
      let running = false;
      button.addEventListener('click', async () => {
        if (running) return;
        running = true; said.textContent = '';
        button.disabled = true; text.textContent = busy || label;
        const spin = el('i', { className: 'rs-spin', ariaHidden: 'true' });
        if (busy) button.prepend(spin);
        try { await act(message => { said.textContent = message; }); }
        catch (error) { said.textContent = error && error.message ? error.message : '没有完成，请重试'; }
        finally { running = false; spin.remove(); if (button.isConnected) { button.disabled = false; text.textContent = button.dataset.label || label; } }
      });
      button.relabel = words => { button.dataset.label = words; if (!running) text.textContent = words; };
      return { said, button, nodes: [said, button] };
    }
  };

  /* A question before something that cannot be undone, as a page: what it is, a few words why, and the button. */
  function confirm({ title, text, label, kind, busy, act }) {
    return { title, build(p) {
      p.body.append(ui.summary(text, kind === 'danger' ? 'bad' : '', kind === 'danger' ? 'stop' : 'spark'));
      const go = ui.go(label, async () => { await act(p.sheet); }, { kind, busy, id: 'rs-confirm' });
      p.foot.append(...go.nodes);
    } };
  }
  // Copies text where the page may (a page served without https cannot use the clipboard's interface).
  async function copy(text) {
    try { if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); return true; } } catch (error) { /* the older way below */ }
    const box = el('textarea', { value: text, readOnly: true, style: 'position:fixed;top:0;left:0;width:1px;height:1px;opacity:0' });
    (document.querySelector('dialog[open]') || document.body).append(box);
    box.select(); box.setSelectionRange(0, text.length);
    let done = false;
    try { done = document.execCommand('copy'); } catch (error) { done = false; }
    box.remove();
    return done;
  }
  ui.confirm = confirm;
  window.RemoteSheet = { open: (page, label, options) => new Sheet(label || page.title, options || (page && page.full ? { full: true } : null)).open(page), ui, copy, confirm: options => new Sheet(options.title).open(confirm(options)), get current() { return current; } };
})();
