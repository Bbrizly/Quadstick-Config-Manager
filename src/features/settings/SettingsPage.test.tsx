import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import { SettingsPage } from "./SettingsPage";

function renderSettings(client: MockQcmClient) {
  return render(
    <I18nProvider initialPreference="en">
      <SettingsPage client={client} />
    </I18nProvider>,
  );
}

describe("GATE_SOFT settings page", () => {
  it("loads settings and patches with the expected revision", async () => {
    const client = new MockQcmClient();
    const update = vi.spyOn(client, "updateSettings");
    renderSettings(client);

    await waitFor(() => {
      expect(screen.getByLabelText(/Interface size/u)).toBeInTheDocument();
    });

    fireEvent.change(screen.getByLabelText(/Interface size/u), { target: { value: "80" } });
    await waitFor(() => {
      expect(update).toHaveBeenCalledWith(1, { interfaceScalePercent: 80 });
    });

    const after = await client.getSettings();
    expect(after.interfaceScalePercent).toBe(80);
    expect(after.revision).toBe(2);
  });

  it("refuses a scale outside the allowlist in the select", async () => {
    const client = new MockQcmClient();
    renderSettings(client);
    await waitFor(() => {
      expect(screen.getByLabelText(/Interface size/u)).toBeInTheDocument();
    });
    const scale = screen.getByLabelText(/Interface size/u) as HTMLSelectElement;
    const values = [...scale.options].map((option) => Number(option.value));
    expect(values).toEqual([60, 70, 80, 90, 100, 125, 150, 200]);
    expect(values).not.toContain(137);
  });

  it("checks for updates and only offers download when newer", async () => {
    const client = new MockQcmClient();
    vi.spyOn(client, "checkForUpdate").mockResolvedValue({
      message: "You are on 0.1.0. 1.0.0 is out.",
      downloadUrl: "https://github.com/Bbrizly/Quadstick-Config-Manager/releases/tag/v1.0.0",
      isNewer: true,
    });
    const open = vi.spyOn(client, "openExternalUrl").mockResolvedValue();
    renderSettings(client);

    await waitFor(() => {
      expect(screen.getByRole("button", { name: /Check for updates/u })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("button", { name: /Check for updates/u }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: /Open the download page/u })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("button", { name: /Open the download page/u }));
    await waitFor(() => {
      expect(open).toHaveBeenCalledWith(
        "https://github.com/Bbrizly/Quadstick-Config-Manager/releases/tag/v1.0.0",
      );
    });
  });

  it("toggles show tutorial as the inverse of tutorialSeen", async () => {
    const client = new MockQcmClient();
    renderSettings(client);
    await waitFor(() => {
      expect(screen.getByLabelText(/Show the tutorial next time/u)).toBeInTheDocument();
    });
    const box = screen.getByLabelText(/Show the tutorial next time/u) as HTMLInputElement;
    expect(box.checked).toBe(true);
    fireEvent.click(box);
    await waitFor(async () => {
      expect((await client.getSettings()).tutorialSeen).toBe(true);
    });
  });
});
