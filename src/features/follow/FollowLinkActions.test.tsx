import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";

import { I18nProvider, useI18n } from "../../i18n";
import { MockQcmClient, type EditorSnapshot, type LatestCheck, type SheetRefusal } from "../../platform";
import { FollowLinkActions } from "./FollowLinkActions";
import { sheetRefusalText } from "./sheetText";

// Port of FollowLinkTests.cs at the window: the press is the only request,
// the list comes before the question, Keep mine is the default, and unsaved
// work is asked about before anything goes out.

function rig(follows: boolean, answer: LatestCheck) {
  const client = new MockQcmClient();
  const calls = { checks: 0, replaced: [] as string[], states: 0 };
  Object.assign(client, {
    getFollowState: () => {
      calls.states += 1;
      return Promise.resolve({ follows });
    },
    forgetFollowLink: () => Promise.resolve({ follows: false }),
    checkLatestFromLink: () => {
      calls.checks += 1;
      return Promise.resolve(answer);
    },
    replaceWithLatest: async (planId: string) => {
      calls.replaced.push(planId);
      const snapshot = await client.getProfileSnapshot(current!.sessionId);
      return { kind: "replaced", snapshot };
    },
  });
  let current: EditorSnapshot | null = null;
  const track = (snapshot: EditorSnapshot): EditorSnapshot => {
    current = snapshot;
    return snapshot;
  };
  return { client, calls, track };
}

function Host({ client, first }: { readonly client: MockQcmClient; readonly first: EditorSnapshot }) {
  const [snapshot, setSnapshot] = useState(first);
  return <FollowLinkActions client={client} snapshot={snapshot} onSnapshot={setSnapshot} />;
}

async function saved(client: MockQcmClient): Promise<EditorSnapshot> {
  const opened = await client.newProfile("game.csv");
  client.willSaveAs("game.csv");
  await client.saveProfileAs(opened.sessionId, opened.revision);
  return client.getProfileSnapshot(opened.sessionId);
}

function show(client: MockQcmClient, first: EditorSnapshot) {
  render(
    <I18nProvider initialPreference="en">
      <Host client={client} first={first} />
    </I18nProvider>,
  );
}

const CHANGES: LatestCheck = {
  kind: "changes",
  planId: "latest-1",
  changes: [
    { kind: "mode_added", mode: "Driving" },
    { kind: "mode_changed", mode: "Walking", added: 1, removed: 1 },
    { kind: "other_changed" },
  ],
  singleTab: true,
};

