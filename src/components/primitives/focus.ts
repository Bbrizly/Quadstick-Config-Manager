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

// A busy button disables itself. Chromium then drops focus to body and never
// gives it back; WebKit keeps it. Put it back once the button is enabled
// again, unless the user has moved on or a dialog owns focus.
let lastFocused: HTMLElement | null = null;
let lastFocusVisible = true;
let parked: { element: HTMLElement; visible: boolean } | null = null;

function matchesFocusVisible(element: HTMLElement): boolean {
  try {
    return element.matches(":focus-visible");
  } catch {
    return true;
  }
}

function onBusyMutation(records: MutationRecord[]): void {
  for (const record of records) {
    const element = record.target;
    if (!(element instanceof HTMLElement)) continue;
    const disabled = element.hasAttribute("disabled");
    if (disabled && element === lastFocused) {
      const active = document.activeElement;
      // Focus already went somewhere else first: nothing to keep.
      if (active === element || active === null || active === document.body) {
        parked = { element, visible: lastFocusVisible };
      }
    } else if (!disabled && parked?.element === element) {
      const { visible } = parked;
      parked = null;
      const active = document.activeElement;
      if (active !== null && active !== document.body) continue;
      if (!element.isConnected || element.closest('[aria-modal="true"]') !== null || openModal() !== null) {
        continue;
      }
      // It never moved, and scrolling to it would cut short a scroll the
      // page started (the status line brought into view).
      element.focus({ ...(visible ? KEYBOARD_FOCUS : {}), preventScroll: true });
    }
  }
}

function onBusyFocusIn(event: Event): void {
  const target = event.target;
  lastFocused = target instanceof HTMLElement ? target : null;
  // Read now: Chromium has already dropped focus when the disable is seen.
  lastFocusVisible = lastFocused === null || matchesFocusVisible(lastFocused);
  if (parked !== null && target !== parked.element) parked = null;
}

function onBusyPointerDown(event: Event): void {
  // A WebKit click on another button leaves focus on body: the user moved on,
  // and the old button must not be parked when a later busy spell disables it.
  const target = event.target instanceof Node ? event.target : null;
  if (lastFocused !== null && (target === null || !lastFocused.contains(target))) lastFocused = null;
  if (parked !== null && (target === null || !parked.element.contains(target))) parked = null;
}

/** Gives focus back to a button that disabled itself while busy. Returns
    the teardown. */
export function keepFocusThroughBusy(): () => void {
  const observer = new MutationObserver(onBusyMutation);
  observer.observe(document.body, { attributes: true, subtree: true, attributeFilter: ["disabled"] });
  document.addEventListener("focusin", onBusyFocusIn);
  document.addEventListener("pointerdown", onBusyPointerDown, true);
  if (document.activeElement instanceof HTMLElement && document.activeElement !== document.body) {
    lastFocused = document.activeElement;
  }
  return () => {
    observer.disconnect();
    document.removeEventListener("focusin", onBusyFocusIn);
    document.removeEventListener("pointerdown", onBusyPointerDown, true);
    lastFocused = null;
    parked = null;
  };
}

export function restoreFocus(target: Element | null): void {
  if (target instanceof HTMLElement && target.isConnected) {
    target.focus();
  }
}
