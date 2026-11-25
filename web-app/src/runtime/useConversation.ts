import { useEffect, useRef, useState } from 'preact/hooks';
import { fetchTerminalChunks, readConversation } from './api';
import { ChatTimelineReducer, parseTranscriptMessages } from './chatParser';
import { ThreadCache } from './threadCache';
import type { Thread } from './threads';
import type { ChatMessageItem } from './types';

const cache = new ThreadCache(16);

/** Where a conversation's words are read from now: a chat's events, or the computer's record of it. */
export function sourceOf(t: Thread | null): string {
  if (!t) return '';
  if (t.runtime === 'chat' && t.terminal) return `chat:${t.terminal.id}`;
  if (t.runtime === 'screen') return '';
  return t.session ? `rec:${t.tool}:${t.session}` : '';
}

export interface Conversation {
  source: string;
  messages: ChatMessageItem[];
  loading: boolean;
  /** Why it cannot be read, where it cannot. */
  problem: string;
  scroll: { top: number; stick: boolean };
  saveScroll: (top: number, stick: boolean) => void;
}

/**
 * The messages of a conversation, kept up to date: a chat's events as they come (long-polled, folded by
 * ChatTimelineReducer), a record read again every few seconds. What was read is kept per source, so going back to a
 * conversation shows it at once and where it was scrolled to.
 */
export function useConversation(t: Thread | null): Conversation {
  const source = sourceOf(t);
  const [messages, setMessages] = useState<ChatMessageItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [problem, setProblem] = useState('');
  const thread = useRef(t);
  thread.current = t;

  useEffect(() => {
    setProblem('');
    if (!source) {
      setMessages([]);
      setLoading(false);
      return;
    }
    const kept = cache.get(source);
    setMessages(kept?.messages || []);
    setLoading(!kept || kept.messages.length === 0);
    let alive = true;
    const stop = new AbortController();

    if (source.startsWith('chat:')) {
      const terminal = source.slice(5);
      const reducer = kept?.reducer || new ChatTimelineReducer();
      let after = kept?.after || 0;
      (async () => {
        while (alive) {
          try {
            const reply = await fetchTerminalChunks(terminal, after, after === 0 ? 0 : 15, stop.signal);
            if (!alive) return;
            const next = reply.reset
              ? reducer.reset(reply.chunks || [])
              : reply.chunks?.length
                ? reducer.append(reply.chunks)
                : reducer.snapshot();
            after = reply.after || after;
            cache.upsert(source, { after, reducer, messages: next });
            setMessages(next);
            setLoading(false);
            if (reply.terminal?.state === 'closed') return;
          } catch (err: any) {
            if (!alive || err?.name === 'AbortError') return;
            setLoading(false);
            await new Promise((r) => window.setTimeout(r, 1500));
          }
        }
      })();
    } else {
      const read = async () => {
        const now = thread.current;
        if (!now || !now.session) return;
        try {
          const reply = await readConversation(now.tool, now.session, now.project, now.folder);
          if (!alive) return;
          if (reply?.unsupported) {
            setProblem('这个工具的记录不能在这里阅读');
            setMessages([]);
          } else {
            const next = parseTranscriptMessages(reply);
            cache.upsert(source, { messages: next });
            setMessages(next);
          }
        } catch (err: any) {
          if (alive) setProblem(err?.message || '读不到这段对话');
        } finally {
          if (alive) setLoading(false);
        }
      };
      read();
      const timer = window.setInterval(read, 4000);
      return () => {
        alive = false;
        window.clearInterval(timer);
      };
    }
    return () => {
      alive = false;
      stop.abort();
    };
  }, [source]);

  const kept = cache.get(source);
  return {
    source,
    messages,
    loading,
    problem,
    scroll: { top: kept?.scrollTop || 0, stick: kept?.stickBottom ?? true },
    saveScroll: (top, stick) => cache.saveScroll(source, top, stick),
  };
}
