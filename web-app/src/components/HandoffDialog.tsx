import { useEffect, useMemo, useState } from 'preact/hooks';
import * as act from '../runtime/actions';
import { workAction } from '../runtime/api';
import { canSay, type Thread } from '../runtime/threads';
import { toolBadgeStyle, toolLabel, toolMeta, type DeviceInfo } from '../runtime/types';
import { Dialog } from './Dialog';

interface Props {
  from: Thread;
  device: DeviceInfo | null;
  threads: Thread[];
  onClose: () => void;
  /** A new conversation was begun with the hand-over: its terminal. */
  onStarted: (terminal: string) => void;
  onDone: (message: string) => void;
}

interface Offer {
  key: string;
  tool: string;
  label: string;
  detail: string;
  /** A new conversation of this tool, or the open conversation `to`. */
  fresh: boolean;
  to?: Thread;
}

interface Membership {
  team: string;
  role: string;
}

const NOTES = ['接着完成剩下的工作', '先检查已做的改动，再继续', '只总结进展和下一步，不改代码'];
const NEW_TOOLS = ['claude', 'codex', 'agy'];

/** Gives a conversation's work to another AI: a new conversation that begins with a summary of it, or one that is open. */
export function HandoffDialog({ from, device, threads, onClose, onStarted, onDone }: Props) {
  const features = device?.features || [];
  const [member, setMember] = useState<Membership | null | undefined>(from.team ? undefined : null);
  const [choice, setChoice] = useState('');
  const [note, setNote] = useState('');
  const [preview, setPreview] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState('');

  // a collaboration member is handed over inside its task: the task goes on with the AI that takes over
  useEffect(() => {
    if (!from.team || !from.terminal) return;
    workAction<{ teams?: any[] }>('teams')
      .then((res) => {
        const team = (res.teams || []).find((x) => x.id === from.team);
        const role = team && Object.keys(team.members || {}).find((r) => team.members[r] === from.terminal!.id);
        setMember(role && ['running', 'paused'].includes(team.state) ? { team: team.id, role } : null);
      })
      .catch(() => setMember(null));
  }, [from.key]);

  const remaining = (tool: string) => {
    const q = (device?.quotas || []).find((one) => one.tool === tool);
    const all = q?.windows || [];
    const main = all.filter((w) => !w.group);
    const pool = main.length ? main : all;
    if (!pool.length) return '';
    const least = Math.min(...pool.map((w) => Math.max(0, Math.min(100, 100 - (Number(w.used) || 0)))));
    return ` · 剩余额度 ${Math.round(least)}%`;
  };

  const offers = useMemo<Offer[]>(() => {
    const allTools = device?.tools || [];
    const tools = allTools.filter(
      (tool) => NEW_TOOLS.includes(tool) && features.includes('chat') && (tool !== 'agy' || features.includes('chat-agy')),
    );
    const flaggedTeam = allTools.filter((tool) => features.includes(`${tool}-team`));
    const teamTools = flaggedTeam.length > 0 ? flaggedTeam : tools;
    if (member) {
      return teamTools
        .filter((tool) => tool !== from.tool)
        .map((tool) => ({ key: `team:${tool}`, tool, label: toolLabel(tool), detail: `在协作任务中接手${remaining(tool)}`, fresh: true }));
    }
    const fresh: Offer[] =
      from.project && features.includes('brief')
        ? tools.map((tool) => ({
            key: `new:${tool}`,
            tool,
            label: `新对话 · ${toolLabel(tool)}`,
            detail: `在 ${from.project} 中开始${tool === from.tool ? '，同一工具重新开始' : ''}${remaining(tool)}`,
            fresh: true,
          }))
        : [];
    const open: Offer[] = threads
      .filter((t) => t.key !== from.key && t.session && t.runtime !== 'idle' && t.runtime !== 'screen' && canSay(t).ok)
      .map((t) => ({ key: t.key, tool: t.tool, label: t.title, detail: `${toolLabel(t.tool)} · ${t.project || '其它文件夹'}`, fresh: false, to: t }));
    // another tool first: the usual reason for a hand-over is that this one has run out
    return [...fresh, ...open].sort((a, b) => Number(a.tool === from.tool) - Number(b.tool === from.tool));
  }, [device, threads, member, from.key]);

  useEffect(() => {
    if (!offers.some((o) => o.key === choice)) setChoice(offers[0]?.key || '');
  }, [offers]);

  const chosen = offers.find((o) => o.key === choice);

  const showPreview = async () => {
    setPreview('');
    try {
      setPreview(await act.brief(from, note.trim()));
    } catch (err: any) {
      setPreview(null);
      setProblem(err?.message || '读不到交接内容');
    }
  };

  const go = async () => {
    if (!chosen || busy) return;
    setBusy(true);
    setProblem('');
    try {
      if (member) {
        await act.handInTeam(member.team, member.role, chosen.tool, note.trim());
        onDone(`已在协作中交给 ${toolLabel(chosen.tool)}`);
      } else if (chosen.fresh) {
        onStarted(await act.handToNew(from, chosen.tool, note.trim()));
      } else if (chosen.to) {
        await act.handToOpen(from, chosen.to, note.trim());
        onDone(`已交给“${chosen.label}”`);
      }
    } catch (err: any) {
      setProblem(err?.message || '没有交出去');
      setBusy(false);
    }
  };

  const title = member ? '在协作中换一个 AI' : '交给另一个 AI';
  return (
    <Dialog
      title={title}
      onClose={onClose}
      foot={
        <>
          {!member && features.includes('brief') && (
            <button type="button" class="btn quiet" onClick={showPreview} disabled={busy}>
              预览交接内容
            </button>
          )}
          <span class="grow" />
          <button type="button" class="btn quiet" onClick={onClose}>
            取消
          </button>
          <button type="button" class="btn primary" disabled={!chosen || busy || member === undefined} onClick={go}>
            {busy ? '正在交接…' : !chosen ? '交接' : chosen.fresh ? `交给 ${toolLabel(chosen.tool)}` : `交给“${chosen.label}”`}
          </button>
        </>
      }
    >
      <div class="dlg-who">
        <span class="tool-mark" style={toolBadgeStyle(from.tool)}>
          {toolMeta(from.tool).mono}
        </span>
        <span>
          <strong>{from.title}</strong>
          <span class="muted">
            {' '}
            · {toolLabel(from.tool)}
            {from.project ? ` · ${from.project}` : ''}
          </span>
        </span>
      </div>

      {member === undefined ? (
        <div class="muted">正在读取协作任务…</div>
      ) : offers.length === 0 ? (
        <div class="banner">{from.project ? '电脑上没有可以接手的 AI 或开着的对话' : '这段对话不在项目里，只能交给开着的对话；现在没有'}</div>
      ) : (
        <div class="pick-list" role="radiogroup" aria-label="交给">
          {offers.map((o) => (
            <button
              key={o.key}
              type="button"
              role="radio"
              aria-checked={o.key === choice}
              class={`pick${o.key === choice ? ' on' : ''}`}
              onClick={() => setChoice(o.key)}
            >
              <span class="tool-mark" style={toolBadgeStyle(o.tool)}>
                {toolMeta(o.tool).mono}
              </span>
              <span class="pick-text">
                <strong>{o.label}</strong>
                <span>{o.detail}</span>
              </span>
            </button>
          ))}
        </div>
      )}

      <label class="field">
        <span>补充说明（可不填）</span>
        <textarea rows={3} maxLength={1000} value={note} onInput={(e) => setNote((e.target as HTMLTextAreaElement).value)} />
      </label>
      <div class="chips">
        {NOTES.map((n) => (
          <button
            key={n}
            type="button"
            class="chip"
            onClick={() => setNote((v) => (v.trim() ? `${v.trim()}\n${n}` : n).slice(0, 1000))}
          >
            {n}
          </button>
        ))}
      </div>

      {preview !== null && (
        <div class="brief">
          {preview === '' ? <div class="muted">正在整理…</div> : <pre>{preview}</pre>}
          {preview && <div class="muted small">{preview.length} 字 · 由电脑从对话记录整理</div>}
        </div>
      )}
      {problem && <div class="banner bad">{problem}</div>}
    </Dialog>
  );
}
