import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
import {
  bootstrapAuthFromHash,
  checkSession,
  fetchOverview,
  loginWithPassword,
  postDesktopMessage,
  sendTerminalAction,
} from './runtime/api';
import type { DeviceInfo, FilePreviewTarget, SelectedItem, SessionEntry, TerminalEntry } from './runtime/types';
import { ChatPane } from './components/ChatPane';
import { CommandPalette } from './components/CommandPalette';
import { FilePreviewBody } from './components/FileExplorer';
import { QuickSpawnStage } from './components/QuickSpawnStage';
import { RightPanel, type RightTab } from './components/RightPanel';
import { Sidebar } from './components/Sidebar';
import { TerminalDrawer } from './components/TerminalDrawer';

const VALID_SKINS = new Set(['auto', 'paper', 'dawn', 'night', 'slate', 'pine', 'dusk', 'pond']);

function paintSkin(name: string) {
  const chosen = VALID_SKINS.has(name) ? name : 'paper';
  const win = window as any;
  if (typeof win.RemoteCliPaint === 'function') {
    const applied = win.RemoteCliPaint(chosen);
    try {
      if (applied?.bg) {
        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta) meta.setAttribute('content', applied.bg);
      }
      if (win.RemoteCliNative?.chrome && applied?.bg) win.RemoteCliNative.chrome(applied.bg);
    } catch {
      // ignore
    }
    return;
  }
  document.documentElement.dataset.skin = chosen === 'auto' ? 'paper' : chosen;
}

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
  const [rightTab, setRightTab] = useState<RightTab>('files');
  const [browseProject, setBrowseProject] = useState<string>('');
  const [expandedFile, setExpandedFile] = useState<FilePreviewTarget | null>(null);
  const [pendingCite, setPendingCite] = useState<{ nonce: number; text: string } | null>(null);
  const [terminalOpen, setTerminalOpen] = useState(false);
  const [activeDrawerTermId, setActiveDrawerTermId] = useState<string | null>(null);
  const [spawnDir, setSpawnDir] = useState<string | null>(null);
  const [addProjectOpen, setAddProjectOpen] = useState(false);
  const [manualProjectPath, setManualProjectPath] = useState('');
  const [addProjectErr, setAddProjectErr] = useState('');
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [focusMode, setFocusMode] = useState(false);

  const [sidebarWidth, setSidebarWidth] = useState(308);
  const [rightWidth, setRightWidth] = useState(430);
  const [mobilePane, setMobilePane] = useState<'sidebar' | 'chat' | 'right'>('sidebar');
  const [skin, setSkin] = useState(() => {
    try {
      const qSkin = new URLSearchParams(window.location.search).get('skin');
      if (qSkin && VALID_SKINS.has(qSkin)) return qSkin;
      const nativeSkin = (window as any).RemoteCliNative?.pref?.('skin');
      if (nativeSkin && VALID_SKINS.has(nativeSkin)) return nativeSkin;
      const saved = localStorage.getItem('rcli-skin') || 'dawn';
      return VALID_SKINS.has(saved) ? saved : 'dawn';
    } catch {
      return 'dawn';
    }
  });

  const revRef = useRef(0);
  const activeProjectRef = useRef('');
  const triggerRefreshRef = useRef<(() => void) | null>(null);

  useEffect(() => {
    paintSkin(skin);
    try {
      localStorage.setItem('rcli-skin', skin);
    } catch {
      // ignore
    }
    try {
      (window as any).RemoteCliNative?.setPref?.('skin', skin);
    } catch {
      // ignore
    }
    const resolved =
      typeof (window as any).RemoteCliResolveSkin === 'function' ? (window as any).RemoteCliResolveSkin(skin) : skin;
    postDesktopMessage(`skin:${resolved}`);
  }, [skin]);

  useEffect(() => {
    const onSys = (window as any).RemoteCliOnSystemTheme;
    if (typeof onSys !== 'function') return;
    return onSys(() => {
      if (skin === 'auto') paintSkin('auto');
    });
  }, [skin]);

  // Global keyboard shortcuts: Ctrl+K palette, Ctrl+N new AI, Ctrl+J terminal drawer, Ctrl+\ focus mode
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setPaletteOpen((v) => !v);
      } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'n') {
        e.preventDefault();
        setExpandedFile(null);
        setSpawnDir(activeProjectRef.current || '');
        setMobilePane('chat');
      } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'j') {
        e.preventDefault();
        setTerminalOpen((v) => !v);
      } else if ((e.ctrlKey || e.metaKey) && e.key === '\\') {
        e.preventDefault();
        setFocusMode((v) => !v);
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

  // Incremental long-poll loop using rev cursor (?rev=N&wait=20)
  useEffect(() => {
    if (!signedIn) return;
    let alive = true;
    let currentAbort: AbortController | null = null;

    const wake = () => {
      revRef.current = 0;
      currentAbort?.abort();
    };
    triggerRefreshRef.current = wake;
    (window as any).RemoteAiConsoleRefresh = wake;

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
      delete (window as any).RemoteAiConsoleRefresh;
    };
  }, [signedIn]);

  // Honor ?open=<terminalId> and ?tab=<name> + desktop WebView2 zero-reload jump
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
  activeProjectRef.current = activeProject;
  const inspectorProject = browseProject || activeProject;
  const activeWorktree = activeTerminal?.worktree || '';

  const openFilesFor = (dir?: string) => {
    if (dir) setBrowseProject(dir);
    setRightTab('files');
    setRightOpen(true);
    setFocusMode(false);
    setMobilePane('right');
  };

  const openSpawnFor = (dir?: string) => {
    setExpandedFile(null);
    setSpawnDir(dir ?? activeProject ?? '');
    setMobilePane('chat');
  };

  const handleCiteFile = (relPath: string, projectDir: string) => {
    const token = `@${relPath}`;
    if (selected) {
      setExpandedFile(null);
      setSpawnDir(null);
      setPendingCite({ nonce: Date.now(), text: token });
      setMobilePane('chat');
    } else {
      openSpawnFor(projectDir);
    }
  };

  const handleAddProjectTrigger = () => {
    // On Windows WebView2 desktop console, open the native FolderBrowserDialog directly.
    if (postDesktopMessage('pick-folder')) return;
    setAddProjectErr('');
    setManualProjectPath('');
    setAddProjectOpen(true);
  };

  const submitAddProject = async (path: string) => {
    const clean = path.trim();
    if (!clean) return;
    setAddProjectErr('');
    try {
      await sendTerminalAction({ action: 'project_add', path: clean });
      setAddProjectOpen(false);
      setManualProjectPath('');
      triggerRefreshRef.current?.();
    } catch (err: any) {
      setAddProjectErr(err?.message || '添加项目失败');
    }
  };

  const handleRunScript = async (dir: string, command: string) => {
    try {
      const started = await sendTerminalAction({ action: 'start', tool: 'shell', dir, history: false });
      const tid = started?.terminal;
      if (tid) {
        setActiveDrawerTermId(tid);
        setTerminalOpen(true);
        triggerRefreshRef.current?.();
        window.setTimeout(() => {
          sendTerminalAction({ action: 'input', terminal: tid, data: command + '\r' }).catch(() => {});
        }, 700);
      }
    } catch {
      // ignore
    }
  };

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
    const onMove = (ev: MouseEvent) => setSidebarWidth(Math.max(232, Math.min(440, startW + (ev.clientX - startX))));
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
    const onMove = (ev: MouseEvent) => setRightWidth(Math.max(310, Math.min(720, startW + (startX - ev.clientX))));
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  };

  if (signedIn === null) {
    return (
      <div class="boot-screen">
        <div class="boot-pill">
          <span class="conn-dot on" />
          正在连接 Remote AI 总控台…
        </div>
      </div>
    );
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
    <div class={`console-shell mobile-${mobilePane}${focusMode ? ' focus-mode' : ''}`}>
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
            setSpawnDir(null);
            setExpandedFile(null);
            setBrowseProject('');
            setMobilePane('chat');
          }}
          onNewTask={(dir) => openSpawnFor(dir)}
          onOpenProjectFiles={(dir) => openFilesFor(dir)}
          onAddProject={handleAddProjectTrigger}
          onOpenPalette={() => setPaletteOpen(true)}
          onOpenRightTab={(tab) => {
            setRightTab(tab);
            setRightOpen(true);
            setFocusMode(false);
            setMobilePane('right');
          }}
          onToggleFocus={() => setFocusMode((v) => !v)}
          onRefresh={() => triggerRefreshRef.current?.()}
        />
      </div>

      <div class="col-resizer" onMouseDown={dragSidebar} title="拖拽调整侧边栏宽度" />

      <div class="col-center">
        {spawnDir !== null ? (
          <QuickSpawnStage
            device={device}
            defaultDir={spawnDir || activeProject}
            contextTitle={activeTerminal?.title || activeSession?.title}
            onBack={() => setSpawnDir(null)}
            onAddProject={handleAddProjectTrigger}
            onCreated={(id) => {
              setSpawnDir(null);
              setExpandedFile(null);
              setSelected({ kind: 'terminal', id });
              setMobilePane('chat');
              triggerRefreshRef.current?.();
            }}
          />
        ) : expandedFile !== null ? (
          <main class="main-pane">
            <FilePreviewBody
              target={expandedFile}
              expanded
              onBack={() => setExpandedFile(null)}
              onToggleExpand={() => {
                const dir = expandedFile.dir;
                setExpandedFile(null);
                openFilesFor(dir);
              }}
              onCiteFile={handleCiteFile}
            />
          </main>
        ) : (
          <ChatPane
            device={device}
            selected={selected}
            terminal={activeTerminal}
            session={activeSession}
            onRefresh={() => triggerRefreshRef.current?.()}
            onToggleTerminal={() => setTerminalOpen((v) => !v)}
            onOpenDiff={() => {
              setBrowseProject('');
              setRightTab('diff');
              setRightOpen(true);
              setFocusMode(false);
              setMobilePane('right');
            }}
            onOpenFiles={(dir) => openFilesFor(dir)}
            onNewTask={(dir) => openSpawnFor(dir)}
            onAddProject={handleAddProjectTrigger}
            onRunScript={handleRunScript}
            onSelectTerminal={(id) => {
              setSelected({ kind: 'terminal', id });
              setMobilePane('chat');
            }}
            onToggleFocus={() => setFocusMode((v) => !v)}
            focusMode={focusMode}
            terminalDrawerOpen={terminalOpen}
            pendingCite={pendingCite}
          />
        )}

        <TerminalDrawer
          open={terminalOpen}
          activeProject={activeProject}
          selectedId={activeDrawerTermId || undefined}
          terminals={terminals}
          onClose={() => setTerminalOpen(false)}
          onRefresh={() => triggerRefreshRef.current?.()}
        />
      </div>

      {rightOpen && (
        <>
          <div class="col-resizer" onMouseDown={dragRight} title="拖拽调整右侧面板宽度" />
          <div class="col-right" style={{ width: `${rightWidth}px` }}>
            <RightPanel
              tab={rightTab}
              onTabChange={setRightTab}
              project={inspectorProject}
              workspaces={device?.workspaces || []}
              worktree={activeWorktree}
              device={device}
              skin={skin}
              onSkinChange={setSkin}
              onExpandPreview={(target) => {
                setSpawnDir(null);
                setExpandedFile(target);
                setMobilePane('chat');
              }}
              onCiteFile={handleCiteFile}
              onClose={() => setRightOpen(false)}
            />
          </div>
        </>
      )}

      {/* Mobile Bottom Tab Navigation (< 960px) */}
      <nav class="mobile-nav" aria-label="移动端导航">
        <button
          type="button"
          class={mobilePane === 'sidebar' ? 'active' : ''}
          onClick={() => setMobilePane('sidebar')}
        >
          项目/任务
        </button>
        <button
          type="button"
          class={mobilePane === 'chat' ? 'active' : ''}
          onClick={() => setMobilePane('chat')}
        >
          对话
        </button>
        <button
          type="button"
          class={mobilePane === 'right' && rightTab === 'files' ? 'active' : ''}
          onClick={() => {
            setRightTab('files');
            setRightOpen(true);
            setMobilePane('right');
          }}
        >
          文件
        </button>
        <button
          type="button"
          class={mobilePane === 'right' && rightTab !== 'files' ? 'active' : ''}
          onClick={() => {
            setRightTab(rightTab === 'files' ? 'diff' : rightTab);
            setRightOpen(true);
            setMobilePane('right');
          }}
        >
          检查/更多
        </button>
      </nav>

      {addProjectOpen && (
        <div class="modal-backdrop" onClick={() => setAddProjectOpen(false)}>
          <form
            class="modal-card"
            onClick={(e) => e.stopPropagation()}
            onSubmit={(e) => {
              e.preventDefault();
              submitAddProject(manualProjectPath);
            }}
          >
            <div class="modal-head">
              <h2>添加项目文件夹</h2>
              <button type="button" class="icon-btn" onClick={() => setAddProjectOpen(false)}>
                ✕
              </button>
            </div>
            {(device?.candidates || []).length > 0 && (
              <div class="nav-group">
                <div class="group-title">已发现的会话文件夹</div>
                {(device?.candidates || []).slice(0, 8).map((c) => (
                  <button
                    key={c.path}
                    type="button"
                    class="nav-row"
                    onClick={() => submitAddProject(c.path)}
                  >
                    <span class="nav-row-main">
                      <span class="nav-row-title">{c.name}</span>
                      <span class="nav-row-sub">{c.path}</span>
                    </span>
                    <span class="chip">+ 添加</span>
                  </button>
                ))}
              </div>
            )}
            <label class="field-label">
              <span>或手动输入电脑上的文件夹路径</span>
              <input
                type="text"
                placeholder="例如 E:\projects\my-app"
                value={manualProjectPath}
                onInput={(e) => setManualProjectPath((e.target as HTMLInputElement).value)}
              />
            </label>
            {addProjectErr && <div class="panel-feedback is-error">{addProjectErr}</div>}
            <div class="modal-foot">
              <button type="button" class="ghost-btn" onClick={() => setAddProjectOpen(false)}>
                取消
              </button>
              <button type="submit" class="primary-btn" disabled={!manualProjectPath.trim()}>
                添加
              </button>
            </div>
          </form>
        </div>
      )}

      <CommandPalette
        open={paletteOpen}
        terminals={terminals}
        sessions={sessions}
        onClose={() => setPaletteOpen(false)}
        onSelect={(item) => {
          setSelected(item);
          setMobilePane('chat');
        }}
        onAction={(action) => {
          if (action === 'new') openSpawnFor(activeProject);
          else if (action === 'terminal') setTerminalOpen((v) => !v);
          else if (action === 'diff' || action === 'connect') {
            setRightTab(action);
            setRightOpen(true);
            setFocusMode(false);
            setMobilePane('right');
          }
        }}
      />
    </div>
  );
}
