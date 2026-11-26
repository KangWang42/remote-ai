import { useEffect, useState } from 'preact/hooks';
import { API_BASE, fetchRoutesInfo, postDesktopMessage } from '../runtime/api';
import { toolBadgeStyle, toolLabel, toolMeta, type DeviceInfo, type RouteEntry } from '../runtime/types';
import { Dialog } from './Dialog';

interface Props {
  device: DeviceInfo | null;
  skin: string;
  onSkin: (skin: string) => void;
  onClose: () => void;
}

const SKINS: Array<{ id: string; label: string; side: string; main: string; ink: string }> = [
  { id: 'dawn', label: '晨光', side: '#f3ede3', main: '#fcf8f2', ink: '#322d47' },
  { id: 'paper', label: '纸白', side: '#f2f3f7', main: '#ffffff', ink: '#1e212b' },
  { id: 'auto', label: '跟随系统', side: '#f2f3f7', main: '#1a1b26', ink: '#7aa2f7' },
  { id: 'night', label: '夜航', side: '#1a1b26', main: '#222436', ink: '#7aa2f7' },
  { id: 'slate', label: '墨岩', side: '#17191e', main: '#1f232a', ink: '#61afef' },
  { id: 'pine', label: '松林', side: '#1e2427', main: '#262e32', ink: '#a7c080' },
  { id: 'dusk', label: '暮紫', side: '#1e1e2e', main: '#262638', ink: '#cba6f7' },
  { id: 'pond', label: '锦鲤池塘', side: '#10191b', main: '#192628', ink: '#edbb83' },
];

// as the phone names them (web/me.js)
const WINDOWS: Record<string, string> = { five_hour: '5 小时', seven_day: '本周' };
function windowName(name: string): string {
  if (WINDOWS[name]) return WINDOWS[name];
  if (/_five_hour$/.test(name)) return '5 小时';
  if (/_seven_day$/.test(name)) return '本周';
  const minutes = /^(\d+)m$/.exec(name);
  if (minutes) return Number(minutes[1]) >= 1440 ? `${Math.round(Number(minutes[1]) / 1440)} 天` : `${Math.round(Number(minutes[1]) / 60)} 小时`;
  return name;
}

const ROUTE: Record<string, string> = { lan: '局域网', tailscale: 'Tailscale', tunnel: '公网隧道', own: '自有中转', public: '公共中转' };

export function SettingsDialog({ device, skin, onSkin, onClose }: Props) {
  const [routes, setRoutes] = useState<RouteEntry[]>(device?.routes || []);
  const desktop = typeof (window as any).chrome?.webview?.postMessage === 'function';

  useEffect(() => {
    fetchRoutesInfo()
      .then((res) => setRoutes((res.routes as RouteEntry[]) || []))
      .catch(() => {});
  }, []);

  const quotas = (device?.quotas || []).filter((q) => q.windows?.length);

  return (
    <Dialog title="设置" wide onClose={onClose}>
      <section class="set-sec">
        <h3>外观</h3>
        <div class="skins">
          {SKINS.map((s) => (
            <button key={s.id} type="button" class={`skin${skin === s.id ? ' on' : ''}`} onClick={() => onSkin(s.id)}>
              <span class="skin-pic" aria-hidden="true">
                <span style={{ background: s.side }} />
                <span style={{ background: s.main }}>
                  <i style={{ background: s.ink }} />
                </span>
              </span>
              <span>{s.label}</span>
            </button>
          ))}
        </div>
        <p class="muted small">与手机 App 使用同一套配色。</p>
      </section>

      <section class="set-sec">
        <h3>手机连接</h3>
        {device?.version && (
          <div class="kv">
            <span>电脑端版本</span>
            <strong>v{device.version}</strong>
          </div>
        )}
        {routes.length > 0 && (
          <div class="routes">
            {routes.map((r) => (
              <div key={r.url} class="route">
                <span class="chip soft">{ROUTE[r.kind] || r.kind}</span>
                <code>{r.url}</code>
              </div>
            ))}
          </div>
        )}
        <div class="row-btns">
          {desktop && (
            <button type="button" class="btn primary small" onClick={() => postDesktopMessage('open-classic')}>
              手机扫码连接
            </button>
          )}
          <a class="btn quiet small" href={`${API_BASE}/computer/`}>
            线路详情
          </a>
        </div>
      </section>

      <section class="set-sec">
        <h3>AI 工具</h3>
        <div class="chips">
          {(device?.tools || []).map((t) => (
            <span key={t} class="chip">
              <span class="tool-mark tiny" style={toolBadgeStyle(t)}>
                {toolMeta(t).mono}
              </span>
              {toolLabel(t)}
            </span>
          ))}
        </div>
        {desktop && (
          <div class="row-btns">
            <button type="button" class="btn quiet small" onClick={() => postDesktopMessage('open-installer')}>
              安装更多工具
            </button>
          </div>
        )}
      </section>

      {quotas.length > 0 && (
        <section class="set-sec">
          <h3>额度</h3>
          {quotas.map((q) =>
            (q.windows.some((w) => !w.group) ? q.windows.filter((w) => !w.group) : q.windows).map((w) => {
              const used = Math.min(100, Math.max(0, w.used));
              return (
                <div key={`${q.tool}:${w.name}`} class="quota">
                  <span class="quota-name">
                    {w.group || toolLabel(q.tool)} · {windowName(w.name)}
                  </span>
                  <span class={`quota-bar${used >= 80 ? ' high' : used > 50 ? ' elevated' : ''}`}>
                    <span style={{ width: `${used}%` }} />
                  </span>
                  <span class="quota-n">已用 {Number(used.toFixed(1))}%</span>
                </div>
              );
            }),
          )}
        </section>
      )}
    </Dialog>
  );
}
