import { fireEvent, render, screen, within } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient, type EditorSnapshot } from "../../platform";
import { EditorWorkspace } from "./EditorWorkspace";

function Harness({ client, initial }: { readonly client: MockQcmClient; readonly initial: EditorSnapshot }) {
  const [snapshot, setSnapshot] = useState(initial);
  return <EditorWorkspace client={client} snapshot={snapshot} onSnapshot={setSnapshot} />;
}

/** What the native save does to a new profile: the version header goes on
 *  top, so every row moves down one. */
function withHeader(snapshot: EditorSnapshot): EditorSnapshot {
  return {
    ...snapshot,
    grid: [["QuadStick Configuration", "Version 1.5", "", "untitled"], ...snapshot.grid],
    modes: snapshot.modes.map((mode) => ({ ...mode, startRow: mode.startRow + 1 })),
  };
}

describe("saving a new profile", () => {
  it("keeps the open mapping selected when the version header moves the rows down", async () => {
    const client = new MockQcmClient();
    const opened = await client.newProfile("untitled.csv");
    render(
      <I18nProvider initialPreference="en">
        <Harness client={client} initial={opened} />
      </I18nProvider>,
    );
    fireEvent.click(screen.getByRole("button", { name: /^Left\. Soft Puff/u }));
    fireEvent.click(await screen.findByRole("button", { name: "Add a new mapping for the Left mouthpiece hole" }));
    const panel = screen.getByRole("complementary", { name: "Left mouthpiece hole" });
    const output = await within(panel).findByLabelText(/^Output for row \d+$/u);
    const row = Number(/\d+$/u.exec(output.getAttribute("aria-label") ?? "")?.[0]);

    const read = client.getProfileSnapshot.bind(client);
    client.getProfileSnapshot = async (sessionId) => withHeader(await read(sessionId));
    client.willSaveAs("untitled.csv");
    fireEvent.click(screen.getByRole("button", { name: /^Save this profile\./u }));

    await within(panel).findByLabelText(`Output for row ${String(row + 1)}`);
    expect(within(panel).getByLabelText(`Input 1 for row ${String(row + 1)}`)).toHaveValue("mp_left_sip");
  });
});
