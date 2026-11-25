import { useEffect, useMemo, useState } from 'preact/hooks';
import { postDesktopMessage } from '../runtime/api';
import {
  approveSupervisionItem,
  endSupervisionItem,
  quickSaySupervisionItem,
  stopSupervisionItem,
  type ProjectHealth,
  type SupervisionItem,
  type SupervisionModel,
} from '../runtime/supervision';
import { formatExactTime, formatRelativeTime } from '../runtime/time';
import { toolBadgeStyle, toolMeta, toolShort, type DeviceInfo, type SelectedItem } from '../runtime/types';

interface Props {
  device: DeviceInfo | null;
  model: SupervisionModel;
  onSelect: (item: SelectedItem) => void;
  onSplit: (item: SelectedItem) => void;
  onNewTask: (dir?: string) => void;
  onOpenFiles: (dir: string) => void;
  onAddProject: () => void;
  onToggleTerminal: () => void;
  onMarkSeen: (item: SupervisionItem) => void;
  onMarkAllDoneSeen: () => void;
  onTogglePinProject: (name: string) => void;
  onRefresh: () => void;
}

type GridFilter = 'all' | 'attention' | 'busy' | 'open';

const STATUS_LABELS: Record<SupervisionItem['status'], string> = {
  confirm: '待确认',
  failed: '异常退出',
  done: '待验收',
  busy: '执行中',
  open: '已打开',
  history: '历史',
};

