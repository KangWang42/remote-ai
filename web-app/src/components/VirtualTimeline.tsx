import { memo } from 'preact/compat';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'preact/hooks';
import type { ChatMessageItem, SessionAsk } from '../runtime/types';
import { MarkdownView } from './MarkdownView';
import { ThoughtBlock } from './ThoughtBlock';

const VIRTUAL_THRESHOLD = 45;
const OVERSCAN_PX = 720;

interface RowProps {
  item: ChatMessageItem;
  index: number;
  streaming: boolean;
  expanded: boolean;
  thoughtExpanded: boolean | undefined;
  highlighted: boolean;
  onToggleTool: (id: string) => void;
  onToggleThought: (id: string, next: boolean) => void;
  onAnswer: (askId: string, decision: string) => void;
  onMeasure?: (id: string, height: number) => void;
}

const TimelineRow = memo(function TimelineRow({
  item,
  index,
  streaming,
  expanded,
  thoughtExpanded,
  highlighted,
  onToggleTool,
  onToggleThought,
  onAnswer,
  onMeasure,
}: RowProps) {
  const hostRef = useRef<HTMLDivElement | null>(null);

  useLayoutEffect(() => {
    const el = hostRef.current;
    if (!el || !onMeasure) return;
    onMeasure(item.id, el.offsetHeight);
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => {
      if (hostRef.current) onMeasure(item.id, hostRef.current.offsetHeight);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [item.id, expanded, thoughtExpanded, highlighted, onMeasure]);

  let content: any;
  if (item.role === 'user') {
    content = (
      <div class="msg msg-user">
        <div class="msg-bubble">{item.text}</div>
      </div>
    );
  } else if (item.role === 'tool') {
    content = (
      <div class={`tool-card status-${item.status || 'done'}`}>
        <button type="button" class="tool-card-head" onClick={() => onToggleTool(item.id)}>
          <span class="tool-step-icon" aria-hidden="true">
            {item.status === 'error' ? '!' : item.status === 'running' ? '…' : '✓'}
          </span>
          <span class="tool-card-title">{item.title || '工具调用'}</span>
          <span class="tool-card-summary">{item.text}</span>
          <span class="caret">{expanded ? '▾' : '▸'}</span>
        </button>
        {expanded && item.output && <pre class="tool-card-out">{item.output}</pre>}
      </div>
    );
  } else if (item.role === 'ask') {
    content = (
      <div class="ask-card">
        <div class="ask-title">{item.title || '等待确认'}</div>
        <pre class="ask-body">{item.text}</pre>
        <div class="ask-actions">
          {(item.options || []).map((opt) => (
            <button
              key={opt.id}
              type="button"
              class={opt.id === 'deny' ? 'danger-btn compact' : 'primary-btn compact'}
              onClick={() => onAnswer(item.askId || item.id, opt.id)}
            >
              {opt.label}
            </button>
          ))}
        </div>
      </div>
    );
  } else if (item.role === 'note' || item.role === 'error') {
    content = <div class={`timeline-note${item.role === 'error' ? ' is-error' : ''}`}>{item.text}</div>;
  } else if (item.role === 'thought') {
    content = (
      <ThoughtBlock
        text={item.text}
        streaming={streaming}
        expanded={thoughtExpanded}
        highlighted={highlighted}
        onToggle={(next) => onToggleThought(item.id, next)}
      />
    );
  } else {
    content = (
      <div class="msg msg-assistant">
        <MarkdownView text={item.text} streaming={streaming} />
      </div>
    );
  }

  return (
    <div
      ref={hostRef}
      data-msg-idx={index}
      class={`tl-item${highlighted ? ' is-search-hit' : ''}`}
    >
      {content}
    </div>
  );
});

/** Role & length bucket estimator so unmeasured items avoid scroll jumping in 500+ turn chats. */
function estimateHeight(item: ChatMessageItem, expanded: boolean, thoughtExpanded?: boolean): number {
  if (item.role === 'note' || item.role === 'error') return 42;
  if (item.role === 'tool') return expanded && item.output ? 240 : 62;
  if (item.role === 'ask') return 180;
  if (item.role === 'thought') {
    if (thoughtExpanded !== true) return 52;
    const newlines = (item.text.match(/\n/g) || []).length;
    const estimatedLines = Math.max(newlines + 1, Math.ceil(item.text.length / 64));
    return Math.min(2400, Math.max(88, 52 + estimatedLines * 21));
  }
  const len = item.text.length;
  if (item.role === 'user') return Math.min(320, 56 + Math.ceil(len / 44) * 24);
  const newlines = (item.text.match(/\n/g) || []).length;
  const estimatedLines = Math.max(newlines + 1, Math.ceil(len / 56));
  return Math.min(1200, Math.max(54, 24 + estimatedLines * 24));
}

interface Props {
  threadKey: string;
  messages: ChatMessageItem[];
  loading: boolean;
  isBusy: boolean;
  sessionAsk?: SessionAsk | null;
  initialScrollTop?: number;
  initialStickBottom?: boolean;
  highlightIndex?: number | null;
  jumpRequest?: { index: number; nonce: number } | null;
  onScrollStateChange?: (scrollTop: number, stickBottom: boolean) => void;
  onAnswer: (askId: string, decision: string) => void;
}

/**
 * High-performance virtualized conversation timeline with:
 * - Measured height cache + role/length bucket estimation
 * - Selection-pinning so dragging text selection across messages never loses DOM nodes while scrolling
 * - Memoized rows paired with O(1) incremental reducer so streaming tokens only re-render the tail
 * - Sticky bottom follow + floating "回到底部" unread pill when reading earlier messages
 */
export function VirtualTimeline({
  threadKey,
  messages,
  loading,
  isBusy,
  sessionAsk,
  initialScrollTop = 0,
  initialStickBottom = true,
  highlightIndex = null,
  jumpRequest = null,
  onScrollStateChange,
  onAnswer,
}: Props) {
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const heightsRef = useRef<Map<string, number>>(new Map());
  const stickBottomRef = useRef(initialStickBottom);
  const prevLenRef = useRef(messages.length);

  const [expandedTools, setExpandedTools] = useState<Record<string, boolean>>({});
  const [expandedThoughts, setExpandedThoughts] = useState<Record<string, boolean>>({});
  const [scrollTop, setScrollTop] = useState(initialScrollTop);
  const [viewportH, setViewportH] = useState(640);
  const [measureVer, setMeasureVer] = useState(0);
  const [pinnedRange, setPinnedRange] = useState<[number, number] | null>(null);
  const [showJumpBottom, setShowJumpBottom] = useState(false);
  const [unreadCount, setUnreadCount] = useState(0);

  // Reset per-thread state & restore cached scroll position when switching threads
  useEffect(() => {
    heightsRef.current.clear();
    setExpandedTools({});
    setExpandedThoughts({});
    setPinnedRange(null);
    setUnreadCount(0);
    stickBottomRef.current = initialStickBottom;
    prevLenRef.current = messages.length;
    setShowJumpBottom(!initialStickBottom);
    const el = scrollRef.current;
    if (el) {
      window.requestAnimationFrame(() => {
        if (!scrollRef.current) return;
        if (initialStickBottom) {
          scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
        } else {
          scrollRef.current.scrollTop = initialScrollTop;
        }
        setScrollTop(scrollRef.current.scrollTop);
        setViewportH(scrollRef.current.clientHeight || 640);
      });
    }
  }, [threadKey]);

  // Track viewport resize
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    setViewportH(el.clientHeight || 640);
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => {
      if (scrollRef.current) setViewportH(scrollRef.current.clientHeight || 640);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Selection-pinning: keep selected message nodes mounted even while scrolling a virtualized thread
  useEffect(() => {
    const onSelectionChange = () => {
      const root = scrollRef.current;
      const sel = window.getSelection();
      if (!root || !sel || sel.isCollapsed || sel.rangeCount === 0) {
        setPinnedRange(null);
        return;
      }
      const findIdx = (node: Node | null): number | null => {
        let cur: Node | null = node;
        while (cur && cur !== root) {
          if (cur instanceof HTMLElement && cur.dataset.msgIdx !== undefined) {
            return Number(cur.dataset.msgIdx);
          }
          cur = cur.parentNode;
        }
        return null;
      };
      const a = findIdx(sel.anchorNode);
      const b = findIdx(sel.focusNode);
      if (a === null || b === null) {
        setPinnedRange(null);
        return;
      }
      setPinnedRange([Math.min(a, b), Math.max(a, b)]);
    };
    document.addEventListener('selectionchange', onSelectionChange);
    return () => document.removeEventListener('selectionchange', onSelectionChange);
  }, []);

  const handleMeasure = useCallback((id: string, h: number) => {
    if (h <= 0) return;
    const prev = heightsRef.current.get(id);
    if (prev !== undefined && Math.abs(prev - h) < 2) return;
    heightsRef.current.set(id, h);
    setMeasureVer((v) => v + 1);
  }, []);

  const handleToggleTool = useCallback((id: string) => {
    setExpandedTools((prev) => ({ ...prev, [id]: !prev[id] }));
  }, []);

  const handleToggleThought = useCallback((id: string, next: boolean) => {
    setExpandedThoughts((prev) => ({ ...prev, [id]: next }));
  }, []);

  const layout = useMemo(() => {
    const count = messages.length;
    const offsets = new Float64Array(count + 1);
    let acc = 0;
    for (let i = 0; i < count; i++) {
      offsets[i] = acc;
      const m = messages[i];
      const h = heightsRef.current.get(m.id) ?? estimateHeight(m, !!expandedTools[m.id], expandedThoughts[m.id]);
      acc += h;
    }
    offsets[count] = acc;
    return { offsets, totalHeight: acc };
  }, [messages, expandedTools, expandedThoughts, measureVer]);

  // Stick to bottom when streaming or new messages arrive (if user hasn't scrolled up)
  const lastTextLen = messages.length > 0 ? messages[messages.length - 1].text.length : 0;
  useEffect(() => {
    const added = messages.length - prevLenRef.current;
    prevLenRef.current = messages.length;
    if (stickBottomRef.current && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
      setScrollTop(scrollRef.current.scrollTop);
    } else if (added > 0) {
      setUnreadCount((c) => c + added);
    }
  }, [messages.length, lastTextLen]);

  // Jump to explicit search match index
  useEffect(() => {
    if (!jumpRequest || !scrollRef.current) return;
    const idx = Math.max(0, Math.min(messages.length - 1, jumpRequest.index));
    const targetTop = Math.max(0, (layout.offsets[idx] || 0) - 96);
    stickBottomRef.current = false;
    setShowJumpBottom(true);
    scrollRef.current.scrollTo({ top: targetTop, behavior: 'smooth' });
  }, [jumpRequest?.nonce]);

  const onScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    const st = el.scrollTop;
    const nearBottom = el.scrollHeight - st - el.clientHeight < 96;
    stickBottomRef.current = nearBottom;
    setShowJumpBottom(!nearBottom);
    if (nearBottom) setUnreadCount(0);
    setScrollTop(st);
    onScrollStateChange?.(st, nearBottom);
  };

  const jumpToBottom = () => {
    const el = scrollRef.current;
    if (!el) return;
    stickBottomRef.current = true;
    setShowJumpBottom(false);
    setUnreadCount(0);
    el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
  };

  // Compute rendered range (direct below threshold, windowed + selection-pinned above threshold)
  const useVirtual = messages.length >= VIRTUAL_THRESHOLD;
  let startIdx = 0;
  let endIdx = messages.length - 1;
  if (useVirtual && messages.length > 0) {
    const minY = Math.max(0, scrollTop - OVERSCAN_PX);
    const maxY = scrollTop + viewportH + OVERSCAN_PX;
    // Binary search for startIdx
    let lo = 0;
    let hi = messages.length - 1;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (layout.offsets[mid + 1] < minY) lo = mid + 1;
      else hi = mid;
    }
    startIdx = lo;
    // Scan forward for endIdx
    endIdx = startIdx;
    while (endIdx < messages.length - 1 && layout.offsets[endIdx] < maxY) {
      endIdx++;
    }
    if (pinnedRange) {
      startIdx = Math.min(startIdx, Math.max(0, pinnedRange[0]));
      endIdx = Math.max(endIdx, Math.min(messages.length - 1, pinnedRange[1]));
    }
    if (highlightIndex !== null && highlightIndex >= 0 && highlightIndex < messages.length) {
      startIdx = Math.min(startIdx, highlightIndex);
      endIdx = Math.max(endIdx, highlightIndex);
    }
  }

  const topPad = useVirtual && startIdx > 0 ? layout.offsets[startIdx] : 0;
  const bottomPad = useVirtual && endIdx < messages.length - 1 ? Math.max(0, layout.totalHeight - layout.offsets[endIdx + 1]) : 0;

  return (
    <div class="timeline-wrap">
      <div class="timeline" ref={scrollRef} onScroll={onScroll}>
        {loading && messages.length === 0 ? (
          <div class="timeline-note">正在加载对话…</div>
        ) : messages.length === 0 ? (
          <div class="timeline-note">还没有消息，在下方输入开始对话。</div>
        ) : (
          <div class="tl-inner">
            {topPad > 0 && <div style={{ height: `${topPad}px`, flex: 'none' }} aria-hidden="true" />}
            {messages.slice(startIdx, endIdx + 1).map((m, relIdx) => {
              const absIdx = startIdx + relIdx;
              const isLast = absIdx === messages.length - 1;
              return (
                <TimelineRow
                  key={m.id}
                  item={m}
                  index={absIdx}
                  streaming={isBusy && isLast}
                  expanded={!!expandedTools[m.id]}
                  thoughtExpanded={expandedThoughts[m.id]}
                  highlighted={highlightIndex === absIdx}
                  onToggleTool={handleToggleTool}
                  onToggleThought={handleToggleThought}
                  onAnswer={onAnswer}
                  onMeasure={useVirtual ? handleMeasure : undefined}
                />
              );
            })}
            {bottomPad > 0 && <div style={{ height: `${bottomPad}px`, flex: 'none' }} aria-hidden="true" />}
          </div>
        )}

        {sessionAsk && (
          <div class="ask-card">
            <div class="ask-title">电脑上的对话等待确认</div>
            <pre class="ask-body">{sessionAsk.text}</pre>
            <div class="ask-actions">
              <button type="button" class="primary-btn compact" onClick={() => onAnswer(sessionAsk.id, 'allow')}>
                允许一次
              </button>
              <button type="button" class="danger-btn compact" onClick={() => onAnswer(sessionAsk.id, 'deny')}>
                拒绝
              </button>
            </div>
          </div>
        )}
      </div>

      {showJumpBottom && messages.length > 0 && (
        <button type="button" class="jump-bottom-pill" onClick={jumpToBottom}>
          ↓ {unreadCount > 0 ? `${unreadCount} 条新消息` : '回到底部'}
        </button>
      )}
    </div>
  );
}
