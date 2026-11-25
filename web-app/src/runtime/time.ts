/** Normalizes Unix timestamps in seconds or milliseconds to epoch milliseconds (0 if missing/invalid). */
export function normalizeMs(raw?: number | null): number {
  const n = Number(raw);
  if (!Number.isFinite(n) || n <= 0) return 0;
  return n < 1e11 ? Math.round(n * 1000) : Math.round(n);
}

function pad2(n: number): string {
  return String(n).padStart(2, '0');
}

/** Exact local timestamp for hover tooltips, e.g. "2026-11-25 14:18:20". */
export function formatExactTime(raw?: number | null): string {
  const ms = normalizeMs(raw);
  if (!ms) return '';
  const d = new Date(ms);
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())} ${pad2(d.getHours())}:${pad2(d.getMinutes())}:${pad2(d.getSeconds())}`;
}

/**
 * Compact, human-readable relative work timestamp:
 * - < 60s -> "刚刚"
 * - < 60m -> "X 分钟前"
 * - same day -> "X 小时前"
 * - yesterday -> "昨天 HH:MM"
 * - same year -> "MM-DD HH:MM"
 * - older -> "YYYY-MM-DD"
 */
export function formatRelativeTime(raw?: number | null, now = Date.now()): string {
  const ms = normalizeMs(raw);
  if (!ms) return '';
  const diffSec = Math.max(0, Math.floor((now - ms) / 1000));
  if (diffSec < 60) return '刚刚';
  if (diffSec < 3600) return `${Math.floor(diffSec / 60)} 分钟前`;

  const d = new Date(ms);
  const cur = new Date(now);
  const startToday = new Date(cur.getFullYear(), cur.getMonth(), cur.getDate()).getTime();
  const startYesterday = startToday - 86400 * 1000;

  if (ms >= startToday) {
    return `${Math.max(1, Math.floor(diffSec / 3600))} 小时前`;
  }
  const hm = `${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
  if (ms >= startYesterday) {
    return `昨天 ${hm}`;
  }
  if (d.getFullYear() === cur.getFullYear()) {
    return `${pad2(d.getMonth() + 1)}-${pad2(d.getDate())} ${hm}`;
  }
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
}
