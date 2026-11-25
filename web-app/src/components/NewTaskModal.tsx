import { useEffect, useState } from 'preact/hooks';
import { fetchTeamModels, sendTerminalAction } from '../runtime/api';
import { TOOL_CATALOG, toolLabel, type DeviceInfo, type ToolModelOption } from '../runtime/types';

interface Props {
  open: boolean;
  device: DeviceInfo | null;
  defaultDir: string;
  onClose: () => void;
  onCreated: (terminalId: string) => void;
}

export function NewTaskModal({ open, device, defaultDir, onClose, onCreated }: Props) {
  const workspaces = device?.workspaces || [];
  const tools = device?.tools && device.tools.length > 0 ? device.tools : ['claude', 'codex', 'shell'];
  const supportsWorktree = (device?.features || []).includes('worktree');
  const supportsParams = (device?.features || []).includes('chat-params');

  const [dir, setDir] = useState(defaultDir || workspaces[0] || '');
  const [tool, setTool] = useState(tools.includes('claude') ? 'claude' : tools[0] || 'shell');
  const [form, setForm] = useState<'chat' | 'terminal'>('chat');
  const [mode, setMode] = useState<'full' | 'edit' | 'read'>('full');
  const [model, setModel] = useState('');
  const [effort, setEffort] = useState('');
  const [modelCatalog, setModelCatalog] = useState<Record<string, ToolModelOption[]>>({});
  const [useWorktree, setUseWorktree] = useState(false);
  const [branch, setBranch] = useState('');
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');

  useEffect(() => {
    if (open && defaultDir) setDir(defaultDir);
  }, [open, defaultDir]);

  useEffect(() => {
    if (!open || !supportsParams || Object.keys(modelCatalog).length > 0) return;
    fetchTeamModels()
      .then((res) => setModelCatalog(res))
      .catch(() => {});
  }, [open, supportsParams]);

  useEffect(() => {
    setModel('');
    setEffort('');
  }, [tool]);

  if (!open) return null;

  const canChat = !!TOOL_CATALOG[tool]?.supportsChat;
  const effectiveForm = canChat ? form : 'terminal';
  const toolModels = modelCatalog[tool] || [];
  const selectedModelMeta = toolModels.find((m) => m.id === model);
  const efforts = selectedModelMeta?.efforts || (tool === 'codex' || tool === 'claude' ? ['low', 'medium', 'high'] : []);

  const submit = async (e: Event) => {
    e.preventDefault();
    const targetDir = dir || workspaces[0] || '';
    if (!targetDir) {
      setErr('请先添加一个项目文件夹');
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
      if (tool !== 'shell' && supportsParams) {
        if (mode) payload.mode = mode;
        if (model) payload.model = model;
        if (effort) payload.effort = effort;
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
          <h2>新建 AI 任务 / 终端</h2>
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
            <span>AI 工具（自动检测本机已安装）</span>
            <select value={tool} onChange={(e) => setTool((e.target as HTMLSelectElement).value)}>
              {tools.map((t) => (
                <option key={t} value={t}>
                  {toolLabel(t)}
                </option>
              ))}
            </select>
          </label>

          {canChat ? (
            <label class="field-label">
              <span>运行形态</span>
              <select value={form} onChange={(e) => setForm((e.target as HTMLSelectElement).value as 'chat' | 'terminal')}>
                <option value="chat">结构化对话（推荐）</option>
                <option value="terminal">完整交互终端</option>
              </select>
            </label>
          ) : (
            <label class="field-label">
              <span>运行形态</span>
              <input type="text" value="交互式终端" disabled />
            </label>
          )}
        </div>

        {tool !== 'shell' && supportsParams && (
          <div class="field-row">
            <label class="field-label">
              <span>权限 / 协作模式</span>
              <select value={mode} onChange={(e) => setMode((e.target as HTMLSelectElement).value as 'full' | 'edit' | 'read')}>
                <option value="full">默认 / 完整权限</option>
                <option value="edit">自动编辑文件</option>
                <option value="read">Plan 只读规划模式</option>
              </select>
            </label>

            <label class="field-label">
              <span>模型（可选）</span>
              {toolModels.length > 0 ? (
                <select value={model} onChange={(e) => setModel((e.target as HTMLSelectElement).value)}>
                  <option value="">跟随工具默认</option>
                  {toolModels.map((m) => (
                    <option key={m.id} value={m.id}>
                      {m.label || m.id}
                    </option>
                  ))}
                </select>
              ) : (
                <input
                  type="text"
                  placeholder="留空使用默认模型"
                  value={model}
                  onInput={(e) => setModel((e.target as HTMLInputElement).value)}
                />
              )}
            </label>
          </div>
        )}

        {tool !== 'shell' && supportsParams && efforts.length > 0 && (
          <label class="field-label">
            <span>推理强度（可选）</span>
            <select value={effort} onChange={(e) => setEffort((e.target as HTMLSelectElement).value)}>
              <option value="">默认</option>
              {efforts.map((ef) => (
                <option key={ef} value={ef}>
                  {ef}
                </option>
              ))}
            </select>
          </label>
        )}

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
