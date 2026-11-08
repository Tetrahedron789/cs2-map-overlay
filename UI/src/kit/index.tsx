// A small UI kit for in-game mod panels: one look shared by every mod that copies this
// folder. Only plain React and the kit's own styles; colours live in _tokens.scss.
import React, { CSSProperties, ReactNode, useEffect, useRef, useState } from "react";
import s from "./kit.module.scss";

export const cx = (...names: (string | false | null | undefined)[]) => names.filter(Boolean).join(" ");

// ---- Panel shell --------------------------------------------------------------------

interface ModPanelProps {
  icon: string;
  title: string;
  subtitle?: ReactNode;
  actions?: ReactNode;
  footer?: ReactNode;
  onClose: () => void;
  closeLabel?: string;
  style?: CSSProperties;
  bodyMaxHeight?: string;
  dragKey?: string; // panels with the same key share where they were dragged to
  children: ReactNode;
}

// Where panels were dragged to, kept while they are closed (for this game session).
const panelOffsets = new Map<string, { x: number; y: number }>();
const kKeepOnScreen = 44; // px of the panel's top that stay visible at the bottom of the screen

export const ModPanel = ({ icon, title, subtitle, actions, footer, onClose, style, bodyMaxHeight, dragKey, children }: ModPanelProps) => {
  const key = dragKey ?? title;
  const [offset, setOffset] = useState(panelOffsets.get(key) ?? { x: 0, y: 0 });
  const panelRef = useRef<HTMLDivElement>(null);
  const stopDrag = useRef<(() => void) | null>(null);

  useEffect(() => () => stopDrag.current?.(), []);

  // Dragged by the icon and title; the header's buttons keep working as usual.
  const startDrag = (e: React.MouseEvent) => {
    if (e.button !== 0 || !panelRef.current) return;
    e.preventDefault();
    const rect = panelRef.current.getBoundingClientRect();
    const startX = e.clientX, startY = e.clientY, from = offset;
    const clamp = (v: number, low: number, high: number) => Math.max(low, Math.min(high, v));
    const move = (ev: MouseEvent) => {
      const next = {
        x: from.x + clamp(ev.clientX - startX, -rect.left, window.innerWidth - rect.right),
        y: from.y + clamp(ev.clientY - startY, -rect.top, window.innerHeight - rect.top - kKeepOnScreen),
      };
      panelOffsets.set(key, next);
      setOffset(next);
    };
    const up = () => stopDrag.current?.();
    stopDrag.current = () => {
      document.removeEventListener("mousemove", move);
      document.removeEventListener("mouseup", up);
      stopDrag.current = null;
    };
    document.addEventListener("mousemove", move);
    document.addEventListener("mouseup", up);
  };

  return (
    <div ref={panelRef} className={s.panel} style={{ ...style, transform: `translate(${offset.x}px, ${offset.y}px)` }}>
      <div className={s.header}>
        <div className={s.dragHandle} onMouseDown={startDrag}>
          <div className={s.badge}>
            <img className={s.badgeIcon} src={icon} />
          </div>
          <div className={s.heading}>
            <div className={s.title}>{title}</div>
            {subtitle && <div className={s.subtitle}>{subtitle}</div>}
          </div>
        </div>
        <div className={s.headerActions}>
          {actions}
          {/* Drawn with CSS: the game's UI font has no ✕ glyph. */}
          <button className={s.iconButton} onClick={onClose}>
            <span className={s.closeIcon}>
              <span className={s.closeBar} />
              <span className={cx(s.closeBar, s.closeBarCross)} />
            </span>
          </button>
        </div>
      </div>
      <div className={s.body} style={bodyMaxHeight ? { maxHeight: bodyMaxHeight } : undefined}>
        {children}
      </div>
      {footer && <div className={s.footer}>{footer}</div>}
    </div>
  );
};

// ---- Tabs ---------------------------------------------------------------------------

interface TabsProps<T> {
  options: T[];
  value: T;
  format: (option: T) => string;
  dimmed?: (option: T) => boolean; // shows the tab as unavailable; it can still be opened to read why
  onSelect: (option: T) => void;
}

