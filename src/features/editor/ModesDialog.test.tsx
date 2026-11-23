import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import editorCss from "../../styles/editor.css?raw";
import { ModesDialog } from "./ModesDialog";

const noop = () => undefined;

async function renderArmed(armedDelete: number | null) {
  const client = new MockQcmClient();
  const snapshot = await client.chooseAndOpenProfile();
  if (snapshot === null) throw new Error("mock open cancelled");
  render(
    <I18nProvider initialPreference="en">
      <ModesDialog
        open
        snapshot={snapshot}
        busy={false}
        armedDelete={armedDelete}
        onClose={noop}
        onApply={noop}
        onMove={noop}
        onDelete={noop}
        onSelect={noop}
        canMove={() => true}
      />
    </I18nProvider>,
  );
  return snapshot;
}

// The first click on delete only arms it. That used to change nothing but the
// accessible name, so a sighted user's second click deleted a mode unwarned.
describe("modes dialog delete", () => {
  it("shows the armed question on the button, not only in its name", async () => {
    const snapshot = await renderArmed(null);
    const first = snapshot.modes.find((mode) => mode.kind === "mode")!;
    const idle = screen.getAllByRole("button", { name: `Delete ${first.name}` })[0]!;
    expect(idle).not.toHaveClass("armed");
    expect(within(idle).queryByText("Really delete?")).toBeNull();
  });

  it("puts Really delete? in the armed button's visible text", async () => {
    const snapshot = await renderArmed(0);
    const first = snapshot.modes.find((mode) => mode.kind === "mode" && mode.index === 0)!;
    const armed = screen.getByRole("button", { name: new RegExp(`Really delete.*${first.name}`, "u") });
    expect(armed).toHaveClass("armed");
    expect(within(armed).getByText("Really delete?")).toBeInTheDocument();
  });
});

function rule(selector: string): string {
  const css = editorCss.replace(/\/\*[\s\S]*?\*\//gu, "");
  const match = [...css.matchAll(/([^{}]+)\{([^{}]*)\}/gu)].find((m) => m[1]!.trim() === selector);
  if (match === undefined) throw new Error(`no ${selector} rule`);
  return match[2]!;
}

// At 900x620 and 200% the name field was squeezed to 61px and showed "M".
// jsdom has no layout; the sweep in the B-113 ledger row measured it.
describe("modes dialog row at a narrow width", () => {
  it("was read, not stubbed empty", () => {
    expect(editorCss.length).toBeGreaterThan(1000);
  });

  it("wraps the buttons under the name instead of squeezing it", () => {
    expect(rule(".modes-dialog-row")).toMatch(/flex-wrap:\s*wrap/u);
    expect(rule(".modes-dialog-row .mode-name-input")).toMatch(/min-width:\s*12ch/u);
    expect(rule(".modes-dialog-row .mode-row-actions")).toMatch(/margin-inline-start:\s*auto/u);
  });

  it("keeps rows further apart than a wrapped row's two lines", () => {
    const between = Number(/gap:\s*(\d+)px/u.exec(rule(".modes-dialog-list"))?.[1]);
    const inRow = Number(/gap:\s*(\d+)px/u.exec(rule(".modes-dialog-row"))?.[1]);
    expect(between).toBeGreaterThan(inRow);
  });
});
