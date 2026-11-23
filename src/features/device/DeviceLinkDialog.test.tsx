import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient, type ConsoleClient, type ConsoleProfilePick } from "../../platform";
import { DeviceLinkDialog, type LiveButtons } from "./DeviceLinkDialog";
import { DeviceLibraryPage } from "./DeviceLibraryPage";
import { SWEEP_LOWEST, strengthSweep } from "./strengthSweep";

// Ported from DeviceLinkWindowTests.cs. The fake console keeps the thresholds
// the window set, and the person breathes when the window says so and reaches
// a known strength. The button the gamepad reports is the firmware's rule: it
// fires when the threshold for that direction is at or under what they reach.

class FakeConsole implements ConsoleClient {
  readonly commands: string[] = [];
  readonly prefs: Record<string, number> = { puff_threshold: 0, sip_threshold: 0, sip_puff_threshold: 60 };
  pick: ConsoleProfilePick | null = null;
  backup: string | null = null;
  recorded: [number, number] | null = null;

  listConsolePorts() {
    return Promise.resolve([{ id: "port-1", label: "QuadStick-SPP (/dev/cu.QuadStick-SPP)" }]);
  }
  connectConsole() {
    this.commands.push("build");
    return Promise.resolve({ kind: "connected", build: 2373 } as const);
  }
  closeConsole() {
    this.commands.push("close");
    return Promise.resolve();
  }
  chooseConsoleProfile() {
    return Promise.resolve(this.pick);
  }
  consoleSendProfile(pickId: string, yesDefault: boolean) {
    this.commands.push(`send,${pickId},${String(yesDefault)}`);
    return Promise.resolve({ name: this.pick?.name ?? "", backup: this.backup });
  }
  consoleStrengthBegin() {
    this.commands.push("begin");
    return Promise.resolve({ threshold: 60 });
  }
  consoleCalibrate() {
    this.commands.push("calibrate");
    return Promise.resolve();
  }
  consoleSetThreshold(puff: boolean, value: number) {
    const name = puff ? "puff_threshold" : "sip_threshold";
    this.prefs[name] = value;
    this.commands.push(`${name},${String(value)}`);
    return Promise.resolve();
  }
  consoleStrengthEnd() {
    this.commands.push("end");
    return Promise.resolve();
  }
  recordStrengthMeasured(puff: number, sip: number) {
    this.recorded = [puff, sip];
    return Promise.resolve();
  }
}

const FAST = { breathMs: 60, settleMs: 1, firstReadingMs: 20, pollMs: 2 };

function person(device: FakeConsole, puff: number, sip: number): LiveButtons {
  return () => {
    const asked = screen.queryByTestId("link-prompt")?.textContent ?? "";
    const fires =
      (asked.includes("Puff") && (device.prefs["puff_threshold"] ?? 0) <= puff) ||
      (asked.includes("Sip") && (device.prefs["sip_threshold"] ?? 0) <= sip);
    return fires ? [1] : [];
  };
}

function open(
  device: FakeConsole,
  live: LiveButtons,
  onMeasured?: (p: number, s: number) => void,
  onClose: () => void = () => undefined,
) {
  const client = new MockQcmClient();
  return render(
    <I18nProvider initialPreference="en">
      <DeviceLinkDialog
        client={client}
        console={device}
        open
        onClose={onClose}
        timing={FAST}
        live={live}
        {...(onMeasured === undefined ? {} : { onMeasured })}
      />
    </I18nProvider>,
  );
}

async function connect(): Promise<void> {
  await screen.findByRole("option", { name: /QuadStick-SPP/u });
  fireEvent.click(screen.getByRole("button", { name: "Connect" }));
  await screen.findByText("Connected to a QuadStick, firmware 2373.");
}

async function measure(): Promise<void> {
  fireEvent.click(screen.getByRole("button", { name: "Measure sip and puff strength" }));
  fireEvent.click(await screen.findByRole("button", { name: "Yes, continue" }));
}

const status = (): string => screen.getByTestId("link-status").textContent ?? "";

