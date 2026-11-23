import { describe, expect, it } from "vitest";

import appCss from "./app.css?raw";
import editorCss from "./editor.css?raw";
import homeCss from "./home.css?raw";
import settingsCss from "./settings.css?raw";
import surfacesCss from "./surfaces.css?raw";
import visualizerCss from "../features/visualizer/visualizer.css?raw";

// WCAG 2.4.7: a keyboard user can always see where focus is. A border-only
// ring vanished on every borderless button (the shell nav, mode list, grid
// cells, device zones): 36 of 158 tab stops in a headless sweep.
const sheets = { appCss, editorCss, homeCss, settingsCss, surfacesCss, visualizerCss };

function rules(css: string): Array<{ selector: string; body: string }> {
  const stripped = css.replace(/\/\*[\s\S]*?\*\//gu, "");
  return [...stripped.matchAll(/([^{}]+)\{([^{}]*)\}/gu)].map((m) => ({
    selector: m[1]!.trim(),
    body: m[2]!,
  }));
}

// An accent-filled control: the focus blue on the fill reads 1.70:1 light and
// 1.53:1 dark. The ring needs surface on both sides of it instead.
const ACCENT_FILLED = [
  ".primary",
  ".primary-action",
  ".skip-link",
  'button.row-select[aria-pressed="true"]',
];

function ringSeparated(body: string): boolean {
  const offset = /outline-offset:\s*(-?\d+)px/u.exec(body);
  const gap = /box-shadow:\s*inset 0 0 0 (\d+)px var\(--qcm-surface\)/u.exec(body);
  if (!/outline:\s*2px solid var\(--qcm-focus\)/u.test(body)) return false;
  // Outset clear of the fill, or inset with a surface band (2px under the
  // ring, 2px between it and the fill).
  return Number(offset?.[1] ?? 0) >= 2 || (gap !== null && Number(gap[1]) >= 4);
}

describe("focus ring", () => {
  it.each(Object.entries(sheets))("%s was read, not stubbed empty", (_name, css) => {
    expect(css.length).toBeGreaterThan(1000);
  });

  it.each(ACCENT_FILLED)("%s keeps surface between the ring and its fill", (selector) => {
    const focused = rules(appCss).filter((rule) =>
      rule.selector.split(",").some((part) => part.trim() === `${selector}:focus-visible`),
    );
    expect(focused.some((rule) => ringSeparated(rule.body))).toBe(true);
  });

  it("draws an outline on every focused control by default", () => {
    const base = rules(appCss).find((rule) => rule.selector.startsWith("button:focus-visible"));
    expect(base?.body).toMatch(/outline:\s*2px solid var\(--qcm-focus\)/u);
  });

  it.each(Object.entries(sheets))("%s never hides the ring on :focus-visible", (_name, css) => {
    for (const rule of rules(css)) {
      if (!rule.selector.includes(":focus-visible")) continue;
      expect({ selector: rule.selector, hides: /outline:\s*(none|0)\b/u.test(rule.body) }).toEqual({
        selector: rule.selector,
        hides: false,
      });
    }
  });
});
