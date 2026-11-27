import { useEffect, useRef, useState } from 'preact/hooks';
import * as act from '../runtime/actions';
import { fetchTeamModels } from '../runtime/api';
import type { Thread } from '../runtime/threads';
import { toolBadgeStyle, toolLabel, toolMeta, type DeviceInfo, type ToolModelOption } from '../runtime/types';

const NEW_TOOLS = ['claude', 'codex', 'agy'];
// The models each tool offers, asked of the computer once a page (again after a failure).
let catalogAsked: Promise<Record<string, ToolModelOption[]>> | null = null;
const catalog = () => (catalogAsked ||= fetchTeamModels().catch((err) => { catalogAsked = null; throw err; }));

interface Props {
  thread: Thread;
  device: DeviceInfo | null;
  /** True while a turn, a question or messages waiting to go keep the model from being changed. */
  blocked: boolean;
  onChanged: () => void;
  /** A new conversation of another AI was begun from this one: its terminal. */
  onHandedTo: (terminal: string) => void;
  /** The full hand-over (a collaboration member is handed over inside its task). */
  onHandOff: () => void;
}

/** The button at the end of the box: the tool's mark and its model. Pressed, it lists the tool's models (one tap goes
 *  on with another) and the other AIs on the computer (one tap hands the conversation to a new conversation of one). */
export function ToolSwitch({ thread, device, blocked, onChanged, onHandedTo, onHandOff }: Props) {
  const [open, setOpen] = useState(false);
  const [list, setList] = useState<ToolModelOption[] | null>(null);
  const [going, setGoing] = useState('');
  const [problem, setProblem] = useState('');
  const host = useRef<HTMLDivElement | null>(null);
  const features = device?.features || [];
  const current = thread.terminal?.model || '';

  // the names of the models: at once where one of its own is chosen (the button names it), else when opened
  const wanted = open || (!!current && features.includes('teams-models'));
  useEffect(() => {
    if (!wanted || list) return;
    let active = true;
    catalog()
      .then((found) => active && setList(found[thread.tool] || []))
      .catch(() => active && open && setList([]));
    return () => { active = false; };
  }, [wanted, thread.tool]);
  useEffect(() => {
    if (!open) return;
    setProblem('');
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
  const reason = !canModel ? '请在运行这段对话的窗口切换模型' : !supported ? '请更新电脑端和中转后再选择模型' :
    !thread.session ? '等待工具分配会话编号' : blocked ? '这一轮结束、待发送消息处理完后再换模型' : '';
  const others = thread.project && thread.session && features.includes('brief') && features.includes('chat')
    ? NEW_TOOLS.filter((tool) => tool !== thread.tool && (device?.tools || []).includes(tool) && (tool !== 'agy' || features.includes('chat-agy')))
    : [];
  const label = (id: string) => (id ? list?.find((m) => m.id === id)?.label || id : '工具默认');
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
  const pick = (id: string) => {
    if (id === current) return setOpen(false);
    if (reason) return setProblem(reason);
    const effort = thread.terminal?.effort || '';
    run(`m:${id}`, async () => {
      await act.changeModel(thread, id, list?.find((m) => m.id === id)?.efforts?.includes(effort) ? effort : '');
      onChanged();
    });
  };
  const handTo = (tool: string) => {
    if (thread.team) {
      setOpen(false);
      onHandOff();
      return;
    }
    run(`t:${tool}`, async () => onHandedTo(await act.handToNew(thread, tool, '')));
  };

  const models = [{ id: '', label: '工具默认' } as ToolModelOption, ...(list || []).filter((m) => m.id)];
  if (current && list && !list.some((m) => m.id === current)) models.push({ id: current, label: current } as ToolModelOption);
  const meta = toolMeta(thread.tool);
  return (
    <div class={`tool-switch${open ? ' is-open' : ''}`} ref={host}>
      <button
        type="button"
        class="tool-switch-btn"
        aria-haspopup="menu"
        aria-expanded={open}
        title={`${toolLabel(thread.tool)} · ${current ? label(current) : '工具默认'}`}
        onClick={() => setOpen((v) => !v)}
      >
        <span class="tool-mark" style={toolBadgeStyle(thread.tool)}>{meta.mono}</span>
        <span class="tool-switch-label">{current ? label(current) : toolLabel(thread.tool)}</span>
        <svg viewBox="0 0 20 20" width="14" height="14" aria-hidden="true">
          <path d="M5.5 7.5 10 12l4.5-4.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
        </svg>
      </button>
      {open && (
        <div class="tool-switch-pop" role="menu">
          <div class="tsp-head">{toolLabel(thread.tool)}</div>
          {list === null ? (
            <div class="tsp-note">正在读取可选模型…</div>
          ) : (
            models.map((m) => (
              <button
                key={m.id || 'default'}
                type="button"
                role="menuitemradio"
                aria-checked={m.id === current}
                class={`tsp-row${m.id === current ? ' on' : ''}`}
                data-model={m.id}
                disabled={!!going}
                onClick={() => pick(m.id)}
              >
                <span class="tsp-label">{m.label || m.id}</span>
                <span class="tsp-end">{going === `m:${m.id}` ? '正在切换…' : m.id === current ? '✓' : ''}</span>
              </button>
            ))
          )}
          {reason && list !== null && <div class="tsp-note">{reason}</div>}
          {others.length > 0 && (
            <>
              <div class="tsp-head">转交给</div>
              {others.map((tool) => (
                <button key={tool} type="button" role="menuitem" class="tsp-row" disabled={!!going} onClick={() => handTo(tool)}>
                  <span class="tool-mark" style={toolBadgeStyle(tool)}>{toolMeta(tool).mono}</span>
                  <span class="tsp-label">{toolLabel(tool)}</span>
                  <span class="tsp-end">{going === `t:${tool}` ? '正在转交…' : left(tool)}</span>
                </button>
              ))}
            </>
          )}
          {problem && <div class="tsp-note bad">{problem}</div>}
        </div>
      )}
    </div>
  );
}
