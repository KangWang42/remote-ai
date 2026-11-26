import { useEffect, useState } from 'preact/hooks';
import { ApiError, opId } from './api';
import { say } from './actions';
import type { Thread } from './threads';
import type { ChatMessageItem } from './types';

interface Pending {
  id: string;
  terminal: string;
  text: string;
  pictures: string[];
  state: 'sending' | 'queued' | 'unknown';
}

class ChatOutbox {
  private key = 'rcli-console-pending';
  private listeners = new Set<() => void>();
  items: Pending[] = [];

  constructor() {
    try {
      const stored: Pending[] = JSON.parse(sessionStorage.getItem(this.key) || '[]');
      this.items = stored.map((p) => ({ ...p, state: p.state === 'sending' ? 'unknown' : p.state }));
    } catch { /* No stored messages. */ }
  }
  subscribe(listener: () => void) {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }
  private save(items: Pending[]) {
    this.items = items;
    try {
      if (items.length) sessionStorage.setItem(this.key, JSON.stringify(items));
      else sessionStorage.removeItem(this.key);
    } catch { /* Keep tracking the open page's messages. */ }
    this.listeners.forEach((fn) => fn());
  }
  remove(ids: Set<string>) {
    if (this.items.some((p) => ids.has(p.id))) this.save(this.items.filter((p) => !ids.has(p.id)));
  }
  mark(ids: Set<string>, state: Pending['state']) {
    if (this.items.some((p) => ids.has(p.id) && p.state !== state))
      this.save(this.items.map((p) => ids.has(p.id) ? { ...p, state } : p));
  }
  async send(thread: Thread, text: string, pictures: string[]) {
    const terminal = thread.terminal!.id;
    if (this.items.some((p) => p.terminal === terminal && p.text === text && p.pictures.join('\n') === pictures.join('\n')))
      throw new Error('这条消息仍在等待确认，请先查看发送状态');
    const id = opId();
    this.save([...this.items, { id, terminal, text, pictures, state: 'sending' }]);
    try {
      await say(thread, text, pictures, id);
      this.mark(new Set([id]), 'queued');
    } catch (e) {
      if (!this.items.some((p) => p.id === id)) return; // The echo arrived before the HTTP result.
      if (e instanceof ApiError && e.status < 500) {
        this.remove(new Set([id]));
        throw e;
      }
      this.mark(new Set([id]), 'unknown');
      throw new Error('送达结果未知，请先检查对话');
    }
  }
}

export const chatOutbox = new ChatOutbox();

export function usePending(terminal: string, messages: ChatMessageItem[]) {
  const [, refresh] = useState(0);
  useEffect(() => chatOutbox.subscribe(() => refresh((n) => n + 1)), []);
  useEffect(() => {
    if (terminal && chatOutbox.items.length) {
      const echoed = messages.flatMap((m) => m.role === 'user' && m.op ? [m.op] : []);
      chatOutbox.remove(new Set(echoed));
    }
  }, [terminal, messages]);
  return chatOutbox.items.filter((p) => p.terminal === terminal);
}
