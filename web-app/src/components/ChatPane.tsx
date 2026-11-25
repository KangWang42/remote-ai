import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
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
import { ChatTimelineReducer, parseTranscriptMessages } from '../runtime/chatParser';
import { ThreadCache } from '../runtime/threadCache';
import { formatExactTime, formatRelativeTime, normalizeMs } from '../runtime/time';
import {
  toolBadgeStyle,
  toolLabel,
  toolMeta,
  toolShort,
  type ChatMessageItem,
  type DeviceInfo,
  type GitBranchEntry,
  type ProjectScriptEntry,
  type SelectedItem,
  type SessionEntry,
  type TerminalEntry,
} from '../runtime/types';
import { VirtualTimeline } from './VirtualTimeline';

interface Props {
  device: DeviceInfo | null;
  selected: SelectedItem | null;
  terminal: TerminalEntry | null;
  session: SessionEntry | null;
  onRefresh: () => void;
  onToggleTerminal: () => void;
  onOpenDiff: () => void;
  onOpenFiles: (dir: string) => void;
  onNewTask: (dir?: string) => void;
  onAddProject: () => void;
  onRunScript: (dir: string, command: string) => void;
  onSelectTerminal: (id: string) => void;
  onToggleFocus: () => void;
  focusMode: boolean;
  terminalDrawerOpen: boolean;
  pendingCite?: { nonce: number; text: string } | null;
  isSplitPane?: boolean;
  onBackToMission?: () => void;
  onSplitCurrent?: () => void;
  onCloseSplit?: () => void;
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
  onOpenFiles,
  onNewTask,
  onAddProject,
  onRunScript,
  onSelectTerminal,
  onToggleFocus,
  focusMode,
  terminalDrawerOpen,
  pendingCite,
  isSplitPane,
  onBackToMission,
  onSplitCurrent,
  onCloseSplit,
}: Props) {
  const [messages, setMessages] = useState<ChatMessageItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [draft, setDraft] = useState('');
  const [attachments, setAttachments] = useState<string[]>([]);
  const [uploading, setUploading] = useState(false);
  const [sending, setSending] = useState(false);
  const [notice, setNotice] = useState('');
  const [scripts, setScripts] = useState<ProjectScriptEntry[]>([]);
  const [branchMenuOpen, setBranchMenuOpen] = useState(false);
  const [branches, setBranches] = useState<GitBranchEntry[]>([]);
  const [currentBranch, setCurrentBranch] = useState('');
  const [newBranchName, setNewBranchName] = useState('');
  const [branchBusy, setBranchBusy] = useState(false);
  const [nowMs, setNowMs] = useState(() => Date.now());
  const [searchOpen, setSearchOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [matchCursor, setMatchCursor] = useState(0);
  const [jumpRequest, setJumpRequest] = useState<{ index: number; nonce: number } | null>(null);

  useEffect(() => {
    const timer = window.setInterval(() => setNowMs(Date.now()), 30000);
    return () => window.clearInterval(timer);
  }, []);

  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const composerRef = useRef<HTMLTextAreaElement | null>(null);
  const searchInputRef = useRef<HTMLInputElement | null>(null);
  const threadCacheRef = useRef<ThreadCache | null>(null);
  if (!threadCacheRef.current) threadCacheRef.current = new ThreadCache(12);
  const threadKey = selected ? `${selected.kind}:${selected.id}` : '';

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

  useEffect(() => {
    if (!pendingCite || !selected) return;
    const next = draft ? `${draft.trimEnd()} ${pendingCite.text} ` : `${pendingCite.text} `;
    updateDraft(next);
    setNotice(`已引用 ${pendingCite.text}`);
    window.setTimeout(() => composerRef.current?.focus(), 40);
  }, [pendingCite?.nonce]);

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

  // Ctrl+F opens in-conversation search across all (including virtualized) messages
  useEffect(() => {
    if (!selected || isRawTerminal) return;
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f') {
        e.preventDefault();
        setSearchOpen(true);
        window.setTimeout(() => searchInputRef.current?.focus(), 30);
      } else if (e.key === 'Escape' && searchOpen) {
        setSearchOpen(false);
        setSearchQuery('');
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [selected?.id, isRawTerminal, searchOpen]);

  const matchIndices = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();
    if (!q) return [];
    const hits: number[] = [];
    for (let i = 0; i < messages.length; i++) {
      const m = messages[i];
      if (
        m.text.toLowerCase().includes(q) ||
        (m.title && m.title.toLowerCase().includes(q)) ||
        (m.output && m.output.toLowerCase().includes(q))
      ) {
        hits.push(i);
      }
    }
    return hits;
  }, [messages, searchQuery]);

  useEffect(() => {
    if (matchIndices.length === 0) {
      setMatchCursor(0);
      return;
    }
    const clamped = Math.min(matchCursor, matchIndices.length - 1);
    setMatchCursor(clamped);
    setJumpRequest({ index: matchIndices[clamped], nonce: Date.now() });
  }, [searchQuery, matchIndices.length]);

  const jumpMatch = (delta: number) => {
    if (matchIndices.length === 0) return;
    const next = (matchCursor + delta + matchIndices.length) % matchIndices.length;
    setMatchCursor(next);
    setJumpRequest({ index: matchIndices[next], nonce: Date.now() });
  };

  useEffect(() => {
    setNotice('');
    setSearchOpen(false);
    setSearchQuery('');
    setJumpRequest(null);
    if (!selected || isRawTerminal) {
      setMessages([]);
      return;
    }

    const cache = threadCacheRef.current!;
    const cached = cache.get(threadKey);
    const controller = new AbortController();
    let cancelled = false;

    if (isChatTerminal && terminal) {
      const reducer = cached?.reducer || new ChatTimelineReducer();
      let after = cached?.after || 0;
      if (cached && cached.messages.length > 0) {
        setMessages(cached.messages);
        setLoading(false);
      } else {
        setMessages([]);
        setLoading(true);
      }

      const loop = async () => {
        while (!cancelled) {
          try {
            const reply = await fetchTerminalChunks(terminal.id, after, after === 0 ? 0 : 15, controller.signal);
            if (cancelled) return;
            const nextMsgs = reply.reset
              ? reducer.reset(reply.chunks || [])
              : reply.chunks && reply.chunks.length > 0
                ? reducer.append(reply.chunks)
                : reducer.snapshot();
            after = reply.after || after;
            cache.upsert(threadKey, { after, reducer, messages: nextMsgs });
            setMessages(nextMsgs);
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
      if (cached && cached.messages.length > 0) {
        setMessages(cached.messages);
        setLoading(false);
      } else {
        setMessages([]);
        setLoading(true);
      }
      const loadSession = async () => {
        try {
          const reply = await readConversation(selected.tool, selected.id, selected.dir, selected.folder);
          if (cancelled) return;
          if (reply?.unsupported) {
            setMessages([]);
            setNotice('该工具暂不支持在线预览历史记录，可直接发消息续聊或在终端中打开');
            return;
          }
          const nextMsgs = parseTranscriptMessages(reply);
          cache.upsert(threadKey, { messages: nextMsgs });
          setMessages(nextMsgs);
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
  }, [threadKey, isChatTerminal, isRawTerminal]);

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

  const CHAT_CAPABLE_TOOLS = new Set(['claude', 'codex', 'agy']);
  const FORK_CAPABLE_TOOLS = new Set(['claude', 'codex']);
  const liveSessionCanSay =
    !session?.live ||
    (Array.isArray(session.reach) && session.reach.includes('say')) ||
    Boolean(session.ways?.say);
  const liveSayWay =
    session?.ways?.say ||
    (session?.transport === 'codex-desktop' || session?.transport === 'agy-desktop' ? session.transport : '');
  const liveStopWay = session?.ways?.stop || session?.transport || 'hook';
  const liveAnswerWay = session?.ways?.answer || (session?.transport === 'agy-desktop' ? 'agy-desktop' : '');
  const liveSayBlocked =
    selected?.kind === 'session' && session?.live && !liveSessionCanSay
      ? session.why?.say || '电脑上的原对话当前无法直接接收手机消息，可接管到当前界面或新开副本继续'
      : '';

  const handleTakeoverSession = async (fork: boolean, firstText = '') => {
    if (!selected || selected.kind !== 'session' || sending) return;
    setSending(true);
    setNotice('');
    try {
      const useChat = !fork && CHAT_CAPABLE_TOOLS.has(selected.tool);
      const payload: Record<string, unknown> = {
        action: 'start',
        tool: selected.tool,
        dir: selected.dir,
        ...(selected.dir === '' && selected.folder ? { folder: selected.folder } : {}),
        session: selected.id,
        ...(fork ? { fork: true } : { takeover: true }),
        ...(useChat ? { form: 'chat' } : {}),
        ...(firstText ? { text: firstText } : {}),
      };
      const started = await sendTerminalAction(payload);
      if (firstText) updateDraft('');
      if (started?.terminal) onSelectTerminal(started.terminal);
      onRefresh();
    } catch (err: any) {
      setNotice(err?.message || (fork ? '新开副本失败' : '接管对话失败'));
    } finally {
      setSending(false);
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
          if (!liveSessionCanSay) {
            setNotice(liveSayBlocked);
            return;
          }
          const payload: Record<string, unknown> = {
            action: 'say',
            tool: selected.tool,
            session: selected.id,
            dir: selected.dir,
            folder: selected.folder,
            text,
          };
          if (liveSayWay === 'codex-desktop' || liveSayWay === 'agy-desktop') {
            payload.transport = liveSayWay;
          }
          await conversationAction(payload);
          updateDraft('');
          setNotice('已发送给电脑上的原对话');
        } else {
          const useChat = CHAT_CAPABLE_TOOLS.has(selected.tool);
          const started = await sendTerminalAction({
            action: 'start',
            tool: selected.tool,
            dir: selected.dir,
            ...(selected.dir === '' && selected.folder ? { folder: selected.folder } : {}),
            session: selected.id,
            ...(useChat ? { form: 'chat' } : {}),
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
        const payload: Record<string, unknown> = {
          action: 'stop',
          tool: selected.tool,
          session: selected.id,
          dir: selected.dir,
          folder: selected.folder,
          transport: liveStopWay,
        };
        if (liveStopWay === 'codex-desktop' && session.turn) {
          payload.turn = session.turn;
        }
        await conversationAction(payload);
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
        const payload: Record<string, unknown> = {
          action: 'answer',
          tool: selected.tool,
          session: selected.id,
          dir: selected.dir,
          folder: selected.folder,
          ask: askId,
          allow: decision !== 'deny',
          scope: decision === 'always' ? 'always' : 'once',
        };
        if (liveAnswerWay === 'agy-desktop') {
          payload.transport = 'agy-desktop';
          if (decision.startsWith('choice:') || decision === 'deny') {
            payload.choice = decision;
          }
        }
        await conversationAction(payload);
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
          <div class="empty-emblem" aria-hidden="true">R</div>
          <h2>Remote AI 总控台</h2>
          <p>
            在一个桌面工作区内统一指挥所有本地 AI 编程工具、隔离 Worktree 并行开发、按需审查 Diff，并随时从手机无缝接管。
          </p>
          {detectedTools.length > 0 && (
            <div class="tool-pill-row">
              {detectedTools.map((t) => {
                const meta = toolMeta(t);
                return (
                  <span key={t} class="chip has-mono" style={{ cursor: 'default' }}>
                    <span class="chip-mono" style={toolBadgeStyle(t)}>
                      {meta.mono}
                    </span>
                    {toolLabel(t)}
                  </span>
                );
              })}
            </div>
          )}
          <div class="empty-actions">
            <button type="button" class="primary-btn" onClick={() => onNewTask()}>
              + 新建 AI 任务
            </button>
            <button type="button" class="ghost-btn" onClick={onAddProject}>
              + 添加项目文件夹
            </button>
            {device?.workspaces?.[0] && (
              <button type="button" class="ghost-btn" onClick={() => onOpenFiles(device.workspaces[0])}>
                预览项目文件
              </button>
            )}
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
  const lastWorkAt = Math.max(
    normalizeMs(terminal?.phase_at),
    normalizeMs(terminal?.created),
    normalizeMs(session?.updated),
  );
  const lastWorkAgo = formatRelativeTime(lastWorkAt, nowMs);
  const lastWorkExact = formatExactTime(lastWorkAt);

  return (
    <main class="main-pane">
      <header class="pane-head">
        <div class="pane-title-group">
          {focusMode && (
            <button type="button" class="icon-btn" title="退出专注模式 (Ctrl+\)" onClick={onToggleFocus}>
              ☰
            </button>
          )}
          <span class="tool-squircle sm" style={toolBadgeStyle(tool)} title={toolShort(tool)}>
            {toolMeta(tool).mono}
          </span>
          <h1 class="pane-title">{title}</h1>
          {projectDir && (
            <button
              type="button"
              class="meta-chip clickable"
              title="点击浏览并预览该项目文件"
              onClick={() => onOpenFiles(projectDir)}
            >
              {projectDir}
            </button>
          )}
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
          {lastWorkAgo && (
            <span class="meta-chip time-chip" title={lastWorkExact ? `上次工作时间：${lastWorkExact}` : undefined}>
              {isBusy ? `活跃 · ${lastWorkAgo}` : `上次工作 ${lastWorkAgo}`}
            </span>
          )}
          {!isRawTerminal && messages.length >= 20 && (
            <span class="meta-chip time-chip">{messages.length} 条记录</span>
          )}
        </div>
        <div class="pane-actions">
          {!isRawTerminal && (
            <button
              type="button"
              class={`ghost-btn compact${searchOpen ? ' active' : ''}`}
              title="在当前对话内查找 (Ctrl+F)"
              onClick={() => {
                const next = !searchOpen;
                setSearchOpen(next);
                if (!next) setSearchQuery('');
                else window.setTimeout(() => searchInputRef.current?.focus(), 30);
              }}
            >
              查找
            </button>
          )}
          <button
            type="button"
            class="primary-btn compact"
            title={projectDir ? `在「${projectDir}」下新建一个 AI 对话 (Ctrl+N)` : '新建 AI 对话 (Ctrl+N)'}
            onClick={() => onNewTask(projectDir || undefined)}
          >
            + 新建 AI
          </button>
          {projectDir && (
            <button type="button" class="ghost-btn compact" title="浏览与预览该项目文件" onClick={() => onOpenFiles(projectDir)}>
              文件
            </button>
          )}
          <button type="button" class="ghost-btn compact" onClick={onOpenDiff}>
            Diff
          </button>
          <button
            type="button"
            class={`ghost-btn compact${terminalDrawerOpen ? ' active' : ''}`}
            onClick={onToggleTerminal}
          >
            终端
          </button>
          {projectDir && (
            <button type="button" class="ghost-btn compact" title="保存隐藏 Git 快照（不污染分支或 stash）" onClick={handleCheckpoint}>
              快照
            </button>
          )}
          {!isSplitPane && onBackToMission && (
            <button type="button" class="ghost-btn compact" title="返回多对话监管总览墙 (Ctrl+0)" onClick={onBackToMission}>
              总览
            </button>
          )}
          {!isSplitPane && onSplitCurrent && (
            <button type="button" class="ghost-btn compact" title="把另一个对话拖到右侧分屏对比" onClick={onSplitCurrent}>
              分屏
            </button>
          )}
          {isSplitPane && onCloseSplit && (
            <button type="button" class="ghost-btn compact" title="关闭此分屏" onClick={onCloseSplit}>
              关闭分屏
            </button>
          )}
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

      {searchOpen && !isRawTerminal && (
        <div class="chat-search-bar">
          <input
            ref={searchInputRef}
            type="search"
            class="chat-search-input"
            placeholder="搜索当前对话消息、工具名或输出内容…"
            value={searchQuery}
            onInput={(e) => setSearchQuery((e.target as HTMLInputElement).value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault();
                jumpMatch(e.shiftKey ? -1 : 1);
              } else if (e.key === 'Escape') {
                setSearchOpen(false);
                setSearchQuery('');
              }
            }}
          />
          <span class="chat-search-count">
            {searchQuery.trim()
              ? matchIndices.length > 0
                ? `${matchCursor + 1} / ${matchIndices.length}`
                : '0 命中'
              : `${messages.length} 条`}
          </span>
          <button type="button" class="icon-btn" disabled={matchIndices.length === 0} onClick={() => jumpMatch(-1)} title="上一条">
            ↑
          </button>
          <button type="button" class="icon-btn" disabled={matchIndices.length === 0} onClick={() => jumpMatch(1)} title="下一条">
            ↓
          </button>
          <button
            type="button"
            class="icon-btn"
            onClick={() => {
              setSearchOpen(false);
              setSearchQuery('');
            }}
            title="关闭查找"
          >
            ✕
          </button>
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
          <VirtualTimeline
            threadKey={threadKey}
            messages={messages}
            loading={loading}
            isBusy={isBusy}
            sessionAsk={session?.ask}
            initialScrollTop={threadCacheRef.current?.get(threadKey)?.scrollTop || 0}
            initialStickBottom={threadCacheRef.current?.get(threadKey)?.stickBottom ?? true}
            highlightIndex={matchIndices.length > 0 ? matchIndices[matchCursor] : null}
            jumpRequest={jumpRequest}
            onScrollStateChange={(st, stick) => threadCacheRef.current?.saveScroll(threadKey, st, stick)}
            onAnswer={handleAnswer}
          />

          <form class="composer" onSubmit={handleSend}>
            <div class="composer-inner">
              {liveSayBlocked && selected?.kind === 'session' && (
                <div class="composer-notice" style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '8px', justifyContent: 'space-between' }}>
                  <span>{liveSayBlocked}</span>
                  <span style={{ display: 'inline-flex', gap: '6px' }}>
                    {session?.can_takeover !== false && (
                      <button
                        type="button"
                        class="ghost-btn compact"
                        disabled={sending}
                        onClick={() => handleTakeoverSession(false, draft.trim())}
                      >
                        接管到当前界面
                      </button>
                    )}
                    {FORK_CAPABLE_TOOLS.has(selected.tool) && (
                      <button
                        type="button"
                        class="ghost-btn compact"
                        disabled={sending}
                        onClick={() => handleTakeoverSession(true)}
                      >
                        新开副本继续
                      </button>
                    )}
                  </span>
                </div>
              )}
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
                  ref={composerRef}
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
