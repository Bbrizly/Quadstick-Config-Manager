import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import { AppShell } from "./AppShell";
import { handleDocumentTab, rememberTabStart, trapTabKey } from "./focus";

// macOS WebKit follows the system Keyboard navigation setting, off by
// default, and then Tab only visits text fields and selects. Every Tab is
// moved in script instead, so these press Tab and check where focus lands.

function tab(shiftKey = false, init: KeyboardEventInit = {}): KeyboardEvent {
  const event = new KeyboardEvent("keydown", {
    key: "Tab",
    shiftKey,
    bubbles: true,
    cancelable: true,
    ...init,
  });
  (document.activeElement ?? document.body).dispatchEvent(event);
  return event;
}

function focusedName(): string {
  const active = document.activeElement as HTMLElement;
  return active.getAttribute("aria-label") ?? active.textContent ?? active.tagName;
}

function walk(steps: number, shiftKey = false): string[] {
  return Array.from({ length: steps }, () => {
    tab(shiftKey);
    return focusedName();
  });
}

function Page() {
  return (
    <>
      <a href="#main">Skip</a>
      <button type="button">One</button>
      <div role="tablist">
        <button type="button" role="tab" tabIndex={0}>
          Tab A
        </button>
        <button type="button" role="tab" tabIndex={-1}>
          Tab B
        </button>
      </div>
      <main id="main" tabIndex={-1}>
        <input aria-label="Field" />
        <button type="button" disabled>
          Off
        </button>
        <button type="button" hidden>
          Hidden
        </button>
        <textarea aria-label="Notes" />
        <select aria-label="Pick">
          <option>a</option>
        </select>
        <button type="button">Last</button>
      </main>
    </>
  );
}

function unmountHandlers() {
  document.removeEventListener("keydown", handleDocumentTab);
  document.removeEventListener("pointerdown", rememberTabStart, true);
  document.removeEventListener("focusin", rememberTabStart);
}

function mount() {
  render(<Page />);
  unmountHandlers();
  document.addEventListener("keydown", handleDocumentTab);
  document.addEventListener("pointerdown", rememberTabStart, true);
  document.addEventListener("focusin", rememberTabStart);
}

const ORDER = ["Skip", "One", "Tab A", "Field", "Notes", "Pick", "Last"];

