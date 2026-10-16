import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { LiveRegion } from "../../components/primitives/LiveRegion";
import {
  MockQcmClient,
  type DriveBackupEvent,
  type DriveConflictChoice,
  type DriveResolution,
  type EditorSnapshot,
  type WorkbookImportReview,
} from "../../platform";
import { FollowLinkActions } from "../follow/FollowLinkActions";
import { DriveBackupNotices } from "./DriveBackupNotices";

/**
 * A fake Drive behind the native push: every save or replace hands the bytes
 * over in the background (as `drive_auto.rs` does), and the outcome comes
 * back over the subscription. `online` false is a machine with no network.
 */
class FakeDrive {
  online = true;
  editedOnline = false;
  dirty = false;
  pushes: string[] = [];
  resolved: DriveConflictChoice[] = [];
  private listener: ((event: DriveBackupEvent) => void) | null = null;
  private pending: (() => void)[] = [];

  subscribe = (onEvent: (event: DriveBackupEvent) => void) => {
    this.listener = onEvent;
    return Promise.resolve({ dispose: () => { this.listener = null; } });
  };

  /** Queue the push; it lands only when `settle` runs, after the caller is back. */
  afterSave(name: string): void {
    this.pending.push(() => this.listener?.(this.push(name)));
  }

  settle(): void {
    const work = this.pending;
    this.pending = [];
    for (const run of work) run();
  }

  retryIfDirty(name: string): void {
    if (this.dirty) this.afterSave(name);
  }

  private push(name: string): DriveBackupEvent {
    this.dirty = true;
    if (!this.online) return { kind: "pending" };
    if (this.editedOnline) return { kind: "conflict", resolutionId: "drive-resolution-1" };
    this.pushes.push(name);
    this.dirty = false;
    return { kind: "pushed" };
  }

  resolve = (resolutionId: string, choice: DriveConflictChoice): Promise<DriveResolution> => {
    expect(resolutionId).toBe("drive-resolution-1");
    this.resolved.push(choice);
    this.editedOnline = false;
    this.dirty = false;
    if (choice === "keep_online") {
      return Promise.resolve({ kind: "review", review: { importId: "online" } as unknown as WorkbookImportReview });
    }
    this.pushes.push("mine");
    return Promise.resolve({ kind: "finished", result: { kind: "pushed", backupDirty: false } });
  };
}

function rig() {
  const client = new MockQcmClient();
  const drive = new FakeDrive();
  let current: EditorSnapshot | null = null;
  Object.assign(client, {
    subscribeDriveBackups: drive.subscribe,
    resolveDriveConflict: drive.resolve,
    getFollowState: () => Promise.resolve({ follows: true }),
    checkLatestFromLink: () =>
      Promise.resolve({ kind: "changes", planId: "latest-1", changes: [{ kind: "other_changed" }], singleTab: true }),
    replaceWithLatest: async () => {
      const snapshot = await client.getProfileSnapshot(current!.sessionId);
      drive.afterSave("latest");
      return { kind: "replaced", snapshot };
    },
  });
  const track = (snapshot: EditorSnapshot): EditorSnapshot => {
    current = snapshot;
    return snapshot;
  };
  return { client, drive, track };
}

function Host({
  client,
  first,
  reviews,
}: {
  readonly client: MockQcmClient;
  readonly first: EditorSnapshot;
  readonly reviews: WorkbookImportReview[];
}) {
  const [snapshot, setSnapshot] = useState(first);
  const [message, setMessage] = useState("");
  return (
    <>
      <FollowLinkActions client={client} snapshot={snapshot} onSnapshot={setSnapshot} />
      <DriveBackupNotices client={client} onMessage={setMessage} onReview={(review) => reviews.push(review)} />
      <LiveRegion>{message}</LiveRegion>
    </>
  );
}

async function show() {
  const { client, drive, track } = rig();
  const opened = await client.newProfile("game.csv");
  client.willSaveAs("game.csv");
  await client.saveProfileAs(opened.sessionId, opened.revision);
  const first = track(await client.getProfileSnapshot(opened.sessionId));
  const reviews: WorkbookImportReview[] = [];
  render(
    <I18nProvider initialPreference="en">
      <Host client={client} first={first} reviews={reviews} />
    </I18nProvider>,
  );
  return { drive, reviews };
}

async function replace(): Promise<void> {
  fireEvent.click(await screen.findByRole("button", { name: /Download this profile again/u }));
  fireEvent.click(await screen.findByRole("button", { name: "Replace my copy" }));
  await screen.findByText(/Replaced with the latest from the link/u);
}

describe("Backup on every save", () => {
  it("a confirmed replace pushes the new profile to Drive", async () => {
    const { drive } = await show();
    await replace();
    act(() => drive.settle());
    await screen.findByText("Backed up to Google Drive.");
    expect(drive.pushes).toEqual(["latest"]);
  });

  it("offline does not hold up the replace; the failure is pending and the next open retries", async () => {
    const { drive } = await show();
    drive.online = false;
    await replace(); // done before the push has even started
    expect(drive.pushes).toEqual([]);
    act(() => drive.settle());
    await screen.findByText("Backup pending");
    expect(drive.dirty).toBe(true);
    expect(screen.queryByRole("dialog")).toBeNull();

    drive.online = true;
    drive.retryIfDirty("latest");
    act(() => drive.settle());
    await screen.findByText("Backed up to Google Drive.");
    expect(drive.dirty).toBe(false);
    expect(drive.pushes).toEqual(["latest"]);
  });

  it("an online edit asks; Yes replaces it with mine", async () => {
    const { drive } = await show();
    drive.editedOnline = true;
    await replace();
    act(() => drive.settle());
    const dialog = await screen.findByRole("dialog", { name: "Sheet edited online" });
    await waitFor(() => expect(screen.getByRole("button", { name: "Cancel" })).toHaveFocus());
    expect(dialog).toHaveTextContent("Choose Yes to replace it with your copy");
    fireEvent.click(screen.getByRole("button", { name: "Yes, continue" }));
    await screen.findByText("Backed up to Google Drive.");
    expect(drive.resolved).toEqual(["replace_with_mine"]);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("Cancel keeps the online copy and hands it over as an import to review", async () => {
    const { drive, reviews } = await show();
    drive.editedOnline = true;
    await replace();
    act(() => drive.settle());
    await screen.findByRole("dialog", { name: "Sheet edited online" });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(reviews).toHaveLength(1));
    expect(drive.resolved).toEqual(["keep_online"]);
    expect(drive.pushes).toEqual([]);
  });
});
