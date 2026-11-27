import { useCallback, useEffect, useMemo, useRef, useState } from 'preact/hooks';
import * as act from './runtime/actions';
import { bootstrapAuthFromHash, checkSession, fetchOverview, loginWithPassword, postDesktopMessage, terminalOp } from './runtime/api';
import { chatOutbox } from './runtime/chatOutbox';
import {
  buildThreads,
  canArchive,
  canEnd,
  canHandOff,
  canReopen,
  canStop,
  canTakeOver,
  loadPinned,
  loadSeen,
  savePinned,
  saveSeen,
  type Thread,
} from './runtime/threads';
import type { DeviceInfo, FilePreviewTarget, SessionEntry, TeamCardEntry, TerminalEntry } from './runtime/types';
import type { DiffTarget } from './components/ChangesPanel';
import { CommandPalette } from './components/CommandPalette';
import { ContextMenu, SEP, type MenuItem, type MenuState } from './components/ContextMenu';
import { Dialog } from './components/Dialog';
import { DiffView } from './components/DiffView';
import { FilePreviewBody } from './components/FileExplorer';
import { HandoffDialog } from './components/HandoffDialog';
import { Home } from './components/Home';
import { Inspector, type InspectorTab } from './components/Inspector';
import { NewThread } from './components/NewThread';
import { SettingsDialog } from './components/SettingsDialog';
import { Sidebar } from './components/Sidebar';
import { TeamView } from './components/TeamView';
import { TerminalPanel } from './components/TerminalPanel';
import { ThreadView } from './components/ThreadView';

/** What the middle of the window shows. */
type Center =
  | { kind: 'home' }
  | { kind: 'thread' }
  | { kind: 'new'; dir: string; together?: boolean }
  | { kind: 'team'; id: string }
  | { kind: 'diff'; target: DiffTarget }
  | { kind: 'file'; target: FilePreviewTarget };

const SKINS = new Set(['auto', 'paper', 'dawn', 'night', 'slate', 'pine', 'dusk', 'pond']);

