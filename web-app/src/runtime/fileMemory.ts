import type { FilePreviewTarget } from './types';

export type FileFilterMode = 'all' | 'today' | 'changed';
export type MarkdownViewMode = 'rendered' | 'source';

export interface ExplorerMemoryState {
  browseDir: string;
  folderPath: string;
  previewTarget: FilePreviewTarget | null;
  filterMode: FileFilterMode;
}

export interface PreviewMemoryState {
  mdMode: MarkdownViewMode;
  scrollTop: number;
}

const EXPLORER_KEY = 'rcli-console-file-explorer-mem-v1';
const PREVIEW_KEY = 'rcli-console-file-preview-mem-v1';
const MAX_ENTRIES = 80;

function trimMap<T>(map: Record<string, T>): Record<string, T> {
  const entries = Object.entries(map);
  return entries.length > MAX_ENTRIES ? Object.fromEntries(entries.slice(-MAX_ENTRIES)) : map;
}

function loadStore<T>(key: string): Record<string, T> {
  try {
    const raw = JSON.parse(sessionStorage.getItem(key) || '{}');
    return raw && typeof raw === 'object' ? raw : {};
  } catch {
    return {};
  }
}

function saveStore<T>(key: string, map: Record<string, T>) {
  try {
    sessionStorage.setItem(key, JSON.stringify(trimMap(map)));
  } catch {
    // Keep memory for the current page session if storage is unavailable.
  }
}

/**
 * Remembers right-pane file browsing and preview state per conversation (and per project fallback),
 * so switching conversations, toggling inspector tabs, or expanding/collapsing preview returns to
 * the exact folder and file the user had open.
 */
export class FileExplorerMemory {
  private scopes: Record<string, ExplorerMemoryState>;
  private previews: Record<string, PreviewMemoryState>;

  constructor() {
    this.scopes = loadStore<ExplorerMemoryState>(EXPLORER_KEY);
    this.previews = loadStore<PreviewMemoryState>(PREVIEW_KEY);
  }

  private validState(state: ExplorerMemoryState | undefined, workspaces: string[]): ExplorerMemoryState | null {
    if (!state || !state.browseDir) return null;
    if (workspaces.length > 0 && !workspaces.includes(state.browseDir)) return null;
    return {
      browseDir: state.browseDir,
      folderPath: typeof state.folderPath === 'string' ? state.folderPath : '',
      previewTarget:
        state.previewTarget && state.previewTarget.dir === state.browseDir && state.previewTarget.name
          ? {
              dir: state.previewTarget.dir,
              path: typeof state.previewTarget.path === 'string' ? state.previewTarget.path : '',
              name: state.previewTarget.name,
            }
          : null,
      filterMode: state.filterMode === 'today' || state.filterMode === 'changed' ? state.filterMode : 'all',
    };
  }

  /** Resolves the remembered explorer state for a conversation/project context, falling back cleanly. */
  resolve(scopeKey: string, activeProject: string, workspaces: string[]): ExplorerMemoryState {
    const fallbackDir = activeProject || workspaces[0] || '';
    if (scopeKey) {
      const own = this.validState(this.scopes[scopeKey], workspaces);
      if (own) return own;
    }
    if (activeProject) {
      const byProj = this.validState(this.scopes[`proj:${activeProject}`], workspaces);
      if (byProj) return byProj;
    }
    return { browseDir: fallbackDir, folderPath: '', previewTarget: null, filterMode: 'all' };
  }

  /** Resolves a specific project's remembered folder/preview when switching projects in the dropdown. */
  resolveProject(project: string, workspaces: string[]): ExplorerMemoryState {
    const byProj = project ? this.validState(this.scopes[`proj:${project}`], workspaces) : null;
    return byProj || { browseDir: project, folderPath: '', previewTarget: null, filterMode: 'all' };
  }

  save(scopeKey: string, state: ExplorerMemoryState) {
    if (!state.browseDir) return;
    const clean: ExplorerMemoryState = {
      browseDir: state.browseDir,
      folderPath: state.folderPath || '',
      previewTarget: state.previewTarget
        ? { dir: state.previewTarget.dir, path: state.previewTarget.path || '', name: state.previewTarget.name }
        : null,
      filterMode: state.filterMode,
    };
    if (scopeKey) this.scopes[scopeKey] = clean;
    this.scopes[`proj:${clean.browseDir}`] = clean;
    this.scopes = trimMap(this.scopes);
    saveStore(EXPLORER_KEY, this.scopes);
  }

  previewKey(target: FilePreviewTarget): string {
    return `${target.dir}/${target.path ? `${target.path}/` : ''}${target.name}`;
  }

  getPreview(target: FilePreviewTarget): PreviewMemoryState {
    const saved = this.previews[this.previewKey(target)];
    return {
      mdMode: saved?.mdMode === 'source' ? 'source' : 'rendered',
      scrollTop: typeof saved?.scrollTop === 'number' && saved.scrollTop > 0 ? saved.scrollTop : 0,
    };
  }

  savePreview(target: FilePreviewTarget, patch: Partial<PreviewMemoryState>) {
    const key = this.previewKey(target);
    const cur = this.getPreview(target);
    this.previews[key] = { ...cur, ...patch };
    this.previews = trimMap(this.previews);
    saveStore(PREVIEW_KEY, this.previews);
  }
}

export const fileMemory = new FileExplorerMemory();
