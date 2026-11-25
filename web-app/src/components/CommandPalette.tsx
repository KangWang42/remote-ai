import { useEffect, useRef, useState } from 'preact/hooks';
import type { SelectedItem, SessionEntry, TerminalEntry } from '../runtime/types';

interface Props {
  open: boolean;
  terminals: TerminalEntry[];
  sessions: SessionEntry[];
  onClose: () => void;
  onSelect: (item: SelectedItem) => void;
  onAction: (action: 'new' | 'diff' | 'terminal' | 'connect') => void;
}

export function CommandPalette({ open, terminals, sessions, onClose, onSelect, onAction }: Props) {
  const [query, setQuery] = useState('');
  const inputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (open) {
      setQuery('');
      window.setTimeout(() => inputRef.current?.focus(), 20);
    }
  }, [open]);

  if (!open) return null;

  const q = query.trim().toLowerCase();
  const quickActions = [
    { id: 'new' as const, label: '新建对话 / Worktree 隔离任务', hint: 'Action' },
    { id: 'diff' as const, label: '打开 Git Diff 审阅面板', hint: 'Panel' },
    { id: 'terminal' as const, label: '切换底部集成终端', hint: 'Panel' },
    { id: 'connect' as const, label: '查看多路由连接状态与外观', hint: 'Settings' },
  ].filter((a) => !q || a.label.toLowerCase().includes(q));

  const matchedTerminals = terminals
    .filter((t) => !q || (t.title || t.tool).toLowerCase().includes(q) || (t.dir || '').toLowerCase().includes(q))
    .slice(0, 8);

  const matchedSessions = sessions
    .filter((s) => !s.hidden && (!q || s.title.toLowerCase().includes(q) || (s.dir || '').toLowerCase().includes(q)))
    .slice(0, 8);

  return (
    <div class="modal-backdrop" onClick={onClose}>
      <div class="palette-card" onClick={(e) => e.stopPropagation()}>
        <input
          ref={inputRef}
          type="search"
          class="palette-input"
          placeholder="搜索对话、终端、项目或命令…（Esc 关闭）"
          value={query}
          onInput={(e) => setQuery((e.target as HTMLInputElement).value)}
          onKeyDown={(e) => {
            if (e.key === 'Escape') onClose();
          }}
        />
        <div class="palette-list">
          {quickActions.map((a) => (
            <button
              key={a.id}
              type="button"
              class="palette-row"
              onClick={() => {
                onAction(a.id);
                onClose();
              }}
            >
              <span>{a.label}</span>
              <span class="muted-code">{a.hint}</span>
            </button>
          ))}
          {matchedTerminals.map((t) => (
            <button
              key={t.id}
              type="button"
              class="palette-row"
              onClick={() => {
                onSelect({ kind: 'terminal', id: t.id });
                onClose();
              }}
            >
              <span>{t.title || t.tool}</span>
              <span class="muted-code">{t.dir || '终端'}</span>
            </button>
          ))}
          {matchedSessions.map((s) => (
            <button
              key={`${s.tool}:${s.id}`}
              type="button"
              class="palette-row"
              onClick={() => {
                onSelect({ kind: 'session', tool: s.tool, id: s.id, dir: s.dir, folder: s.folder });
                onClose();
              }}
            >
              <span>{s.title}</span>
              <span class="muted-code">{s.dir || s.origin || s.tool}</span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
