import { useEffect, useState } from 'preact/hooks';
import {
  gitCheckpoint,
  gitCheckpointRestore,
  gitCheckpointsList,
  gitCommit,
  gitPush,
  gitStatus,
  gitWorktreeApply,
  gitWorktreeRemove,
} from '../runtime/api';
import type { GitCheckpointEntry, GitFileSummary, GitStatusResponse } from '../runtime/types';
import { ContextMenu, SEP, type MenuState } from './ContextMenu';
import { SelectPopover } from './SelectPopover';

export interface DiffTarget {
  project: string;
  worktree: string;
  file: GitFileSummary;
}

interface Props {
  project: string;
  /** The worktree of the conversation in view, shown first. */
  worktree: string;
  openFile: string;
  onOpenDiff: (target: DiffTarget) => void;
}

/** What changed in a project's folder (or a worktree of it): the files, committing them, snapshots, merging a worktree. */
export function ChangesPanel({ project, worktree: given, openFile, onOpenDiff }: Props) {
  const [worktree, setWorktree] = useState(given);
  const [status, setStatus] = useState<GitStatusResponse | null>(null);
  const [snapshots, setSnapshots] = useState<GitCheckpointEntry[]>([]);
  const [chosen, setChosen] = useState<Record<string, boolean>>({});
  const [message, setMessage] = useState('');
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<{ text: string; bad?: boolean } | null>(null);
  const [menu, setMenu] = useState<MenuState | null>(null);

  useEffect(() => setWorktree(given), [given, project]);

  const refresh = async () => {
    if (!project) return;
    setLoading(true);
    try {
      const [res, cps] = await Promise.all([
        gitStatus(project, worktree),
        gitCheckpointsList(project, worktree).catch(() => ({ checkpoints: [] as GitCheckpointEntry[] })),
      ]);
      setStatus(res);
      setSnapshots(cps.checkpoints || []);
      setChosen(Object.fromEntries((res.files || []).map((f) => [f.path, true])));
    } catch (err: any) {
      setNote({ text: err?.message || '读不到 Git 状态', bad: true });
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    setStatus(null);
    setNote(null);
    refresh();
  }, [project, worktree]);

  const run = async (work: () => Promise<string>) => {
    if (busy) return;
    setBusy(true);
    setNote(null);
    try {
      setNote({ text: await work() });
      await refresh();
    } catch (err: any) {
      setNote({ text: err?.message || '没有完成', bad: true });
    } finally {
      setBusy(false);
    }
  };

  if (!project) return <div class="pane-empty">打开一段对话或选一个项目，这里显示它的改动。</div>;
  if (status && !status.is_repo) return <div class="pane-empty">{project} 不是 Git 仓库。</div>;

  const files = status?.files || [];
  const picked = files.filter((f) => chosen[f.path]).map((f) => f.path);
  const adds = files.reduce((a, f) => a + f.additions, 0);
  const dels = files.reduce((a, f) => a + f.deletions, 0);
  const worktrees = (status?.worktrees || []).filter((w) => !w.main);

  const moreMenu = (e: MouseEvent) =>
    setMenu({
      x: e.clientX,
      y: e.clientY,
      items: [
        { label: '保存快照', hint: '不动分支', run: () => run(async () => `已保存快照 ${(await gitCheckpoint(project, `手动 ${new Date().toLocaleTimeString()}`, worktree)).commit || ''}`) },
        ...(snapshots.length
          ? [
              SEP,
              ...snapshots.slice(0, 8).map((cp) => ({
                label: `恢复到 ${cp.label || cp.commit}`,
                hint: new Date(cp.at * (cp.at < 1e12 ? 1000 : 1)).toLocaleString(),
                run: () => {
                  if (window.confirm(`把工作区文件恢复到快照“${cp.label || cp.commit}”？没有保存的改动会被覆盖。`))
                    run(async () => (await gitCheckpointRestore(project, cp.ref, worktree), '已恢复'));
                },
              })),
            ]
          : []),
        ...(worktree
          ? [
              SEP,
              { label: '合并到项目目录', run: () => run(async () => `已合并：${(await gitWorktreeApply(project, worktree, false)).stat || '完成'}`) },
              {
                label: '合并并删除独立工作区',
                run: () =>
                  run(async () => {
                    await gitWorktreeApply(project, worktree, true);
                    setWorktree('');
                    return '已合并并删除独立工作区';
                  }),
              },
              {
                label: '丢弃独立工作区',
                danger: true,
                run: () => {
                  if (window.confirm(`丢弃独立工作区 ${worktree}？没有合并的改动会被删除。`))
                    run(async () => {
                      await gitWorktreeRemove(project, worktree);
                      setWorktree('');
                      return '已丢弃';
                    });
                },
              },
            ]
          : []),
      ],
    });

  return (
    <div class="changes">
      <div class="changes-head">
        <div class="changes-where">
          {worktrees.length > 0 ? (
            <SelectPopover
              compact
              value={worktree}
              onChange={setWorktree}
              options={[
                { value: '', label: status?.branch || '项目目录', sub: '项目目录' },
                ...worktrees.map((w) => ({ value: w.id, label: w.branch || w.id, sub: '独立工作区' })),
              ]}
            />
          ) : (
            <span class="mono strong">{status?.branch || '…'}</span>
          )}
          {status && status.ahead > 0 && <span class="muted small">↑{status.ahead}</span>}
          {status && status.behind > 0 && <span class="muted small">↓{status.behind}</span>}
        </div>
        {status && status.ahead > 0 && (
          <button
            type="button"
            class="btn quiet small"
            disabled={busy}
            title={`把 ${status.ahead} 个提交推送到远端`}
            onClick={() => run(async () => (await gitPush(project, false, worktree), '已推送'))}
          >
            推送
          </button>
        )}
        <button type="button" class="icon-btn" title="刷新" onClick={refresh} disabled={loading}>
          ↻
        </button>
        <button type="button" class="icon-btn" title="快照与工作区" onClick={(e) => moreMenu(e as unknown as MouseEvent)}>
          ⋯
        </button>
      </div>

      {note && <div class={`banner${note.bad ? ' bad' : ''}`}>{note.text}</div>}

      {!status && loading ? (
        <div class="pane-empty">正在读取改动…</div>
      ) : files.length === 0 ? (
        <div class="pane-empty">没有未提交的改动。</div>
      ) : (
        <>
          <div class="changes-sum">
            <label class="check">
              <input
                type="checkbox"
                checked={picked.length === files.length}
                onChange={() =>
                  setChosen(Object.fromEntries(files.map((f) => [f.path, picked.length !== files.length])))
                }
              />
              <span>{files.length} 个文件</span>
            </label>
            <span class="add">+{adds}</span>
            <span class="del">−{dels}</span>
          </div>
          <div class="file-changes">
            {files.map((f) => {
              const slash = f.path.lastIndexOf('/');
              return (
                <div key={f.path} class={`fc-row${openFile === f.path ? ' on' : ''}`}>
                  <input
                    type="checkbox"
                    checked={!!chosen[f.path]}
                    onChange={() => setChosen((c) => ({ ...c, [f.path]: !c[f.path] }))}
                    aria-label={`选中 ${f.path}`}
                  />
                  <button type="button" class="fc-main" onClick={() => onOpenDiff({ project, worktree, file: f })} title={f.path}>
                    <span class={`fc-st st-${f.status === '?' ? 'U' : f.status}`}>{f.status === '?' ? 'U' : f.status}</span>
                    <span class="fc-name">{f.path.slice(slash + 1)}</span>
                    <span class="fc-dir">{slash > 0 ? f.path.slice(0, slash) : ''}</span>
                    <span class="fc-n">
                      {f.additions > 0 && <span class="add">+{f.additions}</span>}
                      {f.deletions > 0 && <span class="del">−{f.deletions}</span>}
                    </span>
                  </button>
                </div>
              );
            })}
          </div>
          <form
            class="commit"
            onSubmit={(e) => {
              e.preventDefault();
              if (!message.trim() || !picked.length) return;
              run(async () => {
                const res = await gitCommit(project, message.trim(), picked.length === files.length ? undefined : picked, worktree);
                setMessage('');
                return `已提交 ${res.commit || ''}`;
              });
            }}
          >
            <textarea
              rows={2}
              placeholder="提交说明"
              value={message}
              onInput={(e) => setMessage((e.target as HTMLTextAreaElement).value)}
            />
            <div class="commit-row">
              <button type="submit" class="btn primary small" disabled={busy || !message.trim() || !picked.length}>
                提交 {picked.length} 个文件
              </button>
            </div>
          </form>
        </>
      )}
      {menu && <ContextMenu {...menu} onClose={() => setMenu(null)} />}
    </div>
  );
}
