import { useCallback, useEffect, useMemo, useState } from 'preact/hooks';
import { workAction } from '../runtime/api';
import { formatExactTime, formatRelativeTime, getRecencyTier, isToday } from '../runtime/time';
import { canArchive, canEnd, canStop, runtimeLabel, type Thread, type ThreadModel } from '../runtime/threads';
import {
  toolBadgeStyle,
  toolLabel,
  toolMeta,
  type ChainEntry,
  type DeviceInfo,
  type ScheduleEntry,
  type TeamCardEntry,
} from '../runtime/types';
import { ChainDialog, ScheduleDialog, automatableThreads } from './AutomationModals';

type HomeView = 'all' | 'attention' | 'running' | 'today' | 'teams' | 'schedules' | 'chains';

interface Props {
  device: DeviceInfo | null;
  model: ThreadModel;
  picked: Record<string, boolean>;
  onOpen: (t: Thread) => void;
  onPick: (t: Thread, range: boolean) => void;
  onPickMany: (keys: string[]) => void;
  onClearPicked: () => void;
  onBatchAction: (action: 'stop' | 'read' | 'archive' | 'end', threads: Thread[]) => void;
  onMenu: (t: Thread, e: MouseEvent) => void;
  onOpenTeam: (card: TeamCardEntry) => void;
  onNew: (dir?: string) => void;
  onNewTeam: (dir?: string) => void;
  onAddProject: () => void;
  onSeenAll?: () => void;
  onToast?: (text: string, bad?: boolean) => void;
  onChanged?: () => void;
}

const NEED_STATUS: Record<string, { label: string; cls: string }> = {
  confirm: { label: '等你确认', cls: 'st-bad' },
  failed: { label: '出错了', cls: 'st-bad' },
  done: { label: '已完成', cls: 'st-good' },
};

const TEAM_MODE: Record<string, string> = { lead: '分工协作', review: '写审协作', compare: '多方比稿', discuss: '讨论' };
const TEAM_STATE: Record<string, { label: string; cls: string; pulse?: boolean }> = {
  running: { label: '进行中', cls: 'st-busy', pulse: true },
  choose: { label: '待选方案', cls: 'st-bad' },
  paused: { label: '已暂停', cls: 'st-idle' },
  done: { label: '已完成', cls: 'st-good' },
  stopped: { label: '已停止', cls: 'st-idle' },
  failed: { label: '出错', cls: 'st-bad' },
};

const DAY_NAMES: Record<number, string> = { 1: '周一', 2: '周二', 3: '周三', 4: '周四', 5: '周五', 6: '周六', 7: '周日' };

function threadStatus(t: Thread, isTodayHit: boolean): { label: string; cls: string; pulse?: boolean } {
  if (t.needs && NEED_STATUS[t.needs]) return NEED_STATUS[t.needs];
  if (t.busy) return { label: '正在执行', cls: 'st-busy', pulse: true };
  if (t.runtime !== 'idle') return { label: runtimeLabel(t) || '已打开', cls: 'st-open' };
  if (isTodayHit) return { label: '今日活跃', cls: 'st-today' };
  return { label: '已保存', cls: 'st-idle' };
}

function formatScheduleRule(s: ScheduleEntry): string {
  if (s.kind === 'every') {
    const mins = s.minutes || 0;
    if (mins >= 1440 && mins % 1440 === 0) return `每 ${mins / 1440} 天`;
    if (mins >= 60 && mins % 60 === 0) return `每 ${mins / 60} 小时`;
    return `每隔 ${mins} 分钟`;
  }
  if (s.kind === 'daily') {
    const days = s.days || [];
    let dayLabel = '每天';
    if (days.length === 5 && days.join(',') === '1,2,3,4,5') dayLabel = '工作日';
    else if (days.length === 2 && days.join(',') === '6,7') dayLabel = '周末';
    else if (days.length > 0 && days.length < 7) dayLabel = days.map((d) => DAY_NAMES[d] || String(d)).join('、');
    return `${dayLabel} ${s.time || ''}`.trim();
  }
  return s.at ? `单次 · ${formatExactTime(s.at)}` : '单次执行';
}

function formatNextDue(ms?: number, now = Date.now()): string {
  if (!ms || ms <= 0) return '未排期';
  const diff = ms - now;
  if (diff <= 0) return '即将执行';
  const mins = Math.round(diff / 60000);
  if (mins < 1) return '1 分钟内';
  if (mins < 60) return `${mins} 分钟后`;
  const hours = Math.floor(mins / 60);
  const rem = mins % 60;
  if (hours < 24) return rem > 0 ? `${hours} 小时 ${rem} 分后` : `${hours} 小时后`;
  return formatExactTime(ms);
}

