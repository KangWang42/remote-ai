import type {
  ChatChunk,
  FileEntry,
  GitBranchEntry,
  GitCheckpointEntry,
  GitFileDiffResponse,
  GitStatusResponse,
  OverviewSnapshot,
  ProjectScriptEntry,
  ToolModelOption,
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

export async function fetchTeamModels(): Promise<Record<string, ToolModelOption[]>> {
  const raw = await requestJson<any>('/api/work', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'team_models' }),
  });
  const out: Record<string, ToolModelOption[]> = {};
  const map = raw?.tools || raw?.models || raw || {};
  for (const key of Object.keys(map)) {
    const val = map[key];
    const items = Array.isArray(val) ? val : Array.isArray(val?.models) ? val.models : [];
    if (items.length > 0) {
      out[key] = items.map((m: any) =>
        typeof m === 'string'
          ? { id: m, label: m }
          : {
              id: m.id || m.model || m.name || '',
              label: m.label || m.name || m.id || '',
              efforts: Array.isArray(m.efforts) ? m.efforts : undefined,
            },
      );
    }
  }
  return out;
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
  base = '',
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
      ...(base ? { base } : {}),
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
      ...(files && files.length ? { files } : {}),
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

export async function gitWorktreeApply(dir: string, worktree: string, removeAfter = false): Promise<any> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'git_worktree_apply', dir, worktree, remove_after: removeAfter }),
  });
}

export async function gitWorktreeRemove(dir: string, worktree: string): Promise<any> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'git_worktree_remove', dir, worktree }),
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

export async function gitBranches(dir: string, worktree = ''): Promise<{ current: string; branches: GitBranchEntry[] }> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'git_branches', dir, ...(worktree ? { worktree } : {}) }),
  });
}

export async function gitCheckout(dir: string, branch: string, create = false, worktree = ''): Promise<any> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({
      id: opId(),
      action: 'git_checkout',
      dir,
      branch,
      create,
      ...(worktree ? { worktree } : {}),
    }),
  });
}

export async function gitCheckpointsList(
  dir: string,
  worktree = '',
): Promise<{ checkpoints: GitCheckpointEntry[] }> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'git_checkpoints_list', dir, ...(worktree ? { worktree } : {}) }),
  });
}

export async function gitCheckpointRestore(dir: string, ref: string, worktree = ''): Promise<any> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'git_checkpoint_restore', dir, ref, ...(worktree ? { worktree } : {}) }),
  });
}

export async function fetchProjectScripts(
  dir: string,
  worktree = '',
): Promise<{ scripts: ProjectScriptEntry[] }> {
  return requestJson('/api/git', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'project_scripts', dir, ...(worktree ? { worktree } : {}) }),
  });
}

export async function listFiles(dir: string, path = ''): Promise<{ path: string; entries: FileEntry[] }> {
  return requestJson('/api/files', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'file_list', dir, path }),
  });
}

function bytesToBase64(bytes: Uint8Array): string {
  let bin = '';
  const step = 0x8000;
  for (let i = 0; i < bytes.length; i += step) {
    bin += String.fromCharCode(...bytes.subarray(i, i + step));
  }
  return btoa(bin);
}

/** Uploads an image attachment into .remote-ai/attachments/ using the existing chunked /api/files protocol. */
export async function uploadImageAttachment(dir: string, file: File): Promise<string> {
  const mimeMap: Record<string, string> = {
    'image/png': 'png',
    'image/jpeg': 'jpg',
    'image/webp': 'webp',
    'image/gif': 'gif',
  };
  const ext = mimeMap[file.type] || 'png';
  const now = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  const stamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;
  const hex = opId().slice(0, 6);
  const relPath = `.remote-ai/attachments/${stamp}-${hex}.${ext}`;
  const upload = opId();
  const buf = new Uint8Array(await file.arrayBuffer());

  await requestJson('/api/files', {
    method: 'POST',
    body: JSON.stringify({ id: opId(), action: 'file_upload_start', dir, path: relPath, upload, size: buf.byteLength }),
  });
  const chunkSize = 512 * 1024;
  let offset = 0;
  try {
    while (offset < buf.byteLength) {
      const slice = buf.subarray(offset, Math.min(buf.byteLength, offset + chunkSize));
      await requestJson('/api/files', {
        method: 'POST',
        body: JSON.stringify({
          id: opId(),
          action: 'file_upload_chunk',
          dir,
          path: relPath,
          upload,
          offset,
          data: bytesToBase64(slice),
        }),
      });
      offset += slice.byteLength;
    }
    await requestJson('/api/files', {
      method: 'POST',
      body: JSON.stringify({ id: opId(), action: 'file_upload_finish', dir, path: relPath, upload }),
    });
    return relPath;
  } catch (err) {
    requestJson('/api/files', {
      method: 'POST',
      body: JSON.stringify({ id: opId(), action: 'file_upload_cancel', dir, path: relPath, upload }),
    }).catch(() => {});
    throw err;
  }
}

export function terminalEmbedUrl(terminalId: string): string {
  return `${API_BASE}/terminal/?id=${encodeURIComponent(terminalId)}&embed=1`;
}

export function postDesktopMessage(msg: string): boolean {
  try {
    const fn = (window as any).chrome?.webview?.postMessage;
    if (typeof fn === 'function') {
      fn.call((window as any).chrome.webview, msg);
      return true;
    }
  } catch {
    // ignore
  }
  return false;
}
