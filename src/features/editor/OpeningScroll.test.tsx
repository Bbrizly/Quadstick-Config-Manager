import { render, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient, type EditorSnapshot } from "../../platform";
import { EditorWorkspace } from "./EditorWorkspace";
import { openingScroll } from "./editorFit";

// B-121. One column at 900x620 and 200%: modes and the command bar filled the
// view and the picture began 1100 CSS pixels down, so the editor opened with
// no picture at all. jsdom does no layout, so the boxes are WebKit's numbers.

const props = ["offsetWidth", "offsetHeight", "clientHeight"] as const;
const saved = props.map((name) => [name, Object.getOwnPropertyDescriptor(HTMLElement.prototype, name)] as const);

afterEach(() => {
  for (const [name, descriptor] of saved) {
    if (descriptor !== undefined) Object.defineProperty(HTMLElement.prototype, name, descriptor);
  }
  vi.restoreAllMocks();
});

function define(name: (typeof props)[number], get: (element: HTMLElement) => number): void {
  Object.defineProperty(HTMLElement.prototype, name, {
    configurable: true,
    get(this: HTMLElement) {
      return get(this);
    },
  });
}

// Rects come back in zoomed pixels, as WebKit and Chromium give them at 200%.
function layOut(width: number, zoom = 1): void {
  define("offsetWidth", (element) => (element.classList.contains("editor-workspace") ? width : 0));
  define("offsetHeight", (element) => (element.classList.contains("problems-dock") ? 83 : 0));
  define("clientHeight", (element) => (element.classList.contains("editor-workspace") ? 223 : 0));
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
    const workspace = this.classList.contains("editor-workspace");
    const main = this.parentElement?.classList.contains("editor-plate") === true && this.nextElementSibling === null;
    const top = workspace ? 87 : main ? 87 + 1130 : 0;
    return new DOMRect(0, top * zoom, workspace ? width * zoom : 0, 0);
  });
}

async function open(): Promise<{ client: MockQcmClient; snapshot: EditorSnapshot }> {
  const client = new MockQcmClient();
  const snapshot = await client.chooseAndOpenProfile();
  if (snapshot === null) throw new Error("mock open cancelled");
  return { client, snapshot };
}

describe("where a one-column editor opens", () => {
  it("scrolls to the main view only when it would start low in a one-column fit", () => {
    expect(openingScroll("tight", 1130, 223, 83)).toBe(1130);
    expect(openingScroll("stacked", 71, 223, 83)).toBe(71);
    // Already in the top half of what shows: leave the command bar in view.
    expect(openingScroll("stacked", 70, 223, 83)).toBeNull();
    // Side by side, the stage is never below the modes.
    expect(openingScroll("full", 1130, 223, 83)).toBeNull();
    expect(openingScroll("inspector-below", 1130, 223, 83)).toBeNull();
    expect(openingScroll("tight", 1130, 80, 83)).toBeNull();
  });

  it("opens on the picture once; an edit or a return to the window does not move it again", async () => {
    layOut(450, 2);
    const { client, snapshot } = await open();
    const view = render(
      <I18nProvider initialPreference="en">
        <EditorWorkspace client={client} snapshot={snapshot} onSnapshot={() => undefined} />
      </I18nProvider>,
    );
    const workspace = view.container.querySelector<HTMLElement>(".editor-workspace")!;
    expect(workspace.dataset["fit"]).toBe("tight");
    await waitFor(() => expect(workspace.scrollTop).toBe(1130));
    expect(document.activeElement).toBe(document.body);

    workspace.scrollTop = 0;
    view.rerender(
      <I18nProvider initialPreference="en">
        <EditorWorkspace client={client} snapshot={{ ...snapshot, revision: snapshot.revision + 1 }} onSnapshot={() => undefined} />
      </I18nProvider>,
    );
    // Coming back to the window reloads the settings; that is not an open.
    const current = await client.getSettings();
    const reloaded = vi.spyOn(client, "getSettings").mockResolvedValue({ ...current });
    window.dispatchEvent(new Event("focus"));
    await waitFor(() => expect(reloaded).toHaveBeenCalled());
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(workspace.scrollTop).toBe(0);
  });

  it("leaves a wide editor where it was", async () => {
    layOut(1280);
    const { client, snapshot } = await open();
    const view = render(
      <I18nProvider initialPreference="en">
        <EditorWorkspace client={client} snapshot={snapshot} onSnapshot={() => undefined} />
      </I18nProvider>,
    );
    const workspace = view.container.querySelector<HTMLElement>(".editor-workspace")!;
    await waitFor(() => expect(workspace.dataset["fit"]).toBe("full"));
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(workspace.scrollTop).toBe(0);
  });
});
