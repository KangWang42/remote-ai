export interface RouteEntry {
  kind: 'lan' | 'tailscale' | 'tunnel' | 'own' | 'public';
  url: string;
  priority: number;
  local_auth?: boolean;
}

export interface ProjectInfo {
  name: string;
  path: string;
  fixed: boolean;
  exists: boolean;
  updated: number;
  live: boolean;
  tools: string[];
  kinds: string[];
}

export interface QuotaWindow {
  name: string;
  used: number;
  resets: number;
  group?: string;
}

export interface QuotaReading {
  tool: string;
  at: number;
  why: string;
  windows: QuotaWindow[];
  billing?: { type: string; balance?: string; unit?: string; unlimited?: boolean };
}

export interface TeamCard {
  id: string;
  dir: string;
  goal: string;
  mode: 'lead' | 'review' | 'compare';
  state: string;
  phase: string;
  note: string;
  updated: number;
  awaited: string[];
  tools: string[];
  confirm?: boolean;
}

export interface DeviceInfo {
  online: boolean;
  enabled: boolean;
  version?: string;
  newer?: string;
  shell?: string;
  environment_id?: string;
  max_mode?: string;
  features: string[];
  tools: string[];
  workspaces: string[];
  projects: ProjectInfo[];
  candidates: ProjectInfo[];
  quotas?: QuotaReading[];
  team_cards?: TeamCard[];
  routes?: RouteEntry[];
}

export interface TerminalEntry {
  id: string;
  tool: string;
  dir: string;
  folder: string;
  title: string;
  state: 'starting' | 'running' | 'closed';
  phase?: 'starting' | 'busy' | 'confirm' | 'idle' | 'ended' | 'failed';
  status: string;
  session: string;
  created: number;
  form?: string;
  said?: string;
  asks?: string[];
  model?: string;
  effort?: string;
  mode?: string;
  branch?: string;
  worktree?: string;
  team?: string;
  error?: string;
  exit_code?: number | null;
}

export interface SessionEntry {
  id: string;
  tool: string;
  dir: string;
  folder: string;
  title: string;
  updated: number;
  live: boolean;
  now: boolean;
  status: string;
  kind?: string;
  origin?: string;
  host?: string;
  terminal: string;
  pinned?: boolean;
  hidden?: boolean;
  reach?: string[] | null;
  ask?: { id: string; text: string } | null;
  last?: string;
  team?: string;
  can_takeover?: boolean;
  ways?: Record<string, string> | null;
  why?: Record<string, string> | null;
}

export interface OverviewSnapshot {
  rev: number;
  unchanged?: boolean;
  online?: boolean;
  device: DeviceInfo;
  terminals: TerminalEntry[];
  sessions: SessionEntry[];
}

export interface ChatChunk {
  seq: number;
  data: string;
}

export interface ChatMessageItem {
  id: string;
  role: 'user' | 'assistant' | 'tool' | 'note' | 'ask' | 'error';
  text: string;
  title?: string;
  status?: string;
  output?: string;
  at?: number;
  askId?: string;
  options?: Array<{ id: string; label: string }>;
}

export interface GitFileSummary {
  path: string;
  status: 'M' | 'A' | 'D' | 'R' | '?';
  staged: boolean;
  unstaged: boolean;
  additions: number;
  deletions: number;
  binary: boolean;
}

export interface GitWorktreeItem {
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
  worktrees: GitWorktreeItem[];
}

export interface GitFileDiffResponse {
  path: string;
  diff: string;
  truncated: boolean;
  binary: boolean;
  additions: number;
  deletions: number;
}

export interface FileEntry {
  name: string;
  kind: 'dir' | 'file';
  size?: number;
  updated?: number;
}

export type SelectedItem =
  | { kind: 'terminal'; id: string }
  | { kind: 'session'; tool: string; id: string; dir: string; folder: string };