export const Tabs = <T,>({ options, value, format, dimmed, onSelect }: TabsProps<T>) => (
  <div className={s.tabs}>
    {options.map((option, i) => (
      <button key={i} className={cx(s.tab, option === value && s.tabOn, dimmed?.(option) && s.tabDimmed)} onClick={() => onSelect(option)}>
        {format(option)}
      </button>
    ))}
  </div>
);

// ---- Sections -----------------------------------------------------------------------

// Open/closed state survives closing the panel (for this game session).
const openSections = new Map<string, boolean>();

interface SectionProps {
  id: string;
  title: string;
  summary?: ReactNode;
  defaultOpen?: boolean;
  children: ReactNode;
}

export const Section = ({ id, title, summary, defaultOpen = false, children }: SectionProps) => {
  const [open, setOpen] = useState(openSections.get(id) ?? defaultOpen);
  const toggle = () => {
    openSections.set(id, !open);
    setOpen(!open);
  };
  return (
    <div className={cx(s.section, open && s.sectionOpen)}>
      <button className={s.sectionHead} onClick={toggle}>
        <span className={cx(s.chevron, open && s.chevronOpen)}>›</span>
        <span className={s.sectionTitle}>{title}</span>
        {summary !== undefined && summary !== "" && <span className={s.summary}>{summary}</span>}
      </button>
      {open && <div className={s.sectionBody}>{children}</div>}
    </div>
  );
};

export const Stack = ({ children, last }: { children: ReactNode; last?: boolean }) => (
  <div className={cx(s.stack, last && s.last)}>{children}</div>
);

// ---- Text ---------------------------------------------------------------------------

export const Hint = ({ children, warning }: { children: ReactNode; warning?: boolean }) => (
  <div className={cx(s.hint, warning && s.warningText)}>{children}</div>
);

export const Caption = ({ children }: { children: ReactNode }) => <div className={s.caption}>{children}</div>;

export const LabelRow = ({ label, value }: { label: ReactNode; value: ReactNode }) => (
  <div className={s.labelRow}>
    <span className={s.label}>{label}</span>
    <span className={s.value}>{value}</span>
  </div>
);

// ---- Controls -----------------------------------------------------------------------

export const Switch = ({ value, onChange }: { value: boolean; onChange: (v: boolean) => void }) => (
  <button className={cx(s.switch, value && s.switchOn)} onClick={() => onChange(!value)}>
    <span className={s.knob} />
  </button>
);

export const ToggleRow = ({ label, value, onChange }: { label: string; value: boolean; onChange: (v: boolean) => void }) => (
  <button className={cx(s.row, s.toggleRow)} onClick={() => onChange(!value)}>
    <span className={s.label}>{label}</span>
    <span className={cx(s.switch, value && s.switchOn)}>
      <span className={s.knob} />
    </span>
  </button>
);

type ButtonKind = "default" | "primary" | "ghost";

interface ButtonProps {
  kind?: ButtonKind;
  wide?: boolean;
  onClick: () => void;
  children: ReactNode;
}

export const Button = ({ kind = "default", wide, onClick, children }: ButtonProps) => (
  <button className={cx(s.button, kind === "primary" && s.primary, kind === "ghost" && s.ghost, wide && s.wide)} onClick={onClick}>
    {children}
  </button>
);

export const Stepper = ({ label, value, onChange }: { label: string; value: ReactNode; onChange: (delta: number) => void }) => (
  <div className={s.row}>
    <span className={s.label}>{label}</span>
    <div className={s.stepper}>
      <button className={s.smallButton} onClick={() => onChange(-1)}>
        −
      </button>
      <span className={s.stepperValue}>{value}</span>
      <button className={s.smallButton} onClick={() => onChange(1)}>
        +
      </button>
    </div>
  </div>
);

interface SegmentedProps<T> {
  options: T[];
  value?: T;
  format: (option: T) => string;
  onSelect: (option: T) => void;
}

// One row of equal buttons; `value` marks the selected one, if any.
export const Segmented = <T,>({ options, value, format, onSelect }: SegmentedProps<T>) => (
  <div className={s.segmented}>
    {options.map((option, i) => (
      <button key={i} className={cx(s.segment, option === value && s.segmentOn)} onClick={() => onSelect(option)}>
        {format(option)}
      </button>
    ))}
  </div>
);

interface SliderProps {
  label: string;
  value: number;
  min?: number;
  max?: number;
  step?: number;
  format?: (v: number) => string;
  disabled?: boolean;
  onCommit: (value: number) => void;
}

