import { conversationAction, sendTerminalAction } from './api';
import { normalizeMs } from './time';
import {
  toolShort,
  type DeviceInfo,
  type SelectedItem,
  type SessionAsk,
  type SessionEntry,
  type TerminalEntry,
} from './types';

export type SupervisionStatus = 'confirm' | 'failed' | 'done' | 'busy' | 'open' | 'history';

export interface SupervisionItem {
  key: string;
  kind: 'terminal' | 'session';
  id: string;
  title: string;
  project: string;
  folder: string;
  tool: string;
  branch?: string;
  status: SupervisionStatus;
  priority: number;
  at: number;
  preview: string;
  origin: string;
  pinned: boolean;
  isChat: boolean;
  canApprove: boolean;
  canQuickSay: boolean;
  canStop: boolean;
  canEnd: boolean;
  canArchive: boolean;
  askId?: string;
  ask?: SessionAsk | null;
  transport?: string;
  turn?: string;
  item: SelectedItem;
  terminal?: TerminalEntry;
  session?: SessionEntry;
}

export interface ProjectHealth {
  name: string;
  confirm: number;
  failed: number;
  done: number;
  busy: number;
  open: number;
  total: number;
  latestAt: number;
  pinned: boolean;
  items: SupervisionItem[];
}

export interface SupervisionModel {
  all: SupervisionItem[];
  confirm: SupervisionItem[];
  failed: SupervisionItem[];
  done: SupervisionItem[];
  busy: SupervisionItem[];
  open: SupervisionItem[];
  history: SupervisionItem[];
  active: SupervisionItem[];
  attentionCount: number;
  projects: ProjectHealth[];
}

const SEEN_STORAGE_KEY = 'rcli-console-seen-v1';
const PINNED_PROJECTS_KEY = 'rcli-console-pinned-projects-v1';

const STATUS_PRIORITY: Record<SupervisionStatus, number> = {
  confirm: 0,
  failed: 1,
  done: 2,
  busy: 3,
  open: 4,
  history: 5,
};

