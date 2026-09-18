import { describe, expect, it } from "vitest";

import type { LiveSnapshot } from "./contracts";
import { MockQcmClient } from "./mockQcmClient";

describe("QcmClient scoped streams", () => {
  it("advertises live input only after the stream boundary exists", async () => {
    const client = new MockQcmClient();
    const snapshot = await client.getAppSnapshot();
    expect(snapshot.capabilities.liveInput).toBe(true);
  });

  it("survives a StrictMode-style mount cleanup mount cleanup with zero listeners", async () => {
    const client = new MockQcmClient();

    const first = await client.startLiveInput(() => undefined);
    expect(client.liveListenerCount).toBe(1);
    first.dispose();
    first.dispose();
    expect(client.liveListenerCount).toBe(0);

    const second = await client.startLiveInput(() => undefined);
    expect(client.liveListenerCount).toBe(1);
    second.dispose();
    second.dispose();
    expect(client.liveListenerCount).toBe(0);
  });

  it("delivers live frames only to active scoped subscribers", async () => {
    const client = new MockQcmClient();
    const seen: LiveSnapshot[] = [];
    const subscription = await client.startLiveInput((frame) => seen.push(frame));
    const frame: LiveSnapshot = {
      seq: 7,
      atMillis: 250,
      status: {
        kind: "reading",
        product: "QuadStick FPS",
        motion: { x: 0.25, y: -0.5, buttons: [1, 3] },
      },
    };

    client.emitLive(frame);
    expect(seen).toEqual([frame]);
    subscription.dispose();
    client.emitLive({ ...frame, seq: 8 });
    expect(seen).toEqual([frame]);
    expect(client.liveListenerCount).toBe(0);
  });

  it("uses device events as invalidations and cleans the listener on dispose", async () => {
    const client = new MockQcmClient();
    const revisions: number[] = [];
    const subscription = await client.subscribeDevicesChanged((event) => revisions.push(event.revision));

    const device = client.plugDevice();
    client.remountDevice(device);
    client.setDeviceWritable(device, false);
    expect(revisions).toEqual([1, 2, 3]);

    subscription.dispose();
    subscription.dispose();
    client.unplugDevice(device);
    expect(revisions).toEqual([1, 2, 3]);
    expect(client.deviceListenerCount).toBe(0);
  });

  it("reports only completed install stages in transaction order", async () => {
    const client = new MockQcmClient();
    const device = client.plugDevice("QUADSTICK", { "racing.csv": "old" });
    const profile = await client.newProfile("racing.csv");
    const plan = await client.prepareInstall(profile.sessionId, device);
    const progress: string[] = [];

    const receipt = await client.commitInstall(plan.planId, undefined, (event) => {
      progress.push(event.stage);
    });

    expect(progress).toEqual([
      "revalidate",
      "read_file",
      "backup",
      "temp_write",
      "temp_read_back",
      "replace_after_displace",
    ]);
    expect(receipt.stages).toEqual(progress);
    expect(receipt.confirmedOnDevice).toBe(true);
  });
});
