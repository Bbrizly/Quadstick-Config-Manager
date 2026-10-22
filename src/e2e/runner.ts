/**
 * The self-driving smoke test (`pnpm e2e:mac`).
 *
 * Built only in Vite mode `e2e` against an `e2e` cargo build. It walks the
 * real UI with real DOM events against the real Rust backend, records each
 * step, every console error, unhandled rejection and failed native call, and
 * an idle check per screen, then writes a report and quits the app through
 * its own close path.
 */
import type { QcmClient } from "../platform";
import { all, bodyHas, button, choose, click, dialog, key, nameOf, one, sleep, typeInto, waitButton, waitFor } from "./dom";
import * as native from "./native";

interface Problem {
  readonly kind: "console.error" | "error" | "rejection" | "native";
  readonly text: string;
}

interface StepResult {
  readonly name: string;
  readonly status: "pass" | "fail" | "skip";
  readonly ms: number;
  readonly detail: string;
  readonly problems: readonly Problem[];
}

interface IdleResult {
  readonly screen: string;
  readonly rafCalls: number;
  readonly runningAnimations: readonly string[];
  readonly pageVisible: boolean;
  readonly pass: boolean;
}

/** Refusals the UI asks for on purpose and answers itself. */
const EXPECTED_REFUSALS = [
  // A drive with no prefs.csv yet: the page then opens an empty one.
  { call: "openDevicePreferences", code: "QCM_STORAGE_FILE_NOT_FOUND" },
] as const;

const problems: Problem[] = [];
const handled: string[] = [];
const steps: StepResult[] = [];
const idles: IdleResult[] = [];
let rafCalls = 0;

function describe(reason: unknown): string {
  if (reason instanceof Error) return `${reason.name}: ${reason.message}`;
  if (typeof reason === "object" && reason !== null) {
    try {
      return JSON.stringify(reason);
    } catch {
      return String(reason);
    }
  }
  return String(reason);
}

function installProbes(): void {
  const consoleError = console.error.bind(console);
  console.error = (...args: unknown[]) => {
    problems.push({ kind: "console.error", text: args.map(describe).join(" ") });
    consoleError(...args);
  };
  window.addEventListener("error", (event) => {
    problems.push({ kind: "error", text: `${event.message} @ ${event.filename}:${String(event.lineno)}` });
  });
  window.addEventListener("unhandledrejection", (event) => {
    problems.push({ kind: "rejection", text: describe(event.reason) });
  });
  const raf = window.requestAnimationFrame.bind(window);
  window.requestAnimationFrame = (callback) => {
    rafCalls += 1;
    return raf(callback);
  };
}

/** Every client call, recorded when the native side refuses it. */
function watch<T extends object>(target: T, prefix: string): T {
  const cache = new Map<PropertyKey, unknown>();
  return new Proxy(target, {
    get(object, property) {
      if (cache.has(property)) return cache.get(property);
      const value: unknown = Reflect.get(object, property, object);
      let out = value;
      if (typeof value === "function") {
        out = (...args: unknown[]) => {
          const result: unknown = (value as (...a: unknown[]) => unknown).apply(object, args);
          if (result instanceof Promise) {
            result.catch((reason: unknown) => {
              const call = `${prefix}${String(property)}`;
              const code = (reason as { payload?: { code?: string } } | null)?.payload?.code ?? "";
              const text = `${call}: ${code} ${describe(reason)}`;
              if (EXPECTED_REFUSALS.some((expected) => expected.call === call && expected.code === code)) handled.push(text);
              else problems.push({ kind: "native", text });
            });
          }
          return result;
        };
      } else if (typeof value === "object" && value !== null && (property === "console" || property === "controller")) {
        out = watch(value, `${String(property)}.`);
      }
      cache.set(property, out);
      return out;
    },
  });
}

