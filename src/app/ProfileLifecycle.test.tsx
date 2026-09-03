import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { MockQcmClient } from "../platform";
import { App } from "./App";

async function openLocal(client: MockQcmClient, name = "Racing.csv"): Promise<void> {
  client.willOpen(name);
  fireEvent.click(screen.getByRole("button", { name: "Open a profile file" }));
  await screen.findByRole("heading", { level: 1, name });
}

async function dirtyCurrentProfile(name = "Changed mode"): Promise<void> {
  const modeName = await screen.findByRole("textbox", { name: "Name of mode 1" });
  fireEvent.change(modeName, { target: { value: name } });
  fireEvent.blur(modeName);
  await screen.findByLabelText("This profile has unsaved changes. Save them before leaving?");
}

describe("TASK-040A local profile lifecycle", () => {
  it("closes a clean local profile and can reopen it through the native picker contract", async () => {
    const client = new MockQcmClient();
    render(<App client={client} />);

    await openLocal(client);
    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    await screen.findByRole("heading", { level: 1, name: "QuadStick Config Manager" });

    await openLocal(client);
    expect(screen.getByRole("heading", { level: 1, name: "Racing.csv" })).toBeInTheDocument();
  });

  it("blocks navigation away from dirty work until the user explicitly decides", async () => {
    const client = new MockQcmClient();
    render(<App client={client} />);

    await openLocal(client);
    await dirtyCurrentProfile();

    fireEvent.click(screen.getByRole("button", { name: "Manage files on your QuadStick" }));
    let dialog = await screen.findByRole("dialog", { name: "Profile" });
    expect(within(dialog).getByText("This profile has unsaved changes. Save them before leaving?")).toBeInTheDocument();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Profile" })).not.toBeInTheDocument());
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

    fireEvent.click(screen.getByRole("button", { name: "New profile" }));
    await screen.findByRole("heading", { level: 1, name: "untitled.csv" });
    await dirtyCurrentProfile("Saved mode");

    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    const dialog = await screen.findByRole("dialog", { name: "Profile" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save  (Ctrl+S)" }));

    await screen.findByRole("heading", { level: 1, name: "QuadStick Config Manager" });
    expect(client.dialogsOpened).toBe(1);
  });
});