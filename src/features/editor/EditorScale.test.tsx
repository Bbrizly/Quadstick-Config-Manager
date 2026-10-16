import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { Profiler, useState } from "react";
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
//
// Wall time under a parallel jsdom suite swung by seconds and failed about one
// run in five, so this counts work instead: React commits, DOM nodes, table
// rows and calls into the client. Those are the same on every run and every
// machine, and they are what goes up when the editor starts drawing or
// recomputing every mode instead of the one on screen.

/** Generous for async waits only: no assertion here is on elapsed time. */
const WAIT = { timeout: 20000 };

interface Work {
  readonly openCommits: number;
  readonly openNodes: number;
  readonly rowsCommits: number;
  readonly rowsNodes: number;
  readonly tableRows: number;
  readonly editCommits: number;
  readonly editCalls: readonly string[];
}

const nodes = (): number => document.body.querySelectorAll("*").length;
const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 50));

async function measure(grid: readonly (readonly string[])[]): Promise<Work> {
  const client = new MockQcmClient();
  client.willOpenGrid("big.csv", grid);
  const opened = await client.chooseAndOpenProfile();
  if (opened === null) throw new Error("mock open cancelled");

  let commits = 0;
  // Until React has gone quiet: a busy machine only makes this wait longer,
  // it never changes the count.
  const settle = async (): Promise<void> => {
    const seen = commits;
    await tick();
    if (commits !== seen) await settle();
  };
  const editCalls: string[] = [];
  const apply = client.applyEditorOps.bind(client);
  client.applyEditorOps = (...args: Parameters<MockQcmClient["applyEditorOps"]>) => {
    editCalls.push("applyEditorOps");
    return apply(...args);
  };

  render(
    <I18nProvider initialPreference="en">
      <Profiler id="editor" onRender={() => (commits += 1)}>
        <Harness client={client} initial={opened} />
      </Profiler>
    </I18nProvider>,
  );
  const rowsButton = await screen.findByRole("button", { name: /Show the mappings as a/u }, WAIT);
  await settle();
  const openCommits = commits;
  const openNodes = nodes();

  commits = 0;
  fireEvent.click(rowsButton);
  const box = await screen.findByRole(
    "textbox",
    { name: "Profile name. Edit to rename; .csv is added for you" },
    WAIT,
  );
  await settle();
  const rowsCommits = commits;
  const rowsNodes = nodes();
  const tableRows = document.querySelectorAll("tr, [role=row]").length;

  commits = 0;
  fireEvent.change(box, { target: { value: "bigger" } });
  fireEvent.blur(box);
  await screen.findByLabelText("This profile has unsaved changes. Save them before leaving?", undefined, WAIT);
  await waitFor(async () => {
    expect((await client.getProfileSnapshot(opened.sessionId)).dirty).toBe(true);
  }, WAIT);
  await settle();
  const editCommits = commits;

  cleanup();
  return { openCommits, openNodes, rowsCommits, rowsNodes, tableRows, editCommits, editCalls };
}

describe("editor at the device limit", () => {
  it("does no more work for 16 modes of 128 than for one mode", async () => {
    const big = bigProfileGrid();
    expect(big.length).toBeGreaterThan(16 * 128);
    // Mode 1 alone: its title, name and header rows, then its 128 mappings.
    const one = big.slice(0, 3 + 128);

    const small = await measure(one);
    const large = await measure(big);

    // Rows draws the mode on screen, not all sixteen.
    expect(small.tableRows).toBeGreaterThanOrEqual(128);
    expect(large.tableRows).toBe(small.tableRows);

    // The same number of renders for each step, whatever the profile's size.
    expect(large.openCommits).toBe(small.openCommits);
    expect(large.rowsCommits).toBe(small.rowsCommits);
    expect(large.editCommits).toBe(small.editCommits);
    expect(large.openCommits).toBeLessThanOrEqual(5);
    expect(large.rowsCommits).toBeLessThanOrEqual(3);
    expect(large.editCommits).toBeLessThanOrEqual(4);

    // The only extra DOM is fifteen more entries in the mode picker: two nodes
    // each today, four allowed.
    const perMode = 4;
    expect(large.openNodes - small.openNodes).toBeLessThanOrEqual(15 * perMode);
    expect(large.rowsNodes - small.rowsNodes).toBeLessThanOrEqual(15 * perMode);

    // A rename is one round trip, not one per row or per mode.
    expect(large.editCalls).toEqual(["applyEditorOps"]);
    expect(small.editCalls).toEqual(["applyEditorOps"]);
  }, 60000);
});
