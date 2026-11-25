import { useEffect, useRef, useState } from 'preact/hooks';
import {
  gitCheckpoint,
  gitCheckpointRestore,
  gitCheckpointsList,
  gitCommit,
  gitFileDiff,
  gitPush,
  gitStatus,
  gitWorktreeApply,
  gitWorktreeRemove,
} from '../runtime/api';
import { diffPool } from '../runtime/diffPool';
import type { ParsedDiff } from '../runtime/diffWorker';
import type { GitCheckpointEntry, GitFileSummary, GitStatusResponse } from '../runtime/types';
import { SelectPopover } from './SelectPopover';

interface Props {
  project: string;
  initialWorktree?: string;
}

const INITIAL_EAGER_FILES = 4;

interface FileDiffCardProps {
  project: string;
  worktree: string;
  file: GitFileSummary;
  viewMode: 'unified' | 'split';
  eager: boolean;
  checked: boolean;
  onToggleCheck: () => void;
}

function FileDiffCard({ project, worktree, file, viewMode, eager, checked, onToggleCheck }: FileDiffCardProps) {
  const cardRef = useRef<HTMLDivElement | null>(null);
  const [visible, setVisible] = useState(eager);
  const [expanded, setExpanded] = useState(true);
  const [parsed, setParsed] = useState<ParsedDiff | null>(null);
  const [truncated, setTruncated] = useState(false);
  const [binary, setBinary] = useState(file.binary);
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState('');

  useEffect(() => {
    if (visible || !cardRef.current) return;
    if (typeof IntersectionObserver === 'undefined') {
      setVisible(true);
      return;
    }
    const io = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setVisible(true);
          io.disconnect();
        }
      },
      { rootMargin: '600px' },
    );
    io.observe(cardRef.current);
    return () => io.disconnect();
  }, [visible]);

  useEffect(() => {
    if (!visible || !expanded) return;
    let alive = true;
    setLoading(true);
    setErr('');
    gitFileDiff(project, file.path, 'working', worktree)
      .then(async (resp) => {
        if (!alive) return;
        setTruncated(resp.truncated);
        setBinary(resp.binary);
        if (resp.binary || !resp.diff) {
          setParsed({ unified: [], split: [], additions: resp.additions, deletions: resp.deletions });
          return;
        }
        const result = await diffPool.parse(`${project}:${worktree}:${file.path}`, resp.diff);
        if (alive) setParsed(result);
      })
      .catch((e: any) => {
        if (alive) setErr(e?.message || '读取文件差异失败');
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => {
      alive = false;
    };
  }, [visible, expanded, project, worktree, file.path, file.additions, file.deletions]);

  return (
    <div class="diff-file-card" ref={cardRef}>
      <div class="diff-file-head">
        <label class="diff-check">
          <input type="checkbox" checked={checked} onChange={onToggleCheck} />
          <span class={`git-badge st-${file.status === '?' ? 'U' : file.status}`}>{file.status}</span>
        </label>
        <button type="button" class="diff-file-name" onClick={() => setExpanded((v) => !v)}>
          <span>{file.path}</span>
          <span class="diff-counts">
            {file.additions > 0 && <em class="c-add">+{file.additions}</em>}
            {file.deletions > 0 && <em class="c-del">-{file.deletions}</em>}
          </span>
          <span class="caret">{expanded ? '▾' : '▸'}</span>
        </button>
      </div>
      {expanded && (
        <div class="diff-file-body">
          {!visible ? (
            <div class="diff-placeholder">滚动至此自动加载差异…</div>
          ) : loading ? (
            <div class="diff-placeholder">正在解析差异…</div>
          ) : err ? (
            <div class="diff-placeholder is-error">{err}</div>
          ) : binary ? (
            <div class="diff-placeholder">二进制文件已改动</div>
          ) : !parsed || parsed.unified.length === 0 ? (
            <div class="diff-placeholder">无文本差异</div>
          ) : viewMode === 'split' ? (
            <div class="diff-split-table">
              {parsed.split.map((row, idx) =>
                row.kind === 'hunk' ? (
                  <div key={idx} class="diff-hunk">
                    {row.header}
                  </div>
                ) : (
                  <div key={idx} class="diff-split-row">
                    <div class={`diff-cell cell-${row.left?.kind || 'empty'}`}>
                      <span class="ln">{row.left?.oldLine ?? ''}</span>
                      <span class="code">{row.left?.text ?? ''}</span>
                    </div>
                    <div class={`diff-cell cell-${row.right?.kind || 'empty'}`}>
                      <span class="ln">{row.right?.newLine ?? ''}</span>
                      <span class="code">{row.right?.text ?? ''}</span>
                    </div>
                  </div>
                ),
              )}
            </div>
          ) : (
            <div class="diff-unified-table">
              {parsed.unified.map((line, idx) =>
                line.kind === 'hunk' ? (
                  <div key={idx} class="diff-hunk">
                    {line.text}
                  </div>
                ) : (
                  <div key={idx} class={`diff-line kind-${line.kind}`}>
                    <span class="ln">{line.oldLine ?? ''}</span>
                    <span class="ln">{line.newLine ?? ''}</span>
                    <span class="sig">{line.kind === 'add' ? '+' : line.kind === 'del' ? '-' : ' '}</span>
                    <span class="code">{line.text}</span>
                  </div>
                ),
              )}
            </div>
          )}
          {truncated && <div class="diff-truncated">单文件差异超过 256 KB，已截断后文。</div>}
        </div>
      )}
    </div>
  );
}

export function DiffPanel({ project, initialWorktree = '' }: Props) {
  const [worktree, setWorktree] = useState(initialWorktree);
  const [status, setStatus] = useState<GitStatusResponse | null>(null);
  const [checkpoints, setCheckpoints] = useState<GitCheckpointEntry[]>([]);
  const [showCheckpoints, setShowCheckpoints] = useState(false);
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState('');
  const [viewMode, setViewMode] = useState<'unified' | 'split'>('unified');
  const [selectedFiles, setSelectedFiles] = useState<Record<string, boolean>>({});
  const [commitMsg, setCommitMsg] = useState('');
  const [actionBusy, setActionBusy] = useState(false);
  const [feedback, setFeedback] = useState('');

  useEffect(() => {
    setWorktree(initialWorktree);
  }, [initialWorktree, project]);

  const refresh = async () => {
    if (!project) return;
    setLoading(true);
    setErr('');
    try {
      const [res, cpRes] = await Promise.all([
        gitStatus(project, worktree),
        gitCheckpointsList(project, worktree).catch(() => ({ checkpoints: [] })),
      ]);
      setStatus(res);
      setCheckpoints(cpRes.checkpoints || []);
      const nextSel: Record<string, boolean> = {};
      for (const f of res.files || []) nextSel[f.path] = true;
      setSelectedFiles(nextSel);
    } catch (e: any) {
      setErr(e?.message || '读取 Git 状态失败');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    refresh();
  }, [project, worktree]);

  if (!project) {
    return <div class="empty-hint">请先选择一个项目或会话，即可在此查看 Git 变更与 Worktree。</div>;
  }

  const files = status?.files || [];
  const totalAdd = files.reduce((a, f) => a + f.additions, 0);
  const totalDel = files.reduce((a, f) => a + f.deletions, 0);
  const chosenPaths = files.filter((f) => selectedFiles[f.path]).map((f) => f.path);

  const doCommit = async (e: Event) => {
    e.preventDefault();
    if (!commitMsg.trim() || actionBusy) return;
    setActionBusy(true);
    setFeedback('');
    try {
      const res = await gitCommit(project, commitMsg.trim(), chosenPaths.length === files.length ? undefined : chosenPaths, worktree);
      setCommitMsg('');
      setFeedback(`已提交 ${res.commit || ''} (${res.branch || ''})`);
      await refresh();
    } catch (e: any) {
      setFeedback(e?.message || '提交失败');
    } finally {
      setActionBusy(false);
    }
  };

  const doPush = async () => {
    if (actionBusy) return;
    setActionBusy(true);
    setFeedback('');
    try {
      await gitPush(project, false, worktree);
      setFeedback('已推送到远端');
      await refresh();
    } catch (e: any) {
      setFeedback(e?.message || '推送失败');
    } finally {
      setActionBusy(false);
    }
  };

  const doCheckpoint = async () => {
    if (actionBusy) return;
    setActionBusy(true);
    setFeedback('');
    try {
      const res = await gitCheckpoint(project, `review-${new Date().toLocaleTimeString()}`, worktree);
      setFeedback(`已保存隐藏快照 ${res.ref || res.commit}`);
      await refresh();
    } catch (e: any) {
      setFeedback(e?.message || '保存快照失败');
    } finally {
      setActionBusy(false);
    }
  };

  const doRestoreCheckpoint = async (ref: string) => {
    if (actionBusy || !window.confirm(`确定将当前工作区文件恢复到快照 ${ref.split('/').pop()} 吗？未保存的改动将被覆盖。`)) return;
    setActionBusy(true);
    setFeedback('');
    try {
      await gitCheckpointRestore(project, ref, worktree);
      setFeedback(`已恢复到快照 ${ref.split('/').pop()}`);
      await refresh();
    } catch (e: any) {
      setFeedback(e?.message || '恢复快照失败');
    } finally {
      setActionBusy(false);
    }
  };

  const doApplyWorktree = async (removeAfter: boolean) => {
    if (!worktree || actionBusy) return;
    setActionBusy(true);
    setFeedback('');
    try {
      const res = await gitWorktreeApply(project, worktree, removeAfter);
      setFeedback(`已合并回主目录：${res.stat || '完成'}`);
      if (removeAfter) setWorktree('');
      await refresh();
    } catch (e: any) {
      setFeedback(e?.message || '合并 Worktree 失败');
    } finally {
      setActionBusy(false);
    }
  };

  const doRemoveWorktree = async () => {
    if (!worktree || actionBusy || !window.confirm(`确定移除独立工作区 ${worktree} 吗？未合并改动将被清除。`)) return;
    setActionBusy(true);
    setFeedback('');
    try {
      await gitWorktreeRemove(project, worktree);
      setWorktree('');
      setFeedback('已清理独立工作区');
      await refresh();
    } catch (e: any) {
      setFeedback(e?.message || '清理失败');
    } finally {
      setActionBusy(false);
    }
  };

  return (
    <div class="diff-panel">
      <div class="diff-toolbar">
        <div class="diff-meta">
          {status?.is_repo ? (
            <>
              <span class="meta-chip branch">{status.branch || 'HEAD'}</span>
              {status.ahead > 0 && <span class="meta-chip">↑{status.ahead}</span>}
              {status.behind > 0 && <span class="meta-chip">↓{status.behind}</span>}
            </>
          ) : (
            <span class="meta-chip">非 Git 仓库</span>
          )}
        </div>
        <div class="diff-controls">
          {status?.worktrees && status.worktrees.length > 1 && (
            <SelectPopover
              compact
              value={worktree}
              onChange={setWorktree}
              options={[
                { value: '', label: '主工作区', sub: status.branch || '当前分支' },
                ...status.worktrees
                  .filter((w) => !w.main)
                  .map((w) => ({
                    value: w.id,
                    label: `WT: ${w.branch || w.id}`,
                    sub: w.head ? w.head.slice(0, 7) : '隔离工作区',
                  })),
              ]}
            />
          )}
          <button
            type="button"
            class={`chip${viewMode === 'unified' ? ' active' : ''}`}
            onClick={() => setViewMode('unified')}
          >
            统一
          </button>
          <button
            type="button"
            class={`chip${viewMode === 'split' ? ' active' : ''}`}
            onClick={() => setViewMode('split')}
          >
            并排
          </button>
          <button
            type="button"
            class={`chip${showCheckpoints ? ' active' : ''}`}
            onClick={() => setShowCheckpoints((v) => !v)}
          >
            快照 ({checkpoints.length})
          </button>
          <button type="button" class="icon-btn" onClick={refresh} title="刷新变更">
            ↻
          </button>
        </div>
      </div>

      {showCheckpoints && (
        <div class="checkpoints-box">
          <div class="panel-head-row">
            <strong style={{ fontSize: '12px' }}>隐藏快照历史</strong>
            <button type="button" class="primary-btn compact" disabled={actionBusy} onClick={doCheckpoint}>
              + 立即保存
            </button>
          </div>
          {checkpoints.length === 0 ? (
            <div class="muted-code">还没有保存过快照（写入 refs/remote-ai/checkpoints，不污染分支）</div>
          ) : (
            checkpoints.slice(0, 8).map((cp) => (
              <div key={cp.ref} class="checkpoint-row">
                <span class="muted-code" style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>
                  {cp.commit} · {cp.label}
                </span>
                <button
                  type="button"
                  class="ghost-btn compact"
                  disabled={actionBusy}
                  onClick={() => doRestoreCheckpoint(cp.ref)}
                >
                  恢复
                </button>
              </div>
            ))
          )}
        </div>
      )}

      {worktree && (
        <div class="wt-actions-bar">
          <button type="button" class="primary-btn compact" disabled={actionBusy} onClick={() => doApplyWorktree(false)}>
            合并到主目录
          </button>
          <button type="button" class="ghost-btn compact" disabled={actionBusy} onClick={() => doApplyWorktree(true)}>
            合并并清理
          </button>
          <button type="button" class="danger-btn compact" disabled={actionBusy} onClick={doRemoveWorktree}>
            丢弃 Worktree
          </button>
        </div>
      )}

      {status?.is_repo && (
        <div class="diff-stats-bar">
          <span>{files.length} 个变更文件</span>
          <em class="c-add">+{totalAdd}</em>
          <em class="c-del">-{totalDel}</em>
          <button type="button" class="ghost-btn compact" disabled={actionBusy} onClick={doCheckpoint}>
            保存快照
          </button>
        </div>
      )}

      {status?.is_repo && files.length > 0 && (
        <form class="commit-box" onSubmit={doCommit}>
          <input
            type="text"
            placeholder="输入 commit message…"
            value={commitMsg}
            onInput={(e) => setCommitMsg((e.target as HTMLInputElement).value)}
          />
          <div class="commit-actions">
            <button type="submit" class="primary-btn compact" disabled={actionBusy || !commitMsg.trim() || chosenPaths.length === 0}>
              提交 ({chosenPaths.length})
            </button>
            <button type="button" class="ghost-btn compact" disabled={actionBusy} onClick={doPush}>
              推送
            </button>
          </div>
        </form>
      )}

      {feedback && <div class="panel-feedback">{feedback}</div>}
      {err && <div class="panel-feedback is-error">{err}</div>}
      {loading && !status && <div class="empty-hint">正在检查工作区变更…</div>}

      {status?.is_repo && files.length === 0 && !loading && (
        <div class="empty-hint">当前工作区干净，没有未提交修改。</div>
      )}

      <div class="diff-files">
        {files.map((file, idx) => (
          <FileDiffCard
            key={`${worktree}:${file.path}`}
            project={project}
            worktree={worktree}
            file={file}
            viewMode={viewMode}
            eager={idx < INITIAL_EAGER_FILES}
            checked={!!selectedFiles[file.path]}
            onToggleCheck={() => setSelectedFiles((p) => ({ ...p, [file.path]: !p[file.path] }))}
          />
        ))}
      </div>
    </div>
  );
}
