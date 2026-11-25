import { useEffect, useMemo, useState } from 'preact/hooks';
import { postDesktopMessage } from '../runtime/api';
import {
  archiveSupervisionItem,
  endSupervisionItem,
  keyForSelected,
  stopSupervisionItem,
  type SupervisionItem,
  type SupervisionModel,
} from '../runtime/supervision';
import { formatExactTime, formatRelativeTime } from '../runtime/time';
import {
  toolBadgeStyle,
  toolColor,
  toolMeta,
  toolShort,
  type DeviceInfo,
  type SelectedItem,
} from '../runtime/types';

interface Props {
  device: DeviceInfo | null;
  model: SupervisionModel;
  selected: SelectedItem | null;
  splitTarget: SelectedItem | null;
  onSelect: (item: SelectedItem) => void;
  onSplit: (item: SelectedItem) => void;
  onOpenMission: () => void;
  onNewTask: (defaultDir?: string) => void;
  onOpenProjectFiles: (dir: string) => void;
  onAddProject: () => void;
  onOpenPalette: () => void;
  onOpenRightTab: (tab: 'files' | 'diff' | 'teams' | 'connect') => void;
  onToggleFocus: () => void;
  onMarkSeen: (item: SupervisionItem) => void;
  onMarkAllDoneSeen: () => void;
  onTogglePinProject: (name: string) => void;
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
  model,
  selected,
  splitTarget,
  onSelect,
  onSplit,
  onOpenMission,
  onNewTask,
  onOpenProjectFiles,
  onAddProject,
  onOpenPalette,
  onOpenRightTab,
  onToggleFocus,
  onMarkSeen,
  onMarkAllDoneSeen,
  onTogglePinProject,
  onRefresh,
  connected,
  latencyMs,
}: Props) {
  const [viewMode, setViewMode] = useState<'duty' | 'projects'>('duty');
  const [query, setQuery] = useState('');
  const [toolFilter, setToolFilter] = useState<string>('all');
  const [stateFilter, setStateFilter] = useState<'all' | 'confirm' | 'busy' | 'done' | 'open'>('all');
  const [collapsedDirs, setCollapsedDirs] = useState<Record<string, boolean>>({});
  const [recentLimit, setRecentLimit] = useState(16);
  const [batchMode, setBatchMode] = useState(false);
  const [pickedKeys, setPickedKeys] = useState<Record<string, boolean>>({});
  const [batchBusy, setBatchBusy] = useState(false);
  const [batchNote, setBatchNote] = useState('');
  const [nowMs, setNowMs] = useState(() => Date.now());

  useEffect(() => {
    const timer = window.setInterval(() => setNowMs(Date.now()), 30000);
    return () => window.clearInterval(timer);
  }, []);

  const q = query.trim().toLowerCase();
  const matches = (entry: SupervisionItem) => {
    if (toolFilter !== 'all' && entry.tool !== toolFilter) return false;
    if (!q) return true;
    return (
      entry.title.toLowerCase().includes(q) ||
      entry.project.toLowerCase().includes(q) ||
      entry.folder.toLowerCase().includes(q) ||
      entry.tool.toLowerCase().includes(q) ||
      entry.preview.toLowerCase().includes(q)
    );
  };

  const filtered = useMemo(() => {
    return {
      confirm: model.confirm.filter(matches),
      failed: model.failed.filter(matches),
      done: model.done.filter(matches),
      busy: model.busy.filter(matches),
      open: model.open.filter(matches),
      history: model.history.filter(matches),
      projects: model.projects
        .map((p) => ({ ...p, items: p.items.filter(matches) }))
        .filter((p) => !q || p.name.toLowerCase().includes(q) || p.items.length > 0),
    };
  }, [model, q, toolFilter]);

  const filterTools = useMemo(() => {
    const detected = device?.tools || ['claude', 'codex', 'agy', 'shell'];
    return Array.from(new Set(['all', ...detected])).slice(0, 8);
  }, [device?.tools]);

  const pickedItems = useMemo(
    () => model.all.filter((entry) => pickedKeys[entry.key]),
    [model.all, pickedKeys],
  );

  const selectedKey = keyForSelected(selected);
  const splitKey = keyForSelected(splitTarget);
  const reviewCount = model.done.length + model.failed.length;

  const runBatchAction = async (kind: 'stop' | 'end' | 'seen' | 'archive') => {
    if (pickedItems.length === 0 || batchBusy) return;
    if (kind === 'seen') {
      for (const item of pickedItems) onMarkSeen(item);
      setBatchNote(`已标记 ${pickedItems.length} 个对话为已读`);
      setPickedKeys({});
      return;
    }
    setBatchBusy(true);
    setBatchNote('');
    let ok = 0;
    try {
      const targets = pickedItems.filter((item) => {
        if (kind === 'stop') return item.canStop;
        if (kind === 'end') return item.canEnd;
        return item.canArchive;
      });
      await Promise.all(
        targets.map((item) =>
          (kind === 'stop'
            ? stopSupervisionItem(item)
            : kind === 'end'
              ? endSupervisionItem(item)
              : archiveSupervisionItem(item)
          )
            .then(() => {
              ok++;
            })
            .catch(() => {}),
        ),
      );
      setBatchNote(
        kind === 'stop'
          ? `已发送停止至 ${ok} 个会话`
          : kind === 'end'
            ? `已结束 ${ok} 个会话`
            : `已归档隐藏 ${ok} 段历史`,
      );
      setPickedKeys({});
      onRefresh();
    } finally {
      setBatchBusy(false);
    }
  };

  const renderRow = (entry: SupervisionItem) => {
    const meta = toolMeta(entry.tool);
    const ago = formatRelativeTime(entry.at, nowMs);
    const exact = formatExactTime(entry.at);
    const checked = !!pickedKeys[entry.key];
    const isActive = !batchMode && selectedKey === entry.key;
    const isSplit = !batchMode && splitKey === entry.key;
    const pillLabel =
      entry.status === 'confirm'
        ? '确认'
        : entry.status === 'failed'
          ? '异常'
          : entry.status === 'done'
            ? '待验'
            : entry.status === 'busy'
              ? '运行'
              : '';

    return (
      <div key={entry.key} class={`nav-row-wrap${isActive ? ' active' : ''}${isSplit ? ' is-split' : ''}`}>
        <button
          type="button"
          class={`nav-row${isActive ? ' active' : ''}${checked ? ' is-picked' : ''}`}
          onClick={() => {
            if (batchMode) {
              setPickedKeys((prev) => ({ ...prev, [entry.key]: !prev[entry.key] }));
              return;
            }
            onSelect(entry.item);
          }}
        >
          {batchMode && (
            <span class={`batch-check${checked ? ' checked' : ''}`} aria-hidden="true">
              {checked ? '✓' : ''}
            </span>
          )}
          <span class="tool-squircle" style={toolBadgeStyle(entry.tool)} title={meta.label}>
            {meta.mono}
          </span>
          <span class="nav-row-main">
            <span class="nav-row-top">
              <span class="nav-row-title">{entry.title}</span>
              {ago && (
                <span class="nav-row-time" title={exact ? `上次工作：${exact}` : undefined}>
                  {ago}
                </span>
              )}
            </span>
            <span class="nav-row-sub">
              <span>{entry.project || entry.origin}</span>
              {entry.branch ? <span class="branch-chip">{entry.branch}</span> : null}
              {isSplit ? <span class="branch-chip">分屏中</span> : null}
            </span>
          </span>
          {pillLabel ? <span class={`state-pill pill-${entry.status}`}>{pillLabel}</span> : null}
        </button>
        {!batchMode && (
          <button
            type="button"
            class="row-split-btn"
            title="在右侧分屏打开对比监工"
            onClick={(e) => {
              e.stopPropagation();
              onSplit(entry.item);
            }}
          >
            ⊞
          </button>
        )}
      </div>
    );
  };

  const primaryQuotas = (device?.quotas || []).filter((q) => q.windows && q.windows.length > 0).slice(0, 3);
  const teamCount = (device?.team_cards || []).filter((c) => c.state === 'running' || c.confirm).length;
  const isDesktopWebView = typeof (window as any).chrome?.webview?.postMessage === 'function';
  const allProjectsCollapsed =
    filtered.projects.length > 0 && filtered.projects.every((p) => collapsedDirs[p.name]);

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
            <button
              type="button"
              class={`circle-btn${selected === null ? ' solid' : ''}`}
              title="多对话监控总览墙 (Ctrl+0)"
              onClick={onOpenMission}
            >
              <svg viewBox="0 0 24 24" width="15" height="15" aria-hidden="true">
                <rect x="3.5" y="3.5" width="7" height="7" rx="1.6" fill="none" stroke="currentColor" stroke-width="1.9" />
                <rect x="13.5" y="3.5" width="7" height="7" rx="1.6" fill="none" stroke="currentColor" stroke-width="1.9" />
                <rect x="3.5" y="13.5" width="7" height="7" rx="1.6" fill="none" stroke="currentColor" stroke-width="1.9" />
                <rect x="13.5" y="13.5" width="7" height="7" rx="1.6" fill="none" stroke="currentColor" stroke-width="1.9" />
              </svg>
            </button>
            {isDesktopWebView ? (
              <button
                type="button"
                class="circle-btn"
                title="手机扫码连接与设置"
                onClick={() => postDesktopMessage('open-classic')}
              >
                <svg viewBox="0 0 24 24" width="15" height="15" aria-hidden="true">
                  <path d="M4 4h6v6H4zm10 0h6v6h-6zM4 14h6v6H4zm10 2h2v2h-2zm4-2h2v6h-4v-2h2z" fill="currentColor" />
                </svg>
              </button>
            ) : null}
            <button
              type="button"
              class={`circle-btn${batchMode ? ' solid' : ''}`}
              title={batchMode ? '退出批量管理' : '批量管理会话（停止/结束/已读/归档）'}
              onClick={() => {
                setBatchMode((v) => !v);
                setPickedKeys({});
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
            <button type="button" class="circle-btn solid" title="新建 AI 任务 (Ctrl+N)" onClick={() => onNewTask()}>
              <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
                <path d="M12 5v14M5 12h14" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" />
              </svg>
            </button>
          </div>
        </div>

        {/* 4-column Supervision KPI Card */}
        <div class="kpi-card kpi-4">
          <button
            type="button"
            class={`kpi-cell${model.confirm.length > 0 ? ' is-confirm' : ''}${stateFilter === 'confirm' ? ' is-selected' : ''}`}
            title="点击仅看等待确认"
            onClick={() => {
              setViewMode('duty');
              setStateFilter((v) => (v === 'confirm' ? 'all' : 'confirm'));
            }}
          >
            <span>待确认</span>
            <strong>{model.confirm.length}</strong>
          </button>
          <button
            type="button"
            class={`kpi-cell${model.busy.length > 0 ? ' is-busy' : ''}${stateFilter === 'busy' ? ' is-selected' : ''}`}
            title="点击仅看正在执行"
            onClick={() => {
              setViewMode('duty');
              setStateFilter((v) => (v === 'busy' ? 'all' : 'busy'));
            }}
          >
            <span>执行中</span>
            <strong>{model.busy.length}</strong>
          </button>
          <button
            type="button"
            class={`kpi-cell${reviewCount > 0 ? ' is-done' : ''}${stateFilter === 'done' ? ' is-selected' : ''}`}
            title="刚完成未读或异常退出"
            onClick={() => {
              setViewMode('duty');
              setStateFilter((v) => (v === 'done' ? 'all' : 'done'));
            }}
          >
            <span>待验收</span>
            <strong>{reviewCount}</strong>
          </button>
          <button
            type="button"
            class={`kpi-cell${model.open.length > 0 ? ' is-open' : ''}${stateFilter === 'open' ? ' is-selected' : ''}`}
            title="已打开空闲会话"
            onClick={() => {
              setViewMode('duty');
              setStateFilter((v) => (v === 'open' ? 'all' : 'open'));
            }}
          >
            <span>已打开</span>
            <strong>{model.open.length}</strong>
          </button>
        </div>

        {/* View Mode Switcher: Duty Queue (zero-dup) vs Project Tree */}
        <div class="sidebar-mode-seg" role="tablist" aria-label="侧栏视图切换">
          <button
            type="button"
            role="tab"
            aria-selected={viewMode === 'duty'}
            class={viewMode === 'duty' ? 'active' : ''}
            onClick={() => setViewMode('duty')}
          >
            值班队列 {model.active.length > 0 ? `(${model.active.length})` : ''}
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={viewMode === 'projects'}
            class={viewMode === 'projects' ? 'active' : ''}
            onClick={() => {
              setViewMode('projects');
              setStateFilter('all');
            }}
          >
            按项目 ({model.projects.length})
          </button>
        </div>

        {batchMode && (
          <div class="batch-bar">
            <div class="batch-bar-top">
              <strong>批量管理 · 已选 {pickedItems.length}</strong>
              <div class="batch-picks">
                <button
                  type="button"
                  class="crumb-btn"
                  onClick={() => {
                    const next: Record<string, boolean> = {};
                    for (const item of model.busy) next[item.key] = true;
                    setPickedKeys(next);
                  }}
                >
                  运行中({model.busy.length})
                </button>
                {reviewCount > 0 && (
                  <button
                    type="button"
                    class="crumb-btn"
                    onClick={() => {
                      const next: Record<string, boolean> = {};
                      for (const item of [...model.done, ...model.failed]) next[item.key] = true;
                      setPickedKeys(next);
                    }}
                  >
                    待验收({reviewCount})
                  </button>
                )}
                {model.history.length > 0 && (
                  <button
                    type="button"
                    class="crumb-btn"
                    onClick={() => {
                      const next: Record<string, boolean> = {};
                      for (const item of model.history.slice(0, 20)) if (item.canArchive) next[item.key] = true;
                      setPickedKeys(next);
                    }}
                  >
                    历史
                  </button>
                )}
              </div>
            </div>
            <div class="batch-bar-actions">
              <button
                type="button"
                class="ghost-btn compact"
                disabled={batchBusy || !pickedItems.some((x) => x.canStop)}
                onClick={() => runBatchAction('stop')}
              >
                停止
              </button>
              <button
                type="button"
                class="danger-btn compact"
                disabled={batchBusy || !pickedItems.some((x) => x.canEnd)}
                onClick={() => runBatchAction('end')}
              >
                结束
              </button>
              <button
                type="button"
                class="ghost-btn compact"
                disabled={batchBusy || pickedItems.length === 0}
                onClick={() => runBatchAction('seen')}
              >
                标已读
              </button>
              <button
                type="button"
                class="ghost-btn compact"
                disabled={batchBusy || !pickedItems.some((x) => x.canArchive)}
                onClick={() => runBatchAction('archive')}
              >
                隐藏归档
              </button>
            </div>
            {batchNote && <div class="batch-note">{batchNote}</div>}
          </div>
        )}

        <input
          type="search"
          class="search-input"
          placeholder="搜索标题、项目、摘要或工具…"
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
        {viewMode === 'duty' ? (
          <>
            {(stateFilter === 'all' || stateFilter === 'confirm') && filtered.confirm.length > 0 && (
              <section class="nav-section">
                <div class="group-title danger">等待确认 · {filtered.confirm.length}</div>
                <div class="card-group">{filtered.confirm.map(renderRow)}</div>
              </section>
            )}

            {(stateFilter === 'all' || stateFilter === 'done') && filtered.failed.length > 0 && (
              <section class="nav-section">
                <div class="group-title danger">异常中断 · {filtered.failed.length}</div>
                <div class="card-group">{filtered.failed.map(renderRow)}</div>
              </section>
            )}

            {(stateFilter === 'all' || stateFilter === 'done') && filtered.done.length > 0 && (
              <section class="nav-section">
                <div class="group-head-row">
                  <div class="group-title done">刚完成待验收 · {filtered.done.length}</div>
                  <button type="button" class="crumb-btn" onClick={onMarkAllDoneSeen}>
                    全部已读
                  </button>
                </div>
                <div class="card-group">{filtered.done.map(renderRow)}</div>
              </section>
            )}

            {(stateFilter === 'all' || stateFilter === 'busy') && filtered.busy.length > 0 && (
              <section class="nav-section">
                <div class="group-title busy">正在执行 · {filtered.busy.length}</div>
                <div class="card-group">{filtered.busy.map(renderRow)}</div>
              </section>
            )}

            {(stateFilter === 'all' || stateFilter === 'open') && filtered.open.length > 0 && (
              <section class="nav-section">
                <div class="group-title">已打开 · {filtered.open.length}</div>
                <div class="card-group">{filtered.open.map(renderRow)}</div>
              </section>
            )}

            {stateFilter !== 'all' && (
              <button type="button" class="ghost-btn compact wide" onClick={() => setStateFilter('all')}>
                显示全部状态
              </button>
            )}

            {stateFilter === 'all' && filtered.history.length > 0 && (
              <section class="nav-section">
                <div class="group-title">最近历史 · {filtered.history.length}</div>
                <div class="card-group">{filtered.history.slice(0, recentLimit).map(renderRow)}</div>
                {filtered.history.length > recentLimit && (
                  <button
                    type="button"
                    class="ghost-btn compact wide load-more-btn"
                    onClick={() => setRecentLimit((n) => n + 24)}
                  >
                    加载更多历史（还有 {filtered.history.length - recentLimit} 条）
                  </button>
                )}
              </section>
            )}
          </>
        ) : (
          <section class="nav-section">
            <div class="group-head-row">
              <div class="group-title">项目工作区 · {filtered.projects.length}</div>
              <div style={{ display: 'inline-flex', gap: '4px', alignItems: 'center' }}>
                {filtered.projects.length > 1 && (
                  <button
                    type="button"
                    class="crumb-btn"
                    onClick={() => {
                      const next: Record<string, boolean> = {};
                      if (!allProjectsCollapsed) {
                        for (const p of filtered.projects) next[p.name] = true;
                      }
                      setCollapsedDirs(next);
                    }}
                  >
                    {allProjectsCollapsed ? '全部展开' : '全部折叠'}
                  </button>
                )}
                <button type="button" class="mini-add" title="添加项目文件夹" onClick={onAddProject}>
                  +
                </button>
              </div>
            </div>
            {filtered.projects.length === 0 ? (
              <button type="button" class="ghost-btn wide" onClick={onAddProject}>
                + 添加第一个项目文件夹
              </button>
            ) : (
              <div class="card-group">
                {filtered.projects.map((p) => {
                  const collapsed = !!collapsedDirs[p.name];
                  return (
                    <div key={p.name} class="project-block">
                      <div class="project-head">
                        <button
                          type="button"
                          class="project-toggle"
                          onClick={() => setCollapsedDirs((prev) => ({ ...prev, [p.name]: !prev[p.name] }))}
                        >
                          <span class="caret" style={{ color: 'var(--muted)' }}>{collapsed ? '▸' : '▾'}</span>
                          <span class="project-name">{p.name}</span>
                          <span class="project-health-dots">
                            {p.confirm + p.failed > 0 && (
                              <span class="mini-dot is-confirm" title={`${p.confirm + p.failed} 项需处理`}>
                                {p.confirm + p.failed}
                              </span>
                            )}
                            {p.busy > 0 && (
                              <span class="mini-dot is-busy" title={`${p.busy} 项执行中`}>
                                {p.busy}
                              </span>
                            )}
                            {p.done > 0 && (
                              <span class="mini-dot is-done" title={`${p.done} 项待验收`}>
                                {p.done}
                              </span>
                            )}
                            {p.confirm + p.failed + p.busy + p.done === 0 && (
                              <span class="project-count">{p.items.length}</span>
                            )}
                          </span>
                        </button>
                        <div class="project-actions">
                          <button
                            type="button"
                            class={`mini-add${p.pinned ? ' pinned' : ''}`}
                            title={p.pinned ? '取消置顶项目' : '置顶项目'}
                            onClick={() => onTogglePinProject(p.name)}
                          >
                            {p.pinned ? '★' : '☆'}
                          </button>
                          <button
                            type="button"
                            class="mini-add"
                            title={`预览 ${p.name} 项目文件`}
                            onClick={() => onOpenProjectFiles(p.name)}
                          >
                            <svg viewBox="0 0 20 20" width="13" height="13" aria-hidden="true">
                              <path d="M2.5 5.5A1.5 1.5 0 0 1 4 4h3.5l1.6 1.8H16A1.5 1.5 0 0 1 17.5 7.3v7.2A1.5 1.5 0 0 1 16 16H4a1.5 1.5 0 0 1-1.5-1.5z" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round" />
                            </svg>
                          </button>
                          <button
                            type="button"
                            class="mini-add"
                            title={`在 ${p.name} 中新建 AI 对话`}
                            onClick={() => onNewTask(p.name)}
                          >
                            +
                          </button>
                        </div>
                      </div>
                      {!collapsed && p.items.length > 0 && (
                        <div class="project-items">{p.items.slice(0, 10).map(renderRow)}</div>
                      )}
                    </div>
                  );
                })}
              </div>
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
