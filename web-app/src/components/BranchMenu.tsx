import { useEffect, useRef, useState } from 'preact/hooks';
import { gitBranches, gitCheckout } from '../runtime/api';
import type { GitBranchEntry } from '../runtime/types';

interface Props {
  project: string;
  worktree: string;
  /** What the conversation says it is on, shown until git has answered. */
  known: string;
  onChanged?: (branch: string) => void;
}

/** The branch a project (or a conversation's own worktree) is on, and switching or making one. */
export function BranchMenu({ project, worktree, known, onChanged }: Props) {
  const [current, setCurrent] = useState(known);
  const [open, setOpen] = useState(false);
  const [list, setList] = useState<GitBranchEntry[]>([]);
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState('');
  const host = useRef<HTMLDivElement | null>(null);

  const load = async () => {
    try {
      const res = await gitBranches(project, worktree);
      setCurrent(res.current || '');
      setList(res.branches || []);
    } catch {
      setList([]);
    }
  };

  useEffect(() => {
    setCurrent(known);
    setOpen(false);
    if (project) load();
  }, [project, worktree]);

  useEffect(() => {
    if (!open) return;
    const away = (e: MouseEvent) => {
      if (host.current && !host.current.contains(e.target as Node)) setOpen(false);
    };
    window.addEventListener('mousedown', away);
    return () => window.removeEventListener('mousedown', away);
  }, [open]);

  if (!project || !current) return null;

  const go = async (branch: string, create: boolean) => {
    if (!branch.trim() || busy) return;
    setBusy(true);
    setProblem('');
    try {
      const res = await gitCheckout(project, branch.trim(), create, worktree);
      const now = res.branch || branch.trim();
      setCurrent(now);
      setName('');
      setOpen(false);
      onChanged?.(now);
      load();
    } catch (err: any) {
      setProblem(err?.message || '切换分支失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div class="branch" ref={host}>
      <button
        type="button"
        class="chip mono"
        title={worktree ? '这段对话独立工作区的分支' : '项目当前分支'}
        onClick={() => {
          setOpen((v) => !v);
          if (!open) load();
        }}
      >
        <svg viewBox="0 0 20 20" width="12" height="12" aria-hidden="true">
          <circle cx="6" cy="5" r="1.8" fill="none" stroke="currentColor" stroke-width="1.5" />
          <circle cx="6" cy="15" r="1.8" fill="none" stroke="currentColor" stroke-width="1.5" />
          <circle cx="14" cy="7" r="1.8" fill="none" stroke="currentColor" stroke-width="1.5" />
          <path d="M6 7v6M14 9c0 3-4 3-8 4" fill="none" stroke="currentColor" stroke-width="1.5" />
        </svg>
        {current}
        {worktree ? <span class="chip-note">独立</span> : null}
      </button>
      {open && (
        <div class="pop branch-pop">
          <form
            class="branch-new"
            onSubmit={(e) => {
              e.preventDefault();
              go(name, true);
            }}
          >
            <input
              type="text"
              placeholder="新分支名"
              value={name}
              onInput={(e) => setName((e.target as HTMLInputElement).value)}
            />
            <button type="submit" class="btn primary small" disabled={busy || !name.trim()}>
              新建
            </button>
          </form>
          {problem && <div class="pop-note bad">{problem}</div>}
          <div class="branch-list">
            {list.map((b) => (
              <button
                key={b.name}
                type="button"
                class={`pop-item${b.current ? ' on' : ''}`}
                disabled={busy || b.current}
                onClick={() => go(b.name, false)}
                title={b.subject}
              >
                <span class="mono">{b.name}</span>
                <span class="pop-sub">{b.head}</span>
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
