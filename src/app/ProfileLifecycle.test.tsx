import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { MockQcmClient } from "../platform";
import { App } from "./App";

async function openLocal(client: MockQcmClient, name = "Racing.csv"): Promise<void> {
  client.willOpen(name);
  fireEvent.click(
    screen.getAllByRole("button", {
      name: "Open a profile CSV file from your computer",
    })[0]!,
  );
  await screen.findByRole("heading", { level: 1, name });
}

async function dirtyCurrentProfile(name = "Changed"): Promise<void> {
  // Avalonia's FileNameBox: renaming the profile is an edit like any other.
  const box = await screen.findByRole("textbox", { name: "Profile name. Edit to rename; .csv is added for you" });
  fireEvent.change(box, { target: { value: name } });
  fireEvent.blur(box);
  await screen.findByLabelText("This profile has unsaved changes. Save them before leaving?");
}

// Avalonia has no Close: Home (or the mark) leaves the editor.
const leaveEditor = (): void => {
  fireEvent.click(screen.getByRole("button", { name: "Home" }));
};

describe("TASK-040A local profile lifecycle", () => {
  it("closes a clean local profile and can reopen it through the native picker contract", async () => {
    const client = new MockQcmClient();
    render(<App client={client} />);

    await openLocal(client);
    leaveEditor();
    await screen.findByRole("heading", { level: 2, name: "Start a profile" });

    await openLocal(client);
    expect(screen.getByRole("heading", { level: 1, name: "Racing.csv" })).toBeInTheDocument();
  });

  it("offers recovered work on home and opens it dirty", async () => {
    const client = new MockQcmClient();
    client.queuePendingRescue({
      rescueId: "Racing-rescued-1.csv",
      displayName: "Racing-rescued-1",
    });
    render(<App client={client} />);

    await screen.findByText(/Unsaved work from last time was recovered/u);
    fireEvent.click(screen.getByRole("button", { name: "Open recovered work" }));
    await screen.findByText(/Recovered profile opened/u);
    expect(
      screen.getByLabelText("This profile has unsaved changes. Save them before leaving?"),
    ).toBeInTheDocument();
  });

  it("blocks navigation away from dirty work until the user explicitly decides", async () => {
    const client = new MockQcmClient();
    render(<App client={client} />);

    await openLocal(client);
    await dirtyCurrentProfile();

    fireEvent.click(screen.getByRole("button", { name: "Manage files on your QuadStick" }));
    let dialog = await screen.findByRole("dialog", { name: "Profile" });
    expect(
      within(dialog).getByText("This profile has unsaved changes. Save them before leaving?"),
    ).toBeInTheDocument();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() =>
      expect(screen.queryByRole("dialog", { name: "Profile" })).not.toBeInTheDocument(),
    );
    expect(screen.getByRole("heading", { level: 1, name: "Racing.csv" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Manage files on your QuadStick" }));
    dialog = await screen.findByRole("dialog", { name: "Profile" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Don't save" }));
    await screen.findByRole("heading", { level: 1, name: "On your QuadStick" });
  });

  it("uses Save As before closing a dirty profile that has no local target", async () => {
    const client = new MockQcmClient();
    client.willSaveAs("Saved.csv");
    render(<App client={client} />);

    fireEvent.click(
      screen.getAllByRole("button", {
        name: "Create a new profile from the factory default template",
      })[0]!,
    );
    await screen.findByRole("heading", { level: 1, name: "untitled.csv" });
    await dirtyCurrentProfile("Saved");

    leaveEditor();
    const dialog = await screen.findByRole("dialog", { name: "Profile" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save (Ctrl+S)" }));

    await screen.findByRole("heading", { level: 2, name: "Start a profile" });
    expect(client.dialogsOpened).toBe(1);
  });
});