async function step(name: string, body: () => Promise<string | void>): Promise<void> {
  const start = performance.now();
  const mark = problems.length;
  let status: StepResult["status"] = "pass";
  let detail = "";
  try {
    detail = (await body()) ?? "";
  } catch (reason) {
    status = "fail";
    detail = describe(reason);
  }
  // Let late rejections from this step land in it.
  await sleep(100);
  const found = problems.slice(mark);
  if (found.length > 0) status = "fail";
  const result = { name, status, ms: Math.round(performance.now() - start), detail, problems: found };
  steps.push(result);
  await native.log(`${status.toUpperCase()} ${name} (${String(result.ms)} ms) ${detail}`).catch(() => undefined);
  for (const problem of found) await native.log(`  ${problem.kind}: ${problem.text}`).catch(() => undefined);
  if (status === "fail") {
    await native.log(`  page: ${pageSummary()}`).catch(() => undefined);
    throw new Error(`step failed: ${name}`);
  }
}

/** What was on screen when a step failed. */
function pageSummary(): string {
  const headings = [...document.querySelectorAll("h1, h2")].map((element) => element.textContent?.trim()).join(" | ");
  const buttons = all("button").slice(0, 40).map(nameOf).join(" | ");
  const dialogs = [...document.querySelectorAll("dialog")].map((element) => `${nameOf(element)}${element.classList.contains("closing") ? " (closing)" : ""}`).join(" | ");
  const fields = all("input, select, textarea").slice(0, 30).map((element) => `${nameOf(element)}=${(element as HTMLInputElement).value}`).join(" | ");
  return `headings [${headings}] dialogs [${dialogs}] fields [${fields}] buttons [${buttons}]`;
}

function skip(name: string, detail: string): void {
  steps.push({ name, status: "skip", ms: 0, detail, problems: [] });
  void native.log(`SKIP ${name} ${detail}`).catch(() => undefined);
}

/** A hidden page (screen locked, window covered) draws nothing, so WebKit
 *  holds its animations still. Timing checks wait for a visible page. */
function hidden(): boolean {
  return document.visibilityState === "hidden";
}

/** At rest the page asks for no frames and runs no endless animation. */
async function idle(screen: string): Promise<void> {
  await sleep(800);
  const before = rafCalls;
  await sleep(2000);
  const raf = rafCalls - before;
  const running = document
    .getAnimations()
    .filter((animation) => animation.playState === "running")
    .map((animation) => {
      const name = animation instanceof CSSAnimation ? animation.animationName : animation.id || "animation";
      const endless = animation.effect?.getTiming().iterations === Infinity;
      return `${name}${endless ? " (infinite)" : ""}`;
    });
  // Hidden, a one-shot entrance is frozen at its start, not running: only an
  // endless one would still be work once the window shows.
  const busy = hidden() ? running.filter((name) => name.endsWith("(infinite)")) : running;
  const pass = raf === 0 && busy.length === 0;
  idles.push({ screen, rafCalls: raf, runningAnimations: running, pageVisible: !hidden(), pass });
  await native.log(
    `${pass ? "IDLE" : "BUSY"} ${screen}: rAF ${String(raf)}, running [${running.join(", ")}], page ${document.visibilityState}`,
  ).catch(() => undefined);
  if (!pass) throw new Error(`${screen} is not idle: ${String(raf)} rAF calls, running [${busy.join(", ")}]`);
}

const UNSAVED = "This profile has unsaved changes. Save them before leaving?";

function unsavedMark(): Element | null {
  return document.querySelector(`[aria-label="${UNSAVED}"]`);
}

function heading(level: 1 | 2, pattern: RegExp): HTMLElement | null {
  return all(`h${String(level)}`).find((element) => pattern.test(element.textContent ?? "")) ?? null;
}

/** The editor's (visually hidden) heading: a profile is open. */
function editorTitle(): HTMLElement | null {
  return document.getElementById("editor-title");
}

async function openLeftSip(): Promise<HTMLElement> {
  click(await waitButton(/^Left\. Soft Puff/u));
  return waitFor("mapping panel", () => one(".mapping-panel"));
}

