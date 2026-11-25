import type { ChatChunk, ChatMessageItem } from './types';

/**
 * Parses Remote AI structured chat JSONL event chunks (Chats.cs) into a normalized message timeline.
 * Coalesces sequential assistant text deltas into the active assistant message in O(N) without rebuilding older items.
 */
export function parseChatChunks(chunks: ChatChunk[]): ChatMessageItem[] {
  const items: ChatMessageItem[] = [];
  const toolIndex = new Map<string, number>();

  for (const chunk of chunks) {
    const lines = (chunk.data || '').split('\n');
    for (let li = 0; li < lines.length; li++) {
      const raw = lines[li].trim();
      if (!raw) continue;
      let ev: any;
      try {
        ev = JSON.parse(raw);
      } catch {
        continue;
      }
      if (!ev || typeof ev !== 'object') continue;
      const t = String(ev.t || '');
      const key = `${chunk.seq}:${li}`;

      if (t === 'user' || t === 'you') {
        items.push({ id: ev.id || key, role: 'user', text: String(ev.text || ''), at: ev.at });
      } else if (t === 'text' || t === 'delta' || t === 'say' || t === 'assistant') {
        const piece = String(ev.text || ev.delta || '');
        if (!piece) continue;
        const last = items[items.length - 1];
        if (last && last.role === 'assistant' && t !== 'say') {
          last.text += piece;
        } else {
          items.push({ id: ev.id || key, role: 'assistant', text: piece, at: ev.at });
        }
      } else if (t === 'tool' || t === 'step' || t === 'call') {
        const callId = String(ev.id || key);
        const existingIdx = toolIndex.get(callId);
        const title = String(ev.name || ev.title || ev.tool || '执行步骤');
        const text = String(ev.summary || ev.input || ev.text || '');
        const output = String(ev.output || '');
        const status = String(ev.status || (ev.done ? 'done' : 'running'));
        if (existingIdx !== undefined) {
          const prev = items[existingIdx];
          items[existingIdx] = {
            ...prev,
            title: title || prev.title,
            text: text || prev.text,
            output: output ? (prev.output ? prev.output + output : output) : prev.output,
            status,
          };
        } else {
          toolIndex.set(callId, items.length);
          items.push({ id: callId, role: 'tool', title, text, output, status, at: ev.at });
        }
      } else if (t === 'ask') {
        const options = Array.isArray(ev.options)
          ? ev.options.map((o: any) => ({ id: String(o.id || ''), label: String(o.label || o.id || '') }))
          : [
              { id: 'allow', label: '允许一次' },
              { id: 'deny', label: '拒绝' },
            ];
        items.push({
          id: ev.id || key,
          role: 'ask',
          askId: String(ev.id || ''),
          title: String(ev.title || '需要确认'),
          text: String(ev.text || ev.command || ''),
          options,
          at: ev.at,
        });
      } else if (t === 'note') {
        items.push({ id: key, role: 'note', text: String(ev.text || ''), at: ev.at });
      } else if (t === 'error') {
        items.push({ id: key, role: 'error', text: String(ev.text || ev.error || '发生错误'), at: ev.at });
      }
    }
  }
  return items;
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
      output: m.output ? String(m.output) : undefined,
      status: m.status ? String(m.status) : undefined,
      at: typeof m.at === 'number' ? m.at : undefined,
    });
  });
  return out;
}
