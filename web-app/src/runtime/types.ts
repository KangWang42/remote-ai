export interface RouteEntry {
  kind: 'lan' | 'tailscale' | 'tunnel' | 'own' | 'public' | string;
  url: string;
  priority: number;
  label?: string;
  requires_local_auth?: boolean;
}

export interface ProjectEntry {
  name: string;
  path?: string;
  fixed?: boolean;
}

export interface CandidateEntry {
  name: string;
  path: string;
  tools?: string[];
  kinds?: string[];
}

export interface QuotaWindow {
  name: string;
  used: number;
  resets: number;
  group?: string;
}

export interface QuotaEntry {
  tool: string;
  at: number;
  windows: QuotaWindow[];
  note?: string;
  plan?: string;
}

export interface TeamCardEntry {
  id: string;
  mode: string;
  goal: string;
  dir: string;
  state: string;
  round?: number;
  rounds?: number;
  confirm?: boolean;
  note?: string;
  updated?: number;
  tools?: string[];
  roster?: Array<{ role: string; tool: string }>;
}

export interface ScheduleEntry {
  id: string;
  tool: string;
  session: string;
  title?: string;
  text: string;
  kind: 'once' | 'daily' | 'every';
  time?: string;
  at?: number;
  minutes?: number;
  days?: number[];
  on: boolean;
  next?: number;
  runs?: number;
  last?: { at: number; ok: boolean; note?: string } | null;
}

export interface ChainEntry {
  id: string;
  from: string;
  from_tool: string;
  from_title?: string;
  to: string;
  to_tool: string;
  to_title?: string;
  text: string;
  left: number;
  rounds: number;
  runs?: number;
  last?: { at: number; ok: boolean; note?: string } | null;
}

export interface DeviceInfo {
  online: boolean;
  enabled: boolean;
  version?: string;
  newer?: string;
  environment_id?: string;
  shell?: string;
  max_mode?: string;
  workspaces: string[];
  tools: string[];
  features?: string[];
  projects?: ProjectEntry[];
  candidates?: CandidateEntry[];
  quotas?: QuotaEntry[];
  team_cards?: TeamCardEntry[];
  routes?: RouteEntry[];
}

export interface TerminalEntry {
  id: string;
  tool: string;
  dir: string;
  folder: string;
  title: string;
  state: 'starting' | 'running' | 'closed' | string;
  phase?: 'starting' | 'busy' | 'confirm' | 'idle' | 'ended' | 'failed' | string;
  status?: string;
  form?: 'chat' | '' | string;
  session?: string;
  seq: number;
  created: number;
  phase_at?: number;
  done?: boolean;
  said?: string;
  ask?: string;
  asks?: string[];
  error?: string;
  exit_code?: number;
  model?: string;
  effort?: string;
  mode?: string;
  team?: string;
  worktree?: string;
  branch?: string;
  /** A task another conversation gave out: that conversation's terminal, and the task's id. */
  parent?: string;
  task?: string;
}

export interface SessionAsk {
  id: string;
  text: string;
  kind?: string;
  options?: Array<{ id: string; label: string; detail?: string }>;
}

export interface SessionEntry {
  id: string;
  tool: string;
  dir: string;
  folder: string;
  title: string;
  updated: number;
  live: boolean;
  now?: boolean;
  status?: string;
  host?: string;
  origin?: string;
  kind?: string;
  activity?: string;
  pinned?: boolean;
  hidden?: boolean;
  can_takeover?: boolean;
  takeover_reason?: string;
  reach?: string[] | null;
  queued?: Array<{ id: string; text: string; state: 'queued' | 'sending' | 'paused'; at: number }>;
  ask?: SessionAsk | null;
  last?: string;
  team?: string;
  transport?: string;
  turn?: string;
  ways?: Record<string, string> | null;
  why?: Record<string, string> | null;
}

export interface OverviewSnapshot {
  rev?: number;
  unchanged?: boolean;
  online?: boolean;
  device?: DeviceInfo;
  terminals?: TerminalEntry[];
  sessions?: SessionEntry[];
}

export interface ChatChunk {
  seq: number;
  data: string;
}

export interface ChatMessageItem {
  id: string;
  op?: string;
  ops?: string[];
  role: 'user' | 'assistant' | 'tool' | 'thought' | 'note' | 'ask' | 'error' | 'switch' | 'task' | 'relayed';
  tool?: string;
  /** role `task`: a task this conversation gave out, as its last `task` event says it stands. */
  task?: TaskCard;
  text: string;
  title?: string;
  detail?: string;
  output?: string;
  status?: string;
  at?: number;
  askId?: string;
  options?: Array<{ id: string; label: string; detail?: string }>;
}

export interface TaskCard {
  task_id: string;
  tool: string;
  title: string;
  state: 'running' | 'waiting' | 'completed' | 'failed' | 'cancelled' | string;
  terminal: string;
  started?: number;
  ended?: number;
  said?: string;
  error?: string;
  question?: string;
  worktree?: string;
}

export interface GitFileSummary {
  path: string;
  status: 'M' | 'A' | 'D' | 'R' | '?' | string;
  staged: boolean;
  unstaged: boolean;
  additions: number;
  deletions: number;
  binary: boolean;
}

export interface WorktreeSummary {
  id: string;
  branch: string;
  head: string;
  main: boolean;
  managed: boolean;
  locked: boolean;
}

