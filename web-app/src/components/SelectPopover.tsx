import { useEffect, useRef, useState } from 'preact/hooks';

export interface SelectOption {
  value: string;
  label: string;
  sub?: string;
  badge?: string;
  badgeStyle?: Record<string, string>;
}

interface Props {
  value: string;
  options: SelectOption[];
  placeholder?: string;
  compact?: boolean;
  onChange: (value: string) => void;
}

/**
 * Custom rounded popover selector replacing raw OS <select> dropdowns across the console.
 * Matches the mobile app's clean grouped list aesthetic with checkmark, soft surface and spring open.
 */
export function SelectPopover({ value, options, placeholder = '请选择', compact = false, onChange }: Props) {
  const [open, setOpen] = useState(false);
  const hostRef = useRef<HTMLDivElement | null>(null);
  const active = options.find((o) => o.value === value);

  useEffect(() => {
    if (!open) return;
    const onPointer = (e: MouseEvent) => {
      if (hostRef.current && !hostRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    window.addEventListener('mousedown', onPointer);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onPointer);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  return (
    <div class={`cselect${compact ? ' is-compact' : ''}${open ? ' is-open' : ''}`} ref={hostRef}>
      <button
        type="button"
        class="cselect-trigger"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
      >
        {active?.badge && (
          <span class="cselect-badge" style={active.badgeStyle as any}>
            {active.badge}
          </span>
        )}
        <span class="cselect-val">{active ? active.label : placeholder}</span>
        <svg class="cselect-chev" viewBox="0 0 20 20" width="15" height="15" aria-hidden="true">
          <path d="M5.5 7.5 10 12l4.5-4.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
        </svg>
      </button>

      {open && (
        <div class="cselect-menu" role="listbox">
          {options.map((opt) => {
            const selected = opt.value === value;
            return (
              <button
                key={opt.value}
                type="button"
                role="option"
                aria-selected={selected}
                class={`cselect-item${selected ? ' selected' : ''}`}
                onClick={() => {
                  onChange(opt.value);
                  setOpen(false);
                }}
              >
                {opt.badge && (
                  <span class="cselect-badge" style={opt.badgeStyle as any}>
                    {opt.badge}
                  </span>
                )}
                <span class="cselect-item-text">
                  <span class="cselect-item-label">{opt.label}</span>
                  {opt.sub && <span class="cselect-item-sub">{opt.sub}</span>}
                </span>
                {selected && (
                  <svg class="cselect-check" viewBox="0 0 20 20" width="16" height="16" aria-hidden="true">
                    <path d="M4.5 10.5 8.2 14l7.3-7.5" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" />
                  </svg>
                )}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
