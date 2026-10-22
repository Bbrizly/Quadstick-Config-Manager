/**
 * Real DOM input for the smoke test: the same event sequence a pointer and a
 * keyboard produce, dispatched on the live page, so React and every native
 * listener see what they see when a person uses the app.
 */

const DEFAULT_WAIT_MS = 10_000;

export function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, ms));
}

/** Polls until `probe` returns a value that is not null, false or undefined. */
export async function waitFor<T>(
  what: string,
  probe: () => T | null | undefined | false | Promise<T | null | undefined | false>,
  timeoutMs = DEFAULT_WAIT_MS,
): Promise<T> {
  const start = performance.now();
  for (;;) {
    const value = await probe();
    if (value !== null && value !== undefined && value !== false) return value;
    if (performance.now() - start > timeoutMs) throw new Error(`timed out waiting for ${what}`);
    await sleep(50);
  }
}

function text(element: Element | null): string {
  return (element?.textContent ?? "").replace(/\s+/gu, " ").trim();
}

/** The accessible name, close enough for the controls this app draws. */
export function nameOf(element: Element): string {
  const label = element.getAttribute("aria-label");
  if (label !== null && label.trim() !== "") return label.trim();
  const labelledBy = element.getAttribute("aria-labelledby");
  if (labelledBy !== null) {
    return labelledBy.split(/\s+/u).map((id) => text(document.getElementById(id))).join(" ").trim();
  }
  if (element instanceof HTMLInputElement || element instanceof HTMLSelectElement || element instanceof HTMLTextAreaElement) {
    const wrapping = element.closest("label");
    if (wrapping !== null) return text(wrapping);
    if (element.id !== "") return text(document.querySelector(`label[for="${CSS.escape(element.id)}"]`));
  }
  return text(element);
}

function matches(name: string, want: string | RegExp): boolean {
  return typeof want === "string" ? name === want : want.test(name);
}

/** On screen and pressable: a closing overlay is inert, and a person cannot
 *  reach it, so neither does the walk (a synthetic click ignores inert). */
function visible(element: Element): boolean {
  if (element.closest("[inert]") !== null) return false;
  return !(element instanceof HTMLElement) || element.offsetParent !== null || element.getClientRects().length > 0;
}

/** Every visible, enabled element matching `selector` whose name fits. */
export function all(selector: string, name?: string | RegExp, root: ParentNode = document): HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>(selector)].filter(
    (element) => visible(element) && (name === undefined || matches(nameOf(element), name)),
  );
}

export function one(selector: string, name?: string | RegExp, root: ParentNode = document): HTMLElement | null {
  return all(selector, name, root)[0] ?? null;
}

export function button(name: string | RegExp, root: ParentNode = document): HTMLElement | null {
  return all("button:not(:disabled), [role=button]", name, root)[0] ?? null;
}

export function waitButton(name: string | RegExp, root: ParentNode = document): Promise<HTMLElement> {
  return waitFor(`button ${String(name)}`, () => button(name, root));
}

/** The open dialog (not one that is fading out) whose title or text fits. */
export function dialog(name: string | RegExp): HTMLElement | null {
  return all("dialog.dialog-panel:not(.closing)").find(
    (panel) => matches(nameOf(panel), name) || matches(text(panel), name),
  ) ?? null;
}

export function bodyHas(pattern: RegExp): boolean {
  return pattern.test(document.body.innerText);
}

function pointer(type: string, target: Element): void {
  const rect = target.getBoundingClientRect();
  const init = {
    bubbles: true,
    cancelable: true,
    composed: true,
    clientX: rect.left + rect.width / 2,
    clientY: rect.top + rect.height / 2,
    button: 0,
    pointerId: 1,
    pointerType: "mouse",
    isPrimary: true,
  };
  target.dispatchEvent(type.startsWith("pointer") ? new PointerEvent(type, init) : new MouseEvent(type, init));
}

/** Press, release, click: the order a mouse produces. */
export function click(target: Element): void {
  if (target instanceof HTMLElement) target.scrollIntoView({ block: "nearest" });
  pointer("pointerdown", target);
  pointer("mousedown", target);
  if (target instanceof HTMLElement) target.focus();
  pointer("pointerup", target);
  pointer("mouseup", target);
  if (target instanceof HTMLElement) target.click();
  else pointer("click", target);
}

export function key(
  name: string,
  modifiers: { ctrl?: boolean; meta?: boolean; shift?: boolean } = {},
  target: Element = document.activeElement ?? document.body,
): void {
  const init = {
    key: name,
    bubbles: true,
    cancelable: true,
    composed: true,
    ctrlKey: modifiers.ctrl ?? false,
    metaKey: modifiers.meta ?? false,
    shiftKey: modifiers.shift ?? false,
  };
  target.dispatchEvent(new KeyboardEvent("keydown", init));
  target.dispatchEvent(new KeyboardEvent("keyup", init));
}

function setNativeValue(element: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement, value: string): void {
  const prototype = Object.getPrototypeOf(element) as object;
  const setter = Object.getOwnPropertyDescriptor(prototype, "value")?.set;
  if (setter === undefined) throw new Error("no value setter");
  setter.call(element, value);
}

/** Clear the field, then type one character at a time. */
export async function typeInto(element: HTMLInputElement | HTMLTextAreaElement, value: string): Promise<void> {
  click(element);
  element.select();
  setNativeValue(element, "");
  element.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "deleteContentBackward" }));
  for (const character of value) {
    const init = { key: character, bubbles: true, cancelable: true };
    element.dispatchEvent(new KeyboardEvent("keydown", init));
    setNativeValue(element, element.value + character);
    element.dispatchEvent(new InputEvent("input", { bubbles: true, data: character, inputType: "insertText" }));
    element.dispatchEvent(new KeyboardEvent("keyup", init));
    await sleep(15);
  }
}

export function choose(select: HTMLSelectElement, value: string): void {
  click(select);
  setNativeValue(select, value);
  select.dispatchEvent(new Event("input", { bubbles: true }));
  select.dispatchEvent(new Event("change", { bubbles: true }));
}
