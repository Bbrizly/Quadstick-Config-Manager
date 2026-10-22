import { useEffect, useState, type RefObject } from "react";

/** Past an exit's own end, how long its `finished` may lag before the
 *  overlay goes anyway. */
const EXIT_GRACE_MS = 150;
/** An exit whose timing cannot be read gets this long. */
const EXIT_FALLBACK_MS = 500;

/** An exit can play: the engine has Web Animations (jsdom does not), nobody
 *  asked for less motion, and the page is drawn. A hidden page holds its
 *  timeline still, so an exit there would never end. Otherwise a closed
 *  thing goes at once. */
function exitAnimates(): boolean {
  if (typeof Element === "undefined" || typeof Element.prototype.getAnimations !== "function") return false;
  if (document.visibilityState === "hidden") return false;
  if (document.documentElement.dataset["reduceMotion"] === "true") return false;
  return window.matchMedia?.("(prefers-reduced-motion: reduce)").matches !== true;
}

/** How long until the last exit should be over, in ms. */
function exitRemainingMs(exits: readonly Animation[]): number {
  let longest = 0;
  for (const animation of exits) {
    const end = Number(animation.effect?.getComputedTiming().endTime);
    const now = Number(animation.currentTime ?? 0);
    const left = Number.isFinite(end) && Number.isFinite(now) ? end - now : EXIT_FALLBACK_MS;
    longest = Math.max(longest, left);
  }
  return longest;
}

/**
 * Keeps a closed overlay on the page only while its `*-out` CSS animation
 * plays, then drops it. The wait is the animations' own `finished` promise,
 * so nothing ticks, and a reopen mid-exit simply cancels it. A timer at the
 * exit's own end backs it up, and a page that hides drops it at once: a
 * timeline that stops (a hidden page) never settles `finished`, and a closed
 * overlay must not sit there waiting for one.
 */
export function usePresence(
  open: boolean,
  ref: RefObject<HTMLElement | null>,
): { readonly mounted: boolean; readonly closing: boolean } {
  const [kept, setKept] = useState(open);
  if (open && !kept) setKept(true);
  if (!open && kept && !exitAnimates()) setKept(false);
  const closing = !open && kept;

  useEffect(() => {
    if (!closing) return undefined;
    let live = true;
    const drop = (): void => {
      if (live) setKept(false);
    };
    const exits = (ref.current?.getAnimations({ subtree: true }) ?? []).filter(
      (animation) => animation instanceof CSSAnimation && animation.animationName.endsWith("-out"),
    );
    void Promise.allSettled(exits.map((animation) => animation.finished)).then(drop);
    const timer = window.setTimeout(drop, exitRemainingMs(exits) + EXIT_GRACE_MS);
    const onVisibility = (): void => {
      if (document.visibilityState === "hidden") drop();
    };
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      live = false;
      window.clearTimeout(timer);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [closing, ref]);

  return { mounted: open || kept, closing };
}
