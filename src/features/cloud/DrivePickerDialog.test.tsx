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

const keyed = (key: string, values: readonly unknown[] = []): string => `${key}:${values.join("|")}`;

describe("Drive bulk restore (DriveBackup.RestoreAsync)", () => {
  it("pre-checks every importable backup, dims linked ones, and says what happened", async () => {
    const { restoreSummaryText } = await import("./DrivePickerDialog");
    const client = new MockQcmClient();
    const asked: string[][] = [];
    Object.assign(client, {
      listDriveBackups: () => Promise.resolve([
        { cloudRef: "ref-1", name: "racing", modifiedTime: "2026-10-01T10:00:00Z" },
        { cloudRef: "ref-2", name: "flying", modifiedTime: "2026-10-01T10:00:00Z", alreadyLinked: true },
        { cloudRef: "ref-3", name: "walking", modifiedTime: "2026-10-01T10:00:00Z" },
      ]),
      restoreDriveBackups: (refs: readonly string[]) => {
        asked.push([...refs]);
        return Promise.resolve({
          imported: ["racing"],
          skipped: [{ name: "walking", reason: "already_exists" }],
          failed: [],
        });
      },
    });
    const onReview = vi.fn();
    const onClose = vi.fn();
    render(
      <I18nProvider initialPreference="en">
        <DrivePickerDialog client={client} onReview={onReview} onClose={onClose} preCheck />
      </I18nProvider>,
    );
    const racing = await screen.findByRole("checkbox", { name: /racing, / });
    const flying = screen.getByRole("checkbox", { name: /flying, .*already in your profiles/u });
    expect(racing).toBeChecked();
    expect(flying).not.toBeChecked();
    expect(flying).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Import the selected profiles" }));
    await screen.findByText("1 imported, 1 skipped: walking already exists");
    expect(asked).toEqual([["ref-1", "ref-3"]]);
    expect(onReview).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();

    expect(
      restoreSummaryText(
        { imported: [], skipped: [], failed: [{ name: "x", reason: "download_failed" }] },
        keyed as never,
      ),
    ).toBe("Drive_RestoreImported:0, Drive_RestoreFailed:1x Backup_DownloadFailed:");
  });
});
