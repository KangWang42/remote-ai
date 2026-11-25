import { formatRelativeTime } from '../runtime/time';
import { runtimeLabel, type Thread, type ThreadModel } from '../runtime/threads';
import { toolBadgeStyle, toolMeta, type DeviceInfo } from '../runtime/types';

interface Props {
  device: DeviceInfo | null;
  model: ThreadModel;
  onOpen: (t: Thread) => void;
  onMenu: (t: Thread, e: MouseEvent) => void;
  onNew: () => void;
  onAddProject: () => void;
}

const NEED: Record<string, string> = { confirm: '等你确认', failed: '出错了', done: '已完成，待查看' };

/** With no conversation open: what needs the owner, and what is running now. */
export function Home({ device, model, onOpen, onMenu, onNew, onAddProject }: Props) {
  const running = model.all.filter((t) => !t.needs && t.runtime !== 'idle');
  const card = (t: Thread) => {
    const meta = toolMeta(t.tool);
    return (
      <button
        key={t.key}
        type="button"
        class={`home-card${t.needs ? ` need-${t.needs}` : ''}`}
        onClick={() => onOpen(t)}
        onContextMenu={(e) => {
          e.preventDefault();
          onMenu(t, e);
        }}
      >
        <span class="home-card-top">
          <span class="tool-mark" style={toolBadgeStyle(t.tool)}>
            {meta.mono}
          </span>
          <span class="home-card-title">{t.title}</span>
          <span class="th-time">{formatRelativeTime(t.at)}</span>
        </span>
        <span class="home-card-where">
          {t.project || t.folder} {runtimeLabel(t) ? `· ${runtimeLabel(t)}` : ''}
          {t.busy ? ' · 正在执行' : ''}
        </span>
        {t.needs && <span class={`th-need need-${t.needs}`}>{NEED[t.needs]}</span>}
        {t.preview && <span class="home-card-text">{t.preview}</span>}
      </button>
    );
  };

  const empty = model.all.length === 0;
  return (
    <main class="home">
      <div class="home-inner">
        {empty ? (
          <div class="welcome">
            <div class="brand-mark big">R</div>
            <h1>开始第一段对话</h1>
            <p>选一个项目文件夹，交给电脑上的 AI 去做。手机上也能看到并接着说。</p>
            <div class="row-btns center">
              <button type="button" class="btn primary" onClick={onNew} disabled={!(device?.workspaces || []).length}>
                新建对话
              </button>
              <button type="button" class="btn quiet" onClick={onAddProject}>
                添加项目文件夹
              </button>
            </div>
          </div>
        ) : (
          <>
            <div class="home-head">
              <h1>总览</h1>
              <button type="button" class="btn primary" onClick={onNew}>
                新建对话
              </button>
            </div>
            {model.attention.length > 0 && (
              <section>
                <h2>需要你处理</h2>
                <div class="home-grid">{model.attention.map(card)}</div>
              </section>
            )}
            <section>
              <h2>正在进行</h2>
              {running.length ? <div class="home-grid">{running.map(card)}</div> : <p class="muted">没有正在运行的对话。</p>}
            </section>
          </>
        )}
      </div>
    </main>
  );
}
