import { useEffect, useRef, useState } from 'preact/hooks';
import {
  conversationAction,
  fetchTerminalChunks,
  gitCheckpoint,
  readConversation,
  sendTerminalAction,
  terminalEmbedUrl,
} from '../runtime/api';
import { parseChatChunks, parseTranscriptMessages } from '../runtime/chatParser';
import type { ChatChunk, ChatMessageItem, SelectedItem, SessionEntry, TerminalEntry } from '../runtime/types';
import { MarkdownView } from './MarkdownView';

interface Props {
  selected: SelectedItem | null;
  terminal: TerminalEntry | null;
  session: SessionEntry | null;
  onRefresh: () => void;
  onToggleTerminal: () => void;
  onOpenDiff: () => void;
  onNewTask: () => void;
  onSelectTerminal: (id: string) => void;
  terminalDrawerOpen: boolean;
}

export function ChatPane({
  selected,
  terminal,
  session,
  onRefresh,
  onToggleTerminal,
  onOpenDiff,
  onNewTask,
  onSelectTerminal,
  terminalDrawerOpen,
}: Props) {
  const [messages, setMessages] = useState<ChatMessageItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [notice, setNotice] = useState('');
  const [expandedTools, setExpandedTools] = useState<Record<string, boolean>>({});
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const stickBottomRef = useRef(true);
  const chunksRef = useRef<ChatChunk[]>([]);

  const isChatTerminal = terminal != null && terminal.form === 'chat';
  const isRawTerminal = terminal != null && terminal.form !== 'chat';
  const projectDir = terminal?.dir || session?.dir || '';
  const worktreeId = terminal?.worktree || '';

  // Keep user scroll position when reading upward; auto-follow only when pinned near bottom.
  const onTimelineScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    stickBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 90;
  };

  useEffect(() => {
    if (stickBottomRef.current && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [messages.length, messages[messages.length - 1]?.text.length]);

  // Subscribe to structured chat terminal chunks via incremental long-poll.
  useEffect(() => {
    setNotice('');
    setExpandedTools({});
    if (!selected) {
      setMessages([]);
      return;
    }

    if (isRawTerminal) {
      setMessages([]);
      return;
    }

    const controller = new AbortController();
    let cancelled = false;

    if (isChatTerminal && terminal) {
      chunksRef.current = [];
      setLoading(true);
      let after = 0;

      const loop = async () => {
        while (!cancelled) {
          try {
            const reply = await fetchTerminalChunks(terminal.id, after, after === 0 ? 0 : 15, controller.signal);
            if (cancelled) return;
            if (reply.reset) chunksRef.current = reply.chunks || [];
            else if (reply.chunks && reply.chunks.length > 0) chunksRef.current = chunksRef.current.concat(reply.chunks);
            after = reply.after || after;
            setMessages(parseChatChunks(chunksRef.current));
            setLoading(false);
            if (reply.terminal?.state === 'closed') break;
          } catch (err: any) {
            if (cancelled || err?.name === 'AbortError') return;
            setLoading(false);
            await new Promise((r) => window.setTimeout(r, 1500));
          }
        }
      };
      loop();
      return () => {
        cancelled = true;
        controller.abort();
      };
    }

    if (selected.kind === 'session') {
      setLoading(true);
      const loadSession = async () => {
        try {
          const reply = await readConversation(selected.tool, selected.id, selected.dir, selected.folder);
          if (cancelled) return;
          setMessages(parseTranscriptMessages(reply));
        } catch (err: any) {
          if (!cancelled) setNotice(err?.message || '无法读取对话内容');
        } finally {
          if (!cancelled) setLoading(false);
        }
      };
      loadSession();
      const timer = window.setInterval(loadSession, 4000);
      return () => {
        cancelled = true;
        window.clearInterval(timer);
      };
    }

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [selected?.kind, selected?.id, isChatTerminal, isRawTerminal]);

  const handleSend = async (e?: Event) => {
    e?.preventDefault();
    const text = draft.trim();
    if (!text || sending || !selected) return;
    setSending(true);
    setNotice('');
    try {
      if (isChatTerminal && terminal) {
        await sendTerminalAction({ action: 'say', terminal: terminal.id, text });
        setDraft('');
      } else if (selected.kind === 'session') {
        if (session?.live) {
          await conversationAction({
            action: 'say',
            tool: selected.tool,
            session: selected.id,
            dir: selected.dir,
            folder: selected.folder,
            text,
          });
          setDraft('');
          setNotice('已发送给电脑上的原对话');
          onRefresh();
        } else {
          const started = await sendTerminalAction({
            action: 'start',
            tool: selected.tool,
            dir: selected.dir,
            folder: selected.folder,
            session: selected.id,
            form: 'chat',
            history: false,
            text,
          });
          setDraft('');
          if (started?.terminal) onSelectTerminal(started.terminal);
          onRefresh();
        }
      }
    } catch (err: any) {
      setNotice(err?.message || '发送失败');
    } finally {
      setSending(false);
    }
  };

  const handleStop = async () => {
    try {
      if (isChatTerminal && terminal) {
        await sendTerminalAction({ action: 'stop', terminal: terminal.id });
        setNotice('已请求停止本轮回答');
      } else if (selected?.kind === 'session' && session?.live) {
        await conversationAction({
          action: 'stop',
          tool: selected.tool,
          session: selected.id,
          dir: selected.dir,
          folder: selected.folder,
        });
        setNotice('已请求停止');
      }
      onRefresh();
    } catch (err: any) {
      setNotice(err?.message || '停止失败');
    }
  };

  const handleAnswer = async (askId: string, decision: string) => {
    try {
      if (isChatTerminal && terminal) {
        await sendTerminalAction({ action: 'answer', terminal: terminal.id, ask: askId, decision });
      } else if (selected?.kind === 'session') {
        await conversationAction({
          action: 'answer',
          tool: selected.tool,
          session: selected.id,
          dir: selected.dir,
          folder: selected.folder,
          ask: askId,
          allow: decision !== 'deny',
          scope: decision === 'always' ? 'always' : 'once',
        });
      }
      onRefresh();
    } catch (err: any) {
      setNotice(err?.message || '回复确认失败');
    }
  };

  const handleCheckpoint = async () => {
    if (!projectDir) return;
    try {
      const cp = await gitCheckpoint(projectDir, `manual ${new Date().toLocaleTimeString()}`, worktreeId);
      setNotice(`已创建隐藏 Git 快照 ${cp.id}（不污染当前分支）`);
    } catch (err: any) {
      setNotice(err?.message || '创建快照失败');
    }
  };

  if (!selected) {
    return (
      <main class="main-pane empty-stage">
        <div class="empty-card">
          <h2>Remote AI 桌面总控台</h2>
          <p>在左侧选择任意电脑上的 Claude Code、Codex、Antigravity 对话或终端，或按 Ctrl+K 快速跳转。</p>
          <div class="empty-actions">
            <button type="button" class="primary-btn" onClick={onNewTask}>
              + 新建对话 / Worktree 任务
            </button>
            <button type="button" class="ghost-btn" onClick={onOpenDiff}>
              查看工作区 Diff
            </button>
          </div>
        </div>
      </main>
    );
  }

  const title = terminal?.title || session?.title || '对话';
  const tool = terminal?.tool || session?.tool || '';
  const isBusy =
    terminal?.status === 'busy' ||
    terminal?.phase === 'busy' ||
    terminal?.state === 'starting' ||
    session?.status === 'busy';

  return (
    <main class="main-pane">
      <header class="pane-head">
        <div class="pane-title-group">
          <span class={`tool-dot tool-${tool}`} />
          <h1 class="pane-title">{title}</h1>
          {projectDir && <span class="meta-chip">{projectDir}</span>}
          {(terminal?.branch || worktreeId) && (
            <span class="meta-chip branch">{terminal?.branch || `wt:${worktreeId}`}</span>
          )}
          {terminal?.model && <span class="meta-chip">{terminal.model}</span>}
          {session?.origin && <span class="meta-chip">{session.origin}</span>}
        </div>
        <div class="pane-actions">
          {projectDir && (
            <button type="button" class="ghost-btn compact" title="保存隐藏 Git 快照（不污染分支）" onClick={handleCheckpoint}>
              快照
            </button>
          )}
          <button
            type="button"
            class={`ghost-btn compact${terminalDrawerOpen ? ' active' : ''}`}
            onClick={onToggleTerminal}
          >
            终端
          </button>
          <button type="button" class="ghost-btn compact" onClick={onOpenDiff}>
            Diff
          </button>
        </div>
      </header>

      {isRawTerminal && terminal ? (
        <div class="raw-terminal-host">
          <iframe
            key={terminal.id}
            src={terminalEmbedUrl(terminal.id)}
            title={terminal.title}
            class="terminal-iframe"
          />
        </div>
      ) : (
        <>
          <div class="timeline" ref={scrollRef} onScroll={onTimelineScroll}>
            {loading && messages.length === 0 ? (
              <div class="timeline-note">正在加载对话…</div>
            ) : messages.length === 0 ? (
              <div class="timeline-note">还没有消息，在下方输入开始对话。</div>
            ) : (
              messages.map((m, idx) => {
                const isLast = idx === messages.length - 1;
                if (m.role === 'user') {
                  return (
                    <div key={m.id} class="msg msg-user">
                      <div class="msg-bubble">{m.text}</div>
                    </div>
                  );
                }
                if (m.role === 'tool') {
                  const open = !!expandedTools[m.id];
                  return (
                    <div key={m.id} class={`tool-card status-${m.status || 'done'}`}>
                      <button
                        type="button"
                        class="tool-card-head"
                        onClick={() => setExpandedTools((p) => ({ ...p, [m.id]: !p[m.id] }))}
                      >
                        <span class="tool-card-title">{m.title || '工具调用'}</span>
                        <span class="tool-card-summary">{m.text}</span>
                        <span class="caret">{open ? '▾' : '▸'}</span>
                      </button>
                      {open && m.output && <pre class="tool-card-out">{m.output}</pre>}
                    </div>
                  );
                }
                if (m.role === 'ask') {
                  return (
                    <div key={m.id} class="ask-card">
                      <div class="ask-title">{m.title || '等待确认'}</div>
                      <pre class="ask-body">{m.text}</pre>
                      <div class="ask-actions">
                        {(m.options || []).map((opt) => (
                          <button
                            key={opt.id}
                            type="button"
                            class={opt.id === 'deny' ? 'danger-btn compact' : 'primary-btn compact'}
                            onClick={() => handleAnswer(m.askId || m.id, opt.id)}
                          >
                            {opt.label}
                          </button>
                        ))}
                      </div>
                    </div>
                  );
                }
                if (m.role === 'note' || m.role === 'error') {
                  return (
                    <div key={m.id} class={`timeline-note${m.role === 'error' ? ' is-error' : ''}`}>
                      {m.text}
                    </div>
                  );
                }
                return (
                  <div key={m.id} class="msg msg-assistant">
                    <MarkdownView text={m.text} streaming={isBusy && isLast} />
                  </div>
                );
              })
            )}

            {session?.ask && (
              <div class="ask-card">
                <div class="ask-title">电脑上的对话等待确认</div>
                <pre class="ask-body">{session.ask.text}</pre>
                <div class="ask-actions">
                  <button type="button" class="primary-btn compact" onClick={() => handleAnswer(session.ask!.id, 'allow')}>
                    允许一次
                  </button>
                  <button type="button" class="danger-btn compact" onClick={() => handleAnswer(session.ask!.id, 'deny')}>
                    拒绝
                  </button>
                </div>
              </div>
            )}
          </div>

          <form class="composer" onSubmit={handleSend}>
            {notice && <div class="composer-notice">{notice}</div>}
            <div class="composer-box">
              <textarea
                rows={2}
                placeholder={
                  isBusy
                    ? 'AI 正在执行，可随时补充要求（Ctrl+Enter 发送）…'
                    : '输入指令或追问，Ctrl+Enter 发送…'
                }
                value={draft}
                onInput={(e) => setDraft((e.target as HTMLTextAreaElement).value)}
                onKeyDown={(e) => {
                  if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') handleSend(e);
                }}
              />
              <div class="composer-bar">
                <span class="composer-hint">
                  {terminal?.state === 'closed' ? '会话已结束，发送新消息将自动恢复' : isBusy ? '正在执行…' : '就绪'}
                </span>
                <div class="composer-btns">
                  {isBusy && (
                    <button type="button" class="danger-btn compact" onClick={handleStop}>
                      停止
                    </button>
                  )}
                  <button type="submit" class="primary-btn compact" disabled={sending || !draft.trim()}>
                    {sending ? '发送中…' : '发送'}
                  </button>
                </div>
              </div>
            </div>
          </form>
        </>
      )}
    </main>
  );
}