export function App() {
  const [signedIn, setSignedIn] = useState<boolean | null>(null);
  const [password, setPassword] = useState('');
  const [loginError, setLoginError] = useState('');

  const [device, setDevice] = useState<DeviceInfo | null>(null);
  const [terminals, setTerminals] = useState<TerminalEntry[]>([]);
  const [records, setRecords] = useState<SessionEntry[]>([]);
  const [connected, setConnected] = useState(false);

  const [seen, setSeen] = useState<Record<string, number>>(loadSeen);
  const [pinned, setPinned] = useState<Record<string, boolean>>(loadPinned);

  const [center, setCenter] = useState<Center>({ kind: 'home' });
  // the conversation in view: by its key, and by its terminal while its tool has not named it yet
  const [sel, setSel] = useState<{ key: string; terminal: string }>({ key: '', terminal: '' });
  const [split, setSplit] = useState('');
  const [picked, setPicked] = useState<Record<string, boolean>>({});
  const [lastPick, setLastPick] = useState('');

  const [inspector, setInspector] = useState(() => window.innerWidth >= 1180);
  const [inspectorTab, setInspectorTab] = useState<InspectorTab>('changes');
  const [browse, setBrowse] = useState('');
  const [termOpen, setTermOpen] = useState(false);
  const [termFocus, setTermFocus] = useState('');
  const [insert, setInsert] = useState<{ nonce: number; text: string } | null>(null);

  const [menu, setMenu] = useState<MenuState | null>(null);
  const [handoff, setHandoff] = useState('');
  const [settings, setSettings] = useState(false);
  const [palette, setPalette] = useState(false);
  const [adding, setAdding] = useState(false);
  const [toast, setToast] = useState<{ text: string; bad?: boolean } | null>(null);
  const [narrowList, setNarrowList] = useState(true);
  const [sideWidth, setSideWidth] = useState(() => Number(localStorage.getItem('rcli-side-w')) || 284);
  const [inspWidth, setInspWidth] = useState(() => Number(localStorage.getItem('rcli-insp-w')) || 380);
  const [skin, setSkin] = useState(initialSkin);

  const rev = useRef(0);
  const wake = useRef<() => void>(() => {});
  const refresh = useCallback(() => wake.current(), []);

  // ---- appearance
  useEffect(() => {
    paintSkin(skin);
    try {
      localStorage.setItem('rcli-skin', skin);
      (window as any).RemoteCliNative?.setPref?.('skin', skin);
    } catch {
      // kept for this page only
    }
    const resolved = typeof (window as any).RemoteCliResolveSkin === 'function' ? (window as any).RemoteCliResolveSkin(skin) : skin;
    postDesktopMessage(`skin:${resolved}`);
    const onSystem = (window as any).RemoteCliOnSystemTheme;
    return typeof onSystem === 'function' ? onSystem(() => skin === 'auto' && paintSkin('auto')) : undefined;
  }, [skin]);

  // ---- signing in (#t=<ticket> from the desktop window, #p=<password> from a scanned link)
  useEffect(() => {
    (async () => {
      try {
        await bootstrapAuthFromHash();
      } catch (e: any) {
        setLoginError(e?.message || '自动登录失败');
      }
      try {
        setSignedIn(!!(await checkSession()).signed_in);
      } catch {
        setSignedIn(false);
      }
    })();
  }, []);

  // ---- the computer's list, long-polled by revision
  useEffect(() => {
    if (!signedIn) return;
    let alive = true;
    let abort: AbortController | null = null;
    wake.current = () => {
      rev.current = 0;
      abort?.abort();
    };
    (window as any).RemoteAiConsoleRefresh = wake.current;
    (async () => {
      while (alive) {
        abort = new AbortController();
        try {
          const snap = await fetchOverview(rev.current, rev.current > 0 ? 20 : 0, abort.signal);
          if (!alive) return;
          setConnected(true);
          if (typeof snap.rev === 'number' && snap.rev > 0) rev.current = snap.rev;
          if (!snap.unchanged && snap.device) {
            setDevice(snap.device);
            setTerminals(snap.terminals || []);
            setRecords(snap.sessions || []);
          }
        } catch (err: any) {
          if (!alive) return;
          if (err?.name === 'AbortError') continue;
          if (err?.status === 401) return setSignedIn(false);
          setConnected(false);
          rev.current = 0;
          await new Promise((r) => window.setTimeout(r, 2000));
        }
      }
    })();
    return () => {
      alive = false;
      abort?.abort();
      delete (window as any).RemoteAiConsoleRefresh;
    };
  }, [signedIn]);

  const model = useMemo(() => buildThreads(device, terminals, records, seen, pinned), [device, terminals, records, seen, pinned]);
  const features = device?.features || [];

  const findThread = (key: string, terminal: string): Thread | null =>
    model.byKey.get(key) || (terminal ? model.all.find((t) => t.terminal?.id === terminal) || null : null);
  const current = center.kind === 'thread' ? findThread(sel.key, sel.terminal) : null;
  const splitThread = split && current ? model.byKey.get(split) || null : null;

  // a conversation that got its name (or moved to another terminal) is followed under its new key
  useEffect(() => {
    if (current && current.key !== sel.key) setSel({ key: current.key, terminal: current.terminal?.id || '' });
  }, [current?.key]);

  // Flush stacked pending messages as one merged prompt whenever any chat thread becomes idle (even in background).
  useEffect(() => {
    const flushIdle = () => {
      for (const t of model.all) {
        if (t.runtime !== 'chat' || !t.terminal || t.terminal.state !== 'running') continue;
        if (t.busy || t.needs === 'confirm') continue;
        if (t.terminal.phase && t.terminal.phase !== 'idle' && t.terminal.phase !== 'done') continue;
        if (!chatOutbox.hasWaiting(t.terminal.id)) continue;
        chatOutbox
          .flush(t, ['waiting'])
          .then((flushed) => {
            if (flushed) refresh();
          })
          .catch(() => {});
      }
    };
    flushIdle();
    return chatOutbox.subscribe(flushIdle);
  }, [model.all, refresh]);

  const project =
    browse ||
    current?.project ||
    (center.kind === 'new' ? center.dir : '') ||
    (center.kind === 'diff' ? center.target.project : '') ||
    device?.workspaces?.[0] ||
    '';

  // ---- seen
  const markSeen = (list: Thread[]) => {
    if (!list.length) return;
    const now = Date.now();
    setSeen((prev) => {
      const next = { ...prev };
      for (const t of list) {
        next[t.key] = Math.max(t.at || 0, now);
        if (t.terminal) (window as any).RemoteCliNative?.seen?.(t.terminal.id, String(next[t.key]));
      }
      saveSeen(next);
      return next;
    });
  };

  const say = (text: string, bad = false) => {
    setToast({ text, bad });
    window.setTimeout(() => setToast((t) => (t?.text === text ? null : t)), 3200);
  };

  // ---- going places
  const open = (t: Thread) => {
    markSeen([t]);
    setSel({ key: t.key, terminal: t.terminal?.id || '' });
    if (split === t.key) setSplit('');
    setCenter({ kind: 'thread' });
    setBrowse('');
    setPicked({});
    setNarrowList(false);
  };
  const openTerminal = (terminal: string) => {
    setSel({ key: `t:${terminal}`, terminal });
    setCenter({ kind: 'thread' });
    setBrowse('');
    setNarrowList(false);
    refresh();
  };
  const goHome = () => {
    setCenter({ kind: 'home' });
    setSplit('');
    setNarrowList(true);
  };
  const newThread = (dir?: string) => {
    setCenter({ kind: 'new', dir: dir ?? current?.project ?? '', together: false });
    setNarrowList(false);
  };
  const newTeam = (dir?: string) => {
    setCenter({ kind: 'new', dir: dir ?? current?.project ?? '', together: true });
    setNarrowList(false);
  };
  const openTeam = (card: TeamCardEntry | string) => {
    setCenter({ kind: 'team', id: typeof card === 'string' ? card : card.id });
    setNarrowList(false);
  };

  // ---- adding a project
  const addProject = () => {
    if (postDesktopMessage('pick-folder')) return;
    setAdding(true);
  };

  // ---- what is done from menus
  const attempt = async (work: () => Promise<unknown>, done?: string) => {
    try {
      await work();
      if (done) say(done);
      refresh();
    } catch (err: any) {
      say(err?.message || '没有完成', true);
    }
  };

  const reopenThread = async (t: Thread, to: 'chat' | 'screen') => {
    const id = await act.reopen(t, to);
    setSel({ key: t.key, terminal: id });
    refresh();
  };

  const threadMenu = (t: Thread): MenuItem[] => {
    const many = model.all.filter((x) => picked[x.key]);
    if (many.length > 1 && picked[t.key]) return manyMenu(many);
    const reopen = canReopen(t, features);
    const stop = canStop(t);
    const end = canEnd(t);
    const handOff = canHandOff(t, features);
    const archive = canArchive(t);
    const items: MenuItem[] = [{ label: '打开', run: () => open(t) }];
    if (current && current.key !== t.key) items.push({ label: '在右侧分屏打开', run: () => setSplit(t.key) });
    items.push(SEP, { label: '交给另一个 AI…', disabled: handOff.ok ? false : handOff.why, run: () => setHandoff(t.key) });
    if (['claude', 'codex'].includes(t.tool) && t.runtime !== 'elsewhere') {
      const to = reopen.to || (t.runtime === 'chat' ? 'screen' : 'chat');
      items.push({
        label: to === 'screen' ? '在终端界面中继续' : '以对话方式继续',
        disabled: reopen.ok ? false : reopen.why,
        run: () => attempt(() => (open(t), reopenThread(t, to))),
      });
    }
    if (t.runtime === 'elsewhere') {
      const take = canTakeOver(t);
      items.push({
        label: '接管到这里',
        hint: '结束电脑上的窗口',
        disabled: take.ok ? false : take.why,
        run: () => attempt(async () => openTerminal(await act.takeOver(t))),
      });
    }
    items.push(SEP);
    items.push({ label: '停止这一轮', disabled: stop.ok ? false : stop.why, run: () => attempt(() => act.stop(t), '已请求停止') });
    items.push({
      label: '结束',
      hint: '记录会保留',
      danger: true,
      disabled: end.ok ? false : end.why,
      run: () => window.confirm(`结束“${t.title}”？对话记录会保留，之后可以接着聊。`) && attempt(() => act.end(t), '已结束'),
    });
    items.push(SEP);
    if (t.record) items.push({ label: t.pinned ? '取消置顶' : '置顶', run: () => attempt(() => act.mark(t, { pinned: !t.pinned })) });
    if (t.needs === 'done' || t.needs === 'failed') items.push({ label: '标为已读', run: () => markSeen([t]) });
    items.push({ label: '归档', disabled: archive.ok ? false : archive.why, run: () => attempt(() => act.mark(t, { hidden: true, pinned: false }), '已归档') });
    if (t.session) items.push(SEP, { label: '复制会话编号', run: () => navigator.clipboard?.writeText(t.session).then(() => say('已复制')) });
    return items;
  };

  const runBatchAction = (action: 'stop' | 'read' | 'archive' | 'end', many: Thread[]) => {
    if (many.length === 0) return;
    if (action === 'read') {
      markSeen(many);
      setPicked({});
      say(`已将 ${many.length} 个对话标为已读`);
      return;
    }
    const all = (list: Thread[], work: (t: Thread) => Promise<unknown>, done: string) =>
      attempt(async () => {
        const results = await Promise.allSettled(list.map(work));
        const failed = results.filter((r) => r.status === 'rejected').length;
        setPicked({});
        if (failed) throw new Error(`${list.length - failed} 个完成，${failed} 个没有完成`);
      }, done);
    if (action === 'stop') {
      const stoppable = many.filter((t) => canStop(t).ok);
      if (stoppable.length) all(stoppable, act.stop, '已请求停止');
    } else if (action === 'archive') {
      const archivable = many.filter((t) => canArchive(t).ok);
      if (archivable.length) all(archivable, (t) => act.mark(t, { hidden: true, pinned: false }), '已归档');
    } else if (action === 'end') {
      const endable = many.filter((t) => canEnd(t).ok);
      if (endable.length && window.confirm(`结束 ${endable.length} 个对话？记录会保留。`)) {
        all(endable, act.end, '已结束');
      }
    }
  };

  const manyMenu = (many: Thread[]): MenuItem[] => {
    const stoppable = many.filter((t) => canStop(t).ok);
    const endable = many.filter((t) => canEnd(t).ok);
    const archivable = many.filter((t) => canArchive(t).ok);
    return [
      { label: `已选 ${many.length} 个对话`, disabled: ' ' },
      SEP,
      { label: `停止 ${stoppable.length} 个`, disabled: stoppable.length ? false : '没有在执行的', run: () => runBatchAction('stop', many) },
      {
        label: `结束 ${endable.length} 个`,
        danger: true,
        disabled: endable.length ? false : '没有可以结束的',
        run: () => runBatchAction('end', many),
      },
      { label: '标为已读', run: () => runBatchAction('read', many) },
      { label: `归档 ${archivable.length} 个`, disabled: archivable.length ? false : '只能归档没有运行的', run: () => runBatchAction('archive', many) },
      SEP,
      { label: '取消选择', run: () => setPicked({}) },
    ];
  };

  const projectMenu = (name: string): MenuItem[] => {
    const info = device?.projects?.find((p) => p.name === name);
    return [
      { label: '新建对话', run: () => newThread(name) },
      { label: '打开终端', run: () => attempt(async () => {
          const id = await act.openShell(name);
          setTermFocus(id);
          setTermOpen(true);
        }) },
      SEP,
      { label: '查看改动', run: () => (setBrowse(name), setInspectorTab('changes'), setInspector(true)) },
      { label: '浏览文件', run: () => (setBrowse(name), setInspectorTab('files'), setInspector(true)) },
      SEP,
      {
        label: pinned[name] ? '取消置顶' : '置顶',
        run: () =>
          setPinned((prev) => {
            const next = { ...prev, [name]: !prev[name] };
            if (!next[name]) delete next[name];
            savePinned(next);
            return next;
          }),
      },
      {
        label: '从列表移除',
        hint: '不删除文件',
        danger: true,
        disabled: info?.fixed && !features.includes('projects') ? '写在电脑端配置文件里' : false,
        run: () => window.confirm(`把“${name}”从项目列表里移除？文件夹和对话记录都不会删除。`) && attempt(() => terminalOp({ action: 'project_remove', name }), '已移除'),
      },
    ];
  };

  const showMenu = (items: MenuItem[], e: MouseEvent) => setMenu({ x: e.clientX, y: e.clientY, items });

  const pick = (t: Thread, range: boolean) => {
    if (range && lastPick) {
      const order = model.all.map((x) => x.key);
      const [a, b] = [order.indexOf(lastPick), order.indexOf(t.key)].sort((x, y) => x - y);
      if (a >= 0 && b >= 0) {
        setPicked((prev) => ({ ...prev, ...Object.fromEntries(order.slice(a, b + 1).map((k) => [k, true])) }));
        return;
      }
    }
    setLastPick(t.key);
    setPicked((prev) => {
      const next = { ...prev };
      if (next[t.key]) delete next[t.key];
      else next[t.key] = true;
      return next;
    });
  };

  // ---- the desktop window's own requests: open a terminal, show a tab
  useEffect(() => {
    const navigate = (openId?: string | null, tab?: string | null) => {
      if (openId && /^[a-f0-9]{32}$/.test(openId)) openTerminal(openId);
      else if (tab === 'together') newTeam();
    };
    const qs = new URLSearchParams(window.location.search);
    navigate(qs.get('open'), qs.get('tab'));
    (window as any).RemoteAiConsoleNavigate = navigate;
    (window as any).RemoteAiConsoleToast = (text: string, bad?: boolean) => say(text, !!bad);
    return () => {
      delete (window as any).RemoteAiConsoleNavigate;
      delete (window as any).RemoteAiConsoleToast;
    };
  }, []);

  // ---- keys
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      const k = e.key.toLowerCase();
      if (k === 'k') setPalette((v) => !v);
      else if (k === 'n') newThread();
      else if (k === 'j') setTermOpen((v) => !v);
      else if (k === 'b') setInspector((v) => !v);
      else if (k === '0') goHome();
      else return;
      e.preventDefault();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  const dragWidth = (e: MouseEvent, side: 'left' | 'right') => {
    e.preventDefault();
    const x0 = e.clientX;
    const w0 = side === 'left' ? sideWidth : inspWidth;
    let w = w0;
    const move = (ev: MouseEvent) => {
      w = side === 'left' ? Math.max(220, Math.min(420, w0 + ev.clientX - x0)) : Math.max(300, Math.min(720, w0 + x0 - ev.clientX));
      (side === 'left' ? setSideWidth : setInspWidth)(w);
    };
    const up = () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
      try {
        localStorage.setItem(side === 'left' ? 'rcli-side-w' : 'rcli-insp-w', String(Math.round(w)));
      } catch {
        // kept for this page only
      }
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
  };

  if (signedIn === null) return <div class="boot">正在连接电脑…</div>;
  if (!signedIn) {
    return (
      <div class="boot">
        <form
          class="login"
          onSubmit={async (e) => {
            e.preventDefault();
            setLoginError('');
            try {
              await loginWithPassword(password.trim());
              setPassword('');
              setSignedIn(true);
            } catch (err: any) {
              setLoginError(err?.message || '密码不正确');
            }
          }}
        >
          <h1>Remote AI</h1>
          <p>输入电脑端窗口里显示的访问密码。</p>
          <input type="password" placeholder="访问密码" value={password} onInput={(e) => setPassword((e.target as HTMLInputElement).value)} required />
          {loginError && <div class="banner bad">{loginError}</div>}
          <button type="submit" class="btn primary">
            进入
          </button>
        </form>
      </div>
    );
  }

  const handoffThread = handoff ? model.byKey.get(handoff) || null : null;
  const threadView = (t: Thread, isSplit: boolean) => (
    <ThreadView
      key={isSplit ? `split:${t.key}` : t.key}
      thread={t}
      features={features}
      device={device}
      inspectorOpen={inspector}
      terminalOpen={termOpen}
      insert={isSplit ? null : insert}
      split={isSplit}
      onMenu={(e) => showMenu(threadMenu(t), e)}
      onReopen={(to) => reopenThread(t, to)}
      onTakeOver={async (text) => openTerminal(await act.takeOver(t, text))}
      onHandOff={() => setHandoff(t.key)}
      onHandedTo={(terminal) => {
        openTerminal(terminal);
        say('已切换 AI 继续');
      }}
      onOpenFiles={() => (setBrowse(''), setInspectorTab('files'), setInspector(true))}
      onToggleInspector={() => setInspector((v) => !v)}
      onToggleTerminal={() => setTermOpen((v) => !v)}
      onCloseSplit={() => setSplit('')}
      onChanged={refresh}
    />
  );

  let middle;
  if (center.kind === 'new') {
    middle = (
      <NewThread
        device={device}
        dir={center.dir}
        initialTogether={center.together}
        onCancel={goHome}
        onAddProject={addProject}
        onStarted={openTerminal}
        onTeamStarted={(id) => (openTeam(id), refresh())}
      />
    );
  } else if (center.kind === 'team') {
    middle = (
      <TeamView
        id={center.id}
        features={features}
        onGone={goHome}
        onChanged={refresh}
        onOpenMember={(tool, session, terminal) => {
          const t = (session && model.byKey.get(`${tool}:${session}`)) || model.all.find((x) => x.terminal?.id === terminal);
          if (t) open(t);
        }}
      />
    );
  } else if (center.kind === 'diff') {
    middle = <DiffView target={center.target} onClose={() => setCenter(current ? { kind: 'thread' } : { kind: 'home' })} />;
  } else if (center.kind === 'file') {
    middle = (
      <main class="file-view">
        <FilePreviewBody
          target={center.target}
          expanded
          onBack={() => setCenter(current || sel.key ? { kind: 'thread' } : { kind: 'home' })}
          onToggleExpand={() => {
            setCenter(sel.key ? { kind: 'thread' } : { kind: 'home' });
            setInspectorTab('files');
            setInspector(true);
          }}
          onCiteFile={(path) => (setInsert({ nonce: Date.now(), text: `@${path}` }), setCenter({ kind: 'thread' }))}
        />
      </main>
    );
  } else if (current) {
    middle = splitThread ? (
      <div class="split">
        {threadView(current, false)}
        {threadView(splitThread, true)}
      </div>
    ) : (
      threadView(current, false)
    );
  } else {
    middle = (
      <Home
        device={device}
        model={model}
        picked={picked}
        onOpen={open}
        onPick={pick}
        onPickMany={(keys) => setPicked(Object.fromEntries(keys.map((k) => [k, true])))}
        onClearPicked={() => setPicked({})}
        onBatchAction={runBatchAction}
        onMenu={(t, e) => showMenu(threadMenu(t), e)}
        onOpenTeam={openTeam}
        onNew={newThread}
        onNewTeam={newTeam}
        onAddProject={addProject}
        onSeenAll={() => markSeen(model.attention.filter((t) => t.needs === 'done' || t.needs === 'failed'))}
        onToast={say}
        onChanged={refresh}
      />
    );
  }

  return (
    <div class={`shell${narrowList ? ' show-list' : ' show-main'}`}>
      <div class="col-side" style={{ width: `${sideWidth}px` }}>
        <Sidebar
          device={device}
          model={model}
          selectedKey={center.kind === 'thread' ? current?.key || '' : ''}
          teamOpen={center.kind === 'team' ? center.id : ''}
          picked={picked}
          connected={connected}
          route={routeName(device)}
          onOpen={open}
          onPick={pick}
          onClearPicked={() => setPicked({})}
          onBatchAction={runBatchAction}
          onThreadMenu={(t, e) => showMenu(threadMenu(t), e)}
          onProjectMenu={(name, e) => showMenu(projectMenu(name), e)}
          onOpenTeam={openTeam}
          onNew={newThread}
          onAddProject={addProject}
          onSeenAll={() => markSeen(model.attention.filter((t) => t.needs === 'done' || t.needs === 'failed'))}
          onSettings={() => setSettings(true)}
          onHome={goHome}
        />
      </div>
      <div class="col-grip" onMouseDown={(e) => dragWidth(e, 'left')} />

      <div class="col-main">
        <button type="button" class="back-to-list" onClick={() => setNarrowList(true)}>
          ← 对话列表
        </button>
        <div class="main-area">{middle}</div>
        {termOpen && (
          <TerminalPanel
            project={project}
            worktree={current?.worktree || ''}
            shells={model.shells}
            focus={termFocus}
            onClose={() => setTermOpen(false)}
            onChanged={refresh}
          />
        )}
      </div>

      {inspector && center.kind !== 'new' && (
        <>
          <div class="col-grip" onMouseDown={(e) => dragWidth(e, 'right')} />
          <div class="col-insp" style={{ width: `${inspWidth}px` }}>
            <Inspector
              tab={inspectorTab}
              scopeKey={browse ? `proj:${browse}` : current?.key ? `thread:${current.key}` : project ? `proj:${project}` : ''}
              project={project}
              worktree={browse ? '' : current?.worktree || ''}
              workspaces={device?.workspaces || []}
              openDiff={center.kind === 'diff' ? center.target.file.path : ''}
              onTab={setInspectorTab}
              onOpenDiff={(target) => setCenter({ kind: 'diff', target })}
              onExpandFile={(target) => setCenter({ kind: 'file', target })}
              onCiteFile={(path) => {
                if (current) {
                  setInsert({ nonce: Date.now(), text: `@${path}` });
                  setCenter({ kind: 'thread' });
                } else newThread(project);
              }}
              onClose={() => setInspector(false)}
            />
          </div>
        </>
      )}

      {menu && <ContextMenu {...menu} onClose={() => setMenu(null)} />}
      {handoffThread && (
        <HandoffDialog
          from={handoffThread}
          device={device}
          threads={model.all}
          onClose={() => setHandoff('')}
          onStarted={(terminal) => {
            setHandoff('');
            openTerminal(terminal);
            say('已交给新的对话');
          }}
          onDone={(text) => {
            setHandoff('');
            say(text);
            refresh();
          }}
        />
      )}
      {settings && <SettingsDialog device={device} skin={skin} onSkin={setSkin} onToast={say} onClose={() => setSettings(false)} />}
      {palette && (
        <CommandPalette
          threads={model.all}
          onOpen={open}
          onClose={() => setPalette(false)}
          commands={[
            { label: '新建对话', keys: 'Ctrl+N', run: () => newThread() },
            { label: '总览', keys: 'Ctrl+0', run: goHome },
            { label: '项目终端', keys: 'Ctrl+J', run: () => setTermOpen((v) => !v) },
            { label: '变更与文件', keys: 'Ctrl+B', run: () => setInspector((v) => !v) },
            { label: '添加项目文件夹', run: addProject },
            { label: '设置', run: () => setSettings(true) },
          ]}
        />
      )}
      {adding && (
        <AddProject
          device={device}
          onClose={() => setAdding(false)}
          onAdded={() => {
            setAdding(false);
            refresh();
          }}
        />
      )}
      {toast && <div class={`toast${toast.bad ? ' bad' : ''}`}>{toast.text}</div>}
    </div>
  );
}

