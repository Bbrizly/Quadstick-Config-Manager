import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient, type EditorSnapshot } from "../../platform";
import { ProfileDriveActions } from "./GoogleDrivePanel";

function rig(connectWorks: boolean) {
  const client = new MockQcmClient();
  let connected = false;
  const shared: string[] = [];
  Object.assign(client, {
    getGoogleAuthStatus: () => Promise.resolve({ supported: true, configured: true, connected }),
    connectGoogle: () => {
      connected = connectWorks;
      return Promise.resolve({ supported: true, configured: true, connected });
    },
    disconnectGoogle: () => Promise.resolve({ supported: true, configured: true, connected: false }),
    backupProfileToDrive: () => Promise.resolve({ kind: "pushed" }),
    shareDriveProfile: (sessionId: string) => {
      shared.push(sessionId);
      return Promise.resolve({ url: "https://docs.google.com/spreadsheets/d/x" });
    },
  });
  return { client, shared };
}

function Host({ client, first }: { readonly client: MockQcmClient; readonly first: EditorSnapshot }) {
  const [snapshot, setSnapshot] = useState(first);
  return (
    <ProfileDriveActions client={client} snapshot={snapshot} onSnapshot={setSnapshot} onReview={() => undefined} />
  );
}

async function open(connectWorks: boolean) {
  const { client, shared } = rig(connectWorks);
  const first = await client.newProfile("untitled.csv");
  render(
    <I18nProvider initialPreference="en">
      <Host client={client} first={first} />
    </I18nProvider>,
  );
  fireEvent.click(screen.getByRole("button", { name: "Copy a link to this profile's Google Sheet" }));
  await screen.findByRole("dialog", { name: "Set up Google Sheets" });
  return { client, shared };
}

describe("ShareSetupWindow port", () => {
  it("walks connect, save, then shares only from the last step", async () => {
    const { client, shared } = await open(true);
    const finish = screen.getByRole("button", {
      name: "Copy share link. Not available yet: finish the steps above first.",
    });
    expect(finish).toBeDisabled();
    expect(screen.getByRole("region", { name: "Step 1 of 3, Connect Google Drive, not done yet" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Connect Google Drive" }));
    await screen.findByRole("region", { name: "Step 1 of 3, Connect Google Drive, done" });
    expect(finish).toBeDisabled();

    client.willSaveAs("racing.csv");
    fireEvent.click(screen.getByRole("button", { name: "Save this profile" }));
    await screen.findByRole("region", { name: "Step 2 of 3, Save this profile, done" });
    expect(shared).toEqual([]);

    const dialog = screen.getByRole("dialog", { name: "Set up Google Sheets" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Copy share link" }));
    await waitFor(() => expect(shared).toHaveLength(1));
    expect(screen.queryByRole("dialog", { name: "Set up Google Sheets" })).toBeNull();
  });

  it("says when Google could not be reached, and Cancel shares nothing", async () => {
    const { shared } = await open(false);
    fireEvent.click(screen.getByRole("button", { name: "Connect Google Drive" }));
    expect(await screen.findByText(/Could not connect to Google/u)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Close without sharing" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(shared).toEqual([]);
  });
});
