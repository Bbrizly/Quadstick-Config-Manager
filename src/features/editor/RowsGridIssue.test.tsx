import { render, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { I18nProvider, type LocalePreference } from "../../i18n";
import type { Issue } from "../../platform";
import editorCss from "../../styles/editor.css?raw";
import { RowsGrid } from "./RowsGrid";

// B-097. A problem cell's warning and error edges are 1.04:1 apart in colour,
// so each also wears the problems bar's glyph, and a warning is dashed. The
// severity a screen reader hears comes from the catalog, not English.

const issues: Issue[] = [
  { severity: "warning", cell: "A2", message: "", fix: "", kind: "unknown_output" },
  { severity: "error", cell: "C2", message: "", fix: "", kind: "unknown_input" },
];

function renderGrid(locale: LocalePreference) {
  return render(
    <I18nProvider initialPreference={locale}>
      <RowsGrid
        rows={[{ row: 2, cells: ["cross", "", "lp", "", "", "", "", "", "", "", ""] }]}
        issues={issues}
        labelStyle={1}
        disabled={false}
        liveRows={new Set()}
        selecting={false}
        selectedRows={new Set()}
        onToggleRow={() => undefined}
        onApply={() => undefined}
        onDeleteRow={() => undefined}
      />
    </I18nProvider>,
  );
}

const glyphOf = (cell: Element | null) => cell?.querySelector(":scope > svg.grid-cell-issue");

function rule(selector: string): string {
  const flat = editorCss.replace(/\/\*[\s\S]*?\*\//gu, "");
  const found = [...flat.matchAll(/([^{}]+)\{([^{}]*)\}/gu)].find((m) => m[1]!.trim() === selector);
  if (found === undefined) throw new Error(`no rule for ${selector}`);
  return found[2]!;
}

describe("rows grid problem cells", () => {
  it("reads the real stylesheet", () => {
    expect(editorCss.length).toBeGreaterThan(1000);
  });

  it("puts the warning or error glyph in the cell, hidden from the reader", () => {
    const { container } = renderGrid("en");
    const warning = container.querySelector(".grid-cell.issue-warning");
    const error = container.querySelector(".grid-cell.issue-error");
    expect(glyphOf(warning)?.getAttribute("aria-hidden")).toBe("true");
    expect(glyphOf(error)?.getAttribute("aria-hidden")).toBe("true");
    // Two shapes, not one shape in two colours.
    expect(glyphOf(warning)?.innerHTML).not.toBe(glyphOf(error)?.innerHTML);
    expect(container.querySelectorAll(".grid-cell:not([class*='issue-']) svg.grid-cell-issue")).toHaveLength(0);
  });

  it("dashes a warning's edge so it differs from an error's by more than colour", () => {
    expect(rule(".grid-cell.issue-warning")).toMatch(/border-style:\s*dashed/u);
    expect(rule(".grid-cell.issue-error")).not.toMatch(/dashed/u);
  });

  it("speaks the severity in the interface language", async () => {
    const { container } = renderGrid("de");
    // The German catalog is its own chunk and lands after the first render.
    await waitFor(() => {
      expect(container.querySelector(".grid-cell.issue-warning button")?.getAttribute("aria-label")).toMatch(/^Warnung: /u);
    });
    expect(container.querySelector(".grid-cell.issue-error button")?.getAttribute("aria-label")).toMatch(/^Fehler: /u);
  });
});
