import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import { CrashReportPrompt } from "./CrashReportPrompt";

const DETAILS = '{"schema":1,"chain":[{"type":"Panic","frames":[]}]}';

function renderPrompt(client: MockQcmClient) {
  const onResolved = vi.fn();
  const onDismiss = vi.fn();
  client.queuePendingCrashReport({
    reportId: "crash-ui-1.json",
    details: DETAILS,
  });
  render(
    <I18nProvider initialPreference="en">
      <CrashReportPrompt
        client={client}
        report={{ reportId: "crash-ui-1.json", details: DETAILS }}
        onResolved={onResolved}
        onDismiss={onDismiss}
      />
    </I18nProvider>,
  );
  return { onResolved, onDismiss, client };
}

describe("GATE_SOFT crash report prompt", () => {
  it("shows Send, Later, Never with accessible names and monospace details", async () => {
    const client = new MockQcmClient();
    renderPrompt(client);

    expect(
      screen.getByRole("dialog", { name: "Send a crash report?" }),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send this crash report" })).toBeInTheDocument();
    expect(
      screen.getByRole("button", {
        name: "Not now, keep the report and ask again later",
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Stop asking about crash reports" }),
    ).toBeInTheDocument();

    const details = screen.getByLabelText("Crash details used to build the report");
    expect(details.tagName).toBe("TEXTAREA");
    expect(details).toHaveValue(DETAILS);
    expect(details.className).toContain("crash-report-details");
  });

  it("Later keeps the pending report and dismisses", async () => {
    const client = new MockQcmClient();
    const resolve = vi.spyOn(client, "resolveCrashReport");
    const { onDismiss, onResolved } = renderPrompt(client);

    fireEvent.click(
      screen.getByRole("button", {
        name: "Not now, keep the report and ask again later",
      }),
    );
    await waitFor(() => {
      expect(resolve).toHaveBeenCalledWith("crash-ui-1.json", "later");
      expect(onDismiss).toHaveBeenCalled();
    });
    expect(onResolved).not.toHaveBeenCalled();
    await expect(client.getPendingCrashReport()).resolves.toEqual({
      reportId: "crash-ui-1.json",
      details: DETAILS,
    });
  });

  it("Never discards and stops asking", async () => {
    const client = new MockQcmClient();
    const { onDismiss } = renderPrompt(client);

    fireEvent.click(
      screen.getByRole("button", { name: "Stop asking about crash reports" }),
    );
    await waitFor(async () => {
      expect(onDismiss).toHaveBeenCalled();
      const settings = await client.getSettings();
      expect(settings.askAboutCrashes).toBe(false);
      await expect(client.getPendingCrashReport()).resolves.toBeNull();
    });
  });

  it("Send with Soft keep-on-fail shows the gentle failure message", async () => {
    const client = new MockQcmClient();
    const { onResolved } = renderPrompt(client);

    fireEvent.click(screen.getByRole("button", { name: "Send this crash report" }));
    await waitFor(() => {
      expect(onResolved).toHaveBeenCalledWith(
        "Could not send the crash report. It is still on your computer.",
      );
    });
  });
});
