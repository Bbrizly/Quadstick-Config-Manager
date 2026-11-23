import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import { InstallProfileDialog } from "./InstallProfileDialog";

function pad(cells: string[]): string[] {
  return [...cells, ...Array.from({ length: 11 - cells.length }, () => "")];
}

// Install becomes "Yes, continue" in the same spot when the file would
// overwrite default.csv, so two Enters replaced the device's boot profile.
describe("install over default.csv", () => {
  it("focuses Cancel when the overwrite question appears", async () => {
    const client = new MockQcmClient();
    client.plugDevice("QUADSTICK", { "default.csv": "x" });
    client.willOpenGrid("default.csv", [
      pad(["Profile Name", "", "Racing"]),
      pad(["default.csv"]),
      pad(["PlayStation Outputs", "Function", "usb"]),
      pad(["cross", "normal", "puff"]),
    ]);
    const profile = await client.chooseAndOpenProfile();
    if (profile === null) throw new Error("mock open cancelled");

    render(
      <I18nProvider initialPreference="en">
        <InstallProfileDialog client={client} profile={profile} open onClose={() => undefined} />
      </I18nProvider>,
    );
    const install = await screen.findByRole("button", { name: "Install to QuadStick" });
    await waitFor(() => expect(install).toBeEnabled());
    fireEvent.click(install);

    await screen.findByRole("button", { name: "Yes, continue" });
    await waitFor(() => expect(screen.getByRole("button", { name: "Cancel" })).toHaveFocus());
  });
});
