/* Reading and drafts shared by the two conversation surfaces. No transport or tool ownership lives here. */
(() => {
  'use strict';
  class Reader {
    constructor(root) {
      this.root = root;
      this.deferred = new Map();
      this.selectionChanged = () => {
        if (!this.root.isConnected) return this.dispose();
        this.change(() => {
          for (const [node, update] of this.deferred) {
            if (!node.isConnected) this.deferred.delete(node);
            else if (!this.selected(node)) { this.deferred.delete(node); update(); }
          }
        });
      };
      document.addEventListener('selectionchange', this.selectionChanged);
    }
    selected(node = this.root) {
      const selection = window.getSelection();
      if (!selection || selection.isCollapsed || !selection.rangeCount) return false;
      return selection.getRangeAt(0).intersectsNode(node);
    }
    atEnd() { return this.root.scrollHeight - this.root.scrollTop - this.root.clientHeight < 80; }
    change(update, initial = false) {
      const root = this.root, follow = (initial || this.atEnd()) && !this.selected();
      const edge = root.getBoundingClientRect().top;
      const anchors = Array.from(root.children).filter(node => node.getBoundingClientRect().bottom > edge)
        .map(node => [node, node.getBoundingClientRect().top]);
      const previous = root.scrollTop;
      update();
      if (follow) root.scrollTop = root.scrollHeight;
      else {
        const anchor = anchors.find(([node]) => node.parentNode === root);
        if (anchor) root.scrollTop += anchor[0].getBoundingClientRect().top - anchor[1];
        else root.scrollTop = previous;
      }
    }
    paint(node, update) {
      if (this.selected(node)) this.deferred.set(node, update);
      else { this.deferred.delete(node); update(); }
    }
    // Keys describe actual message content, including occurrence order. Retaining a node also retains its
    // selection, keyboard focus and open details; changed content alone receives a new node.
    replace(fresh) {
      const kept = new Map();
      for (const node of this.root.children) {
        if (node.readKey === undefined) continue;
        const list = kept.get(node.readKey) || [];
        list.push(node); kept.set(node.readKey, list);
      }
      let cursor = this.root.firstChild;
      for (const incoming of Array.from(fresh.children)) {
        const node = kept.get(incoming.readKey)?.shift() || incoming;
        if (node === cursor) cursor = cursor.nextSibling;
        else this.root.insertBefore(node, cursor);
      }
      while (cursor) { const next = cursor.nextSibling; cursor.remove(); cursor = next; }
    }
    dispose() { document.removeEventListener('selectionchange', this.selectionChanged); this.deferred.clear(); }
  }
  class Draft {
    constructor(scope) { this.key = 'rcli-draft:' + scope; this.memory = ''; }
    get() {
      try { return sessionStorage.getItem(this.key) || ''; } catch (error) { return this.memory; }
    }
    set(value) {
      this.memory = value;
      try { if (value) sessionStorage.setItem(this.key, value); else sessionStorage.removeItem(this.key); } catch (error) { /* memory remains usable */ }
    }
    clear(sent) { if (this.get() === sent) this.set(''); }
  }
  window.RemoteConversation = { Reader, Draft };
})();
