import { ChatTimelineReducer } from './chatParser';
import type { ChatMessageItem } from './types';

export interface CachedThreadState {
  key: string;
  after: number;
  reducer: ChatTimelineReducer;
  messages: ChatMessageItem[];
  scrollTop: number;
  stickBottom: boolean;
}

/**
 * Bounded multi-instance LRU cache keeping the most recently visited conversations warm in memory.
 * Switching between parallel AI threads restores timeline state and exact scroll position in 0ms
 * and resumes incremental long-polling from the cached `after` sequence cursor.
 */
export class ThreadCache {
  private readonly maxEntries: number;
  private readonly store = new Map<string, CachedThreadState>();

  constructor(maxEntries = 12) {
    this.maxEntries = Math.max(2, maxEntries);
  }

  get(key: string): CachedThreadState | undefined {
    const hit = this.store.get(key);
    if (!hit) return undefined;
    // Refresh LRU recency
    this.store.delete(key);
    this.store.set(key, hit);
    return hit;
  }

  upsert(key: string, patch: Partial<CachedThreadState>): CachedThreadState {
    let entry = this.store.get(key);
    if (!entry) {
      entry = {
        key,
        after: 0,
        reducer: new ChatTimelineReducer(),
        messages: [],
        scrollTop: 0,
        stickBottom: true,
      };
    } else {
      this.store.delete(key);
    }
    Object.assign(entry, patch);
    this.store.set(key, entry);
    while (this.store.size > this.maxEntries) {
      const oldest = this.store.keys().next().value;
      if (oldest === undefined) break;
      this.store.delete(oldest);
    }
    return entry;
  }

  saveScroll(key: string, scrollTop: number, stickBottom: boolean) {
    const hit = this.store.get(key);
    if (!hit) return;
    hit.scrollTop = scrollTop;
    hit.stickBottom = stickBottom;
  }

  delete(key: string) {
    this.store.delete(key);
  }

  clear() {
    this.store.clear();
  }
}
