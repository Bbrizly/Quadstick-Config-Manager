import { act, fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import { DRIVE_ARM_MS, HomeDriveButton } from "./HomeDriveButton";

// OnDriveButtonClick: first press arms, a second signs in, then the app offers
// to restore, and a yes opens the picker with every backup ticked.
function rig(connected: boolean) {
  const client = new MockQcmClient();
  const calls = { connect: 0, lists: 0 };
  let signedIn = connected;
  Object.assign(client, {
    getGoogleAuthStatus: () => Promise.resolve({ supported: true, configured: true, connected: signedIn }),
    connectGoogle: () => {
      calls.connect += 1;
      signedIn = true;
      return Promise.resolve({ supported: true, configured: true, connected: true });
    },
    listDriveBackups: () => {
      calls.lists += 1;
      return Promise.resolve([{ cloudRef: "ref-1", name: "racing", modifiedTime: "" }]);
    },
    restoreDriveBackups: () => Promise.resolve({ imported: ["racing"], skipped: [], failed: [] }),
  });
  render(
    <I18nProvider initialPreference="en">
      <HomeDriveButton client={client} onReview={() => undefined} />
    </I18nProvider>,
  );
  return calls;
}

describe("Home Drive button", () => {
  it("arms, signs in on the second press, then offers to restore everything", async () => {
    const calls = rig(false);
    const button = await screen.findByRole("button", { name: /Click to sign in and back up/u });
    expect(calls.lists).toBe(0); // nothing goes to Drive just by showing Home
    fireEvent.click(button);
    expect(calls.connect).toBe(0);
    fireEvent.click(screen.getByRole("button", { name: "Press again to open Google sign-in." }));
    await screen.findByRole("dialog", { name: "Restore your profiles?" });
    expect(calls.connect).toBe(1);
    fireEvent.click(screen.getByRole("button", { name: "Yes, continue" }));
    expect(await screen.findByRole("checkbox", { name: /racing/u })).toBeChecked();
  });

  it("disarms after four seconds", async () => {
    vi.useFakeTimers();
    try {
      const calls = rig(false);
      await act(async () => {
        await Promise.resolve();
      });
      fireEvent.click(screen.getByRole("button", { name: /Click to sign in and back up/u }));
      act(() => {
        vi.advanceTimersByTime(DRIVE_ARM_MS + 1);
      });
      fireEvent.click(screen.getByRole("button", { name: /Click to sign in and back up/u }));
      expect(calls.connect).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it("opens the picker straight away once connected, nothing ticked", async () => {
    rig(true);
    fireEvent.click(await screen.findByRole("button", { name: /Click to import your other profiles/u }));
    expect(await screen.findByRole("checkbox", { name: /racing/u })).not.toBeChecked();
  });
});
