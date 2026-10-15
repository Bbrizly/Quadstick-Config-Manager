import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { App } from "../../app/App";
import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import { InstallProfileDialog } from "../device/InstallProfileDialog";

const NOTICE = "Help improve QuadStick Config Manager";

describe("first-run usage notice (notice 2)", () => {
  it("is not shown when nothing can be sent", async () => {
    const client = new MockQcmClient();
    render(<App client={client} />);
    await waitFor(() => expect(screen.getByRole("main")).toBeInTheDocument());
    expect(screen.queryByRole("dialog", { name: NOTICE })).toBeNull();
  });

  it("asks before the crash prompt, and No records the notice without an id", async () => {
    const client = new MockQcmClient();
    client.enableUsageData();
    client.queuePendingCrashReport({ reportId: "crash-1.json", details: '{"chain":[{"type":"panic"}]}' });
    render(<App client={client} />);
    await screen.findByRole("dialog", { name: NOTICE });
    expect(screen.queryByRole("dialog", { name: "Send a crash report?" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Read the privacy policy, opens in your browser" }));
    expect(client.privacyPolicyOpened).toBe(1);

    fireEvent.click(screen.getByRole("button", { name: "No thanks, do not share usage data" }));
    await screen.findByRole("dialog", { name: "Send a crash report?" });
    const settings = await client.getSettings();
    expect(settings.telemetryNoticeVersion).toBe(2);
    expect(settings.usageAnalytics).toBe(false);
    expect(settings.installId).toBe("");
  });

  it("Yes turns usage on and mints the install id", async () => {
    const client = new MockQcmClient();
    client.enableUsageData();
    render(<App client={client} />);
    fireEvent.click(await screen.findByRole("button", { name: "Yes, share anonymous usage data" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: NOTICE })).toBeNull());
    const settings = await client.getSettings();
    expect(settings.usageAnalytics).toBe(true);
    expect(settings.installId).not.toBe("");
  });

  it("a failed write is a no, and says the change did not stick", async () => {
    const client = new MockQcmClient();
    client.enableUsageData();
    client.failNextSettingsWrite();
    render(<App client={client} />);
    fireEvent.click(await screen.findByRole("button", { name: "Yes, share anonymous usage data" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: NOTICE })).toBeNull());
    expect((await client.getSettings()).usageAnalytics).toBe(false);
    expect(screen.getByText(/Could not save that preference/)).toBeInTheDocument();
  });

  it("is not shown again once answered", async () => {
    const client = new MockQcmClient();
    client.enableUsageData();
    const settings = await client.getSettings();
    await client.updateSettings(settings.revision, { usageAnalytics: false });
    render(<App client={client} />);
    await waitFor(() => expect(screen.getByRole("main")).toBeInTheDocument());
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.queryByRole("dialog", { name: NOTICE })).toBeNull();
  });
});

describe("install cancel reasons (InstallFlow.cs)", () => {
  it("reports cancelled_folder when the folder picker is cancelled", async () => {
    const client = new MockQcmClient();
    const profile = await client.newProfile("untitled.csv");
    client.willCancelDeviceFolder();
    render(
      <I18nProvider initialPreference="en">
        <InstallProfileDialog client={client} profile={profile} open onClose={() => undefined} />
      </I18nProvider>,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Choose the QuadStick drive" }));
    await waitFor(() => expect(client.installCancels).toEqual(["cancelled_folder"]));
  });

  it("reports cancelled_device when two drives were offered and none was used", async () => {
    const client = new MockQcmClient();
    client.plugDevice("QUADSTICK");
    client.plugDevice("QS2");
    const profile = await client.newProfile("untitled.csv");
    render(
      <I18nProvider initialPreference="en">
        <InstallProfileDialog client={client} profile={profile} open onClose={() => undefined} />
      </I18nProvider>,
    );
    await screen.findByRole("combobox");
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(client.installCancels).toEqual(["cancelled_device"]);
  });

  it("does not report a plain close with one drive", async () => {
    const client = new MockQcmClient();
    client.plugDevice("QUADSTICK");
    const profile = await client.newProfile("untitled.csv");
    render(
      <I18nProvider initialPreference="en">
        <InstallProfileDialog client={client} profile={profile} open onClose={() => undefined} />
      </I18nProvider>,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    expect(client.installCancels).toEqual([]);
  });
});