export interface GitStatusResponse {
  is_repo: boolean;
  branch: string;
  head: string;
  ahead: number;
  behind: number;
  worktree: string;
  files: GitFileSummary[];
  worktrees: WorktreeSummary[];
}

export interface GitFileDiffResponse {
  path: string;
  diff: string;
  truncated: boolean;
  binary: boolean;
  additions: number;
  deletions: number;
}

export interface GitBranchEntry {
  name: string;
  head: string;
  updated: number;
  subject: string;
  current: boolean;
}

export interface GitCheckpointEntry {
  ref: string;
  commit: string;
  at: number;
  label: string;
}

export interface ProjectScriptEntry {
  name: string;
  command: string;
  detail: string;
  source: string;
}

export interface ToolModelOption {
  id: string;
  name?: string;
  label?: string;
  efforts?: string[];
}

export interface FileEntry {
  name: string;
  dir: boolean;
  kind?: 'dir' | 'file';
  size?: number;
  modified?: number;
  hidden?: boolean;
}

export interface FilePreviewTarget {
  dir: string;
  path: string;
  name: string;
}


export interface ToolMeta {
  id: string;
  label: string;
  short: string;
  mono: string;
  color: string;
  lightBg: string;
  lightFg: string;
  darkBg: string;
  darkFg: string;
  supportsChat: boolean;
}

/** Matches Android Kit.java & web/app.css pastel squircle identity families. */
export const TOOL_CATALOG: Record<string, ToolMeta> = {
  claude: { id: 'claude', label: 'Claude Code', short: 'Claude', mono: 'CC', color: '#a9441d', lightBg: '#fbe9e1', lightFg: '#a9441d', darkBg: '#3a2318', darkFg: '#f7b593', supportsChat: true },
  codex: { id: 'codex', label: 'Codex', short: 'Codex', mono: 'CX', color: '#1d4fbf', lightBg: '#e6eefd', lightFg: '#1d4fbf', darkBg: '#17284a', darkFg: '#9dbbf7', supportsChat: true },
  agy: { id: 'agy', label: 'Antigravity CLI', short: 'Agy', mono: 'AG', color: '#5536c9', lightBg: '#ece8fd', lightFg: '#5536c9', darkBg: '#261d4a', darkFg: '#bca9f8', supportsChat: true },
  gemini: { id: 'gemini', label: 'Gemini CLI', short: 'Gemini', mono: 'GM', color: '#2445c8', lightBg: '#e8efff', lightFg: '#2445c8', darkBg: '#1a2748', darkFg: '#9ab8ff', supportsChat: false },
  opencode: { id: 'opencode', label: 'OpenCode', short: 'OpenCode', mono: 'OC', color: '#15803d', lightBg: '#e6f6ec', lightFg: '#15803d', darkBg: '#153224', darkFg: '#86efac', supportsChat: false },
  cursor: { id: 'cursor', label: 'Cursor Agent', short: 'Cursor', mono: 'CS', color: '#6d28d9', lightBg: '#f1eaff', lightFg: '#6d28d9', darkBg: '#2b1f48', darkFg: '#d4b8ff', supportsChat: false },
  qoder: { id: 'qoder', label: 'Qoder', short: 'Qoder', mono: 'QD', color: '#2445c8', lightBg: '#eef1ff', lightFg: '#2445c8', darkBg: '#1b2548', darkFg: '#9dbbf7', supportsChat: false },
  cline: { id: 'cline', label: 'Cline', short: 'Cline', mono: 'CL', color: '#7c3aed', lightBg: '#f3e8ff', lightFg: '#7c3aed', darkBg: '#2d1b4e', darkFg: '#d8b4fe', supportsChat: false },
  pi: { id: 'pi', label: 'Pi', short: 'Pi', mono: 'PI', color: '#9a5b00', lightBg: '#fef3c7', lightFg: '#9a5b00', darkBg: '#3b2a10', darkFg: '#fcd34d', supportsChat: false },
  kiro: { id: 'kiro', label: 'Kiro CLI', short: 'Kiro', mono: 'KR', color: '#9333ea', lightBg: '#f5e8ff', lightFg: '#9333ea', darkBg: '#321c47', darkFg: '#e0b3ff', supportsChat: false },
  copilot: { id: 'copilot', label: 'Copilot CLI', short: 'Copilot', mono: 'CP', color: '#0b7a96', lightBg: '#e5f5f9', lightFg: '#0b7a96', darkBg: '#15303a', darkFg: '#8cd8eb', supportsChat: false },
  shell: { id: 'shell', label: 'PowerShell 终端', short: 'Shell', mono: 'SH', color: '#5b6270', lightBg: '#eef0f5', lightFg: '#3f4756', darkBg: '#2a2f3d', darkFg: '#c5ccd9', supportsChat: false },
};

export function toolMeta(tool: string): ToolMeta {
  return TOOL_CATALOG[tool] || TOOL_CATALOG.shell;
}

export function toolLabel(tool: string): string {
  return TOOL_CATALOG[tool]?.label || tool;
}

export function toolShort(tool: string): string {
  return TOOL_CATALOG[tool]?.short || tool;
}

export function toolColor(tool: string): string {
  return TOOL_CATALOG[tool]?.color || '#5b6270';
}

export function toolBadgeStyle(tool: string): Record<string, string> {
  const m = toolMeta(tool);
  return {
    '--tb-lbg': m.lightBg,
    '--tb-lfg': m.lightFg,
    '--tb-dbg': m.darkBg,
    '--tb-dfg': m.darkFg,
  };
}
