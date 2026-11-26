import { useEffect, useMemo, useState } from 'preact/hooks';
import { fileViewerUrl, gitStatus, listFiles, readFileBytes } from '../runtime/api';
import { formatExactTime, formatRelativeTime, getRecencyTier, isToday, normalizeMs } from '../runtime/time';
import type { FileEntry, FilePreviewTarget, GitFileSummary } from '../runtime/types';
import type { DiffTarget } from './ChangesPanel';
import { MarkdownView } from './MarkdownView';
import { SelectPopover } from './SelectPopover';

type PreviewKind = 'markdown' | 'image' | 'rich' | 'text';

const IMAGE_EXTS: Record<string, string> = {
  png: 'image/png',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  webp: 'image/webp',
  gif: 'image/gif',
  svg: 'image/svg+xml',
  bmp: 'image/bmp',
  ico: 'image/x-icon',
};

const RICH_EXTS = new Set(['pdf', 'docx', 'xlsx', 'xls', 'pptx']);
const MD_EXTS = new Set(['md', 'mdx', 'markdown']);

function fileExt(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot > 0 ? name.slice(dot + 1).toLowerCase() : '';
}

function detectKind(name: string): PreviewKind {
  const ext = fileExt(name);
  if (MD_EXTS.has(ext)) return 'markdown';
  if (IMAGE_EXTS[ext]) return 'image';
  if (RICH_EXTS.has(ext)) return 'rich';
  return 'text';
}

function fileBadge(entry: FileEntry): { tag: string; cls: string } {
  if (entry.dir) return { tag: 'DIR', cls: 'ft-dir' };
  const ext = fileExt(entry.name);
  if (MD_EXTS.has(ext)) return { tag: 'MD', cls: 'ft-md' };
  if (IMAGE_EXTS[ext]) return { tag: 'IMG', cls: 'ft-img' };
  if (ext === 'pdf') return { tag: 'PDF', cls: 'ft-doc' };
  if (ext === 'docx' || ext === 'doc') return { tag: 'DOC', cls: 'ft-doc' };
  if (ext === 'xlsx' || ext === 'xls' || ext === 'csv') return { tag: 'XLS', cls: 'ft-xls' };
  if (ext === 'pptx') return { tag: 'PPT', cls: 'ft-doc' };
  if (['ts', 'tsx', 'js', 'jsx', 'mjs', 'cjs'].includes(ext)) return { tag: ext.toUpperCase().slice(0, 3), cls: 'ft-code' };
  if (['py', 'cs', 'java', 'swift', 'rs', 'go', 'r', 'cpp', 'c', 'h', 'sh', 'ps1'].includes(ext)) {
    return { tag: ext.toUpperCase().slice(0, 3), cls: 'ft-code' };
  }
  if (['json', 'yaml', 'yml', 'toml', 'xml', 'css', 'html', 'ini', 'env'].includes(ext)) {
    return { tag: ext.toUpperCase().slice(0, 3), cls: 'ft-cfg' };
  }
  return { tag: (ext || 'TXT').toUpperCase().slice(0, 3), cls: 'ft-txt' };
}

