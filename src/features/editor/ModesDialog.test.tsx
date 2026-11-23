import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import { ModesDialog } from "./ModesDialog";

async function renderArmed(armedDelete: number | null) {
  const client = new MockQcmClient();
  const snapshot = await client.chooseAndOpenProfile();
  if (snapshot === null) throw new Error("mock open cancelled");
  const noop = () => undefined;
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
