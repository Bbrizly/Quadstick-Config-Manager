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
import { type AnimationInfo, describeAnimation, formatAnimation, IdleWatch, judge, settle } from "./idle";
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
  readonly excused: readonly string[];
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
/** Where the last few frame requests came from, for a busy screen. */
const rafStacks: string[] = [];

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
    rafStacks.push(`${String(Math.round(performance.now()))}ms ${(new Error().stack ?? "").split("\n").slice(1, 6).join(" < ")}`);
    if (rafStacks.length > 20) rafStacks.shift();
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
    await native.log(`  visibility: ${hidden() ? `hidden, ${await hiddenReason()}` : "visible"}, focus ${String(document.hasFocus())}, animations [${animationNames().join(", ")}]`).catch(() => undefined);
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

/** A hidden page draws nothing, so WebKit holds its animations still and
 *  exit timing cannot be checked. */
function hidden(): boolean {
  return document.visibilityState === "hidden";
}

/** Why the page is hidden, from the native window and the session. */
async function hiddenReason(): Promise<string> {
  const state = await native.windowState().catch(() => null);
  if (state === null || !state.window) return "no main window";
  if (state.screenLocked === true) return "screen locked";
  if (state.displayAsleep === true) return "display asleep";
  if (state.visible === false) return "window not shown";
  if (state.minimized === true) return "window minimized";
  return `window shown but occluded (covered by another window or on another Space; focused ${String(state.focused)})`;
}

/** Bring the test window forward and wait for WebKit to call the page
 *  visible. Returns "" when visible, else the reason it is not. */
async function makeVisible(timeoutMs = 3000): Promise<string> {
  if (!hidden()) return "";
  await native.bringForward().catch(() => undefined);
  const start = performance.now();
  while (hidden() && performance.now() - start < timeoutMs) await sleep(100);
  return hidden() ? hiddenReason() : "";
}

function animationNames(): string[] {
  return document.getAnimations().map((animation) => `${formatAnimation(describeAnimation(animation))} (${animation.playState})`);
}

/** At rest the page asks for no frames and holds no animation at all.
 *  Rest is measured: entrances are waited out through `finished`, one-shot
 *  work (the first live-input status once HID is scanned) through a run of
 *  frame-free seconds, then a 2 s window must start nothing new. */
async function idle(screen: string): Promise<void> {
  const why = await makeVisible();
  // Hidden, WebKit holds animations still, so `finished` would never come.
  const wait = (ms: number): Promise<AnimationInfo[]> => settle(() => document.getAnimations(), hidden() ? 0 : ms);
  await wait(3000);
  for (let quietSince = rafCalls, waited = 0; waited < 5000; waited += 1000) {
    await sleep(1000);
    if (rafCalls === quietSince) break;
    quietSince = rafCalls;
  }
  // Still running after its wait: it would cross the window unseen.
  const unsettled = await wait(3000);
  const starts = new IdleWatch(document);
  const before = rafCalls;
  const windowStart = Math.round(performance.now());
  starts.start(document.getAnimations());
  // By the clock: a hidden page stretches each short timer.
  for (const end = performance.now() + 2000; performance.now() < end;) {
    await sleep(100);
    starts.sample(document.getAnimations());
  }
  const started = starts.stop();
  const raf = rafCalls - before;
  // Anything still running now started in the window (and is in `started`)
  // unless it never ends; give a late one its own duration to finish.
  const left = await wait(1000);
  const visible = !hidden();
  // Hidden, a one-shot entrance is frozen at its start rather than running:
  // only an endless one would still be work once the window shows.
  const stuck = [...new Set([...unsettled, ...left].map(formatAnimation))]
    .map((text) => [...unsettled, ...left].find((info) => formatAnimation(info) === text))
    .filter((info): info is AnimationInfo => info !== undefined && (visible || info.endless));
  const verdict = judge({ raf, started, stuck });
  idles.push({ screen, rafCalls: raf, runningAnimations: verdict.busy, excused: verdict.excused, pageVisible: visible, pass: verdict.pass });
  await native.log(
    `${verdict.pass ? "IDLE" : "BUSY"} ${screen}: rAF ${String(raf)}, window from ${String(windowStart)}ms, animations [${verdict.busy.join("; ")}]`
      + `${verdict.excused.length === 0 ? "" : `, excused [${verdict.excused.join("; ")}]`}`
      + `${visible ? "" : `, page hidden: ${why || await hiddenReason()}`}`,
  ).catch(() => undefined);
  if (!verdict.pass) {
    for (const stack of rafStacks.slice(-Math.max(raf, 1))) await native.log(`  rAF from ${stack}`).catch(() => undefined);
    throw new Error(`${screen} is not idle: ${String(raf)} rAF calls, animations [${verdict.busy.join("; ")}]`);
  }
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
  const why = await makeVisible();
  const start = performance.now();
  close();
  if (hidden() && !reduced) {
    // Its exit animation cannot advance until the window shows. Closed means
    // fading and inert, so nothing in it can be pressed again.
    await waitFor("overlay to start closing", () => sawClosing || !element.isConnected, 4000);
    observer.disconnect();
    const inert = !element.isConnected || element.closest("[inert]") !== null || element.classList.contains("closing");
    if (!inert) throw new Error("closing overlay is still interactive");
    return `page hidden (${why || await hiddenReason()}): exit started and the overlay is inert; unmount not observable`;
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
  const why = await makeVisible(5000);
  await native.log(why === "" ? "page visible" : `page hidden: ${why}`).catch(() => undefined);

  await step("first-run consent and tour", async () => {
    click(await waitButton("No thanks, do not share usage data"));
    await waitFor("tour", () => bodyHas(/Step 1 of \d+/u));
    // Only the open tour: once closed it fades out inert, and is not pressed.
    for (let n = 0, tour = dialog(/Step \d+ of \d+/u); n < 12 && tour !== null; n += 1, tour = dialog(/Step \d+ of \d+/u)) {
      click(await waitButton("Next step", tour));
      await sleep(150);
    }
    // Its exit is 140 ms; well past that it is stuck, not slow.
    await waitFor("tour to close", () => !bodyHas(/Step \d+ of \d+/u), 2000);
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
