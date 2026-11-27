import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
import * as act from '../runtime/actions';
import { terminalEmbedUrl } from '../runtime/api';
import { chatOutbox, usePending } from '../runtime/chatOutbox';
import { normalizeMs } from '../runtime/time';
import { canReopen, canSay, canStop, canTakeOver, type Thread } from '../runtime/threads';
import { useConversation } from '../runtime/useConversation';
import { toolBadgeStyle, toolLabel, toolMeta, type DeviceInfo } from '../runtime/types';
import { BranchMenu } from './BranchMenu';
import { Composer } from './Composer';
import { Dots } from './Sidebar';
import { VirtualTimeline } from './VirtualTimeline';
import { ToolSwitch, type PendingSwitch } from './ToolSwitch';
import { RunStatus } from './RunStatus';

interface Props {
  thread: Thread;
  features: string[];
  device: DeviceInfo | null;
  inspectorOpen: boolean;
  terminalOpen: boolean;
  insert?: { nonce: number; text: string } | null;
  split?: boolean;
  onMenu: (e: MouseEvent) => void;
  onReopen: (to: 'chat' | 'screen') => Promise<void>;
  onTakeOver: (text?: string) => Promise<void>;
  onHandOff: () => void;
  /** A new conversation of another AI was begun from this one (the switcher in the box): its terminal. */
  onHandedTo: (terminal: string) => void;
  onOpenFiles: () => void;
  onToggleInspector: () => void;
  onToggleTerminal: () => void;
  onCloseSplit?: () => void;
  onChanged: () => void;
}

