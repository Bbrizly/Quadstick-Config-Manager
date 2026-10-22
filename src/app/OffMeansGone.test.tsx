import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { GoogleDriveSettings } from "../features/cloud/GoogleDrivePanel";
import { SettingsPage } from "../features/settings/SettingsPage";
import { I18nProvider } from "../i18n";
import { MockQcmClient, NETWORK_METHODS, withoutNetwork, type QcmClient } from "../platform";
import { App } from "./App";

// S5: with the network off, a network feature's control is not there at all,
// rather than there and greyed.

function online(xlsx = true): QcmClient {
  const client = new MockQcmClient();
  const stubs: Record<string, unknown> = Object.fromEntries(
    Object.keys(NETWORK_METHODS).map((name) => [name, () => new Promise(() => undefined)]),
  );
  stubs["getGoogleAuthStatus"] = () => Promise.resolve({ supported: true, configured: true, connected: false });
  if (xlsx) stubs["exportProfileXlsx"] = () => Promise.resolve(null);
  return Object.assign(client, stubs);
}

function settings(client: QcmClient) {
  return render(
    <I18nProvider>
      <SettingsPage client={client} extra={<GoogleDriveSettings client={client} onReview={() => undefined} />} />
    </I18nProvider>,
  );
}

describe.each([
  ["online", false],
  ["network off", true],
])("%s", (_name, off) => {
  const make = (): QcmClient => (off ? withoutNetwork(online()) : online());
  const present = (found: unknown): void => {
    if (off) expect(found).toBeNull();
    else expect(found).not.toBeNull();
  };

  it("settings: update check, Drive backup and feedback", async () => {
    settings(make());
    await screen.findByRole("tab", { name: "Contact" });
    present(screen.queryByRole("button", { name: "Check for updates" }));
    present(screen.queryByRole("heading", { name: "Google Drive backup" }));
    fireEvent.click(screen.getByRole("tab", { name: "Contact" }));
    present(screen.queryByRole("button", { name: "Send feedback" }));
  });

  it("shell and Home: community", async () => {
    render(<App client={make()} />);
    await screen.findAllByRole("button", { name: /^Create a new profile/u });
    present(screen.queryByRole("button", { name: "Browse community profiles" }));
    present(screen.queryByRole("button", { name: /^Browse the community list/u }));
  });

  it("editor: the Share flyout", async () => {
    // The .xlsx export is local, so it would keep the flyout; leave it out.
    render(<App client={off ? withoutNetwork(online(false)) : online()} />);
    fireEvent.click(screen.getAllByRole("button", { name: /^Create a new profile/u })[0]!);
    await screen.findByRole("heading", { level: 1, name: "untitled.csv" });
    present(screen.queryByRole("button", { name: "Share this profile as a Google Sheet" }));
  });
});
