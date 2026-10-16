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

// The mock changes its state before its promise resolves, so the client can
// look done while the editor is still busy with the reply. A click in that gap
// is dropped (the editor ignores input while busy), which on a loaded machine
// failed the undo test about one run in ten. Wait for the editor to be idle.
const idle = () =>
  waitFor(() => {
    expect(screen.getByRole("button", { name: "Save this profile. Shortcut Control S" })).toBeEnabled();
  });

describe("TASK-038 editor workspace", () => {
  it("edits through EditorOp, undoes with the keyboard, then saves canonical state", async () => {
    const client = new MockQcmClient();
    client.willOpen("Racing.csv");
    const opened = await client.chooseAndOpenProfile();
    if (opened === null) throw new Error("mock open unexpectedly cancelled");
    renderEditor(client, opened);

    // Rows view: the sheet as Avalonia draws it, each cell a picker.
    fireEvent.click(screen.getByRole("button", { name: /Show the mappings as a/u }));
    fireEvent.click(screen.getByRole("button", { name: "Warning: Output for row 4: cross" }));
    const output = screen.getByLabelText("Warning: Output for row 4");
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
    await idle();

    fireEvent.click(screen.getByRole("button", { name: "Warning: Output for row 4: cross" }));
    const outputAgain = screen.getByLabelText("Warning: Output for row 4");
    fireEvent.change(outputAgain, { target: { value: "circle" } });
    fireEvent.blur(outputAgain);
    await waitFor(async () => {
      const current = await client.getProfileSnapshot(opened.sessionId);
      expect(current.dirty).toBe(true);
    });
    await idle();

    fireEvent.click(screen.getByRole("button", { name: "Save this profile. Shortcut Control S" }));
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

    // The pencil beside Modes opens the Modes window (`ModesWindow`).
    fireEvent.click(screen.getByRole("button", { name: "Edit modes: add, rename, reorder and delete" }));
    const first = screen.getByTestId("modes-dialog-row-0");
    const firstName = within(first).getByRole("textbox", { name: "Name of mode 1" });
    fireEvent.change(firstName, { target: { value: "First" } });
    fireEvent.blur(firstName);
    await waitFor(async () => {
      expect((await client.getProfileSnapshot(opened.sessionId)).modes[0]?.name).toBe("First");
    });

    const second = screen.getByTestId("modes-dialog-row-1");
    const secondName = within(second).getByRole("textbox", { name: "Name of mode 2" });
    fireEvent.change(secondName, { target: { value: "Second" } });
    fireEvent.blur(secondName);
    await waitFor(async () => {
      expect((await client.getProfileSnapshot(opened.sessionId)).modes[1]?.name).toBe("Second");
    });

    fireEvent.click(within(screen.getByTestId("modes-dialog-row-0")).getByRole("button", { name: "Move First down" }));
    await waitFor(async () => {
      const current = await client.getProfileSnapshot(opened.sessionId);
      expect(current.modes.map((mode) => mode.name)).toEqual(["Second", "First"]);
    });
    // The sidebar follows: the moved mode is still the one picked.
    await waitFor(() => {
      expect(within(screen.getByTestId("mode-row-1")).getByRole("button")).toHaveAttribute("aria-current", "true");
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

  it("writes a row's note and its function through set_cell", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("racing.csv");
    renderEditor(client, opened);

    fireEvent.click(screen.getByRole("button", { name: /Show the mappings as a/u }));
    const note = screen.getByLabelText("Note for row 4. Saved in the file, ignored by the QuadStick");
    fireEvent.change(note, { target: { value: "gas" } });
    fireEvent.blur(note);
    await waitFor(async () => {
      expect((await client.getProfileSnapshot(opened.sessionId)).grid[3]?.[10]).toBe("gas");
    });
    // The function cell reads "normal", the word the file holds, never "nothing yet".
    expect(screen.getAllByRole("button", { name: /^Function for row 4.*normal$/u })).toHaveLength(1);
  });

  it("lists the four Avalonia sidebar parts in device view and starts with nothing selected", () => {
    const client = new MockQcmClient();
    return client.newProfile("racing.csv").then((opened) => {
      renderEditor(client, opened);
      for (const name of [/^Main controls\./u, /^Hole combos/u, /^Switch jacks/u, /^USB devices/u]) {
        expect(screen.getByRole("button", { name })).toBeInTheDocument();
      }
      expect(screen.getByText(/^Nothing selected\./u)).toBeInTheDocument();
      expect(screen.getByText("Unused (145)")).toBeInTheDocument();
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