const OUTPUT = /^Output for row \d+$/u;
/** The output field of the mapping the walk added, and the value it last
 *  saw there. A save can renumber rows (the version header goes on top), so
 *  the field is found again by its value when its row label moves. */
let outputName = "";
let outputValue = "";

function findOutput(root: ParentNode): HTMLInputElement | null {
  const named = one("input", outputName, root) as HTMLInputElement | null;
  if (named !== null && named.value === outputValue) return named;
  const moved = all("input", OUTPUT, root).find((element) => (element as HTMLInputElement).value === outputValue);
  if (moved === undefined) return named;
  outputName = moved.getAttribute("aria-label") ?? outputName;
  return moved as HTMLInputElement;
}

async function outputField(): Promise<HTMLInputElement> {
  const panel = one(".mapping-panel");
  const open = panel === null ? null : findOutput(panel);
  if (open !== null) return open;
  const opened = await openLeftSip();
  return waitFor(`field ${outputName}`, () => findOutput(opened));
}

/** Type a new output for the left sip and leave the field. */
async function setOutput(value: string): Promise<void> {
  const field = await outputField();
  await typeInto(field, value);
  field.blur();
  outputValue = value;
  await waitFor(`output ${value}`, async () => (await outputField()).value === value || null);
}

async function currentOutput(): Promise<string> {
  return (await outputField()).value;
}

/** Close an overlay and watch it leave: a `closing` frame, then gone. */
async function watchExit(element: HTMLElement, close: () => void): Promise<string> {
  const reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches
    || document.documentElement.dataset["reduceMotion"] === "true";
  let sawClosing = false;
  const observer = new MutationObserver(() => {
    if (element.classList.contains("closing") || element.closest(".closing") !== null) sawClosing = true;
  });
  observer.observe(element.parentElement ?? element, { attributes: true, subtree: true, attributeFilter: ["class"] });
  const start = performance.now();
  close();
  if (hidden() && !reduced) {
    // Its exit animation cannot advance until the window shows. Closed means
    // fading and inert, so nothing in it can be pressed again.
    await waitFor("overlay to start closing", () => sawClosing || !element.isConnected, 4000);
    observer.disconnect();
    const inert = !element.isConnected || element.closest("[inert]") !== null || element.classList.contains("closing");
    if (!inert) throw new Error("closing overlay is still interactive");
    return "page hidden (screen locked): exit started and the overlay is inert; unmount waits for the window to show";
  }
  await waitFor("overlay to unmount", () => !element.isConnected, 4000);
  observer.disconnect();
  const ms = Math.round(performance.now() - start);
  if (!sawClosing && !reduced) throw new Error("closed without its exit animation");
  return `${sawClosing ? "exit animation played" : "reduced motion, no exit animation"}, unmounted after ${String(ms)} ms`;
}

async function goHome(): Promise<void> {
  click(await waitButton("Home"));
}

async function waitHome(): Promise<void> {
  await waitFor("Home", () => heading(2, /^Start a profile$/u));
}

