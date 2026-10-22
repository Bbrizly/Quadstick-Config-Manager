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
    // A closing dialog or menu is inert while it fades: not a Tab stop.
    (element) =>
      !element.hasAttribute("hidden") &&
      element.getAttribute("aria-hidden") !== "true" &&
      element.closest("[inert]") === null,
  );
}

export function focusFirst(root: HTMLElement): void {
  // A disabled autofocus button (Install, while the drive is still being
  // looked for) cannot take focus, and focus would stay behind the dialog.
  const focusable = focusableElements(root);
  const preferred = root.querySelector<HTMLElement>("[data-autofocus]");
  const target = preferred !== null && focusable.includes(preferred) ? preferred : focusable[0];
  (target ?? root).focus();
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
  } else if (!event.shiftKey && (active === last || !root.contains(active))) {
    event.preventDefault();
    first?.focus();
  }
}

export function restoreFocus(target: Element | null): void {
  if (target instanceof HTMLElement && target.isConnected) {
    target.focus();
  }
}
