import { describe, expect, it } from "vitest";

import homePage from "../features/home/HomePage.tsx?raw";
import editorCss from "./editor.css?raw";
import homeCss from "./home.css?raw";
import tokenCss from "./tokens.css?raw";

// B-106: a colour written into a rule skips the theme and forced-colours
// blocks. The card initials and the grid cell hover were the last two.

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
  it("reads the stylesheets", () => {
    expect(homeCss.length).toBeGreaterThan(1000);
    expect(editorCss.length).toBeGreaterThan(1000);
    expect(tokenCss.length).toBeGreaterThan(1000);
  });

  it("leaves none in home.css or editor.css outside a shadow", () => {
    const offenders = [...declarations(homeCss), ...declarations(editorCss)].filter(
      (decl) => LITERAL.test(decl) && !decl.startsWith("box-shadow"),
    );
    expect(offenders).toEqual([]);
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
