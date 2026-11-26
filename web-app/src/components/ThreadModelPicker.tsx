import { useEffect, useState } from 'preact/hooks';
import { changeModel } from '../runtime/actions';
import { fetchTeamModels } from '../runtime/api';
import type { Thread } from '../runtime/threads';
import type { ToolModelOption } from '../runtime/types';
import { Dialog } from './Dialog';

export function ThreadModelPicker({ thread, features, blocked, onChanged }: {
  thread: Thread; features: string[]; blocked: boolean; onChanged: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [list, setList] = useState<ToolModelOption[] | null>(null);
  const [selected, setSelected] = useState('');
  const [applying, setApplying] = useState(false);
  const [error, setError] = useState('');
  const current = thread.terminal?.model || '';
  useEffect(() => {
    if (!open) return;
    let active = true;
    setList(null);
    setError('');
    setSelected(current);
    fetchTeamModels().then((catalog) => {
      if (active) setList(catalog[thread.tool] || []);
    }).catch((e) => { if (active) setError(e.message || '读取模型失败'); });
    return () => { active = false; };
  }, [open, thread.tool]);

  if (!['chat', 'idle'].includes(thread.runtime) || !['codex', 'claude', 'agy'].includes(thread.tool))
    return current ? <span class="chip soft">{current}</span> : null;
  const supported = features.includes('teams-models') && (thread.runtime === 'idle' || thread.team || features.includes('chat-model'));
  const reason = !supported ? '请更新电脑端和中转后再选择模型' : !thread.session ? '等待工具分配会话编号' :
    blocked ? '请等本轮结束、待发送消息处理完后再切换' : '';
  const apply = async () => {
    if (reason || applying || selected === current) return;
    setApplying(true);
    setError('');
    const effort = thread.terminal?.effort || '';
    try {
      await changeModel(thread, selected, list?.find((m) => m.id === selected)?.efforts?.includes(effort) ? effort : '');
      setOpen(false);
      onChanged();
    } catch (e: any) {
      setError(e.message || '切换模型失败');
    } finally {
      setApplying(false);
    }
  };
  return <>
    <button class="chip model-picker" type="button" title={reason || '选择模型'} disabled={!!reason} onClick={() => setOpen(true)}>
      {current || '选择模型'} ▾
    </button>
    {open && <Dialog title="选择模型" onClose={() => !applying && setOpen(false)} foot={
      <button class="btn primary" disabled={!list?.length || !!reason || applying || current === selected} onClick={apply}>
        {applying ? '正在切换…' : '应用模型'}
      </button>
    }>
      {list?.length ? <label class="field"><span>在这段对话中使用</span>
        <select value={selected} onChange={(e) => setSelected(e.currentTarget.value)}>
          <option value="">工具默认</option>
          {current && !list.some((m) => m.id === current) && <option value={current}>{current}</option>}
          {list.filter((m) => m.id).map((m) => <option key={m.id} value={m.id}>{m.label || m.id}</option>)}
        </select>
      </label> : !error && <div class="muted">{list ? '电脑端没有返回可选模型' : '正在读取可选模型…'}</div>}
      {(error || reason) && <div class="banner">{error || reason}</div>}
    </Dialog>}
  </>;
}
