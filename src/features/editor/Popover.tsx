import { useEffect, useId, useRef, useState, type ReactNode } from "react";

export interface PopoverProps {
  /** The trigger's content (an icon, a label). */
  readonly label: ReactNode;
  /** The trigger's accessible name. */
  readonly name: string;
  readonly title?: string;
  readonly className?: string;
  readonly disabled?: boolean;
  /** Pinned to the trigger's right edge, as Avalonia's BottomEdgeAlignedRight. */
  readonly alignEnd?: boolean;
  readonly children: (close: () => void) => ReactNode;
}

/**
 * A flyout (`Flyout`/`MenuFlyout`): a button that opens a panel under it.
 * Escape or a press outside closes it and focus goes back to the button. The
 * listeners exist only while it is open, so a closed one costs nothing.
 */
export function Popover({ label, name, title, className, disabled = false, alignEnd = false, children }: PopoverProps) {
  const [open, setOpen] = useState(false);
  const panelId = useId();
  const triggerId = useId();
  const rootRef = useRef<HTMLSpanElement>(null);
  // Closed from inside (Escape, a choice): focus goes back to the button.
  const backToTrigger = (): void => {
    document.getElementById(triggerId)?.focus();
  };

  useEffect(() => {
    if (!open) return undefined;
    const onKey = (event: KeyboardEvent): void => {
      if (event.key !== "Escape") return;
      event.preventDefault();
      setOpen(false);
      document.getElementById(triggerId)?.focus();
    };
    const onPointer = (event: PointerEvent): void => {
      if (rootRef.current?.contains(event.target as Node) === true) return;
      setOpen(false);
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("pointerdown", onPointer);
    const first = rootRef.current?.querySelector<HTMLElement>(
      `#${CSS.escape(panelId)} button:not(:disabled), #${CSS.escape(panelId)} [tabindex="-1"]`,
    );
    first?.focus();
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("pointerdown", onPointer);
    };
  }, [open, panelId, triggerId]);

  const close = (): void => {
    setOpen(false);
    backToTrigger();
  };

  return (
    <span className="popover-root" ref={rootRef}>
      <button
        id={triggerId}
        type="button"
        className={className}
        disabled={disabled}
        aria-label={name}
        title={title}
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        onClick={() => setOpen((value) => !value)}
      >
        {label}
      </button>
      {open ? (
        <div id={panelId} className={alignEnd ? "popover-panel end" : "popover-panel"}>
          {children(close)}
        </div>
      ) : null}
    </span>
  );
}

/** The `?` beside a heading: a title and a few sentences (`ShowInfoFlyout`). */
export function InfoButton({
  name,
  title,
  body,
  className = "icon quiet info-button",
  label = "?",
}: {
  readonly name: string;
  readonly title: string;
  readonly body: string;
  readonly className?: string;
  readonly label?: ReactNode;
}) {
  return (
    <Popover label={label} name={name} title={title} className={className}>
      {() => (
        <div className="info-flyout" tabIndex={-1}>
          <strong>{title}</strong>
          {body.split(/\n{2,}/u).map((paragraph) => (
            <p key={paragraph}>{paragraph}</p>
          ))}
        </div>
      )}
    </Popover>
  );
}