describe("Follow a Sheets link", () => {
  it("shows nothing and asks for nothing on a profile that does not follow", async () => {
    const { client, calls, track } = rig(false, CHANGES);
    show(client, track(await saved(client)));
    await waitFor(() => expect(calls.states).toBe(1));
    expect(screen.queryByRole("button", { name: /Download this profile again/u })).toBeNull();
    expect(calls.checks).toBe(0);
  });

  it("lists what changed before the question, and Keep mine changes nothing", async () => {
    const { client, calls, track } = rig(true, CHANGES);
    show(client, track(await saved(client)));
    expect(calls.checks).toBe(0); // opening it is not the press
    fireEvent.click(await screen.findByRole("button", { name: /Download this profile again/u }));
    const dialog = await screen.findByRole("dialog", { name: "Replace your copy with the latest?" });
    expect(dialog).toHaveTextContent("New in the sheet: Driving");
    expect(dialog).toHaveTextContent("Walking: 1 added, 1 removed");
    expect(dialog).toHaveTextContent("Other cells differ");
    expect(dialog).toHaveTextContent("a published link only ever gives a single tab");
    await waitFor(() => expect(screen.getByRole("button", { name: "Keep mine" })).toHaveFocus());
    fireEvent.click(screen.getByRole("button", { name: "Keep mine" }));
    await screen.findByText("Kept your copy. Nothing was changed.");
    expect(calls.replaced).toEqual([]);
  });

  it("replaces only after a yes", async () => {
    const { client, calls, track } = rig(true, CHANGES);
    show(client, track(await saved(client)));
    fireEvent.click(await screen.findByRole("button", { name: /Download this profile again/u }));
    fireEvent.click(await screen.findByRole("button", { name: "Replace my copy" }));
    await screen.findByText(/Replaced with the latest from the link/u);
    expect(calls.replaced).toEqual(["latest-1"]);
  });

  it("asks before saving unsaved work, and a no makes no request", async () => {
    const { client, calls, track } = rig(true, { kind: "up_to_date" });
    const first = await saved(client);
    const dirty = track(
      await client.applyEditorOps(first.sessionId, first.revision, [
        { op: "set_cell", row: 1, col: 0, value: "other.csv" },
      ]),
    );
    show(client, dirty);
    fireEvent.click(await screen.findByRole("button", { name: /Download this profile again/u }));
    await screen.findByRole("dialog", { name: "Save your changes first?" });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(calls.checks).toBe(0);

    fireEvent.click(screen.getByRole("button", { name: /Download this profile again/u }));
    await screen.findByRole("dialog", { name: "Save your changes first?" });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await screen.findByText("Already up to date. The sheet matches your copy.");
    expect(calls.checks).toBe(1);
    expect((await client.getProfileSnapshot(dirty.sessionId)).dirty).toBe(false);
  });

  it("says why a sheet that would not import was refused", async () => {
    const { client, track } = rig(true, { kind: "refused", refusal: { kind: "web_page" } });
    show(client, track(await saved(client)));
    fireEvent.click(await screen.findByRole("button", { name: /Download this profile again/u }));
    await screen.findByText(/Could not get the latest: Google returned a web page/u);
  });
});

function Refusal({ refusal }: { readonly refusal: SheetRefusal }) {
  const { t } = useI18n();
  return <p>{sheetRefusalText(refusal, t)}</p>;
}

describe("NoProfileTab wording", () => {
  it.each([
    [{ kind: "no_profile_tab", skipped: ["Notes"], empty: false, a1: "" }, 'The tab "Notes" was passed over.'],
    [{ kind: "no_profile_tab", skipped: [], empty: true, a1: "" }, "came back empty"],
    [{ kind: "no_profile_tab", skipped: [], empty: false, a1: "Hello" }, 'cell A1 says "Hello"'],
    [{ kind: "not_a_link" }, "does not look like a Google Sheets link"],
  ] as const)("%o", (refusal, text) => {
    render(
      <I18nProvider initialPreference="en">
        <Refusal refusal={refusal} />
      </I18nProvider>,
    );
    expect(screen.getByText(new RegExp(text.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&"), "u"))).toBeInTheDocument();
  });
});

describe("Home link import", () => {
  it("hands the pasted link to the import, not the workbook picker, and shows what it said", async () => {
    const { HomePage } = await import("../home/HomePage");
    const links: string[] = [];
    let picked = 0;
    render(
      <I18nProvider initialPreference="en">
        <HomePage
          client={new MockQcmClient()}
          rescue={null}
          workbookBusy={false}
          onNewProfile={() => undefined}
          onOpenProfile={() => undefined}
          onImportWorkbook={() => {
            picked += 1;
          }}
          onImportSheetsLink={(url) => links.push(url)}
          sheetsMessage="That does not look like a Google Sheets link."
          onOpenCommunity={() => undefined}
          onManageDevice={() => undefined}
          onOpenHelp={() => undefined}
          onOpenRescue={() => undefined}
          onDismissRescue={() => undefined}
        />
      </I18nProvider>,
    );
    const box = screen.getByRole("textbox", { name: /Paste a Google Sheets/u });
    fireEvent.change(box, { target: { value: " https://docs.google.com/spreadsheets/d/x/edit " } });
    const importButtons = screen.getAllByRole("button", { name: /Import the profile from the/u });
    fireEvent.click(importButtons[importButtons.length - 1]!);
    expect(links).toEqual([" https://docs.google.com/spreadsheets/d/x/edit "]);
    expect(picked).toBe(0);
    expect(screen.getByRole("status")).toHaveTextContent("does not look like a Google Sheets link");
  });
});
