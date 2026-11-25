import { useMemo, useState } from 'preact/hooks';
import { postDesktopMessage } from '../runtime/api';
import {
  toolColor,
  toolShort,
  type DeviceInfo,
  type SelectedItem,
  type SessionEntry,
  type TerminalEntry,
} from '../runtime/types';

interface Props {
  device: DeviceInfo | null;
  terminals: TerminalEntry[];
  sessions: SessionEntry[];
  selected: SelectedItem | null;
  onSelect: (item: SelectedItem) => void;
  onNewTask: (defaultDir?: string) => void;
  onAddProject: () => void;
  onOpenPalette: () => void;
  onOpenRightTab: (tab: 'diff' | 'files' | 'teams' | 'connect') => void;
  onToggleFocus: () => void;
  connected: boolean;
  latencyMs: number | null;
}

function routeBadge(device: DeviceInfo | null): string {
  const host = window.location.hostname;
  if (host === '127.0.0.1' || host === 'localhost') return '本机直连';
  if (host.endsWith('.ts.net') || /^100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])\./.test(host)) return 'Tailscale';
  if (host.endsWith('.trycloudflare.com')) return '公网隧道';
  if (/^\/c\/[a-f0-9]{20}\//.test(window.location.pathname)) return '公共中转';
  if (/^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(host)) return '局域网直连';
  return device?.routes?.length ? `多路由 (${device.routes.length})` : '中转连接';
}

