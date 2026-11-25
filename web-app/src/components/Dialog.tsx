import type { ComponentChildren } from 'preact';
import { useEffect } from 'preact/hooks';

interface Props {
  title: string;
  wide?: boolean;
  onClose: () => void;
  foot?: ComponentChildren;
  children: ComponentChildren;
}

/** A dialog over the window: Escape or a click outside closes it. */
export function Dialog({ title, wide, onClose, foot, children }: Props) {
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [onClose]);

  return (
    <div class="dlg-back" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div class={`dlg${wide ? ' wide' : ''}`} role="dialog" aria-modal="true" aria-label={title}>
        <div class="dlg-head">
          <h2>{title}</h2>
          <button type="button" class="icon-btn" onClick={onClose} aria-label="关闭">
            ×
          </button>
        </div>
        <div class="dlg-body">{children}</div>
        {foot && <div class="dlg-foot">{foot}</div>}
      </div>
    </div>
  );
}
