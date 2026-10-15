import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import axe from "axe-core";
import { useState } from "react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient, type EditorSnapshot } from "../../platform";
import { EditorWorkspace } from "./EditorWorkspace";

function Harness({ client, initial }: { readonly client: MockQcmClient; readonly initial: EditorSnapshot }) {
  const [snapshot, setSnapshot] = useState(initial);
  return <EditorWorkspace client={client} snapshot={snapshot} onSnapshot={setSnapshot} />;
}

function renderEditor(client: MockQcmClient, snapshot: EditorSnapshot) {
  return render(
    <I18nProvider initialPreference="en">
      <Harness client={client} initial={snapshot} />
    </I18nProvider>,
  );
}

describe("TASK-038 editor workspace", () => {
  it("edits through EditorOp, undoes with the keyboard, then saves canonical state", async () => {
    const client = new MockQcmClient();
    client.willOpen("Racing.csv");
    const opened = await client.chooseAndOpenProfile();
    if (opened === null) throw new Error("mock open unexpectedly cancelled");
    renderEditor(client, opened);

    fireEvent.click(screen.getByTestId("binding-row-4"));
    const output = screen.getByLabelText("Output for row 4");
    fireEvent.change(output, { target: { value: "circle" } });
    fireEvent.blur(output);

    await waitFor(async () => {
      const current = await client.getProfileSnapshot(opened.sessionId);
      expect(current.grid[3]?.[0]).toBe("circle");
    });
    await waitFor(() => {
      expect(screen.getByRole("button", { name: /Undo/u })).toBeEnabled();
    });

    fireEvent.keyDown(window, { key: "z", ctrlKey: true });
    await waitFor(async () => {
      const current = await client.getProfileSnapshot(opened.sessionId);
      expect(current.grid[3]?.[0]).toBe("cross");
    });

    fireEvent.click(screen.getByTestId("binding-row-4"));
    const outputAgain = screen.getByLabelText("Output for row 4");
    fireEvent.change(outputAgain, { target: { value: "circle" } });
    fireEvent.blur(outputAgain);
    await waitFor(async () => {
      const current = await client.getProfileSnapshot(opened.sessionId);
      expect(current.dirty).toBe(true);
    });

    fireEvent.click(screen.getByRole("button", { name: /Save/u }));
    await waitFor(async () => {
      const current = await client.getProfileSnapshot(opened.sessionId);
      expect(current.dirty).toBe(false);
      expect(current.grid[3]?.[0]).toBe("circle");
    });
  });

  it("reorders whole modes through the typed mode operation", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    renderEditor(client, opened);

    const first = screen.getByTestId("mode-row-0");
    const firstName = within(first).getByRole("textbox", { name: "Name of mode 1" });
    fireEvent.change(firstName, { target: { value: "First" } });
    fireEvent.blur(firstName);
    await waitFor(async () => {
      expect((await client.getProfileSnapshot(opened.sessionId)).modes[0]?.name).toBe("First");
    });

    const second = screen.getByTestId("mode-row-1");
    const secondName = within(second).getByRole("textbox", { name: "Name of mode 2" });
    fireEvent.change(secondName, { target: { value: "Second" } });
    fireEvent.blur(secondName);
    await waitFor(async () => {
      expect((await client.getProfileSnapshot(opened.sessionId)).modes[1]?.name).toBe("Second");
    });

    fireEvent.click(within(screen.getByTestId("mode-row-0")).getByRole("button", { name: "Move it later" }));
    await waitFor(async () => {
      const current = await client.getProfileSnapshot(opened.sessionId);
      expect(current.modes.map((mode) => mode.name)).toEqual(["Second", "First"]);
    });
    // Only the picked mode shows its controls: focus follows the moved mode
    // rather than staying on a button its old row now hides.
    await waitFor(() => {
      expect(document.activeElement?.closest('[data-testid="mode-row-1"]')).not.toBeNull();
    });
  });

  it("moves focus from an issue to the affected canonical row", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    const withIssue: EditorSnapshot = {
      ...opened,
      issues: [
        {
          severity: "error",
          cell: "A4",
          message: "Unknown output",
          fix: "Choose a supported output",
          kind: "unknown_input",
        },
      ],
      errorCount: 1,
    };
    renderEditor(client, withIssue);

    // Avalonia's bar: one line with the count, the list only on demand.
    fireEvent.click(screen.getByRole("button", { name: /1 error.*Shows the list of problems/u }));
    fireEvent.click(screen.getByRole("button", { name: /Unknown output/u }));
    await waitFor(() => expect(screen.getByTestId("binding-row-4")).toHaveFocus());
  });

  it("shows the mock fixture's four warnings in the bottom bar, as Avalonia does", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    renderEditor(client, opened);

    expect(screen.getByRole("button", { name: /^4 warnings\s+\(click to view\)\. Shows/u })).toBeInTheDocument();
    expect(screen.getByText("4 warnings. The device skips those rows, so this still installs.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /4 warnings/u }));
    expect(screen.getAllByRole("listitem").filter((item) => item.closest(".problems-list"))).toHaveLength(4);
  });

  it("keeps the advanced grid a projection that edits through the same session", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    renderEditor(client, opened);

    fireEvent.click(screen.getByRole("button", { name: /spreadsheet/u }));
    const cell = screen.getByLabelText("Contents of cell A, 4");
    fireEvent.change(cell, { target: { value: "circle" } });
    fireEvent.blur(cell);

    await waitFor(async () => {
      expect((await client.getProfileSnapshot(opened.sessionId)).grid[3]?.[0]).toBe("circle");
    });
  });

  it("has no automated accessibility violations in the friendly editor", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    const { container } = renderEditor(client, opened);
    const result = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations).toEqual([]);
  });
});