export function loadSeenMap(): Record<string, number> {
  try {
    const raw = localStorage.getItem(SEEN_STORAGE_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

export function saveSeenMap(map: Record<string, number>): void {
  try {
    const entries = Object.entries(map);
    const trimmed = entries.length > 400 ? Object.fromEntries(entries.slice(-300)) : map;
    localStorage.setItem(SEEN_STORAGE_KEY, JSON.stringify(trimmed));
  } catch {
    // ignore storage quota errors
  }
}

export function loadPinnedProjects(): Record<string, boolean> {
  try {
    const raw = localStorage.getItem(PINNED_PROJECTS_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

export function savePinnedProjects(map: Record<string, boolean>): void {
  try {
    localStorage.setItem(PINNED_PROJECTS_KEY, JSON.stringify(map));
  } catch {
    // ignore
  }
}

export function keyForSelected(sel: SelectedItem | null): string {
  if (!sel) return '';
  return sel.kind === 'terminal' ? `t:${sel.id}` : `s:${sel.tool}:${sel.id}`;
}

export function buildSupervisionModel(
  device: DeviceInfo | null,
  terminals: TerminalEntry[],
  sessions: SessionEntry[],
  seenMap: Record<string, number>,
  pinnedProjects: Record<string, boolean>,
): SupervisionModel {
  const sessionTimeById = new Map<string, number>();
  for (const s of sessions) {
    const ts = normalizeMs(s.updated);
    if (ts > 0) sessionTimeById.set(s.id, ts);
  }

  const attachedSessionIds = new Set<string>();
  const all: SupervisionItem[] = [];

  for (const t of terminals) {
    const running = t.state === 'running' || t.state === 'starting';
    if (t.session && running) attachedSessionIds.add(t.session);

    const at = Math.max(
      normalizeMs(t.phase_at),
      normalizeMs(t.created),
      t.session ? sessionTimeById.get(t.session) || 0 : 0,
    );
    const key = `t:${t.id}`;
    const seenAt = seenMap[key] || 0;
    const isChat = t.form === 'chat';
    const askId = (t.ask || '').trim();

    let status: SupervisionStatus = 'history';
    if (running) {
      if (t.phase === 'confirm' || t.status === 'confirm' || askId.length > 0) {
        status = 'confirm';
      } else if (t.phase === 'busy' || t.status === 'busy' || t.state === 'starting') {
        status = 'busy';
      } else if ((t.phase === 'done' || t.done) && at > seenAt) {
        status = 'done';
      } else {
        status = 'open';
      }
    } else if (((t.exit_code && t.exit_code !== 0) || Boolean(t.error)) && at > seenAt) {
      status = 'failed';
    }

    const preview =
      status === 'failed'
        ? t.error || `程序异常退出（代码 ${t.exit_code}）`
        : (t.said || '').trim();

    all.push({
      key,
      kind: 'terminal',
      id: t.id,
      title: t.title || toolShort(t.tool),
      project: t.dir || '',
      folder: t.folder || '',
      tool: t.tool,
      branch: t.branch || (t.worktree ? `wt:${t.worktree}` : undefined),
      status,
      priority: STATUS_PRIORITY[status],
      at,
      preview,
      origin: isChat ? '共用对话' : '终端',
      pinned: false,
      isChat,
      canApprove: status === 'confirm' && isChat && askId.length > 0,
      canQuickSay: running,
      canStop: running && status === 'busy',
      canEnd: running,
      canArchive: false,
      askId: askId || undefined,
      item: { kind: 'terminal', id: t.id },
      terminal: t,
    });
  }

  for (const s of sessions) {
    if (s.hidden || attachedSessionIds.has(s.id)) continue;
    const key = `s:${s.tool}:${s.id}`;
    const at = normalizeMs(s.updated);
    const seenAt = seenMap[key] || 0;
    const reach = Array.isArray(s.reach) ? s.reach : [];
    const ways = s.ways || {};

    let status: SupervisionStatus = 'history';
    if (s.live) {
      if (s.status === 'confirm' || Boolean(s.ask)) {
        status = 'confirm';
      } else if (s.status === 'busy') {
        status = 'busy';
      } else if (s.status === 'waiting' && at > seenAt) {
        status = 'done';
      } else if (s.now !== false) {
        status = 'open';
      }
    }

    const canApprove =
      status === 'confirm' &&
      Boolean(s.ask?.id) &&
      (reach.includes('answer') || Boolean(ways.answer) || s.transport === 'agy-desktop');
    const canQuickSay =
      s.live &&
      (reach.includes('say') ||
        Boolean(ways.say) ||
        s.transport === 'codex-desktop' ||
        s.transport === 'agy-desktop');
    const canStop =
      s.live &&
      status === 'busy' &&
      (reach.includes('stop') || Boolean(ways.stop) || Boolean(s.transport));
    const canEnd = s.live && (ways.end === 'process' || s.can_takeover === true);

    const preview =
      status === 'confirm' && s.ask?.text
        ? s.ask.text
        : (s.last || '').trim();

    all.push({
      key,
      kind: 'session',
      id: s.id,
      title: s.title || toolShort(s.tool),
      project: s.dir || '',
      folder: s.folder || '',
      tool: s.tool,
      status,
      priority: STATUS_PRIORITY[status],
      at,
      preview,
      origin: s.origin || s.kind || '电脑对话',
      pinned: Boolean(s.pinned),
      isChat: false,
      canApprove,
      canQuickSay,
      canStop,
      canEnd,
      canArchive: !s.live,
      askId: s.ask?.id,
      ask: s.ask,
      transport: ways.say || ways.stop || ways.answer || s.transport,
      turn: s.turn,
      item: { kind: 'session', tool: s.tool, id: s.id, dir: s.dir, folder: s.folder },
      session: s,
    });
  }

  all.sort((a, b) => {
    if (a.priority !== b.priority) return a.priority - b.priority;
    if (a.pinned !== b.pinned) return a.pinned ? -1 : 1;
    return b.at - a.at;
  });

  const confirm = all.filter((x) => x.status === 'confirm');
  const failed = all.filter((x) => x.status === 'failed');
  const done = all.filter((x) => x.status === 'done');
  const busy = all.filter((x) => x.status === 'busy');
  const open = all.filter((x) => x.status === 'open');
  const history = all.filter((x) => x.status === 'history');
  const active = [...confirm, ...failed, ...done, ...busy, ...open];

  const byProject = new Map<string, ProjectHealth>();
  for (const p of device?.projects || []) {
    byProject.set(p.name, {
      name: p.name,
      confirm: 0,
      failed: 0,
      done: 0,
      busy: 0,
      open: 0,
      total: 0,
      latestAt: 0,
      pinned: Boolean(pinnedProjects[p.name]),
      items: [],
    });
  }

  for (const entry of all) {
    if (!entry.project) continue;
    let group = byProject.get(entry.project);
    if (!group) {
      group = {
        name: entry.project,
        confirm: 0,
        failed: 0,
        done: 0,
        busy: 0,
        open: 0,
        total: 0,
        latestAt: 0,
        pinned: Boolean(pinnedProjects[entry.project]),
        items: [],
      };
      byProject.set(entry.project, group);
    }
    group.items.push(entry);
    group.total++;
    if (entry.at > group.latestAt) group.latestAt = entry.at;
    if (entry.status === 'confirm') group.confirm++;
    else if (entry.status === 'failed') group.failed++;
    else if (entry.status === 'done') group.done++;
    else if (entry.status === 'busy') group.busy++;
    else if (entry.status === 'open') group.open++;
  }

  const projects = Array.from(byProject.values()).sort((a, b) => {
    if (a.pinned !== b.pinned) return a.pinned ? -1 : 1;
    const aUrgent = a.confirm + a.failed;
    const bUrgent = b.confirm + b.failed;
    if (aUrgent !== bUrgent) return bUrgent - aUrgent;
    const aActive = a.done + a.busy + a.open;
    const bActive = b.done + b.busy + b.open;
    if (aActive !== bActive) return bActive - aActive;
    return b.latestAt - a.latestAt;
  });

  return {
    all,
    confirm,
    failed,
    done,
    busy,
    open,
    history,
    active,
    attentionCount: confirm.length + failed.length + done.length,
    projects,
  };
}

export async function approveSupervisionItem(item: SupervisionItem, allow: boolean): Promise<void> {
  if (item.kind === 'terminal' && item.askId) {
    await sendTerminalAction({
      action: 'answer',
      terminal: item.id,
      ask: item.askId,
      decision: allow ? 'once' : 'deny',
    });
    return;
  }
  if (item.kind === 'session' && item.askId) {
    const answerWay =
      item.session?.ways?.answer || (item.session?.transport === 'agy-desktop' ? 'agy-desktop' : '');
    const payload: Record<string, unknown> = {
      action: 'answer',
      tool: item.tool,
      session: item.id,
      dir: item.project,
      folder: item.folder,
      ask: item.askId,
      allow,
      scope: 'once',
    };
    if (answerWay === 'agy-desktop') {
      payload.transport = 'agy-desktop';
      if (!allow) payload.choice = 'deny';
    }
    await conversationAction(payload);
  }
}

export async function quickSaySupervisionItem(item: SupervisionItem, text: string): Promise<void> {
  const clean = text.trim();
  if (!clean) return;
  if (item.kind === 'terminal') {
    await sendTerminalAction({ action: 'say', terminal: item.id, text: clean });
    return;
  }
  const sayWay =
    item.session?.ways?.say ||
    (item.session?.transport === 'codex-desktop' || item.session?.transport === 'agy-desktop'
      ? item.session.transport
      : '');
  const payload: Record<string, unknown> = {
    action: 'say',
    tool: item.tool,
    session: item.id,
    dir: item.project,
    folder: item.folder,
    text: clean,
  };
  if (sayWay === 'codex-desktop' || sayWay === 'agy-desktop') {
    payload.transport = sayWay;
  }
  await conversationAction(payload);
}

export async function stopSupervisionItem(item: SupervisionItem): Promise<void> {
  if (item.kind === 'terminal') {
    await sendTerminalAction({ action: 'stop', terminal: item.id });
    return;
  }
  const stopWay = item.session?.ways?.stop || item.session?.transport || 'hook';
  const payload: Record<string, unknown> = {
    action: 'stop',
    tool: item.tool,
    session: item.id,
    dir: item.project,
    folder: item.folder,
    transport: stopWay,
  };
  if (stopWay === 'codex-desktop' && item.turn) payload.turn = item.turn;
  await conversationAction(payload);
}

export async function endSupervisionItem(item: SupervisionItem): Promise<void> {
  if (item.kind === 'terminal') {
    await sendTerminalAction({ action: 'close', terminal: item.id });
    return;
  }
  await conversationAction({
    action: 'end',
    tool: item.tool,
    session: item.id,
    dir: item.project,
    folder: item.folder,
  });
}

export async function archiveSupervisionItem(item: SupervisionItem): Promise<void> {
  if (item.kind !== 'session') return;
  await conversationAction({
    action: 'mark',
    tool: item.tool,
    session: item.id,
    dir: item.project,
    folder: item.folder,
    hidden: true,
    pinned: false,
  });
}
