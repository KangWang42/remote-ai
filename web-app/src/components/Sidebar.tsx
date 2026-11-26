import { useEffect, useMemo, useState } from 'preact/hooks';
import { formatExactTime, formatRelativeTime, getRecencyTier, isToday } from '../runtime/time';
import { runtimeLabel, type ProjectGroup, type Thread, type ThreadModel } from '../runtime/threads';
import { toolBadgeStyle, toolMeta, type DeviceInfo, type TeamCardEntry } from '../runtime/types';

type SideFilter = 'all' | 'today' | 'active';

interface Props {
  device: DeviceInfo | null;
  model: ThreadModel;
  selectedKey: string;
  teamOpen: string;
  picked: Record<string, boolean>;
  connected: boolean;
  route: string;
  onOpen: (t: Thread) => void;
  onPick: (t: Thread, range: boolean) => void;
  onThreadMenu: (t: Thread, e: MouseEvent) => void;
  onProjectMenu: (name: string, e: MouseEvent) => void;
  onOpenTeam: (card: TeamCardEntry) => void;
  onNew: (dir?: string) => void;
  onAddProject: () => void;
  onSeenAll: () => void;
  onSettings: () => void;
  onHome: () => void;
}

const HISTORY_SHOWN = 3;

export function Sidebar(p: Props) {
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState<SideFilter>('all');
  const [folded, setFolded] = useState<Record<string, boolean>>(() => loadFolded());
  const [more, setMore] = useState<Record<string, boolean>>({});
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 30000);
    return () => window.clearInterval(timer);
  }, []);

  const matchesFilter = (t: Thread) => {
    if (filter === 'today') return isToday(t.at, now);
    if (filter === 'active') return t.runtime !== 'idle' || t.needs !== null;
    return true;
  };

  const todayTotal = useMemo(() => p.model.all.filter((t) => isToday(t.at, now)).length, [p.model.all, now]);
  const activeTotal = useMemo(
    () => p.model.all.filter((t) => t.runtime !== 'idle' || t.needs !== null).length,
    [p.model.all],
  );

  const q = query.trim().toLowerCase();
  const found = useMemo(() => {
    if (!q) return null;
    return p.model.all.filter(
      (t) =>
        matchesFilter(t) &&
        [t.title, t.project, t.folder, t.preview, toolMeta(t.tool).label].some((s) => (s || '').toLowerCase().includes(q)),
    );
  }, [p.model.all, q, filter, now]);

  const toggleFold = (name: string) => {
    setFolded((prev) => {
      const next = { ...prev, [name]: !prev[name] };
      saveFolded(next);
      return next;
    });
  };

  const row = (t: Thread, withPreview = false) => {
    const meta = toolMeta(t.tool);
    const active = p.selectedKey === t.key;
    const picked = !!p.picked[t.key];
    const state = t.needs || (t.busy ? 'busy' : t.runtime !== 'idle' ? 'open' : 'idle');
    const tier = getRecencyTier(t.at, now);
    const todayHit = tier === 'recent' || tier === 'today';
    const exact = formatExactTime(t.at);
    return (
      <button
        key={t.key}
        type="button"
        class={`th-row${active ? ' active' : ''}${picked ? ' picked' : ''}${todayHit ? ` is-${tier}` : ''}`}
        onClick={(e) => {
          if (e.ctrlKey || e.metaKey || e.shiftKey) p.onPick(t, e.shiftKey);
          else p.onOpen(t);
        }}
        onContextMenu={(e) => {
          e.preventDefault();
          p.onThreadMenu(t, e);
        }}
        title={exact ? `${t.title} · ${exact}` : t.title}
      >
        <span class={`th-dot st-${state}`} aria-hidden="true" />
        <span class="tool-mark" style={toolBadgeStyle(t.tool)} title={meta.label}>
          {meta.mono}
        </span>
        <span class="th-main">
          <span class="th-top">
            <span class="th-title">{t.title}</span>
            {t.pinned && (
              <span class="th-pin" title="已置顶" aria-label="已置顶">
                置顶
              </span>
            )}
            {todayHit && (
              <span class={`rec-tag is-${tier}`} title={exact ? `今日更新：${exact}` : '今日'}>
                今日
              </span>
            )}
            <span class={`th-time is-${tier}`} title={exact || undefined}>
              {formatRelativeTime(t.at, now)}
            </span>
          </span>
          {withPreview && (t.preview || t.needs) ? (
            <span class="th-sub">
              {t.needs ? <span class={`th-need need-${t.needs}`}>{NEED_WORD[t.needs]}</span> : null}
              <span class="th-preview">{t.preview || runtimeLabel(t)}</span>
            </span>
          ) : null}
        </span>
      </button>
    );
  };

  const teamRow = (card: TeamCardEntry, members: Thread[]) => {
    const active = p.teamOpen === card.id;
    const state = card.confirm ? 'confirm' : card.state === 'running' ? 'busy' : 'idle';
    const tier = getRecencyTier(card.updated, now);
    const todayHit = tier === 'recent' || tier === 'today';
    return (
      <div key={`team:${card.id}`} class="th-team">
        <button
          type="button"
          class={`th-row${active ? ' active' : ''}${todayHit ? ` is-${tier}` : ''}`}
          onClick={() => p.onOpenTeam(card)}
          title={card.goal}
        >
          <span class={`th-dot st-${state}`} aria-hidden="true" />
          <span class="tool-mark team-mark">协</span>
          <span class="th-main">
            <span class="th-top">
              <span class="th-title">{card.goal || '协作任务'}</span>
              {todayHit && <span class={`rec-tag is-${tier}`}>今日</span>}
              <span class={`th-time is-${tier}`}>{card.updated ? formatRelativeTime(card.updated, now) : ''}</span>
            </span>
            <span class="th-sub">
              <span class="th-preview">
                {TEAM_MODE[card.mode] || '协作'} · {members.length} 位成员{card.confirm ? ' · 待确认' : ''}
              </span>
            </span>
          </span>
        </button>
        {members.length > 0 && <div class="th-members">{members.map((m) => row(m))}</div>}
      </div>
    );
  };

  const group = (g: ProjectGroup) => {
    const isFolded = filter === 'all' && !!folded[g.name];
    const teamIds = new Set(g.teams.map((c) => c.id));
    const filteredTeams =
      filter === 'all'
        ? g.teams
        : g.teams.filter((c) => {
            if (filter === 'today') return isToday(c.updated, now) || g.threads.some((t) => t.team === c.id && isToday(t.at, now));
            return c.state === 'running' || !!c.confirm || g.threads.some((t) => t.team === c.id && (t.runtime !== 'idle' || t.needs !== null));
          });
    const solo = g.threads.filter((t) => (!t.team || !teamIds.has(t.team)) && matchesFilter(t));
    if (filter !== 'all' && solo.length === 0 && filteredTeams.length === 0) return null;

    const todayInProj = g.threads.filter((t) => isToday(t.at, now)).length;
    const live = solo.filter((t) => t.runtime !== 'idle' || t.needs);
    const history = solo.filter((t) => t.runtime === 'idle' && !t.needs);
    const showAllHistory = filter !== 'all' || !!more[g.name];
    const shownHistory = showAllHistory ? history : history.slice(0, Math.max(0, HISTORY_SHOWN - live.length));
    const hidden = history.length - shownHistory.length;
    const projTitle = g.latestAt ? `${g.name} · 最近活跃：${formatRelativeTime(g.latestAt, now)}` : g.name;

    return (
      <section key={g.name} class={`proj${todayInProj > 0 ? ' has-today' : ''}`}>
        <div
          class="proj-head"
          onContextMenu={(e) => {
            e.preventDefault();
            p.onProjectMenu(g.name, e);
          }}
        >
          <button type="button" class="proj-toggle" onClick={() => toggleFold(g.name)} title={projTitle}>
            <span class={`fold${isFolded ? '' : ' open'}`} aria-hidden="true" />
            <span class="proj-name">{g.name}</span>
            {g.pinned ? <span class="proj-pin" aria-label="已置顶">●</span> : null}
            <span class="proj-badges">
              {todayInProj > 0 ? (
                <span class="proj-today" title={`今日有 ${todayInProj} 段对话更新`}>
                  今日 {todayInProj}
                </span>
              ) : null}
              {g.attention > 0 ? (
                <span class="proj-count need">{g.attention}</span>
              ) : g.busy > 0 ? (
                <span class="proj-count busy">{g.busy}</span>
              ) : null}
            </span>
          </button>
          <button type="button" class="proj-act" title={`在 ${g.name} 中新建对话`} onClick={() => p.onNew(g.name)}>
            <Plus />
          </button>
          <button
            type="button"
            class="proj-act"
            title="更多"
            onClick={(e) => p.onProjectMenu(g.name, e as unknown as MouseEvent)}
          >
            <Dots />
          </button>
        </div>
        {!isFolded && (
          <div class="proj-body">
            {filteredTeams.map((card) =>
              teamRow(
                card,
                g.threads.filter((t) => t.team === card.id && matchesFilter(t)),
              ),
            )}
            {live.map((t) => row(t))}
            {shownHistory.map((t) => row(t))}
            {hidden > 0 && (
              <button type="button" class="th-more" onClick={() => setMore((m) => ({ ...m, [g.name]: true }))}>
                更早的 {hidden} 个对话
              </button>
            )}
            {filter === 'all' && more[g.name] && history.length > HISTORY_SHOWN && (
              <button type="button" class="th-more" onClick={() => setMore((m) => ({ ...m, [g.name]: false }))}>
                收起
              </button>
            )}
            {filter === 'all' && g.threads.length === 0 && g.teams.length === 0 && (
              <button type="button" class="th-more" onClick={() => p.onNew(g.name)}>
                还没有对话，新建一个
              </button>
            )}
          </div>
        )}
      </section>
    );
  };

  const attention = p.model.attention;
  const renderedProjects = p.model.projects.map(group).filter(Boolean);
  const filteredLoose = p.model.loose.filter(matchesFilter);

  return (
    <aside class="sidebar">
      <div class="side-top">
        <button type="button" class="brand" onClick={p.onHome} title="总览">
          <span class="brand-mark">R</span>
          <span class="brand-name">Remote AI</span>
        </button>
        <button type="button" class="new-btn" onClick={() => p.onNew()} title="新建对话 (Ctrl+N)">
          <Plus />
          <span>新建对话</span>
        </button>
        <input
          type="search"
          class="side-search"
          placeholder="搜索对话"
          value={query}
          onInput={(e) => setQuery((e.target as HTMLInputElement).value)}
          onKeyDown={(e) => {
            if (e.key === 'Escape') setQuery('');
          }}
        />
        <div class="side-filters" role="tablist" aria-label="对话筛选">
          <button
            type="button"
            role="tab"
            aria-selected={filter === 'all'}
            class={`filter-pill${filter === 'all' ? ' on' : ''}`}
            onClick={() => setFilter('all')}
          >
            全部
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={filter === 'today'}
            class={`filter-pill${filter === 'today' ? ' on' : ''}`}
            onClick={() => setFilter(filter === 'today' ? 'all' : 'today')}
          >
            今日{todayTotal > 0 ? <span class="pill-n">{todayTotal}</span> : null}
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={filter === 'active'}
            class={`filter-pill${filter === 'active' ? ' on' : ''}`}
            onClick={() => setFilter(filter === 'active' ? 'all' : 'active')}
          >
            进行中{activeTotal > 0 ? <span class="pill-n">{activeTotal}</span> : null}
          </button>
        </div>
      </div>

      <div class="side-scroll">
        {found ? (
          <section class="side-sec">
            <div class="side-label">搜索结果 · {found.length}</div>
            {found.length === 0 ? <div class="side-empty">没有找到</div> : found.map((t) => row(t, true))}
          </section>
        ) : (
          <>
            {filter === 'all' && attention.length > 0 && (
              <section class="side-sec need-sec">
                <div class="side-label">
                  <span>需要你处理 · {attention.length}</span>
                  {attention.some((t) => t.needs === 'done' || t.needs === 'failed') && (
                    <button type="button" class="link-btn" onClick={p.onSeenAll}>
                      全部已读
                    </button>
                  )}
                </div>
                {attention.map((t) => row(t, true))}
              </section>
            )}

            <section class="side-sec">
              <div class="side-label">
                <span>{filter === 'today' ? '今日项目' : filter === 'active' ? '进行中的项目' : '项目'}</span>
                <button type="button" class="link-btn" onClick={p.onAddProject} title="添加项目文件夹">
                  添加
                </button>
              </div>
              {p.model.projects.length === 0 ? (
                <button type="button" class="th-more" onClick={p.onAddProject}>
                  添加第一个项目文件夹
                </button>
              ) : renderedProjects.length === 0 ? (
                <div class="side-empty">
                  {filter === 'today' ? '今天还没有活跃的对话' : '当前没有进行中的对话'}
                </div>
              ) : (
                renderedProjects
              )}
            </section>

            {filteredLoose.length > 0 && (
              <section class="side-sec">
                <div class="side-label">
                  <span>其它文件夹</span>
                </div>
                {filteredLoose.map((t) => row(t))}
              </section>
            )}
          </>
        )}
      </div>

      <div class="side-foot">
        <button type="button" class="foot-btn" onClick={p.onSettings} title="外观、连接与手机、AI 工具">
          <Gear />
          <span>设置</span>
        </button>
        <span class={`conn ${p.connected && p.device?.online ? 'on' : 'off'}`} title="与电脑端的连接">
          <span class="conn-dot" />
          {p.connected ? (p.device?.enabled === false ? '远控已关闭' : p.route) : '正在重连'}
        </span>
      </div>
    </aside>
  );
}

