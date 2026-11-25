import { normalizeMs } from './time';
import { toolShort, type DeviceInfo, type SessionEntry, type TeamCardEntry, type TerminalEntry } from './types';

/**
 * One conversation, wherever it runs. A conversation is its tool and its id in that tool; the terminals the computer
 * runs it in and the record it keeps of it are two views of the same thing and are shown as one. How it runs now:
 *   chat      this program runs it in its tool's structured mode (a Remote AI conversation)
 *   screen    this program runs it on its tool's own screen (a terminal)
 *   elsewhere a program of the computer's own has it open (the user's terminal, a desktop app)
 *   idle      nobody has it open: its record, which a message goes on with
 * Shell terminals are not conversations: they belong to a project and are listed apart.
 */
export type Runtime = 'chat' | 'screen' | 'elsewhere' | 'idle';
export type Needs = 'confirm' | 'failed' | 'done' | null;

export interface Thread {
  key: string;
  tool: string;
  session: string;
  title: string;
  project: string;
  folder: string;
  branch: string;
  worktree: string;
  runtime: Runtime;
  busy: boolean;
  needs: Needs;
  at: number;
  preview: string;
  pinned: boolean;
  team: string;
  terminal: TerminalEntry | null;
  record: SessionEntry | null;
  /** Where it is open, in words: 对话 / 终端 / Codex 桌面版 … */
  place: string;
}

export interface ProjectGroup {
  name: string;
  threads: Thread[];
  teams: TeamCardEntry[];
  pinned: boolean;
  attention: number;
  busy: number;
  latestAt: number;
}

export interface ThreadModel {
  all: Thread[];
  byKey: Map<string, Thread>;
  attention: Thread[];
  projects: ProjectGroup[];
  /** Conversations open in folders that are no project. */
  loose: Thread[];
  shells: TerminalEntry[];
}

const LIVE = (t: TerminalEntry) => t.state === 'running' || t.state === 'starting';

