import { useEffect, useRef, useState } from 'preact/hooks';
import {
  conversationAction,
  fetchProjectScripts,
  fetchTerminalChunks,
  gitBranches,
  gitCheckout,
  gitCheckpoint,
  postDesktopMessage,
  readConversation,
  sendTerminalAction,
  terminalEmbedUrl,
  uploadImageAttachment,
} from '../runtime/api';
import { parseChatChunks, parseTranscriptMessages } from '../runtime/chatParser';
import {
  toolColor,
  toolLabel,
  toolShort,
  type ChatChunk,
  type ChatMessageItem,
  type DeviceInfo,
  type GitBranchEntry,
  type ProjectScriptEntry,
  type SelectedItem,
  type SessionEntry,
  type TerminalEntry,
} from '../runtime/types';
import { MarkdownView } from './MarkdownView';

interface Props {
  device: DeviceInfo | null;
  selected: SelectedItem | null;
  terminal: TerminalEntry | null;
  session: SessionEntry | null;
  onRefresh: () => void;
  onToggleTerminal: () => void;
  onOpenDiff: () => void;
  onNewTask: () => void;
  onAddProject: () => void;
  onRunScript: (dir: string, command: string) => void;
  onSelectTerminal: (id: string) => void;
  onToggleFocus: () => void;
  focusMode: boolean;
  terminalDrawerOpen: boolean;
}

function draftKey(selected: SelectedItem | null): string {
  if (!selected) return '';
  return `rcli-draft:${selected.kind}:${selected.id}`;
}

