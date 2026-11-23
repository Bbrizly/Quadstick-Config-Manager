import { describe, expect, it } from "vitest";

// The macOS floor is 12.0 with Safari 16 (B-118). WKWebView is the system
// Safari, so the bundle may use nothing newer: the build lowers syntax, but
// it cannot add a missing built-in or CSS function.
const SOURCES = import.meta.glob(["/src/{app,features,components,platform,i18n}/**/*.{ts,tsx}", "!**/*.test.*"], { eager: true, query: "?raw", import: "default" }) as Record<string, string>;
const SHEETS = import.meta.glob("/src/**/*.css", { eager: true, query: "?raw", import: "default" }) as Record<string, string>;
const CONFIG = import.meta.glob(["/src-tauri/tauri.conf.json", "/vite.config.ts"], { eager: true, query: "?raw", import: "default" }) as Record<string, string>;

/** Built-ins that arrived after Safari 16.0, with the version. */
const NEWER: readonly [RegExp, string][] = [
  [/Object\.groupBy|Map\.groupBy/u, "Object.groupBy (Safari 17.4)"],
  [/Promise\.withResolvers/u, "Promise.withResolvers (Safari 17.4)"],
  [/Array\.fromAsync/u, "Array.fromAsync (Safari 16.4)"],
  [/\.(union|intersection|difference|symmetricDifference|isSubsetOf|isSupersetOf|isDisjointFrom)\(/u, "Set methods (Safari 17)"],
  [/\.(isWellFormed|toWellFormed)\(/u, "String well-formed (Safari 16.4)"],
];

describe("macOS 12 / Safari 16 floor", () => {
  it("reads the real files", () => {
    expect(Object.keys(SOURCES).length).toBeGreaterThan(50);
    expect(Object.keys(SHEETS).length).toBeGreaterThan(5);
    for (const text of [...Object.values(SHEETS), ...Object.values(CONFIG)]) expect(text.length).toBeGreaterThan(500);
  });

  it("declares the floor and builds for it", () => {
    const conf = JSON.parse(CONFIG["/src-tauri/tauri.conf.json"]!) as {
      bundle: { macOS?: { minimumSystemVersion?: string } };
    };
    expect(conf.bundle.macOS?.minimumSystemVersion).toBe("12.0");
    expect(CONFIG["/vite.config.ts"]).toMatch(/target:\s*\[[^\]]*"safari16"/u);
  });

  it("uses no built-in newer than Safari 16", () => {
    const found = Object.entries(SOURCES).flatMap(([path, text]) =>
      NEWER.filter(([pattern]) => pattern.test(text)).map(([, name]) => `${path}: ${name}`),
    );
    expect(found).toEqual([]);
  });

  it("uses no color-mix(), which needs Safari 16.2", () => {
    const found = Object.entries(SHEETS)
      .filter(([, text]) => text.replace(/\/\*[\s\S]*?\*\//gu, "").includes("color-mix("))
      .map(([path]) => path);
    expect(found).toEqual([]);
  });
});
