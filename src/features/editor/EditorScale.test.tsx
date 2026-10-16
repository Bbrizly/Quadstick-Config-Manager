import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient, type EditorSnapshot } from "../../platform";
import { bigProfileGrid } from "../../platform/mockPreviews";
import { EditorWorkspace } from "./EditorWorkspace";

function Harness({ client, initial }: { readonly client: MockQcmClient; readonly initial: EditorSnapshot }) {
  const [snapshot, setSnapshot] = useState(initial);
  return <EditorWorkspace client={client} snapshot={snapshot} onSnapshot={setSnapshot} />;
}

// The device's limit is 16 modes of 128 mappings. Headless Chrome on the
// production build: open 41 ms, Rows 26 ms, rename 17 ms, cell edit 22 ms.
// jsdom under a parallel suite is far slower and noisier, so these bounds
// only catch a render or edit that went quadratic, not a few milliseconds.
const RENDER_BOUND_MS = 10000;
const EDIT_BOUND_MS = 8000;

describe("editor at the device limit", () => {
  it("renders a 16-mode, 128-row profile and applies an edit within bounds", async () => {
    const client = new MockQcmClient();
    client.willOpenGrid("big.csv", bigProfileGrid());
    const opened = await client.chooseAndOpenProfile();
    if (opened === null) throw new Error("mock open cancelled");
    expect(opened.grid.length).toBeGreaterThan(16 * 128);

    const start = performance.now();
    render(
      <I18nProvider initialPreference="en">
        <Harness client={client} initial={opened} />
      </I18nProvider>,
    );
    const renderMs = performance.now() - start;
    expect(renderMs).toBeLessThan(RENDER_BOUND_MS);

    const rowsStart = performance.now();
    fireEvent.click(screen.getByRole("button", { name: /Show the mappings as a/u }));
    const rowsMs = performance.now() - rowsStart;
    expect(rowsMs).toBeLessThan(RENDER_BOUND_MS);
    const box = await screen.findByRole("textbox", {
      name: "Profile name. Edit to rename; .csv is added for you",
    });
    const editStart = performance.now();
    fireEvent.change(box, { target: { value: "bigger" } });
    fireEvent.blur(box);
    await screen.findByLabelText("This profile has unsaved changes. Save them before leaving?");
    const editMs = performance.now() - editStart;
    await waitFor(async () => {
      expect((await client.getProfileSnapshot(opened.sessionId)).dirty).toBe(true);
    });
    expect(editMs).toBeLessThan(EDIT_BOUND_MS);
  }, 30000);
});
