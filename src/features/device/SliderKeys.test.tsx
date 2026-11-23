import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { App } from "../../app/App";
import type { PreferenceDefinition, QcmClient } from "../../platform";
import { MockQcmClient } from "../../platform/mockQcmClient";

// B-123: each arrow commits on key up, and the new revision was in the
// slider's key, so it remounted and focus fell to the page after one step.

const PREFS = "Preferences\nprefs.csv\nPreference,Value,Units,Description\nmouse_speed,100,,\n";
const SPEED: PreferenceDefinition = {
  name: "mouse_speed", label: "Mouse speed", category: "Mouse", editor: "integer", default: "100",
  minimum: 0, maximum: 200, unit: "", description: "", options: [], modeOverride: false, risk: "",
  source: "", firmwareMayAddMore: false, alsoCalled: "", advanced: false,
};

describe("device page slider keys", () => {
  it("keeps focus on the same slider through four arrows and commits each shown value", async () => {
    const client = new MockQcmClient();
    client.plugDevice("QUADSTICK", { "prefs.csv": PREFS });
    const writable: { -readonly [K in keyof QcmClient]: QcmClient[K] } = client;
    writable.getPreferenceCatalog = () => Promise.resolve({ categories: ["Mouse"], definitions: [SPEED] });
    const sent = vi.spyOn(client, "applyEditorOps");
    render(<App client={client} />);
    fireEvent.click(screen.getByRole("button", { name: "Manage files on your QuadStick" }));
    const slider = await screen.findByRole<HTMLInputElement>("slider", { name: "Mouse speed" });
    expect(slider.value).toBe("100");
    act(() => slider.focus());
    for (let step = 1; step <= 4; step += 1) {
      const shown = String(100 + step);
      fireEvent.keyDown(slider, { key: "ArrowRight" });
      // jsdom does not move a range on a key; the browser's step is this change.
      fireEvent.change(slider, { target: { value: shown } });
      fireEvent.keyUp(slider, { key: "ArrowRight" });
      await waitFor(() => expect(sent).toHaveBeenCalledTimes(step));
      await waitFor(() => expect(slider).toBeEnabled());
      expect(document.activeElement).toBe(slider);
      expect(slider.isConnected).toBe(true);
      expect(slider.value).toBe(shown);
    }
    const values = sent.mock.calls.map((call) => (call[2][0] as { value: string }).value);
    expect(values).toEqual(["101", "102", "103", "104"]);

    // Undo changes reads the stick again: the kept slider shows its 100.
    fireEvent.click(screen.getByRole("button", { name: /Put every setting back/u }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Undo changes" }));
    await waitFor(() => expect(screen.getByRole<HTMLInputElement>("slider", { name: "Mouse speed" }).value).toBe("100"));
    expect(screen.getByRole<HTMLInputElement>("spinbutton", { name: "Mouse speed" }).value).toBe("100");
  });
});
