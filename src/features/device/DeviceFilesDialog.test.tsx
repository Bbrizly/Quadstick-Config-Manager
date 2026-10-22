import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import { DeviceFilesDialog } from "./DeviceFilesDialog";

// jsdom has no Web Animations, so presence.ts closes at once there. Saying
// the engine has them is what lets a closing dialog stay for its exit.
afterEach(() => {
  vi.restoreAllMocks();
  delete (Element.prototype as { getAnimations?: unknown }).getAnimations;
});

describe("files window", () => {
  it("fades out like every other dialog instead of vanishing", () => {
    Element.prototype.getAnimations = () => [];
    const client = new MockQcmClient();
    const view = (open: boolean) => (
      <I18nProvider initialPreference="en">
        <DeviceFilesDialog client={client} open={open} onClose={() => undefined} onOpenProfile={() => undefined} />
      </I18nProvider>
    );
    const { rerender } = render(view(true));
    expect(screen.getByRole("dialog", { name: "Files on your QuadStick" })).toBeInTheDocument();
    rerender(view(false));
    const closing = document.querySelector("dialog.dialog-panel");
    expect(closing).not.toBeNull();
    expect(closing?.classList.contains("closing")).toBe(true);
  });

  it("reads nothing while shut", () => {
    const client = new MockQcmClient();
    const scan = vi.spyOn(client, "refreshDevices");
    render(
      <I18nProvider initialPreference="en">
        <DeviceFilesDialog client={client} open={false} onClose={() => undefined} onOpenProfile={() => undefined} />
      </I18nProvider>,
    );
    expect(document.querySelector("dialog")).toBeNull();
    expect(scan).not.toHaveBeenCalled();
  });
});