export function Sidebar({
  device,
  terminals,
  sessions,
  selected,
  onSelect,
  onNewTask,
  onAddProject,
  onOpenPalette,
  onOpenRightTab,
  onToggleFocus,
  connected,
  latencyMs,
}: Props) {
  const [query, setQuery] = useState('');
  const [toolFilter, setToolFilter] = useState<string>('all');
  const [collapsedDirs, setCollapsedDirs] = useState<Record<string, boolean>>({});

  const q = query.trim().toLowerCase();

  const matches = (title: string, dir: string, tool: string) => {
    if (toolFilter !== 'all' && tool !== toolFilter) return false;
    if (!q) return true;
    return title.toLowerCase().includes(q) || dir.toLowerCase().includes(q) || tool.toLowerCase().includes(q);
  };

  const { confirmItems, activeItems, openItems, projectGroups, recentSessions } = useMemo(() => {
    const confirm: Array<{ key: string; title: string; sub: string; tool: string; branch?: string; item: SelectedItem }> = [];
    const active: typeof confirm = [];
    const opened: typeof confirm = [];
    const byProject = new Map<string, typeof confirm>();
    const recent: typeof confirm = [];

    for (const p of device?.projects || []) {
      byProject.set(p.name, []);
    }

    const attachedSessionIds = new Set<string>();
    for (const t of terminals) {
      if (t.session && t.state !== 'closed') attachedSessionIds.add(t.session);
      if (!matches(t.title || t.tool, t.dir || t.folder, t.tool)) continue;
      const entry = {
        key: `t:${t.id}`,
        title: t.title || toolShort(t.tool),
        sub: t.dir || t.folder || '终端',
        tool: t.tool,
        branch: t.branch || (t.worktree ? `wt:${t.worktree}` : undefined),
        item: { kind: 'terminal' as const, id: t.id },
      };
      if (t.state === 'running' || t.state === 'starting') {
        if (t.phase === 'confirm' || t.status === 'confirm') confirm.push(entry);
        else if (t.phase === 'busy' || t.status === 'busy' || t.state === 'starting') active.push(entry);
        else opened.push(entry);
      }
      if (t.dir) {
        const list = byProject.get(t.dir) || [];
        list.push(entry);
        byProject.set(t.dir, list);
      }
    }

    for (const s of sessions) {
      if (s.hidden || attachedSessionIds.has(s.id)) continue;
      if (!matches(s.title, s.dir || s.folder, s.tool)) continue;
      const entry = {
        key: `s:${s.tool}:${s.id}`,
        title: s.title,
        sub: s.dir || s.folder || s.origin || '会话',
        tool: s.tool,
        item: { kind: 'session' as const, tool: s.tool, id: s.id, dir: s.dir, folder: s.folder },
      };
      if (s.live) {
        if (s.status === 'confirm' || s.ask) confirm.push(entry);
        else if (s.status === 'busy') active.push(entry);
        else if (s.now) opened.push(entry);
        else recent.push(entry);
      } else {
        recent.push(entry);
      }
      if (s.dir && (s.live || (byProject.get(s.dir)?.length || 0) < 6)) {
        const list = byProject.get(s.dir) || [];
        list.push(entry);
        byProject.set(s.dir, list);
      }
    }

    return {
      confirmItems: confirm,
      activeItems: active,
      openItems: opened,
      projectGroups: Array.from(byProject.entries()),
      recentSessions: recent.slice(0, 20),
    };
  }, [device, terminals, sessions, q, toolFilter]);

  const filterTools = useMemo(() => {
    const detected = device?.tools || ['claude', 'codex', 'agy', 'shell'];
    const unique = Array.from(new Set(['all', ...detected]));
    return unique.slice(0, 8);
  }, [device?.tools]);

  const isSelected = (item: SelectedItem) => {
    if (!selected || selected.kind !== item.kind) return false;
    return selected.id === item.id;
  };

  const renderRow = (row: { key: string; title: string; sub: string; tool: string; branch?: string; item: SelectedItem }, badge?: string) => (
    <button
      key={row.key}
      type="button"
      class={`nav-row${isSelected(row.item) ? ' active' : ''}`}
      onClick={() => onSelect(row.item)}
    >
      <span class="tool-dot" style={{ background: toolColor(row.tool) }} title={toolShort(row.tool)} />
      <span class="nav-row-main">
        <span class="nav-row-title">{row.title}</span>
        <span class="nav-row-sub">
          {row.sub}
          {row.branch ? <span class="branch-chip">{row.branch}</span> : null}
        </span>
      </span>
      {badge ? <span class={`state-pill pill-${badge}`}>{badge === 'confirm' ? '确认' : '运行'}</span> : null}
    </button>
  );

  const primaryQuotas = (device?.quotas || []).filter((q) => q.windows && q.windows.length > 0).slice(0, 3);
  const teamCount = (device?.team_cards || []).filter((c) => c.state === 'running' || c.confirm).length;
  const isDesktopWebView = typeof (window as any).chrome?.webview?.postMessage === 'function';

  return (
    <aside class="sidebar">
      <div class="sidebar-head">
        <div class="brand-row">
          <div class="brand-mark">
            {typeof (window as any).RemoteCliNative?.home === 'function' ? (
              <button
                type="button"
                class="icon-btn"
                title="返回多机工作台"
                onClick={() => (window as any).RemoteCliNative.home()}
              >
                ←
              </button>
            ) : null}
            <span class={`conn-dot ${connected && device?.online ? 'on' : 'off'}`} />
            <strong>Remote AI</strong>
            <span class="route-tag" title="点击管理路由、外观与手机配对" onClick={() => onOpenRightTab('connect')}>
              {routeBadge(device)}
              {latencyMs !== null ? ` · ${latencyMs}ms` : ''}
            </span>
          </div>
          <div class="head-actions">
            {isDesktopWebView ? (
              <button
                type="button"
                class="icon-btn"
                title="手机扫码连接与本机设置"
                onClick={() => postDesktopMessage('open-classic')}
              >
                扫码
              </button>
            ) : null}
            <button type="button" class="icon-btn" title="命令面板 (Ctrl+K)" onClick={onOpenPalette}>
              ⌘K
            </button>
            <button type="button" class="primary-btn compact" onClick={() => onNewTask()}>
              + 新建
            </button>
          </div>
        </div>

        <div class="search-row">
          <input
            type="search"
            class="search-input"
            placeholder="筛选会话、项目或终端…"
            value={query}
            onInput={(e) => setQuery((e.target as HTMLInputElement).value)}
          />
        </div>

        <div class="filter-chips">
          {filterTools.map((t) => (
            <button
              key={t}
              type="button"
              class={`chip${toolFilter === t ? ' active' : ''}`}
              onClick={() => setToolFilter(t)}
            >
              {t === 'all' ? '全部' : toolShort(t)}
            </button>
          ))}
        </div>
      </div>

      <div class="sidebar-scroll">
        {confirmItems.length > 0 && (
          <section class="nav-group">
            <div class="group-title danger">等待确认 ({confirmItems.length})</div>
            {confirmItems.map((r) => renderRow(r, 'confirm'))}
          </section>
        )}

        {activeItems.length > 0 && (
          <section class="nav-group">
            <div class="group-title busy">正在执行 ({activeItems.length})</div>
            {activeItems.map((r) => renderRow(r, 'busy'))}
          </section>
        )}

        {openItems.length > 0 && (
          <section class="nav-group">
            <div class="group-title">进行中 ({openItems.length})</div>
            {openItems.map((r) => renderRow(r))}
          </section>
        )}

        <section class="nav-group">
          <div class="group-head-row">
            <div class="group-title">项目工作区 ({projectGroups.length})</div>
            <button
              type="button"
              class="mini-add"
              title="添加项目文件夹"
              onClick={onAddProject}
            >
              +
            </button>
          </div>
          {projectGroups.length === 0 ? (
            <button type="button" class="ghost-btn compact wide" onClick={onAddProject}>
              + 添加第一个项目文件夹
            </button>
          ) : (
            projectGroups.map(([dir, items]) => {
              const collapsed = !!collapsedDirs[dir];
              return (
                <div key={dir} class="project-block">
                  <div class="project-head">
                    <button
                      type="button"
                      class="project-toggle"
                      onClick={() => setCollapsedDirs((prev) => ({ ...prev, [dir]: !prev[dir] }))}
                    >
                      <span class="caret">{collapsed ? '▸' : '▾'}</span>
                      <span class="project-name">{dir}</span>
                      <span class="project-count">{items.length}</span>
                    </button>
                    <button
                      type="button"
                      class="mini-add"
                      title={`在 ${dir} 中新建 AI 任务`}
                      onClick={() => onNewTask(dir)}
                    >
                      +
                    </button>
                  </div>
                  {!collapsed && items.length > 0 && (
                    <div class="project-items">{items.map((r) => renderRow(r))}</div>
                  )}
                </div>
              );
            })
          )}
        </section>

        {recentSessions.length > 0 && (
          <section class="nav-group">
            <div class="group-title">最近对话</div>
            {recentSessions.map((r) => renderRow(r))}
          </section>
        )}
      </div>

      <div class="sidebar-foot">
        {primaryQuotas.length > 0 && (
          <div class="quota-strip">
            {primaryQuotas.map((q) => {
              const w = q.windows[0];
              const pct = Math.min(100, Math.max(0, Math.round(w.used)));
              return (
                <div key={q.tool} class="quota-mini" title={`${toolShort(q.tool)} · ${w.name}: 已用 ${pct}%`}>
                  <span>{toolShort(q.tool)}</span>
                  <div class="quota-bar">
                    <div class="quota-fill" style={{ width: `${pct}%`, background: toolColor(q.tool) }} />
                  </div>
                  <span>{pct}%</span>
                </div>
              );
            })}
          </div>
        )}
        <div class="foot-links">
          <button type="button" class="foot-btn" onClick={() => onOpenRightTab('teams')}>
            协作{teamCount > 0 ? ` (${teamCount})` : ''}
          </button>
          <button type="button" class="foot-btn" onClick={() => onOpenRightTab('connect')}>
            外观 / 连接
          </button>
          <button type="button" class="icon-btn" title="专注模式 (Ctrl+\)" onClick={onToggleFocus}>
            ⤢
          </button>
        </div>
      </div>
    </aside>
  );
}
