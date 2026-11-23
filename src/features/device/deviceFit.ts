import { useLayoutEffect, useRef, useState } from "react";

// How the device page lays out for the CSS size it really has. Interface size
// is root `zoom`, which media queries never see, so the page measures its own
// width and the window's height in script, as the editor does (B-096, B-124).

/** The group rail and the 16 beside it. */
const RAIL = 210 + 16;
/** A row: 32 padding, a 128 label floor, 16 gap, slider 285 + 12 + number box 125. */
export const ROW_WIDTH = 32 + 128 + 16 + 285 + 12 + 125;
export const WIDE_WIDTH = RAIL + ROW_WIDTH;

/** wide: rail beside the list. single: rail above it. tight: each row stacks too. */
export type DeviceFit = "wide" | "single" | "tight";

export function deviceFit(width: number): DeviceFit {
  if (width <= 0 || width >= WIDE_WIDTH) return "wide";
  if (width >= ROW_WIDTH) return "single";
  return "tight";
}

/** The old `max-height: 780px` cut, now in CSS pixels: under it the page scrolls as one. */
export const SHORT_WINDOW = 780;

export function windowIsShort(innerHeight: number, zoom: number): boolean {
  return innerHeight > 0 && innerHeight / (zoom > 0 ? zoom : 1) <= SHORT_WINDOW;
}

/** The rail's sticky top. */
export const RAIL_TOP = 12;

/** A pinned bar may take a third of the view at most; past that it covers more than it helps. */
export function barFits(bar: number, view: number): boolean {
  return bar > 0 && view > 0 && bar * 3 <= view;
}

/** The rail stays pinned only if all of it shows between its top and the pinned bar. */
export function railFits(rail: number, view: number, bar: number): boolean {
  return rail > 0 && view > 0 && RAIL_TOP + rail <= view - bar;
}

/** How much of the list has to show on open: one control's height. */
export const LIST_PEEK = 48;

/**
 * Where a page that scrolls as one opens. The header and the controller's
 * explanations come first, so at 200% they filled the view and no setting
 * showed. If less than LIST_PEEK of the list shows above the bar, open at the
 * first of `stops` (layout, band, list tops) that puts the whole first row in
 * view, else at the list. Positions are CSS pixels from the scroller's top.
 * Returns the scrollTop, or null to stay at the top.
 */
export function openingScroll(view: number, bar: number, listTop: number, rowBottom: number, stops: readonly number[]): number | null {
  const open = view - bar;
  if (open <= 0 || listTop + LIST_PEEK <= open) return null;
  const stop = stops.find((top) => rowBottom - top <= open);
  return Math.round(stop ?? listTop);
}

/** Applies `openingScroll` to the page's scroller, in CSS pixels. */
export function openOnList(page: HTMLElement, pinnedBar: number): void {
  const scroller = page.closest<HTMLElement>(".shell-main") ?? page.parentElement;
  const list = page.querySelector(".device-preference-list");
  if (scroller === null || list === null) return;
  const width = page.offsetWidth;
  // Rects are zoomed pixels under Interface size; offsets and scrollTop are not.
  const zoom = width > 0 ? page.getBoundingClientRect().width / width || 1 : 1;
  const base = scroller.getBoundingClientRect().top;
  const top = (edge: number) => (edge - base) / zoom + scroller.scrollTop;
  const listTop = top(list.getBoundingClientRect().top);
  const row = list.firstElementChild;
  const rowBottom = row === null ? listTop + LIST_PEEK : top(row.getBoundingClientRect().bottom);
  const stops = [page.querySelector(".device-preference-layout"), page.querySelector(".device-preference-main")]
    .filter((element): element is Element => element !== null)
    .map((element) => top(element.getBoundingClientRect().top));
  const target = openingScroll(scroller.clientHeight, pinnedBar, listTop, rowBottom, stops);
  if (target !== null) scroller.scrollTop = target;
}

export interface DeviceLayout {
  readonly fit: DeviceFit;
  readonly short: boolean;
  readonly pinRail: boolean;
  readonly pinBar: boolean;
}

const FILL: DeviceLayout = { fit: "wide", short: false, pinRail: false, pinBar: false };

/** True when the page grows with its content and its scroller scrolls it whole. */
export function scrollsWhole(layout: Pick<DeviceLayout, "fit" | "short">): boolean {
  return layout.short || layout.fit !== "wide";
}

function same(a: DeviceLayout, b: DeviceLayout): boolean {
  return a.fit === b.fit && a.short === b.short && a.pinRail === b.pinRail && a.pinBar === b.pinBar;
}

/**
 * Watches the page, its rail and save bar, and the window. Publishes the
 * pinned bar's height as `--qcm-save-h` on the scroller, which owns the
 * scroll padding that keeps a focused control clear of the bar (B-126).
 * `ready` turns true once the settings are drawn. The opening scroll runs
 * once after that and only with a stick read: with none, the header's warn
 * line and drive button are what there is to act on, so the page stays put.
 */
export function useDeviceFit(ready: boolean, readStick: boolean) {
  const page = useRef<HTMLElement>(null);
  const rail = useRef<HTMLElement>(null);
  const bar = useRef<HTMLDivElement>(null);
  const [layout, setLayout] = useState<DeviceLayout>(FILL);
  const opened = useRef(false);

  useLayoutEffect(() => {
    const element = page.current;
    if (element === null) return undefined;
    const scroller = element.closest<HTMLElement>(".shell-main") ?? element.parentElement;
    const measure = () => {
      // offsetWidth is CSS pixels under zoom in both engines; rects are not.
      const width = element.offsetWidth;
      const zoom = width > 0 ? element.getBoundingClientRect().width / width : 1;
      const fit = deviceFit(width);
      const short = windowIsShort(window.innerHeight, zoom);
      const view = scroller?.clientHeight ?? 0;
      const barHeight = bar.current?.offsetHeight ?? 0;
      const whole = scrollsWhole({ fit, short });
      const pinBar = whole && barFits(barHeight, view);
      const pinRail = whole && fit === "wide" && railFits(rail.current?.offsetHeight ?? 0, view, pinBar ? barHeight : 0);
      if (scroller !== null) {
        if (pinBar) scroller.style.setProperty("--qcm-save-h", `${String(barHeight)}px`);
        else scroller.style.removeProperty("--qcm-save-h");
      }
      const next = { fit, short, pinRail, pinBar };
      setLayout((current) => (same(current, next) ? current : next));
    };
    measure();
    window.addEventListener("resize", measure);
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(measure);
    // The rail is drawn with the settings, so it is watched once they are.
    const targets = ready ? [element, scroller, bar.current, rail.current] : [element, scroller, bar.current];
    for (const target of targets) if (target) observer?.observe(target);
    return () => {
      observer?.disconnect();
      window.removeEventListener("resize", measure);
      scroller?.style.removeProperty("--qcm-save-h");
    };
  }, [ready]);

  useLayoutEffect(() => {
    const element = page.current;
    if (!ready || !readStick || opened.current || element === null) return;
    opened.current = true;
    if (scrollsWhole(layout)) openOnList(element, layout.pinBar ? (bar.current?.offsetHeight ?? 0) : 0);
  }, [ready, readStick, layout]);

  return { page, rail, bar, layout };
}