/** Command-center overview: KPI filters, multi-select batch bar, conversations, collaborations, schedules and chains. */
export function Home({
  device,
  model,
  picked,
  onOpen,
  onPick,
  onPickMany,
  onClearPicked,
  onBatchAction,
  onMenu,
  onOpenTeam,
  onNew,
  onNewTeam,
  onAddProject,
  onSeenAll,
  onToast,
  onChanged,
}: Props) {
  const [view, setView] = useState<HomeView>('all');
  const [selectMode, setSelectMode] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const [schedules, setSchedules] = useState<ScheduleEntry[]>([]);
  const [chains, setChains] = useState<ChainEntry[]>([]);
  const [autoLoading, setAutoLoading] = useState(false);
  const [scheduleModal, setScheduleModal] = useState<{ open: boolean; editing: ScheduleEntry | null }>({
    open: false,
    editing: null,
  });
  const [chainModal, setChainModal] = useState<{ open: boolean; editing: ChainEntry | null }>({
    open: false,
    editing: null,
  });

  const features = device?.features || [];
  const hasWork = features.includes('work');
  const hasChains = features.includes('chains');
  const tools = (device?.tools || []).filter((t) => t !== 'shell');
  const flaggedTeamTools = tools.filter((t) => features.includes(`${t}-team`));
  const chatTools = tools.filter((t) => ['claude', 'codex', 'agy'].includes(t) && features.includes('chat'));
  const canTeam = features.includes('teams') && (flaggedTeamTools.length >= 2 || chatTools.length >= 2);
  const teamCards = device?.team_cards || [];

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 30000);
    return () => window.clearInterval(timer);
  }, []);

  const loadAutomations = useCallback(async () => {
    if (!device?.online || (!hasWork && !hasChains)) return;
    setAutoLoading(true);
    try {
      const [schedRes, chainRes] = await Promise.all([
        hasWork ? workAction<{ schedules?: ScheduleEntry[] }>('schedules').catch(() => ({ schedules: [] })) : Promise.resolve({ schedules: [] }),
        hasChains ? workAction<{ chains?: ChainEntry[] }>('chains').catch(() => ({ chains: [] })) : Promise.resolve({ chains: [] }),
      ]);
      setSchedules(Array.isArray(schedRes.schedules) ? schedRes.schedules : []);
      setChains(Array.isArray(chainRes.chains) ? chainRes.chains : []);
    } finally {
      setAutoLoading(false);
    }
  }, [device?.online, hasWork, hasChains]);

  useEffect(() => {
    loadAutomations();
  }, [loadAutomations]);

  const attention = model.attention;
  const running = useMemo(() => model.all.filter((t) => !t.needs && t.runtime !== 'idle'), [model.all]);
  const todayAll = useMemo(() => model.all.filter((t) => isToday(t.at, now)), [model.all, now]);
  const todayIdle = useMemo(() => model.all.filter((t) => !t.needs && t.runtime === 'idle' && isToday(t.at, now)), [model.all, now]);
  const earlier = useMemo(() => model.all.filter((t) => !t.needs && t.runtime === 'idle' && !isToday(t.at, now)).slice(0, 8), [model.all, now]);
  const activeSchedules = useMemo(() => schedules.filter((s) => s.on), [schedules]);
  const activeChains = useMemo(() => chains.filter((c) => c.left > 0), [chains]);
  const runningTeams = useMemo(() => teamCards.filter((t) => t.state === 'running' || t.confirm || t.state === 'choose'), [teamCards]);

  const pickedThreads = useMemo(() => model.all.filter((t) => picked[t.key]), [model.all, picked]);
  const pickingActive = selectMode || pickedThreads.length > 0;
  const stoppableCount = useMemo(() => pickedThreads.filter((t) => canStop(t).ok).length, [pickedThreads]);
  const archivableCount = useMemo(() => pickedThreads.filter((t) => canArchive(t).ok).length, [pickedThreads]);
  const endableCount = useMemo(() => pickedThreads.filter((t) => canEnd(t).ok).length, [pickedThreads]);
  const automatableCount = useMemo(() => automatableThreads(model.all).length, [model.all]);

  const visibleThreadsForBatch = useMemo(() => {
    if (view === 'attention') return attention;
    if (view === 'running') return running;
    if (view === 'today') return todayAll;
    if (view === 'all') return [...attention, ...running, ...todayIdle.slice(0, 6), ...earlier];
    return [];
  }, [view, attention, running, todayAll, todayIdle, earlier]);

  const toggleView = (next: HomeView) => setView((cur) => (cur === next ? 'all' : next));

  const exitSelectMode = () => {
    setSelectMode(false);
    onClearPicked();
  };

  const toggleScheduleOn = async (s: ScheduleEntry) => {
    const nextOn = !s.on;
    const item: Record<string, unknown> = {
      id: s.id,
      session: s.session,
      kind: s.kind,
      text: s.text,
      on: nextOn,
    };
    if (s.kind === 'daily') {
      item.time = s.time || '09:30';
      if (s.days && s.days.length > 0 && s.days.length < 7) item.days = s.days;
    } else if (s.kind === 'every') {
      item.minutes = s.minutes || 60;
    } else if (s.kind === 'once') {
      item.at = s.at && s.at > Date.now() ? s.at : Date.now() + 30 * 60 * 1000;
    }
    try {
      await workAction('schedule_set', { item });
      await loadAutomations();
      onToast?.(nextOn ? '已启用定时任务' : '已暂停定时任务');
    } catch (err: any) {
      onToast?.(err?.message || '更新定时任务失败', true);
    }
  };

  const removeSchedule = async (s: ScheduleEntry) => {
    if (!window.confirm('删除这个定时任务？')) return;
    try {
      await workAction('schedule_remove', { schedule: s.id });
      await loadAutomations();
      onToast?.('已删除定时任务');
    } catch (err: any) {
      onToast?.(err?.message || '删除定时任务失败', true);
    }
  };

  const removeChain = async (c: ChainEntry) => {
    if (!window.confirm('删除这条对话接力规则？')) return;
    try {
      await workAction('chain_remove', { chain: c.id });
      await loadAutomations();
      onToast?.('已删除对话接力');
    } catch (err: any) {
      onToast?.(err?.message || '删除对话接力失败', true);
    }
  };

  const toggleTeamRun = async (card: TeamCardEntry, e: MouseEvent) => {
    e.stopPropagation();
    const action = card.state === 'running' ? 'team_stop' : 'team_resume';
    try {
      await workAction(action, { team: card.id });
      onToast?.(action === 'team_stop' ? '已暂停协作任务' : '已继续协作任务');
      onChanged?.();
    } catch (err: any) {
      onToast?.(err?.message || '操作失败', true);
    }
  };

  const openSessionThread = (tool: string, session: string) => {
    const found = model.byKey.get(`${tool}:${session}`) || model.all.find((t) => t.session === session);
    if (found) onOpen(found);
    else onToast?.('在当前列表中未找到该对话，可能已被归档', true);
  };

  const card = (t: Thread) => {
    const meta = toolMeta(t.tool);
    const tier = getRecencyTier(t.at, now);
    const todayHit = tier === 'recent' || tier === 'today';
    const status = threadStatus(t, todayHit);
    const place = runtimeLabel(t);
    const exact = formatExactTime(t.at);
    const isPicked = !!picked[t.key];
    const accentCls = t.needs ? `acc-${t.needs}` : '';

    return (
      <div
        key={t.key}
        role="button"
        tabIndex={0}
        class={`home-card ${accentCls}${isPicked ? ' is-picked' : ''}${pickingActive ? ' is-selecting' : ''}`}
        onClick={(e) => {
          if (pickingActive || e.ctrlKey || e.metaKey || e.shiftKey) onPick(t, e.shiftKey);
          else onOpen(t);
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            if (pickingActive) onPick(t, false);
            else onOpen(t);
          }
        }}
        onContextMenu={(e) => {
          e.preventDefault();
          onMenu(t, e);
        }}
      >
        <div class="hc-head">
          <button
            type="button"
            class={`hc-check${isPicked ? ' on' : ''}`}
            aria-label={isPicked ? '取消选择' : '选择对话'}
            aria-pressed={isPicked}
            onClick={(e) => {
              e.stopPropagation();
              onPick(t, (e as MouseEvent).shiftKey);
            }}
          >
            {isPicked ? '✓' : ''}
          </button>
          <span class="tool-mark hc-mark" style={toolBadgeStyle(t.tool)} title={meta.label}>
            {meta.mono}
          </span>
          <span class="hc-title" title={t.title}>
            {t.title}
          </span>
          <span class={`hc-status ${status.cls}${status.pulse ? ' is-pulse' : ''}`}>
            <span class="hc-dot" aria-hidden="true" />
            {status.label}
          </span>
        </div>

        <p class={`hc-body${t.preview ? '' : ' is-empty'}`}>
          {t.preview || (t.runtime === 'idle' ? `在 ${t.project || meta.short} 中继续这段对话` : `${meta.label} · ${place || '进行中'}`)}
        </p>

        <div class="hc-foot">
          <span class="hc-where" title={t.folder || t.project}>
            <span class="hc-proj">{t.project || t.folder.split(/[\\/]/).pop() || '未归项目'}</span>
            {place && <span class="hc-sep">·</span>}
            {place && <span>{place}</span>}
            {t.branch && <span class="hc-branch">{t.branch}</span>}
          </span>
          <span class={`hc-time is-${tier}`} title={exact || undefined}>
            {formatRelativeTime(t.at, now)}
          </span>
        </div>
      </div>
    );
  };

  const compactRow = (t: Thread) => {
    const meta = toolMeta(t.tool);
    const exact = formatExactTime(t.at);
    const isPicked = !!picked[t.key];
    return (
      <div
        key={t.key}
        role="button"
        tabIndex={0}
        class={`home-list-row${isPicked ? ' is-picked' : ''}${pickingActive ? ' is-selecting' : ''}`}
        onClick={(e) => {
          if (pickingActive || e.ctrlKey || e.metaKey || e.shiftKey) onPick(t, e.shiftKey);
          else onOpen(t);
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            if (pickingActive) onPick(t, false);
            else onOpen(t);
          }
        }}
        onContextMenu={(e) => {
          e.preventDefault();
          onMenu(t, e);
        }}
      >
        <button
          type="button"
          class={`hc-check${isPicked ? ' on' : ''}`}
          aria-label={isPicked ? '取消选择' : '选择对话'}
          aria-pressed={isPicked}
          onClick={(e) => {
            e.stopPropagation();
            onPick(t, (e as MouseEvent).shiftKey);
          }}
        >
          {isPicked ? '✓' : ''}
        </button>
        <span class="tool-mark" style={toolBadgeStyle(t.tool)} title={meta.label}>
          {meta.mono}
        </span>
        <span class="hl-main">
          <span class="hl-title">{t.title}</span>
          {t.preview && <span class="hl-preview">{t.preview}</span>}
        </span>
        <span class="hl-proj">{t.project || t.folder.split(/[\\/]/).pop() || ''}</span>
        <span class="hl-time" title={exact || undefined}>
          {formatRelativeTime(t.at, now)}
        </span>
      </div>
    );
  };

  const teamCard = (c: TeamCardEntry) => {
    const st = c.confirm
      ? { label: '等你确认', cls: 'st-bad', pulse: false }
      : TEAM_STATE[c.state] || { label: c.state, cls: 'st-idle', pulse: false };
    const toolsList = c.tools && c.tools.length > 0 ? c.tools : (c.roster || []).map((r) => r.tool);
    return (
      <div
        key={c.id}
        role="button"
        tabIndex={0}
        class={`auto-card team-card${c.confirm || c.state === 'failed' ? ' acc-confirm' : ''}`}
        onClick={() => onOpenTeam(c)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            onOpenTeam(c);
          }
        }}
      >
        <div class="hc-head">
          <span class="tool-mark team-mark hc-mark">协</span>
          <span class="hc-title" title={c.goal}>
            {c.goal || '协作任务'}
          </span>
          <span class={`hc-status ${st.cls}${st.pulse ? ' is-pulse' : ''}`}>
            <span class="hc-dot" aria-hidden="true" />
            {st.label}
          </span>
        </div>

        <p class={`hc-body${c.note ? '' : ' is-empty'}`}>
          {c.note || `${TEAM_MODE[c.mode] || '协作'}${c.rounds ? ` · 第 ${c.round || 1}/${c.rounds} 轮` : ''}`}
        </p>

        <div class="hc-foot">
          <span class="hc-where">
            <span class="hc-proj">{c.dir || '未指定项目'}</span>
            <span class="hc-sep">·</span>
            <span>{TEAM_MODE[c.mode] || c.mode}</span>
            {toolsList.length > 0 && (
              <span class="team-tools-inline">
                {toolsList.slice(0, 4).map((t, i) => (
                  <span key={`${t}:${i}`} class="tool-mark tiny" style={toolBadgeStyle(t)} title={toolLabel(t)}>
                    {toolMeta(t).mono}
                  </span>
                ))}
              </span>
            )}
          </span>
          <span class="auto-card-acts">
            {(c.state === 'running' || c.state === 'paused') && (
              <button
                type="button"
                class="link-btn"
                onClick={(e) => toggleTeamRun(c, e as unknown as MouseEvent)}
              >
                {c.state === 'running' ? '暂停' : '继续'}
              </button>
            )}
            <span class="hc-time">{c.updated ? formatRelativeTime(c.updated, now) : ''}</span>
          </span>
        </div>
      </div>
    );
  };

  const scheduleCard = (s: ScheduleEntry) => {
    const meta = toolMeta(s.tool);
    return (
      <div key={s.id} class={`auto-card${s.on ? '' : ' is-muted'}`}>
        <div class="hc-head">
          <span class="tool-mark hc-mark" style={toolBadgeStyle(s.tool)} title={meta.label}>
            {meta.mono}
          </span>
          <span class="hc-title" title={s.title || s.session}>
            {s.title || `${toolLabel(s.tool)} 对话`}
          </span>
          <span class={`hc-status ${s.on ? 'st-open' : 'st-idle'}`}>
            <span class="hc-dot" aria-hidden="true" />
            {s.on ? formatNextDue(s.next, now) : '已停用'}
          </span>
        </div>

        <p class="hc-body" title={s.text}>
          {s.text}
        </p>

        <div class="hc-foot">
          <span class="hc-where">
            <span class="hc-proj">{formatScheduleRule(s)}</span>
            {s.runs ? (
              <>
                <span class="hc-sep">·</span>
                <span>已执行 {s.runs} 次</span>
              </>
            ) : null}
            {s.last ? (
              <>
                <span class="hc-sep">·</span>
                <span class={s.last.ok ? 'auto-ok' : 'auto-bad'}>
                  {s.last.ok ? '上次成功' : s.last.note || '上次失败'}
                </span>
              </>
            ) : null}
          </span>
          <span class="auto-card-acts">
            <button type="button" class="link-btn" onClick={() => openSessionThread(s.tool, s.session)}>
              打开对话
            </button>
            <button type="button" class="link-btn" onClick={() => toggleScheduleOn(s)}>
              {s.on ? '停用' : '启用'}
            </button>
            <button type="button" class="link-btn" onClick={() => setScheduleModal({ open: true, editing: s })}>
              编辑
            </button>
            <button type="button" class="link-btn danger-link" onClick={() => removeSchedule(s)}>
              删除
            </button>
          </span>
        </div>
      </div>
    );
  };

  const chainCard = (c: ChainEntry) => {
    const active = c.left > 0;
    return (
      <div key={c.id} class={`auto-card${active ? '' : ' is-muted'}`}>
        <div class="hc-head">
          <span class="tool-mark tiny" style={toolBadgeStyle(c.from_tool)} title={toolLabel(c.from_tool)}>
            {toolMeta(c.from_tool).mono}
          </span>
          <span class="hc-title chain-flow" title={`${c.from_title || c.from} → ${c.to_title || c.to}`}>
            {c.from_title || '起点对话'} <span class="chain-arrow">→</span> {c.to_title || '目标对话'}
          </span>
          <span class={`hc-status ${active ? 'st-open' : 'st-idle'}`}>
            <span class="hc-dot" aria-hidden="true" />
            {active ? `剩 ${c.left}/${c.rounds} 轮` : '已完成'}
          </span>
        </div>

        <p class="hc-body" title={c.text}>
          {c.text}
        </p>

        <div class="hc-foot">
          <span class="hc-where">
            <span class="hc-proj">
              {toolLabel(c.from_tool)} → {toolLabel(c.to_tool)}
            </span>
            {c.runs ? (
              <>
                <span class="hc-sep">·</span>
                <span>已触发 {c.runs} 次</span>
              </>
            ) : null}
            {c.last ? (
              <>
                <span class="hc-sep">·</span>
                <span class={c.last.ok ? 'auto-ok' : 'auto-bad'}>
                  {c.last.ok ? '上次成功' : c.last.note || '上次失败'}
                </span>
              </>
            ) : null}
          </span>
          <span class="auto-card-acts">
            <button type="button" class="link-btn" onClick={() => openSessionThread(c.from_tool, c.from)}>
              看起点
            </button>
            <button type="button" class="link-btn" onClick={() => setChainModal({ open: true, editing: c })}>
              编辑
            </button>
            <button type="button" class="link-btn danger-link" onClick={() => removeChain(c)}>
              删除
            </button>
          </span>
        </div>
      </div>
    );
  };

  const empty = model.all.length === 0 && teamCards.length === 0 && schedules.length === 0 && chains.length === 0;
  const subSummary = [
    attention.length > 0 ? `${attention.length} 项待处理` : '',
    running.length > 0 ? `${running.length} 段进行中` : '',
    teamCards.length > 0 ? `${teamCards.length} 个协作` : '',
    activeSchedules.length > 0 ? `${activeSchedules.length} 个定时` : '',
    activeChains.length > 0 ? `${activeChains.length} 条接力` : '',
    todayAll.length > 0 ? `今日活跃 ${todayAll.length} 段` : '',
    `${model.projects.length} 个项目`,
  ]
    .filter(Boolean)
    .join(' · ');

  return (
    <main class="home">
      <div class="home-inner">
        {empty ? (
          <div class="welcome">
            <div class="brand-mark big">R</div>
            <h1>开始第一段对话</h1>
            <p>选一个项目文件夹，交给电脑上的 AI 去做。手机上也能看到并接着说。</p>
            <div class="row-btns center">
              <button type="button" class="btn primary" onClick={() => onNew()} disabled={!(device?.workspaces || []).length}>
                新建对话
              </button>
              <button type="button" class="btn quiet" onClick={onAddProject}>
                添加项目文件夹
              </button>
            </div>
          </div>
        ) : (
          <>
            <header class="home-head">
              <div class="home-head-text">
                <h1>总览</h1>
                <p class="home-sub">{subSummary}</p>
              </div>
              <div class="home-head-acts">
                <button
                  type="button"
                  class={`btn quiet small${pickingActive ? ' is-on' : ''}`}
                  onClick={() => (pickingActive ? exitSelectMode() : setSelectMode(true))}
                >
                  {pickingActive ? '退出多选' : '多选'}
                </button>
                {canTeam && (
                  <button type="button" class="btn quiet small" onClick={() => onNewTeam()}>
                    发起协作
                  </button>
                )}
                <button type="button" class="btn quiet small" onClick={onAddProject}>
                  添加项目
                </button>
                <button type="button" class="btn primary" onClick={() => onNew()}>
                  新建对话
                </button>
              </div>
            </header>

            <div class="home-kpi-strip" role="tablist" aria-label="总览筛选">
              <button
                type="button"
                role="tab"
                aria-selected={view === 'attention'}
                class={`kpi-cell${attention.length > 0 ? ' has-bad' : ''}${view === 'attention' ? ' is-on' : ''}`}
                onClick={() => toggleView('attention')}
              >
                <span class="kpi-label">
                  {attention.length > 0 && <span class="kpi-dot" aria-hidden="true" />}
                  待处理
                </span>
                <strong class="kpi-val">{attention.length}</strong>
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={view === 'running'}
                class={`kpi-cell${running.length > 0 ? ' has-busy' : ''}${view === 'running' ? ' is-on' : ''}`}
                onClick={() => toggleView('running')}
              >
                <span class="kpi-label">
                  {running.length > 0 && <span class="kpi-dot" aria-hidden="true" />}
                  进行中
                </span>
                <strong class="kpi-val">{running.length}</strong>
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={view === 'today'}
                class={`kpi-cell${todayAll.length > 0 ? ' has-accent' : ''}${view === 'today' ? ' is-on' : ''}`}
                onClick={() => toggleView('today')}
              >
                <span class="kpi-label">
                  {todayAll.length > 0 && <span class="kpi-dot" aria-hidden="true" />}
                  今日活跃
                </span>
                <strong class="kpi-val">{todayAll.length}</strong>
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={view === 'all'}
                class={`kpi-cell${view === 'all' ? ' is-on' : ''}`}
                onClick={() => setView('all')}
              >
                <span class="kpi-label">全部对话</span>
                <strong class="kpi-val">{model.all.length}</strong>
              </button>
            </div>

            <div class="home-task-bar" role="tablist" aria-label="任务类型">
              <button
                type="button"
                role="tab"
                aria-selected={view === 'all' || view === 'attention' || view === 'running' || view === 'today'}
                class={`task-tab${view === 'all' || view === 'attention' || view === 'running' || view === 'today' ? ' on' : ''}`}
                onClick={() => setView('all')}
              >
                全部概览
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={view === 'teams'}
                class={`task-tab${view === 'teams' ? ' on' : ''}`}
                onClick={() => setView('teams')}
              >
                多 AI 协作
                {teamCards.length > 0 && <span class="pill-n">{teamCards.length}</span>}
              </button>
              {hasWork && (
                <button
                  type="button"
                  role="tab"
                  aria-selected={view === 'schedules'}
                  class={`task-tab${view === 'schedules' ? ' on' : ''}`}
                  onClick={() => {
                    setView('schedules');
                    loadAutomations();
                  }}
                >
                  定时任务
                  {schedules.length > 0 && <span class="pill-n">{schedules.length}</span>}
                </button>
              )}
              {hasChains && (
                <button
                  type="button"
                  role="tab"
                  aria-selected={view === 'chains'}
                  class={`task-tab${view === 'chains' ? ' on' : ''}`}
                  onClick={() => {
                    setView('chains');
                    loadAutomations();
                  }}
                >
                  对话接力
                  {chains.length > 0 && <span class="pill-n">{chains.length}</span>}
                </button>
              )}
              <span class="grow" />
              {hasWork && (
                <button
                  type="button"
                  class="link-btn"
                  disabled={automatableCount === 0}
                  title={automatableCount === 0 ? '先有一段 Claude Code 或 Codex 对话后再创建定时' : '为对话设置定时提问'}
                  onClick={() => setScheduleModal({ open: true, editing: null })}
                >
                  + 定时
                </button>
              )}
              {hasChains && (
                <button
                  type="button"
                  class="link-btn"
                  disabled={automatableCount < 2}
                  title={automatableCount < 2 ? '至少需要两段 Claude Code 或 Codex 对话' : '设置完成一轮后自动交给下一段对话'}
                  onClick={() => setChainModal({ open: true, editing: null })}
                >
                  + 接力
                </button>
              )}
            </div>

            {pickingActive && (
              <div class="home-batch-bar" role="region" aria-label="批量操作">
                <span class="batch-count">已选 {pickedThreads.length} 个对话</span>
                {visibleThreadsForBatch.length > 0 && (
                  <button
                    type="button"
                    class="link-btn"
                    onClick={() => {
                      const allSelected = visibleThreadsForBatch.every((t) => picked[t.key]);
                      if (allSelected) onClearPicked();
                      else onPickMany(visibleThreadsForBatch.map((t) => t.key));
                    }}
                  >
                    {visibleThreadsForBatch.every((t) => picked[t.key]) ? '取消全选' : `全选当前 (${visibleThreadsForBatch.length})`}
                  </button>
                )}
                <span class="grow" />
                <button
                  type="button"
                  class="btn quiet small"
                  disabled={stoppableCount === 0}
                  onClick={() => onBatchAction('stop', pickedThreads)}
                >
                  停止{stoppableCount > 0 ? ` (${stoppableCount})` : ''}
                </button>
                <button
                  type="button"
                  class="btn quiet small"
                  disabled={pickedThreads.length === 0}
                  onClick={() => onBatchAction('read', pickedThreads)}
                >
                  标为已读
                </button>
                <button
                  type="button"
                  class="btn quiet small"
                  disabled={archivableCount === 0}
                  onClick={() => onBatchAction('archive', pickedThreads)}
                >
                  归档{archivableCount > 0 ? ` (${archivableCount})` : ''}
                </button>
                <button
                  type="button"
                  class="btn quiet small is-danger"
                  disabled={endableCount === 0}
                  onClick={() => onBatchAction('end', pickedThreads)}
                >
                  结束{endableCount > 0 ? ` (${endableCount})` : ''}
                </button>
                <button type="button" class="link-btn" onClick={exitSelectMode}>
                  完成
                </button>
              </div>
            )}

            {view === 'teams' ? (
              <section class="home-sec">
                <div class="home-sec-head">
                  <h2>多 AI 协作任务</h2>
                  <span class="sec-count">{teamCards.length}</span>
                  {canTeam && (
                    <button type="button" class="btn primary small sec-act" onClick={() => onNewTeam()}>
                      发起协作
                    </button>
                  )}
                </div>
                {teamCards.length > 0 ? (
                  <div class="auto-grid">{teamCards.map(teamCard)}</div>
                ) : (
                  <div class="home-empty-card">
                    <span>还没有多 AI 协作任务，可让多个工具分工、写审或比稿完成同一目标。</span>
                    {canTeam && (
                      <button type="button" class="link-btn" onClick={() => onNewTeam()}>
                        立即发起协作
                      </button>
                    )}
                  </div>
                )}
              </section>
            ) : view === 'schedules' ? (
              <section class="home-sec">
                <div class="home-sec-head">
                  <h2>定时提问任务</h2>
                  <span class="sec-count">{schedules.length}</span>
                  <button
                    type="button"
                    class="link-btn sec-act"
                    disabled={autoLoading}
                    onClick={() => loadAutomations()}
                  >
                    {autoLoading ? '刷新中…' : '刷新'}
                  </button>
                  <button
                    type="button"
                    class="btn primary small"
                    disabled={automatableCount === 0}
                    onClick={() => setScheduleModal({ open: true, editing: null })}
                  >
                    新建定时
                  </button>
                </div>
                {schedules.length > 0 ? (
                  <div class="auto-grid">{schedules.map(scheduleCard)}</div>
                ) : (
                  <div class="home-empty-card">
                    <span>还没有定时任务，可按天、按星期、固定间隔或单次定时向对话发消息。</span>
                    <button
                      type="button"
                      class="link-btn"
                      disabled={automatableCount === 0}
                      onClick={() => setScheduleModal({ open: true, editing: null })}
                    >
                      新建第一个定时任务
                    </button>
                  </div>
                )}
              </section>
            ) : view === 'chains' ? (
              <section class="home-sec">
                <div class="home-sec-head">
                  <h2>对话接力规则</h2>
                  <span class="sec-count">{chains.length}</span>
                  <button
                    type="button"
                    class="link-btn sec-act"
                    disabled={autoLoading}
                    onClick={() => loadAutomations()}
                  >
                    {autoLoading ? '刷新中…' : '刷新'}
                  </button>
                  <button
                    type="button"
                    class="btn primary small"
                    disabled={automatableCount < 2}
                    onClick={() => setChainModal({ open: true, editing: null })}
                  >
                    新建接力
                  </button>
                </div>
                {chains.length > 0 ? (
                  <div class="auto-grid">{chains.map(chainCard)}</div>
                ) : (
                  <div class="home-empty-card">
                    <span>还没有对话接力，可设置某段对话完成一轮后自动把回复交给另一段对话继续处理。</span>
                    <button
                      type="button"
                      class="link-btn"
                      disabled={automatableCount < 2}
                      onClick={() => setChainModal({ open: true, editing: null })}
                    >
                      新建第一条接力
                    </button>
                  </div>
                )}
              </section>
            ) : view === 'attention' ? (
              <section class="home-sec">
                <div class="home-sec-head">
                  <h2>需要你处理</h2>
                  <span class="sec-count">{attention.length}</span>
                  {onSeenAll && attention.some((t) => t.needs === 'done' || t.needs === 'failed') && (
                    <button type="button" class="link-btn sec-act" onClick={onSeenAll}>
                      全部已读
                    </button>
                  )}
                </div>
                {attention.length ? (
                  <div class="home-grid">{attention.map(card)}</div>
                ) : (
                  <div class="home-empty-card">当前没有等待确认或未读的对话。</div>
                )}
              </section>
            ) : view === 'running' ? (
              <section class="home-sec">
                <div class="home-sec-head">
                  <h2>正在进行</h2>
                  <span class="sec-count">{running.length}</span>
                </div>
                {running.length ? (
                  <div class="home-grid">{running.map(card)}</div>
                ) : (
                  <div class="home-empty-card">
                    <span>当前没有正在运行的对话。</span>
                    <button type="button" class="link-btn" onClick={() => onNew()}>
                      新建一段对话
                    </button>
                  </div>
                )}
              </section>
            ) : view === 'today' ? (
              <section class="home-sec">
                <div class="home-sec-head">
                  <h2>今日活跃</h2>
                  <span class="sec-count">{todayAll.length}</span>
                </div>
                {todayAll.length ? (
                  <div class="home-grid">{todayAll.map(card)}</div>
                ) : (
                  <div class="home-empty-card">今天还没有更新过的对话。</div>
                )}
              </section>
            ) : (
              <>
                {attention.length > 0 && (
                  <section class="home-sec">
                    <div class="home-sec-head">
                      <h2>需要你处理</h2>
                      <span class="sec-count is-bad">{attention.length}</span>
                      {onSeenAll && attention.some((t) => t.needs === 'done' || t.needs === 'failed') && (
                        <button type="button" class="link-btn sec-act" onClick={onSeenAll}>
                          全部已读
                        </button>
                      )}
                    </div>
                    <div class="home-grid">{attention.map(card)}</div>
                  </section>
                )}

                {running.length > 0 && (
                  <section class="home-sec">
                    <div class="home-sec-head">
                      <h2>正在进行</h2>
                      <span class="sec-count is-busy">{running.length}</span>
                    </div>
                    <div class="home-grid">{running.map(card)}</div>
                  </section>
                )}

                {(canTeam || teamCards.length > 0 || hasWork || hasChains) && (
                  <section class="home-sec">
                    <div class="home-sec-head">
                      <h2>协作与自动化任务</h2>
                      <span class="sec-count">{teamCards.length + schedules.length + chains.length}</span>
                    </div>
                    <div class="home-hub">
                      <div class="hub-tile" onClick={() => setView('teams')}>
                        <div class="hub-top">
                          <span class="hub-name">多 AI 协作</span>
                          <span class="hub-num">{teamCards.length}</span>
                        </div>
                        <p class="hub-desc">
                          {runningTeams.length > 0
                            ? `${runningTeams.length} 个进行中或待确认`
                            : teamCards.length > 0
                              ? `共 ${teamCards.length} 个协作记录`
                              : '分工、写审或比稿，多工具并行完成'}
                        </p>
                        <div class="hub-foot">
                          <button
                            type="button"
                            class="link-btn"
                            onClick={(e) => {
                              e.stopPropagation();
                              setView('teams');
                            }}
                          >
                            查看全部
                          </button>
                          {canTeam && (
                            <button
                              type="button"
                              class="link-btn"
                              onClick={(e) => {
                                e.stopPropagation();
                                onNewTeam();
                              }}
                            >
                              + 发起
                            </button>
                          )}
                        </div>
                      </div>

                      {hasWork && (
                        <div class="hub-tile" onClick={() => setView('schedules')}>
                          <div class="hub-top">
                            <span class="hub-name">定时提问</span>
                            <span class="hub-num">{activeSchedules.length}</span>
                          </div>
                          <p class="hub-desc">
                            {activeSchedules.length > 0
                              ? `已启用 ${activeSchedules.length} 个 · 下次 ${formatNextDue(
                                  activeSchedules
                                    .map((s) => s.next || 0)
                                    .filter((n) => n > 0)
                                    .sort((a, b) => a - b)[0],
                                  now,
                                )}`
                              : schedules.length > 0
                                ? `已保存 ${schedules.length} 个（均已停用）`
                                : '按天、按星期或固定间隔自动追问'}
                          </p>
                          <div class="hub-foot">
                            <button
                              type="button"
                              class="link-btn"
                              onClick={(e) => {
                                e.stopPropagation();
                                setView('schedules');
                              }}
                            >
                              管理定时
                            </button>
                            <button
                              type="button"
                              class="link-btn"
                              disabled={automatableCount === 0}
                              onClick={(e) => {
                                e.stopPropagation();
                                setScheduleModal({ open: true, editing: null });
                              }}
                            >
                              + 新建
                            </button>
                          </div>
                        </div>
                      )}

                      {hasChains && (
                        <div class="hub-tile" onClick={() => setView('chains')}>
                          <div class="hub-top">
                            <span class="hub-name">对话接力</span>
                            <span class="hub-num">{activeChains.length}</span>
                          </div>
                          <p class="hub-desc">
                            {activeChains.length > 0
                              ? `${activeChains.length} 条接力等待触发`
                              : chains.length > 0
                                ? `已保存 ${chains.length} 条（已完成）`
                                : '一段对话完成后自动把结果转给下一段'}
                          </p>
                          <div class="hub-foot">
                            <button
                              type="button"
                              class="link-btn"
                              onClick={(e) => {
                                e.stopPropagation();
                                setView('chains');
                              }}
                            >
                              管理接力
                            </button>
                            <button
                              type="button"
                              class="link-btn"
                              disabled={automatableCount < 2}
                              onClick={(e) => {
                                e.stopPropagation();
                                setChainModal({ open: true, editing: null });
                              }}
                            >
                              + 新建
                            </button>
                          </div>
                        </div>
                      )}
                    </div>

                    {runningTeams.length > 0 && (
                      <div class="auto-grid">{runningTeams.slice(0, 2).map(teamCard)}</div>
                    )}
                  </section>
                )}

                {todayIdle.length > 0 && (
                  <section class="home-sec">
                    <div class="home-sec-head">
                      <h2>今日对话</h2>
                      <span class="sec-count">{todayIdle.length}</span>
                    </div>
                    <div class="home-grid">{todayIdle.slice(0, 6).map(card)}</div>
                  </section>
                )}

                {earlier.length > 0 && (
                  <section class="home-sec">
                    <div class="home-sec-head">
                      <h2>更早的最近对话</h2>
                      <span class="sec-count">{earlier.length}</span>
                    </div>
                    <div class="home-list-card">{earlier.map(compactRow)}</div>
                  </section>
                )}
              </>
            )}
          </>
        )}
      </div>

      {scheduleModal.open && (
        <ScheduleDialog
          threads={model.all}
          features={features}
          editing={scheduleModal.editing}
          onClose={() => setScheduleModal({ open: false, editing: null })}
          onSaved={() => {
            setScheduleModal({ open: false, editing: null });
            loadAutomations();
            onToast?.('定时任务已保存');
          }}
        />
      )}

      {chainModal.open && (
        <ChainDialog
          threads={model.all}
          editing={chainModal.editing}
          onClose={() => setChainModal({ open: false, editing: null })}
          onSaved={() => {
            setChainModal({ open: false, editing: null });
            loadAutomations();
            onToast?.('对话接力已保存');
          }}
        />
      )}
    </main>
  );
}
