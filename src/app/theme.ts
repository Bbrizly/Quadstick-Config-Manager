import { INTERFACE_SCALES } from "../platform/contracts";

export type ThemePreference = "system" | "light" | "dark";

export const THEME_PREFERENCES: readonly ThemePreference[] = ["system", "light", "dark"];

export function applyThemePreference(
  preference: ThemePreference,
  root: HTMLElement = document.documentElement,
): void {
  if (preference === "system") {
    delete root.dataset["theme"];
    return;
  }
  root.dataset["theme"] = preference;
}

/** Match Avalonia ZoomHost scale. Chromium WebView understands `zoom`. */
export function applyInterfaceScale(
  percent: number,
  root: HTMLElement = document.documentElement,
): void {
  const allowed = INTERFACE_SCALES.includes(percent as (typeof INTERFACE_SCALES)[number]);
  const pct = allowed ? percent : 100;
  if (pct === 100) {
    root.style.removeProperty("zoom");
    return;
  }
  root.style.zoom = `${pct}%`;
}

export function applyReduceMotion(
  on: boolean,
  root: HTMLElement = document.documentElement,
): void {
  if (on) {
    root.dataset["reduceMotion"] = "true";
    return;
  }
  delete root.dataset["reduceMotion"];
}
