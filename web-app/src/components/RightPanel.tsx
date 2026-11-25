import { useEffect, useState } from 'preact/hooks';
import { API_BASE, fetchRoutesInfo, listFiles, postDesktopMessage } from '../runtime/api';
import { toolColor, toolLabel, type DeviceInfo, type FileEntry, type RouteEntry } from '../runtime/types';
import { DiffPanel } from './DiffPanel';

export type RightTab = 'diff' | 'files' | 'teams' | 'connect';

interface Props {
  tab: RightTab;
  onTabChange: (tab: RightTab) => void;
  project: string;
  worktree?: string;
  device: DeviceInfo | null;
  skin: string;
  onSkinChange: (skin: string) => void;
  onClose?: () => void;
}

const SKINS: Array<{ id: string; label: string; sub: string; bg: string; accent: string }> = [
  { id: 'auto', label: '跟随系统', sub: '浅纸白 · 深夜航', bg: 'linear-gradient(135deg,#f2f3f7 50%,#1a1b26 50%)', accent: '#7aa2f7' },
  { id: 'paper', label: '纸白', sub: '清透浅色', bg: '#f2f3f7', accent: '#2f54eb' },
  { id: 'dawn', label: '晨光', sub: '暖纸浅色', bg: '#f3ede3', accent: '#286983' },
  { id: 'night', label: '夜航', sub: 'Tokyo Night', bg: '#1a1b26', accent: '#7aa2f7' },
  { id: 'slate', label: '墨岩', sub: '冷静灰黑', bg: '#17191e', accent: '#61afef' },
  { id: 'pine', label: '松林', sub: '护眼森绿', bg: '#1e2427', accent: '#a7c080' },
  { id: 'dusk', label: '暮紫', sub: '微光柔紫', bg: '#1e1e2e', accent: '#cba6f7' },
  { id: 'pond', label: '锦鲤池塘', sub: '动态游鱼', bg: '#10191b', accent: '#edbb83' },
];

const ROUTE_KIND_NAMES: Record<string, string> = {
  lan: '局域网直连',
  tailscale: 'Tailscale',
  tunnel: '公网隧道',
  own: '自有中转',
  public: '公共中转',
};

