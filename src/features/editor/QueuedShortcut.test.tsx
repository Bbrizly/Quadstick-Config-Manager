import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient, type EditorSnapshot } from "../../platform";
import { EditorWorkspace } from "./EditorWorkspace";

function Harness({ client, initial }: { readonly client: MockQcmClient; readonly initial: EditorSnapshot }) {
  const [snapshot, setSnapshot] = useState(initial);
  return <EditorWorkspace client={client} snapshot={snapshot} onSnapshot={setSnapshot} />;
}

/** Holds each edit until `release`, like a native round trip under load. */
function slowEdits(client: MockQcmClient): () => void {
  const apply = client.applyEditorOps.bind(client);
  let release: (() => void) | null = null;
  client.applyEditorOps = (...args) => {
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    return gate.then(() => apply(...args));
  };
  return () => release?.();
}

async function editedOutput(client: MockQcmClient): Promise<{ readonly output: HTMLElement; readonly release: () => void }> {
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
  await waitFor(() => expect(screen.getByRole("button", { name: /^Save this profile\./u })).toBeEnabled());
  const release = slowEdits(client);
  fireEvent.change(output, { target: { value: "cross" } });
  fireEvent.blur(output);
  return { output, release };
}

describe("shortcuts pressed while an edit is in flight", () => {
  it("Ctrl+Z waits for the edit, then takes it back", async () => {
    const client = new MockQcmClient();
    const { output, release } = await editedOutput(client);
    fireEvent.keyDown(window, { key: "z", ctrlKey: true });
    release();
    await screen.findByText("Change undone.");
    const label = output.getAttribute("aria-label") ?? "";
    await waitFor(() => expect(screen.getByLabelText(label)).toHaveValue(""));
  });

  it("Ctrl+S waits for the edit, then saves it", async () => {
    const client = new MockQcmClient();
    const { release } = await editedOutput(client);
    client.willSaveAs("untitled.csv");
    fireEvent.keyDown(window, { key: "s", ctrlKey: true });
    release();
    await screen.findByText("Saved to untitled.csv.");
  });
});