describe("strengthSweep", () => {
  it("finds every strength exactly in seven breaths", async () => {
    for (let strength = SWEEP_LOWEST - 1; strength <= 95; strength++) {
      let tries = 0;
      // oxlint-disable-next-line no-await-in-loop -- one sweep per strength, in order
      const found = await strengthSweep((t) => {
        tries++;
        return Promise.resolve(t <= strength);
      });
      expect(found).toBe(strength);
      expect(tries).toBeGreaterThanOrEqual(1);
      expect(tries).toBeLessThanOrEqual(7);
    }
  });
});

describe("DeviceLinkDialog", () => {
  it("finds the person's puff and sip, then puts everything back", async () => {
    const device = new FakeConsole();
    let measured: [number, number] | null = null;
    open(device, person(device, 62, 37), (p, s) => {
      measured = [p, s];
    });
    await connect();
    await measure();
    await waitFor(() => expect(status()).toContain("Strongest puff"), { timeout: 10_000 });

    expect(measured).toEqual([62, 37]);
    expect(device.recorded).toEqual([62, 37]);
    expect(status()).toContain("62%");
    expect(status()).toContain("37%");
    expect(status()).toContain("60%");
    const c = device.commands;
    expect(c.indexOf("calibrate")).toBeLessThan(c.findIndex((x) => x.startsWith("puff_threshold,")));
    expect(c.indexOf("begin")).toBeLessThan(c.indexOf("calibrate"));
    const puffs = c.filter((x) => x.startsWith("puff_threshold,")).length;
    const sips = c.filter((x) => x.startsWith("sip_threshold,")).length;
    expect(puffs).toBeGreaterThanOrEqual(1);
    expect(puffs).toBeLessThanOrEqual(7);
    expect(sips).toBeGreaterThanOrEqual(1);
    expect(sips).toBeLessThanOrEqual(7);
    expect(c.at(-1)).toBe("end");
    expect(screen.queryByTestId("link-prompt")).toBeNull();
    // The restart drops the port, so Send and Measure wait for a new Connect.
    expect(screen.getByRole("button", { name: "Measure sip and puff strength" })).toBeDisabled();
  }, 15_000);

  it("reads someone who reaches nothing as under the bottom, not zero", async () => {
    const device = new FakeConsole();
    open(device, person(device, 0, 0));
    await connect();
    await measure();
    await waitFor(() => expect(status()).toContain("Strongest puff"), { timeout: 10_000 });
    expect(status()).toContain(`under ${String(SWEEP_LOWEST)}%`);
    expect(device.recorded).toEqual([SWEEP_LOWEST - 1, SWEEP_LOWEST - 1]);
    expect(device.commands.at(-1)).toBe("end");
  }, 15_000);

  it("still restarts the QuadStick after Stop", async () => {
    const device = new FakeConsole();
    open(device, person(device, 62, 37));
    await connect();
    await measure();
    await screen.findByText(/Puff as hard as you can/u, undefined, { timeout: 5_000 });
    fireEvent.click(screen.getByRole("button", { name: "Stop" }));
    await waitFor(() => expect(status()).toBe(
      "Stopped. The QuadStick was restarted, so your own profile and settings are back.",
    ));
    expect(device.recorded).toBeNull();
    expect(device.commands.at(-1)).toBe("end");
  });

  // 6934bbf: closing mid-test would drop the port with the test file still
  // loaded and the thresholds moved. The close waits for the device to be put
  // back (the restart deletes the test file and boots), then happens.
  it("closing mid-test puts the QuadStick back before the window goes", async () => {
    const device = new FakeConsole();
    let closed = 0;
    open(device, person(device, 62, 37), undefined, () => {
      closed += 1;
      device.commands.push("closed");
    });
    await connect();
    await measure();
    await screen.findByText(/Puff as hard as you can/u, undefined, { timeout: 5_000 });
    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(closed).toBe(0);
    await waitFor(() => expect(closed).toBe(1));
    expect(device.recorded).toBeNull();
    expect(device.commands.slice(-2)).toEqual(["end", "closed"]);
  });

  it("closing mid-send waits for the send to finish", async () => {
    const device = new FakeConsole();
    device.pick = { pickId: "pick-1", name: "game.csv", isDefault: false, isPreferences: false };
    const held: { finish: (() => void) | null } = { finish: null };
    device.consoleSendProfile = (pickId: string) => {
      device.commands.push(`send,${pickId}`);
      return new Promise((resolve) => {
        held.finish = () => resolve({ name: "game.csv", backup: null });
      });
    };
    let closed = 0;
    open(device, () => [], undefined, () => {
      closed += 1;
    });
    await connect();
    fireEvent.click(screen.getByRole("button", { name: "Send a profile…" }));
    await waitFor(() => expect(device.commands.at(-1)).toBe("send,pick-1"));
    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(closed).toBe(0);
    act(() => held.finish?.());
    await waitFor(() => expect(closed).toBe(1));
  });

  it("sends nothing to the QuadStick without a USB reading", async () => {
    const device = new FakeConsole();
    open(device, () => null);
    await connect();
    await measure();
    await waitFor(() => expect(status()).toContain("Plug the QuadStick in by USB as well"));
    expect(device.commands).toEqual(["build"]);
  });

  it("reports where the old profile went", async () => {
    const device = new FakeConsole();
    device.pick = { pickId: "pick-1", name: "game.csv", isDefault: false, isPreferences: false };
    device.backup = "QuadStickBackups/20261015-game.csv";
    open(device, () => []);
    await connect();
    fireEvent.click(screen.getByRole("button", { name: "Send a profile…" }));
    await waitFor(() => expect(status()).toContain("game.csv is on the QuadStick and loaded"));
    expect(status()).toContain("QuadStickBackups/20261015-game.csv");
    expect(device.commands.at(-1)).toBe("send,pick-1,false");
  });

  it("asks before replacing default.csv and sends nothing on Cancel", async () => {
    const device = new FakeConsole();
    device.pick = { pickId: "pick-1", name: "default.csv", isDefault: true, isPreferences: false };
    open(device, () => []);
    await connect();
    fireEvent.click(screen.getByRole("button", { name: "Send a profile…" }));
    await screen.findByText("Overwrite default.csv?");
    // Cancel left and focused; Yes right and plain, never the accent.
    const cancel = screen.getByRole("button", { name: "Cancel" });
    const yes = screen.getByRole("button", { name: "Yes, continue" });
    expect(cancel.compareDocumentPosition(yes) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    await waitFor(() => expect(cancel).toHaveFocus());
    expect(yes).not.toHaveClass("primary-action");
    fireEvent.click(cancel);
    await screen.findByRole("button", { name: "Send a profile…" });
    expect(device.commands).toEqual(["build"]);

    fireEvent.click(screen.getByRole("button", { name: "Send a profile…" }));
    fireEvent.click(await screen.findByRole("button", { name: "Yes, continue" }));
    await waitFor(() => expect(device.commands.at(-1)).toBe("send,pick-1,true"));
  });

  it("has no Send profile when the console cannot send one", async () => {
    const device = new FakeConsole();
    // The same console without the send pair.
    const bare = Object.create(device) as FakeConsole;
    Object.defineProperty(bare, "chooseConsoleProfile", { value: undefined });
    Object.defineProperty(bare, "consoleSendProfile", { value: undefined });
    open(bare, person(device, 50, 50));
    await connect();
    expect(screen.queryByRole("button", { name: /Send a profile/u })).toBeNull();
    expect(screen.getByRole("button", { name: "Measure sip and puff strength" })).toBeEnabled();
  });

  it("lets go of the port when it closes", async () => {
    const device = new FakeConsole();
    const view = open(device, () => []);
    await connect();
    act(() => view.unmount());
    expect(device.commands.at(-1)).toBe("close");
  });

  it("is reached from the device page by a named button", async () => {
    const client = new MockQcmClient();
    Object.assign(client, { console: new FakeConsole() });
    render(
      <I18nProvider initialPreference="en">
        <DeviceLibraryPage client={client} onOpenProfile={() => undefined} />
      </I18nProvider>,
    );
    fireEvent.click(
      await screen.findByRole("button", { name: "Connect to the QuadStick over Bluetooth or a serial cable" }),
    );
    expect(await screen.findByRole("heading", { name: "QuadStick over Bluetooth or cable" })).toBeInTheDocument();
    // B-115: the "?" asks, it does not repeat the title.
    expect(screen.getByRole("button", { name: "How does the Bluetooth or cable link work?" })).toBeInTheDocument();
  });
});
