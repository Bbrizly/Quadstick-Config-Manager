import { describe, expect, it } from "vitest";

import tokenCss from "./tokens.css?raw";

// WCAG 2.2 contrast, computed from the shipped tokens. Body text needs
// 4.5:1 on every surface it is drawn on; the focus ring is a non-text
// indicator and needs 3:1 against the page.

function block(selector: string): Record<string, string> {
  const start = tokenCss.indexOf(selector);
  if (start < 0) throw new Error(`no ${selector} block`);
  const body = tokenCss.slice(start, tokenCss.indexOf("}", start));
  return Object.fromEntries(
    [...body.matchAll(/--qcm-([a-z-]+):\s*(#[0-9a-f]{6})/gu)].map((m) => [m[1]!, m[2]!]),
  );
}

function luminance(hex: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = Number.parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
}

function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi! + 0.05) / (lo! + 0.05);
}

const SURFACES = ["bg", "surface", "surface-subtle", "sidebar-bg"];
const TINTS = ["output-tint", "function-tint", "input-tint", "new-row-tint", "selection-tint", "live-tint"];
// Status colours sit on plain surfaces; body, secondary and accent text also
// sit on the editor's row and selection tints.
const PAIRS: ReadonlyArray<readonly [string, readonly string[]]> = [
  ["text", [...SURFACES, ...TINTS]],
  ["text-secondary", [...SURFACES, ...TINTS]],
  ["accent-text", [...SURFACES, ...TINTS]],
  ["error", [...SURFACES, ...TINTS.filter((tint) => tint !== "selection-tint")]],
  ["success", [...SURFACES, ...TINTS.filter((tint) => tint !== "selection-tint")]],
  ["warning", [...SURFACES, ...TINTS.filter((tint) => tint !== "selection-tint")]],
  ["on-accent", ["accent"]],
];

describe.each([
  ["light", ':root[data-theme="light"]'],
  ["dark", ':root[data-theme="dark"]'],
])("%s theme contrast", (_name, selector) => {
  const tokens = block(selector);

  it.each(PAIRS)("%s reads at 4.5:1 or better", (fg, backgrounds) => {
    for (const bg of backgrounds) {
      const ratio = contrast(tokens[fg]!, tokens[bg]!);
      expect({ fg, bg, ok: ratio >= 4.5 }).toEqual({ fg, bg, ok: true });
    }
  });

  it("draws the focus ring at 3:1 against every surface", () => {
    for (const bg of SURFACES) {
      expect(contrast(tokens["focus"]!, tokens[bg]!)).toBeGreaterThanOrEqual(3);
    }
  });
});

it("follows the OS dark setting with the same colours as the dark theme", () => {
  const explicit = block(':root[data-theme="dark"]');
  const system = block("@media (prefers-color-scheme: dark)");
  expect(system).toEqual(explicit);
});
