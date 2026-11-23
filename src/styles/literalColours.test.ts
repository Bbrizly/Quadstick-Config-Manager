import { describe, expect, it } from "vitest";

import homePage from "../features/home/HomePage.tsx?raw";
import tokenCss from "./tokens.css?raw";

// B-106: a colour written into a rule skips the theme and forced-colours
// blocks. The card initials and the grid cell hover were the last two.
// B-117: every sheet is scanned, not two. tokens.css is where colours live.
const sheets = Object.fromEntries(
  Object.entries({
    ...import.meta.glob<string>("./*.css", { query: "?raw", import: "default", eager: true }),
    ...import.meta.glob<string>("../features/**/*.css", { query: "?raw", import: "default", eager: true }),
  }).filter(([path]) => !path.endsWith("/tokens.css")),
);

const strip = (css: string) => css.replace(/\/\*[\s\S]*?\*\//gu, "");

function declarations(css: string): string[] {
  return strip(css)
    .split(/[;{}]/u)
    .map((part) => part.trim())
    .filter((part) => /^[a-z-]+\s*:/u.test(part));
}

const LITERAL = /#[0-9a-f]{3,8}\b|rgba?\(|hsla?\(/iu;

function tokenBlocks(name: string): string[] {
  return [...strip(tokenCss).matchAll(new RegExp(`--qcm-${name}:\\s*([^;]+);`, "gu"))].map(
    (m) => m[1]!.trim(),
  );
}

function lum(hex: string): number {
  const [r, g, b] = [0, 2, 4].map((i) => {
    const c = Number.parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
}

describe("literal colours", () => {
  it("reads every stylesheet", () => {
    expect(Object.keys(sheets).toSorted()).toEqual([
      "../features/community/community.css",
      "../features/visualizer/visualizer.css",
      "./app.css",
      "./editor.css",
      "./home.css",
      "./motion.css",
      "./settings.css",
      "./surfaces.css",
    ]);
    for (const css of Object.values(sheets)) expect(css.length).toBeGreaterThan(500);
    expect(tokenCss.length).toBeGreaterThan(1000);
  });

  it.each(Object.keys(sheets))("leaves none in %s outside a shadow", (path) => {
    const offenders = declarations(sheets[path]!).filter(
      (decl) => LITERAL.test(decl) && !decl.startsWith("box-shadow"),
    );
    expect(offenders).toEqual([]);
  });

  it("gives the dialog scrim a light, dark, system dark and forced value", () => {
    expect(tokenBlocks("scrim")).toHaveLength(4);
  });

  it("gives both new tokens a light, dark, system dark and forced value", () => {
    expect(tokenBlocks("on-tile")).toEqual(["#ffffff", "#ffffff", "#ffffff", "CanvasText"]);
    expect(tokenBlocks("cell-hover-wash")).toEqual([
      "rgb(255 255 255 / 0.25)",
      "rgb(255 255 255 / 0.25)",
      "rgb(255 255 255 / 0.25)",
      "transparent",
    ]);
  });

  it("keeps initials text at 4.5:1 on every card swatch", () => {
    const swatches = [...(/TILE_COLORS = \[([^\]]+)\]/u.exec(homePage)?.[1] ?? "").matchAll(/#([0-9A-F]{6})/giu)].map(
      (m) => m[1]!,
    );
    expect(swatches).toHaveLength(12);
    const onTile = lum(tokenBlocks("on-tile")[0]!.slice(1));
    for (const swatch of swatches) {
      expect((onTile + 0.05) / (lum(swatch) + 0.05)).toBeGreaterThanOrEqual(4.5);
    }
  });
});