async function walk(): Promise<void> {
  await native.log(`walk started ${new Date().toISOString()}`).catch(() => undefined);

  await step("first-run consent and tour", async () => {
    click(await waitButton("No thanks, do not share usage data"));
    await waitFor("tour", () => bodyHas(/Step 1 of \d+/u));
    for (let n = 0; n < 12 && bodyHas(/Step \d+ of \d+/u); n += 1) {
      click(await waitButton("Next step"));
      await sleep(150);
    }
    await waitFor("tour to close", () => !bodyHas(/Step \d+ of \d+/u));
  });

  await step("Home", async () => {
    await waitHome();
    await idle("Home");
  });

  await step("create a new profile", async () => {
    click(await waitButton(/^Create a new profile/u));
    await waitFor("editor", editorTitle);
    await idle("Editor");
  });

  await step("edit a binding", async () => {
    click(await waitButton(/^Left\. Soft Puff/u));
    const panel = await waitFor("mapping panel", () => one(".mapping-panel"));
    const before = new Set(all("input", OUTPUT, panel).map((element) => element.getAttribute("aria-label")));
    click(await waitButton("Add a new mapping for the Left mouthpiece hole"));
    const added = await waitFor("the new mapping", () =>
      all("input", OUTPUT, panel).find((element) => !before.has(element.getAttribute("aria-label"))),
    );
    outputName = added.getAttribute("aria-label") ?? "";
    await setOutput("cross");
    await waitFor("unsaved mark", unsavedMark);
  });

  await step("save a new profile (Save As)", async () => {
    click(await waitButton(/^Save this profile\./u));
    await waitFor("saved", () => unsavedMark() === null);
    await waitFor("saved status", () => bodyHas(/Saved to untitled\.csv\./u));
  });

  await step("undo (button)", async () => {
    await setOutput("circle");
    click(await waitButton("Undo the last change. Shortcut Control Z"));
    await waitFor("undone", async () => (await currentOutput()) === "cross" || null);
    outputValue = "cross";
  });

  await step("undo (Cmd+Z)", async () => {
    await setOutput("circle");
    document.body.focus();
    key("z", { meta: true }, document.body);
    await waitFor("undone", async () => (await currentOutput()) === "cross" || null);
    outputValue = "cross";
  });

  skip("redo", "the app has no redo, as the Avalonia app has none");

  await step("save to the same file (Cmd+S)", async () => {
    await setOutput("square");
    await waitFor("unsaved mark", unsavedMark);
    key("s", { meta: true }, document.body);
    await waitFor("saved", () => unsavedMark() === null);
  });

  await step("problems bar open and close", async () => {
    click(await waitFor("problems toggle", () => one(".problems-toggle")));
    await waitFor("problems list", () => one(".problems-list"));
    click(await waitFor("problems toggle", () => one(".problems-toggle")));
    await waitFor("problems list gone", () => one(".problems-list") === null);
  });

  await step("dialog opens and closes", async () => {
    click(await waitButton(/^Save this profile as a reusable template/u));
    const panel = await waitFor("template dialog", () => dialog("Save as template"));
    const backdrop = panel.closest<HTMLElement>(".dialog-backdrop") ?? panel;
    return watchExit(backdrop, () => key("Escape"));
  });

  await step("leaving with unsaved changes asks first", async () => {
    await setOutput("triangle");
    await goHome();
    const prompt = await waitFor("unsaved prompt", () => dialog(/unsaved changes/u));
    click(await waitButton("Cancel", prompt));
    await waitFor("prompt gone", () => dialog(/unsaved changes/u) === null);
    if (editorTitle() === null || (await currentOutput()) !== "triangle") throw new Error("Cancel did not keep the edit open");
    await goHome();
    const again = await waitFor("unsaved prompt", () => dialog(/unsaved changes/u));
    click(await waitButton("Don't save", again));
    await waitHome();
  });

  await step("menu opens and closes", async () => {
    const trigger = await waitButton("More actions for untitled");
    click(trigger);
    const panelId = await waitFor("menu", () => trigger.getAttribute("aria-controls"));
    const panel = await waitFor("menu panel", () => document.getElementById(panelId));
    return watchExit(panel, () => key("Escape"));
  });

  await step("open the saved profile from Home", async () => {
    const card = await waitFor("card", () =>
      all("button", /untitled/u).find((element) => !(element.getAttribute("aria-label") ?? "").startsWith("More actions")),
    );
    click(card);
    await waitFor("editor", editorTitle);
    outputValue = "square";
    const value = await currentOutput();
    if (value !== "square") throw new Error(`saved output is ${value}, expected square`);
    await goHome();
    await waitHome();
  });

  await step("device view with no QuadStick", async () => {
    click(await waitButton("Manage files on your QuadStick"));
    await waitFor("device page", () => document.getElementById("device-preferences-title"));
    await waitFor("device scan", () => !bodyHas(/Looking for your QuadStick/u) || null);
    const empty = bodyHas(/No QuadStick is plugged in/u);
    await idle("Device");
    return empty ? "no-QuadStick state shown" : "a real QuadStick is attached; the empty state was not checked";
  });

  await step("device settings and files", async () => {
    click(await waitButton("Choose the QuadStick drive"));
    await waitFor("stand-in drive", () => bodyHas(/Found your QuadStick at QUADSTICK\./u));
    await waitFor("settings groups", () => button(/^Joystick\d+ settings$/u));
    await idle("Device settings");
    click(await waitButton(/^Manage the profile files on your QuadStick/u));
    const files = await waitFor("files window", () => dialog("Files on your QuadStick"));
    await waitFor("default.csv listed", () => /default\.csv/u.test(files.textContent ?? ""));
    const backdrop = files.closest<HTMLElement>(".dialog-backdrop") ?? files;
    const exit = await watchExit(backdrop, () => key("Escape"));
    await goHome();
    await waitHome();
    return exit;
  });

  let original = "system";
  await step("Settings page", async () => {
    click(await waitButton("Open Settings"));
    const select = await waitFor("language", () => one("select", "Language") as HTMLSelectElement | null);
    original = select.value;
    await idle("Settings");
  });

  await step("switch to Arabic and back", async () => {
    const select = await waitFor("language", () => one("select", "Language") as HTMLSelectElement | null);
    choose(select, "ar");
    await waitFor("Arabic", () => document.documentElement.lang === "ar" && document.documentElement.dir === "rtl");
    await waitFor("Arabic text", () => /[؀-ۿ]{3}/u.test(document.body.innerText));
    await idle("Settings (Arabic)");
    const back = await waitFor("language", () =>
      [...document.querySelectorAll<HTMLSelectElement>("select")].find((element) =>
        [...element.options].some((option) => option.value === "ar"),
      ));
    choose(back, original);
    await waitFor("English", () => document.documentElement.dir === "ltr" && bodyHas(/Language/u));
    click(await waitFor("settings toggle", () => one(".shell-settings-button")));
    await waitHome();
  });

  await step("closing the window with unsaved changes asks first", async () => {
    const card = await waitFor("card", () =>
      all("button", /untitled/u).find((element) => !(element.getAttribute("aria-label") ?? "").startsWith("More actions")),
    );
    click(card);
    await waitFor("editor", editorTitle);
    await setOutput("hexagon");
    await waitFor("unsaved mark", unsavedMark);
    await native.requestWindowClose();
    const prompt = await waitFor("unsaved prompt", () => dialog(/unsaved changes/u), 5000);
    click(await waitButton("Cancel", prompt));
    await waitFor("prompt gone", () => dialog(/unsaved changes/u) === null);
    await sleep(500);
    if (editorTitle() === null) throw new Error("Cancel did not keep the window and the edit");
  });
}

