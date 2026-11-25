import { useEffect, useRef, useState } from 'preact/hooks';
import type { Thread } from '../runtime/threads';
import { toolBadgeStyle, toolMeta } from '../runtime/types';

export interface Command {
  label: string;
  keys?: string;
  run: () => void;
}

interface Props {
  threads: Thread[];
  commands: Command[];
  onOpen: (t: Thread) => void;
  onClose: () => void;
}

/** Ctrl+K: go to a conversation or do something, by typing a few letters of it. */
export function CommandPalette({ threads, commands, onOpen, onClose }: Props) {
  const [query, setQuery] = useState('');
  const [at, setAt] = useState(0);
  const box = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    window.setTimeout(() => box.current?.focus(), 10);
  }, []);

  const q = query.trim().toLowerCase();
  const rows: Array<{ key: string; label: string; sub: string; tool?: string; run: () => void }> = [
    ...commands
      .filter((c) => !q || c.label.toLowerCase().includes(q))
      .map((c) => ({ key: `c:${c.label}`, label: c.label, sub: c.keys || '', run: c.run })),
    ...threads
      .filter((t) => !q || [t.title, t.project, t.folder].some((s) => (s || '').toLowerCase().includes(q)))
      .slice(0, 30)
      .map((t) => ({ key: t.key, label: t.title, sub: t.project || t.folder, tool: t.tool, run: () => onOpen(t) })),
  ];
  const chosen = Math.min(at, rows.length - 1);

  const go = (i: number) => {
    const row = rows[i];
    if (!row) return;
    onClose();
    row.run();
  };

  return (
    <div class="dlg-back top" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div class="palette" role="dialog" aria-label="跳转">
        <input
          ref={box}
          type="search"
          placeholder="找对话或命令"
          value={query}
          onInput={(e) => {
            setQuery((e.target as HTMLInputElement).value);
            setAt(0);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Escape') onClose();
            else if (e.key === 'ArrowDown') {
              e.preventDefault();
              setAt((v) => Math.min(rows.length - 1, v + 1));
            } else if (e.key === 'ArrowUp') {
              e.preventDefault();
              setAt((v) => Math.max(0, v - 1));
            } else if (e.key === 'Enter') {
              e.preventDefault();
              go(chosen);
            }
          }}
        />
        <div class="palette-list">
          {rows.length === 0 && <div class="pane-empty">没有找到</div>}
          {rows.map((r, i) => (
            <button key={r.key} type="button" class={`palette-row${i === chosen ? ' on' : ''}`} onMouseEnter={() => setAt(i)} onClick={() => go(i)}>
              {r.tool ? (
                <span class="tool-mark tiny" style={toolBadgeStyle(r.tool)}>
                  {toolMeta(r.tool).mono}
                </span>
              ) : (
                <span class="tool-mark tiny cmd">›</span>
              )}
              <span class="palette-label">{r.label}</span>
              <span class="palette-sub">{r.sub}</span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
