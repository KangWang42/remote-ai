import { useEffect, useRef, useState } from 'preact/hooks';
import * as act from '../runtime/actions';
import { fetchTeamModels, opId, workAction } from '../runtime/api';
import { TOOL_CATALOG, toolBadgeStyle, toolLabel, toolMeta, type DeviceInfo, type ToolModelOption } from '../runtime/types';
import { SelectPopover } from './SelectPopover';

interface Props {
  device: DeviceInfo | null;
  dir: string;
  onCancel: () => void;
  onAddProject: () => void;
  onStarted: (terminal: string) => void;
  onTeamStarted: (team: string) => void;
}

type TeamMode = 'lead' | 'review' | 'compare';
const ADD = '\u0000add';
const CHAT_TOOLS = ['claude', 'codex', 'agy'];

/**
 * Begins a conversation: what to do is the main thing on the page; where (project), with which AI and how widely
 * it may work sit on one line above it. An AI that has a chat mode is begun as a chat (it can be shown on its own
 * screen later); one that has none runs on its screen. "多个 AI 协作" begins a collaboration task instead.
 */
export function NewThread({ device, dir: initialDir, onCancel, onAddProject, onStarted, onTeamStarted }: Props) {
  const features = device?.features || [];
  const workspaces = device?.workspaces || [];
  const tools = (device?.tools || []).filter((t) => t !== 'shell');
  const chatTools = tools.filter((t) => CHAT_TOOLS.includes(t) && features.includes('chat') && (t !== 'agy' || features.includes('chat-agy')));
  const params = features.includes('chat-params');
  const teams = features.includes('teams') && chatTools.length >= 2;

  const [dir, setDir] = useState(initialDir || workspaces[0] || '');
  const [tool, setTool] = useState(() => (tools.includes('claude') ? 'claude' : tools[0] || ''));
  const [text, setText] = useState('');
  const [model, setModel] = useState('');
  const [effort, setEffort] = useState('');
  const [mode, setMode] = useState<'full' | 'edit' | 'read'>('full');
  const [worktree, setWorktree] = useState(false);
  const [branch, setBranch] = useState('');
  const [catalog, setCatalog] = useState<Record<string, ToolModelOption[]>>({});
  const [together, setTogether] = useState(false);
  const [teamMode, setTeamMode] = useState<TeamMode>('lead');
  const [leader, setLeader] = useState(chatTools[0] || '');
  const [members, setMembers] = useState<string[]>(chatTools.slice(1, 2));
  const [writer, setWriter] = useState(chatTools[0] || '');
  const [reviewer, setReviewer] = useState(chatTools[1] || chatTools[0] || '');
  const [racers, setRacers] = useState<string[]>(chatTools.slice(0, 2));
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState('');
  const box = useRef<HTMLTextAreaElement | null>(null);

  useEffect(() => {
    if (initialDir) setDir(initialDir);
  }, [initialDir]);
  useEffect(() => {
    if (!dir && workspaces[0]) setDir(workspaces[0]);
  }, [workspaces.join('|')]);
  useEffect(() => {
    box.current?.focus();
  }, []);
  useEffect(() => {
    if (!params) return;
    fetchTeamModels()
      .then(setCatalog)
      .catch(() => {});
  }, [params]);
  useEffect(() => {
    setModel('');
    setEffort('');
  }, [tool]);
  useEffect(() => {
    const el = box.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(Math.max(el.scrollHeight, 120), 360)}px`;
  }, [text]);

  const asChat = chatTools.includes(tool);
  const models = catalog[tool] || [];
  const efforts = models.find((m) => m.id === model)?.efforts || (tool === 'claude' || tool === 'codex' ? ['low', 'medium', 'high'] : []);

  const start = async () => {
    if (busy) return;
    if (!dir) {
      setProblem('先选一个项目');
      return;
    }
    setBusy(true);
    setProblem('');
    try {
      if (together) {
        const goal = text.trim();
        if (!goal) throw new Error('写下要几个 AI 一起完成什么');
        const chosen =
          teamMode === 'review' ? [writer, reviewer] : teamMode === 'lead' ? [leader, ...members.filter((m) => m !== leader)] : racers;
        if (teamMode === 'lead' && chosen.length < 2) throw new Error('至少选一个执行成员');
        if (teamMode === 'compare' && chosen.length < 2) throw new Error('比稿至少选 2 个 AI');
        const item = { id: opId(), mode: teamMode, dir, goal, tools: chosen, rounds: teamMode === 'compare' ? 1 : 3 };
        const made = await workAction<{ team?: { id: string } }>('team_start', { item });
        onTeamStarted(made.team?.id || item.id);
        return;
      }
      if (!tool) throw new Error('这台电脑上还没有可用的 AI 工具');
      const terminal = await act.begin({
        tool,
        dir,
        text: asChat ? text.trim() : '',
        screen: !asChat,
        ...(params ? { model, effort, mode } : {}),
        worktree: worktree && features.includes('worktree'),
        branch: branch.trim(),
      });
      onStarted(terminal);
    } catch (err: any) {
      setProblem(err?.message || '没有开始');
      setBusy(false);
    }
  };

  const projectOptions = [
    ...workspaces.map((w) => ({ value: w, label: w, sub: device?.projects?.find((p) => p.name === w)?.path })),
    { value: ADD, label: '添加文件夹…' },
  ];
  const toolOptions = tools.map((t) => ({
    value: t,
    label: toolLabel(t),
    sub: chatTools.includes(t) ? undefined : '在终端里运行',
    badge: toolMeta(t).mono,
    badgeStyle: toolBadgeStyle(t),
  }));

  const toggleIn = (list: string[], t: string, set: (v: string[]) => void, most: number) =>
    set(list.includes(t) ? list.filter((x) => x !== t) : [...list, t].slice(-most));

  return (
    <main class="new-page">
      <div class="new-inner">
        <h1 class="new-title">{together ? '几个 AI 一起做' : '新建对话'}</h1>

        <div class="new-line">
          <label class="new-field">
            <span>项目</span>
            {workspaces.length ? (
              <SelectPopover
                value={dir}
                options={projectOptions}
                onChange={(v) => (v === ADD ? onAddProject() : setDir(v))}
              />
            ) : (
              <button type="button" class="btn quiet" onClick={onAddProject}>
                添加项目文件夹
              </button>
            )}
          </label>
          {!together && (
            <label class="new-field">
              <span>AI</span>
              <SelectPopover value={tool} options={toolOptions} onChange={setTool} />
            </label>
          )}
          {teams && (
            <div class="new-field">
              <span>方式</span>
              <div class="seg">
                <button type="button" class={!together ? 'on' : ''} onClick={() => setTogether(false)}>
                  一个 AI
                </button>
                <button type="button" class={together ? 'on' : ''} onClick={() => setTogether(true)}>
                  多个 AI 协作
                </button>
              </div>
            </div>
          )}
        </div>

        {together && (
          <div class="team-plan">
            <div class="seg">
              {(
                [
                  ['lead', '分工', '领队拆分任务，成员各做一部分'],
                  ['review', '写审', '一个写，一个审，轮流改到通过'],
                  ['compare', '比稿', '几个 AI 各做一版，比较后选用'],
                ] as Array<[TeamMode, string, string]>
              ).map(([id, label, hint]) => (
                <button key={id} type="button" class={teamMode === id ? 'on' : ''} title={hint} onClick={() => setTeamMode(id)}>
                  {label}
                </button>
              ))}
            </div>
            {teamMode === 'lead' && (
              <div class="team-row">
                <span class="team-k">领队</span>
                <ToolChips tools={chatTools} on={[leader]} pick={(t) => setLeader(t)} />
                <span class="team-k">成员</span>
                <ToolChips
                  tools={chatTools.filter((t) => t !== leader)}
                  on={members}
                  pick={(t) => toggleIn(members, t, setMembers, 3)}
                />
              </div>
            )}
            {teamMode === 'review' && (
              <div class="team-row">
                <span class="team-k">写</span>
                <ToolChips tools={chatTools} on={[writer]} pick={setWriter} />
                <span class="team-k">审</span>
                <ToolChips tools={chatTools} on={[reviewer]} pick={setReviewer} />
              </div>
            )}
            {teamMode === 'compare' && (
              <div class="team-row">
                <span class="team-k">参与</span>
                <ToolChips tools={chatTools} on={racers} pick={(t) => toggleIn(racers, t, setRacers, 3)} />
              </div>
            )}
          </div>
        )}

        <form
          class="new-box"
          onSubmit={(e) => {
            e.preventDefault();
            start();
          }}
        >
          {asChat || together ? (
            <textarea
              ref={box}
              value={text}
              placeholder={together ? '要一起完成什么' : `要 ${toolLabel(tool)} 做什么（可以先不写，直接开始）`}
              onInput={(e) => setText((e.target as HTMLTextAreaElement).value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                  e.preventDefault();
                  start();
                }
              }}
            />
          ) : (
            <div class="new-screen-note">{toolLabel(tool)} 没有对话方式，会在它自己的终端界面里开始。</div>
          )}

          {!together && (
            <div class="new-opts">
              {params && (
                <div class="seg small" title="它能改动多少">
                  {(
                    [
                      ['full', '完整权限'],
                      ['edit', '自动改文件'],
                      ['read', '只读'],
                    ] as const
                  ).map(([id, label]) => (
                    <button key={id} type="button" class={mode === id ? 'on' : ''} onClick={() => setMode(id)}>
                      {label}
                    </button>
                  ))}
                </div>
              )}
              {params && models.length > 0 && (
                <SelectPopover
                  compact
                  value={model}
                  options={[{ value: '', label: '默认模型' }, ...models.map((m) => ({ value: m.id, label: m.label || m.id }))]}
                  onChange={setModel}
                />
              )}
              {params && efforts.length > 0 && (
                <SelectPopover
                  compact
                  value={effort}
                  options={[{ value: '', label: '默认思考深度' }, ...efforts.map((e) => ({ value: e, label: `思考 ${e}` }))]}
                  onChange={setEffort}
                />
              )}
              {features.includes('worktree') && (
                <label class="check" title="在独立的 Git 工作区里改动，不影响项目目录，做完再合并">
                  <input type="checkbox" checked={worktree} onChange={() => setWorktree((v) => !v)} />
                  <span>独立工作区</span>
                </label>
              )}
              {worktree && (
                <input
                  class="new-branch"
                  type="text"
                  placeholder="分支名（可不填）"
                  value={branch}
                  onInput={(e) => setBranch((e.target as HTMLInputElement).value)}
                />
              )}
            </div>
          )}

          {problem && <div class="banner bad">{problem}</div>}

          <div class="new-foot">
            <span class="muted small">{asChat || together ? 'Ctrl+Enter 开始' : ''}</span>
            <span class="grow" />
            <button type="button" class="btn quiet" onClick={onCancel}>
              取消
            </button>
            <button type="submit" class="btn primary" disabled={busy || !dir || (!together && !tool)}>
              {busy ? '正在开始…' : '开始'}
            </button>
          </div>
        </form>
      </div>
    </main>
  );
}

function ToolChips({ tools, on, pick }: { tools: string[]; on: string[]; pick: (t: string) => void }) {
  return (
    <span class="chips">
      {tools.map((t) => (
        <button key={t} type="button" class={`chip${on.includes(t) ? ' on' : ''}`} onClick={() => pick(t)}>
          <span class="tool-mark tiny" style={toolBadgeStyle(t)}>
            {TOOL_CATALOG[t]?.mono || t}
          </span>
          {toolLabel(t)}
        </button>
      ))}
    </span>
  );
}