function AddProject({ device, onClose, onAdded }: { device: DeviceInfo | null; onClose: () => void; onAdded: () => void }) {
  const [path, setPath] = useState('');
  const [problem, setProblem] = useState('');
  const [busy, setBusy] = useState(false);
  const add = async (folder: string) => {
    if (!folder.trim() || busy) return;
    setBusy(true);
    setProblem('');
    try {
      await terminalOp({ action: 'project_add', path: folder.trim() });
      onAdded();
    } catch (err: any) {
      setProblem(err?.message || '没有添加');
      setBusy(false);
    }
  };
  return (
    <Dialog
      title="添加项目文件夹"
      onClose={onClose}
      foot={
        <>
          <span class="grow" />
          <button type="button" class="btn quiet" onClick={onClose}>
            取消
          </button>
          <button type="button" class="btn primary" disabled={busy || !path.trim()} onClick={() => add(path)}>
            添加
          </button>
        </>
      }
    >
      {(device?.candidates || []).length > 0 && (
        <div class="pick-list">
          {(device?.candidates || []).slice(0, 8).map((c) => (
            <button key={c.path} type="button" class="pick" onClick={() => add(c.path)}>
              <span class="pick-text">
                <strong>{c.name}</strong>
                <span>{c.path}</span>
              </span>
            </button>
          ))}
        </div>
      )}
      <label class="field">
        <span>电脑上的文件夹路径</span>
        <input
          type="text"
          placeholder="例如 E:\projects\my-app"
          value={path}
          onInput={(e) => setPath((e.target as HTMLInputElement).value)}
          onKeyDown={(e) => e.key === 'Enter' && add(path)}
        />
      </label>
      {problem && <div class="banner bad">{problem}</div>}
    </Dialog>
  );
}

