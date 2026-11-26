import { useEffect, useState } from 'preact/hooks';
import { formatExactTime, formatRelativeTime, getRecencyTier, isToday } from '../runtime/time';
import { runtimeLabel, type Thread, type ThreadModel } from '../runtime/threads';
import { toolBadgeStyle, toolMeta, type DeviceInfo } from '../runtime/types';

type HomeView = 'all' | 'attention' | 'running' | 'today';

interface Props {
  device: DeviceInfo | null;
  model: ThreadModel;
  onOpen: (t: Thread) => void;
  onMenu: (t: Thread, e: MouseEvent) => void;
  onNew: (dir?: string) => void;
  onAddProject: () => void;
  onSeenAll?: () => void;
}

const NEED_STATUS: Record<string, { label: string; cls: string }> = {
  confirm: { label: '等你确认', cls: 'st-bad' },
  failed: { label: '出错了', cls: 'st-bad' },
  done: { label: '已完成', cls: 'st-good' },
};

function threadStatus(t: Thread, isTodayHit: boolean): { label: string; cls: string; pulse?: boolean } {
  if (t.needs && NEED_STATUS[t.needs]) return NEED_STATUS[t.needs];
  if (t.busy) return { label: '正在执行', cls: 'st-busy', pulse: true };
  if (t.runtime !== 'idle') return { label: runtimeLabel(t) || '已打开', cls: 'st-open' };
  if (isTodayHit) return { label: '今日活跃', cls: 'st-today' };
  return { label: '已保存', cls: 'st-idle' };
}

/** Command-center overview: hairline KPI summary bar, equal-height conversation cards, and compact earlier list. */
export function Home({ device, model, onOpen, onMenu, onNew, onAddProject, onSeenAll }: Props) {
  const [view, setView] = useState<HomeView>('all');
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 30000);
    return () => window.clearInterval(timer);
  }, []);

  const attention = model.attention;
  const running = model.all.filter((t) => !t.needs && t.runtime !== 'idle');
  const todayAll = model.all.filter((t) => isToday(t.at, now));
  const todayIdle = model.all.filter((t) => !t.needs && t.runtime === 'idle' && isToday(t.at, now));
  const earlier = model.all.filter((t) => !t.needs && t.runtime === 'idle' && !isToday(t.at, now)).slice(0, 8);

  const toggleView = (next: HomeView) => setView((cur) => (cur === next ? 'all' : next));

  const card = (t: Thread) => {
    const meta = toolMeta(t.tool);
    const tier = getRecencyTier(t.at, now);
    const todayHit = tier === 'recent' || tier === 'today';
    const status = threadStatus(t, todayHit);
    const place = runtimeLabel(t);
    const exact = formatExactTime(t.at);
    const accentCls = t.needs
      ? `acc-${t.needs}`
      : t.busy
        ? 'acc-busy'
        : todayHit
          ? `acc-${tier}`
          : '';

    return (
      <button
        key={t.key}
        type="button"
        class={`home-card ${accentCls}`}
        onClick={() => onOpen(t)}
        onContextMenu={(e) => {
          e.preventDefault();
          onMenu(t, e);
        }}
      >
        <div class="hc-head">
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
      </button>
    );
  };

  const compactRow = (t: Thread) => {
    const meta = toolMeta(t.tool);
    const exact = formatExactTime(t.at);
    return (
      <button
        key={t.key}
        type="button"
        class="home-list-row"
        onClick={() => onOpen(t)}
        onContextMenu={(e) => {
          e.preventDefault();
          onMenu(t, e);
        }}
      >
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
      </button>
    );
  };

  const empty = model.all.length === 0;
  const subSummary = [
    attention.length > 0 ? `${attention.length} 项待处理` : '',
    running.length > 0 ? `${running.length} 段进行中` : '',
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

            {view === 'attention' ? (
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
    </main>
  );
}
