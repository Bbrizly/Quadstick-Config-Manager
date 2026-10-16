import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { MockQcmClient } from "../platform";
import { App } from "./App";

// A profile that arrives while another is open (a qcm:// link, an accepted
// workbook, a file) replaces it. The open one is closed, session and all,
// and unsaved work is asked about first instead of being dropped.

function withLinks(client: MockQcmClient): { push: (id: string) => void } {
  const state: { push: ((id: string) => void) | null } = { push: null };
  Object.assign(client, {
    takeLaunchProfileLink: () => Promise.resolve(null),
    subscribeProfileLinks: (onLink: (id: string) => void) => {
      state.push = onLink;
      return Promise.resolve({ dispose: () => undefined });
    },
    openRegistryProfile: (id: string) => client.newProfile(`${id}.csv`),
  });
  return {
    push: (id) => {
      if (state.push === null) throw new Error("not subscribed yet");
      state.push(id);
    },
  };
}

async function openDirty(client: MockQcmClient): Promise<void> {
  client.willOpen("Racing.csv");
  fireEvent.click(
    screen.getAllByRole("button", { name: "Open a profile CSV file from your computer" })[0]!,
  );
  await screen.findByRole("heading", { level: 1, name: "Racing.csv" });
  const box = await screen.findByRole("textbox", {
    name: "Profile name. Edit to rename; .csv is added for you",
  });
  fireEvent.change(box, { target: { value: "Changed" } });
  fireEvent.blur(box);
  await screen.findByLabelText("This profile has unsaved changes. Save them before leaving?");
}

describe("replacing the open profile", () => {
  it("closes a clean profile's session when a link replaces it", async () => {
    const client = new MockQcmClient();
    const links = withLinks(client);
    render(<App client={client} />);
    client.willOpen("Racing.csv");
    fireEvent.click(
      screen.getAllByRole("button", { name: "Open a profile CSV file from your computer" })[0]!,
    );
    await screen.findByRole("heading", { level: 1, name: "Racing.csv" });
    expect(client.openSessionCount).toBe(1);

    links.push("halo");
    await screen.findByRole("heading", { level: 1, name: "halo.csv" });
    expect(client.openSessionCount).toBe(1);
    expect(screen.queryByRole("dialog", { name: "Profile" })).not.toBeInTheDocument();
  });

  it("asks before a link replaces unsaved work; Cancel keeps it and closes the link", async () => {
    const client = new MockQcmClient();
    const links = withLinks(client);
    render(<App client={client} />);
    await openDirty(client);

    links.push("halo");
    const dialog = await screen.findByRole("dialog", { name: "Profile" });
    expect(within(dialog).getByRole("button", { name: "Save (Ctrl+S)" })).toBeInTheDocument();
    expect(client.openSessionCount).toBe(2);

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(client.openSessionCount).toBe(1));
    expect(screen.getByRole("heading", { level: 1, name: "Racing.csv" })).toBeInTheDocument();
    expect(screen.getByLabelText("This profile has unsaved changes. Save them before leaving?")).toBeInTheDocument();
  });

  it("Don't save drops the old work and opens the link", async () => {
    const client = new MockQcmClient();
    const links = withLinks(client);
    render(<App client={client} />);
    await openDirty(client);

    links.push("halo");
    const dialog = await screen.findByRole("dialog", { name: "Profile" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Don't save" }));
    await screen.findByRole("heading", { level: 1, name: "halo.csv" });
    expect(client.openSessionCount).toBe(1);
  });

  it("Save writes the old work, then opens the link", async () => {
    const client = new MockQcmClient();
    const links = withLinks(client);
    const dispositions: string[] = [];
    const close = client.closeProfile.bind(client);
    client.closeProfile = (id, disposition) => {
      dispositions.push(disposition);
      return close(id, disposition);
    };
    render(<App client={client} />);
    await openDirty(client);

    links.push("halo");
    const dialog = await screen.findByRole("dialog", { name: "Profile" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save (Ctrl+S)" }));
    await screen.findByRole("heading", { level: 1, name: "halo.csv" });
    expect(client.openSessionCount).toBe(1);
    expect(dispositions).toContain("save");
  });

  it("a second link while the prompt is up closes the first", async () => {
    const client = new MockQcmClient();
    const links = withLinks(client);
    render(<App client={client} />);
    await openDirty(client);

    links.push("halo");
    await screen.findByRole("dialog", { name: "Profile" });
    links.push("doom");
    await waitFor(() => expect(client.openSessionCount).toBe(2));
    const dialog = screen.getByRole("dialog", { name: "Profile" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Don't save" }));
    await screen.findByRole("heading", { level: 1, name: "doom.csv" });
    expect(client.openSessionCount).toBe(1);
  });
});