export function MissionGrid({
  device,
  model,
  onSelect,
  onSplit,
  onNewTask,
  onOpenFiles,
  onAddProject,
  onToggleTerminal,
  onMarkSeen,
  onMarkAllDoneSeen,
  onTogglePinProject,
  onRefresh,
}: Props) {
  const [filter, setFilter] = useState<GridFilter>('all');
  const [projectFilter, setProjectFilter] = useState<string>('');
  const [busyKey, setBusyKey] = useState<string>('');
  const [quickSayKey, setQuickSayKey] = useState<string>('');
  const [quickDraft, setQuickDraft] = useState<string>('');
  const [feedback, setFeedback] = useState<string>('');
  const [nowMs, setNowMs] = useState(() => Date.now());

  useEffect(() => {
    const timer = window.setInterval(() => setNowMs(Date.now()), 30000);
    return () => window.clearInterval(timer);
  }, []);

  const visibleItems = useMemo(() => {
    let base: SupervisionItem[];
    if (filter === 'attention') base = [...model.confirm, ...model.failed, ...model.done];
    else if (filter === 'busy') base = model.busy;
    else if (filter === 'open') base = model.open;
    else base = model.active;

    if (projectFilter) {
      base = base.filter((item) => item.project === projectFilter);
    }
    return base;
  }, [model, filter, projectFilter]);

  const firstConfirm = model.confirm[0] || null;
  const isDesktop = typeof (window as any).chrome?.webview?.postMessage === 'function';

  const runAction = async (key: string, fn: () => Promise<void>, okText: string) => {
    setBusyKey(key);
    setFeedback('');
    try {
      await fn();
      setFeedback(okText);
      onRefresh();
    } catch (err: any) {
      setFeedback(err?.message || '操作失败');
    } finally {
      setBusyKey('');
    }
  };

  return (
    <main class="main-pane mission-pane">
      <header class="mission-head">
        <div class="mission-head-left">
          <h1>多对话监管台</h1>
          <span class="mission-sub">
            {model.active.length > 0
              ? `${model.active.length} 个活跃对话 · ${model.projects.length} 个项目`
              : `${model.projects.length} 个项目 · 当前没有运行中的对话`}
          </span>
        </div>
        <div class="mission-head-actions">
          {model.done.length + model.failed.length > 0 && (
            <button type="button" class="ghost-btn compact" onClick={onMarkAllDoneSeen}>
              全部标为已读 ({model.done.length + model.failed.length})
            </button>
          )}
          <button type="button" class="ghost-btn compact" onClick={onAddProject}>
            + 添加项目
          </button>
          <button type="button" class="primary-btn compact" onClick={() => onNewTask(projectFilter || undefined)}>
            + 新建 AI
          </button>
        </div>
      </header>

      <div class="mission-scroll">
        {/* Speed Approval Triage Banner */}
        {firstConfirm && (
          <section class="triage-banner" aria-label="快捷审批通道">
            <div class="triage-head">
              <span class="state-pill pill-confirm">待确认 {model.confirm.length}</span>
              <span class="tool-squircle sm" style={toolBadgeStyle(firstConfirm.tool)}>
                {toolMeta(firstConfirm.tool).mono}
              </span>
              <strong>{firstConfirm.title}</strong>
              {firstConfirm.project && <span class="meta-chip">{firstConfirm.project}</span>}
              <span class="triage-spacer" />
              <span class="triage-time">{formatRelativeTime(firstConfirm.at, nowMs)}</span>
            </div>
            {firstConfirm.preview && <div class="triage-body">{firstConfirm.preview}</div>}
            <div class="triage-actions">
              {firstConfirm.canApprove && (
                <>
                  <button
                    type="button"
                    class="primary-btn compact"
                    disabled={busyKey === firstConfirm.key}
                    onClick={() =>
                      runAction(firstConfirm.key, () => approveSupervisionItem(firstConfirm, true), '已允许本次操作')
                    }
                  >
                    允许一次
                  </button>
                  <button
                    type="button"
                    class="danger-btn compact"
                    disabled={busyKey === firstConfirm.key}
                    onClick={() =>
                      runAction(firstConfirm.key, () => approveSupervisionItem(firstConfirm, false), '已拒绝本次操作')
                    }
                  >
                    拒绝
                  </button>
                </>
              )}
              <button type="button" class="ghost-btn compact" onClick={() => onSelect(firstConfirm.item)}>
                进入对话查看
              </button>
            </div>
          </section>
        )}

        {feedback && <div class="mission-feedback">{feedback}</div>}

        {/* Filter Bar */}
        <div class="mission-filters">
          <div class="mission-seg">
            <button
              type="button"
              class={`chip${filter === 'all' ? ' active' : ''}`}
              onClick={() => setFilter('all')}
            >
              全部活跃 ({model.active.length})
            </button>
            <button
              type="button"
              class={`chip${filter === 'attention' ? ' active' : ''}`}
              onClick={() => setFilter('attention')}
            >
              需处理 ({model.attentionCount})
            </button>
            <button
              type="button"
              class={`chip${filter === 'busy' ? ' active' : ''}`}
              onClick={() => setFilter('busy')}
            >
              执行中 ({model.busy.length})
            </button>
            <button
              type="button"
              class={`chip${filter === 'open' ? ' active' : ''}`}
              onClick={() => setFilter('open')}
            >
              空闲/已打开 ({model.open.length})
            </button>
          </div>

          {projectFilter && (
            <button
              type="button"
              class="chip active"
              title="点击清除项目筛选"
              onClick={() => setProjectFilter('')}
            >
              项目：{projectFilter} ✕
            </button>
          )}
        </div>

        {/* Active Conversation Cards Grid */}
        {visibleItems.length > 0 ? (
          <section class="mission-grid" aria-label="活跃对话监控墙">
            {visibleItems.map((entry) => {
              const meta = toolMeta(entry.tool);
              const ago = formatRelativeTime(entry.at, nowMs);
              const exact = formatExactTime(entry.at);
              const isBusyAction = busyKey === entry.key;
              const sayOpen = quickSayKey === entry.key;
              return (
                <article
                  key={entry.key}
                  class={`mission-card is-${entry.status}`}
                  onClick={() => onSelect(entry.item)}
                >
                  <div class="mission-card-top">
                    <span class="tool-squircle sm" style={toolBadgeStyle(entry.tool)} title={meta.label}>
                      {meta.mono}
                    </span>
                    <div class="mission-card-meta">
                      <div class="mission-card-title">{entry.title}</div>
                      <div class="mission-card-sub">
                        <span>{entry.project || entry.origin}</span>
                        {entry.branch && <span class="branch-chip">{entry.branch}</span>}
                        {ago && <span title={exact || undefined}>{ago}</span>}
                      </div>
                    </div>
                    <span class={`state-pill pill-${entry.status}`}>{STATUS_LABELS[entry.status]}</span>
                  </div>

                  <div class="mission-card-preview">
                    {entry.preview || (entry.status === 'busy' ? '正在执行当前步骤…' : '点击进入查看完整上下文')}
                  </div>

                  {sayOpen && (
                    <form
                      class="mission-quicksay"
                      onClick={(e) => e.stopPropagation()}
                      onSubmit={(e) => {
                        e.preventDefault();
                        const text = quickDraft.trim();
                        if (!text) return;
                        runAction(
                          entry.key,
                          async () => {
                            await quickSaySupervisionItem(entry, text);
                            setQuickDraft('');
                            setQuickSayKey('');
                          },
                          `已发送到「${entry.title}」`,
                        );
                      }}
                    >
                      <input
                        type="text"
                        placeholder="输入追问内容并回车…"
                        value={quickDraft}
                        onInput={(e) => setQuickDraft((e.target as HTMLInputElement).value)}
                      />
                      <button type="submit" class="primary-btn compact" disabled={isBusyAction || !quickDraft.trim()}>
                        发送
                      </button>
                    </form>
                  )}

                  <div class="mission-card-actions" onClick={(e) => e.stopPropagation()}>
                    {entry.canApprove && (
                      <>
                        <button
                          type="button"
                          class="primary-btn compact"
                          disabled={isBusyAction}
                          onClick={() =>
                            runAction(entry.key, () => approveSupervisionItem(entry, true), '已允许')
                          }
                        >
                          允许
                        </button>
                        <button
                          type="button"
                          class="danger-btn compact"
                          disabled={isBusyAction}
                          onClick={() =>
                            runAction(entry.key, () => approveSupervisionItem(entry, false), '已拒绝')
                          }
                        >
                          拒绝
                        </button>
                      </>
                    )}
                    {!entry.canApprove && entry.canQuickSay && (
                      <button
                        type="button"
                        class="ghost-btn compact"
                        onClick={() => {
                          setQuickSayKey(sayOpen ? '' : entry.key);
                          setQuickDraft('');
                        }}
                      >
                        {sayOpen ? '收起' : '追问'}
                      </button>
                    )}
                    {(entry.status === 'done' || entry.status === 'failed') && (
                      <button
                        type="button"
                        class="ghost-btn compact"
                        onClick={() => onMarkSeen(entry)}
                      >
                        已读
                      </button>
                    )}
                    {entry.canStop && (
                      <button
                        type="button"
                        class="danger-btn compact"
                        disabled={isBusyAction}
                        onClick={() => runAction(entry.key, () => stopSupervisionItem(entry), '已发送停止')}
                      >
                        停止
                      </button>
                    )}
                    {entry.canEnd && !entry.canStop && (
                      <button
                        type="button"
                        class="ghost-btn compact"
                        disabled={isBusyAction}
                        onClick={() => runAction(entry.key, () => endSupervisionItem(entry), '已结束会话')}
                      >
                        结束
                      </button>
                    )}
                    <button
                      type="button"
                      class="ghost-btn compact"
                      title="固定到右侧分屏对比监工"
                      onClick={() => onSplit(entry.item)}
                    >
                      分屏
                    </button>
                    <button type="button" class="ghost-btn compact" onClick={() => onSelect(entry.item)}>
                      进入
                    </button>
                  </div>
                </article>
              );
            })}
          </section>
        ) : (
          <div class="mission-empty">
            <h3>{projectFilter ? `「${projectFilter}」下暂无符合筛选的活跃对话` : '所有对话均已安静'}</h3>
            <p>
              {model.history.length > 0
                ? `侧栏中有 ${model.history.length} 段历史记录可随时恢复，也可以直接在任意项目下新建并行 AI 任务。`
                : '从任意项目目录发起 AI 任务，或在电脑端打开 Claude / Codex / Antigravity 后即可在此统一监管。'}
            </p>
            <div class="empty-actions">
              <button type="button" class="primary-btn" onClick={() => onNewTask(projectFilter || undefined)}>
                + 新建 AI 任务
              </button>
              <button type="button" class="ghost-btn" onClick={onToggleTerminal}>
                终端抽屉 (Ctrl+J)
              </button>
              {isDesktop && (
                <button type="button" class="ghost-btn" onClick={() => postDesktopMessage('open-classic')}>
                  手机扫码连接
                </button>
              )}
            </div>
          </div>
        )}

        {/* Project Health Overview */}
        {model.projects.length > 0 && (
          <section class="project-health-section" aria-label="项目健康度总览">
            <div class="section-bar">
              <h2>项目健康度 · {model.projects.length}</h2>
              <span class="section-hint">点击项目卡片按该项目筛选活跃对话</span>
            </div>
            <div class="project-health-grid">
              {model.projects.map((p: ProjectHealth) => {
                const selectedProj = projectFilter === p.name;
                return (
                  <div
                    key={p.name}
                    class={`project-health-card${selectedProj ? ' is-selected' : ''}${p.confirm + p.failed > 0 ? ' is-urgent' : ''}`}
                    onClick={() => setProjectFilter(selectedProj ? '' : p.name)}
                  >
                    <div class="ph-top">
                      <strong class="ph-name">{p.name}</strong>
                      <div class="ph-badges">
                        {p.confirm > 0 && <span class="ph-dot is-confirm">{p.confirm} 确认</span>}
                        {p.failed > 0 && <span class="ph-dot is-confirm">{p.failed} 异常</span>}
                        {p.busy > 0 && <span class="ph-dot is-busy">{p.busy} 运行</span>}
                        {p.done > 0 && <span class="ph-dot is-done">{p.done} 待验</span>}
                        {p.confirm + p.failed + p.busy + p.done === 0 && (
                          <span class="ph-dot is-idle">{p.total} 记录</span>
                        )}
                      </div>
                    </div>
                    <div class="ph-bottom" onClick={(e) => e.stopPropagation()}>
                      <span class="ph-time">
                        {p.latestAt ? `上次工作 ${formatRelativeTime(p.latestAt, nowMs)}` : '暂无记录'}
                      </span>
                      <div class="ph-actions">
                        <button
                          type="button"
                          class={`icon-btn${p.pinned ? ' active' : ''}`}
                          title={p.pinned ? '取消置顶项目' : '置顶此项目'}
                          onClick={() => onTogglePinProject(p.name)}
                        >
                          {p.pinned ? '★' : '☆'}
                        </button>
                        <button
                          type="button"
                          class="ghost-btn compact"
                          onClick={() => onOpenFiles(p.name)}
                        >
                          文件
                        </button>
                        <button
                          type="button"
                          class="ghost-btn compact"
                          onClick={() => onNewTask(p.name)}
                        >
                          + AI
                        </button>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          </section>
        )}

        {/* Quick tool summary footer when no projects yet */}
        {device?.tools && device.tools.length > 0 && model.projects.length === 0 && (
          <div class="empty-tools">
            {device.tools
              .filter((t) => t !== 'shell')
              .map((t) => (
                <span key={t} class="tool-pill-info">
                  <span class="tool-squircle sm" style={toolBadgeStyle(t)}>
                    {toolMeta(t).mono}
                  </span>
                  {toolShort(t)}
                </span>
              ))}
          </div>
        )}
      </div>
    </main>
  );
}