function report(passed: boolean, note: string): string {
  return JSON.stringify({ passed, note, steps, idle: idles, problems, handledRefusals: handled }, null, 2);
}

async function run(): Promise<void> {
  try {
    await walk();
  } catch (reason) {
    await native.writeReport(report(false, describe(reason))).catch(() => undefined);
    await native.exit(1);
    return;
  }
  // The last step quits through the app's own path: a close request, the
  // prompt, Don't save. The runner script checks the process then exits 0.
  try {
    await native.requestWindowClose();
    const prompt = await waitFor("unsaved prompt", () => dialog(/unsaved changes/u), 5000);
    await native.writeReport(report(true, "quit: Don't save on the close prompt; the runner checks the process exits 0"));
    click(await waitButton("Don't save", prompt));
    await sleep(8000);
    throw new Error("still running 8 s after Don't save");
  } catch (reason) {
    steps.push({ name: "quit", status: "fail", ms: 0, detail: describe(reason), problems: [] });
    await native.writeReport(report(false, describe(reason))).catch(() => undefined);
    await native.exit(1);
  }
}

/** Probes go in before the app mounts; the walk starts once it has. */
export function startE2e(client: QcmClient): QcmClient {
  installProbes();
  window.setTimeout(() => void run(), 0);
  return watch(client, "");
}
