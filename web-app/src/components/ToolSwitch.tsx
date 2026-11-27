import { useEffect, useRef, useState } from 'preact/hooks';
import * as act from '../runtime/actions';
import { fetchTeamModels } from '../runtime/api';
import type { Thread } from '../runtime/threads';
import { toolBadgeStyle, toolLabel, toolMeta, toolShort, type DeviceInfo, type ToolModelOption } from '../runtime/types';

const NEW_TOOLS = ['claude', 'codex', 'agy'];
// The models each tool offers, asked of the computer once a page (again after a failure).
let catalogAsked: Promise<Record<string, ToolModelOption[]>> | null = null;
const catalog = () => (catalogAsked ||= fetchTeamModels().catch((err) => { catalogAsked = null; throw err; }));

export interface PendingSwitch {
  tool: string;
  model: string;
  effort: string;
}

interface Props {
  thread: Thread;
  device: DeviceInfo | null;
  /** True while a turn, a question or messages waiting to go keep the model or AI from being changed. */
  blocked: boolean;
  /** Staged in-place AI switch for the next message, or null. */
  pending: PendingSwitch | null;
  onStageSwitch: (next: PendingSwitch | null) => void;
  onChanged: () => void;
  /** A new conversation of another AI was begun from this one (fallback when `handover` is unavailable). */
  onHandedTo: (terminal: string) => void;
  /** The full hand-over (a collaboration member is handed over inside its task). */
  onHandOff: () => void;
}

/** The button at the end of the composer: a row of available AIs across the top of the popover,
 *  and only the selected AI's models listed below. */