export function ChatPane({
  device,
  selected,
  terminal,
  session,
  onRefresh,
  onToggleTerminal,
  onOpenDiff,
  onNewTask,
  onAddProject,
  onRunScript,
  onSelectTerminal,
  onToggleFocus,
  focusMode,
  terminalDrawerOpen,
}: Props) {
  const [messages, setMessages] = useState<ChatMessageItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [draft, setDraft] = useState('');
  const [attachments, setAttachments] = useState<string[]>([]);
  const [uploading, setUploading] = useState(false);
  const [sending, setSending] = useState(false);
  const [notice, setNotice] = useState('');
  const [expandedTools, setExpandedTools] = useState<Record<string, boolean>>({});
  const [scripts, setScripts] = useState<ProjectScriptEntry[]>([]);
  const [branchMenuOpen, setBranchMenuOpen] = useState(false);
  const [branches, setBranches] = useState<GitBranchEntry[]>([]);
  const [currentBranch, setCurrentBranch] = useState('');
  const [newBranchName, setNewBranchName] = useState('');
  const [branchBusy, setBranchBusy] = useState(false);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const stickBottomRef = useRef(true);
  const chunksRef = useRef<ChatChunk[]>([]);

  const isChatTerminal = terminal != null && terminal.form === 'chat';
  const isRawTerminal = terminal != null && terminal.form !== 'chat';
  const projectDir = terminal?.dir || session?.dir || '';
  const worktreeId = terminal?.worktree || '';
  const supportsAttachments = (device?.features || []).includes('attachments') && !!projectDir;

  // Restore saved draft per conversation/terminal so switching threads never loses typed prompts.
  useEffect(() => {
    const key = draftKey(selected);
    if (!key) {
      setDraft('');
      setAttachments([]);
      return;
    }
    try {
      setDraft(sessionStorage.getItem(key) || '');
    } catch {
      setDraft('');
    }
    setAttachments([]);
    setBranchMenuOpen(false);
  }, [selected?.kind, selected?.id]);

  const updateDraft = (val: string) => {
    setDraft(val);
    const key = draftKey(selected);
    if (!key) return;
    try {
      if (val) sessionStorage.setItem(key, val);
      else sessionStorage.removeItem(key);
    } catch {
      // ignore
    }
  };

  // Load runnable project scripts (package.json / Makefile / Cargo / pyproject)
  useEffect(() => {
    if (!projectDir) {
      setScripts([]);
      setCurrentBranch('');
      return;
    }
    let alive = true;
    fetchProjectScripts(projectDir, worktreeId)
      .then((res) => {
        if (alive) setScripts(res.scripts || []);
      })
      .catch(() => {
        if (alive) setScripts([]);
      });
    gitBranches(projectDir, worktreeId)
      .then((res) => {
        if (alive) {
          setCurrentBranch(res.current || '');
          setBranches(res.branches || []);
        }
      })
      .catch(() => {});
    return () => {
      alive = false;
    };
  }, [projectDir, worktreeId]);

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

  useEffect(() => {
    setNotice('');
    setExpandedTools({});
    if (!selected || isRawTerminal) {
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

  const handleAttachFiles = async (fileList: FileList | File[]) => {
    if (!supportsAttachments || !projectDir) return;
    const files = Array.from(fileList).filter((f) => f.type.startsWith('image/')).slice(0, 4 - attachments.length);
    if (files.length === 0) return;
    setUploading(true);
    setNotice('');
    try {
      const added: string[] = [];
      for (const f of files) {
        const path = await uploadImageAttachment(projectDir, f);
        added.push(path);
      }
      setAttachments((prev) => [...prev, ...added].slice(0, 4));
    } catch (err: any) {
      setNotice(err?.message || '图片上传失败');
    } finally {
      setUploading(false);
    }
  };

  const handlePaste = (e: ClipboardEvent) => {
    if (!supportsAttachments || !e.clipboardData) return;
    const imgs: File[] = [];
    for (const item of Array.from(e.clipboardData.items)) {
      if (item.type.startsWith('image/')) {
        const f = item.getAsFile();
        if (f) imgs.push(f);
      }
    }
    if (imgs.length > 0) {
      e.preventDefault();
      handleAttachFiles(imgs);
    }
  };

  const handleSend = async (e?: Event) => {
    e?.preventDefault();
    const text = draft.trim();
    if ((!text && attachments.length === 0) || sending || !selected) return;
    setSending(true);
    setNotice('');
    try {
      if (isChatTerminal && terminal) {
        await sendTerminalAction({
          action: 'say',
          terminal: terminal.id,
          text,
          ...(attachments.length ? { attachments } : {}),
        });
        updateDraft('');
        setAttachments([]);
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
          updateDraft('');
          setNotice('已发送给电脑上的原对话');
        } else {
          const started = await sendTerminalAction({
            action: 'start',
            tool: selected.tool,
            dir: selected.dir,
            session: selected.id,
            form: 'chat',
            text,
          });
          updateDraft('');
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
      } else if (selected?.kind === 'session' && session?.live) {
        await conversationAction({
          action: 'stop',
          tool: selected.tool,
          session: selected.id,
          dir: selected.dir,
          folder: selected.folder,
        });
      }
      setNotice('已发送停止请求');
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
      setNotice('已提交确认');
      onRefresh();
    } catch (err: any) {
      setNotice(err?.message || '提交确认失败');
    }
  };

  const handleCheckpoint = async () => {
    if (!projectDir) return;
    try {
      const res = await gitCheckpoint(projectDir, `manual-${new Date().toLocaleTimeString()}`, worktreeId);
      setNotice(`已保存隐藏快照 ${res.ref || res.commit || ''}`);
    } catch (err: any) {
      setNotice(err?.message || '保存快照失败');
    }
  };

  const handleBranchSwitch = async (name: string, create = false) => {
    if (!projectDir || !name.trim() || branchBusy) return;
    setBranchBusy(true);
    try {
      const res = await gitCheckout(projectDir, name.trim(), create, worktreeId);
      setCurrentBranch(res.branch || name.trim());
      setNewBranchName('');
      setBranchMenuOpen(false);
      setNotice(`已切换到分支 ${res.branch || name.trim()}`);
      const list = await gitBranches(projectDir, worktreeId);
      setBranches(list.branches || []);
    } catch (err: any) {
      setNotice(err?.message || '切换分支失败');
    } finally {
      setBranchBusy(false);
    }
  };

  if (!selected) {
    const detectedTools = (device?.tools || []).filter((t) => t !== 'shell');
    const isDesktop = typeof (window as any).chrome?.webview?.postMessage === 'function';
    return (
      <main class="main-pane empty-stage">
        <div class="empty-card">
          <h2>Remote AI 总控台</h2>
          <p>
            在一个桌面工作区内统一指挥所有本地 AI 编程工具、隔离 Worktree 并行开发、按需审查 Diff，并随时从手机无缝接管。
          </p>
          {detectedTools.length > 0 && (
            <div class="tool-pill-row">
              {detectedTools.map((t) => (
                <span key={t} class="meta-chip">
                  <span class="tool-dot" style={{ background: toolColor(t) }} />
                  {toolLabel(t)}
                </span>
              ))}
            </div>
          )}
          <div class="empty-actions">
            <button type="button" class="primary-btn" onClick={onNewTask}>
              + 新建 AI 任务
            </button>
            <button type="button" class="ghost-btn" onClick={onAddProject}>
              + 添加项目文件夹
            </button>
            <button type="button" class="ghost-btn" onClick={onToggleTerminal}>
              终端抽屉 (Ctrl+J)
            </button>
            {isDesktop && (
              <button type="button" class="ghost-btn" onClick={() => postDesktopMessage('open-classic')}>
                手机扫码连接
              </button>
            )}
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
  const shownBranch = terminal?.branch || currentBranch || (worktreeId ? `wt:${worktreeId}` : '');

  return (
    <main class="main-pane">
      <header class="pane-head">
        <div class="pane-title-group">
          {focusMode && (
            <button type="button" class="icon-btn" title="退出专注模式 (Ctrl+\)" onClick={onToggleFocus}>
              ☰
            </button>
          )}
          <span class="tool-dot" style={{ background: toolColor(tool) }} title={toolShort(tool)} />
          <h1 class="pane-title">{title}</h1>
          {projectDir && <span class="meta-chip">{projectDir}</span>}
          {projectDir && shownBranch && (
            <div class="branch-wrap">
              <button
                type="button"
                class="meta-chip branch"
                title="点击切换或新建 Git 分支"
                onClick={async () => {
                  const next = !branchMenuOpen;
                  setBranchMenuOpen(next);
                  if (next) {
                    try {
                      const res = await gitBranches(projectDir, worktreeId);
                      setBranches(res.branches || []);
                      setCurrentBranch(res.current || '');
                    } catch {
                      // ignore
                    }
                  }
                }}
              >
                ⎇ {shownBranch} ▾
              </button>
              {branchMenuOpen && (
                <div class="branch-popover">
                  <div class="branch-create-row">
                    <input
                      type="text"
                      placeholder="新分支名…"
                      value={newBranchName}
                      onInput={(e) => setNewBranchName((e.target as HTMLInputElement).value)}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') {
                          e.preventDefault();
                          handleBranchSwitch(newBranchName, true);
                        }
                      }}
                    />
                    <button
                      type="button"
                      class="primary-btn compact"
                      disabled={branchBusy || !newBranchName.trim()}
                      onClick={() => handleBranchSwitch(newBranchName, true)}
                    >
                      新建
                    </button>
                  </div>
                  <div class="branch-list">
                    {branches.map((b) => (
                      <button
                        key={b.name}
                        type="button"
                        class={`branch-item${b.current ? ' current' : ''}`}
                        disabled={branchBusy || b.current}
                        onClick={() => handleBranchSwitch(b.name, false)}
                      >
                        <span>{b.name}</span>
                        <span class="muted-code">{b.head}</span>
                      </button>
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}
          {terminal?.model && <span class="meta-chip">{terminal.model}</span>}
          {terminal?.mode && <span class="meta-chip">{terminal.mode === 'read' ? 'Plan 只读' : terminal.mode === 'edit' ? '自动编辑' : '完整权限'}</span>}
          {session?.origin && <span class="meta-chip">{session.origin}</span>}
        </div>
        <div class="pane-actions">
          {projectDir && (
            <button type="button" class="ghost-btn compact" title="保存隐藏 Git 快照（不污染分支或 stash）" onClick={handleCheckpoint}>
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

      {projectDir && scripts.length > 0 && (
        <div class="scripts-bar">
          <span class="scripts-label">脚本</span>
          {scripts.slice(0, 10).map((s) => (
            <button
              key={`${s.source}:${s.name}`}
              type="button"
              class="script-pill"
              title={`${s.command}${s.detail ? ` — ${s.detail}` : ''}`}
              onClick={() => onRunScript(projectDir, s.command)}
            >
              ▶ {s.name}
            </button>
          ))}
        </div>
      )}

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
            <div class="composer-inner">
              {notice && <div class="composer-notice">{notice}</div>}
              {attachments.length > 0 && (
                <div class="attach-strip">
                  {attachments.map((p) => (
                    <span key={p} class="attach-chip">
                      🖼 {p.split('/').pop()}
                      <button type="button" onClick={() => setAttachments((prev) => prev.filter((x) => x !== p))}>
                        ×
                      </button>
                    </span>
                  ))}
                </div>
              )}
              <div class="composer-box">
                <textarea
                  rows={2}
                  placeholder={
                    isBusy
                      ? 'AI 正在执行，可随时补充要求或粘贴截图（Ctrl+Enter 发送）…'
                      : '输入指令或追问，支持 Ctrl+V 粘贴截图，Ctrl+Enter 发送…'
                  }
                  value={draft}
                  onPaste={handlePaste as any}
                  onInput={(e) => updateDraft((e.target as HTMLTextAreaElement).value)}
                  onKeyDown={(e) => {
                    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') handleSend(e);
                  }}
                />
                <div class="composer-bar">
                  <div class="composer-left">
                    {supportsAttachments && isChatTerminal && (
                      <>
                        <input
                          ref={fileInputRef}
                          type="file"
                          accept="image/png,image/jpeg,image/webp,image/gif"
                          multiple
                          style={{ display: 'none' }}
                          onChange={(e) => {
                            const files = (e.target as HTMLInputElement).files;
                            if (files) handleAttachFiles(files);
                            (e.target as HTMLInputElement).value = '';
                          }}
                        />
                        <button
                          type="button"
                          class="icon-btn"
                          disabled={uploading || attachments.length >= 4}
                          title="添加截图附件（也可 Ctrl+V 粘贴）"
                          onClick={() => fileInputRef.current?.click()}
                        >
                          {uploading ? '上传中…' : '📎 附图'}
                        </button>
                      </>
                    )}
                    <span class="composer-hint">
                      {terminal?.state === 'closed' ? '会话已结束，发送新消息将自动恢复' : isBusy ? '正在执行…' : '草稿自动保存'}
                    </span>
                  </div>
                  <div class="composer-btns">
                    {isBusy && (
                      <button type="button" class="danger-btn compact" onClick={handleStop}>
                        停止
                      </button>
                    )}
                    <button
                      type="submit"
                      class="primary-btn compact"
                      disabled={sending || uploading || (!draft.trim() && attachments.length === 0)}
                    >
                      {sending ? '发送中…' : '发送'}
                    </button>
                  </div>
                </div>
              </div>
            </div>
          </form>
        </>
      )}
    </main>
  );
}
