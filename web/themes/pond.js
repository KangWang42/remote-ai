/* Koi are baked once at build time; the compositor reuses two decoded textures. */
export class PondTheme {
  constructor() {
    this.enabled = false;
    this.ready = false;
    this.active = true;
    this.idleTimer = 0;
    this.busyUntil = 0;
    this.touching = false;
    this.paused = false;
    try { this.paused = localStorage.getItem('rcli-pond-paused') === '1'; } catch (_) { /* private storage */ }
    this.reduced = matchMedia('(prefers-reduced-motion: reduce)');
    this.forced = matchMedia('(forced-colors: active)');
    this.background = document.createElement('div');
    this.background.className = 'pond-background';
    this.background.setAttribute('aria-hidden', 'true');
    this.background.dataset.running = 'false';
    const images = ['koi-amber.png', 'koi-pearl.png'].map(name => {
      const img = document.createElement('img');
      img.className = 'pond-fish';
      img.alt = '';
      img.width = 192; img.height = 320;
      img.decoding = 'async';
      img.draggable = false;
      img.src = new URL('./assets/' + name, import.meta.url).href;
      this.background.append(img);
      return img;
    });
    // Decode once before movement. Re-enabling keeps these same image objects and textures.
    Promise.all(images.map(img => img.decode())).then(() => {
      this.ready = true;
      this.sync();
    }).catch(() => { /* Keep the plain theme usable if a packaged texture is missing. */ });
    this.button = document.createElement('button');
    this.button.type = 'button';
    this.button.className = 'pond-toggle';
    this.button.onclick = () => {
      this.paused = !this.paused;
      try { localStorage.setItem('rcli-pond-paused', this.paused ? '1' : '0'); } catch (_) { /* private storage */ }
      this.sync();
    };
    this.onState = () => { this.active = !document.hidden; this.sync(); };
    this.onHide = () => {
      this.active = false;
      this.touching = false;
      this.busyUntil = 0;
      clearTimeout(this.idleTimer); this.idleTimer = 0;
      this.stop();
    };
    this.onActivity = () => this.yield();
    this.onTouchStart = () => { this.touching = true; this.yield(); };
    this.onTouchEnd = event => { this.touching = event.touches.length > 0; this.yield(); };
    this.activityEvents = ['wheel', 'scroll', 'keydown', 'input', 'pointerdown', 'rcli-terminal-motion'];
    if (!document.querySelector('link[data-pond-style]')) {
      const css = document.createElement('link');
      css.rel = 'stylesheet'; css.dataset.pondStyle = '';
      css.href = new URL('./pond.css', import.meta.url).href;
      document.head.append(css);
    }
  }

  setEnabled(enabled) {
    if (this.enabled === enabled) return;
    this.enabled = enabled;
    if (enabled) {
      this.active = !document.hidden;
      document.body.prepend(this.background);
      const header = document.querySelector('main header, header');
      if (header) header.insertBefore(this.button, header.querySelector('#menu'));
      this.reduced.addEventListener('change', this.onState);
      this.forced.addEventListener('change', this.onState);
      document.addEventListener('visibilitychange', this.onState);
      window.addEventListener('pagehide', this.onHide);
      window.addEventListener('pageshow', this.onState);
      window.addEventListener('blur', this.onHide);
      window.addEventListener('focus', this.onState);
      for (const type of this.activityEvents) document.addEventListener(type, this.onActivity, { capture: true, passive: true });
      document.addEventListener('touchstart', this.onTouchStart, { capture: true, passive: true });
      document.addEventListener('touchmove', this.onActivity, { capture: true, passive: true });
      document.addEventListener('touchend', this.onTouchEnd, { capture: true, passive: true });
      document.addEventListener('touchcancel', this.onTouchEnd, { capture: true, passive: true });
      this.sync();
    } else {
      this.stop();
      this.reduced.removeEventListener('change', this.onState);
      this.forced.removeEventListener('change', this.onState);
      document.removeEventListener('visibilitychange', this.onState);
      window.removeEventListener('pagehide', this.onHide);
      window.removeEventListener('pageshow', this.onState);
      window.removeEventListener('blur', this.onHide);
      window.removeEventListener('focus', this.onState);
      for (const type of this.activityEvents) document.removeEventListener(type, this.onActivity, true);
      document.removeEventListener('touchstart', this.onTouchStart, true);
      document.removeEventListener('touchmove', this.onActivity, true);
      document.removeEventListener('touchend', this.onTouchEnd, true);
      document.removeEventListener('touchcancel', this.onTouchEnd, true);
      clearTimeout(this.idleTimer); this.idleTimer = 0;
      this.touching = false; this.busyUntil = 0;
      this.background.remove(); this.button.remove();
    }
  }

  sync() {
    this.stop();
    if (!this.enabled) return;
    this.background.hidden = this.forced.matches;
    this.button.hidden = this.forced.matches;
    const still = this.paused || this.reduced.matches;
    this.button.disabled = this.reduced.matches;
    const label = this.reduced.matches ? '池塘静止：系统已减少动态效果' : this.paused ? '播放池塘动画' : '暂停池塘动画';
    if (this.button.title !== label) {
      this.button.setAttribute('aria-label', label); this.button.title = label;
      this.button.innerHTML = '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">' +
        (still ? '<path d="m9 5 10 7-10 7z" fill="currentColor"/>' : '<path d="M8 5v14M16 5v14" stroke="currentColor" stroke-width="3"/>') + '</svg>';
    }
    if (this.ready && this.active && !document.hidden && !this.forced.matches && !still && !this.touching && performance.now() >= this.busyUntil && document.hasFocus()) {
      this.background.dataset.running = 'true';
    }
  }

  stop() { if (this.background.dataset.running !== 'false') this.background.dataset.running = 'false'; }

  yield() {
    this.busyUntil = performance.now() + 1200;
    this.stop();
    // One pending wake-up, not a new timer for every finger movement or terminal frame.
    if (!this.idleTimer) this.idleTimer = setTimeout(() => this.resume(), 1250);
  }

  resume() {
    this.idleTimer = 0;
    if (!this.enabled || this.touching) return;
    const remaining = this.busyUntil - performance.now();
    if (remaining > 0) this.idleTimer = setTimeout(() => this.resume(), remaining + 50);
    else this.sync();
  }
}
