import { useEffect, useId, useLayoutEffect, useRef, type ReactNode } from "react";

import { focusFirst, restoreFocus, trapTabKey } from "./focus";
import { usePresence } from "./presence";

export interface DialogProps {
  readonly open: boolean;
  readonly title: string;
  readonly onClose: () => void;
  readonly children: ReactNode;
  readonly actions?: ReactNode;
  /** A window-sized dialog (DeviceFilesWindow is 760 wide). */
  readonly wide?: boolean;
}

export function Dialog({ open, title, onClose, children, actions, wide = false }: DialogProps) {
  const panelRef = useRef<HTMLDialogElement>(null);
  const backdropRef = useRef<HTMLDivElement>(null);
  const titleId = useId();
  // Closed, it stays only while it fades out: inert, so nothing in it can be
  // pressed twice, and its keys are already gone with the effect below.
  const { mounted, closing } = usePresence(open, backdropRef);
  // Callers pass a fresh onClose each render; the effect must not rerun for
  // it, or focus jumps back to the first button mid-task.
  const onCloseRef = useRef(onClose);
  useEffect(() => {
    onCloseRef.current = onClose;
  });

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
      // A dialog opened inside this one (the files window's delete question)
      // owns the keys until it closes.
      if (panel.querySelector(".dialog-panel:not(.closing)") !== null) return;
      if (event.key === "Escape") {
        event.preventDefault();
        onCloseRef.current();
        return;
      }
      trapTabKey(event, panel);
    };

    document.addEventListener("keydown", onKeyDown, true);
    return () => {
      document.removeEventListener("keydown", onKeyDown, true);
      restoreFocus(previous);
    };
  }, [open]);

  // A dialog that swaps its buttons while open (Install's confirm, then
  // Close; the link window's question) unmounts the focused one, and focus
  // drops to the page behind. The panel itself holds focus only while every
  // button is busy. Either is pulled onto a button; focus in a flyout outside
  // the panel is where the user put it.
  useLayoutEffect(() => {
    const panel = panelRef.current;
    if (!open || panel === null) return;
    const active = document.activeElement;
    if (active !== null && active !== document.body && active !== panel) return;
    if (panel.querySelector(".dialog-panel:not(.closing)") !== null) return;
    focusFirst(panel);
  });

  if (!mounted) {
    return null;
  }

  return (
    <div
      ref={backdropRef}
      className={closing ? "dialog-backdrop closing" : "dialog-backdrop"}
      data-testid="dialog-backdrop"
      inert={closing}
    >
      <dialog
        ref={panelRef}
        open
        className={`dialog-panel${wide ? " wide" : ""}${closing ? " closing" : ""}`}
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
      >
        <div className="dialog-head">
          <h2 id={titleId} className="dialog-title">
            {title}
          </h2>
        </div>
        <div className="dialog-body">
          {children}
          {actions === undefined ? null : <div className="dialog-actions">{actions}</div>}
        </div>
      </dialog>
    </div>
  );
}
