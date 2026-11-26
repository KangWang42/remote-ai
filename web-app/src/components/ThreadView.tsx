import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
import * as act from '../runtime/actions';
import { terminalEmbedUrl } from '../runtime/api';
import { chatOutbox, usePending } from '../runtime/chatOutbox';
import { normalizeMs } from '../runtime/time';
import { canReopen, canSay, canStop, canTakeOver, type Thread } from '../runtime/threads';
import { useConversation } from '../runtime/useConversation';
import { toolBadgeStyle, toolLabel, toolMeta } from '../runtime/types';
import { BranchMenu } from './BranchMenu';
import { Composer } from './Composer';
import { Dots } from './Sidebar';
import { VirtualTimeline } from './VirtualTimeline';
import { ThreadModelPicker } from './ThreadModelPicker';
import { RunStatus } from './RunStatus';

interface Props {
  thread: Thread;
  features: string[];
  inspectorOpen: boolean;
  terminalOpen: boolean;
  insert?: { nonce: number; text: string } | null;
  split?: boolean;
  onMenu: (e: MouseEvent) => void;
  onReopen: (to: 'chat' | 'screen') => Promise<void>;
  onTakeOver: (text?: string) => Promise<void>;
  onHandOff: () => void;
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
  const searchBox = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    setSearching(false);
    setQuery('');
    setProblem('');
  }, [t.key]);

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
          <ThreadModelPicker thread={t} features={p.features} blocked={working || phase === 'confirm' || phase === 'starting' || pending.length > 0 || queued.length > 0} onChanged={p.onChanged} />
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
            {(pending.length > 0 || queued.length > 0) && <ul class="pending-list" aria-label="待发送消息" aria-live="polite">
              {queued.map((m) => <li key={m.id}><strong>{({ queued: '待发送', paused: '已暂停发送', sending: '发送中' })[m.state]}</strong><span>{m.text}</span></li>)}
              {pending.map((m) => {
                const unknown = m.state === 'unknown' || t.runtime !== 'chat';
                return <li key={m.id}>
                  <strong>{unknown ? '送达结果未知' : m.state === 'sending' ? '正在提交' : '待发送'}</strong>
                  <span>{[m.text, ...m.pictures.map((path) => path.split('/').pop())].filter(Boolean).join(' · ')}</span>
                  {unknown && <button type="button" class="link-btn" onClick={() => chatOutbox.remove(new Set([m.id]))}>已核对</button>}
                </li>;
              })}
            </ul>}
          </div>
          <Composer
            draftKey={`rcli-draft:${t.key}`}
            placeholder={
              t.runtime === 'idle'
                ? `接着这段对话说点什么，${toolLabel(t.tool)} 会在这里继续`
                : working
                  ? '补充要求'
                  : '说点什么'
            }
            pictureDir={t.runtime === 'chat' && t.project ? t.project : ''}
            busy={working}
            canStop={stop.ok || t.runtime === 'chat'}
            disabled={phase === 'confirm' ? '先回答它的确认请求' : composerDisabled}
            insert={p.insert}
            onSend={async (text, pictures) => {
              if (t.runtime === 'chat') await chatOutbox.send(t, text, pictures);
              else await act.say(t, text, pictures);
              p.onChanged();
            }}
            onStop={async () => {
              await act.stop(t);
              chatOutbox.mark(new Set(pending.map((m) => m.id)), 'unknown');
              p.onChanged();
            }}
          />
        </>
      )}
    </main>
  );
}
