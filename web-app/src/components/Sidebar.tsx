import { useEffect, useMemo, useState } from 'preact/hooks';
import { postDesktopMessage, sendTerminalAction } from '../runtime/api';
import { formatExactTime, formatRelativeTime, normalizeMs } from '../runtime/time';
import {
  toolBadgeStyle,
  toolColor,
  toolMeta,
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
  onOpenProjectFiles: (dir: string) => void;
  onAddProject: () => void;
  onOpenPalette: () => void;
  onOpenRightTab: (tab: 'files' | 'diff' | 'teams' | 'connect') => void;
  onToggleFocus: () => void;
  onRefresh: () => void;
  connected: boolean;
  latencyMs: number | null;
}

function routeBadge(device: DeviceInfo | null): string {
  const host = window.location.hostname;
  if (host === '127.0.0.1' || host === 'localhost') return '本机直连';
  if (host.endsWith('.ts.net') || /^100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])\./.test(host)) return 'Tailscale';
  if (host.endsWith('.trycloudflare.com')) return '公网隧道';
  if (/^\/c\/[a-f0-9]{20}\//.test(window.location.pathname)) return '公共中转';
  if (/^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(host)) return '局域网';
  return device?.routes?.length ? `多路由 (${device.routes.length})` : '中转';
}

