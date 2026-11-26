import { useEffect, useState } from 'preact/hooks';

const labels: Record<string, string> = {
  starting: '正在启动', busy: '正在执行', confirm: '等待确认', idle: '等待消息', ended: '已结束', failed: '执行失败',
};

export function RunStatus({ phase, since }: { phase: string; since: number }) {
  const [now, setNow] = useState(Date.now);
  const [observed, setObserved] = useState(Date.now);
  useEffect(() => {
    if (phase !== 'busy') return;
    const start = Date.now();
    setObserved(start);
    setNow(start);
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [phase]);
  const seconds = Math.max(0, Math.floor((now - (since || observed)) / 1000));
  return <span class="run-status">
    <span role="status">{labels[phase] || labels.idle}</span>
    {phase === 'busy' && <span role="timer">{Math.floor(seconds / 60)}:{String(seconds % 60).padStart(2, '0')}</span>}
  </span>;
}
