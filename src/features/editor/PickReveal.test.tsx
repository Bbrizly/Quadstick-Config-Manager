import { fireEvent, render, waitFor } from "@testing-library/react";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient, type EditorSnapshot } from "../../platform";
import { EditorWorkspace } from "./EditorWorkspace";

function Harness({ client, initial }: { readonly client: MockQcmClient; readonly initial: EditorSnapshot }) {
  const [snapshot, setSnapshot] = useState(initial);
  return <EditorWorkspace client={client} snapshot={snapshot} onSnapshot={setSnapshot} />;
}

// Stacked layouts put the inspector under the picture, so a pick changed
// something off screen and looked like it did nothing.
describe("picking a part", () => {
  afterEach(() => {
    delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView;
  });

  it("brings the inspector into view", async () => {
    const scrolled: Element[] = [];
    Element.prototype.scrollIntoView = vi.fn(function (this: Element) {
      scrolled.push(this);
    });
    const client = new MockQcmClient();
    const opened = await client.chooseAndOpenProfile();
    if (opened === null) throw new Error("mock open cancelled");
    const view = render(
      <I18nProvider initialPreference="en">
        <Harness client={client} initial={opened} />
      </I18nProvider>,
    );
    const callout = view.container.querySelector<HTMLElement>(".zone-callout");
    expect(callout).not.toBeNull();
    fireEvent.click(callout!);
    await waitFor(() => {
      expect(scrolled.some((element) => element.classList.contains("mapping-panel"))).toBe(true);
    });
  });
});
