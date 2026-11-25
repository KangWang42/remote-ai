import { useEffect, useLayoutEffect, useRef, useState } from 'preact/hooks';

export interface MenuItem {
  label: string;
  /** Not available now: shown greyed, with the reason beside it. */
  disabled?: string | false;
  danger?: boolean;
  hint?: string;
  run?: () => void;
}

/** A separator between groups. */
export const SEP: MenuItem = { label: '-' };

interface Props {
  x: number;
  y: number;
  items: MenuItem[];
  onClose: () => void;
}

/** A menu at the pointer (right click or a "more" button), kept inside the window. */
export function ContextMenu({ x, y, items, onClose }: Props) {
  const ref = useRef<HTMLDivElement | null>(null);
  const [pos, setPos] = useState({ left: x, top: y });

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    setPos({
      left: Math.max(6, Math.min(x, window.innerWidth - r.width - 6)),
      top: Math.max(6, Math.min(y, window.innerHeight - r.height - 6)),
    });
  }, [x, y]);

  useEffect(() => {
    const away = (e: Event) => {
      if (ref.current && !ref.current.contains(e.target as Node)) onClose();
    };
    const key = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('mousedown', away, true);
    window.addEventListener('contextmenu', away, true);
    window.addEventListener('keydown', key);
    window.addEventListener('blur', onClose);
    window.addEventListener('resize', onClose);
    return () => {
      window.removeEventListener('mousedown', away, true);
      window.removeEventListener('contextmenu', away, true);
      window.removeEventListener('keydown', key);
      window.removeEventListener('blur', onClose);
      window.removeEventListener('resize', onClose);
    };
  }, [onClose]);

  return (
    <div class="ctx-menu" ref={ref} style={{ left: `${pos.left}px`, top: `${pos.top}px` }} role="menu">
      {items.map((item, i) =>
        item.label === '-' ? (
          <div key={i} class="ctx-sep" />
        ) : (
          <button
            key={i}
            type="button"
            role="menuitem"
            class={`ctx-item${item.danger ? ' danger' : ''}`}
            disabled={Boolean(item.disabled)}
            title={item.disabled || undefined}
            onClick={() => {
              onClose();
              item.run?.();
            }}
          >
            <span class="ctx-label">{item.label}</span>
            {item.disabled ? <span class="ctx-why">{item.disabled}</span> : item.hint ? <span class="ctx-hint">{item.hint}</span> : null}
          </button>
        ),
      )}
    </div>
  );
}

/** Which menu is open, and where: one at a time for the whole window. */
export interface MenuState {
  x: number;
  y: number;
  items: MenuItem[];
}
