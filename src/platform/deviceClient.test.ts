import { describe, expect, it } from "vitest";

import { ERROR_CODES } from "./contracts";
import { MockQcmClient } from "./mockQcmClient";
import { isQcmCommandError } from "./qcmClient";

function codeOf(reason: unknown): string {
  if (!isQcmCommandError(reason)) throw new Error(`expected QcmCommandError, got ${String(reason)}`);
  return reason.code;
}

describe("MockQcmClient device boundary", () => {
  it("models no, one and two mounted QuadSticks with opaque identity", async () => {
    const client = new MockQcmClient();
    await expect(client.listDevices()).resolves.toEqual({ devices: [], changed: false });

    const first = client.plugDevice("QUADSTICK A");
    const second = client.plugDevice("QUADSTICK B");
    const snapshot = await client.refreshDevices();

    expect(snapshot.devices.map((device) => device.deviceId)).toEqual([first, second]);
    expect(snapshot.devices.every((device) => /^dev-\d+$/u.test(device.deviceId))).toBe(true);
    expect(JSON.stringify(snapshot)).not.toContain("/Users/");
    expect(JSON.stringify(snapshot)).not.toContain("C:\\");
  });

  it("advertises device install now that the commands exist", async () => {
    const client = new MockQcmClient();
    const snapshot = await client.getAppSnapshot();
    expect(snapshot.capabilities.deviceInstall).toBe(true);
  });

  it("treats cancelling the device folder picker as a result", async () => {
    const client = new MockQcmClient();
    client.willCancelDeviceFolder();
    await expect(client.chooseDeviceFolder()).resolves.toBeNull();
    await expect(client.listDevices()).resolves.toEqual({ devices: [], changed: false });
  });

  it("rejects a stale generation before a device file is opened", async () => {
    const client = new MockQcmClient();
    const deviceId = client.plugDevice("QUADSTICK", { "racing.csv": "profile" });
    const before = await client.getDeviceLibrary(deviceId);
    client.remountDevice(deviceId);

    const reason = await client
      .openDeviceProfile(deviceId, before.generation, "racing.csv")
      .catch((error: unknown) => error);
    expect(codeOf(reason)).toBe(ERROR_CODES.deviceStale);
  });

  it("rejects traversal before delete planning", async () => {
    const client = new MockQcmClient();
    const deviceId = client.plugDevice("QUADSTICK", { "racing.csv": "profile" });
    const library = await client.getDeviceLibrary(deviceId);

    const reason = await client
      .prepareDeleteDeviceProfile(deviceId, library.generation, "../racing.csv")
      .catch((error: unknown) => error);
    expect(codeOf(reason)).toBe(ERROR_CODES.storageNameRejected);
  });

  it("binds delete confirmation to one one-shot plan", async () => {
    const client = new MockQcmClient();
    const deviceId = client.plugDevice("QUADSTICK", {
      "one.csv": "one",
      "two.csv": "two",
    });
    const library = await client.getDeviceLibrary(deviceId);
    const one = await client.prepareDeleteDeviceProfile(deviceId, library.generation, "one.csv");
    const two = await client.prepareDeleteDeviceProfile(deviceId, library.generation, "two.csv");

    const mismatch = await client
      .commitDeleteDeviceProfile(two.planId, one.confirmation.confirmationId)
      .catch((error: unknown) => error);
    expect(codeOf(mismatch)).toBe(ERROR_CODES.confirmationMismatch);

    const replay = await client
      .commitDeleteDeviceProfile(two.planId, two.confirmation.confirmationId)
      .catch((error: unknown) => error);
    expect(codeOf(replay)).toBe(ERROR_CODES.requestOutOfRange);
  });

  it("requires the protected default.csv confirmation and consumes a failed plan", async () => {
    const client = new MockQcmClient();
    const deviceId = client.plugDevice();
    const profile = await client.newProfile("default.csv");
    const plan = await client.prepareInstall(profile.sessionId, deviceId);
    expect(plan.confirmation?.kind).toBe("overwrite_default_csv");

    const missing = await client.commitInstall(plan.planId).catch((error: unknown) => error);
    expect(codeOf(missing)).toBe(ERROR_CODES.confirmationRequired);

    const replay = await client
      .commitInstall(plan.planId, plan.confirmation?.confirmationId)
      .catch((error: unknown) => error);
    expect(codeOf(replay)).toBe(ERROR_CODES.requestOutOfRange);
  });

  it("installs a normal profile through the prepared plan and exposes it in the library", async () => {
    const client = new MockQcmClient();
    const deviceId = client.plugDevice();
    const profile = await client.newProfile("racing.csv");
    const plan = await client.prepareInstall(profile.sessionId, deviceId);
    expect(plan.confirmation).toBeNull();

    const receipt = await client.commitInstall(plan.planId);
    expect(receipt.target).toBe("racing.csv");
    expect(receipt.confirmedOnDevice).toBe(true);

    const library = await client.getDeviceLibrary(deviceId);
    expect(library.files.map((file) => file.name)).toContain("racing.csv");
  });
});
