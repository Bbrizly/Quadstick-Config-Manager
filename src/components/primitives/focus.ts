const FOCUSABLE = [
  "a[href]",
  "button:not([disabled])",
  "input:not([disabled])",
  "select:not([disabled])",
  "textarea:not([disabled])",
  "[tabindex]:not([tabindex='-1'])",
].join(",");

export function focusableElements(root: HTMLElement): HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>(FOCUSABLE)].filter(
    (element) => !element.hasAttribute("hidden") && element.getAttribute("aria-hidden") !== "true",
  );
}

export function focusFirst(root: HTMLElement): void {
  const preferred = root.querySelector<HTMLElement>("[data-autofocus]");
  (preferred ?? focusableElements(root)[0] ?? root).focus();
}

export function trapTabKey(event: KeyboardEvent, root: HTMLElement): void {
  if (event.key !== "Tab") {
    return;
  }

  const focusable = focusableElements(root);
  if (focusable.length === 0) {
    event.preventDefault();
    root.focus();
    return;
  }

  const first = focusable[0];
  const last = focusable.at(-1);
  const active = document.activeElement;

  if (event.shiftKey && (active === first || !root.contains(active))) {
    event.preventDefault();
    last?.focus();
  } else if (!event.shiftKey && active === last) {
    event.preventDefault();
    first?.focus();
  }
}

export function restoreFocus(target: Element | null): void {
  if (target instanceof HTMLElement && target.isConnected) {
    target.focus();
  }
}
