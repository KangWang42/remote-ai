import type { ChatChunk, ChatMessageItem } from './types';

const MAX_TOOL_OUTPUT_CHARS = 32 * 1024;
const TOOL_HEAD_CHARS = 16 * 1024;
const TOOL_TAIL_CHARS = 14 * 1024;

function boundToolOutput(prev: string, added: string): string {
  if (!added) return prev;
  const combined = prev ? prev + added : added;
  if (combined.length <= MAX_TOOL_OUTPUT_CHARS) return combined;
  return `${combined.slice(0, TOOL_HEAD_CHARS)}\n\n…（中间的输出过长，没有显示）…\n\n${combined.slice(-TOOL_TAIL_CHARS)}`;
}

const DECIDED: Record<string, string> = { allow: '已允许', always: '已总是允许', deny: '已拒绝', gone: '已不再等待' };

/**
 * Folds a chat's events (docs/PROTOCOL.md, "Chats": one JSON object a line, `t` naming the kind) into the
 * conversation's items, as they come. Items that do not change keep their identity, so the timeline only
 * draws again what changed: what is being written at the end.
 */
export class ChatTimelineReducer {
  phase: { state: string; at: number } | null = null;
  private turnStarted = 0;
  private items: ChatMessageItem[] = [];
  private index = new Map<string, number>();
  private lastSeq = 0;
  private rest = '';
  private cache: ChatMessageItem[] = [];
  private dirty = false;

  reset(chunks: ChatChunk[] = []): ChatMessageItem[] {
    this.phase = null;
    this.turnStarted = 0;
    this.items = [];
    this.index.clear();
    this.lastSeq = 0;
    this.rest = '';
    this.cache = [];
    this.dirty = false;
    return this.append(chunks);
  }

  append(chunks: ChatChunk[]): ChatMessageItem[] {
    for (const chunk of chunks || []) {
      if (chunk.seq <= this.lastSeq) continue;
      this.lastSeq = chunk.seq;
      const lines = (this.rest + (chunk.data || '')).split('\n');
      this.rest = lines.pop() || '';
      lines.forEach((line, i) => this.line(line, `${chunk.seq}:${i}`));
    }
    return this.snapshot();
  }

  snapshot(): ChatMessageItem[] {
    if (this.dirty) {
      this.cache = this.items.slice();
      this.dirty = false;
    }
    return this.cache;
  }

  private line(raw: string, key: string) {
    const text = raw.trim();
    if (!text) return;
    try {
      this.event(JSON.parse(text), key);
    } catch {
      // not an event: nothing to show
    }
  }

  private add(item: ChatMessageItem) {
    this.index.set(item.id, this.items.length);
    this.items.push(item);
    this.dirty = true;
  }

  private change(id: string, patch: (item: ChatMessageItem) => Partial<ChatMessageItem>): boolean {
    const at = this.index.get(id);
    if (at === undefined) return false;
    const item = this.items[at];
    this.items[at] = { ...item, ...patch(item) };
    this.dirty = true;
    return true;
  }

  private event(ev: any, key: string) {
    if (!ev || typeof ev !== 'object') return;
    const id = String(ev.id || key);
    switch (ev.t) {
      case 'state': {
        const state = String(ev.state), at = Number(ev.at) || 0;
        if (state === 'busy' && !this.turnStarted) this.turnStarted = at;
        if (state === 'idle' || state === 'ended') this.turnStarted = 0;
        this.phase = { state, at: this.turnStarted || at };
        return;
      }
      case 'history':
        for (const m of parseTranscriptMessages(ev)) this.add({ ...m, id: `h:${key}:${m.id}` });
        return;
      case 'item': {
        const kind = String(ev.kind || '');
        if (kind === 'user') {
          // Queued turns can run back to back without an intervening idle state.
          if (this.phase?.state === 'busy' && !this.turnStarted && ev.at) {
            this.turnStarted = Number(ev.at);
            this.phase = { state: 'busy', at: this.turnStarted };
          }
          this.add({ id, role: 'user', op: ev.op, text: String(ev.text || ''), at: ev.at });
        }
        else if (kind === 'text') this.add({ id, role: 'assistant', text: String(ev.text || ''), at: ev.at });
        else if (kind === 'thought') this.add({ id, role: 'thought', text: String(ev.text || ''), at: ev.at });
        else if (kind === 'step')
          this.add({ id, role: 'tool', title: String(ev.name || ev.text || '操作'), text: String(ev.detail || ev.text || ''), status: 'running', output: '', at: ev.at });
        else if (kind === 'note') this.add({ id, role: 'note', text: String(ev.text || ''), at: ev.at });
        return;
      }
      case 'more':
        if (!this.change(id, (it) => ({ text: it.text + String(ev.text || '') })))
          this.add({ id, role: 'assistant', text: String(ev.text || '') });
        return;
      case 'set':
        if (!this.change(id, () => ({ text: String(ev.text || '') }))) this.add({ id, role: 'assistant', text: String(ev.text || '') });
        return;
      case 'out':
        if (!this.change(id, (it) => ({ output: boundToolOutput(it.output || '', String(ev.text || '')) })))
          this.add({ id, role: 'tool', title: '操作', text: '', status: 'running', output: String(ev.text || '') });
        return;
      case 'done':
        this.change(id, (it) => ({
          status: ev.ok === false ? 'error' : 'done',
          output: ev.result && !it.output ? boundToolOutput('', String(ev.result)) : it.output,
        }));
        return;
      case 'ask':
        this.add({
          id,
          role: 'ask',
          askId: String(ev.id || ''),
          title: String(ev.title || '需要确认'),
          text: String(ev.text || ''),
          options: Array.isArray(ev.options) ? ev.options.map((o: any) => ({ id: String(o.id), label: String(o.label || o.id) })) : [],
          at: ev.at,
        });
        return;
      case 'answered':
        this.change(id, (it) => ({ role: 'note', text: `${it.title || '确认'}：${DECIDED[String(ev.decision || 'gone')] || '已回答'}` }));
        return;
      case 'turn':
        this.turnStarted = 0;
        if (ev.end === 'failed') this.add({ id: key, role: 'error', text: String(ev.error || '这一轮出错了'), at: ev.at });
        else if (ev.end === 'stopped') this.add({ id: key, role: 'note', text: '已停止', at: ev.at });
        return;
      case 'note':
        this.add({ id: key, role: 'note', text: String(ev.text || ''), at: ev.at });
        return;
    }
  }
}

/** The messages `/api/conversation` reads from a saved record (and a chat's `history`): `role`, `text`, `detail`, `result`. */
export function parseTranscriptMessages(reply: any): ChatMessageItem[] {
  const raw = Array.isArray(reply?.messages) ? reply.messages : [];
  return raw.map((m: any, i: number): ChatMessageItem => {
    const role = String(m.role || '').toLowerCase();
    const id = String(m.id || `m${i}`);
    const at = typeof m.at === 'number' ? m.at : undefined;
    if (role === 'tool')
      return { id, role: 'tool', title: String(m.text || '操作'), text: String(m.detail || ''), output: m.result ? boundToolOutput('', String(m.result)) : '', status: 'done', at };
    if (role === 'thought')
      return { id, role: 'thought', text: String(m.text || ''), at };
    if (role === 'note' || role === 'error')
      return { id, role, text: String(m.text || ''), at };
    return { id, role: role === 'user' ? 'user' : 'assistant', text: String(m.text || ''), at };
  });
}
