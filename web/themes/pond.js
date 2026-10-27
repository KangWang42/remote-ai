/* The optional pond skin. The upstream simulation stays separate from app lifecycle and controls. */
import { Pond, PondRenderer, DARK, LIGHT } from './vendor/ascii-pond/pond.js';
import { koi } from './vendor/ascii-pond/koi.js';

export class PondTheme {
  constructor() {
    this.enabled = false;
    this.frame = 0;
    this.last = 0;
    this.accumulator = 0;
    this.pond = null;
    this.renderer = null;
    this.paused = false;
    try { this.paused = localStorage.getItem('rcli-pond-paused') === '1'; } catch (_) { /* private storage */ }
    this.reduced = matchMedia('(prefers-reduced-motion: reduce)');
    this.forced = matchMedia('(forced-colors: active)');
    this.canvas = document.createElement('canvas');
    this.canvas.className = 'pond-background';
    this.canvas.setAttribute('aria-hidden', 'true');
    this.button = document.createElement('button');
    this.button.type = 'button';
    this.button.className = 'pond-toggle';
    this.button.onclick = () => {
      this.paused = !this.paused;
      try { localStorage.setItem('rcli-pond-paused', this.paused ? '1' : '0'); } catch (_) { /* private storage */ }
      this.sync();
    };
    this.onState = () => this.sync();
    this.onHide = () => this.stop();
    this.onResize = () => this.resize();
    this.tick = now => this.draw(now);
    this.resizeObserver = new ResizeObserver(this.onResize);
    const css = document.createElement('link');
    css.rel = 'stylesheet';
    css.href = new URL('./pond.css', import.meta.url).href;
    document.head.append(css);
  }

  setEnabled(enabled) {
    if (this.enabled === enabled) return;
    this.enabled = enabled;
    if (enabled) {
      document.body.prepend(this.canvas);
      const header = document.querySelector('main header, header');
      if (header) header.insertBefore(this.button, header.querySelector('#menu'));
      this.reduced.addEventListener('change', this.onState);
      this.forced.addEventListener('change', this.onState);
      document.addEventListener('visibilitychange', this.onState);
      window.addEventListener('pagehide', this.onHide);
      window.addEventListener('pageshow', this.onState);
      window.addEventListener('blur', this.onHide);
      window.addEventListener('focus', this.onState);
      this.resizeObserver.observe(document.documentElement);
      this.sync();
    } else {
      this.stop();
      this.resizeObserver.disconnect();
      this.reduced.removeEventListener('change', this.onState);
      this.forced.removeEventListener('change', this.onState);
      document.removeEventListener('visibilitychange', this.onState);
      window.removeEventListener('pagehide', this.onHide);
      window.removeEventListener('pageshow', this.onState);
      window.removeEventListener('blur', this.onHide);
      window.removeEventListener('focus', this.onState);
      this.canvas.remove();
      this.button.remove();
      this.pond = this.renderer = null;
      this.canvas.width = this.canvas.height = 0;
      this.accumulator = 0;
    }
  }

  resize() {
    if (!this.enabled || this.forced.matches) return;
    const width = document.documentElement.clientWidth, height = window.innerHeight;
    if (!width || !height) return;
    if (!this.pond) {
      const fish = koi({ count: 3, young: 1 });
      this.pond = new Pond([fish]);
      const dark = { ...DARK, ...fish.colors.dark, page: [16, 25, 27], bg: [16, 25, 27] };
      this.renderer = new PondRenderer(this.canvas, { dark, light: { ...LIGHT, ...fish.colors.light } });
    }
    // Cap backing pixels as well as frame rate on high-density phones.
    this.renderer.resize(width, height, Math.min(devicePixelRatio || 1, 1.5));
    this.pond.resize(width, height);
    this.renderer.render(this.pond);
  }

  sync() {
    this.stop();
    if (!this.enabled) return;
    this.canvas.hidden = this.forced.matches;
    this.button.hidden = this.forced.matches;
    const still = this.paused || this.reduced.matches;
    this.button.disabled = this.reduced.matches;
    const label = this.reduced.matches ? '池塘静止：系统已减少动态效果' : this.paused ? '播放池塘动画' : '暂停池塘动画';
    this.button.setAttribute('aria-label', label);
    this.button.title = label;
    this.button.innerHTML = '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">' +
      (still ? '<path d="m9 5 10 7-10 7z" fill="currentColor"/>' : '<path d="M8 5v14M16 5v14" stroke="currentColor" stroke-width="3"/>') + '</svg>';
    if (this.forced.matches || document.hidden) return;
    if (!this.pond) this.resize();
    if (!still && document.hasFocus()) this.frame = requestAnimationFrame(this.tick);
  }

  stop() {
    cancelAnimationFrame(this.frame);
    this.frame = 0;
    this.last = 0;
  }

  draw(now) {
    this.frame = 0;
    if (!this.enabled || document.hidden || this.paused) return;
    if (this.reduced.matches || this.forced.matches) { this.sync(); return; }
    if (!this.last) this.last = now;
    const elapsed = now - this.last;
    if (elapsed >= 1000 / 24 && this.pond) {
      this.last = now;
      this.accumulator += Math.min(elapsed / 1000, 0.1) * 0.65;
      const step = 1 / 120;
      let steps = 0;
      while (this.accumulator >= step && steps < 12) {
        this.pond.step(step);
        this.accumulator -= step;
        steps++;
      }
      if (steps === 12) this.accumulator = 0;
      this.renderer.depositWake(this.pond);
      this.renderer.diffuse(steps * step);
      this.renderer.render(this.pond);
    }
    this.frame = requestAnimationFrame(this.tick);
  }
}