export function ToolSwitch({ thread, device, blocked, pending, onStageSwitch, onChanged, onHandedTo, onHandOff }: Props) {
  const [open, setOpen] = useState(false);
  const [allModels, setAllModels] = useState<Record<string, ToolModelOption[]> | null>(null);
  const [viewTool, setViewTool] = useState(pending?.tool || thread.tool);
  const [going, setGoing] = useState('');
  const [problem, setProblem] = useState('');
  const host = useRef<HTMLDivElement | null>(null);
  const features = device?.features || [];
  const current = thread.terminal?.model || '';

  const wanted = open || ((!!current || !!pending?.model) && features.includes('teams-models'));
  useEffect(() => {
    if (!wanted || allModels) return;
    let active = true;
    catalog()
      .then((found) => active && setAllModels(found || {}))
      .catch(() => active && open && setAllModels({}));
    return () => { active = false; };
  }, [wanted]);

  useEffect(() => {
    if (!open) return;
    const outside = (e: MouseEvent) => host.current && !host.current.contains(e.target as Node) && setOpen(false);
    const key = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false);
    window.addEventListener('mousedown', outside);
    window.addEventListener('keydown', key);
    return () => {
      window.removeEventListener('mousedown', outside);
      window.removeEventListener('keydown', key);
    };
  }, [open]);

  if (!NEW_TOOLS.includes(thread.tool)) return null;
  const canModel = ['chat', 'idle'].includes(thread.runtime);
  const supported = features.includes('teams-models') && (thread.runtime === 'idle' || !!thread.team || features.includes('chat-model'));
  const modelReason = !canModel ? '请在运行这段对话的窗口切换模型' : !supported ? '请更新电脑端和中转后再选择模型' :
    !thread.session ? '等待工具分配会话编号' : blocked ? '这一轮结束、待发送消息处理完后再切换' : '';

  const canSwitchAi = Boolean(thread.project && features.includes('chat') && (features.includes('handover') || features.includes('brief')));
  const availableTools = canSwitchAi
    ? NEW_TOOLS.filter((tool) => tool === thread.tool || ((device?.tools || []).includes(tool) && (tool !== 'agy' || features.includes('chat-agy'))))
    : [thread.tool];
  const activeTool = availableTools.includes(viewTool) ? viewTool : thread.tool;
  const isCurrentTool = activeTool === thread.tool;
  const list = allModels ? (allModels[activeTool] || []) : null;

  const labelOf = (tool: string, id: string) => (id ? allModels?.[tool]?.find((m) => m.id === id)?.label || id : '工具默认');
  const left = (tool: string) => {
    const q = (device?.quotas || []).find((one) => one.tool === tool);
    const all = q?.windows || [], main = all.filter((w) => !w.group), pool = main.length ? main : all;
    return pool.length ? `剩余 ${Math.round(Math.min(...pool.map((w) => Math.max(0, Math.min(100, 100 - (Number(w.used) || 0))))))}%` : '';
  };

  const run = async (key: string, work: () => Promise<void>) => {
    if (going) return;
    setGoing(key);
    setProblem('');
    try {
      await work();
      setOpen(false);
    } catch (err: any) {
      setProblem(err?.message || '没有完成');
    } finally {
      setGoing('');
    }
  };

  const pickSameTool = (id: string) => {
    if (id === current) {
      if (pending) onStageSwitch(null);
      return setOpen(false);
    }
    if (modelReason) return setProblem(modelReason);
    const effort = thread.terminal?.effort || '';
    run(`m:${id}`, async () => {
      if (pending) onStageSwitch(null);
      await act.changeModel(thread, id, list?.find((m) => m.id === id)?.efforts?.includes(effort) ? effort : '');
      onChanged();
    });
  };

  const pickOtherTool = (id: string) => {
    if (thread.team) {
      setOpen(false);
      onHandOff();
      return;
    }
    if (!thread.session) return setProblem('发出第一条消息后才能换 AI');
    if (blocked) return setProblem('这一轮结束、待发送消息处理完后再换 AI');
    if (features.includes('handover')) {
      onStageSwitch({ tool: activeTool, model: id, effort: '' });
      setOpen(false);
      return;
    }
    run(`t:${activeTool}`, async () => onHandedTo(await act.handToNew(thread, activeTool, '')));
  };

  const models = [{ id: '', label: '工具默认' } as ToolModelOption, ...(list || []).filter((m) => m.id)];
  if (isCurrentTool && current && list && !list.some((m) => m.id === current)) {
    models.push({ id: current, label: current } as ToolModelOption);
  }

  const shownTool = pending?.tool || thread.tool;
  const shownModel = pending ? pending.model : current;
  const meta = toolMeta(shownTool);
  const btnText = pending
    ? `${shownModel ? labelOf(shownTool, shownModel) : toolLabel(shownTool)} · 接续`
    : (current ? labelOf(thread.tool, current) : toolLabel(thread.tool));
  const quotaText = left(activeTool);

  return (
    <div class={`tool-switch${open ? ' is-open' : ''}`} ref={host}>
      <button
        type="button"
        class={`tool-switch-btn${pending ? ' is-staged' : ''}`}
        aria-haspopup="menu"
        aria-expanded={open}
        title={`${toolLabel(shownTool)} · ${shownModel ? labelOf(shownTool, shownModel) : '工具默认'}`}
        onClick={() =>
          setOpen((v) => {
            const next = !v;
            if (next) {
              setViewTool(pending?.tool || thread.tool);
              setProblem('');
            }
            return next;
          })
        }
      >
        <span class="tool-mark" style={toolBadgeStyle(shownTool)}>{meta.mono}</span>
        <span class="tool-switch-label">{btnText}</span>
        <svg viewBox="0 0 20 20" width="14" height="14" aria-hidden="true">
          <path d="M5.5 7.5 10 12l4.5-4.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
        </svg>
      </button>
      {open && (
        <div class="tool-switch-pop" role="menu">
          {availableTools.length > 1 && (
            <div class="tsp-tools" role="tablist" aria-label="选择 AI">
              {availableTools.map((tool) => {
                const tm = toolMeta(tool);
                const on = tool === activeTool;
                return (
                  <button
                    key={tool}
                    type="button"
                    role="tab"
                    aria-selected={on}
                    data-tool={tool}
                    class={`tsp-tool-tab${on ? ' on' : ''}`}
                    disabled={!!going}
                    onClick={() => { setViewTool(tool); setProblem(''); }}
                  >
                    <span class="tool-mark" style={toolBadgeStyle(tool)}>{tm.mono}</span>
                    <span>{toolShort(tool)}</span>
                  </button>
                );
              })}
            </div>
          )}
          <div class="tsp-head">
            <span>{toolLabel(activeTool)}</span>
            {quotaText && <span class="tsp-quota">{quotaText}</span>}
          </div>
          {!isCurrentTool && thread.team ? (
            <button type="button" role="menuitem" class="tsp-row" onClick={() => pickOtherTool('')}>
              <span class="tool-mark" style={toolBadgeStyle(activeTool)}>{toolMeta(activeTool).mono}</span>
              <span class="tsp-label">在协作中交给 {toolLabel(activeTool)}</span>
            </button>
          ) : list === null ? (
            <div class="tsp-note">正在读取可选模型…</div>
          ) : (
            models.map((m) => {
              const checked = isCurrentTool
                ? !pending && m.id === current
                : pending?.tool === activeTool && pending.model === m.id;
              return (
                <button
                  key={`${activeTool}:${m.id || 'default'}`}
                  type="button"
                  role="menuitemradio"
                  aria-checked={checked}
                  class={`tsp-row${checked ? ' on' : ''}`}
                  data-model={m.id}
                  disabled={!!going}
                  onClick={() => (isCurrentTool ? pickSameTool(m.id) : pickOtherTool(m.id))}
                >
                  <span class="tsp-label">{m.label || m.id}</span>
                  <span class="tsp-end">{going === `m:${m.id}` || going === `t:${activeTool}` ? '正在切换…' : checked ? '✓' : ''}</span>
                </button>
              );
            })
          )}
          {isCurrentTool && modelReason && list !== null && <div class="tsp-note">{modelReason}</div>}
          {!isCurrentTool && !thread.team && list !== null && (
            <div class="tsp-note">
              {!thread.session
                ? '发出第一条消息后才能换 AI'
                : blocked
                  ? '这一轮结束、待发送消息处理完后再换 AI'
                  : features.includes('handover')
                    ? `选择模型后，下一条消息由 ${toolLabel(activeTool)} 在当前对话继续`
                    : `将在新对话中交给 ${toolLabel(activeTool)}`}
            </div>
          )}
          {problem && <div class="tsp-note bad">{problem}</div>}
        </div>
      )}
    </div>
  );
}
