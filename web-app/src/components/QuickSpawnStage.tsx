import { useEffect, useState } from 'preact/hooks';
import { fetchTeamModels, sendTerminalAction } from '../runtime/api';
import {
  TOOL_CATALOG,
  toolBadgeStyle,
  toolLabel,
  toolMeta,
  type DeviceInfo,
  type ToolModelOption,
} from '../runtime/types';
import { SelectPopover } from './SelectPopover';

interface Props {
  device: DeviceInfo | null;
  defaultDir: string;
  contextTitle?: string;
  onBack?: () => void;
  onAddProject: () => void;
  onCreated: (terminalId: string) => void;
}

/**
 * Contextual, non-blocking inline AI launcher rendered directly inside the center workspace.
 * Lets users spawn a new AI right inside any conversation's project with visual tool cards,
 * segmented mode/view toggles, optional Git Worktree isolation, and an immediate prompt box.
 */
export function QuickSpawnStage({
  device,
  defaultDir,
  contextTitle,
  onBack,
  onAddProject,
  onCreated,
}: Props) {
  const workspaces = device?.workspaces || [];
  const tools = device?.tools && device.tools.length > 0 ? device.tools : ['claude', 'codex', 'agy', 'shell'];
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
    if (defaultDir) setDir(defaultDir);
    else if (!dir && workspaces[0]) setDir(workspaces[0]);
  }, [defaultDir, workspaces.join('|')]);

  useEffect(() => {
    if (!supportsParams || Object.keys(modelCatalog).length > 0) return;
    fetchTeamModels()
      .then((res) => setModelCatalog(res))
      .catch(() => {});
  }, [supportsParams]);

  useEffect(() => {
    setModel('');
    setEffort('');
  }, [tool]);

  const canChat = !!TOOL_CATALOG[tool]?.supportsChat;
  const effectiveForm = canChat ? form : 'terminal';
  const toolModels = modelCatalog[tool] || [];
  const selectedModelMeta = toolModels.find((m) => m.id === model);
  const efforts = selectedModelMeta?.efforts || (tool === 'codex' || tool === 'claude' ? ['low', 'medium', 'high'] : []);

  const workspaceOptions = workspaces.map((w) => ({ value: w, label: w }));
  const modelOptions = [
    { value: '', label: '跟随工具默认' },
    ...toolModels.map((m) => ({ value: m.id, label: m.label || m.id })),
  ];
  const effortOptions = [
    { value: '', label: '默认' },
    ...efforts.map((ef) => ({ value: ef, label: ef })),
  ];

  const submit = async (e?: Event) => {
    e?.preventDefault();
    const targetDir = dir || workspaces[0] || '';
    if (!targetDir) {
      setErr('请先添加一个项目文件夹');
      return;
    }
    if (busy) return;
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
      if (res?.terminal) {
        setText('');
        onCreated(res.terminal);
      } else {
        setErr('未返回新会话编号');
      }
    } catch (error: any) {
      setErr(error?.message || '启动失败');
    } finally {
      setBusy(false);
    }
  };

  const activeMeta = toolMeta(tool);

  return (
    <main class="main-pane spawn-stage">
      <header class="pane-head">
        <div class="pane-title-group">
          {onBack && (
            <button type="button" class="ghost-btn compact" onClick={onBack}>
              ← {contextTitle ? `返回「${contextTitle.slice(0, 14)}」` : '返回'}
            </button>
          )}
          <span class="tool-squircle sm" style={toolBadgeStyle(tool)}>
            {activeMeta.mono}
          </span>
          <h1 class="pane-title">{dir ? `在「${dir}」下新建 AI` : '新建 AI 对话'}</h1>
        </div>
        <div class="pane-actions">
          {workspaces.length > 0 ? (
            <SelectPopover
              compact
              value={dir || workspaces[0]}
              options={workspaceOptions}
              onChange={setDir}
            />
          ) : (
            <button type="button" class="primary-btn compact" onClick={onAddProject}>
              + 添加项目文件夹
            </button>
          )}
        </div>
      </header>

      <form class="spawn-scroll" onSubmit={submit}>
        <div class="spawn-inner">
          <div class="spawn-hero">
            <h2>选择 AI 工具开始协作</h2>
            <p>
              {dir
                ? `新会话将直接运行在项目 ${dir} 中，可随时与当前其他对话并行工作，并同步到手机端。`
                : '请先添加或选择一个项目文件夹，即可启动本地 AI 编程工具。'}
            </p>
          </div>

          <section class="spawn-section">
            <div class="group-title">本地 AI 工具</div>
            <div class="tool-grid">
              {tools.map((t) => {
                const m = toolMeta(t);
                const selected = tool === t;
                return (
                  <button
                    key={t}
                    type="button"
                    class={`tool-tile${selected ? ' active' : ''}`}
                    onClick={() => setTool(t)}
                  >
                    <span class="tool-squircle" style={toolBadgeStyle(t)}>
                      {m.mono}
                    </span>
                    <span class="tool-tile-text">
                      <strong>{toolLabel(t)}</strong>
                      <span>{m.supportsChat ? '对话 / 终端' : '交互终端'}</span>
                    </span>
                    {selected && <span class="tool-tile-check">✓</span>}
                  </button>
                );
              })}
            </div>
          </section>

          {tool !== 'shell' && (
            <section class="spawn-section">
              <div class="group-title">运行方式与隔离设置</div>
              <div class="card-group spawn-options">
                {canChat && (
                  <div class="opt-row">
                    <div class="opt-label">
                      <strong>交互界面</strong>
                      <span>结构化对话适合阅读代码与工具卡片</span>
                    </div>
                    <div class="seg-mini">
                      <button type="button" class={form === 'chat' ? 'active' : ''} onClick={() => setForm('chat')}>
                        结构化对话
                      </button>
                      <button type="button" class={form === 'terminal' ? 'active' : ''} onClick={() => setForm('terminal')}>
                        完整终端
                      </button>
                    </div>
                  </div>
                )}

                {supportsParams && (
                  <div class="opt-row">
                    <div class="opt-label">
                      <strong>操作权限</strong>
                      <span>控制该 AI 对工作区文件的修改范围</span>
                    </div>
                    <div class="seg-mini">
                      <button type="button" class={mode === 'full' ? 'active' : ''} onClick={() => setMode('full')}>
                        默认
                      </button>
                      <button type="button" class={mode === 'edit' ? 'active' : ''} onClick={() => setMode('edit')}>
                        自动编辑
                      </button>
                      <button type="button" class={mode === 'read' ? 'active' : ''} onClick={() => setMode('read')}>
                        Plan 只读
                      </button>
                    </div>
                  </div>
                )}

                {supportsParams && (toolModels.length > 0 || efforts.length > 0) && (
                  <div class="opt-row wrap-row">
                    <div class="opt-field">
                      <span class="opt-field-lbl">模型</span>
                      <SelectPopover compact value={model} options={modelOptions} onChange={setModel} />
                    </div>
                    {efforts.length > 0 && (
                      <div class="opt-field">
                        <span class="opt-field-lbl">思考深度</span>
                        <SelectPopover compact value={effort} options={effortOptions} onChange={setEffort} />
                      </div>
                    )}
                  </div>
                )}

                {supportsWorktree && (
                  <div class="opt-row col-row">
                    <div class="opt-row-top">
                      <div class="opt-label">
                        <strong>独立 Git Worktree 隔离分支</strong>
                        <span>多个 AI 并行修改同一仓库时互不冲突，完成后可一键合并</span>
                      </div>
                      <button
                        type="button"
                        role="switch"
                        aria-checked={useWorktree}
                        class={`toggle-pill${useWorktree ? ' on' : ''}`}
                        onClick={() => setUseWorktree((v) => !v)}
                      >
                        <span class="toggle-knob" />
                      </button>
                    </div>
                    {useWorktree && (
                      <input
                        type="text"
                        class="spawn-branch-input"
                        placeholder="分支名（留空自动生成 remote-ai/...）"
                        value={branch}
                        onInput={(e) => setBranch((e.target as HTMLInputElement).value)}
                      />
                    )}
                  </div>
                )}
              </div>
            </section>
          )}

          {err && <div class="panel-feedback is-error">{err}</div>}

          <div class="composer-box spawn-composer">
            {effectiveForm === 'chat' ? (
              <textarea
                rows={3}
                placeholder={`输入要交给 ${toolLabel(tool)} 的任务指令（可留空直接启动），Ctrl+Enter 立即开始…`}
                value={text}
                onInput={(e) => setText((e.target as HTMLTextAreaElement).value)}
                onKeyDown={(e) => {
                  if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') submit(e);
                }}
              />
            ) : (
              <div class="spawn-term-hint">将以原生交互终端模式启动 {toolLabel(tool)}，启动后可直接在终端内输入操作。</div>
            )}
            <div class="composer-bar">
              <span class="composer-hint">
                {toolLabel(tool)} · {dir || '未选项目'}
                {useWorktree ? ' · 独立 Worktree' : ''}
              </span>
              <div class="composer-btns">
                {onBack && (
                  <button type="button" class="ghost-btn compact" onClick={onBack}>
                    取消
                  </button>
                )}
                <button type="submit" class="primary-btn compact" disabled={busy || !dir}>
                  {busy ? '正在启动…' : '立即启动 AI'}
                </button>
              </div>
            </div>
          </div>
        </div>
      </form>
    </main>
  );
}