export function RightPanel({ tab, onTabChange, project, worktree, device, skin, onSkinChange, onClose }: Props) {
  const [filesPath, setFilesPath] = useState('');
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const [filesLoading, setFilesLoading] = useState(false);
  const [routesDetail, setRoutesDetail] = useState<{ environment_id?: string; routes?: RouteEntry[] } | null>(null);

  useEffect(() => {
    setFilesPath('');
  }, [project]);

  useEffect(() => {
    if (tab !== 'files' || !project) return;
    let alive = true;
    setFilesLoading(true);
    listFiles(project, filesPath)
      .then((res) => {
        if (alive) setEntries(res.entries || []);
      })
      .catch(() => {
        if (alive) setEntries([]);
      })
      .finally(() => {
        if (alive) setFilesLoading(false);
      });
    return () => {
      alive = false;
    };
  }, [tab, project, filesPath]);

  useEffect(() => {
    if (tab !== 'connect') return;
    fetchRoutesInfo()
      .then((res) => setRoutesDetail(res))
      .catch(() => setRoutesDetail({ environment_id: device?.environment_id, routes: device?.routes }));
  }, [tab, device?.environment_id]);

  const shownRoutes = routesDetail?.routes || device?.routes || [];
  const isDesktopWebView = typeof (window as any).chrome?.webview?.postMessage === 'function';

  return (
    <aside class="right-panel">
      <div class="right-tabs">
        {([
          ['diff', 'Diff'],
          ['files', '文件'],
          ['teams', '协作'],
          ['connect', '外观/连接'],
        ] as Array<[RightTab, string]>).map(([id, label]) => (
          <button
            key={id}
            type="button"
            class={`right-tab${tab === id ? ' active' : ''}`}
            onClick={() => onTabChange(id)}
          >
            {label}
          </button>
        ))}
        {onClose && (
          <button type="button" class="icon-btn" onClick={onClose} title="收起右侧栏">
            ✕
          </button>
        )}
      </div>

      <div class="right-body">
        {tab === 'diff' && <DiffPanel project={project} initialWorktree={worktree} />}

        {tab === 'files' && (
          <div class="files-panel">
            {!project ? (
              <div class="empty-hint">选择一个项目以浏览文件</div>
            ) : (
              <>
                <div class="files-crumb">
                  <button type="button" class="ghost-btn compact" disabled={!filesPath} onClick={() => setFilesPath('')}>
                    {project}
                  </button>
                  {filesPath && (
                    <>
                      <span>/</span>
                      <span class="muted-code">{filesPath}</span>
                      <button
                        type="button"
                        class="ghost-btn compact"
                        onClick={() => {
                          const parts = filesPath.split('/').filter(Boolean);
                          parts.pop();
                          setFilesPath(parts.join('/'));
                        }}
                      >
                        上级
                      </button>
                    </>
                  )}
                  <a class="ghost-btn compact" href={`${API_BASE}/files/?dir=${encodeURIComponent(project)}`} target="_blank" rel="noreferrer">
                    完整浏览
                  </a>
                </div>
                {filesLoading ? (
                  <div class="empty-hint">加载目录中…</div>
                ) : entries.length === 0 ? (
                  <div class="empty-hint">空目录</div>
                ) : (
                  <div class="file-rows">
                    {entries.map((entry) => (
                      <button
                        key={entry.name}
                        type="button"
                        class="file-row"
                        onClick={() => {
                          const next = filesPath ? `${filesPath}/${entry.name}` : entry.name;
                          if (entry.kind === 'dir') setFilesPath(next);
                          else window.open(`${API_BASE}/files/?dir=${encodeURIComponent(project)}&path=${encodeURIComponent(next)}`, '_blank');
                        }}
                      >
                        <span class="file-kind">{entry.kind === 'dir' ? '目录' : '文件'}</span>
                        <span class="file-name">{entry.name}</span>
                        {entry.size !== undefined && entry.kind === 'file' && (
                          <span class="muted-code">{entry.size > 1024 ? `${Math.round(entry.size / 1024)} KB` : `${entry.size} B`}</span>
                        )}
                      </button>
                    ))}
                  </div>
                )}
              </>
            )}
          </div>
        )}

        {tab === 'teams' && (
          <div class="teams-panel">
            <div class="panel-head-row">
              <strong>多 AI 协作任务</strong>
              <a class="primary-btn compact" href={`${API_BASE}/?tab=together&do=team`}>
                + 发起协作
              </a>
            </div>
            {!device?.team_cards || device.team_cards.length === 0 ? (
              <div class="empty-hint">当前没有进行中的协作任务。可发起领队分工、写审循环或并行比稿。</div>
            ) : (
              device.team_cards.map((card) => (
                <a key={card.id} class="team-card" href={`${API_BASE}/?tab=together&item=${encodeURIComponent(card.id)}`}>
                  <div class="team-card-top">
                    <span class="meta-chip">{card.mode === 'lead' ? '分工' : card.mode === 'review' ? '写审' : '比稿'}</span>
                    <span class="meta-chip">{card.dir}</span>
                    <span class={`state-pill pill-${card.confirm ? 'confirm' : card.state === 'running' ? 'busy' : 'idle'}`}>
                      {card.confirm ? '待确认' : card.state}
                    </span>
                  </div>
                  <div class="team-goal">{card.goal}</div>
                  {card.note && <div class="team-note">{card.note}</div>}
                </a>
              ))
            )}
          </div>
        )}

        {tab === 'connect' && (
          <div class="connect-panel">
            <section class="info-card">
              <div class="info-title">外观主题（与手机端统一配色）</div>
              <div class="skin-grid">
                {SKINS.map((s) => (
                  <button
                    key={s.id}
                    type="button"
                    class={`skin-card${skin === s.id ? ' active' : ''}`}
                    onClick={() => onSkinChange(s.id)}
                  >
                    <span
                      class="skin-swatch"
                      style={{ background: s.bg, '--sw-accent': s.accent } as any}
                    />
                    <span class="skin-meta">
                      <span class="skin-name">{s.label}</span>
                      <span class="skin-sub">{s.sub}</span>
                    </span>
                  </button>
                ))}
              </div>
            </section>

            <section class="info-card">
              <div class="info-title">已检测到的 AI 工具 ({(device?.tools || []).length})</div>
              <div class="filter-chips">
                {(device?.tools || []).map((t) => (
                  <span key={t} class="meta-chip">
                    <span class="tool-dot" style={{ background: toolColor(t) }} />
                    {toolLabel(t)}
                  </span>
                ))}
              </div>
              {isDesktopWebView && (
                <div class="panel-links">
                  <button type="button" class="ghost-btn compact" onClick={() => postDesktopMessage('open-installer')}>
                    安装更多 CLI 工具
                  </button>
                </div>
              )}
            </section>

            <section class="info-card">
              <div class="info-title">手机配对与多路由状态</div>
              <div class="info-kv">
                <span>电脑端版本</span>
                <strong>{device?.version ? `v${device.version}` : '未知'}</strong>
              </div>
              <div class="info-kv">
                <span>环境标识</span>
                <code class="muted-code">{(routesDetail?.environment_id || device?.environment_id || '未生成').slice(0, 16)}</code>
              </div>
              <div class="route-list">
                {shownRoutes.length === 0 ? (
                  <div class="empty-hint">当前仅通过单一地址连接</div>
                ) : (
                  shownRoutes.map((r) => (
                    <div key={r.url} class="route-row">
                      <span class="meta-chip">{ROUTE_KIND_NAMES[r.kind] || r.kind}</span>
                      <code class="route-url">{r.url}</code>
                    </div>
                  ))
                )}
              </div>
              <div class="panel-links">
                {isDesktopWebView && (
                  <button type="button" class="primary-btn compact" onClick={() => postDesktopMessage('open-classic')}>
                    打开扫码连接与设置
                  </button>
                )}
                <a class="ghost-btn compact" href={`${API_BASE}/computer/`}>
                  线路详情
                </a>
                <a class="ghost-btn compact" href={`${API_BASE}/`}>
                  经典移动视图
                </a>
              </div>
            </section>
          </div>
        )}
      </div>
    </aside>
  );
}
