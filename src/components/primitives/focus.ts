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
    // `button:not([disabled])` also matches a roving tab parked at -1.
    (element) =>
      element.tabIndex >= 0 &&
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

// WebKit does not show the ring for a script focus that follows a mouse focus
// (a dialog opened by click). Not in TypeScript's DOM types yet.
const KEYBOARD_FOCUS: FocusOptions & { focusVisible?: boolean } = { focusVisible: true };

// Where the last click or focus was, with its ancestors. A WebKit click never
// focuses a button and a removed control drops focus to body; browsers then
// Tab on from that spot, not from the top. Kept as a path so a removed node
// falls back to the nearest ancestor still on the page.
let startPath: Node[] = [];

/** Remembers where Tab goes on from once focus is lost to body. */
export function rememberTabStart(event: Event): void {
  const target = event.target;
  if (!(target instanceof Node) || target === document.body) return;
  // A dialog puts focus back on its opener when it closes.
  if (event.type === "focusin" && target instanceof Element && target.closest('[aria-modal="true"]') !== null) {
    return;
  }
  startPath = [];
  for (let node: Node | null = target; node !== null && node !== document.body; node = node.parentNode) {
    startPath.push(node);
  }
}

function tabStart(root: HTMLElement): Node | null {
  const active = document.activeElement;
  if (active instanceof HTMLElement && active !== root && active !== document.body && root.contains(active)) {
    return active;
  }
  return startPath.find((node) => node.isConnected && node !== root && root.contains(node)) ?? null;
}

/** Moves focus one stop through `root` in DOM order, wrapping. False when
    nothing in it can take focus. */
export function moveFocus(root: HTMLElement, backward: boolean): boolean {
  const stops = focusableElements(root);
  const count = stops.length;
  if (count === 0) return false;

  const from = tabStart(root);
  let start = backward ? count - 1 : 0;
  if (from !== null) {
    const at = stops.indexOf(from as HTMLElement);
    // The start can sit off the list (the main landmark after the skip link,
    // a dialog panel, a roving -1 tab): step from its place in the document.
    const next =
      at >= 0
        ? at + 1
        : stops.findIndex(
            (stop) => (from.compareDocumentPosition(stop) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0,
          );
    const after = next === -1 ? count : next;
    start = backward ? (at >= 0 ? at - 1 : after - 1) : after;
  }

  for (let step = 0; step < count; step += 1) {
    const index = (((start + (backward ? -step : step)) % count) + count) % count;
    const stop = stops[index]!;
    stop.focus(KEYBOARD_FOCUS);
    // A stop hidden by CSS refuses focus; try the next one.
    if (document.activeElement === stop) return true;
  }
  return false;
}

function isPlainTab(event: KeyboardEvent): boolean {
  return (
    event.key === "Tab" && !event.ctrlKey && !event.metaKey && !event.altKey && !event.isComposing
  );
}

export function trapTabKey(event: KeyboardEvent, root: HTMLElement): void {
  if (!isPlainTab(event)) {
    return;
  }
  // Always moved here, never left to the browser: WebKit skips buttons.
  event.preventDefault();
  if (!moveFocus(root, event.shiftKey)) {
    root.focus();
  }
}

function openModal(): HTMLElement | null {
  const modals = [...document.querySelectorAll<HTMLElement>('[aria-modal="true"]')].filter(
    (modal) => modal.closest("[inert]") === null && !modal.classList.contains("closing"),
  );
  return modals.at(-1) ?? null;
}

/** macOS WebKit follows the system Keyboard navigation setting, off by
    default, and then Tab only visits text fields and selects. Avalonia never
    did that, so every Tab is moved here in DOM order. Register it in the
    bubble phase so a control that owns Tab can preventDefault first. */
export function handleDocumentTab(event: KeyboardEvent): void {
  if (event.defaultPrevented || !isPlainTab(event)) {
    return;
  }
  if (moveFocus(openModal() ?? document.body, event.shiftKey)) {
    event.preventDefault();
  }
}

export function restoreFocus(target: Element | null): void {
  if (target instanceof HTMLElement && target.isConnected) {
    target.focus();
  }
}
