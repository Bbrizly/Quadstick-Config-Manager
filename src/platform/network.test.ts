import { describe, expect, it } from "vitest";

import hostRust from "../../src-tauri/src/host.rs?raw";
import { MockQcmClient } from "./mockQcmClient";
import { NETWORK_METHODS, withoutNetwork } from "./network";
import { TAURI_COMMANDS } from "./tauriQcmClient";

function rustNetworkCommands(): string[] {
  const block = /pub const NETWORK_COMMANDS: &\[&str\] = &\[([\s\S]*?)\];/u.exec(hostRust)?.[1] ?? "";
  return [...block.matchAll(/"([a-z_]+)"/gu)].map((match) => match[1]!);
}

describe("off means gone", () => {
  it("names every native network command the page can call", () => {
    const rust = new Set(rustNetworkCommands());
    expect(rust.size).toBeGreaterThan(10);
    const ours = new Set<string>(Object.values(NETWORK_METHODS).flat());
    // Telemetry is sent from native only; no page method reaches it.
    const fromPage = [...rust].filter((command) => (TAURI_COMMANDS as readonly string[]).includes(command));
    expect([...ours].toSorted()).toEqual(fromPage.toSorted());
  });

  it("leaves every network method undefined and says so in the capabilities", async () => {
    const client = new MockQcmClient();
    client.enableUsageData();
    const stubbed = Object.assign(client, Object.fromEntries(
      Object.keys(NETWORK_METHODS).map((name) => [name, () => Promise.reject(new Error("network"))]),
    ));
    const offline = withoutNetwork(stubbed);
    for (const name of Object.keys(NETWORK_METHODS)) {
      expect((offline as unknown as Record<string, unknown>)[name], name).toBeUndefined();
      expect(name in offline, name).toBe(false);
    }
    const snapshot = await offline.getAppSnapshot();
    expect(snapshot.capabilities).toMatchObject({ communityCatalog: false, googleBackup: false, usageData: false });
    expect(snapshot.capabilities.profileEditing).toBe(true);
    // Everything else still reaches the client, bound to it.
    const opened = await offline.newProfile("untitled.csv");
    expect((await offline.getProfileSnapshot(opened.sessionId)).sessionId).toBe(opened.sessionId);
  });
});