describe("document Tab order", () => {
  afterEach(() => {
    unmountHandlers();
    cleanup();
  });

  it("visits every button, field and select in DOM order, then wraps", () => {
    mount();
    expect(walk(ORDER.length + 1)).toEqual([...ORDER, "Skip"]);
  });

  it("goes back with Shift+Tab, wrapping from the first stop to the last", () => {
    mount();
    screen.getByRole("button", { name: "One" }).focus();
    expect(walk(3, true)).toEqual(["Skip", "Last", "Pick"]);
  });

  it("takes the Tab so the browser cannot skip the buttons", () => {
    mount();
    expect(tab().defaultPrevented).toBe(true);
  });

  it("leaves a roving tab parked at -1 out", () => {
    mount();
    screen.getByRole("tab", { name: "Tab A" }).focus();
    tab();
    expect(focusedName()).toBe("Field");
  });

  it("steps on from the main landmark the skip link focused", () => {
    mount();
    document.getElementById("main")!.focus();
    expect(walk(1)).toEqual(["Field"]);
    document.getElementById("main")!.focus();
    expect(walk(1, true)).toEqual(["Tab A"]);
  });

  it("steps on from a roving tab focused by arrow keys", () => {
    mount();
    screen.getByRole("tab", { name: "Tab B" }).focus();
    expect(walk(1)).toEqual(["Field"]);
    screen.getByRole("tab", { name: "Tab B" }).focus();
    expect(walk(1, true)).toEqual(["Tab A"]);
  });

  it("tabs on from a click that did not focus (a WebKit button click)", () => {
    mount();
    const one = screen.getByRole("button", { name: "One" });
    one.dispatchEvent(new Event("pointerdown", { bubbles: true }));
    expect(document.activeElement).toBe(document.body);
    expect(walk(1)).toEqual(["Tab A"]);
    one.dispatchEvent(new Event("pointerdown", { bubbles: true }));
    (document.activeElement as HTMLElement).blur();
    expect(walk(1, true)).toEqual(["Skip"]);
  });

  it("tabs on from where a removed control was", () => {
    mount();
    const main = document.getElementById("main")!;
    const gone = document.createElement("button");
    gone.textContent = "Gone";
    main.prepend(gone);
    gone.focus();
    gone.remove();
    expect(document.activeElement).toBe(document.body);
    expect(walk(1)).toEqual(["Field"]);
  });

  it("skips a stop that refuses focus (hidden by CSS)", () => {
    mount();
    const notes = screen.getByRole("textbox", { name: "Notes" });
    notes.focus = () => undefined;
    screen.getByRole("textbox", { name: "Field" }).focus();
    expect(walk(1)).toEqual(["Pick"]);
  });

  it("asks for the ring, which WebKit drops after a mouse focus", () => {
    mount();
    const focus = vi.spyOn(HTMLElement.prototype, "focus");
    tab();
    expect(focus).toHaveBeenLastCalledWith({ focusVisible: true });
    focus.mockRestore();
  });

  it("leaves a Tab some control already took, and modified Tabs, alone", () => {
    mount();
    const one = screen.getByRole("button", { name: "One" });
    one.focus();
    one.addEventListener("keydown", (event) => event.preventDefault(), { once: true });
    tab();
    expect(document.activeElement).toBe(one);
    for (const init of [{ ctrlKey: true }, { metaKey: true }, { altKey: true }]) {
      expect(tab(false, init).defaultPrevented).toBe(false);
      expect(document.activeElement).toBe(one);
    }
  });

  it("stays inside an open modal", () => {
    mount();
    render(
      <div role="dialog" aria-modal="true">
        <button type="button">Yes</button>
        <button type="button">No</button>
      </div>,
    );
    screen.getByRole("button", { name: "No" }).focus();
    expect(walk(2)).toEqual(["Yes", "No"]);
  });

  it("is mounted by the app shell", () => {
    render(
      <I18nProvider locale="en">
        <AppShell
          activeDestination="home"
          onNavigate={() => undefined}
          themePreference="system"
          onThemePreferenceChange={() => undefined}
          onOpenSettings={() => undefined}
        >
          <button type="button">Inside</button>
        </AppShell>
      </I18nProvider>,
    );
    const settings = screen.getByRole("button", { name: /settings/iu });
    settings.focus();
    expect(tab().defaultPrevented).toBe(true);
    expect(focusedName()).toBe("Inside");
    (document.activeElement as HTMLElement).blur();
    screen.getByRole("button", { name: /Go to/u }).dispatchEvent(new Event("pointerdown", { bubbles: true }));
    tab();
    expect(focusedName()).toBe("Home");
  });
});

describe("dialog Tab trap", () => {
  afterEach(cleanup);

  it("moves Tab between the middle buttons too, not only at the edges", () => {
    render(
      <div data-testid="panel">
        <button type="button">A</button>
        <button type="button">B</button>
        <button type="button">C</button>
      </div>,
    );
    const panel = screen.getByTestId("panel");
    const press = (shiftKey: boolean) => {
      const event = new KeyboardEvent("keydown", { key: "Tab", shiftKey, cancelable: true });
      trapTabKey(event, panel);
      return event;
    };
    screen.getByRole("button", { name: "A" }).focus();
    expect(press(false).defaultPrevented).toBe(true);
    expect(focusedName()).toBe("B");
    press(false);
    press(false);
    expect(focusedName()).toBe("A");
    press(true);
    expect(focusedName()).toBe("C");
  });
});
