import { conversationAction, terminalOp, workAction } from './api';
import type { Thread } from './threads';

/**
 * What can be done to a conversation, one function each, whichever way it runs (threads.ts). Each resolves when the
 * computer has done it and rejects with its reason. One that starts a terminal resolves with the new terminal's id.
 */

function recordPayload(t: Thread, action: string): Record<string, unknown> {
  return { action, tool: t.tool, session: t.session, dir: t.project, folder: t.folder };
}

/** Its tool's own way of being told something: the desktop apps take it through their own channel. */
function sayWay(t: Thread): string {
  const s = t.record;
  const way = s?.ways?.say || '';
  if (way === 'codex-desktop' || way === 'agy-desktop') return way;
  return s?.transport === 'codex-desktop' || s?.transport === 'agy-desktop' ? s.transport : '';
}

/** Says something. A conversation nobody has open is gone on with as a Remote AI conversation: resolves with its terminal. */
export async function say(t: Thread, text: string, attachments: string[] = [], id?: string): Promise<string | void> {
  if (t.runtime === 'chat' && t.terminal) {
    await terminalOp({ action: 'say', terminal: t.terminal.id, text, ...(id ? { id } : {}), ...(attachments.length ? { attachments } : {}) });
    return;
  }
  if (t.runtime === 'elsewhere') {
    const way = sayWay(t);
    await conversationAction({ ...recordPayload(t, 'say'), text, ...(way ? { transport: way } : {}) });
    return;
  }
  return resume(t, 'chat', text);
}

export async function stop(t: Thread): Promise<void> {
  if (t.runtime === 'chat' && t.terminal) {
    await terminalOp({ action: 'stop', terminal: t.terminal.id });
    return;
  }
  const s = t.record;
  const way = s?.ways?.stop || s?.transport || 'hook';
  await conversationAction({ ...recordPayload(t, 'stop'), transport: way, ...(way === 'codex-desktop' && s?.turn ? { turn: s.turn } : {}) });
}

/** Answers what it asks: `allow`, `always`, `deny`, or `choice:<n>`. */
export async function answer(t: Thread, ask: string, decision: string): Promise<void> {
  if (t.runtime === 'chat' && t.terminal) {
    await terminalOp({ action: 'answer', terminal: t.terminal.id, ask, decision });
    return;
  }
  const s = t.record;
  const desktop = (s?.ways?.answer || s?.transport) === 'agy-desktop';
  await conversationAction({
    ...recordPayload(t, 'answer'),
    ask,
    allow: decision !== 'deny',
    scope: decision === 'always' ? 'always' : 'once',
    ...(desktop ? { transport: 'agy-desktop', ...(decision.startsWith('choice:') || decision === 'deny' ? { choice: decision } : {}) } : {}),
  });
}

/** Ends the program that runs it; its record stays. */
export async function end(t: Thread): Promise<void> {
  if (t.terminal && (t.runtime === 'chat' || t.runtime === 'screen')) {
    await terminalOp({ action: 'close', terminal: t.terminal.id });
    return;
  }
  await conversationAction(recordPayload(t, 'end'));
}

export async function mark(t: Thread, marks: { pinned?: boolean; hidden?: boolean }): Promise<void> {
  await conversationAction({
    ...recordPayload(t, 'mark'),
    pinned: marks.pinned ?? t.pinned,
    hidden: marks.hidden ?? false,
  });
}

/** Shows a running conversation the other way (chat ⇄ its tool's screen); resolves with the new terminal. */
export async function reopen(t: Thread, to: 'chat' | 'screen'): Promise<string> {
  if (t.runtime === 'idle') return resume(t, to);
  if (!t.terminal) throw new Error('这段对话没有在这里运行');
  const started = await terminalOp({
    action: 'start',
    tool: t.tool,
    dir: t.terminal.dir,
    reopen: t.terminal.id,
    ...(to === 'chat' ? { form: 'chat' } : {}),
  });
  return started.terminal;
}

