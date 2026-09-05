/**
 * The mock has to keep the same promises the native side keeps.
 *
 * A UI built against a mock that is looser than the real thing ships bugs the
 * mock cannot show. These are the scenarios that matter: the revision contract,
 * an all-or-nothing batch, a cancelled dialog, and a refusal arriving as a code.
 */

import { describe, expect, it } from "vitest";

import { ERROR_CODES } from "./contracts";
import { MockQcmClient } from "./mockQcmClient";
import { isQcmCommandError, QcmCommandError } from "./qcmClient";

function codeOf(reason: unknown): string {
  if (!isQcmCommandError(reason)) {
    throw new Error(`expected a QcmCommandError, got ${String(reason)}`);
  }
  return reason.code;
}

describe("MockQcmClient", () => {
  it("opens a new profile clean, with nothing to undo", async () => {
    const client = new MockQcmClient();
    const snapshot = await client.newProfile("racing.csv");
    expect(snapshot.dirty).toBe(false);
    expect(snapshot.canUndo).toBe(false);
    expect(snapshot.saveTarget).toBeNull();
    expect(snapshot.sessionId).toMatch(/^session-\d+$/);
    expect(snapshot.grid[1]?.[0]).toBe("racing.csv");
  });

  it("numbers modes by position, not by name", async () => {
    const client = new MockQcmClient();
    const snapshot = await client.newProfile("racing.csv");
    expect(snapshot.modes.map((mode) => mode.number)).toEqual([1, 2]);
    expect(snapshot.modes[0]?.name).toBe(snapshot.modes[1]?.name);
  });

  it("refuses an edit made against a revision that has moved on", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    const edited = await client.applyEditorOps(opened.sessionId, opened.revision, [
      { op: "set_cell", row: 4, col: 0, value: "circle" },
    ]);
    expect(edited.revision).toBe(opened.revision + 1);

    const stale = await client
      .applyEditorOps(opened.sessionId, opened.revision, [
        { op: "set_cell", row: 4, col: 0, value: "square" },
      ])
      .catch((reason: unknown) => reason);
    expect(codeOf(stale)).toBe(ERROR_CODES.profileRevisionConflict);

    const now = await client.applyEditorOps(opened.sessionId, edited.revision, []);
    expect(now.grid[3]?.[0]).toBe("circle");
  });

  it("applies none of a batch when one operation is refused", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    const before = opened.grid;

    const rejected = await client
      .applyEditorOps(opened.sessionId, opened.revision, [
        { op: "set_cell", row: 4, col: 0, value: "circle" },
        { op: "set_cell", row: 0, col: 0, value: "nowhere" },
      ])
      .catch((reason: unknown) => reason);
    expect(codeOf(rejected)).toBe(ERROR_CODES.profileOperationRejected);

    const now = await client.applyEditorOps(opened.sessionId, opened.revision, []);
    expect(now.grid).toEqual(before);
    expect(now.dirty).toBe(false);
  });

  it("makes every operation in a batch its own undo step", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    const after = await client.applyEditorOps(opened.sessionId, opened.revision, [
      { op: "set_cell", row: 4, col: 0, value: "circle" },
      { op: "set_cell", row: 4, col: 0, value: "square" },
    ]);
    expect(after.revision).toBe(opened.revision + 2);
    const once = await client.undoEditor(opened.sessionId, after.revision);
    expect(once.grid[3]?.[0]).toBe("circle");
  });

  it("says there is nothing to undo rather than doing nothing quietly", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    const reason = await client.undoEditor(opened.sessionId, opened.revision).catch((e: unknown) => e);
    expect(codeOf(reason)).toBe(ERROR_CODES.profileNothingToUndo);
  });

  it("treats a cancelled Open dialog as a result and not a failure", async () => {
    const client = new MockQcmClient();
    client.willCancelOpen();
    await expect(client.chooseAndOpenProfile()).resolves.toBeNull();
  });

  it("opens a file with a save target so plain Save has somewhere to go", async () => {
    const client = new MockQcmClient();
    client.willOpen("Racing.csv");
    const snapshot = await client.chooseAndOpenProfile();
    expect(snapshot?.saveTarget).toBe("Racing.csv");
    expect(snapshot?.source).toEqual({ kind: "local", name: "Racing.csv" });
  });

  it("asks for a place before saving a profile that has never been saved", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    const reason = await client
      .saveProfile(opened.sessionId, opened.revision)
      .catch((e: unknown) => e);
    expect(codeOf(reason)).toBe(ERROR_CODES.profileNeedsSaveTarget);
    expect((reason as QcmCommandError).payload.action?.kind).toBe("choose_save_location");
  });

  it("leaves a profile untouched when Save As is cancelled", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    client.willCancelSaveAs();
    await expect(client.saveProfileAs(opened.sessionId, opened.revision)).resolves.toBeNull();

    const reason = await client
      .saveProfile(opened.sessionId, opened.revision)
      .catch((e: unknown) => e);
    expect(codeOf(reason)).toBe(ERROR_CODES.profileNeedsSaveTarget);
  });

  it("refuses a stale Save As before a dialog is ever opened", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    await client.applyEditorOps(opened.sessionId, opened.revision, [
      { op: "set_cell", row: 4, col: 0, value: "circle" },
    ]);
    const reason = await client
      .saveProfileAs(opened.sessionId, opened.revision)
      .catch((e: unknown) => e);
    expect(codeOf(reason)).toBe(ERROR_CODES.profileRevisionConflict);
    expect(client.dialogsOpened).toBe(0);
  });

  it("clears dirty on a save and reports a receipt naming no place", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    const edited = await client.applyEditorOps(opened.sessionId, opened.revision, [
      { op: "set_cell", row: 4, col: 0, value: "circle" },
    ]);
    client.willSaveAs("Racing.csv");
    const receipt = await client.saveProfileAs(opened.sessionId, edited.revision);
    expect(receipt?.name).toBe("Racing.csv");
    expect(JSON.stringify(receipt)).not.toContain("/");

    const now = await client.applyEditorOps(opened.sessionId, edited.revision, []);
    expect(now.dirty).toBe(false);
  });

  it("keeps a dirty profile open when close is asked without an answer", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    await client.applyEditorOps(opened.sessionId, opened.revision, [
      { op: "set_cell", row: 4, col: 0, value: "circle" },
    ]);
    await expect(client.closeProfile(opened.sessionId, "if_clean")).resolves.toEqual({
      kind: "keptOpenUnsavedChanges",
    });
    await expect(client.closeProfile(opened.sessionId, "discard")).resolves.toEqual({
      kind: "closed",
    });
  });

  it("does not reuse a session id, so a late call fails instead of landing", async () => {
    const client = new MockQcmClient();
    const first = await client.newProfile("racing.csv");
    await client.closeProfile(first.sessionId, "discard");
    const second = await client.newProfile("flying.csv");
    expect(second.sessionId).not.toBe(first.sessionId);

    const reason = await client.undoEditor(first.sessionId, 1).catch((e: unknown) => e);
    expect(codeOf(reason)).toBe(ERROR_CODES.profileUnknownSession);
  });

  it("refuses an interface scale the app does not offer rather than rounding it", async () => {
    const client = new MockQcmClient();
    const before = await client.getSettings();
    const reason = await client
      .updateSettings(before.revision, { interfaceScalePercent: 137 })
      .catch((e: unknown) => e);
    expect(codeOf(reason)).toBe(ERROR_CODES.requestOutOfRange);
    await expect(client.getSettings()).resolves.toEqual(before);
  });

  it("moves the settings revision on a real change and not on a no-op", async () => {
    const client = new MockQcmClient();
    const before = await client.getSettings();
    const after = await client.updateSettings(before.revision, { theme: "dark" });
    expect(after.theme).toBe("dark");
    expect(after.revision).toBe(before.revision + 1);

    const again = await client.updateSettings(after.revision, { theme: "dark" });
    expect(again.revision).toBe(after.revision);
  });

  it("refuses a settings change made against a stale revision", async () => {
    const client = new MockQcmClient();
    const before = await client.getSettings();
    await client.updateSettings(before.revision, { reduceMotion: true });
    const reason = await client
      .updateSettings(before.revision, { theme: "dark" })
      .catch((e: unknown) => e);
    expect(codeOf(reason)).toBe(ERROR_CODES.profileRevisionConflict);
  });

  it("claims only the capabilities that are wired", async () => {
    const client = new MockQcmClient();
    const snapshot = await client.getAppSnapshot();
    expect(snapshot.capabilities.profileEditing).toBe(true);
    expect(snapshot.capabilities.deviceInstall).toBe(true);
    expect(snapshot.capabilities.liveInput).toBe(true);
  });

  it("reports a failed save rather than clearing dirty anyway", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    const edited = await client.applyEditorOps(opened.sessionId, opened.revision, [
      { op: "set_cell", row: 4, col: 0, value: "circle" },
    ]);
    client.willSaveAs("Racing.csv");
    client.willFailNextSave();
    const reason = await client
      .saveProfileAs(opened.sessionId, edited.revision)
      .catch((e: unknown) => e);
    expect(codeOf(reason)).toBe(ERROR_CODES.storageFull);

    const now = await client.applyEditorOps(opened.sessionId, edited.revision, []);
    expect(now.dirty).toBe(true);
  });
});