function initialSkin(): string {
  try {
    const asked = new URLSearchParams(window.location.search).get('skin');
    if (asked && SKINS.has(asked)) return asked;
    const native = (window as any).RemoteCliNative?.pref?.('skin');
    if (native && SKINS.has(native)) return native;
    const saved = localStorage.getItem('rcli-skin') || 'dawn';
    return SKINS.has(saved) ? saved : 'dawn';
  } catch {
    return 'dawn';
  }
}

function paintSkin(name: string) {
  const chosen = SKINS.has(name) ? name : 'paper';
  const win = window as any;
  if (typeof win.RemoteCliPaint === 'function') {
    const applied = win.RemoteCliPaint(chosen);
    if (applied?.bg) {
      document.querySelector('meta[name="theme-color"]')?.setAttribute('content', applied.bg);
      try {
        win.RemoteCliNative?.chrome?.(applied.bg);
      } catch {
        // not in the app
      }
    }
    return;
  }
  document.documentElement.dataset.skin = chosen === 'auto' ? 'paper' : chosen;
}

function routeName(device: DeviceInfo | null): string {
  const host = window.location.hostname;
  if (host === '127.0.0.1' || host === 'localhost') return '本机';
  if (host.endsWith('.ts.net') || /^100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])\./.test(host)) return 'Tailscale';
  if (host.endsWith('.trycloudflare.com')) return '公网隧道';
  if (/^\/c\/[a-f0-9]{20}\//.test(window.location.pathname)) return '公共中转';
  if (/^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(host)) return '局域网';
  return device?.routes?.length ? '中转' : '已连接';
}
