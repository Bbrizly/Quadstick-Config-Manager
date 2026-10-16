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

describe("focus ring", () => {
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
