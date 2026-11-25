/**
 * Off-main-thread unified diff parser for both Unified and Split (side-by-side) views.
 * Runs inside a bounded worker pool so large diffs never block scrolling or typing.
 */

export interface DiffLine {
  kind: 'hunk' | 'ctx' | 'add' | 'del' | 'meta';
  oldLine: number | null;
  newLine: number | null;
  text: string;
}

export interface SplitRow {
  kind: 'hunk' | 'pair';
  header?: string;
  left?: DiffLine | null;
  right?: DiffLine | null;
}

export interface ParsedDiff {
  unified: DiffLine[];
  split: SplitRow[];
  additions: number;
  deletions: number;
}

export function parseUnifiedDiff(raw: string): ParsedDiff {
  const unified: DiffLine[] = [];
  const split: SplitRow[] = [];
  let additions = 0;
  let deletions = 0;
  let oldNum = 0;
  let newNum = 0;

  const lines = (raw || '').replace(/\r\n/g, '\n').split('\n');
  let pendingDels: DiffLine[] = [];
  let pendingAdds: DiffLine[] = [];

  const flushPairs = () => {
    const max = Math.max(pendingDels.length, pendingAdds.length);
    for (let i = 0; i < max; i++) {
      split.push({
        kind: 'pair',
        left: pendingDels[i] || null,
        right: pendingAdds[i] || null,
      });
    }
    pendingDels = [];
    pendingAdds = [];
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (i === lines.length - 1 && line === '') break;

    if (line.startsWith('@@')) {
      flushPairs();
      const match = line.match(/@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/);
      if (match) {
        oldNum = parseInt(match[1], 10);
        newNum = parseInt(match[2], 10);
      }
      const hunkLine: DiffLine = { kind: 'hunk', oldLine: null, newLine: null, text: line };
      unified.push(hunkLine);
      split.push({ kind: 'hunk', header: line });
      continue;
    }

    if (
      line.startsWith('diff --git ') ||
      line.startsWith('index ') ||
      line.startsWith('--- ') ||
      line.startsWith('+++ ') ||
      line.startsWith('new file mode') ||
      line.startsWith('deleted file mode')
    ) {
      continue;
    }

    if (line.startsWith('+')) {
      additions++;
      const item: DiffLine = { kind: 'add', oldLine: null, newLine: newNum++, text: line.slice(1) };
      unified.push(item);
      pendingAdds.push(item);
      continue;
    }

    if (line.startsWith('-')) {
      deletions++;
      const item: DiffLine = { kind: 'del', oldLine: oldNum++, newLine: null, text: line.slice(1) };
      unified.push(item);
      pendingDels.push(item);
      continue;
    }

    if (line.startsWith('\\')) {
      flushPairs();
      unified.push({ kind: 'meta', oldLine: null, newLine: null, text: line });
      continue;
    }

    flushPairs();
    const text = line.startsWith(' ') ? line.slice(1) : line;
    const ctx: DiffLine = { kind: 'ctx', oldLine: oldNum++, newLine: newNum++, text };
    unified.push(ctx);
    split.push({ kind: 'pair', left: ctx, right: ctx });
  }

  flushPairs();
  return { unified, split, additions, deletions };
}

self.onmessage = (event: MessageEvent<{ id: number; raw: string }>) => {
  const { id, raw } = event.data;
  const parsed = parseUnifiedDiff(raw);
  (self as unknown as Worker).postMessage({ id, parsed });
};
