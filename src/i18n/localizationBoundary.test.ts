import { describe, expect, it } from "vitest";

const ALLOWED_BRAND_TEXT = new Set(["Q", "QCM"]);
const SOURCES = import.meta.glob("/src/**/*.tsx", {
  eager: true,
  query: "?raw",
  import: "default",
}) as Record<string, string>;

function visibleLiterals(source: string): string[] {
  const found: string[] = [];
  for (const match of source.matchAll(/>([^<{][^<]*)</gu)) {
    const text = match[1]?.trim();
    if (text && /[A-Za-z]{2}/u.test(text) && !ALLOWED_BRAND_TEXT.has(text)) found.push(text);
  }
  for (const match of source.matchAll(/\b(?:aria-label|title|placeholder)="([^"]*[A-Za-z][^"]*)"/gu)) {
    const text = match[1];
    if (text !== undefined) found.push(text);
  }
  return found;
}

describe("TASK-037 frontend string boundary", () => {
  it("keeps user-visible React prose behind the localization catalog", () => {
    const misses: string[] = [];
    for (const [path, source] of Object.entries(SOURCES)) {
      if (path.endsWith(".test.tsx") || path.includes("/platform/") || path.includes("/test/")) continue;
      for (const text of visibleLiterals(source)) misses.push(`${path}: ${text}`);
    }
    expect(misses).toEqual([]);
  });
});