function formatSize(bytes?: number): string {
  if (bytes === undefined || bytes < 0) return '';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes < 10240 ? 1 : 0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function looksBinary(bytes: Uint8Array): boolean {
  const limit = Math.min(bytes.byteLength, 4096);
  let suspicious = 0;
  for (let i = 0; i < limit; i++) {
    const b = bytes[i];
    if (b === 0) return true;
    if (b < 7 || (b > 14 && b < 32)) suspicious++;
  }
  return limit > 0 && suspicious / limit > 0.2;
}

interface PreviewProps {
  target: FilePreviewTarget;
  expanded?: boolean;
  onBack?: () => void;
  onToggleExpand?: () => void;
  onCiteFile?: (relPath: string, projectDir: string) => void;
}

/** Inline or full-center live previewer for code, Markdown, images and rich Office/PDF documents. */
export function FilePreviewBody({ target, expanded = false, onBack, onToggleExpand, onCiteFile }: PreviewProps) {
  const kind = detectKind(target.name);
  const fullPath = target.path ? `${target.path}/${target.name}` : target.name;
  const viewerUrl = fileViewerUrl(target.dir, target.path, target.name);

  const [loading, setLoading] = useState(kind !== 'rich');
  const [err, setErr] = useState('');
  const [text, setText] = useState('');
  const [isBinary, setIsBinary] = useState(false);
  const [truncated, setTruncated] = useState(false);
  const [size, setSize] = useState(0);
  const [imgUrl, setImgUrl] = useState('');
  const [mdMode, setMdMode] = useState<'rendered' | 'source'>('rendered');
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    setErr('');
    setText('');
    setIsBinary(false);
    setTruncated(false);
    setSize(0);
    setCopied(false);

    if (kind === 'rich') {
      setLoading(false);
      return;
    }

    const controller = new AbortController();
    let alive = true;
    let objectUrl = '';
    setLoading(true);

    const maxBytes = kind === 'image' ? 6 * 1024 * 1024 : 512 * 1024;
    readFileBytes(target.dir, fullPath, maxBytes, controller.signal)
      .then((res) => {
        if (!alive) return;
        setSize(res.size);
        setTruncated(res.truncated);
        if (kind === 'image') {
          const mime = IMAGE_EXTS[fileExt(target.name)] || 'application/octet-stream';
          const copy = new Uint8Array(res.bytes.byteLength);
          copy.set(res.bytes);
          objectUrl = URL.createObjectURL(new Blob([copy], { type: mime }));
          setImgUrl(objectUrl);
          return;
        }
        if (looksBinary(res.bytes)) {
          setIsBinary(true);
          return;
        }
        const decoder = new TextDecoder('utf-8', { fatal: false });
        setText(decoder.decode(res.bytes));
      })
      .catch((e: any) => {
        if (!alive || e?.name === 'AbortError') return;
        setErr(e?.message || '读取文件失败');
      })
      .finally(() => {
        if (alive) setLoading(false);
      });

    return () => {
      alive = false;
      controller.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      setImgUrl('');
    };
  }, [target.dir, fullPath, kind]);

  const lines = useMemo(() => {
    if (!text) return [];
    const arr = text.split(/\r?\n/);
    if (arr.length > 1 && arr[arr.length - 1] === '') arr.pop();
    return arr;
  }, [text]);

  const handleCopy = async () => {
    if (!text) return;
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1600);
    } catch {
      // ignore
    }
  };

  return (
    <div class={`fpreview${expanded ? ' is-expanded' : ''}`}>
      <header class="fpreview-head">
        <div class="fpreview-title-wrap">
          {onBack && (
            <button type="button" class="ghost-btn compact" onClick={onBack}>
              ← {expanded ? '返回对话' : '返回目录'}
            </button>
          )}
          <div class="fpreview-meta">
            <strong class="fpreview-name" title={fullPath}>
              {target.name}
            </strong>
            <span class="fpreview-sub">
              {target.dir}/{fullPath}
              {size > 0 ? ` · ${formatSize(size)}` : ''}
              {lines.length > 0 ? ` · ${lines.length} 行` : ''}
            </span>
          </div>
        </div>

        <div class="fpreview-actions">
          {kind === 'markdown' && !loading && !err && (
            <div class="seg-mini">
              <button
                type="button"
                class={mdMode === 'rendered' ? 'active' : ''}
                onClick={() => setMdMode('rendered')}
              >
                预览
              </button>
              <button
                type="button"
                class={mdMode === 'source' ? 'active' : ''}
                onClick={() => setMdMode('source')}
              >
                源码
              </button>
            </div>
          )}
          {onCiteFile && (
            <button
              type="button"
              class="primary-btn compact"
              title="将此文件路径插入当前对话输入框，方便让 AI 检查或修改"
              onClick={() => onCiteFile(fullPath, target.dir)}
            >
              引用给 AI
            </button>
          )}
          {text && (
            <button type="button" class="ghost-btn compact" onClick={handleCopy}>
              {copied ? '已复制' : '复制'}
            </button>
          )}
          {onToggleExpand && (
            <button type="button" class="ghost-btn compact" onClick={onToggleExpand}>
              {expanded ? '收起到右侧' : '大屏预览'}
            </button>
          )}
          <a class="ghost-btn compact" href={viewerUrl} target="_blank" rel="noreferrer" title="在独立查看器打开">
            ↗
          </a>
        </div>
      </header>

      <div class="fpreview-body">
        {loading ? (
          <div class="empty-hint">正在读取文件内容…</div>
        ) : err ? (
          <div class="panel-feedback is-error" style={{ margin: '14px' }}>
            {err}
          </div>
        ) : kind === 'rich' ? (
          <iframe src={viewerUrl} title={target.name} class="fpreview-iframe" />
        ) : kind === 'image' ? (
          <div class="fpreview-image-stage">
            {imgUrl ? <img src={imgUrl} alt={target.name} /> : <div class="empty-hint">图片无法显示</div>}
          </div>
        ) : isBinary ? (
          <div class="empty-hint">
            <p>该文件为二进制内容，无法以纯文本显示。</p>
            <a class="ghost-btn compact" href={viewerUrl} target="_blank" rel="noreferrer">
              使用完整查看器打开
            </a>
          </div>
        ) : kind === 'markdown' && mdMode === 'rendered' ? (
          <div class="fpreview-md">
            <MarkdownView text={text} />
          </div>
        ) : (
          <div class="fpreview-code">
            {lines.length === 0 ? (
              <div class="empty-hint">（空文件）</div>
            ) : (
              lines.map((line, i) => (
                <div key={i} class="code-row">
                  <span class="code-ln">{i + 1}</span>
                  <span class="code-tx">{line || ' '}</span>
                </div>
              ))
            )}
          </div>
        )}
        {truncated && (
          <div class="diff-truncated">
            文件较大，已预览前 {kind === 'image' ? '6 MB' : '512 KB'}；可点击右上角 ↗ 在完整查看器查看全部。
          </div>
        )}
      </div>
    </div>
  );
}

