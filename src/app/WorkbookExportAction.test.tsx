import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { MockQcmClient, type WorkbookExportReceipt } from "../platform";
import { App } from "./App";

class ExportClient extends MockQcmClient {
  readonly exportCalls: Array<{ sessionId: string; revision: number }> = [];
  exportResult: WorkbookExportReceipt | null = { name: "untitled.xlsx", bytes: 1234 };

  exportProfileXlsx(
    sessionId: string,
    expectedRevision: number,
  ): Promise<WorkbookExportReceipt | null> {
    this.exportCalls.push({ sessionId, revision: expectedRevision });
    return Promise.resolve(this.exportResult);
  }
}

describe("TASK-040B editor XLSX export", () => {
  it("exports the canonical open session at its current revision", async () => {
    const client = new ExportClient();
    render(<App client={client} />);

    fireEvent.click(screen.getByRole("button", { name: "New profile" }));
    await screen.findByRole("heading", { level: 1, name: "untitled.csv" });

    fireEvent.click(screen.getByRole("button", { name: "Save .xlsx" }));

    await waitFor(() => expect(client.exportCalls).toHaveLength(1));
    expect(client.exportCalls[0]?.revision).toBe(0);
    expect(await screen.findByText("Saved to untitled.xlsx.")).toBeInTheDocument();
  });

  it("treats native save-picker cancellation as cancellation, not success", async () => {
    const client = new ExportClient();
    client.exportResult = null;
    render(<App client={client} />);

    fireEvent.click(screen.getByRole("button", { name: "New profile" }));
    await screen.findByRole("heading", { level: 1, name: "untitled.csv" });
    fireEvent.click(screen.getByRole("button", { name: "Save .xlsx" }));

    await waitFor(() => expect(client.exportCalls).toHaveLength(1));
    expect(screen.queryByText(/Saved to/)).not.toBeInTheDocument();
  });
});
