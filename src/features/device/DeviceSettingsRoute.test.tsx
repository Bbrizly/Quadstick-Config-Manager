import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { App } from "../../app/App";
import { MockQcmClient } from "../../platform/mockQcmClient";

const PREFS = "Preferences\nprefs.csv\nPreference,Value,Units,Description\nmouse_speed,100,,\n";

describe("device nav (ShowDevicePageAsync)", () => {
  it("opens the settings, names the mount path, and keeps the files in a window", async () => {
    const client = new MockQcmClient();
    client.plugDevice("QUADSTICK", { "prefs.csv": PREFS });
    render(<App client={client} />);
    fireEvent.click(screen.getByRole("button", { name: "Manage files on your QuadStick" }));
    await screen.findByRole("heading", { level: 1, name: "Your QuadStick's settings" });
    expect(await screen.findByText("Found your QuadStick at /Volumes/QUADSTICK.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /Manage the profile files/u }));
    const files = await screen.findByRole("dialog", { name: "Files on your QuadStick" });
    expect(within(files).queryByRole("button", { name: "Device settings" })).toBeNull();
    fireEvent.click(within(files).getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog", { name: "Files on your QuadStick" })).toBeNull();
  });

  it("with no stick says so in bold words and still offers the drive picker", async () => {
    render(<App client={new MockQcmClient()} />);
    fireEvent.click(screen.getByRole("button", { name: "Manage files on your QuadStick" }));
    const line = await screen.findByText(/No QuadStick is plugged in, so these are the settings/u);
    expect(line).toHaveClass("warn");
    expect(screen.getByRole("button", { name: "Choose the QuadStick drive" })).toBeEnabled();
  });
});
