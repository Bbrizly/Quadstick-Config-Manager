import { describe, expect, it } from "vitest";

import appCss from "./app.css?raw";
import surfacesCss from "./surfaces.css?raw";
import tokenCss from "./tokens.css?raw";

// WebKit's own placeholder grey is #a9a9a9: 2.09:1 on a light field. Fields
// are white or black at 40% over the page, so the check composites first.

function block(selector: string): string {
  const start = tokenCss.indexOf(selector);
  if (start < 0) throw new Error(`no ${selector} block`);
  return tokenCss.slice(start, tokenCss.indexOf("}", start));
}

function token(body: string, name: string): string {
  const match = new RegExp(`--qcm-${name}:\\s*([^;]+);`, "u").exec(body);
  if (match === null) throw new Error(`no --qcm-${name}`);
  return match[1]!.trim();
}

type Rgb = readonly [number, number, number];

function hex(value: string): Rgb {
  return [1, 3, 5].map((i) => Number.parseInt(value.slice(i, i + 2), 16)) as unknown as Rgb;
}

function overlay(value: string, under: Rgb): Rgb {
  const match = /rgb\((\d+) (\d+) (\d+) \/ ([\d.]+)\)/u.exec(value);
  if (match === null) throw new Error(`not a translucent rgb: ${value}`);
  const alpha = Number(match[4]);
  return [1, 2, 3].map((i, at) => alpha * Number(match[i]) + (1 - alpha) * under[at]!) as unknown as Rgb;
}

function luminance(rgb: Rgb): number {
  const [r, g, b] = rgb.map((v) => {
    const c = v / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
}

function contrast(a: Rgb, b: Rgb): number {
  const [hi, lo] = [luminance(a), luminance(b)].toSorted((x, y) => y - x);
  return (hi! + 0.05) / (lo! + 0.05);
}

const PLACEHOLDER = /::placeholder\s*\{([^}]*)\}/u;

describe("placeholder text", () => {
  it("reads the sheets, not an empty stub", () => {
    for (const css of [appCss, surfacesCss, tokenCss]) expect(css.length).toBeGreaterThan(1000);
  });

  it("is drawn in the secondary text colour at full strength", () => {
    const body = PLACEHOLDER.exec(appCss)?.[1] ?? "";
    expect(body).toMatch(/color:\s*var\(--qcm-text-secondary\)/u);
    expect(body).toMatch(/opacity:\s*1\b/u);
  });

  it.each([
    ["light", ':root[data-theme="light"]'],
    ["dark", ':root[data-theme="dark"]'],
  ])("reads at 4.5:1 on a %s field over every surface", (_name, selector) => {
    const tokens = block(selector);
    const text = hex(token(tokens, "text-secondary"));
    for (const surface of ["bg", "surface", "surface-subtle", "sidebar-bg"]) {
      const field = overlay(token(tokens, "field-bg"), hex(token(tokens, surface)));
      expect({ surface, ok: contrast(text, field) >= 4.5 }).toEqual({ surface, ok: true });
    }
  });
});

describe("band LED colours", () => {
  it("come from tokens, not hex in the sheet", () => {
    const led = [...surfacesCss.matchAll(/\.band-led[\w-]*\s*\{([^}]*)\}/gu)].map((m) => m[1]!);
    expect(led.length).toBeGreaterThanOrEqual(2);
    for (const body of led) expect(body).not.toMatch(/#[0-9a-f]{3,6}\b/iu);
    expect(surfacesCss).toMatch(/fill:\s*var\(--qcm-led\)/u);
    expect(surfacesCss).toMatch(/fill:\s*var\(--qcm-led-halo\)/u);
  });

  it.each([
    ':root[data-theme="light"]',
    ':root[data-theme="dark"]',
    "@media (prefers-color-scheme: dark)",
    "@media (forced-colors: active)",
  ])("are defined in %s", (selector) => {
    const tokens = block(selector);
    expect(token(tokens, "led")).not.toBe("");
    expect(token(tokens, "led-halo")).not.toBe("");
  });

  it("map to a system colour when forced colours are on", () => {
    const tokens = block("@media (forced-colors: active)");
    expect(token(tokens, "led")).toBe("Highlight");
    expect(token(tokens, "led-halo")).toBe("Highlight");
  });
});
