import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import { DrivePickerDialog, shortDate } from "./DrivePickerDialog";

function show(client: MockQcmClient) {
  const onReview = vi.fn();
  const onClose = vi.fn();
  render(
    <I18nProvider initialPreference="en">
      <DrivePickerDialog client={client} onReview={onReview} onClose={onClose} />
    </I18nProvider>,
  );
  return { onReview, onClose };
}

describe("DrivePickerWindow port", () => {
  it("loads after opening, needs a pick, then hands the review on", async () => {
    const client = new MockQcmClient();
    const review = { importId: "imp-1" };
    Object.assign(client, {
      listDriveBackups: () => Promise.resolve([
        { cloudRef: "ref-1", name: "racing.csv", modifiedTime: "2026-10-01T10:00:00Z" },
      ]),
      restoreDriveBackup: (ref: string) => (ref === "ref-1" ? Promise.resolve(review) : Promise.reject(new Error("x"))),
    });
    const { onReview, onClose } = show(client);
    expect(screen.getByText("Loading your backups...")).toBeInTheDocument();
    const row = await screen.findByRole("radio", { name: /racing\.csv, / });
    fireEvent.click(screen.getByRole("button", { name: "Import the selected profiles" }));
    expect(screen.getByText("Nothing selected to import.")).toBeInTheDocument();
    fireEvent.click(row);
    fireEvent.click(screen.getByRole("button", { name: "Import the selected profiles" }));
    await waitFor(() => expect(onReview).toHaveBeenCalledWith(review));
    expect(onClose).toHaveBeenCalled();
  });

  it("says when there is nothing, and when Drive could not be read", async () => {
    const empty = new MockQcmClient();
    Object.assign(empty, { listDriveBackups: () => Promise.resolve([]) });
    show(empty);
    expect(await screen.findByText("No backups found in your Google Drive yet.")).toBeInTheDocument();
  });

  it("shows the raw time when Drive's date does not parse", () => {
    expect(shortDate("not a date")).toBe("not a date");
  });
});
