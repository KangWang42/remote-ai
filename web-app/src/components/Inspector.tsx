import type { FilePreviewTarget } from '../runtime/types';
import { ChangesPanel, type DiffTarget } from './ChangesPanel';
import { FileExplorer } from './FileExplorer';

export type InspectorTab = 'changes' | 'files';

interface Props {
  tab: InspectorTab;
  project: string;
  worktree: string;
  workspaces: string[];
  openDiff: string;
  onTab: (tab: InspectorTab) => void;
  onOpenDiff: (target: DiffTarget) => void;
  onExpandFile: (target: FilePreviewTarget) => void;
  onCiteFile: (relPath: string, project: string) => void;
  onClose: () => void;
}

/** Beside a conversation: what its project's folder has changed, and its files. */
export function Inspector(p: Props) {
  return (
    <aside class="inspector">
      <div class="insp-head">
        <div class="seg" role="tablist">
          <button type="button" role="tab" aria-selected={p.tab === 'changes'} class={p.tab === 'changes' ? 'on' : ''} onClick={() => p.onTab('changes')}>
            变更
          </button>
          <button type="button" role="tab" aria-selected={p.tab === 'files'} class={p.tab === 'files' ? 'on' : ''} onClick={() => p.onTab('files')}>
            文件
          </button>
        </div>
        <span class="insp-project" title={p.project}>
          {p.project}
        </span>
        <button type="button" class="icon-btn" onClick={p.onClose} title="收起 (Ctrl+B)" aria-label="收起">
          ×
        </button>
      </div>
      <div class="insp-body">
        {p.tab === 'changes' ? (
          <ChangesPanel project={p.project} worktree={p.worktree} openFile={p.openDiff} onOpenDiff={p.onOpenDiff} />
        ) : (
          <FileExplorer
            workspaces={p.workspaces}
            activeProject={p.project}
            worktree={p.worktree}
            onExpandPreview={p.onExpandFile}
            onOpenDiff={p.onOpenDiff}
            onCiteFile={p.onCiteFile}
          />
        )}
      </div>
    </aside>
  );
}
