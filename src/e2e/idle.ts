/**
 * The idle rule of the smoke test: at rest a screen asks for no frames and
 * holds no animation. "At rest" is measured, not slept for: every one-shot
 * animation on screen is waited out through its `finished` promise, then a
 * quiet window watches for anything that starts again.
 *
 * A person's mouse over the test window is real input, not the app moving on
 * its own: a hover transition that starts right after a trusted pointer event
 * is logged as excused rather than failed (the walk's own events are never
 * trusted, so it cannot excuse itself).
 */

/** The parts of a Web Animation the check reads (a CSSAnimation, a
 *  CSSTransition, or one from element.animate). */
export interface AnimationLike {
  readonly animationName?: string;
  readonly transitionProperty?: string;
  readonly id?: string;
  readonly playState: string;
  readonly startTime: number | null | CSSNumberish;
  readonly finished: Promise<unknown>;
  readonly effect: { readonly target?: Element | null; getTiming(): { iterations?: number } } | null;
}

export interface AnimationInfo {
  readonly name: string;
  readonly target: string;
  readonly startedAt: number | null;
  readonly endless: boolean;
}

export interface StartRecord {
  readonly name: string;
  readonly target: string;
  readonly at: number;
  readonly byPointer: boolean;
}

/** A hover transition this soon after a real pointer event is the pointer's. */
const POINTER_GRACE_MS = 300;

/** tag#id.class "accessible label", enough to find the element by eye. */
export function describeTarget(element: Element | null | undefined): string {
  if (element === null || element === undefined) return "(no element)";
  const id = element.id === "" ? "" : `#${element.id}`;
  const classes = [...element.classList].map((name) => `.${name}`).join("");
  const label = element.getAttribute("aria-label") ?? "";
  return `${element.tagName.toLowerCase()}${id}${classes}${label === "" ? "" : ` "${label}"`}`;
}

export function describeAnimation(animation: AnimationLike): AnimationInfo {
  const name = animation.animationName
    ?? (animation.transitionProperty === undefined ? undefined : `transition ${animation.transitionProperty}`)
    ?? (animation.id === undefined || animation.id === "" ? "animation" : animation.id);
  const start = animation.startTime;
  return {
    name,
    target: describeTarget(animation.effect?.target),
    startedAt: typeof start === "number" ? Math.round(start) : null,
    endless: animation.effect?.getTiming().iterations === Infinity,
  };
}

export function formatAnimation(info: AnimationInfo): string {
  const at = info.startedAt === null ? "not started" : `started ${String(info.startedAt)}ms`;
  return `${info.name} on ${info.target}, ${at}${info.endless ? ", infinite" : ""}`;
}

/** Wait out every finite animation in `list()`, again and again until none
 *  is left or `timeoutMs` passes. Returns what is still running: endless
 *  animations at once, and finite ones that outlived the wait. */
export async function settle(list: () => readonly AnimationLike[], timeoutMs: number): Promise<AnimationInfo[]> {
  const deadline = performance.now() + timeoutMs;
  for (;;) {
    const running = list().filter((animation) => animation.playState === "running" || animation.playState === "pending");
    const finite = running.filter((animation) => !describeAnimation(animation).endless);
    const left = deadline - performance.now();
    if (finite.length === 0 || left <= 0) return running.map(describeAnimation);
    let timer: ReturnType<typeof setTimeout> | undefined;
    await Promise.race([
      Promise.allSettled(finite.map((animation) => animation.finished)),
      new Promise<void>((resolve) => {
        timer = setTimeout(resolve, left);
      }),
    ]);
    clearTimeout(timer);
  }
}

/** Every animation and transition that starts while it listens, with when
 *  and on what, and whether a real pointer was just moving. */
export class IdleWatch {
  private readonly started: StartRecord[] = [];
  private lastPointer = Number.NEGATIVE_INFINITY;
  private readonly seen = new WeakSet<object>();

  private readonly root: Document;

  constructor(root: Document) {
    this.root = root;
  }

  private readonly onStart = (event: Event): void => {
    const target = event.target instanceof Element ? event.target : null;
    const at = performance.now();
    const transition = event.type === "transitionrun";
    const what = transition
      ? `transition ${(event as TransitionEvent).propertyName}`
      : (event as AnimationEvent).animationName;
    const pseudo = (event as TransitionEvent).pseudoElement ?? "";
    this.started.push({
      name: what,
      target: describeTarget(target) + pseudo,
      at: Math.round(at),
      byPointer: transition && at - this.lastPointer <= POINTER_GRACE_MS,
    });
  };

  private readonly onPointer = (event: Event): void => {
    if (event.isTrusted) this.notePointer(performance.now());
  };

  notePointer(at: number): void {
    this.lastPointer = at;
  }

  /** An element.animate() animation fires no DOM event, so the window also
   *  samples the list: one there now and not at `start` began in between. */
  sample(animations: readonly AnimationLike[]): void {
    for (const animation of animations) {
      if (this.seen.has(animation)) continue;
      this.seen.add(animation);
      // CSS ones were recorded by their own event, with a sharper time.
      if (animation.animationName !== undefined || animation.transitionProperty !== undefined) continue;
      const info = describeAnimation(animation);
      this.started.push({ name: info.name, target: info.target, at: Math.round(performance.now()), byPointer: false });
    }
  }

  start(present: readonly AnimationLike[] = []): void {
    this.started.length = 0;
    for (const animation of present) this.seen.add(animation);
    this.root.addEventListener("transitionrun", this.onStart, true);
    this.root.addEventListener("animationstart", this.onStart, true);
    for (const type of ["pointermove", "pointerover", "pointerout"]) this.root.addEventListener(type, this.onPointer, true);
  }

  stop(): StartRecord[] {
    this.root.removeEventListener("transitionrun", this.onStart, true);
    this.root.removeEventListener("animationstart", this.onStart, true);
    for (const type of ["pointermove", "pointerover", "pointerout"]) this.root.removeEventListener(type, this.onPointer, true);
    return [...this.started];
  }
}

export interface Verdict {
  readonly pass: boolean;
  readonly busy: readonly string[];
  readonly excused: readonly string[];
}

/** No frames, nothing started at rest (but for the person's own hover), and
 *  nothing left running. */
function line(record: StartRecord): string {
  return `${record.name} on ${record.target} at ${String(record.at)}ms`;
}

export function judge(input: { raf: number; started: readonly StartRecord[]; stuck: readonly AnimationInfo[] }): Verdict {
  const busy = [
    ...input.started.filter((record) => !record.byPointer).map(line),
    ...input.stuck.map(formatAnimation),
  ];
  const excused = input.started.filter((record) => record.byPointer).map((record) => `${line(record)} (real pointer)`);
  return { pass: input.raf === 0 && busy.length === 0, busy, excused };
}
