import { useEffect, useId, useRef, type ReactNode } from "react";

import { focusFirst, restoreFocus, trapTabKey } from "./focus";

export interface DialogProps {
  readonly open: boolean;
  readonly title: string;
  readonly onClose: () => void;
  readonly children: ReactNode;
  readonly actions?: ReactNode;
}

export function Dialog({ open, title, onClose, children, actions }: DialogProps) {
  const panelRef = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    if (!open) {
      return undefined;
    }

    const previous = document.activeElement;
    const panel = panelRef.current;
    if (panel === null) {
      return undefined;
    }

    focusFirst(panel);

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        onClose();
        return;
      }
      trapTabKey(event, panel);
    };

    document.addEventListener("keydown", onKeyDown, true);
    return () => {
      document.removeEventListener("keydown", onKeyDown, true);
      restoreFocus(previous);
    };
  }, [open, onClose]);

  if (!open) {
    return null;
  }

  return (
    <div className="dialog-backdrop" data-testid="dialog-backdrop">
      <dialog
        ref={panelRef}
        open
        className="dialog-panel"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
      >
        <h2 id={titleId} className="dialog-title">
          {title}
        </h2>
        {children}
        {actions === undefined ? null : <div className="dialog-actions">{actions}</div>}
      </dialog>
    </div>
  );
}
