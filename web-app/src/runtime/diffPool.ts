import { parseUnifiedDiff, type ParsedDiff } from './diffWorker';

interface PendingJob {
  id: number;
  raw: string;
  resolve: (value: ParsedDiff) => void;
  reject: (err: unknown) => void;
}

interface WorkerSlot {
  worker: Worker;
  busy: boolean;
  job: PendingJob | null;
}

const IDLE_TTL_MS = 30_000;
const CACHE_LIMIT = 120;

/**
 * Bounded Web Worker pool for diff parsing (clamped to 2..4 workers, 30s idle teardown, LRU cache).
 * Gracefully falls back to inline parsing if Workers are unavailable in a restricted WebView.
 */
class DiffWorkerPool {
  private slots: WorkerSlot[] = [];
  private queue: PendingJob[] = [];
  private cache = new Map<string, ParsedDiff>();
  private nextId = 1;
  private idleTimer: number | null = null;
  private workersBroken = false;

  private maxWorkers(): number {
    const cores = typeof navigator !== 'undefined' && navigator.hardwareConcurrency ? navigator.hardwareConcurrency : 4;
    return Math.max(2, Math.min(4, Math.floor(cores / 2)));
  }

  parse(cacheKey: string, raw: string): Promise<ParsedDiff> {
    const key = `${cacheKey}:${raw.length}:${raw.slice(0, 64)}`;
    const cached = this.cache.get(key);
    if (cached) return Promise.resolve(cached);

    // Small diffs parse in < 0.2ms inline without worker messaging overhead.
    if (this.workersBroken || typeof Worker === 'undefined' || raw.length < 4096) {
      const parsed = parseUnifiedDiff(raw);
      this.remember(key, parsed);
      return Promise.resolve(parsed);
    }

    return new Promise<ParsedDiff>((resolve, reject) => {
      const job: PendingJob = {
        id: this.nextId++,
        raw,
        resolve: (val) => {
          this.remember(key, val);
          resolve(val);
        },
        reject,
      };
      this.queue.push(job);
      this.pump();
    });
  }

  private remember(key: string, value: ParsedDiff): void {
    if (this.cache.size >= CACHE_LIMIT) {
      const oldest = this.cache.keys().next().value;
      if (oldest !== undefined) this.cache.delete(oldest);
    }
    this.cache.set(key, value);
  }

  private spawn(): WorkerSlot | null {
    try {
      const worker = new Worker(new URL('./diffWorker.ts', import.meta.url), { type: 'module' });
      const slot: WorkerSlot = { worker, busy: false, job: null };
      worker.onmessage = (event: MessageEvent<{ id: number; parsed: ParsedDiff }>) => {
        const finished = slot.job;
        slot.job = null;
        slot.busy = false;
        if (finished && finished.id === event.data.id) finished.resolve(event.data.parsed);
        this.pump();
      };
      worker.onerror = () => {
        const failed = slot.job;
        slot.job = null;
        slot.busy = false;
        this.workersBroken = true;
        this.teardown();
        if (failed) failed.resolve(parseUnifiedDiff(failed.raw));
        while (this.queue.length > 0) {
          const rest = this.queue.shift()!;
          rest.resolve(parseUnifiedDiff(rest.raw));
        }
      };
      this.slots.push(slot);
      return slot;
    } catch {
      this.workersBroken = true;
      return null;
    }
  }

  private pump(): void {
    if (this.idleTimer !== null) {
      window.clearTimeout(this.idleTimer);
      this.idleTimer = null;
    }
    while (this.queue.length > 0) {
      let slot = this.slots.find((s) => !s.busy) || null;
      if (!slot && this.slots.length < this.maxWorkers()) slot = this.spawn();
      if (!slot) break;
      const job = this.queue.shift()!;
      slot.busy = true;
      slot.job = job;
      slot.worker.postMessage({ id: job.id, raw: job.raw });
    }
    if (this.queue.length === 0 && this.slots.every((s) => !s.busy) && this.slots.length > 0) {
      this.idleTimer = window.setTimeout(() => this.teardown(), IDLE_TTL_MS);
    }
  }

  private teardown(): void {
    for (const s of this.slots) {
      try {
        s.worker.terminate();
      } catch {
        // ignore
      }
    }
    this.slots = [];
    this.idleTimer = null;
  }
}

export const diffPool = new DiffWorkerPool();