const NEED_WORD: Record<string, string> = { confirm: '等你确认', failed: '出错', done: '已完成' };
const TEAM_MODE: Record<string, string> = { lead: '分工', review: '写审', race: '比稿', discuss: '讨论' };

const FOLD_KEY = 'rcli-console-folded-v1';
function loadFolded(): Record<string, boolean> {
  try {
    return JSON.parse(localStorage.getItem(FOLD_KEY) || '{}') || {};
  } catch {
    return {};
  }
}
function saveFolded(map: Record<string, boolean>) {
  try {
    localStorage.setItem(FOLD_KEY, JSON.stringify(map));
  } catch {
    // the fold lasts as long as the page
  }
}

export const Plus = () => (
  <svg viewBox="0 0 20 20" width="14" height="14" aria-hidden="true">
    <path d="M10 4.5v11M4.5 10h11" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
  </svg>
);
export const Dots = () => (
  <svg viewBox="0 0 20 20" width="14" height="14" aria-hidden="true">
    <circle cx="4.5" cy="10" r="1.5" fill="currentColor" />
    <circle cx="10" cy="10" r="1.5" fill="currentColor" />
    <circle cx="15.5" cy="10" r="1.5" fill="currentColor" />
  </svg>
);
const Gear = () => (
  <svg viewBox="0 0 20 20" width="15" height="15" aria-hidden="true">
    <circle cx="10" cy="10" r="2.6" fill="none" stroke="currentColor" stroke-width="1.6" />
    <path
      d="M10 2.8v2M10 15.2v2M2.8 10h2M15.2 10h2M4.9 4.9l1.4 1.4M13.7 13.7l1.4 1.4M4.9 15.1l1.4-1.4M13.7 6.3l1.4-1.4"
      stroke="currentColor"
      stroke-width="1.6"
      stroke-linecap="round"
    />
  </svg>
);
