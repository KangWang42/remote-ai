import { useEffect, useState } from 'preact/hooks';
import * as act from '../runtime/actions';
import { fetchProjectScripts, terminalEmbedUrl, terminalOp } from '../runtime/api';
import type { ProjectScriptEntry, TerminalEntry } from '../runtime/types';
import { ContextMenu, type MenuState } from './ContextMenu';

interface Props {
  project: string;
  worktree: string;
  shells: TerminalEntry[];
  /** A terminal to show (one just opened elsewhere). */
  focus: string;
  onClose: () => void;
  onChanged: () => void;
}

const KEPT = 4;

/**
 * The project's own terminals, under the conversation. The last few that were looked at stay loaded while hidden,
 * so switching between them or folding the panel keeps what they show.
 */
export function TerminalPanel({ project, worktree, shells, focus, onClose, onChanged }: Props) {
  const [active, setActive] = useState('');
  const [mounted, setMounted] = useState<string[]>([]);
  const [height, setHeight] = useState(() => {
    try {
      return Number(localStorage.getItem('rcli-term-h')) || 280;
    } catch {
      return 280;
    }
  });
  const [scripts, setScripts] = useState<ProjectScriptEntry[]>([]);
  const [menu, setMenu] = useState<MenuState | null>(null);
  const [problem, setProblem] = useState('');

  const ordered = [...shells.filter((t) => t.dir === project), ...shells.filter((t) => t.dir !== project)];

  useEffect(() => {
    if (focus) setActive(focus);
  }, [focus]);

  useEffect(() => {
    if (active && shells.some((t) => t.id === active)) return;
    setActive(ordered[0]?.id || '');
  }, [shells.map((t) => t.id).join(','), project]);

  useEffect(() => {
    if (!active) return;
    setMounted((prev) => [active, ...prev.filter((id) => id !== active && shells.some((t) => t.id === id))].slice(0, KEPT));
  }, [active, shells.length]);

  useEffect(() => {
    if (!project) return setScripts([]);
    let alive = true;
    fetchProjectScripts(project, worktree)
      .then((res) => alive && setScripts(res.scripts || []))
      .catch(() => alive && setScripts([]));
    return () => {
      alive = false;
    };
  }, [project, worktree]);

  const open = async (command = '') => {
    if (!project) return;
    setProblem('');
    try {
      const id = await act.openShell(project);
      setActive(id);
      onChanged();
      if (command) window.setTimeout(() => act.typeInto(id, command + '\r').catch(() => {}), 700);
    } catch (err: any) {
      setProblem(err?.message || '终端没有打开');
    }
  };

  const close = async (id: string) => {
    try {
      await terminalOp({ action: 'close', terminal: id });
      onChanged();
    } catch (err: any) {
      setProblem(err?.message || '终端没有关闭');
    }
  };

  const drag = (e: MouseEvent) => {
    e.preventDefault();
    const y0 = e.clientY;
    const h0 = height;
    let h = h0;
    const move = (ev: MouseEvent) => {
      h = Math.max(140, Math.min(window.innerHeight * 0.7, h0 + (y0 - ev.clientY)));
      setHeight(h);
    };
    const up = () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
      try {
        localStorage.setItem('rcli-term-h', String(Math.round(h)));
      } catch {
        // the height lasts as long as the page
      }
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
  };

  return (
    <section class="term-panel" style={{ height: `${height}px` }}>
      <div class="term-grip" onMouseDown={drag} />
      <div class="term-bar">
        <div class="term-tabs">
          {ordered.map((t) => (
            <span key={t.id} class={`term-tab${t.id === active ? ' on' : ''}`}>
              <button type="button" onClick={() => setActive(t.id)} title={t.dir}>
                {t.dir === project ? t.title || '终端' : `${t.dir} · ${t.title || '终端'}`}
              </button>
              <button type="button" class="term-x" onClick={() => close(t.id)} aria-label="关闭终端">
                ×
              </button>
            </span>
          ))}
          <button type="button" class="btn quiet small" disabled={!project} onClick={() => open()} title={project ? `在 ${project} 中打开终端` : '先选一个项目'}>
            + 终端
          </button>
          {scripts.length > 0 && (
            <button
              type="button"
              class="btn quiet small"
              onClick={(e) =>
                setMenu({
                  x: (e as MouseEvent).clientX,
                  y: (e as MouseEvent).clientY,
                  items: scripts.slice(0, 16).map((s) => ({ label: s.name, hint: s.command, run: () => open(s.command) })),
                })
              }
            >
              运行脚本
            </button>
          )}
        </div>
        {problem && <span class="term-problem">{problem}</span>}
        <button type="button" class="icon-btn" onClick={onClose} title="收起 (Ctrl+J)" aria-label="收起">
          ×
        </button>
      </div>
      <div class="term-body">
        {mounted.length === 0 ? (
          <div class="pane-empty">{project ? `在 ${project} 中打开一个终端` : '先选一个项目'}</div>
        ) : (
          mounted.map((id) => (
            <iframe key={id} src={terminalEmbedUrl(id)} title="终端" class="screen-frame" style={{ display: id === active ? 'block' : 'none' }} />
          ))
        )}
      </div>
      {menu && <ContextMenu {...menu} onClose={() => setMenu(null)} />}
    </section>
  );
}
