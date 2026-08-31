import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { describe, expect, it } from "vitest";

const ROOTS = ["src/app", "src/components", "src/features"];
const ALLOWED_BRAND_TEXT = new Set(["Q", "QCM"]);

function sourceFiles(root: string): string[] {
  if (!existsSync(root)) return [];
  const files: string[] = [];
  for (const entry of readdirSync(root)) {
    const path = join(root, entry);
    if (statSync(path).isDirectory()) files.push(...sourceFiles(path));
    else if (path.endsWith(".tsx") && !path.endsWith(".test.tsx")) files.push(path);
  }
  return files;
}

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
    for (const root of ROOTS) {
      for (const path of sourceFiles(root)) {
        for (const text of visibleLiterals(readFileSync(path, "utf8"))) {
          misses.push(`${relative(".", path)}: ${text}`);
        }
      }
    }
    expect(misses).toEqual([]);
  });
});