// cs2/ui exposes no slider, so this is a small pointer-driven one. The value is shown
// live while dragging and sent once, on release.
export const Slider = ({ label, value, min = 0, max = 100, step = 1, format = (v) => `${v}`, disabled, onCommit }: SliderProps) => {
  const trackRef = useRef<HTMLDivElement>(null);
  const [dragValue, setDragValue] = useState<number | null>(null);
  const shown = dragValue ?? value;
  const clamp = (v: number) => Math.max(min, Math.min(max, v));
  const ratio = max > min ? (clamp(shown) - min) / (max - min) : 0;

  const valueAt = (clientX: number) => {
    const rect = trackRef.current!.getBoundingClientRect();
    const r = rect.width > 0 ? (clientX - rect.left) / rect.width : 0;
    return clamp(Math.round((min + Math.max(0, Math.min(1, r)) * (max - min)) / step) * step);
  };

  useEffect(() => {
    if (dragValue === null) return;
    const move = (e: MouseEvent) => setDragValue(valueAt(e.clientX));
    const up = (e: MouseEvent) => {
      const final = valueAt(e.clientX);
      setDragValue(null);
      onCommit(final);
    };
    document.addEventListener("mousemove", move);
    document.addEventListener("mouseup", up);
    return () => {
      document.removeEventListener("mousemove", move);
      document.removeEventListener("mouseup", up);
    };
  }, [dragValue !== null]);

  const nudge = (d: number) => onCommit(clamp(value + d * step * 5));

  return (
    <div className={cx(s.stack, disabled && s.disabled)}>
      <LabelRow label={label} value={format(shown)} />
      <div className={s.sliderLine}>
        <button className={s.smallButton} onClick={() => nudge(-1)}>
          −
        </button>
        <div
          ref={trackRef}
          className={s.track}
          onMouseDown={(e) => {
            if (e.button !== 0) return;
            setDragValue(valueAt(e.clientX));
          }}
        >
          <div className={s.rail} />
          <div className={s.fill} style={{ width: `${ratio * 100}%` }} />
          <div className={s.thumb} style={{ left: `${ratio * 100}%` }} />
        </div>
        <button className={s.smallButton} onClick={() => nudge(1)}>
          +
        </button>
      </div>
    </div>
  );
};

interface TextFieldProps {
  value: string;
  placeholder?: string;
  action: string;
  onChange?: (value: string) => void;
  onSubmit: (value: string) => void;
}

// Input with an action button; Enter does the same as the button.
export const TextField = ({ value, placeholder, action, onChange, onSubmit }: TextFieldProps) => (
  <div className={s.fieldRow}>
    <input
      className={s.input}
      type="text"
      value={value}
      placeholder={placeholder}
      onChange={(e) => onChange?.(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === "Enter") onSubmit(value);
      }}
    />
    <Button kind="primary" onClick={() => onSubmit(value)}>
      {action}
    </Button>
  </div>
);

// Keeps a local draft of a value owned by the game, refreshed when the game's value changes.
export const useDraft = (value: string): [string, (v: string) => void] => {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  return [draft, setDraft];
};

export const List = <T,>({ items, label, onPick }: { items: T[]; label: (item: T) => string; onPick: (item: T) => void }) => (
  <div className={s.list}>
    {items.map((item, i) => (
      <button key={i} className={s.listItem} onClick={() => onPick(item)}>
        {label(item)}
      </button>
    ))}
  </div>
);

interface CalloutProps {
  tone?: "warning" | "info";
  children: ReactNode;
  actions?: ReactNode;
}

export const Callout = ({ tone = "warning", children, actions }: CalloutProps) => (
  <div className={cx(s.callout, tone === "info" && s.calloutInfo)}>
    <div className={s.calloutText}>{children}</div>
    {actions && <div className={s.calloutActions}>{actions}</div>}
  </div>
);

export const Progress = ({ value }: { value: number }) => (
  <div className={s.progress}>
    <div className={s.progressFill} style={{ width: `${Math.max(0, Math.min(1, value)) * 100}%` }} />
  </div>
);

export const Meta = ({ left, right }: { left: ReactNode; right?: ReactNode }) => (
  <div className={s.meta}>
    <span>{left}</span>
    {right !== undefined && <span>{right}</span>}
  </div>
);
