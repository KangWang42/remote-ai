import { useEffect, useState } from 'preact/hooks';
import { sendTerminalAction, terminalEmbedUrl } from '../runtime/api';
import type { TerminalEntry } from '../runtime/types';

interface Props {
  open: boolean;
  activeProject: string;
  selectedId?: string;
  terminals: TerminalEntry[];
  onClose: () => void;
  onRefresh: () => void;
}

const MAX_KEPT_TERMINALS = 4;

/**
 * Persistent background terminal drawer (inspired by T3 Code's PersistentThreadTerminals):
 * keeps up to 4 recently visited PTY iframes mounted while hidden so switching between shells
 * or collapsing the drawer never loses screen state or scrollback.
 */
export function TerminalDrawer({ open, activeProject, selectedId, terminals, onClose, onRefresh }: Props) {
  const shellTerminals = terminals.filter((t) => t.form !== 'chat' && t.state !== 'closed');
  const [activeId, setActiveId] = useState<string>('');
  const [mountedIds, setMountedIds] = useState<string[]>([]);
  const [height, setHeight] = useState(280);
  const [starting, setStarting] = useState(false);

  useEffect(() => {
    if (selectedId) setActiveId(selectedId);
  }, [selectedId]);

  useEffect(() => {
    if (activeId && shellTerminals.some((t) => t.id === activeId)) return;
    if (shellTerminals.length > 0) setActiveId(shellTerminals[0].id);
    else setActiveId('');
  }, [shellTerminals.map((t) => t.id).join(',')]);

  useEffect(() => {
    if (!activeId) return;
    setMountedIds((prev) => {
      const next = [activeId, ...prev.filter((id) => id !== activeId && shellTerminals.some((t) => t.id === id))];
      return next.slice(0, MAX_KEPT_TERMINALS);
    });
  }, [activeId, shellTerminals.length]);

  const startShell = async () => {
    if (!activeProject || starting) return;
    setStarting(true);
    try {
      const res = await sendTerminalAction({ action: 'start', tool: 'shell', dir: activeProject, history: false });
      if (res?.terminal) setActiveId(res.terminal);
      onRefresh();
    } finally {
      setStarting(false);
    }
  };

  const beginDrag = (e: MouseEvent) => {
    e.preventDefault();
    const startY = e.clientY;
    const startH = height;
    const onMove = (ev: MouseEvent) => {
      const next = Math.max(160, Math.min(window.innerHeight * 0.65, startH + (startY - ev.clientY)));
      setHeight(next);
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  };

  if (!open) return null;

  return (
    <section class="term-drawer" style={{ height: `${height}px` }}>
      <div class="term-resizer" onMouseDown={beginDrag} title="拖拽调整终端高度" />
      <div class="term-bar">
        <div class="term-tabs">
          {shellTerminals.map((t) => (
            <button
              key={t.id}
              type="button"
              class={`term-tab${t.id === activeId ? ' active' : ''}`}
              onClick={() => setActiveId(t.id)}
            >
              <span>{t.title || t.tool}</span>
              {t.dir && <span class="term-tab-dir">{t.dir}</span>}
            </button>
          ))}
          {activeProject && (
            <button type="button" class="ghost-btn compact" disabled={starting} onClick={startShell}>
              {starting ? '启动中…' : '+ Shell'}
            </button>
          )}
        </div>
        <button type="button" class="icon-btn" onClick={onClose} title="收起终端">
          ✕
        </button>
      </div>
      <div class="term-body">
        {mountedIds.length === 0 ? (
          <div class="empty-hint">
            {activeProject ? '点击“+ Shell”在当前项目打开共享集成终端' : '先选择一个项目以启动集成终端'}
          </div>
        ) : (
          mountedIds.map((id) => (
            <iframe
              key={id}
              src={terminalEmbedUrl(id)}
              title={`terminal-${id}`}
              class="terminal-iframe"
              style={{ display: id === activeId ? 'block' : 'none' }}
            />
          ))
        )}
      </div>
    </section>
  );
}
