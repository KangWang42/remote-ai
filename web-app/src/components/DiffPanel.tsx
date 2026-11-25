import { useEffect, useRef, useState } from 'preact/hooks';
import {
  gitCheckpoint,
  gitCommit,
  gitFileDiff,
  gitPush,
  gitStatus,
  gitWorktreeApply,
  gitWorktreeRemove,
} from '../runtime/api';
import { diffPool } from '../runtime/diffPool';
import type { ParsedDiff } from '../runtime/diffWorker';
import type { GitFileSummary, GitStatusResponse } from '../runtime/types';

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
                  <div key={idx} class={`diff-u-row cell-${line.kind}`}>
                    <span class="ln">{line.oldLine ?? ''}</span>
                    <span class="ln">{line.newLine ?? ''}</span>
                    <span class="sig">{line.kind === 'add' ? '+' : line.kind === 'del' ? '-' : ' '}</span>
                    <span class="code">{line.text}</span>
                  </div>
                ),
              )}
            </div>
          )}
          {truncated && <div class="diff-placeholder">差异较大，已按预算截断显示</div>}
        </div>
      )}
    </div>
  );
}

export function DiffPanel({ project, initialWorktree = '' }: Props) {
  const [worktree, setWorktree] = useState(initialWorktree);
  const [status, setStatus] = useState<GitStatusResponse | null>(null);
  const [viewMode, setViewMode] = useState<'unified' | 'split'>(() => (window.innerWidth >= 1280 ? 'split' : 'unified'));
  const [selectedFiles, setSelectedFiles] = useState<Record<string, boolean>>({});
  const [commitMsg, setCommitMsg] = useState('');
  const [loading, setLoading] = useState(false);
  const [busyAction, setBusyAction] = useState('');
  const [feedback, setFeedback] = useState('');

  useEffect(() => {
    setWorktree(initialWorktree);
  }, [initialWorktree, project]);

  const reload = async (wt = worktree) => {
    if (!project) return;
    setLoading(true);
    setFeedback('');
    try {
      const resp = await gitStatus(project, wt);
      setStatus(resp);
      const nextSel: Record<string, boolean> = {};
      for (const f of resp.files || []) nextSel[f.path] = true;
      setSelectedFiles(nextSel);
    } catch (e: any) {
      setFeedback(e?.message || '无法读取 Git 状态');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    reload(worktree);
  }, [project, worktree]);

  const chosenPaths = (status?.files || []).filter((f) => selectedFiles[f.path]).map((f) => f.path);

  const runCommit = async (e: Event) => {
    e.preventDefault();
    if (!commitMsg.trim() || busyAction) return;
    setBusyAction('commit');
    setFeedback('');
    try {
      const res = await gitCommit(project, commitMsg.trim(), chosenPaths.length > 0 ? chosenPaths : undefined, worktree);
      setCommitMsg('');
      setFeedback(`已提交 ${res.commit || ''} 到 ${res.branch || status?.branch || ''}`);
      await reload(worktree);
    } catch (err: any) {
      setFeedback(err?.message || '提交失败');
    } finally {
      setBusyAction('');
    }
  };

  const runPush = async () => {
    if (busyAction) return;
    setBusyAction('push');
    setFeedback('');
    try {
      const res = await gitPush(project, true, worktree);
      setFeedback(res.output || '推送完成');
      await reload(worktree);
    } catch (err: any) {
      setFeedback(err?.message || '推送失败');
    } finally {
      setBusyAction('');
    }
  };

  const runCheckpoint = async () => {
    if (busyAction) return;
    setBusyAction('cp');
    setFeedback('');
    try {
      const res = await gitCheckpoint(project, `review ${new Date().toLocaleTimeString()}`, worktree);
      setFeedback(`已保存快照 ${res.id}`);
    } catch (err: any) {
      setFeedback(err?.message || '快照失败');
    } finally {
      setBusyAction('');
    }
  };

  const runApplyWorktree = async () => {
    if (!worktree || busyAction) return;
    setBusyAction('apply');
    setFeedback('');
    try {
      const res = await gitWorktreeApply(project, worktree, true);
      setFeedback(`已合并回主分支并清理 Worktree：${res.stat || ''}`);
      setWorktree('');
      await reload('');
    } catch (err: any) {
      setFeedback(err?.message || '合并 Worktree 失败');
    } finally {
      setBusyAction('');
    }
  };

  const runRemoveWorktree = async () => {
    if (!worktree || busyAction) return;
    setBusyAction('remove');
    setFeedback('');
    try {
      await gitWorktreeRemove(project, worktree);
      setFeedback('已安全移除 Worktree');
      setWorktree('');
      await reload('');
    } catch (err: any) {
      setFeedback(err?.message || '清理 Worktree 失败');
    } finally {
      setBusyAction('');
    }
  };

  if (!project) return <div class="empty-hint">选择一个项目或对话以查看 Git Diff 与变更</div>;

  return (
    <div class="diff-panel">
      <div class="diff-toolbar">
        <div class="diff-branch-meta">
          <span class="branch-chip">{status?.branch || '未检测分支'}</span>
          {status?.head && <span class="muted-code">{status.head}</span>}
          {status && (status.ahead > 0 || status.behind > 0) && (
            <span class="muted-code">
              ↑{status.ahead} ↓{status.behind}
            </span>
          )}
        </div>
        <div class="diff-tool-btns">
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
            分栏
          </button>
          <button type="button" class="ghost-btn compact" disabled={!!busyAction} onClick={runCheckpoint}>
            快照
          </button>
          <button type="button" class="ghost-btn compact" disabled={loading} onClick={() => reload(worktree)}>
            {loading ? '…' : '刷新'}
          </button>
        </div>
      </div>

      {status && status.worktrees.length > 1 && (
        <div class="wt-bar">
          <select value={worktree} onChange={(e) => setWorktree((e.target as HTMLSelectElement).value)}>
            <option value="">主工作区 ({status.worktrees.find((w) => w.main)?.branch || 'main'})</option>
            {status.worktrees
              .filter((w) => w.managed)
              .map((w) => (
                <option key={w.id} value={w.id}>
                  Worktree · {w.branch || w.id}
                </option>
              ))}
          </select>
          {worktree && (
            <div class="wt-actions">
              <button type="button" class="primary-btn compact" disabled={!!busyAction} onClick={runApplyWorktree}>
                合并并清理
              </button>
              <button type="button" class="danger-btn compact" disabled={!!busyAction} onClick={runRemoveWorktree}>
                丢弃
              </button>
            </div>
          )}
        </div>
      )}

      {feedback && <div class="panel-feedback">{feedback}</div>}

      {status && !status.is_repo ? (
        <div class="empty-hint">项目“{project}”不是 Git 仓库</div>
      ) : (
        <>
          <form class="commit-box" onSubmit={runCommit}>
            <input
              type="text"
              placeholder="输入提交说明（例如 feat: optimize route selection）"
              value={commitMsg}
              onInput={(e) => setCommitMsg((e.target as HTMLInputElement).value)}
            />
            <div class="commit-actions">
              <span class="muted-code">已选 {chosenPaths.length} / {status?.files.length || 0} 文件</span>
              <div class="commit-btns">
                <button
                  type="submit"
                  class="primary-btn compact"
                  disabled={!commitMsg.trim() || !status?.files.length || !!busyAction}
                >
                  {busyAction === 'commit' ? '提交中…' : 'Commit'}
                </button>
                <button type="button" class="ghost-btn compact" disabled={!!busyAction} onClick={runPush}>
                  {busyAction === 'push' ? '推送中…' : 'Push'}
                </button>
              </div>
            </div>
          </form>

          <div class="diff-list">
            {!status || status.files.length === 0 ? (
              <div class="empty-hint">{loading ? '正在读取变更…' : '工作区干净，没有未提交的改动'}</div>
            ) : (
              status.files.map((file, idx) => (
                <FileDiffCard
                  key={`${worktree}:${file.path}`}
                  project={project}
                  worktree={worktree}
                  file={file}
                  viewMode={viewMode}
                  eager={idx < INITIAL_EAGER_FILES}
                  checked={!!selectedFiles[file.path]}
                  onToggleCheck={() => setSelectedFiles((prev) => ({ ...prev, [file.path]: !prev[file.path] }))}
                />
              ))
            )}
          </div>
        </>
      )}
    </div>
  );
}
