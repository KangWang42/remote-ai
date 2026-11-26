import { useState } from 'preact/hooks';
import { opId, workAction } from '../runtime/api';
import type { Thread } from '../runtime/threads';
import { toolBadgeStyle, toolLabel, toolMeta, type ChainEntry, type ScheduleEntry } from '../runtime/types';
import { Dialog } from './Dialog';
import { SelectPopover } from './SelectPopover';

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const WEEKDAYS: Array<[number, string]> = [
  [1, '周一'],
  [2, '周二'],
  [3, '周三'],
  [4, '周四'],
  [5, '周五'],
  [6, '周六'],
  [7, '周日'],
];

/** Threads that support scheduled prompts and automated relay chains (Claude Code & Codex with UUID sessions). */
export function automatableThreads(threads: Thread[]): Thread[] {
  return threads.filter((t) => (t.tool === 'claude' || t.tool === 'codex') && UUID_RE.test(t.session || ''));
}

function defaultOnceLocalValue(): string {
  const d = new Date(Date.now() + 30 * 60 * 1000);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function msToLocalInput(ms?: number): string {
  if (!ms || ms <= Date.now()) return defaultOnceLocalValue();
  const d = new Date(ms);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

interface ScheduleDialogProps {
  threads: Thread[];
  features: string[];
  editing?: ScheduleEntry | null;
  onSaved: () => void;
  onClose: () => void;
}

/** Modal to create or edit a scheduled prompt (`work_schedule_set`) for a Claude Code or Codex conversation. */
export function ScheduleDialog({ threads, features, editing, onSaved, onClose }: ScheduleDialogProps) {
  const candidates = automatableThreads(threads);
  const supportsDays = features.includes('schedule-days');
  const [session, setSession] = useState(editing?.session || candidates[0]?.session || '');
  const [kind, setKind] = useState<'once' | 'daily' | 'every'>(editing?.kind || 'daily');
  const [time, setTime] = useState(editing?.time || '09:30');
  const [days, setDays] = useState<number[]>(editing?.days || []);
  const [minutes, setMinutes] = useState(String(editing?.minutes || 60));
  const [onceAt, setOnceAt] = useState(() => msToLocalInput(editing?.at));
  const [text, setText] = useState(editing?.text || '');
  const [on, setOn] = useState(editing ? editing.on : true);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState('');

  const options = candidates.map((t) => ({
    value: t.session,
    label: t.title,
    sub: `${toolLabel(t.tool)}${t.project ? ` · ${t.project}` : ''}`,
    badge: toolMeta(t.tool).mono,
    badgeStyle: toolBadgeStyle(t.tool),
  }));

  const toggleDay = (day: number) => {
    setDays((prev) => (prev.includes(day) ? prev.filter((d) => d !== day) : [...prev, day].sort((a, b) => a - b)));
  };

  const save = async () => {
    if (busy) return;
    const words = text.trim();
    if (!session) {
      setProblem('请先选择要提问的对话');
      return;
    }
    if (!words) {
      setProblem('请输入到点自动发送的提问内容');
      return;
    }
    const item: Record<string, unknown> = {
      id: editing?.id || opId(),
      session,
      kind,
      text: words,
      on,
    };
    if (kind === 'daily') {
      item.time = time.trim();
      if (supportsDays && days.length > 0 && days.length < 7) item.days = days;
    } else if (kind === 'every') {
      const m = Number.parseInt(minutes, 10);
      if (!Number.isFinite(m) || m < 5 || m > 10080) {
        setProblem('间隔需在 5 分钟到 10080 分钟（7 天）之间');
        return;
      }
      item.minutes = m;
    } else {
      const at = Date.parse(onceAt);
      if (!Number.isFinite(at) || at <= Date.now()) {
        setProblem('请选择未来的执行时间');
        return;
      }
      item.at = at;
    }

    setBusy(true);
    setProblem('');
    try {
      await workAction('schedule_set', { item });
      onSaved();
    } catch (err: any) {
      setProblem(err?.message || '保存定时任务失败');
      setBusy(false);
    }
  };

  return (
    <Dialog
      title={editing ? '编辑定时任务' : '新建定时任务'}
      wide
      onClose={onClose}
      foot={
        <>
          <button type="button" class="btn quiet" onClick={onClose}>
            取消
          </button>
          <button type="button" class="btn primary" disabled={busy || candidates.length === 0} onClick={save}>
            {busy ? '正在保存…' : '保存'}
          </button>
        </>
      }
    >
      {candidates.length === 0 ? (
        <div class="banner">暂无可定时的 Claude Code 或 Codex 对话，请先发起一段对话后再创建定时任务。</div>
      ) : (
        <div class="auto-form">
          <div class="field">
            <span>目标对话</span>
            <SelectPopover value={session} options={options} onChange={setSession} />
          </div>

          <div class="field">
            <span>触发频率</span>
            <div class="seg">
              {(
                [
                  ['daily', '每天定时'],
                  ['every', '固定间隔'],
                  ['once', '单次执行'],
                ] as const
              ).map(([id, label]) => (
                <button key={id} type="button" class={kind === id ? 'on' : ''} onClick={() => setKind(id)}>
                  {label}
                </button>
              ))}
            </div>
          </div>

          {kind === 'daily' && (
            <>
              <label class="field">
                <span>执行时间</span>
                <input type="time" value={time} onInput={(e) => setTime((e.target as HTMLInputElement).value)} />
              </label>
              {supportsDays && (
                <div class="field">
                  <span>重复星期（不选则每天执行）</span>
                  <div class="day-chips">
                    {WEEKDAYS.map(([num, label]) => (
                      <button
                        key={num}
                        type="button"
                        class={`chip${days.includes(num) ? ' on' : ''}`}
                        onClick={() => toggleDay(num)}
                      >
                        {label}
                      </button>
                    ))}
                  </div>
                </div>
              )}
            </>
          )}

          {kind === 'every' && (
            <label class="field">
              <span>间隔分钟数（5 – 10080）</span>
              <input
                type="number"
                min={5}
                max={10080}
                value={minutes}
                onInput={(e) => setMinutes((e.target as HTMLInputElement).value)}
              />
            </label>
          )}

          {kind === 'once' && (
            <label class="field">
              <span>执行时间</span>
              <input
                type="datetime-local"
                value={onceAt}
                onInput={(e) => setOnceAt((e.target as HTMLInputElement).value)}
              />
            </label>
          )}

          <label class="field">
            <span>到点发送的内容</span>
            <textarea
              rows={4}
              placeholder="例如：检查今天的新增改动并生成简明日报"
              value={text}
              onInput={(e) => setText((e.target as HTMLTextAreaElement).value)}
            />
          </label>

          <label class="check">
            <input type="checkbox" checked={on} onChange={() => setOn((v) => !v)} />
            <span>保存后立即启用</span>
          </label>

          {problem && <div class="banner bad">{problem}</div>}
        </div>
      )}
    </Dialog>
  );
}

interface ChainDialogProps {
  threads: Thread[];
  editing?: ChainEntry | null;
  onSaved: () => void;
  onClose: () => void;
}

/** Modal to create or edit a conversation handoff chain (`work_chain_set`). */
export function ChainDialog({ threads, editing, onSaved, onClose }: ChainDialogProps) {
  const candidates = automatableThreads(threads);
  const [from, setFrom] = useState(editing?.from || candidates[0]?.session || '');
  const [to, setTo] = useState(editing?.to || candidates[1]?.session || candidates[0]?.session || '');
  const [rounds, setRounds] = useState(String(editing?.rounds || 3));
  const [text, setText] = useState(editing?.text || '');
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState('');

  const options = candidates.map((t) => ({
    value: t.session,
    label: t.title,
    sub: `${toolLabel(t.tool)}${t.project ? ` · ${t.project}` : ''}`,
    badge: toolMeta(t.tool).mono,
    badgeStyle: toolBadgeStyle(t.tool),
  }));

  const save = async () => {
    if (busy) return;
    const words = text.trim();
    if (!from || !to) {
      setProblem('请选择起点对话与接收对话');
      return;
    }
    if (from === to) {
      setProblem('不能接力给同一段对话');
      return;
    }
    if (!words) {
      setProblem('请输入交给下一段对话时的附带指令');
      return;
    }
    const r = Number.parseInt(rounds, 10);
    if (!Number.isFinite(r) || r < 1 || r > 10) {
      setProblem('最多触发次数需在 1 到 10 之间');
      return;
    }
    setBusy(true);
    setProblem('');
    try {
      await workAction('chain_set', {
        item: { id: editing?.id || opId(), from, to, text: words, rounds: r },
      });
      onSaved();
    } catch (err: any) {
      setProblem(err?.message || '保存对话接力失败');
      setBusy(false);
    }
  };

  return (
    <Dialog
      title={editing ? '编辑对话接力' : '新建对话接力'}
      wide
      onClose={onClose}
      foot={
        <>
          <button type="button" class="btn quiet" onClick={onClose}>
            取消
          </button>
          <button type="button" class="btn primary" disabled={busy || candidates.length < 2} onClick={save}>
            {busy ? '正在保存…' : '保存'}
          </button>
        </>
      }
    >
      {candidates.length < 2 ? (
        <div class="banner">至少需要两段 Claude Code 或 Codex 对话才能建立接力规则。</div>
      ) : (
        <div class="auto-form">
          <div class="field">
            <span>当这段对话完成一轮后</span>
            <SelectPopover value={from} options={options} onChange={setFrom} />
          </div>

          <div class="field">
            <span>自动把结果交给</span>
            <SelectPopover value={to} options={options} onChange={setTo} />
          </div>

          <label class="field">
            <span>附带指令</span>
            <textarea
              rows={4}
              placeholder="例如：请审查上面的实现，指出边界条件或潜在缺陷"
              value={text}
              onInput={(e) => setText((e.target as HTMLTextAreaElement).value)}
            />
          </label>

          <label class="field">
            <span>最多自动触发轮数（1 – 10）</span>
            <input
              type="number"
              min={1}
              max={10}
              value={rounds}
              onInput={(e) => setRounds((e.target as HTMLInputElement).value)}
            />
          </label>

          {problem && <div class="banner bad">{problem}</div>}
        </div>
      )}
    </Dialog>
  );
}
