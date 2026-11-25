import { useEffect, useState } from 'preact/hooks';
import { API_BASE, fetchRoutesInfo, postDesktopMessage } from '../runtime/api';
import {
  toolBadgeStyle,
  toolLabel,
  toolMeta,
  type DeviceInfo,
  type FilePreviewTarget,
  type RouteEntry,
} from '../runtime/types';
import { DiffPanel } from './DiffPanel';
import { FileExplorer } from './FileExplorer';

export type RightTab = 'files' | 'diff' | 'teams' | 'connect';

interface Props {
  tab: RightTab;
  onTabChange: (tab: RightTab) => void;
  project: string;
  workspaces: string[];
  worktree?: string;
  device: DeviceInfo | null;
  skin: string;
  onSkinChange: (skin: string) => void;
  onExpandPreview: (target: FilePreviewTarget) => void;
  onCiteFile: (relPath: string, projectDir: string) => void;
  onClose?: () => void;
}

interface SkinCardSpec {
  id: string;
  label: string;
  sub: string;
  side: string;
  main: string;
  bar: string;
  pill: string;
}

const SKINS: SkinCardSpec[] = [
  { id: 'dawn', label: '晨光', sub: '暖黄纸感', side: '#f3ede3', main: '#fcf8f2', bar: '#d9ccba', pill: '#322d47' },
  { id: 'paper', label: '纸白', sub: '清爽浅色', side: '#f2f3f7', main: '#ffffff', bar: '#dfe3eb', pill: '#1e212b' },
  { id: 'pond', label: '锦鲤池塘', sub: '动态游鱼', side: '#10191b', main: '#192628', bar: '#2e4244', pill: '#edbb83' },
  { id: 'auto', label: '跟随系统', sub: '浅白 · 深夜', side: '#f2f3f7', main: '#1a1b26', bar: '#64748b', pill: '#7aa2f7' },
  { id: 'night', label: '夜航', sub: 'Tokyo Night', side: '#1a1b26', main: '#222436', bar: '#323756', pill: '#7aa2f7' },
  { id: 'slate', label: '墨岩', sub: '冷静灰黑', side: '#17191e', main: '#1f232a', bar: '#333a47', pill: '#61afef' },
  { id: 'pine', label: '松林', sub: '护眼森绿', side: '#1e2427', main: '#262e32', bar: '#3b474d', pill: '#a7c080' },
  { id: 'dusk', label: '暮紫', sub: '微光柔紫', side: '#1e1e2e', main: '#262638', bar: '#3a3b54', pill: '#cba6f7' },
];

const ROUTE_KIND_NAMES: Record<string, string> = {
  lan: '局域网直连',
  tailscale: 'Tailscale',
  tunnel: '公网隧道',
  own: '自有中转',
  public: '公共中转',
};

export function RightPanel({
  tab,
  onTabChange,
  project,
  workspaces,
  worktree,
  device,
  skin,
  onSkinChange,
  onExpandPreview,
  onCiteFile,
  onClose,
}: Props) {
  const [routesDetail, setRoutesDetail] = useState<{ environment_id?: string; routes?: RouteEntry[] } | null>(null);

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
      <div class="right-tabs-wrap">
        <div class="right-tabs" role="tablist">
          {([
            ['files', '文件'],
            ['diff', 'Diff'],
            ['teams', '协作'],
            ['connect', '外观/连接'],
          ] as Array<[RightTab, string]>).map(([id, label]) => (
            <button
              key={id}
              type="button"
              role="tab"
              aria-selected={tab === id}
              class={`right-tab${tab === id ? ' active' : ''}`}
              onClick={() => onTabChange(id)}
            >
              {label}
            </button>
          ))}
        </div>
        {onClose && (
          <button type="button" class="circle-btn" onClick={onClose} title="收起右侧栏" aria-label="收起右侧栏">
            <svg viewBox="0 0 20 20" width="14" height="14" aria-hidden="true">
              <path d="M5.5 5.5 14.5 14.5M14.5 5.5 5.5 14.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
            </svg>
          </button>
        )}
      </div>

      <div class="right-body">
        {tab === 'files' && (
          <FileExplorer
            workspaces={workspaces}
            activeProject={project}
            onExpandPreview={onExpandPreview}
            onCiteFile={onCiteFile}
          />
        )}

        {tab === 'diff' && <DiffPanel project={project} initialWorktree={worktree} />}

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
              <div class="info-title">外观主题（与手机 App 同套配色）</div>
              <div class="skin-grid">
                {SKINS.map((s) => (
                  <button
                    key={s.id}
                    type="button"
                    class={`skin-card${skin === s.id ? ' active' : ''}`}
                    onClick={() => onSkinChange(s.id)}
                  >
                    <div class="skin-preview" aria-hidden="true">
                      <div class="skin-prev-side" style={{ background: s.side }}>
                        <span class="skin-prev-bar" style={{ background: s.bar, width: '72%' }} />
                        <span class="skin-prev-bar" style={{ background: s.bar, width: '54%' }} />
                        <span class="skin-prev-bar" style={{ background: s.bar, width: '64%' }} />
                      </div>
                      <div class="skin-prev-main" style={{ background: s.main }}>
                        <span class="skin-prev-bar" style={{ background: s.bar, width: '68%' }} />
                        <span class="skin-prev-pill" style={{ background: s.pill }} />
                      </div>
                    </div>
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
              <div class="filter-chips" style={{ flexWrap: 'wrap' }}>
                {(device?.tools || []).map((t) => {
                  const meta = toolMeta(t);
                  return (
                    <span key={t} class="chip has-mono" style={{ cursor: 'default' }}>
                      <span class="chip-mono" style={toolBadgeStyle(t)}>
                        {meta.mono}
                      </span>
                      {toolLabel(t)}
                    </span>
                  );
                })}
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
              <div class="muted-code" style={{ lineHeight: 1.55 }}>
                iPhone / iPad：可安装原生 iOS 版（RemoteCli-iOS.ipa）扫码直连，或在 Safari 打开连接地址后点“分享 → 添加到主屏幕”免安装全屏使用。
              </div>
            </section>
          </div>
        )}
      </div>
    </aside>
  );
}
