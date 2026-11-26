interface Props {
  text: string;
  streaming: boolean;
  /** User's explicit choice: true = full untruncated, false = collapsed 1-line bar, undefined = auto. */
  expanded: boolean | undefined;
  /** Search match forces full display so the matched line stays visible. */
  highlighted?: boolean;
  onToggle: (next: boolean) => void;
}

function summarizeThought(raw: string): { trimmed: string; lines: number; chars: number; snippet: string } {
  const trimmed = raw.trim();
  if (!trimmed) return { trimmed: '', lines: 0, chars: 0, snippet: '' };
  const parts = trimmed.split(/\r?\n/);
  const first = parts.find((l) => l.trim().length > 0) || parts[0];
  const snippet = first.replace(/\s+/g, ' ').trim();
  return { trimmed, lines: parts.length, chars: trimmed.length, snippet };
}

function formatMeta(lines: number, chars: number): string {
  if (!chars) return '';
  const charLabel = chars >= 1000 ? `${(chars / 1000).toFixed(1)}k 字` : `${chars} 字`;
  return lines > 1 ? `${lines} 行 · ${charLabel}` : charLabel;
}

/**
 * Collapsible / expandable thinking block for Antigravity, Claude Code, and Codex reasoning.
 * Never hard-clips expanded content; shows a live preview while streaming unless the user collapses or expands it.
 */
export function ThoughtBlock({ text, streaming, expanded, highlighted = false, onToggle }: Props) {
  const { trimmed, lines, chars, snippet } = summarizeThought(text);
  const isFull = highlighted || expanded === true;
  const isLivePreview = !isFull && streaming && expanded !== false;
  const isCollapsed = !isFull && !isLivePreview;
  const meta = formatMeta(lines, chars);
  const needsFootToggle = isFull && (lines > 8 || chars > 360);

  return (
    <div class={`thought-card${isFull ? ' is-open' : isLivePreview ? ' is-live' : ' is-shut'}`}>
      <button
        type="button"
        class="thought-head"
        aria-expanded={isFull}
        onClick={() => onToggle(!isFull)}
      >
        <span class="thought-caret" aria-hidden="true">
          {isFull ? '▾' : '▸'}
        </span>
        <span class={`thought-label${streaming ? ' is-streaming' : ''}`}>
          {streaming ? '正在思考…' : '思考过程'}
        </span>
        {isCollapsed && snippet && <span class="thought-snippet">{snippet}</span>}
        {meta && <span class="thought-meta">{meta}</span>}
        <span class="thought-action">{isFull ? '收起' : '展开全部'}</span>
      </button>

      {isLivePreview && trimmed && (
        <div class="thought-live-wrap">
          <div class="thought-body is-preview">{trimmed}</div>
          <div class="thought-live-bar">
            <button type="button" class="link-btn" onClick={() => onToggle(true)}>
              展开全部
            </button>
            <button type="button" class="link-btn muted-link" onClick={() => onToggle(false)}>
              收起
            </button>
          </div>
        </div>
      )}

      {isFull && (
        <>
          <div class="thought-body is-full">{trimmed || '（暂无思考内容）'}</div>
          {needsFootToggle && (
            <button type="button" class="thought-foot" onClick={() => onToggle(false)}>
              收起思考 ▴
            </button>
          )}
        </>
      )}
    </div>
  );
}