type FileSortMode = 'name' | 'modified';
type FileFilterMode = 'all' | 'today' | 'changed';

const SORT_KEY = 'rcli-console-files-sort-v1';
function loadFileSort(): FileSortMode {
  try {
    return localStorage.getItem(SORT_KEY) === 'modified' ? 'modified' : 'name';
  } catch {
    return 'name';
  }
}
function saveFileSort(mode: FileSortMode) {
  try {
    localStorage.setItem(SORT_KEY, mode);
  } catch {
    // kept for this page only
  }
}

const GIT_STATUS_TITLE: Record<string, string> = {
  M: 'Git 已修改',
  A: 'Git 新增',
  D: 'Git 已删除',
  R: 'Git 重命名',
  '?': 'Git 未跟踪新文件',
  U: 'Git 未跟踪新文件',
};

interface ExplorerProps {
  workspaces: string[];
  activeProject: string;
  worktree?: string;
  onExpandPreview: (target: FilePreviewTarget) => void;
  onOpenDiff?: (target: DiffTarget) => void;
  onCiteFile?: (relPath: string, projectDir: string) => void;
}

/** Project file explorer with recency & Git change badges, sort/filter controls, breadcrumbs, and live preview. */
export function FileExplorer({ workspaces, activeProject, worktree = '', onExpandPreview, onOpenDiff, onCiteFile }: ExplorerProps) {
  const [browseDir, setBrowseDir] = useState(activeProject || workspaces[0] || '');
  const [folderPath, setFolderPath] = useState('');
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const [gitFilesList, setGitFilesList] = useState<GitFileSummary[]>([]);
  const [more, setMore] = useState(false);
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState('');
  const [search, setSearch] = useState('');
  const [showHidden, setShowHidden] = useState(false);
  const [sortMode, setSortMode] = useState<FileSortMode>(loadFileSort);
  const [filterMode, setFilterMode] = useState<FileFilterMode>('all');
  const [previewTarget, setPreviewTarget] = useState<FilePreviewTarget | null>(null);
  const [tick, setTick] = useState(0);
  const [now, setNow] = useState(() => Date.now());

  const activeWorktree = browseDir === activeProject ? worktree : '';

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 30000);
    return () => window.clearInterval(timer);
  }, []);

  // Follow active project switch automatically when user jumps between projects
  useEffect(() => {
    if (activeProject && activeProject !== browseDir) {
      setBrowseDir(activeProject);
      setFolderPath('');
      setPreviewTarget(null);
      setSearch('');
      setFilterMode('all');
    } else if (!browseDir && workspaces[0]) {
      setBrowseDir(workspaces[0]);
    }
  }, [activeProject, workspaces.join('|')]);

  useEffect(() => {
    if (!browseDir) {
      setEntries([]);
      setGitFilesList([]);
      return;
    }
    let alive = true;
    setLoading(true);
    setErr('');
    setNow(Date.now());
    listFiles(browseDir, folderPath)
      .then((res) => {
        if (!alive) return;
        setEntries(res.entries || []);
        setMore(!!res.more);
      })
      .catch((e: any) => {
        if (!alive) return;
        setEntries([]);
        setErr(e?.message || '无法读取目录');
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => {
      alive = false;
    };
  }, [browseDir, folderPath, tick]);

  // Fetch Git status for the selected project to surface uncommitted file & directory badges
  useEffect(() => {
    if (!browseDir) {
      setGitFilesList([]);
      return;
    }
    let alive = true;
    gitStatus(browseDir, activeWorktree)
      .then((res) => {
        if (!alive) return;
        setGitFilesList(res?.is_repo ? res.files || [] : []);
      })
      .catch(() => {
        if (alive) setGitFilesList([]);
      });
    return () => {
      alive = false;
    };
  }, [browseDir, activeWorktree, tick]);

  const { gitFileMap, gitDirCounts } = useMemo(() => {
    const files = new Map<string, GitFileSummary>();
    const dirs = new Map<string, number>();
    for (const f of gitFilesList) {
      const norm = f.path.replace(/\\/g, '/').replace(/^\/+/, '');
      files.set(norm, f);
      const parts = norm.split('/');
      for (let i = 1; i < parts.length; i++) {
        const prefix = parts.slice(0, i).join('/');
        dirs.set(prefix, (dirs.get(prefix) || 0) + 1);
      }
    }
    return { gitFileMap: files, gitDirCounts: dirs };
  }, [gitFilesList]);

  const visibleBase = useMemo(() => {
    const q = search.trim().toLowerCase();
    return entries.filter((e) => (showHidden || !e.hidden) && (!q || e.name.toLowerCase().includes(q)));
  }, [entries, showHidden, search]);

  const entryGitState = (entry: FileEntry) => {
    const rel = folderPath ? `${folderPath}/${entry.name}` : entry.name;
    const gitFile = !entry.dir ? gitFileMap.get(rel) : undefined;
    const dirChanges = entry.dir ? gitDirCounts.get(rel) || 0 : 0;
    return { rel, gitFile, dirChanges, hasChange: Boolean(gitFile) || dirChanges > 0 };
  };

  const todayCount = useMemo(
    () => visibleBase.filter((e) => isToday(e.modified, now)).length,
    [visibleBase, now],
  );
  const changedCount = useMemo(
    () => visibleBase.filter((e) => entryGitState(e).hasChange).length,
    [visibleBase, folderPath, gitFileMap, gitDirCounts],
  );

  const filtered = useMemo(() => {
    const list = visibleBase.filter((e) => {
      if (filterMode === 'today') return isToday(e.modified, now);
      if (filterMode === 'changed') return entryGitState(e).hasChange;
      return true;
    });
    return list.sort((a, b) => {
      if (a.dir !== b.dir) return a.dir ? -1 : 1;
      if (sortMode === 'modified') {
        const diff = normalizeMs(b.modified) - normalizeMs(a.modified);
        if (diff !== 0) return diff;
      }
      return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
    });
  }, [visibleBase, filterMode, sortMode, now, folderPath, gitFileMap, gitDirCounts]);

  const hiddenCount = useMemo(() => entries.filter((e) => e.hidden).length, [entries]);

  const toggleSort = () => {
    const next: FileSortMode = sortMode === 'name' ? 'modified' : 'name';
    setSortMode(next);
    saveFileSort(next);
  };

  if (!browseDir) {
    return <div class="empty-hint">请先在左侧添加至少一个项目文件夹，即可在此快速浏览与预览所有文件。</div>;
  }

  if (previewTarget) {
    return (
      <FilePreviewBody
        target={previewTarget}
        onBack={() => setPreviewTarget(null)}
        onToggleExpand={() => {
          const t = previewTarget;
          setPreviewTarget(null);
          onExpandPreview(t);
        }}
        onCiteFile={onCiteFile}
      />
    );
  }

  const crumbs = folderPath ? folderPath.split('/').filter(Boolean) : [];
  const projectOptions = (workspaces.length > 0 ? workspaces : [browseDir]).map((w) => ({
    value: w,
    label: w,
  }));

  return (
    <div class="files-panel">
      <div class="files-top-row">
        <div style={{ flex: 1, minWidth: 0 }}>
          <SelectPopover
            compact
            value={browseDir}
            options={projectOptions}
            onChange={(next) => {
              setBrowseDir(next);
              setFolderPath('');
              setSearch('');
              setFilterMode('all');
              setPreviewTarget(null);
            }}
          />
        </div>
        <button
          type="button"
          class={`icon-btn${showHidden ? ' is-on' : ''}`}
          title={showHidden ? '隐藏点文件' : `显示隐藏文件 (${hiddenCount})`}
          onClick={() => setShowHidden((v) => !v)}
        >
          {showHidden ? '显隐' : '隐藏'}
        </button>
        <button type="button" class="icon-btn" title="刷新目录" onClick={() => setTick((n) => n + 1)}>
          ↻
        </button>
      </div>

      <div class="files-crumb">
        <button
          type="button"
          class={`crumb-btn${crumbs.length === 0 ? ' current' : ''}`}
          onClick={() => {
            setFolderPath('');
            setSearch('');
          }}
        >
          {browseDir}
        </button>
        {crumbs.map((seg, idx) => {
          const target = crumbs.slice(0, idx + 1).join('/');
          const isLast = idx === crumbs.length - 1;
          return (
            <span key={target} class="crumb-seg">
              <span class="crumb-sep">/</span>
              <button
                type="button"
                class={`crumb-btn${isLast ? ' current' : ''}`}
                disabled={isLast}
                onClick={() => {
                  setFolderPath(target);
                  setSearch('');
                }}
              >
                {seg}
              </button>
            </span>
          );
        })}
      </div>

      <input
        type="search"
        class="search-input compact-search"
        placeholder="筛选当前目录文件…"
        value={search}
        onInput={(e) => setSearch((e.target as HTMLInputElement).value)}
      />

      <div class="files-controls">
        <div class="mini-pills" role="tablist" aria-label="文件筛选">
          <button
            type="button"
            role="tab"
            aria-selected={filterMode === 'all'}
            class={`filter-pill${filterMode === 'all' ? ' on' : ''}`}
            onClick={() => setFilterMode('all')}
          >
            全部
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={filterMode === 'today'}
            class={`filter-pill${filterMode === 'today' ? ' on' : ''}`}
            onClick={() => setFilterMode(filterMode === 'today' ? 'all' : 'today')}
          >
            今日{todayCount > 0 ? <span class="pill-n">{todayCount}</span> : null}
          </button>
          {gitFilesList.length > 0 && (
            <button
              type="button"
              role="tab"
              aria-selected={filterMode === 'changed'}
              class={`filter-pill${filterMode === 'changed' ? ' on' : ''}`}
              onClick={() => setFilterMode(filterMode === 'changed' ? 'all' : 'changed')}
            >
              有改动{changedCount > 0 ? <span class="pill-n">{changedCount}</span> : null}
            </button>
          )}
        </div>
        <button
          type="button"
          class={`files-sort-btn${sortMode === 'modified' ? ' on' : ''}`}
          title={sortMode === 'modified' ? '当前按最近修改优先排序，点击切换为按名称排序' : '当前按名称排序，点击切换为最近修改优先'}
          onClick={toggleSort}
        >
          {sortMode === 'modified' ? '最近修改 ↓' : '按名称'}
        </button>
      </div>

      {err && <div class="panel-feedback is-error">{err}</div>}

      {loading && entries.length === 0 ? (
        <div class="empty-hint">正在加载项目文件…</div>
      ) : filtered.length === 0 ? (
        <div class="empty-hint">
          {search
            ? `未找到包含“${search}”的文件`
            : filterMode === 'today'
              ? '当前目录今日没有修改记录'
              : filterMode === 'changed'
                ? '当前目录没有未提交的 Git 改动'
                : hiddenCount > 0 && !showHidden
                  ? `此目录仅有 ${hiddenCount} 个隐藏项，可点击上方“隐藏”按钮显示`
                  : '空目录'}
        </div>
      ) : (
        <div class="file-rows">
          {crumbs.length > 0 && (
            <button
              type="button"
              class="file-row"
              onClick={() => {
                setFolderPath(crumbs.slice(0, -1).join('/'));
                setSearch('');
              }}
            >
              <span class="file-badge ft-dir">..</span>
              <span class="file-name">返回上级目录</span>
            </button>
          )}
          {filtered.map((entry) => {
            const badge = fileBadge(entry);
            const tier = getRecencyTier(entry.modified, now);
            const exact = formatExactTime(entry.modified);
            const relTime = formatRelativeTime(entry.modified, now);
            const { gitFile, dirChanges } = entryGitState(entry);
            const gitCode = gitFile ? (gitFile.status === '?' ? 'U' : gitFile.status) : '';
            const recClass = tier === 'recent' ? ' is-recent' : tier === 'today' ? ' is-today' : '';
            const gitClass = gitFile || dirChanges > 0 ? ' is-git-changed' : '';
            return (
              <div key={entry.name} class={`file-row-wrap${entry.hidden ? ' is-hidden' : ''}${recClass}${gitClass}`}>
                <button
                  type="button"
                  class="file-row"
                  onClick={() => {
                    if (entry.dir) {
                      setFolderPath(folderPath ? `${folderPath}/${entry.name}` : entry.name);
                      setSearch('');
                    } else {
                      setPreviewTarget({ dir: browseDir, path: folderPath, name: entry.name });
                    }
                  }}
                >
                  <span class={`file-badge ${badge.cls}`}>{badge.tag}</span>
                  <span class="file-row-text">
                    <span class="file-name-line">
                      <span class="file-name">{entry.name}</span>
                      {tier === 'recent' ? (
                        <span class="rec-tag is-recent" title={exact ? `刚刚修改：${exact}` : '最近一小时内修改'}>
                          刚改
                        </span>
                      ) : tier === 'today' ? (
                        <span class="rec-tag is-today" title={exact ? `今日修改：${exact}` : '今日修改'}>
                          今日
                        </span>
                      ) : null}
                      {dirChanges > 0 && (
                        <span class="git-dir-tag" title={`包含 ${dirChanges} 个未提交改动的文件`}>
                          {dirChanges} 改
                        </span>
                      )}
                    </span>
                    <span class="file-sub">
                      {!entry.dir && formatSize(entry.size) ? <span>{formatSize(entry.size)}</span> : null}
                      {!entry.dir && formatSize(entry.size) && relTime ? <span> · </span> : null}
                      <span class={`file-mtime is-${tier}`} title={exact || undefined}>
                        {relTime || (entry.dir ? '文件夹' : '')}
                      </span>
                    </span>
                  </span>
                  {entry.dir && <span class="file-chev">›</span>}
                </button>
                {gitFile && (
                  <button
                    type="button"
                    class={`git-st-btn st-${gitCode}`}
                    title={`${GIT_STATUS_TITLE[gitFile.status] || 'Git 改动'}${onOpenDiff ? ' · 点击查看 Diff' : ''}`}
                    disabled={!onOpenDiff}
                    onClick={() => onOpenDiff?.({ project: browseDir, worktree: activeWorktree, file: gitFile })}
                  >
                    {gitCode}
                  </button>
                )}
                {!entry.dir && (
                  <button
                    type="button"
                    class="file-quick-expand"
                    title="在主区域大屏预览"
                    onClick={() => onExpandPreview({ dir: browseDir, path: folderPath, name: entry.name })}
                  >
                    ⤢
                  </button>
                )}
              </div>
            );
          })}
        </div>
      )}
      {more && <div class="muted-code" style={{ textAlign: 'center' }}>目录项较多，已显示前 3000 项</div>}
    </div>
  );
}
