import type { ChatChunk, ChatMessageItem } from './types';

const MAX_TOOL_OUTPUT_CHARS = 32 * 1024;
const TOOL_HEAD_CHARS = 16 * 1024;
const TOOL_TAIL_CHARS = 14 * 1024;

function boundToolOutput(prev: string, added: string): string {
  if (!added) return prev;
  const combined = prev ? prev + added : added;
  if (combined.length <= MAX_TOOL_OUTPUT_CHARS) return combined;
  const head = combined.slice(0, TOOL_HEAD_CHARS);
  const tail = combined.slice(-TOOL_TAIL_CHARS);
  return `${head}\n\n…（输出过长，已裁剪中间片段以保持流畅）…\n\n${tail}`;
}

/**
 * Multi-instance O(1) incremental reducer for Remote AI structured chat JSONL streams.
 * Preserves referential identity (`===`) of all previously finished ChatMessageItem objects so
 * memoized virtualized rows never re-render when new streaming deltas arrive at the tail.
 */
export class ChatTimelineReducer {
  private items: ChatMessageItem[] = [];
  private toolIndex = new Map<string, number>();
  private lastSeq = 0;
  private lineRest = '';
  private snapshotCache: ChatMessageItem[] = [];
  private dirty = false;

  reset(chunks: ChatChunk[] = []): ChatMessageItem[] {
    this.items = [];
    this.toolIndex.clear();
    this.lastSeq = 0;
    this.lineRest = '';
    this.snapshotCache = [];
    this.dirty = false;
    if (chunks.length > 0) this.append(chunks);
    return this.snapshot();
  }

  append(chunks: ChatChunk[]): ChatMessageItem[] {
    if (!chunks || chunks.length === 0) return this.snapshot();
    for (const chunk of chunks) {
      if (chunk.seq <= this.lastSeq) continue;
      this.lastSeq = chunk.seq;
      const combined = this.lineRest + (chunk.data || '');
      const endsWithNewline = combined.endsWith('\n');
      const lines = combined.split('\n');
      this.lineRest = endsWithNewline ? '' : lines.pop() || '';

      for (let li = 0; li < lines.length; li++) {
        this.applyLine(lines[li], `${chunk.seq}:${li}`);
      }
      // If a single JSON event arrived without a trailing newline (e.g. mock/synthetic chunk), parse immediately.
      if (this.lineRest.trim().startsWith('{') && this.lineRest.trim().endsWith('}')) {
        if (this.tryApplyCompleteLine(this.lineRest, `${chunk.seq}:tail`)) {
          this.lineRest = '';
        }
      }
    }
    return this.snapshot();
  }

  snapshot(): ChatMessageItem[] {
    if (this.dirty) {
      this.snapshotCache = this.items.slice();
      this.dirty = false;
    }
    return this.snapshotCache;
  }

  get seq(): number {
    return this.lastSeq;
  }

  private tryApplyCompleteLine(raw: string, key: string): boolean {
    const trimmed = raw.trim();
    if (!trimmed) return true;
    let ev: any;
    try {
      ev = JSON.parse(trimmed);
    } catch {
      return false;
    }
    this.applyEvent(ev, key);
    return true;
  }

  private applyLine(raw: string, key: string) {
    const trimmed = raw.trim();
    if (!trimmed) return;
    let ev: any;
    try {
      ev = JSON.parse(trimmed);
    } catch {
      return;
    }
    this.applyEvent(ev, key);
  }

  private applyEvent(ev: any, key: string) {
    if (!ev || typeof ev !== 'object') return;
    const t = String(ev.t || ev.kind || '');

    if (t === 'user' || t === 'you') {
      this.items.push({ id: String(ev.id || key), role: 'user', text: String(ev.text || ''), at: ev.at });
      this.dirty = true;
      return;
    }

    if (t === 'text' || t === 'delta' || t === 'say' || t === 'assistant') {
      const piece = String(ev.text || ev.delta || '');
      if (!piece) return;
      const lastIdx = this.items.length - 1;
      const last = lastIdx >= 0 ? this.items[lastIdx] : null;
      if (last && last.role === 'assistant' && t !== 'say') {
        // Clone only the active tail item so memoized earlier items stay referentially equal.
        this.items[lastIdx] = { ...last, text: last.text + piece };
      } else {
        this.items.push({ id: String(ev.id || key), role: 'assistant', text: piece, at: ev.at });
      }
      this.dirty = true;
      return;
    }

    if (t === 'tool' || t === 'step' || t === 'call') {
      const callId = String(ev.id || key);
      const existingIdx = this.toolIndex.get(callId);
      const title = String(ev.name || ev.title || ev.tool || '执行步骤');
      const text = String(ev.summary || ev.detail || ev.input || ev.text || '');
      const output = String(ev.output || '');
      const status = String(ev.status || (ev.done ? 'done' : 'running'));
      if (existingIdx !== undefined) {
        const prev = this.items[existingIdx];
        this.items[existingIdx] = {
          ...prev,
          title: title || prev.title,
          text: text || prev.text,
          output: boundToolOutput(prev.output || '', output),
          status,
        };
      } else {
        this.toolIndex.set(callId, this.items.length);
        this.items.push({
          id: callId,
          role: 'tool',
          title,
          text,
          output: boundToolOutput('', output),
          status,
          at: ev.at,
        });
      }
      this.dirty = true;
      return;
    }

    if (t === 'ask') {
      const options = Array.isArray(ev.options)
        ? ev.options.map((o: any) => ({ id: String(o.id || ''), label: String(o.label || o.id || '') }))
        : [
            { id: 'allow', label: '允许一次' },
            { id: 'deny', label: '拒绝' },
          ];
      this.items.push({
        id: String(ev.id || key),
        role: 'ask',
        askId: String(ev.id || ''),
        title: String(ev.title || '需要确认'),
        text: String(ev.text || ev.command || ''),
        options,
        at: ev.at,
      });
      this.dirty = true;
      return;
    }

    if (t === 'note') {
      this.items.push({ id: key, role: 'note', text: String(ev.text || ''), at: ev.at });
      this.dirty = true;
      return;
    }

    if (t === 'error') {
      this.items.push({ id: key, role: 'error', text: String(ev.text || ev.error || '发生错误'), at: ev.at });
      this.dirty = true;
    }
  }
}

/** Convenience wrapper parsing a complete chunk list via ChatTimelineReducer. */
export function parseChatChunks(chunks: ChatChunk[]): ChatMessageItem[] {
  return new ChatTimelineReducer().reset(chunks);
}

/** Normalizes /api/conversation read response into the same ChatMessageItem timeline. */
export function parseTranscriptMessages(reply: any): ChatMessageItem[] {
  const out: ChatMessageItem[] = [];
  const raw = Array.isArray(reply?.messages) ? reply.messages : [];
  raw.forEach((m: any, idx: number) => {
    const roleRaw = String(m.role || m.who || '').toLowerCase();
    const role: ChatMessageItem['role'] =
      roleRaw === 'user' || roleRaw === 'you'
        ? 'user'
        : roleRaw === 'tool' || roleRaw === 'step'
          ? 'tool'
          : 'assistant';
    out.push({
      id: String(m.id || `hist-${idx}`),
      role,
      title: m.title ? String(m.title) : undefined,
      text: String(m.text || m.body || ''),
      output: m.output ? boundToolOutput('', String(m.output)) : undefined,
      status: m.status ? String(m.status) : undefined,
      at: typeof m.at === 'number' ? m.at : undefined,
    });
  });
  return out;
}
