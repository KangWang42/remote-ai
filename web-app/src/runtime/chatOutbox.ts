import { useEffect, useState } from 'preact/hooks';
import { ApiError, opId } from './api';
import { say } from './actions';
import type { Thread } from './threads';
import type { ChatMessageItem } from './types';

export type PendingState = 'waiting' | 'paused' | 'sending' | 'sent' | 'unknown';

export interface PendingMessage {
  id: string;
  terminal: string;
  text: string;
  pictures: string[];
  state: PendingState;
}

function mergeItems(list: PendingMessage[]): { text: string; pictures: string[] } {
  const text = list
    .map((p) => p.text.trim())
    .filter(Boolean)
    .join('\n\n');
  const pictures = Array.from(new Set(list.flatMap((p) => p.pictures || [])));
  return { text, pictures };
}

class ChatOutbox {
  private key = 'rcli-console-pending';
  private listeners = new Set<() => void>();
  private flushing = new Set<string>();
  items: PendingMessage[] = [];

  constructor() {
    try {
      const stored: any[] = JSON.parse(sessionStorage.getItem(this.key) || '[]');
      this.items = (Array.isArray(stored) ? stored : []).map((p) => {
        const raw = String(p?.state || 'waiting');
        const state: PendingState =
          raw === 'sending' ? 'unknown'
            : raw === 'queued' ? 'sent'
              : (raw === 'waiting' || raw === 'paused' || raw === 'sent' || raw === 'unknown') ? raw
                : 'waiting';
        return {
          id: String(p?.id || opId()),
          terminal: String(p?.terminal || ''),
          text: String(p?.text || ''),
          pictures: Array.isArray(p?.pictures) ? p.pictures.map(String) : [],
          state,
        };
      }).filter((p) => p.terminal);
    } catch { /* No stored messages. */ }
  }

  subscribe(listener: () => void) {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  private save(items: PendingMessage[]) {
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

  mark(ids: Set<string>, state: PendingState) {
    if (this.items.some((p) => ids.has(p.id) && p.state !== state))
      this.save(this.items.map((p) => (ids.has(p.id) ? { ...p, state } : p)));
  }

  hasWaiting(terminal: string): boolean {
    return this.items.some((p) => p.terminal === terminal && p.state === 'waiting');
  }

  /** Queues a follow-up locally while a turn runs so stacked messages can be merged and sent in one turn. */
  enqueue(terminal: string, text: string, pictures: string[]) {
    const id = opId();
    this.save([...this.items, { id, terminal, text, pictures: [...pictures], state: 'waiting' }]);
    return id;
  }

  /** Pauses waiting messages (e.g. when user clicks Stop) so they never auto-fire before a new instruction. */
  pauseWaiting(terminal: string) {
    const ids = new Set(this.items.filter((p) => p.terminal === terminal && p.state === 'waiting').map((p) => p.id));
    if (ids.size) this.mark(ids, 'paused');
  }

  /** Marks already-dispatched unconfirmed messages as unknown when interrupted. */
  markInFlightUnknown(terminal: string) {
    const ids = new Set(
      this.items.filter((p) => p.terminal === terminal && (p.state === 'sending' || p.state === 'sent')).map((p) => p.id),
    );
    if (ids.size) this.mark(ids, 'unknown');
  }

  clearDrafts(terminal: string, states: Array<'waiting' | 'paused'> = ['waiting', 'paused']) {
    const allowed = new Set<PendingState>(states);
    const ids = new Set(this.items.filter((p) => p.terminal === terminal && allowed.has(p.state)).map((p) => p.id));
    if (ids.size) this.remove(ids);
  }

  /** Sends one prompt immediately to the backend chat driver. */
  async sendNow(thread: Thread, text: string, pictures: string[]) {
    const terminal = thread.terminal!.id;
    if (
      this.items.some(
        (p) =>
          p.terminal === terminal &&
          (p.state === 'sending' || p.state === 'sent') &&
          p.text === text &&
          p.pictures.join('\n') === pictures.join('\n'),
      )
    ) {
      throw new Error('这条消息仍在等待确认，请先查看发送状态');
    }
    const id = opId();
    this.save([...this.items, { id, terminal, text, pictures: [...pictures], state: 'sending' }]);
    try {
      await say(thread, text, pictures, id);
      if (this.items.some((p) => p.id === id)) this.mark(new Set([id]), 'sent');
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

  /** Merges all waiting (or paused) messages for a terminal into one prompt and dispatches them in a single turn. */
  async flush(thread: Thread, states: Array<'waiting' | 'paused'> = ['waiting']): Promise<boolean> {
    const terminal = thread.terminal?.id;
    if (!terminal || this.flushing.has(terminal)) return false;
    const allowed = new Set<PendingState>(states);
    const batch = this.items.filter((p) => p.terminal === terminal && allowed.has(p.state));
    if (batch.length === 0) return false;

    const merged = mergeItems(batch);
    const batchIds = new Set(batch.map((p) => p.id));
    if (!merged.text && merged.pictures.length === 0) {
      this.remove(batchIds);
      return false;
    }

    this.flushing.add(terminal);
    const id = opId();
    const withoutBatch = this.items.filter((p) => !batchIds.has(p.id));
    this.save([...withoutBatch, { id, terminal, text: merged.text, pictures: merged.pictures, state: 'sending' }]);
    try {
      await say(thread, merged.text, merged.pictures, id);
      if (this.items.some((p) => p.id === id)) this.mark(new Set([id]), 'sent');
      return true;
    } catch (e) {
      if (!this.items.some((p) => p.id === id)) return true; // Echo already arrived.
      if (e instanceof ApiError && e.status < 500) {
        // Restore items as paused so the user never loses stacked prompts on rejection.
        const restored = batch.map((p) => ({ ...p, state: 'paused' as PendingState }));
        this.save([...this.items.filter((p) => p.id !== id), ...restored]);
        throw e;
      }
      this.mark(new Set([id]), 'unknown');
      throw new Error('送达结果未知，请先检查对话');
    } finally {
      this.flushing.delete(terminal);
    }
  }
}

export const chatOutbox = new ChatOutbox();

export function usePending(terminal: string, messages: ChatMessageItem[]) {
  const [, refresh] = useState(0);
  useEffect(() => chatOutbox.subscribe(() => refresh((n) => n + 1)), []);
  useEffect(() => {
    if (terminal && chatOutbox.items.length) {
      const echoed = new Set<string>();
      for (const m of messages) {
        if (m.role !== 'user') continue;
        if (m.op) echoed.add(m.op);
        if (m.ops) m.ops.forEach((op) => echoed.add(op));
      }
      if (echoed.size) chatOutbox.remove(echoed);
    }
  }, [terminal, messages]);
  return chatOutbox.items.filter((p) => p.terminal === terminal);
}
