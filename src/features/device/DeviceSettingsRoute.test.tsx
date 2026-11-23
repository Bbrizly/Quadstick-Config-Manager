import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { App } from "../../app/App";
import type { PreferenceDefinition, QcmClient } from "../../platform";
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

  it("files window with no stick lays out like DeviceFilesWindow", async () => {
    render(<App client={new MockQcmClient()} />);
    fireEvent.click(screen.getByRole("button", { name: "Manage files on your QuadStick" }));
    fireEvent.click(await screen.findByRole("button", { name: /Manage the profile files/u }));
    const files = await screen.findByRole("dialog", { name: "Files on your QuadStick" });
    // The sentence names the modes that hide the drive, as Avalonia's does.
    expect(await within(files).findByText(/USB emulation modes 1, 3, 5, 6, 7 hide the drive/u)).toHaveClass(
      "device-files-summary",
    );
    // Refresh and Close share the foot, in that order.
    const foot = files.querySelector(".device-files-buttons");
    expect(foot).not.toBeNull();
    expect(within(foot as HTMLElement).getAllByRole("button").map((b) => b.textContent)).toEqual(["Refresh", "Close"]);
    fireEvent.click(within(foot as HTMLElement).getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog", { name: "Files on your QuadStick" })).toBeNull();
  });

  it("with no prefs.csv on the stick offers an empty one to fill in, like Avalonia", async () => {
    const client = new MockQcmClient();
    client.plugDevice("QUADSTICK", {});
    const definition: PreferenceDefinition = {
      name: "mouse_speed", label: "Mouse speed", category: "Mouse", editor: "integer", default: "100",
      minimum: 0, maximum: 200, unit: "", description: "", options: [], modeOverride: false, risk: "",
      source: "", firmwareMayAddMore: false, alsoCalled: "", advanced: false,
    };
    const writable: { -readonly [K in keyof QcmClient]: QcmClient[K] } = client;
    writable.getPreferenceCatalog = () => Promise.resolve({ categories: ["Mouse"], definitions: [definition] });
    render(<App client={client} />);
    fireEvent.click(screen.getByRole("button", { name: "Manage files on your QuadStick" }));
    expect(await screen.findByText(/It has no settings file yet/u)).toBeInTheDocument();
    const add = (await screen.findAllByRole("button", { name: /^Add /u }))[0]!;
    expect(add).toBeEnabled();
    fireEvent.click(add);
    expect(await screen.findByText(/1 setting changed/u)).toBeInTheDocument();
    expect(screen.getByText("Save to your QuadStick").closest("button")).toBeEnabled();
  });
});

describe("device settings save question", () => {
  // It overwrites prefs.csv, and Save sat where Save to your QuadStick was,
  // so two Enters wrote the stick. It opens on Cancel and nothing is accent.
  it("opens on Cancel, with Cancel left and a plain Save right", async () => {
    const client = new MockQcmClient();
    client.plugDevice("QUADSTICK", {});
    const definition: PreferenceDefinition = {
      name: "mouse_speed", label: "Mouse speed", category: "Mouse", editor: "integer", default: "100",
      minimum: 0, maximum: 200, unit: "", description: "", options: [], modeOverride: false, risk: "",
      source: "", firmwareMayAddMore: false, alsoCalled: "", advanced: false,
    };
    const writable: { -readonly [K in keyof QcmClient]: QcmClient[K] } = client;
    writable.getPreferenceCatalog = () => Promise.resolve({ categories: ["Mouse"], definitions: [definition] });
    render(<App client={client} />);
    fireEvent.click(screen.getByRole("button", { name: "Manage files on your QuadStick" }));
    fireEvent.click((await screen.findAllByRole("button", { name: /^Add /u }))[0]!);
    await screen.findByText(/1 setting changed/u);
    fireEvent.click(screen.getByText("Save to your QuadStick").closest("button")!);

    const ask = await screen.findByRole("dialog", { name: "Device settings" });
    const buttons = within(ask).getAllByRole("button");
    expect(buttons.map((b) => b.textContent)).toEqual(["Cancel", "Save"]);
    await waitFor(() => expect(buttons[0]).toHaveFocus());
    expect(buttons[1]).not.toHaveClass("primary-action");
  });
});
