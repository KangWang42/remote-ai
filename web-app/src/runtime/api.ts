import type {
  ChatChunk,
  FileEntry,
  GitFileDiffResponse,
  GitStatusResponse,
  OverviewSnapshot,
} from './types';

function resolveBase(): string {
  try {
    return new URL('../', window.location.href).href.replace(/\/$/, '');
  } catch {
    return '';
  }
}

export const API_BASE = resolveBase();

export function opId(): string {
  const bytes = new Uint8Array(16);
  if (typeof crypto !== 'undefined' && crypto.getRandomValues) {
    crypto.getRandomValues(bytes);
  } else {
    for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256);
  }
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

export class ApiError extends Error {
  status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  const resp = await fetch(API_BASE + path, {
    credentials: 'same-origin',
    cache: 'no-store',
    ...init,
    headers: {
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...(init?.headers || {}),
    },
  });
  let data: any = {};
  try {
    data = await resp.json();
  } catch {
    data = {};
  }
  if (!resp.ok) {
    throw new ApiError(resp.status, data?.error || `请求失败（HTTP ${resp.status}）`);
  }
  return data as T;
}

export async function checkSession(): Promise<{ signed_in: boolean; version?: string }> {
  return requestJson('/api/session');
}

export async function loginWithPassword(password: string): Promise<{ ok: boolean }> {
  return requestJson('/api/login', {
    method: 'POST',
    body: JSON.stringify({ password }),
  });
}

export async function loginWithTicket(ticket: string): Promise<{ ok: boolean }> {
  return requestJson('/api/login', {
    method: 'POST',
    body: JSON.stringify({ ticket }),
  });
}

/** Consumes #t=<ticket> or #p=<password> from URL hash and scrubs it from browser history immediately. */
export async function bootstrapAuthFromHash(): Promise<boolean> {
  const hash = window.location.hash.replace(/^#/, '');
  if (!hash) return false;
  const params = new URLSearchParams(hash);
  const ticket = params.get('t');
  const password = params.get('p');
  if (!ticket && !password) return false;
  try {
    window.history.replaceState(null, '', window.location.pathname + window.location.search);
  } catch {
    // ignore
  }
  if (ticket) {
    await loginWithTicket(ticket);
    return true;
  }
  if (password) {
    await loginWithPassword(password);
    return true;
  }
  return false;
}

export async function fetchOverview(rev = 0, wait = 0, signal?: AbortSignal): Promise<OverviewSnapshot> {
  const qs = new URLSearchParams();
  if (rev > 0) qs.set('rev', String(rev));
  if (wait > 0) qs.set('wait', String(wait));
  const suffix = qs.toString() ? '?' + qs.toString() : '';
  return requestJson('/api/terminal' + suffix, { signal });
}

export async function fetchTerminalChunks(
  terminalId: string,
  after: number,
  wait = 15,
  signal?: AbortSignal,
): Promise<{ chunks: ChatChunk[]; after: number; reset?: boolean; terminal: any; device: any }> {
  const qs = new URLSearchParams({
    terminal: terminalId,
    after: String(after),
    wait: String(wait),
  });
  return requestJson('/api/terminal?' + qs.toString(), { signal });
}

export async function sendTerminalAction(payload: Record<string, unknown>): Promise<any> {
  return requestJson('/api/terminal', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), ...payload }),
  });
}

export async function readConversation(tool: string, session: string, dir: string, folder: string): Promise<any> {
  return requestJson('/api/conversation', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'read', tool, session, dir, folder }),
  });
}

export async function conversationAction(payload: Record<string, unknown>): Promise<any> {
  return requestJson('/api/conversation', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), ...payload }),
  });
}

export async function fetchComputerInfo(): Promise<any> {
  return requestJson('/api/computer', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'settings_read' }),
  });
}

export async function fetchRoutesInfo(): Promise<{ environment_id: string; routes: any[]; local_password?: string }> {
  return requestJson('/api/computer', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'routes_read' }),
  });
}

export async function gitStatus(dir: string, worktree = ''): Promise<GitStatusResponse> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'git_status', dir, ...(worktree ? { worktree } : {}) }),
  });
}

export async function gitFileDiff(
  dir: string,
  path: string,
  scope: 'working' | 'staged' | 'base' = 'working',
  worktree = '',
): Promise<GitFileDiffResponse> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({
      id: opId(),
      action: 'git_file_diff',
      dir,
      path,
      scope,
      ...(worktree ? { worktree } : {}),
    }),
  });
}

export async function gitCommit(dir: string, message: string, files?: string[], worktree = ''): Promise<any> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({
      id: opId(),
      action: 'git_commit',
      dir,
      message,
      ...(files && files.length > 0 ? { files } : {}),
      ...(worktree ? { worktree } : {}),
    }),
  });
}

export async function gitPush(dir: string, setUpstream = false, worktree = ''): Promise<any> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({
      id: opId(),
      action: 'git_push',
      dir,
      set_upstream: setUpstream,
      ...(worktree ? { worktree } : {}),
    }),
  });
}

export async function gitCheckpoint(dir: string, label = '', worktree = ''): Promise<any> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({
      id: opId(),
      action: 'git_checkpoint',
      dir,
      ...(label ? { label } : {}),
      ...(worktree ? { worktree } : {}),
    }),
  });
}

export async function gitWorktreeApply(dir: string, worktree: string, removeAfter = true): Promise<any> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({
      id: opId(),
      action: 'git_worktree_apply',
      dir,
      worktree,
      remove_after: removeAfter,
    }),
  });
}

export async function gitWorktreeRemove(dir: string, worktree: string): Promise<any> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({
      id: opId(),
      action: 'git_worktree_remove',
      dir,
      worktree,
    }),
  });
}

export async function listFiles(dir: string, path = ''): Promise<{ entries: FileEntry[] }> {
  return requestJson('/api/files', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'file_list', dir, path }),
  });
}

export function terminalEmbedUrl(terminalId: string): string {
  return `${API_BASE}/terminal/?id=${encodeURIComponent(terminalId)}&embed=1`;
}