export function Sidebar({
  device,
  terminals,
  sessions,
  selected,
  onSelect,
  onNewTask,
  onOpenProjectFiles,
  onAddProject,
  onOpenPalette,
  onOpenRightTab,
  onToggleFocus,
  onRefresh,
  connected,
  latencyMs,
}: Props) {
  const [query, setQuery] = useState('');
  const [toolFilter, setToolFilter] = useState<string>('all');
  const [stateFilter, setStateFilter] = useState<'all' | 'confirm' | 'busy' | 'open'>('all');
  const [collapsedDirs, setCollapsedDirs] = useState<Record<string, boolean>>({});
  const [recentLimit, setRecentLimit] = useState(16);
  const [batchMode, setBatchMode] = useState(false);
  const [pickedIds, setPickedIds] = useState<Record<string, boolean>>({});
  const [batchBusy, setBatchBusy] = useState(false);
  const [batchNote, setBatchNote] = useState('');
  const [nowMs, setNowMs] = useState(() => Date.now());

  useEffect(() => {
    const timer = window.setInterval(() => setNowMs(Date.now()), 30000);
    return () => window.clearInterval(timer);
  }, []);

  const q = query.trim().toLowerCase();

  const matches = (title: string, dir: string, tool: string) => {
    if (toolFilter !== 'all' && tool !== toolFilter) return false;
    if (!q) return true;
    return title.toLowerCase().includes(q) || dir.toLowerCase().includes(q) || tool.toLowerCase().includes(q);
  };

  const { confirmItems, activeItems, openItems, projectGroups, recentSessions } = useMemo(() => {
    const confirm: Array<{ key: string; title: string; sub: string; tool: string; branch?: string; at: number; item: SelectedItem }> = [];
    const active: typeof confirm = [];
    const opened: typeof confirm = [];
    const byProject = new Map<string, typeof confirm>();
    const recent: typeof confirm = [];

    for (const p of device?.projects || []) {
      byProject.set(p.name, []);
    }

    const sessionTimeById = new Map<string, number>();
    for (const s of sessions) {
      const ts = normalizeMs(s.updated);
      if (ts > 0) sessionTimeById.set(s.id, ts);
    }

    const attachedSessionIds = new Set<string>();
    for (const t of terminals) {
      if (t.session && t.state !== 'closed') attachedSessionIds.add(t.session);
      if (!matches(t.title || t.tool, t.dir || t.folder, t.tool)) continue;
      const at = Math.max(
        normalizeMs(t.phase_at),
        normalizeMs(t.created),
        t.session ? sessionTimeById.get(t.session) || 0 : 0,
      );
      const entry = {
        key: `t:${t.id}`,
        title: t.title || toolShort(t.tool),
        sub: t.dir || t.folder || '终端',
        tool: t.tool,
        branch: t.branch || (t.worktree ? `wt:${t.worktree}` : undefined),
        at,
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
        at: normalizeMs(s.updated),
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

    const byLatest = (a: { at: number }, b: { at: number }) => b.at - a.at;
    confirm.sort(byLatest);
    active.sort(byLatest);
    opened.sort(byLatest);
    recent.sort(byLatest);
    for (const list of byProject.values()) list.sort(byLatest);

    return {
      confirmItems: confirm,
      activeItems: active,
      openItems: opened,
      projectGroups: Array.from(byProject.entries()),
      recentSessions: recent,
    };
  }, [device, terminals, sessions, q, toolFilter]);

  const filterTools = useMemo(() => {
    const detected = device?.tools || ['claude', 'codex', 'agy', 'shell'];
    return Array.from(new Set(['all', ...detected])).slice(0, 8);
  }, [device?.tools]);

  const runningTerminalIds = useMemo(
    () => terminals.filter((t) => t.state === 'running' || t.state === 'starting').map((t) => t.id),
    [terminals],
  );
  const closedTerminalIds = useMemo(
    () => terminals.filter((t) => t.state === 'closed').map((t) => t.id),
    [terminals],
  );
  const pickedList = useMemo(
    () => terminals.filter((t) => pickedIds[t.id]).map((t) => t.id),
    [terminals, pickedIds],
  );

  const runBatchAction = async (action: 'stop' | 'close', ids: string[]) => {
    if (ids.length === 0 || batchBusy) return;
    setBatchBusy(true);
    setBatchNote('');
    let ok = 0;
    try {
      await Promise.all(
        ids.map((id) =>
          sendTerminalAction({ action, terminal: id })
            .then(() => {
              ok++;
            })
            .catch(() => {}),
        ),
      );
      setBatchNote(action === 'stop' ? `已发送停止至 ${ok} 个会话` : `已关闭 ${ok} 个会话`);
      setPickedIds({});
      onRefresh();
    } finally {
      setBatchBusy(false);
    }
  };

  const isSelected = (item: SelectedItem) => {
    if (!selected || selected.kind !== item.kind) return false;
    return selected.id === item.id;
  };

  const renderRow = (row: { key: string; title: string; sub: string; tool: string; branch?: string; at: number; item: SelectedItem }, badge?: string) => {
    const meta = toolMeta(row.tool);
    const ago = formatRelativeTime(row.at, nowMs);
    const exact = formatExactTime(row.at);
    const isTerm = row.item.kind === 'terminal';
    const checked = isTerm && !!pickedIds[row.item.id];
    return (
      <button
        key={row.key}
        type="button"
        class={`nav-row${!batchMode && isSelected(row.item) ? ' active' : ''}${checked ? ' is-picked' : ''}`}
        onClick={() => {
          if (batchMode && isTerm) {
            setPickedIds((prev) => ({ ...prev, [row.item.id]: !prev[row.item.id] }));
            return;
          }
          onSelect(row.item);
        }}
      >
        {batchMode && isTerm && (
          <span class={`batch-check${checked ? ' checked' : ''}`} aria-hidden="true">
            {checked ? '✓' : ''}
          </span>
        )}
        <span class="tool-squircle" style={toolBadgeStyle(row.tool)} title={meta.label}>
          {meta.mono}
        </span>
        <span class="nav-row-main">
          <span class="nav-row-top">
            <span class="nav-row-title">{row.title}</span>
            {ago && (
              <span class="nav-row-time" title={exact ? `上次工作：${exact}` : undefined}>
                {ago}
              </span>
            )}
          </span>
          <span class="nav-row-sub">
            <span>{row.sub}</span>
            {row.branch ? <span class="branch-chip">{row.branch}</span> : null}
          </span>
        </span>
        {badge ? <span class={`state-pill pill-${badge}`}>{badge === 'confirm' ? '确认' : '运行'}</span> : null}
      </button>
    );
  };

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
                class="circle-btn"
                title="返回多机工作台"
                onClick={() => (window as any).RemoteCliNative.home()}
              >
                ←
              </button>
            ) : null}
            <strong>Remote AI</strong>
            <button type="button" class="route-pill" title="外观主题与连接状态" onClick={() => onOpenRightTab('connect')}>
              <span class={`conn-dot ${connected && device?.online ? 'on' : 'off'}`} />
              <span>
                {routeBadge(device)}
                {latencyMs !== null ? ` · ${latencyMs}ms` : ''}
              </span>
            </button>
          </div>
          <div class="head-actions">
            {isDesktopWebView ? (
              <button
                type="button"
                class="circle-btn"
                title="手机扫码连接与设置"
                onClick={() => postDesktopMessage('open-classic')}
              >
                <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
                  <path d="M4 4h6v6H4zm10 0h6v6h-6zM4 14h6v6H4zm10 2h2v2h-2zm4-2h2v6h-4v-2h2z" fill="currentColor" />
                </svg>
              </button>
            ) : null}
            <button
              type="button"
              class={`circle-btn${batchMode ? ' solid' : ''}`}
              title={batchMode ? '退出批量管理' : '批量管理多个会话'}
              onClick={() => {
                setBatchMode((v) => !v);
                setPickedIds({});
                setBatchNote('');
              }}
            >
              <svg viewBox="0 0 24 24" width="15" height="15" aria-hidden="true">
                <path d="M5 7h2m4 0h8M5 12h2m4 0h8M5 17h2m4 0h8" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" />
              </svg>
            </button>
            <button type="button" class="circle-btn" title="命令面板 (Ctrl+K)" onClick={onOpenPalette}>
              <svg viewBox="0 0 24 24" width="15" height="15" aria-hidden="true">
                <circle cx="11" cy="11" r="6.5" fill="none" stroke="currentColor" stroke-width="2" />
                <path d="m16 16 4 4" stroke="currentColor" stroke-width="2" stroke-linecap="round" />
              </svg>
            </button>
            <button type="button" class="circle-btn solid" title="新建 AI 任务" onClick={() => onNewTask()}>
              <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
                <path d="M12 5v14M5 12h14" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" />
              </svg>
            </button>
          </div>
        </div>

        {/* Interactive 3-column KPI Monitor Card matching mobile app */}
        <div class="kpi-card">
          <button
            type="button"
            class={`kpi-cell${confirmItems.length > 0 ? ' is-confirm' : ''}${stateFilter === 'confirm' ? ' is-selected' : ''}`}
            title="点击仅看等待确认的任务"
            onClick={() => setStateFilter((v) => (v === 'confirm' ? 'all' : 'confirm'))}
          >
            <span>待确认</span>
            <strong>{confirmItems.length}</strong>
          </button>
          <button
            type="button"
            class={`kpi-cell${activeItems.length > 0 ? ' is-busy' : ''}${stateFilter === 'busy' ? ' is-selected' : ''}`}
            title="点击仅看正在执行的任务"
            onClick={() => setStateFilter((v) => (v === 'busy' ? 'all' : 'busy'))}
          >
            <span>执行中</span>
            <strong>{activeItems.length}</strong>
          </button>
          <button
            type="button"
            class={`kpi-cell${openItems.length > 0 ? ' is-open' : ''}${stateFilter === 'open' ? ' is-selected' : ''}`}
            title="点击仅看已打开的会话"
            onClick={() => setStateFilter((v) => (v === 'open' ? 'all' : 'open'))}
          >
            <span>已打开</span>
            <strong>{openItems.length}</strong>
          </button>
        </div>

        {batchMode && (
          <div class="batch-bar">
            <div class="batch-bar-top">
              <strong>批量管理 · 已选 {pickedList.length}</strong>
              <div class="batch-picks">
                <button
                  type="button"
                  class="crumb-btn"
                  onClick={() => {
                    const next: Record<string, boolean> = {};
                    for (const id of runningTerminalIds) next[id] = true;
                    setPickedIds(next);
                  }}
                >
                  运行中({runningTerminalIds.length})
                </button>
                {closedTerminalIds.length > 0 && (
                  <button
                    type="button"
                    class="crumb-btn"
                    onClick={() => {
                      const next: Record<string, boolean> = {};
                      for (const id of closedTerminalIds) next[id] = true;
                      setPickedIds(next);
                    }}
                  >
                    已结束({closedTerminalIds.length})
                  </button>
                )}
              </div>
            </div>
            <div class="batch-bar-actions">
              <button
                type="button"
                class="ghost-btn compact"
                disabled={batchBusy || pickedList.length === 0}
                onClick={() => runBatchAction('stop', pickedList)}
              >
                批量停止
              </button>
              <button
                type="button"
                class="danger-btn compact"
                disabled={batchBusy || pickedList.length === 0}
                onClick={() => runBatchAction('close', pickedList)}
              >
                批量关闭
              </button>
              {closedTerminalIds.length > 0 && (
                <button
                  type="button"
                  class="ghost-btn compact"
                  disabled={batchBusy}
                  onClick={() => runBatchAction('close', closedTerminalIds)}
                >
                  清理已结束
                </button>
              )}
            </div>
            {batchNote && <div class="batch-note">{batchNote}</div>}
          </div>
        )}

        <input
          type="search"
          class="search-input"
          placeholder="搜索对话、终端和项目…"
          value={query}
          onInput={(e) => setQuery((e.target as HTMLInputElement).value)}
        />

        <div class="filter-chips">
          {filterTools.map((t) => {
            if (t === 'all') {
              return (
                <button
                  key={t}
                  type="button"
                  class={`chip${toolFilter === t ? ' active' : ''}`}
                  onClick={() => setToolFilter(t)}
                >
                  全部
                </button>
              );
            }
            const m = toolMeta(t);
            return (
              <button
                key={t}
                type="button"
                class={`chip has-mono${toolFilter === t ? ' active' : ''}`}
                onClick={() => setToolFilter(t)}
              >
                <span class="chip-mono" style={toolBadgeStyle(t)}>
                  {m.mono}
                </span>
                <span>{m.short}</span>
              </button>
            );
          })}
        </div>
      </div>

      <div class="sidebar-scroll">
        {(stateFilter === 'all' || stateFilter === 'confirm') && confirmItems.length > 0 && (
          <section class="nav-section">
            <div class="group-title danger">等待确认 · {confirmItems.length}</div>
            <div class="card-group">{confirmItems.map((r) => renderRow(r, 'confirm'))}</div>
          </section>
        )}

        {(stateFilter === 'all' || stateFilter === 'busy') && activeItems.length > 0 && (
          <section class="nav-section">
            <div class="group-title busy">正在执行 · {activeItems.length}</div>
            <div class="card-group">{activeItems.map((r) => renderRow(r, 'busy'))}</div>
          </section>
        )}

        {(stateFilter === 'all' || stateFilter === 'open') && openItems.length > 0 && (
          <section class="nav-section">
            <div class="group-title">已打开 · {openItems.length}</div>
            <div class="card-group">{openItems.map((r) => renderRow(r))}</div>
          </section>
        )}

        {stateFilter !== 'all' && (
          <button type="button" class="ghost-btn compact wide" onClick={() => setStateFilter('all')}>
            显示全部项目与对话
          </button>
        )}

        {stateFilter === 'all' && (
        <section class="nav-section">
          <div class="group-head-row">
            <div class="group-title">项目工作区 · {projectGroups.length}</div>
            <button type="button" class="mini-add" title="添加项目文件夹" onClick={onAddProject}>
              +
            </button>
          </div>
          {projectGroups.length === 0 ? (
            <button type="button" class="ghost-btn wide" onClick={onAddProject}>
              + 添加第一个项目文件夹
            </button>
          ) : (
            <div class="card-group">
              {projectGroups.map(([dir, items]) => {
                const collapsed = !!collapsedDirs[dir];
                return (
                  <div key={dir} class="project-block">
                    <div class="project-head">
                      <button
                        type="button"
                        class="project-toggle"
                        onClick={() => setCollapsedDirs((prev) => ({ ...prev, [dir]: !prev[dir] }))}
                      >
                        <span class="caret" style={{ color: 'var(--muted)' }}>{collapsed ? '▸' : '▾'}</span>
                        <span class="project-name">{dir}</span>
                        <span class="project-count">{items.length}</span>
                      </button>
                      <div class="project-actions">
                        <button
                          type="button"
                          class="mini-add"
                          title={`预览 ${dir} 项目文件`}
                          onClick={() => onOpenProjectFiles(dir)}
                        >
                          <svg viewBox="0 0 20 20" width="14" height="14" aria-hidden="true">
                            <path d="M2.5 5.5A1.5 1.5 0 0 1 4 4h3.5l1.6 1.8H16A1.5 1.5 0 0 1 17.5 7.3v7.2A1.5 1.5 0 0 1 16 16H4a1.5 1.5 0 0 1-1.5-1.5z" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round" />
                          </svg>
                        </button>
                        <button
                          type="button"
                          class="mini-add"
                          title={`在 ${dir} 中新建 AI 对话`}
                          onClick={() => onNewTask(dir)}
                        >
                          +
                        </button>
                      </div>
                    </div>
                    {!collapsed && items.length > 0 && (
                      <div class="project-items">{items.map((r) => renderRow(r))}</div>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </section>
        )}

        {stateFilter === 'all' && recentSessions.length > 0 && (
          <section class="nav-section">
            <div class="group-title">最近对话 · {recentSessions.length}</div>
            <div class="card-group">{recentSessions.slice(0, recentLimit).map((r) => renderRow(r))}</div>
            {recentSessions.length > recentLimit && (
              <button
                type="button"
                class="ghost-btn compact wide load-more-btn"
                onClick={() => setRecentLimit((n) => n + 24)}
              >
                加载更多历史（还有 {recentSessions.length - recentLimit} 条）
              </button>
            )}
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
        <div class="foot-seg">
          <button type="button" class="foot-btn" onClick={() => onOpenRightTab('teams')}>
            协作{teamCount > 0 ? ` (${teamCount})` : ''}
          </button>
          <button type="button" class="foot-btn" onClick={() => onOpenRightTab('connect')}>
            外观与连接
          </button>
          <button type="button" class="foot-btn" style={{ flex: '0 0 auto', padding: '6px 10px' }} title="专注模式 (Ctrl+\)" onClick={onToggleFocus}>
            专注
          </button>
        </div>
      </div>
    </aside>
  );
}
