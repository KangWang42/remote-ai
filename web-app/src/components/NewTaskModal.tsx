import { useState } from 'preact/hooks';
import { sendTerminalAction } from '../runtime/api';
import type { DeviceInfo } from '../runtime/types';

interface Props {
  open: boolean;
  device: DeviceInfo | null;
  defaultDir: string;
  onClose: () => void;
  onCreated: (terminalId: string) => void;
}

export function NewTaskModal({ open, device, defaultDir, onClose, onCreated }: Props) {
  const workspaces = device?.workspaces || [];
  const tools = device?.tools || ['claude', 'codex', 'shell'];
  const supportsWorktree = (device?.features || []).includes('worktree');

  const [dir, setDir] = useState(defaultDir || workspaces[0] || '');
  const [tool, setTool] = useState(tools.includes('claude') ? 'claude' : tools[0] || 'shell');
  const [form, setForm] = useState<'chat' | 'terminal'>('chat');
  const [useWorktree, setUseWorktree] = useState(false);
  const [branch, setBranch] = useState('');
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');

  if (!open) return null;

  const canChat = tool === 'claude' || tool === 'codex' || tool === 'agy';
  const effectiveForm = canChat ? form : 'terminal';

  const submit = async (e: Event) => {
    e.preventDefault();
    const targetDir = dir || workspaces[0] || '';
    if (!targetDir) {
      setErr('请先在电脑端添加项目文件夹');
      return;
    }
    setBusy(true);
    setErr('');
    try {
      const payload: Record<string, unknown> = {
        action: 'start',
        tool,
        dir: targetDir,
        history: false,
      };
      if (effectiveForm === 'chat') {
        payload.form = 'chat';
        if (text.trim()) payload.text = text.trim();
      }
      if (useWorktree && supportsWorktree) {
        payload.worktree = true;
        if (branch.trim()) payload.branch = branch.trim();
      }
      const res = await sendTerminalAction(payload);
      if (res?.terminal) onCreated(res.terminal);
      setText('');
      setBranch('');
      onClose();
    } catch (error: any) {
      setErr(error?.message || '启动失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div class="modal-backdrop" onClick={onClose}>
      <form class="modal-card" onClick={(e) => e.stopPropagation()} onSubmit={submit}>
        <div class="modal-head">
          <h2>新建任务 / 对话</h2>
          <button type="button" class="icon-btn" onClick={onClose}>
            ✕
          </button>
        </div>

        <label class="field-label">
          <span>项目工作区</span>
          <select value={dir || workspaces[0] || ''} onChange={(e) => setDir((e.target as HTMLSelectElement).value)}>
            {workspaces.map((w) => (
              <option key={w} value={w}>
                {w}
              </option>
            ))}
          </select>
        </label>

        <div class="field-row">
          <label class="field-label">
            <span>工具</span>
            <select value={tool} onChange={(e) => setTool((e.target as HTMLSelectElement).value)}>
              {tools.map((t) => (
                <option key={t} value={t}>
                  {t === 'claude' ? 'Claude Code' : t === 'codex' ? 'Codex' : t === 'agy' ? 'Antigravity CLI' : 'PowerShell 终端'}
                </option>
              ))}
            </select>
          </label>

          {canChat && (
            <label class="field-label">
              <span>运行形态</span>
              <select value={form} onChange={(e) => setForm((e.target as HTMLSelectElement).value as 'chat' | 'terminal')}>
                <option value="chat">结构化对话（推荐）</option>
                <option value="terminal">完整交互终端</option>
              </select>
            </label>
          )}
        </div>

        {supportsWorktree && (
          <div class="wt-opt-box">
            <label class="checkbox-row">
              <input type="checkbox" checked={useWorktree} onChange={(e) => setUseWorktree((e.target as HTMLInputElement).checked)} />
              <span>在独立 Git Worktree 中隔离运行（并行改代码互不干扰）</span>
            </label>
            {useWorktree && (
              <input
                type="text"
                placeholder="自定义分支名（留空自动生成 remote/<id>）"
                value={branch}
                onInput={(e) => setBranch((e.target as HTMLInputElement).value)}
              />
            )}
          </div>
        )}

        {effectiveForm === 'chat' && (
          <label class="field-label">
            <span>首条指令（可选）</span>
            <textarea
              rows={3}
              placeholder="描述你想让 AI 完成的任务…"
              value={text}
              onInput={(e) => setText((e.target as HTMLTextAreaElement).value)}
            />
          </label>
        )}

        {err && <div class="panel-feedback is-error">{err}</div>}

        <div class="modal-foot">
          <button type="button" class="ghost-btn" onClick={onClose}>
            取消
          </button>
          <button type="submit" class="primary-btn" disabled={busy}>
            {busy ? '启动中…' : '立即启动'}
          </button>
        </div>
      </form>
    </div>
  );
}
