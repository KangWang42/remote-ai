import { useEffect, useMemo, useState } from 'preact/hooks';
import { fileViewerUrl, listFiles, readFileBytes } from '../runtime/api';
import { formatRelativeTime } from '../runtime/time';
import type { FileEntry, FilePreviewTarget } from '../runtime/types';
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

interface ExplorerProps {
  workspaces: string[];
  activeProject: string;
  onExpandPreview: (target: FilePreviewTarget) => void;
  onCiteFile?: (relPath: string, projectDir: string) => void;
}

/** Full-featured project file explorer with project switcher, breadcrumbs, search, and inline preview. */
export function FileExplorer({ workspaces, activeProject, onExpandPreview, onCiteFile }: ExplorerProps) {
  const [browseDir, setBrowseDir] = useState(activeProject || workspaces[0] || '');
  const [folderPath, setFolderPath] = useState('');
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const [more, setMore] = useState(false);
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState('');
  const [search, setSearch] = useState('');
  const [showHidden, setShowHidden] = useState(false);
  const [previewTarget, setPreviewTarget] = useState<FilePreviewTarget | null>(null);
  const [tick, setTick] = useState(0);

  // Follow active project switch automatically when user jumps between projects
  useEffect(() => {
    if (activeProject && activeProject !== browseDir) {
      setBrowseDir(activeProject);
      setFolderPath('');
      setPreviewTarget(null);
      setSearch('');
    } else if (!browseDir && workspaces[0]) {
      setBrowseDir(workspaces[0]);
    }
  }, [activeProject, workspaces.join('|')]);

  useEffect(() => {
    if (!browseDir) {
      setEntries([]);
      return;
    }
    let alive = true;
    setLoading(true);
    setErr('');
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

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    return entries
      .filter((e) => (showHidden || !e.hidden) && (!q || e.name.toLowerCase().includes(q)))
      .sort((a, b) => {
        if (a.dir !== b.dir) return a.dir ? -1 : 1;
        return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
      });
  }, [entries, showHidden, search]);

  const hiddenCount = useMemo(() => entries.filter((e) => e.hidden).length, [entries]);

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

      {err && <div class="panel-feedback is-error">{err}</div>}

      {loading && entries.length === 0 ? (
        <div class="empty-hint">正在加载项目文件…</div>
      ) : filtered.length === 0 ? (
        <div class="empty-hint">
          {search
            ? `未找到包含“${search}”的文件`
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
            return (
              <div key={entry.name} class={`file-row-wrap${entry.hidden ? ' is-hidden' : ''}`}>
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
                    <span class="file-name">{entry.name}</span>
                    <span class="file-sub">
                      {entry.dir ? formatRelativeTime(entry.modified) || '文件夹' : [formatSize(entry.size), formatRelativeTime(entry.modified)].filter(Boolean).join(' · ')}
                    </span>
                  </span>
                  {entry.dir && <span class="file-chev">›</span>}
                </button>
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
