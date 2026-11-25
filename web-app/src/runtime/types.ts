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
  said?: string;
  asks?: string[];
  error?: string;
  exit_code?: number;
  model?: string;
  effort?: string;
  mode?: string;
  team?: string;
  worktree?: string;
  branch?: string;
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
  role: 'user' | 'assistant' | 'tool' | 'thought' | 'note' | 'ask' | 'error';
  text: string;
  title?: string;
  detail?: string;
  output?: string;
  status?: string;
  at?: number;
  askId?: string;
  options?: Array<{ id: string; label: string; detail?: string }>;
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
  kind: 'dir' | 'file';
  size?: number;
  modified?: number;
}

export type SelectedItem =
  | { kind: 'terminal'; id: string }
  | { kind: 'session'; tool: string; id: string; dir: string; folder: string };

export interface ToolMeta {
  id: string;
  label: string;
  short: string;
  color: string;
  supportsChat: boolean;
}

export const TOOL_CATALOG: Record<string, ToolMeta> = {
  claude: { id: 'claude', label: 'Claude Code', short: 'Claude', color: '#d97706', supportsChat: true },
  codex: { id: 'codex', label: 'Codex', short: 'Codex', color: '#10b981', supportsChat: true },
  agy: { id: 'agy', label: 'Antigravity CLI', short: 'Agy', color: '#6366f1', supportsChat: true },
  gemini: { id: 'gemini', label: 'Gemini CLI', short: 'Gemini', color: '#3b82f6', supportsChat: false },
  opencode: { id: 'opencode', label: 'OpenCode', short: 'OpenCode', color: '#14b8a6', supportsChat: false },
  cursor: { id: 'cursor', label: 'Cursor Agent', short: 'Cursor', color: '#8b5cf6', supportsChat: false },
  qoder: { id: 'qoder', label: 'Qoder', short: 'Qoder', color: '#ec4899', supportsChat: false },
  cline: { id: 'cline', label: 'Cline', short: 'Cline', color: '#f59e0b', supportsChat: false },
  pi: { id: 'pi', label: 'Pi', short: 'Pi', color: '#ef4444', supportsChat: false },
  kiro: { id: 'kiro', label: 'Kiro CLI', short: 'Kiro', color: '#a855f7', supportsChat: false },
  copilot: { id: 'copilot', label: 'Copilot CLI', short: 'Copilot', color: '#06b6d4', supportsChat: false },
  shell: { id: 'shell', label: 'PowerShell 终端', short: 'Shell', color: '#64748b', supportsChat: false },
};

export function toolLabel(tool: string): string {
  return TOOL_CATALOG[tool]?.label || tool;
}

export function toolShort(tool: string): string {
  return TOOL_CATALOG[tool]?.short || tool;
}

export function toolColor(tool: string): string {
  return TOOL_CATALOG[tool]?.color || '#64748b';
}
