import { useEffect, useState, type RefObject } from "react";

/** An exit can play: the engine has Web Animations (jsdom does not) and
 *  nobody asked for less motion. Otherwise a closed thing goes at once. */
function exitAnimates(): boolean {
  if (typeof Element === "undefined" || typeof Element.prototype.getAnimations !== "function") return false;
  if (document.documentElement.dataset["reduceMotion"] === "true") return false;
  return window.matchMedia?.("(prefers-reduced-motion: reduce)").matches !== true;
}

/**
 * Keeps a closed overlay on the page only while its `*-out` CSS animation
 * plays, then drops it. The wait is the animations' own `finished` promise,
 * so nothing ticks, and a reopen mid-exit simply cancels it.
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
    const exits = (ref.current?.getAnimations({ subtree: true }) ?? []).filter(
      (animation) => animation instanceof CSSAnimation && animation.animationName.endsWith("-out"),
    );
    void Promise.allSettled(exits.map((animation) => animation.finished)).then(() => {
      if (live) setKept(false);
    });
    return () => {
      live = false;
    };
  }, [closing, ref]);

  return { mounted: open || kept, closing };
}
