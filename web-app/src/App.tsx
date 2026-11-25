import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
import { bootstrapAuthFromHash, checkSession, fetchOverview, loginWithPassword } from './runtime/api';
import type { DeviceInfo, SelectedItem, SessionEntry, TerminalEntry } from './runtime/types';
import { ChatPane } from './components/ChatPane';
import { CommandPalette } from './components/CommandPalette';
import { NewTaskModal } from './components/NewTaskModal';
import { RightPanel, type RightTab } from './components/RightPanel';
import { Sidebar } from './components/Sidebar';
import { TerminalDrawer } from './components/TerminalDrawer';

export function App() {
  const [signedIn, setSignedIn] = useState<boolean | null>(null);
  const [password, setPassword] = useState('');
  const [loginError, setLoginError] = useState('');

  const [device, setDevice] = useState<DeviceInfo | null>(null);
  const [terminals, setTerminals] = useState<TerminalEntry[]>([]);
  const [sessions, setSessions] = useState<SessionEntry[]>([]);
  const [connected, setConnected] = useState(false);
  const [latencyMs, setLatencyMs] = useState<number | null>(null);

  const [selected, setSelected] = useState<SelectedItem | null>(null);
  const [rightOpen, setRightOpen] = useState(() => window.innerWidth >= 1040);
  const [rightTab, setRightTab] = useState<RightTab>('diff');
  const [terminalOpen, setTerminalOpen] = useState(false);
  const [newTaskDir, setNewTaskDir] = useState<string | null>(null);
  const [paletteOpen, setPaletteOpen] = useState(false);

  const [sidebarWidth, setSidebarWidth] = useState(280);
  const [rightWidth, setRightWidth] = useState(420);
  const [mobilePane, setMobilePane] = useState<'sidebar' | 'chat' | 'right'>('sidebar');
  const [skin, setSkin] = useState(() => {
    try {
      const nativeSkin = (window as any).RemoteCliNative?.pref?.('skin');
      if (nativeSkin && nativeSkin !== 'auto') return nativeSkin;
    } catch {
      // ignore
    }
    return localStorage.getItem('rcli-skin') || 'paper';
  });

  const revRef = useRef(0);
  const triggerRefreshRef = useRef<(() => void) | null>(null);

  useEffect(() => {
    document.documentElement.dataset.skin = skin;
    try {
      localStorage.setItem('rcli-skin', skin);
    } catch {
      // ignore
    }
  }, [skin]);

  // Global keyboard shortcuts (Ctrl+K command palette, Ctrl+J terminal drawer)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setPaletteOpen((v) => !v);
      } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'j') {
        e.preventDefault();
        setTerminalOpen((v) => !v);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // Auth bootstrap (#t=<ticket> for desktop WebView2 or #p=<password> for mobile)
  useEffect(() => {
    (async () => {
      try {
        await bootstrapAuthFromHash();
      } catch (e: any) {
        setLoginError(e?.message || '自动登录失败');
      }
      try {
        const s = await checkSession();
        setSignedIn(!!s.signed_in);
      } catch {
        setSignedIn(false);
      }
    })();
  }, []);

  // Incremental long-poll loop using rev cursor (?rev=N&wait=20) so idle cycles cost ~40 bytes and updates arrive instantly.
  useEffect(() => {
    if (!signedIn) return;
    let alive = true;
    let currentAbort: AbortController | null = null;

    const wake = () => {
      revRef.current = 0;
      currentAbort?.abort();
    };
    triggerRefreshRef.current = wake;

    const loop = async () => {
      while (alive) {
        currentAbort = new AbortController();
        const started = performance.now();
        try {
          const snap = await fetchOverview(revRef.current, revRef.current > 0 ? 20 : 0, currentAbort.signal);
          if (!alive) return;
          setConnected(true);
          if (revRef.current === 0) setLatencyMs(Math.max(1, Math.round(performance.now() - started)));
          if (typeof snap.rev === 'number' && snap.rev > 0) revRef.current = snap.rev;
          if (!snap.unchanged && snap.device) {
            setDevice(snap.device);
            setTerminals(snap.terminals || []);
            setSessions(snap.sessions || []);
          }
        } catch (err: any) {
          if (!alive) return;
          if (err?.name === 'AbortError') continue;
          if (err?.status === 401) {
            setSignedIn(false);
            return;
          }
          setConnected(false);
          revRef.current = 0;
          await new Promise((r) => window.setTimeout(r, 2000));
        }
      }
    };

    loop();
    return () => {
      alive = false;
      currentAbort?.abort();
      triggerRefreshRef.current = null;
    };
  }, [signedIn]);

  // Honor ?open=<terminalId> and ?tab=<name> query parameters on initial load,
  // and allow desktop WebView2 host to focus a terminal or panel without reloading.
  useEffect(() => {
    const applyTarget = (openId?: string | null, tab?: string | null) => {
      if (openId && /^[a-f0-9]{32}$/.test(openId)) {
        setSelected({ kind: 'terminal', id: openId });
        setMobilePane('chat');
      }
      if (tab === 'together' || tab === 'teams') {
        setRightTab('teams');
        setRightOpen(true);
        setMobilePane('right');
      } else if (tab === 'diff' || tab === 'files' || tab === 'connect') {
        setRightTab(tab);
        setRightOpen(true);
        setMobilePane('right');
      }
    };
    const params = new URLSearchParams(window.location.search);
    applyTarget(params.get('open'), params.get('tab'));
    (window as any).RemoteAiConsoleNavigate = applyTarget;
    return () => {
      delete (window as any).RemoteAiConsoleNavigate;
    };
  }, []);

  const activeTerminal = useMemo(
    () => (selected?.kind === 'terminal' ? terminals.find((t) => t.id === selected.id) || null : null),
    [selected, terminals],
  );

  const activeSession = useMemo(
    () => (selected?.kind === 'session' ? sessions.find((s) => s.id === selected.id && s.tool === selected.tool) || null : null),
    [selected, sessions],
  );

  const activeProject = activeTerminal?.dir || activeSession?.dir || device?.workspaces?.[0] || '';
  const activeWorktree = activeTerminal?.worktree || '';

  const handleLogin = async (e: Event) => {
    e.preventDefault();
    setLoginError('');
    try {
      await loginWithPassword(password.trim());
      setPassword('');
      setSignedIn(true);
    } catch (err: any) {
      setLoginError(err?.message || '密码不正确');
    }
  };

  const dragSidebar = (e: MouseEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = sidebarWidth;
    const onMove = (ev: MouseEvent) => setSidebarWidth(Math.max(220, Math.min(440, startW + (ev.clientX - startX))));
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  };

  const dragRight = (e: MouseEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = rightWidth;
    const onMove = (ev: MouseEvent) => setRightWidth(Math.max(300, Math.min(720, startW + (startX - ev.clientX))));
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  };

  if (signedIn === null) {
    return <div class="boot-screen">正在连接 Remote AI 总控台…</div>;
  }

  if (!signedIn) {
    return (
      <div class="login-screen">
        <form class="login-card" onSubmit={handleLogin}>
          <h1>Remote AI 总控台</h1>
          <p>输入电脑端窗口中显示的访问密码。</p>
          <input
            type="password"
            placeholder="访问密码"
            value={password}
            onInput={(e) => setPassword((e.target as HTMLInputElement).value)}
            required
          />
          {loginError && <div class="panel-feedback is-error">{loginError}</div>}
          <button type="submit" class="primary-btn wide">
            进入总控台
          </button>
        </form>
      </div>
    );
  }

  return (
    <div class={`console-shell mobile-${mobilePane}`}>
      <div class="col-sidebar" style={{ width: `${sidebarWidth}px` }}>
        <Sidebar
          device={device}
          terminals={terminals}
          sessions={sessions}
          selected={selected}
          connected={connected}
          latencyMs={latencyMs}
          onSelect={(item) => {
            setSelected(item);
            setMobilePane('chat');
          }}
          onNewTask={(dir) => setNewTaskDir(dir ?? '')}
          onOpenPalette={() => setPaletteOpen(true)}
          onOpenRightTab={(tab) => {
            setRightTab(tab);
            setRightOpen(true);
            setMobilePane('right');
          }}
        />
      </div>

      <div class="col-resizer" onMouseDown={dragSidebar} title="拖拽调整侧边栏宽度" />

      <div class="col-center">
        <ChatPane
          selected={selected}
          terminal={activeTerminal}
          session={activeSession}
          terminalDrawerOpen={terminalOpen}
          onRefresh={() => triggerRefreshRef.current?.()}
          onToggleTerminal={() => setTerminalOpen((v) => !v)}
          onOpenDiff={() => {
            setRightTab('diff');
            setRightOpen(true);
            setMobilePane('right');
          }}
          onNewTask={() => setNewTaskDir(activeProject)}
          onSelectTerminal={(id) => {
            setSelected({ kind: 'terminal', id });
            setMobilePane('chat');
          }}
        />
        <TerminalDrawer
          open={terminalOpen}
          activeProject={activeProject}
          terminals={terminals}
          onClose={() => setTerminalOpen(false)}
          onRefresh={() => triggerRefreshRef.current?.()}
        />
      </div>

      {rightOpen && (
        <>
          <div class="col-resizer" onMouseDown={dragRight} title="拖拽调整审阅面板宽度" />
          <div class="col-right" style={{ width: `${rightWidth}px` }}>
            <RightPanel
              tab={rightTab}
              onTabChange={setRightTab}
              project={activeProject}
              worktree={activeWorktree}
              device={device}
              skin={skin}
              onSkinChange={setSkin}
              onClose={() => {
                setRightOpen(false);
                setMobilePane('chat');
              }}
            />
          </div>
        </>
      )}

      <nav class="mobile-nav" aria-label="手机导航">
        <button type="button" class={mobilePane === 'sidebar' ? 'active' : ''} onClick={() => setMobilePane('sidebar')}>
          任务与项目
        </button>
        <button type="button" class={mobilePane === 'chat' ? 'active' : ''} onClick={() => setMobilePane('chat')}>
          对话
        </button>
        <button
          type="button"
          class={mobilePane === 'right' && rightTab === 'diff' ? 'active' : ''}
          onClick={() => {
            setRightTab('diff');
            setRightOpen(true);
            setMobilePane('right');
          }}
        >
          Diff
        </button>
        <button
          type="button"
          class={mobilePane === 'right' && rightTab === 'connect' ? 'active' : ''}
          onClick={() => {
            setRightTab('connect');
            setRightOpen(true);
            setMobilePane('right');
          }}
        >
          连接
        </button>
      </nav>

      <NewTaskModal
        open={newTaskDir !== null}
        device={device}
        defaultDir={newTaskDir || activeProject}
        onClose={() => setNewTaskDir(null)}
        onCreated={(id) => {
          setSelected({ kind: 'terminal', id });
          setMobilePane('chat');
          triggerRefreshRef.current?.();
        }}
      />

      <CommandPalette
        open={paletteOpen}
        terminals={terminals}
        sessions={sessions}
        onClose={() => setPaletteOpen(false)}
        onSelect={(item) => {
          setSelected(item);
          setMobilePane('chat');
        }}
        onAction={(act) => {
          if (act === 'new') setNewTaskDir(activeProject);
          else if (act === 'terminal') setTerminalOpen((v) => !v);
          else if (act === 'diff' || act === 'connect') {
            setRightTab(act);
            setRightOpen(true);
            setMobilePane('right');
          }
        }}
      />
    </div>
  );
}
