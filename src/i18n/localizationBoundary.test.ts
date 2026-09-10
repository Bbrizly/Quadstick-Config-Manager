import * as ts from "typescript";
import { describe, expect, it } from "vitest";

const ALLOWED_BRAND_TEXT = new Set(["Q", "QCM"]);
const USER_TEXT_ATTRIBUTES = new Set(["alt", "aria-description", "aria-label", "placeholder", "title"]);
const SOURCES = import.meta.glob("/src/**/*.tsx", {
  eager: true,
  query: "?raw",
  import: "default",
}) as Record<string, string>;

function recordVisible(found: string[], value: string): void {
  const text = value.replace(/\s+/gu, " ").trim();
  if (text !== "" && /\p{L}{2}/u.test(text) && !ALLOWED_BRAND_TEXT.has(text)) {
    found.push(text);
  }
}

function attributeLiteral(node: ts.JsxAttribute): string | null {
  const initializer = node.initializer;
  if (initializer === undefined) return null;
  if (ts.isStringLiteral(initializer)) return initializer.text;
  if (
    ts.isJsxExpression(initializer) &&
    initializer.expression !== undefined &&
    ts.isStringLiteralLike(initializer.expression)
  ) {
    return initializer.expression.text;
  }
  return null;
}

function visibleLiterals(path: string, source: string): string[] {
  const found: string[] = [];
  const file = ts.createSourceFile(path, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);

  const visit = (node: ts.Node): void => {
    if (ts.isJsxText(node)) {
      recordVisible(found, node.text);
    } else if (
      ts.isJsxExpression(node) &&
      node.expression !== undefined &&
      ts.isStringLiteralLike(node.expression)
    ) {
      recordVisible(found, node.expression.text);
    } else if (ts.isJsxAttribute(node)) {
      const name = node.name.getText(file);
      if (USER_TEXT_ATTRIBUTES.has(name)) {
        const literal = attributeLiteral(node);
        if (literal !== null) recordVisible(found, literal);
      }
    }
    ts.forEachChild(node, visit);
  };

  visit(file);
  return found;
}

describe("TASK-037 frontend string boundary", () => {
  it("keeps user-visible React prose behind the localization catalog", () => {
    const misses: string[] = [];
    for (const [path, source] of Object.entries(SOURCES)) {
      if (path.endsWith(".test.tsx") || path.includes("/platform/") || path.includes("/test/")) continue;
      for (const text of visibleLiterals(path, source)) misses.push(`${path}: ${text}`);
    }
    expect(misses).toEqual([]);
  });
});