/** One line of what was said, for a list: without the Markdown marks the tools write. */
function plain(text: string): string {
  return text
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/[*`]+/g, '')
    .replace(/^[#>\s]+/gm, '')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\s+/g, ' ')
    .trim();
}

export function threadKey(tool: string, session: string, terminalId: string): string {
  return session ? `${tool}:${session}` : `t:${terminalId}`;
}

export function buildThreads(
  device: DeviceInfo | null,
  terminals: TerminalEntry[],
  records: SessionEntry[],
  seen: Record<string, number>,
  pinnedProjects: Record<string, boolean>,
): ThreadModel {
  const byKey = new Map<string, Thread>();
  const shells: TerminalEntry[] = [];

  // Terminals first: a running one is how its conversation runs now; of ended ones the relay keeps the last.
  const ordered = [...terminals].sort((a, b) => Number(LIVE(b)) - Number(LIVE(a)) || b.created - a.created);
  for (const t of ordered) {
    if (t.tool === 'shell') {
      if (LIVE(t)) shells.push(t);
      continue;
    }
    const key = threadKey(t.tool, t.session || '', t.id);
    if (byKey.has(key)) continue;
    byKey.set(key, fromTerminal(key, t, seen[key] || 0));
  }

  for (const s of records) {
    if (s.hidden) continue;
    const key = threadKey(s.tool, s.id, '');
    const known = byKey.get(key);
    if (known) {
      known.record = s;
      known.pinned = Boolean(s.pinned);
      if (known.runtime === 'idle' || (known.terminal && !LIVE(known.terminal))) applyRecord(known, s, seen[key] || 0);
      if (!known.title || known.title === toolShort(known.tool)) known.title = s.title || known.title;
      continue;
    }
    const thread = blank(key, s.tool, s.id);
    thread.record = s;
    thread.title = s.title || toolShort(s.tool);
    thread.project = s.dir || '';
    thread.folder = s.folder || '';
    thread.pinned = Boolean(s.pinned);
    thread.team = s.team || '';
    applyRecord(thread, s, seen[key] || 0);
    byKey.set(key, thread);
  }

  const all = [...byKey.values()].sort(order);
  const attention = all.filter((t) => t.needs !== null);

  const groups = new Map<string, ProjectGroup>();
  const group = (name: string) => {
    let g = groups.get(name);
    if (!g) {
      g = { name, threads: [], teams: [], pinned: Boolean(pinnedProjects[name]), attention: 0, busy: 0, latestAt: 0 };
      groups.set(name, g);
    }
    return g;
  };
  for (const p of device?.projects || []) group(p.name);
  for (const name of device?.workspaces || []) group(name);
  const loose: Thread[] = [];
  for (const t of all) {
    if (!t.project) {
      if (t.runtime !== 'idle' || t.needs) loose.push(t);
      continue;
    }
    const g = group(t.project);
    g.threads.push(t);
    if (t.needs) g.attention++;
    if (t.busy) g.busy++;
    g.latestAt = Math.max(g.latestAt, t.at);
  }
  for (const card of device?.team_cards || []) {
    if (card.dir) group(card.dir).teams.push(card);
  }
  const projects = [...groups.values()].sort(
    (a, b) => Number(b.pinned) - Number(a.pinned) || b.attention - a.attention || b.busy - a.busy || b.latestAt - a.latestAt,
  );

  for (const t of all) t.preview = plain(t.preview);
  return { all, byKey, attention, projects, loose, shells };
}

function blank(key: string, tool: string, session: string): Thread {
  return {
    key,
    tool,
    session,
    title: '',
    project: '',
    folder: '',
    branch: '',
    worktree: '',
    runtime: 'idle',
    busy: false,
    needs: null,
    at: 0,
    preview: '',
    pinned: false,
    team: '',
    terminal: null,
    record: null,
    place: '',
  };
}

function fromTerminal(key: string, t: TerminalEntry, seenAt: number): Thread {
  const thread = blank(key, t.tool, t.session || '');
  thread.terminal = t;
  thread.title = t.title || toolShort(t.tool);
  thread.project = t.dir || '';
  thread.folder = t.folder || '';
  thread.branch = t.branch || '';
  thread.worktree = t.worktree || '';
  thread.team = t.team || '';
  thread.at = Math.max(normalizeMs(t.phase_at), normalizeMs(t.created));
  const chat = t.form === 'chat';
  if (LIVE(t)) {
    thread.runtime = chat ? 'chat' : 'screen';
    thread.place = chat ? '对话' : '终端';
    const asking = (t.ask || '').trim().length > 0 || t.phase === 'confirm' || t.status === 'confirm';
    thread.busy = !asking && (t.phase === 'busy' || t.status === 'busy' || t.state === 'starting');
    if (asking) thread.needs = 'confirm';
    else if (!thread.busy && (t.phase === 'done' || t.done) && thread.at > seenAt) thread.needs = 'done';
    thread.preview = asking ? (t.ask || '').trim() : (t.said || '').trim();
  } else {
    thread.place = '';
    const failed = (t.exit_code !== undefined && t.exit_code !== 0) || Boolean(t.error);
    if (failed && thread.at > seenAt) thread.needs = 'failed';
    thread.preview = failed ? t.error || `程序异常退出（代码 ${t.exit_code}）` : (t.said || '').trim();
  }
  return thread;
}

/** What the computer's record says of a conversation no terminal of this program runs now. */
function applyRecord(thread: Thread, s: SessionEntry, seenAt: number) {
  const at = normalizeMs(s.updated);
  thread.at = Math.max(thread.at, at);
  if (s.live) {
    thread.runtime = 'elsewhere';
    thread.place = s.origin || s.kind || '电脑上';
    const asking = s.status === 'confirm' || Boolean(s.ask);
    thread.busy = !asking && s.status === 'busy';
    thread.needs = asking ? 'confirm' : s.status === 'waiting' && at > seenAt ? 'done' : thread.needs;
    thread.preview = asking && s.ask?.text ? s.ask.text : (s.last || thread.preview || '').trim();
  } else {
    thread.runtime = 'idle';
    thread.busy = false;
    if (!thread.preview) thread.preview = (s.last || '').trim();
  }
}

const NEED_RANK: Record<string, number> = { confirm: 0, failed: 1, done: 2 };

function order(a: Thread, b: Thread): number {
  const na = a.needs ? NEED_RANK[a.needs] : 9;
  const nb = b.needs ? NEED_RANK[b.needs] : 9;
  if (na !== nb) return na - nb;
  const la = a.runtime === 'idle' ? 1 : 0;
  const lb = b.runtime === 'idle' ? 1 : 0;
  if (la !== lb) return la - lb;
  if (a.pinned !== b.pinned) return a.pinned ? -1 : 1;
  return b.at - a.at;
}

// ---- what can be done with a conversation now, and why not

export interface Ability {
  ok: boolean;
  why?: string;
}

const yes: Ability = { ok: true };
const no = (why: string): Ability => ({ ok: false, why });

export function canSay(t: Thread): Ability {
  if (t.runtime === 'chat') return t.needs === 'confirm' ? no('先回答它的确认请求') : yes;
  if (t.runtime === 'screen') return no('在终端里直接输入');
  if (t.runtime === 'idle') return ['claude', 'codex', 'agy'].includes(t.tool) ? yes : no('这个工具只能在终端里继续');
  const s = t.record;
  const ways = s?.ways || {};
  const reach = Array.isArray(s?.reach) ? s!.reach! : [];
  if (ways.say || reach.includes('say') || s?.transport === 'codex-desktop' || s?.transport === 'agy-desktop') return yes;
  return no(s?.why?.say || '电脑上的这个窗口不接收外来消息，可以接管到这里继续');
}

export function canStop(t: Thread): Ability {
  if (!t.busy) return no('没有在执行');
  if (t.runtime === 'chat') return yes;
  if (t.runtime === 'screen') return no('在终端里按 Esc 或 Ctrl+C');
  const s = t.record;
  if (s?.ways?.stop || (Array.isArray(s?.reach) && s!.reach!.includes('stop')) || s?.transport) return yes;
  return no(s?.why?.stop || '这个窗口不接受外来的停止');
}

export function canEnd(t: Thread): Ability {
  if (t.runtime === 'chat' || t.runtime === 'screen') return yes;
  if (t.runtime === 'elsewhere') {
    const s = t.record;
    return s?.ways?.end === 'process' || s?.can_takeover ? yes : no(s?.takeover_reason || '无法确认是哪个程序打开了它');
  }
  return no('没有在运行');
}

/** Showing a conversation the other way: 'chat' or 'screen', or why it cannot be. */
export function canReopen(t: Thread, features: string[]): Ability & { to?: 'chat' | 'screen' } {
  if (!['claude', 'codex'].includes(t.tool)) return no('只有 Claude Code 和 Codex 能切换');
  if (t.runtime === 'idle') return { ok: true, to: 'screen' };
  if (t.runtime === 'elsewhere') return no('它开在电脑的其它窗口里');
  if (!features.includes('reopen')) return no('电脑端版本较旧，请更新');
  if (!t.session) return no('发一条消息后才能切换');
  if (t.team) return no('协作任务中的对话不能切换');
  if (t.busy || t.needs === 'confirm') return no('等这一轮结束或先停止');
  return { ok: true, to: t.runtime === 'chat' ? 'screen' : 'chat' };
}

export function canTakeOver(t: Thread): Ability {
  if (t.runtime !== 'elsewhere') return no('');
  return t.record?.can_takeover ? yes : no(t.record?.takeover_reason || '无法确认是哪个程序打开了它');
}

export function canHandOff(t: Thread, features: string[]): Ability {
  if (!features.includes('work')) return no('电脑端版本较旧，请更新');
  if (!t.session) return no('发一条消息后才能交接');
  if (!t.record && !t.team) return no('电脑还没有列出这段对话，稍后再试');
  return yes;
}

export function canArchive(t: Thread): Ability {
  return t.runtime === 'idle' && t.record ? yes : no('只能归档没有运行的对话');
}

export function runtimeLabel(t: Thread): string {
  if (t.runtime === 'chat') return '对话';
  if (t.runtime === 'screen') return '终端';
  if (t.runtime === 'elsewhere') return t.place || '电脑上';
  return '';
}

// ---- seen and pinned, kept on this computer's browser

const SEEN_KEY = 'rcli-console-seen-v2';
const PINNED_KEY = 'rcli-console-pinned-projects-v1';

function loadMap<T>(key: string): Record<string, T> {
  try {
    const parsed = JSON.parse(localStorage.getItem(key) || '{}');
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

function saveMap(key: string, map: Record<string, unknown>) {
  try {
    const entries = Object.entries(map);
    localStorage.setItem(key, JSON.stringify(entries.length > 400 ? Object.fromEntries(entries.slice(-300)) : map));
  } catch {
    // storage full or blocked: the marks last as long as the page
  }
}

export const loadSeen = () => loadMap<number>(SEEN_KEY);
export const saveSeen = (map: Record<string, number>) => saveMap(SEEN_KEY, map);
export const loadPinned = () => loadMap<boolean>(PINNED_KEY);
export const savePinned = (map: Record<string, boolean>) => saveMap(PINNED_KEY, map);