export async function changeModel(t: Thread, model: string, effort: string): Promise<void> {
  if (t.busy || t.needs === 'confirm' || !t.session) throw new Error('请等当前对话空闲后再切换模型');
  if (t.team) {
    const { teams } = await workAction('teams');
    const team = teams?.find((entry: { id: string }) => entry.id === t.team);
    const role = Object.keys(team?.members || {}).find((key) => team.members[key] === t.terminal?.id);
    if (!role || !['running', 'paused'].includes(team.state)) throw new Error('这段对话已不是当前协作成员');
    await workAction('team_swap', { team: t.team, role, tool: t.tool, model, effort });
    return;
  }
  if (t.runtime === 'chat' && t.terminal) {
    await terminalOp({ action: 'start', reopen: t.terminal.id, dir: t.project, tool: t.tool, form: 'chat', model, effort });
    return;
  }
  if (t.runtime !== 'idle') throw new Error('请在运行这段对话的窗口切换模型');
  await terminalOp({ ...recordPayload(t, 'start'), form: 'chat', model, effort, ...(t.terminal?.mode ? { mode: t.terminal.mode } : {}) });
}

/** Goes on with a conversation nobody has open, as a chat or on its tool's screen. */
export async function resume(t: Thread, as: 'chat' | 'screen', text = ''): Promise<string> {
  const started = await terminalOp({
    action: 'start',
    tool: t.tool,
    dir: t.project,
    ...(t.project === '' && t.folder ? { folder: t.folder } : {}),
    session: t.session,
    ...(as === 'chat' ? { form: 'chat' } : {}),
    ...(text ? { text } : {}),
  });
  return started.terminal;
}

/** Ends the computer's own program that has it open and goes on with it here as a chat. */
export async function takeOver(t: Thread, text = ''): Promise<string> {
  const started = await terminalOp({
    action: 'start',
    tool: t.tool,
    dir: t.project,
    ...(t.project === '' && t.folder ? { folder: t.folder } : {}),
    session: t.session,
    takeover: true,
    ...(['claude', 'codex', 'agy'].includes(t.tool) ? { form: 'chat' } : {}),
    ...(text ? { text } : {}),
  });
  return started.terminal;
}

export interface NewConversation {
  tool: string;
  dir: string;
  text?: string;
  model?: string;
  effort?: string;
  mode?: string;
  worktree?: boolean;
  branch?: string;
  /** On its tool's own screen rather than as a chat (tools that have no chat always are). */
  screen?: boolean;
}

export async function begin(c: NewConversation): Promise<string> {
  const started = await terminalOp({
    action: 'start',
    tool: c.tool,
    dir: c.dir,
    history: false,
    ...(c.screen || c.tool === 'shell' ? {} : { form: 'chat', ...(c.text ? { text: c.text } : {}) }),
    ...(c.model ? { model: c.model } : {}),
    ...(c.effort ? { effort: c.effort } : {}),
    ...(c.mode && c.tool !== 'shell' ? { mode: c.mode } : {}),
    ...(c.worktree ? { worktree: true, ...(c.branch ? { branch: c.branch } : {}) } : {}),
  });
  return started.terminal;
}

export async function openShell(dir: string): Promise<string> {
  const started = await terminalOp({ action: 'start', tool: 'shell', dir, history: false });
  return started.terminal;
}

export async function typeInto(terminal: string, data: string): Promise<void> {
  await terminalOp({ action: 'input', terminal, data });
}

// ---- handing a conversation over

/** What a hand-over of `t` says: its first and last requests, its last answer and steps, and the note. */
export async function brief(t: Thread, note: string): Promise<string> {
  const reply = await workAction<{ text?: string }>('brief', { from: t.session, note });
  return String(reply.text || '');
}

/** Continues the conversation in-place with another AI: sends the handover summary + owner's next message. */
export async function handover(t: Thread, tool: string, text: string, model = '', effort = ''): Promise<string> {
  const started = await terminalOp({
    action: 'start',
    tool,
    dir: t.project,
    form: 'chat',
    from: t.session,
    from_tool: t.tool,
    text,
    ...(model ? { model } : {}),
    ...(effort ? { effort } : {}),
  });
  return started.terminal;
}

/** Begins a new conversation of `tool` in the same project with the hand-over as its first message. */
export async function handToNew(t: Thread, tool: string, note: string): Promise<string> {
  const text = (await brief(t, note)).slice(0, 8000);
  return begin({ tool, dir: t.project, text });
}

/** Tells a conversation that is open the hand-over. */
export async function handToOpen(t: Thread, to: Thread, note: string): Promise<void> {
  await workAction('handoff', { from: t.session, to: to.session, note });
}

/** A collaboration member's work given to another tool in the same task. */
export async function handInTeam(team: string, role: string, tool: string, note: string): Promise<void> {
  await workAction('team_swap', { team, role, tool, ...(note ? { note } : {}) });
}
