import { render } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient, type EditorSnapshot } from "../../platform";
import { EditorWorkspace } from "./EditorWorkspace";

function Harness({ client, initial }: { readonly client: MockQcmClient; readonly initial: EditorSnapshot }) {
  const [snapshot, setSnapshot] = useState(initial);
  return <EditorWorkspace client={client} snapshot={snapshot} onSnapshot={setSnapshot} />;
}

// In Arabic the page runs right to left, and a bare ".csv" is all neutral
// characters plus Latin, so the bidi algorithm drew it as "csv.". The suffix
// is a file extension and always reads left to right.
describe("profile name in RTL", () => {
  it("keeps the .csv suffix left to right", async () => {
    const client = new MockQcmClient();
    const opened = await client.chooseAndOpenProfile();
    if (opened === null) throw new Error("mock open cancelled");
    const view = render(
      <I18nProvider initialPreference="en">
        <Harness client={client} initial={opened} />
      </I18nProvider>,
    );
    const suffix = view.container.querySelector(".filename-suffix");
    expect(suffix).not.toBeNull();
    expect(suffix).toHaveAttribute("dir", "ltr");
  });
});
