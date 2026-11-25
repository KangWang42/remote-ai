import { useEffect, useState } from 'preact/hooks';
import { gitFileDiff } from '../runtime/api';
import { diffPool } from '../runtime/diffPool';
import type { ParsedDiff } from '../runtime/diffWorker';
import type { DiffTarget } from './ChangesPanel';

interface Props {
  target: DiffTarget;
  onClose: () => void;
}

/** One changed file, in the middle of the window where it has room: line by line, or side by side. */
export function DiffView({ target, onClose }: Props) {
  const { project, worktree, file } = target;
  const [parsed, setParsed] = useState<ParsedDiff | null>(null);
  const [truncated, setTruncated] = useState(false);
  const [binary, setBinary] = useState(false);
  const [problem, setProblem] = useState('');
  const [split, setSplit] = useState(() => {
    try {
      return localStorage.getItem('rcli-diff-split') === '1';
    } catch {
      return false;
    }
  });

  useEffect(() => {
    let alive = true;
    setParsed(null);
    setProblem('');
    gitFileDiff(project, file.path, 'working', worktree)
      .then(async (res) => {
        if (!alive) return;
        setTruncated(res.truncated);
        setBinary(res.binary);
        if (res.binary || !res.diff) {
          setParsed({ unified: [], split: [], additions: res.additions, deletions: res.deletions });
          return;
        }
        const out = await diffPool.parse(`${project}:${worktree}:${file.path}`, res.diff);
        if (alive) setParsed(out);
      })
      .catch((err: any) => alive && setProblem(err?.message || '读不到这个文件的改动'));
    return () => {
      alive = false;
    };
  }, [project, worktree, file.path, file.additions, file.deletions]);

  const setView = (v: boolean) => {
    setSplit(v);
    try {
      localStorage.setItem('rcli-diff-split', v ? '1' : '0');
    } catch {
      // the choice lasts as long as the page
    }
  };

  return (
    <main class="diff-view">
      <header class="thread-head">
        <div class="head-main">
          <span class={`fc-st st-${file.status === '?' ? 'U' : file.status}`}>{file.status === '?' ? 'U' : file.status}</span>
          <h1 class="head-title mono" title={file.path}>
            {file.path}
          </h1>
          <span class="add">+{file.additions}</span>
          <span class="del">−{file.deletions}</span>
        </div>
        <div class="head-actions">
          <div class="seg small">
            <button type="button" class={!split ? 'on' : ''} onClick={() => setView(false)}>
              逐行
            </button>
            <button type="button" class={split ? 'on' : ''} onClick={() => setView(true)}>
              并排
            </button>
          </div>
          <button type="button" class="btn quiet small" onClick={onClose}>
            关闭
          </button>
        </div>
      </header>
      <div class="diff-body">
        {problem ? (
          <div class="pane-empty">{problem}</div>
        ) : !parsed ? (
          <div class="pane-empty">正在读取…</div>
        ) : binary ? (
          <div class="pane-empty">二进制文件，无法逐行比较。</div>
        ) : parsed.unified.length === 0 ? (
          <div class="pane-empty">没有文字改动。</div>
        ) : split ? (
          <div class="diff-grid split">
            {parsed.split.map((row, i) =>
              row.kind === 'hunk' ? (
                <div key={i} class="d-hunk">
                  {row.header}
                </div>
              ) : (
                <div key={i} class="d-pair">
                  <div class={`d-line k-${row.left?.kind || 'none'}`}>
                    <span class="d-n">{row.left?.oldLine ?? ''}</span>
                    <span class="d-code">{row.left?.text ?? ''}</span>
                  </div>
                  <div class={`d-line k-${row.right?.kind || 'none'}`}>
                    <span class="d-n">{row.right?.newLine ?? ''}</span>
                    <span class="d-code">{row.right?.text ?? ''}</span>
                  </div>
                </div>
              ),
            )}
          </div>
        ) : (
          <div class="diff-grid">
            {parsed.unified.map((line, i) =>
              line.kind === 'hunk' ? (
                <div key={i} class="d-hunk">
                  {line.text}
                </div>
              ) : (
                <div key={i} class={`d-line k-${line.kind}`}>
                  <span class="d-n">{line.oldLine ?? ''}</span>
                  <span class="d-n">{line.newLine ?? ''}</span>
                  <span class="d-sig">{line.kind === 'add' ? '+' : line.kind === 'del' ? '−' : ''}</span>
                  <span class="d-code">{line.text}</span>
                </div>
              ),
            )}
          </div>
        )}
        {truncated && <div class="pane-empty">文件改动超过 256 KB，后面的部分没有显示。</div>}
      </div>
    </main>
  );
}
