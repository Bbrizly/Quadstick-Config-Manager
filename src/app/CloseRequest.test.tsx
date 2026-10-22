import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { MockQcmClient } from "../platform";
import { App } from "./App";

// `MainWindow.Closing`: the window's close button with unsaved work asks
// Save / Don't save / Cancel, and only an answer quits.

function closableClient() {
  const client = new MockQcmClient();
  let onRequest: (() => void) | null = null;
  let quits = 0;
  Object.assign(client, {
    subscribeCloseRequests: (listener: () => void) => {
      onRequest = listener;
      return Promise.resolve({ dispose: () => undefined });
    },
    quitApp: () => {
      quits += 1;
      return Promise.resolve();
    },
  });
  return {
    client,
    close: () => onRequest?.(),
    quits: () => quits,
  };
}

async function editedProfile(close: () => void): Promise<void> {
  fireEvent.click(screen.getAllByRole("button", { name: /^Create a new profile/u })[0]!);
  await screen.findByRole("heading", { level: 1, name: "untitled.csv" });
  fireEvent.click(screen.getByRole("button", { name: /^Left\. Soft Puff/u }));
  fireEvent.click(await screen.findByRole("button", { name: "Add a new mapping for the Left mouthpiece hole" }));
  await screen.findByLabelText("This profile has unsaved changes. Save them before leaving?");
  close();
}

describe("closing the window", () => {
  it("asks first with unsaved changes; Cancel keeps the window and the edit", async () => {
    const { client, close, quits } = closableClient();
    render(<App client={client} />);
    await editedProfile(close);
    const prompt = await screen.findByRole("dialog", { name: "Profile" });
    expect(within(prompt).getByText("This profile has unsaved changes. Save them before leaving?")).toBeInTheDocument();
    fireEvent.click(within(prompt).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Profile" })).toBeNull());
    expect(screen.getByRole("heading", { level: 1, name: "untitled.csv" })).toBeInTheDocument();
    expect(quits()).toBe(0);
  });

  it("quits after Don't save", async () => {
    const { client, close, quits } = closableClient();
    render(<App client={client} />);
    await editedProfile(close);
    const prompt = await screen.findByRole("dialog", { name: "Profile" });
    fireEvent.click(within(prompt).getByRole("button", { name: "Don't save" }));
    await waitFor(() => expect(quits()).toBe(1));
  });

  it("a Cancel does not leave a later Home trip quitting the app", async () => {
    const { client, close, quits } = closableClient();
    render(<App client={client} />);
    await editedProfile(close);
    fireEvent.click(within(await screen.findByRole("dialog", { name: "Profile" })).getByRole("button", { name: "Cancel" }));
    fireEvent.click(screen.getByRole("button", { name: "Home" }));
    fireEvent.click(within(await screen.findByRole("dialog", { name: "Profile" })).getByRole("button", { name: "Don't save" }));
    await screen.findByRole("heading", { level: 2, name: "Start a profile" });
    expect(quits()).toBe(0);
  });
});
