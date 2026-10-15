import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { BindingInspector } from "./BindingInspector";
import { duplicateCounts } from "./duplicates";
import { SentenceCard } from "./SentenceCard";

const row = (n: number, output: string, ...inputs: string[]) => ({
  row: n,
  cells: [output, "normal", ...inputs, ...Array.from({ length: 10 - inputs.length }, () => "")],
});

function inEnglish(node: React.ReactNode) {
  return render(<I18nProvider initialPreference="en">{node}</I18nProvider>);
}

describe("duplicate-input mark (DuplicateUses.cs)", () => {
  it("counts outputs and each input cell per mode, trimmed and ordinal", () => {
    const counts = duplicateCounts([row(4, "x", "puff", "puff"), row(5, " x ", "Puff"), row(6, "", "")]);
    expect(counts.output("x")).toBe(2);
    expect(counts.input("puff")).toBe(2);
    expect(counts.input("Puff")).toBe(1);
    expect(counts.output("")).toBe(0);
  });

  it("marks a repeat with its count and says it in words", () => {
    const rows = [row(4, "x", "puff"), row(5, "x", "sip")];
    const counts = duplicateCounts(rows);
    inEnglish(<SentenceCard row={rows[0]!} zoneId="joystick" selected={false} counts={counts} onSelect={() => undefined} />);
    expect(screen.getByRole("button").getAttribute("aria-label")).toContain("Used 2 times in this mode");
  });

  it("a combo's input is not its own duplicate (3ad22ce), a use outside it is", () => {
    const combo = [row(4, "a", "puff"), row(5, "b", "puff")];
    const alone = duplicateCounts(combo);
    const view = inEnglish(
      <BindingInspector row={4} cells={combo[0]!.cells} members={combo} disabled={false} onSetCell={() => undefined} counts={alone} />,
    );
    expect(screen.queryByRole("img", { name: "Used 2 times in this mode" })).toBeNull();
    view.unmount();

    const outside = duplicateCounts([...combo, row(6, "c", "puff")]);
    inEnglish(
      <BindingInspector row={4} cells={combo[0]!.cells} members={combo} disabled={false} onSetCell={() => undefined} counts={outside} />,
    );
    expect(screen.getByRole("img", { name: "Used 2 times in this mode" })).toBeInTheDocument();
  });
});