/** One conversation: what was said and done in it, and saying more, however it runs now. */
export function ThreadView(p: Props) {
  const t = p.thread;
  const conv = useConversation(t);
  const pending = usePending(t.terminal?.id || '', conv.messages);
  const queued = t.record?.queued || [];
  const livePhase = t.runtime === 'chat' ? conv.phase : null;
  const phase = t.runtime === 'idle' ? (t.needs === 'failed' ? 'failed' : 'ended') :
    livePhase?.state || (t.needs === 'confirm' ? 'confirm' : t.terminal?.state === 'starting' ? 'starting' : t.busy ? 'busy' : 'idle');
  const since = normalizeMs(t.runtime === 'elsewhere' ? conv.asked : livePhase?.at || t.terminal?.phase_at);
  const working = phase === 'busy';
  const [searching, setSearching] = useState(false);
  const [query, setQuery] = useState('');
  const [cursor, setCursor] = useState(0);
  const [jump, setJump] = useState<{ index: number; nonce: number } | null>(null);
  const [switching, setSwitching] = useState('');
  const [problem, setProblem] = useState('');
  const [stoppingNow, setStoppingNow] = useState(false);
  const [flushingNow, setFlushingNow] = useState(false);
  const [recallInsert, setRecallInsert] = useState<{ nonce: number; text: string } | null>(null);
  const [pendingSwitch, setPendingSwitch] = useState<PendingSwitch | null>(null);
  const searchBox = useRef<HTMLInputElement | null>(null);

  const waitingPending = pending.filter((m) => m.state === 'waiting');
  const pausedPending = pending.filter((m) => m.state === 'paused');
  const inflightPending = pending.filter((m) => m.state === 'sending' || m.state === 'sent' || m.state === 'unknown');
  const composerInsert = useMemo(() => {
    if (!p.insert) return recallInsert;
    if (!recallInsert) return p.insert;
    return recallInsert.nonce > p.insert.nonce ? recallInsert : p.insert;
  }, [p.insert, recallInsert]);

  useEffect(() => {
    setSearching(false);
    setQuery('');
    setProblem('');
    setStoppingNow(false);
    setRecallInsert(null);
    setPendingSwitch(null);
  }, [t.key]);

  useEffect(() => {
    if (phase === 'idle' || phase === 'ended' || phase === 'failed') {
      setStoppingNow(false);
    }
  }, [phase]);

  // Automatically merge & send accumulated waiting messages in one turn as soon as the current turn ends.
  useEffect(() => {
    if (t.runtime !== 'chat' || !t.terminal || phase !== 'idle') return;
    if (!chatOutbox.hasWaiting(t.terminal.id)) return;
    chatOutbox
      .flush(t, ['waiting'])
      .then((flushed) => {
        if (flushed) p.onChanged();
      })
      .catch((err: any) => setProblem(err?.message || '合并发送待发消息失败'));
  }, [t.key, t.runtime, t.terminal?.id, phase, waitingPending.length]);

  useEffect(() => {
    if (t.runtime === 'screen') return;
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f' && !p.split) {
        e.preventDefault();
        setSearching(true);
        window.setTimeout(() => searchBox.current?.focus(), 20);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [t.key, t.runtime, p.split]);

  const hits = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return [] as number[];
    const out: number[] = [];
    conv.messages.forEach((m, i) => {
      if ([m.text, m.title, m.output].some((s) => (s || '').toLowerCase().includes(q))) out.push(i);
    });
    return out;
  }, [conv.messages, query]);

  useEffect(() => {
    if (hits.length === 0) return;
    const at = Math.min(cursor, hits.length - 1);
    setCursor(at);
    setJump({ index: hits[at], nonce: Date.now() });
  }, [query, hits.length]);

  const step = (d: number) => {
    if (hits.length === 0) return;
    const next = (cursor + d + hits.length) % hits.length;
    setCursor(next);
    setJump({ index: hits[next], nonce: Date.now() });
  };

  const reopen = canReopen(t, p.features);
  const say = canSay(t);
  const stop = canStop(t);
  const takeOver = canTakeOver(t);
  const meta = toolMeta(t.tool);

  const switchTo = async (to: 'chat' | 'screen') => {
    if (switching) return;
    setSwitching(to);
    setProblem('');
    try {
      await p.onReopen(to);
    } catch (err: any) {
      setProblem(err?.message || '切换失败');
    } finally {
      setSwitching('');
    }
  };

  const viewSwitch =
    t.runtime === 'elsewhere' ? (
      <span class="chip soft" title="这段对话开在电脑的其它窗口里">
        {t.place}
      </span>
    ) : ['claude', 'codex'].includes(t.tool) ? (
      <div class="seg" role="tablist" aria-label="显示方式">
        {(['chat', 'screen'] as const).map((v) => {
          const on = t.runtime === v || (t.runtime === 'idle' && v === 'chat');
          const allowed = !on && (t.runtime === 'idle' ? v === 'screen' : reopen.ok && reopen.to === v);
          return (
            <button
              key={v}
              type="button"
              role="tab"
              aria-selected={on}
              class={on ? 'on' : ''}
              disabled={Boolean(switching) || (!on && !allowed)}
              title={on ? undefined : allowed ? (v === 'chat' ? '以对话方式继续这段对话' : `在 ${meta.label} 自己的终端界面里继续`) : reopen.why}
              onClick={() => !on && allowed && switchTo(v)}
            >
              {switching === v ? '切换中…' : v === 'chat' ? '对话' : '终端'}
            </button>
          );
        })}
      </div>
    ) : null;

  const composerDisabled = !say.ok ? say.why : '';
  const canStopChat = stop.ok || t.runtime === 'chat';
  const queueMode = t.runtime === 'chat' && (working || phase === 'starting') && !stoppingNow;

  const stopIgnoringIdle = async () => {
    try {
      await act.stop(t);
    } catch (err: any) {
      const msg = String(err?.message || '');
      if (!msg.includes('没有在执行') && !msg.includes('已结束')) throw err;
    }
  };

  const stopAndFlushWaiting = async () => {
    if (!t.terminal || flushingNow || waitingPending.length === 0) return;
    setFlushingNow(true);
    setProblem('');
    setStoppingNow(true);
    try {
      if (working) await stopIgnoringIdle();
      await chatOutbox.flush(t, ['waiting']);
      p.onChanged();
    } catch (err: any) {
      setProblem(err?.message || '停止并发送失败');
    } finally {
      setFlushingNow(false);
    }
  };

  const flushPaused = async () => {
    if (!t.terminal || flushingNow || pausedPending.length === 0) return;
    setFlushingNow(true);
    setProblem('');
    try {
      await chatOutbox.flush(t, ['paused']);
      p.onChanged();
    } catch (err: any) {
      setProblem(err?.message || '合并发送失败');
    } finally {
      setFlushingNow(false);
    }
  };

  const editPendingItem = (id: string, text: string) => {
    chatOutbox.remove(new Set([id]));
    if (text) setRecallInsert({ nonce: Date.now(), text });
  };

  return (
    <main class="thread">
      <header class="thread-head">
        <div class="head-main">
          <span class="tool-mark" style={toolBadgeStyle(t.tool)} title={meta.label}>
            {meta.mono}
          </span>
          <h1 class="head-title" title={t.title}>
            {t.title}
          </h1>
          {viewSwitch}
        </div>
        <div class="head-meta">
          {(t.project || t.folder) && (
            <button type="button" class="chip" onClick={p.onOpenFiles} title={t.folder || t.project}>
              {t.project || t.folder.split(/[\\/]/).pop()}
            </button>
          )}
          {t.project && <BranchMenu project={t.project} worktree={t.worktree} known={t.branch} />}
          {t.terminal?.mode === 'read' && <span class="chip soft">只读</span>}
        </div>
        <div class="head-actions">
          {p.split && p.onCloseSplit ? (
            <button type="button" class="btn quiet small" onClick={p.onCloseSplit}>
              关闭分屏
            </button>
          ) : (
            <>
              <button type="button" class="btn quiet small" onClick={p.onHandOff} title="交给另一个 AI 接着做">
                交接
              </button>
              <button
                type="button"
                class={`icon-btn${p.terminalOpen ? ' on' : ''}`}
                onClick={p.onToggleTerminal}
                title="项目终端 (Ctrl+J)"
              >
                <svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true">
                  <rect x="2.5" y="3.5" width="15" height="13" rx="2" fill="none" stroke="currentColor" stroke-width="1.5" />
                  <path d="m6 8 2.5 2L6 12M10.5 12.5h3.5" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" />
                </svg>
              </button>
              <button
                type="button"
                class={`icon-btn${p.inspectorOpen ? ' on' : ''}`}
                onClick={p.onToggleInspector}
                title="变更与文件 (Ctrl+B)"
              >
                <svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true">
                  <rect x="2.5" y="3.5" width="15" height="13" rx="2" fill="none" stroke="currentColor" stroke-width="1.5" />
                  <path d="M12.5 3.5v13" stroke="currentColor" stroke-width="1.5" />
                </svg>
              </button>
            </>
          )}
          <button type="button" class="icon-btn" title="更多" onClick={(e) => p.onMenu(e as unknown as MouseEvent)}>
            <Dots />
          </button>
        </div>
      </header>

      {problem && (
        <div class="banner bad">
          <span>{problem}</span>
          <button type="button" class="link-btn" onClick={() => setProblem('')}>
            知道了
          </button>
        </div>
      )}

      {searching && t.runtime !== 'screen' && (
        <div class="find-bar">
          <input
            ref={searchBox}
            type="search"
            placeholder="在这段对话里查找"
            value={query}
            onInput={(e) => setQuery((e.target as HTMLInputElement).value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault();
                step(e.shiftKey ? -1 : 1);
              } else if (e.key === 'Escape') {
                setSearching(false);
                setQuery('');
              }
            }}
          />
          <span class="find-count">{query.trim() ? (hits.length ? `${cursor + 1} / ${hits.length}` : '没有找到') : ''}</span>
          <button type="button" class="icon-btn" disabled={!hits.length} onClick={() => step(-1)} title="上一个">
            ↑
          </button>
          <button type="button" class="icon-btn" disabled={!hits.length} onClick={() => step(1)} title="下一个">
            ↓
          </button>
          <button
            type="button"
            class="icon-btn"
            onClick={() => {
              setSearching(false);
              setQuery('');
            }}
            title="关闭"
          >
            ×
          </button>
        </div>
      )}

      {t.runtime === 'screen' && t.terminal ? (
        <div class="screen-host">
          <iframe key={t.terminal.id} src={terminalEmbedUrl(t.terminal.id)} title={t.title} class="screen-frame" />
        </div>
      ) : (
        <>
          {conv.problem && <div class="banner">{conv.problem}</div>}
          <VirtualTimeline
            threadKey={conv.source || t.key}
            messages={conv.messages}
            loading={conv.loading}
            isBusy={working}
            sessionAsk={t.runtime === 'elsewhere' ? t.record?.ask : null}
            initialScrollTop={conv.scroll.top}
            initialStickBottom={conv.scroll.stick}
            highlightIndex={hits.length ? hits[cursor] : null}
            jumpRequest={jump}
            onScrollStateChange={conv.saveScroll}
            onAnswer={async (ask, decision) => {
              try {
                await act.answer(t, ask, decision);
                p.onChanged();
              } catch (err: any) {
                setProblem(err?.message || '没有提交');
              }
            }}
          />
          {t.runtime === 'elsewhere' && !say.ok && takeOver.ok && (
            <div class="banner">
              <span>{say.why}</span>
              <button type="button" class="btn quiet small" onClick={() => p.onTakeOver().catch((e) => setProblem(e?.message || '接管失败'))}>
                接管到这里
              </button>
            </div>
          )}
          <div class="thread-feedback">
            <RunStatus key={t.terminal?.id || t.key} phase={phase} since={since} />
            {(pending.length > 0 || queued.length > 0) && (
              <div class="pending-panel" aria-label="待发送消息" aria-live="polite">
                {waitingPending.length > 0 && (
                  <div class="pending-group">
                    <div class="pending-head">
                      <span class="pending-title">
                        <strong>待发送 · {waitingPending.length} 条</strong>
                        <span>{waitingPending.length > 1 ? '本轮结束后合并一次发出' : '本轮结束后发出'}</span>
                      </span>
                      <span class="pending-acts">
                        {working && canStopChat && (
                          <button
                            type="button"
                            class="pending-act primary"
                            disabled={flushingNow}
                            onClick={stopAndFlushWaiting}
                          >
                            {flushingNow ? '正在发送…' : waitingPending.length > 1 ? '停止并合并发送' : '停止并立即发送'}
                          </button>
                        )}
                        {!working && phase === 'idle' && (
                          <button
                            type="button"
                            class="pending-act primary"
                            disabled={flushingNow}
                            onClick={stopAndFlushWaiting}
                          >
                            {waitingPending.length > 1 ? '立即合并发送' : '立即发出'}
                          </button>
                        )}
                        <button
                          type="button"
                          class="pending-act"
                          disabled={flushingNow}
                          onClick={() => t.terminal && chatOutbox.clearDrafts(t.terminal.id, ['waiting'])}
                        >
                          清空
                        </button>
                      </span>
                    </div>
                    <ul class="pending-list">
                      {waitingPending.map((m, idx) => (
                        <li key={m.id}>
                          <span class="pending-idx">{idx + 1}</span>
                          <span class="pending-text">
                            {[m.text, ...m.pictures.map((path) => path.split('/').pop())].filter(Boolean).join(' · ')}
                          </span>
                          <button type="button" class="link-btn" onClick={() => editPendingItem(m.id, m.text)}>
                            编辑
                          </button>
                          <button
                            type="button"
                            class="pending-del"
                            title="移除这条待发送消息"
                            aria-label="移除"
                            onClick={() => chatOutbox.remove(new Set([m.id]))}
                          >
                            ×
                          </button>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}

                {pausedPending.length > 0 && (
                  <div class="pending-group is-paused">
                    <div class="pending-head">
                      <span class="pending-title">
                        <strong>已暂停待发 · {pausedPending.length} 条</strong>
                        <span>可先输入新指令，或随时合并发出</span>
                      </span>
                      <span class="pending-acts">
                        <button
                          type="button"
                          class="pending-act primary"
                          disabled={flushingNow || working || phase === 'confirm'}
                          onClick={flushPaused}
                        >
                          {pausedPending.length > 1 ? '合并发送' : '立即发送'}
                        </button>
                        <button
                          type="button"
                          class="pending-act"
                          disabled={flushingNow}
                          onClick={() => t.terminal && chatOutbox.clearDrafts(t.terminal.id, ['paused'])}
                        >
                          清空
                        </button>
                      </span>
                    </div>
                    <ul class="pending-list">
                      {pausedPending.map((m, idx) => (
                        <li key={m.id}>
                          <span class="pending-idx">{idx + 1}</span>
                          <span class="pending-text">
                            {[m.text, ...m.pictures.map((path) => path.split('/').pop())].filter(Boolean).join(' · ')}
                          </span>
                          <button type="button" class="link-btn" onClick={() => editPendingItem(m.id, m.text)}>
                            编辑
                          </button>
                          <button
                            type="button"
                            class="pending-del"
                            title="移除这条待发送消息"
                            aria-label="移除"
                            onClick={() => chatOutbox.remove(new Set([m.id]))}
                          >
                            ×
                          </button>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}

                {(queued.length > 0 || inflightPending.length > 0) && (
                  <ul class="pending-list">
                    {queued.map((m) => (
                      <li key={m.id}>
                        <strong>{({ queued: '待发送', paused: '已暂停发送', sending: '发送中' })[m.state]}</strong>
                        <span class="pending-text">{m.text}</span>
                      </li>
                    ))}
                    {inflightPending.map((m) => {
                      const unknown = m.state === 'unknown' || t.runtime !== 'chat';
                      return (
                        <li key={m.id}>
                          <strong>{unknown ? '送达结果未知' : m.state === 'sending' ? '正在提交' : '已提交，等待接收'}</strong>
                          <span class="pending-text">
                            {[m.text, ...m.pictures.map((path) => path.split('/').pop())].filter(Boolean).join(' · ')}
                          </span>
                          {unknown && (
                            <button type="button" class="link-btn" onClick={() => chatOutbox.remove(new Set([m.id]))}>
                              已核对
                            </button>
                          )}
                        </li>
                      );
                    })}
                  </ul>
                )}
              </div>
            )}
          </div>
          {pendingSwitch && (
            <div class="switch-banner">
              <span class="tool-mark" style={toolBadgeStyle(pendingSwitch.tool)}>
                {toolMeta(pendingSwitch.tool).mono}
              </span>
              <span class="switch-banner-text">
                下一条消息将由 {toolLabel(pendingSwitch.tool)}
                {pendingSwitch.model ? `（${pendingSwitch.model}）` : ''} 在当前对话里继续
              </span>
              <button type="button" class="link-btn" onClick={() => setPendingSwitch(null)}>
                取消
              </button>
            </div>
          )}
          <Composer
            draftKey={`rcli-draft:${t.key}`}
            placeholder={
              pendingSwitch
                ? `输入消息，发送后由 ${toolLabel(pendingSwitch.tool)} 继续`
                : t.runtime === 'idle'
                  ? `接着这段对话说点什么，${toolLabel(t.tool)} 会在这里继续`
                  : stoppingNow
                    ? '输入新指令，发送后会立即执行'
                    : working
                      ? '补充要求（可多条累计，或点“停止并发送”立即执行）'
                      : '说点什么'
            }
            pictureDir={t.runtime === 'chat' && t.project && !pendingSwitch ? t.project : ''}
            tail={
              <ToolSwitch
                thread={t}
                device={p.device}
                blocked={working || phase === 'confirm' || phase === 'starting' || pending.length > 0 || queued.length > 0}
                pending={pendingSwitch}
                onStageSwitch={setPendingSwitch}
                onChanged={p.onChanged}
                onHandedTo={p.onHandedTo}
                onHandOff={p.onHandOff}
              />
            }
            busy={working}
            canStop={canStopChat}
            queueMode={queueMode && !pendingSwitch}
            stopping={stoppingNow}
            disabled={phase === 'confirm' ? '先回答它的确认请求' : composerDisabled}
            insert={composerInsert}
            onSend={async (text, pictures) => {
              if (pendingSwitch) {
                if (pictures.length > 0) throw new Error('换 AI 的第一条消息暂不支持图片，请先发送文字');
                if (working || phase === 'confirm' || phase === 'starting') throw new Error('请等这一轮结束后再切换 AI');
                const nextTerm = await act.handover(t, pendingSwitch.tool, text, pendingSwitch.model, pendingSwitch.effort);
                setPendingSwitch(null);
                p.onHandedTo(nextTerm);
                return;
              }
              if (t.runtime === 'chat' && t.terminal) {
                if ((working || phase === 'starting') && !stoppingNow) {
                  chatOutbox.enqueue(t.terminal.id, text, pictures);
                  return;
                }
                await chatOutbox.sendNow(t, text, pictures);
              } else {
                await act.say(t, text, pictures);
              }
              p.onChanged();
            }}
            onStop={async () => {
              setStoppingNow(true);
              if (t.terminal) {
                chatOutbox.pauseWaiting(t.terminal.id);
                chatOutbox.markInFlightUnknown(t.terminal.id);
              }
              await act.stop(t);
              p.onChanged();
            }}
            onStopAndSend={
              t.runtime === 'chat' && canStopChat
                ? async (text, pictures) => {
                    setStoppingNow(true);
                    if (t.terminal) chatOutbox.pauseWaiting(t.terminal.id);
                    await stopIgnoringIdle();
                    await chatOutbox.sendNow(t, text, pictures);
                    p.onChanged();
                  }
                : undefined
            }
          />
        </>
      )}
    </main>
  );
}
