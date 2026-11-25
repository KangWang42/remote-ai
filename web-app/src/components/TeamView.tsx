import { useEffect, useState } from 'preact/hooks';
import { workAction } from '../runtime/api';
import { formatExactTime, formatRelativeTime } from '../runtime/time';
import { toolBadgeStyle, toolLabel, toolMeta } from '../runtime/types';
import { Composer } from './Composer';
import { ContextMenu, type MenuState } from './ContextMenu';
import { Dots } from './Sidebar';

interface Team {
  id: string;
  dir: string;
  mode: string;
  goal: string;
  state: string;
  note?: string;
  phase?: string;
  round?: number;
  rounds?: number;
  tools?: string[];
  members?: Record<string, string>;
  member_sessions?: Record<string, { id: string; tool: string }>;
  awaited?: string[];
  failed?: Record<string, string>;
  candidates?: Array<{ tool: string; summary?: string }>;
  picked?: string;
  log?: Array<{ at: number; who: string; text: string }>;
}

interface Props {
  id: string;
  features: string[];
  onOpenMember: (tool: string, session: string, terminal: string) => void;
  onGone: () => void;
  onChanged: () => void;
}

const MODE: Record<string, string> = { lead: '分工', review: '写审', compare: '比稿' };
const STATE: Record<string, string> = { running: '进行中', paused: '已暂停', done: '已完成', stopped: '已停止', failed: '出错' };

function roleName(role: string): string {
  if (role === 'leader') return '领队';
  if (role === 'writer') return '写';
  if (role === 'reviewer') return '审';
  const m = /^member-(\d)$/.exec(role);
  return m ? `成员 ${m[1]}` : role;
}

/** A collaboration task: what it is to do, who is in it and how far they are, and telling it more. */
export function TeamView({ id, features, onOpenMember, onGone, onChanged }: Props) {
  const [team, setTeam] = useState<Team | null>(null);
  const [problem, setProblem] = useState('');
  const [menu, setMenu] = useState<MenuState | null>(null);

  const load = async () => {
    try {
      const res = await workAction<{ teams?: Team[] }>('teams');
      const found = (res.teams || []).find((t) => t.id === id);
      if (!found) onGone();
      else setTeam(found);
    } catch (err: any) {
      setProblem(err?.message || '读不到协作任务');
    }
  };

  useEffect(() => {
    setTeam(null);
    load();
    const timer = window.setInterval(load, 3000);
    return () => window.clearInterval(timer);
  }, [id]);

  const doWork = async (action: string, more: Record<string, unknown> = {}) => {
    setProblem('');
    try {
      await workAction(action, { team: id, ...more });
      await load();
      onChanged();
    } catch (err: any) {
      setProblem(err?.message || '没有完成');
    }
  };

  if (!team) return <main class="thread">{problem ? <div class="banner bad">{problem}</div> : <div class="pane-empty">正在读取协作任务…</div>}</main>;

  const running = team.state === 'running';
  const members = Object.entries(team.members || {});

  return (
    <main class="thread">
      <header class="thread-head">
        <div class="head-main">
          <span class="tool-mark team-mark">协</span>
          <h1 class="head-title" title={team.goal}>
            {team.goal || '协作任务'}
          </h1>
          <span class={`chip soft st-${team.state}`}>{STATE[team.state] || team.state}</span>
        </div>
        <div class="head-meta">
          <span class="chip soft">{MODE[team.mode] || team.mode}</span>
          <span class="chip soft">{team.dir}</span>
          {team.rounds ? (
            <span class="chip soft">
              第 {team.round || 1} / {team.rounds} 轮
            </span>
          ) : null}
        </div>
        <div class="head-actions">
          {running ? (
            <button type="button" class="btn quiet small" onClick={() => doWork('team_stop')}>
              暂停
            </button>
          ) : team.state !== 'done' ? (
            <button type="button" class="btn quiet small" onClick={() => doWork('team_resume')}>
              继续
            </button>
          ) : null}
          <button
            type="button"
            class="icon-btn"
            title="更多"
            onClick={(e) =>
              setMenu({
                x: (e as MouseEvent).clientX,
                y: (e as MouseEvent).clientY,
                items: [
                  {
                    label: '删除协作任务',
                    danger: true,
                    disabled: running ? '先暂停' : false,
                    run: () => window.confirm('删除这个协作任务？成员的对话记录会保留。') && doWork('team_remove').then(onGone),
                  },
                ],
              })
            }
          >
            <Dots />
          </button>
        </div>
      </header>

      {problem && <div class="banner bad">{problem}</div>}
      {team.note && <div class="banner">{team.note}</div>}

      <div class="team-body">
        <section class="team-sec">
          <h2>成员</h2>
          <div class="team-members">
            {members.map(([role, terminal]) => {
              const seat = team.member_sessions?.[role];
              const tool = seat?.tool || '';
              const waiting = (team.awaited || []).includes(role);
              const failed = team.failed?.[tool];
              return (
                <button
                  key={role}
                  type="button"
                  class="member"
                  disabled={!seat && !terminal}
                  onClick={() => onOpenMember(tool, seat?.id || '', terminal)}
                >
                  <span class="tool-mark" style={toolBadgeStyle(tool)}>
                    {toolMeta(tool).mono}
                  </span>
                  <span class="member-text">
                    <strong>{roleName(role)}</strong>
                    <span>
                      {toolLabel(tool)}
                      {failed ? ` · ${failed}` : waiting && running ? ' · 正在做' : ''}
                    </span>
                  </span>
                </button>
              );
            })}
          </div>
        </section>

        {team.mode === 'compare' && (team.candidates || []).length > 0 && (
          <section class="team-sec">
            <h2>各自的方案</h2>
            {(team.candidates || []).map((c) => (
              <div key={c.tool} class={`candidate${team.picked === c.tool ? ' on' : ''}`}>
                <div class="candidate-head">
                  <span class="tool-mark" style={toolBadgeStyle(c.tool)}>
                    {toolMeta(c.tool).mono}
                  </span>
                  <strong>{toolLabel(c.tool)}</strong>
                  <span class="grow" />
                  {features.includes('teams-v2') && (
                    <button type="button" class="btn quiet small" disabled={team.picked === c.tool} onClick={() => doWork('team_pick', { tool: c.tool })}>
                      {team.picked === c.tool ? '已选用' : '选用这一版'}
                    </button>
                  )}
                </div>
                {c.summary && <p class="candidate-sum">{c.summary}</p>}
              </div>
            ))}
          </section>
        )}

        <section class="team-sec">
          <h2>进展</h2>
          <ol class="team-log">
            {(team.log || [])
              .slice()
              .reverse()
              .map((step, i) => (
                <li key={i}>
                  <span class="log-who">{/^(leader|writer|reviewer|member-\d)$/.test(step.who) ? roleName(step.who) : step.who}</span>
                  <span class="log-text">{step.text}</span>
                  <span class="log-at" title={formatExactTime(step.at)}>
                    {formatRelativeTime(step.at)}
                  </span>
                </li>
              ))}
          </ol>
        </section>
      </div>

      {features.includes('teams-v2') && (
        <Composer
          draftKey={`rcli-draft:team:${id}`}
          placeholder={running ? '对整个协作补充要求' : '发新的要求，协作会接着做'}
          pictureDir=""
          busy={false}
          canStop={false}
          onSend={async (text) => {
            await workAction('team_say', { team: id, text });
            load();
            onChanged();
          }}
          onStop={async () => {}}
        />
      )}
      {menu && <ContextMenu {...menu} onClose={() => setMenu(null)} />}
    </main>
  );
}
