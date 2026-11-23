import { describe, expect, it } from "vitest";

import appCss from "./app.css?raw";
import surfacesCss from "./surfaces.css?raw";

// B-120: a native slider takes a click anywhere in its box, so the box's
// height is the target a mouth joystick has to land in. It was 16-17px.

const SLIDER = '.preference-number-control input[type="range"]';

function rule(css: string, selector: string): string {
  const start = css.indexOf(`${selector} {`);
  if (start < 0) throw new Error(`no ${selector} rule`);
  return css.slice(start, css.indexOf("}", start));
}

describe("device page slider target", () => {
  it("reads the sheets, not an empty stub", () => {
    for (const css of [appCss, surfacesCss]) expect(css.length).toBeGreaterThan(1000);
  });

  it("is at least 44px tall", () => {
    const height = /(?:^|[\s;{])height:\s*(\d+)px/u.exec(rule(surfacesCss, SLIDER))?.[1];
    expect(Number(height)).toBeGreaterThanOrEqual(44);
  });

  it("stays a native slider, so the whole box takes the click and the keys", () => {
    expect(surfacesCss).not.toMatch(/input\[type="range"\][^{]*\{[^}]*appearance:\s*none/u);
  });

  it("keeps the app's focus ring", () => {
    const ring = rule(appCss, "textarea:focus-visible");
    expect(appCss).toMatch(/input:focus-visible,\s*textarea:focus-visible/u);
    expect(ring).toMatch(/outline:\s*2px solid var\(--qcm-focus\)/u);
  });
});
