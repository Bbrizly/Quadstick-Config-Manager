import { useLayoutEffect, useRef, useState } from "react";

// How the editor lays out for the CSS width it really has. Interface size is
// root `zoom`, which media queries never see and WebKit's container queries
// read in zoomed pixels, so the width is measured in script instead (B-096).

/** Plate margins and border, sidebar, stage floor with canvas padding, inspector floor. */
const PLATE_CHROME = 34;
const SIDEBAR = 304;
const STAGE = 634;
const INSPECTOR = 264;

export type EditorFit = "full" | "inspector-below" | "stacked" | "tight";

/** Below 700 the plate drops its margins so the 626 stage still fits at 200%. */
export const TIGHT_WIDTH = 700;
export const STACKED_WIDTH = PLATE_CHROME + SIDEBAR + STAGE;
export const FULL_WIDTH = STACKED_WIDTH + INSPECTOR;

export function editorFit(width: number): EditorFit {
  if (width <= 0 || width >= FULL_WIDTH) return "full";
  if (width >= STACKED_WIDTH) return "inspector-below";
  if (width >= TIGHT_WIDTH) return "stacked";
  return "tight";
}

/** B-090's cut: at or under 650 CSS pixels of window height the whole sidebar scrolls. */
export const SHORT_WINDOW = 650;

/** The window's height in CSS pixels: under root zoom, innerHeight is not. */
export function windowIsShort(innerHeight: number, zoom: number): boolean {
  return innerHeight > 0 && innerHeight / (zoom > 0 ? zoom : 1) <= SHORT_WINDOW;
}

/** Watches the workspace and the window and says which layout and height apply. */
export function useEditorFit() {
  const workspace = useRef<HTMLElement>(null);
  const [fit, setFit] = useState<EditorFit>("full");
  const [short, setShort] = useState(false);
  useLayoutEffect(() => {
    const element = workspace.current;
    if (element === null) return undefined;
    // offsetWidth is CSS pixels under zoom in both engines; rects are not.
    // It counts the scrollbar too, so a stacked column cannot flip back.
    const measure = () => {
      const width = element.offsetWidth;
      setFit(editorFit(width));
      const zoom = width > 0 ? element.getBoundingClientRect().width / width : 1;
      setShort(windowIsShort(window.innerHeight, zoom));
    };
    measure();
    if (typeof ResizeObserver === "undefined") return undefined;
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    window.addEventListener("resize", measure);
    return () => {
      observer.disconnect();
      window.removeEventListener("resize", measure);
    };
  }, []);
  return { workspace, fit, short };
}
